"""
Audit the frame-rate study's CSV logs: structure, internal consistency, and agreement
between the three files of a session.

    python Analysis/audit_logs.py                       # newest session under Data/Logs
    python Analysis/audit_logs.py 8_vvK5                # a session by name (under --data)
    python Analysis/audit_logs.py "I:/JND/1_EHux - Lab PC"   # a session folder by path
    python Analysis/audit_logs.py --all                 # every session, one summary line each
    python Analysis/audit_logs.py --all --data "I:/JND - Framerate/Pre Pilot"
    python Analysis/audit_logs.py 8_vvK5 --verbose      # every message, not the first few per check

Options
    folder ...        session folders (paths, or names under the logs root)
    --data ROOT       a Data folder (sessions under ROOT/Logs) or a folder that holds session
                      folders directly. Default: <repo>/Data
    --all             every session under --data, one summary line each (add --verbose for
                      the messages)
    --verbose         print every message (default: the first 6 of each check)
    --level-type L    manual | scroll | auto: the level to assume for logs written before
                      levelType existed, instead of inferring it (see LEVEL INFERENCE)

STANDARD LIBRARY ONLY: it has to run on a lab PC with a bare Python 3.6+.

Exit code 1 if any check FAILS in any session audited. WARN does not fail: a WARN is either a
known, quantified gap in what the game logs, or data that is logged correctly but should not
be trusted as it stands (a frame rate the machine did not deliver, a display slower than the
rates tested, a non-16:9 screen the chance rate was not computed for). NOTE is information.
Every message names the session, the file, its row number(s) and the values. Row numbers are
LINE numbers in the file: the header is row 1, the first data row is row 2.

------------------------------------------------------------------------------------------
LOG GENERATIONS  (classified from the headers; checks whose columns are absent are skipped)

  stutter   the stutter study that came before (RoundLog files, stimulusMs / weapon /
            spikeFired columns) - numbered folders, and some <id>_<runId> folders too.
            Skipped with a one-line note.
  v1        frame-rate, practice ladder per block (cfg_practiceDiffsFps), cfg_stimCount,
            no jndRangeEdge. Practice rows carry their block's identity and base.
  v2        as v1 with cfg_diffStepFps and jndRangeEdge.
  v3        practice from Data/PracticeRounds.csv: one practice phase before MAIN 1 under
            blockIndex 0 / blockOrdinal 0, blank cfg_*, each round's own baseFps (the pair's
            higher rate). No levelType.
  v4        SPEC section 4: levelType in all three logs, the extended SessionLog / ShotLog
            columns, camX in the PlayerLog, one practice phase per level type, blank QUEST+
            columns on practice rows.

LEVEL INFERENCE (logs without levelType). scroll if the PlayerLog's ufoX spans more than
the still camera can show - its view width at the play plane W = 2*100*tan(7.5 deg)*aspect
(46.81 on 16:9) plus the +-6 pan between rounds plus 1 - or if the tower X of a round's live
frames jumps (the scrolling world reports the boundary copy nearest the UFO, so it jumps by
a period). Main-phase frames are used when there are any, else all frames. The aspect comes
from the SessionLog's resolution (16:9 assumed without one). SPEC.md's fixed 60 is this
limit for 16:9 only: a 21:9 screen (W = 62.9) can span ~75 without scrolling. Reports mark
an inferred level with '*'.

------------------------------------------------------------------------------------------
CHECKS

Structural (every file): each row has the header's column count; booleans are 0/1; numeric
columns parse; NaN/Infinity never written as text (NaN = an empty cell); enumerations hold
({practice,main}, {shot,expired,crossingCap}, {Left,Right}, {base,altered,''}, ...); UTF-8
without BOM and \\n line ends; identity columns (sessionId, runId) match the file name.

ShotLog, per row
  isHit == (playerFired and |missDistX| <= closeRadius)          tolerance 1e-3 at the edge
  missDistX == hitX - towerX                                     2e-4 (three 4-dp values)
  alteredFps == baseFps -/+ diffFps by direction (main; v1/v2 practice too)
  practice v3+: baseFps is the pair's higher rate: alteredFps < baseFps, diffFps = difference
  outcome vs playerFired: shot <=> 1; expired/crossingCap => 0, isHit 0, and blank hitX,
      missDistX, firedAtSec, sinceLastCrossingSec
  expired => roundDurationSec >= roundTimeoutSec; crossingCap => crossings = cap + 1; a shot
      never has more crossings than the cap
  countedByStaircase only on main rows (and on every main row)
  crossings == 0 <=> lastCrossingAtSec blank
  sinceLastCrossingSec == firedAtSec - lastCrossingAtSec          2e-4
  roundDurationSec == firedAtSec - trialStartSec (shots)          5e-4 (float32 realtime)
  0 <= firedAtSec - timeSinceStartSec < 0.1 s (the click lands inside the frame it is logged on)
  timeSinceStartSec never runs backwards in a phase; a round arms after the previous response
  roundNumber: +1 per row; practice restarts at 1 per practice phase; main continues across
      blocks without gaps
  totalScore is the running sum of hitPoints / missPoints within a phase
  main diffFps on the QUEST+ grid [minDiffFps, maxDiffFps] step diffStepFps
  side == Right iff hitX >= towerX (shots, |missDistX| > 1e-3)
  identity: baseFps/direction/frameLimiter == cfg_*; latinOrder[blockOrdinal] == blockIndex;
      v3+ practice: blockIndex 0, blockOrdinal 0, blank cfg_*
  QUEST+ columns: practice rows blank (v4) or unchanged through the practice phase (v1-v3,
      which repeat the prior)
  v4: firstCrossingAtSec <= lastCrossingAtSec, both blank iff no crossings; meanCrossingSpeed /
      lastCrossingSpeed blank iff no crossings; ufoViewportX / towerViewportX in [-0.05, 1.05]
      and == 0.5 + (x - camX)/viewWidthWorld (1e-3); levelType == cfg_levelType when set

PlayerLog, per phase (a phase = rows sharing phase + phaseStartIso)
  frameIndex contiguous from 0; timeSinceStartSec starts at 0 and increases; rows of one phase
  are contiguous; roundNumber steps by 0 or +1 and starts at the phase's first ShotLog round
  pacedBy blank <=> diffFps/alteredSide blank, except by design (see ExperimentDirector.
      LateUpdate / PerturbationController.Update): the frame a round's split opens (GO!) has
      alteredSide but no pacedBy yet, and the response frame has pacedBy (its period was paced
      by the round's cap) but no alteredSide (the split closed in that frame's Update). At most
      one of each per round, in those places.
  targetFps: pacedBy base => the round's baseFps, altered => its alteredFps, blank => the
      frame's baseFps (the idle cap)
  side consistent with sign(ufoX - towerX) whenever |ufoX - towerX| > 0.25 (the dead zone)
      + 1e-3; a live side change without crossed=1 is flagged (non-scroll)
  towerX constant over a round's live frames (non-scroll levels); scroll: changes only by
      whole multiples of the boundary period (inferred from the jumps; 1e-2)
  v4: camX constant over a round's live frames outside scroll levels; auto: UFO speed within
      the path's maximum on live frames

Cross-log, ShotLog vs PlayerLog per phase
  one shotFired frame per ShotLog row, carrying its roundNumber, at its timeSinceStartSec
      (1.5e-4: 4 vs 5 dp); shotHitX == hitX for fired rounds; the shot's frame shows a click
      or Space
  the frames of a round carry its diffFps, alteredSide (turned per boundary copy in scroll
      levels) and, on paced frames, its baseFps
  per round: sum of crossed == crossings; the last crossed frame starts before
      lastCrossingAtSec and the next frame after it
  measuredBaseFps / measuredAlteredFps == frames / seconds of unscaledDeltaMs over the round's
      frames grouped by pacedBy (0.05 fps or 0.05%), baseSideSec / alteredSideSec == the
      seconds (2 ms)
  KNOWN GAP, reported as WARN not FAIL: a phase's FIRST round opens its split in
      ExperimentDirector.PhaseStartingGun - before BeginPhase starts the frame log - so the GO!
      beat (readyBeatSec, ~0.5 s) is in that round's measured* / *SideSec and any crossing
      during it is in its crossings (lastCrossingAtSec can then be negative), with no frame
      rows to show for either. Quantified per phase.

SessionLog vs ShotLog / PlayerLog, per main block (matched on blockOrdinal)
  trials, shotsFired, hits, accuracy, score, roundsExpired, roundsCrossingCapped,
  avgRoundDurationSec, avgCrossingsPerRound, shotsWithoutCrossing, avgSinceLastCrossingSec,
  avg/median/sd/max miss distance, staircaseTrials == countedByStaircase rows, lastDiffFps,
  jndDiffFps / sd / slopeEstimate / lapseEstimate == the last round's posterior,
  jndAlteredFps == base -/+ jnd, jndFrameTimeDiffMs == |1000/alt - 1000/base|,
  frameCount, liveFrames, baseSideFrames, alteredSideFrames, baseSideMeasuredFps,
  baseFrameTimeSdMs, alteredFrameTimeErrPct, alteredFrameTimeSdMs, slowFrames,
  maxLiveFrameTimeMs, playDurationSec, totalMousePathPx, avgMouseSpeedPxPerSec,
  peakMouseSpeedPxPerSec, totalUfoPathWorld recomputed from the PlayerLog;
  endReason in {converged, maxTrials, abandoned} and consistent with staircaseTrials / sd;
  sessionDurationSec == endIso - startIso; startIso == the main phase's phaseStartIso.
  Tolerances: each value's own rounding plus the rounding of its inputs (see TOL below).

Delivery and setup (WARN, the logs are right but the stimulus or the setup was not):
  baseSideMeasuredFps < 97% of baseFps; alteredFrameTimeErrPct > 3%; slow frames > 1%;
  frame rates above the display's refresh rate; a non-16:9 screen; guessRate off the screen's.
"""

import argparse
import csv
import math
import re
import statistics
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DEFAULT_DATA = REPO / "Data"

# ------------------------------------------------------------------------- constants

DEAD_ZONE = 0.25            # StudyConfig.crossingDeadZone
CLOSE_RADIUS = 5.5          # StudyConfig.closeRadius (the logged closeRadius is used when present)
READY_BEAT_SEC = 0.5        # StudyConfig.readyBeatSec - the GO! beat
PAN_RANGE = 6.0             # GameManager.cameraPanRange
VIEW_W_PER_ASPECT = 2.0 * 100.0 * math.tan(math.radians(7.5))   # 26.33: W = this * aspect
EXAMPLES = 6                # messages shown per check without --verbose

TOL = {
    "edge": 1e-3,          # isHit at |missDistX| == closeRadius
    "x4": 2e-4,            # a difference of 4-dp values (each off by <= 5e-5)
    "time4": 2e-4,         # sinceLastCrossingSec vs firedAtSec - lastCrossingAtSec
    "dur": 5e-4,           # roundDurationSec vs firedAtSec - trialStartSec: float32 realtime
    "frameTime": 1.5e-4,   # a ShotLog time (4 dp) vs its PlayerLog frame (5 dp)
    "fpsAbs": 0.05,        # measured fps vs frames (ShotLog has 2 dp)
    "fpsRel": 5e-4,
    "sideSec": 2e-3,       # baseSideSec (3 dp) vs the sum of the frames' unscaledDeltaMs
    "deadZone": 1e-3,      # past the dead zone by more than this before side must agree
    "viewport": 1e-3,
    "period": 1e-2,        # scroll: towerX jumps are whole periods to within this
}

NAN_TOKENS = {"nan", "NaN", "NAN", "-nan", "+nan", "Infinity", "-Infinity", "+Infinity",
              "inf", "-inf", "+inf", "Inf", "-Inf", "\u221e", "-\u221e"}

BOOL_COLS = {"isHit", "playerFired", "countedByStaircase", "crossed", "leftButtonDown",
             "leftButtonPressed", "fireKeyDown", "shotFired"}

TEXT_COLS = {"runId", "latinOrder", "direction", "frameLimiter", "levelType", "phase",
             "phaseStartIso", "alteredSide", "side", "outcome", "pacedBy", "endReason",
             "resolution", "startIso", "endIso", "jndRangeEdge"}

