#!/usr/bin/env python3
"""
Figures, tables and a report for a study that mixes LEVEL TYPES: manual (the mouse flies the UFO,
the camera holds still), scroll (the world scrolls under it) and auto (the UFO flies itself; the
participant only clicks). A condition here is (levelType, baseFps, direction), so 60 down as a
manual level and 60 down as an auto level are compared, never pooled.

    python Analysis/level_analysis.py                                   # Data/Logs -> Analysis/results
    python Analysis/level_analysis.py --data "I:/JND - Framerate/Pre Pilot" --out "I:/JND - Framerate/Levels"
    python Analysis/level_analysis.py --no-frames                       # skip the PlayerLogs (fast)
    python Analysis/level_analysis.py --figs "L0*" "L12*"               # only some figures
    python Analysis/level_analysis.py --legacy-level manual             # level of logs from before levelType

Writes into --out:
    figures/<group>/L##_<name>.png   thresholds, psychophysics, behaviour, auto, frames, quality
    figures/participants/            one staircase page per participant
    tables/blocks.csv                every main block: SessionLog columns + participant, condition, flags
    tables/shots.csv                 every round (main and practice) with derived columns
    tables/conditions.csv            per condition: n, threshold (Weber %, fps, ms) mean and 95% CI
    tables/level_contrasts.csv       within-participant differences between level types
    report.md                        inventory, data-quality flags, thresholds, contrasts, figure list

Logs from before levelType was logged are read too: their level is inferred from how far the UFO
travelled (a scroll level carries it far past what a still camera shows on that screen), or set
with --legacy-level, and every report says when a level was inferred. Figures that need columns
those logs lack (the auto level's lead/lag, the UFO's speed at crossings, screen positions in a
scroll level) use the blocks that have them and say so.

Design: one categorical colour per LEVEL TYPE - manual violet, scroll magenta, auto green -
validated with the dataviz validator over all pairs (worst CVD dE 17.6, normal vision 28.1; the
magenta is under 3:1 against white, so every figure also carries a legend and a marker shape per
level: circle, square, triangle). Direction is the fill: down filled, up hollow. Base rate is
always an axis or a facet, never a colour, so nothing here repaints Analysis/jnd_analysis.py's
base-rate colours.

Requires: numpy, pandas, matplotlib, scipy (and Analysis/jnd_analysis.py beside this file).
"""

from __future__ import annotations

import argparse
import fnmatch
import math
import re
import sys
import traceback
import warnings
from pathlib import Path

import matplotlib as mpl

mpl.use("Agg")

import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402
from matplotlib.lines import Line2D  # noqa: E402
from matplotlib.patches import Patch  # noqa: E402
from scipy import stats  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
import jnd_analysis as J  # noqa: E402 - house style and the statistics helpers

REPO = Path(__file__).resolve().parent.parent
LEVELS = ("manual", "scroll", "auto")
LEVEL_COLOR = {"manual": "#6250d6", "scroll": "#e87ba4", "auto": "#008300"}
LEVEL_MARKER = {"manual": "o", "scroll": "s", "auto": "^"}
LEVEL_LABEL = {"manual": "Manual", "scroll": "Scroll", "auto": "Auto"}
ARROW = {"down": "↓", "up": "↑"}
CLOSE_R = 6.0
VIEW_W_16_9 = 2 * J.CAM_DIST * math.tan(math.radians(J.CAM_FOV_DEG / 2)) * 16 / 9


def close_radius(st):
    if hasattr(st, "shots") and "closeRadius" in st.shots and st.shots["closeRadius"].notna().any():
        return float(st.shots["closeRadius"].dropna().iloc[0])
    return CLOSE_R

FIGURES = []          # (stem, group, needs_frames, function, description)


def figure(stem, group, description, needs_frames=False):
    def wrap(fn):
        FIGURES.append((stem, group, needs_frames, fn, description))
        return fn
    return wrap


def log(msg=""):
    print(msg, flush=True)


# ================================================================== loading

def discover(roots):
    out = []
    for root in roots:
        root = Path(root)
        cands = {p.parent for p in root.rglob("SessionLog_*.csv")} | {p.parent for p in root.rglob("ShotLog_*.csv")}
        for folder in sorted(cands):
            if list(folder.glob("RoundLog_*.csv")):
                continue
            shot = sorted(folder.glob("ShotLog_*.csv"))
            sess = sorted(folder.glob("SessionLog_*.csv"))
            if not shot:
                continue
            head = shot[0].open(encoding="utf-8-sig").readline()
            if "stimulusMs" in head or "weapon" in head.split(","):
                continue                      # the stutter study
            out.append(dict(folder=folder, shot=shot[0], session=sess[0] if sess else None,
                            player=next(iter(sorted(folder.glob("PlayerLog_*.csv"))), None),
                            info=next(iter(sorted(folder.glob("SessionInfo_*.csv"))), None)))
    return out


def aspect_of(resolution):
    m = re.match(r"\s*(\d+)\s*[x×]\s*(\d+)", str(resolution))
    return int(m.group(1)) / int(m.group(2)) if m else 16 / 9


PLAYER_COLS = ["blockIndex", "phase", "levelType", "baseFps", "roundNumber", "timeSinceStartSec",
               "unscaledDeltaMs", "targetFps", "pacedBy", "side", "alteredSide", "crossed", "mouseDeltaX",
               "mouseDeltaY", "ufoX", "ufoY", "towerX", "camX", "phaseStartIso"]


def read_frames(path):
    head = pd.read_csv(path, nrows=0).columns
    cols = [c for c in PLAYER_COLS if c in head]
    num = {c: "float32" for c in cols if c not in ("phase", "levelType", "pacedBy", "side", "alteredSide",
                                                     "phaseStartIso")}
    df = pd.read_csv(path, usecols=cols, dtype=num, keep_default_na=False, na_values=[""])
    for c in ("phase", "levelType", "pacedBy", "side", "alteredSide", "phaseStartIso"):
        if c in df:
            df[c] = df[c].fillna("").astype("category")
    return df


def infer_level(sess, shots, legacy, player=None):
    """(level, how) for a session logged before levelType existed: scroll if the UFO travelled farther than a
    still camera lets it on that screen (J.scroll_span: the view's width + the pans between rounds). The
    PlayerLog's UFO track says it best (main-phase frames, else all - a run abandoned in practice has only
    those); without one, the shots' and towers' positions."""
    if legacy:
        return legacy, f"set to {legacy} with --legacy-level"
    aspect = aspect_of(sess["resolution"].iloc[0]) if sess is not None and "resolution" in sess and len(sess) else 16 / 9
    limit = J.scroll_span(aspect)
    if player is not None:
        try:
            fr = pd.read_csv(player, usecols=["phase", "ufoX"], dtype={"ufoX": "float32"})
            main = fr[fr["phase"].astype(str).eq("main")]
            x = (main if len(main) else fr)["ufoX"].dropna()
            if len(x):
                span = float(x.max() - x.min())
                lv = "scroll" if span > limit else "manual"
                return lv, (f"inferred {lv}: the UFO's x spans {span:.1f} world units in the PlayerLog "
                            f"({'main phase' if len(main) else 'practice only'}; a still camera allows {limit:.1f})")
        except Exception:  # noqa: BLE001 - fall back to the shots
            pass
    main = shots[shots["phase"].astype(str).eq("main")]
    use = main if len(main) >= 2 else shots
    xs = pd.concat([use.get("hitX", pd.Series(dtype=float)), use.get("towerX", pd.Series(dtype=float))]).dropna()
    if len(xs) == 0:
        return "manual", "no positions to infer it from: taken as manual"
    span = float(xs.max() - xs.min())
    lv = "scroll" if span > limit else "manual"
    return lv, f"inferred {lv}: shots and towers span {span:.1f} world units (a still camera allows {limit:.1f})"


