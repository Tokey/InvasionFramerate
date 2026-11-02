# What is in each file

The config files the study is driven from, and the logs it produces.
Config lives in `Data/`; logs land in `Data/Logs/<sessionId>_<runId>/`: three CSV logs plus
`SessionInfo_<id>.csv` (what the session ran on and with).

The session and shot logs additionally carry **`cfg_<column>` for every column of
`ExperimentConfig.csv`**, so either file states the conditions it was recorded under
without joining back to anything. The frame log does not (join on `blockIndex`).

The frame-time-stutter study that came before this one wrote different columns; its logs
are in the git history and `Analysis/build_db.py` still reads them.

---

## `Data/ExperimentConfig.csv` — one row per setting (6 rows: 3 level types x 2 directions)

Row order here is the settings' **identity**, not the order anyone plays them in. Row
number = `blockIndex` in the logs.

| Group | Columns |
|---|---|
| Level | `levelType` (`manual` = the mouse flies the UFO, camera still / `scroll` = the mouse flies it, the world scrolls / `auto` = the UFO flies itself, the participant only clicks; missing = the scene's World Scroll box), `scrollFollow` (`locked` / `handheld` / blank = Inspector) |
| Stimulus | `baseFps`; `direction` (`down` = altered side at base − Δ, `up` = base + Δ); `minDiffFps`, `maxDiffFps`, `diffStepFps` (the Δ grid QUEST+ searches: min, min + step, … max); `alteredSide` (`random` per round / `left` / `right`); `frameLimiter` (`precise` busy-wait / `unity` targetFrameRate) |
| Round bounds | `maxCrossingsPerRound` (0 = no cap), `roundTimeoutSec` |
| Scoring | `hitPoints`, `missPoints` (`hitPoints` also defines `isHit`) |
| QUEST+ | `threshCount` (θ grid over `minDiffFps`–`maxDiffFps`), `slopeMin/Max/Count`, `lapseMin/Max/Count`, `guessRate` (γ: 0.255 on 16:9 for closeRadius=6.0), `maxTrials`, `minTrials`, `stopSD` (fps) |
| Autopilot (auto rows) | `autoSpeed` (world units/s, 12), `autoPath` (`linear` constant speed with smooth turns / `sine`), `autoSweep` (share of the screen width, 0.9), `autoTurnSec` (0.6), `autoHeight` (0–1 of the flight band, 0.4), `autoBobAmp` (0.5 units) and `autoBobHz` (0.4): the slight sine wave, `autoStart` (`random` / `continue`). Blank on other rows. |

Not in the file, pinned in `StudyConfig`: `closeRadius` = 6.0, `crossingDeadZone` = 0.25,
`fireCooldown` = 0.25 s, `revealHoldSec` = 2 s, `readyBeatSec` = 0.5 s.

## `Data/PracticeRounds.csv` — the practice phases

`round, fpsA, fpsB`: one practice round per row, played in order before the first block of each
level type the session's order brings (before MAIN 1, and again before the first block of any other
level type). The higher rate is the round's base, the lower its altered side; which side of the tower
is altered is random. A round's reveal plays at its own base.

## `Data/LatinSquare.csv` — one row per session, cycled

`row, pos1 … posN`. Each cell is a 1-based row number in `ExperimentConfig.csv`; the row a
session uses is `((sessionId − 1) mod rowCount) + 1`. Comment lines (`#`) are ignored.

With 6 settings the shipped design is a **Williams** design of 6 rows (pos1 to pos6):
every setting takes every position equally often *and* every ordered pair of settings
occurs exactly once. Run participants in groups of 6 for full balance. Validated at load.

---

## Identity columns — first in all three logs

`sessionId`, `runId`, `blockIndex` (which setting), `blockOrdinal` (when in this session),
`latinRow`, `latinOrder` (e.g. `3;1;2`), `baseFps`, `direction`, `frameLimiter`, `levelType`
(`manual` / `scroll` / `auto`; logs from before it existed lack the column - infer it from how
far the UFO travelled, as `Analysis/audit_logs.py` and `level_analysis.py` do).