INT_COLS = {"sessionId", "blockIndex", "blockOrdinal", "latinRow", "baseFps", "roundNumber",
            "diffFps", "alteredFps", "lateFrames", "crossings", "frameIndex", "targetFps",
            "staircaseTrials", "trials", "shotsFired", "hits", "roundsExpired",
            "roundsCrossingCapped", "shotsWithoutCrossing", "frameCount", "liveFrames",
            "baseSideFrames", "alteredSideFrames", "slowFrames"}

ENUMS = {
    "phase": {"practice", "main"},
    "outcome": {"shot", "expired", "crossingCap"},
    "side": {"Left", "Right"},
    "alteredSide": {"Left", "Right", ""},
    "pacedBy": {"", "base", "altered"},
    "direction": {"down", "up"},
    "frameLimiter": {"precise", "unity"},
    "levelType": {"manual", "scroll", "auto"},
    "endReason": {"converged", "maxTrials", "abandoned"},
    "jndRangeEdge": {"", "top", "bottom"},
}

QUEST_COLS = ("threshDiffFps", "sd", "slopeEstimate", "lapseEstimate")

GEN_LABEL = {
    "v1": "frame-rate v1 (practice ladder per block, cfg_stimCount)",
    "v2": "frame-rate v2 (practice ladder per block, cfg_diffStepFps)",
    "v3": "frame-rate v3 (PracticeRounds.csv pairs, before levelType)",
    "v4": "frame-rate v4 (levelType + extended columns)",
}


# ------------------------------------------------------------------------- small helpers

def fnum(s):
    """float, or None for an empty / unparseable / non-finite cell."""
    if s is None or s == "":
        return None
    try:
        v = float(s)
    except ValueError:
        return None
    if v != v or v in (float("inf"), float("-inf")):
        return None
    return v


def inum(s):
    v = fnum(s)
    return None if v is None else int(round(v))


def fmt(v, nd=4):
    if v is None:
        return "blank"
    if isinstance(v, float):
        if v == int(v) and abs(v) < 1e9 and nd <= 4:
            return str(int(v)) if abs(v - int(v)) < 1e-12 else ("%.*f" % (nd, v)).rstrip("0").rstrip(".")
        return ("%.*f" % (nd, v)).rstrip("0").rstrip(".")
    return str(v)


def close(a, b, tol):
    return a is not None and b is not None and abs(a - b) <= tol


def mean(v):
    return sum(v) / len(v) if v else None


def sample_sd(v):
    if len(v) < 2:
        return None
    m = sum(v) / len(v)
    return math.sqrt(sum((x - m) ** 2 for x in v) / (len(v) - 1))


def parse_iso(s):
    """ISO 8601 with up to 7 fractional digits and an offset (DateTime.ToString("o"))."""
    if not s:
        return None
    m = re.match(r"^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(?:\.(\d+))?(Z|[+-]\d\d:\d\d)?$", s)
    if not m:
        return None
    y, mo, d, hh, mm, ss, frac, tz = m.groups()
    us = int((frac or "0")[:6].ljust(6, "0"))
    tzinfo = None
    if tz == "Z":
        tzinfo = timezone.utc
    elif tz:
        sign = 1 if tz[0] == "+" else -1
        tzinfo = timezone(sign * timedelta(hours=int(tz[1:3]), minutes=int(tz[4:6])))
    return datetime(int(y), int(mo), int(d), int(hh), int(mm), int(ss), us, tzinfo)


def rows_text(lines, limit=8):
    lines = list(lines)
    if not lines:
        return "-"
    if len(lines) == 1:
        return "row %d" % lines[0]
    shown = ",".join(str(x) for x in lines[:limit])
    more = "" if len(lines) <= limit else ",... (%d rows)" % len(lines)
    return "rows %s%s" % (shown, more)


# ------------------------------------------------------------------------- findings

class Findings:
    LEVELS = ("FAIL", "WARN", "NOTE")

    def __init__(self, session):
        self.session = session
        self.items = []          # (level, check, file, rows, text)

    def add(self, level, check, file, rows, text):
        if isinstance(rows, int):
            rows = [rows]
        self.items.append((level, check, file or "-", list(rows or []), text))

    def fail(self, check, file, rows, text):
        self.add("FAIL", check, file, rows, text)

    def warn(self, check, file, rows, text):
        self.add("WARN", check, file, rows, text)

    def note(self, check, file, rows, text):
        self.add("NOTE", check, file, rows, text)

    def by_level(self, level):
        return [it for it in self.items if it[0] == level]

    def checks(self, level):
        out = {}
        for it in self.items:
            if it[0] == level:
                out[it[1]] = out.get(it[1], 0) + 1
        return out

    def print(self, verbose, limit=EXAMPLES):
        for level in self.LEVELS:
            groups = {}
            for it in self.items:
                if it[0] == level:
                    groups.setdefault(it[1], []).append(it)
            for check, items in groups.items():
                shown = items if verbose else items[:limit]
                for _, _, file, rows, text in shown:
                    print("  %s [%s] %s %s %s: %s" % (level, check, self.session, file,
                                                     rows_text(rows), text))
                if len(items) > len(shown):
                    print("  %s [%s] ... %d more like this (--verbose shows all)"
                          % (level, check, len(items) - len(shown)))


# ------------------------------------------------------------------------- reading

class Table:
    """One small CSV (SessionLog / ShotLog) as dict rows, each with its file line number."""

    def __init__(self, path, findings):
        self.path = path
        self.name = path.name
        self.header = []
        self.rows = []
        raw, lines = read_csv_checked(path, findings)
        if raw is None:
            return
        self.header = raw[0] if raw else []
        idx = {c: i for i, c in enumerate(self.header)}
        self.cols = set(self.header)
        for r, line in zip(raw[1:], lines):
            d = {c: r[i] for c, i in idx.items()}
            d["_line"] = line
            self.rows.append(d)


def read_csv_checked(path, findings, keep=True, sink=None):
    """Reads a CSV, runs the structural checks, and returns (rows, line numbers) for the
    well-formed rows. With keep=False each well-formed row is handed to sink(row, line)
    instead of being kept."""
    name = path.name
    with path.open("rb") as fh:
        head = fh.read(4096)
    if head.startswith(b"\xef\xbb\xbf"):
        findings.warn("struct.encoding", name, [1], "file starts with a UTF-8 BOM (the logger writes none)")
    if b"\r\n" in head:
        findings.warn("struct.encoding", name, [1], "CRLF line ends (the logger writes \\n): the file "
                      "was re-saved by another program")

    with path.open("r", encoding="utf-8-sig", newline="") as fh:
        reader = csv.reader(fh)
        try:
            header = next(reader)
        except StopIteration:
            findings.fail("struct.empty", name, [], "file is empty: no header row")
            return None, []
        n = len(header)
        dup = sorted({c for c in header if header.count(c) > 1})
        if dup:
            findings.fail("struct.header", name, [1], "duplicate column(s) in header: %s" % ", ".join(dup))

        bool_idx = [i for i, c in enumerate(header) if c in BOOL_COLS]
        num_idx = [i for i, c in enumerate(header)
                   if not c.startswith("cfg_") and c not in TEXT_COLS and c not in BOOL_COLS]
        int_idx = [i for i, c in enumerate(header) if c in INT_COLS]
        enum_idx = [(i, ENUMS[c]) for i, c in enumerate(header) if c in ENUMS]

        bad_count, bad_bool, bad_num, bad_int, bad_nan, bad_enum = [], {}, {}, {}, {}, {}
        rows, lines = ([header], []) if keep else (None, [])
        line = 1
        for r in reader:
            line = reader.line_num
            if not r or (len(r) == 1 and r[0] == ""):
                continue
            if len(r) != n:
                bad_count.append((line, len(r)))
                continue
            ok = True
            for c in r:
                if c in NAN_TOKENS:
                    bad_nan.setdefault(header[r.index(c)], []).append((line, c))
                    ok = False
                    break
            for i in bool_idx:
                if r[i] not in ("0", "1"):
                    bad_bool.setdefault(header[i], []).append((line, r[i]))
                    ok = False
            for i in num_idx:
                c = r[i]
                if c != "":
                    try:
                        float(c)
                    except ValueError:
                        bad_num.setdefault(header[i], []).append((line, c))
                        ok = False
            for i in int_idx:
                c = r[i]
                if c != "" and not re.match(r"^-?\d+$", c):
                    v = fnum(c)
                    if v is None or abs(v - round(v)) > 1e-9:
                        bad_int.setdefault(header[i], []).append((line, c))
            for i, allowed in enum_idx:
                if r[i] not in allowed:
                    bad_enum.setdefault(header[i], []).append((line, r[i]))
            if not ok:
                continue
            if keep:
                rows.append(r)
                lines.append(line)
            else:
                sink(r, line)

    if bad_count:
        findings.fail("struct.cells", name, [l for l, _ in bad_count],
                      "%d row(s) with the wrong number of cells (header has %d; first: %d)"
                      % (len(bad_count), n, bad_count[0][1]))
    for col, lst in bad_nan.items():
        findings.fail("struct.nan", name, [l for l, _ in lst],
                      "%s written as text %r (NaN must be an empty cell)" % (col, lst[0][1]))
    for col, lst in bad_bool.items():
        findings.fail("struct.bool", name, [l for l, _ in lst],
                      "boolean %s is %r, not 0/1" % (col, lst[0][1]))
    for col, lst in bad_num.items():
        findings.fail("struct.number", name, [l for l, _ in lst],
                      "%s does not parse as a number: %r" % (col, lst[0][1]))
    for col, lst in bad_int.items():
        findings.warn("struct.int", name, [l for l, _ in lst],
                      "%s should be a whole number, is %r" % (col, lst[0][1]))
    for col, lst in bad_enum.items():
        vals = sorted({v for _, v in lst})
        findings.fail("struct.enum", name, [l for l, _ in lst],
                      "%s has value(s) %s outside %s" % (col, vals, sorted(ENUMS[col])))
    if keep:
        return rows, lines
    return header, lines


class Frames:
    """The PlayerLog as typed columns (it can be 150,000+ rows)."""

    WANT_F = ("timeSinceStartSec", "unscaledDeltaMs", "mouseDeltaX", "mouseDeltaY", "ufoX", "ufoY",
              "towerX", "shotHitX", "camX")
    WANT_I = ("roundNumber", "frameIndex", "targetFps", "diffFps", "baseFps", "crossed",
              "leftButtonDown", "leftButtonPressed", "fireKeyDown", "shotFired", "blockIndex",
              "blockOrdinal")
    WANT_S = ("phase", "phaseStartIso", "pacedBy", "alteredSide", "side", "levelType", "sessionId",
              "runId", "direction", "frameLimiter", "latinOrder")

    def __init__(self, path, findings):
        self.path = path
        self.name = path.name
        self.n = 0
        self.header = []
        self.c = {}
        self.line = []
        intern = {}
        cols = {}

        def sink(r, line):
            for name, i, kind in spec:
                v = r[i]
                if kind == "f":
                    cols[name].append(fnum(v))
                elif kind == "i":
                    cols[name].append(None if v == "" else inum(v))
                else:
                    cols[name].append(intern.setdefault(v, v))
            self.line.append(line)

        with path.open("r", encoding="utf-8-sig", newline="") as fh:
            first = fh.readline()
        header = next(csv.reader([first])) if first else []
        idx = {c: i for i, c in enumerate(header)}
        spec = []
        for name in self.WANT_F:
            if name in idx:
                spec.append((name, idx[name], "f"))
        for name in self.WANT_I:
            if name in idx:
                spec.append((name, idx[name], "i"))
        for name in self.WANT_S:
            if name in idx:
                spec.append((name, idx[name], "s"))
        for name, _, _ in spec:
            cols[name] = []
        res = read_csv_checked(path, findings, keep=False, sink=sink)
        if res[0] is None:
            return
        self.header = header
        self.cols = set(header)
        self.c = cols
        self.n = len(self.line)

    def has(self, col):
        return col in self.c


# ------------------------------------------------------------------------- session

def log_files(folder, prefix):
    return sorted(p for p in folder.glob(prefix + "_*.csv") if p.is_file())


def session_folders(root):
    out = []
    for d in sorted(root.iterdir(), key=lambda p: p.name.lower()):
        if d.is_dir() and any(d.glob("*Log_*.csv")):
            out.append(d)
    return out