class Study:
    def __init__(self, roots, frames=True, legacy=None, only=None, exclude=None):
        found = discover(roots)
        blocks, shots, infos, self.notes = [], [], [], []
        self.frames, self.player_paths = {}, {}
        sessions = []
        for f in found:
            try:
                sh = pd.read_csv(f["shot"], keep_default_na=False, na_values=["", "NaN"])
                ss = pd.read_csv(f["session"], keep_default_na=False, na_values=["", "NaN"]) if f["session"] else None
            except Exception as exc:  # noqa: BLE001
                self.notes.append(f"{f['folder'].name}: unreadable ({exc})")
                continue
            if sh.empty:
                continue
            sessions.append((f, sh, ss))
        sessions.sort(key=lambda t: (int(t[1]["sessionId"].iloc[0]), t[0]["folder"].name))
        n = 0
        for f, sh, ss in sessions:
            n += 1
            pid = f"P{n}"
            names = {pid.lower(), f["folder"].name.lower(), str(int(sh["sessionId"].iloc[0]))}
            if only and not names & {o.lower() for o in only}:
                continue
            if exclude and names & {e.lower() for e in exclude}:
                continue
            inferred = ""
            if "levelType" not in sh.columns or sh["levelType"].isna().all():
                lv, inferred = infer_level(ss, sh, legacy, f["player"])
                sh["levelType"] = lv
                if ss is not None:
                    ss["levelType"] = lv
                self.notes.append(f"{pid} ({f['folder'].name}): levelType not logged - {inferred}")
            for df in (sh, ss):
                if df is None:
                    continue
                df.insert(0, "pid", pid)
                df.insert(1, "folder", f["folder"].name)
                df["levelInferred"] = bool(inferred)
            shots.append(sh)
            if ss is not None and not ss.empty:
                blocks.append(ss)
            if f["player"] is not None:
                self.player_paths[pid] = f["player"]
            if f["info"] is not None:
                try:
                    inf = pd.read_csv(f["info"], keep_default_na=False)
                    inf.insert(0, "pid", pid)
                    infos.append(inf)
                except Exception:  # noqa: BLE001
                    pass
        self.shots = self._prep_shots(pd.concat(shots, ignore_index=True) if shots else pd.DataFrame())
        self.blocks = self._prep_blocks(pd.concat(blocks, ignore_index=True) if blocks else pd.DataFrame())
        self.info = pd.concat(infos, ignore_index=True) if infos else pd.DataFrame(columns=["pid", "key", "value"])
        self.want_frames = frames
        self.pids = sorted(set(self.shots["pid"]), key=lambda p: int(p[1:])) if len(self.shots) else []

    # -- shots
    @staticmethod
    def _prep_shots(s):
        if s.empty:
            return s
        s["levelType"] = s["levelType"].fillna("manual").astype(str)
        s["direction"] = s["direction"].fillna("down").astype(str)
        s["phase"] = s["phase"].astype(str)
        s["main"] = s["phase"].eq("main")
        for c in ("isHit", "playerFired", "countedByStaircase"):
            if c in s:
                s[c] = pd.to_numeric(s[c], errors="coerce").fillna(0).astype(int)
        s["weber"] = s["diffFps"] / s["baseFps"]
        s["cond"] = cond_key(s)
        if "ufoVelX" in s:
            moving = s["ufoVelX"].abs() > 0.5
            s["missAlongTravelX"] = np.where(moving & s["missDistX"].notna(),
                                             s["missDistX"] * np.sign(s["ufoVelX"]), np.nan)
        return s

    # -- blocks
    @staticmethod
    def _prep_blocks(b):
        if b.empty:
            return b
        b["levelType"] = b["levelType"].fillna("manual").astype(str)
        b["direction"] = b["direction"].fillna("down").astype(str)
        b["cond"] = cond_key(b)
        b["weberPct"] = 100 * b["jndDiffFps"] / b["baseFps"]
        b["abandoned"] = b["endReason"].astype(str).eq("abandoned")
        b["rangeEdge"] = b["jndRangeEdge"].fillna("").astype(str).ne("") if "jndRangeEdge" in b else False
        stop = pd.to_numeric(b.get("cfg_stopSD", pd.Series(np.nan, index=b.index)), errors="coerce")
        b["unreliable"] = b["sd"] > 2 * stop
        b["deliveryShort"] = b["baseSideMeasuredFps"] < 0.97 * b["baseFps"]
        b["usable"] = ~b["abandoned"]
        b["clean"] = b["usable"] & ~b["rangeEdge"] & ~b["unreliable"].fillna(False)
        return b

    def frames_of(self, pid):
        if not self.want_frames or pid not in self.player_paths:
            return None
        if pid not in self.frames:
            p = self.player_paths[pid]
            log(f"  reading {p.name} ({p.stat().st_size / 1e6:.0f} MB)")
            try:
                fr = read_frames(p)
                if "levelType" not in fr or (fr["levelType"].astype(str) == "").all():
                    lv = self.shots.loc[self.shots["pid"] == pid, "levelType"].iloc[0]
                    fr["levelType"] = lv
                self.frames[pid] = fr
            except Exception as exc:  # noqa: BLE001
                log(f"  {pid}: PlayerLog unreadable ({exc})")
                self.frames[pid] = None
        return self.frames[pid]

    def conditions(self, df=None):
        df = self.blocks if df is None else df
        keys = df[["levelType", "baseFps", "direction"]].drop_duplicates().itertuples(index=False)
        return sorted(keys, key=lambda k: (int(k[1]), k[2] != "down", LEVELS.index(k[0]) if k[0] in LEVELS else 9))


def cond_key(df):
    return df["levelType"].astype(str) + "|" + df["baseFps"].astype(int).astype(str) + "|" + df["direction"].astype(str)


def cond_label(level, base, direction, short=False):
    return f"{int(base)}{ARROW.get(direction, '?')}" + ("" if short else f" {level}")


# ================================================================== style helpers

def level_handles(levels, directions=("down",)):
    hs = []
    for lv in levels:
        hs.append(Line2D([], [], color=LEVEL_COLOR[lv], marker=LEVEL_MARKER[lv], ls="-", lw=1.2, ms=5,
                         mfc=LEVEL_COLOR[lv], mec="white", mew=0.5, label=LEVEL_LABEL[lv]))
    if "up" in directions and "down" in directions:
        hs.append(Line2D([], [], color=J.INK_2, marker="o", ls="", ms=5, mfc=J.INK_2, label="down (filled)"))
        hs.append(Line2D([], [], color=J.INK_2, marker="o", ls="", ms=5, mfc="white", label="up (hollow)"))
    return hs


def mfc(level, direction):
    return LEVEL_COLOR[level] if direction == "down" else "white"


def top_legend(fig, handles, ncol=None):
    fig.legend(handles=handles, loc="outside upper center", ncol=ncol or len(handles), frameon=False,
               handlelength=1.6, columnspacing=1.2)


def note(ax, text):
    ax.text(0.5, 0.5, text, ha="center", va="center", transform=ax.transAxes, color=J.MUTED, fontsize=J.FS_SMALL,
            wrap=True)
    ax.set_xticks([])
    ax.set_yticks([])


def grid_y(ax):
    ax.grid(axis="y", color=J.GRID, lw=0.45)
    ax.set_axisbelow(True)


# ================================================================== figures: thresholds

def _threshold_panel(ax, b, value, direction, ylabel, log_y=False):
    b = b[(b["direction"] == direction) & b["usable"]]
    bases = sorted(b["baseFps"].astype(int).unique())
    levels = [lv for lv in LEVELS if lv in set(b["levelType"])]
    if not bases or not levels:
        note(ax, f"no {direction} blocks")
        return
    width = 0.72
    off = {lv: (i - (len(levels) - 1) / 2) * width / max(1, len(levels)) for i, lv in enumerate(levels)}
    rng = np.random.default_rng(7)
    for xi, base in enumerate(bases):
        sub = b[b["baseFps"] == base]
        # a participant's thresholds at this base, across levels: joined, paired
        for pid, g in sub.groupby("pid"):
            g = g.set_index("levelType").reindex([lv for lv in levels if lv in set(g["levelType"])])
            if len(g) > 1:
                ax.plot([xi + off[lv] for lv in g.index], g[value], color=J.INDIVIDUAL, lw=0.6, zorder=1)
        for lv in levels:
            v = sub.loc[sub["levelType"] == lv, value].dropna()
            if v.empty:
                continue
            x = xi + off[lv]
            jit = rng.uniform(-0.035, 0.035, len(v))
            ax.scatter(x + jit, v, s=11, marker=LEVEL_MARKER[lv], facecolor=mfc(lv, direction),
                       edgecolor=LEVEL_COLOR[lv], lw=0.7, zorder=3, alpha=0.85)
            m, lo, hi = J.mean_ci(v.to_numpy())
            if math.isfinite(lo):
                ax.plot([x, x], [lo, hi], color=LEVEL_COLOR[lv], lw=1.3, zorder=4, solid_capstyle="butt")
            ax.plot([x - 0.08, x + 0.08], [m, m], color=LEVEL_COLOR[lv], lw=2.0, zorder=5)
    ax.set_xticks(range(len(bases)), [f"{b_} fps {ARROW[direction]}" for b_ in bases])
    ax.set_xlim(-0.6, len(bases) - 0.4)
    ax.set_ylabel(ylabel)
    if log_y:
        ax.set_yscale("log")
    else:
        ax.set_ylim(bottom=0)       # a threshold is never below 0; a small-n interval is clipped there
    grid_y(ax)
    J.title(ax, "", f"Altered side {'slower' if direction == 'down' else 'faster'} ({direction})")