Practice rows carry `blockIndex = 0`, `blockOrdinal = 0`, blank `cfg_*`, the round's own
`baseFps` (each practice pair has its own base), and the `levelType` being practised: a practice
phase is `phase = practice` plus its `levelType`.

## `SessionLog_<id>.csv` — one row per block

| Group | Columns |
|---|---|
| Where / when | `displayHz`, `resolution`, `startIso`, `endIso`, `sessionDurationSec`, `playDurationSec`, `endReason` (`converged` / `maxTrials` / `abandoned`) |
| QUEST+ result | `jndDiffFps` (posterior **mean** of θ, fps), `jndAlteredFps` (= base ∓ jnd), `jndFrameTimeDiffMs`, `sd`, `priorSd`, `jndMedianDiffFps`, `jndModeDiffFps`, `jndCI95LoDiffFps`, `jndCI95HiDiffFps`, `jndRangeEdge` (`top` / `bottom` / empty: the estimate is pressed against an end of the Δ range), `slopeEstimate` (β̂), `lapseEstimate` (λ̂), `staircaseTrials`, `lastDiffFps` |
| Performance | `trials`, `shotsFired`, `hits`, `accuracy`, `score`, `roundsExpired`, `roundsCrossingCapped`, `avgRoundDurationSec` |
| Exploration | `avgCrossingsPerRound`, `shotsWithoutCrossing`, `avgSinceLastCrossingSec` |
| Shot geometry (world units on X) | `avgMissDistX`, `avgMissDistXMissesOnly`, `medianMissDistX`, `sdMissDistX`, `maxMissDistX` |
| Movement | `totalMousePathPx`, `avgMouseSpeedPxPerSec`, `peakMouseSpeedPxPerSec`, `totalUfoPathWorld` |
| Frame delivery | `frameCount`, `liveFrames`, `baseSideFrames`, `baseSideMeasuredFps`, `baseFrameTimeSdMs`, `alteredSideFrames`, `alteredFrameTimeErrPct`, `alteredFrameTimeSdMs`, `slowFrames`, `maxLiveFrameTimeMs` |
| Screen and motion | `viewWidthWorld` (the view's width at the play plane: screen x = `0.5 + (x − camX) / viewWidthWorld`), `guessRateScreen` (γ this screen implies for the level), `avgFirstCrossingSec`, `avgCrossingSpeed` (UFO speed at crossings), `avgShotUfoSpeed`, `medianSinceLastCrossingSec`, `avgMissAlongTravelX` (mean `missDistX × sign(ufoVelX)`: + = shots land past the tower, late) |

**Check frame delivery before trusting `jndDiffFps`.** The stimulus is the frame rate, so a
block whose `baseSideMeasuredFps` fell short of `baseFps`, or whose `alteredFrameTimeErrPct`
is well above 0, presented smaller differences than QUEST+ was told.

**Drop or bound any block with `jndRangeEdge` set.** A participant who never locates the
boundary piles the posterior against the largest Δ, its SD collapses there, and the stop rule
reports `converged` within ~20 rounds on a value that only means "above the range".

`jndDiffFps` is the posterior mean. The median/mode/CI are there because the θ posterior sits
on a bounded grid and skews near its ends, where mean ± 2·`sd` is not the interval the
posterior actually assigns 95% to. Mean far from mode ⇒ the estimate is pressed against the
grid and should not be read at face value.

## `ShotLog_<id>.csv` — one row per **round**

| Group | Columns |
|---|---|
| Phase | `closeRadius`, `phase` (`practice`/`main`), `phaseStartIso` (absolute time = `phaseStartIso + timeSinceStartSec`) |
| Position in run | `roundNumber`, `timeSinceStartSec` |
| The split presented | `diffFps` (the stimulus), `alteredFps`, `alteredSide` (`Left`/`Right` of the tower) |
| What was delivered | `measuredBaseFps`, `measuredAlteredFps` (blank if the UFO never went there), `baseSideSec`, `alteredSideSec`, `lateFrames` (precise limiter: frames that overran) |
| Exploration | `crossings`, `lastCrossingAtSec`, `sinceLastCrossingSec` |
| Outcome | `outcome` (`shot` / `expired` / `crossingCap`), `playerFired`, `isHit`, `countedByStaircase`, `totalScore` |
| Timing | `trialStartSec` (firing handed back at GO!), `firedAtSec`, `roundDurationSec` |
| Geometry | `hitX`, `missDistX` (`hitX − towerX`), `towerX`, `ufoY`, `side` (the side the shot was fired from) |
| Posterior after this round | `threshDiffFps`, `sd`, `slopeEstimate`, `lapseEstimate` (blank on practice rows) |
| Screen and motion | `camX` (camera rest X), `ufoViewportX`, `towerViewportX` (0 = left edge, 1 = right), `ufoVelX` (UFO velocity at the response over the last 0.1 s, + = right), `ufoXAtStart`, `firstCrossingAtSec`, `meanCrossingSpeed`, `lastCrossingSpeed` |

**Any refit of the psychometric function needs `phase = 'main'` AND
`countedByStaircase = 1`.** Practice rows are logged in full but never reached the posterior.

## `PlayerLog_<id>.csv` — one row per rendered frame

| Group | Columns |
|---|---|
| Phase | `phase`, `phaseStartIso` |
| Frame | `roundNumber`, `frameIndex`, `timeSinceStartSec`, `unscaledDeltaMs` |
| Pacing | `targetFps` (the cap that paced THIS frame — the one `unscaledDeltaMs` measures), `pacedBy` (`base` / `altered` / empty between rounds) |
| Round | `diffFps`, `alteredSide` (both empty between rounds), `side` (the UFO's side of the tower), `crossed` (1 on a crossing frame) |
| Input | `mouseX`, `mouseY`, `mouseDeltaX`, `mouseDeltaY`, `leftButtonDown`, `leftButtonPressed`, `fireKeyDown` |
| World | `ufoX`, `ufoY`, `towerX` |
| Shot | `shotFired` (a response was logged — *not* the same as `leftButtonPressed`), `shotHitX` |
| Camera | `camX` (rest X this frame) |

The cap is chosen at the end of a frame and paces the next one, so on the single frame after
a crossing `side` already shows the new side while `targetFps` / `pacedBy` still show the old
cap - and on the frame a round's split opens, `pacedBy` is still blank while `alteredSide` is set.
Every GO! is logged, the first of a phase included: the phase clock starts with it.

## `SessionInfo_<id>.csv` — key, value

What the session ran on and with, written under the first prompt: `session.*`, `app.*` (version,
Unity version, editor or build), `system.*` (CPU, GPU, memory), `display.*` (size, refresh rate),
`quality.*`, `input.*`, `config.*` (path, size, last write and SHA-256 of ExperimentConfig.csv,
LatinSquare.csv and PracticeRounds.csv), and `<Type>.<field>` for every public setting of the
game's components in the scene - e.g. `UIManager.showDebug` (must be `false` for participants),
`CameraRig.parallax`, `Weather.kind`, `GameManager.worldScrollPeriod`.

---

## Joining

`Analysis/build_db.py` builds `<sessionId>_<runId>.db` from the CSVs, hoisting everything
constant across a `(sessionId, runId, blockIndex, phase, levelType)` group into a `block` table
(`baseFps` stays on every row too: practice rounds each have their own), SessionInfo into
`session_info`, and exposing `v_session` / `v_shot` / `v_frame` views in the original flat shape.
`--verify` checks the views give the CSVs back cell for cell. Run
`python Analysis/build_db.py --schema` for the schema and example queries, including
delivered-vs-target frame rates per round and thresholds per level type.