def newest_session(root):
    best, best_t = None, -1.0
    for d in session_folders(root):
        t = max(p.stat().st_mtime for p in d.glob("*Log_*.csv"))
        if t > best_t:
            best, best_t = d, t
    return best


def classify(headers):
    cols = set()
    for h in headers:
        cols |= set(h)
    if {"stimulusMs", "weapon", "spikeFired", "stuttersMs", "jndEstimateMs"} & cols:
        return "stutter"
    if "diffFps" not in cols and "jndDiffFps" not in cols:
        return "unknown"
    if "levelType" in cols:
        return "v4"
    if "cfg_practiceDiffsFps" in cols:
        return "v1" if "cfg_stimCount" in cols else "v2"
    return "v3"


class Phase:
    def __init__(self, key, order):
        self.key = key                  # (phase, phaseStartIso)
        self.kind = key[0]
        self.order = order
        self.shots = []                 # ShotLog dict rows
        self.f0 = None                  # PlayerLog index range [f0, f1)
        self.f1 = None
        self.level = None
        self.level_inferred = False
        self.label = ""


class Session:
    def __init__(self, folder, opts):
        self.folder = folder
        self.name = folder.name
        self.opts = opts
        self.F = Findings(self.name)
        self.gen = None
        self.sess = None
        self.shot = None
        self.frames = None
        self.phases = []
        self.skip_reason = None
        self.period = None             # scroll boundary period, when inferred
        self.level = None
        self.level_inferred = False
        self.level_evidence = ""


def audit_folder(folder, opts):
    S = Session(folder, opts)
    F = S.F
    if any(folder.glob("RoundLog_*.csv")):
        S.skip_reason = "stutter-study logs (RoundLog files): not audited"
        return S
    files = {k: log_files(folder, k) for k in ("SessionLog", "ShotLog", "PlayerLog")}
    headers = []
    for k, lst in files.items():
        if len(lst) > 1:
            F.warn("files.duplicate", lst[1].name, [], "%d %s files in one folder (%s): the logger "
                   "only suffixes _2, _3 on a name collision; auditing %s"
                   % (len(lst), k, ", ".join(p.name for p in lst), lst[0].name))
        if lst:
            with lst[0].open("r", encoding="utf-8-sig", newline="") as fh:
                line = fh.readline()
            headers.append(next(csv.reader([line])) if line else [])
    S.gen = classify(headers)
    if S.gen == "stutter":
        S.skip_reason = "stutter-study logs (stimulusMs / weapon columns): not audited"
        return S
    if S.gen == "unknown":
        S.skip_reason = "no frame-rate study columns (diffFps) found: not audited"
        return S
    for k in ("SessionLog", "ShotLog", "PlayerLog"):
        if not files[k]:
            F.fail("files.missing", "%s_*.csv" % k, [], "no %s file in the folder" % k)

    if files["SessionLog"]:
        S.sess = Table(files["SessionLog"][0], F)
    if files["ShotLog"]:
        S.shot = Table(files["ShotLog"][0], F)
    if files["PlayerLog"]:
        S.frames = Frames(files["PlayerLog"][0], F)

    check_identity(S)
    build_phases(S)
    infer_levels(S)
    check_shot_rows(S)
    check_frames(S)
    check_cross(S)
    check_session_log(S)
    check_structure_of_phases(S)
    return S


# ------------------------------------------------------------------------- identity

def file_id(path):
    m = re.match(r"^(?:SessionLog|ShotLog|PlayerLog)_(.+?)(?:_(\d+))?\.csv$", path.name)
    return m.group(1) if m else None


def check_identity(S):
    F = S.F
    expected = None
    for t in (S.shot, S.sess, S.frames):
        if t is None:
            continue
        fid = re.sub(r"_\d+$", "", file_id(t.path) or "") if t.path.name.count("_") > 2 else file_id(t.path)
        expected = expected or fid
    for t in (S.sess, S.shot):
        if t is None or not t.rows:
            continue
        pairs = {}
        for r in t.rows:
            pairs.setdefault((r.get("sessionId"), r.get("runId")), []).append(r["_line"])
        if len(pairs) > 1:
            F.fail("id.session", t.name, [], "rows from several sessions/runs: %s"
                   % ", ".join("%s_%s (%d rows)" % (k[0], k[1], len(v)) for k, v in pairs.items()))
        for (sid, rid), lines in pairs.items():
            if expected and rid is not None and "%s_%s" % (sid, rid) != expected:
                F.fail("id.session", t.name, lines[:1], "sessionId_runId %s_%s does not match the "
                       "file name's %s" % (sid, rid, expected))
    fr = S.frames
    if fr is not None and fr.n and fr.has("sessionId") and fr.has("runId"):
        sids = set(fr.c["sessionId"])
        rids = set(fr.c["runId"])
        if len(sids) > 1 or len(rids) > 1:
            F.fail("id.session", fr.name, [], "rows from several sessions/runs: %s / %s"
                   % (sorted(sids), sorted(rids)))
        elif expected and "%s_%s" % (next(iter(sids)), next(iter(rids))) != expected:
            F.fail("id.session", fr.name, [fr.line[0]], "sessionId_runId %s_%s does not match the "
                   "file name's %s" % (next(iter(sids)), next(iter(rids)), expected))


# ------------------------------------------------------------------------- phases

def build_phases(S):
    F = S.F
    phases = {}
    order = []

    def get(key):
        if key not in phases:
            phases[key] = Phase(key, len(order))
            order.append(key)
        return phases[key]

    if S.shot is not None and "phase" in S.shot.cols:
        for r in S.shot.rows:
            get((r.get("phase"), r.get("phaseStartIso", ""))).shots.append(r)
    fr = S.frames
    if fr is not None and fr.n and fr.has("phase"):
        ph, iso = fr.c["phase"], fr.c.get("phaseStartIso") or [""] * fr.n
        start = 0
        seen = set()
        for i in range(1, fr.n + 1):
            if i == fr.n or ph[i] != ph[start] or iso[i] != iso[start]:
                key = (ph[start], iso[start])
                if key in seen:
                    F.fail("frame.phaseRows", fr.name, [fr.line[start]], "rows of phase %s (%s) are "
                           "not contiguous: they start again at this row" % key)
                else:
                    p = get(key)
                    p.f0, p.f1 = start, i
                seen.add(key)
                start = i
    # Order phases by when they started.
    S.phases = sorted((phases[k] for k in order), key=lambda p: (parse_iso(p.key[1]) is None,
                                                                    parse_iso(p.key[1]) or datetime.min,
                                                                    p.order))
    n_pr = 0
    n_main = 0
    for p in S.phases:
        if p.kind == "practice":
            n_pr += 1
            p.label = "practice#%d" % n_pr
        else:
            n_main += 1
            bo = p.shots[0].get("blockOrdinal") if p.shots else (
                str(fr.c["blockOrdinal"][p.f0]) if fr is not None and p.f0 is not None and fr.has("blockOrdinal") else "?")
            p.label = "MAIN %s" % bo
        if p.f0 is None and p.shots:
            F.fail("cross.phase", S.shot.name, [p.shots[0]["_line"]], "%s has %d ShotLog rows but no "
                   "PlayerLog frames" % (p.label, len(p.shots)))
        if p.f0 is not None and not p.shots and fr is not None:
            F.note("cross.phase", fr.name, [fr.line[p.f0]], "%s has %d frames and no ShotLog rows: the "
                   "session ended before its first response" % (p.label, p.f1 - p.f0))


# ------------------------------------------------------------------------- level types

def screen_aspect(S):
    if S.sess is not None:
        for r in S.sess.rows:
            m = re.match(r"^(\d+)x(\d+)$", r.get("resolution", ""))
            if m and int(m.group(2)) > 0:
                return int(m.group(1)) / int(m.group(2)), r.get("resolution")
    return 16.0 / 9.0, None


def infer_levels(S):
    F = S.F
    fr = S.frames
    if S.gen == "v4":
        for p in S.phases:
            vals = set()
            if p.shots:
                vals |= {r.get("levelType", "") for r in p.shots}
            if fr is not None and p.f0 is not None and fr.has("levelType"):
                vals |= set(fr.c["levelType"][p.f0:p.f1])
            vals.discard("")
            if len(vals) > 1:
                F.fail("level.mixed", (S.shot or fr).name, [], "%s mixes level types %s" % (p.label, sorted(vals)))
            p.level = next(iter(vals)) if vals else None
        levels = sorted({p.level for p in S.phases if p.level})
        S.level = "+".join(levels) if levels else "?"
        return

    if S.opts.level_type:
        S.level = S.opts.level_type
        S.level_inferred = False
        S.level_evidence = "from --level-type"
    else:
        aspect, res = screen_aspect(S)
        W = VIEW_W_PER_ASPECT * aspect
        limit = W + 2 * PAN_RANGE + 1.0
        span, which = None, ""
        jump = 0.0
        if fr is not None and fr.n:
            ph = fr.c.get("phase")
            ux = fr.c.get("ufoX")
            idx = [i for i in range(fr.n) if ph[i] == "main"] if ph else []
            which = "main"
            if not idx:
                idx = list(range(fr.n))
                which = "all"
            xs = [ux[i] for i in idx if ux[i] is not None]
            if xs:
                span = max(xs) - min(xs)
            # Live tower jumps within a round: only the scrolling world moves the tower mid-round.
            tx, rn, alt = fr.c.get("towerX"), fr.c.get("roundNumber"), fr.c.get("alteredSide")
            if tx and rn and alt:
                for i in range(1, fr.n):
                    if alt[i] and alt[i - 1] and rn[i] == rn[i - 1] and ph[i] == ph[i - 1] \
                            and tx[i] is not None and tx[i - 1] is not None:
                        jump = max(jump, abs(tx[i] - tx[i - 1]))
        scroll = (span is not None and span > limit) or jump > 30.0
        S.level = "scroll" if scroll else "manual"
        S.level_inferred = True
        S.level_evidence = ("%s-phase ufoX span %s vs %.1f (W %.1f for %s%s)%s"
                            % (which, fmt(span, 1), limit, W, res or "16:9 assumed",
                               "" if res else "", "; live towerX jump %.1f" % jump if jump > 30 else ""))
        if span is not None and (span > 60.0) != (span > limit):
            F.note("level.infer", fr.name if fr else "-", [], "SPEC.md's fixed 60-unit rule would call "
                   "this session %s (ufoX span %.1f), but on this %s screen a still camera spans up to %.1f"
                   % ("scroll" if span > 60 else "manual", span, res or "16:9", limit))
    for p in S.phases:
        p.level = S.level
        p.level_inferred = S.level_inferred


# ------------------------------------------------------------------------- ShotLog rows

def cfg_val(r, name):
    return r.get("cfg_" + name, "")


def practice_points(S, p):
    """hit/miss points a practice phase was scored with: v3+ practice runs in the first main
    block's setup, whose cfg the practice rows leave blank."""
    if p.shots and fnum(cfg_val(p.shots[0], "hitPoints")) is not None:
        return fnum(cfg_val(p.shots[0], "hitPoints")), fnum(cfg_val(p.shots[0], "missPoints"))
    later = [q for q in S.phases if q.kind == "main" and q.order != p.order and q.shots
             and (parse_iso(q.key[1]) or datetime.max.replace(tzinfo=None)) is not None]
    for q in S.phases:
        if q.kind == "main" and q.shots and S.phases.index(q) > S.phases.index(p):
            return fnum(cfg_val(q.shots[0], "hitPoints")), fnum(cfg_val(q.shots[0], "missPoints"))
    if S.sess is not None and S.sess.rows:
        r = S.sess.rows[0]
        return fnum(cfg_val(r, "hitPoints")), fnum(cfg_val(r, "missPoints"))
    return None, None