def _thresholds(st, value, ylabel, log_y=False):
    b = st.blocks
    if b.empty or not b["usable"].any():
        return None
    dirs = [d for d in ("down", "up") if d in set(b["direction"])]
    fig, axes = plt.subplots(1, len(dirs), figsize=(J.TEXT_W, 2.6), layout="constrained", squeeze=False)
    for ax, d in zip(axes[0], dirs):
        _threshold_panel(ax, b, value, d, ylabel, log_y)
    top_legend(fig, level_handles([lv for lv in LEVELS if lv in set(b["levelType"])]) +
               [Line2D([], [], color=J.INDIVIDUAL, lw=0.6, label="one participant")])
    return fig


@figure("L01_thresholds_weber", "thresholds",
        "Threshold per condition as a Weber fraction (diff / base, %): every participant, joined across level "
        "types at the same base rate, with the mean and its 95% t-interval")
def fig_thresholds_weber(st):
    return _thresholds(st, "weberPct", "Threshold (% of the base rate)")


@figure("L02_thresholds_fps", "thresholds",
        "The same thresholds as frame-rate differences (fps, log scale)")
def fig_thresholds_fps(st):
    return _thresholds(st, "jndDiffFps", "Threshold difference (fps)", log_y=True)


def level_contrasts(st):
    b = st.blocks[st.blocks["usable"]]
    rows = []
    for (base, d), g in b.groupby(["baseFps", "direction"]):
        piv = g.pivot_table(index="pid", columns="levelType", values="weberPct", aggfunc="mean")
        for a, c in (("manual", "scroll"), ("manual", "auto"), ("scroll", "auto")):
            if a not in piv or c not in piv:
                continue
            pair = piv[[a, c]].dropna()
            if len(pair) < 1:
                continue
            diff = (pair[c] - pair[a]).to_numpy()
            m, lo, hi = J.mean_ci(diff)
            p_w = stats.wilcoxon(diff).pvalue if len(diff) >= 5 and np.any(diff != 0) else np.nan
            rows.append(dict(baseFps=int(base), direction=d, contrast=f"{c} - {a}", n=len(diff), meanDiffWeberPts=m,
                             ci95Lo=lo, ci95Hi=hi, signFlipP=J.sign_flip_p(diff) if len(diff) >= 2 else np.nan,
                             wilcoxonP=p_w, diffs=";".join(f"{v:.2f}" for v in diff)))
    return pd.DataFrame(rows)


@figure("L03_level_contrasts", "thresholds",
        "Within-participant differences between level types at the same base rate and direction (Weber points): "
        "each participant, the mean and its 95% interval; above 0 = the second level is harder")
def fig_level_contrasts(st):
    lc = level_contrasts(st)
    if lc.empty:
        return None
    lc = lc.sort_values(["direction", "baseFps", "contrast"]).reset_index(drop=True)
    fig, ax = plt.subplots(figsize=(J.COL_W, 0.3 * len(lc) + 1.0), layout="constrained")
    for i, r in lc.iterrows():
        y = len(lc) - 1 - i
        d = np.array([float(v) for v in r["diffs"].split(";")]) if r["diffs"] else np.array([])
        later = r["contrast"].split(" - ")[0]
        ax.scatter(d, np.full(len(d), y), s=9, color=LEVEL_COLOR.get(later, J.INK_2), alpha=0.7, zorder=3,
                   marker=LEVEL_MARKER.get(later, "o"), lw=0)
        if math.isfinite(r["ci95Lo"]):
            ax.plot([r["ci95Lo"], r["ci95Hi"]], [y, y], color=J.INK, lw=1.2, zorder=4)
        ax.plot([r["meanDiffWeberPts"]], [y], marker="|", ms=9, color=J.INK, mew=1.8, zorder=5)
    ax.axvline(0, color=J.AXIS, lw=0.8)
    ax.set_yticks(range(len(lc)), [f"{r.baseFps}{ARROW[r.direction]}  {r.contrast}" for r in lc[::-1].itertuples()])
    ax.set_xlabel("Difference in threshold (Weber points)")
    ax.grid(axis="x", color=J.GRID, lw=0.45)
    return fig


@figure("L04_order_effects", "thresholds",
        "Threshold (Weber %) by the block's position in the session, by level type: a practice or fatigue trend "
        "would slope")
def fig_order(st):
    b = st.blocks[st.blocks["usable"]]
    if b.empty or "blockOrdinal" not in b:
        return None
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.3), layout="constrained")
    for lv in LEVELS:
        g = b[b["levelType"] == lv]
        if g.empty:
            continue
        for d in ("down", "up"):
            h = g[g["direction"] == d]
            ax.scatter(h["blockOrdinal"] + {"manual": -0.12, "scroll": 0, "auto": 0.12}[lv], h["weberPct"], s=12,
                       marker=LEVEL_MARKER[lv], facecolor=mfc(lv, d), edgecolor=LEVEL_COLOR[lv], lw=0.7)
        means = g.groupby("blockOrdinal")["weberPct"].mean()
        ax.plot(means.index, means.values, color=LEVEL_COLOR[lv], lw=1.0, alpha=0.8)
    ax.set_xlabel("Block position in the session")
    ax.set_ylabel("Threshold (% of base)")
    ax.xaxis.set_major_locator(mpl.ticker.MaxNLocator(integer=True))
    grid_y(ax)
    top_legend(fig, level_handles([lv for lv in LEVELS if lv in set(b["levelType"])], tuple(set(b["direction"]))), 3)
    return fig


# ================================================================== figures: psychophysics

def _bins(x, n_bins=7):
    x = np.asarray(x, float)
    if len(x) < 4:
        return np.array([x.min(), x.max() + 1e-9])
    qs = np.unique(np.quantile(x, np.linspace(0, 1, n_bins + 1)))
    return qs if len(qs) > 2 else np.array([x.min(), x.max() + 1e-9])


@figure("L05_psychometric", "psychophysics",
        "Hit rate against the difference (Weber %) per condition: rounds pooled over participants in quantile bins "
        "(Wilson 95% intervals, area ~ rounds), the pooled Weibull fit with chance fixed at the block's guessRate "
        "(dotted line)")
def fig_psychometric(st):
    s = st.shots[st.shots["main"] & st.shots["countedByStaircase"].eq(1)]
    if s.empty:
        return None
    conds = st.conditions(s)
    n = len(conds)
    ncol = min(4, n)
    nrow = math.ceil(n / ncol)
    fig, axes = plt.subplots(nrow, ncol, figsize=(J.TEXT_W, 1.75 * nrow + 0.5), layout="constrained",
                             squeeze=False, sharey=True)
    for ax in axes.flat[n:]:
        ax.set_visible(False)
    for ax, (lv, base, d) in zip(axes.flat, conds):
        g = s[(s["levelType"] == lv) & (s["baseFps"] == base) & (s["direction"] == d)]
        x = 100 * g["weber"].to_numpy()
        y = g["isHit"].to_numpy()
        edges = _bins(x)
        idx = np.clip(np.digitize(x, edges[1:-1]), 0, len(edges) - 2)
        for k in range(len(edges) - 1):
            m = idx == k
            if not m.any():
                continue
            lo, hi = J.wilson(int(y[m].sum()), int(m.sum()))
            xc = float(np.median(x[m]))
            ax.plot([xc, xc], [lo, hi], color=LEVEL_COLOR[lv], lw=0.9, alpha=0.7)
            ax.scatter([xc], [y[m].mean()], s=6 + 2.2 * m.sum(), marker=LEVEL_MARKER[lv], facecolor=mfc(lv, d),
                       edgecolor=LEVEL_COLOR[lv], lw=0.8, zorder=3)
        gamma = float(pd.to_numeric(g["cfg_guessRate"], errors="coerce").dropna().iloc[0]) \
            if "cfg_guessRate" in g and g["cfg_guessRate"].notna().any() else 0.235
        fit = J.mle_weibull(g["diffFps"].to_numpy(float), y.astype(float), gamma)
        if fit.get("ok") and math.isfinite(fit["theta"]):
            xx = np.linspace(max(0.1, x.min() * 0.8), x.max() * 1.05, 200)
            yy = J.weibull(xx / 100 * base, fit["theta"], fit["beta"], fit["lam"], gamma)
            ax.plot(xx, yy, color=LEVEL_COLOR[lv], lw=1.3)
            ax.axvline(100 * fit["theta"] / base, color=LEVEL_COLOR[lv], lw=0.6, ls=(0, (2, 2)))
        ax.axhline(gamma, color=J.MUTED, lw=0.7, ls=":")
        ax.set_ylim(-0.03, 1.03)
        ax.set_title(f"{int(base)} fps {ARROW[d]} · {LEVEL_LABEL[lv]}  (n={len(g)})", fontsize=J.FS_SMALL + 0.5)
        grid_y(ax)
    for ax in axes[:, 0]:
        ax.set_ylabel("Hit rate")
    for ax in axes[-1, :]:
        ax.set_xlabel("Difference (% of base)")
    return fig


