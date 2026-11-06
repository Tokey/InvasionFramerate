#!/usr/bin/env python3
"""
Synthetic session logs in the game's CURRENT format, for testing the analysis tools before (or
without) real sessions: the three level types (manual / scroll / auto), both directions,
practice per level type, QUEST+ picking every difference, and per-frame pacing.

    python Analysis/simulate_logs.py --out <folder>                # 4 participants
    python Analysis/simulate_logs.py --out <folder> --participants 8 --seed 3 --max-trials 30
    python Analysis/simulate_logs.py --out <folder> --config Data/ExperimentConfig.csv

Each participant gets a folder <sessionId>_SIMn holding SessionLog_, ShotLog_, PlayerLog_ and
SessionInfo_ files laid out exactly as ExperimentLogger.cs writes them (README "Log files"),
plus ExperimentConfig_used.csv. Nothing here is real data: the folder names end in SIM and
SessionInfo says simulated=true.

What is simulated
  observer   P(detect | diff) = (1 - lapse) * (1 - exp(-(diff / theta)^beta)); theta is a Weber
             fraction of the base rate that depends on the level (scroll easiest, auto hardest),
             the direction, and the participant (lognormal). A guess is never "detected": it lands
             wherever the UFO happens to be, so chance comes from the geometry, as in the game.
  QUEST+     the exact port in jnd_analysis.py (Grid, Posterior: the C#'s grids and entropy rule),
             with each row's grids, guessRate and stop rule.
  manual     the participant sweeps back and forth across the tower region at 8-25 world units/s,
             the camera still within a round and panning up to +-6 between rounds.
  scroll     the same flight, the camera locked on the UFO; the boundary repeats every 96 units
             with the rates swapped at odd copies, and the logs report the copy nearest the UFO.
  auto       the autopilot's exact path (UfoAutopilot.cs, AutoFlightPath) at the row's settings,
             a random starting point each round; a detected boundary is clicked a reaction time
             (lognormal, ~0.45 s) after a crossing; the mouse only fidgets.
  frames     each frame lasts the cap chosen at the end of the one before (base or altered by the
             UFO's side, the 0.25-unit dead zone), with jitter and rare hitches; GO! (0.5 s, split
             open, UFO still), the reveal and reset (~3.3 s at the round's base, no split).
Every ShotLog and SessionLog value is computed from the simulated frames with the definitions the
game uses, so Analysis/audit_logs.py should pass on the output.

Requires: numpy (and Analysis/jnd_analysis.py beside this file, for QUEST+).
"""

from __future__ import annotations

import argparse
import csv
import math
import sys
from datetime import datetime, timedelta
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from jnd_analysis import Grid, Posterior, altered_fps_for  # noqa: E402 - the exact QUEST+ port

VIEW_W = 2 * 100 * math.tan(math.radians(7.5)) * 16 / 9      # play-plane width on 16:9: 46.81
CLOSE_R, DEAD_ZONE, PAN, PERIOD, SPAWN = 6.0, 0.25, 6.0, 96.0, 0.6
GO_SEC, GAP_SEC = 0.5, 3.3
FLOOR_Y, CEIL_Y = 2.85, 15.9
COUNTS_PER_UNIT = 25.0                                         # mouse counts per world unit flown
PRACTICE = [(30, 60), (60, 500), (60, 144), (144, 500), (240, 500)]

CONFIG_COLUMNS = [
    "levelType", "baseFps", "direction", "maxDiffMs", "minDiffMs", "diffStepMs", "alteredSide", "frameLimiter",
    "maxCrossingsPerRound", "roundTimeoutSec", "hitPoints", "missPoints", "threshCount", "slopeMin", "slopeMax",
    "slopeCount", "lapseMin", "lapseMax", "lapseCount", "guessRate", "maxTrials", "minTrials", "stopSD",
    "scrollFollow", "autoSpeed", "autoPath", "autoSweep", "autoTurnSec", "autoHeight", "autoBobAmp", "autoBobHz",
    "autoStart", "swFlipDelayMinSec", "swFlipDelayMaxSec", "swWindowSec",
    "swMaxEarlyPerRound", "maxFlipsPerRound", "showDebug"]
_GRID = "1,7,10,0,0.06,4"
_AUTO = "8,linear,0.9,0.6,0.4,0.5,0.4,random"
DEFAULT_ROWS = [
    f"manual,60,down,50,0.5,0.5,random,unity,15,15,100,-10,100,{_GRID},0.235,50,8,0.5,,,,,,,,,",
    f"scroll,144,down,50,0.5,0.5,random,unity,15,15,100,-10,100,{_GRID},0.235,50,8,0.5,locked,,,,,,,,",
    f"auto,500,down,98,0.5,0.5,random,unity,15,20,100,-10,196,{_GRID},0.248,50,8,0.5,,{_AUTO}",
    f"auto,60,up,14.5,0.5,0.5,random,unity,15,20,100,-10,29,{_GRID},0.248,50,8,0.5,,{_AUTO}",
    f"manual,144,up,4.5,0.5,0.5,random,unity,15,15,100,-10,9,{_GRID},0.235,50,8,0.5,,,,,,,,,",
    f"auto,60,down,50,0.5,0.5,random,unity,15,20,100,-10,100,{_GRID},0.248,50,8,0.5,,{_AUTO}",
]
# The observer's threshold, as a Weber fraction of the base, by level; 'up' a little easier.
WEBER = {"manual": 0.30, "scroll": 0.22, "auto": 0.38, "shockwave": 0.34}
UP_FACTOR = 0.85

IDENTITY = ["sessionId", "runId", "blockIndex", "blockOrdinal", "latinRow", "latinOrder", "baseFps", "direction",
            "frameLimiter", "levelType"]
