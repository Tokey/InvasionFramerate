#!/usr/bin/env python3
"""
JND analysis for the frame-rate study: CHI-ready figures, tables and statistics.

The game ("Invasion - Frame Rate") splits the screen at a hidden tower: one side runs at the
block's base frame rate, the other at base - diffFps. The participant flies the UFO across and
shoots where they think the boundary is; a shot within closeRadius of the tower base is a hit.
QUEST+ chooses diffFps every round and estimates the threshold theta (in fps) per block. Three
blocks per session, one per base rate (60, 144, 500 fps), in a Latin-square order.

Reads every participant folder under --data (each holds SessionLog_<id>.csv and
ShotLog_<id>.csv, and optionally PlayerLog_<id>.csv) and writes into --out:

    paper/                      figures drawn at final ACM sigconf size (column 3.337 in or
                                text 7.006 in), as 600-dpi PNG; paper/main holds the set chosen
                                for the paper (renumbered), paper/misc everything else
        Fig01_thresholds        threshold per base rate in fps and as a Weber fraction, paired
                                per participant, plus the paired Weber-fraction contrasts
        Fig02_psychometric      pooled data and pooled fit per base rate, individual fits behind
        Fig02b_detection_overlay  the three pooled fits corrected for chance, on one relative axis
        Fig03_convergence       posterior SD (relative to each block's stop rule) by trial, and
                                the trials each block needed
        Fig04_order_effects     threshold (Weber fraction) by block position in the Latin square
        Fig05_frame_timing      manipulation check: frame times per side, delivered vs requested
                                rate per round, the base rate each block achieved, late frames
        Fig06_outcomes          what every round ended in (hit / miss / time-out / crossing cap)
        Fig11_aiming            shot-tower distance against the frame-rate difference
        Fig12_exposure          boundary crossings before the shot, against the difference
                                (+ 12b binned means, 12c with a density bubble layer)
        Fig13_threshold_sd_sessions   every session's threshold +- posterior SD, per base rate
                                (13b: the same with every value printed)
        Fig14_slope_staircases  posterior slope after every trial, per block
        Fig16/18                shot-tower distance for hits / misses per participant
        Fig20/22                the same, pooled over participants (per base rate / merged on a
                                relative axis)
        Fig23-25                mean threshold +- average posterior SD and 95% CI: all blocks,
                                reliable blocks, converged blocks (+ 25b/c tertile splits)
        Fig27/29                Fig20 averaged in difference bins (all / converged blocks)
        Fig31/39                cumulative distribution of shot-tower distance (dots / lines)
        Fig32-37                posterior SD and slope summaries (all / reliable / converged)
        Fig41_score             points per round, per participant and base rate
        FigS2_threshold_units   the threshold as diffFps, altered rate, frame-time difference
                                (ms) and Weber fraction
        FigS3_sd_targets        trials needed to reach each posterior-SD target (x stop rule)
        FigS4_sd_all_runs       posterior SD of every block, one panel per participant
        FigS5_heatmaps          where the UFO was during live play (PlayerLog)
        FigS6_behaviour_all     every SessionLog block metric, small multiples
        FigS7_targeting         where shots landed relative to the tower, per difference bin
        FigS8_staircases        the difference QUEST+ placed on every trial, hit/miss, theta trace
    participants/<P#>_<folder>/ per-participant figures: curves, convergence, sd, sd_overlay,
                                parameters, composite, heatmap, roundmap, roundmap_relative
    tables/                     blocks.csv, trials.csv, responses.csv, conditions.csv,
                                stats.csv, sd_targets.csv, replay_check.csv, thresholds.tex
    report.md                   data quality and integrity checks, thresholds in every unit,
                                inferential results in APA style, sensitivity analyses,
                                frame-delivery and display checks
    captions.md                 draft captions and \\Description{} alt text for each paper figure

Thresholds hold the guess rate gamma FIXED. gamma is chance performance - the chance that a
blind shot lands on the tower, set by the screen geometry - not a property of the
participant, so it is never fitted: every threshold here is recomputed from the trial data
under QUEST+'s own model (Weibull in diffFps, gamma pinned, uniform priors over the logged
theta/beta/lambda grids, single-precision grids exactly as QuestPlusStaircase.cs builds
them), which reproduces the online estimate when gamma is the value the block ran with
(checked on every run, see report.md). gamma is read off each block's cfg_guessRate, so
sessions run with different values are each replayed with their own; --gamma overrides it
for every block, and every threshold, curve and trace is then recomputed under the new value.

Blocks that were abandoned (endReason "abandoned") are not measurements: they are listed in
the report and left out of every threshold summary and test. A block whose estimate is pressed
against an end of the difference range (rangeEdge "top"/"bottom") only says the threshold lies
beyond the range; it is flagged as unreliable, like a block whose final posterior SD stayed
above RELIABLE_SD_FACTOR x its stop rule, and is dropped in the sensitivity analyses.

Participants are the unit of analysis. Group results are means of per-participant
thresholds with 95% t-intervals across participants; pooled fits are drawn for illustration.

Usage
    python jnd_analysis.py
    python jnd_analysis.py --data "I:/JND - Framerate/Pre Pilot" --out "I:/JND - Framerate/Results"
    python jnd_analysis.py --gamma 0.235           # one fixed gamma for every block
    python jnd_analysis.py --no-frames             # skip the PlayerLogs
    python jnd_analysis.py --only P1 P3            # or folder names, e.g. 1_EHux
    python jnd_analysis.py --formats png svg       # png is the default; also svg, tif
    python jnd_analysis.py --figs "Fig01*" "Fig_roundmap*"   # rebuild only some figures

Requires: numpy, pandas, matplotlib, scipy
"""

from __future__ import annotations

import argparse
import fnmatch
import itertools
import math
import re
import sys
import textwrap
import warnings
from dataclasses import dataclass, field
from pathlib import Path

import matplotlib as mpl

mpl.use("Agg")

import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402
from matplotlib import font_manager  # noqa: E402
from matplotlib.colors import to_rgb  # noqa: E402
from matplotlib.gridspec import GridSpec  # noqa: E402
from matplotlib.legend_handler import HandlerTuple  # noqa: E402
from matplotlib.lines import Line2D  # noqa: E402
from matplotlib.patches import Patch  # noqa: E402
from matplotlib.ticker import FixedLocator, MaxNLocator, NullFormatter, ScalarFormatter  # noqa: E402
from scipy import optimize, stats  # noqa: E402

DEFAULT_DATA = Path(__file__).resolve().parent.parent / "Pre Pilot"

# ACM sigconf (acmart v2.20): \columnwidth = 241.15 pt, \textwidth = 506.30 pt. CHI wants
# figures drawn at final size rather than scaled, so every figure is built at one of these.
COL_W = 3.337
TEXT_W = 7.006

# The three settings: the base frame rate of a block. Each is played once per session.
FPS_LEVELS = (60, 144, 500)
CONDITIONS = FPS_LEVELS
COND_X = {60: 0.0, 144: 1.0, 500: 2.0}              # x of each setting on a condition axis

# A block counts as unreliable when its final posterior SD is above this multiple of its stop
# rule (cfg_stopSD, which differs by base rate), or when the estimate is pressed against an end
# of the difference range. Declared here, used for flagging and for the sensitivity analyses,
# never to silently drop data.
RELIABLE_SD_FACTOR = 2.0

# Colour carries the base frame rate everywhere, so 60 fps is the same blue in every figure.
# 60 and 500 keep the frame-time study's pair; 144 is a raspberry chosen to pass the dataviz
# validator on white against both (all pairs: worst protan/deutan dE 14.7, normal-vision dE
# 21.6, each >= 3:1 contrast). It shares the blue's luminance, so in greyscale 60 and 144
# part by marker and dash instead: every base rate also has its own marker shape and line
# style, and nothing is told apart by colour alone.
FPS_COLOR = {60: "#1c5cab", 144: "#ad1457", 500: "#eb6834"}
FPS_MARKER = {60: "o", 144: "s", 500: "D"}
FPS_MS = {60: 4.2, 144: 3.9, 500: 3.6}               # a square or diamond reads larger than a circle
FPS_LS = {60: "-", 144: (0, (3.6, 1.8)), 500: (0, (5.0, 1.4, 1.2, 1.4))}

INK, INK_2, MUTED = "#0b0b0b", "#52514e", "#898781"
GRID, AXIS = "#e4e3dc", "#8c8b85"
INDIVIDUAL = "#b4b3ab"      # participant-level lines: present, but behind the group result
SURFACE = "#ffffff"         # paper white, so a figure blends into the page

# Round outcomes are nominal and not frame rate, so they never borrow the fps hues: the hit
# takes the one accent, the kinds of failure are greys at clearly different luminance, so they
# also part in greyscale. Every segment wide enough carries its count as a direct label.
OUTCOME_ORDER = [("hit", "Hit"), ("miss", "Missed shot"), ("expired", "Timed out"),
                 ("crossingCap", "Crossing limit")]
OUTCOME_COLOR = {"hit": "#1baf7a", "miss": "#3d3c39", "expired": "#bdbcb4", "crossingCap": "#e3e2dc"}

LABEL_BOX = dict(facecolor=SURFACE, edgecolor="none", alpha=0.85, boxstyle="square,pad=0.15")

# Type sizes at print size. CHI sets no hard minimum, but 7 pt against the 9 pt body is the
# floor for anything a reader has to read; nothing in a figure goes below FS_SMALL.
FS_SMALL = 7.0      # in-panel annotations, value labels, table cells
FS_BASE = 7.5       # tick labels, legends
FS_LABEL = 8.0      # axis labels, panel titles

FONT_STACK = ["Linux Biolinum O", "Linux Biolinum", "Libertinus Sans", "Helvetica",
              "Arial", "Helvetica Neue", "Source Sans 3", "Segoe UI", "DejaVu Sans"]

# The game's geometry, for the chance rate a screen implies (GuessRateCalculator.cs): a 15 deg
# vertical field of view from 100 units in front of the play plane, towers dealt over the middle
# SPAWN_FRACTION of the view, a hit within CLOSE_RADIUS of the tower base.
CAM_FOV_DEG = 15.0
CAM_DIST = 100.0
SPAWN_FRACTION = 0.8
CLOSE_RADIUS = 5.5


def log(msg=""):
    print(msg, flush=True)


# ================================================================ style

def pick_font():
    """ACM's own sans (Biolinum) when it is installed, else the usual journal grotesques."""
    installed = {f.name for f in font_manager.fontManager.ttflist}
    for name in FONT_STACK:
        if name in installed:
            return name
    return "DejaVu Sans"


def use_chi_style():
    """Type and chrome for an ACM two-column paper.

    Sizes target the printed page: no text under 7 pt against the 9 pt body (ACM sets no
    minimum, but CHI reassigns papers it finds inaccessible), ticks and gridlines are
    hairlines one step off white, and data is the only saturated ink.
    """
    family = pick_font()
    mpl.rcParams.update({
        "font.family": "sans-serif",
        "font.sans-serif": [family] + FONT_STACK,
        # Maths in the body face, so theta/beta/gamma do not come out of a second typeface.
        "mathtext.fontset": "custom",
        "mathtext.rm": family,
        "mathtext.it": f"{family}:italic",
        "mathtext.bf": f"{family}:bold",
        "mathtext.default": "it",

        "font.size": FS_BASE,
        "axes.titlesize": FS_LABEL,
        "axes.titleweight": "bold",
        "axes.titlelocation": "left",
        "axes.titlepad": 4,
        "axes.labelsize": FS_LABEL,
        "axes.labelpad": 3,
        "xtick.labelsize": FS_BASE,
        "ytick.labelsize": FS_BASE,
        "legend.fontsize": FS_BASE,
        "legend.title_fontsize": FS_BASE,
        "figure.titlesize": 9,
        "figure.labelsize": FS_LABEL,

        "axes.linewidth": 0.6,
        "axes.edgecolor": AXIS,
        "axes.labelcolor": INK,
        "axes.titlecolor": INK,
        "axes.facecolor": SURFACE,
        "axes.spines.top": False,
        "axes.spines.right": False,
        "axes.axisbelow": True,
        "axes.grid": False,
        "grid.color": GRID,
        "grid.linewidth": 0.45,
        "grid.linestyle": "-",

        "xtick.color": AXIS, "ytick.color": AXIS,
        "xtick.labelcolor": INK_2, "ytick.labelcolor": INK_2,
        "xtick.major.width": 0.6, "ytick.major.width": 0.6,
        "xtick.minor.width": 0.4, "ytick.minor.width": 0.4,
        "xtick.major.size": 2.5, "ytick.major.size": 2.5,
        "xtick.minor.size": 1.5, "ytick.minor.size": 1.5,
        "xtick.direction": "out", "ytick.direction": "out",

        "lines.linewidth": 1.1,
        "lines.markersize": 3.5,
        "lines.solid_capstyle": "round",
        "lines.solid_joinstyle": "round",
        "errorbar.capsize": 2,

        "legend.frameon": False,
        "legend.handlelength": 1.8,
        "legend.handletextpad": 0.5,
        "legend.columnspacing": 1.3,
        "legend.borderaxespad": 0.2,
        "legend.labelcolor": INK_2,

        "figure.facecolor": SURFACE,
        "figure.dpi": 150,
        # Constrained layout spaces titles, tick labels, colour bars and "outside" legends
        # so they cannot collide; figures that need exact placement turn it off.
        "figure.constrained_layout.h_pad": 0.03,
        "figure.constrained_layout.w_pad": 0.03,
        "figure.constrained_layout.hspace": 0.04,
        "figure.constrained_layout.wspace": 0.04,
        "savefig.facecolor": SURFACE,
        "savefig.dpi": 600,
        "savefig.bbox": "tight",
        "savefig.pad_inches": 0.02,
        "ps.fonttype": 42,
        "svg.fonttype": "none",
        "image.interpolation": "nearest",
        "image.resample": False,
    })
    return family


def tint(color, amount):
    """Mix a colour toward white; amount 0 = the colour, 1 = white."""
    r, g, b = to_rgb(color)
    return (r + (1 - r) * amount, g + (1 - g) * amount, b + (1 - b) * amount)


def cond_label(fps, short=False):
    return f"{fps}" if short else f"{fps} fps"


def cond_handle(fps, hollow=False, ms=None, ls=""):
    return Line2D([], [], marker=FPS_MARKER[fps], ls=ls, ms=ms or FPS_MS[fps],
                  color=FPS_COLOR[fps], mfc=SURFACE if hollow else FPS_COLOR[fps],
                  mec=FPS_COLOR[fps] if hollow else SURFACE, mew=0.9 if hollow else 0.5,
                  label=cond_label(fps))


class KeyTuple(tuple):
    """Several legend handles drawn side by side under one label (HandlerTuple)."""
    label = ""


def condition_key(factory=None, fps_list=FPS_LEVELS):
    """One legend entry per base rate: colour, marker and dash together, so the key reads in
    greyscale and with any colour-vision deficiency."""
    factory = factory or cond_handle
    out = []
    for f in fps_list:
        h = factory(f)
        h.set_label(cond_label(f))
        out.append(h)
    return out


def line_handle(fps, lw=1.4, marker=True):
    return Line2D([], [], color=FPS_COLOR[fps], ls=FPS_LS[fps], lw=lw,
                  marker=FPS_MARKER[fps] if marker else "", ms=FPS_MS[fps] - 0.4, mec=SURFACE,
                  mew=0.5, label=cond_label(fps))


def title(ax, letter, text, pad=None):
    ax.set_title(f"({letter})  {text}" if letter else text, loc="left", pad=pad)


def no_xgrid(ax):
    ax.grid(axis="x", visible=False)


def log_axis(ax, axis, ticks):
    """A log axis that labels the given ticks as plain numbers, nothing else."""
    if axis == "y":
        ax.set_yscale("log")
        ax.yaxis.set_major_locator(FixedLocator(ticks))
        ax.yaxis.set_major_formatter(ScalarFormatter())
        ax.yaxis.set_minor_formatter(NullFormatter())
    else:
        ax.set_xscale("log")
        ax.xaxis.set_major_locator(FixedLocator(ticks))
        ax.xaxis.set_major_formatter(ScalarFormatter())
        ax.xaxis.set_minor_formatter(NullFormatter())


def nice_log_ticks(lo, hi):
    """1-2-5 ticks covering [lo, hi] for a log axis."""
    lo, hi = max(lo, 1e-6), max(hi, lo * 1.01)
    out = []
    for e in range(int(math.floor(math.log10(lo))) - 1, int(math.ceil(math.log10(hi))) + 1):
        for m in (1, 2, 5):
            v = m * 10 ** e
            if lo * 0.999 <= v <= hi * 1.001:
                out.append(v if v >= 1 else round(v, 6))
    return out or [lo, hi]


def stamp(fig, text):
    """Identification for per-participant figures - drawn even without titles, because a
    figure dragged out of its folder still has to say whose it is.

    Under constrained layout it is the figure's footer (supxlabel, left-aligned): the layout
    reserves room for it, so it never meets the top legend."""
    if fig.get_layout_engine() is not None:
        fig.supxlabel(text, x=0.0, ha="left", fontsize=FS_BASE, fontweight="bold", color=INK_2)
    else:
        lift = mpl.transforms.ScaledTranslation(0, 5 / 72, fig.dpi_scale_trans)
        fig.text(0.0, 1.0, text, ha="left", va="bottom", fontsize=FS_BASE, fontweight="bold",
                 color=INK_2, transform=fig.transFigure + lift)


_CORNERS = {
    "upper right": (0.98, 0.97, "right", "top"),
    "upper left": (0.02, 0.97, "left", "top"),
    "lower right": (0.98, 0.03, "right", "bottom"),
    "lower left": (0.02, 0.03, "left", "bottom"),
    "center right": (0.98, 0.5, "right", "center"),
    "upper center": (0.5, 0.97, "center", "top"),
}


def _densify(p, step=0.01):
    """Points along a polyline (axes fractions) every `step`, so a long straight segment
    still counts as covered where it runs, not only at its two ends."""
    p = p[np.all(np.isfinite(p), axis=1)]
    if len(p) < 2:
        return p
    out = [p]
    seg = np.diff(p, axis=0)
    n = np.clip((np.hypot(seg[:, 0], seg[:, 1]) / step).astype(int), 0, 200)
    for k in np.flatnonzero(n > 1):
        f = np.linspace(0, 1, n[k] + 1)[1:-1, None]
        out.append(p[k] + f * seg[k])
    return np.concatenate(out)


def _axes_points(ax):
    """Every plotted point of an axes, in axes-fraction coordinates (lines, scatters, bars
    and filled bands), for judging where an annotation would cover data."""
    pts = []
    to_axes = ax.transAxes.inverted()
    for ln in ax.lines:
        if ln.get_gid() == "ref":                    # reference lines may sit under a label
            continue
        xy = np.asarray(ln.get_xydata(), float)
        if xy.ndim == 2 and len(xy):
            p = to_axes.transform(ln.get_transform().transform(xy))
            pts.append(_densify(p) if ln.get_linestyle() not in ("None", "", " ") else p)
    for col in ax.collections:
        if col.get_gid() == "ref":
            continue
        if isinstance(col, mpl.collections.PathCollection):      # scatter: points at offsets
            off = np.asarray(col.get_offsets(), float)
            if off.ndim == 2 and len(off):
                pts.append(to_axes.transform(col.get_offset_transform().transform(off)))
            continue
        for path in col.get_paths()[:400]:                       # bands, polygons, segments
            v = np.asarray(path.vertices, float)
            if len(v):
                pts.append(_densify(to_axes.transform(col.get_transform().transform(v)), 0.03))
    for patch in ax.patches:
        if patch.get_gid() == "ref":
            continue
        try:
            bb = patch.get_window_extent()
        except Exception:  # noqa: BLE001 - a patch that cannot report extent covers nothing
            continue
        corners = np.array([[bb.x0, bb.y0], [bb.x1, bb.y1], [bb.x0, bb.y1], [bb.x1, bb.y0],
                            [(bb.x0 + bb.x1) / 2, (bb.y0 + bb.y1) / 2]])
        pts.append(to_axes.transform(corners))
    if not pts:
        return np.empty((0, 2))
    p = np.concatenate(pts)
    return p[np.all(np.isfinite(p), axis=1)]


def place_note(ax, text, candidates=("upper right", "upper left", "lower right", "lower left"),
               box=True, **kw):
    """Put a short annotation in whichever candidate corner covers the fewest data points.

    Call it after the panel's data and limits are final. Ties keep the order given, so the
    first candidate is the preferred spot."""
    kw.setdefault("fontsize", FS_SMALL)
    kw.setdefault("color", INK)
    kw.setdefault("zorder", 7)
    if box:
        kw.setdefault("bbox", LABEL_BOX)
    x, y, ha, va = _CORNERS[candidates[0]]
    t = ax.text(x, y, text, transform=ax.transAxes, ha=ha, va=va, **kw)
    fig = ax.figure
    renderer = fig.canvas.get_renderer()
    ext = t.get_window_extent(renderer).transformed(ax.transAxes.inverted())
    w, h = ext.width + 0.02, ext.height + 0.02
    pts = _axes_points(ax)
    mx = 6 / max(1.0, ax.get_window_extent(renderer).width)
    my = 6 / max(1.0, ax.get_window_extent(renderer).height)
    best, best_n = candidates[0], None
    for name in candidates:
        cx, cy, cha, cva = _CORNERS[name]
        x0 = cx - w if cha == "right" else (cx - w / 2 if cha == "center" else cx)
        y0 = cy - h if cva == "top" else (cy - h / 2 if cva == "center" else cy)
        n = int(((pts[:, 0] >= x0 - mx) & (pts[:, 0] <= x0 + w + mx) &
                 (pts[:, 1] >= y0 - my) & (pts[:, 1] <= y0 + h + my)).sum()) if len(pts) else 0
        if best_n is None or n < best_n:
            best, best_n = name, n
    x, y, ha, va = _CORNERS[best]
    t.set_position((x, y))
    t.set_ha(ha)
    t.set_va(va)
    return t


LEGEND_BOX = dict(frameon=True, facecolor=SURFACE, edgecolor=INK_2, framealpha=1.0, fancybox=False,
                  borderpad=0.4, borderaxespad=0.3)
_LEGEND_LOCS = ("upper right", "upper left", "upper center", "lower right", "lower left",
                "center right", "center left", "lower center", "center")


def fig_legend_top(fig, handles, ncol, inside=True, **kw):
    """The figure's legend. With `inside` the choice is deferred to save(), which puts it in
    a white box inside whichever panel has room for it without covering data or labels
    (place_legend); only when no panel has room does it go above all panels, where
    constrained layout ("outside") makes the panels give way."""
    kw.setdefault("columnspacing", 1.2)
    kw.setdefault("handletextpad", 0.4)
    if inside:
        fig._legend_request = (list(handles), ncol, kw)
        return None
    kw.setdefault("frameon", False)
    loc = "outside upper center" if fig.get_layout_engine() is not None else "upper center"
    return fig.legend(handles=handles, loc=loc, ncol=ncol, **kw)


def _text_points(ax, renderer):
    """Points over every text drawn in an axes (value labels, notes), in axes fractions,
    so a legend is kept off them as well as off the data."""
    to_axes = ax.transAxes.inverted()
    pts = []
    for t in ax.texts:
        if not t.get_visible() or not t.get_text().strip():
            continue
        bb = t.get_window_extent(renderer)
        xs, ys = np.linspace(bb.x0, bb.x1, 6), np.linspace(bb.y0, bb.y1, 3)
        pts.append(to_axes.transform(np.array([(x, y) for x in xs for y in ys])))
    return np.concatenate(pts) if pts else np.empty((0, 2))


def place_legend(fig):
    """Carry out a deferred fig_legend_top: try every panel (in reading order), legend shape
    (the requested columns, then fewer) and position, and keep the first that fits inside
    the panel and covers no plotted point or text. Falls back to a boxed legend on top."""
    req = getattr(fig, "_legend_request", None)
    if req is None:
        return
    handles, ncol, kw = req
    fig._legend_request = None
    kw = {**kw, **LEGEND_BOX, "handler_map": {tuple: HandlerTuple(ndivide=None, pad=0.3)}}
    kw["labels"] = [h.label if isinstance(h, KeyTuple) else h.get_label() for h in handles]
    fig.canvas.draw()
    renderer = fig.canvas.get_renderer()
    # A heatmap fills its panel with cells the point test cannot see, so it never hosts one.
    axes = [ax for ax in fig.axes if ax.get_visible() and ax.axison and ax.get_legend() is None
            and (ax.lines or ax.collections or ax.patches) and not ax.images
            and not any(isinstance(c, mpl.collections.QuadMesh) for c in ax.collections)]
    axes.sort(key=lambda a: (-round(a.get_position().y1, 2), a.get_position().x0))
    shapes = list(dict.fromkeys([min(ncol, len(handles))] + list(range(min(ncol, len(handles)) - 1, 0, -1))))
    for ax in axes:
        pts = _axes_points(ax)
        txt = _text_points(ax, renderer)
        pts = np.concatenate([pts, txt]) if len(txt) else pts
        box = ax.get_window_extent(renderer)
        mx, my = 4 / max(1.0, box.width), 4 / max(1.0, box.height)
        for nc in shapes:
            for loc in _LEGEND_LOCS:
                leg = ax.legend(handles=handles, loc=loc, ncol=nc, **kw)
                bb = leg.get_window_extent(renderer)
                if bb.width > box.width - 4 or bb.height > box.height - 4:
                    leg.remove()
                    break                       # this shape does not fit the panel anywhere
                e = bb.transformed(ax.transAxes.inverted())
                hit = len(pts) and bool(((pts[:, 0] >= e.x0 - mx) & (pts[:, 0] <= e.x1 + mx) &
                                         (pts[:, 1] >= e.y0 - my) & (pts[:, 1] <= e.y1 + my)).any())
                if not hit:
                    leg.set_zorder(20)
                    leg.get_frame().set_linewidth(0.6)
                    return
                leg.remove()
    loc = "outside upper center" if fig.get_layout_engine() is not None else "upper center"
    leg = fig.legend(handles=handles, loc=loc, ncol=ncol, **kw)
    leg.get_frame().set_linewidth(0.6)


@dataclass
class FigureDoc:
    """A paper figure plus the words that go with it."""
    stem: str
    fig: object
    caption: str
    alt: str


def printed_width(fig):
    """Width in inches the saved (tight-cropped) figure will have on the page."""
    fig.canvas.draw()
    bb = fig.get_tightbbox(fig.canvas.get_renderer())
    pad = mpl.rcParams["savefig.pad_inches"]
    return bb.width + 2 * pad, bb.height + 2 * pad


def save(fig, out_dir, stem, formats, dpi):
    """Write the figure in every requested format.

    Tight cropping means anything drawn outside the canvas widens the file; a figure wider
    than the column/text width it was drawn for gets scaled down by LaTeX and its 7 pt text
    shrinks with it, so that case is reported rather than silently written."""
    out_dir.mkdir(parents=True, exist_ok=True)
    place_legend(fig)
    width, height = printed_width(fig)
    nominal = fig.get_figwidth()
    target_w = COL_W if nominal <= COL_W + 0.05 else TEXT_W
    if width > max(target_w, nominal) * 1.01:
        log(f"  WARNING {stem}: prints {width:.2f} in wide, more than the {target_w:.3f} in "
            f"{'column' if target_w == COL_W else 'text'} width - something overflows the canvas")
    written = []
    for fmt in formats:
        target = out_dir / f"{stem}.{fmt}"
        kw = {"dpi": dpi}
        if fmt in ("tif", "tiff"):
            kw["pil_kwargs"] = {"compression": "tiff_lzw"}
        fig.savefig(target, **kw)
        written.append(target)
    plt.close(fig)
    return written


def cond_axis(ax, fps_list=FPS_LEVELS, label=True):
    """x positions for the base rates, with the rate as the tick label."""
    xs = [COND_X[f] for f in fps_list]
    ax.set_xticks(xs, [str(f) for f in fps_list])
    ax.set_xlim(min(xs) - 0.6, max(xs) + 0.7)
    if label:
        ax.set_xlabel("Base frame rate (fps)")
    ax.tick_params(axis="x", length=0)
    no_xgrid(ax)
    return xs


# ================================================================ numbers

def mean_ci(values, conf=0.95):
    """Mean with a t-interval across participants (NaN bounds below n = 2)."""
    x = np.asarray(values, dtype=float)
    x = x[np.isfinite(x)]
    n = len(x)
    if n == 0:
        return np.nan, np.nan, np.nan
    m = float(x.mean())
    if n < 2:
        return m, np.nan, np.nan
    half = stats.t.ppf(0.5 + conf / 2, n - 1) * x.std(ddof=1) / math.sqrt(n)
    return m, m - half, m + half


def geo_mean_ci(values, conf=0.95):
    """Geometric mean with the back-transformed t-interval of log10 values."""
    v = np.asarray(values, float)
    v = v[np.isfinite(v) & (v > 0)]
    if not len(v):
        return np.nan, np.nan, np.nan
    m, lo, hi = mean_ci(np.log10(v), conf)
    return 10 ** m, (10 ** lo if np.isfinite(lo) else np.nan), (10 ** hi if np.isfinite(hi) else np.nan)


def wilson(k, n, z=1.959964):
    if n <= 0:
        return np.nan, np.nan
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return c - h, c + h


def apa_p(p):
    if p is None or not np.isfinite(p):
        return "p = n/a"
    return "p < .001" if p < 0.001 else "p = " + f"{p:.3f}".lstrip("0")


def fmt_num(v, digits=2):
    return "n/a" if v is None or not np.isfinite(v) else f"{v:.{digits}f}"


def sign_flip_p(d):
    """Exact two-sided sign-flip permutation p for a mean difference of zero.

    With N participants there are only 2^N ways to flip the signs of the paired differences
    (256 at N = 8), so the null distribution is enumerated, not sampled. It needs no
    normality assumption - the t-test's weak point when one block is far out."""
    d = np.asarray(d, float)
    d = d[np.isfinite(d)]
    n = len(d)
    if n < 2 or n > 20 or not np.any(d):
        return np.nan
    signs = 1 - 2 * ((np.arange(2 ** n)[:, None] >> np.arange(n)) & 1)
    null = np.abs(signs @ d) / n
    return float(np.mean(null >= abs(d.mean()) - 1e-12))


def shapiro_p(d):
    d = np.asarray(d, float)
    d = d[np.isfinite(d)]
    if len(d) < 3 or np.ptp(d) == 0:
        return np.nan
    return float(stats.shapiro(d).pvalue)


def holm(pvals):
    """Holm step-down adjusted p values (NaN stays NaN)."""
    p = np.asarray(pvals, float)
    out = np.full(len(p), np.nan)
    ok = np.flatnonzero(np.isfinite(p))
    if not len(ok):
        return out
    order = ok[np.argsort(p[ok])]
    m = len(order)
    running = 0.0
    for i, k in enumerate(order):
        running = max(running, (m - i) * p[k])
        out[k] = min(1.0, running)
    return out


def paired_contrast(a, b):
    """b - a for paired samples: mean, 95% CI, t, df, p, Cohen's d_z, and the exact
    sign-flip permutation p as an assumption-free check."""
    a, b = np.asarray(a, float), np.asarray(b, float)
    ok = np.isfinite(a) & np.isfinite(b)
    d = b[ok] - a[ok]
    n = len(d)
    out = {"n": n, "mean_a": float(a[ok].mean()) if n else np.nan,
           "mean_b": float(b[ok].mean()) if n else np.nan}
    m, lo, hi = mean_ci(d)
    out.update(diff=m, ci_lo=lo, ci_hi=hi)
    if n >= 2 and d.std(ddof=1) > 0:
        sd = d.std(ddof=1)
        t = m / (sd / math.sqrt(n))
        out.update(t=t, df=n - 1, p=2 * stats.t.sf(abs(t), n - 1), dz=m / sd)
    else:
        out.update(t=np.nan, df=n - 1, p=np.nan, dz=np.nan)
    out.update(p_perm=sign_flip_p(d), shapiro_p=shapiro_p(d))
    return out


def _rm_F(Y):
    """F of a one-way repeated-measures ANOVA on complete participants x levels data."""
    n, k = Y.shape
    grand = Y.mean()
    ss_cond = n * float(((Y.mean(axis=0) - grand) ** 2).sum())
    ss_subj = k * float(((Y.mean(axis=1) - grand) ** 2).sum())
    ss_err = float(((Y - grand) ** 2).sum()) - ss_cond - ss_subj
    df1, df2 = k - 1, (n - 1) * (k - 1)
    F = (ss_cond / df1) / (ss_err / df2) if ss_err > 1e-12 else (np.inf if ss_cond > 0 else np.nan)
    return F, ss_cond, ss_err, df1, df2