def _trial_sd(st):
    s = st.shots[st.shots["main"] & st.shots["countedByStaircase"].eq(1)].copy()
    if s.empty or "sd" not in s:
        return s
    s["trial"] = s.groupby(["pid", "blockIndex"]).cumcount() + 1
    stop = pd.to_numeric(s.get("cfg_stopSD", np.nan), errors="coerce")
    s["sdRatio"] = s["sd"] / stop
    return s


@figure("L06_convergence", "psychophysics",
        "How fast QUEST+ became sure: posterior SD over the block's stop rule (cfg_stopSD) by trial, per level type "
        "(median and interquartile range over blocks; 1 = the stop rule)")
def fig_convergence(st):
    s = _trial_sd(st)
    if s.empty or "sdRatio" not in s or s["sdRatio"].isna().all():
        return None
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.3), layout="constrained")
    for lv in LEVELS:
        g = s[s["levelType"] == lv]
        if g.empty:
            continue
        q = g.groupby("trial")["sdRatio"].quantile([0.25, 0.5, 0.75]).unstack()
        q = q[g.groupby("trial").size() >= 2] if len(q) > 3 else q
        ax.fill_between(q.index, q[0.25], q[0.75], color=LEVEL_COLOR[lv], alpha=0.15, lw=0)
        ax.plot(q.index, q[0.5], color=LEVEL_COLOR[lv], lw=1.4, marker=LEVEL_MARKER[lv], ms=3, markevery=5,
                label=LEVEL_LABEL[lv])
    ax.axhline(1, color=J.INK_2, lw=0.8, ls=(0, (3, 2)))
    ax.set_yscale("log")
    ax.set_xlabel("Trial")
    ax.set_ylabel("Posterior SD / stop rule")
    grid_y(ax)
    top_legend(fig, level_handles([lv for lv in LEVELS if lv in set(s["levelType"])]), 3)
    return fig


def staircase_page(st, pid):
    s = st.shots[(st.shots["pid"] == pid) & st.shots["main"] & st.shots["countedByStaircase"].eq(1)]
    if s.empty:
        return None
    blocks = list(s.groupby("blockIndex", sort=False))
    n = len(blocks)
    ncol = min(3, n)
    nrow = math.ceil(n / ncol)
    fig, axes = plt.subplots(nrow, ncol, figsize=(J.TEXT_W, 1.7 * nrow + 0.5), layout="constrained", squeeze=False)
    for ax in axes.flat[n:]:
        ax.set_visible(False)
    for ax, (bi, g) in zip(axes.flat, blocks):
        lv, d, base = g["levelType"].iloc[0], g["direction"].iloc[0], int(g["baseFps"].iloc[0])
        t = np.arange(1, len(g) + 1)
        if "threshDiffFps" in g and g["threshDiffFps"].notna().any():
            ax.fill_between(t, g["threshDiffFps"] - g["sd"], g["threshDiffFps"] + g["sd"], color=LEVEL_COLOR[lv],
                            alpha=0.15, lw=0)
            ax.plot(t, g["threshDiffFps"], color=LEVEL_COLOR[lv], lw=1.1)
        hit = g["isHit"].to_numpy() == 1
        ax.plot(t, g["diffFps"], color=J.INK_2, lw=0.6)
        ax.scatter(t[hit], g["diffFps"].to_numpy()[hit], s=12, marker="o", color=J.OUTCOME_COLOR["hit"], zorder=3,
                   lw=0, label="hit")
        ax.scatter(t[~hit], g["diffFps"].to_numpy()[~hit], s=12, marker="x", color=J.INK, zorder=3, lw=0.9,
                   label="miss / ran out")
        b = st.blocks[(st.blocks["pid"] == pid) & (st.blocks["blockIndex"] == bi)]
        end = b["endReason"].iloc[0] if len(b) else ""
        ax.set_title(f"{base} fps {ARROW[d]} · {LEVEL_LABEL[lv]} · {end}", fontsize=J.FS_SMALL + 0.5)
        ax.set_xlabel("Trial")
        ax.set_ylabel("Difference (fps)")
        grid_y(ax)
    hs = [Line2D([], [], marker="o", ls="", color=J.OUTCOME_COLOR["hit"], label="hit"),
          Line2D([], [], marker="x", ls="", color=J.INK, label="miss / ran out"),
          Patch(color=J.MUTED, alpha=0.3, label="threshold ± SD (QUEST+)")]
    top_legend(fig, hs)
    fig.suptitle(f"{pid} · {s['folder'].iloc[0]}", fontsize=J.FS_LABEL, x=0.01, ha="left")
    return fig


# ================================================================== figures: behaviour

@figure("L07_outcomes", "behaviour",
        "What every main round ended in, per condition: hit, missed shot, timed out, crossing limit (counts in the "
        "bars)")
def fig_outcomes(st):
    s = st.shots[st.shots["main"]]
    if s.empty:
        return None
    s = s.assign(result=np.where(s["outcome"].eq("shot"), np.where(s["isHit"].eq(1), "hit", "miss"), s["outcome"]))
    conds = st.conditions(s)
    fig, ax = plt.subplots(figsize=(J.COL_W, 0.28 * len(conds) + 0.9), layout="constrained")
    for i, (lv, base, d) in enumerate(conds):
        g = s[(s["levelType"] == lv) & (s["baseFps"] == base) & (s["direction"] == d)]
        left = 0.0
        y = len(conds) - 1 - i
        for key, lab in J.OUTCOME_ORDER:
            k = int((g["result"] == key).sum())
            if not k:
                continue
            w = k / len(g)
            ax.barh(y, w, left=left, color=J.OUTCOME_COLOR[key], edgecolor="white", lw=1.0, height=0.7)
            if w > 0.08:
                ax.text(left + w / 2, y, str(k), ha="center", va="center", fontsize=J.FS_SMALL,
                        color="white" if key in ("hit", "miss") else J.INK)
            left += w
    ax.set_yticks(range(len(conds)), [cond_label(lv, b_, d) for lv, b_, d in conds[::-1]])
    ax.set_xlim(0, 1)
    ax.set_xlabel("Share of rounds")
    top_legend(fig, [Patch(color=J.OUTCOME_COLOR[k], label=lab) for k, lab in J.OUTCOME_ORDER], 2)
    return fig


def _box_strip(ax, groups, colors, markers, labels):
    pos = np.arange(len(groups))
    rng = np.random.default_rng(3)
    for p, v, c, m in zip(pos, groups, colors, markers):
        v = np.asarray(v, float)
        v = v[np.isfinite(v)]
        if not len(v):
            continue
        q1, med, q3 = np.percentile(v, [25, 50, 75])
        ax.plot([p, p], [np.percentile(v, 5), np.percentile(v, 95)], color=c, lw=0.8)
        ax.add_patch(plt.Rectangle((p - 0.18, q1), 0.36, q3 - q1, facecolor="none", edgecolor=c, lw=1.1))
        ax.plot([p - 0.18, p + 0.18], [med, med], color=c, lw=2.0)
        ax.scatter(p + 0.3 + rng.uniform(-0.06, 0.06, len(v)), v, s=4, color=c, alpha=0.45, lw=0, marker=m)
    ax.set_xticks(pos, labels)
    ax.set_xlim(-0.6, len(groups) - 0.2)


@figure("L08_latency", "behaviour",
        "When the shot came after the last boundary crossing (sinceLastCrossingSec), per level type, hits and misses "
        "apart: in an auto level this is the reaction time; flying by hand, how long the participant searched after "
        "the last crossing")