SESSION_COLS = IDENTITY + [
    "displayHz", "resolution", "startIso", "endIso", "sessionDurationSec", "playDurationSec", "endReason",
    "jndDiffFps", "jndAlteredFps", "jndFrameTimeDiffMs", "sd", "priorSd", "jndMedianDiffFps", "jndModeDiffFps",
    "jndCI95LoDiffFps", "jndCI95HiDiffFps", "jndMedianDiffMs", "jndModeDiffMs", "jndCI95LoDiffMs", "jndCI95HiDiffMs",
    "jndRangeEdge", "slopeEstimate", "lapseEstimate", "staircaseTrials",
    "lastDiffFps", "lastDiffMs", "trials", "shotsFired", "hits", "accuracy", "score", "roundsExpired", "roundsCrossingCapped",
    "avgRoundDurationSec", "avgCrossingsPerRound", "shotsWithoutCrossing", "avgSinceLastCrossingSec",
    "avgMissDistX", "avgMissDistXMissesOnly", "medianMissDistX", "sdMissDistX", "maxMissDistX",
    "totalMousePathPx", "avgMouseSpeedPxPerSec", "peakMouseSpeedPxPerSec", "totalUfoPathWorld", "frameCount",
    "liveFrames", "baseSideFrames", "baseSideMeasuredFps", "baseFrameTimeSdMs", "alteredSideFrames",
    "alteredFrameTimeErrPct", "alteredFrameTimeSdMs", "slowFrames", "maxLiveFrameTimeMs", "viewWidthWorld",
    "guessRateScreen", "avgFirstCrossingSec", "avgCrossingSpeed", "avgShotUfoSpeed", "medianSinceLastCrossingSec",
    "avgMissAlongTravelX"]
SHOT_COLS = IDENTITY + [
    "closeRadius", "phase", "phaseStartIso", "roundNumber", "timeSinceStartSec", "diffFps", "diffMs", "alteredFps",
    "alteredSide", "measuredBaseFps", "measuredAlteredFps", "baseSideSec", "alteredSideSec", "lateFrames",
    "crossings", "lastCrossingAtSec", "sinceLastCrossingSec", "outcome", "playerFired", "isHit",
    "countedByStaircase", "totalScore", "trialStartSec", "firedAtSec", "roundDurationSec", "hitX", "missDistX",
    "towerX", "ufoY", "side", "threshDiffFps", "threshDiffMs", "sd", "slopeEstimate", "lapseEstimate", "camX", "ufoViewportX",
    "towerViewportX", "ufoVelX", "ufoXAtStart", "firstCrossingAtSec", "meanCrossingSpeed", "lastCrossingSpeed"]
PLAYER_COLS = IDENTITY + [
    "phase", "phaseStartIso", "roundNumber", "frameIndex", "timeSinceStartSec", "unscaledDeltaMs", "targetFps",
    "pacedBy", "diffFps", "alteredSide", "side", "crossed", "shockwaveFastPhase", "shockwaveWindow", "shockwaveFlips", "mouseX", "mouseY", "mouseDeltaX", "mouseDeltaY",
    "ufoX", "ufoY", "towerX", "leftButtonDown", "leftButtonPressed", "fireKeyDown", "shotFired", "shotHitX",
    "camX"]


# ------------------------------------------------------------------ formatting (CsvTable.F / I / B)

def F(v, d=3):
    if v is None or (isinstance(v, float) and not math.isfinite(v)):
        return ""
    return f"{v:.{d}f}"


def B(v):
    return "1" if v else "0"


def iso(t):
    return t.isoformat(timespec="microseconds") + "-04:00"


# ------------------------------------------------------------------ the autopilot path (AutoFlightPath)

def auto_period(mode, v, turn, width):
    v = max(1e-3, v)
    if mode == "sine":
        return 2 * width / v
    r = min(max(0.0, v * turn / math.pi), width / 2)
    return 2 * (width - 2 * r) / v + 2 * math.pi * r / v


def auto_sample(mode, v, turn, width, tau):
    v = max(1e-3, v)
    p = auto_period(mode, v, turn, width)
    phi = tau % p
    if mode == "sine":
        w = 2 * math.pi / p
        return width / 2 - width / 2 * math.cos(w * phi), width / 2 * w * math.sin(w * phi)
    r = min(max(0.0, v * turn / math.pi), width / 2)
    tl, tc = (width - 2 * r) / v, math.pi * r / v
    if phi < tl:
        return r + v * phi, v
    phi -= tl
    if phi < tc:
        th = phi * v / r
        return width - r + r * math.sin(th), v * math.cos(th)
    phi -= tc
    if phi < tl:
        return width - r - v * phi, -v
    phi -= tl
    th = phi * v / r if r > 1e-6 else 0.0
    return r - r * math.sin(th), -v * math.cos(th)


def auto_gamma(sweep, mode, v, turn, n=4000):
    xa, width = (0.5 - sweep / 2) * VIEW_W - VIEW_W / 2, sweep * VIEW_W
    t_lo, t_hi = -SPAWN * VIEW_W / 2, SPAWN * VIEW_W / 2
    p = auto_period(mode, v, turn, width)
    s = 0.0
    for i in range(n):
        x = xa + auto_sample(mode, v, turn, width, (i + 0.5) / n * p)[0]
        s += max(0.0, min(t_hi, x + CLOSE_R) - max(t_lo, x - CLOSE_R)) / (t_hi - t_lo)
    return s / n