def rm_anova_oneway(Y, n_perm=20000, seed=20261023):
    """One-way repeated-measures ANOVA over the base rates for a participants x levels matrix
    (columns in FPS_LEVELS order). Participants with a missing cell are dropped (the n says how
    many were used). With three levels sphericity can fail, so the Greenhouse-Geisser and
    Huynh-Feldt epsilons and corrected p values are reported beside the uncorrected one; a
    within-participant permutation p (condition labels shuffled inside each participant,
    exact when the patterns are few) and the Friedman test need no normality."""
    Y = np.asarray(Y, float)
    Y = Y[np.all(np.isfinite(Y), axis=1)] if Y.ndim == 2 else np.empty((0, 0))
    n = len(Y)
    k = Y.shape[1] if Y.ndim == 2 and n else len(FPS_LEVELS)
    row = {"effect": "Base frame rate", "n": n, "df1": k - 1, "df2": (n - 1) * (k - 1),
           "F": np.nan, "p": np.nan, "eta_p2": np.nan, "eps_gg": np.nan, "p_gg": np.nan,
           "eps_hf": np.nan, "p_hf": np.nan, "p_perm": np.nan, "friedman_p": np.nan}
    if n < 2 or k < 2:
        return row
    F, ss_cond, ss_err, df1, df2 = _rm_F(Y)
    row.update(F=F, eta_p2=ss_cond / (ss_cond + ss_err) if (ss_cond + ss_err) > 0 else np.nan)
    row["p"] = float(stats.f.sf(F, df1, df2)) if np.isfinite(F) else (0.0 if F == np.inf else np.nan)
    # Sphericity: epsilon from the double-centred covariance of the levels.
    S = np.cov(Y, rowvar=False, ddof=1)
    C = np.eye(k) - np.ones((k, k)) / k
    lam = np.linalg.eigvalsh(C @ S @ C)
    if lam.sum() > 0:
        eps = float(lam.sum() ** 2 / ((k - 1) * (lam ** 2).sum()))
        eps = min(1.0, max(1.0 / (k - 1), eps))
        hf = (n * (k - 1) * eps - 2) / ((k - 1) * (n - 1 - (k - 1) * eps)) if n - 1 - (k - 1) * eps > 0 else 1.0
        hf = min(1.0, max(eps, hf))
        row.update(eps_gg=eps, eps_hf=hf)
        if np.isfinite(F):
            row["p_gg"] = float(stats.f.sf(F, eps * df1, eps * df2))
            row["p_hf"] = float(stats.f.sf(F, hf * df1, hf * df2))
    # Permutation: shuffle the levels within each participant.
    perms = np.array(list(itertools.permutations(range(k))))
    total = len(perms) ** n
    if np.isfinite(F):
        if total <= 200000:
            idx = np.array(np.unravel_index(np.arange(total), (len(perms),) * n)).T
        else:
            rng = np.random.default_rng(seed)
            idx = rng.integers(0, len(perms), size=(n_perm, n))
        null = np.empty(len(idx))
        for j, choice in enumerate(idx):
            Yp = np.take_along_axis(Y, perms[choice], axis=1)
            null[j] = _rm_F(Yp)[0]
        row["p_perm"] = float(np.mean(null >= F - 1e-9))
    if n >= 2 and k >= 3:
        try:
            row["friedman_p"] = float(stats.friedmanchisquare(*Y.T).pvalue)
        except ValueError:
            pass
    return row


def linear_trend(Y):
    """Per-participant linear trend over ordered positions (columns), tested against 0."""
    Y = np.asarray(Y, float)
    Y = Y[np.all(np.isfinite(Y), axis=1)] if Y.ndim == 2 else np.empty((0, 0))
    k = Y.shape[1] if Y.ndim == 2 and len(Y) else 0
    if len(Y) < 2 or k < 2:
        return {"n": len(Y), "slope": np.nan, "ci_lo": np.nan, "ci_hi": np.nan, "t": np.nan,
                "df": len(Y) - 1, "p": np.nan}
    w = np.arange(k) - (k - 1) / 2
    slopes = Y @ w / (w @ w)                          # least-squares slope per participant
    m, lo, hi = mean_ci(slopes)
    sd = slopes.std(ddof=1)
    t = m / (sd / math.sqrt(len(slopes))) if sd > 0 else np.nan
    return {"n": len(slopes), "slope": m, "ci_lo": lo, "ci_hi": hi, "t": t,
            "df": len(slopes) - 1,
            "p": 2 * stats.t.sf(abs(t), len(slopes) - 1) if np.isfinite(t) else np.nan}


# ================================================================ QUEST+ model

F32 = np.float32


def weibull(x, theta, beta, lam, gamma):
    """QUEST+'s psychometric function, as QuestPlusStaircase.cs evaluates it.

    P(hit) = gamma + (1 - gamma - lambda) * (1 - exp(-(x / theta)^beta)),  x = diffFps

    gamma is the chance rate and is held FIXED; theta (threshold), beta (slope) and lambda
    (lapse) are what the posterior is over. theta is where the guess-corrected detection
    probability reaches 1 - 1/e = 63.2 %, whatever gamma is, so thresholds stay comparable
    between screens with different chance rates.
    """
    x = np.asarray(x, dtype=float)
    with np.errstate(divide="ignore", invalid="ignore", over="ignore"):
        inner = np.where(x > 0, np.power(np.maximum(x, 0) / theta, beta), 0.0)
    return gamma + (1.0 - gamma - lam) * (1.0 - np.exp(-inner))


def linspace32(lo, hi, n):
    """QuestPlusConfig.Linspace in single precision: lo + (hi - lo) * i / (n - 1)."""
    lo, hi, n = F32(lo), F32(hi), max(1, int(n))
    if n == 1:
        return np.array([lo], dtype=np.float32)
    d = F32(hi - lo)
    return np.array([F32(lo + F32(F32(d * F32(i)) / F32(n - 1))) for i in range(n)], dtype=np.float32)


def effective_config(row):
    """The block's QUEST+ configuration as the game used it: the cfg_ echo re-validated the
    way StudyConfig.Validate does (it pulls maxDiffFps down onto the step grid, e.g. 143 ->
    142 in 2-fps steps, and below the base rate when the altered side runs slower)."""
    g = lambda k, d=np.nan: row.get(f"cfg_{k}", d)  # noqa: E731
    base = int(g("baseFps"))
    direction = str(g("direction", "down")).strip().lower()
    mn, mx, step = int(g("minDiffFps")), int(g("maxDiffFps")), int(g("diffStepFps"))
    mn = max(1, mn)
    if direction == "down" and base - mx < 1:
        mx = base - 1
    step = max(1, step)
    if mx > mn and (mx - mn) % step != 0:
        mx = mn + (mx - mn) // step * step
    mx = max(mx, mn)
    return {
        "baseFps": base, "direction": direction, "minDiffFps": mn, "maxDiffFps": mx,
        "diffStepFps": step, "threshCount": max(2, int(g("threshCount"))),
        "slopeMin": float(g("slopeMin")), "slopeMax": float(g("slopeMax")),
        "slopeCount": max(1, int(g("slopeCount"))),
        "lapseMin": float(g("lapseMin")), "lapseMax": float(g("lapseMax")),
        "lapseCount": max(1, int(g("lapseCount"))),
        "guessRate": float(min(1.0, max(0.0, float(g("guessRate"))))),
        "maxTrials": max(1, int(g("maxTrials"))), "minTrials": max(0, int(g("minTrials"))),
        "stopSD": max(0.0, float(g("stopSD"))),
    }


@dataclass
class Grid:
    stim: np.ndarray       # the differences QUEST+ could place (float32 of whole fps)
    theta: np.ndarray      # float32, as the C# builds them
    beta: np.ndarray
    lam: np.ndarray
    stop_sd: float
    min_trials: int
    max_trials: int
    base: int
    lo: int
    hi: int

    @classmethod
    def from_cfg(cls, row):
        c = effective_config(row)
        stim = np.arange(c["minDiffFps"], c["maxDiffFps"] + 1, c["diffStepFps"]).astype(np.float32)
        return cls(stim,
                   linspace32(c["minDiffFps"], c["maxDiffFps"], c["threshCount"]),
                   linspace32(c["slopeMin"], c["slopeMax"], c["slopeCount"]),
                   linspace32(c["lapseMin"], c["lapseMax"], c["lapseCount"]),
                   c["stopSD"], c["minTrials"], c["maxTrials"], c["baseFps"],
                   c["minDiffFps"], c["maxDiffFps"])

    @classmethod
    def spanning(cls, grids):
        """One grid wide enough for every block of a condition (blocks run under different
        configs differ in their difference range): for the pooled observer."""
        lo, hi = min(g.lo for g in grids), max(g.hi for g in grids)
        n = max(len(g.theta) for g in grids)
        g0 = grids[0]
        return cls(np.unique(np.concatenate([g.stim for g in grids])), linspace32(lo, hi, n),
                   g0.beta, g0.lam, g0.stop_sd, g0.min_trials, g0.max_trials, g0.base, lo, hi)


def grid_quantile(marginal, grid, p, centered=True):
    """A quantile of the theta marginal, interpolated so it does not snap to the grid.

    centered=False is QuestPlusStaircase.ThresholdQuantile exactly: cell t's mass spread
    over [theta_t, theta_t+1). That is what the SessionLog's median and CI95 hold, and it
    sits half a grid step high - the posterior MEAN treats the same mass as a point at
    theta_t. centered=True spreads it over the half-way points either side, the convention
    consistent with the mean, and is what this script reports; the C# form is kept for the
    replay check against the logs."""
    grid = np.asarray(grid, float)
    n = len(grid)
    if centered and n > 1:
        mids = (grid[:-1] + grid[1:]) / 2
        lo_edges = np.r_[grid[0], mids]
        hi_edges = np.r_[mids, grid[-1]]
    else:
        lo_edges = grid
        hi_edges = np.r_[grid[1:], grid[-1]]
    cum = 0.0
    for t in range(n):
        nxt = cum + marginal[t]
        if nxt >= p or t == n - 1:
            within = (p - cum) / marginal[t] if marginal[t] > 1e-12 else 0.0
            within = min(1.0, max(0.0, within))
            return float(lo_edges[t] + within * (hi_edges[t] - lo_edges[t]))
        cum = nxt
    return float(grid[-1])


class Posterior:
    """The QUEST+ posterior over (theta, beta, lambda) with gamma fixed - multiply and
    normalise, exactly as the C# does (x / theta in single precision, the rest in double),
    so replaying a block's responses reproduces the logged estimates."""

    def __init__(self, grid, gamma):
        self.grid, self.gamma = grid, float(gamma)
        self.T32 = grid.theta[:, None, None]
        self.B = grid.beta.astype(float)[None, :, None]
        self.L = grid.lam.astype(float)[None, None, :]
        shape = (len(grid.theta), len(grid.beta), len(grid.lam))
        self.p = np.full(shape, 1.0 / float(np.prod(shape)))
        # The 50 % guess-corrected detection point of every grid Weibull.
        self.x50 = grid.theta.astype(float)[:, None, None] * np.log(2.0) ** (1.0 / self.B)

    def psi(self, x, gamma=None):
        g = self.gamma if gamma is None else float(gamma)
        ratio = (F32(x) / self.T32).astype(np.float32).astype(float)
        return g + (1.0 - g - self.L) * (1.0 - np.exp(-np.power(ratio, self.B)))

    def update(self, x, hit, gamma=None):
        q = self.psi(x, gamma)
        self.p *= q if hit else (1.0 - q)
        total = self.p.sum()
        if total > 0:
            self.p /= total

    def next_stimulus(self):
        """The entropy-minimising placement QUEST+ makes after this posterior (first minimum
        wins), to check the logged difference of every trial against the procedure."""
        best, best_h = None, math.inf
        flat = self.p.ravel()
        for x in self.grid.stim:
            q = self.psi(x).ravel()
            p_hit = float(flat @ q)
            h = 0.0
            for lik, w in ((q, p_hit), (1.0 - q, 1.0 - p_hit)):
                post = flat * lik
                s = post.sum()
                if s <= 0:
                    continue
                post = post / s
                post = post[post > 0]
                h += w * float(-(post * np.log(post)).sum())
            if h < best_h:
                best, best_h = float(x), h
        return best

    def light(self):
        m = self.p.sum(axis=(1, 2))
        th = self.grid.theta.astype(float)
        mean = float(m @ th)
        return {"theta": mean,
                "sd": float(math.sqrt(max(0.0, float(m @ (th - mean) ** 2)))),
                "beta": float(self.p.sum(axis=(0, 2)) @ self.grid.beta.astype(float)),
                "lam": float(self.p.sum(axis=(0, 1)) @ self.grid.lam.astype(float))}

    def summary(self):
        out = self.light()
        m = self.p.sum(axis=(1, 2))
        th = self.grid.theta.astype(float)
        q975_cs = grid_quantile(m, th, 0.975, centered=False)
        q025_cs = grid_quantile(m, th, 0.025, centered=False)
        edge = ""
        if len(th) >= 3:
            edge = "top" if q975_cs >= th[-2] else ("bottom" if q025_cs <= th[1] else "")
        out.update(median=grid_quantile(m, th, 0.5), mode=float(th[int(np.argmax(m))]),
                   ci_lo=grid_quantile(m, th, 0.025), ci_hi=grid_quantile(m, th, 0.975),
                   x50=float((self.p * self.x50).sum()), range_edge=edge,
                   # The C#'s convention, only to check the replay against the SessionLog.
                   median_cs=grid_quantile(m, th, 0.5, centered=False),
                   ci_lo_cs=q025_cs, ci_hi_cs=q975_cs)
        return out


def mle_weibull(x, y, gamma, lam_max=0.06):
    """Maximum-likelihood refit with gamma fixed, as a robustness check on the posterior.

    theta and beta free (log-parameterised), lambda bounded to the QUEST+ prior range
    [0, lam_max]. Several starts, because a QUEST+ block piles trials near threshold and
    leaves the slope weakly constrained. gamma may be one value or one per trial."""
    x, y = np.asarray(x, float), np.asarray(y, float)
    gamma = np.broadcast_to(np.asarray(gamma, float), x.shape)
    if len(x) < 5 or np.ptp(x) == 0:
        return {"theta": np.nan, "beta": np.nan, "lam": np.nan, "ok": False, "at_bound": True,
                "bound": "too few trials"}

    def nll(par):
        q = np.clip(weibull(x, math.exp(par[0]), math.exp(par[1]), par[2], gamma), 1e-9, 1 - 1e-9)
        return -float((y * np.log(q) + (1 - y) * np.log(1 - q)).sum())

    bounds = [(math.log(0.5), math.log(2000.0)), (math.log(0.3), math.log(25.0)), (0.0, lam_max)]
    best = None
    for th0 in np.percentile(x, [30, 50, 70]):
        for be0 in (1.5, 3.0, 6.0):
            r = optimize.minimize(nll, [math.log(max(th0, 1.0)), math.log(be0), 0.02],
                                  method="L-BFGS-B", bounds=bounds)
            if best is None or r.fun < best.fun:
                best = r
    th, be, la = math.exp(best.x[0]), math.exp(best.x[1]), float(best.x[2])
    # A parameter on its box edge means the box, not the data, set it.
    names = ("theta", "beta", "lambda")
    edge = [n for n, v, bb in zip(names, best.x, bounds)
            if any(abs(v - b) < 1e-3 for b in bb) and not (n == "lambda" and abs(v) < 1e-3)]
    return {"theta": th, "beta": be, "lam": la, "ok": bool(best.success),
            "at_bound": bool(edge), "bound": ",".join(edge)}


def screen_gamma(width_px, height_px, inset=0.0, radius=CLOSE_RADIUS, spawn=SPAWN_FRACTION):
    """Chance that a blind shot lands within `radius` of the tower, for a screen of this
    shape (GuessRateCalculator.cs): shots can land anywhere in the UFO's flight band (the full
    view, or inset by `inset` of its width at each side in builds that clamped the UFO there),
    towers are dealt over the middle `spawn` of the view, and a tower near an edge carries a
    window partly over ground the UFO cannot reach."""
    if not (width_px and height_px):
        return np.nan
    W = 2 * math.tan(math.radians(CAM_FOV_DEG / 2)) * CAM_DIST * width_px / height_px
    u0, u1 = inset * W, (1 - inset) * W
    m = (1 - spawn) / 2
    t = np.linspace(m * W, (1 - m) * W, 20001)
    reach = np.clip(np.minimum(u1, t + radius) - np.maximum(u0, t - radius), 0, None)
    return float(reach.mean() / (u1 - u0))


# ================================================================ data

TEXT_BLOCK_COLS = {"runId", "latinOrder", "direction", "frameLimiter", "resolution", "startIso",
                   "endIso", "endReason", "jndRangeEdge", "cfg_direction", "cfg_alteredSide",
                   "cfg_frameLimiter"}
TEXT_SHOT_COLS = {"runId", "latinOrder", "direction", "frameLimiter", "phase", "phaseStartIso",
                  "alteredSide", "outcome", "side", "cfg_direction", "cfg_alteredSide",
                  "cfg_frameLimiter"}


@dataclass
class FrameSummary:
    """What the analysis needs from a PlayerLog, kept small."""
    normal: pd.DataFrame          # per block x side: frame-time statistics of live frames
    hist: dict                    # (fps, side) -> counts of live frame times over FRAME_BINS
    err_hist: dict                # (fps, side) -> counts of (frame time - target) over ERR_BINS
    occupancy: dict               # fps -> live seconds per OCC_YE x OCC_XE cell (x from the tower)
    live_sec: dict                # fps -> seconds of live play
    crossings: pd.DataFrame       # every boundary crossing in live play, with where the UFO was
    paths: pd.DataFrame           # the UFO's live path per round, thinned to PATH_HZ


FRAME_BINS = np.geomspace(0.8, 120.0, 140)
ERR_BINS = np.linspace(-3.0, 12.0, 151)

# Positions in the play field relative to the round's tower (the boundary): the camera pans
# between rounds, so world x says little; x - towerX is where the player looked for it.
FIELD_X = (-40.0, 40.0)
FIELD_Y = (0.0, 16.0)
OCC_XE = np.arange(FIELD_X[0], FIELD_X[1] + 1e-9, 1.0)
OCC_YE = np.arange(FIELD_Y[0], FIELD_Y[1] + 1e-9, 1.0)
TRACK_HZ = 100.0      # occupancy is sampled on a uniform clock, so a 500 fps block (8x the
                      # rows) weighs the same per second as a 60 fps one
PATH_HZ = 125.0       # path resolution kept for the round maps


def _runs(mask, *splits):
    """(start, stop) inclusive of every contiguous True run in mask, also split wherever one
    of `splits` changes value."""
    idx = np.flatnonzero(mask)
    if not len(idx):
        return []
    brk = np.diff(idx) != 1
    for a in splits:
        a = np.asarray(a)[idx]
        brk |= a[1:] != a[:-1]
    cut = np.flatnonzero(brk)
    return list(zip(np.r_[idx[0], idx[cut + 1]], np.r_[idx[cut], idx[-1]]))


def load_frames(path, blocks):
    """Frame times by side, where the UFO was, and every crossing, from a PlayerLog.

    Live play is a round from its start to the shot (or the time-out): the frames whose
    pacedBy is set ('base' or 'altered'). The reveal, the veil and the camera pan between
    rounds have it empty and are left out. pacedBy and targetFps describe the cap that paced
    that very frame (unscaledDeltaMs measures it), so a frame's error is its time minus
    1000 / targetFps."""
    want = {"blockIndex", "phase", "roundNumber", "frameIndex", "timeSinceStartSec",
            "unscaledDeltaMs", "targetFps", "pacedBy", "diffFps", "side", "crossed", "ufoX",
            "ufoY", "towerX", "shotFired", "baseFps"}
    df = pd.read_csv(path, usecols=lambda c: c in want, low_memory=False)
    phase = df["phase"].astype(str).str.strip().str.lower().to_numpy()
    paced = df["pacedBy"].astype(str).str.strip().str.lower().to_numpy()
    block = pd.to_numeric(df["blockIndex"], errors="coerce").fillna(-1).to_numpy(int)
    base_of = {int(r.blockIndex): int(r.baseFps) for r in blocks.itertuples()}
    fps = np.array([base_of.get(b, -1) for b in block])
    t = pd.to_numeric(df["timeSinceStartSec"], errors="coerce").to_numpy(float)
    delta = pd.to_numeric(df["unscaledDeltaMs"], errors="coerce").to_numpy(float)
    target = pd.to_numeric(df["targetFps"], errors="coerce").to_numpy(float)
    rnd = pd.to_numeric(df["roundNumber"], errors="coerce").fillna(-1).to_numpy(int)
    x = pd.to_numeric(df["ufoX"], errors="coerce").to_numpy(float)
    y = pd.to_numeric(df["ufoY"], errors="coerce").to_numpy(float)
    tower = pd.to_numeric(df["towerX"], errors="coerce").to_numpy(float)
    crossed = pd.to_numeric(df["crossed"], errors="coerce").fillna(0).to_numpy() == 1
    fired = pd.to_numeric(df["shotFired"], errors="coerce").fillna(0).to_numpy() == 1
    main = phase == "main"
    live = main & np.isin(paced, ["base", "altered"]) & (fps > 0)
    ok_t = live & np.isfinite(delta) & np.isfinite(target) & (target > 0)
    err = delta - 1000.0 / np.where(target > 0, target, np.nan)

    rows, hist, err_hist = [], {}, {}
    for b in np.unique(block[ok_t]):
        for side in ("base", "altered"):
            sel = ok_t & (block == b) & (paced == side)
            if not sel.any():
                continue
            d, e = delta[sel], err[sel]
            f = int(fps[sel][0])
            rows.append({"blockIndex": int(b), "fps": f, "side": side, "frames": int(sel.sum()),
                         "medianMs": float(np.median(d)), "meanMs": float(d.mean()),
                         "sdMs": float(d.std(ddof=1)) if len(d) > 1 else np.nan,
                         "p99Ms": float(np.percentile(d, 99)), "maxMs": float(d.max()),
                         "medianErrMs": float(np.median(e)), "p99ErrMs": float(np.percentile(e, 99)),
                         "lateShare": float((e > 1.0).mean()),
                         "deliveredFps": float(1000.0 / d.mean())})
            hist[(f, side)] = hist.get((f, side), 0) + np.histogram(d, bins=FRAME_BINS)[0]
            err_hist[(f, side)] = err_hist.get((f, side), 0) + np.histogram(np.clip(e, ERR_BINS[0], ERR_BINS[-1] - 1e-9), bins=ERR_BINS)[0]
    normal = pd.DataFrame(rows)

    # Occupancy on a uniform clock, relative to the tower.
    occ, live_sec, paths, cross = {}, {}, [], []
    rel = x - tower
    for f in sorted(set(fps[live])):
        H = occ.setdefault(int(f), np.zeros((len(OCC_YE) - 1, len(OCC_XE) - 1)))
        live_sec.setdefault(int(f), 0.0)
        for a, z in _runs(live & (fps == f), rnd, block):
            ticks = np.arange(t[a], t[z], 1.0 / TRACK_HZ)
            if not len(ticks):
                continue
            k = a + np.searchsorted(t[a:z + 1], ticks, side="right") - 1
            h, _, _ = np.histogram2d(np.clip(y[k], OCC_YE[0], OCC_YE[-1] - 1e-6),
                                     np.clip(rel[k], OCC_XE[0], OCC_XE[-1] - 1e-6),
                                     bins=[OCC_YE, OCC_XE])
            H += h / TRACK_HZ
            live_sec[int(f)] += len(ticks) / TRACK_HZ
    # Thinned live paths (plus every crossing and shot frame) for the round maps.
    for b in np.unique(block[live]):
        sel = np.flatnonzero(live & (block == b))
        f = int(fps[sel[0]])
        step = max(1, int(round(f / PATH_HZ)))
        keep = sel[(np.arange(len(sel)) % step == 0) | crossed[sel] | fired[sel]]
        paths.append(pd.DataFrame({"blockIndex": int(b), "roundNumber": rnd[keep], "t": t[keep],
                                   "x": x[keep].astype(np.float32), "y": y[keep].astype(np.float32)}))
        # Heading at each crossing, over the 0.1 s before it. The clock restarts with every
        # block, so the search runs within this block's frames only.
        tb, xb = t[sel], x[sel]
        kl = np.flatnonzero(crossed[sel])
        j0 = np.searchsorted(tb, tb[kl] - 0.1)
        ks = sel[kl]
        vel = (xb[kl] - xb[j0]) / np.maximum(tb[kl] - tb[j0], 1e-6)
        cross.append(pd.DataFrame({"blockIndex": int(b), "fps": f, "roundNumber": rnd[ks],
                                   "t": t[ks], "x": x[ks], "y": y[ks], "vel": vel,
                                   "towerX": tower[ks]}))
    crossings = (pd.concat(cross, ignore_index=True) if cross else
                 pd.DataFrame(columns=["blockIndex", "fps", "roundNumber", "t", "x", "y", "vel", "towerX"]))
    path_df = (pd.concat(paths, ignore_index=True) if paths else
               pd.DataFrame(columns=["blockIndex", "roundNumber", "t", "x", "y"]))
    return FrameSummary(normal, hist, err_hist, occ, live_sec, crossings, path_df)


@dataclass
class Participant:
    pid: str
    folder: str
    session_id: int
    run_id: str
    blocks: pd.DataFrame
    shots: pd.DataFrame
    frames: FrameSummary | None = None
    notes: str = ""


def _resolution(cell):
    m = re.match(r"\s*(\d+)\s*[x×]\s*(\d+)", str(cell))
    return (int(m.group(1)), int(m.group(2))) if m else (np.nan, np.nan)


def prepare_blocks(df):
    df = df.copy()
    for c in df.columns:
        if c not in TEXT_BLOCK_COLS:
            df[c] = pd.to_numeric(df[c], errors="coerce")
    for c in TEXT_BLOCK_COLS & set(df.columns):
        df[c] = df[c].fillna("").astype(str).str.strip()
    df["fps"] = df["baseFps"].astype(int)
    wh = df["resolution"].map(_resolution) if "resolution" in df else pd.Series([(np.nan, np.nan)] * len(df))
    df["screenW"] = [w for w, _ in wh]
    df["screenH"] = [h for _, h in wh]
    df["aspect"] = df["screenW"] / df["screenH"]
    df["endReason"] = df["endReason"].astype(str)
    return df


def prepare_shots(df):
    df = df.copy()
    for c in df.columns:
        if c not in TEXT_SHOT_COLS:
            df[c] = pd.to_numeric(df[c], errors="coerce")
    for c in TEXT_SHOT_COLS & set(df.columns):
        df[c] = df[c].fillna("").astype(str).str.strip()
    df["phase"] = df["phase"].str.lower()
    df["fps"] = df["baseFps"].astype(int)
    df["isHit"] = df["isHit"].fillna(0).astype(int)
    df["playerFired"] = df["playerFired"].fillna(0).astype(int)
    df["countedByStaircase"] = df["countedByStaircase"].fillna(0).astype(int)
    df["main"] = df["phase"].eq("main")
    df["counted"] = df["main"] & df["countedByStaircase"].eq(1)
    df["trial"] = np.nan
    counted = df[df["counted"]]
    df.loc[counted.index, "trial"] = counted.groupby("blockIndex").cumcount() + 1
    df["shot"] = df["outcome"].eq("shot") & df["playerFired"].eq(1)
    df["result"] = np.where(df["shot"], np.where(df["isHit"] == 1, "hit", "miss"), df["outcome"])
    df["absMissX"] = df["missDistX"].abs().where(df["shot"])
    df["weber"] = df["diffFps"] / df["baseFps"]
    df["relativeX"] = df["missDistX"].where(df["shot"])
    # Signed toward the slower side: positive = the shot landed on the altered side.
    sgn = np.where(df["alteredSide"].str.lower().eq("right"), 1.0, -1.0)
    df["towardAlteredX"] = df["missDistX"].where(df["shot"]) * sgn
    # Time in the round from the starting gun to the shot (or to the end of the round).
    df["liveSec"] = df["firedAtSec"].where(df["shot"]).fillna(df["timeSinceStartSec"]) - df["trialStartSec"]
    df["exposures"] = df["crossings"]
    # Per-round points from the running score.
    df["points"] = df.groupby(["blockIndex", "phase"])["totalScore"].diff().fillna(df["totalScore"])
    return df


def discover(root):
    """(folder, tag, session, shot, player) for every ShotLog with a SessionLog beside it."""
    found = []
    for shot in sorted(root.rglob("ShotLog_*.csv"), key=str):
        tag = shot.stem[len("ShotLog_"):]
        folder = shot.parent
        session = folder / f"SessionLog_{tag}.csv"
        if not session.exists():
            log(f"  {folder.name}: no SessionLog_{tag}.csv beside the shot log - skipped")
            continue
        found.append((folder, tag, session, shot, folder / f"PlayerLog_{tag}.csv"))
    return found


def load_participants(root, only=None, exclude=None, frames=True):
    found = discover(root)
    loaded = []
    for folder, tag, session_path, shot_path, player_path in found:
        try:
            blocks = prepare_blocks(pd.read_csv(session_path))
            shots = prepare_shots(pd.read_csv(shot_path))
        except Exception as exc:  # truncated mid-write, older schema
            log(f"  {folder.name}: unreadable ({exc}) - skipped")
            continue
        if blocks.empty or not shots["counted"].any():
            log(f"  {folder.name}: no main-phase rounds - skipped")
            continue
        notes = " | ".join(p.read_text(encoding="utf-8", errors="replace").strip()
                           for p in sorted(folder.glob("*.txt")))
        sid = int(blocks["sessionId"].iloc[0])
        run = str(blocks["runId"].iloc[0]) if "runId" in blocks else tag
        p = Participant("", folder.name, sid, run, blocks, shots, None, notes)
        p._player_path = player_path
        loaded.append(p)

    # Participant numbers follow session order; folder names stay in every table.
    loaded.sort(key=lambda p: (p.session_id, p.folder))
    for i, p in enumerate(loaded, start=1):
        p.pid = f"P{i}"

    def keep(p):
        names = {p.pid.lower(), p.folder.lower(), str(p.session_id), f"{p.session_id}_{p.run_id}".lower()}
        if only and not names & {o.lower() for o in only}:
            return False
        if exclude and names & {e.lower() for e in exclude}:
            return False
        return True

    loaded = [p for p in loaded if keep(p)]
    if frames:
        for p in loaded:
            if p._player_path.exists():
                log(f"  {p.pid}: reading {p._player_path.name} "
                    f"({p._player_path.stat().st_size / 1e6:.0f} MB)")
                try:
                    p.frames = load_frames(p._player_path, p.blocks)
                except Exception as exc:  # noqa: BLE001
                    log(f"  {p.pid}: PlayerLog unreadable ({exc}) - frame figures skip it")
    return loaded


# ================================================================ analysis

@dataclass
class Study:
    participants: list
    gamma_override: dict | None = None             # {"*": value} and/or {pid: value}
    fits: pd.DataFrame = None            # one row per participant x block
    traces: dict = field(default_factory=dict)     # (pid, fps) -> per-trial frame
    pooled: dict = field(default_factory=dict)     # fps -> pooled summary
    blocks: pd.DataFrame = None
    responses: pd.DataFrame = None
    trials: pd.DataFrame = None
    replay: pd.DataFrame = None

    @property
    def pids(self):
        return [p.pid for p in self.participants]

    def by_pid(self, pid):
        return next(p for p in self.participants if p.pid == pid)

    @property
    def used(self):
        """Blocks that are measurements: every block but the abandoned ones."""
        return self.blocks[self.blocks["included"]]

    def gamma_for(self, pid, logged):
        """The gamma a participant's blocks are replayed with: an override for that participant,
        else a global override, else the value the block ran with."""
        ov = self.gamma_override or {}
        return float(ov.get(pid, ov.get("*", logged)))

    def fit(self, pid, fps):
        r = self.fits[(self.fits["pid"] == pid) & (self.fits["fps"] == fps)]
        return r.iloc[0] if len(r) else None