def fig_latency(st):
    s = st.shots[st.shots["main"] & st.shots["playerFired"].eq(1)]
    s = s[s["sinceLastCrossingSec"].notna()]
    if s.empty:
        return None
    levels = [lv for lv in LEVELS if lv in set(s["levelType"])]
    groups, colors, markers, labels = [], [], [], []
    for lv in levels:
        for hit in (1, 0):
            groups.append(s.loc[(s["levelType"] == lv) & (s["isHit"] == hit), "sinceLastCrossingSec"])
            colors.append(LEVEL_COLOR[lv])
            markers.append(LEVEL_MARKER[lv])
            labels.append(f"{LEVEL_LABEL[lv]}\n{'hits' if hit else 'misses'}")
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.4), layout="constrained")
    _box_strip(ax, groups, colors, markers, labels)
    ax.set_yscale("log")
    ax.set_ylabel("Shot after last crossing (s)")
    grid_y(ax)
    return fig


@figure("L09_exploration", "behaviour",
        "Exploration against the difference: boundary crossings per round and the round's duration, in bins of the "
        "difference (median, IQR), per level type")
def fig_exploration(st):
    s = st.shots[st.shots["main"]]
    if s.empty:
        return None
    fig, axes = plt.subplots(1, 2, figsize=(J.TEXT_W, 2.3), layout="constrained")
    for ax, col, lab in ((axes[0], "crossings", "Crossings per round"), (axes[1], "roundDurationSec", "Round duration (s)")):
        for lv in LEVELS:
            g = s[s["levelType"] == lv]
            if g.empty:
                continue
            x = 100 * g["weber"].to_numpy()
            edges = _bins(x, 6)
            idx = np.clip(np.digitize(x, edges[1:-1]), 0, len(edges) - 2)
            xs, med, lo, hi = [], [], [], []
            for k in range(len(edges) - 1):
                m = idx == k
                if m.sum() < 3:
                    continue
                v = g[col].to_numpy()[m]
                xs.append(np.median(x[m]))
                med.append(np.median(v))
                lo.append(np.percentile(v, 25))
                hi.append(np.percentile(v, 75))
            if xs:
                ax.fill_between(xs, lo, hi, color=LEVEL_COLOR[lv], alpha=0.13, lw=0)
                ax.plot(xs, med, color=LEVEL_COLOR[lv], marker=LEVEL_MARKER[lv], ms=4, lw=1.3)
        ax.set_xlabel("Difference (% of base)")
        ax.set_ylabel(lab)
        grid_y(ax)
    top_legend(fig, level_handles([lv for lv in LEVELS if lv in set(s["levelType"])]))
    return fig


@figure("L10_miss_distance", "behaviour",
        "Where shots landed: cumulative share of shots by |shot X - tower X| per level type, the hit radius (5.5) "
        "dashed - the curve's height there is the hit rate")
def fig_miss_cdf(st):
    s = st.shots[st.shots["main"] & st.shots["playerFired"].eq(1)]
    if s.empty:
        return None
    cr = close_radius(st)
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.3), layout="constrained")
    for lv in LEVELS:
        v = np.sort(s.loc[s["levelType"] == lv, "missDistX"].abs().dropna().to_numpy())
        if not len(v):
            continue
        ax.step(v, np.arange(1, len(v) + 1) / len(v), where="post", color=LEVEL_COLOR[lv], lw=1.4,
                label=LEVEL_LABEL[lv])
    ax.axvline(cr, color=J.INK_2, lw=0.8, ls=(0, (3, 2)))
    ax.text(cr, 0.02, " hit radius", color=J.INK_2, fontsize=J.FS_SMALL, va="bottom")
    ax.set_xlabel("|shot X - tower X| (world units)")
    ax.set_ylabel("Share of shots")
    ax.set_ylim(0, 1.02)
    grid_y(ax)
    top_legend(fig, level_handles([lv for lv in LEVELS if lv in set(s["levelType"])]), 3)
    return fig


# ================================================================== figures: the auto level

def _auto_shots(st):
    s = st.shots[st.shots["main"] & st.shots["playerFired"].eq(1) & st.shots["levelType"].eq("auto")]
    return s


@figure("L11_auto_lead_lag", "auto",
        "Auto level: where the shot landed along the UFO's direction of travel (+ = past the tower: late; - = before "
        "it: early), the hit window shaded; and the same as an implied reaction time (distance / autoSpeed) against "
        "the difference")
def fig_auto_leadlag(st):
    s = _auto_shots(st)
    if s.empty or "missAlongTravelX" not in s or s["missAlongTravelX"].isna().all():
        return None
    s = s[s["missAlongTravelX"].notna()]
    cr = close_radius(st)
    fig, axes = plt.subplots(1, 2, figsize=(J.TEXT_W, 2.4), layout="constrained")
    ax = axes[0]
    ax.axvspan(-cr, cr, color=J.OUTCOME_COLOR["hit"], alpha=0.12, lw=0)
    for hit, col, lab in ((1, J.OUTCOME_COLOR["hit"], "hits"), (0, J.INK_2, "misses")):
        v = s.loc[s["isHit"] == hit, "missAlongTravelX"]
        if len(v):
            ax.hist(v, bins=np.arange(-30, 31, 1.5), color=col, alpha=0.75, label=f"{lab} (n={len(v)})",
                    edgecolor="white", lw=0.5)
    ax.axvline(0, color=J.INK, lw=0.8)
    ax.set_xlabel("Shot X - tower X along the direction of travel (units)")
    ax.set_ylabel("Shots")
    ax.legend(frameon=False, loc="upper left")
    grid_y(ax)
    ax = axes[1]
    speed = pd.to_numeric(s.get("cfg_autoSpeed", np.nan), errors="coerce").fillna(8.0)
    rt = s["missAlongTravelX"] / speed
    for hit, col, mk in ((1, J.OUTCOME_COLOR["hit"], "o"), (0, J.INK_2, "x")):
        m = s["isHit"] == hit
        ax.scatter(100 * s.loc[m, "weber"], rt[m], s=10, color=col, marker=mk, lw=0.8 if mk == "x" else 0,
                   alpha=0.75, label="hits" if hit else "misses")
    ax.axhline(0, color=J.INK, lw=0.8)
    med = float(np.median(rt[s["isHit"] == 1])) if (s["isHit"] == 1).any() else math.nan
    if math.isfinite(med):
        ax.axhline(med, color=J.OUTCOME_COLOR["hit"], lw=0.8, ls=(0, (3, 2)))
        ax.text(ax.get_xlim()[1], med, f"median {med:.2f} s ", ha="right", va="bottom", fontsize=J.FS_SMALL,
                color=J.INK_2)
    ax.set_xlabel("Difference (% of base)")
    ax.set_ylabel("Implied lag (s): distance / speed")
    grid_y(ax)
    return fig


@figure("L12_auto_hit_vs_latency", "auto",
        "Auto level: hit rate against how long after the last crossing the click came (Wilson 95% intervals), "
        "with the latency distribution behind - a reactive click early, a guess late")
def fig_auto_latency(st):
    s = _auto_shots(st)
    s = s[s["sinceLastCrossingSec"].notna()]
    if len(s) < 4:
        return None
    x = s["sinceLastCrossingSec"].to_numpy()
    y = s["isHit"].to_numpy()
    edges = np.unique(np.quantile(x, np.linspace(0, 1, 7)))
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.3), layout="constrained")
    ax.hist(x, bins=30, color=J.GRID, weights=np.full(len(x), 1.0 / len(x)), edgecolor="white", lw=0.3)
    idx = np.clip(np.digitize(x, edges[1:-1]), 0, len(edges) - 2)
    for k in range(len(edges) - 1):
        m = idx == k
        if not m.any():
            continue
        lo, hi = J.wilson(int(y[m].sum()), int(m.sum()))
        xc = float(np.median(x[m]))
        ax.plot([xc, xc], [lo, hi], color=LEVEL_COLOR["auto"], lw=1)
        ax.scatter([xc], [y[m].mean()], s=8 + 2 * m.sum(), marker="^", color=LEVEL_COLOR["auto"], zorder=3)
    ax.set_xscale("log")
    ax.set_ylim(0, 1.02)
    ax.set_xlabel("Click after the last crossing (s)")
    ax.set_ylabel("Hit rate")
    grid_y(ax)
    return fig


@figure("L13_crossing_speed", "behaviour",
        "How fast the boundary was crossed: the UFO's speed at each round's crossings, per level type (the auto "
        "level's is its autoSpeed by design); and at the moment of the shot")
