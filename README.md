# Invasion — Frame Rate

A small Unity game I built to measure how big a **frame-rate difference** has to be before people notice it. In research terms that's the **JND**, the just-noticeable difference. The game finds each person's threshold on its own, using an adaptive method called **QUEST+**.

You fly a UFO with the mouse over a city at night. One of the towers standing in front of the skyline splits the city in two: on one side of it the game runs at one frame rate, on the other side at a different one. Any tower can be the one, and they all look alike, so the only way to find it is by the motion. Your job is to find where the motion changes and shoot that tower.

Each block of the study is one of the [kinds of level](#level-types), set per row of the config. The current study uses two: **manual laser** (you fly, and shoot the tower where the frame rate changes) and **shockwave** (the UFO flies an infinity sign by itself, the whole screen's frame rate changes at random moments, and you fire as soon as you notice). **Scroll** and **auto** levels are still in the game for other configs.

> The previous version of this project measured frame-time **stutters** with two weapons (laser and shockwave). That study is in the git history up to commit `a61a84f`.

---

## How the study works

Everything is set and calculated in **frame time** (FT). Frame rates are only how it is shown and capped.

| Block | Base frame time | FT direction | Δ range (step 0.5 ms) | Altered frame time | As a frame rate |
|---|---|---|---|---|---|
| 500 fps | **2 ms** | **up**: frames get longer | 0.5 to 48 ms | 2.5 to 50 ms | 400 down to 20 fps |
| 60 fps | **16.67 ms** | **down**: frames get shorter | 0.5 to 14.67 ms | 16.17 to exactly 2 ms | 62 up to 500 fps |

Note the direction is the other way round from the frame rate: frame time going **up** is the frame rate going **down**.

- **Why those limits.** 16.67 ms stops at 2 ms because that is one frame of the 500 Hz display. 2 ms stops at 50 ms because the largest pilot threshold in the levels now used was about 11 ms (18 in the scroll level), so 48 ms is well past anything a participant needs, and beyond about 22 ms whole-number frame rates are already further apart than a 0.5 ms step.
- **Whole-number caps.** `Application.targetFrameRate` only takes whole numbers, so each Δ shows at the nearest whole frame rate. QUEST+ and the logs use the frame-time difference that rate really gives.
- **QUEST+ picks Δ every round**, bigger after a miss and smaller after a hit, in milliseconds of frame time.
- **Laser (manual):** a hidden tower splits the screen. One side runs at the base frame time, the other Δ away. Shooting the tower means they noticed.
- **Shockwave:** the UFO flies itself, and the whole screen flips between the base and the altered frame time at random moments. A press inside the response window after a flip means they noticed.
- **A shot fired before the frame rate has changed in that round never reaches QUEST+**, in either level: there was nothing to answer. The screen says TOO EARLY! in both. It is still logged, with `outcome` = `early`. It does not change `guessRate`. In a laser level it scores nothing, adds no tally pip, and ends the round, and the next round presents the same difference.
- **After every round** the reveal shows each frame rate as a big number with its frame time underneath.
- **Practice first**, once per kind of level, from `Data/PracticeRounds.csv`. Practice never feeds QUEST+.
- **Then four main blocks**, MAIN 1/4 to MAIN 4/4, in Latin-square order.

## What you need

- **Unity 6000.5.0f1**. URP and the new Input System come with the project.
- A **Windows PC that can actually hold every frame rate in the config**, 500 fps included. The frame rate *is* the stimulus: if the machine only manages 340 fps, a "500 vs 400" round shows no difference at all. The Editor adds a lot of overhead (the stutter study's Editor sessions only reached ~300–360 fps at a 500 cap), so check a **build**. The logs record what each side really got (see `baseSideMeasuredFps` and `measuredBaseFps` / `measuredAlteredFps`), and the Console warns at the end of any block that fell short.
- A **monitor fast enough for the rates you're testing**: 500 Hz for this config. vSync is off, so frames above the display's refresh rate show up as tearing rather than smoother motion. At startup the Console warns about any block that asks for more than the connected display can show, and the session log records the refresh rate as `displayHz`.
- A **16:9 monitor**. The chance level (see `guessRate`) is worked out for 16:9.
- A mouse that reports at **1000 Hz or more** (most gaming mice; it's set in the mouse's own software). The UFO moves by the mouse's reports, so at 500 fps a standard 125 Hz office mouse would move it only every fourth frame, in steps, whatever the game renders. At 1000 Hz every frame gets fresh movement.
- **Python 3**, but only for the analysis scripts. Builds bring their own Python for the post-session database. The DB is created from the CSV so this is non-critical.

## Setup

1. Clone the repo and open the folder in Unity Hub with Unity 6000.5.0f1.
2. Open `Assets/Scenes/InvasionScene.unity`.
3. Look over `Data/ExperimentConfig.csv` and `Data/LatinSquare.csv`. The `Data` folder sits next to `Assets`, not inside it. Everything in them is explained below.
4. Hit **Play** to try it in the Editor.
5. For real sessions, make a **Windows build** (File → Build Profiles → Windows → Build). Always run participants on a build.

After a build, the build folder should look like this:

```
Invasion.exe
Data/ExperimentConfig.csv   copied from the project automatically
Data/LatinSquare.csv        copied from the project automatically
Data/PracticeRounds.csv     copied from the project automatically
Analysis/build_db.py        for the post-session database
Python/                     a private Python (Windows builds only)
```

The Console prints `[Build] Copied ExperimentConfig.csv → ...` when the copy worked.

> **Heads up:** every build copies the project's `ExperimentConfig.csv` and `LatinSquare.csv` over the ones in the build folder. If you edit the build's copies, make the same edit in the project too, or the next build will undo it.

## Debug mode

The debug readout is set per row by the `showDebug` column (0 for every row now). It sits in the top-right corner and shows:

```
FPS JND | MAIN 2/4 | scroll | base 60 up | diff 0.5-14.5 ms step 0.5 | precise limiter
LIVE  diff 3.51 ms: 60 vs 76 fps, altered on RIGHT   (L 60 | R 76)
measured  L 59.9 | R 76.0   crossings 4   late 0
now RIGHT @ 76 fps cap | spin 12.3 ms
QUEST+ trial 6/50  threshold 3.42 ms  SD 1.06  slope 2.40  lapse 0.021  prev diff 5.04
round 7/15 s
```

(Plain ASCII on purpose: the HUD font has nothing beyond it, so Greek letters or arrows would draw as blanks.)

That's the block and its level type, the round's split (which side is altered and both target rates), what each side actually measured, which side the UFO is on and the cap in force, how much spare time the precise limiter had to wait out (`spin`, near 0 = the PC isn't keeping up; `late` = frames that overran), and the QUEST+ state.

**Keep it off for participants.** It spells out the very difference they're being asked to find.

There is also a live QUEST+ convergence plot (`GameManager` object → `StaircaseConvergencePlot` → **Show Plot**). It's off: it draws with IMGUI every frame, and in a frame-rate study that's not free.

To check the end-of-round reveal without playing, use **Tools ▸ JND ▸ Render FPS Reveal Previews**. It renders the reveal into `Temp/FpsRevealPreviews` at 15 tower positions, from 0.03 to 0.97 of the screen width. That runs past the game's own range, which never places the tower outside 0.05–0.95.

## Running a participant

1. Start `Invasion.exe`.
2. The session opens with the **PRACTICE** instruction screen. The participant presses **Space**, sees **GO!**, and plays the five practice rounds. Then each main block opens with its own screen (**MAIN 1/4**, **MAIN 2/4**, …) and **Space** starts it. The first time the order brings a new kind of level, its own **PRACTICE** comes first. Every screen's instructions are the ones for its level (`ExperimentDirector` → `manualInstruction` / `scrollInstruction` / `autoInstruction` / `shockwaveInstruction`).
3. Controls: **mouse** to fly, **left click** (or **Space**) to fire. In an auto level the mouse flies nothing: **left click** (or **Space**) fires when the UFO is over the boundary. In a shockwave level the mouse flies nothing either: **left click** (or **Space**) fires when the frame rate changes.
4. After the last block the game shows "Thank you", saves everything and closes itself.

Logs go to `Data/Logs/<sessionId>_<runId>/` next to the .exe:

```
Data/Logs/3_GTZ3/
  SessionLog_3_GTZ3.csv   one row per block (the threshold is here: jndFrameTimeDiffMs)
  ShotLog_3_GTZ3.csv      one row per press
  PlayerLog_3_GTZ3.csv    one row per frame
  SessionInfo_3_GTZ3.csv  what it ran on and with: build, machine, display, config hashes, scene settings
  3_GTZ3.db               the same data as SQLite, built at the end
```

`sessionId` is the participant number. `runId` is a random 4-character tag, so two runs can never overwrite each other. Every column is explained in [Log files](#log-files).

**Participant numbers** come from `Data/SessionState.csv`:

```
nextSessionId,lastUpdatedIso
4,2026-10-01T18:13:22-04:00
```

- The number only goes up when a session **finishes**. If someone quits halfway, the next run gets the same number and the same block order. The half run stays in its own folder.
- To skip or redo a number, just edit `nextSessionId`.
- Builds never copy this file, so each lab PC keeps its own counter.

## The Data folder

```
Data/
  ExperimentConfig.csv   the settings, one row per setting
  LatinSquare.csv        the block order for each participant
  PracticeRounds.csv     the practice phase's frame-rate pairs
  SessionState.csv       the next participant number
  Logs/                  one folder per session
```

- In the Editor it's next to `Assets`. In a build it's next to the .exe. You can edit these files between participants without rebuilding.
- If `ExperimentConfig.csv` is missing, the game writes the [default config](#the-current-config-4-blocks). If `LatinSquare.csv` is missing, it writes a balanced square for however many settings the config has.
- Empty lines and lines starting with `#` are ignored. Use a dot for decimals. Lists inside one cell use `;`, because `,` separates the columns.
- Excel works, but make sure it saves with commas and dot decimals. A plain text editor is safer.

---

## ExperimentConfig.csv

One header row, then **one row per setting**. Each row is one block, and each block is its own QUEST+ run.

The row number is the setting's ID. The first row under the header is setting **1**, the next is **2**, and so on. The Latin square refers to settings by these numbers, and the logs save them as `blockIndex`.

**The row order is not the play order.** The play order comes from `LatinSquare.csv`.

### The current config: 4 blocks

| Row | Level | Base FT | FT direction | Δ range, ms (step) | threshCount | γ | stopSD, ms |
|---|---|---|---|---|---|---|---|
| 1 | shockwave | 2 | up | 0.5 to 48 (0.5) | 96 | 0.36 | 1 |
| 2 | shockwave | 16.667 | down | 0.5 to 14.667 (0.5) | 30 | 0.36 | 1 |
| 3 | manual laser | 2 | up | 0.5 to 48 (0.5) | 96 | 0.256 | 1 |
| 4 | manual laser | 16.667 | down | 0.5 to 14.667 (0.5) | 30 | 0.256 | 1 |

The other settings every row shares: `slope` 1 to 7 (10 values), `lapse` 0 to 0.06 (4 values), `maxTrials` 50, `minTrials` 8, `alteredSide` random, `frameLimiter` unity, `hitPoints` 100, `missPoints` -10.

| Setting | Laser rows | Shockwave rows |
|---|---|---|
| Round limits | `maxCrossingsPerRound` 15, `roundTimeoutSec` 20 | none; `maxFlipsPerRound` 5 |
| Timing | | `swFlipDelayMinSec` 1, `swFlipDelayMaxSec` 3.5, `swWindowSec` 0.9, `swMaxEarlyPerRound` 1 |
| Flight | the mouse | `autoSpeed` 20, `autoPath` linear (the infinity sign), `autoSweep` 0.92, `autoTurnSec` 1, `autoHeight` 0.38, `autoBobAmp` 0.25, `autoBobHz` 1.1, `autoStart` random |

**About stopSD 1.** Simulated from the pilot, it saves about 9 of 33 minutes over running all 50 trials, with only a little more error in the threshold, and the same value and step suit both base frame times: see [the assessment](https://claude.ai/artifact/Q3XkNFLr6HtzycUfza9oja) (`Analysis/sd_from_pilot.py` reproduces it). It rests on one pilot participant, and the pilot has no shockwave block, so check it against the first real sessions.

**guessRate for shockwave** is simply the window over the spread of the wait: 0.9 / (3.5 - 1) = **0.36**. Early presses are deliberately left out of it.

> **Note:** if the file goes missing, the game writes these same four rows (`DefaultCsv()` in `StudyConfig.cs`). A config from before this one, with `baseFps` and `direction` (of the frame rate), still loads. Shockwave timing under older column names is not read: the Console reports an error, and that row uses the defaults (1 to 3.5 s, 5 flips).

### Columns

**Default** is what a blank cell falls back to; where this study's rows use something else, the cell says so.

**The level** (see [Level types](#level-types))

| Column | What it does | Default |
|---|---|---|
| `levelType` | `manual`: the mouse flies the UFO. `scroll`: the mouse flies it while the world scrolls. `auto`: the UFO flies itself. `shockwave`: the UFO flies itself while the whole screen's frame rate flips between the base and the altered rate after a random wait. | If the column is missing or blank: `scroll` when `GameManager` → **World Scroll** is ticked in the scene, else `manual`. |
| `scrollFollow` | Scroll levels: `locked` (the UFO always exactly mid-screen) or `handheld` (the camera follows it like a camera operator, see `GameManager` → **World Scroll Handheld Follow**). Blank leaves the Inspector's choice. | blank |

**The autopilot** (auto and shockwave levels; leave them blank on other rows)

| Column | What it does | Default |
|---|---|---|
| `autoSpeed` | How fast the UFO flies, in world units a second, at every point of the path (the view is about 47 wide at the play plane). For `sine` it is the mean speed. | 20 |
| `autoPath` | `linear`: an infinity sign flown at one constant speed, lobes included. `sine`: level, the UFO's X following a sine wave, slowing to a stop at each end. | linear |
| `autoSweep` | How much of the screen's width the path covers, centred. At 0.92 the ship stays in view through its turns. Keep it at least 0.6 in an auto level, the spawn band of the tower. | 0.92 |
| `autoTurnSec` | `linear` only: seconds a lobe of the infinity sign takes. Speed × this sets the lobes' height: at 20 u/s and 1 s, about 4 units either side of the flight height. | 1 |
| `autoHeight` | The infinity sign's middle inside the flight band: 0 = bottom, 1 = top. | 0.38 |
| `autoBobAmp`, `autoBobHz` | The very slight sine wobble on top of the path: amplitude in world units and frequency. 0 flies clean. | 0.25, 1.1 |
| `autoStart` | Where each later round's flight starts, with the ship hidden under the veil: `random`, a random point of the lap; `continue`, where the last round stopped. A phase's first round always starts from wherever the ship is, because it is in view. | random |

**Shockwave** (shockwave levels; leave them blank on other rows)

| Column | What it does | Default |
|---|---|---|
| `swFlipDelayMinSec`, `swFlipDelayMaxSec` | The random wait before every flip, in seconds: from GO! or TRY AGAIN!, from a forgiven early press, and from a closed window. With `swWindowSec` it sets `guessRate`. | 1, 3.5 |
| `swWindowSec` | How long after a flip a press counts as `detected`, in seconds. | 0.9 |
| `swMaxEarlyPerRound` | Early presses a main round forgives; the next one ends the round, uncounted. Practice forgives them all. | 1 |
| `maxFlipsPerRound` | Unanswered flips before the round ends (`flipCap`, a counted miss). Practice stops at 3. | 5 |

**The debug readout**

| Column | What it does | Default |
|---|---|---|
| `showDebug` | `1` shows the debug readout on screen. Keep `0` for participants. | 0 |

**The stimulus**

| Column | What it does | Default |
|---|---|---|
| `baseFrameMs` | The reference frame time, ms: 2 is 500 fps, 16.667 is 60 fps. One side always runs at exactly this. Between rounds the whole game runs at it too. | 2 / 16.667 |
| `ftDirection` | `up`: the altered frame time is Δ longer (a lower frame rate). `down`: Δ shorter (a higher one). | up / down |
| `maxDiffMs` | The biggest Δ QUEST+ may use, in ms of frame time, and the top of its threshold range. It's clamped so the altered side stays at 1 fps or above on a `down` row, and keeps a frame time above zero on an `up` row. | 14.5; this study 48 / 14.667 |
| `minDiffMs` | The smallest Δ QUEST+ may use (ms), and the bottom of its threshold range. | 0.5 |
| `diffStepMs` | Spacing of the Δ values QUEST+ may use (ms): `minDiffMs`, `minDiffMs + step`, … up to `maxDiffMs`. If `maxDiffMs − minDiffMs` is not a multiple of it, the last step is shorter, so the range still ends exactly at `maxDiffMs`. | 0.5 |
| `alteredSide` | Which side of the tower is altered: `random` (drawn every round), `left` or `right`. | random |
| `frameLimiter` | How the frame rate is enforced: `unity` or `precise`. See [The frame limiter](#the-frame-limiter). | unity |

> **Whole-number frame rates.** `Application.targetFrameRate` only takes whole numbers, so each step shows the nearest whole fps, and QUEST+ and the logs use the frame-time difference that rate really gives (`diffMs`), not the nominal step. Above about 44 fps one fps is worth less than a step, so the steps stay close to even. Below that, steps start landing on the same rate, and those count once. That makes the slow end of the 500 fps rows sparser than the step: 65 different values out of 96 steps, even up to about 22 ms and up to 2.4 ms apart at the far end.

**Round limits** (on a laser row keep at least one of them above 0, or a round could run forever; a shockwave row sets both to 0 and is bounded by `maxFlipsPerRound`)

| Column | What it does | Default |
|---|---|---|
| `maxCrossingsPerRound` | Most tower crossings one round may have; the next crossing ends the round as a miss. `0` means no limit. Keep it generous: crossing the tower is how people compare the two rates. | 0; this study 15 (laser), 0 (shockwave) |
| `roundTimeoutSec` | Time limit per round, in seconds. No shot by then means a miss. `0` turns it off. | 15; this study 20 (laser), 0 (shockwave) |

**Score**

| Column | What it does | Default |
|---|---|---|
| `hitPoints` | Points for a hit. This also defines what a hit is, so keep it above `missPoints`. | 100 |
| `missPoints` | Points for a miss or a round that ran out. Negative means a penalty. | -10 |

**QUEST+** (the part that finds the threshold)

QUEST+ keeps a big table of possible answers for each participant and picks the Δ that tells it the most.

| Column | What it does | Default |
|---|---|---|
| `threshCount` | How many threshold values it considers, spread over the same range. Keep them no further apart than a step, `(maxDiffMs − minDiffMs) / diffStepMs + 1`; on a coarser grid the estimate snaps to the nearest value, and the Console warns. | 50; this study 96 / 30 |
| `slopeMin`, `slopeMax`, `slopeCount` | How sharply noticing goes from "never" to "always". | 1 – 7, 10 values |
| `lapseMin`, `lapseMax`, `lapseCount` | How often people miss even huge differences (blinks, attention). | 0 – 0.06, 4 values |
| `guessRate` | Chance level, γ: how often a blind shot lands on the tower anyway. It's **fixed**, never estimated. See below. | this study 0.256 (laser), 0.36 (shockwave) |
| `maxTrials` | Most responses QUEST+ uses in one block. | 50 |
| `minTrials` | No early stop before this many. | 8 |
| `stopSD` | Stop early once QUEST+ is sure enough: the uncertainty of the threshold (posterior SD, in ms) is at or below this. At 1, simulated from the pilot, a manual block stops after a median of 41 to 50 rounds and a shockwave block after about 30. | 0.5; this study 1 |

A block ends when `stopSD` is reached (after `minTrials`) or when it hits `maxTrials`. The SessionLog saves which one happened as `endReason`: `converged` or `maxTrials` (or `roundCap`, see [Shockwave](#shockwave)).

> **Watch for estimates stuck at the edge of the range.** Someone who never finds the boundary piles QUEST+'s belief against the largest Δ. Its SD collapses there, so the block "converges" in under 20 rounds on a number that only means "somewhere above the range". The session log flags this as `jndRangeEdge = top` (or `bottom` at the other end), and the Console warns. Treat such a block's threshold as a bound, not an estimate.

## PracticeRounds.csv

The practice phase, played before the first block of each level type in the session's order (so before **MAIN 1**, and again before the first block of any other kind of level; `ExperimentDirector` → **Practice Each Level Type** off plays it once, before MAIN 1, as before). One row per round, in order:

```
round,fpsA,fpsB
1,30,60
2,60,500
3,60,144
4,144,500
5,240,500
```

- Each round shows the two frame rates on either side of the hidden tower. The higher one plays the role of the base, and which side gets the lower one is random, like a main round. Its reveal plays at its own base rate; the next pair's starts at the next GO!.
- The first column (`round`) is just a label. Order in the file is play order.
- Practice runs in the level, limiter, round limits and scoring of the main block it comes before. On shockwave, a practice round ends after 3 unanswered flips rather than 5, and every early press is forgiven. It never feeds QUEST+. Each practice phase is logged with `blockIndex` 0, `phase` = practice and the `levelType` it practised.
- During practice, a line on screen says `FIND WHERE THE FRAME RATE CHANGES`, and it flashes `FRAME RATE CHANGED — FIRE HERE` every time the UFO crosses the tower. On shockwave it says `WAIT FOR THE FRAME RATE TO CHANGE`, and flashes `FRAME RATE CHANGED — FIRE NOW!` on each flip. Never in main rounds.
- Delete every row to skip practice. If the file is missing, the game writes these five rounds.

### The frame limiter

The `frameLimiter` column picks how each frame-rate cap is enforced, per row:

- **`unity`** (default): `Application.targetFrameRate`, Unity's own limiter. In the Editor it holds 60 fps frames to about ±0.6 ms.
- **`precise`**: a busy-wait hooked into the very start of Unity's frame, the same place Unity's own limiter waits, before time is sampled and before input events are processed. Each frame is released exactly one period after the previous one, so frame times land within microseconds of the target, and it adds no input lag beyond what any frame cap implies. The cost: one CPU core runs at 100% while it waits. That's fine on a lab PC.

Either way vSync is off. To compare them, run the same row twice with different limiters and look at `baseFrameTimeSdMs` / `alteredFrameTimeSdMs`, or run `Invasion.exe -jndBenchmark` once per setting (edit the build's own `Data/ExperimentConfig.csv` in between). Add `-jndNoBackdrop`, `-jndNoForeground` or `-jndNoParallax` to benchmark (or `-jndCapture`) without that part of the scene and see what it costs.

Measured on the dev PC (i9-9900K, RTX 3080, windowed 1600×900, `-jndBenchmark`), same build, 500 fps block, base side, debug readout off as a participant has it:

| | `unity` | `precise` |
|---|---|---|
| Mean frame rate delivered | 491.5 fps | 499.9 fps |
| 99th-percentile frame time | 2.53 ms | 2.01 ms |
| Frames late by more than 0.05 ms | 9.8% | 0.5% |

Both reach 500. The frame rate used to fall short because of the scene, not the limiter: the old fog bank, full-resolution SSAO, forced depth and opaque copies, and a skyline rebuilt from prefabs every round. The current scene, with the out-of-focus city, the grass, the parallax swing and all of the scene's own animation, needs about 0.77 ms a frame (about 1,270 fps uncapped on that PC). With that headroom, `precise` paces the stimulus more tightly. Unity's limiter is late on about one frame in nine whatever the scene; that is its own pacing, not the work.

### Setting `guessRate`

γ matters a lot. If it's wrong, the thresholds come out wrong.

γ is the chance that a blind shot lands on the tower. At startup the game works it out from the screen and prints it in the Console (or in `Player.log` in a build), in a `[GuessRate]` block that ends with `>>> guessRate : <number>`. Use that number. It's about 0.256 on a 16:9 screen (the UFO can fly until its centre reaches either edge, so a shot can land anywhere across the view), and it changes with the screen shape, which is why the monitor should be 16:9. If a row's `guessRate` is far from it, the Console says so. The SessionLog records the value the screen implied for each block as `guessRateScreen`.

Manual and scroll levels share that one γ (a scroll round's boundary is dealt as far from the UFO, in mouse travel, as without the scroll). An **auto** level has its own, printed once per distinct autopilot setting: there a blind response is a click at a random moment, so it lands wherever the autopilot has the UFO then, and γ is the share of tower positions the UFO is over, averaged over one sweep there and back. With `linear` at 8 units/s and `autoSweep` 0.9 it was **0.248** on 16:9, before the infinity-sign path: use the printed value. A narrower sweep raises it (the UFO spends more of its time over the towers' band): 0.267 at 0.8. A `sine` path lowers it, because the UFO lingers at the ends: 0.216.

### Fixed in code, not in the CSV

These define the task itself, so they live in `Assets/Scripts/Experiment/StudyConfig.cs`:

| Setting | Value |
|---|---|
| Hit radius (`closeRadius`) | 6.0 world units on each side of the tower (giving γ = 0.256 on 16:9). Changing it changes γ. |
| Dead zone around the tower | 0.25 world units. The UFO keeps its side until it's this far past the boundary, so hovering on the line can't flicker the frame rate. |
| Fire cooldown | 0.25 s |
| Reveal after a shot | 2 s |
| "GO!" before each round | 0.5 s |
| Lockout after a forgiven early press (`swEarlyLockoutSec`) | 1 s |
| Unanswered flips before a shockwave practice round ends (`practiceMaxFlipsPerRound`) | 3 |

### Adding or removing a setting

1. Add or remove a row in `ExperimentConfig.csv`.
2. Make `LatinSquare.csv` match the new number of settings. The easy way is to delete it. On the next start the game writes a balanced square for the new count (for an odd number of settings it writes twice as many rows, which is what balancing needs).

---

## What a round looks like

1. **GO!** The veil lifts and the split switches on: from now until the shot, the side of the tower the UFO is on decides the frame rate.
2. The participant flies back and forth and shoots where they think the tower is. In an auto level the UFO flies back and forth by itself and the participant clicks when it is over the place they think it is.
3. **The reveal.** The tower standing on the boundary lights up green, its beam shoots up out of it, and its hit-zone lines spread out. The two frame rates slide out from behind the beam as a pair, one on each side of it, high in the sky between the beam and the hit-zone lines, so they read as one comparison across the boundary: `500 | 85`. The altered side's number rolls away from the base rate to where it ended up, and the rates actually measured on each side fade in underneath. Meanwhile each side of the screen slowly takes on its own colour, cool for the faster side and warm for the slower one. The colour starts fully transparent and is faint inside the hit zone, so the zone stays a clear window exactly as wide as the target. It is strongest just outside the zone lines and leaves the city clear.

   It looks the same wherever the tower is. The hit zone is wide enough to hold a three-digit rate at full size, so the pair keeps its size and place beside the beam across most of the screen. If the screen cuts into the zone, both numbers shrink together to fit the narrower side and stay the same size. On a 16:9 screen, at the default spawn range (the middle 60% of the view), the zone always fits on screen, so that doesn't happen. Each side is clipped to its own side of the tower line, so nothing ever crosses it.

   The pacing fits the hold. In the study's 2-second hold, the roll and the measured rates have landed within the first second, and the colour keeps deepening until just before it all fades.
4. The scene resets under a veil (`SAVING…`, `READY…`). The logs are written first. Then the scene changes once: as the camera pans to its new view, the old row of towers dissolves into the new one, dealt around the next boundary, and the reveal's beam and glow fade out over the tower they marked. The new row has settled before GO! lifts the veil. Everything between rounds runs at the base frame rate.

## Level types

Every row of the config picks one with `levelType`. Manual, scroll and auto retain the spatial tower task. Shockwave keeps the automatic flight but makes the response temporal: each flip switches the whole screen between the base rate and the round's altered rate, and opens the response window. The phase, the cap and the flip count are written to `PlayerLog`.

### Manual

The original level. The mouse flies the UFO, and the camera holds still during a round. Between rounds, under the veil, the camera pans a little (up to `GameManager` → `cameraPanRange`, 6 units either side of its origin), so the boundary isn't tied to one place on the screen.

### Scroll

The mouse flies the UFO, and the camera follows it, so the UFO holds the middle of the screen and the city slides past under it, without end. Whenever any block of the config scrolls, the city, the towers, the grass and the sky are built endless at startup: one stretch of them repeats seamlessly. The boundary repeats too, once every `GameManager` → `worldScrollPeriod` (96 units, about two screens), and the frame rate swaps sides at every copy (base | altered | base | altered …), so wherever the UFO goes it meets one. Each round's boundary is dealt as far from the UFO, in mouse travel, as it would be without the scroll. The round is scored, revealed and logged against the copy nearest where it ended, so `towerX`, `side` and `alteredSide` read as they do around a single boundary. `scrollFollow` picks the camera: `locked` keeps the UFO exactly mid-screen; `handheld` follows like a camera operator, so the UFO leads a little as it flies and drifts back to the middle when it stops.

Whole-screen motion shows the frame rate far more than a UFO crossing a still scene, so don't compare scroll thresholds with the other levels' without saying so.

### Auto and shockwave flight

The UFO flies an **infinity sign** by itself, at one constant speed at every point of the lap, the turns included. So every frame moves it the same distance on screen, and a change of frame rate shows equally wherever the ship is: slowing into a turn is what would hide it. The heading eases through each lobe, so it never lurches, and the hull is drawn slightly bigger at the top of the path and smaller at the bottom, so it reads as flight in depth. A very slight sine wobble rides on top, and the camera's hand-held wander is 3.5 times stronger than in a level the participant flies.

- **It never teleports in view.** When Space starts a phase, the ship takes off from where it is and melts onto the path over 0.8 s. Later rounds start at a random point of the lap, with the ship still hidden under the veil.
- **The same motion at every frame rate.** The path is a function of flight time alone, so both frame rates show one motion, sampled at two rates.
- **It flies only while a round is live**, and hovers where it fired through the reveal.

### Shockwave

No tower and no aiming. From GO! the UFO flies, and the whole screen's frame rate flips between the base and the altered rate after a random wait (`swFlipDelayMinSec` to `swFlipDelayMaxSec`, the same range before every flip). No visual or sound marks a flip. What a press does:

| Press | Outcome | QUEST+ | The round |
|---|---|---|---|
| Inside `swWindowSec` after a flip | `detected`: the blast goes off | counted hit | ends |
| After the window closed | `late` | counted miss | goes on: TRY AGAIN!, then QUEST+'s new Δ from the base rate |
| Before any flip since GO! or TRY AGAIN! (within `swMaxEarlyPerRound`) | `early`, forgiven; 1 s lockout | **not counted** | goes on, wait redrawn |
| Before any flip, past that | `early` | **not counted** | ends |
| `maxFlipsPerRound` flips pass unanswered | `flipCap` | counted miss | ends |

If a counted miss finishes QUEST+, the round ends there. A press that lands nothing only fizzles at the muzzle, so the view stays clear. Because uncounted rounds do not advance QUEST+, a block also ends at 3 × `maxTrials` rounds (`endReason` = `roundCap`), so it cannot run for ever.

### Mixing levels in one session

Each block sets its own level as its prompt goes up. Leaving a scroll block, the camera goes straight back over its origin under the prompt, the UFO keeping its place on screen; joining one, the camera eases onto the UFO while the prompt is up. The repeating boundary only applies while a block scrolls: a manual or auto block over the endless world has the one boundary it dealt. A participant meets each kind of level in practice before the first block that counts.

## The scene

Everything below is built at runtime by the scripts, so the scene file itself is unchanged. Each piece has Inspector toggles on the component named; components created at runtime can be added to the scene by hand to tune them in edit mode.

- **The city** (`CitySkyline`, `CitySparks`, created by `TowerManager`). A night skyline built procedurally when the scene starts: five depth layers fading into haze, back to a dense sprawl just under the horizon; lit windows; landmark towers with pulsing aviation beacons; neon, warm street glow and light pollution along the horizon; and a few buildings broken open by the invasion, glowing and throwing sparks and embers. The city is lived in: now and then a window's light goes off or comes on, fading over most of a second; televisions glow, drifting slowly brighter and dimmer; and now and then someone walks past a lit window. Searchlights rise from behind the skyline and sweep the sky slowly (`searchlights`). Nothing flickers. It is the same city for every participant (`seed`), it never moves or changes between rounds, and every building stands behind the play plane, so nothing can pass in front of the UFO.
- **Out of focus** (`Backdrop`). Everything behind the nearest row of buildings is drawn blurred, as by a lens focused on the play plane (`blurPx`, 2.4 px at 1080p). The UFO, the towers, the nearest buildings, every shot and the stars stay sharp. Just before the main camera renders, the far city is drawn from its exact viewpoint into a half-resolution texture, blurred, and laid in behind the play plane. So it moves and animates exactly as before, every frame, and nothing drawn over it (the sight, a beam, a trail) is blurred with it, as it would be with a depth-of-field post effect. Off: the main camera draws the city sharp.
- **The foreground** (`Foreground`). The camera stands on a dark, grassy slope looking over the city. Three rows of grass cover the bottom of the screen, each softer the nearer it is: the grass line just in front of the towers is nearly in focus and edged by the city's glow, and the nearest row is a dark blur along the bottom edge. They bend and sway in a gusty breeze: calm stretches, then gusts arriving at random and rolling across the field, the nearest grass moving most. A few soft motes of lit dust drift downwind and fade over them. The towers' feet stand in the grass. Each row's shape, blur included, is drawn once at startup into a texture, so per frame the grass is three textured quads. `grass`, `motes` and `wind` switch or scale each part. (`CitySkyline` → `waterfront` brings back the earlier quay and water instead.)
- **Rain** (`Weather`). A light rain over the city: thin, faint streaks falling fast at a slant with the wind, in two layers at two depths, both behind the play plane, so nothing falls in front of the UFO, the towers or a shot. It is there as much for the study as for the look: fast motion across the whole screen is where a frame rate shows most plainly (at a low rate each streak visibly jumps from frame to frame; at a high one it slides). `kind` switches it to a light snow (soft flakes drifting down slowly, a gentler cue) or off; `amount` and `brightness` scale it. Drawn procedurally, so it costs almost nothing.
- **The sky** (`NightSky`, on the city). Faint twinkling stars, an occasional soft shooting star, a faint aurora (soft green curtains in the upper sky, their edges folding slowly, rays fading up into violet; `aurora`, `auroraBrightness`), and thin clouds drifting slowly across. All of it is computed on the GPU and knows nothing about the boundary.
- **The camera** (`CameraRig`). Two motions on top of the pan between rounds, both moving the view only:
  - `parallax`: the camera swings a little around the play plane as the UFO moves, the way a camera operator follows a subject. It eases after the UFO's place on screen (halfway in about 0.4 s, settled in about 1 s). Because it pivots on the play plane, the UFO, the towers and the shots stay where they were on screen, to within a pixel or two at the edges, and the mouse feels the same. What is nearer or farther moves: with the UFO going from the left edge to the right, the city slides about 24 px right and the grass line about 26 px left (at 1600×900, `parallaxYaw` 0.65°; `parallaxPitch` 0.22° does the same up and down). That parallax is what makes the scene read in 3D.
  - `handheld`: a faint, slow wander, as if the camera were held by hand: usually a couple of pixels at 1080p, rarely more than seven, at about a third of a cycle per second (`handheldDrift`, `handheldTurn`, `handheldHz`).

  The UFO moves by mouse deltas, which neither motion changes, so the UFO, the towers and every shot keep their world positions. The side of the boundary is measured along the camera's axes at rest, so the split, the score and the logs are unaffected.
- **The towers** (`TowerField`). A dense row of towers drawn from the scene's tower models (`TowerManager` → `skylineBuildings`), all in one dark backlit material, their windows lit from inside and lived in: lamps of different colours drifting brighter and dimmer, televisions, people passing, lights going off and on, every tower by the same rules. **The boundary is always one of them, and any of them can be it**: every round the whole row is dealt again on a jittered lattice anchored on the boundary. The boundary tower gets a random model, height, angle and beacon from the same pool as every other tower, so nothing about how it looks or where it stands sets it apart. The scene's own `Tower` object is never drawn; it only marks the boundary's position for the split, the scoring and the logs.
- **The ground fog is off** (`TowerManager` → `groundFog`). The fog fades in the reveal still run as timing: they drive the beam and the boundary tower's glow over the same durations as before, so the round's pacing is unchanged. `legacyHitPrefabs` (off) brings back the old particle-pack impact prefabs, one of which is a smoke cloud.
- **The UFO** (`UfoFlair`, added by `UfoController`): a spin (one turn every 2 s, `spinDegreesPerSec` 180), a bank into its motion, a jelly squash-and-stretch, and light trails. It animates a rig of child objects only. The ship's own transform, which the split, the score and the logs read, is never touched, and the belly turret the shot fires from stays out of the rig. The ship is drawn 1.8 times its size in the scene (`UfoController` → `size`), its trails with it. It flies until its centre reaches either edge of the screen, so at the edge half of it is cut off and a shot can land anywhere across the view. `guessRate` assumes exactly that band. The edges are measured on the camera at rest, so the shake after a shot cannot carry the ship further out. The ceiling works the same way: a set `maxY` never takes the centre above the top of the screen. The floor (`minY` 2.85) keeps the ship's belly turret clear of the tallest tower and its beacon.
- **The laser.** The sight (`WeaponSight`) is a soft line of light ending in a small dot on the ground line, which, like the impact point of a shot, is now down in the grass, the way the old fog hid it. It runs down through the tower row, so it is drawn 10 units nearer the camera along the lines of sight through it (`drawInFront`): exactly the same pixels, but in front of the towers, which would otherwise hide it under the first rooftop below the ship. The grass in front still covers its foot. It is quietly alive: a faint glint slides down it from the muzzle to the ground about once a second (`glintSweepSec` 1.2 s), and the dot swells and brightens a little as each one lands (`glintLanding`). Both run on the clock and know nothing about the boundary; `glint` off holds the sight still. The shot (`ShotFx`, added by `LaserFirer`) is a bolt that strikes the ground in 35 ms with an energy pulse racing down it, a muzzle flash, an impact flash, a shockwave ring, and teal sparks and orange embers. A shot that misses everything now ends on the ground line instead of in mid-air. Only the end point's height changed; the x and z it is scored and logged on are exactly as before.

**Two things to keep in mind for the data.** Neither is a bug, but both change the stimulus compared with sessions run before this version.

- Everything that moves on screen shows the frame rate. That includes the UFO's spin and trails, the glint running down the sight, the rain, the sparks, the beacons, the windows' lights and the figures passing them, the searchlights, the aurora, the twinkling stars, the swaying grass and motes, and the camera, and they show it wherever the UFO is. Every one of them updates on every frame (nothing runs on a fixed tick, and nothing uses physics), computed from the clock or stepped by the time that frame took, so each moves at the same speed at any frame rate and changes 500 times a second at 500 fps. A trail is laid down one point per frame, so on a slow side it is drawn in visibly fewer, longer segments. The **rain and the parallax swing are the strongest of these cues**. The rain falls across the whole screen all the time, fast, so at a low rate its streaks step visibly even while the UFO is still. The swing moves the whole picture behind and in front of the play plane whenever the UFO moves, and keeps easing for about a second after it stops, so on a slow side the whole city and the grass step visibly. Thresholds may come out lower than with the old, mostly static scene. Each effect can be switched off on its component (`Weather` → `kind` for the rain, `CameraRig` → `parallax` for the swing). Decide before collecting data, and keep the same settings for a whole study.
- Because the boundary always stands on a tower, a participant who aims *at a tower* when guessing blind lands within the hit radius a little more often than a blind shot anywhere. The excess is about the tower spacing divided by the screen width: roughly +0.05 at the default spacing of 2.7 world units on a 46.8-unit-wide view. `guessRate` still assumes a blind shot anywhere. A denser row (`TowerField` → `spacing`) shrinks the excess.
- The boundary is only dealt in the middle 60% of the view (`TowerManager` → `spawnWidthFraction`), so a participant who learns that and always fires mid-screen lands on it more often than `guessRate` assumes: 43% of the time, against 32% with the older 80% band. In simulation, someone doing that on every blind shot reads about 10–13% below their true threshold. The row of towers still fills the whole width, so the scenery doesn't show the band.

---

## LatinSquare.csv

This file decides **which order** each participant plays the settings in.

```
row,pos1,pos2,pos3,pos4
1,1,2,4,3
2,2,3,1,4
3,3,4,2,1
4,4,1,3,2
```

The numbers are row numbers of ExperimentConfig.csv: 1 = shockwave 2 ms up, 2 = shockwave 16.67 ms down, 3 = laser 2 ms up, 4 = laser 16.67 ms down. Participant `N` uses square row `((N - 1) mod 4) + 1`, so run participants in groups of 4.

It is a **Williams design**: every setting takes every position once, and every setting comes right after every other setting once.

### Safety checks

At startup the game checks the square and prints something like:

```
[LatinSquare] 10 row(s) x 5 setting(s) from LatinSquare.csv — Williams balanced (every ordered pair equally often).
```

If the file is broken, the game logs an error and plays a generated square instead. Broken means:

- the wrong number of settings in a line,
- a setting twice in one line,
- or a setting in the same position too often.

Fix the file before running anyone. A plain Latin square written by hand is fine too. The game accepts it and tells you it's not Williams-balanced.

---

## Log files

Each session folder has three CSVs. All three start with the same identity columns.

### Columns in all three logs

| Column | What it is |
|---|---|
| `sessionId` | Participant number, from `SessionState.csv`. |
| `runId` | Random 4-character tag for this run of the game. |
| `blockIndex` | Which setting: the row of `ExperimentConfig.csv`. `0` for a practice phase. |
| `blockOrdinal` | When this block was played in the session (1 = MAIN 1, 2 = MAIN 2, …). `0` for a practice phase. |
| `latinRow` | Which Latin-square row this participant used. |
| `latinOrder` | The participant's whole block order, e.g. `3;1;2`. |
| `baseFps` | The base frame rate: the block's, or in practice the round's (each practice pair has its own). |
| `direction` | `down` or `up`. |
| `frameLimiter` | `precise` or `unity`. |
| `levelType` | `manual`, `scroll`, `auto` or `shockwave`: the level the row was played in. On practice rows, the level being practised. |

The SessionLog and ShotLog also end with `cfg_*`: a copy of every `ExperimentConfig.csv` column for the block. It's blank on practice rows, which don't come from a config row. The PlayerLog doesn't carry that copy; at hundreds of rows a second it would be most of the file, and `blockIndex` joins it to the config.

ShotLog and PlayerLog also have:

| Column | What it is |
|---|---|
| `phase` | `practice` or `main`. Only `main` counts toward the threshold. |
| `phaseStartIso` | Clock time this phase started: its first GO!. `timeSinceStartSec` and the `...AtSec` times count from here. |

### SessionLog: one row per block

Covers the main run only, never practice.

| Column | What it is |
|---|---|
| `displayHz` | The display's refresh rate. |
| `resolution` | Screen size in pixels, e.g. `2560x1440`. |
| `startIso` | Clock time the main run started: its first GO!. |
| `endIso` | Clock time the block finished and its row was written. |
| `sessionDurationSec` | `endIso` − `startIso`, in seconds. |
| `playDurationSec` | Length of the main run up to its last frame, in seconds. |
| `endReason` | Why the block stopped: `converged` (reached `stopSD`), `maxTrials`, `roundCap` (3 × `maxTrials` rounds went by) or `abandoned` (game closed mid-block). |
| `jndFrameTimeDiffMs` | **The threshold.** QUEST+'s best estimate of Δ (posterior mean), in ms of frame time. |
| `jndAlteredFps` | The same threshold as the altered side's frame rate: 1000 / (1000/`baseFps` ± `jndFrameTimeDiffMs`). |
| `jndDiffFps` | The same threshold as a frame-rate difference: \|`jndAlteredFps` − `baseFps`\|. |
| `sd` | How unsure the estimate is (posterior SD, ms). Smaller is more sure. |
| `priorSd` | The same SD before the first round. |
| `jndMedianDiffMs` | Median of the threshold posterior. `jndMedianDiffFps` is the same in fps, as with the next two. |
| `jndModeDiffMs` | Most likely threshold value on the grid. |
| `jndCI95LoDiffMs` / `jndCI95HiDiffMs` | The threshold's 95% credible interval. |
| `jndRangeEdge` | `top` if the estimate is pressed against the largest Δ tested (the true threshold is probably above the range), `bottom` against the smallest, empty otherwise. See the warning under QUEST+ columns. |
| `slopeEstimate` | Estimated slope of the psychometric curve. |
| `lapseEstimate` | Estimated lapse rate (misses even on huge differences). |
| `staircaseTrials` | Rounds QUEST+ actually used. Use this as the trial count. |
| `lastDiffMs`, `lastDiffFps` | Δ of the last round, in ms and in fps. |
| `rounds` | Rounds ended: by a response, or by running out. |
| `shotsFired` | Every shot fired, including those that did not end a round. |
| `hits` | Rounds with `isHit` = 1. |
| `accuracy` | `hits` / `rounds`. |
| `score` | Final points. |
| `roundsExpired` | Rounds that hit the time limit with no shot. |
| `roundsCrossingCapped` | Rounds that hit `maxCrossingsPerRound` with no shot. |
| `roundsFlipCapped` | Shockwave rounds that hit `maxFlipsPerRound` with no response. |
| `avgRoundDurationSec` | Mean time from GO! to the answer. |
| `avgCrossingsPerRound` | Mean tower crossings per round: how much comparing they did. |
| `shotsWithoutCrossing` | Shots fired without crossing the tower once that round. They only ever saw one side, so the shot had nothing to compare. These are the laser's TOO EARLY! rows. |
| `avgSinceLastCrossingSec` | Mean time from the last crossing to the shot. |
| `avgMissDistX` | Mean \|shot X − tower X\| over all shots (world units). |
| `avgMissDistXMissesOnly` | The same over misses only. |
| `medianMissDistX`, `sdMissDistX`, `maxMissDistX` | Median, SD and largest \|shot X − tower X\|. |
| `totalMousePathPx` | Total mouse movement, in raw mouse counts. |
| `avgMouseSpeedPxPerSec` | `totalMousePathPx` / `playDurationSec`. |
| `peakMouseSpeedPxPerSec` | Fastest single-frame mouse speed (counts/s). |
| `totalUfoPathWorld` | Total distance the UFO flew (world units). |
| `frameCount` | Frames rendered in the main run, between rounds included. |
| `liveFrames` | Frames paced by one side's cap during rounds. |
| `baseSideFrames` | Live frames on the base side. |
| `baseSideMeasuredFps` | **Check this first.** The frame rate actually delivered on the base side. Should match `baseFps`. |
| `baseFrameTimeSdMs` | Frame-to-frame jitter on the base side (SD of frame time minus the cap's period, ms). |
| `alteredSideFrames` | Live frames on the altered side. |
| `alteredFrameTimeErrPct` | How much longer altered-side frames ran than their cap, on average, in %. Near 0 is good; positive means the altered side was delivered slower than intended. (The altered cap changes every round, which is why this is a percentage rather than a frame rate.) |
| `alteredFrameTimeSdMs` | Frame-to-frame jitter on the altered side. |
| `slowFrames` | Live frames that took more than 1.5× their cap's period: hitches. |
| `maxLiveFrameTimeMs` | Longest live frame (ms). |
| `viewWidthWorld` | Width of the camera's view at the play plane, in world units (about 47 on 16:9). With a row's `camX`, it turns world X into a place on screen: `0.5 + (x − camX) / viewWidthWorld`. |
| `guessRateScreen` | The chance rate γ this screen implies for the block's level (auto levels have their own). Compare with `cfg_guessRate`. |
| `avgFirstCrossingSec` | Mean time from a round's start to its first crossing, over rounds with one. |
| `avgCrossingSpeed` | Mean horizontal speed of the UFO at the block's crossings (world units/s): how fast the boundary was crossed. The autopilot's speed in an auto level; the participant's own otherwise. |
| `avgShotUfoSpeed` | Mean \|`ufoVelX`\| at the shots. |
| `medianSinceLastCrossingSec` | Median time from the last crossing to the shot. Less skewed than the mean by shots that waited for a later pass. |
| `avgMissAlongTravelX` | Mean of `missDistX` × sign(`ufoVelX`) over shots fired on the move: + = the shots landed past the tower in the direction of travel (late), − = before reaching it (early). The auto level's reaction-time signature. |

### ShotLog: one row per press

**Every press of the fire button in an open phase has a row, with the state of the game at that instant**, including presses that fired nothing. So a round has as many rows as it had presses. `roundEnded` = 1 marks the row that closed it, and `countedByStaircase` = 1 marks those that reached QUEST+. `outcome` is `shot` (laser, after at least one crossing), `detected`, `early` (shockwave before the first flip, laser before the first crossing), `late`, `flipCap` (`maxFlipsPerRound` flips unanswered), `expired`, `crossingCap`, or, for a press that fired nothing, `cooldown` (inside the fire cooldown), `lockout` (after a forgiven early press) or `notLive` (firing off: reveal, reset, GO!, TRY AGAIN!).

In a shockwave row, `crossings` and every other `…Crossing…` column are the flips of the frame rate, and `alteredSide` and `side` are blank.

| Column | What it is |
|---|---|
| `closeRadius` | Hit radius in world units (6.0). |
| `roundNumber` | Round number. Main rounds keep counting across blocks; practice restarts at 1. |
| `timeSinceStartSec` | Phase clock at the response. |
| `diffMs` | **The stimulus:** Δ for this round in ms of frame time, picked by QUEST+ (or the practice pair's difference): \|1000/`alteredFps` − 1000/`baseFps`\|. |
| `diffFps` | The same Δ as a frame-rate difference. |
| `alteredFps` | The altered side's cap: `baseFps ∓ diffFps`. |
| `alteredSide` | `Left` or `Right`: which side of the tower was altered. |
| `measuredBaseFps` | The frame rate actually delivered on the base side this round. Blank if the UFO never went there. |
| `measuredAlteredFps` | The same for the altered side. Compare with `alteredFps`. |
| `baseSideSec` / `alteredSideSec` | Time the UFO spent on each side this round. |
| `lateFrames` | Precise limiter only: frames this round that overran their slot. |
| `crossings` | Times the UFO crossed the tower this round. |
| `lastCrossingAtSec` | When the last crossing happened (phase clock). Blank if none. |
| `sinceLastCrossingSec` | How long after the last crossing the shot came. |
| `outcome` | How the row ended: see the list above this table. |
| `playerFired` | 1 if they fired; 0 when the round ran out or the press fired nothing. |
| `isHit` | 1 = the shot landed within `closeRadius` of the tower; on shockwave, the press came inside the window. |
| `countedByStaircase` | 1 if QUEST+ used this response. For any refit, keep `phase` = main and this = 1. |
| `totalScore` | Running score after this round. |
| `roundStartSec` | When the round started: firing handed back at GO! (phase clock). |
| `firedAtSec` | When they fired (phase clock). Blank if they didn't. |
| `roundDurationSec` | From the round's start to its end. |
| `hitX` | X position where the shot landed (the UFO's X at the press). |
| `missDistX` | `hitX` − `towerX`. Negative = left of the tower. |
| `towerX` | X position of the hidden tower. |
| `ufoY` | UFO height at the response. |
| `side` | `Left` or `Right`: which side of the tower the shot was fired from. |
| `threshDiffMs` | QUEST+'s threshold estimate after this round (ms). Blank on practice rows, which never reach QUEST+. |
| `threshDiffFps` | The same estimate as a frame-rate difference. |
| `sd` | SD of that estimate (ms). |
| `slopeEstimate`, `lapseEstimate` | Slope and lapse estimates after this round. |
| `camX` | The camera's resting X at the response (no shake or swing). It moves only in a scroll level, where the camera follows the UFO. |
| `ufoViewportX`, `towerViewportX` | Where the UFO (the shot) and the tower were across the screen at the response: 0 = left edge, 1 = right edge. |
| `ufoVelX` | The UFO's horizontal velocity at the response (world units/s, + = moving right), measured over the last 0.1 s of frames. With `missDistX` it tells a shot fired as the UFO reached the boundary from one fired after it had passed. |
| `ufoXAtStart` | Where the UFO was when the round started (firing handed back after GO!). |
| `firstCrossingAtSec` | When the round's first crossing happened (phase clock). Blank if none. |
| `meanCrossingSpeed`, `lastCrossingSpeed` | The UFO's mean, and last, horizontal speed at this round's crossings (world units/s). Blank if none. |

These describe the press and the state it met:

| Column | What it is |
|---|---|
| `shotInRound`, `roundEnded` | Which shot of the round this is, and whether this row closed it. |
| `inputDevice`, `firingEnabled` | `mouse` or `key`; whether firing was on. |
| `capFps`, `capFrameMs`, `pacedBy`, `windowOpen` | The cap pacing this frame, as a rate and a frame time, whose it was, and whether the shockwave window was open. |
| `reactionSec`, `framesSinceLastChange` | **How long after the last frame-rate change the press came**, in seconds and in frames. |
| `changeFromFps`, `changeToFps`, `changeFromMs`, `changeToMs`, `changeDeltaMs`, `changeDir` | **How much that change was**: the caps either side of it, as rates and frame times, the signed frame-time change (+ = longer), and `slower` or `faster`. |
| `foreperiodSec`, `windowSec` | Shockwave: the random wait that ended in the last flip, and the response window. |
| `changeTimesSec`, `changesSinceLastPress` | Every change so far this round (phase clock, `;`-separated), and how many since the last press. |
| `changeUfoX`, `changeUfoY`, `ufoX` | Where the ship was at the change, and at the press. |
| `earlyPresses`, `latePresses`, `lockoutPresses`, `cooldownPresses`, `notLivePresses` | Presses of each kind so far this round, this row included. |
| `baseFrameMs`, `ftDirection`, `alteredFrameMs`, `measuredBaseMs`, `measuredAlteredMs` | The stimulus in frame time, nominal and as delivered since the attempt began. |

The SessionLog adds `baseFrameMs`, `ftDirection`, `swDetections`, `earlyFires`, `swLateFires`, `roundsFlipCapped`, `lockoutPresses`, `cooldownPresses`, `notLivePresses`, `roundsNotCounted`, reaction-time statistics (`avg`, `sd`, `median`, `min`, `max` `ReactionSec`, over hits), `shotsPerMinute`, and live frame-time percentiles (`p95FrameTimeMs`, `p99FrameTimeMs`, `baseP99FrameTimeMs`, `alteredP99FrameTimeMs`). `rounds` counts the rounds that ended, `staircaseTrials` the responses QUEST+ used (a TOO LATE is one, without ending its round), and `shotsFired` every shot. `direction` in the identity columns stays the direction of the frame **rate**, for the analysis scripts.

### PlayerLog: one row per frame

| Column | What it is |
|---|---|
| `roundNumber` | Round this frame belongs to. Same numbering as ShotLog, so the two join on it. |
| `frameIndex` | Frame number within the phase, starting at 0. |
| `timeSinceStartSec` | Phase clock at the start of this frame. |
| `unscaledDeltaMs` | How long this frame took (ms). |
| `targetFps` | The cap that paced **this** frame: the one `unscaledDeltaMs` measures. |
| `pacedBy` | Which side's cap that was: `base`, `altered`, or empty between rounds. |
| `diffFps` | The round's Δ, as a frame-rate difference. Empty between rounds. |
| `alteredSide` | The round's altered side. Empty between rounds. |
| `side` | `Left` or `Right`: which side of the tower the UFO is on. Means nothing on a shockwave row, and neither does `alteredSide` there; the ShotLog leaves both blank. |
| `crossed` | 1 on the frame the UFO crossed the tower during a round; on shockwave, the frame of a flip. |
| `shockwaveFastPhase`, `shockwaveWindow`, `shockwaveFlips` | Shockwave rows only: whether the cap now set, which paces the next frame, is the faster of the round's two rates; whether the response window was open; and how many flips the round has had so far. |
| `mouseX`, `mouseY` | Mouse pointer position on screen (pixels). |
| `mouseDeltaX`, `mouseDeltaY` | Raw mouse movement this frame (counts). |
| `ufoX`, `ufoY` | UFO position (world units). |
| `towerX` | X position of the hidden tower. |
| `leftButtonDown` | 1 while the left mouse button is held. |
| `leftButtonPressed` | 1 on the frame the left mouse button went down. |
| `fireKeyDown` | 1 while Space (the other fire key) is held. |
| `shotFired` | 1 on the frame a response was logged. Frames where a round ran out get a 1 too. |
| `shotHitX` | Where that shot landed on X (0 when there's no shot). |
| `camX` | The camera's resting X this frame. The UFO's place on screen is `0.5 + (ufoX − camX) / viewWidthWorld` in every level. |
| `diffMs`, `armed` | The round's Δ in frame time (blank between rounds), and whether firing was on this frame. |

Every GO! is in the frame log, the first of a phase included (the phase clock starts with it), and the UFO holds still through each one, so no crossing happens before a round's firing is handed back and every frame a round's measured frame rates count is in the log.

The cap for a frame is chosen at the end of the frame before it, so on the single frame after a crossing `side` already shows the new side while `targetFps` / `pacedBy` still show the old cap. That's the real delivery order, not a logging lag.

---

## After the sessions

Everything is in `Analysis/`:

| Script | What it does |
|---|---|
| `audit_logs.py` | **Run this first.** Checks every session's logs: each row's structure and internal consistency, and that the three files agree with each other (shots against the frames they fired on, each round's measured frame rates recomputed from its frames, the session row recomputed from the rounds). `python Analysis/audit_logs.py --all` (one line per session; `--data "<build>/Data"` for a build's logs, `--verbose` for every message). Exit code 1 on any FAIL. It reads every log format this study has written; for logs from before `levelType` it infers the level from how far the UFO travelled. Standard library only. |
| `build_db.py` | Builds a SQLite `.db` for each session. It runs by itself at the end of every session, and reads both this study's logs and the stutter study's. To rebuild: `python Analysis/build_db.py --data "<build>/Data"`. `--schema` prints the tables and example queries; `--verify` checks the database gives the CSVs back cell for cell; `--combined all.db` adds one database of every session. |
| `level_analysis.py` | Figures, tables and a report for a study with **several level types**: a condition is level type × base rate × direction, so manual, scroll, auto and shockwave blocks are compared and never pooled. Thresholds (Weber %, fps and ms) with participants paired across level types; the within-participant level contrasts (sign-flip and Wilcoxon tests); psychometric curves; QUEST+ convergence and every block's staircase; round outcomes, response latency after the crossing, miss distances; for auto levels, where the shot landed along the UFO's direction of travel and the reaction lag it implies, and hit rate against latency; the UFO's speed at crossings; screen-position heatmaps, mouse activity and frame-time errors (from the PlayerLogs); frame delivery, the chance rate per block, practice, and a data inventory. `python Analysis/level_analysis.py --data Data/Logs --out <folder>` (`--no-frames` skips the PlayerLogs; `--list` lists the figures). |
| `jnd_analysis.py` | The paper figures, statistics and report for one kind of block. It keys conditions by base rate, so it now runs once per level type × direction, into `<out>/<levelType>_<direction>/` (or `--level` / `--direction` to pick one). For logs from before `levelType` it infers the level (`--legacy-level` to set it). Not for this study's logs: it skips any log in frame time. |
| `simulate_logs.py` | Synthetic sessions for the frame-rate design, in the columns it shares with today's logs: manual, scroll and auto, both directions, QUEST+ and per-frame pacing, from simulated observers. It refuses this study's config; `Invasion.exe -jndRobot` gives real logs of it. For testing the analysis before real data exist, or checking a design: `python Analysis/simulate_logs.py --out <folder> --participants 8`. The folders end in `SIM`; nothing in them is real data. |
| `plot_quest.py`, `quick_plot.py`, `sd_target_analysis.py`, `heatmap_generator.py`, `reaction_after_spike.py`, `repair_stimulus.py`, `questplus.py` | Written for the **stutter** study. They expect its columns (`jndEstimateMs`, `stimulusMs`, `weapon`, …). |

`Analysis/COLUMNS.md` has a compact list of every config and log column.

**Check a build with a stand-in participant:** `Invasion.exe -jndRobot` plays a real session through real input, with some presses deliberately too early, too late and ignored, then `python Analysis/audit_logs.py --data "<build>/Data"`. It does not advance the participant number. `Invasion.exe -jndShockFx <folder>` photographs the flight and the blast frame by frame, and `-jndBenchmark` answers whether the PC holds 500 fps.

`level_analysis.py` is the script for these logs: it treats shockwave as a level and compares it with manual, but has no shockwave-specific figures yet (reaction time, foreperiod, outcomes); the columns they need are all logged. `jnd_analysis.py` skips frame-time logs and points to it. Logs already recorded keep the names they were written with; `Analysis/COLUMNS.md` lists them, and `audit_logs.py` and `level_analysis.py` read both.

A good routine after each session: `python Analysis/audit_logs.py --all`, then check `SessionInfo_<id>.csv` says `UIManager.showDebug,false` (`level_analysis.py`'s report flags any session where it was on), then the analysis.