def fit_all(study, check_stimuli=True):
    """Replay every block under its fixed gamma: per-trial traces, final posterior, an MLE
    refit, and - when gamma is the logged one - the replay check against the logs, including
    whether every trial's difference is the one QUEST+'s placement rule chooses."""
    fits, checks, all_trials = [], [], []
    for p in study.participants:
        for _, b in p.blocks.sort_values("blockOrdinal").iterrows():
            rows = p.shots[p.shots["counted"] & (p.shots["blockIndex"] == b["blockIndex"])].sort_values("trial")
            if rows.empty:
                continue
            f = int(b["fps"])
            grid = Grid.from_cfg(rows.iloc[0])
            logged = float(rows["cfg_guessRate"].iloc[0])
            gamma = study.gamma_for(p.pid, logged)
            post = Posterior(grid, gamma)
            same_gamma = abs(gamma - logged) < 1e-9
            mismatch = 0
            expect = post.next_stimulus() if (check_stimuli and same_gamma) else None
            trace = [{"trial": 0, **post.light(), "diffFps": np.nan, "isHit": np.nan, "result": "prior"}]
            for _, r in rows.iterrows():
                if expect is not None and int(round(expect)) != int(r["diffFps"]):
                    mismatch += 1
                post.update(r["diffFps"], r["isHit"] == 1)
                trace.append({"trial": int(r["trial"]), **post.light(), "diffFps": r["diffFps"],
                              "isHit": int(r["isHit"]), "result": r["result"],
                              "loggedTheta": r["threshDiffFps"], "loggedSd": r["sd"]})
                if expect is not None:
                    expect = post.next_stimulus()
            tr = pd.DataFrame(trace)
            study.traces[(p.pid, f)] = tr
            s = post.summary()
            mle = mle_weibull(rows["diffFps"], rows["isHit"], gamma, lam_max=float(grid.lam.max()))
            abandoned = str(b["endReason"]).strip().lower() == "abandoned"

            if same_gamma:
                lg = rows[["threshDiffFps", "sd", "slopeEstimate", "lapseEstimate"]].to_numpy(float)
                mine = tr.iloc[1:][["theta", "sd", "beta", "lam"]].to_numpy(float)
                diff = np.nanmax(np.abs(mine - lg), axis=0)
                logged_edge = str(b.get("jndRangeEdge", "") or "").strip()
                checks.append({"pid": p.pid, "fps": f, "blockIndex": int(b["blockIndex"]),
                               "trials": len(rows), "gamma": gamma,
                               "maxAbsDiffTheta": diff[0], "maxAbsDiffSd": diff[1],
                               "maxAbsDiffBeta": diff[2], "maxAbsDiffLambda": diff[3],
                               "finalDiffMean": s["theta"] - b["jndDiffFps"],
                               "finalDiffSd": s["sd"] - b["sd"],
                               "finalDiffMode": s["mode"] - b["jndModeDiffFps"],
                               "finalDiffMedian": s["median_cs"] - b["jndMedianDiffFps"],
                               "finalDiffCiLo": s["ci_lo_cs"] - b["jndCI95LoDiffFps"],
                               "finalDiffCiHi": s["ci_hi_cs"] - b["jndCI95HiDiffFps"],
                               "rangeEdgeReplay": s["range_edge"], "rangeEdgeLogged": logged_edge,
                               "stimulusMismatches": mismatch if check_stimuli else np.nan,
                               "medianShiftCentered": s["median"] - s["median_cs"]})

            converged = bool(s["sd"] <= grid.stop_sd and len(rows) >= grid.min_trials)
            fits.append({
                "pid": p.pid, "fps": f, "blockIndex": int(b["blockIndex"]),
                "blockOrdinal": int(b["blockOrdinal"]), "gamma": gamma, "gammaLogged": logged,
                "trials": len(rows), "hits": int(rows["isHit"].sum()),
                "theta": s["theta"], "thetaSd": s["sd"], "thetaMedian": s["median"],
                "thetaMode": s["mode"], "ciLo": s["ci_lo"], "ciHi": s["ci_hi"],
                "beta": s["beta"], "lam": s["lam"], "x50": s["x50"], "rangeEdge": s["range_edge"],
                "mleTheta": mle["theta"], "mleBeta": mle["beta"], "mleLam": mle["lam"],
                "mleOk": mle["ok"], "mleAtBound": mle["at_bound"], "mleBound": mle["bound"],
                "converged": converged,
                # Declared reliability rule: the posterior is still wider than
                # RELIABLE_SD_FACTOR x the stop rule, or pressed against an end of the
                # difference range - either way the data have not located the threshold.
                "reliable": bool(s["sd"] <= RELIABLE_SD_FACTOR * grid.stop_sd and not s["range_edge"]
                                 and not abandoned),
                "abandoned": abandoned, "endReason": str(b["endReason"]),
                "stopSD": grid.stop_sd, "minTrials": grid.min_trials, "maxTrials": grid.max_trials,
                "gridLo": grid.lo, "gridHi": grid.hi, "gridStep": int(grid.stim[1] - grid.stim[0]) if len(grid.stim) > 1 else 1,
                "_grid": grid,
            })
            t = rows[["trial", "diffFps", "weber", "isHit", "result", "crossings", "liveSec",
                      "blockOrdinal", "absMissX", "towardAlteredX"]].copy()
            t.insert(0, "gamma", gamma)
            t.insert(0, "abandoned", abandoned)
            t.insert(0, "fps", f)
            t.insert(0, "pid", p.pid)
            all_trials.append(t)
    fits_df = pd.DataFrame(fits)
    study.replay = pd.DataFrame(checks)
    study.trials = pd.concat(all_trials, ignore_index=True) if all_trials else pd.DataFrame()

    # The pooled observer per base rate: every participant's trials (abandoned blocks left
    # out) in one posterior, each trial under its own block's gamma - for the picture only;
    # the inference uses the per-participant thresholds.
    for f in FPS_LEVELS:
        sub = fits_df[(fits_df["fps"] == f) & ~fits_df["abandoned"]] if len(fits_df) else fits_df
        if sub.empty:
            continue
        grid = Grid.spanning(list(sub["_grid"]))
        g = study.trials[(study.trials["fps"] == f) & ~study.trials["abandoned"]]
        post = Posterior(grid, float(g["gamma"].mean()))
        for x, h, gm in zip(g["diffFps"], g["isHit"], g["gamma"]):
            post.update(x, h == 1, gamma=gm)
        study.pooled[f] = {**post.summary(), "n": len(g), "participants": g["pid"].nunique(),
                           "gamma": float(g["gamma"].mean()),
                           "gammas": sorted(set(round(v, 4) for v in g["gamma"])),
                           "gridHi": grid.hi}
    study.fits = fits_df.drop(columns="_grid") if len(fits_df) else fits_df
    study._grids = {(r["pid"], r["fps"]): r["_grid"] for _, r in fits_df.iterrows()} if len(fits_df) else {}


def frame_time_diff_ms(base, diff):
    """How much longer an altered-side frame lasts than a base-side frame, in ms."""
    alt = base - diff
    with np.errstate(divide="ignore", invalid="ignore"):
        return np.where(alt > 0, 1000.0 / alt - 1000.0 / base, np.nan)


def build_tables(study):
    """One row per participant x block: every SessionLog metric, the fixed-gamma refit, the
    threshold in every unit, the data-quality flags, and the rates a figure actually wants
    (block lengths differ, so totals mislead)."""
    rows = []
    for p in study.participants:
        fr = p.frames.normal if p.frames is not None and len(p.frames.normal) else pd.DataFrame()
        for _, b in p.blocks.iterrows():
            f = int(b["fps"])
            r = {"pid": p.pid, "folder": p.folder, "condition": cond_label(f)}
            for c, v in b.items():
                if not str(c).startswith("cfg_") or c in ("cfg_guessRate", "cfg_stopSD", "cfg_maxTrials",
                                                          "cfg_minTrials", "cfg_maxDiffFps", "cfg_minDiffFps",
                                                          "cfg_diffStepFps", "cfg_maxCrossingsPerRound",
                                                          "cfg_roundTimeoutSec"):
                    r[c] = v
            fit = study.fits[(study.fits["pid"] == p.pid) & (study.fits["blockIndex"] == b["blockIndex"])]
            if not fit.empty:
                for c, v in fit.iloc[0].items():
                    if c not in ("pid", "fps", "blockIndex", "blockOrdinal", "endReason"):
                        r[f"fit_{c}"] = v
            s = p.shots[p.shots["main"] & (p.shots["blockIndex"] == b["blockIndex"])]
            minutes = b["playDurationSec"] / 60.0 if b["playDurationSec"] > 0 else np.nan
            n_rounds = len(s)
            shots = s[s["shot"]]
            prac = p.shots[(p.shots["phase"] == "practice") & (p.shots["fps"] == f)]
            r.update({
                "pCorrect": (s["isHit"].sum() / n_rounds) if n_rounds else np.nan,
                "liveRoundSec": float(s["liveSec"].median()) if n_rounds else np.nan,
                "crossingsPerRound": float(s["crossings"].mean()) if n_rounds else np.nan,
                "crossingsBeforeShot": float(shots["crossings"].mean()) if len(shots) else np.nan,
                "sinceLastCrossingSec": float(shots["sinceLastCrossingSec"].median()) if len(shots) else np.nan,
                "timeoutShare": float((~s["shot"]).mean()) if n_rounds else np.nan,
                "missDistMedian": float(shots["absMissX"].median()) if len(shots) else np.nan,
                "towardAlteredMedian": float(shots["towardAlteredX"].median()) if len(shots) else np.nan,
                "pointsPerRound": float(s["points"].mean()) if n_rounds else np.nan,
                "practiceCorrect": int(prac["isHit"].sum()), "practiceTrials": int(len(prac)),
                "playDurationMin": minutes,
                "ufoSpeedWorldPerSec": b["totalUfoPathWorld"] / b["playDurationSec"] if b["playDurationSec"] > 0 else np.nan,
                "mousePathPerMinPx": b["totalMousePathPx"] / minutes if minutes else np.nan,
                "achievedBaseRatio": b["baseSideMeasuredFps"] / f if f else np.nan,
                "measuredAlteredErrPct": float(((s["measuredAlteredFps"] - s["alteredFps"]) / s["alteredFps"]).median() * 100)
                                         if s["measuredAlteredFps"].notna().any() else np.nan,
                "aboveRefresh": bool(np.isfinite(b.get("displayHz", np.nan)) and f > b["displayHz"] + 0.5),
                "screenGamma": screen_gamma(b.get("screenW"), b.get("screenH")),
                "screenGammaInset": screen_gamma(b.get("screenW"), b.get("screenH"), inset=0.02),
            })
            if len(fr):
                for side in ("base", "altered"):
                    q = fr[(fr["blockIndex"] == b["blockIndex"]) & (fr["side"] == side)]
                    if len(q):
                        q = q.iloc[0]
                        r[f"{side}FrameSdMs"] = q["sdMs"]
                        r[f"{side}FrameP99Ms"] = q["p99Ms"]
                        r[f"{side}LateShare"] = q["lateShare"]
                        r[f"{side}DeliveredFps"] = q["deliveredFps"]
            rows.append(r)
    blocks = pd.DataFrame(rows)
    blocks = blocks.sort_values(["pid", "fps"]).reset_index(drop=True)
    blocks["fps"] = blocks["fps"].astype(int)
    # The figures read thresholds from the refit, which is the logged value whenever gamma
    # is the logged one, and the recomputed one whenever --gamma changed it.
    blocks["theta"] = blocks["fit_theta"]
    blocks["thetaLo"] = blocks["fit_ciLo"]
    blocks["thetaHi"] = blocks["fit_ciHi"]
    blocks["thetaSdFinal"] = blocks["fit_thetaSd"]
    blocks["reliable"] = blocks["fit_reliable"].fillna(False).astype(bool)
    blocks["converged"] = blocks["fit_converged"].fillna(False).astype(bool)
    blocks["abandoned"] = blocks["endReason"].str.lower().eq("abandoned")
    blocks["included"] = ~blocks["abandoned"] & blocks["theta"].notna()
    blocks["pinned"] = blocks["fit_rangeEdge"].fillna("").astype(str)
    blocks["sdRatio"] = blocks["thetaSdFinal"] / blocks["fit_stopSD"]
    blocks["ciWidth"] = blocks["thetaHi"] - blocks["thetaLo"]
    blocks["logTheta"] = np.log10(blocks["theta"])
    # An MLE refit that ends on its box edge (theta at its ceiling, typically: a block with almost no
    # hits has no finite maximum) is not identified; the sensitivity analysis treats it as missing.
    blocks["mleThetaIdentified"] = blocks["fit_mleTheta"].where(
        ~blocks["fit_mleAtBound"].fillna(True).astype(bool) & blocks["included"])
    # The threshold in the units a reader might want. Direction 'down': the altered side runs
    # at base - theta.
    base = blocks["fps"].astype(float)
    blocks["alteredAtTheta"] = base - blocks["theta"]
    blocks["weber"] = blocks["theta"] / base
    blocks["weberPct"] = 100.0 * blocks["weber"]
    blocks["frameTimeDiffMs"] = frame_time_diff_ms(base, blocks["theta"])
    blocks["frameTimeRatio"] = base / (base - blocks["theta"])
    blocks["practiceAccuracy"] = blocks["practiceCorrect"] / blocks["practiceTrials"].replace(0, np.nan)
    blocks.loc[~blocks["included"], ["theta", "weber", "weberPct", "logTheta", "frameTimeDiffMs",
                                     "alteredAtTheta", "frameTimeRatio"]] = np.nan
    study.blocks = blocks

    resp = []
    for p in study.participants:
        s = p.shots[p.shots["main"]].copy()
        s.insert(0, "pid", p.pid)
        resp.append(s)
    responses = pd.concat(resp, ignore_index=True)
    keep = [c for c in responses.columns if not c.startswith("cfg_")]
    study.responses = responses[keep].merge(
        blocks[["pid", "blockIndex", "abandoned", "reliable", "converged"]], on=["pid", "blockIndex"], how="left")


def wide(blocks, value, pids=None):
    """participants x FPS_LEVELS matrix of one per-block metric (abandoned blocks are NaN)."""
    b = blocks[blocks["included"]] if "included" in blocks else blocks
    t = b.pivot_table(index="pid", columns="fps", values=value, aggfunc="first")
    t = t.reindex(columns=[f for f in FPS_LEVELS])
    if pids is not None:
        t = t.reindex(pids)
    return t


# ---------------------------------------------------------------- statistics

THRESHOLD_METRICS = [("theta", "Threshold, posterior mean (fps)"),
                     ("fit_thetaMedian", "Threshold, posterior median (fps)"),
                     ("fit_thetaMode", "Threshold, posterior mode (fps)"),
                     ("fit_x50", "50% detection point (fps)"),
                     ("fit_mleTheta", "Threshold, MLE refit (fps)"),
                     ("weberPct", "Weber fraction (%)"),
                     ("alteredAtTheta", "Altered rate at threshold (fps)"),
                     ("frameTimeDiffMs", "Frame-time difference at threshold (ms)"),
                     ("thetaSdFinal", "Posterior SD (fps)"),
                     ("ciWidth", "95% CrI width (fps)"),
                     ("fit_beta", "Slope beta"), ("fit_lam", "Lapse lambda")]


def condition_descriptives(blocks, metrics):
    rows = []
    b = blocks[blocks["included"]]
    for spec in metrics:
        key, label = spec[0], spec[1]
        if key not in b:
            continue
        for f in FPS_LEVELS:
            v = b[b["fps"] == f][key].to_numpy(float)
            v = v[np.isfinite(v)]
            m, lo, hi = mean_ci(v)
            rows.append({"metric": key, "label": label, "fps": f, "n": len(v),
                         "mean": m, "sd": float(v.std(ddof=1)) if len(v) > 1 else np.nan,
                         "median": float(np.median(v)) if len(v) else np.nan,
                         "min": float(v.min()) if len(v) else np.nan,
                         "max": float(v.max()) if len(v) else np.nan,
                         "ci_lo": lo, "ci_hi": hi})
    return pd.DataFrame(rows)


CONTRASTS = [(60, 144), (144, 500), (60, 500)]


def inferential(study):
    """One-way RM-ANOVA over the base rates and the three pairwise contrasts, for the
    threshold in every unit and the sensitivity checks; order effects as a linear trend."""
    blocks = study.blocks
    rows = []

    def add(key, label, table, fam):
        if key not in table:
            return
        start = len(rows)
        mat = wide(table, key, study.pids)
        rows.append({"dv": label, "test": "RM-ANOVA", **rm_anova_oneway(mat.to_numpy(float))})
        for a, bb in CONTRASTS:
            c = paired_contrast(mat[a], mat[bb])
            rows.append({"dv": label, "test": f"{bb} - {a} fps", **c})
        for r in rows[start:]:
            r["family"] = fam

    reliable = blocks.copy()
    for c in ("theta", "logTheta", "weberPct"):
        reliable.loc[~reliable["reliable"], c] = np.nan
    specs = [("theta", "Threshold (fps)", blocks, "threshold"),
             ("weberPct", "Weber fraction (%)", blocks, "threshold"),
             ("frameTimeDiffMs", "Frame-time difference at threshold (ms)", blocks, "units"),
             ("logTheta", "log10 threshold", blocks, "units"),
             ("theta", "Threshold (fps), unreliable blocks excluded", reliable, "sensitivity"),
             ("weberPct", "Weber fraction (%), unreliable blocks excluded", reliable, "sensitivity"),
             ("fit_x50", "50% detection point (fps)", blocks, "sensitivity"),
             ("mleThetaIdentified", "Threshold, MLE refit (fps; fits on a parameter bound left out)", blocks, "sensitivity")]
    for key, label, table, fam in specs:
        add(key, label, table, fam)

    # Procedure-dependent: QUEST+ sets the hit rate and the block length, so these describe
    # the procedure more than the participant (reported, not interpreted).
    for key, label in [("staircaseTrials", "Staircase trials"),
                       ("pCorrect", "Proportion hit (staircase trials)"),
                       ("crossingsBeforeShot", "Boundary crossings before the shot"),
                       ("liveRoundSec", "Time in a round (s)"),
                       ("avgMouseSpeedPxPerSec", "Mouse speed (counts/s)")]:
        add(key, label, blocks, "secondary")

    # Order: the threshold's scale differs by base rate, so the trend over block position is
    # tested on the Weber fraction and on log10 theta (both comparable across rates).
    for key, label in [("weberPct", "Weber fraction (%)"), ("logTheta", "log10 threshold")]:
        pos = blocks[blocks["included"]].pivot_table(index="pid", columns="blockOrdinal", values=key)
        tr = linear_trend(pos.to_numpy(float))
        rows.append({"dv": label, "test": "Linear trend over block position", "family": "order", **tr})

    out = pd.DataFrame(rows)
    out["p_holm"] = np.nan
    for (dv, fam), idx in out.groupby(["dv", "family"]).groups.items():
        sel = [i for i in idx if out.loc[i, "test"] != "RM-ANOVA"]
        if sel:
            out.loc[sel, "p_holm"] = holm(out.loc[sel, "p"].to_numpy(float))
    return out


# ================================================================ paper figures

def fps_mark(ax, x, y, f, ok=True, grow=0.0, z=3, **kw):
    """One block, in its base rate's colour and marker; hollow when it is not reliable."""
    ax.plot(x, y, marker=FPS_MARKER[f], ms=FPS_MS[f] + grow, ls="", color=FPS_COLOR[f],
            mfc=FPS_COLOR[f] if ok else SURFACE, mec=SURFACE if ok else FPS_COLOR[f],
            mew=0.5 if ok else 0.9, zorder=z, **kw)


def edge_marks(ax, xs, ys, top, colors, fmt="{:.0f}", marker="^", label_side="right"):
    """Values above `top` drawn as open triangles on the top edge with their value printed
    beside - the point is shown, not dropped, without squashing every other point."""
    for x, y, c in zip(xs, ys, colors):
        if not np.isfinite(y) or y <= top:
            continue
        ax.plot(x, top, marker=marker, ms=4.6, mfc=SURFACE, mec=c, mew=1.0, ls="", zorder=6,
                clip_on=False)
        offset, ha = {"right": ((4, -1), "left"), "left": ((-4, -1), "right"),
                      "below": ((0, -5), "center")}[label_side]
        ax.annotate(fmt.format(y), (x, top), xytext=offset, textcoords="offset points", ha=ha,
                    va="top", fontsize=FS_SMALL, color=c, zorder=6, annotation_clip=False)


def setting_panel(ax, b, value, lo=None, hi=None, fmt="{:.1f}", individual_ci=None, top=None,
                  label_means=True):
    """The three base rates on one axis.

    Participants sit left of each rate (grey lines join a participant's blocks - the
    within-subject pairing), the group mean with its 95% t-interval sits right, and black
    lines join the means. Individual credible intervals are drawn while they stay legible
    (N <= 8). Unreliable blocks are hollow, pinned ones carry a caret (the threshold lies
    beyond the difference range); values above `top` are edge marks. The means are printed
    in a header row above the panel, where they cannot collide with the data."""
    b = b[b["included"]]
    pids = list(dict.fromkeys(b["pid"]))
    n = len(pids)
    if individual_ci is None:
        individual_ci = lo is not None and n <= 8
    spread = min(0.07, 0.36 / max(1, n))
    lim = np.inf if top is None else top
    drawn = []
    for i, pid in enumerate(pids):
        off = -0.15 + (i - (n - 1) / 2) * spread
        pts = []
        for f in FPS_LEVELS:
            r = b[(b["pid"] == pid) & (b["fps"] == f)]
            if r.empty or not np.isfinite(r[value].iloc[0]):
                pts.append(None)
                continue
            r = r.iloc[0]
            x, y = COND_X[f] + off, float(r[value])
            if individual_ci and np.isfinite(r[lo]) and np.isfinite(r[hi]):
                ylo, yhi = float(r[lo]), float(r[hi])
                if ylo < lim:
                    ax.plot([x, x], [ylo, min(yhi, lim)], color=tint(FPS_COLOR[f], 0.55), lw=0.8,
                            zorder=1.5, solid_capstyle="butt")
                if yhi <= lim:
                    drawn.append(yhi)
            if y > lim:
                edge_marks(ax, [x], [y], lim, [FPS_COLOR[f]], label_side="left")
                pts.append((x, lim))
                continue
            fps_mark(ax, x, y, f, ok=bool(r["reliable"]), grow=-0.3)
            if r["pinned"]:
                ax.annotate("≥" if r["pinned"] == "top" else "≤", (x, y), xytext=(-5, 0),
                            textcoords="offset points", ha="right", va="center", fontsize=FS_SMALL,
                            color=FPS_COLOR[f])
            drawn.append(y)
            pts.append((x, y))
        seg = [q for q in pts if q is not None]
        if len(seg) >= 2:
            ax.plot([q[0] for q in seg], [q[1] for q in seg], color=INDIVIDUAL, lw=0.8, zorder=2)
    header = ax.get_xaxis_transform()
    means = []
    for f in FPS_LEVELS:
        x = COND_X[f] + 0.26
        m, clo, chi = mean_ci(b[b["fps"] == f][value])
        if np.isfinite(clo):
            ylo = clo if ax.get_yscale() != "log" else max(clo, 1e-9)
            ax.errorbar(x, m, yerr=[[m - ylo], [min(chi, lim) - m]], fmt="none", ecolor=INK,
                        elinewidth=0.9, capsize=2.2, capthick=0.9, zorder=4)
            drawn.append(min(chi, lim))
        if np.isfinite(m):
            ax.plot(x, m, marker=FPS_MARKER[f], ms=FPS_MS[f] + 1.8, ls="", color=FPS_COLOR[f],
                    mec=INK, mew=0.8, zorder=5)
            if label_means:
                ax.text(COND_X[f], 1.0, fmt.format(m), transform=header, ha="center", va="bottom",
                        fontsize=FS_SMALL, color=INK, fontweight="bold", zorder=6)
            means.append((x, m))
    if len(means) >= 2:
        ax.plot([p[0] for p in means], [p[1] for p in means], color=INK, lw=1.0, zorder=3.5)
    cond_axis(ax)
    return max(drawn) if drawn else 1.0


def paired_diffs(blocks, value):
    """[(x, label, differences, both blocks reliable)] for the three pairwise contrasts."""
    b = blocks[blocks["included"]]
    t = b.pivot_table(index="pid", columns="fps", values=value)
    ok = b.pivot_table(index="pid", columns="fps", values="reliable", aggfunc="first")
    out = []
    for k, (a, c) in enumerate(CONTRASTS):
        if a not in t or c not in t:
            continue
        pair = t[[a, c]].dropna()
        if pair.empty:
            continue
        good = ok.reindex(pair.index)[[a, c]].fillna(False).astype(bool).all(axis=1)
        out.append((k, f"{c}−{a}", (pair[c] - pair[a]).to_numpy(float), good.to_numpy(bool)))
    return out


def diff_panel(ax, blocks, value, fmt="{:+.1f}", ylim=None):
    """Paired differences with the mean and its 95% CI - an estimation plot, one column per
    contrast. Differences involving an unreliable block are hollow; values outside ylim are
    edge triangles with their value."""
    groups = paired_diffs(blocks, value)
    lo_lim, hi_lim = ylim if ylim is not None else (-np.inf, np.inf)
    for k, label, d, ok in groups:
        n = len(d)
        xs = k - 0.2 + (np.arange(n) - (n - 1) / 2) * min(0.05, 0.25 / max(1, n))
        inside = (d >= lo_lim) & (d <= hi_lim)
        ax.plot(xs[ok & inside], d[ok & inside], marker="o", ms=3.6, ls="", color=MUTED,
                mec=SURFACE, mew=0.5, zorder=3)
        ax.plot(xs[~ok & inside], d[~ok & inside], marker="o", ms=3.6, ls="", mfc=SURFACE,
                mec=MUTED, mew=0.9, zorder=3)
        for x_, y_ in zip(xs[~inside], d[~inside]):
            edge = hi_lim if y_ > hi_lim else lo_lim
            ax.plot(x_, edge, marker="^" if y_ > hi_lim else "v", ms=4.4, mfc=SURFACE, mec=MUTED,
                    mew=1.0, ls="", zorder=6, clip_on=False)
            ax.annotate(f"{y_:+.0f}", (x_, edge), xytext=(-4, 0), textcoords="offset points",
                        ha="right", va="center", fontsize=FS_SMALL, color=INK_2, zorder=6,
                        annotation_clip=False)
        m, lo, hi = mean_ci(d)
        if np.isfinite(lo):
            ax.errorbar(k + 0.2, m, yerr=[[m - max(lo, lo_lim)], [min(hi, hi_lim) - m]], fmt="none",
                        ecolor=INK, elinewidth=0.9, capsize=2.2, capthick=0.9, zorder=4)
        ax.plot(k + 0.2, m, marker="o", ms=5.2, ls="", color=INK, mec=SURFACE, mew=0.6, zorder=5)
        top_y = min(hi, hi_lim) if np.isfinite(hi) else m
        ax.annotate(fmt.format(m) + ("" if n >= 2 else f"\n(n = {n})"), xy=(k + 0.2, top_y),
                    xytext=(0, 3), textcoords="offset points", ha="center", va="bottom",
                    fontsize=FS_SMALL, color=INK, zorder=6, bbox=LABEL_BOX)
    ax.axhline(0, color=INK_2, lw=0.7, zorder=1)
    ax.set_xticks([g[0] for g in groups], [g[1] for g in groups])
    ax.set_xlim(-0.6, len(CONTRASTS) - 0.4)
    ax.tick_params(axis="x", length=0)
    no_xgrid(ax)
    return groups


def study_numbers(study):
    """The sentences captions and alt text share, with the real numbers in."""
    b = study.used
    parts, alt_parts, w_parts = [], [], []
    for f in FPS_LEVELS:
        v = b[b["fps"] == f]["theta"]
        m, lo, hi = mean_ci(v)
        wm, wlo, whi = mean_ci(b[b["fps"] == f]["weberPct"])
        if np.isfinite(m):
            parts.append(f"{f} fps M = {m:.1f} fps [{fmt_num(lo, 1)}, {fmt_num(hi, 1)}] (n = {v.notna().sum()})")
            w_parts.append(f"{f} fps {wm:.1f}% [{fmt_num(wlo, 1)}, {fmt_num(whi, 1)}]")
            alt_parts.append(f"at {f} fps the threshold averages {m:.1f} fps ({wm:.0f}% of the base rate)")
    return {"main_sentence": "Means [95% CI]: " + "; ".join(parts) + ".",
            "weber_sentence": "Weber fractions: " + "; ".join(w_parts) + ".",
            "alt_means": ("; ".join(alt_parts) + ".") if alt_parts else ""}


def flag_text(study):
    b = study.used
    flagged = b[~b["reliable"]]
    out = ""
    if len(flagged):
        out += (" Hollow markers are blocks whose threshold is not identified by the data - final "
                f"posterior SD above {RELIABLE_SD_FACTOR:g}× the block's stop rule, or an estimate "
                "pressed against the end of the difference range (≥: the threshold lies above it) - ("
                + ", ".join(f"{r.pid} {cond_label(int(r.fps))}, {r.theta:.1f} fps"
                            + (" pinned at the top" if r.pinned == "top" else "")
                            for r in flagged.itertuples()) + "); they are included in the means.")
    ab = study.blocks[study.blocks["abandoned"]]
    if len(ab):
        out += (" Abandoned blocks are not measurements and are left out ("
                + ", ".join(f"{r.pid} {cond_label(int(r.fps))} after {int(r.staircaseTrials)} trial(s)"
                            for r in ab.itertuples()) + ").")
    return out


def fig_main(study):
    """The headline: the threshold at each base rate in fps (log axis: it grows with the base
    rate) and as a Weber fraction of the base rate (the scale on which the rates compare),
    then the paired Weber-fraction contrasts - so every comparison has its own interval."""
    b = study.blocks
    fig = plt.figure(figsize=(TEXT_W, 2.95), layout="constrained")
    gs = fig.add_gridspec(1, 3, width_ratios=[1.2, 1.2, 1.0], wspace=0.06)
    ax_a, ax_b, ax_c = (fig.add_subplot(gs[i]) for i in range(3))

    ax_a.set_yscale("log")
    u = study.used
    lo_v = np.nanmin(np.r_[u["thetaLo"], u["theta"]]) if len(u) else 1
    hi_v = np.nanmax(np.r_[u["thetaHi"], u["theta"]]) if len(u) else 10
    setting_panel(ax_a, b, "theta", lo="thetaLo", hi="thetaHi")
    ax_a.set_ylim(max(0.5, lo_v * 0.7), hi_v * 1.35)
    log_axis(ax_a, "y", nice_log_ticks(max(0.5, lo_v * 0.7), hi_v * 1.35))
    ax_a.set_ylabel("JND threshold $\\theta$ (fps, log)")
    title(ax_a, "a", "Threshold in fps", pad=13)

    vals = list(u.loc[u["reliable"], "weberPct"]) + [mean_ci(u[u["fps"] == f]["weberPct"])[2] for f in FPS_LEVELS]
    top = max([v for v in vals if np.isfinite(v)] + [10.0]) * 1.08
    setting_panel(ax_b, b, "weberPct", fmt="{:.0f}%", top=top, individual_ci=False)
    ax_b.set_ylim(0, top * 1.07)
    ax_b.set_ylabel("Weber fraction $\\theta$ / base (%)")
    title(ax_b, "b", "Relative to the base rate", pad=13)

    groups = paired_diffs(b, "weberPct")
    allv = np.concatenate([g[2] for g in groups]) if groups else np.array([0.0])
    span = max(5.0, float(np.nanmax(np.abs(allv))) * 1.25)
    diff_panel(ax_c, b, "weberPct", fmt="{:+.0f} pp", ylim=(-span, span))
    ax_c.set_ylim(-span * 1.1, span * 1.25)
    ax_c.set_ylabel("$\\Delta$ Weber fraction (percentage points)")
    ax_c.set_xlabel("Contrast (fps)")
    title(ax_c, "c", "Paired differences")

    handles = [
        Line2D([], [], color=INDIVIDUAL, lw=0.8, label="Participant (paired)"),
        Line2D([], [], color=tint(INK_2, 0.55), lw=0.9, label="Participant's 95% CrI"),
        Line2D([], [], marker="o", ls="-", ms=5.5, color=INK, mfc=MUTED, mec=INK, lw=0.9,
               label="Mean ± 95% CI"),
        Line2D([], [], marker="o", ls="", ms=4.2, mfc=SURFACE, mec=INK_2, mew=0.9,
               label="Unreliable block (hollow)")] + condition_key()
    fig_legend_top(fig, handles, ncol=4)
    n = len(study.pids)
    desc = study_numbers(study)
    gam = sorted(set(round(g, 3) for g in study.fits["gamma"]))
    caption = (
        f"Frame-rate difference thresholds at the three base rates (N = {n}). (a) The threshold "
        "$\\theta$ in fps - how far below the base rate the altered side has to run for the "
        "boundary to be located - on a log axis, since it grows with the base rate. Small markers "
        "are participants, joined by grey lines across rates; thin bars are each participant's 95% "
        "credible interval from the QUEST+ posterior; large markers are the mean across "
        "participants with a 95% t-interval, printed above each column. Thresholds are the "
        "posterior mean of the Weibull $\\theta$ with the guess rate fixed at the value each block "
        f"ran with ($\\gamma$ = {', '.join(f'{g:.3f}' for g in gam)}). (b) The same thresholds as a "
        "Weber fraction, $\\theta$ divided by the base rate. (c) Paired differences of the Weber "
        "fraction between rates; black markers are the mean difference with its 95% CI (value "
        f"above); intervals that cross zero are compatible with no difference.{flag_text(study)} "
        f"{desc['main_sentence']} {desc['weber_sentence']}")
    alt = (f"Three-panel dot plot. Panel a shows frame-rate difference thresholds on a log scale for "
           f"{n} participants at base rates of 60, 144 and 500 frames per second, participants joined "
           f"by lines, with a mean and confidence interval per rate: {desc['alt_means']} Panel b shows "
           "the same thresholds as a percentage of the base rate. Panel c shows each participant's "
           "paired differences in that percentage between rates, with mean differences and intervals.")
    return FigureDoc("Fig01_thresholds", fig, caption, alt)