def fig_crossing_speed(st):
    s = st.shots[st.shots["main"]]
    if "meanCrossingSpeed" not in s or s["meanCrossingSpeed"].isna().all():
        return None
    levels = [lv for lv in LEVELS if lv in set(s.loc[s["meanCrossingSpeed"].notna(), "levelType"])]
    fig, axes = plt.subplots(1, 2, figsize=(J.TEXT_W, 2.3), layout="constrained")
    _box_strip(axes[0], [s.loc[s["levelType"] == lv, "meanCrossingSpeed"] for lv in levels],
               [LEVEL_COLOR[lv] for lv in levels], [LEVEL_MARKER[lv] for lv in levels],
               [LEVEL_LABEL[lv] for lv in levels])
    axes[0].set_ylabel("Speed at crossings (units/s)")
    grid_y(axes[0])
    f = s[s["playerFired"].eq(1)]
    _box_strip(axes[1], [f.loc[f["levelType"] == lv, "ufoVelX"].abs() for lv in levels],
               [LEVEL_COLOR[lv] for lv in levels], [LEVEL_MARKER[lv] for lv in levels],
               [LEVEL_LABEL[lv] for lv in levels])
    axes[1].set_ylabel("Speed at the shot (units/s)")
    grid_y(axes[1])
    return fig


# ================================================================== figures: frames

def _live_frames(st):
    out = []
    for pid in st.pids:
        fr = st.frames_of(pid)
        if fr is None or fr.empty:
            continue
        live = fr[fr["pacedBy"].astype(str).isin(["base", "altered"]) & fr["phase"].astype(str).eq("main")]
        if live.empty:
            continue
        b = st.blocks[st.blocks["pid"] == pid]
        w = float(b["viewWidthWorld"].dropna().iloc[0]) if "viewWidthWorld" in b and b["viewWidthWorld"].notna().any() \
            else 2 * J.CAM_DIST * math.tan(math.radians(J.CAM_FOV_DEG / 2)) * \
            (aspect_of(b["resolution"].iloc[0]) if len(b) and "resolution" in b else 16 / 9)
        cam = live["camX"] if "camX" in live and live["camX"].notna().any() else pd.Series(0.0, index=live.index)
        vx = 0.5 + (live["ufoX"] - cam) / w
        out.append(pd.DataFrame({"pid": pid, "levelType": live["levelType"].astype(str).to_numpy(),
                                 "vx": vx.to_numpy(), "rel": (live["ufoX"] - live["towerX"]).to_numpy(),
                                 "y": live["ufoY"].to_numpy(), "dt": live["unscaledDeltaMs"].to_numpy() / 1000,
                                 "hasCam": "camX" in live and live["camX"].notna().any()}))
    return pd.concat(out, ignore_index=True) if out else pd.DataFrame()


@figure("L14_screen_position", "frames",
        "Where on the screen the UFO was during live play (time-weighted), per level type: across the screen (0 = left "
        "edge, 1 = right) and in height. A scroll level holds the middle; logs from before camX assume a still camera",
        needs_frames=True)
def fig_screen_position(st):
    f = _live_frames(st)
    if f.empty:
        return None
    levels = [lv for lv in LEVELS if lv in set(f["levelType"])]
    fig, axes = plt.subplots(1, len(levels), figsize=(J.TEXT_W, 2.2), layout="constrained", squeeze=False, sharey=True)
    for ax, lv in zip(axes[0], levels):
        g = f[f["levelType"] == lv]
        h, xe, ye = np.histogram2d(g["vx"].clip(-0.05, 1.05), g["y"], bins=[44, 18], weights=g["dt"],
                                   range=[[-0.05, 1.05], [float(f["y"].min()), float(f["y"].max()) + 1e-6]])
        ax.imshow(h.T, origin="lower", aspect="auto", extent=[xe[0], xe[-1], ye[0], ye[-1]], cmap="Greens"
                  if lv == "auto" else ("Purples" if lv == "manual" else "RdPu"), interpolation="nearest")
        ax.set_title(f"{LEVEL_LABEL[lv]}  ({g['dt'].sum() / 60:.1f} min)", fontsize=J.FS_SMALL + 0.5)
        ax.set_xlabel("Across the screen")
    axes[0][0].set_ylabel("Height (world units)")
    return fig


@figure("L15_position_vs_tower", "frames",
        "How far from the boundary the UFO spent its live time, per level type (time-weighted density of UFO X - "
        "tower X); the hit window shaded", needs_frames=True)
def fig_relative(st):
    f = _live_frames(st)
    if f.empty:
        return None
    cr = close_radius(st)
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.3), layout="constrained")
    ax.axvspan(-cr, cr, color=J.GRID, alpha=0.7, lw=0)
    for lv in LEVELS:
        g = f[f["levelType"] == lv]
        if g.empty:
            continue
        h, e = np.histogram(g["rel"].clip(-50, 50), bins=100, range=(-50, 50), weights=g["dt"], density=True)
        ax.plot((e[:-1] + e[1:]) / 2, h, color=LEVEL_COLOR[lv], lw=1.3, label=LEVEL_LABEL[lv])
    ax.axvline(0, color=J.INK, lw=0.7)
    ax.set_xlabel("UFO X - tower X (world units)")
    ax.set_ylabel("Share of live time / unit")
    grid_y(ax)
    top_legend(fig, level_handles([lv for lv in LEVELS if lv in set(f["levelType"])]), 3)
    return fig


@figure("L16_mouse_activity", "frames",
        "Mouse movement per second of live play, per round and level type: in an auto level the mouse flies nothing, "
        "so this is fidgeting - a check that nobody tried to steer", needs_frames=True)
def fig_mouse(st):
    rows = []
    for pid in st.pids:
        fr = st.frames_of(pid)
        if fr is None or fr.empty or "mouseDeltaX" not in fr:
            continue
        live = fr[fr["pacedBy"].astype(str).isin(["base", "altered"]) & fr["phase"].astype(str).eq("main")]
        if live.empty:
            continue
        px = np.hypot(live["mouseDeltaX"], live["mouseDeltaY"])
        g = pd.DataFrame({"r": live["roundNumber"].to_numpy(), "lv": live["levelType"].astype(str).to_numpy(),
                          "px": px.to_numpy(), "dt": live["unscaledDeltaMs"].to_numpy() / 1000})
        per = g.groupby(["r", "lv"]).agg(px=("px", "sum"), dt=("dt", "sum")).reset_index()
        per["rate"] = per["px"] / per["dt"]
        rows.append(per)
    if not rows:
        return None
    d = pd.concat(rows)
    levels = [lv for lv in LEVELS if lv in set(d["lv"])]
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.3), layout="constrained")
    _box_strip(ax, [d.loc[d["lv"] == lv, "rate"] + 1e-3 for lv in levels], [LEVEL_COLOR[lv] for lv in levels],
               [LEVEL_MARKER[lv] for lv in levels], [LEVEL_LABEL[lv] for lv in levels])
    ax.set_yscale("log")
    ax.set_ylabel("Mouse movement (counts/s)")
    grid_y(ax)
    return fig


@figure("L17_frame_times", "frames",
        "Frame-time error per side (delivered frame time - the cap's period, ms) per base rate: the manipulation "
        "check at frame level (5th-95th percentile, IQR, median)", needs_frames=True)
def fig_frame_times(st):
    rows = []
    for pid in st.pids:
        fr = st.frames_of(pid)
        if fr is None or fr.empty:
            continue
        live = fr[fr["pacedBy"].astype(str).isin(["base", "altered"]) & fr["phase"].astype(str).eq("main")
                  & (fr["targetFps"] > 0)]
        err = live["unscaledDeltaMs"] - 1000.0 / live["targetFps"]
        rows.append(pd.DataFrame({"base": live["baseFps"].to_numpy(), "side": live["pacedBy"].astype(str).to_numpy(),
                                  "err": err.to_numpy()}))
    if not rows:
        return None
    d = pd.concat(rows)
    keys = sorted(d.groupby(["base", "side"]).groups)
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.4), layout="constrained")
    groups = [d.loc[(d["base"] == b) & (d["side"] == s_), "err"].clip(-5, 20) for b, s_ in keys]
    pos = np.arange(len(keys))
    for p, v, (b, s_) in zip(pos, groups, keys):
        if not len(v):
            continue
        c = J.INK if s_ == "base" else J.INK_2
        q = np.percentile(v, [5, 25, 50, 75, 95])
        ax.plot([p, p], [q[0], q[4]], color=c, lw=0.8)
        ax.add_patch(plt.Rectangle((p - 0.2, q[1]), 0.4, max(1e-3, q[3] - q[1]), facecolor="white" if s_ == "altered"
                                   else J.GRID, edgecolor=c, lw=1))
        ax.plot([p - 0.2, p + 0.2], [q[2], q[2]], color=c, lw=1.8)
    ax.axhline(0, color=J.AXIS, lw=0.8)
    ax.set_xticks(pos, [f"{int(b)}\n{s_}" for b, s_ in keys])
    ax.set_ylabel("Frame time - period (ms)")
    grid_y(ax)
    return fig