def check_shot_rows(S):
    F = S.F
    T = S.shot
    if T is None or not T.rows:
        return
    name = T.name
    cols = T.cols
    gen = S.gen
    main_next = None          # expected first roundNumber of the next main phase

    for p in S.phases:
        rows = p.shots
        if not rows:
            continue
        practice = p.kind == "practice"
        hitp, missp = practice_points(S, p) if practice else (None, None)
        prev = None
        first_quest = None
        for k, r in enumerate(rows):
            L = r["_line"]
            g = lambda c: fnum(r.get(c, ""))
            outcome = r.get("outcome", "")
            fired = r.get("playerFired") == "1"
            hit = r.get("isHit") == "1"
            R = g("closeRadius") if "closeRadius" in cols and g("closeRadius") is not None else CLOSE_RADIUS
            hitX, miss, towerX = g("hitX"), g("missDistX"), g("towerX")
            base, diff, alt = inum(r.get("baseFps")), inum(r.get("diffFps")), inum(r.get("alteredFps"))
            rn = inum(r.get("roundNumber"))

            # outcome vs playerFired and the blanks a round that ran out leaves
            if outcome == "shot" and not fired:
                F.fail("shot.outcome", name, [L], "outcome=shot but playerFired=0")
            if outcome in ("expired", "crossingCap"):
                if fired:
                    F.fail("shot.outcome", name, [L], "outcome=%s but playerFired=1" % outcome)
                filled = [c for c in ("hitX", "missDistX", "firedAtSec", "sinceLastCrossingSec")
                          if c in cols and r.get(c, "") != ""]
                if filled:
                    F.fail("shot.outcome", name, [L], "outcome=%s but %s not blank (%s)" % (
                        outcome, "/".join(filled), ", ".join("%s=%s" % (c, r[c]) for c in filled)))
                if hit:
                    F.fail("shot.outcome", name, [L], "outcome=%s with isHit=1" % outcome)
            if fired:
                missing = [c for c in ("hitX", "missDistX", "firedAtSec") if c in cols and r.get(c, "") == ""]
                if missing:
                    F.fail("shot.outcome", name, [L], "a shot with blank %s" % "/".join(missing))

            # isHit and the miss distance
            if fired and miss is not None:
                if hit and abs(miss) > R + TOL["edge"]:
                    F.fail("shot.isHit", name, [L], "isHit=1 but |missDistX|=%s > closeRadius %s"
                           % (fmt(abs(miss)), fmt(R)))
                if not hit and abs(miss) < R - TOL["edge"]:
                    F.fail("shot.isHit", name, [L], "isHit=0 but |missDistX|=%s <= closeRadius %s"
                           % (fmt(abs(miss)), fmt(R)))
            if hitX is not None and towerX is not None and miss is not None:
                if abs(miss - (hitX - towerX)) > TOL["x4"]:
                    F.fail("shot.missDist", name, [L], "missDistX=%s but hitX - towerX = %s - %s = %s"
                           % (fmt(miss), fmt(hitX), fmt(towerX), fmt(hitX - towerX)))
            if fired and hitX is not None and towerX is not None and "side" in cols \
                    and miss is not None and abs(miss) > 1e-3:
                want = "Right" if hitX - towerX >= 0 else "Left"
                if r.get("side") != want:
                    F.fail("shot.side", name, [L], "side=%s but hitX %s is %s of towerX %s"
                           % (r.get("side"), fmt(hitX), "right" if want == "Right" else "left", fmt(towerX)))

            # the stimulus
            direction = r.get("direction", "down")
            if base is not None and diff is not None and alt is not None:
                if practice and gen in ("v3", "v4"):
                    if not (alt < base and base - alt == diff):
                        F.fail("shot.alteredFps", name, [L], "practice round: baseFps %d should be the pair's "
                               "higher rate and diffFps %d the difference, but alteredFps=%d" % (base, diff, alt))
                else:
                    want = max(1, base + diff) if direction == "up" else max(1, base - diff)
                    if alt != want:
                        F.fail("shot.alteredFps", name, [L], "alteredFps=%d but baseFps %d %s diffFps %d = %d"
                               % (alt, base, "+" if direction == "up" else "-", diff, want))
            if r.get("alteredSide", "") == "":
                F.fail("shot.alteredSide", name, [L], "blank alteredSide (the round had no split open?)")

            # QUEST+ bookkeeping
            counted = r.get("countedByStaircase")
            if practice and counted == "1":
                F.fail("shot.counted", name, [L], "practice row with countedByStaircase=1")
            if not practice and counted == "0":
                F.warn("shot.counted", name, [L], "main row with countedByStaircase=0: its response did "
                       "not reach QUEST+")
            qv = tuple(r.get(c, "") for c in QUEST_COLS if c in cols)
            if practice:
                if gen == "v4":
                    filled = [c for c in QUEST_COLS if c in cols and r.get(c, "") != ""]
                    if filled:
                        F.fail("shot.practiceQuest", name, [L], "practice row with %s filled (%s); "
                               "SPEC 4.3: blank on practice rows" % (
                                   "/".join(filled), ", ".join(r[c] for c in filled)))
                else:
                    if first_quest is None:
                        first_quest = qv
                    elif qv != first_quest:
                        F.warn("shot.practiceQuest", name, [L], "practice QUEST+ columns changed within "
                               "the practice phase (%s -> %s): practice must not move the posterior"
                               % (",".join(first_quest), ",".join(qv)))
            elif "threshDiffFps" in cols and r.get("threshDiffFps", "") == "":
                F.fail("shot.quest", name, [L], "main row with blank threshDiffFps")

            # crossings and their timing
            cr = inum(r.get("crossings"))
            last, since = g("lastCrossingAtSec"), g("sinceLastCrossingSec")
            fired_at, start = g("firedAtSec"), g("trialStartSec")
            if cr is not None and "lastCrossingAtSec" in cols:
                if (cr == 0) != (last is None):
                    F.fail("shot.crossBlank", name, [L], "crossings=%d but lastCrossingAtSec=%s"
                           % (cr, fmt(last)))
            if fired and last is not None and fired_at is not None:
                if since is None:
                    F.fail("shot.sinceCross", name, [L], "sinceLastCrossingSec blank with firedAtSec=%s, "
                           "lastCrossingAtSec=%s" % (fmt(fired_at), fmt(last)))
                elif abs(since - (fired_at - last)) > TOL["time4"]:
                    F.fail("shot.sinceCross", name, [L], "sinceLastCrossingSec=%s but firedAtSec - "
                           "lastCrossingAtSec = %s - %s = %s" % (fmt(since), fmt(fired_at), fmt(last),
                                                                 fmt(fired_at - last)))
                if last > fired_at + TOL["time4"]:
                    F.fail("shot.sinceCross", name, [L], "lastCrossingAtSec %s after firedAtSec %s"
                           % (fmt(last), fmt(fired_at)))
            elif since is not None and (last is None or not fired):
                F.fail("shot.sinceCross", name, [L], "sinceLastCrossingSec=%s with %s" % (
                    fmt(since), "no crossing" if last is None else "no shot"))
            if gen == "v4" and "firstCrossingAtSec" in cols and cr is not None:
                first = g("firstCrossingAtSec")
                if (cr == 0) != (first is None):
                    F.fail("new.crossings", name, [L], "crossings=%d but firstCrossingAtSec=%s" % (cr, fmt(first)))
                if first is not None and last is not None and first > last + TOL["time4"]:
                    F.fail("new.crossings", name, [L], "firstCrossingAtSec %s after lastCrossingAtSec %s"
                           % (fmt(first), fmt(last)))
                if cr == 1 and first is not None and last is not None and abs(first - last) > TOL["time4"]:
                    F.fail("new.crossings", name, [L], "one crossing but firstCrossingAtSec %s != "
                           "lastCrossingAtSec %s" % (fmt(first), fmt(last)))
                for c in ("meanCrossingSpeed", "lastCrossingSpeed"):
                    if c in cols:
                        v = g(c)
                        if (cr == 0) != (v is None):
                            F.fail("new.crossings", name, [L], "crossings=%d but %s=%s" % (cr, c, fmt(v)))
                        if v is not None and v < 0:
                            F.fail("new.crossings", name, [L], "%s=%s is negative" % (c, fmt(v)))
            cap = inum(cfg_val(r, "maxCrossingsPerRound"))
            if cap is not None and cap > 0 and cr is not None:
                if outcome == "crossingCap" and cr != cap + 1:
                    F.fail("shot.cap", name, [L], "outcome=crossingCap with crossings=%d (cap %d: the "
                           "round closes on crossing %d)" % (cr, cap, cap + 1))
                if outcome == "shot" and cr > cap:
                    F.fail("shot.cap", name, [L], "a shot after %d crossings, past the cap of %d" % (cr, cap))
            if outcome == "crossingCap" and (cap is None or cap <= 0) and gen != "v3" and not practice:
                F.fail("shot.cap", name, [L], "outcome=crossingCap but cfg_maxCrossingsPerRound=%s"
                       % cfg_val(r, "maxCrossingsPerRound"))

            # round timing
            dur = g("roundDurationSec")
            now = g("timeSinceStartSec")
            if fired and fired_at is not None and start is not None and dur is not None:
                if abs(dur - (fired_at - start)) > TOL["dur"]:
                    F.fail("shot.duration", name, [L], "roundDurationSec=%s but firedAtSec - trialStartSec = "
                           "%s - %s = %s" % (fmt(dur), fmt(fired_at), fmt(start), fmt(fired_at - start)))
            if start is not None and fired_at is not None and fired_at < start - TOL["time4"]:
                F.fail("shot.duration", name, [L], "firedAtSec %s before trialStartSec %s" % (fmt(fired_at), fmt(start)))
            timeout = fnum(cfg_val(r, "roundTimeoutSec"))
            if outcome == "expired" and dur is not None and timeout:
                if dur < timeout - 1e-3:
                    F.fail("shot.duration", name, [L], "expired after roundDurationSec=%s, before the %s s "
                           "time limit" % (fmt(dur), fmt(timeout)))
                elif dur > timeout + 0.1:
                    F.warn("shot.duration", name, [L], "expired %s s after a %s s time limit"
                           % (fmt(dur - timeout, 3), fmt(timeout)))
            if fired and fired_at is not None and now is not None:
                lag = fired_at - now
                if lag < -TOL["time4"] or lag > 0.1:
                    F.warn("shot.firedAt", name, [L], "firedAtSec %s is %s s from the frame clock %s it was "
                           "logged on (expected 0 to one frame)" % (fmt(fired_at), fmt(lag), fmt(now)))
            if prev is not None:
                t0 = fnum(prev.get("timeSinceStartSec"))
                if t0 is not None and now is not None and now < t0:
                    F.fail("shot.time", name, [prev["_line"], L], "timeSinceStartSec runs backwards: %s -> %s"
                           % (fmt(t0), fmt(now)))
                if t0 is not None and start is not None and start < t0 - TOL["time4"]:
                    F.fail("shot.time", name, [prev["_line"], L], "round armed at trialStartSec %s, before the "
                           "previous response at %s" % (fmt(start), fmt(t0)))

            # numbering
            if rn is not None:
                if k == 0:
                    want = 1 if practice else (main_next if main_next is not None else 1)
                    if gen in ("v1", "v2") and not practice and main_next is None:
                        want = 1
                    if rn != want:
                        F.fail("shot.round", name, [L], "%s starts at roundNumber %d, expected %d (%s)" % (
                            p.label, rn, want, "practice restarts at 1" if practice else
                            "main continues across blocks"))
                else:
                    r0 = inum(prev.get("roundNumber"))
                    if r0 is not None and rn != r0 + 1:
                        F.fail("shot.round", name, [prev["_line"], L], "roundNumber %d follows %d" % (rn, r0))

            # running score
            ts = g("totalScore")
            hp = fnum(cfg_val(r, "hitPoints")) if not practice else hitp
            mp = fnum(cfg_val(r, "missPoints")) if not practice else missp
            if ts is not None and hp is not None and mp is not None:
                before = fnum(prev.get("totalScore")) if prev is not None else 0.0
                if before is not None and abs(ts - (before + (hp if hit else mp))) > 1e-2:
                    F.fail("shot.score", name, [L], "totalScore %s after %s (%s) from %s; expected %s" % (
                        fmt(ts), "a hit" if hit else "a miss", fmt(hp if hit else mp), fmt(before),
                        fmt(before + (hp if hit else mp))))

            # identity and grid
            if not practice or gen in ("v1", "v2"):
                for c in ("baseFps", "direction", "frameLimiter"):
                    cv = cfg_val(r, c)
                    if cv != "" and r.get(c, "") != "" and cv != r.get(c):
                        F.fail("id.cfg", name, [L], "%s=%s but cfg_%s=%s" % (c, r.get(c), c, cv))
            if not practice and diff is not None:
                lo, hi = inum(cfg_val(r, "minDiffFps")), inum(cfg_val(r, "maxDiffFps"))
                step = inum(cfg_val(r, "diffStepFps"))
                if lo is not None and hi is not None and not (lo <= diff <= hi):
                    F.fail("shot.grid", name, [L], "diffFps %d outside [%d, %d]" % (diff, lo, hi))
                elif lo is not None and step and (diff - lo) % step:
                    F.fail("shot.grid", name, [L], "diffFps %d is off the grid %d + k*%d" % (diff, lo, step))
            if practice and gen in ("v3", "v4"):
                if r.get("blockIndex") != "0" or r.get("blockOrdinal") != "0":
                    F.fail("id.practice", name, [L], "practice row with blockIndex=%s blockOrdinal=%s "
                           "(expected 0/0)" % (r.get("blockIndex"), r.get("blockOrdinal")))
                filled = [c for c in cols if c.startswith("cfg_") and r.get(c, "") != ""]
                if filled:
                    F.fail("id.practice", name, [L], "practice row with cfg_* filled (%s)" % ", ".join(filled[:4]))
            bo, bi, lo_ = inum(r.get("blockOrdinal")), r.get("blockIndex"), r.get("latinOrder", "")
            if not practice and bo and lo_:
                order = lo_.split(";")
                if bo > len(order) or order[bo - 1] != bi:
                    F.fail("id.latin", name, [L], "blockOrdinal %d of latinOrder %s is %s, but blockIndex=%s"
                           % (bo, lo_, order[bo - 1] if bo <= len(order) else "missing", bi))
            if gen == "v4":
                lt, clt = r.get("levelType", ""), cfg_val(r, "levelType")
                if not practice and clt and lt != clt:
                    F.fail("new.levelType", name, [L], "levelType=%s but cfg_levelType=%s" % (lt, clt))
                for c in ("ufoViewportX", "towerViewportX"):
                    v = g(c)
                    if c in cols and v is not None and not (-0.05 <= v <= 1.05):
                        F.fail("new.viewport", name, [L], "%s=%s outside [-0.05, 1.05]" % (c, fmt(v)))
            prev = r
        if not practice:
            last_rn = inum(rows[-1].get("roundNumber"))
            if last_rn is not None:
                main_next = last_rn + 1
        # identity constant within the phase
        for c in ("blockIndex", "blockOrdinal", "direction", "frameLimiter", "closeRadius",
                  "latinRow", "latinOrder", "levelType") + (() if practice and gen in ("v3", "v4") else ("baseFps",)):
            if c in cols:
                vals = {r.get(c) for r in rows}
                if len(vals) > 1:
                    F.fail("id.phase", name, [rows[0]["_line"]], "%s takes %d values within %s: %s"
                           % (c, len(vals), p.label, sorted(vals)))