def manual_gamma():
    u_lo, u_hi, t_lo, t_hi = -VIEW_W / 2, VIEW_W / 2, -SPAWN * VIEW_W / 2, SPAWN * VIEW_W / 2
    ts = np.linspace(t_lo, t_hi, 4001)
    return float(np.mean(np.clip(np.minimum(u_hi, ts + CLOSE_R) - np.maximum(u_lo, ts - CLOSE_R), 0, None))
                 / (u_hi - u_lo))


def shockwave_gamma(row):
    d_min, d_max = num(row, "swFlipDelayMinSec", 1.0), num(row, "swFlipDelayMaxSec", 3.5)
    w = num(row, "swWindowSec", 0.9)
    spread = d_max - d_min
    return min(1.0, max(0.0, w / spread)) if spread > 1e-4 else 1.0


def alt_at(base, direction, diff_ms):
    """StudyConfig.AlteredFpsAt: a threshold (ms) as the altered side's frame rate, unrounded."""
    ms = 1000.0 / base + (-diff_ms if direction == "up" else diff_ms)
    return 1000.0 / ms if ms > 0 else math.nan


# ------------------------------------------------------------------ config

def load_rows(path):
    if path is None:
        text = [",".join(CONFIG_COLUMNS)] + DEFAULT_ROWS
    else:
        text = [ln for ln in Path(path).read_text(encoding="utf-8").splitlines()
                if ln.strip() and not ln.lstrip().startswith("#")]
    rows = list(csv.reader(text))
    header = [h.strip() for h in rows[0]]
    out = []
    for cells in rows[1:]:
        r = {h: (cells[i].strip() if i < len(cells) else "") for i, h in enumerate(header)}
        out.append(r)
    return header, out


def num(r, k, d):
    try:
        return float(r.get(k, ""))
    except ValueError:
        return d


def williams(n):
    """Balanced Latin square (Williams): n rows for even n, 2n (the square and its mirror) for odd n."""
    base = [0]
    lo, hi = 1, n - 1
    for i in range(1, n):
        base.append(lo if i % 2 else hi)
        lo, hi = (lo + 1, hi) if i % 2 else (lo, hi - 1)
    sq = [[(b + r) % n + 1 for b in base] for r in range(n)]
    return sq if n % 2 == 0 else sq + [list(reversed(row)) for row in sq]


# ------------------------------------------------------------------ one session