# ================================================================== figures: quality

@figure("L18_frame_delivery", "quality",
        "Was the stimulus delivered? Each round's measured frame rate against its cap, base and altered sides, per "
        "level type; points under the identity line are shortfalls")
def fig_delivery(st):
    s = st.shots[st.shots["main"]]
    if s.empty:
        return None
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.6), layout="constrained")
    for lv in LEVELS:
        g = s[s["levelType"] == lv]
        if g.empty:
            continue
        ax.scatter(g["baseFps"], g["measuredBaseFps"], s=9, marker=LEVEL_MARKER[lv], facecolor=LEVEL_COLOR[lv],
                   edgecolor="none", alpha=0.6)
        ax.scatter(g["alteredFps"], g["measuredAlteredFps"], s=9, marker=LEVEL_MARKER[lv], facecolor="white",
                   edgecolor=LEVEL_COLOR[lv], lw=0.7, alpha=0.8)
    lim = [1, max(600, float(np.nanmax(s[["baseFps", "alteredFps"]].to_numpy())) * 1.2)]
    ax.plot(lim, lim, color=J.INK_2, lw=0.8, ls=(0, (3, 2)))
    ax.set_xscale("log")
    ax.set_yscale("log")
    ax.set_xlim(lim)
    ax.set_ylim(lim)
    ax.set_xlabel("Cap (fps)")
    ax.set_ylabel("Measured (fps)")
    grid_y(ax)
    hs = level_handles([lv for lv in LEVELS if lv in set(s["levelType"])])
    hs += [Line2D([], [], marker="o", ls="", color=J.INK_2, label="base side"),
           Line2D([], [], marker="o", ls="", mfc="white", color=J.INK_2, label="altered side")]
    top_legend(fig, hs, 3)
    return fig


@figure("L19_guess_rate", "quality",
        "Chance rate per block: the guessRate QUEST+ ran with (cfg_guessRate) against the one the screen implied for "
        "the level (guessRateScreen; logs from before it existed have none)")
def fig_guess(st):
    b = st.blocks
    if b.empty or "guessRateScreen" not in b or b["guessRateScreen"].isna().all():
        return None
    b = b[b["guessRateScreen"].notna()]
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.4), layout="constrained")
    for lv in LEVELS:
        g = b[b["levelType"] == lv]
        ax.scatter(g["guessRateScreen"], pd.to_numeric(g["cfg_guessRate"], errors="coerce"), s=16,
                   marker=LEVEL_MARKER[lv], color=LEVEL_COLOR[lv], label=LEVEL_LABEL[lv])
    lo = float(min(b["guessRateScreen"].min(), pd.to_numeric(b["cfg_guessRate"], errors="coerce").min())) - 0.01
    hi = float(max(b["guessRateScreen"].max(), pd.to_numeric(b["cfg_guessRate"], errors="coerce").max())) + 0.01
    ax.plot([lo, hi], [lo, hi], color=J.INK_2, lw=0.8, ls=(0, (3, 2)))
    ax.fill_between([lo, hi], [lo - 0.005, hi - 0.005], [lo + 0.005, hi + 0.005], color=J.GRID, alpha=0.6, lw=0)
    ax.set_xlabel("Chance rate the screen implies")
    ax.set_ylabel("guessRate in the config")
    grid_y(ax)
    top_legend(fig, level_handles([lv for lv in LEVELS if lv in set(b["levelType"])]), 3)
    return fig


@figure("L20_inventory", "quality",
        "Data inventory: every participant x condition - complete (converged or maxTrials), flagged (range edge, "
        "unreliable, base rate not held), abandoned, or missing - with the trials each block used")
def fig_inventory(st):
    b = st.blocks
    if b.empty:
        return None
    conds = st.conditions()
    pids = st.pids
    fig, ax = plt.subplots(figsize=(min(J.TEXT_W, 1.2 + 0.55 * len(conds)), 0.6 + 0.25 * len(pids)),
                           layout="constrained")
    colors = {"complete": J.OUTCOME_COLOR["hit"], "flagged": "#eda100", "abandoned": J.INK_2, "missing": "white"}
    for i, pid in enumerate(pids):
        for j, (lv, base, d) in enumerate(conds):
            g = b[(b["pid"] == pid) & (b["levelType"] == lv) & (b["baseFps"] == base) & (b["direction"] == d)]
            if g.empty:
                state, txt = "missing", ""
            else:
                r = g.iloc[0]
                state = "abandoned" if r["abandoned"] else ("flagged" if (r["rangeEdge"] or r["unreliable"] is True or
                                                                          r["deliveryShort"] is True) else "complete")
                txt = str(int(r["staircaseTrials"])) if pd.notna(r.get("staircaseTrials")) else ""
            y = len(pids) - 1 - i
            ax.add_patch(plt.Rectangle((j - 0.45, y - 0.4), 0.9, 0.8, facecolor=colors[state],
                                       edgecolor=J.AXIS if state == "missing" else "white", lw=0.8))
            if txt:
                ax.text(j, y, txt, ha="center", va="center", fontsize=J.FS_SMALL,
                        color="white" if state in ("complete", "abandoned") else J.INK)
    ax.set_xlim(-0.6, len(conds) - 0.4)
    ax.set_ylim(-0.6, len(pids) - 0.4)
    ax.set_xticks(range(len(conds)), [cond_label(lv, b_, d).replace(" ", "\n") for lv, b_, d in conds])
    ax.set_yticks(range(len(pids)), pids[::-1])
    for sp in ax.spines.values():
        sp.set_visible(False)
    top_legend(fig, [Patch(facecolor=c, edgecolor=J.AXIS, label=k) for k, c in colors.items()], 4)
    return fig


@figure("L21_practice", "quality",
        "Practice: hit rate on each practice pair, per level type practised (practice never feeds QUEST+)")
def fig_practice(st):
    s = st.shots[~st.shots["main"]]
    if s.empty:
        return None
    s = s.assign(pair=s["alteredFps"].astype(int).astype(str) + " vs " + s["baseFps"].astype(int).astype(str))
    pairs = list(dict.fromkeys(s.sort_values(["roundNumber"])["pair"]))
    levels = [lv for lv in LEVELS if lv in set(s["levelType"])]
    fig, ax = plt.subplots(figsize=(J.COL_W, 2.3), layout="constrained")
    w = 0.8 / max(1, len(levels))
    for k, lv in enumerate(levels):
        g = s[s["levelType"] == lv]
        for j, pr in enumerate(pairs):
            h = g[g["pair"] == pr]
            if h.empty:
                continue
            x = j + (k - (len(levels) - 1) / 2) * w
            p = h["isHit"].mean()
            lo, hi = J.wilson(int(h["isHit"].sum()), len(h))
            ax.bar(x, p, width=w * 0.9, color=LEVEL_COLOR[lv], edgecolor="white", lw=0.8)
            ax.plot([x, x], [lo, hi], color=J.INK, lw=0.8)
    ax.set_xticks(range(len(pairs)), pairs, fontsize=J.FS_SMALL)
    ax.set_ylim(0, 1.05)
    ax.set_ylabel("Hit rate")
    ax.set_xlabel("Practice pair (fps)")
    grid_y(ax)
    top_legend(fig, [Patch(color=LEVEL_COLOR[lv], label=LEVEL_LABEL[lv]) for lv in levels], 3)
    return fig


# ================================================================== tables and report

def condition_table(st):
    b = st.blocks[st.blocks["usable"]]
    rows = []
    for (lv, base, d), g in b.groupby(["levelType", "baseFps", "direction"]):
        for col, unit in (("weberPct", "weberPct"), ("jndDiffFps", "fps"), ("jndFrameTimeDiffMs", "ms")):
            v = g[col].dropna().to_numpy()
            m, lo, hi = J.mean_ci(v)
            rows.append(dict(levelType=lv, baseFps=int(base), direction=d, unit=unit, n=len(v), mean=m,
                             sd=float(np.std(v, ddof=1)) if len(v) > 1 else np.nan, ci95Lo=lo, ci95Hi=hi,
                             nRangeEdge=int(g["rangeEdge"].sum()), nUnreliable=int(g["unreliable"].fillna(False).sum())))
    return pd.DataFrame(rows)