# ------------------------------------------------------------------------- PlayerLog

def round_segments(fr, f0, f1):
    """{roundNumber: (start, end)} for the frames [f0, f1) of one phase."""
    out = {}
    rn = fr.c["roundNumber"]
    start = f0
    for i in range(f0 + 1, f1 + 1):
        if i == f1 or rn[i] != rn[start]:
            out.setdefault(rn[start], []).append((start, i))
            start = i
    return out


def infer_period(S):
    """Scroll levels: the boundary period, from the jumps of towerX within rounds' live frames."""
    fr = S.frames
    if fr is None or not fr.n:
        return None
    tx, alt, rn, ph = fr.c.get("towerX"), fr.c.get("alteredSide"), fr.c.get("roundNumber"), fr.c.get("phase")
    jumps = []
    for i in range(1, fr.n):
        if alt[i] and alt[i - 1] and rn[i] == rn[i - 1] and ph[i] == ph[i - 1] \
                and tx[i] is not None and tx[i - 1] is not None and abs(tx[i] - tx[i - 1]) > 1.0:
            jumps.append(abs(tx[i] - tx[i - 1]))
    if not jumps:
        return None
    return min(jumps)


def check_frames(S):
    F = S.F
    fr = S.frames
    if fr is None or not fr.n:
        return
    name = fr.name
    c = fr.c
    need = ("frameIndex", "timeSinceStartSec", "roundNumber")
    if any(not fr.has(x) for x in need):
        F.fail("frame.columns", name, [], "missing column(s): %s" % ", ".join(x for x in need if not fr.has(x)))
        return
    scroll_any = any(p.level == "scroll" for p in S.phases)
    if scroll_any:
        S.period = infer_period(S)
    fi, t, dt, rn = c["frameIndex"], c["timeSinceStartSec"], c["unscaledDeltaMs"], c["roundNumber"]
    paced, alt, side, ux, tx = c.get("pacedBy"), c.get("alteredSide"), c.get("side"), c.get("ufoX"), c.get("towerX")
    tgt, base, diff, crossed = c.get("targetFps"), c.get("baseFps"), c.get("diffFps"), c.get("crossed")
    L = fr.line
    clock_dev = []

    for p in S.phases:
        if p.f0 is None:
            continue
        f0, f1 = p.f0, p.f1
        scroll = p.level == "scroll"
        # frame index and clock
        if fi[f0] != 0:
            F.fail("frame.index", name, [L[f0]], "%s's first frame has frameIndex %s, not 0" % (p.label, fi[f0]))
        if t[f0] is not None and abs(t[f0]) > 1e-5:
            F.warn("frame.time", name, [L[f0]], "%s's first frame has timeSinceStartSec %s, not 0"
                   % (p.label, fmt(t[f0], 5)))
        gaps, back = [], []
        for i in range(f0 + 1, f1):
            if fi[i] != fi[i - 1] + 1:
                gaps.append((L[i], fi[i - 1], fi[i]))
            if t[i] is not None and t[i - 1] is not None:
                if t[i] <= t[i - 1]:
                    back.append((L[i], t[i - 1], t[i]))
                elif dt is not None and dt[i] is not None:
                    clock_dev.append((abs((t[i] - t[i - 1]) * 1000.0 - dt[i]), L[i]))
        if gaps:
            F.fail("frame.index", name, [g[0] for g in gaps], "%s: frameIndex jumps %s -> %s (%d gap(s))"
                   % (p.label, gaps[0][1], gaps[0][2], len(gaps)))
        if back:
            F.fail("frame.time", name, [b[0] for b in back], "%s: timeSinceStartSec does not increase: "
                   "%s -> %s (%d time(s))" % (p.label, fmt(back[0][1], 5), fmt(back[0][2], 5), len(back)))

        # round labels
        first_round = inum(p.shots[0].get("roundNumber")) if p.shots else None
        if first_round is not None and rn[f0] != first_round:
            F.fail("frame.round", name, [L[f0]], "%s's frames start at roundNumber %s but its first "
                   "ShotLog round is %d" % (p.label, rn[f0], first_round))
        bad_step = [(L[i], rn[i - 1], rn[i]) for i in range(f0 + 1, f1)
                    if rn[i] is not None and rn[i - 1] is not None and rn[i] not in (rn[i - 1], rn[i - 1] + 1)]
        if bad_step:
            F.fail("frame.round", name, [b[0] for b in bad_step], "%s: roundNumber steps %s -> %s"
                   % (p.label, bad_step[0][1], bad_step[0][2]))

        if paced is None or alt is None:
            continue
        # paced / alteredSide per round (the GO frame and the response frame are the exceptions)
        shot_by_round = {inum(r.get("roundNumber")): r for r in p.shots}
        segs = round_segments(fr, f0, f1)
        for r_no, spans in segs.items():
            idx = [i for a, b in spans for i in range(a, b)]
            live = [i for i in idx if alt[i]]
            go_like = [i for i in idx if alt[i] and not paced[i]]
            resp_like = [i for i in idx if paced[i] and not alt[i]]
            # GO frame: the first live frame only
            bad_go = [i for i in go_like if not live or i != live[0]]
            if bad_go:
                F.fail("frame.paced", name, [L[i] for i in bad_go], "%s round %s: alteredSide=%s with blank "
                       "pacedBy on a frame that is not the frame the split opened" % (p.label, r_no, alt[bad_go[0]]))
            shotf = c.get("shotFired")
            bad_resp = [i for i in resp_like if not (shotf and shotf[i] == 1)]
            if bad_resp:
                F.fail("frame.paced", name, [L[i] for i in bad_resp], "%s round %s: pacedBy=%s with blank "
                       "alteredSide/diffFps on a frame that is not the response frame" % (p.label, r_no, paced[bad_resp[0]]))
            # diffFps blank exactly when alteredSide is
            if diff is not None:
                bad_d = [i for i in idx if (diff[i] is None) != (not alt[i])]
                if bad_d:
                    # v1/v2 loggers wrote diffFps 0 rather than blank between rounds; the current one writes blank.
                    F.add("WARN" if S.gen in ("v1", "v2") else "FAIL", "frame.diff", name, [L[i] for i in bad_d],
                          "%s round %s: diffFps=%s with alteredSide=%r"
                           % (p.label, r_no, diff[bad_d[0]], alt[bad_d[0]]))
            sr = shot_by_round.get(r_no)
            if sr is not None and live:
                d_shot = inum(sr.get("diffFps"))
                bad = [i for i in live if diff is not None and diff[i] != d_shot]
                if bad:
                    F.fail("frame.stimulus", name, [L[i] for i in bad], "%s round %s: frames carry diffFps %s, "
                           "the ShotLog row (row %d) %s" % (p.label, r_no, diff[bad[0]], sr["_line"], d_shot))
                # alteredSide vs the ShotLog's (scroll: turned per boundary copy)
                s_alt, s_tx = sr.get("alteredSide"), fnum(sr.get("towerX"))
                bad = []
                for i in live:
                    want = s_alt
                    if scroll and S.period and tx[i] is not None and s_tx is not None:
                        k = int(round((tx[i] - s_tx) / S.period))
                        if k % 2:
                            want = "Left" if s_alt == "Right" else "Right"
                    elif scroll and tx[i] is not None and s_tx is not None and abs(tx[i] - s_tx) > 1.0:
                        continue
                    if alt[i] != want:
                        bad.append(i)
                if bad:
                    F.fail("frame.stimulus", name, [L[i] for i in bad], "%s round %s: frames carry alteredSide %s, "
                           "the ShotLog row (row %d) %s at towerX %s" % (p.label, r_no, alt[bad[0]], sr["_line"],
                                                                         s_alt, sr.get("towerX")))
                # targetFps and baseFps on paced frames
                s_base, s_alt_fps = inum(sr.get("baseFps")), inum(sr.get("alteredFps"))
                if tgt is not None:
                    bad = [i for i in idx if paced[i] == "base" and tgt[i] != s_base] + \
                          [i for i in idx if paced[i] == "altered" and tgt[i] != s_alt_fps]
                    if bad:
                        F.fail("frame.target", name, [L[i] for i in sorted(bad)], "%s round %s: targetFps %s "
                               "on a pacedBy=%s frame; the round's base/altered caps are %s/%s"
                               % (p.label, r_no, tgt[bad[0]], paced[bad[0]], s_base, s_alt_fps))
                if base is not None:
                    bad = [i for i in idx if paced[i] and base[i] != s_base]
                    if bad:
                        resp = [i for i in bad if c.get("shotFired") and c["shotFired"][i] == 1]
                        known = p.kind == "practice" and len(resp) == len(bad) and S.gen != "v4"
                        F.add("WARN" if known else "FAIL", "frame.baseFps", name, [L[i] for i in bad],
                              "%s round %s: baseFps=%s on %s, but the "
                               "round's base (ShotLog row %d) is %s%s" % (
                                   p.label, r_no, base[bad[0]],
                                   "its response frame" if len(resp) == len(bad) else "%d paced frame(s)" % len(bad),
                                   sr["_line"], s_base,
                                   " - the next practice pair's rate: ExperimentDirector.RunBlock switches the pair "
                                   "(SetPractice) in the coroutine step of the response frame, before LateUpdate "
                                   "logs that frame's CurrentBaseFps (a known issue of logs written before levelType "
                                   "existed; the game now keeps the ended round's base until the next GO!)"
                                   if p.kind == "practice" and len(resp) == len(bad) else ""))
                # towerX over the live frames
                if tx is not None:
                    txs = [tx[i] for i in live if tx[i] is not None]
                    if txs and not scroll and max(txs) - min(txs) > 1e-4:
                        F.fail("frame.tower", name, [L[live[0]], L[live[-1]]], "%s round %s: towerX moves during "
                               "the round's live frames (%s .. %s) in a %s level" % (
                                   p.label, r_no, fmt(min(txs)), fmt(max(txs)), p.level))
                    if txs and not scroll and s_tx is not None and abs(txs[-1] - s_tx) > TOL["x4"]:
                        F.fail("frame.tower", name, [L[live[-1]], sr["_line"]], "%s round %s: frames' towerX %s, "
                               "the ShotLog's %s" % (p.label, r_no, fmt(txs[-1]), fmt(s_tx)))
                    if txs and scroll and S.period:
                        bad = []
                        for a_, b_ in zip(live, live[1:]):
                            if tx[a_] is None or tx[b_] is None:
                                continue
                            d = tx[b_] - tx[a_]
                            if abs(d) > 1e-4:
                                k = d / S.period
                                if abs(k - round(k)) * S.period > TOL["period"]:
                                    bad.append(b_)
                        if bad:
                            F.fail("frame.tower", name, [L[i] for i in bad], "%s round %s: towerX jumps by %s, not a "
                                   "whole number of boundary periods (%s)" % (
                                       p.label, r_no, fmt(tx[bad[0]] - tx[bad[0] - 1]), fmt(S.period)))
            elif sr is None and live and r_no is not None and p.shots:
                last_round = inum(p.shots[-1].get("roundNumber"))
                if last_round is not None and r_no <= last_round:
                    F.fail("frame.round", name, [L[live[0]]], "%s: frames of round %s have no ShotLog row"
                           % (p.label, r_no))

        # targetFps on idle frames is the idle cap. Not on the frame a round's split opens on (pacedBy still
        # blank, alteredSide already set): its row carries the new round's base, but its own duration was paced by
        # the idle cap before it - in practice the last pair's base, which differs - the delivery order the README
        # describes at crossings.
        if tgt is not None and base is not None:
            bad = [i for i in range(f0, f1) if not paced[i] and tgt[i] is not None and base[i] is not None
                   and tgt[i] != base[i] and not (alt is not None and alt[i])]
            if bad:
                F.warn("frame.target", name, [L[i] for i in bad], "%s: %d idle frame(s) (pacedBy blank) with "
                       "targetFps %s != baseFps %s" % (p.label, len(bad), tgt[bad[0]], base[bad[0]]))

        # side vs position
        if side is not None and ux is not None and tx is not None:
            bad = []
            for i in range(f0, f1):
                if ux[i] is None or tx[i] is None:
                    continue
                d = ux[i] - tx[i]
                if d > DEAD_ZONE + TOL["deadZone"] and side[i] != "Right":
                    bad.append(i)
                elif d < -DEAD_ZONE - TOL["deadZone"] and side[i] != "Left":
                    bad.append(i)
            if bad:
                i = bad[0]
                F.fail("frame.side", name, [L[j] for j in bad], "%s: side=%s with ufoX - towerX = %s - %s = %s, "
                       "outside the %s dead zone (%d frame(s))" % (p.label, side[i], fmt(ux[i]), fmt(tx[i]),
                                                                   fmt(ux[i] - tx[i]), DEAD_ZONE, len(bad)))
            if crossed is not None and not scroll:
                silent = [i for i in range(f0 + 1, f1) if alt[i] and alt[i - 1] and side[i] != side[i - 1]
                          and crossed[i] != 1 and rn[i] == rn[i - 1]]
                if silent:
                    F.warn("frame.side", name, [L[i] for i in silent], "%s: the UFO changes side on %d live "
                           "frame(s) without crossed=1" % (p.label, len(silent)))
            if crossed is not None:
                odd = [i for i in range(f0 + 1, f1) if crossed[i] == 1 and side[i] == side[i - 1]]
                if odd:
                    F.fail("frame.crossed", name, [L[i] for i in odd], "%s: crossed=1 but side stays %s"
                           % (p.label, side[odd[0]]))
                dead = [i for i in range(f0, f1) if crossed[i] == 1 and not alt[i]]
                if dead:
                    F.fail("frame.crossed", name, [L[i] for i in dead], "%s: crossed=1 on a frame with no split "
                           "open" % p.label)

    if clock_dev:
        worst = max(clock_dev)
        if worst[0] > 0.5:
            F.warn("frame.clock", name, [worst[1]], "timeSinceStartSec steps differ from unscaledDeltaMs by up "
                   "to %.3f ms" % worst[0])
        elif worst[0] > 0.02:
            F.note("frame.clock", name, [worst[1]], "timeSinceStartSec steps differ from unscaledDeltaMs by up to "
                   "%.3f ms: the phase clock is a float32 difference of Time.unscaledTime (seconds since the app "
                   "started), so it is quantised - use unscaledDeltaMs for frame times" % worst[0])


