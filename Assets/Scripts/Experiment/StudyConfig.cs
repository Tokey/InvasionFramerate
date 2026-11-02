using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>Which way the altered side of the tower moves away from the base frame rate.</summary>
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
    ///   Unity    Application.targetFrameRate — Unity's own limiter, as the stutter study used, and
    ///            the default. Its jitter is whatever the engine gives (±0.6 ms at 60 fps in the
    ///            Editor logs from the stutter study), but it leaves the main thread free between
    ///            frames instead of spinning a core on it.
    ///   Precise  <see cref="FrameLimiter"/>'s busy-wait, hooked into the player loop where Unity's
    ///            own limiter waits, ahead of time sampling and input processing. Frame times land
    ///            within microseconds of the target; it costs one CPU core spinning while it waits.
    /// </summary>
    public enum FrameLimiterMode { Precise, Unity }

    /// <summary>
    /// The kind of level a block is played in. The split, the shot, the scoring and QUEST+ are the same in all
    /// three; what changes is who flies the UFO and whether the world moves.
    ///
    ///   Manual  the participant flies the UFO with the mouse, the camera holding still (it pans a little between
    ///           rounds). The original level.
    ///   Scroll  the participant flies the UFO and the camera follows it, so the endless world scrolls past under
    ///           it (<see cref="GameManager.worldScroll"/>). The boundary repeats once a period there, the frame
    ///           rates swapping sides at every copy.
    ///   Auto    the UFO flies itself back and forth across the screen (<see cref="UfoAutopilot"/>), the camera
    ///           holding still as in Manual. The participant only fires: when the UFO is over the place where the
    ///           frame rate changes. The mouse does not move it.
    /// </summary>
    public enum LevelType { Manual, Scroll, Auto }

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
    /// <see cref="baseFps"/>, the other at base − Δ (<see cref="FpsDirection.Down"/>) or base + Δ
    /// (<see cref="FpsDirection.Up"/>). The participant flies across the boundary, notices where
    /// the motion changes, and shoots the hidden tower; landing the shot is the detection response.
    /// QUEST+ picks Δ every round, from <see cref="minDiffFps"/> to <see cref="maxDiffFps"/>, and
    /// homes in on the smallest Δ the participant can locate.
    ///
    /// Columns, grouped:
    ///
    ///   level      levelType (manual / scroll / auto), scrollFollow
    ///   stimulus   baseFps, direction, maxDiffFps, minDiffFps, diffStepFps, alteredSide, frameLimiter
    ///   round      maxCrossingsPerRound, roundTimeoutSec, hitPoints, missPoints
    ///   QUEST+     threshCount, slope/lapse grids, guessRate, maxTrials, minTrials, stopSD
    ///   autopilot  autoSpeed, autoPath, autoSweep, autoTurnSec, autoHeight, autoBobAmp, autoBobHz, autoStart
    ///              (auto levels only)
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

        /// <summary>Study identifier, for console messages only.</summary>
        public readonly string label = "fps_jnd";

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
        public bool AutoFlight => levelType == LevelType.Auto;

        /// <summary>"manual" / "scroll" / "auto" — the levelType column of every log.</summary>
        public string LevelCell => LevelCellOf(levelType);

        public static string LevelCellOf(LevelType t) =>
            t == LevelType.Scroll ? "scroll" : t == LevelType.Auto ? "auto" : "manual";

        // ── Stimulus ─────────────────────────────────────────────────────────

        /// <summary>The reference frame rate. One side of the tower always runs at exactly this.</summary>
        public int baseFps = 60;

        /// <summary>Down: the altered side runs at base − Δ. Up: base + Δ.</summary>
        public FpsDirection direction = FpsDirection.Down;

        /// <summary>Largest frame-rate difference QUEST+ may present, and the top of its threshold
        /// grid. On a Down row it is clamped so the altered side never drops below 1 fps.</summary>
        public int maxDiffFps = 50;

        /// <summary>Smallest difference QUEST+ may present, and the bottom of its threshold grid.</summary>
        public int minDiffFps = 1;

        /// <summary>
        /// Spacing of the differences QUEST+ may present: minDiffFps, minDiffFps + step, … up to
        /// maxDiffFps — an exactly even grid of whole frame rates. (maxDiffFps − minDiffFps) should
        /// be a multiple of it; if not, maxDiffFps is pulled down to the last value on the grid.
        /// </summary>
        public int diffStepFps = 1;

        /// <summary>How many Δ values QUEST+ chooses between.</summary>
        public int DiffGridSize => (maxDiffFps - minDiffFps) / diffStepFps + 1;

        public AlteredSideMode  alteredSide  = AlteredSideMode.Random;
        public FrameLimiterMode frameLimiter = FrameLimiterMode.Unity;

        /// <summary>The altered side's frame rate for a difference of <paramref name="diffFps"/>.</summary>
        public int AlteredFpsFor(int diffFps) =>
            Mathf.Max(1, direction == FpsDirection.Up ? baseFps + diffFps : baseFps - diffFps);

        /// <summary>"60↓" / "120↑ auto" — for console lines. The level is named unless it is manual.</summary>
        public string ShortLabel =>
            $"{baseFps}{(direction == FpsDirection.Up ? "↑" : "↓")}{(levelType == LevelType.Manual ? "" : " " + LevelCell)}";

        public string DirectionCell => direction == FpsDirection.Up ? "up" : "down";
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
        public float guessRate = 0.235f;
        public int   maxTrials = 50;
        public int   minTrials = 8;
        /// <summary>Stop once the posterior SD of the threshold (fps) is at or below this, after
        /// minTrials. 0 = run to maxTrials.</summary>
        public float stopSD    = 2f;

        // ── Autopilot (auto levels) ──────────────────────────────────────────
        // How the UFO flies itself in an auto level. Ignored by the other levels. The path is a function of
        // flight time alone (AutoFlightPath), so it is the same at every frame rate. Its motion is the stimulus
        // the participant watches, so keep it the same across a study's auto blocks unless it is what is varied.

        /// <summary>Horizontal speed, world units a second (the mean speed for a sine path). The view is about
        /// 47 units wide at the play plane and the hit window 11, so at 8 the ship crosses the window in about
        /// 1.4 s and runs about 4 units past the boundary in a 0.5 s reaction time.</summary>
        public float autoSpeed = 8f;

        public AutoPathMode autoPath = AutoPathMode.Linear;

        /// <summary>Share of the view's width the sweep spans, centred: 0.9 runs the ship's centre from 5% to 95%
        /// of the width. Must reach past every place the tower can be dealt (the middle 80%), or a boundary near
        /// an edge could never be crossed.</summary>
        public float autoSweep = 0.9f;

        /// <summary>Linear path: seconds a turn at either end takes. The turn is a half sine at the same speed,
        /// so the ship never jerks; 0 turns on the spot.</summary>
        public float autoTurnSec = 0.6f;

        /// <summary>Flight height within the UFO's flight band: 0 = as low as it may go, 1 = as high.</summary>
        public float autoHeight = 0.4f;

        /// <summary>The vertical sine wave the ship bobs on as it flies: amplitude in world units (0.5 is about
        /// 20 px of a 1080p screen) and frequency in Hz. 0 flies level.</summary>
        public float autoBobAmp = 0.5f;
        public float autoBobHz  = 0.4f;

        public AutoStartMode autoStart = AutoStartMode.Random;

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
        /// at startup and warns when the CSV disagrees. r = 6.0 gives γ = 0.255 on 16:9.
        /// </summary>
        public readonly float closeRadius      = 6.0f;
        public readonly bool  showHitZone      = true;
        /// <summary>Half-width (world units) of the dead zone around the tower. The UFO keeps its
        /// side until it is this far past the boundary, so hovering on the line cannot flicker the
        /// frame rate between the two caps every frame.</summary>
        public readonly float crossingDeadZone = 0.25f;
        public readonly float fireCooldown     = 0.25f;
        /// <summary>How long the reveal holds after a shot — the tower, and both sides' frame
        /// rates. Long enough to read two numbers and their measured values.</summary>
        public readonly float revealHoldSec    = 2f;
        /// <summary>How long "GO!" holds and shakes before firing is handed back.</summary>
        public readonly float readyBeatSec     = 0.5f;

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
            if (!System.Array.Exists(columns, c => c.Trim().Equals("baseFps", System.StringComparison.OrdinalIgnoreCase)))
                Debug.LogError($"[StudyConfig] {path} has no baseFps column — it looks like the old " +
                                "frame-time-stutter config. Delete it to have the frame-rate defaults " +
                                "written, or add the new columns by hand.");

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
                                  "levelType column (manual / scroll / auto) to say so in the file.");
            return blocks;
        }

        /// <summary>"scroll level, 60 fps down by 1–50 in steps of 1 (precise limiter)"; an auto level adds its
        /// flight.</summary>
        public string Describe()
        {
            string d = $"{LevelCell} level, {baseFps} fps {DirectionCell} by {minDiffFps}–{maxDiffFps} in steps of " +
                       $"{diffStepFps} ({LimiterCell} limiter)";
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

            c.baseFps      = CsvTable.GetInt(row, "baseFps", 60);
            c.direction    = ParseDirection(CsvTable.GetString(row, "direction", "down"), tag);
            c.maxDiffFps   = CsvTable.GetInt(row, "maxDiffFps", 50);
            c.minDiffFps   = CsvTable.GetInt(row, "minDiffFps", 1);
            c.diffStepFps  = ReadStep(row, c.minDiffFps, c.maxDiffFps, tag);
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
            c.guessRate   = Mathf.Clamp01(CsvTable.GetFloat(row, "guessRate", 0.235f));
            c.maxTrials   = Mathf.Max(1, CsvTable.GetInt(row, "maxTrials", 50));
            c.minTrials   = Mathf.Max(0, CsvTable.GetInt(row, "minTrials", 8));
            c.stopSD      = Mathf.Max(0f, CsvTable.GetFloat(row, "stopSD", 2f));

            c.autoSpeed   = CsvTable.GetFloat(row, "autoSpeed", 8f);
            c.autoPath    = ParsePath(CsvTable.GetString(row, "autoPath", "linear"), tag);
            c.autoSweep   = CsvTable.GetFloat(row, "autoSweep", 0.9f);
            c.autoTurnSec = CsvTable.GetFloat(row, "autoTurnSec", 0.6f);
            c.autoHeight  = CsvTable.GetFloat(row, "autoHeight", 0.4f);
            c.autoBobAmp  = CsvTable.GetFloat(row, "autoBobAmp", 0.5f);
            c.autoBobHz   = CsvTable.GetFloat(row, "autoBobHz", 0.4f);
            c.autoStart   = ParseStart(CsvTable.GetString(row, "autoStart", "random"), tag);

            c.Validate(tag);
            return c;
        }

        /// <summary>
        /// Clamps what would otherwise run an impossible stimulus, and says so. A frame rate below 1
        /// cannot be presented, and a difference grid that is empty or upside down cannot be searched.
        /// </summary>
        void Validate(string tag)
        {
            if (baseFps < 1)
            {
                Debug.LogError($"{tag}: baseFps={baseFps} is not a frame rate — using 60.");
                baseFps = 60;
            }

            if (minDiffFps < 1)
            {
                Debug.LogWarning($"{tag}: minDiffFps={minDiffFps} — a difference of 0 is no stimulus. Using 1.");
                minDiffFps = 1;
            }

            if (direction == FpsDirection.Down && baseFps - maxDiffFps < 1)
            {
                int clamped = baseFps - 1;
                Debug.LogError($"{tag}: {baseFps} fps down by {maxDiffFps} would go below 1 fps — " +
                                $"maxDiffFps clamped to {clamped}.");
                maxDiffFps = clamped;
            }

            if (diffStepFps < 1)
            {
                Debug.LogWarning($"{tag}: diffStepFps={diffStepFps} — using 1.");
                diffStepFps = 1;
            }

            // Pulled onto the grid, so the largest Δ QUEST+ can show IS maxDiffFps and the logs'
            // maxDiffFps means what it says.
            if (maxDiffFps > minDiffFps && (maxDiffFps - minDiffFps) % diffStepFps != 0)
            {
                int onGrid = minDiffFps + (maxDiffFps - minDiffFps) / diffStepFps * diffStepFps;
                Debug.LogWarning($"{tag}: {minDiffFps}–{maxDiffFps} fps is not a whole number of " +
                                  $"{diffStepFps}-fps steps — maxDiffFps pulled down to {onGrid}.");
                maxDiffFps = onGrid;
            }

            if (maxDiffFps < minDiffFps)
            {
                Debug.LogError($"{tag}: maxDiffFps ({maxDiffFps}) is below minDiffFps ({minDiffFps}) — " +
                                "QUEST+ would have nothing to search. Using maxDiffFps = minDiffFps.");
                maxDiffFps = minDiffFps;
            }

            if (maxCrossingsPerRound <= 0 && roundTimeoutSec <= 0f)
                Debug.LogError($"{tag}: maxCrossingsPerRound=0 and roundTimeoutSec=0 — a round with no " +
                                "shot would never end. Set one of them in Data/ExperimentConfig.csv.");

            ValidateAutopilot(tag);
        }

        // The autopilot's settings are clamped on every row, so a stray value in a manual row's cell cannot
        // fail later; only an auto level is warned about, since only there do they fly anything.
        void ValidateAutopilot(string tag)
        {
            bool warn = AutoFlight;
            if (!(autoSpeed > 0f))
            {
                if (warn) Debug.LogError($"{tag}: autoSpeed={autoSpeed} — the autopilot needs a speed above 0. Using 8.");
                autoSpeed = 8f;
            }
            float sweep = Mathf.Clamp(autoSweep, 0.2f, 1f);
            if (warn && !Mathf.Approximately(sweep, autoSweep))
                Debug.LogWarning($"{tag}: autoSweep={autoSweep} is outside 0.2–1 — using {sweep}.");
            autoSweep   = sweep;
            autoTurnSec = Mathf.Max(0f, autoTurnSec);
            autoHeight  = Mathf.Clamp01(autoHeight);
            autoBobAmp  = Mathf.Max(0f, autoBobAmp);
            autoBobHz   = Mathf.Max(0f, autoBobHz);
        }


        /// <summary>
        /// diffStepFps, or — for a config written before that column existed — the step implied by
        /// its stimCount over the same range.
        /// </summary>
        static int ReadStep(Dictionary<string, string> row, int min, int max, string tag)
        {
            if (row.ContainsKey("diffStepFps") || !row.ContainsKey("stimCount"))
                return CsvTable.GetInt(row, "diffStepFps", 1);

            int count = Mathf.Max(2, CsvTable.GetInt(row, "stimCount", 50));
            int step  = Mathf.Max(1, Mathf.RoundToInt((max - min) / (float)(count - 1)));
            Debug.LogWarning($"{tag}: stimCount is superseded by diffStepFps — using a step of {step} fps. " +
                              "Update Data/ExperimentConfig.csv.");
            return step;
        }

        static FpsDirection ParseDirection(string cell, string tag)
        {
            string s = (cell ?? "").Trim();
            if (s.Equals("up",   System.StringComparison.OrdinalIgnoreCase)) return FpsDirection.Up;
            if (s.Equals("down", System.StringComparison.OrdinalIgnoreCase)) return FpsDirection.Down;
            Debug.LogError($"{tag}: direction='{cell}' is not 'up' or 'down' — using down.");
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
            Debug.LogError($"{tag}: levelType='{cell}' is not manual/scroll/auto — using manual.");
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
            if (!AutoFlight || Row == null) return;
            void Fill(string key, string value)
            {
                if (System.Array.Exists(Columns, c => c.Trim().Equals(key, System.StringComparison.OrdinalIgnoreCase)) &&
                    (!Row.TryGetValue(key, out string v) || string.IsNullOrWhiteSpace(v)))
                    Row[key] = value;
            }
            Fill("autoSpeed",   CsvTable.F(autoSpeed, 3));
            Fill("autoPath",    autoPath == AutoPathMode.Sine ? "sine" : "linear");
            Fill("autoSweep",   CsvTable.F(autoSweep, 3));
            Fill("autoTurnSec", CsvTable.F(autoTurnSec, 3));
            Fill("autoHeight",  CsvTable.F(autoHeight, 3));
            Fill("autoBobAmp",  CsvTable.F(autoBobAmp, 3));
            Fill("autoBobHz",   CsvTable.F(autoBobHz, 3));
            Fill("autoStart",   autoStart == AutoStartMode.Continue ? "continue" : "random");
        }

        // The seed file, written only when Data/ExperimentConfig.csv is missing. The same five rows the repository
        // ships: the two most common refresh rates and 500, each searched down, and 60 and 144 searched up to
        // 500, all as scroll levels. guessRate is the laser's geometric chance level on 16:9 (see
        // GuessRateCalculator). The autopilot columns are blank: they only apply to auto rows.
        static string DefaultCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("levelType,baseFps,direction,maxDiffFps,minDiffFps,diffStepFps,alteredSide,frameLimiter," +
                          "maxCrossingsPerRound,roundTimeoutSec,hitPoints,missPoints," +
                          "threshCount,slopeMin,slopeMax,slopeCount,lapseMin,lapseMax,lapseCount," +
                          "guessRate,maxTrials,minTrials,stopSD," +
                          "scrollFollow,autoSpeed,autoPath,autoSweep,autoTurnSec,autoHeight,autoBobAmp,autoBobHz,autoStart");

            const string tail = "15,15,100,-10";
            const string grid = "50,1,7,10,0,0.06,4,0.235,50,8";
            const string auto = "locked,,,,,,,,";
            sb.AppendLine($"scroll,60,down,59,1,1,random,unity,{tail},{grid},5,{auto}");
            sb.AppendLine($"scroll,144,down,143,2,2,random,unity,{tail},{grid},10,{auto}");
            sb.AppendLine($"scroll,500,down,499,5,5,random,unity,{tail},{grid},20,{auto}");
            sb.AppendLine($"scroll,60,up,440,5,5,random,unity,{tail},{grid},20,{auto}");
            sb.AppendLine($"scroll,144,up,356,4,4,random,unity,{tail},{grid},15,{auto}");
            return sb.ToString();
        }
    }
}