def bin_trials(x, y, min_per_bin=8, max_bins=9):
    """Adjacent-stimulus bins of roughly equal count, never splitting identical stimuli."""
    x, y = np.asarray(x, float), np.asarray(y, float)
    order = np.argsort(x, kind="stable")
    x, y = x[order], y[order]
    k = int(max(1, min(max_bins, len(x) // min_per_bin)))
    edges = np.unique(np.quantile(x, np.linspace(0, 1, k + 1)))
    if len(edges) < 2:
        edges = np.array([x.min(), x.max() + 1e-9])
    idx = np.clip(np.searchsorted(edges, x, side="right") - 1, 0, len(edges) - 2)
    out = []
    for i in np.unique(idx):
        sel = idx == i
        n, kk = int(sel.sum()), int(y[sel].sum())
        lo, hi = wilson(kk, n)
        out.append({"x": float(x[sel].mean()), "n": n, "p": kk / n, "lo": lo, "hi": hi})
    return pd.DataFrame(out)


def psychometric_xmax(study, f):
    g = study.trials[(study.trials["fps"] == f) & ~study.trials["abandoned"]]
    hi = study.pooled.get(f, {}).get("gridHi", f)
    if g.empty:
        return float(hi)
    return float(min(hi * 1.02, max(g["diffFps"].max() * 1.15, 5)))


def fig_psychometric(study):
    fig, axes = plt.subplots(1, 3, figsize=(TEXT_W, 2.45), sharey=True, layout="constrained")
    notes = {}
    for ax, f, letter in zip(axes, FPS_LEVELS, "abc"):
        x_max = psychometric_xmax(study, f)
        xs = np.linspace(0.2, x_max, 400)
        fits = study.fits[(study.fits["fps"] == f) & ~study.fits["abandoned"]]
        for _, r in fits.iterrows():
            ax.plot(xs, weibull(xs, r["theta"], r["beta"], r["lam"], r["gamma"]), color=INDIVIDUAL,
                    lw=0.8, zorder=2)
        g = study.trials[(study.trials["fps"] == f) & ~study.trials["abandoned"]]
        if not g.empty:
            bins = bin_trials(g["diffFps"], g["isHit"], min_per_bin=6)
            for _, bb in bins.iterrows():
                ax.plot([bb["x"], bb["x"]], [bb["lo"], bb["hi"]], color=MUTED, lw=0.7, zorder=3,
                        solid_capstyle="butt")
            ax.scatter(bins["x"], bins["p"], s=6 + 2.2 * bins["n"], color=SURFACE, edgecolor=INK,
                       linewidth=0.7, zorder=4, clip_on=False)
        pooled = study.pooled.get(f)
        gamma = pooled["gamma"] if pooled else (fits["gamma"].mean() if len(fits) else np.nan)
        if pooled:
            ax.plot(xs, weibull(xs, pooled["theta"], pooled["beta"], pooled["lam"], gamma),
                    color=FPS_COLOR[f], lw=1.6, ls=FPS_LS[f], zorder=5)
            py = float(weibull(pooled["theta"], pooled["theta"], pooled["beta"], pooled["lam"], gamma))
            ax.plot([pooled["theta"]] * 2, [0, py], color=FPS_COLOR[f], lw=0.6, ls=":", zorder=4)
            ax.plot(pooled["theta"], py, marker=FPS_MARKER[f], ms=FPS_MS[f] + 0.6, color=FPS_COLOR[f],
                    mec=SURFACE, mew=0.6, zorder=6)
            notes[ax] = (f"$\\theta$ {pooled['theta']:.1f} fps, $\\beta$ {pooled['beta']:.2f}, "
                         f"n {pooled['n']}")
        if np.isfinite(gamma):
            ax.axhline(gamma, color=MUTED, lw=0.7, ls=":", zorder=1)
            gs_ = pooled["gammas"] if pooled else [gamma]
            gtxt = (f"$\\gamma$ = {gs_[0]:.3f}" if len(gs_) == 1
                    else f"$\\gamma$ = {min(gs_):.3f}–{max(gs_):.3f}")
            ax.annotate(gtxt, xy=(1, gamma), xycoords=("axes fraction", "data"), xytext=(-2, -2),
                        textcoords="offset points", ha="right", va="top", fontsize=FS_SMALL, color=INK_2)
        title(ax, letter, f"Base {cond_label(f)}", pad=12)
        if ax in notes:
            ax.text(0.0, 1.0, notes[ax], transform=ax.transAxes, ha="left", va="bottom",
                    fontsize=FS_SMALL, color=INK_2)
        ax.set_xlim(0, x_max)
        ax.set_ylim(-0.04, 1.04)
        ax.spines["left"].set_bounds(0, 1)
        ax.set_yticks([0, 0.2, 0.4, 0.6, 0.8, 1.0])
        ax.set_xlabel("Frame-rate difference (fps)")
        if g.empty and not len(fits):
            ax.text(0.5, 0.5, "no usable block", transform=ax.transAxes, ha="center", va="center",
                    color=MUTED)
    axes[0].set_ylabel("P(hit)")
    pooled_h = KeyTuple(Line2D([], [], color=FPS_COLOR[f], ls=FPS_LS[f], lw=1.6) for f in FPS_LEVELS)
    pooled_h.label = "Pooled fit"
    handles = [pooled_h,
               Line2D([], [], color=INDIVIDUAL, lw=0.8, label="Participant fit"),
               Line2D([], [], marker="o", ls="", ms=5, mfc=SURFACE, mec=INK, mew=0.7,
                      label="Binned data (size = trials) ± 95% CI"),
               Line2D([], [], color=MUTED, lw=0.7, ls=":", label="Guess rate $\\gamma$ (fixed)")]
    fig_legend_top(fig, handles, ncol=2)
    caption = (
        "Psychometric functions per base rate. Grey curves are each participant's final QUEST+ "
        "posterior-mean Weibull (abandoned blocks left out); coloured curves are a pooled fit to "
        "all participants' trials, each trial under its own block's guess rate (for illustration - "
        "inference uses per-participant thresholds). Circles show the proportion hit in adjacent "
        "difference bins (area proportional to trials, bars = 95% Wilson intervals). The dotted "
        "horizontal line is the guess rate $\\gamma$, held fixed at the chance level each block ran "
        "with; the dotted vertical line marks the pooled threshold $\\theta$, where chance-adjusted "
        "detection reaches 63%; the line under each title gives the pooled fit's $\\theta$, slope "
        "$\\beta$ and trial count. Pooling weights participants by their trial count, and QUEST+ "
        "gives the longest blocks to the least certain observers, so the pooled threshold can differ "
        "from the mean of the participants' thresholds.")
    pooled_txt = ", ".join(f"{f} fps {study.pooled[f]['theta']:.1f} fps" for f in FPS_LEVELS if f in study.pooled)
    alt = (f"Three line plots of proportion hit against the frame-rate difference in frames per "
           f"second, one per base rate. Each shows S-shaped curves rising from the guess rate toward "
           f"1, with binned data points. Pooled thresholds: {pooled_txt}.")
    return FigureDoc("Fig02_psychometric", fig, caption, alt)


def fig_detection_overlay(study):
    """All three base rates on one scale: the pooled fits with the luck floor removed, against
    the difference as a share of the base rate.

    Raw P(hit) starts at the guess rate. Adjusting for chance (Abbott's formula) keeps only the
    share actually seen: P(seen) = (P(hit) - gamma) / (1 - gamma - lambda) = 1 - exp(-(x/theta)^beta),
    which runs 0-1 and crosses 63% exactly at theta. On a relative axis the three curves line up
    if the threshold is a fixed fraction of the base rate (Weber's law)."""
    fig = plt.figure(figsize=(COL_W, 3.0))
    gs = GridSpec(2, 1, figure=fig, height_ratios=[3.0, 1.15], hspace=0.08,
                  left=0.17, right=0.95, top=0.82, bottom=0.14)
    ax = fig.add_subplot(gs[0])
    strip = fig.add_subplot(gs[1], sharex=ax)
    crit = 1 - math.exp(-1)
    xs = np.linspace(0.1, 100, 600)
    for f in FPS_LEVELS:
        pooled = study.pooled.get(f)
        if not pooled:
            continue
        F = 1.0 - np.exp(-np.power(xs * f / 100.0 / pooled["theta"], pooled["beta"]))
        ax.plot(xs, F, color=FPS_COLOR[f], ls=FPS_LS[f], lw=1.5, zorder=3)
        ax.plot(100 * pooled["theta"] / f, crit, marker=FPS_MARKER[f], ms=FPS_MS[f] + 0.4,
                color=FPS_COLOR[f], mec=SURFACE, mew=0.6, ls="", zorder=4)
    ax.axhline(crit, color=MUTED, lw=0.6, ls=":", zorder=1)
    ax.annotate("63% = threshold $\\theta$", xy=(1, crit), xycoords=("axes fraction", "data"),
                xytext=(-2, -2), textcoords="offset points", ha="right", va="top", fontsize=FS_SMALL,
                color=INK_2)
    ax.set_ylim(0, 1.0)
    ax.set_ylabel("P(boundary seen),\ncorrected for chance")
    ax.tick_params(labelbottom=False)
    title(ax, "", "Pooled detection functions")
    u = study.used
    for k, f in enumerate(FPS_LEVELS):
        v = u[u["fps"] == f]["weberPct"]
        m, lo, hi = mean_ci(v)
        y = len(FPS_LEVELS) - 1 - k
        for val in v.dropna():
            strip.plot(val, y, marker="|", ms=5, color=tint(FPS_COLOR[f], 0.3), mew=0.9, zorder=2)
        if np.isfinite(lo):
            strip.plot([lo, hi], [y, y], color=FPS_COLOR[f], lw=1.1, solid_capstyle="butt", zorder=3)
        if np.isfinite(m):
            strip.plot(m, y, marker=FPS_MARKER[f], ms=FPS_MS[f] + 0.8, color=FPS_COLOR[f], mec=INK,
                       mew=0.6, ls="", zorder=4)
    strip.set_yticks(range(len(FPS_LEVELS)), [cond_label(f) for f in reversed(FPS_LEVELS)])
    strip.tick_params(axis="y", length=0)
    strip.set_ylim(-0.6, len(FPS_LEVELS) - 0.4)
    strip.grid(axis="y", visible=False)
    strip.set_xlim(0, 100)
    strip.set_xlabel("Frame-rate difference (% of the base rate)")
    strip.text(0.99, 0.04, "mean of participants ± 95% CI", transform=strip.transAxes,
               ha="right", va="bottom", fontsize=FS_SMALL, color=INK_2)
    fig_legend_top(fig, [line_handle(f, lw=1.5) for f in FPS_LEVELS], ncol=3, columnspacing=1.0)
    caption = (
        "Detection functions for the three base rates, corrected for chance: the pooled fits of "
        "Figure 2 with the hits expected from guessing removed, P(seen) = (P(hit) − "
        "$\\gamma$)/(1 − $\\gamma$ − $\\lambda$) = 1 − exp(−(x/$\\theta$)$^\\beta$), "
        "drawn against the difference as a percentage of the base rate. Corrected, all three run "
        "from 0 to 1 and cross 63% at their threshold; if the threshold were a fixed fraction of "
        "the base rate (Weber's law) the three would coincide. Below: each participant's Weber "
        "fraction (ticks) and the mean across participants with its 95% CI.")
    order = sorted((100 * study.pooled[f]["theta"] / f, cond_label(f)) for f in FPS_LEVELS if f in study.pooled)
    alt = ("Line chart of S-shaped detection curves rising from 0 to 1 against the frame-rate "
           "difference as a percentage of the base rate, one per base rate, with a strip below of mean "
           "Weber fractions. From earliest to latest 63% crossing: "
           + ", ".join(f"{n} ({t:.0f}%)" for t, n in order) + ".")
    return FigureDoc("Fig02b_detection_overlay", fig, caption, alt)


def trace_matrix(study, f, col, trials, relative=False):
    """trials x participants matrix of a posterior trace (abandoned blocks left out), final
    value carried forward once a block has stopped; relative=True divides by the stop rule."""
    cols = []
    for pid in study.pids:
        tr = study.traces.get((pid, f))
        fit = study.fit(pid, f)
        if tr is None or fit is None or fit["abandoned"]:
            continue
        s = tr.set_index("trial")[col].reindex(range(trials + 1)).ffill()
        if relative:
            s = s / float(fit["stopSD"])
        cols.append(s.to_numpy(float))
    return np.array(cols).T if cols else np.empty((trials + 1, 0))


def fig_convergence(study):
    fig, (ax_a, ax_b) = plt.subplots(1, 2, figsize=(TEXT_W, 2.5), layout="constrained",
                                     gridspec_kw={"width_ratios": [1.5, 1]})
    mt_ = int(study.fits["maxTrials"].max())
    for (pid, f), tr in study.traces.items():
        fit = study.fit(pid, f)
        rel = tr["sd"] / float(fit["stopSD"])
        col = tint(FPS_COLOR[f], 0.6 if not fit["abandoned"] else 0.85)
        ax_a.plot(tr["trial"], rel, color=col, ls=FPS_LS[f], lw=0.6, zorder=2)
        ax_a.plot(tr["trial"].iloc[-1], rel.iloc[-1], marker=FPS_MARKER[f], ms=FPS_MS[f] - 0.8,
                  color=tint(FPS_COLOR[f], 0.3), mec=SURFACE, mew=0.4, ls="", zorder=3)
    for f in FPS_LEVELS:
        M = trace_matrix(study, f, "sd", mt_, relative=True)
        if M.shape[1]:
            ax_a.plot(np.arange(mt_ + 1), np.exp(np.nanmean(np.log(M), axis=1)), color=FPS_COLOR[f],
                      ls=FPS_LS[f], lw=1.6, zorder=4)
    ax_a.axhline(1.0, color=INK_2, lw=0.8, zorder=1)
    ax_a.axhline(RELIABLE_SD_FACTOR, color=MUTED, lw=0.6, ls=":", zorder=1)
    ax_a.annotate("stop rule", xy=(0, 1.0), xycoords=("axes fraction", "data"), xytext=(3, -2),
                  textcoords="offset points", ha="left", va="top", fontsize=FS_SMALL, color=INK_2,
                  bbox=LABEL_BOX)
    ax_a.annotate(f"{RELIABLE_SD_FACTOR:g}×: reliability limit", xy=(0, RELIABLE_SD_FACTOR),
                  xycoords=("axes fraction", "data"), xytext=(3, -2), textcoords="offset points",
                  ha="left", va="top", fontsize=FS_SMALL, color=INK_2, bbox=LABEL_BOX)
    vals = np.concatenate([(tr["sd"] / float(study.fit(pid, f)["stopSD"])).to_numpy()
                           for (pid, f), tr in study.traces.items()])
    log_axis(ax_a, "y", nice_log_ticks(max(0.3, np.nanmin(vals) * 0.8), np.nanmax(vals) * 1.2))
    ax_a.set_xlabel("Trial")
    ax_a.set_ylabel("Posterior SD of $\\theta$ / stop rule (log)")
    ax_a.set_xlim(0, mt_ + 1)
    title(ax_a, "a", "Posterior SD after every trial")

    b = study.blocks
    for pid in study.pids:
        pts = [(COND_X[f], b[(b["pid"] == pid) & (b["fps"] == f)]["staircaseTrials"]) for f in FPS_LEVELS]
        vals = [(x, float(v.iloc[0])) for x, v in pts if len(v)]
        if len(vals) >= 2:
            ax_b.plot([v[0] for v in vals], [v[1] for v in vals], color=INDIVIDUAL, lw=0.7, zorder=1)
    for f in FPS_LEVELS:
        sub = b[b["fps"] == f]
        for _, r in sub.iterrows():
            if r["abandoned"]:
                ax_b.plot(COND_X[f], r["staircaseTrials"], marker="x", ms=4.2, color=FPS_COLOR[f],
                          mew=1.0, ls="", zorder=3)
                continue
            fps_mark(ax_b, COND_X[f], r["staircaseTrials"], f, ok=bool(r["converged"]))
        m, lo, hi = mean_ci(sub[sub["included"]]["staircaseTrials"])
        if np.isfinite(lo):
            ax_b.errorbar(COND_X[f] + 0.28, m, yerr=[[m - lo], [hi - m]], fmt="none", ecolor=INK,
                          elinewidth=0.9, capsize=2, zorder=4)
        if np.isfinite(m):
            ax_b.plot(COND_X[f] + 0.28, m, marker=FPS_MARKER[f], ms=FPS_MS[f] + 1.2, ls="",
                      color=FPS_COLOR[f], mec=INK, mew=0.7, zorder=5)
    mt = float(study.fits["maxTrials"].max())
    ax_b.axhline(mt, color=INK_2, lw=0.8, zorder=1)
    ax_b.annotate(f"trial limit ({mt:g})", xy=(0, mt), xycoords=("axes fraction", "data"),
                  xytext=(3, 3), textcoords="offset points", ha="left", va="bottom", fontsize=FS_SMALL,
                  color=INK_2)
    cond_axis(ax_b)
    ax_b.set_ylabel("Trials in block")
    ax_b.set_ylim(0, mt * 1.12)
    title(ax_b, "b", "Trials to stop")
    handles = condition_key() + [
        Line2D([], [], marker="o", ls="", ms=4, mfc=SURFACE, mec=MUTED, mew=0.9, label="Did not converge"),
        Line2D([], [], marker="x", ls="", ms=4, color=MUTED, mew=1.0, label="Abandoned")]
    fig_legend_top(fig, handles, ncol=5)
    fits = study.fits[~study.fits["abandoned"]]
    conv = fits["converged"]
    stops = ", ".join(f"{f} fps {s:g}" for f, s in fits.groupby("fps")["stopSD"].agg(lambda v: sorted(set(v))[0]).items())
    caption = (
        "Convergence. (a) Posterior standard deviation of $\\theta$ after every staircase trial, "
        "divided by the block's stop rule (log scale; trial 0 is the prior; 1 = the stop rule, "
        f"stop rules in fps: {stops}): thin lines are the {len(study.fits)} blocks, bold lines the "
        "geometric mean per base rate with each stopped block's final SD carried forward. Blocks "
        "stop once SD falls to the stop rule after at least the minimum number of trials, or at the "
        f"trial limit. (b) Trials each block used, with the mean per base rate and its 95% CI; "
        f"hollow markers did not reach the stop rule ({int((~conv).sum())} of {len(conv)} blocks), "
        "crosses are abandoned blocks.")
    alt = (f"Left: line plot of posterior SD relative to the stop rule, falling over trials, one line "
           f"per block. Right: dot plot of the number of staircase trials per block by base rate; "
           f"{int(conv.sum())} blocks met the precision target.")
    return FigureDoc("Fig03_convergence", fig, caption, alt)


def fig_order(study):
    """Threshold by the position the block was played in. The threshold's scale differs by
    base rate, so position effects are read on the Weber fraction."""
    b = study.used
    fig, ax = plt.subplots(figsize=(COL_W, 2.55), layout="constrained")
    vals = list(b.loc[b["reliable"], "weberPct"]) + [mean_ci(g["weberPct"])[2] for _, g in b.groupby("blockOrdinal")]
    top = max([v for v in vals if np.isfinite(v)] + [10.0]) * 1.08
    for pid in study.pids:
        sub = b[b["pid"] == pid].sort_values("blockOrdinal")
        ys = []
        for _, r in sub.iterrows():
            y = float(r["weberPct"])
            if y > top:
                edge_marks(ax, [r["blockOrdinal"]], [y], top, [FPS_COLOR[int(r["fps"])]], label_side="below")
                ys.append(top)
                continue
            fps_mark(ax, r["blockOrdinal"], y, int(r["fps"]), ok=bool(r["reliable"]))
            ys.append(y)
        ax.plot(sub["blockOrdinal"], ys, color=INDIVIDUAL, lw=0.8, zorder=1)
    for pos, g in b.groupby("blockOrdinal"):
        m, lo, hi = mean_ci(g["weberPct"])
        if np.isfinite(lo):
            ax.errorbar(pos + 0.24, m, yerr=[[m - lo], [min(hi, top) - m]], fmt="none", ecolor=INK,
                        elinewidth=0.9, capsize=2.2, capthick=0.9, zorder=4)
        if np.isfinite(m):
            ax.plot(pos + 0.24, m, marker="s", ms=4.6, color=INK, mec=SURFACE, mew=0.5, ls="", zorder=5)
    pos = sorted(study.blocks["blockOrdinal"].dropna().unique())
    ax.set_xticks(pos)
    ax.set_xlim(min(pos) - 0.4, max(pos) + 0.5)
    ax.tick_params(axis="x", length=0)
    ax.set_xlabel("Block position in session")
    ax.set_ylabel("Weber fraction $\\theta$ / base (%)")
    ax.set_ylim(0, top * 1.07)
    no_xgrid(ax)
    handles = condition_key() + [
        Line2D([], [], marker="s", ls="", ms=4.5, color=INK, mec=SURFACE, label="Mean ± 95% CI")]
    fig_legend_top(fig, handles, ncol=2, columnspacing=0.9)
    tr = linear_trend(study.used.pivot_table(index="pid", columns="blockOrdinal", values="weberPct").to_numpy(float))
    rows = study.blocks.groupby("pid")["latinRow"].first().value_counts().sort_index()
    caption = (
        "Order effects: each block's threshold as a Weber fraction by the position it was played in "
        "(Latin-square counterbalancing; rows used: " + ", ".join(f"{int(k)} × {int(v)}" for k, v in rows.items())
        + "); grey lines follow each participant through the session, colour and marker give the base "
        f"rate. Linear trend across positions: {fmt_num(tr['slope'], 2)} percentage points per block "
        f"[{fmt_num(tr['ci_lo'], 2)}, {fmt_num(tr['ci_hi'], 2)}], t({tr['df']}) = {fmt_num(tr['t'], 2)}, "
        f"{apa_p(tr['p'])} (n = {tr['n']}, participants with every block). Hollow markers are unreliable "
        "blocks; abandoned blocks are left out.")
    alt = ("Dot plot of Weber-fraction thresholds by block position one to three, participants joined by "
           "lines and markers coded by base rate, with a mean per position.")
    return FigureDoc("Fig04_order_effects", fig, caption, alt)


def fig_frame_timing(study):
    """Manipulation check: was each side of the boundary running at the rate QUEST+ asked for?"""
    has_frames = any(p.frames is not None for p in study.participants)
    fig, axes = plt.subplots(1, 4, figsize=(TEXT_W, 2.6), layout="constrained",
                             gridspec_kw={"width_ratios": [1.1, 1.0, 1.1, 1.0]})
    ax_a, ax_b, ax_c, ax_d = axes
    b = study.blocks
    if has_frames:
        centers = np.sqrt(FRAME_BINS[:-1] * FRAME_BINS[1:])
        for f in FPS_LEVELS:
            counts = sum(p.frames.hist.get((f, "base"), 0) for p in study.participants if p.frames is not None)
            if np.sum(counts) == 0:
                continue
            share = counts / np.sum(counts)
            ax_a.step(centers, np.where(share > 0, share, np.nan), where="mid", color=FPS_COLOR[f],
                      ls=FPS_LS[f], lw=1.0)
            ax_a.axvline(1000.0 / f, color=FPS_COLOR[f], lw=0.6, ls=":", gid="ref")
        ax_a.set_yscale("log")
        log_axis(ax_a, "x", [1, 2, 5, 10, 20, 50, 100])
        ax_a.set_xlim(FRAME_BINS[0], FRAME_BINS[-1])
        ax_a.set_xlabel("Base-side frame time (ms, log)")
        ax_a.set_ylabel("Share of live frames (log)")
        title(ax_a, "a", "Base side")
        ec = (ERR_BINS[:-1] + ERR_BINS[1:]) / 2
        for f in FPS_LEVELS:
            for side, ls in (("base", "-"), ("altered", (0, (2.5, 1.5)))):
                counts = sum(p.frames.err_hist.get((f, side), 0) for p in study.participants if p.frames is not None)
                if np.sum(counts) == 0:
                    continue
                share = counts / np.sum(counts)
                ax_b.step(ec, np.where(share > 0, share, np.nan), where="mid", color=FPS_COLOR[f], ls=ls,
                          lw=0.9 if side == "base" else 0.8)
        ax_b.set_yscale("log")
        ax_b.set_xlim(-1.0, 8.0)
        ax_b.axvline(0, color=INK_2, lw=0.6, gid="ref")
        ax_b.set_xlabel("Frame time − the cap's period (ms)")
        ax_b.set_ylabel("Share of live frames (log)")
        title(ax_b, "b", "Pacing error")
    else:
        for ax in (ax_a, ax_b):
            ax.text(0.5, 0.5, "needs PlayerLog\n(run without --no-frames)", transform=ax.transAxes,
                    ha="center", va="center", color=MUTED)
            ax.set_axis_off()
    # (c) Every round: the altered rate delivered against the one requested.
    r = study.responses
    for k, f in enumerate(reversed(FPS_LEVELS)):        # the low rates last, so they stay visible
        g = r[(r["fps"] == f) & r["measuredAlteredFps"].notna()]
        ax_c.scatter(g["alteredFps"], g["measuredAlteredFps"], s=9, marker=FPS_MARKER[f],
                     facecolor="none", edgecolor=FPS_COLOR[f], linewidth=0.6, zorder=2 + k)
    lim = (5, 600)
    ax_c.plot(lim, lim, color=INK_2, lw=0.7, zorder=1, gid="ref")
    log_axis(ax_c, "x", [10, 20, 50, 100, 200, 500])
    log_axis(ax_c, "y", [10, 20, 50, 100, 200, 500])
    ax_c.set_xlim(*lim)
    ax_c.set_ylim(*lim)
    ax_c.set_xlabel("Requested altered rate (fps)")
    ax_c.set_ylabel("Delivered altered rate (fps)")
    title(ax_c, "c", "Every round")
    # (d) Every block: the base rate it achieved, against the display's refresh.
    for i, pid in enumerate(study.pids):
        for f in FPS_LEVELS:
            q = b[(b["pid"] == pid) & (b["fps"] == f)]
            if q.empty:
                continue
            q = q.iloc[0]
            y = 100 * float(q["achievedBaseRatio"])
            x = COND_X[f] + (i - (len(study.pids) - 1) / 2) * 0.12
            fps_mark(ax_d, x, y, f, ok=not bool(q["aboveRefresh"]))
    ax_d.axhline(100, color=INK_2, lw=0.7, gid="ref")
    cond_axis(ax_d)
    ax_d.set_ylabel("Achieved / target base rate (%)")
    lo = np.nanmin(100 * b["achievedBaseRatio"]) if len(b) else 95
    ax_d.set_ylim(min(95.0, lo - 1.0), 101.0)
    title(ax_d, "d", "Every block")
    handles = [line_handle(f, lw=1.0) for f in FPS_LEVELS]
    handles += [Line2D([], [], color=INK_2, lw=0.8, ls=(0, (2.5, 1.5)), label="Altered side (b)"),
                Line2D([], [], marker="o", ls="", ms=4, mfc=SURFACE, mec=MUTED, mew=0.9,
                       label="Base above the display refresh (d)")]
    fig_legend_top(fig, handles, ncol=3)
    lims = b.groupby("pid")[["frameLimiter", "displayHz"]].first()
    hw = "; ".join(f"{pid}: {row.frameLimiter} limiter, {row.displayHz:g} Hz display" for pid, row in lims.iterrows())
    short = b[b["achievedBaseRatio"] < 0.97]
    short_txt = ("" if short.empty else " Base rates short of their target by more than 3%: "
                 + ", ".join(f"{s.pid} {cond_label(int(s.fps))} ({s.baseSideMeasuredFps:.1f} fps)" for s in short.itertuples()) + ".")
    caption = (
        "Manipulation check: were both sides of the boundary running at the rates QUEST+ asked for? "
        "(a) Frame times on the base side during live play (each round from its start to the shot), "
        "pooled over participants; dotted lines mark each base rate's period. (b) Each live frame's time "
        "minus the period of the cap that paced it, base side solid and altered side dashed: the "
        "frame limiter's timing error. (c) For every round, the altered side's measured rate against "
        "the rate requested (the identity line is perfect delivery); a rate the participant never "
        "flew into has no measurement. (d) The base rate each block achieved, as a share of its "
        f"target. Hardware: {hw}.{short_txt}")
    alt = ("Four panels checking frame delivery: base-side frame-time histograms peaking at each base "
           "rate's period, pacing errors concentrated near zero, delivered against requested altered "
           "rates along the identity line, and achieved base rates as a percentage of target per block.")
    return FigureDoc("Fig05_frame_timing", fig, caption, alt)


def outcome_counts(study):
    r = study.responses
    rows = []
    for (pid, f), g in r.groupby(["pid", "fps"]):
        c = {k: int((g["result"] == k).sum()) for k, _ in OUTCOME_ORDER}
        rows.append({"pid": pid, "fps": int(f), "abandoned": bool(g["abandoned"].fillna(False).any()), **c})
    return pd.DataFrame(rows)


def fig_outcomes(study):
    n = len(study.pids)
    h = 0.75 + 0.22 * 3 * n
    fig, ax = plt.subplots(figsize=(TEXT_W * 0.62, h))
    fig.subplots_adjust(left=0.27, right=0.98, top=1 - 0.45 / h, bottom=0.36 / h)
    c = outcome_counts(study)
    labels = []
    for i, pid in enumerate(study.pids):
        for j, f in enumerate(FPS_LEVELS):
            y = i * (len(FPS_LEVELS) + 0.6) + j
            row = c[(c["pid"] == pid) & (c["fps"] == f)]
            labels.append((y, f"{pid} · {f} fps"))
            if row.empty:
                continue
            row = row.iloc[0]
            total = sum(row[k] for k, _ in OUTCOME_ORDER)
            left = 0.0
            for k, _ in OUTCOME_ORDER:
                share = row[k] / total if total else 0
                if share <= 0:
                    continue
                ax.barh(y, share, left=left, height=0.78, color=OUTCOME_COLOR[k], edgecolor=SURFACE,
                        linewidth=1.0)
                if share >= 0.07:
                    lum = np.dot(to_rgb(OUTCOME_COLOR[k]), [0.299, 0.587, 0.114])
                    ax.text(left + share / 2, y, str(int(row[k])), ha="center", va="center",
                            fontsize=FS_SMALL, color=SURFACE if lum < 0.6 else INK)
                left += share
            if row["abandoned"]:
                ax.text(1.01, y, "abandoned", ha="left", va="center", fontsize=FS_SMALL, color=INK_2,
                        transform=ax.get_yaxis_transform(), clip_on=False)
    ax.set_xlim(0, 1)
    ax.set_xticks([0, 0.25, 0.5, 0.75, 1], ["0", ".25", ".50", ".75", "1"])
    ax.set_xlabel("Share of main-phase rounds")
    ax.grid(axis="y", visible=False)
    ax.set_yticks([y for y, _ in labels], [t for _, t in labels])
    ax.tick_params(axis="y", length=0)
    ax.invert_yaxis()
    ax.legend(handles=[Patch(facecolor=OUTCOME_COLOR[k], edgecolor="none", label=lab) for k, lab in OUTCOME_ORDER],
              loc="lower left", bbox_to_anchor=(0, 1.02), ncol=len(OUTCOME_ORDER), frameon=False,
              handlelength=1.0, columnspacing=0.9, borderaxespad=0)
    caption = (
        "What every main-phase round ended in: the shot landed within the hit radius of the boundary "
        "tower (hit); the shot landed outside it (missed shot); the round ran out of time without a "
        "shot (timed out); or the UFO crossed the boundary the maximum number of times (crossing "
        "limit). Every round counts for QUEST+, the last two as misses. Numbers are counts.")
    alt = ("Horizontal stacked bar chart, one bar per participant and base rate, showing the share of "
           "rounds that ended in a hit, a missed shot, a time-out or the crossing limit.")
    return FigureDoc("Fig06_outcomes", fig, caption, alt)


# ---------------------------------------------------------------- aiming and exposure

HIT_ZONE = "#cdeee1"              # the hit zone: a pale tint of the success accent
MISS_RED = "#c0392b"


def shots_of(study, outcome="all", converged_only=False):
    """Main-phase rounds that ended in a shot (abandoned blocks left out), with the
    difference that round presented, how far the shot landed from the tower and whether it
    hit. outcome: 'hit', 'miss' or 'all'."""
    r = study.responses
    d = r[r["shot"] & ~r["abandoned"].fillna(False) & r["absMissX"].notna()]
    if converged_only:
        d = d[d["converged"].fillna(False)]
    if outcome == "hit":
        d = d[d["isHit"] == 1]
    elif outcome == "miss":
        d = d[d["isHit"] == 0]
    return d.assign(x=d["diffFps"], y=d["absMissX"], w=100 * d["weber"])


def radius_of(study):
    r = study.responses["closeRadius"].dropna()
    return float(r.iloc[0]) if len(r) else CLOSE_RADIUS


def trend_panel(ax, d, f, hollow, xcol="x", r2=True, s=10):
    """Points + least-squares line for one base rate; returns the R^2 text (or None)."""
    col = FPS_COLOR[f]
    if hollow:
        ax.scatter(d[xcol], d["y"], s=s, marker=FPS_MARKER[f], facecolor=SURFACE, edgecolor=col,
                   linewidth=0.7, zorder=3)
    else:
        ax.scatter(d[xcol], d["y"], s=s, marker=FPS_MARKER[f], color=col, edgecolor=SURFACE,
                   linewidth=0.35, zorder=3)
    if r2 and len(d) >= 5 and d[xcol].nunique() > 2:
        fit = stats.linregress(d[xcol], d["y"])
        xs = np.array([d[xcol].min(), d[xcol].max()])
        ax.plot(xs, fit.intercept + fit.slope * xs, color=col, ls=FPS_LS[f], lw=1.0, zorder=2)
        return f"$R^2$ = {fit.rvalue ** 2:.2f}"
    return None


def bin_values(x, y, kind="median", min_per_bin=6, max_bins=7):
    x, y = np.asarray(x, float), np.asarray(y, float)
    order = np.argsort(x, kind="stable")
    x, y = x[order], y[order]
    k = int(max(1, min(max_bins, len(x) // min_per_bin)))
    edges = np.unique(np.quantile(x, np.linspace(0, 1, k + 1)))
    if len(edges) < 2:
        return pd.DataFrame({"x": [x.mean()], "y": [np.median(y)], "n": [len(x)]})
    idx = np.clip(np.searchsorted(edges, x, side="right") - 1, 0, len(edges) - 2)
    out = [{"x": float(np.median(x[idx == i])),
            "y": float(np.median(y[idx == i]) if kind == "median" else y[idx == i].mean()),
            "n": int((idx == i).sum())} for i in np.unique(idx)]
    return pd.DataFrame(out)


def fig_aiming(study):
    d = shots_of(study)
    radius = radius_of(study)
    fig, ax = plt.subplots(figsize=(COL_W, 2.6), layout="constrained")
    for f in FPS_LEVELS:
        g = d[d["fps"] == f]
        hit = g["isHit"] == 1
        ax.scatter(g.loc[hit, "w"], g.loc[hit, "y"], s=8, marker=FPS_MARKER[f], color=tint(FPS_COLOR[f], 0.3),
                   edgecolor=SURFACE, linewidth=0.3, zorder=2)
        ax.scatter(g.loc[~hit, "w"], g.loc[~hit, "y"], s=8, marker=FPS_MARKER[f], facecolor=SURFACE,
                   edgecolor=tint(FPS_COLOR[f], 0.1), linewidth=0.6, zorder=2)
    for f in FPS_LEVELS:
        g = d[d["fps"] == f]
        if len(g) >= 8:
            bb = bin_values(g["w"], g["y"], kind="median")
            ax.plot(bb["x"], bb["y"], color=FPS_COLOR[f], ls=FPS_LS[f], lw=1.5, zorder=4,
                    marker=FPS_MARKER[f], ms=FPS_MS[f] - 0.6, mec=SURFACE, mew=0.5)
    ax.axhspan(0, radius, color=HIT_ZONE, lw=0, zorder=0, gid="ref")
    ax.set_xlim(0, 100)
    ax.set_ylim(bottom=0)
    ax.set_xlabel("Frame-rate difference (% of the base rate)")
    ax.set_ylabel("Shot–tower distance (world units)")
    handles = [line_handle(f, lw=1.5) for f in FPS_LEVELS]
    handles += [Line2D([], [], marker="o", ls="", ms=4, mfc=SURFACE, mec=MUTED, mew=0.6, label="Missed shot"),
                Patch(facecolor=HIT_ZONE, label=f"Hit zone (≤ {radius:g})")]
    fig_legend_top(fig, handles, ncol=2, columnspacing=0.8)
    caption = (
        "Aiming error against the frame-rate difference the round presented, as a percentage of "
        f"the base rate. Shots within {radius:g} world units of the boundary tower (shaded) count as "
        "hits. Each point is a shot (filled = hit, hollow = miss; colour and marker = base rate); "
        "lines join medians of adjacent difference bins. Abandoned blocks are left out.")
    alt = ("Scatter of shot distance from the boundary tower against the frame-rate difference as a "
           "percentage of the base rate, with binned medians per base rate.")
    return FigureDoc("Fig11_aiming", fig, caption, alt)


def _exposure_rows(study):
    d = shots_of(study)
    return d[d["exposures"].notna()]


def fig_exposure(study):
    d = _exposure_rows(study)
    fig, ax = plt.subplots(figsize=(COL_W, 2.45), layout="constrained")
    for f in FPS_LEVELS:
        g = d[d["fps"] == f]
        if g.empty:
            continue
        jit = (np.arange(len(g)) % 7 - 3) * 0.05
        ax.scatter(g["w"], g["exposures"] + jit, s=5, marker=FPS_MARKER[f],
                   color=tint(FPS_COLOR[f], 0.35), edgecolor="none", zorder=2)
    for f in FPS_LEVELS:
        g = d[d["fps"] == f]
        if len(g) >= 8:
            bb = bin_values(g["w"], g["exposures"], kind="mean")
            ax.plot(bb["x"], bb["y"], color=FPS_COLOR[f], lw=1.5, ls=FPS_LS[f], zorder=4,
                    marker=FPS_MARKER[f], ms=FPS_MS[f] - 0.5, mec=SURFACE, mew=0.4)
    ax.set_xlim(0, 100)
    ax.set_ylim(bottom=0)
    ax.yaxis.set_major_locator(mpl.ticker.MaxNLocator(integer=True))
    ax.set_xlabel("Frame-rate difference (% of the base rate)")
    ax.set_ylabel("Boundary crossings before the shot")
    fig_legend_top(fig, [line_handle(f, lw=1.5) for f in FPS_LEVELS], ncol=3)
    caption = (
        "How often the participant crossed the boundary - and so saw the frame rate change - "
        "before shooting, against the difference the round presented (as a percentage of the base "
        "rate). Points are shots (jittered vertically); lines join bin means per base rate.")
    alt = ("Scatter of the number of boundary crossings before each shot against the frame-rate "
           "difference, with binned means per base rate.")
    return FigureDoc("Fig12_exposure", fig, caption, alt)


def _binned_exposure(ax, d, width=10.0):
    for k, f in enumerate(FPS_LEVELS):
        g = d[d["fps"] == f]
        if g.empty:
            continue
        lo = np.floor(g["w"] / width) * width
        rows = []
        for b0, gg in g.assign(lo=lo).groupby("lo"):
            v = gg["exposures"].to_numpy(float)
            if len(v) < 3:
                continue
            ci = stats.t.ppf(0.975, len(v) - 1) * v.std(ddof=1) / math.sqrt(len(v)) if len(v) > 1 else 0
            rows.append((b0 + width / 2 + (k - 1) * 1.2, v.mean(), ci))
        if rows:
            xs, ms_, cis = zip(*rows)
            ax.errorbar(xs, ms_, yerr=cis, color=SURFACE, lw=2.2, zorder=3, marker=FPS_MARKER[f],
                        ms=FPS_MS[f] + 1)
            ax.errorbar(xs, ms_, yerr=cis, color=FPS_COLOR[f], lw=1.2, ls=FPS_LS[f], zorder=4,
                        marker=FPS_MARKER[f], ms=FPS_MS[f], mec=SURFACE, mew=0.5, capsize=2.5)


def fig_exposure_main(study, bubbles=False):
    d = _exposure_rows(study)
    fig, ax = plt.subplots(figsize=(TEXT_W * 0.6, 2.9), layout="constrained")
    if bubbles:
        for f in FPS_LEVELS:
            g = d[d["fps"] == f]
            if g.empty:
                continue
            c = g.assign(b=(g["w"] // 5) * 5 + 2.5).groupby(["b", "exposures"]).size().reset_index(name="n")
            ax.scatter(c["b"], c["exposures"], s=5 + np.sqrt(c["n"]) * 10, marker=FPS_MARKER[f],
                       color=FPS_COLOR[f], edgecolor=SURFACE, linewidth=0.2, alpha=0.35, zorder=2)
    _binned_exposure(ax, d)
    ax.set_xlim(0, 100)
    ax.set_ylim(-0.3, max(4.0, float(d["exposures"].max()) + 0.7) if len(d) else 5)
    ax.yaxis.set_major_locator(mpl.ticker.MaxNLocator(integer=True))
    ax.set_xlabel("Frame-rate difference (% of the base rate)")
    ax.set_ylabel("Boundary crossings before the shot")
    handles = [line_handle(f, lw=1.2) for f in FPS_LEVELS]
    if bubbles:
        handles.append(Line2D([], [], marker="o", ls="", color=MUTED, alpha=0.5, label="Shots (bubble area = count)"))
    fig_legend_top(fig, handles, ncol=2)
    caption = ("Boundary crossings before the shot against the frame-rate difference (as a percentage "
               "of the base rate), averaged in 10-percentage-point bins (mean ± 95% CI, bins with at "
               "least 3 shots)" + ("; bubbles show where the individual shots fall (area = count)." if bubbles else "."))
    alt = "Line plot of mean boundary crossings before the shot per difference bin, one line per base rate."
    return FigureDoc("Fig12c_exposure_main_heatmap" if bubbles else "Fig12b_exposure_main", fig, caption, alt)


# ---------------------------------------------------------------- sessions and staircases

def fig_threshold_sd_sessions_values(study, values=True):
    """Every session's final threshold +- posterior SD, one panel per base rate (each on its
    own scale), the value printed upright on top of its bar."""
    fits = study.fits
    pids = study.pids
    width = max(TEXT_W, len(pids) * 0.45 * 3)
    fig, axes = plt.subplots(1, 3, figsize=(width, 3.1), layout="constrained")
    xs = np.arange(1, len(pids) + 1)
    upright = []
    for ax, f, letter in zip(axes, FPS_LEVELS, "abc"):
        top = 0.0
        for x, pid in zip(xs, pids):
            row = fits[(fits["pid"] == pid) & (fits["fps"] == f)]
            if row.empty:
                continue
            row = row.iloc[0]
            if row["abandoned"]:
                ax.annotate(f"abandoned\n({int(row['trials'])} trial)", (x, 0), xytext=(0, 3),
                            textcoords="offset points", ha="center", va="bottom", rotation=90,
                            fontsize=FS_SMALL, color=MUTED)
                continue
            theta, sd = float(row["theta"]), float(row["thetaSd"])
            conv, rel = bool(row["converged"]), bool(row["reliable"])
            col = FPS_COLOR[f] if conv else tint(FPS_COLOR[f], 0.35)
            ax.errorbar(x, theta, yerr=sd, fmt="none", ecolor=col, elinewidth=1.0, capsize=2.0,
                        zorder=2, ls="-" if rel else ":")
            ax.plot(x, theta, marker=FPS_MARKER[f], ms=FPS_MS[f] + 0.6, ls="",
                    mfc=col if conv else SURFACE, mec=INK if conv else col, mew=0.6 if conv else 1.0,
                    zorder=3)
            top = max(top, theta + sd)
            if values:
                text = f"{theta:.1f} ± {sd:.1f}" + (" ≥" if row["rangeEdge"] == "top" else "")
                t = ax.annotate(text, (x, theta + sd), xytext=(0, 2.5), textcoords="offset points",
                                ha="center", va="bottom", rotation=90, fontsize=FS_SMALL,
                                fontweight="bold" if conv else "normal", color=col)
                upright.append((t, theta + sd))
        ax.set_xticks(xs, [p.lstrip("P") for p in pids])
        ax.set_xlim(0.4, len(pids) + 0.6)
        ax.set_xlabel("Participant")
        ax.grid(False)
        ax.set_ylim(0, max(top, 1.0) * (1.6 if values else 1.12))
        title(ax, letter, f"Base {cond_label(f)}")
    axes[0].set_ylabel("JND threshold $\\theta$ (fps)")
    fig.canvas.draw()
    for ax in axes:
        ymax = ax.get_ylim()[1]
        for _ in range(3):
            fig.canvas.draw()
            h = ax.get_window_extent().height
            need = ymax
            for t, cap in upright:
                if t.axes is not ax:
                    continue
                cap_px = ax.transData.transform((0, cap))[1]
                length = t.get_window_extent().y1 - cap_px
                need = max(need, cap / max(0.2, 0.97 - length / h))
            if need <= ymax * 1.001:
                break
            ymax = need
            ax.set_ylim(0, ymax)
    handles = [Line2D([], [], marker="o", ls="-", ms=4.5, color=INK_2, mfc=INK_2, mec=INK, mew=0.6,
                      lw=1.0, label="Converged (SD ≤ stop rule)"),
               Line2D([], [], marker="o", ls=":", ms=4.5, color=MUTED, mfc=SURFACE, mec=MUTED, mew=1.0,
                      lw=1.0, label="Not converged / dotted bar: unreliable")]
    fig_legend_top(fig, handles, ncol=2)
    stops = ", ".join(f"{f} fps {s:g}" for f, s in study.fits.groupby("fps")["stopSD"].agg(lambda v: sorted(set(v))[0]).items())
    caption = (
        "Final JND threshold of each session at the three base rates"
        + (", with its value printed as posterior mean ± posterior SD (fps)" if values else "")
        + ": marker = mean, bar = SD (the quantity the stop rule targets; stop rules in fps: "
        f"{stops}). Filled markers converged (final SD at most the stop rule)"
        + (" and carry their value in bold" if values else "") + "; hollow, paler markers ended at the "
        "trial limit without converging; a dotted bar marks an unreliable block (SD above "
        f"{RELIABLE_SD_FACTOR:g}× the stop rule, or the estimate pressed against the top of the "
        "difference range, ≥). Abandoned blocks are labelled, not plotted. Each panel has its own scale.")
    alt = ("Three dot plots, one per base rate, of each participant's final threshold with a "
           "posterior-SD error bar" + (" and its numeric value printed above the bar." if values else "."))
    return FigureDoc("Fig13b_threshold_sd_sessions_values" if values else "Fig13_threshold_sd_sessions",
                     fig, caption, alt)


def fig_threshold_sd_sessions(study):
    return fig_threshold_sd_sessions_values(study, values=False)


def staircase_panel(ax, tr, f, show_band=True):
    tr = tr[tr["trial"] > 0]
    if show_band:
        ax.fill_between(tr["trial"], np.maximum(0, tr["theta"] - tr["sd"]), tr["theta"] + tr["sd"],
                        color=tint(FPS_COLOR[f], 0.82), lw=0, zorder=1)
    ax.plot(tr["trial"], tr["theta"], color=FPS_COLOR[f], lw=1.1, ls=FPS_LS[f], zorder=2)
    hit = tr["isHit"] == 1
    ax.plot(tr["trial"][hit], tr["diffFps"][hit], marker=FPS_MARKER[f], ls="", ms=FPS_MS[f] - 1.0,
            color=FPS_COLOR[f], mec=SURFACE, mew=0.4, zorder=3)
    ax.plot(tr["trial"][~hit], tr["diffFps"][~hit], marker=FPS_MARKER[f], ls="", ms=FPS_MS[f] - 0.8,
            mfc=SURFACE, mec=FPS_COLOR[f], mew=0.8, zorder=3)


def _final_note(tr, fit, col="theta", fmt="$\\hat\\theta$ = {:.1f} ± {:.1f}"):
    last = tr[tr["trial"] > 0].iloc[-1]
    text = fmt.format(last[col], last["sd"]) if "{:.1f} ±" in fmt else fmt.format(last[col])
    tags = []
    if fit["abandoned"]:
        tags.append("abandoned")
    elif not fit["converged"]:
        tags.append("max. trials" if fit["trials"] >= fit["maxTrials"] else "not converged")
    if fit["rangeEdge"]:
        tags.append(f"≥ range {'top' if fit['rangeEdge'] == 'top' else 'bottom'}")
    elif not fit["reliable"] and not fit["abandoned"]:
        tags.append("‡ unreliable")
    return text + ("\n" + ", ".join(tags) if tags else "")


def staircase_grid(study, value_label, draw, note, ylims):
    """Participants x base rates small multiples, each column on its own scale."""
    pids = study.pids
    n = len(pids)
    fig, axes = plt.subplots(n, 3, figsize=(TEXT_W, 0.85 + 1.0 * n), sharex=True, squeeze=False,
                             layout="constrained")
    notes = []
    for i, pid in enumerate(pids):
        for j, f in enumerate(FPS_LEVELS):
            ax = axes[i, j]
            tr = study.traces.get((pid, f))
            if tr is None:
                ax.text(0.5, 0.5, "no block", transform=ax.transAxes, ha="center", va="center", color=MUTED)
            else:
                draw(ax, tr, f)
                notes.append((ax, note(tr, study.fit(pid, f))))
            ax.set_ylim(*ylims[f])
            if i == 0:
                ax.set_title(f"Base {cond_label(f)}", loc="center", fontsize=FS_BASE)
            if j == 0:
                ax.set_ylabel(pid, rotation=0, ha="right", va="center", fontweight="bold")
            if i == n - 1:
                ax.set_xlabel("Trial")
    axes[0, 0].set_xlim(0, max(study.fits["maxTrials"].max(), 10) + 1)
    fig.supylabel(value_label)
    fig.canvas.draw()
    for ax, text in notes:
        place_note(ax, text, candidates=("upper right", "upper left", "lower right", "lower left"))
    return fig


def fig_staircases(study):
    ylims = {}
    for f in FPS_LEVELS:
        vals = [0.0]
        for (pid, ff), tr in study.traces.items():
            if ff == f:
                t = tr[tr["trial"] > 0]
                vals += list(t["diffFps"]) + list(t["theta"] + t["sd"])
        ylims[f] = (0, max(vals) * 1.08 if max(vals) > 0 else f)
    fig = staircase_grid(study, "Frame-rate difference (fps)", lambda ax, tr, f: staircase_panel(ax, tr, f),
                         _final_note, ylims)
    handles = [Line2D([], [], marker="o", ls="", ms=3.5, color=INK_2, mec=SURFACE, label="Hit"),
               Line2D([], [], marker="o", ls="", ms=3.5, mfc=SURFACE, mec=INK_2, mew=0.8, label="Miss"),
               Line2D([], [], color=INK_2, lw=1.1, label="Threshold estimate"),
               Patch(facecolor=tint(INK_2, 0.82), label="±1 posterior SD")]
    fig_legend_top(fig, handles, ncol=4)
    n = len(study.pids)
    caption = (
        "QUEST+ staircases. Each panel is one block: markers are the frame-rate difference "
        "presented on every staircase trial (filled = hit, hollow = miss, which includes rounds that "
        "timed out or hit the crossing limit); the line is the posterior-mean threshold after that "
        "trial and the band ±1 posterior SD (replayed from the trial data; identical to the online "
        "estimate under the logged guess rates). Text gives the final estimate and flags blocks that "
        "ran to the trial limit, were abandoned, or ended pressed against the top of the difference "
        "range. Each column has its own scale.")
    alt = (f"Grid of {n} by 3 small scatter plots, participants as rows and base rates as columns, "
           "showing the presented frame-rate difference over staircase trials with a threshold-estimate "
           "line that settles as trials accumulate.")
    return FigureDoc("FigS8_staircases", fig, caption, alt)


def fig_slope_staircases(study):
    def draw(ax, tr, f):
        t = tr[tr["trial"] > 0]
        ax.plot(t["trial"], t["beta"], color=FPS_COLOR[f], lw=1.1, ls=FPS_LS[f], zorder=2)
        hit = t["isHit"] == 1
        ax.plot(t["trial"][hit], t["beta"][hit], marker=FPS_MARKER[f], ls="", ms=FPS_MS[f] - 1.0,
                color=FPS_COLOR[f], mec=SURFACE, mew=0.4, zorder=3)
        ax.plot(t["trial"][~hit], t["beta"][~hit], marker=FPS_MARKER[f], ls="", ms=FPS_MS[f] - 0.8,
                mfc=SURFACE, mec=FPS_COLOR[f], mew=0.8, zorder=3)

    vals = np.concatenate([tr.loc[tr["trial"] > 0, "beta"].to_numpy(float) for tr in study.traces.values()])
    lo, hi = float(np.nanmin(vals)), float(np.nanmax(vals))
    pad = max(0.2, 0.08 * (hi - lo))
    fig = staircase_grid(study, "Slope $\\beta$ (posterior mean)", draw,
                         lambda tr, fit: _final_note(tr, fit, col="beta", fmt="$\\hat\\beta$ = {:.2f}"),
                         {f: (max(0, lo - pad), hi + pad) for f in FPS_LEVELS})
    handles = [Line2D([], [], marker="o", ls="", ms=3.5, color=INK_2, mec=SURFACE, label="Hit"),
               Line2D([], [], marker="o", ls="", ms=3.5, mfc=SURFACE, mec=INK_2, mew=0.8, label="Miss"),
               Line2D([], [], color=INK_2, lw=1.1, label="Slope estimate")]
    fig_legend_top(fig, handles, ncol=3)
    sl = study.trials
    caption = (
        "QUEST+ slope traces. Each panel is one block: markers show the posterior-mean Weibull slope "
        "after each trial (filled = hit, hollow = miss), and the line connects the estimates across "
        "trials. Text gives the final estimate and flags blocks that ran to the trial limit or were "
        "abandoned. The slope grid runs over the logged cfg_slopeMin..cfg_slopeMax range, so estimates "
        "near its ends sit at the grid's floor or ceiling.")
    alt = (f"Grid of {len(study.pids)} by 3 small plots, participants as rows and base rates as columns, "
           "showing posterior Weibull slope estimates over staircase trials.")
    return FigureDoc("Fig14_slope_staircases", fig, caption, alt)


def response_grid(study, outcome):
    """Participants x base rates grid of shot-tower distance against the difference."""
    n = len(study.pids)
    data = {(p.pid, f): shots_of(study, outcome).query("pid == @p.pid and fps == @f")
            for p in study.participants for f in FPS_LEVELS}
    ally = np.concatenate([d["y"].to_numpy() for d in data.values()] or [np.array([1.0])])
    radius = radius_of(study)
    ymax = max(float(np.nanmax(ally)) * 1.08 if len(ally) else 1.0, radius * 1.15 if outcome == "hit" else 0)
    fig, axes = plt.subplots(n, 3, figsize=(TEXT_W, 0.75 + 0.9 * n), sharey=True, squeeze=False,
                             layout="constrained")
    notes = []
    for i, p in enumerate(study.participants):
        for j, f in enumerate(FPS_LEVELS):
            ax = axes[i, j]
            d = data[(p.pid, f)]
            ax.axhspan(0, radius, color=HIT_ZONE, lw=0, zorder=0, gid="ref")
            if d.empty:
                ax.text(0.5, 0.5, "none", transform=ax.transAxes, ha="center", va="center",
                        color=MUTED, fontsize=FS_SMALL)
            else:
                note = trend_panel(ax, d, f, hollow=(outcome != "hit"))
                notes.append((ax, (note + f", n = {len(d)}") if note else f"n = {len(d)}"))
            hi = study.pooled.get(f, {}).get("gridHi", f)
            ax.set_xlim(0, hi)
            if i == 0:
                ax.set_title(f"Base {cond_label(f)}", loc="center", fontsize=FS_BASE)
            if j == 0:
                ax.set_ylabel(p.pid, rotation=0, ha="right", va="center", fontweight="bold")
            if i == n - 1:
                ax.set_xlabel("Frame-rate difference (fps)")
    axes[0, 0].set_ylim(0, ymax)
    fig.supylabel("Shot–tower distance (world units)")
    fig.canvas.draw()
    for ax, text in notes:
        place_note(ax, text, candidates=("upper right", "upper left", "center right"))
    return fig, radius


def fig_laser_distance_from_tower(study, outcome="hit"):
    is_hit = outcome == "hit"
    fig, radius = response_grid(study, outcome)
    handles = [Line2D([], [], marker="o", ls="-", ms=4, color=INK_2, mfc=INK_2 if is_hit else SURFACE,
                      mec=SURFACE if is_hit else INK_2, lw=1.0,
                      label=("Hit" if is_hit else "Missed shot") + " + least-squares trend"),
               Patch(facecolor=HIT_ZONE, label=f"Hit zone (≤ {radius:g})")]
    fig_legend_top(fig, handles, ncol=2)
    n = len(study.pids)
    caption = (
        f"Aiming for {'hits' if is_hit else 'missed shots'} by participant and base rate: distance "
        "between where the shot landed and the boundary tower, against the frame-rate difference the "
        "round presented. Lines are least-squares fits with $R^2$ and n (drawn only with at least 5 "
        f"points); the shaded band is the hit zone (≤ {radius:g} world units). Rounds that ended "
        "without a shot, and abandoned blocks, are left out.")
    alt = (f"Grid of {n} participant rows and three base-rate columns of scatter plots of shot-to-tower "
           f"distance for {'hits' if is_hit else 'misses'} against the frame-rate difference.")
    return FigureDoc("Fig16_laser_distance_from_tower" if is_hit else "Fig18_laser_misses", fig, caption, alt)


def _aggregate_figure(study, merged, converged_only=False):
    d = shots_of(study, "hit", converged_only=converged_only)
    radius = radius_of(study)
    if merged:
        fig, ax = plt.subplots(figsize=(COL_W, 2.7), layout="constrained")
        axes = [ax] * 3
    else:
        fig, axes = plt.subplots(1, 3, figsize=(TEXT_W, 2.6), sharey=True, layout="constrained")
    handles = []
    xcol = "w" if merged else "x"
    for ax, f, letter in zip(axes, FPS_LEVELS, "abc"):
        g = d[d["fps"] == f]
        note = trend_panel(ax, g, f, hollow=False, xcol=xcol, s=4) if len(g) else None
        handles.append(Line2D([], [], marker=FPS_MARKER[f], ls=FPS_LS[f], ms=4, color=FPS_COLOR[f], mec=SURFACE,
                              lw=1.0, label=f"{f} fps" + (f" ({note}, n = {len(g)})" if note and merged else "")))
        if not merged:
            title(ax, letter, f"Base {cond_label(f)}")
            ax.set_title(f"{note}, n = {len(g)}" if note else f"n = {len(g)}", loc="right",
                         fontsize=FS_SMALL, fontweight="normal", color=INK_2)
            ax.set_xlim(0, study.pooled.get(f, {}).get("gridHi", f))
            ax.set_xlabel("Frame-rate difference (fps)")
    if merged:
        axes[0].set_xlim(0, 100)
        axes[0].set_xlabel("Frame-rate difference (% of the base rate)")
    axes[0].set_ylabel("Shot–tower distance (world units)")
    for ax in set(axes):
        ax.axhspan(0, radius, color=HIT_ZONE, lw=0, zorder=0, gid="ref")
        ax.set_ylim(0, radius * 1.15)
    handles.append(Patch(facecolor=HIT_ZONE, label=f"Hit zone (≤ {radius:g})"))
    fig_legend_top(fig, handles, ncol=2 if merged else 4)
    caption = ("All participants' hits - the distance from the shot to the boundary tower - against the "
               "frame-rate difference, " + ("the three base rates on one axis as a percentage of the base "
               "rate (colour, marker and line style)" if merged else "one panel per base rate")
               + "; lines are least-squares fits with $R^2$ and n."
               + (" Only converged blocks." if converged_only else ""))
    alt = (f"{'Merged scatter' if merged else 'Side-by-side scatters'} of hit distance from the tower against "
           "the frame-rate difference for 60, 144 and 500 fps base rates, with trend lines.")
    return FigureDoc("Fig22_aggregate_laser_hits_merged" if merged else "Fig20_aggregate_laser_hits", fig, caption, alt)


def fig_aggregate_laser_hits(study):
    return _aggregate_figure(study, merged=False)


def fig_aggregate_laser_hits_merged(study):
    return _aggregate_figure(study, merged=True)


BIN_PCT = 10.0


def _aggregate_binned_figure(study, converged_only=False):
    """Hits averaged in BIN_PCT-wide bins of the difference (as % of the base rate), one
    panel per base rate: bin means +- SEM, a least-squares line through the bin means."""
    d = shots_of(study, "hit", converged_only=converged_only)
    radius = radius_of(study)
    fig, axes = plt.subplots(1, 3, figsize=(TEXT_W, 2.6), sharex=True, sharey=True, layout="constrained")
    for ax, f, letter in zip(axes, FPS_LEVELS, "abc"):
        g = d[d["fps"] == f]
        col = FPS_COLOR[f]
        note = None
        if len(g):
            lo = np.floor(g["w"] / BIN_PCT) * BIN_PCT
            gg = g.assign(lo=lo).groupby("lo")
            bb = pd.DataFrame({"x": gg["w"].mean(), "y": gg["y"].mean(), "sem": gg["y"].sem(),
                               "n": gg["y"].size()}).reset_index()
            ax.errorbar(bb["x"], bb["y"], yerr=bb["sem"], fmt="none", ecolor=col, elinewidth=0.9,
                        capsize=2.0, zorder=2)
            ax.plot(bb["x"], bb["y"], marker=FPS_MARKER[f], ms=4.5, ls="", mfc=col, mec=SURFACE, mew=0.5,
                    zorder=3)
            if len(bb) >= 3:
                fit = stats.linregress(bb["x"], bb["y"])
                xs = np.array([bb["x"].min(), bb["x"].max()])
                ax.plot(xs, fit.intercept + fit.slope * xs, color=col, ls=FPS_LS[f], lw=1.0, zorder=1)
                note = f"$R^2$ = {fit.rvalue ** 2:.2f}"
            stat = f"{len(bb)} bins, n = {len(g)}"
        else:
            stat = "n = 0"
        title(ax, letter, f"Base {cond_label(f)}")
        ax.set_title(f"{note}, {stat}" if note else stat, loc="right", fontsize=FS_SMALL,
                     fontweight="normal", color=INK_2)
        ax.set_xlabel("Difference (% of base)")
        ax.axhspan(0, radius, color=HIT_ZONE, lw=0, zorder=0, gid="ref")
    axes[0].set_ylabel("Mean shot–tower distance\n(world units)")
    axes[0].set_xlim(0, 100)
    axes[0].set_ylim(0, radius * 1.15)
    handles = [line_handle(f, lw=1.0) for f in FPS_LEVELS] + [
        Line2D([], [], color=INK_2, lw=0.9, marker="_", ms=6, label="±1 SEM"),
        Patch(facecolor=HIT_ZONE, label=f"Hit zone (≤ {radius:g})")]
    fig_legend_top(fig, handles, ncol=5)
    caption = (f"All participants' hits (distance from the shot to the boundary tower) against the "
               f"frame-rate difference, binned in {BIN_PCT:g}-percentage-point bins of the base rate, one "
               "panel per base rate. Each point is a bin: the mean difference and mean distance of the "
               "hits in it, with ±1 SEM; empty bins are not drawn. Lines are least-squares fits "
               "through the bin means (unweighted), with their $R^2$; n is the number of hits."
               + (" Only converged blocks (final posterior SD at most the stop rule)." if converged_only else ""))
    alt = ("Three plots of binned hit distance against the frame-rate difference, one per base rate, bin "
           "means with standard-error bars and trend lines.")
    return FigureDoc("Fig29_aggregate_laser_hits_binned_converged" if converged_only
                     else "Fig27_aggregate_laser_hits_binned", fig, caption, alt)


def fig_aggregate_laser_hits_binned(study):
    return _aggregate_binned_figure(study)


def fig_aggregate_laser_hits_binned_converged(study):
    return _aggregate_binned_figure(study, converged_only=True)


def _cdf_figure(study, dots=True):
    d = shots_of(study)
    radius = radius_of(study)
    fig, ax = plt.subplots(figsize=(COL_W, 2.7), layout="constrained")
    handles = []
    for f in FPS_LEVELS:
        g = d[d["fps"] == f].sort_values("y")
        if g.empty:
            continue
        y = g["y"].to_numpy()
        h = g["isHit"].to_numpy()
        p = np.arange(1, len(y) + 1) / len(y)
        ax.plot(y, p, color=FPS_COLOR[f], lw=1.0, ls=FPS_LS[f], zorder=4, drawstyle="steps-post")
        if dots:
            ax.plot(y[h == 1], p[h == 1], marker="o", ms=2.0, ls="", mfc="none", mec=OUTCOME_COLOR["hit"],
                    mew=0.5, alpha=0.6, zorder=2)
            ax.plot(y[h == 0], p[h == 0], marker="o", ms=2.0, ls="", mfc="none", mec=MISS_RED, mew=0.5,
                    alpha=0.6, zorder=3)
        handles.append(Line2D([], [], color=FPS_COLOR[f], lw=1.2, ls=FPS_LS[f], label=f"{f} fps (n = {len(g)})"))
    ax.axvspan(0, radius, color=HIT_ZONE, lw=0, zorder=0, gid="ref")
    handles.append(Patch(facecolor=HIT_ZONE, label=f"Hit zone (≤ {radius:g})"))
    if dots:
        handles += [Line2D([], [], color="none", marker="o", ms=2.5, mfc="none", mec=OUTCOME_COLOR["hit"],
                           mew=0.6, label="Hit"),
                    Line2D([], [], color="none", marker="o", ms=2.5, mfc="none", mec=MISS_RED, mew=0.6,
                           label="Miss")]
    ax.set_xlabel("Shot–tower distance (world units)")
    ax.set_ylabel("Cumulative proportion of shots")
    ax.set_xlim(left=0)
    ax.set_ylim(0, 1.05)
    fig_legend_top(fig, handles, ncol=2 if dots else 1)
    caption = ("Cumulative distribution of the shot–tower distance of every shot across all "
               "participants, per base rate" + ("; dots mark hits (green) and misses (red)." if dots else "."))
    alt = "Cumulative distribution plot of shot distance from the boundary tower, one curve per base rate."
    return FigureDoc("Fig31_cdf_laser_all" if dots else "Fig39_cdf_laser_lines", fig, caption, alt)


def fig_cdf_laser_all(study):
    return _cdf_figure(study, dots=True)


def fig_cdf_laser_lines(study):
    return _cdf_figure(study, dots=False)


# ---------------------------------------------------------------- summaries

def _subset(study, which):
    b = study.used
    if which == "reliable":
        return b[b["reliable"]]
    if which == "converged":
        return b[b["converged"] & b["reliable"]]
    return b


WHICH_TEXT = {None: "", "reliable": " after excluding unreliable blocks (final posterior SD above "
              f"{RELIABLE_SD_FACTOR:g}× the stop rule, or pressed against the end of the difference range)",
              "converged": " for blocks that converged (final posterior SD at most the stop rule) and are reliable"}


def _threshold_summary(study, which=None):
    b = _subset(study, which)
    fig, ax = plt.subplots(figsize=(TEXT_W * 0.75, 2.6), layout="constrained")
    ax.set_yscale("log")
    labels, lows, highs = [], [], []
    for x, f in enumerate(FPS_LEVELS, start=1):
        sub = b[b["fps"] == f]
        v = sub["theta"].to_numpy(float)
        v = v[np.isfinite(v)]
        labels.append(f"Base\n{cond_label(f)}")
        if not len(v):
            ax.text(x, 1.0, "no block", transform=ax.get_xaxis_transform(), ha="center", va="top",
                    fontsize=FS_SMALL, color=MUTED)
            continue
        mean = float(v.mean())
        psd = float(np.nanmean(sub["thetaSdFinal"]))
        ci = float(stats.t.ppf(0.975, len(v) - 1) * v.std(ddof=1) / math.sqrt(len(v))) if len(v) > 1 else np.nan
        col = FPS_COLOR[f]
        x_sd, x_ci = x - 0.12, x + 0.12
        ax.errorbar(x_sd, mean, yerr=[[min(psd, mean * 0.95)], [psd]], fmt="none", ecolor=col,
                    elinewidth=1.2, capsize=3.0, zorder=2)
        ax.plot(x_sd, mean, marker=FPS_MARKER[f], ms=FPS_MS[f] + 1.2, ls="", mfc=col, mec=INK, mew=0.7, zorder=3)
        ax.annotate(f"{mean:.1f} ± {psd:.1f}", (x_sd, mean), xytext=(-7, 0), textcoords="offset points",
                    ha="right", va="center", fontsize=FS_SMALL, fontweight="bold", color=col)
        if np.isfinite(ci):
            ax.errorbar(x_ci, mean, yerr=[[min(ci, mean * 0.95)], [ci]], fmt="none", ecolor=col,
                        elinewidth=1.2, capsize=3.0, zorder=2)
        ax.plot(x_ci, mean, marker="s", ms=FPS_MS[f] + 1.0, ls="", mfc=SURFACE, mec=col, mew=1.1, zorder=3)
        ax.annotate(f"{mean:.1f} ± {fmt_num(ci, 1)}" + (f" (n = {len(v)})" if len(v) < 3 else ""),
                    (x_ci, mean), xytext=(7, 0), textcoords="offset points", ha="left", va="center",
                    fontsize=FS_SMALL, color=col)
        lows.append(max(0.5, mean - max(psd, ci if np.isfinite(ci) else 0)))
        highs.append(mean + max(psd, ci if np.isfinite(ci) else 0))
    ax.set_xlim(0.4, len(FPS_LEVELS) + 0.6)
    ax.set_xticks(np.arange(1, len(FPS_LEVELS) + 1), labels)
    ax.tick_params(axis="x", length=0)
    ax.grid(False)
    if highs:
        lo, hi = max(0.5, min(lows) * 0.6), max(highs) * 1.6
        ax.set_ylim(lo, hi)
        log_axis(ax, "y", nice_log_ticks(lo, hi))
    ax.set_ylabel("JND threshold $\\theta$ (fps, log)")
    handles = [Line2D([], [], marker="o", ls="", ms=4.5, color=INK_2, mfc=INK_2, mec=INK, mew=0.6,
                      label="Mean ± average posterior SD"),
               Line2D([], [], marker="s", ls="", ms=4.3, color=INK_2, mfc=SURFACE, mec=INK_2, mew=1.0,
                      label="Mean ± 95% CI")]
    fig_legend_top(fig, handles, ncol=1)
    caption = (f"Across-participant mean JND threshold at the three base rates{WHICH_TEXT[which]} (log "
               "axis). The filled marker is the mean threshold across the included blocks and its bar "
               "±1 average posterior SD across them; the square is the same mean with a 95% t-based "
               "confidence interval across thresholds. Labels beside each bar give mean ± error in fps.")
    alt = ("Paired dot plots, one per base rate, of the mean threshold with average posterior-SD and 95% "
           "confidence-interval error bars on a log axis.")
    stem = {None: "Fig23_threshold_summary", "reliable": "Fig24_threshold_summary_reliable",
            "converged": "Fig25_threshold_summary_converged"}[which]
    return FigureDoc(stem, fig, caption, alt)


def fig_threshold_summary(study):
    return _threshold_summary(study)


def fig_threshold_summary_reliable(study):
    return _threshold_summary(study, "reliable")


def fig_threshold_summary_converged(study):
    return _threshold_summary(study, "converged")


def _tertiles(study):
    """Participants split into thirds by their mean Weber fraction over converged, reliable
    blocks (the threshold itself differs by base rate, its Weber fraction does not)."""
    b = _subset(study, "converged")
    means = b.groupby("pid")["weberPct"].mean().dropna().sort_values()
    n = len(means)
    if n < 3:
        return None, b
    g1, g2 = n // 3, 2 * n // 3
    pids = list(means.index)
    return [("Low threshold", pids[:g1]), ("Mid threshold", pids[g1:g2]), ("High threshold", pids[g2:])], b


def fig_threshold_summary_converged_tertiles(study):
    groups, b = _tertiles(study)
    if groups is None:
        return None
    fig, axes = plt.subplots(3, 1, figsize=(TEXT_W * 0.75, 2.2 * 3), sharex=True, sharey=True, layout="constrained")
    for ax, (name, pids) in zip(axes, groups):
        gb = b[b["pid"].isin(pids)]
        for x, f in enumerate(FPS_LEVELS, start=1):
            v = gb[gb["fps"] == f]["weberPct"].dropna().to_numpy(float)
            if not len(v):
                continue
            m, lo, hi = mean_ci(v)
            col = FPS_COLOR[f]
            if np.isfinite(lo):
                ax.errorbar(x, m, yerr=[[m - lo], [hi - m]], fmt="none", ecolor=col, elinewidth=1.2, capsize=3)
            ax.plot(x, m, marker=FPS_MARKER[f], ms=FPS_MS[f] + 1.2, ls="", mfc=col, mec=INK, mew=0.7)
            ax.annotate(f"{m:.0f}%", (x, m), xytext=(7, 0), textcoords="offset points", ha="left",
                        va="center", fontsize=FS_SMALL, color=col)
        ax.set_title(f"{name} (n = {len(pids)})", fontsize=FS_SMALL)
        ax.set_ylabel("Weber fraction (%)")
        ax.grid(False)
    axes[-1].set_xticks([1, 2, 3], [cond_label(f) for f in FPS_LEVELS])
    axes[-1].set_xlim(0.5, 3.5)
    axes[0].set_ylim(bottom=0)
    fig_legend_top(fig, [Line2D([], [], marker="o", ls="", ms=4.3, color=INK_2, mec=INK, label="Mean ± 95% CI")], ncol=1)
    return FigureDoc("Fig25b_threshold_summary_converged_tertiles", fig,
                     "Converged threshold summary (Weber fraction) split into participant thirds by their mean "
                     "Weber fraction.", "Three dot plots, one per third of participants, of mean Weber fraction by base rate.")


def fig_threshold_summary_converged_tertiles_distribution(study):
    groups, b = _tertiles(study)
    if groups is None:
        return None
    means = b.groupby("pid")["weberPct"].mean().dropna().sort_values()
    fig, ax = plt.subplots(figsize=(max(TEXT_W * 0.5, len(means) * 0.25), 2.8), layout="constrained")
    xs = np.arange(1, len(means) + 1)
    cols = {name: c for (name, _), c in zip(groups, ("#1c5cab", "#898781", "#eb6834"))}
    for x, (pid, v) in zip(xs, means.items()):
        name = next(n for n, ps in groups if pid in ps)
        ax.plot(x, v, marker="o", ms=4.5, ls="", color=cols[name])
        ax.annotate(f"{v:.0f}", (x, v), xytext=(0, 4), textcoords="offset points", ha="center",
                    va="bottom", rotation=90, fontsize=FS_SMALL, color=cols[name])
    for k in (len(groups[0][1]), len(groups[0][1]) + len(groups[1][1])):
        ax.axvline(k + 0.5, color=MUTED, ls="--", lw=1.0)
    ax.set_xticks(xs, [p.lstrip("P") for p in means.index])
    ax.set_xlabel("Participant (sorted by mean converged Weber fraction)")
    ax.set_ylabel("Mean Weber fraction (%)")
    ax.set_ylim(0, means.max() * 1.3)
    ax.grid(False)
    return FigureDoc("Fig25c_threshold_summary_converged_tertiles_distribution", fig,
                     "How the participants divide into thirds by mean Weber fraction.", "Sorted dot plot of participant means.")


def _parameter_summary(study, col, label, unit, stem, which=None, log=False, fmt="{:.1f} ± {}"):
    b = _subset(study, which)
    fig, ax = plt.subplots(figsize=(TEXT_W * 0.75, 2.45), layout="constrained")
    if log:
        ax.set_yscale("log")
    lows, highs = [], []
    for x, f in enumerate(FPS_LEVELS, start=1):
        v = b[b["fps"] == f][col].to_numpy(float)
        v = v[np.isfinite(v)]
        if not len(v):
            continue
        m = float(v.mean())
        ci = float(stats.t.ppf(0.975, len(v) - 1) * v.std(ddof=1) / math.sqrt(len(v))) if len(v) > 1 else np.nan
        c = FPS_COLOR[f]
        if np.isfinite(ci):
            ax.errorbar(x, m, yerr=[[min(ci, m * 0.95) if log else ci], [ci]], fmt="none", ecolor=c,
                        elinewidth=1.2, capsize=3.0, zorder=2)
        ax.plot(x, m, marker="s", ms=FPS_MS[f] + 1.0, ls="", mfc=SURFACE, mec=c, mew=1.1, zorder=3)
        ax.plot(np.full(len(v), x - 0.18), v, marker=FPS_MARKER[f], ms=FPS_MS[f] - 0.6, ls="", color=c,
                mec=SURFACE, mew=0.4, zorder=3)
        dig = 2 if "beta" in col or "lam" in col else 1
        ax.annotate(f"{m:.{dig}f} ± {fmt_num(ci, dig)}", (x, m), xytext=(7, 0), textcoords="offset points",
                    ha="left", va="center", fontsize=FS_SMALL, color=c)
        lows.append(min(v.min(), m - (ci if np.isfinite(ci) else 0)))
        highs.append(max(v.max(), m + (ci if np.isfinite(ci) else 0)))
    ax.set_xlim(0.4, len(FPS_LEVELS) + 0.6)
    ax.set_xticks(np.arange(1, len(FPS_LEVELS) + 1), [f"Base\n{cond_label(f)}" for f in FPS_LEVELS])
    ax.tick_params(axis="x", length=0)
    ax.grid(False)
    if highs:
        if log:
            lo, hi = max(0.05, min(lows) * 0.6), max(highs) * 1.6
            ax.set_ylim(lo, hi)
            log_axis(ax, "y", nice_log_ticks(lo, hi))
        else:
            ax.set_ylim(0, max(highs) * 1.18)
    ax.set_ylabel(f"{label} ({unit})" if unit else label)
    fig_legend_top(fig, [Line2D([], [], marker="s", ls="", ms=4.3, color=INK_2, mfc=SURFACE, mec=INK_2, mew=1.0,
                                label="Mean ± 95% CI"),
                         Line2D([], [], marker="o", ls="", ms=3.5, color=MUTED, mec=SURFACE, label="Block")], ncol=1)
    caption = (f"Across-participant mean {re.sub(r'[$]', '', label)} at the three base rates{WHICH_TEXT[which]}: "
               "squares are means with 95% t-intervals, small markers the blocks.")
    alt = f"Dot plots of {re.sub(r'[$]', '', label)} per base rate with means and confidence intervals."
    return FigureDoc(stem, fig, caption, alt)


def fig_sd_summary(study):
    return _parameter_summary(study, "thetaSdFinal", "Posterior SD of $\\theta$", "fps, log", "Fig32_sd_summary", log=True)


def fig_sd_summary_reliable(study):
    return _parameter_summary(study, "thetaSdFinal", "Posterior SD of $\\theta$", "fps, log", "Fig33_sd_summary_reliable",
                              "reliable", log=True)


def fig_sd_summary_converged(study):
    return _parameter_summary(study, "thetaSdFinal", "Posterior SD of $\\theta$", "fps, log", "Fig34_sd_summary_converged",
                              "converged", log=True)


def fig_slope_summary(study):
    return _parameter_summary(study, "fit_beta", "Slope $\\beta$", "", "Fig35_slope_summary")


def fig_slope_summary_reliable(study):
    return _parameter_summary(study, "fit_beta", "Slope $\\beta$", "", "Fig36_slope_summary_reliable", "reliable")


def fig_slope_summary_converged(study):
    return _parameter_summary(study, "fit_beta", "Slope $\\beta$", "", "Fig37_slope_summary_converged", "converged")


def fig_slope_summary_converged_tertiles(study):
    groups, b = _tertiles(study)
    if groups is None:
        return None
    fig, axes = plt.subplots(3, 1, figsize=(TEXT_W * 0.75, 2.2 * 3), sharex=True, sharey=True, layout="constrained")
    for ax, (name, pids) in zip(axes, groups):
        gb = b[b["pid"].isin(pids)]
        for x, f in enumerate(FPS_LEVELS, start=1):
            v = gb[gb["fps"] == f]["fit_beta"].dropna().to_numpy(float)
            if not len(v):
                continue
            m, lo, hi = mean_ci(v)
            if np.isfinite(lo):
                ax.errorbar(x, m, yerr=[[m - lo], [hi - m]], fmt="none", ecolor=FPS_COLOR[f], elinewidth=1.2, capsize=3)
            ax.plot(x, m, marker=FPS_MARKER[f], ms=FPS_MS[f] + 1.2, ls="", mfc=FPS_COLOR[f], mec=INK, mew=0.7)
        ax.set_title(f"{name} (n = {len(pids)})", fontsize=FS_SMALL)
        ax.set_ylabel("Slope $\\beta$")
        ax.grid(False)
    axes[-1].set_xticks([1, 2, 3], [cond_label(f) for f in FPS_LEVELS])
    axes[-1].set_xlim(0.5, 3.5)
    return FigureDoc("Fig37b_slope_summary_converged_tertiles", fig,
                     "Converged slope summary split into participant thirds by mean Weber fraction.",
                     "Three dot plots of mean slope by base rate, one per third of participants.")


def fig_score(study):
    """Points per round, per participant and base rate (the game's own scoring)."""
    r = study.responses[~study.responses["abandoned"].fillna(False)]
    pids = study.pids
    fig, axes = plt.subplots(1, 3, figsize=(max(TEXT_W * 0.8, len(pids) * 0.35 * 3), 2.8), sharey=True,
                             layout="constrained")
    xs = np.arange(1, len(pids) + 1)
    for ax, f, letter in zip(axes, FPS_LEVELS, "abc"):
        for x, pid in zip(xs, pids):
            v = r[(r["pid"] == pid) & (r["fps"] == f)]["points"].to_numpy(float)
            if not len(v):
                continue
            m, lo, hi = mean_ci(v)
            if np.isfinite(lo):
                ax.errorbar(x, m, yerr=[[m - lo], [hi - m]], fmt="none", ecolor=FPS_COLOR[f], elinewidth=1.0, capsize=2.0)
            ax.plot(x, m, marker=FPS_MARKER[f], ms=FPS_MS[f] + 0.6, ls="", mfc=FPS_COLOR[f], mec=INK, mew=0.6)
        ax.axhline(0, color=INK_2, lw=0.6)
        ax.set_xticks(xs, [p.lstrip("P") for p in pids])
        ax.set_xlim(0.4, len(pids) + 0.6)
        ax.set_xlabel("Participant")
        ax.grid(False)
        title(ax, letter, f"Base {cond_label(f)}")
    axes[0].set_ylabel("Points per round (mean ± 95% CI)")
    hp = study.blocks["cfg_hitPoints"].dropna() if "cfg_hitPoints" in study.blocks else pd.Series(dtype=float)
    caption = ("Mean points per main-phase round, per participant and base rate, from the game's running "
               "score (a hit scores the hit points, a miss or a round without a shot the miss points).")
    alt = "Dot plots of mean points per round per participant, one panel per base rate."
    return FigureDoc("Fig41_score", fig, caption, alt)


def fig_threshold_units(study):
    """The same thresholds in the four units a reader might want."""
    b = study.blocks
    specs = [("theta", "Difference $\\theta$ (fps, log)", "{:.1f}", "a", True),
             ("alteredAtTheta", "Altered rate at threshold (fps, log)", "{:.0f}", "b", True),
             ("frameTimeDiffMs", "Frame-time difference (ms)", "{:.1f}", "c", False),
             ("weberPct", "Weber fraction $\\theta$/base (%)", "{:.0f}%", "d", False)]
    fig, axes = plt.subplots(1, 4, figsize=(TEXT_W, 2.8), layout="constrained")
    u = study.used
    for ax, (key, lab, fmt, letter, logy) in zip(axes, specs):
        v = u[key].dropna()
        if logy:
            ax.set_yscale("log")
        setting_panel(ax, b, key, fmt=fmt, individual_ci=False)
        if len(v):
            if logy:
                lo, hi = max(0.5, v.min() * 0.6), v.max() * 1.6
                ax.set_ylim(lo, hi)
                log_axis(ax, "y", nice_log_ticks(lo, hi))
            else:
                ax.set_ylim(0, v.max() * 1.25)
        ax.set_ylabel(lab)
        title(ax, letter, lab.split(" (")[0].split(" $")[0], pad=13)
    handles = [Line2D([], [], color=INDIVIDUAL, lw=0.8, label="Participant"),
               Line2D([], [], marker="o", ls="-", ms=5.5, color=INK, mfc=MUTED, mec=INK, lw=0.9, label="Mean ± 95% CI"),
               Line2D([], [], marker="o", ls="", ms=4.2, mfc=SURFACE, mec=INK_2, mew=0.9, label="Unreliable block (hollow)")]
    fig_legend_top(fig, handles, ncol=3, columnspacing=0.9)
    caption = (
        "The thresholds in four units. (a) The frame-rate difference $\\theta$ QUEST+ varied (fps). "
        "(b) The altered side's rate at threshold, base − $\\theta$. (c) The frame-time difference at "
        "threshold: how much longer each altered-side frame lasted than a base-side frame, "
        "1000/(base − $\\theta$) − 1000/base (ms). (d) The difference as a share of the base rate "
        "(Weber fraction). Abandoned blocks are left out.")
    alt = ("Four dot plots of the same thresholds by base rate: the frame-rate difference, the altered rate "
           "at threshold, the frame-time difference in milliseconds, and the Weber fraction.")
    return FigureDoc("FigS2_threshold_units", fig, caption, alt)


SD_TARGET_RATIOS = [1.0, 1.25, 1.5, 2.0, 3.0, 4.0]


def sd_target_records(study, ratios=SD_TARGET_RATIOS):
    rows = []
    for (pid, f), tr in study.traces.items():
        fit = study.fit(pid, f)
        if fit is None or fit["abandoned"]:
            continue
        t = tr[tr["trial"] > 0]
        for k in ratios:
            target = k * float(fit["stopSD"])
            reached = t[t["sd"] <= target]
            rows.append({"pid": pid, "fps": f, "ratio": k, "target_sd": target,
                         "trials": int(reached["trial"].iloc[0]) if len(reached) else int(t["trial"].max()),
                         "reached": bool(len(reached))})
    return pd.DataFrame(rows)


def fig_sd_targets(study):
    rec = sd_target_records(study)
    fig, (ax, ax_r) = plt.subplots(2, 1, figsize=(TEXT_W * 0.62, 3.2), sharex=True, layout="constrained",
                                   gridspec_kw={"height_ratios": [3.2, 1]})
    if rec.empty:
        return FigureDoc("FigS3_sd_targets", fig, "No usable block.", "Empty."), rec
    for f in FPS_LEVELS:
        g = rec[rec["fps"] == f].groupby("ratio")
        if not len(g):
            continue
        m, allr = g["trials"].mean(), g["reached"].all()
        ax.plot(m.index, m.values, color=FPS_COLOR[f], ls=FPS_LS[f], lw=1.1, zorder=2)
        for x, y, ok in zip(m.index, m.values, allr.values):
            fps_mark(ax, x, y, f, ok=bool(ok), grow=-0.4)
    g = rec.groupby("ratio")
    m, rate = g["trials"].mean(), g["reached"].mean()
    ax.plot(m.index, m.values, color=INK, lw=2.0, zorder=4, marker="s", ms=4, mec=SURFACE, mew=0.5)
    for a in (ax, ax_r):
        a.axvline(1.0, color=MUTED, lw=0.7, ls=":", zorder=1)
    ax.set_ylabel("Trials to reach target\n(mean per block)")
    ax.set_ylim(bottom=0)
    ax_r.bar(rate.index, rate.values * 100, width=0.12, color=tint(INK_2, 0.55), zorder=2)
    for x, v in zip(rate.index, rate.values):
        ax_r.annotate(f"{v * 100:.0f}", (x, v * 100), xytext=(0, 1.5), textcoords="offset points",
                      ha="center", va="bottom", fontsize=FS_SMALL, color=INK_2)
    ax_r.set_ylim(0, 125)
    ax_r.set_yticks([0, 50, 100])
    ax_r.set_ylabel("Blocks reaching\nit (%)")
    ax_r.set_xticks(SD_TARGET_RATIOS, [f"{k:g}×" for k in SD_TARGET_RATIOS])
    ax_r.set_xlabel("Target posterior SD (multiple of the block's stop rule)")
    no_xgrid(ax_r)
    handles = condition_key() + [Line2D([], [], color=INK, lw=2.0, marker="s", ms=4, mec=SURFACE, label="All blocks"),
                                 Line2D([], [], marker="o", ls="", ms=4, mfc=SURFACE, mec=MUTED, mew=0.8,
                                        label="Some blocks never reached it")]
    fig_legend_top(fig, handles, ncol=2)
    caption = ("Trials needed for the posterior SD to first fall to each target - a multiple of the block's "
               "own stop rule, since the stop rules differ by base rate - averaged over blocks (blocks that "
               "never reached a target count at their final trial, so hollow markers underestimate); below, "
               "the share of blocks that reached each target.")
    alt = "Line chart of mean trials to reach posterior-SD targets, per base rate and overall, with a bar strip of the share reaching each."
    return FigureDoc("FigS3_sd_targets", fig, caption, alt), rec


def fig_sd_all_runs(study):
    pids = study.pids
    cols = min(4, len(pids))
    rows = math.ceil(len(pids) / cols)
    fig, axes = plt.subplots(rows, cols, figsize=(TEXT_W if cols > 2 else COL_W * cols, 0.6 + 1.9 * rows),
                             sharey=True, squeeze=False, layout="constrained")
    for k, pid in enumerate(pids):
        ax = axes.flat[k]
        ax.set_yscale("log")
        for f in FPS_LEVELS:
            tr = study.traces.get((pid, f))
            if tr is None:
                continue
            t = tr[tr["trial"] > 0]
            ax.plot(t["trial"], t["sd"], color=FPS_COLOR[f], ls=FPS_LS[f], lw=1.0, zorder=2)
            hit = t["isHit"] == 1
            ax.plot(t["trial"][hit], t["sd"][hit], marker=FPS_MARKER[f], ms=2.4, ls="", color=FPS_COLOR[f],
                    mec=SURFACE, mew=0.3, zorder=3)
            ax.plot(t["trial"][~hit], t["sd"][~hit], marker=FPS_MARKER[f], ms=2.8, ls="", mfc=SURFACE,
                    mec=FPS_COLOR[f], mew=0.6, zorder=3)
            fit = study.fit(pid, f)
            ax.axhline(float(fit["stopSD"]), color=FPS_COLOR[f], lw=0.6, ls=":", zorder=1)
        title(ax, None, pid)
        ax.set_xlabel("Trial")
        if k % cols == 0:
            ax.set_ylabel("Posterior SD (fps, log)")
    vals = np.concatenate([tr.loc[tr["trial"] > 0, "sd"].to_numpy(float) for tr in study.traces.values()])
    log_axis(axes.flat[0], "y", nice_log_ticks(np.nanmin(vals) * 0.8, np.nanmax(vals) * 1.2))
    for ax in axes.flat[len(pids):]:
        ax.set_visible(False)
    handles = [line_handle(f, lw=1.0) for f in FPS_LEVELS]
    handles += [Line2D([], [], marker="o", ls="", ms=3, mfc=SURFACE, mec=MUTED, mew=0.6, label="Miss"),
                Line2D([], [], color=MUTED, lw=0.6, ls=":", label="Stop rule")]
    fig_legend_top(fig, handles, ncol=5)
    caption = ("Posterior SD of every block, one panel per participant (filled = hit, hollow = miss trial); "
               "dotted lines are each block's stop rule, in its base rate's colour.")
    alt = "Small multiples of posterior SD over trials, one panel per participant, one line per base rate."
    return FigureDoc("FigS4_sd_all_runs", fig, caption, alt)


# ---------------------------------------------------------------- where things happened

# Dwell time: magma reversed, so more = darker on paper and in greyscale, a rarely visited cell
# fades toward the white page, and the near-black end is cut so marks on the densest cells stay
# visible.
DWELL_CMAP = mpl.colors.ListedColormap(plt.get_cmap("magma_r")(np.linspace(0.0, 0.9, 256)), name="magma_r_cut")
RESP_HIT = dict(marker="o", ls="", mfc=OUTCOME_COLOR["hit"], mec=INK, mew=0.4)
RESP_MISS = dict(marker="x", ls="", color=INK, mew=0.8)
CROSS_MARK = dict(marker="o", ls="", mfc="none", mec=SURFACE, mew=0.9)


def draw_dwell(ax, occ_seconds, norm, responses=None, crossings=None):
    """Where the UFO spent its live time, relative to the boundary tower (x = 0): share of time
    per 1 x 1 world-unit cell, log colour; shots where the UFO was at the trigger."""
    total = occ_seconds.sum()
    share = 100.0 * occ_seconds / total if total > 0 else occ_seconds
    mesh = ax.pcolormesh(OCC_XE, OCC_YE, np.ma.masked_less_equal(share, 0), cmap=DWELL_CMAP,
                         norm=norm, shading="flat", rasterized=True, zorder=1)
    ax.axvline(0, color=INK_2, lw=0.8, zorder=2)
    if crossings is not None and len(crossings):
        ax.plot(crossings["x"] - crossings["towerX"], crossings["y"], ms=2.6, zorder=4, **CROSS_MARK)
    if responses is not None and len(responses):
        h = responses["isHit"].to_numpy() == 1
        x = responses["relativeX"].to_numpy(float)
        y = responses["ufoY"].to_numpy(float)
        ax.plot(x[h], y[h], ms=3.6, zorder=5, **RESP_HIT)
        ax.plot(x[~h], y[~h], ms=3.2, zorder=5, **RESP_MISS)
    ax.set_xlim(*FIELD_X)
    ax.set_ylim(*FIELD_Y)
    ax.set_xticks([-30, -15, 0, 15, 30])
    no_xgrid(ax)
    return mesh


def dwell_norm(grids):
    vals = []
    for g in grids:
        t = g.sum()
        if t > 0:
            s = 100.0 * g / t
            vals.append(s[s > 0])
    v = np.concatenate(vals) if vals else np.array([1e-3, 1.0])
    return mpl.colors.LogNorm(vmin=max(float(np.percentile(v, 2)), 1e-3), vmax=float(v.max()))


def fig_dwell(study, pids=None, per_participant=False):
    parts = [p for p in study.participants if p.frames is not None and (pids is None or p.pid in pids)]
    if not parts:
        return None
    grids, secs, rounds = {}, {}, {}
    for f in FPS_LEVELS:
        grids[f] = sum((p.frames.occupancy.get(f, 0) for p in parts), np.zeros((len(OCC_YE) - 1, len(OCC_XE) - 1)))
        secs[f] = sum(p.frames.live_sec.get(f, 0.0) for p in parts)
        rounds[f] = sum(int(((p.shots["main"]) & (p.shots["fps"] == f)).sum()) for p in parts)
    norm = dwell_norm(grids.values())
    fig, axes = plt.subplots(1, 3, figsize=(TEXT_W, 2.4), layout="constrained", sharex=True, sharey=True)
    mesh = None
    for ax, f in zip(axes, FPS_LEVELS):
        resp = cross = None
        if per_participant:
            p = parts[0]
            resp = p.shots[p.shots["main"] & p.shots["shot"] & (p.shots["fps"] == f)]
            cross = p.frames.crossings[p.frames.crossings["fps"] == f]
        mesh = draw_dwell(ax, grids[f], norm, responses=resp, crossings=cross)
        ax.set_title(f"Base {cond_label(f)}", loc="left")
        ax.set_title(f"{secs[f] / 60:.1f} min · {rounds[f]} rounds", loc="right", fontsize=FS_SMALL,
                     fontweight="normal", color=INK_2)
    axes[1].set_xlabel("Position from the boundary (world units)")
    axes[0].set_ylabel("Height (world units)")
    cb = fig.colorbar(mesh, ax=list(axes), location="right", shrink=0.85, aspect=22, pad=0.02)
    cb.outline.set_visible(False)
    ticks = [t for t in (0.01, 0.03, 0.1, 0.3, 1, 3) if mesh.norm.vmin <= t <= mesh.norm.vmax]
    cb.set_ticks(ticks, labels=[f"{t:g}" for t in ticks])
    cb.ax.minorticks_off()
    cb.set_label("Share of live time per 1 × 1 cell (%)")
    handles = [Line2D([], [], color=INK_2, lw=0.8, label="Boundary (tower)")]
    if per_participant:
        handles += [Line2D([], [], ms=4.2, label="Shot: hit", **RESP_HIT),
                    Line2D([], [], ms=4.0, label="Shot: miss", **RESP_MISS),
                    Line2D([], [], marker="o", ls="", ms=3.4, mfc="none", mec=INK_2, mew=0.9, label="Crossing")]
    fig_legend_top(fig, handles, ncol=len(handles))
    if per_participant:
        p = parts[0]
        stamp(fig, f"{p.pid} ({p.folder}) — where the UFO was during live play")
        return fig
    caption = (
        f"Where the UFO was during live play, pooled over {len(parts)} participants: share of live time "
        "spent in each 1 × 1 world-unit cell, by position relative to the round's boundary tower (x = 0) "
        "and height (log colour; darker = more time), sampled on a uniform 100 Hz clock so 60 and 500 fps "
        "blocks weigh the same per second. Live play runs from each round's start to its shot; the reveal, "
        "the veil and the camera pan are left out. Positions are relative to the tower because the camera "
        "pans between rounds.")
    alt = ("Three heat maps of position relative to the boundary by height, one per base rate, showing "
           "where the participant searched for the boundary.")
    return FigureDoc("FigS5_heatmaps", fig, caption, alt)


def fig_heatmaps(study):
    return fig_dwell(study)


def fig_p_heatmap(study, pid):
    return fig_dwell(study, pids=[pid], per_participant=True)


TARGET_ROWS = [(0, 15, "≤ 15"), (15, 30, "15–30"), (30, 45, "30–45"), (45, 60, "45–60"),
               (60, 75, "60–75"), (75, 101, "≥ 75")]


def fig_targeting(study):
    """Where shots landed relative to the boundary, signed toward the slower side, for each
    difference (rows, as % of the base rate), per base rate."""
    d = shots_of(study)
    if d.empty:
        return None
    radius = radius_of(study)
    fig = plt.figure(figsize=(TEXT_W, 2.8), layout="constrained")
    gs = fig.add_gridspec(1, 8, width_ratios=[2.2, 0.7, 0.22, 2.2, 0.7, 0.22, 2.2, 0.7], wspace=0.02)
    norm = mpl.colors.Normalize(0, 40)
    xe = np.arange(-24.0, 24.0 + 1e-9, 2.0)
    mesh = None
    for c, f in enumerate(FPS_LEVELS):
        ax = fig.add_subplot(gs[0, 3 * c])
        ax_r = fig.add_subplot(gs[0, 3 * c + 1], sharey=ax)
        g = d[d["fps"] == f]
        grid = np.zeros((len(TARGET_ROWS), len(xe) - 1))
        for r, (lo, hi, _) in enumerate(TARGET_ROWS):
            q = g[(g["w"] > lo) & (g["w"] <= hi)]
            if len(q):
                x = np.clip(q["towardAlteredX"].to_numpy(float), xe[0] + 1e-6, xe[-1] - 1e-6)
                grid[r] = 100.0 * np.histogram(x, bins=xe)[0] / len(q)
                k, n = int(q["isHit"].sum()), len(q)
                lo_, hi_ = wilson(k, n)
                ax_r.plot([lo_, hi_], [r + 0.5] * 2, color=INK_2, lw=0.9, solid_capstyle="butt")
                ax_r.plot(k / n, r + 0.5, marker="o", ms=3.8, mfc=OUTCOME_COLOR["hit"], mec=INK, mew=0.4, ls="")
                ax_r.annotate(f"{n}", xy=(1.0, r + 0.5), xycoords=("axes fraction", "data"), xytext=(3, 0),
                              textcoords="offset points", ha="left", va="center", fontsize=FS_SMALL,
                              color=INK_2, annotation_clip=False)
        mesh = ax.pcolormesh(xe, np.arange(len(TARGET_ROWS) + 1), np.ma.masked_less_equal(grid, 0),
                             cmap=DWELL_CMAP, norm=norm, rasterized=True, zorder=1)
        ax.axvspan(-radius, radius, color=HIT_ZONE, lw=0, zorder=0)
        for xv in (-radius, radius):
            ax.axvline(xv, color=INK, lw=0.9, zorder=3)
        ax.axvline(0, color=INK_2, lw=0.5, ls=(0, (1.5, 1.5)), zorder=3)
        ax.set_xlim(xe[0], xe[-1])
        ax.set_ylim(len(TARGET_ROWS), 0)
        ax.set_yticks(np.arange(len(TARGET_ROWS)) + 0.5, [lab for _, _, lab in TARGET_ROWS])
        ax.tick_params(axis="y", length=0)
        ax.set_xticks([-20, -10, 0, 10, 20])
        no_xgrid(ax)
        ax.set_title(f"Base {cond_label(f)}", loc="left")
        ax_r.set_xlim(0, 1)
        ax_r.set_xticks([0, 0.5, 1], ["0", ".5", "1"])
        ax_r.tick_params(axis="y", left=False, labelleft=False)
        ax_r.spines["left"].set_visible(False)
        ax_r.set_xlabel("Hit rate")
        if c == 0:
            ax.set_ylabel("Difference (% of base)")
        else:
            ax.tick_params(labelleft=False)
    cb = fig.colorbar(mesh, ax=fig.axes, location="right", shrink=0.85, aspect=22, pad=0.02)
    cb.outline.set_visible(False)
    cb.set_label("Share of the row's shots (%)")
    # One label for the three maps, below every panel's own (the hit-rate panels keep theirs).
    fig.supxlabel("Heat maps: shot from the boundary, + = into the slower side (world units)",
                  fontsize=FS_LABEL)
    fig_legend_top(fig, [Patch(facecolor=HIT_ZONE, edgecolor=INK_2, lw=0.6, label=f"Hit zone (tower ± {radius:g})"),
                         Line2D([], [], marker="o", ls="", ms=4, mfc=OUTCOME_COLOR["hit"], mec=INK, mew=0.4,
                                label="Hit rate ± 95% CI (n = shots)")], ncol=2)
    caption = (
        "Where shots landed relative to the boundary tower, for each frame-rate difference (rows, as a "
        "percentage of the base rate), pooled over participants, one heat map per base rate. The horizontal "
        "axis is signed toward the round's altered side, so positive = the shot landed on the slower side; "
        f"colour gives the share of the row's shots in each 2-unit bin. Shots within ±{radius:g} of the "
        "tower (shaded) are hits. The narrow panel beside each map gives the row's hit rate with a 95% "
        "Wilson interval and the number of shots.")
    alt = ("Three heat maps, one per base rate, of shot landing position relative to the boundary by "
           "frame-rate difference, with hit rates per difference beside each.")
    return FigureDoc("FigS7_targeting", fig, caption, alt)


# ---------------------------------------------------------------- behaviour small multiples

BEHAVIOUR = [
    ("pCorrect", "Proportion hit, staircase trials"),
    ("liveRoundSec", "Time in a round, start to shot (s)"),
    ("crossingsBeforeShot", "Boundary crossings before the shot"),
    ("sinceLastCrossingSec", "Last crossing to shot (s)"),
    ("timeoutShare", "Rounds without a shot (share)"),
    ("staircaseTrials", "Staircase trials to stop"),
    ("playDurationMin", "Block duration (min)"),
    ("thetaSdFinal", "Final posterior SD (fps)"),
    ("sdRatio", "Final SD / stop rule"),
    ("fit_beta", "Slope $\\hat\\beta$"),
    ("fit_lam", "Lapse $\\hat\\lambda$"),
    ("missDistMedian", "Median shot–tower distance"),
    ("towardAlteredMedian", "Median shot offset toward the slower side"),
    ("pointsPerRound", "Points per round"),
    # Input System Mouse.delta counts - raw device units, not screen pixels.
    ("avgMouseSpeedPxPerSec", "Mean mouse speed (counts/s)"),
    ("ufoSpeedWorldPerSec", "UFO speed (world units/s)"),
    ("achievedBaseRatio", "Achieved / target base rate"),
    ("baseFrameSdMs", "Base-side frame-time SD (ms)"),
    ("alteredFrameSdMs", "Altered-side frame-time SD (ms)"),
    ("practiceAccuracy", "Practice proportion hit"),
]


def metric_panel(ax, b, key, label):
    b = b[b["included"]]
    for pid in sorted(b["pid"].unique()):
        pts = []
        for f in FPS_LEVELS:
            s = b[(b["pid"] == pid) & (b["fps"] == f)][key]
            if len(s) and np.isfinite(s.iloc[0]):
                pts.append((COND_X[f], float(s.iloc[0])))
        if len(pts) >= 2:
            ax.plot([p[0] for p in pts], [p[1] for p in pts], color=INDIVIDUAL, lw=0.7, zorder=1)
    for f in FPS_LEVELS:
        v = b[b["fps"] == f][key].to_numpy(float)
        v = v[np.isfinite(v)]
        if not len(v):
            continue
        ax.plot(np.full(len(v), COND_X[f]), v, marker=FPS_MARKER[f], ms=FPS_MS[f] - 0.5, ls="",
                color=FPS_COLOR[f], mec=SURFACE, mew=0.4, zorder=3)
        m, lo, hi = mean_ci(v)
        if np.isfinite(lo):
            ax.errorbar(COND_X[f] + 0.3, m, yerr=[[m - lo], [hi - m]], fmt="none", ecolor=INK,
                        elinewidth=0.8, capsize=1.8, zorder=4)
        ax.plot(COND_X[f] + 0.3, m, marker=FPS_MARKER[f], ms=FPS_MS[f] + 0.8, ls="", color=FPS_COLOR[f],
                mec=INK, mew=0.6, zorder=5)
    cond_axis(ax, label=False)
    ax.set_title(label, loc="left", fontsize=FS_BASE, fontweight="normal")
    ax.margins(y=0.12)
    vals = np.abs(b[key].to_numpy(float))
    if np.isfinite(vals).any() and np.nanmax(vals) >= 10000:
        ax.yaxis.set_major_formatter(mpl.ticker.FuncFormatter(lambda v, _: "0" if v == 0 else f"{v / 1000:g}k"))


def fig_behaviour(study, metrics=BEHAVIOUR, stem="FigS6_behaviour_all", ncols=5):
    b = study.blocks
    metrics = [m for m in metrics if m[0] in b and b.loc[b["included"], m[0]].notna().any()]
    n = len(metrics)
    rows = math.ceil(n / ncols)
    fig, axes = plt.subplots(rows, ncols, figsize=(TEXT_W, 0.6 + 1.6 * rows), squeeze=False, layout="constrained")
    width = int(TEXT_W / ncols * 72 / (FS_BASE * 0.58))
    for i, (key, label) in enumerate(metrics):
        text = f"({'abcdefghijklmnopqrstuvwxyz'[i]})  {label}"
        metric_panel(axes.flat[i], b, key, textwrap.fill(text, width=width, subsequent_indent="      "))
    for ax in axes.flat[n:]:
        ax.set_visible(False)
    handles = [Line2D([], [], color=INDIVIDUAL, lw=0.7, label="Participant"),
               Line2D([], [], marker="o", ls="", ms=5, color=MUTED, mec=INK, mew=0.6, label="Mean ± 95% CI")]
    fig_legend_top(fig, handles, ncol=2, columnspacing=1.0)
    caption = ("Secondary behavioural, procedural and frame-delivery measures per block, by base rate. Small "
               "markers are participants (grey lines join a participant's blocks); large markers the mean with "
               "95% CI. Totals are shown as rates because QUEST+ ends blocks at different lengths; abandoned "
               "blocks are left out.")
    plain = [re.sub(r"\$[^$]*\$", "", m[1]).replace("  ", " ").strip() for m in metrics]
    alt = f"Grid of {n} small dot plots, one per measure ({'; '.join(plain)}), each comparing the three base rates."
    return FigureDoc(stem, fig, caption, alt)


# ================================================================ per-participant figures

SWEEP = "#d3d2cb"                 # where the UFO roamed in a round: present, never loud


def trial_ramp(n):
    """Early trials light grey, late trials near-black: lightness carries order, so it survives
    greyscale printing and colour-vision deficiency, and the converged estimate stands out."""
    return [tint(INK_2, 0.82 - 0.62 * (i / max(1, n - 1))) for i in range(n)]


def draw_family(ax, tr, fit, f, x_max, show_title=True):
    xs = np.linspace(0.2, x_max, 300)
    t = tr[tr["trial"] > 0]
    gamma = float(fit["gamma"])
    cols = trial_ramp(len(t))
    for i, (_, r) in enumerate(t.iterrows()):
        if i == len(t) - 1:
            break
        ax.plot(xs, weibull(xs, r["theta"], r["beta"], r["lam"], gamma), color=cols[i], lw=0.6, zorder=2)
    last = t.iloc[-1]
    ax.plot(xs, weibull(xs, last["theta"], last["beta"], last["lam"], gamma), color=FPS_COLOR[f],
            lw=1.7, ls=FPS_LS[f], zorder=3)
    fy = float(weibull(last["theta"], last["theta"], last["beta"], last["lam"], gamma))
    ax.plot(last["theta"], fy, marker=FPS_MARKER[f], ms=FPS_MS[f] + 0.6, color=FPS_COLOR[f], mec=SURFACE,
            mew=0.6, zorder=4)
    flag = ""
    if fit["abandoned"]:
        flag = "  abandoned"
    elif fit["rangeEdge"]:
        flag = "  ≥ range top" if fit["rangeEdge"] == "top" else "  ≤ range bottom"
    elif not fit["reliable"]:
        flag = "  ‡ unreliable"
    ax.text(0.0, 1.0, f"$\\hat\\theta$ = {fit['theta']:.1f} ± {fit['thetaSd']:.1f} fps{flag}",
            transform=ax.transAxes, ha="left", va="bottom", fontsize=FS_SMALL, color=INK_2)
    ax.axhline(gamma, color=MUTED, lw=0.7, ls=":", zorder=1)
    ax.annotate(f"$\\gamma$ = {gamma:.3f}", xy=(1, gamma), xycoords=("axes fraction", "data"), xytext=(-2, -2),
                textcoords="offset points", ha="right", va="top", fontsize=FS_SMALL, color=INK_2)
    if show_title:
        ax.set_title(f"Base {cond_label(f)}\nblock {int(fit['blockOrdinal'])} · {len(t)} trials", loc="left",
                     fontsize=FS_BASE, pad=12, linespacing=1.2)
    ax.set_xlim(0, x_max)
    ax.set_ylim(0, 1)
    ax.set_xlabel("Frame-rate difference (fps)")


def block_xmax(study, pid, f):
    tr = study.traces.get((pid, f))
    fit = study.fit(pid, f)
    hi = float(fit["gridHi"]) if fit is not None else f
    if tr is None:
        return hi
    t = tr[tr["trial"] > 0]
    return float(min(hi * 1.02, max(t["diffFps"].max(), fit["theta"]) * 1.25))


def fig_p_curves(study, pid):
    p = study.by_pid(pid)
    blocks = p.blocks.sort_values("blockOrdinal")
    fig, axes = plt.subplots(1, 3, figsize=(TEXT_W, 2.8), sharey=True, squeeze=False, layout="constrained")
    axes = axes.flat
    for ax, (_, b) in zip(axes, blocks.iterrows()):
        f = int(b["fps"])
        tr = study.traces.get((pid, f))
        if tr is None:
            ax.set_visible(False)
            continue
        draw_family(ax, tr, study.fit(pid, f), f, block_xmax(study, pid, f))
    axes[0].set_ylabel("P(hit)")
    for ax in list(axes)[len(blocks):]:
        ax.set_visible(False)
    handles = [Line2D([], [], color=tint(INK_2, 0.6), lw=0.6, label="Estimate after each trial (light = early)"),
               Line2D([], [], color=INK_2, lw=1.7, label="Final estimate (colour = base rate)"),
               Line2D([], [], color=MUTED, lw=0.7, ls=":", label="Guess rate $\\gamma$")]
    fig_legend_top(fig, handles, ncol=3)
    stamp(fig, f"{pid} ({p.folder}) — psychometric functions in play order")
    return fig


def session_sequence(study, pid):
    """The participant's blocks in play order, with a continuous trial axis."""
    p = study.by_pid(pid)
    seq, offset = [], 0
    for _, b in p.blocks.sort_values("blockOrdinal").iterrows():
        f = int(b["fps"])
        tr = study.traces.get((pid, f))
        if tr is None:
            continue
        t = tr[tr["trial"] > 0].copy()
        t["x"] = t["trial"] + offset
        seq.append((f, int(b["blockOrdinal"]), t, offset, study.fit(pid, f)))
        offset += len(t)
    return seq


def draw_trace(ax, seq, col, band=False, restart=False, markers=True, scale=None):
    for k, (f, ordinal, t, offset, fit) in enumerate(seq):
        x = t["trial"] if restart else t["x"]
        div = float(fit[scale]) if scale else 1.0
        y = t[col] / div
        if band:
            ax.fill_between(x, np.maximum(0, (t["theta"] - t["sd"]) / div), (t["theta"] + t["sd"]) / div,
                            color=tint(FPS_COLOR[f], 0.82), lw=0, zorder=1)
        ax.plot(x, y, color=FPS_COLOR[f], ls=FPS_LS[f], lw=1.1, zorder=3)
        if markers:
            hit = t["isHit"] == 1
            ax.plot(x[hit], y[hit], marker=FPS_MARKER[f], ms=2.6, ls="", color=FPS_COLOR[f], mec=SURFACE,
                    mew=0.3, zorder=4)
            ax.plot(x[~hit], y[~hit], marker=FPS_MARKER[f], ms=3.0, ls="", mfc=SURFACE, mec=FPS_COLOR[f],
                    mew=0.7, zorder=4)
        if not restart and k > 0:
            ax.axvline(offset + 0.5, color=MUTED, lw=0.6, zorder=0)


def outcome_handles(seq, extra=None):
    handles = [Line2D([], [], color=FPS_COLOR[f], ls=FPS_LS[f], lw=1.1, marker=FPS_MARKER[f], ms=3.4,
                      mec=SURFACE, label=f"{cond_label(f)} (block {o})") for f, o, _, _, _ in seq]
    handles += [Line2D([], [], marker="o", ls="", ms=3.4, color=INK_2, mec=SURFACE, label="Hit"),
                Line2D([], [], marker="o", ls="", ms=3.6, mfc=SURFACE, mec=INK_2, mew=0.7, label="Miss")]
    return handles + (extra or [])


def fig_p_convergence(study, pid):
    """The threshold estimate by trial, as a Weber fraction so blocks at different base rates
    share one axis."""
    seq = session_sequence(study, pid)
    fig, ax = plt.subplots(figsize=(TEXT_W, 2.9), layout="constrained")
    draw_trace(ax, [(f, o, t.assign(theta=100 * t["theta"] / f, sd=100 * t["sd"] / f), off, fit)
                    for f, o, t, off, fit in seq], "theta", band=True)
    ax.set_xlabel("Trial (continuous across blocks, in play order)")
    ax.set_ylabel("Threshold estimate (% of the base rate)")
    ax.set_ylim(0, 105)
    ax.set_xlim(0, max(t["x"].max() for _, _, t, _, _ in seq) + 1)
    fig_legend_top(fig, outcome_handles(seq, [Patch(facecolor=tint(INK_2, 0.82), label="±1 posterior SD")]),
                   ncol=4, fontsize=FS_SMALL, columnspacing=1.0)
    stamp(fig, f"{pid} ({study.by_pid(pid).folder}) — threshold estimate by trial (Weber fraction)")
    return fig


def sd_crossings(seq, ratios=(3, 2, 1.5, 1.25, 1)):
    rows = []
    for f, o, t, _, fit in seq:
        cells = []
        for k in ratios:
            hit = t[t["sd"] <= k * float(fit["stopSD"])]
            cells.append(str(int(hit["trial"].iloc[0])) if len(hit) else "–")
        rows.append((f"{cond_label(f)} (block {o})", cells))
    return rows


def fig_p_sd(study, pid, restart=False):
    seq = session_sequence(study, pid)
    ratios = (3, 2, 1.5, 1.25, 1)
    fig, (ax, ax_tab) = plt.subplots(2, 1, figsize=(TEXT_W, 4.1), layout="constrained",
                                     gridspec_kw={"height_ratios": [2.6, 1.0]})
    ax.set_yscale("log")
    draw_trace(ax, seq, "sd", restart=restart, scale="stopSD")
    ax.axhline(1.0, color=INK_2, lw=0.8, zorder=0)
    for f, o, t, _, fit in seq:
        x = t["trial"] if restart else t["x"]
        for k in ratios:
            hit = t[t["sd"] <= k * float(fit["stopSD"])]
            if len(hit):
                i = hit.index[0]
                ax.plot(x.loc[i], hit["sd"].iloc[0] / float(fit["stopSD"]), marker="*", ms=5.5,
                        color=FPS_COLOR[f], mec=INK, mew=0.3, zorder=5)
    vals = np.concatenate([(t["sd"] / float(fit["stopSD"])).to_numpy() for _, _, t, _, fit in seq])
    log_axis(ax, "y", nice_log_ticks(max(0.3, np.nanmin(vals) * 0.8), np.nanmax(vals) * 1.2))
    ax.set_ylabel("Posterior SD / stop rule (log)")
    ax.set_xlabel("Trial, restarted per block" if restart else "Trial (continuous across blocks, in play order)")
    ax.set_xlim(0, (max(len(t) for _, _, t, _, _ in seq) if restart else max(t["x"].max() for _, _, t, _, _ in seq)) + 1)
    fig_legend_top(fig, outcome_handles(seq, [Line2D([], [], marker="*", ls="", ms=5.5, color=INK_2, mec=INK, mew=0.3,
                                                     label="First trial at SD ≤ 3, 2, 1.5, 1.25, 1× stop rule")]),
                   ncol=4, fontsize=FS_SMALL, columnspacing=1.0)
    rows = sd_crossings(seq, ratios)
    ax_tab.set_axis_off()
    tab = ax_tab.table(cellText=[c for _, c in rows], rowLabels=[r for r, _ in rows],
                       colLabels=[f"SD ≤ {k:g}× stop" for k in ratios], loc="center",
                       bbox=[0.3, 0.0, 0.7, 1.0], cellLoc="center")
    tab.auto_set_font_size(False)
    tab.set_fontsize(FS_SMALL)
    for (r, c), cell in tab.get_celld().items():
        cell.set_edgecolor(GRID)
        cell.set_linewidth(0.5)
        cell.get_text().set_color(INK if r > 0 else INK_2)
    stamp(fig, f"{pid} ({study.by_pid(pid).folder}) — posterior SD by trial, relative to each block's stop rule"
               + (" (blocks overlaid)" if restart else "") + "; table: first trial reaching each target")
    return fig


def fig_p_parameters(study, pid, axes=None):
    seq = session_sequence(study, pid)
    p = study.by_pid(pid)
    cfg = p.shots[p.shots["counted"]].iloc[0]
    own = axes is None
    if own:
        fig, axes = plt.subplots(1, 2, figsize=(TEXT_W, 2.9), layout="constrained")

    def prior_mean(lo, hi, n, fallback):
        try:
            return float(linspace32(cfg[lo], cfg[hi], cfg[n]).astype(float).mean())
        except (KeyError, ValueError, TypeError):
            return fallback

    for ax, col, lab, pm in ((axes[0], "beta", "Slope $\\beta$", prior_mean("cfg_slopeMin", "cfg_slopeMax", "cfg_slopeCount", 4.0)),
                             (axes[1], "lam", "Lapse $\\lambda$", prior_mean("cfg_lapseMin", "cfg_lapseMax", "cfg_lapseCount", 0.03))):
        draw_trace(ax, seq, col)
        ax.axhline(pm, color=INK_2, lw=0.8, ls=(0, (3, 2)), zorder=1)
        ax.set_ylabel(lab)
        ax.set_xlabel("Trial (continuous)")
        ax.margins(y=0.15)
    if own:
        fig_legend_top(fig, outcome_handles(seq, [Line2D([], [], color=INK_2, lw=0.8, ls=(0, (3, 2)), label="Prior mean")]),
                       ncol=4, fontsize=FS_SMALL, columnspacing=1.0)
        stamp(fig, f"{pid} ({p.folder}) — slope and lapse against their prior means")
        return fig


def fig_p_composite(study, pid):
    p = study.by_pid(pid)
    fig = plt.figure(figsize=(TEXT_W, 8.2), layout="constrained")
    gs = fig.add_gridspec(3, 3, height_ratios=[1.0, 1.0, 0.95])
    for j, f in enumerate(FPS_LEVELS):
        ax = fig.add_subplot(gs[0, j])
        tr = study.traces.get((pid, f))
        if tr is None:
            ax.set_visible(False)
            continue
        draw_family(ax, tr, study.fit(pid, f), f, block_xmax(study, pid, f))
        if j == 0:
            ax.set_ylabel("P(hit)")
        else:
            ax.tick_params(labelleft=False)
    seq = session_sequence(study, pid)
    ax = fig.add_subplot(gs[1, :])
    draw_trace(ax, [(f, o, t.assign(theta=100 * t["theta"] / f, sd=100 * t["sd"] / f), off, fit)
                    for f, o, t, off, fit in seq], "theta", band=True)
    ax.set_ylabel("Threshold estimate (% of base)")
    ax.set_xlabel("Trial (continuous across blocks, in play order)")
    ax.set_ylim(0, 105)
    gs2 = gs[2, :].subgridspec(1, 2)
    fig_p_parameters(study, pid, axes=[fig.add_subplot(gs2[0]), fig.add_subplot(gs2[1])])
    fig_legend_top(fig, outcome_handles(seq, [Patch(facecolor=tint(INK_2, 0.82), label="±1 posterior SD"),
                                              Line2D([], [], color=INK_2, lw=0.8, ls=(0, (3, 2)), label="Prior mean")]),
                   ncol=4, fontsize=FS_SMALL, columnspacing=1.0)
    stamp(fig, f"{pid} ({p.folder}) — QUEST+ convergence")
    return fig


def round_trials(study, pid, f):
    """One dict per staircase trial of a participant's block at base rate f, with the live
    path and crossings of its round (PlayerLog) and the shot (ShotLog)."""
    p = study.by_pid(pid)
    if p.frames is None:
        return []
    s = p.shots[p.shots["counted"] & (p.shots["fps"] == f)].sort_values("trial")
    if s.empty:
        return []
    blk = int(s["blockIndex"].iloc[0])
    paths = {r: g for r, g in p.frames.paths[p.frames.paths["blockIndex"] == blk].groupby("roundNumber")}
    cr = p.frames.crossings[p.frames.crossings["blockIndex"] == blk]
    crosses = {r: g for r, g in cr.groupby("roundNumber")}
    tr = study.traces.get((pid, f))
    th = tr[tr["trial"] > 0].set_index("trial")[["theta", "sd"]] if tr is not None else None
    out = []
    for r in s.itertuples():
        path = paths.get(r.roundNumber)
        q = crosses.get(r.roundNumber, cr.iloc[:0])
        out.append({
            "trial": int(r.trial), "stim": float(r.diffFps), "isHit": int(r.isHit), "shot": bool(r.shot),
            "tower": float(r.towerX), "hitX": float(r.hitX) if r.shot else np.nan,
            "radius": float(r.closeRadius) if np.isfinite(r.closeRadius) else CLOSE_RADIUS,
            "xmin": float(path["x"].min()) if path is not None and len(path) else np.nan,
            "xmax": float(path["x"].max()) if path is not None and len(path) else np.nan,
            "cx": q["x"].to_numpy(float), "cv": q["vel"].to_numpy(float),
            "flip": 1.0 if str(r.alteredSide).strip().lower() == "right" else -1.0,
            "theta": float(th.loc[r.trial, "theta"]) if th is not None and r.trial in th.index else np.nan,
            "sd": float(th.loc[r.trial, "sd"]) if th is not None and r.trial in th.index else np.nan,
            "result": r.result,
        })
    return out


def draw_round_map(ax, trials, f, relative=False, row_h=0.72):
    """One row per trial. relative=False: world positions - the tower (dark tick) with its hit
    zone, the stretch the UFO covered in the live round (grey), each crossing (triangle pointing
    the way the UFO was moving) and the shot (filled = hit, hollow = miss) joined to the tower.
    relative=True: re-centred on the tower and mirrored so the slower side is always to the
    right - which side the shot erred toward reads directly."""
    col = FPS_COLOR[f]
    zones, sweeps, ticks, links = [], [], [], []
    marks = {"hit": ([], []), "miss": ([], []), ">": ([], []), "<": ([], []), "none": ([], [])}
    for r in trials:
        y = r["trial"]
        y0, y1 = y - row_h / 2, y + row_h / 2
        ref, sgn = (r["tower"], r["flip"]) if relative else (0.0, 1.0)
        tx = sgn * (r["tower"] - ref)
        R = r["radius"]
        zones.append([(tx - R, y0), (tx + R, y0), (tx + R, y1), (tx - R, y1)])
        ticks.append([(tx, y0), (tx, y1)])
        if np.isfinite(r["xmin"]):
            a, b_ = sgn * (r["xmin"] - ref), sgn * (r["xmax"] - ref)
            sweeps.append([(min(a, b_), y), (max(a, b_), y)])
        for x_, v_ in zip(r["cx"], r["cv"]):
            m = marks[">" if sgn * v_ >= 0 else "<"]
            m[0].append(sgn * (x_ - ref))
            m[1].append(y)
        if r["shot"]:
            hx = sgn * (r["hitX"] - ref)
            links.append([(tx, y), (hx, y)])
            key = "hit" if r["isHit"] else "miss"
            marks[key][0].append(hx)
            marks[key][1].append(y)
        else:
            marks["none"][0].append(tx)
            marks["none"][1].append(y)
    ax.add_collection(mpl.collections.PolyCollection(zones, facecolor=HIT_ZONE, edgecolor="none", zorder=1))
    ax.add_collection(mpl.collections.LineCollection(sweeps, colors=SWEEP, linewidths=1.6, zorder=1.5, capstyle="round"))
    ax.add_collection(mpl.collections.LineCollection(ticks, colors=INK_2, linewidths=0.8, zorder=2))
    ax.add_collection(mpl.collections.LineCollection(links, colors=col, linewidths=0.9, zorder=3, capstyle="butt"))
    for k in (">", "<"):
        ax.plot(*marks[k], ls="", marker=k, ms=3.0, mfc=INK, mec=SURFACE, mew=0.25, zorder=4)
    ax.plot(*marks["hit"], ls="", marker="o", ms=3.6, mfc=col, mec=SURFACE, mew=0.45, zorder=5)
    ax.plot(*marks["miss"], ls="", marker="o", ms=3.3, mfc=SURFACE, mec=col, mew=0.95, zorder=5)
    ax.plot(*marks["none"], ls="", marker="x", ms=3.6, color=INK_2, mew=0.9, zorder=5)
    xs = [x for seg in sweeps for x, _ in seg] + [x for z in zones for x, _ in z]
    if relative:
        ax.set_xlim(-32.0, 32.0)
    elif xs:
        ax.set_xlim(min(xs) - 2, max(xs) + 2)
    no_xgrid(ax)


def draw_round_strip(ax, trials, f, x_max):
    col = FPS_COLOR[f]
    tr = np.array([r["trial"] for r in trials], float)
    st = np.array([r["stim"] for r in trials], float)
    th = np.array([r["theta"] for r in trials], float)
    sd = np.array([r["sd"] for r in trials], float)
    hit = np.array([r["isHit"] == 1 for r in trials])
    ax.fill_betweenx(tr, np.maximum(0, th - sd), th + sd, color=tint(col, 0.82), lw=0, zorder=1)
    ax.plot(th, tr, color=col, lw=1.0, zorder=2)
    ax.plot(st[hit], tr[hit], ls="", marker=FPS_MARKER[f], ms=2.8, mfc=col, mec=SURFACE, mew=0.35, zorder=3)
    ax.plot(st[~hit], tr[~hit], ls="", marker=FPS_MARKER[f], ms=2.6, mfc=SURFACE, mec=col, mew=0.8, zorder=3)
    ax.set_xlim(0, x_max)
    ax.xaxis.set_major_locator(MaxNLocator(2, integer=True))
    ax.tick_params(axis="y", left=False, labelleft=False)
    ax.spines["left"].set_visible(False)


def fig_p_roundmap(study, pid, relative=False):
    """Every block of one participant: per trial, where the UFO was, where the tower was, each
    crossing and where the shot landed, beside the staircase that chose the difference."""
    p = study.by_pid(pid)
    if p.frames is None:
        return None
    blocks = {f: round_trials(study, pid, f) for f in FPS_LEVELS}
    fps_list = [f for f in FPS_LEVELS if blocks[f]]
    if not fps_list:
        return None
    n = max(max(r["trial"] for r in blocks[f]) for f in fps_list)
    fig = plt.figure(figsize=(TEXT_W, 1.25 + 0.1 * n), layout="constrained")
    ratios = []
    for c in range(len(fps_list)):
        ratios += ([0.1] if c else []) + [2.2, 0.75]
    gs = fig.add_gridspec(1, len(ratios), width_ratios=ratios, wspace=0.02)
    col_i = 0
    for c, f in enumerate(fps_list):
        if c:
            col_i += 1
        trials = blocks[f]
        axm = fig.add_subplot(gs[0, col_i])
        axs = fig.add_subplot(gs[0, col_i + 1], sharey=axm)
        col_i += 2
        draw_round_map(axm, trials, f, relative=relative)
        draw_round_strip(axs, trials, f, block_xmax(study, pid, f))
        axm.set_ylim(n + 0.7, 0.3)
        axm.set_yticks([t for t in (1, 10, 20, 30, 40, 50) if t <= n])
        fit = study.fit(pid, f)
        th = f", $\\hat\\theta$ = {fit['theta']:.0f}" if fit is not None else ""
        axm.set_title(f"Base {cond_label(f)}, block {int(fit['blockOrdinal'])}{th}", loc="left", fontsize=FS_BASE)
        axm.set_xlabel("Shot − tower, + = slower side" if relative else "Horizontal position (world units)")
        axs.set_xlabel("Diff. (fps)")
        axm.set_ylabel("Staircase trial" if c == 0 else "")
    handles = [Patch(facecolor=HIT_ZONE, label=f"Hit zone (tower ± {CLOSE_RADIUS:g})"),
               Line2D([], [], color=INK_2, lw=0.8, label="Tower (boundary)"),
               Line2D([], [], color=SWEEP, lw=1.6, label="UFO's range in the round"),
               Line2D([], [], marker=">", ls="", ms=3.6, mfc=INK, mec=SURFACE, mew=0.25, label="Crossing (points the way it moved)"),
               Line2D([], [], marker="o", ls="", ms=4, mfc=INK_2, mec=SURFACE, label="Shot: hit"),
               Line2D([], [], marker="o", ls="", ms=3.8, mfc=SURFACE, mec=INK_2, mew=0.95, label="Shot: miss"),
               Line2D([], [], marker="x", ls="", ms=3.8, color=INK_2, mew=0.9, label="No shot (timed out / limit)"),
               Line2D([], [], color=INK_2, lw=1.0, label="Threshold estimate ± SD")]
    fig_legend_top(fig, handles, ncol=4, fontsize=FS_SMALL)
    what = ("re-centred on the tower and mirrored so the slower side is to the right" if relative
            else "where the UFO was, where the tower was, where the shot landed")
    stamp(fig, f"{pid} ({p.folder}) — rounds: {what}")
    return fig


# ================================================================ reporting

def latex_table(study):
    """Per-participant thresholds and the group mean, booktabs, ready for \\input{}."""
    b = study.blocks
    lines = [
        "% Generated by jnd_analysis.py - thresholds are posterior means (fps) with 95% CrI;",
        "% the Mean row is the across-participant mean with a 95% t-interval, the GM row the",
        "% geometric mean with its 95% interval (computed on log10 theta).",
        "\\begin{tabular}{l" + "c" * len(FPS_LEVELS) + "}",
        "\\toprule",
        " & " + " & ".join(f"\\multicolumn{{1}}{{c}}{{{f} fps}}" for f in FPS_LEVELS) + " \\\\",
        "\\midrule",
    ]
    for pid in study.pids:
        cells = []
        for f in FPS_LEVELS:
            r = b[(b["pid"] == pid) & (b["fps"] == f)]
            if r.empty or not r["included"].iloc[0]:
                cells.append("--" if r.empty else "abandoned")
                continue
            r = r.iloc[0]
            mark = ""
            if r["pinned"] == "top":
                mark = "$^\\S$"
            elif not r["reliable"]:
                mark = f"$^\\ddagger$ (SD {r['thetaSdFinal']:.1f})"
            elif not r["converged"]:
                mark = "$^\\dagger$"
            cells.append(f"{r['theta']:.1f} [{r['thetaLo']:.1f}, {r['thetaHi']:.1f}]{mark}")
        lines.append(f"{pid} & " + " & ".join(cells) + " \\\\")
    lines.append("\\midrule")
    cells, gcells = [], []
    for f in FPS_LEVELS:
        v = study.used[study.used["fps"] == f]["theta"]
        m, lo, hi = mean_ci(v)
        cells.append(f"\\textbf{{{fmt_num(m, 1)}}} [{fmt_num(lo, 1)}, {fmt_num(hi, 1)}]")
        gm, glo, ghi = geo_mean_ci(v)
        gcells.append(f"{fmt_num(gm, 1)} [{fmt_num(glo, 1)}, {fmt_num(ghi, 1)}]")
    lines.append("Mean & " + " & ".join(cells) + " \\\\")
    lines.append("GM & " + " & ".join(gcells) + " \\\\")
    lines += ["\\bottomrule", "\\end{tabular}",
              "% $^\\dagger$ block ended before reaching the SD stop rule.",
              f"% $^\\ddagger$ final posterior SD above {RELIABLE_SD_FACTOR:g} x the stop rule.",
              "% $^\\S$ estimate pressed against the top of the difference range: the threshold lies above it."]
    return "\n".join(lines) + "\n"


def block_diagnostics(study, pid, f):
    """What a block's misses looked like: shots just outside the hit zone on differences far
    above the threshold read as aiming, not detection."""
    p = study.by_pid(pid)
    s = p.shots[p.shots["counted"] & (p.shots["fps"] == f)]
    radius = float(s["closeRadius"].dropna().iloc[0]) if s["closeRadius"].notna().any() else CLOSE_RADIUS
    miss = s[(s["isHit"] == 0) & s["shot"]]
    near = miss[(miss["absMissX"] > radius) & (miss["absMissX"] <= 2 * radius)]
    return {"trials": len(s), "misses": len(miss), "noShot": int((~s["shot"]).sum()), "near": len(near),
            "maxStim": float(s["diffFps"].max()) if len(s) else np.nan,
            "hitsAtTop": int(((s["diffFps"] >= 0.9 * s["diffFps"].max()) & (s["isHit"] == 1)).sum()) if len(s) else 0,
            "radius": radius}


def report(study, stats_df, desc, sd_rec, out_dir, args):
    b = study.blocks
    u = study.used
    L = []
    w = L.append
    n_all = len(study.pids)
    w("# JND analysis report - frame-rate study\n")
    w(f"Data: `{args.data}`  \nParticipants: {n_all} "
      f"({', '.join(f'{p.pid} = {p.folder}' for p in study.participants)})  \n"
      f"Blocks: {len(b)} ({int(b['included'].sum())} measured, {int(b['abandoned'].sum())} abandoned); "
      f"staircase trials: {len(study.trials)}\n")
    if n_all < 6:
        w(f"> **Pilot-sized sample (N = {n_all}).** Everything below is descriptive. The tests are computed "
          "so the pipeline is ready for the full study, but with this few participants their intervals are "
          "very wide and their p values carry no evidential weight.\n")

    # ---------------------------------------------------------------- sessions and hardware
    w("## Sessions and hardware\n")
    w("| Participant | Started | Display | Screen | Limiter | Order (Latin row) | Δ range (fps) 60/144/500 | stopSD | γ logged |")
    w("|---|---|---|---|---|---|---|---|---|")
    for p in study.participants:
        bb = b[b["pid"] == p.pid].sort_values("fps")
        first = bb.iloc[0]
        # The range as the game used it (StudyConfig pulls the maximum onto the step grid).
        maxd = "/".join(f"{int(r.fit_gridLo)}–{int(r.fit_gridHi)}" if np.isfinite(r.fit_gridHi) else "-"
                        for r in bb.itertuples())
        stop = "/".join(f"{r.cfg_stopSD:g}" for r in bb.itertuples())
        order = " → ".join(str(int(f)) for f in bb.sort_values("blockOrdinal")["fps"])
        w(f"| {p.pid} ({p.folder}) | {str(first['startIso'])[:16].replace('T', ' ')} | {first['displayHz']:g} Hz | "
          f"{first['resolution']} ({first['aspect']:.2f}:1) | {first['frameLimiter']} | {order} (row {int(first['latinRow'])}) | "
          f"{maxd} | {stop} | {first['cfg_guessRate']:.3f} |")
    w("")
    diff_cfg = b.groupby("fps")[["cfg_maxDiffFps", "cfg_stopSD", "cfg_guessRate"]].nunique().max().max() > 1
    if diff_cfg:
        w("**The sessions ran under different configurations** (Δ range, stop rule or γ differ above). Each block is "
          "replayed under its own logged configuration, so its threshold is exactly the online one; but a block "
          "with a wider Δ range or a looser stop rule is not measured with the same precision, and a block whose "
          "estimate ends against the top of its range only bounds the threshold from below.\n")
    above = b[b["aboveRefresh"]]
    if len(above):
        w("**Base rate above the display's refresh rate.** " + ", ".join(
            f"{r.pid} {cond_label(int(r.fps))} on a {r.displayHz:g} Hz display" for r in above.itertuples())
          + ". A display shows at most its refresh rate, so on such a block both sides of the boundary are "
            "presented as at most that rate (plus tearing): the frame-rate difference the game ran is not the "
            "difference on screen. These blocks should be dropped or analysed with the effective rates "
            "min(rate, refresh).\n")

    # ---------------------------------------------------------------- gamma
    w("## Guess rate (fixed, not fitted)\n")
    if study.gamma_override:
        ov = ", ".join(f"{'all blocks' if k == '*' else k} = {v:g}" for k, v in study.gamma_override.items())
        w(f"**Overridden** on the command line ({ov}): every threshold, curve and trace below is recomputed under "
          "it, and the replay check against the log covers only the blocks still at their logged value.\n")
    for p in study.participants:
        bb = b[b["pid"] == p.pid]
        g_log = sorted(set(round(v, 4) for v in bb["cfg_guessRate"].dropna()))
        g_used = sorted(set(round(v, 4) for v in bb["fit_gamma"].dropna()))
        first = bb.iloc[0]
        sg, sgi = first["screenGamma"], first["screenGammaInset"]
        w(f"- {p.pid}: γ used {', '.join(f'{g:.3f}' for g in g_used)} (logged {', '.join(f'{g:.3f}' for g in g_log)}). "
          f"Geometric chance for this {first['resolution']} screen: {fmt_num(sg, 3)} with the UFO flying to the "
          f"edges, {fmt_num(sgi, 3)} with it held 2% inside them (builds before 22 Oct 2026)."
          + ("" if min(abs(g_log[0] - sg), abs(g_log[0] - sgi)) <= 0.005 else
             f" **The logged γ is off the screen's chance rate by {min(abs(g_log[0] - sg), abs(g_log[0] - sgi)):.3f}**: "
             "QUEST+ assumed a different chance level than this screen gives, which biases the threshold. "
             f"Re-run with `--gamma {p.pid}=<value>` to see the threshold under the screen's own chance rate."))
    w("")

    # ---------------------------------------------------------------- integrity
    if study.replay is not None and len(study.replay):
        r = study.replay
        w("## Integrity: replaying QUEST+ from the trial data\n")
        mm = r["stimulusMismatches"].sum()
        edge_ok = (r["rangeEdgeReplay"].fillna("") == r["rangeEdgeLogged"].fillna("")).all()
        w(f"Every block was replayed trial by trial under its logged configuration (single-precision grids, Δ range "
          f"validated as StudyConfig does). Max |difference| from the logged per-trial posterior over {len(r)} "
          f"blocks: θ {r['maxAbsDiffTheta'].max():.4f} fps, SD {r['maxAbsDiffSd'].max():.4f} fps, β "
          f"{r['maxAbsDiffBeta'].max():.4f}, λ {r['maxAbsDiffLambda'].max():.5f}. Final mean/median/CI vs "
          f"SessionLog: max |diff| "
          f"{np.nanmax(np.abs(r[['finalDiffMean', 'finalDiffMedian', 'finalDiffCiLo', 'finalDiffCiHi']].to_numpy())):.4f} fps "
          "(logged to 3 decimals). Range-edge flags " + ("agree" if edge_ok else "**disagree**") + " with the log. "
          f"Stimulus placement: {int(mm)} of {int(r['trials'].sum())} trials differ from the difference QUEST+'s "
          "placement rule chooses after the previous trial" + (" - the online procedure is reproduced exactly." if mm == 0
                                                                 else " - **check these blocks**.") + "\n")

    # ---------------------------------------------------------------- data quality
    w("## Data quality\n")
    w(f"**Reliability rule (declared in the script, `RELIABLE_SD_FACTOR` = {RELIABLE_SD_FACTOR:g}).** A block is "
      f"unreliable when its final posterior SD is above {RELIABLE_SD_FACTOR:g} × its stop rule, or when the estimate "
      "is pressed against an end of the difference range (QUEST+'s rangeEdge: the 95% credible interval reaches the "
      "last grid step). Either way the data have not located the threshold: the posterior mean is set by the prior "
      "and the range limits. Such blocks are flagged in the figures and tables, kept in the primary analysis, and "
      "dropped in the sensitivity analyses. Abandoned blocks are not measurements and are left out everywhere.\n")
    ab = b[b["abandoned"]]
    if len(ab):
        w("| Abandoned block | Trials | Posterior at abandonment | Note |")
        w("|---|---|---|---|")
        for r_ in ab.itertuples():
            note = "above the display refresh" if r_.aboveRefresh else ""
            w(f"| {r_.pid} {cond_label(int(r_.fps))} | {int(r_.staircaseTrials)} | "
              f"{fmt_num(r_.fit_theta, 1)} ± {fmt_num(r_.thetaSdFinal, 1)} fps | {note} |")
        w("")
    flagged = u[~u["reliable"]]
    if flagged.empty:
        w("No measured block is flagged.\n")
    else:
        w("| Block | θ (fps) | Posterior SD | 95% CrI | Trials | Ended | Notes |")
        w("|---|---|---|---|---|---|---|")
        for _, r_ in flagged.iterrows():
            d = block_diagnostics(study, r_["pid"], int(r_["fps"]))
            notes = []
            if r_["pinned"] == "top":
                notes.append(f"estimate pressed against the top of the {int(r_['fit_gridLo'])}–{int(r_['fit_gridHi'])} "
                             f"fps range: the threshold is above about {r_['thetaLo']:.0f} fps, its value is not measured")
            notes.append(f"{d['misses']} missed shots, {d['noShot']} rounds without a shot; {d['near']} misses landed just "
                         f"outside the hit zone ({d['radius']:g}–{2 * d['radius']:g} units)")
            w(f"| {r_['pid']} {cond_label(int(r_['fps']))} | {r_['theta']:.1f} | {r_['thetaSdFinal']:.1f} | "
              f"[{r_['thetaLo']:.1f}, {r_['thetaHi']:.1f}] | {int(r_['staircaseTrials'])} | {r_['endReason']} | "
              f"{'; '.join(notes)} |")
        w("")
    rows = b.groupby("pid")["latinRow"].first().value_counts().sort_index()
    w("**Counterbalancing.** Latin-square rows used: " + ", ".join(f"row {int(k)} × {int(v)}" for k, v in rows.items())
      + (" (each row once or equally often)." if rows.nunique() == 1 and len(rows) > 1 else "."))
    if len(rows) == 1 and n_all > 1:
        w(" **Every session used the same row**, so block position and base rate are confounded: an order effect "
          "(practice, fatigue) cannot be told apart from a frame-rate effect.")
    w("\n")
    prac = b.groupby("pid")[["practiceCorrect", "practiceTrials"]].sum()
    w("**Practice.** " + "; ".join(f"{pid}: {int(r_.practiceCorrect)}/{int(r_.practiceTrials)} practice rounds hit"
                                    for pid, r_ in prac.iterrows()) + ".\n")

    # ---------------------------------------------------------------- thresholds
    w("## Thresholds by base rate\n")
    w("| Base rate | n | M (fps) | SD | 95% CI | GM (fps) [95% CI] | Median | Range | Weber M (%) | Frame-time Δ M (ms) | Altered rate M (fps) |")
    w("|---|---|---|---|---|---|---|---|---|---|---|")
    for f in FPS_LEVELS:
        g = u[u["fps"] == f]
        v = g["theta"].to_numpy(float)
        v = v[np.isfinite(v)]
        if not len(v):
            w(f"| {cond_label(f)} | 0 | – | – | – | – | – | – | – | – | – |")
            continue
        m, lo, hi = mean_ci(v)
        gm, glo, ghi = geo_mean_ci(v)
        sd = v.std(ddof=1) if len(v) > 1 else np.nan
        w(f"| {cond_label(f)} | {len(v)} | {m:.2f} | {fmt_num(sd)} | [{fmt_num(lo)}, {fmt_num(hi)}] | "
          f"{gm:.2f} [{fmt_num(glo)}, {fmt_num(ghi)}] | {np.median(v):.2f} | {v.min():.2f}–{v.max():.2f} | "
          f"{g['weberPct'].mean():.1f} | {g['frameTimeDiffMs'].mean():.2f} | {g['alteredAtTheta'].mean():.1f} |")
    w("")
    if not flagged.empty:
        w("Without the flagged block(s):\n")
        w("| Base rate | n | M (fps) | 95% CI | Weber M (%) |")
        w("|---|---|---|---|---|")
        rb = u[u["reliable"]]
        for f in FPS_LEVELS:
            g = rb[rb["fps"] == f]
            m, lo, hi = mean_ci(g["theta"])
            w(f"| {cond_label(f)} | {g['theta'].notna().sum()} | {fmt_num(m)} | [{fmt_num(lo)}, {fmt_num(hi)}] | "
              f"{fmt_num(g['weberPct'].mean(), 1)} |")
        w("")
    w("The frame-time difference is how much longer each altered-side frame lasts than a base-side frame at "
      "threshold, 1000/(base − θ) − 1000/base ms; the Weber fraction is θ / base.\n")

    # ---------------------------------------------------------------- inference
    w("## Inferential statistics\n")
    w(f"N = {n_all}. The RM-ANOVA uses only participants with all three base rates measured (n); the paired "
      "contrasts use every complete pair. Alongside the parametric p: the Greenhouse-Geisser corrected p (p_GG, with "
      "ε) for the omnibus test, a within-participant permutation p (p_perm: condition labels shuffled inside each "
      "participant, exact when the patterns are few; for contrasts the exact sign-flip test), the Friedman test, "
      "and p_Holm, Holm-adjusted across a dependent variable's three contrasts. SW = Shapiro-Wilk p of the contrast "
      "scores (small = the t assumption is doubtful)."
      + (" With fewer than ~10 participants these tests are illustrative; report the estimates and intervals first."
         if n_all < 10 else "") + "\n")
    fam_title = {"threshold": "Primary: threshold (posterior mean) and Weber fraction",
                 "units": "The threshold in other units", "sensitivity": "Sensitivity analyses",
                 "secondary": "Procedure-dependent measures (descriptive; QUEST+ sets them)",
                 "order": "Order effects"}
    for fam in ["threshold", "units", "sensitivity", "secondary", "order"]:
        g_f = stats_df[stats_df["family"] == fam]
        if g_f.empty:
            continue
        w(f"### {fam_title[fam]}\n")
        for dv, g in g_f.groupby("dv", sort=False):
            w(f"**{dv}**\n")
            for _, r_ in g.iterrows():
                if r_["test"] == "RM-ANOVA":
                    if not np.isfinite(r_.get("F", np.nan)):
                        w(f"- Base frame rate: not testable (n = {int(r_['n'])} participant(s) with all three rates)")
                        continue
                    w(f"- Base frame rate: F({int(r_['df1'])}, {int(r_['df2'])}) = {fmt_num(r_['F'])}, {apa_p(r_['p'])}, "
                      f"η²p = {fmt_num(r_['eta_p2'])}; ε_GG = {fmt_num(r_['eps_gg'])}, {apa_p(r_['p_gg']).replace('p ', 'p_GG ', 1)}; "
                      f"p_perm = {fmt_num(r_['p_perm'], 3)}; Friedman p = {fmt_num(r_['friedman_p'], 3)} (n = {int(r_['n'])})")
                elif "trend" in str(r_["test"]):
                    w(f"- {r_['test']}: slope {fmt_num(r_['slope'])} per block [{fmt_num(r_.get('ci_lo'))}, "
                      f"{fmt_num(r_.get('ci_hi'))}], t({int(r_['df'])}) = {fmt_num(r_['t'])}, {apa_p(r_['p'])} (n = {int(r_['n'])})")
                else:
                    if int(r_["n"]) < 2:
                        w(f"- {r_['test']}: MΔ = {fmt_num(r_['diff'])} (n = {int(r_['n'])}; no interval)")
                        continue
                    w(f"- {r_['test']}: MΔ = {fmt_num(r_['diff'])} [{fmt_num(r_['ci_lo'])}, {fmt_num(r_['ci_hi'])}], "
                      f"t({int(r_['df'])}) = {fmt_num(r_['t'])}, {apa_p(r_['p'])}, d_z = {fmt_num(r_['dz'])} "
                      f"(n = {int(r_['n'])}; p_perm = {fmt_num(r_.get('p_perm'), 3)}, p_Holm = {fmt_num(r_.get('p_holm'), 3)}, "
                      f"SW = {fmt_num(r_.get('shapiro_p'), 3)})")
            w("")

    # ---------------------------------------------------------------- convergence
    w("## Convergence\n")
    fits = study.fits[~study.fits["abandoned"]]
    conv = fits["converged"]
    w(f"{int(conv.sum())} of {len(conv)} measured blocks met their SD stop rule; {int((~conv).sum())} did not. "
      f"Trials per block: M = {fits['trials'].mean():.1f}, range {fits['trials'].min()}–{fits['trials'].max()}.\n")
    if not sd_rec.empty:
        w("Trials to first reach each posterior-SD target (a multiple of the block's stop rule). Blocks that never "
          "reached a target are censored: 'Median trials (reached)' uses only the blocks that got there.\n")
        w("| Target (× stop rule) | Blocks reaching it | Median trials (reached) | Mean trials (censored at block end) |")
        w("|---|---|---|---|")
        for k, g in sd_rec.groupby("ratio"):
            reached = g[g["reached"]]
            w(f"| {k:g} | {g['reached'].mean() * 100:.0f}% | "
              f"{fmt_num(reached['trials'].median() if len(reached) else np.nan, 0)} | {g['trials'].mean():.1f} |")
        w("")

    # ---------------------------------------------------------------- behaviour
    w("## Exploration and aiming\n")
    for f in FPS_LEVELS:
        g = u[u["fps"] == f]
        if g.empty:
            continue
        w(f"- {cond_label(f)}: crossings before the shot M = {g['crossingsBeforeShot'].mean():.2f}; time in a round "
          f"(median per block) M = {g['liveRoundSec'].mean():.1f} s; rounds without a shot {100 * g['timeoutShare'].mean():.0f}%; "
          f"median shot–tower distance M = {g['missDistMedian'].mean():.2f} units; median offset toward the slower side "
          f"M = {g['towardAlteredMedian'].mean():+.2f} units (positive = shots land on the slower side)")
    w("")

    # ---------------------------------------------------------------- frame timing
    w("## Frame delivery\n")
    for f in FPS_LEVELS:
        g = b[b["fps"] == f]
        if g.empty:
            continue
        line = (f"- {cond_label(f)} target: base side achieved {g['baseSideMeasuredFps'].mean():.1f} fps "
                f"(blocks {g['baseSideMeasuredFps'].min():.1f}–{g['baseSideMeasuredFps'].max():.1f}); base frame-time SD "
                f"{g['baseFrameTimeSdMs'].mean():.3f} ms; altered side frame-time error {g['alteredFrameTimeErrPct'].mean():.2f}%")
        if "baseLateShare" in g:
            line += f"; base-side frames over their period by > 1 ms: {100 * g['baseLateShare'].mean():.2f}%"
        w(line)
    short = b[b["achievedBaseRatio"] < 0.97]
    if len(short):
        w("")
        w("**Base rates short of their target by more than 3%:** " + ", ".join(
            f"{r_.pid} {cond_label(int(r_.fps))} achieved {r_.baseSideMeasuredFps:.1f} fps ({100 * r_.achievedBaseRatio:.1f}%, "
            f"{r_.frameLimiter} limiter)" for r_ in short.itertuples())
          + ". On such a block the difference actually shown is smaller than the one QUEST+ was told, so its threshold "
            "is overestimated; the effective difference is the measured base rate minus the measured altered rate.")
    w("")
    w("## Participant notes (.txt files in each folder, verbatim)\n")
    for p in study.participants:
        w(f"- {p.pid} ({p.folder}): {p.notes or '-'}")
    w("")
    (out_dir / "report.md").write_text("\n".join(L) + "\n", encoding="utf-8")


def write_captions(docs, out_dir):
    L = ["# Figure captions and alt text\n",
         "Captions are drafts with the numbers filled in. Each `\\Description{}` is the alt text ACM's acmart "
         "class expects inside every figure environment (CHI requires one per figure).\n"]
    for d in docs:
        L.append(f"## {d.stem}\n")
        L.append(f"**Caption.** {d.caption}\n")
        L.append("```latex")
        L.append(f"\\Description{{{d.alt}}}")
        L.append("```\n")
    (out_dir / "captions.md").write_text("\n".join(L), encoding="utf-8")


# ================================================================ driver

def paper_figures():
    """(stem, builder, needs PlayerLogs) for every paper figure, in the order they are drawn."""
    return [
        ("Fig01_thresholds", fig_main, False),
        ("Fig02_psychometric", fig_psychometric, False),
        ("Fig02b_detection_overlay", fig_detection_overlay, False),
        ("Fig03_convergence", fig_convergence, False),
        ("Fig04_order_effects", fig_order, False),
        ("Fig05_frame_timing", fig_frame_timing, False),
        ("Fig06_outcomes", fig_outcomes, False),
        ("Fig11_aiming", fig_aiming, False),
        ("Fig12_exposure", fig_exposure, False),
        ("Fig12b_exposure_main", fig_exposure_main, False),
        ("Fig12c_exposure_main_heatmap", lambda s: fig_exposure_main(s, bubbles=True), False),
        ("Fig13_threshold_sd_sessions", fig_threshold_sd_sessions, False),
        ("Fig13b_threshold_sd_sessions_values", fig_threshold_sd_sessions_values, False),
        ("Fig23_threshold_summary", fig_threshold_summary, False),
        ("Fig24_threshold_summary_reliable", fig_threshold_summary_reliable, False),
        ("Fig25_threshold_summary_converged", fig_threshold_summary_converged, False),
        ("Fig25b_threshold_summary_converged_tertiles", fig_threshold_summary_converged_tertiles, False),
        ("Fig25c_threshold_summary_converged_tertiles_distribution", fig_threshold_summary_converged_tertiles_distribution, False),
        ("Fig32_sd_summary", fig_sd_summary, False),
        ("Fig33_sd_summary_reliable", fig_sd_summary_reliable, False),
        ("Fig34_sd_summary_converged", fig_sd_summary_converged, False),
        ("Fig35_slope_summary", fig_slope_summary, False),
        ("Fig36_slope_summary_reliable", fig_slope_summary_reliable, False),
        ("Fig37_slope_summary_converged", fig_slope_summary_converged, False),
        ("Fig37b_slope_summary_converged_tertiles", fig_slope_summary_converged_tertiles, False),
        ("Fig14_slope_staircases", fig_slope_staircases, False),
        ("Fig16_laser_distance_from_tower", lambda s: fig_laser_distance_from_tower(s, "hit"), False),
        ("Fig18_laser_misses", lambda s: fig_laser_distance_from_tower(s, "miss"), False),
        ("Fig20_aggregate_laser_hits", fig_aggregate_laser_hits, False),
        ("Fig22_aggregate_laser_hits_merged", fig_aggregate_laser_hits_merged, False),
        ("Fig27_aggregate_laser_hits_binned", fig_aggregate_laser_hits_binned, False),
        ("Fig29_aggregate_laser_hits_binned_converged", fig_aggregate_laser_hits_binned_converged, False),
        ("Fig31_cdf_laser_all", fig_cdf_laser_all, False),
        ("Fig39_cdf_laser_lines", fig_cdf_laser_lines, False),
        ("Fig41_score", fig_score, False),
        ("FigS2_threshold_units", fig_threshold_units, False),
        ("FigS3_sd_targets", lambda s: fig_sd_targets(s)[0], False),
        ("FigS4_sd_all_runs", fig_sd_all_runs, False),
        ("FigS5_heatmaps", fig_heatmaps, True),
        ("FigS6_behaviour_all", fig_behaviour, False),
        ("FigS7_targeting", fig_targeting, False),
        ("FigS8_staircases", fig_staircases, False),
    ]


def participant_figures():
    """(name, builder(study, pid)) for the per-participant figures; a builder may return None."""
    return [
        ("curves", fig_p_curves),
        ("convergence", fig_p_convergence),
        ("sd", fig_p_sd),
        ("sd_overlay", lambda s, pid: fig_p_sd(s, pid, restart=True)),
        ("parameters", fig_p_parameters),
        ("composite", fig_p_composite),
        ("heatmap", fig_p_heatmap),
        ("roundmap", fig_p_roundmap),
        ("roundmap_relative", lambda s, pid: fig_p_roundmap(s, pid, relative=True)),
    ]


# The paper's own numbering (paper/main): the set chosen for the frame-time paper, carried over
# minus the shockwave and response-time figures and renumbered without gaps. Every other
# figure goes to paper/misc as FigS01, FigS02, ... in drawing order.
MAIN_MAPPING = {
    "13": "Fig01", "13b": "Fig01b", "14": "Fig02", "20": "Fig03", "22": "Fig04",
    "23": "Fig05", "24": "Fig06", "25": "Fig07", "25b": "Fig07b", "25c": "Fig07c",
    "27": "Fig08", "32": "Fig09", "34": "Fig10", "35": "Fig11", "37": "Fig12", "37b": "Fig12b",
    "39": "Fig13", "S8": "Fig14", "12b": "Fig15", "12c": "Fig15a",
}


def parse_gamma(items):
    """--gamma VALUE (every block) and/or --gamma P2=VALUE (one participant's blocks)."""
    out = {}
    for it in items or []:
        m = re.fullmatch(r"\s*(?:([^=\s]+)\s*=\s*)?([0-9]*\.?[0-9]+)\s*", it)
        if not m or not 0.0 <= float(m.group(2)) < 1.0:
            sys.exit(f"--gamma expects VALUE or PARTICIPANT=VALUE with 0 <= VALUE < 1 (e.g. 0.235, P2=0.175), got {it!r}")
        out[m.group(1) or "*"] = float(m.group(2))
    return out or None


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", type=Path, default=DEFAULT_DATA,
                    help=f"Folder holding one subfolder per participant. Default: {DEFAULT_DATA}")
    ap.add_argument("--out", type=Path, help="Output folder. Default: <data>/../Results")
    ap.add_argument("--gamma", nargs="+", metavar="[PID=]VALUE",
                    help="Fix gamma instead of each block's logged cfg_guessRate: one value for every block "
                         "and/or PID=VALUE for one participant, e.g. --gamma P2=0.175")
    ap.add_argument("--only", nargs="+", metavar="ID", help="Only these participants (P1, folder name, or sessionId)")
    ap.add_argument("--exclude", nargs="+", metavar="ID", help="Leave these participants out")
    ap.add_argument("--no-frames", action="store_true", help="Skip PlayerLogs (frame-time, heatmap, round-map figures)")
    ap.add_argument("--no-participants", action="store_true", help="Skip the per-participant figures")
    ap.add_argument("--no-stimulus-check", action="store_true",
                    help="Skip re-deriving every trial's difference from QUEST+'s placement rule (faster)")
    ap.add_argument("--formats", nargs="+", default=["png"], choices=["png", "svg", "tif"],
                    help="Output formats. Default: png.")
    ap.add_argument("--dpi", type=int, default=600, help="Raster resolution (PNG/TIF). Default 600.")
    ap.add_argument("--figs", nargs="+", metavar="PATTERN",
                    help="Only build figures whose name matches one of these shell patterns, e.g. Fig01* "
                         "'FigS*' 'Fig_curves*' (tables and report are always written)")
    args = ap.parse_args()

    if not args.data.is_dir():
        sys.exit(f"No such folder: {args.data}")
    out = args.out or args.data.parent / "Results"
    family = use_chi_style()
    log(f"Data:  {args.data}\nOut:   {out}\nFont:  {family}")

    log("Loading ...")
    participants = load_participants(args.data, args.only, args.exclude, frames=not args.no_frames)
    if not participants:
        sys.exit("No usable participant folders found.")
    study = Study(participants, parse_gamma(args.gamma))
    log(f"  {len(participants)} participant(s): " + ", ".join(f"{p.pid}={p.folder}" for p in participants))

    log("Fitting ...")
    fit_all(study, check_stimuli=not args.no_stimulus_check)
    build_tables(study)
    log("  gamma (fixed): " + "; ".join(
        f"{pid} {', '.join(f'{g:.3f}' for g in sorted(set(study.fits[study.fits['pid'] == pid]['gamma'])))}"
        for pid in study.pids))
    if len(study.replay):
        r = study.replay
        log(f"  replay vs log: max |d theta| = {r['maxAbsDiffTheta'].max():.4f} fps, "
            f"max |d SD| = {r['maxAbsDiffSd'].max():.4f} fps over {len(r)} blocks; "
            f"stimulus placement mismatches: {int(np.nansum(r['stimulusMismatches']))}")
    stats_df = inferential(study)
    desc = condition_descriptives(study.blocks, THRESHOLD_METRICS + [(k, lab) for k, lab in BEHAVIOUR])

    tables = out / "tables"
    tables.mkdir(parents=True, exist_ok=True)
    study.blocks.to_csv(tables / "blocks.csv", index=False, float_format="%.6g")
    study.trials.to_csv(tables / "trials.csv", index=False, float_format="%.6g")
    study.responses.to_csv(tables / "responses.csv", index=False, float_format="%.6g")
    desc.to_csv(tables / "conditions.csv", index=False, float_format="%.6g")
    stats_df.to_csv(tables / "stats.csv", index=False, float_format="%.6g")
    (tables / "thresholds.tex").write_text(latex_table(study), encoding="utf-8")
    if len(study.replay):
        study.replay.to_csv(tables / "replay_check.csv", index=False, float_format="%.6g")
    sd_rec = sd_target_records(study)
    sd_rec.to_csv(tables / "sd_targets.csv", index=False)

    def wanted(name):
        return not args.figs or any(fnmatch.fnmatch(name, pat) for pat in args.figs)

    paper_main, paper_misc = out / "paper" / "main", out / "paper" / "misc"
    misc_num = 1
    has_frames = any(p.frames is not None for p in participants)
    log("Paper figures ...")
    docs = []
    for stem, build, needs_frames in paper_figures():
        if not wanted(stem) or (needs_frames and not has_frames):
            continue
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", RuntimeWarning)
            try:
                doc = build(study)
            except Exception as exc:  # noqa: BLE001 - one figure must not stop the rest
                log(f"  {stem}: FAILED ({type(exc).__name__}: {exc})")
                plt.close("all")
                continue
        if doc is None:
            log(f"  {stem}: skipped (not enough data)")
            continue
        m = re.match(r"^Fig([a-zA-Z]*[0-9]+)([a-z]?)_?(.*)$", doc.stem)
        orig_id, letter, suffix = (m.group(1) + m.group(2), m.group(2), m.group(3)) if m else (doc.stem, "", doc.stem)
        if orig_id in MAIN_MAPPING:
            doc.stem = f"{MAIN_MAPPING[orig_id]}_{suffix}".rstrip("_")
            target = paper_main
        else:
            if letter:
                doc.stem = f"FigS{max(1, misc_num - 1):02d}{letter}_{suffix}".rstrip("_")
            else:
                doc.stem = f"FigS{misc_num:02d}_{suffix}".rstrip("_")
                misc_num += 1
            target = paper_misc
        save(doc.fig, target, doc.stem, args.formats, args.dpi)
        docs.append(doc)
        log(f"  {doc.stem}  (from {stem})")
    docs.sort(key=lambda d: d.stem)

    if not args.no_participants:
        log("Participant figures ...")
        for p in participants:
            target = out / "participants" / f"{p.pid}_{p.folder}"
            made = 0
            for name, build in participant_figures():
                stem = f"Fig_{name}_{p.pid}"
                if not wanted(stem):
                    continue
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore", RuntimeWarning)
                    try:
                        fig = build(study, p.pid)
                    except Exception as exc:  # noqa: BLE001
                        log(f"  {p.pid} {name}: FAILED ({type(exc).__name__}: {exc})")
                        plt.close("all")
                        continue
                if fig is not None:
                    save(fig, target, stem, args.formats, args.dpi)
                    made += 1
            log(f"  {p.pid}: {made} figures")

    report(study, stats_df, desc, sd_rec, out, args)
    if not args.figs:          # a partial run must not overwrite the full caption file
        write_captions(docs, out)

    log("\nThresholds (posterior mean, fps, gamma fixed; abandoned blocks blank):")
    log(wide(study.blocks, "theta", study.pids).round(2).to_string())
    log("\nWeber fraction (%):")
    log(wide(study.blocks, "weberPct", study.pids).round(1).to_string())
    log(f"\nDone -> {out}")


if __name__ == "__main__":
    main()