# ------------------------------------------------------------------------- cross-log

def check_cross(S):
    F = S.F
    fr = S.frames
    T = S.shot
    if fr is None or not fr.n or T is None:
        return
    c = fr.c
    L = fr.line
    if not fr.has("shotFired"):
        return
    sf, rn, t, dt = c["shotFired"], c["roundNumber"], c["timeSinceStartSec"], c.get("unscaledDeltaMs")
    paced, crossed, ux, shx = c.get("pacedBy"), c.get("crossed"), c.get("ufoX"), c.get("shotHitX")
    lbp, fkd = c.get("leftButtonPressed"), c.get("fireKeyDown")

    for p in S.phases:
        if p.f0 is None or not p.shots:
            continue
        f0, f1 = p.f0, p.f1
        frames_fired = [i for i in range(f0, f1) if sf[i] == 1]
        if len(frames_fired) != len(p.shots):
            F.fail("cross.shotFrames", fr.name, [L[i] for i in frames_fired], "%s: %d shotFired frame(s) but %d "
                   "ShotLog row(s) (every response - a shot or a round that ran out - flags its frame)"
                   % (p.label, len(frames_fired), len(p.shots)))
        by_round = {}
        for i in frames_fired:
            by_round.setdefault(rn[i], []).append(i)
        segs = round_segments(fr, f0, f1)
        for k, r in enumerate(p.shots):
            r_no = inum(r.get("roundNumber"))
            fi = by_round.get(r_no, [])
            st = fnum(r.get("timeSinceStartSec"))
            if len(fi) != 1:
                near = [i for i in frames_fired if st is not None and t[i] is not None and abs(t[i] - st) <= TOL["frameTime"]]
                F.fail("cross.shotFrames", T.name, [r["_line"]], "%s round %s: %d shotFired frame(s) carry this "
                       "roundNumber%s" % (p.label, r_no, len(fi), "; the frame at its time is labelled round %s (row %d)"
                                          % (rn[near[0]], L[near[0]]) if near else ""))
                continue
            i = fi[0]
            if st is not None and t[i] is not None and abs(t[i] - st) > TOL["frameTime"]:
                F.fail("cross.shotFrames", T.name, [r["_line"]], "%s round %s: timeSinceStartSec %s, its shotFired "
                       "frame (PlayerLog row %d) %s" % (p.label, r_no, fmt(st), L[i], fmt(t[i], 5)))
            fired = r.get("playerFired") == "1"
            hx = fnum(r.get("hitX"))
            if fired and hx is not None and shx is not None and shx[i] is not None and abs(shx[i] - hx) > TOL["x4"]:
                F.fail("cross.shotHitX", fr.name, [L[i]], "%s round %s: shotHitX %s but the ShotLog's hitX is %s "
                       "(row %d)" % (p.label, r_no, fmt(shx[i]), fmt(hx), r["_line"]))
            if fired and lbp is not None and fkd is not None and lbp[i] != 1 and fkd[i] != 1:
                F.warn("cross.shotInput", fr.name, [L[i]], "%s round %s: a shot on a frame with neither "
                       "leftButtonPressed nor fireKeyDown" % (p.label, r_no))
            if fired and hx is not None and ux is not None and i > f0:
                if not (close(ux[i - 1], hx, TOL["x4"]) or close(ux[i], hx, TOL["x4"])):
                    F.warn("cross.shotHitX", T.name, [r["_line"]], "%s round %s: hitX %s is neither the UFO's X "
                           "before (%s) nor after (%s) the shot frame's move" % (p.label, r_no, fmt(hx),
                                                                                fmt(ux[i - 1]), fmt(ux[i])))
            if not fired and shx is not None and shx[i] is not None and abs(shx[i]) > 1e-9 and k == 0:
                pass
            # side of a round that ran out: the UFO's X at the response
            if not fired and ux is not None and "side" in T.cols and i > f0:
                tw = fnum(r.get("towerX"))
                x = ux[i - 1]
                if tw is not None and x is not None and abs(x - tw) > 1e-3:
                    want = "Right" if x - tw >= 0 else "Left"
                    if r.get("side") != want and abs(ux[i] - tw) > 1e-3 and ("Right" if ux[i] - tw >= 0 else "Left") != r.get("side"):
                        F.fail("shot.side", T.name, [r["_line"]], "%s round %s ran out with side=%s but the UFO "
                               "was at %s, %s of towerX %s" % (p.label, r_no, r.get("side"), fmt(x),
                                                               want.lower(), fmt(tw)))

            # per-round crossings and delivery
            spans = segs.get(r_no, [])
            idx = [j for a, b in spans for j in range(a, b)]
            if not idx:
                continue
            cr = inum(r.get("crossings"))
            n_cross = sum(1 for j in idx if crossed is not None and crossed[j] == 1)
            first_of_phase = k == 0 and S.gen != "v4"
            last = fnum(r.get("lastCrossingAtSec"))
            pre = None
            if cr is not None and crossed is not None:
                if n_cross > cr:
                    F.fail("cross.crossings", T.name, [r["_line"]], "%s round %s: crossings=%d but the PlayerLog "
                           "flags %d crossed frame(s)" % (p.label, r_no, cr, n_cross))
                elif n_cross < cr and not first_of_phase:
                    F.fail("cross.crossings", T.name, [r["_line"]], "%s round %s: crossings=%d but the PlayerLog "
                           "flags %d crossed frame(s)" % (p.label, r_no, cr, n_cross))
                elif n_cross < cr:
                    pre = cr - n_cross
                cross_idx = [j for j in idx if crossed[j] == 1]
                if cross_idx and last is not None and (pre is None or last >= 0):
                    j = cross_idx[-1]
                    nxt = t[j + 1] if j + 1 < f1 and t[j + 1] is not None else None
                    if t[j] is not None and (last < t[j] - TOL["time4"] or (nxt is not None and last > nxt + TOL["time4"])):
                        F.fail("cross.crossTime", T.name, [r["_line"], L[j]], "%s round %s: lastCrossingAtSec %s is "
                               "outside its crossed frame [%s, %s)" % (p.label, r_no, fmt(last), fmt(t[j], 5), fmt(nxt, 5)))
            # measured fps
            if paced is not None and dt is not None:
                res = {}
                for kind in ("base", "altered"):
                    sel = [dt[j] for j in idx if paced[j] == kind and dt[j] is not None]
                    secs = sum(sel) / 1000.0
                    res[kind] = (len(sel), secs, len(sel) / secs if secs > 0 else None)
                bad = []
                for kind, mcol, scol in (("base", "measuredBaseFps", "baseSideSec"),
                                         ("altered", "measuredAlteredFps", "alteredSideSec")):
                    n, secs, fps = res[kind]
                    m_log, s_log = fnum(r.get(mcol)), fnum(r.get(scol))
                    if fps is None and m_log is not None and not first_of_phase:
                        bad.append("%s=%s but no %s-paced frames" % (mcol, fmt(m_log, 2), kind))
                    elif fps is not None and m_log is None:
                        bad.append("%s blank but %d %s-paced frames" % (mcol, n, kind))
                    elif fps is not None and m_log is not None and abs(fps - m_log) > max(TOL["fpsAbs"], TOL["fpsRel"] * m_log):
                        bad.append("%s=%s, frames give %d/%.4fs = %.3f" % (mcol, fmt(m_log, 2), n, secs, fps))
                    if s_log is not None and abs(secs - s_log) > TOL["sideSec"]:
                        bad.append("%s=%s, frames give %.4f" % (scol, fmt(s_log, 3), secs))
                if bad and not first_of_phase:
                    F.fail("cross.measured", T.name, [r["_line"]], "%s round %s: %s" % (p.label, r_no, "; ".join(bad)))
                elif first_of_phase:
                    tot_log = (fnum(r.get("baseSideSec")) or 0.0) + (fnum(r.get("alteredSideSec")) or 0.0)
                    tot_fr = res["base"][1] + res["altered"][1]
                    gap = tot_log - tot_fr
                    first_live_t = next((t[j] for j in idx if paced[j]), None)
                    msg = ("%s round %s (the phase's first): the split opened %.3f s before the frame log "
                           "started (baseSideSec+alteredSideSec %.3f s vs %.3f s of paced frames)"
                           % (p.label, r_no, gap, tot_log, tot_fr))
                    if pre:
                        msg += "; %d of its %d crossing(s) happened in that time and have no crossed frame%s" % (
                            pre, cr, " (lastCrossingAtSec %s < 0)" % fmt(last) if last is not None and last < 0 else "")
                    if bad:
                        msg += "; so %s" % "; ".join(bad)
                    level = "WARN" if (pre or bad or gap > READY_BEAT_SEC + 0.25 or gap < -TOL["sideSec"]) else "NOTE"
                    F.add(level, "cross.goBeat", T.name, [r["_line"]], msg)