class Session:
    def __init__(self, sid, rng, header, rows, max_trials, folder, start):
        self.sid, self.rng, self.header, self.rows = sid, rng, header, rows
        self.run = f"SIM{sid}"
        self.max_trials = max_trials
        self.folder = folder
        self.clock = start
        self.square = williams(len(rows))
        self.latin_row = (sid - 1) % len(self.square) + 1
        self.order = self.square[self.latin_row - 1]
        self.order_text = ";".join(map(str, self.order))
        self.shots, self.frames, self.sessions = [], [], []
        self.main_round = 0
        self.ufo_x, self.ufo_y, self.cam_x = 0.0, 7.0, 0.0
        self.tau = 0.0
        self.skill = float(np.exp(rng.normal(0, 0.25)))
        self.speed_pref = float(rng.uniform(10, 22))
        self.mouse = np.array([960.0, 540.0])

    def threshold(self, level, base, direction):
        """The observer's threshold in ms of frame time: the Weber fraction of the base rate, converted."""
        w = WEBER[level] * self.skill * (UP_FACTOR if direction == "up" else 1.0)
        fps = max(1.0, w * base)
        alt = base + fps if direction == "up" else max(1.0, base - fps)
        return abs(1000.0 / alt - 1000.0 / base)

    # one phase: practice (pairs) or a main block (QUEST+)
    def phase(self, row, block_index, ordinal, practice):
        lv = row["levelType"] or "manual"
        direction = (row.get("direction") or "down").lower()
        limiter = row.get("frameLimiter") or "unity"
        self.phase_start = self.clock
        self.phase_iso = iso(self.clock)
        self.t = 0.0                   # phase clock
        self.frame_index = 0
        self.trail = []                # (t, x) of logged frames
        self.cur_round = (0 if practice else self.main_round) + 1
        self.score = 0.0
        self.lv, self.row, self.practice = lv, row, practice
        self.id_cells = dict(sessionId=str(self.sid), runId=self.run, blockIndex=str(block_index),
                             blockOrdinal=str(ordinal), latinRow=str(self.latin_row), latinOrder=self.order_text,
                             direction="down" if practice else direction, frameLimiter=limiter, levelType=lv)
        cfg_vals = {f"cfg_{h}": ("" if practice else row.get(h, "")) for h in self.header}
        self.cfg_vals = cfg_vals
        cap_crossings = int(num(row, "maxCrossingsPerRound", 15))
        timeout = num(row, "roundTimeoutSec", 15)
        hit_pts, miss_pts = num(row, "hitPoints", 100), num(row, "missPoints", -10)
        shots_here, frames_start = [], len(self.frames)
        crossing_speeds = []
        # the prompt before the phase runs at its idle rate: the first pair's base, or the block's
        self.prev_cap = PRACTICE[0][1] if practice else int(num(row, "baseFps", 60))
        self.prev_pace = ""
        if practice:
            pairs = PRACTICE
            post = None
        else:
            cfg_row = {f"cfg_{k}": v for k, v in row.items()}
            grid = Grid.from_cfg(cfg_row)
            if self.max_trials:
                grid.max_trials = min(grid.max_trials, self.max_trials)
                grid.min_trials = min(grid.min_trials, grid.max_trials)
            gamma = num(row, "guessRate", 0.235)
            post = Posterior(grid, gamma)
            prior_sd = post.light()["sd"]
            base = int(num(row, "baseFps", 60))
            theta = self.threshold(lv, base, direction)
            pairs = None
        k = 0
        end_reason = "maxTrials"
        while True:
            if practice:
                if k >= len(pairs):
                    break
                lo, hi = pairs[k]
                base_r, alt_r, diff = hi, lo, hi - lo
                p_det = 0.95
            else:
                x_ms = post.next_stimulus()
                base_r = int(num(row, "baseFps", 60))
                alt_r = altered_fps_for(base_r, direction, x_ms)
                diff = abs(alt_r - base_r)
                p_det = (1 - 0.02) * (1 - math.exp(-((x_ms / theta) ** 2.5)))
            res = self.play_round(base_r, alt_r, diff, p_det, timeout, cap_crossings, k == 0, crossing_speeds)
            self.score += hit_pts if res["isHit"] else miss_pts
            res["totalScore"] = self.score
            if post is not None:
                post.update(x_ms, res["isHit"])
                li = post.light()
                res.update(threshDiffMs=li["theta"], threshDiffFps=abs(alt_at(base_r, direction, li["theta"]) - base_r),
                           sd=li["sd"], slopeEstimate=li["beta"], lapseEstimate=li["lam"])
                n = len(shots_here) + 1
                if n >= grid.max_trials:
                    end_reason = "maxTrials"
                elif grid.stop_sd > 0 and n >= grid.min_trials and li["sd"] <= grid.stop_sd:
                    end_reason = "converged"
                else:
                    end_reason = None
            shots_here.append(res)
            k += 1
            if post is not None and end_reason is not None:
                break
        # the phase's log ends on its last response frame (ExperimentDirector.EndPhase closes it the frame after)
        if not practice:
            self.main_round = self.cur_round - 1
            self.sessions.append(self.session_row(row, shots_here, frames_start, end_reason, post, prior_sd,
                                                  crossing_speeds))
        self.clock = self.phase_start + timedelta(seconds=self.t + 1.0)

    # ---- frames
    def emit(self, base, cap, pace, diff, alt_side, side, crossed, tower_x, dmouse, live, fired=None, click=False,
             sw_fast=False, sw_win=False, sw_evt=0):
        """One frame row. `cap`/`pace` are what this frame's end chooses for the NEXT frame; the row itself
        carries the previous choice (targetFps, pacedBy), the one this frame's duration measures."""
        rng = self.rng
        target = self.prev_cap if self.prev_cap else cap
        dt = 1.0 / target + rng.normal(0, 2e-5)
        if rng.random() < 4e-4:
            dt *= rng.uniform(1.6, 3.0)
        dt = max(dt, 1e-4)
        if self.frame_index > 0:
            self.t += dt
        self.mouse = np.clip(self.mouse + dmouse, 0, [1919, 1079])
        row = dict(self.id_cells)
        row.update(baseFps=str(base), phase="practice" if self.practice else "main", phaseStartIso=self.phase_iso,
                   roundNumber=str(self.cur_round), frameIndex=str(self.frame_index),
                   timeSinceStartSec=F(self.t, 5), unscaledDeltaMs=F(dt * 1000, 4), targetFps=str(target),
                   pacedBy=self.prev_pace, diffFps=str(diff) if alt_side else "", alteredSide=alt_side, side=side,
                   crossed=B(crossed), shockwaveFastPhase=B(sw_fast), shockwaveWindow=B(sw_win),
                   shockwaveFlips=str(sw_evt), mouseX=F(self.mouse[0], 2), mouseY=F(self.mouse[1], 2),
                   mouseDeltaX=F(dmouse[0], 3), mouseDeltaY=F(dmouse[1], 3), ufoX=F(self.ufo_x, 4),
                   ufoY=F(self.ufo_y, 4), towerX=F(tower_x, 4), leftButtonDown=B(click),
                   leftButtonPressed=B(click), fireKeyDown="0", shotFired=B(fired is not None),
                   shotHitX=F(fired if fired is not None else 0.0, 4), camX=F(self.cam_x, 4))
        self.frames.append(row)
        self.trail.append((self.t, self.ufo_x))
        self.frame_index += 1
        self.prev_cap, self.prev_pace = cap, pace
        return dt

    def trail_velocity(self, upto=None):
        tr = self.trail if upto is None else self.trail[:upto]
        if len(tr) < 2:
            return math.nan
        t1, x1 = tr[-1]
        for t0, x0 in reversed(tr[:-1]):
            if t1 - t0 >= 0.1:
                break
        return (x1 - x0) / (t1 - t0) if t1 - t0 > 1e-5 else math.nan

    def gap(self, base, seconds=GAP_SEC):
        """The reveal and the reset after a response: the round's base rate, no split, the UFO still."""
        n = max(1, int(seconds * base))
        for _ in range(n):
            self.emit(base, base, "", 0, "", self.side_cell, False, self.tower_x_log, (0.0, 0.0), False)

    def play_round(self, base, alt, diff, p_det, timeout, cap_crossings, first, crossing_speeds):
        rng, lv = self.rng, self.lv
        if not first:
            self.gap(self.prev_base)          # the last round's reveal and reset, at its own base
        # the reset: the camera pans (still levels; the UFO keeps its place on screen), the boundary is dealt
        if lv != "scroll":
            old = self.cam_x
            self.cam_x = float(rng.uniform(-PAN, PAN))
            self.ufo_x += self.cam_x - old
        else:
            self.cam_x = self.ufo_x
        bx = self.cam_x + float(rng.uniform(-SPAWN * VIEW_W / 2, SPAWN * VIEW_W / 2))
        altered = "Left" if rng.random() < 0.5 else "Right"
        if lv in ("auto", "shockwave"):
            sweep = num(self.row, "autoSweep", 0.9) if not self.practice else 0.9
            self.auto = dict(mode=(self.row.get("autoPath") or "linear"), v=num(self.row, "autoSpeed", 8),
                             turn=num(self.row, "autoTurnSec", 0.6), sweep=sweep,
                             height=num(self.row, "autoHeight", 0.4), amp=num(self.row, "autoBobAmp", 0.5),
                             hz=num(self.row, "autoBobHz", 0.4))
            a = self.auto
            width = a["sweep"] * VIEW_W
            self.tau = float(rng.uniform(0, auto_period(a["mode"], a["v"], a["turn"], width)))
            self.place_auto()
        else:
            self.ufo_x = float(np.clip(self.ufo_x, self.cam_x - VIEW_W / 2, self.cam_x + VIEW_W / 2)) \
                if lv == "manual" else self.ufo_x

        def nearest(x):
            if lv != "scroll":
                return bx, False
            kk = round((x - bx) / PERIOD)
            return bx + kk * PERIOD, kk % 2 != 0

        def along(x):
            tx, flip = nearest(x)
            return -(x - tx) if flip else (x - tx)

        side_sign = 1 if along(self.ufo_x) >= 0 else -1

        def cells(x):
            tx, flip = nearest(x)
            s = "Right" if side_sign > 0 else "Left"
            if flip:
                s = "Left" if s == "Right" else "Right"
            a_s = altered if not flip else ("Left" if altered == "Right" else "Right")
            return tx, s, a_s

        sw_alt_phase = False
        sw_win_closes = 0.0
        sw_evt = 0
        sw_next_flip = math.inf

        def cap_now():
            if lv == "shockwave":
                return (alt, "altered") if sw_alt_phase else (base, "base")
            on_alt = (side_sign > 0) == (altered == "Right")
            return (alt, "altered") if on_alt else (base, "base")

        # GO!: the split opens, the UFO holds still
        go_frames = 0
        go_t0 = self.t
        f_go = len(self.frames)
        cap, pace = cap_now()
        while self.t - go_t0 < GO_SEC:
            tx, s, a_s = cells(self.ufo_x)
            self.emit(base, cap, pace, diff, a_s, s, False, tx, rng.normal(0, 0.2, 2), True)
            cap, pace = cap_now()
            go_frames += 1
        arm_t = self.t + 1e-4
        x_start = self.ufo_x
        detect = rng.random() < p_det
        crossings, first_cross, last_cross = 0, math.nan, math.nan
        speeds = []
        # the plan for when to respond
        rt = float(rng.lognormal(math.log(0.45), 0.25))
        passes = 1 + min(1, int(rng.poisson(0.4)))     # usually the first pass, sometimes the second
        guess_at = float(rng.uniform(1.0, max(1.5, timeout - 1.0)))
        give_up = rng.random() < 0.03
        aim = float(np.clip(rng.normal(0, 2.0), -5.0, 5.0))
        fire_at = math.nan
        # manual / scroll flight: sweeps between turn points around the boundary
        speed = self.speed_pref * float(rng.uniform(0.8, 1.2))
        heading = 1.0 if self.ufo_x < bx else -1.0
        # turn points either side of the boundary, inside the view (a still camera's edge stops the UFO)
        lo_lim = self.cam_x - VIEW_W / 2 + 1.0 if lv == "manual" else -1e9
        hi_lim = self.cam_x + VIEW_W / 2 - 1.0 if lv == "manual" else 1e9

        def turn_point(sign):
            return float(np.clip(bx + sign * rng.uniform(4, 16), lo_lim, hi_lim))
        turn_l, turn_r = turn_point(-1), turn_point(+1)
        outcome, fired_x = None, None
        vel = 0.0
        prev_off = None
        while True:
            el = self.t - arm_t
            if timeout > 0 and el >= timeout:
                outcome = "expired"
            elif cap_crossings > 0 and crossings > cap_crossings:
                outcome = "crossingCap"
            # decide to fire on this frame?
            fire = False
            if outcome is None:
                if lv in ("auto", "shockwave"):
                    if detect and not give_up and crossings >= passes and not math.isnan(last_cross):
                        fire = self.t - last_cross >= rt
                    elif (not detect) and not give_up:
                        fire = el >= guess_at
                else:
                    if detect and not give_up and crossings >= passes:
                        # fire as the UFO passes the point the participant aims at (it can step over any
                        # fixed window in one frame at a low rate)
                        tx, _, _ = cells(self.ufo_x)
                        off = self.ufo_x - (tx + aim)
                        fire = prev_off is not None and (off == 0 or (off > 0) != (prev_off > 0))
                        prev_off = off
                    elif (not detect) and not give_up:
                        fire = el >= guess_at
            if outcome is not None or fire:
                break
            # move
            dt_est = 1.0 / cap
            if lv in ("auto", "shockwave"):
                self.tau += dt_est
                old = self.ufo_x
                self.place_auto()
                vel = (self.ufo_x - old) / dt_est
                dm = rng.normal(0, 0.3, 2)
            else:
                if heading > 0 and self.ufo_x >= turn_r:
                    heading, turn_r = -1.0, turn_point(+1)
                elif heading < 0 and self.ufo_x <= turn_l:
                    heading, turn_l = 1.0, turn_point(-1)
                vel += (heading * speed - vel) * min(1.0, dt_est * 6.0)
                old = self.ufo_x
                self.ufo_x += vel * dt_est
                if lv == "manual":
                    self.ufo_x = float(np.clip(self.ufo_x, self.cam_x - VIEW_W / 2, self.cam_x + VIEW_W / 2))
                else:
                    self.cam_x = self.ufo_x
                self.ufo_y = float(np.clip(self.ufo_y + rng.normal(0, 0.02), FLOOR_Y, CEIL_Y))
                dm = np.array([(self.ufo_x - old) * COUNTS_PER_UNIT, rng.normal(0, 0.5)])
            # side, crossing (dead zone)
            a = along(self.ufo_x)
            new = side_sign
            if a > DEAD_ZONE:
                new = 1
            elif a < -DEAD_ZONE:
                new = -1
            crossed = new != side_sign
            side_sign = new
            tx, s, a_s = cells(self.ufo_x)
            self.emit(base, cap, pace, diff, a_s, s, crossed, tx, dm, True)
            if crossed:
                crossings += 1
                last_cross = self.t
                if crossings == 1:
                    first_cross = self.t
                v = abs(self.trail_velocity())
                if math.isfinite(v):
                    speeds.append(v)
                    crossing_speeds.append(v)
            cap, pace = cap_now()
        # the response frame: the split closes in its Update, so its row has no diff / altered side, and the
        # frame after it is paced by the base with no split (pacedBy blank)
        vel_resp = self.trail_velocity()      # over the frames before this one, as the game's trail is
        played = outcome is None
        tx, s_plain, a_s = cells(self.ufo_x)
        if lv == "shockwave":
            tx, s_plain, a_s = bx, "Left", altered
        if played:
            fired_x = self.ufo_x
            if lv == "shockwave":
                if sw_win_closes > 0 and self.t < sw_win_closes:
                    outcome = "detected"
                elif sw_evt == 0:
                    outcome = "early"
                else:
                    outcome = "late"
            else:
                outcome = "shot"
        self.emit(base, base, "", diff, "", s_plain, False, tx, (0.0, 0.0), True,
                  fired=fired_x if played else self.ufo_x, click=played)
        fire_t = self.t + 1e-4
        frames = self.frames[f_go:]
        meas = {}
        for kind in ("base", "altered"):
            ds = [float(f["unscaledDeltaMs"]) / 1000 for f in frames if f["pacedBy"] == kind]
            meas[kind] = (len(ds) / sum(ds) if ds else math.nan, sum(ds))
        tower_x, flip = nearest(self.ufo_x)
        alt_side = altered if not flip else ("Left" if altered == "Right" else "Right")
        miss = math.nan if (not played or lv == "shockwave") else (fired_x - tower_x)
        is_hit = (outcome == "detected") if lv == "shockwave" else (played and abs(miss) <= CLOSE_R)
        ref_x = fired_x if played else self.ufo_x
        res = dict(self.id_cells)
        res.update(
            baseFps=base, closeRadius=CLOSE_R, phase="practice" if self.practice else "main",
            phaseStartIso=self.phase_iso, roundNumber=self.cur_round, timeSinceStartSec=self.t, diffFps=diff,
            diffMs=abs(1000.0 / alt - 1000.0 / base),
            alteredFps=alt, alteredSide=alt_side, measuredBaseFps=meas["base"][0], measuredAlteredFps=meas["altered"][0],
            baseSideSec=meas["base"][1], alteredSideSec=meas["altered"][1], lateFrames=0, crossings=crossings,
            lastCrossingAtSec=last_cross, sinceLastCrossingSec=(fire_t - last_cross) if played else math.nan,
            outcome=outcome, playerFired=played, isHit=is_hit, countedByStaircase=not self.practice,
            trialStartSec=arm_t, firedAtSec=fire_t if played else math.nan,
            roundDurationSec=(fire_t if played else self.t) - arm_t, hitX=fired_x if played else math.nan,
            missDistX=miss, towerX=tower_x, ufoY=self.ufo_y,
            side=("Right" if (ref_x - tower_x) >= 0 else "Left"),
            threshDiffFps=math.nan, threshDiffMs=math.nan, sd=math.nan, slopeEstimate=math.nan, lapseEstimate=math.nan,
            camX=self.cam_x, ufoViewportX=0.5 + (ref_x - self.cam_x) / VIEW_W,
            towerViewportX=0.5 + (tower_x - self.cam_x) / VIEW_W, ufoVelX=vel_resp, ufoXAtStart=x_start,
            firstCrossingAtSec=first_cross, meanCrossingSpeed=float(np.mean(speeds)) if speeds else math.nan,
            lastCrossingSpeed=speeds[-1] if speeds else math.nan, _speeds=speeds)
        self.tower_x_log, self.side_cell = tower_x, s_plain
        self.cur_round += 1
        self.prev_base = base
        self.shots.append(res)
        return res

    def place_auto(self):
        a = self.auto
        width = a["sweep"] * VIEW_W
        xa = self.cam_x - VIEW_W / 2 + (0.5 - a["sweep"] / 2) * VIEW_W
        x, _ = auto_sample(a["mode"], a["v"], a["turn"], width, self.tau)
        self.ufo_x = xa + x
        y0 = FLOOR_Y + a["height"] * (CEIL_Y - FLOOR_Y)
        self.ufo_y = float(np.clip(y0 + a["amp"] * math.sin(2 * math.pi * a["hz"] * self.tau), FLOOR_Y, CEIL_Y))

    # ---- the block's session row (SessionStats.cs)
    def session_row(self, row, shots, f0, end_reason, post, prior_sd, crossing_speeds):
        fr = self.frames[f0:]
        base = int(num(row, "baseFps", 60))
        direction = (row.get("direction") or "down").lower()
        sm = post.summary()
        jnd = sm["theta"]
        alt_j = alt_at(base, direction, jnd)

        def diff_fps(ms):
            return abs(alt_at(base, direction, ms) - base)

        fired = [s for s in shots if s["playerFired"]]
        miss_abs = [abs(s["missDistX"]) for s in fired if math.isfinite(s["missDistX"])]
        misses = [abs(s["missDistX"]) for s in fired if not s["isHit"] and math.isfinite(s["missDistX"])]
        dts = np.array([float(f["unscaledDeltaMs"]) for f in fr])
        tgt = np.array([float(f["targetFps"]) for f in fr])
        paced = np.array([f["pacedBy"] for f in fr])
        period = 1000.0 / tgt
        b_m, a_m = paced == "base", paced == "altered"
        live = b_m | a_m
        mdx = np.array([float(f["mouseDeltaX"]) for f in fr])
        mdy = np.array([float(f["mouseDeltaY"]) for f in fr])
        px = np.hypot(mdx, mdy)
        ux = np.array([float(f["ufoX"]) for f in fr])
        uy = np.array([float(f["ufoY"]) for f in fr])
        play = float(fr[-1]["timeSinceStartSec"]) if fr else math.nan
        along = [s["missDistX"] * math.copysign(1, s["ufoVelX"]) for s in fired
                 if math.isfinite(s["missDistX"]) and math.isfinite(s["ufoVelX"]) and abs(s["ufoVelX"]) > 0.5]
        since = [s["sinceLastCrossingSec"] for s in fired if math.isfinite(s["sinceLastCrossingSec"])]
        firsts = [s["firstCrossingAtSec"] - s["trialStartSec"] for s in shots if math.isfinite(s["firstCrossingAtSec"])]
        lv = row["levelType"] or "manual"
        g_screen = (shockwave_gamma(row) if lv == "shockwave" else
                    auto_gamma(num(row, "autoSweep", 0.9), row.get("autoPath") or "linear", num(row, "autoSpeed", 8),
                               num(row, "autoTurnSec", 0.6)) if lv == "auto" else manual_gamma())
        start = self.phase_start
        end = start + timedelta(seconds=play + 0.2)

        def sd(v):
            return float(np.std(v, ddof=1)) if len(v) > 1 else math.nan

        r = dict(self.id_cells)
        r.update(
            baseFps=str(base), displayHz="500.00", resolution="1920x1080", startIso=iso(start), endIso=iso(end),
            sessionDurationSec=F((end - start).total_seconds(), 3), playDurationSec=F(play, 3), endReason=end_reason,
            jndDiffFps=F(diff_fps(jnd), 3), jndAlteredFps=F(alt_j, 3), jndFrameTimeDiffMs=F(jnd, 4), sd=F(sm["sd"], 3),
            priorSd=F(prior_sd, 3), jndMedianDiffFps=F(diff_fps(sm["median_cs"]), 3),
            jndModeDiffFps=F(diff_fps(sm["mode"]), 3), jndCI95LoDiffFps=F(diff_fps(sm["ci_lo_cs"]), 3),
            jndCI95HiDiffFps=F(diff_fps(sm["ci_hi_cs"]), 3), jndMedianDiffMs=F(sm["median_cs"], 4),
            jndModeDiffMs=F(sm["mode"], 4), jndCI95LoDiffMs=F(sm["ci_lo_cs"], 4), jndCI95HiDiffMs=F(sm["ci_hi_cs"], 4),
            jndRangeEdge=sm["range_edge"],
            slopeEstimate=F(sm["beta"], 3), lapseEstimate=F(sm["lam"], 4), staircaseTrials=str(len(shots)),
            lastDiffFps=F(shots[-1]["diffFps"], 0), lastDiffMs=F(shots[-1]["diffMs"], 3), trials=str(len(shots)),
            shotsFired=str(len(fired)),
            hits=str(sum(s["isHit"] for s in shots)), accuracy=F(sum(s["isHit"] for s in shots) / len(shots), 4),
            score=F(self.score, 2), roundsExpired=str(sum(s["outcome"] in ("expired", "timeout") for s in shots)),
            roundsCrossingCapped=str(sum(s["outcome"] == "crossingCap" for s in shots)),
            avgRoundDurationSec=F(float(np.mean([s["roundDurationSec"] for s in shots])), 3),
            avgCrossingsPerRound=F(float(np.mean([s["crossings"] for s in shots])), 3),
            shotsWithoutCrossing=str(sum(1 for s in fired if s["crossings"] == 0)),
            avgSinceLastCrossingSec=F(float(np.mean(since)) if since else math.nan, 4),
            avgMissDistX=F(float(np.mean(miss_abs)) if miss_abs else math.nan, 4),
            avgMissDistXMissesOnly=F(float(np.mean(misses)) if misses else math.nan, 4),
            medianMissDistX=F(float(np.median(miss_abs)) if miss_abs else math.nan, 4),
            sdMissDistX=F(sd(miss_abs), 4), maxMissDistX=F(max(miss_abs) if miss_abs else math.nan, 4),
            totalMousePathPx=F(float(px.sum()), 2), avgMouseSpeedPxPerSec=F(float(px.sum()) / play, 2),
            peakMouseSpeedPxPerSec=F(float(np.max(px / (dts / 1000))), 2),
            totalUfoPathWorld=F(float(np.hypot(np.diff(ux), np.diff(uy)).sum()), 4), frameCount=str(len(fr)),
            liveFrames=str(int(live.sum())), baseSideFrames=str(int(b_m.sum())),
            baseSideMeasuredFps=F(b_m.sum() / (dts[b_m].sum() / 1000) if b_m.any() else math.nan, 3),
            baseFrameTimeSdMs=F(sd(dts[b_m] - period[b_m]), 4), alteredSideFrames=str(int(a_m.sum())),
            alteredFrameTimeErrPct=F(100 * float(np.mean((dts[a_m] - period[a_m]) / period[a_m])) if a_m.any()
                                     else math.nan, 3),
            alteredFrameTimeSdMs=F(sd(dts[a_m] - period[a_m]), 4),
            slowFrames=str(int((live & (dts > 1.5 * period)).sum())),
            maxLiveFrameTimeMs=F(float(dts[live].max()) if live.any() else 0.0, 3), viewWidthWorld=F(VIEW_W, 4),
            guessRateScreen=F(g_screen, 4), avgFirstCrossingSec=F(float(np.mean(firsts)) if firsts else math.nan, 4),
            avgCrossingSpeed=F(float(np.mean(crossing_speeds)) if crossing_speeds else math.nan, 3),
            avgShotUfoSpeed=F(float(np.mean([abs(s["ufoVelX"]) for s in fired if math.isfinite(s["ufoVelX"])]))
                              if fired else math.nan, 3),
            medianSinceLastCrossingSec=F(float(np.median(since)) if since else math.nan, 4),
            avgMissAlongTravelX=F(float(np.mean(along)) if along else math.nan, 4))
        r.update(self.cfg_vals)
        return r

    # ---- the whole session
    def run_all(self):
        practised = set()
        for ordinal, idx in enumerate(self.order, start=1):
            row = self.rows[idx - 1]
            lv = row["levelType"] or "manual"
            if lv not in practised:
                practised.add(lv)
                self.phase(row, 0, 0, practice=True)
            self.phase(row, idx, ordinal, practice=False)

    def write(self):
        self.folder.mkdir(parents=True, exist_ok=True)
        tag = f"{self.sid}_{self.run}"
        cfg_cols = [f"cfg_{h}" for h in self.header]

        def write(name, cols, rows, fmt):
            with (self.folder / f"{name}_{tag}.csv").open("w", encoding="utf-8", newline="") as fh:
                fh.write(",".join(cols) + "\n")
                for r in rows:
                    fh.write(",".join(fmt(c, r.get(c, "")) for c in cols) + "\n")

        decimals = dict(timeSinceStartSec=4, measuredBaseFps=2, measuredAlteredFps=2, baseSideSec=3,
                        alteredSideSec=3, lastCrossingAtSec=4, sinceLastCrossingSec=4, totalScore=2,
                        trialStartSec=4, firedAtSec=4, roundDurationSec=4, hitX=4, missDistX=4, towerX=4, ufoY=4,
                        threshDiffFps=3, threshDiffMs=4, diffMs=3, sd=3, slopeEstimate=3, lapseEstimate=4, camX=4, ufoViewportX=4,
                        towerViewportX=4, ufoVelX=3, ufoXAtStart=4, firstCrossingAtSec=4, meanCrossingSpeed=3,
                        lastCrossingSpeed=3, closeRadius=3)

        def shot_fmt(c, v):
            if isinstance(v, bool):
                return B(v)
            if isinstance(v, (int, np.integer)):
                return str(int(v))
            if isinstance(v, float):
                return F(v, decimals.get(c, 3))
            return str(v)

        cfg_main = {r["blockIndex"]: r for r in self.sessions}
        for s in self.shots:
            for c in cfg_cols:
                s[c] = "" if s["phase"] == "practice" else cfg_main.get(s["blockIndex"], {}).get(c, "")
        write("SessionLog", SESSION_COLS + cfg_cols, self.sessions, lambda c, v: str(v))
        write("ShotLog", SHOT_COLS + cfg_cols, self.shots, shot_fmt)
        write("PlayerLog", PLAYER_COLS, self.frames, lambda c, v: str(v))
        with (self.folder / f"SessionInfo_{tag}.csv").open("w", encoding="utf-8", newline="") as fh:
            fh.write("key,value\n")
            for k, v in (("session.sessionId", self.sid), ("session.runId", self.run), ("session.simulated", "true"),
                         ("app.isEditor", "false"), ("display.refreshHz", "500"), ("display.width", "1920"),
                         ("display.height", "1080"), ("UIManager.showDebug", "false"),
                         ("CameraRig.parallax", "true"), ("Weather.kind", "Rain"),
                         ("GameManager.worldScrollPeriod", "96")):
                fh.write(f"{k},{v}\n")
        with (self.folder / "ExperimentConfig_used.csv").open("w", encoding="utf-8", newline="") as fh:
            fh.write(",".join(self.header) + "\n")
            for r in self.rows:
                fh.write(",".join(r.get(h, "") for h in self.header) + "\n")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True, type=Path, help="Folder to write the session folders into")
    ap.add_argument("--participants", type=int, default=4)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--config", type=Path, help="An ExperimentConfig.csv to simulate (default: a 6-row mix of "
                                                "all three levels and both directions)")
    ap.add_argument("--max-trials", type=int, default=24,
                    help="Cap on main rounds per block, for speed (0 = the config's maxTrials). Default 24.")
    args = ap.parse_args(argv)
    header, rows = load_rows(args.config)
    if args.max_trials:
        for r in rows:
            mt = int(num(r, "maxTrials", 50))
            if mt > args.max_trials:
                r["maxTrials"] = str(args.max_trials)
                r["minTrials"] = str(min(int(num(r, "minTrials", 8)), args.max_trials))
    for h in CONFIG_COLUMNS:
        if h not in header:
            header.append(h)
            for r in rows:
                r.setdefault(h, "")
    start = datetime(2026, 11, 2, 10, 0, 0)
    for i in range(1, args.participants + 1):
        rng = np.random.default_rng(args.seed * 1000 + i)
        sid = 100 + i
        s = Session(sid, rng, header, rows, args.max_trials, args.out / f"{sid}_SIM{sid}",
                    start + timedelta(hours=i))
        s.run_all()
        s.write()
        print(f"  {s.folder.name}: {len(s.sessions)} block(s), {len(s.shots)} rounds, {len(s.frames)} frames, "
              f"order {s.order_text}")
    print(f"Done -> {args.out}")


if __name__ == "__main__":
    main()
