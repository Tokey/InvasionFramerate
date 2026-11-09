using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>Which way the altered side moves away from the base frame RATE. The config says it in frame time, where it
    /// reads the other way round: ftDirection up (frames get longer) is <see cref="Down"/> here, and down is
    /// <see cref="Up"/>.</summary>
    public enum FpsDirection { Down, Up }

    /// <summary>
    /// Which side of the tower runs at the altered frame rate. <see cref="Random"/> draws it afresh
    /// every round, so a participant cannot learn "the slow side is always on the right" and use
    /// that instead of the boundary.
    /// </summary>
    public enum AlteredSideMode { Random, Left, Right }

    /// <summary>
    /// How the per-side frame rate is enforced.
    ///
    ///   Unity    Application.targetFrameRate — Unity's own limiter, and the default. Its jitter is
    ///            whatever the engine gives (about ±0.6 ms at 60 fps in the Editor), but it leaves the
    ///            main thread free between frames instead of spinning a core on it.
    ///   Precise  <see cref="FrameLimiter"/>'s busy-wait, hooked into the player loop where Unity's
    ///            own limiter waits, ahead of time sampling and input processing. Frame times land
    ///            within microseconds of the target; it costs one CPU core spinning while it waits.
    /// </summary>
    public enum FrameLimiterMode { Precise, Unity }

    /// <summary>
    /// The kind of level a block is played in. In the first three the split, the shot, the scoring and QUEST+ are the
    /// same; what changes is who flies the UFO and whether the world moves. Shockwave makes the response temporal.
    ///
    ///   Manual  the participant flies the UFO with the mouse, the camera holding still (it pans a little between
    ///           rounds). The original level.
    ///   Scroll  the participant flies the UFO and the camera follows it, so the endless world scrolls past under
    ///           it (<see cref="GameManager.worldScroll"/>). The boundary repeats once a period there, the frame
    ///           rates swapping sides at every copy.
    ///   Auto    the UFO flies itself back and forth across the screen (<see cref="UfoAutopilot"/>), the camera
    ///           holding still as in Manual. The participant only fires: when the UFO is over the place where the
    ///           frame rate changes. The mouse does not move it.
    ///   Shockwave  the UFO flies itself and there is no tower to find: the whole screen's frame rate flips between the
    ///           base and the altered rate after a random wait (<see cref="ShockwaveController"/>), and the participant
    ///           presses inside the response window after a flip.
    /// </summary>
    public enum LevelType { Manual, Scroll, Auto, Shockwave }

    /// <summary>
    /// How the autopilot sweeps from one end of its band to the other. Linear: at one constant speed, with a
    /// smooth turn at each end, so the ship crosses the boundary at the same speed wherever the boundary is.
    /// Sine: its X follows a sine, fastest mid-screen and slowing into each end — so a boundary near the middle
    /// is crossed faster than one near an edge.
    /// </summary>
    public enum AutoPathMode { Linear, Sine }

    /// <summary>Where the autopilot's flight starts each round: at a random point of its sweep, heading either
    /// way, or where the last round left it.</summary>
    public enum AutoStartMode { Random, Continue }

    /// <summary>The scroll's camera for a block: as GameManager's Inspector has it, locked on the ship, or
    /// following it as a hand-held camera would (<see cref="GameManager.worldScrollHandheldFollow"/>).</summary>
    public enum ScrollFollowMode { Inherit, Locked, Handheld }

    /// <summary>
    /// The whole study's configuration: <c>Data/ExperimentConfig.csv</c>, a header row plus one
    /// value row per block.
    ///
    /// The task is a frame-rate JND. A hidden radio tower splits the city in two: one side runs at
    /// <see cref="baseFps"/>, the other at a lower (<see cref="FpsDirection.Down"/>) or higher
    /// (<see cref="FpsDirection.Up"/>) rate. The participant flies across the boundary, notices where
    /// the motion changes, and shoots the hidden tower; landing the shot is the detection response.
    /// QUEST+ works in frame time: Δ is how many milliseconds the altered side's frame time is from the
    /// base's, it picks Δ every round from <see cref="minDiffMs"/> to <see cref="maxDiffMs"/> in steps of
    /// <see cref="diffStepMs"/>, and homes in on the smallest Δ the participant can locate.
    ///
    /// Columns, grouped:
    ///
    ///   level      levelType (manual / scroll / auto / shockwave), scrollFollow
    ///   stimulus   baseFrameMs, ftDirection, maxDiffMs, minDiffMs, diffStepMs, alteredSide, frameLimiter
    ///   round      maxCrossingsPerRound, roundTimeoutSec, hitPoints, missPoints
    ///   QUEST+     threshCount, slope/lapse grids, guessRate, maxTrials, minTrials, stopSD
    ///   autopilot  autoSpeed, autoPath, autoSweep, autoTurnSec, autoHeight, autoBobAmp, autoBobHz, autoStart
    ///              (auto and shockwave levels)
    ///   shockwave  swFlipDelayMinSec, swFlipDelayMaxSec, swWindowSec, swMaxEarlyPerRound, maxFlipsPerRound
    ///   debug      showDebug
    ///
    /// Everything that defines the task itself rather than the study — the hit radius, the fire
    /// cooldown, the reveal timing — is pinned below rather than read from the file, so it stays
    /// identical for every participant.
    /// </summary>
    public class StudyConfig
    {
        /// <summary>The raw config row, for echoing every setting into the session and shot logs.</summary>
        public Dictionary<string, string> Row { get; private set; }

        /// <summary>Column order as it appeared in the file, so log headers stay stable.</summary>
        public string[] Columns { get; private set; } = new string[0];

        /// <summary>1-based position of this row in ExperimentConfig.csv — the block number.</summary>
        public int blockIndex;

        // ── Level ────────────────────────────────────────────────────────────

        /// <summary>The kind of level this block is played in. See <see cref="LevelType"/>.</summary>
        public LevelType levelType = LevelType.Manual;

        /// <summary>False when the row left levelType out or blank, and the scene's GameManager.worldScroll box
        /// decided it instead (a config written before the column existed).</summary>
        public bool levelTypeFromFile;

        /// <summary>Scroll levels: the camera locked on the ship or following it by hand; Inherit leaves the
        /// Inspector's choice.</summary>
        public ScrollFollowMode scrollFollow = ScrollFollowMode.Inherit;

            public bool Scrolls    => levelType == LevelType.Scroll;
            public bool AutoFlight => levelType == LevelType.Auto || levelType == LevelType.Shockwave;
            public bool IsShockwave => levelType == LevelType.Shockwave;

        /// <summary>"manual" / "scroll" / "auto" / "shockwave" — the levelType column of every log.</summary>
        public string LevelCell => LevelCellOf(levelType);

        public static string LevelCellOf(LevelType t) =>
            t == LevelType.Scroll ? "scroll" :
            t == LevelType.Auto ? "auto" :
            t == LevelType.Shockwave ? "shockwave" : "manual";

        // ── Stimulus ─────────────────────────────────────────────────────────

        /// <summary>The reference frame rate. One side of the tower always runs at exactly this.</summary>
        public int baseFps = 60;

        /// <summary>Down: the altered side's frame rate is lower than the base's, so its frame time is
        /// longer by Δ. Up: the rate is higher, the frame time shorter by Δ.</summary>
        public FpsDirection direction = FpsDirection.Down;

        /// <summary>Largest frame-time difference QUEST+ may present (ms), and the top of its threshold
        /// grid. Clamped so the altered side stays at 1 fps or above (Down) and its frame time above
        /// zero (Up).</summary>
        public float maxDiffMs = 14.5f;

        /// <summary>Smallest difference QUEST+ may present (ms), and the bottom of its threshold grid.</summary>
        public float minDiffMs = 0.5f;

        /// <summary>
        /// Spacing of the differences QUEST+ may present (ms): minDiffMs, minDiffMs + step, … up to
        /// maxDiffMs. If (maxDiffMs − minDiffMs) is not a multiple of it, the last step is shorter, so the range still ends
        /// exactly at maxDiffMs. See <see cref="DiffGridMs"/> for what is actually shown.
        /// </summary>
        public float diffStepMs = 0.5f;

        public AlteredSideMode  alteredSide  = AlteredSideMode.Random;
        public FrameLimiterMode frameLimiter = FrameLimiterMode.Unity;

        double BaseFrameMs => 1000.0 / baseFps;

        /// <summary>The altered side's frame time for a difference of <paramref name="diffMs"/> (ms).</summary>
        double AlteredFrameMs(float diffMs) =>
            direction == FpsDirection.Up ? BaseFrameMs - diffMs : BaseFrameMs + diffMs;

        /// <summary>
        /// The frame-rate cap that presents a difference of <paramref name="diffMs"/>: the whole number
        /// nearest the frame time asked for, because Application.targetFrameRate takes nothing else.
        /// </summary>
        public int AlteredFpsFor(float diffMs)
        {
            double ms = AlteredFrameMs(diffMs);
            return ms > 0.0 ? Mathf.Max(1, (int)System.Math.Round(1000.0 / ms)) : baseFps;
        }

        /// <summary>The frame-time difference between two caps (ms): what a pair of rates really shows.</summary>
        public static float FrameTimeDiffMs(int fpsA, int fpsB) =>
            (float)System.Math.Abs(1000.0 / fpsA - 1000.0 / fpsB);

        /// <summary>A threshold (ms) restated as the altered side's frame rate. Unrounded: θ is a parameter
        /// of the curve, not a cap anything ran at. NaN passes through.</summary>
        public float AlteredFpsAt(float diffMs)
        {
            double ms = AlteredFrameMs(diffMs);
            return ms > 0.0 ? (float)(1000.0 / ms) : float.NaN;
        }

        /// <summary>A threshold (ms) restated as a frame-rate difference, for the logs' fps columns.</summary>
        public float DiffFpsAt(float diffMs) => Mathf.Abs(AlteredFpsAt(diffMs) - baseFps);

        /// <summary>
        /// The differences QUEST+ chooses between, ms: each step of the minDiffMs…maxDiffMs grid moved to
        /// the frame-time difference of the whole-number cap that presents it, since QUEST+ must reason
        /// about the stimulus that was actually shown. Steps are as even as whole caps allow. A step that
        /// lands on the same cap as the last one, or on the base itself, is the same stimulus and appears
        /// once — which starts to happen where one fps is worth more than a step of frame time (below about
        /// 44 fps for a 0.5 ms step), so the slow end of a long Down range is sparser than the step.
        /// </summary>
        public float[] DiffGridMs()
        {
            int steps = Mathf.FloorToInt((maxDiffMs - minDiffMs) / diffStepMs + 1e-4f);
            var grid = new List<float>(steps + 2);
            int last = baseFps;
            for (int k = 0; k <= steps; k++)
            {
                int cap = AlteredFpsFor(minDiffMs + k * diffStepMs);
                if (cap == last) continue;
                grid.Add(FrameTimeDiffMs(baseFps, cap));
                last = cap;
            }
            // The range ends exactly at maxDiffMs, on a shorter last step if it is not a whole number of steps away: the
            // 16.67 ms block's range ends at exactly 2 ms.
            int top = AlteredFpsFor(maxDiffMs);
            if (top != last && top != baseFps) grid.Add(FrameTimeDiffMs(baseFps, top));
            return grid.ToArray();
        }

        /// <summary>"60↓" / "120↑ auto" — for console lines. The level is named unless it is manual.</summary>
        public string ShortLabel =>
            $"{baseFps}{(direction == FpsDirection.Up ? "↑" : "↓")}{(levelType == LevelType.Manual ? "" : " " + LevelCell)}";

        public string DirectionCell => direction == FpsDirection.Up ? "up" : "down";
        /// <summary>The same direction in frame time, where it reads the other way round: up = frames get longer.</summary>
        public string FtDirectionCell => direction == FpsDirection.Up ? "down" : "up";
        public string LimiterCell   => frameLimiter == FrameLimiterMode.Unity ? "unity" : "precise";

        // ── Round bounds ─────────────────────────────────────────────────────

        /// <summary>
        /// Tower crossings a round may contain before it is closed as a miss: the (N+1)th crossing
        /// ends it. 0 = no cap. Off by default for this task — crossing the boundary is how the two
        /// frame rates are compared, so capping crossings caps the comparison itself.
        /// </summary>
        public int maxCrossingsPerRound;

        /// <summary>Wall-clock cap on one round, seconds from the starting gun. A round that reaches
        /// it with no shot is closed as a miss. 0 = none. Never shown to the participant.</summary>
        public float roundTimeoutSec = 15f;

        // ── Scoring ──────────────────────────────────────────────────────────
        /// <summary>Score for a hit. Also defines a hit: isHit is shotScore >= hitPoints.</summary>
        public float hitPoints;
        /// <summary>Score for a miss — negative makes it a penalty.</summary>
        public float missPoints;

        // ── QUEST+ ───────────────────────────────────────────────────────────
        public int   threshCount = 50;
        public float slopeMin = 1f, slopeMax = 7f;
        public int   slopeCount = 10;
        public float lapseMin = 0f, lapseMax = 0.06f;
        public int   lapseCount = 4;
        /// <summary>Chance level γ: the odds a blind shot lands on the tower. Geometric — see
        /// <see cref="GuessRateCalculator"/>, which prints the value for the running screen.</summary>
        public float guessRate = 0.256f;
        public int   maxTrials = 50;
        public int   minTrials = 8;
        /// <summary>Stop once the posterior SD of the threshold (ms) is at or below this, after
        /// minTrials. 0 = run to maxTrials.</summary>
        public float stopSD    = 0.5f;

        // ── Autopilot (auto levels) ──────────────────────────────────────────
        // How the UFO flies itself in an auto level. Ignored by the other levels. The path is a function of
        // flight time alone (AutoFlightPath), so it is the same at every frame rate. Its motion is the stimulus
        // the participant watches, so keep it the same across a study's auto blocks unless it is what is varied.

        /// <summary>Horizontal speed, world units a second (the mean speed for a sine path). The view is about
        /// 47 units wide at the play plane and the hit window 12, so at 20 the ship crosses the view in about
        /// 2.35 s.</summary>
        public float autoSpeed = 20f;

        public AutoPathMode autoPath = AutoPathMode.Linear;

        /// <summary>Share of the view's width the sweep spans, centred: 0.9 runs the ship's centre from 5% to 95%
        /// of the width. Must reach past every place the tower can be dealt (the middle 60%), or a boundary near
        /// an edge could never be crossed.</summary>
        public float autoSweep = 0.92f;

        /// <summary>Linear path, the infinity sign: seconds a lobe at either end takes. The ship keeps its speed all the
        /// way round, so a longer time is a taller lobe: about 4 units either side of the flight height at 20 u/s and
        /// 1 s.</summary>
        public float autoTurnSec = 1f;

        /// <summary>Flight height within the UFO's flight band: 0 = as low as it may go, 1 = as high.</summary>
        public float autoHeight = 0.38f;

        /// <summary>The vertical sine wave the ship bobs on as it flies: amplitude in world units (0.5 is about
        /// 20 px of a 1080p screen) and frequency in Hz. 0 flies level.</summary>
        public float autoBobAmp = 0.25f;
        public float autoBobHz  = 1.1f;

        public AutoStartMode autoStart = AutoStartMode.Random;

        // ── Shockwave (shockwave levels) ─────────────────────────────────────
        /// <summary>Shortest wait before a frame-rate flip (seconds): from GO!, from a forgiven early press, or from a
        /// closed window. With the longest, it sets the shockwave guess rate.</summary>
        public float swFlipDelayMinSec = 1f;
        /// <summary>Longest wait before a frame-rate flip (seconds).</summary>
        public float swFlipDelayMaxSec = 3.5f;
        /// <summary>How long the detection window stays open after each frame-rate flip (seconds).</summary>
        public float swWindowSec = 0.9f;
        /// <summary>How many early presses a main round forgives before the next one forfeits the round as a miss.</summary>
        public int swMaxEarlyPerRound = 1;
        /// <summary>How many unanswered frame-rate flips may pass in a main shockwave round before it ends (outcome flipCap).</summary>
        public int maxFlipsPerRound = 5;

        // ── HUD / Debug ──────────────────────────────────────────────────────
        /// <summary>Whether the top-right debug readout is shown during this block (from ExperimentConfig.csv).</summary>
        public bool showDebug = false;

        /// <summary>The autopilot's path for this block, without its band (which depends on the view).</summary>
        public AutoFlightPath AutoPath => new AutoFlightPath
        {
            mode = autoPath, speed = autoSpeed, turnSec = autoTurnSec, bobAmp = autoBobAmp, bobHz = autoBobHz,
        };

        // ── Fixed task settings ──────────────────────────────────────────────
        // Deliberately not CSV columns: they define the task rather than the study, so they are
        // pinned here and stay identical for every participant. They are still pushed onto the
        // scene each block, so an Inspector value that has drifted cannot quietly change the task.

        /// <summary>
        /// Half-width of the hit window, world units, calibrated on a 16:9 display. Feeds the
        /// chance-level hit rate directly (γ = 2r / shot-reachable width), so changing it REQUIRES
        /// changing guessRate in ExperimentConfig.csv — GuessRateCalculator prints the right value
        /// at startup and warns when the CSV disagrees. r = 6.0 gives γ = 0.256 on 16:9.
        /// </summary>
        public readonly float closeRadius               = 6.0f;
        public readonly bool  showHitZone               = true;
        /// <summary>Half-width (world units) of the dead zone around the tower. The UFO keeps its
        /// side until it is this far past the boundary, so hovering on the line cannot flicker the
        /// frame rate between the two caps every frame.</summary>
        public readonly float crossingDeadZone          = 0.25f;
        public readonly float fireCooldown              = 0.25f;
        /// <summary>How long the reveal holds after a shot — the tower, and both sides' frame
        /// rates. Long enough to read two numbers and their measured values.</summary>
        public readonly float revealHoldSec             = 2f;
        /// <summary>How long "GO!" holds and shakes before firing is handed back.</summary>
        public readonly float readyBeatSec              = 0.5f;
        /// <summary>Practice shockwave cap on unanswered flips per round.</summary>
        public readonly int   practiceMaxFlipsPerRound  = 3;
        /// <summary>Lockout after a forgiven TOO EARLY press: further clicks are ignored and logged as lockout (seconds).</summary>
        public readonly float swEarlyLockoutSec         = 1f;

        // ── Loading ──────────────────────────────────────────────────────────

        /// <summary>
        /// Loads every row of Data/ExperimentConfig.csv — one block each, run back to back within a
        /// single session. Writes the default file first if none exists. File order is the
        /// SETTINGS' identity (<see cref="blockIndex"/>), not the running order, which comes from
        /// <see cref="LatinSquare"/>.
        /// </summary>
        /// <param name="unsetLevel">The level of a row that leaves levelType out or blank — a config written before
        /// the column existed. The director passes Scroll when the scene's GameManager.worldScroll box is ticked,
        /// which is how such a config was played.</param>
        public static List<StudyConfig> LoadAll(LevelType unsetLevel = LevelType.Manual)
        {
            string path = ExperimentPaths.Config;
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[StudyConfig] {path} not found — writing defaults.");
                File.WriteAllText(path, DefaultCsv(), new UTF8Encoding(false));
            }

            List<string[]> raw = CsvTable.ReadRaw(path);
            List<Dictionary<string, string>> rows = CsvTable.Read(path);
            var blocks = new List<StudyConfig>();

            if (rows.Count == 0)
            {
                Debug.LogError($"[StudyConfig] {path} has a header but no value rows — cannot run.");
                return blocks;
            }

            string[] columns = raw.Count > 0 ? raw[0] : new string[0];
            if (!System.Array.Exists(columns, c => c.Trim().Equals("baseFrameMs", System.StringComparison.OrdinalIgnoreCase) ||
                                                   c.Trim().Equals("baseFps", System.StringComparison.OrdinalIgnoreCase)))
                Debug.LogError($"[StudyConfig] {path} has no baseFrameMs column — it looks like a " +
                                "config from an earlier study. Delete it to have the frame-rate defaults " +
                                "written, or add the new columns by hand.");
            if (!System.Array.Exists(columns, c => c.Trim().Equals("diffStepMs", System.StringComparison.OrdinalIgnoreCase)))
                Debug.LogError($"[StudyConfig] {path} has no diffStepMs column, so every block falls back to " +
                                "the default Δ range. QUEST+ now steps in frame time: replace maxDiffFps, " +
                                "minDiffFps and diffStepFps with maxDiffMs, minDiffMs and diffStepMs.");

            for (int i = 0; i < rows.Count; i++)
            {
                var c = FromRow(rows[i], i + 1, unsetLevel);
                c.Columns = columns;
                c.FillAutopilotEcho();
                blocks.Add(c);
            }

            Debug.Log($"[StudyConfig] Loaded {blocks.Count} block(s) from {path} — " +
                      string.Join(", ", blocks.ConvertAll(b => b.Describe())));

            List<StudyConfig> unset = blocks.FindAll(b => !b.levelTypeFromFile);
            if (unset.Count > 0)
                Debug.LogWarning($"[StudyConfig] {unset.Count} row(s) of {path} give no levelType, so they play as " +
                                  $"{LevelCellOf(unsetLevel)} levels (the scene's GameManager.worldScroll box). Add a " +
                                  "levelType column (manual / scroll / auto / shockwave) to say so in the file.");
            return blocks;
        }

        /// <summary>"scroll level, 60 fps down by 0.5–14.5 ms in steps of 0.5 ms (precise limiter)"; an auto level
        /// adds its flight.</summary>
        public string Describe()
        {
            string d = $"{LevelCell} level, {1000f / baseFps:0.##} ms frame time ({baseFps} fps) {FtDirectionCell} by {minDiffMs:0.##}–{maxDiffMs:0.##} ms in " +
                       $"steps of {diffStepMs:0.##} ms ({LimiterCell} limiter)";
            if (AutoFlight)
                d += $", autopilot {(autoPath == AutoPathMode.Sine ? "sine" : "linear")} at {autoSpeed:0.##} u/s over " +
                     $"{autoSweep:0.##} of the width, bob {autoBobAmp:0.##} u at {autoBobHz:0.##} Hz";
            return d;
        }

        public static StudyConfig FromRow(Dictionary<string, string> row, int blockIndex,
                                          LevelType unsetLevel = LevelType.Manual)
        {
            var c = new StudyConfig { Row = row, blockIndex = blockIndex };
            string tag = $"[StudyConfig] Block {blockIndex}";

            string level = CsvTable.GetString(row, "levelType", "");
            c.levelTypeFromFile = level.Length > 0;
            c.levelType    = c.levelTypeFromFile ? ParseLevel(level, tag) : unsetLevel;
            c.scrollFollow = ParseFollow(CsvTable.GetString(row, "scrollFollow", ""), tag);

            // The study is set in frame time: baseFrameMs and ftDirection. A config from before that says baseFps and
            // direction (of the frame rate), which still read as they did.
            float baseMs   = CsvTable.GetFloat(row, "baseFrameMs", 0f);
            c.baseFps      = baseMs > 0f ? Mathf.RoundToInt(1000f / baseMs) : CsvTable.GetInt(row, "baseFps", 60);
            string ftDir   = CsvTable.GetString(row, "ftDirection", "");
            c.direction    = ftDir.Length > 0 ? ParseFtDirection(ftDir, tag)
                                              : ParseDirection(CsvTable.GetString(row, "direction", "down"), tag);
            c.maxDiffMs    = CsvTable.GetFloat(row, "maxDiffMs", 14.5f);
            c.minDiffMs    = CsvTable.GetFloat(row, "minDiffMs", 0.5f);
            c.diffStepMs   = CsvTable.GetFloat(row, "diffStepMs", 0.5f);
            c.alteredSide  = ParseSide(CsvTable.GetString(row, "alteredSide", "random"), tag);
            c.frameLimiter = ParseLimiter(CsvTable.GetString(row, "frameLimiter", "unity"), tag);

            c.maxCrossingsPerRound = Mathf.Max(0,  CsvTable.GetInt(row,   "maxCrossingsPerRound", 0));
            c.roundTimeoutSec      = Mathf.Max(0f, CsvTable.GetFloat(row, "roundTimeoutSec", 15f));

            c.hitPoints  = CsvTable.GetFloat(row, "hitPoints", 100f);
            c.missPoints = CsvTable.GetFloat(row, "missPoints", -10f);

            c.threshCount = Mathf.Max(2, CsvTable.GetInt(row, "threshCount", 50));
            c.slopeMin    = CsvTable.GetFloat(row, "slopeMin", 1f);
            c.slopeMax    = CsvTable.GetFloat(row, "slopeMax", 7f);
            c.slopeCount  = Mathf.Max(1, CsvTable.GetInt(row, "slopeCount", 10));
            c.lapseMin    = CsvTable.GetFloat(row, "lapseMin", 0f);
            c.lapseMax    = CsvTable.GetFloat(row, "lapseMax", 0.06f);
            c.lapseCount  = Mathf.Max(1, CsvTable.GetInt(row, "lapseCount", 4));
            c.guessRate   = Mathf.Clamp01(CsvTable.GetFloat(row, "guessRate", 0.256f));
            c.maxTrials   = Mathf.Max(1, CsvTable.GetInt(row, "maxTrials", 50));
            c.minTrials   = Mathf.Max(0, CsvTable.GetInt(row, "minTrials", 8));
            c.stopSD      = Mathf.Max(0f, CsvTable.GetFloat(row, "stopSD", 0.5f));

            c.autoSpeed   = CsvTable.GetFloat(row, "autoSpeed", 20f);
            c.autoPath    = ParsePath(CsvTable.GetString(row, "autoPath", "linear"), tag);
            c.autoSweep   = CsvTable.GetFloat(row, "autoSweep", 0.92f);
            c.autoTurnSec = CsvTable.GetFloat(row, "autoTurnSec", 1f);
            c.autoHeight  = CsvTable.GetFloat(row, "autoHeight", 0.38f);
            c.autoBobAmp  = CsvTable.GetFloat(row, "autoBobAmp", 0.25f);
            c.autoBobHz   = CsvTable.GetFloat(row, "autoBobHz", 1.1f);
            c.autoStart   = ParseStart(CsvTable.GetString(row, "autoStart", "random"), tag);

            // A config copied over from an earlier build: its timing columns would be ignored without a word.
            if (c.IsShockwave && !row.ContainsKey("swFlipDelayMinSec"))
                Debug.LogError($"{tag}: shockwave row has no swFlipDelayMinSec column, so its flips use the default timing " +
                                "and cap (1 to 3.5 s, 5 flips), whatever the file says under older names.");
            c.swFlipDelayMinSec  = CsvTable.GetFloat(row, "swFlipDelayMinSec", 1f);
            c.swFlipDelayMaxSec  = CsvTable.GetFloat(row, "swFlipDelayMaxSec", 3.5f);
            c.swWindowSec        = CsvTable.GetFloat(row, "swWindowSec",       0.9f);
            c.swMaxEarlyPerRound = Mathf.Max(0, CsvTable.GetInt(row, "swMaxEarlyPerRound", 1));
            c.maxFlipsPerRound   = Mathf.Max(0, CsvTable.GetInt(row, "maxFlipsPerRound", c.IsShockwave ? 5 : 0));
            c.showDebug          = CsvTable.GetBool(row, "showDebug", false);

            c.Validate(tag);
            return c;
        }

        /// <summary>
        /// Clamps what would otherwise run an impossible stimulus, and says so. A frame rate below 1
        /// cannot be presented, and a difference grid that is empty or upside down cannot be searched.
        /// </summary>
        void Validate(string tag)
        {
            // Down from 1 fps there is no slower whole frame rate to show.
            if (baseFps < 1 || (baseFps < 2 && direction == FpsDirection.Down))
            {
                Debug.LogError($"{tag}: baseFps={baseFps} leaves nothing to compare it with — using 60.");
                baseFps = 60;
            }

            if (!(diffStepMs > 0f))
            {
                Debug.LogWarning($"{tag}: diffStepMs={diffStepMs} — using 0.5.");
                diffStepMs = 0.5f;
            }

            if (!(minDiffMs > 0f))
            {
                Debug.LogWarning($"{tag}: minDiffMs={minDiffMs} — a difference of 0 is no stimulus. " +
                                  $"Using one step, {diffStepMs}.");
                minDiffMs = diffStepMs;
            }

            float ceiling = direction == FpsDirection.Down
                ? FrameTimeDiffMs(baseFps, 1)
                : (float)BaseFrameMs - diffStepMs;
            if (maxDiffMs > ceiling)
            {
                Debug.LogError($"{tag}: {baseFps} fps {DirectionCell} by {maxDiffMs} ms is past what a frame rate " +
                                $"can show — maxDiffMs clamped to {ceiling}.");
                maxDiffMs = ceiling;
            }

            if (maxDiffMs < minDiffMs)
            {
                Debug.LogError($"{tag}: maxDiffMs ({maxDiffMs}) is below minDiffMs ({minDiffMs}) — " +
                                "QUEST+ would have nothing to search. Using maxDiffMs = minDiffMs.");
                maxDiffMs = minDiffMs;
            }

            // Too small a range to reach the next whole frame rate: show that one, or there is no stimulus.
            if (DiffGridMs().Length == 0)
            {
                int next = direction == FpsDirection.Up ? baseFps + 1 : baseFps - 1;
                Debug.LogError($"{tag}: no whole frame rate lies within {minDiffMs}–{maxDiffMs} ms of {baseFps} fps " +
                                $"— using the nearest one, {next} fps, as the only difference.");
                minDiffMs = maxDiffMs = FrameTimeDiffMs(baseFps, next);
            }

            // A threshold grid coarser than the stimulus steps quantises the estimate: the posterior piles
            // onto the nearest grid value, whatever the steps could have resolved.
            float thetaSpacing = (maxDiffMs - minDiffMs) / (threshCount - 1);
            if (thetaSpacing > diffStepMs * 1.01f)
                Debug.LogWarning($"{tag}: threshCount={threshCount} spaces the threshold grid {thetaSpacing:0.##} ms " +
                                  $"apart, coarser than the {diffStepMs}-ms steps. Set it to at least " +
                                  $"{Mathf.CeilToInt((maxDiffMs - minDiffMs) / diffStepMs) + 1}.");

            if (!IsShockwave && maxCrossingsPerRound <= 0 && roundTimeoutSec <= 0f)
                Debug.LogError($"{tag}: maxCrossingsPerRound=0 and roundTimeoutSec=0 — a round with no " +
                                "shot would never end. Set one of them in Data/ExperimentConfig.csv.");

            if (IsShockwave && maxFlipsPerRound <= 0 && roundTimeoutSec <= 0f)
            {
                Debug.LogWarning($"{tag}: shockwave row has maxFlipsPerRound=0 and roundTimeoutSec=0 — using maxFlipsPerRound=5.");
                maxFlipsPerRound = 5;
            }

            ValidateAutopilot(tag);
        }

        // The autopilot's settings are clamped on every row, so a stray value in a manual row's cell cannot
        // fail later; only an auto level is warned about, since only there do they fly anything.
        void ValidateAutopilot(string tag)
        {
            bool warn = AutoFlight;
            if (!(autoSpeed > 0f))
            {
                if (warn) Debug.LogError($"{tag}: autoSpeed={autoSpeed} — the autopilot needs a speed above 0. Using 20.");
                autoSpeed = 20f;
            }
            float sweep = Mathf.Clamp(autoSweep, 0.2f, 1f);
            if (warn && !Mathf.Approximately(sweep, autoSweep))
                Debug.LogWarning($"{tag}: autoSweep={autoSweep} is outside 0.2–1 — using {sweep}.");
            autoSweep   = sweep;
            autoTurnSec = Mathf.Max(0f, autoTurnSec);
            autoHeight  = Mathf.Clamp01(autoHeight);
            autoBobAmp  = Mathf.Max(0f, autoBobAmp);
            autoBobHz   = Mathf.Max(0f, autoBobHz);

            if (swFlipDelayMaxSec < swFlipDelayMinSec)
                (swFlipDelayMinSec, swFlipDelayMaxSec) = (swFlipDelayMaxSec, swFlipDelayMinSec);
            swFlipDelayMinSec = Mathf.Max(0.05f, swFlipDelayMinSec);
            swFlipDelayMaxSec = Mathf.Max(swFlipDelayMinSec, swFlipDelayMaxSec);
            swWindowSec       = Mathf.Max(0.05f, swWindowSec);
        }


        static FpsDirection ParseDirection(string cell, string tag)
        {
            string s = (cell ?? "").Trim();
            if (s.Equals("up",   System.StringComparison.OrdinalIgnoreCase)) return FpsDirection.Up;
            if (s.Equals("down", System.StringComparison.OrdinalIgnoreCase)) return FpsDirection.Down;
            Debug.LogError($"{tag}: direction='{cell}' is not 'up' or 'down' — using down.");
            return FpsDirection.Down;
        }

        static FpsDirection ParseFtDirection(string cell, string tag)
        {
            string s = (cell ?? "").Trim();
            if (s.Equals("up",   System.StringComparison.OrdinalIgnoreCase)) return FpsDirection.Down;
            if (s.Equals("down", System.StringComparison.OrdinalIgnoreCase)) return FpsDirection.Up;
            Debug.LogError($"{tag}: ftDirection='{cell}' is not 'up' or 'down' — using up.");
            return FpsDirection.Down;
        }

        static AlteredSideMode ParseSide(string cell, string tag)
        {
            string s = (cell ?? "").Trim();
            if (s.Equals("random", System.StringComparison.OrdinalIgnoreCase)) return AlteredSideMode.Random;
            if (s.Equals("left",   System.StringComparison.OrdinalIgnoreCase)) return AlteredSideMode.Left;
            if (s.Equals("right",  System.StringComparison.OrdinalIgnoreCase)) return AlteredSideMode.Right;
            Debug.LogError($"{tag}: alteredSide='{cell}' is not random/left/right — using random.");
            return AlteredSideMode.Random;
        }

        static FrameLimiterMode ParseLimiter(string cell, string tag)
        {
            string s = (cell ?? "").Trim();
            if (s.Equals("precise", System.StringComparison.OrdinalIgnoreCase)) return FrameLimiterMode.Precise;
            if (s.Equals("unity",   System.StringComparison.OrdinalIgnoreCase)) return FrameLimiterMode.Unity;
            Debug.LogError($"{tag}: frameLimiter='{cell}' is not unity/precise — using unity.");
            return FrameLimiterMode.Unity;
        }

        static LevelType ParseLevel(string cell, string tag)
        {
            string s = (cell ?? "").Trim();
            if (s.Equals("manual", System.StringComparison.OrdinalIgnoreCase)) return LevelType.Manual;
            if (s.Equals("scroll", System.StringComparison.OrdinalIgnoreCase)) return LevelType.Scroll;
            if (s.Equals("auto",   System.StringComparison.OrdinalIgnoreCase)) return LevelType.Auto;
            if (s.Equals("shockwave", System.StringComparison.OrdinalIgnoreCase)) return LevelType.Shockwave;
            Debug.LogError($"{tag}: levelType='{cell}' is not manual/scroll/auto/shockwave — using manual.");
            return LevelType.Manual;
        }

        static ScrollFollowMode ParseFollow(string cell, string tag)
        {
            string s = (cell ?? "").Trim();
            if (s.Length == 0) return ScrollFollowMode.Inherit;
            if (s.Equals("locked",   System.StringComparison.OrdinalIgnoreCase)) return ScrollFollowMode.Locked;
            if (s.Equals("handheld", System.StringComparison.OrdinalIgnoreCase)) return ScrollFollowMode.Handheld;
            Debug.LogError($"{tag}: scrollFollow='{cell}' is not locked/handheld (or blank) — leaving the " +
                            "Inspector's setting.");
            return ScrollFollowMode.Inherit;
        }

        static AutoPathMode ParsePath(string cell, string tag)
        {
            string s = (cell ?? "").Trim();
            if (s.Equals("linear", System.StringComparison.OrdinalIgnoreCase)) return AutoPathMode.Linear;
            if (s.Equals("sine",   System.StringComparison.OrdinalIgnoreCase)) return AutoPathMode.Sine;
            Debug.LogError($"{tag}: autoPath='{cell}' is not linear/sine — using linear.");
            return AutoPathMode.Linear;
        }

        static AutoStartMode ParseStart(string cell, string tag)
        {
            string s = (cell ?? "").Trim();
            if (s.Equals("random",   System.StringComparison.OrdinalIgnoreCase)) return AutoStartMode.Random;
            if (s.Equals("continue", System.StringComparison.OrdinalIgnoreCase)) return AutoStartMode.Continue;
            Debug.LogError($"{tag}: autoStart='{cell}' is not random/continue — using random.");
            return AutoStartMode.Random;
        }


        // ── Log echo ─────────────────────────────────────────────────────────

        /// <summary>This row's values in the file's column order — echoed into the logs.</summary>
        public string[] ValuesInColumnOrder()
        {
            var vals = new string[Columns.Length];
            for (int i = 0; i < Columns.Length; i++)
            {
                string key = Columns[i].Trim();
                vals[i] = Row != null && Row.TryGetValue(key, out string v) ? v : string.Empty;
            }
            return vals;
        }

        /// <summary>Log-header names for the echoed settings, prefixed so they can never collide
        /// with a metric column of the same name.</summary>
        public string[] SettingColumnNames()
        {
            var names = new string[Columns.Length];
            for (int i = 0; i < Columns.Length; i++) names[i] = "cfg_" + Columns[i].Trim();
            return names;
        }

        /// <summary>
        /// An auto level's autopilot settings as the block actually flies them, written into the row's cells that
        /// the file has a column for but left blank — so the cfg_* echo in the logs never shows a blank for a
        /// setting that flew. Only for auto levels: the other levels ignore these settings, and their blanks say so.
        /// </summary>
        void FillAutopilotEcho()
        {
            if (Row == null) return;
            void Fill(string key, string value)
            {
                if (System.Array.Exists(Columns, c => c.Trim().Equals(key, System.StringComparison.OrdinalIgnoreCase)) &&
                    (!Row.TryGetValue(key, out string v) || string.IsNullOrWhiteSpace(v)))
                    Row[key] = value;
            }
            Fill("showDebug", showDebug ? "1" : "0");
            if (!AutoFlight) return;
            Fill("autoSpeed",   CsvTable.F(autoSpeed, 3));
            Fill("autoPath",    autoPath == AutoPathMode.Sine ? "sine" : "linear");
            Fill("autoSweep",   CsvTable.F(autoSweep, 3));
            Fill("autoTurnSec", CsvTable.F(autoTurnSec, 3));
            Fill("autoHeight",  CsvTable.F(autoHeight, 3));
            Fill("autoBobAmp",  CsvTable.F(autoBobAmp, 3));
            Fill("autoBobHz",   CsvTable.F(autoBobHz, 3));
            Fill("autoStart",   autoStart == AutoStartMode.Continue ? "continue" : "random");
            if (!IsShockwave) return;
            Fill("swFlipDelayMinSec",  CsvTable.F(swFlipDelayMinSec, 3));
            Fill("swFlipDelayMaxSec",  CsvTable.F(swFlipDelayMaxSec, 3));
            Fill("swWindowSec",        CsvTable.F(swWindowSec, 3));
            Fill("swMaxEarlyPerRound", swMaxEarlyPerRound.ToString(CultureInfo.InvariantCulture));
            Fill("maxFlipsPerRound",   maxFlipsPerRound.ToString(CultureInfo.InvariantCulture));
        }

        // The seed file, written only when Data/ExperimentConfig.csv is missing. The 4 conditions: shockwave (the UFO
        // flies itself) and manual laser, each from a 2 ms base with frame time going up (2.5 to 50 ms: 400 to 20 fps) and
        // from a 16.67 ms base with frame time going down (16.17 to exactly 2 ms: 62 to 500 fps).
        // guessRate is 0.256 for the laser on 16:9 (closeRadius=6.0) and 0.36 for the shockwave
        // timing schedule (1–3.5s delay, 0.9s window, 1 early forgiven, 5 flips max).
        static string DefaultCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("levelType,baseFrameMs,ftDirection,maxDiffMs,minDiffMs,diffStepMs,alteredSide,frameLimiter," +
                          "maxCrossingsPerRound,roundTimeoutSec,hitPoints,missPoints," +
                          "threshCount,slopeMin,slopeMax,slopeCount,lapseMin,lapseMax,lapseCount," +
                          "guessRate,maxTrials,minTrials,stopSD," +
                          "scrollFollow,autoSpeed,autoPath,autoSweep,autoTurnSec,autoHeight,autoBobAmp,autoBobHz,autoStart," +
                          "swFlipDelayMinSec,swFlipDelayMaxSec,swWindowSec,swMaxEarlyPerRound,maxFlipsPerRound,showDebug");

            // threshCount spaces the threshold grid one 0.5-ms step apart over each row's range.
            const string down    = "2,up,48,0.5,0.5,random,unity,15,20,100,-10,96";
            const string up      = "16.667,down,14.667,0.5,0.5,random,unity,15,20,100,-10,30";
            const string swDown  = "2,up,48,0.5,0.5,random,unity,0,0,100,-10,96";
            const string swUp    = "16.667,down,14.667,0.5,0.5,random,unity,0,0,100,-10,30";
            const string grid    = "1,7,10,0,0.06,4,0.256,50,8,1";
            const string swGrid  = "1,7,10,0,0.06,4,0.36,50,8,1";
            const string auto    = ",20,linear,0.92,1,0.38,0.25,1.1,random";
            const string swTiming = ",1,3.5,0.9,1,5,0";
            const string noSw    = ",,,,,,,0";
            sb.AppendLine($"shockwave,{swDown},{swGrid},{auto}{swTiming}");
            sb.AppendLine($"shockwave,{swUp},{swGrid},{auto}{swTiming}");
            sb.AppendLine($"manual,{down},{grid},,,,,,,,{noSw}");
            sb.AppendLine($"manual,{up},{grid},,,,,,,,{noSw}");
            return sb.ToString();
        }
    }
}