# ------------------------------------------------------------------------- SessionLog

def check_session_log(S):
    F = S.F
    T = S.sess
    if T is None or not T.rows:
        return
    name = T.name
    cols = T.cols
    fr = S.frames
    mains = [p for p in S.phases if p.kind == "main"]
    by_ord = {}
    for p in mains:
        bo = p.shots[0].get("blockOrdinal") if p.shots else (
            str(fr.c["blockOrdinal"][p.f0]) if fr is not None and p.f0 is not None and fr.has("blockOrdinal") else None)
        by_ord.setdefault(bo, []).append(p)
    seen = set()
    for k, r in enumerate(T.rows):
        L = r["_line"]
        g = lambda col: fnum(r.get(col, ""))
        bo = r.get("blockOrdinal")
        if bo in seen:
            F.fail("sess.rows", name, [L], "a second row for blockOrdinal %s" % bo)
        seen.add(bo)
        er = r.get("endReason", "")
        if er == "abandoned" and k != len(T.rows) - 1:
            F.fail("sess.endReason", name, [L], "endReason=abandoned on a block that is not the session's last")
        ps = by_ord.get(bo, [])
        if len(ps) != 1:
            F.fail("sess.match", name, [L], "blockOrdinal %s matches %d main phase(s) in the ShotLog/PlayerLog"
                   % (bo, len(ps)))
            continue
        p = ps[0]
        shots = p.shots
        # identity vs the phase's rows
        if shots:
            for col in ("blockIndex", "baseFps", "direction", "frameLimiter", "latinRow", "latinOrder", "levelType"):
                if col in cols and col in S.shot.cols and r.get(col) != shots[0].get(col):
                    F.fail("sess.identity", name, [L], "%s=%s but the block's ShotLog rows say %s"
                           % (col, r.get(col), shots[0].get(col)))
            for col in [c for c in cols if c.startswith("cfg_")]:
                if col in S.shot.cols and r.get(col) != shots[0].get(col):
                    F.fail("sess.identity", name, [L], "%s=%s but the block's ShotLog rows say %s"
                           % (col, r.get(col), shots[0].get(col)))
        # times
        st, en = parse_iso(r.get("startIso")), parse_iso(r.get("endIso"))
        sd_ = g("sessionDurationSec")
        if st and en and sd_ is not None and abs((en - st).total_seconds() - sd_) > 2e-3:
            F.fail("sess.time", name, [L], "sessionDurationSec %s but endIso - startIso = %.3f"
                   % (fmt(sd_, 3), (en - st).total_seconds()))
        ps_iso = parse_iso(p.key[1])
        if st and ps_iso and abs((ps_iso - st).total_seconds()) > 0.05:
            F.warn("sess.time", name, [L], "startIso %s is %.3f s from the main phase's phaseStartIso %s"
                   % (r.get("startIso"), (ps_iso - st).total_seconds(), p.key[1]))

        def cmp(col, want, tol, why=""):
            have = g(col)
            if col not in cols or want is None and have is None:
                return
            if (want is None) != (have is None) or abs(have - want) > tol:
                F.fail("sess.recompute", name, [L], "%s %s=%s, recomputed %s%s" % (
                    p.label, col, fmt(have, 4), fmt(want if want is None else float(want), 4), why))

        if shots:
            fired = [s for s in shots if s.get("playerFired") == "1"]
            hits = [s for s in shots if s.get("isHit") == "1"]
            n = len(shots)
            cmp("trials", n, 0)
            cmp("shotsFired", len(fired), 0)
            cmp("hits", len(hits), 0)
            cmp("accuracy", len(hits) / n, 6e-5)
            cmp("score", fnum(shots[-1].get("totalScore")), 6e-3)
            cmp("roundsExpired", sum(1 for s in shots if s.get("outcome") == "expired"), 0)
            cmp("roundsCrossingCapped", sum(1 for s in shots if s.get("outcome") == "crossingCap"), 0)
            durs = [fnum(s.get("roundDurationSec")) for s in shots if fnum(s.get("roundDurationSec")) is not None]
            cmp("avgRoundDurationSec", mean(durs), 6e-4)
            crs = [inum(s.get("crossings")) or 0 for s in shots]
            cmp("avgCrossingsPerRound", mean(crs), 6e-4)
            cmp("shotsWithoutCrossing", sum(1 for s in fired if (inum(s.get("crossings")) or 0) == 0), 0)
            since = [fnum(s.get("sinceLastCrossingSec")) for s in shots if fnum(s.get("sinceLastCrossingSec")) is not None]
            cmp("avgSinceLastCrossingSec", mean(since), 1.5e-4)
            miss = [abs(fnum(s.get("missDistX"))) for s in shots if fnum(s.get("missDistX")) is not None]
            miss_only = [abs(fnum(s.get("missDistX"))) for s in shots
                         if fnum(s.get("missDistX")) is not None and s.get("isHit") != "1"]
            cmp("avgMissDistX", mean(miss), 1.5e-4)
            cmp("avgMissDistXMissesOnly", mean(miss_only), 1.5e-4)
            cmp("medianMissDistX", statistics.median(miss) if miss else None, 1.5e-4)
            cmp("sdMissDistX", sample_sd(miss), 3e-4)
            cmp("maxMissDistX", max(miss) if miss else None, 1.5e-4)
            cmp("staircaseTrials", sum(1 for s in shots if s.get("countedByStaircase") == "1"), 0)
            cmp("lastDiffFps", fnum(shots[-1].get("diffFps")), 0)
            for col, scol, tol in (("jndDiffFps", "threshDiffFps", 1.5e-3), ("sd", "sd", 1.5e-3),
                                   ("slopeEstimate", "slopeEstimate", 1.5e-3), ("lapseEstimate", "lapseEstimate", 1.5e-4)):
                if scol in S.shot.cols:
                    cmp(col, fnum(shots[-1].get(scol)), tol, " (the last round's posterior)")
            if S.gen == "v4":
                firsts = [fnum(s.get("firstCrossingAtSec")) - fnum(s.get("trialStartSec")) for s in shots
                          if fnum(s.get("firstCrossingAtSec")) is not None and fnum(s.get("trialStartSec")) is not None]
                cmp("avgFirstCrossingSec", mean(firsts), 3e-4)
                w = [(fnum(s.get("meanCrossingSpeed")), inum(s.get("crossings")) or 0) for s in shots
                     if fnum(s.get("meanCrossingSpeed")) is not None]
                cmp("avgCrossingSpeed", sum(a * b for a, b in w) / sum(b for a, b in w) if w and sum(b for a, b in w) else None,
                    2e-3, " (crossing-weighted mean of meanCrossingSpeed)")
                vel = [abs(fnum(s.get("ufoVelX"))) for s in fired if fnum(s.get("ufoVelX")) is not None]
                cmp("avgShotUfoSpeed", mean(vel), 2e-3)
                cmp("medianSinceLastCrossingSec", statistics.median(since) if since else None, 1.5e-4)
                along = [fnum(s.get("missDistX")) * (1 if fnum(s.get("ufoVelX")) > 0 else -1) for s in fired
                         if fnum(s.get("missDistX")) is not None and fnum(s.get("ufoVelX")) is not None
                         and abs(fnum(s.get("ufoVelX"))) > 0.5]
                cmp("avgMissAlongTravelX", mean(along), 3e-4)
        # QUEST+ derived values
        base = g("baseFps")
        jnd, jalt, jft = g("jndDiffFps"), g("jndAlteredFps"), g("jndFrameTimeDiffMs")
        direction = r.get("direction", "down")
        if base is not None and jnd is not None:
            want_alt = base + jnd if direction == "up" else base - jnd
            cmp("jndAlteredFps", want_alt, 2e-3)
            if want_alt > 0 and jft is not None:
                want_ft = abs(1000.0 / want_alt - 1000.0 / base)
                tol = 1000.0 / (want_alt * want_alt) * 1e-3 + 1.5e-4
                cmp("jndFrameTimeDiffMs", want_ft, tol)
        lo, med, hi = g("jndCI95LoDiffFps"), g("jndMedianDiffFps"), g("jndCI95HiDiffFps")
        if lo is not None and med is not None and hi is not None and not (lo - 1e-3 <= med <= hi + 1e-3):
            F.fail("sess.quest", name, [L], "jndMedianDiffFps %s outside its 95%% interval [%s, %s]"
                   % (fmt(med), fmt(lo), fmt(hi)))
        trials_q = g("staircaseTrials")
        mx, mn, stop = fnum(cfg_val(r, "maxTrials")), fnum(cfg_val(r, "minTrials")), fnum(cfg_val(r, "stopSD"))
        sdv = g("sd")
        if er == "maxTrials" and mx is not None and trials_q is not None and trials_q != mx:
            F.fail("sess.endReason", name, [L], "endReason=maxTrials after %s trials (maxTrials %s)" % (fmt(trials_q), fmt(mx)))
        if er == "converged":
            if mn is not None and trials_q is not None and trials_q < mn:
                F.fail("sess.endReason", name, [L], "converged after %s trials, before minTrials %s" % (fmt(trials_q), fmt(mn)))
            if stop is not None and sdv is not None and sdv > stop + 1e-3:
                F.fail("sess.endReason", name, [L], "converged with sd %s above stopSD %s" % (fmt(sdv), fmt(stop)))
        if r.get("jndRangeEdge"):
            F.warn("deliv.rangeEdge", name, [L], "%s: jndRangeEdge=%s - the estimate %s is pressed against the %s "
                   "of the range: a bound, not a threshold" % (p.label, r.get("jndRangeEdge"), fmt(jnd, 1),
                                                               "largest" if r.get("jndRangeEdge") == "top" else "smallest"))
        # frame-level recomputation
        if fr is not None and p.f0 is not None and fr.has("unscaledDeltaMs") and fr.has("pacedBy"):
            check_session_frames(S, r, p)
        # delivery and setup
        hz = g("displayHz")
        top = None
        if base is not None:
            mxd = fnum(cfg_val(r, "maxDiffFps"))
            top = base + mxd if direction == "up" and mxd is not None else base
        if hz and top and top > hz + 1:
            F.warn("deliv.display", name, [L], "%s shows up to %s fps on a %s Hz display: frames above the refresh "
                   "rate never reach the screen as separate images" % (p.label, fmt(top, 0), fmt(hz, 2)))
        bm = g("baseSideMeasuredFps")
        if base and bm is not None and bm < 0.97 * base:
            F.warn("deliv.base", name, [L], "%s: baseSideMeasuredFps %s against a %s fps cap - the machine did not "
                   "hold the base rate, so the differences shown were not the ones QUEST+ was told"
                   % (p.label, fmt(bm, 1), fmt(base, 0)))
        ae = g("alteredFrameTimeErrPct")
        if ae is not None and ae > 3:
            F.warn("deliv.altered", name, [L], "%s: altered-side frames ran %s%% longer than their cap" % (p.label, fmt(ae, 1)))
        lf, sl = g("liveFrames"), g("slowFrames")
        if lf and sl is not None and sl > 0.01 * lf:
            F.warn("deliv.slow", name, [L], "%s: %s of %s live frames took over 1.5x their cap's period (max %s ms)"
                   % (p.label, fmt(sl, 0), fmt(lf, 0), r.get("maxLiveFrameTimeMs")))
        gr, grs = fnum(cfg_val(r, "guessRate")), g("guessRateScreen")
        if gr is not None and grs is not None and abs(gr - grs) > 0.005:
            F.warn("deliv.guessRate", name, [L], "%s: cfg_guessRate %s but this screen implies %s" % (p.label, fmt(gr, 3), fmt(grs, 3)))
        if k == 0:
            aspect, res = screen_aspect(S)
            if res and abs(aspect - 16.0 / 9.0) > 0.02:
                F.warn("deliv.aspect", name, [L], "screen %s is %.2f:1, not 16:9 (1.78): guessRate %s was computed "
                       "for 16:9, and the wider view changes both the chance rate and the UFO's travel"
                       % (res, aspect, cfg_val(r, "guessRate")))
            vw = g("viewWidthWorld")
            if vw is not None and res and abs(vw - VIEW_W_PER_ASPECT * aspect) > 0.02 * vw:
                F.warn("new.viewWidth", name, [L], "viewWidthWorld %s but a 15 deg camera 100 units from the play plane "
                       "on %s gives %.2f" % (fmt(vw, 3), res, VIEW_W_PER_ASPECT * aspect))