def write_report(st, out, made, skipped, cond_tab, contrasts):
    L = ["# Level-type analysis\n"]
    L.append(f"Participants: {len(st.pids)}; main blocks: {len(st.blocks)} "
             f"({int(st.blocks['usable'].sum()) if len(st.blocks) else 0} usable); rounds: "
             f"{int(st.shots['main'].sum()) if len(st.shots) else 0} main, "
             f"{int((~st.shots['main']).sum()) if len(st.shots) else 0} practice.\n")
    if st.notes:
        L.append("## Notes\n")
        L += [f"- {n}" for n in st.notes]
        L.append("")
    if len(st.info):
        dbg = st.info[st.info["key"] == "UIManager.showDebug"]
        on = dbg[dbg["value"].astype(str).str.lower() == "true"]
        L.append("## Session settings (SessionInfo)\n")
        L.append(f"- SessionInfo files: {st.info['pid'].nunique()} of {len(st.pids)} sessions.")
        if len(on):
            L.append(f"- **Debug readout ON** in: {', '.join(on['pid'])} - it shows the difference being measured; "
                     "treat those sessions as invalid.")
        for key in ("CameraRig.parallax", "Weather.kind", "GameManager.worldScrollPeriod", "app.isEditor"):
            vals = st.info[st.info["key"] == key]
            if len(vals):
                L.append(f"- {key}: " + ", ".join(f"{r.pid}={r.value}" for r in vals.itertuples()))
        L.append("")
    if len(st.blocks):
        b = st.blocks
        L.append("## Flags\n")
        for col, what in (("abandoned", "abandoned"), ("rangeEdge", "estimate pressed against the range edge"),
                          ("unreliable", "final SD over 2x the stop rule"), ("deliveryShort", "base rate not held")):
            f = b[b[col].fillna(False).astype(bool)]
            if len(f):
                L.append(f"- {what}: " + ", ".join(f"{r.pid} {cond_label(r.levelType, r.baseFps, r.direction)}"
                                                    for r in f.itertuples()))
        L.append("")
    if len(cond_tab):
        L.append("## Thresholds per condition (usable blocks)\n")
        L.append("| condition | n | Weber % mean [95% CI] | diff fps mean | frame-time ms mean |")
        L.append("|---|---|---|---|---|")
        for (lv, base, d), g in cond_tab.groupby(["levelType", "baseFps", "direction"]):
            w = g[g["unit"] == "weberPct"].iloc[0]
            fp = g[g["unit"] == "fps"].iloc[0]
            ms = g[g["unit"] == "ms"].iloc[0]
            ci = f"[{w['ci95Lo']:.1f}, {w['ci95Hi']:.1f}]" if math.isfinite(w["ci95Lo"]) else ""
            L.append(f"| {cond_label(lv, base, d)} | {int(w['n'])} | {w['mean']:.1f} {ci} | {fp['mean']:.1f} | "
                     f"{ms['mean']:.2f} |")
        L.append("")
    if len(contrasts):
        L.append("## Level-type contrasts (within participant, Weber points)\n")
        L.append("| base, direction | contrast | n | mean diff [95% CI] | sign-flip p | Wilcoxon p |")
        L.append("|---|---|---|---|---|---|")
        for r in contrasts.itertuples():
            ci = f"[{r.ci95Lo:.1f}, {r.ci95Hi:.1f}]" if math.isfinite(r.ci95Lo) else ""
            L.append(f"| {r.baseFps} {r.direction} | {r.contrast} | {r.n} | {r.meanDiffWeberPts:.1f} {ci} | "
                     f"{J.apa_p(r.signFlipP)} | {J.apa_p(r.wilcoxonP)} |")
        L.append("\nWith few participants these are descriptive; the sign-flip test is exact for any n.\n")
    L.append("## Figures\n")
    for stem, group, desc in made:
        L.append(f"- `figures/{group}/{stem}.png` - {desc}")
    for stem, why in skipped:
        L.append(f"- {stem}: not drawn ({why})")
    (out / "report.md").write_text("\n".join(L) + "\n", encoding="utf-8")


# ================================================================== main

def main(argv=None):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", nargs="+", type=Path, default=[REPO / "Data" / "Logs"],
                    help="Folder(s) holding session folders. Default: Data/Logs")
    ap.add_argument("--out", type=Path, default=REPO / "Analysis" / "results", help="Output folder")
    ap.add_argument("--only", nargs="+", metavar="ID", help="Only these participants (P1, folder name or sessionId)")
    ap.add_argument("--exclude", nargs="+", metavar="ID", help="Leave these participants out")
    ap.add_argument("--no-frames", action="store_true", help="Skip the PlayerLogs (the frames figures)")
    ap.add_argument("--no-participants", action="store_true", help="Skip the per-participant staircase pages")
    ap.add_argument("--legacy-level", choices=LEVELS, help="Level of logs written before levelType existed")
    ap.add_argument("--formats", nargs="+", default=["png"], choices=["png", "svg", "pdf"])
    ap.add_argument("--dpi", type=int, default=300)
    ap.add_argument("--figs", nargs="+", metavar="PATTERN", help="Only figures whose stem matches, e.g. 'L0*'")
    ap.add_argument("--list", action="store_true", help="List the figures and stop")
    args = ap.parse_args(argv)

    if args.list:
        for stem, group, needs, _, desc in FIGURES:
            print(f"{stem:28} {group:14} {'(frames) ' if needs else ''}{desc}")
        return 0

    J.use_chi_style()
    log(f"Data: {', '.join(map(str, args.data))}\nOut:  {args.out}")
    st = Study(args.data, frames=not args.no_frames, legacy=args.legacy_level, only=args.only, exclude=args.exclude)
    if not st.pids:
        sys.exit("No frame-rate sessions found.")
    log(f"  {len(st.pids)} participant(s), {len(st.blocks)} main block(s), {len(st.shots)} round(s)")
    for n_ in st.notes:
        log(f"  {n_}")

    out = args.out
    (out / "tables").mkdir(parents=True, exist_ok=True)
    st.blocks.to_csv(out / "tables" / "blocks.csv", index=False, float_format="%.6g")
    st.shots.to_csv(out / "tables" / "shots.csv", index=False, float_format="%.6g")
    cond_tab = condition_table(st) if len(st.blocks) else pd.DataFrame()
    cond_tab.to_csv(out / "tables" / "conditions.csv", index=False, float_format="%.6g")
    contrasts = level_contrasts(st) if len(st.blocks) else pd.DataFrame()
    contrasts.to_csv(out / "tables" / "level_contrasts.csv", index=False, float_format="%.6g")

    made, skipped = [], []

    def wanted(stem):
        return not args.figs or any(fnmatch.fnmatch(stem, p) for p in args.figs)

    def save(fig, folder, stem):
        folder.mkdir(parents=True, exist_ok=True)
        for fmt in args.formats:
            fig.savefig(folder / f"{stem}.{fmt}", dpi=args.dpi)
        plt.close(fig)

    for stem, group, needs, fn, desc in FIGURES:
        if not wanted(stem):
            continue
        if needs and args.no_frames:
            skipped.append((stem, "--no-frames"))
            continue
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            try:
                fig = fn(st)
            except Exception as exc:  # noqa: BLE001 - one figure must not stop the rest
                log(f"  {stem}: FAILED ({type(exc).__name__}: {exc})")
                traceback.print_exc(limit=3)
                plt.close("all")
                skipped.append((stem, f"failed: {type(exc).__name__}: {exc}"))
                continue
        if fig is None:
            skipped.append((stem, "no data for it"))
            log(f"  {stem}: skipped (no data for it)")
            continue
        save(fig, out / "figures" / group, stem)
        made.append((stem, group, desc))
        log(f"  {stem}")

    if not args.no_participants and wanted("participant"):
        for pid in st.pids:
            fig = staircase_page(st, pid)
            if fig is not None:
                save(fig, out / "figures" / "participants", f"staircases_{pid}")
        made.append(("staircases_<pid>", "participants", "Every block's staircase per participant: the difference "
                     "each trial (hit / miss), QUEST+'s threshold and its SD"))

    write_report(st, out, made, skipped, cond_tab, contrasts)
    log(f"Done -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