def check_session_frames(S, r, p):
    F = S.F
    fr = S.frames
    c = fr.c
    name = S.sess.name
    L = r["_line"]
    f0, f1 = p.f0, p.f1
    dt, paced, tgt = c["unscaledDeltaMs"], c["pacedBy"], c.get("targetFps")
    mdx, mdy, ux, uy, t = c.get("mouseDeltaX"), c.get("mouseDeltaY"), c.get("ufoX"), c.get("ufoY"), c["timeSinceStartSec"]
    g = lambda col: fnum(r.get(col, ""))
    n_base = n_alt = slow = 0
    base_sec = 0.0
    base_err, alt_err, alt_rel = [], [], []
    mx_live = 0.0
    border = 0
    for i in range(f0, f1):
        d = dt[i]
        if d is None or not paced[i] or tgt is None or not tgt[i]:
            continue
        period = 1000.0 / tgt[i]
        mx_live = max(mx_live, d)
        if d > 1.5 * period:
            slow += 1
        if abs(d - 1.5 * period) < 2e-4:
            border += 1
        if paced[i] == "base":
            n_base += 1
            base_sec += d / 1000.0
            base_err.append(d - period)
        else:
            n_alt += 1
            alt_err.append(d - period)
            alt_rel.append((d - period) / period)

    def cmp(col, want, tol, why=""):
        have = g(col)
        if col not in S.sess.cols:
            return
        if want is None and have is None:
            return
        if (want is None) != (have is None) or abs(have - want) > tol:
            F.fail("sess.frames", name, [L], "%s %s=%s, recomputed from the PlayerLog %s%s"
                   % (p.label, col, fmt(have, 4), fmt(want if want is None else float(want), 4), why))

    cmp("frameCount", f1 - f0, 0)
    cmp("liveFrames", n_base + n_alt, 0)
    cmp("baseSideFrames", n_base, 0)
    cmp("alteredSideFrames", n_alt, 0)
    if base_sec > 0:
        want = n_base / base_sec
        cmp("baseSideMeasuredFps", want, max(2e-3, 2e-5 * want))
    cmp("baseFrameTimeSdMs", sample_sd(base_err), 2e-4)
    cmp("alteredFrameTimeErrPct", 100.0 * mean(alt_rel) if alt_rel else None, 2e-3)
    cmp("alteredFrameTimeSdMs", sample_sd(alt_err), 2e-4)
    have_slow = g("slowFrames")
    if have_slow is not None and abs(have_slow - slow) > border:
        F.fail("sess.frames", name, [L], "%s slowFrames=%s, recomputed %d (frames over 1.5x their cap)"
               % (p.label, fmt(have_slow, 0), slow))
    cmp("maxLiveFrameTimeMs", mx_live if (n_base + n_alt) else 0.0, 1.5e-3)
    cmp("playDurationSec", t[f1 - 1], 1.5e-3, " (the last frame's timeSinceStartSec)")
    if mdx is not None and mdy is not None:
        path = 0.0
        peak = 0.0
        for i in range(f0, f1):
            if mdx[i] is None or mdy[i] is None:
                continue
            px = math.hypot(mdx[i], mdy[i])
            path += px
            if dt[i] is not None and dt[i] > 0.001:
                peak = max(peak, px / (dt[i] * 0.001))
        cmp("totalMousePathPx", path, max(0.02, 2e-6 * (f1 - f0)) + 1e-5 * path)
        cmp("peakMouseSpeedPxPerSec", peak, max(0.02, 2e-3 * peak), " (max |mouseDelta| / unscaledDeltaMs)")
        pd = g("playDurationSec")
        if pd:
            want = g("totalMousePathPx") / pd
            cmp("avgMouseSpeedPxPerSec", want, max(0.02, 1e-4 * want), " (float32 totals: 1e-4 relative)")
        if peak and dt is not None:
            j = max((i for i in range(f0, f1) if mdx[i] is not None and dt[i] and dt[i] > 0.001),
                    key=lambda i: math.hypot(mdx[i], mdy[i]) / dt[i])
            if dt[j] < 0.25 * min((1000.0 / tgt[j]) if tgt and tgt[j] else 1e9, 1e9):
                F.warn("sess.peakMouse", name, [L], "%s peakMouseSpeedPxPerSec %s comes from a %s ms frame (PlayerLog "
                       "row %d, |delta| %s counts) - a short frame after a long one, not a fast hand"
                       % (p.label, fmt(peak, 0), fmt(dt[j], 4), fr.line[j], fmt(math.hypot(mdx[j], mdy[j]), 2)))
    if ux is not None and uy is not None:
        path = 0.0
        for i in range(f0 + 1, f1):
            if None in (ux[i], ux[i - 1], uy[i], uy[i - 1]):
                continue
            path += math.hypot(ux[i] - ux[i - 1], uy[i] - uy[i - 1])
        cmp("totalUfoPathWorld", path, max(0.02, 2e-3 * path), " (from the 4-dp positions)")


# ------------------------------------------------------------------------- phase structure

def check_structure_of_phases(S):
    F = S.F
    fname = S.shot.name if S.shot is not None else (S.frames.name if S.frames is not None else "-")
    pr = [p for p in S.phases if p.kind == "practice"]
    mains = [p for p in S.phases if p.kind == "main"]
    if S.gen == "v3" and len(pr) > 1:
        F.fail("phase.practice", fname, [], "%d practice phases; this generation plays one, before MAIN 1" % len(pr))
    if S.gen == "v3" and pr and mains and S.phases.index(pr[0]) > S.phases.index(mains[0]):
        F.fail("phase.practice", fname, [], "the practice phase comes after a main phase")
    if S.gen == "v4" and pr:
        lv = [p.level for p in pr]
        if len(set(lv)) != len(lv):
            F.warn("phase.practice", fname, [], "practice phases repeat a level type: %s" % lv)
        for q in pr:
            firsts = [m for m in mains if m.level == q.level]
            if firsts and S.phases.index(firsts[0]) < S.phases.index(q):
                F.warn("phase.practice", fname, [], "the %s practice comes after the first %s main block" % (q.level, q.level))
    # an unfinished last phase
    if S.shot is not None and S.frames is not None:
        for p in S.phases:
            if p.f0 is None:
                continue
            last_label = S.frames.c["roundNumber"][p.f1 - 1]
            if p.shots:
                last = inum(p.shots[-1].get("roundNumber"))
                if last is not None and last_label == last + 1:
                    F.note("phase.unfinished", S.frames.name, [S.frames.line[p.f1 - 1]], "%s ends inside round %d, "
                           "which has frames but no ShotLog row (the session was closed mid-round)"
                           % (p.label, last_label))


# ------------------------------------------------------------------------- report

def summary_line(S):
    F = S.F
    if S.skip_reason:
        return "SKIP %-22s %s" % (S.name, S.skip_reason)
    n_sess = len(S.sess.rows) if S.sess is not None else 0
    n_shot = len(S.shot.rows) if S.shot is not None else 0
    n_fr = S.frames.n if S.frames is not None else 0
    fails, warns = F.checks("FAIL"), F.checks("WARN")
    status = "FAIL" if fails else ("WARN" if warns else "OK  ")
    lvl = (S.level or "?") + ("*" if S.level_inferred else "")
    parts = ["%s %-22s %s %-8s %d block row(s), %d rounds, %d frames" % (
        status, S.name, S.gen, lvl, n_sess, n_shot, n_fr)]
    if fails:
        parts.append("fail: " + " ".join("%s(%d)" % kv for kv in sorted(fails.items())))
    if warns:
        parts.append("warn: " + " ".join("%s(%d)" % kv for kv in sorted(warns.items())))
    return " | ".join(parts)


def print_session(S, verbose):
    if S.skip_reason:
        print(summary_line(S))
        return
    lvl = S.level or "?"
    print("== %s  %s | level %s%s | %s" % (
        S.name, GEN_LABEL.get(S.gen, S.gen), lvl,
        " (inferred: %s)" % S.level_evidence if S.level_inferred else (" (%s)" % S.level_evidence if S.level_evidence else ""),
        S.folder))
    for p in S.phases:
        n = (p.f1 - p.f0) if p.f0 is not None else 0
        print("   phase %-12s %s  %d round(s), %d frame(s)" % (p.label, p.key[1], len(p.shots), n))
    S.F.print(verbose)
    print(summary_line(S))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folders", nargs="*", help="session folders (paths, or names under the logs root)")
    ap.add_argument("--data", default=None, help="Data folder (sessions under DATA/Logs) or a folder of session folders")
    ap.add_argument("--all", action="store_true", help="every session under --data, one summary line each")
    ap.add_argument("--verbose", action="store_true", help="print every message")
    ap.add_argument("--level-type", choices=("manual", "scroll", "auto"), default=None,
                    help="level to assume for logs without a levelType column (default: infer)")
    args = ap.parse_args(argv)

    data = Path(args.data) if args.data else DEFAULT_DATA
    root = data / "Logs" if (data / "Logs").is_dir() else data
    if args.folders:
        targets = []
        for f in args.folders:
            p = Path(f)
            if p.is_dir():
                targets.append(p)
            elif (root / f).is_dir():
                targets.append(root / f)
            else:
                print("no session folder %r (looked in %s)" % (f, root), file=sys.stderr)
                return 2
    elif not root.is_dir():
        print("no logs folder at %s" % root, file=sys.stderr)
        return 2
    elif args.all:
        targets = session_folders(root)
    else:
        newest = newest_session(root)
        targets = [newest] if newest else []
    if not targets:
        print("no session folders under %s" % root, file=sys.stderr)
        return 2

    failed = 0
    counts = {"OK": 0, "WARN": 0, "FAIL": 0, "SKIP": 0}
    for folder in targets:
        try:
            S = audit_folder(folder, args)
        except Exception as exc:          # a broken file must not stop an --all run
            print("FAIL %-22s audit crashed: %s: %s" % (folder.name, type(exc).__name__, exc))
            failed += 1
            counts["FAIL"] += 1
            continue
        if args.all and not args.verbose:
            print(summary_line(S))
        else:
            print_session(S, args.verbose)
            if len(targets) > 1:
                print()
        if S.skip_reason:
            counts["SKIP"] += 1
        elif S.F.checks("FAIL"):
            counts["FAIL"] += 1
            failed += 1
        elif S.F.checks("WARN"):
            counts["WARN"] += 1
        else:
            counts["OK"] += 1
    if len(targets) > 1:
        print("%d session(s): %d OK, %d with warnings only, %d FAILED, %d skipped"
              % (len(targets), counts["OK"], counts["WARN"], counts["FAIL"], counts["SKIP"]))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
