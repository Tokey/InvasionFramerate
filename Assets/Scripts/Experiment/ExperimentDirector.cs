using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JndUfo
{
    /// <summary>
    /// Runs a session end to end: reserves the session ID, plays one practice phase
    /// (Data/PracticeRounds.csv), then every block of Data/ExperimentConfig.csv in this session's
    /// Latin-square order as a main run of QUEST+ — "MAIN 1/3", "MAIN 2/3", … — and writes the
    /// session, shot and player logs.
    ///
    /// A block ends when its staircase reports IsFinished — the posterior-SD precision target
    /// (after minTrials) or the maxTrials cap, both from the block's row.
    ///
    /// Execution order 1000 puts <see cref="LateUpdate"/> after PerturbationController's, so a frame's
    /// log row sees the side and crossing that frame produced.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class ExperimentDirector : MonoBehaviour
    {
        public static ExperimentDirector Instance { get; private set; }

        [Header("Session Flow")]
        [Tooltip("Start automatically on Play. Turn off to call StartSession() yourself.")]
        public bool autoStart = true;
        [Tooltip("Key the participant presses to begin.")]
        public Key startKey = Key.Space;
        [Tooltip("Safety cap on how long to wait for an in-flight shot reveal once the run ends.")]
        public float maxRevealWaitSec = 6f;
        [Tooltip("Seconds of 'thank you' before the app closes.")]
        public float thankYouCountdownSec = 3f;
        [Tooltip("Close the application once the session log is written. Turn off to leave the " +
                 "end screen up (useful while piloting in the Editor).")]
        public bool quitOnSessionComplete = true;

        [Header("Post-Session Database")]
        [Tooltip("After the logs are closed, run the script below to build a SQLite database " +
                 "from them. Requires Python on this machine. The CSVs are written either way — " +
                 "if this fails it only logs a warning, and the database can be rebuilt by " +
                 "running Analysis/build_db.py by hand.")]
        public bool buildDatabaseOnFinish = true;

        [Tooltip("Python command. An absolute path to python.exe is used as-is. Otherwise a " +
                 "Windows build first uses the private Python it ships in Python/ beside the " +
                 ".exe, then falls back to this command on PATH ('python'). The Editor has no " +
                 "bundled copy, so it always uses PATH.")]
        public string pythonExecutable = "python";

        [Tooltip("Script to run, relative to the project root in the Editor or to the folder " +
                 "holding the .exe in a build. An absolute path is used as-is.")]
        public string databaseScriptPath = "Analysis/build_db.py";

        [Tooltip("How long the closing screen waits for the database build before quitting " +
                 "anyway. The build keeps running after quit if it needs longer.")]
        public float databaseWaitSec = 20f;

        [Header("Practice Hints")]
        [Tooltip("During PRACTICE only, flash a line the moment the UFO crosses the frame-rate " +
                 "boundary, so the participant learns what they are looking for. Never shown in a " +
                 "main round — there it would give away the boundary QUEST+ is asking them to find.")]
        public bool showPracticeHints = true;

        [Tooltip("The standing prompt, up for the whole practice round and quietly breathing.")]
        public string standingHintText = "FIND WHERE THE FRAME RATE CHANGES";

        [Tooltip("Flashed as the UFO crosses the boundary. The tower is wherever the UFO is at that " +
                 "instant, so 'here' is the lesson.")]
        public string crossingHintText = "FRAME RATE CHANGED — FIRE HERE";

        [Tooltip("The standing prompt in a shockwave practice round.")]
        public string shockwaveStandingHintText = "WAIT FOR THE FRAME RATE TO CHANGE";

        [Tooltip("Flashed in a shockwave practice round the moment the frame rate flips.")]
        public string shockwaveFlipHintText = "FRAME RATE CHANGED — FIRE NOW!";

        [Tooltip("Floor on how long the alert stays readable after the round ENDS on it.")]
        [Min(0.1f)] public float practiceHintSec = 1.1f;

        [Header("Level Types")]
        [Tooltip("Play the practice rounds before the first block of EACH level type in the session's order " +
                 "(manual, scroll, auto, shockwave), so nobody meets a new kind of level in a " +
                 "block that counts. Off: one practice phase only, before MAIN 1, in its level.")]
        public bool practiceEachLevelType = true;

        [Tooltip("What the participant is told to do in a manual level (the mouse flies the UFO). Says nothing " +
                 "about the frame rates themselves: those are what is measured.")]
        [TextArea(2, 5)]
        public string manualInstruction =
            "The hidden tower splits the city in two.\n" +
            "One side has a different frame rate than the other.\n" +
            "Fly back and forth, find where the framerate changes, and shoot the hidden tower.";

        [Tooltip("The same for a scroll level (the mouse flies the UFO; the city scrolls past under it).")]
        [TextArea(2, 5)]
        public string scrollInstruction =
            "The hidden tower splits the city in two.\n" +
            "One side has a different frame rate than the other.\n" +
            "Fly back and forth as the city scrolls by, find where the framerate changes, and shoot the hidden tower.";

        [Tooltip("The same for an auto level (the UFO flies itself; the participant only fires).")]
        [TextArea(2, 5)]
        public string autoInstruction =
            "The hidden tower splits the city in two.\n" +
            "One side has a different frame rate than the other.\n" +
            "The UFO flies back and forth by itself. Watch it, and click to shoot\n" +
            "when it is right over the spot where the framerate changes.";

        [Tooltip("What the participant is told to do in a shockwave level. The timing window is not named.")]
        [TextArea(2, 5)]
        public string shockwaveInstruction =
            "The UFO flies back and forth by itself.\n" +
            "The whole screen's frame rate will change at random times.\n" +
            "Watch the motion, and click to fire the shockwave as soon as you notice the frame rate change.";

        [Header("References  (auto-found; drag-override if needed)")]
        public GameManager            gameManager;
        public LaserFirer             laser;
        public TowerManager           towerManager;
        public ScoreManager           scoreManager;
        public PerturbationController perturbation;
        public UfoController          ufo;
        public ExperimentOverlay      overlay;

        // ── Public state ─────────────────────────────────────────────────────
        /// <summary>True only while a phase is live and responses should count.</summary>
        public bool   PhaseOpen     { get; private set; }
        public int    SessionId     { get; private set; }
        public string RunId         { get; private set; }

        /// <summary>The block currently running — one row of ExperimentConfig.csv.</summary>
        public StudyConfig Config { get; private set; }

        /// <summary>The counterbalancing square, and this session's row of it.</summary>
        public LatinSquare Square    { get; private set; }
        public int         SquareRow { get; private set; }

        /// <summary>This session's running order, as 1-based ExperimentConfig.csv row numbers.</summary>
        public int[] BlockOrder { get; private set; } = new int[0];

        /// <summary>1-based position of the block in progress within this session's order — the
        /// counterpart to StudyConfig.blockIndex, which identifies the SETTING rather than when it
        /// was played. Both go into every log.</summary>
        public int BlockOrdinal { get; private set; }

        /// <summary>How far through the practice rounds the participant is, 0–1, for the HUD's
        /// progress bar. The main run's progress comes from the staircase — see QuestProgress.</summary>
        public float PracticeProgress
        {
            get
            {
                int rounds = _practice != null ? _practice.Count : 0;
                return rounds > 0 ? Mathf.Clamp01(_roundNumber / (float)rounds) : 0f;
            }
        }

        // Interned rather than Enum.ToString(): the frame log writes a side on EVERY frame, and
        // ToString() on an enum allocates a fresh string each time — garbage whose collection lands
        // as a long frame in a study of frame rates.
        const string SideLeft  = "Left";
        const string SideRight = "Right";
        static string SideCell(PerturbationController.Side s) =>
            s == PerturbationController.Side.Left ? SideLeft : SideRight;

        // A side as seen from a copy of the boundary across which the frame rates run the other way round.
        static PerturbationController.Side Turned(PerturbationController.Side s, bool turned) =>
            turned ? PerturbationController.Opposite(s) : s;

        // ── Private ──────────────────────────────────────────────────────────
        List<StudyConfig> _blocks;
        List<PracticePair> _practice = new List<PracticePair>();
        ExperimentLogger  _logger;
        SessionStats      _stats;

        Key _fireKey = Key.Space;

        DateTime _startedAt;
        float    _startRealtime;
        // The same instant in double: the frame log's phase clock is taken from it, because
        // Time.unscaledTime counts from the app's start in float, which an hour in resolves only
        // about 0.24 ms — an eighth of a frame at 500 fps.
        double   _startRealtimeD;
        // The origin Time.realtimeSinceStartup stamps (crossings, shots) are rebased against so they
        // sit on the frame log's axis. Normally _startRealtime itself — the two clocks share an
        // origin — and BeginPhase checks that each phase.
        float    _phaseClockOrigin;
        int      _frameIndex;

        // Set by RecordShot (from Update) and consumed by LateUpdate, so the frame row that carries
        // a response is the frame it happened on.
        bool  _shotFiredThisFrame;
        float _shotHitXThisFrame;

        // Presses so far in the round in progress, for its shot rows, and rounds so far in this block.
        int _shotsThisRound, _nEarly, _nLate, _nLockout, _nCooldown, _nNotLive, _blockRounds;

        // Rounds completed so far in this phase.
        int      _roundNumber;
        // The round the frame log labels the current frame with. Trails _roundNumber by a frame: the
        // response that closes a round advances _roundNumber from Update, and its own frame row
        // (written in LateUpdate) still belongs to the round it closed.
        int      _frameRound;
        // Main rounds run continuously across blocks; practice is numbered separately from 1.
        int      _mainRoundsCompleted;

        Vector3  _prevUfoPos;
        bool     _hasPrevUfoPos;

        // Non-null once the run has a reason to stop.
        string   _endReason;

        // True from the main phase opening until its session row is written. If the app goes away
        // in between, the row is written on the way out with endReason "abandoned".
        bool     _blockSummaryPending;

        // The block's screen, for its session row: the view's width at the play plane, and the chance
        // rate the screen implies for the block's level. Measured as the block is set up.
        float    _blockViewWidth  = float.NaN;
        float    _blockGuessRate  = float.NaN;

        // The UFO's recent X, one sample per logged frame, for its velocity at a response or a
        // crossing: over the last VelocityWindowSec rather than one frame, so it reads alike at 1 fps
        // and at 500 (one frame at a low rate is a long step; one at a high rate, between two mouse
        // reports, can be no step at all). A ring, allocated once.
        const int   TrailLength       = 1024;
        const float VelocityWindowSec = 0.1f;
        readonly float[] _trailT = new float[TrailLength];
        readonly float[] _trailX = new float[TrailLength];
        int _trailHead, _trailCount;

        // The round in progress, from its arming (firing handed back) to its response: the UFO's X
        // then, and its speed at each crossing.
        bool  _roundArmed;
        float _roundUfoXAtStart = float.NaN;
        float _roundCrossSpeedSum;
        int   _roundCrossSpeedN;
        float _roundLastCrossSpeed = float.NaN;

        // ── Lifecycle ────────────────────────────────────────────────────────

        void Awake()
        {
            Instance = this;
            QuietenConsoleLogging();
            ResolveReferences();

            // A command-line check of a build is not a session: no participant number, no logs, no
            // prompts — QaRunner drives the rounds itself. The instance stays registered with no
            // phase open, so each reveal ends the way it does between phases.
            if (QaRunner.Active)
            {
                enabled = false;
                return;
            }

            // A config written before levelType existed plays as the scene's world-scroll box says.
            _blocks = StudyConfig.LoadAll(gameManager != null && gameManager.worldScroll ? LevelType.Scroll
                                                                                          : LevelType.Manual);
            if (_blocks.Count == 0)
            {
                Debug.LogError("[ExperimentDirector] No study config — the session cannot run.");
                enabled = false;
                return;
            }

            _practice = PracticeRounds.Load();

            SessionId = SessionState.Peek();
            RunId     = JndUfo.RunId.Generate();

            // Loaded against the config's row count, so a mismatch between the two files is caught
            // here rather than surfacing as a missing block mid-session.
            Square     = LatinSquare.Load(_blocks.Count);
            SquareRow  = Square.RowForSession(SessionId);
            BlockOrder = Square.OrderForSession(SessionId);

            Config  = _blocks[BlockIndexAt(0)];
            _logger = new ExperimentLogger(SessionId, RunId, Config);

            // Settled here, in Awake, because the scenery is built in TowerManager's Start, once: endless if any
            // block scrolls, and the first block's scroll on from the first frame (CameraRig.Start).
            if (gameManager != null)
            {
                gameManager.EndlessWorldRequested = _blocks.Exists(b => b.Scrolls);
                gameManager.worldScroll           = Config.Scrolls;
            }

            Debug.Log($"[ExperimentDirector] Session {SessionId} (run {RunId}) — {_blocks.Count} block(s), " +
                      $"square row {SquareRow}/{Square.RowCount}, order {LatinSquare.OrderText(BlockOrder)} " +
                      $"({DescribeOrder()}). Display {Screen.width}x{Screen.height} @ " +
                      $"{Screen.currentResolution.refreshRateRatio.value:0.##} Hz.");
        }

        /// <summary>
        /// Drops stack traces from ordinary log and warning lines. A Unity log call's dominant cost is
        /// walking and formatting the managed stack — up to a millisecond of main thread — and lines
        /// are written on every response. Errors keep their traces.
        /// </summary>
        static void QuietenConsoleLogging()
        {
            Application.SetStackTraceLogType(LogType.Log,     StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        }

        /// <summary>A position in this session's order → an index into <see cref="_blocks"/>,
        /// clamped rather than trusted.</summary>
        int BlockIndexAt(int ordinalZeroBased)
        {
            if (BlockOrder == null || BlockOrder.Length == 0) return 0;
            int oneBased = BlockOrder[Mathf.Clamp(ordinalZeroBased, 0, BlockOrder.Length - 1)];
            return Mathf.Clamp(oneBased - 1, 0, _blocks.Count - 1);
        }

        // "60↓ → 500↓ → 120↑", for the startup line.
        string DescribeOrder()
        {
            if (BlockOrder == null || BlockOrder.Length == 0) return "";
            var parts = new List<string>(BlockOrder.Length);
            for (int i = 0; i < BlockOrder.Length; i++) parts.Add(_blocks[BlockIndexAt(i)].ShortLabel);
            return string.Join(" → ", parts);
        }

        void Start()
        {
            if (overlay == null) overlay = gameObject.AddComponent<ExperimentOverlay>();
            if (laser != null)
            {
                laser.SetFiringEnabled(false);
                _fireKey = laser.altFireKey;
                laser.OnPressIgnored += RecordIgnoredPress;
            }
            LogChanceHitRate();
            LogDisplayCheck();
            StartCoroutine(WriteSessionInfo());
            if (autoStart) StartSession();
        }

        /// <summary>
        /// SessionInfo_&lt;id&gt;.csv — the build, the machine, the display, the config files' hashes and every
        /// Inspector setting of the game's components (<see cref="SessionInfo"/>) — two frames in, once every
        /// Start has run and the scenery it builds exists. The first prompt is up by then; nothing is measured yet.
        /// </summary>
        IEnumerator WriteSessionInfo()
        {
            yield return null;
            yield return null;
            if (_logger == null) yield break;
            try { _logger.WriteSessionInfo(SessionInfo.Gather(SessionId, RunId)); }
            catch (Exception e) { Debug.LogError($"[ExperimentDirector] Session info not written: {e.Message}"); }

            if (_blocks != null && _blocks.Exists(b => b.showDebug))
                Debug.LogWarning("[ExperimentDirector] showDebug is ON for at least one block in ExperimentConfig.csv. " +
                                 "It spells out the frame-rate difference being measured: set showDebug=0 before running " +
                                 "a participant.");
        }

        /// <summary>
        /// Warns for every block that asks for a frame rate the display cannot show. vSync is off, so
        /// frames above the refresh rate are rendered but never appear as distinct images — they
        /// tear instead — and a "500 vs 400" comparison on a 165 Hz screen measures tearing, not
        /// frame rate.
        /// </summary>
        void LogDisplayCheck()
        {
            double hz = Screen.currentResolution.refreshRateRatio.value;
            if (_blocks == null || hz <= 1.0) return;

            foreach (StudyConfig block in _blocks)
            {
                int highest = block.direction == FpsDirection.Up
                    ? block.AlteredFpsFor(block.maxDiffMs)
                    : block.baseFps;
                if (highest > hz + 1.0)
                    Debug.LogWarning($"[ExperimentDirector] Block {block.blockIndex} ({block.Describe()}) " +
                                      $"runs up to {highest} fps, but this display refreshes at {hz:0.##} Hz. " +
                                      "Frames above the refresh rate never reach the screen as separate " +
                                      "images, so that part of the comparison is not visible. Run it on a " +
                                      $"display of at least {highest} Hz.");
            }

            if (_practice == null) return;
            foreach (PracticePair pair in _practice)
            {
                if (pair.highFps > hz + 1.0)
                    Debug.LogWarning($"[ExperimentDirector] Practice round {pair} fps runs above this " +
                                      $"display's {hz:0.##} Hz refresh rate, so the participant cannot see " +
                                      "that pair's full difference.");
            }
        }

        /// <summary>
        /// Prints the chance-level hit rate from the running screen's geometry so guessRate can be
        /// set from the study as it actually runs, and warns for every block whose CSV value
        /// disagrees. Logged, never applied: γ belongs in the config, versioned with the rest of the
        /// study. Every block shares the hit radius and the screen, so one report covers them all.
        /// </summary>
        void LogChanceHitRate()
        {
            if (_blocks == null || _blocks.Count == 0) return;

            var reported = new HashSet<string>();
            foreach (StudyConfig block in _blocks)
            {
                float gamma = ScreenGuessRate(block, out string report, out string key);
                if (float.IsNaN(gamma))
                {
                    Debug.LogWarning("[GuessRate] Could not compute a chance rate — no camera.");
                    return;
                }
                if (reported.Add(key)) Debug.Log(report);

                if (Mathf.Abs(block.guessRate - gamma) > 0.005f)
                    Debug.LogWarning($"[GuessRate] ExperimentConfig.csv row {block.blockIndex} ({block.LevelCell} " +
                                      $"level) has guessRate={block.guessRate:0.000} but this setup implies " +
                                      $"{gamma:0.000}. A γ far from chance biases the threshold — update the CSV " +
                                      "unless this is deliberate.");

                if (block.AutoFlight && !block.IsShockwave) CheckAutopilot(block);
            }
        }

        /// <summary>
        /// The chance rate this screen/timing implies for <paramref name="block"/>'s level.
        /// </summary>
        float ScreenGuessRate(StudyConfig block, out string report, out string key)
        {
            report = "";
            key    = "";

            if (block != null && block.IsShockwave)
            {
                float swGamma = ShockwaveGuessRate.Compute(block, out string detail);
                report = $"[GuessRate] Shockwave temporal chance rate: γ = {swGamma:0.0000} ({detail}).";
                key    = $"shockwave:{detail}";
                return swGamma;
            }

            Camera cam           = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
            float  fixedZ        = ufo != null ? ufo.fixedZ : 0f;
            float  spawnFraction = towerManager != null ? towerManager.spawnWidthFraction : 1f;

            key = "scene";
            bool ok = GuessRateCalculator.TryCompute(cam, fixedZ, spawnFraction, block != null ? block.closeRadius : 2f,
                                                     out float gamma, out report);
            return ok ? gamma : float.NaN;
        }

        /// <summary>
        /// Warnings for an auto level whose flight cannot do its job: a sweep that does not reach every place the
        /// tower can be dealt (a boundary beyond it is never crossed), or a round too short for the ship to get
        /// across the screen and back (a boundary on the far side may never be met).
        /// </summary>
        void CheckAutopilot(StudyConfig block)
        {
            float spawn = towerManager != null ? towerManager.spawnWidthFraction : 0.6f;
            if (!GuessRateCalculator.AutoSweepCoversSpawn(block.autoSweep, spawn))
                Debug.LogWarning($"[ExperimentDirector] Row {block.blockIndex} (auto): autoSweep={block.autoSweep:0.##} " +
                                  $"does not reach every place the tower can be dealt (the middle {spawn:0.##} of the " +
                                  "width), so some boundaries are never crossed. Use an autoSweep of at least " +
                                  $"{spawn:0.##}.");

            Camera cam = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
            if (cam == null || block.roundTimeoutSec <= 0f) return;
            float z      = ufo != null ? ufo.fixedZ : 0f;
            float width  = Mathf.Abs(CameraRig.ViewportPointAtZ(cam, 1f, 0.5f, z).x -
                                     CameraRig.ViewportPointAtZ(cam, 0f, 0.5f, z).x) * block.autoSweep;
            float period = block.AutoPath.Period(width);
            if (block.roundTimeoutSec < period)
                Debug.LogWarning($"[ExperimentDirector] Row {block.blockIndex} (auto): roundTimeoutSec=" +
                                  $"{block.roundTimeoutSec:0.#} is shorter than one sweep there and back " +
                                  $"({period:0.0} s at autoSpeed {block.autoSpeed:0.##}), so a round can end before " +
                                  "the ship has reached the boundary. Lengthen the round or speed the ship up.");
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (laser != null) laser.OnPressIgnored -= RecordIgnoredPress;
            CloseLogs();
            FrameLimiter.Release();
        }

        void OnApplicationQuit()
        {
            CloseLogs();
            FrameLimiter.Release();
        }

        // Everything still in memory goes to disk: the block in progress gets its summary row, then
        // the buffered rows are flushed. Safe to call more than once.
        void CloseLogs()
        {
            if (_logger == null) return;
            if (_blockSummaryPending && _stats != null)
            {
                _blockSummaryPending = false;
                _logger.WriteSessionLog(_startedAt, DateTime.Now, _stats, "abandoned", perturbation,
                                        _blockViewWidth, _blockGuessRate);
            }
            _logger.Dispose();
        }

        void ResolveReferences()
        {
            if (gameManager  == null) gameManager  = FindAnyObjectByType<GameManager>();
            if (laser        == null) laser        = FindAnyObjectByType<LaserFirer>();
            if (towerManager == null) towerManager = FindAnyObjectByType<TowerManager>();
            if (scoreManager == null) scoreManager = FindAnyObjectByType<ScoreManager>();
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();
            if (ufo          == null) ufo          = FindAnyObjectByType<UfoController>();
        }

        // ── Session ──────────────────────────────────────────────────────────

        public void StartSession() => StartCoroutine(RunSession());

        IEnumerator RunSession()
        {
            // StartSession is public, so it can legitimately be called before Start() has run.
            if (overlay == null) overlay = gameObject.AddComponent<ExperimentOverlay>();

            // One block per row of ExperimentConfig.csv, in this session's row of the Latin square
            // rather than file order, so neither position nor the block played before it is
            // confounded with the threshold a block produces.
            var practised = new HashSet<LevelType>();
            for (int i = 0; i < BlockOrder.Length; i++)
            {
                BlockOrdinal = i + 1;
                Config       = _blocks[BlockIndexAt(i)];

                // Practice runs ahead of the first block of each level type the order brings (or, with
                // practiceEachLevelType off, once ahead of the first block), in that block's setup: its
                // level, limiter, round limits and scoring.
                bool practise = _practice.Count > 0 &&
                                (practiceEachLevelType ? practised.Add(Config.levelType) : i == 0);
                yield return RunBlock(withPractice: practise);
            }

            if (!QaRobot.Running) SessionState.Advance(SessionId);

            _logger.Dispose();
            Debug.Log($"[ExperimentDirector] Session {SessionId} complete — {_blocks.Count} block(s). " +
                      $"Logs in {_logger.Directory}");

            // Only after Dispose: the CSVs have to be flushed and closed before anything reads them.
            System.Diagnostics.Process dbBuild = buildDatabaseOnFinish
                ? PostSessionHook.Launch(pythonExecutable, databaseScriptPath)
                : null;

            yield return ThankYouAndQuit(dbBuild);
        }

        /// <summary>
        /// What the participant is asked to do in the current block's level. Deliberately says nothing
        /// about the frame rates, the size of the difference, or which side is altered — those are
        /// what is being measured.
        /// </summary>
        string TaskInstruction() => Config == null ? manualInstruction : Config.levelType switch
        {
            LevelType.Scroll    => scrollInstruction,
            LevelType.Auto      => autoInstruction,
            LevelType.Shockwave => shockwaveInstruction,
            _                   => manualInstruction,
        };

        string StartPrompt() =>
            $"{TaskInstruction()}\n\nPress {startKey.ToString().ToUpperInvariant()} to start";

        /// <summary>"MAIN 2/3" — the title of a main block's prompt, and its debug-readout label.</summary>
        string MainLabel => BlockOrder.Length > 1
            ? $"MAIN {BlockOrdinal}/{BlockOrder.Length}"
            : "MAIN";

        const string PracticeLabel = "PRACTICE";

        IEnumerator RunBlock(bool withPractice)
        {
            _stats     = new SessionStats();
            _endReason = null;

            // ── Setup ────────────────────────────────────────────────────────
            if (perturbation != null) perturbation.BeginBlock(Config);
            if (scoreManager != null) scoreManager.ResetScore();
            if (laser != null)
            {
                laser.SetCooldown(Config.fireCooldown);
                laser.SetFiringEnabled(false);
            }
            if (gameManager != null)
            {
                gameManager.hitDisplayDuration = Config.revealHoldSec;
                gameManager.readyBeatDuration  = Config.readyBeatSec;
                // The block's level: the scroll on or off, the autopilot or the pointer flying the ship. First,
                // so the boundary below is dealt in the view the block plays in.
                gameManager.ApplyLevel(Config);
            }
            MeasureBlockScreen();

            // Scrolling the world, the phase's start prompt deals it instead, about the ship (GameManager.
            // HoldForPhaseStart) — in this same frame, and two deals in one frame would cut the row on screen off
            // rather than let it fade.
            if (towerManager != null && (!Config.Scrolls || gameManager == null))
            {
                towerManager.MoveTowerToRandomPosition();
                towerManager.ApplyVisibility();
            }

            // ── Practice ─────────────────────────────────────────────────────
            // The session's one practice phase: fixed frame-rate pairs from PracticeRounds.csv,
            // ahead of the first main block. Logged in full under blockIndex 0, tagged
            // phase=practice, and PerturbationController never hands its outcomes to QUEST+.
            int practiceRounds = withPractice ? _practice.Count : 0;
            if (practiceRounds > 0)
            {
                _logger.BeginPractice(Config, SquareRow, LatinSquare.OrderText(BlockOrder));
                _logger.CurrentPhase = "practice";
                if (perturbation != null)
                {
                    perturbation.DebugLabel = PracticeLabel;
                    perturbation.SetPractice(true, _practice[0]);
                }

                yield return WaitForStartKey(PracticeLabel, StartPrompt());

                // The phase's clock and frame log start with its first GO!, as every later round's GO! is
                // logged: the split is open through it, so its frames belong to the round's measured rates.
                BeginPhase(isPractice: true);
                yield return PhaseStartingGun();
                ArmPhase();

                // One pair per round, walked in order. The frame rates are never shown before the
                // round — only in the reveal after it.
                for (int i = 0; i < practiceRounds; i++)
                {
                    perturbation?.SetPractice(true, _practice[i]);
                    overlay.ShowBanner($"PRACTICE — round {i + 1} of {practiceRounds}");

                    // The loop comes back round the same frame this round's response lands, so the
                    // next step is in place long before the reveal ends and its GO! opens the split.
                    int target = i + 1;
                    while (_roundNumber < target) yield return null;
                }

                yield return EndPhase(isPractice: true);
                overlay.HideBanner();
                overlay.HideHint();

                // Between phases, disk I/O cannot land on anything being measured.
                _logger.FlushBuffers();

                perturbation?.SetPractice(false);
                if (scoreManager != null) scoreManager.ResetScore();
                RefreshProgressBar();
            }

            // ── Main run ─────────────────────────────────────────────────────
            // The practice rows are on disk by now (flushed above), so the logger can take this
            // block's identity for everything that follows.
            _logger.BeginBlock(Config, BlockOrdinal, SquareRow, LatinSquare.OrderText(BlockOrder));
            _logger.CurrentPhase = "main";
            _stats = new SessionStats();   // practice performance is not part of the result
            if (perturbation != null) perturbation.DebugLabel = MainLabel;

            yield return WaitForStartKey(MainLabel, StartPrompt());

            _startedAt = DateTime.Now;
            BeginPhase(isPractice: false);
            _blockSummaryPending = true;
            yield return PhaseStartingGun();
            ArmPhase();

            // The staircase can only finish on a response, so RecordShot evaluates IsFinished once
            // per round and parks the reason here.
            while (_endReason == null) yield return null;

            // ── Wind down ────────────────────────────────────────────────────
            if (perturbation != null) perturbation.EndBlock();
            yield return EndPhase(isPractice: false);

            _blockSummaryPending = false;
            _logger.WriteSessionLog(_startedAt, DateTime.Now, _stats, _endReason, perturbation,
                                    _blockViewWidth, _blockGuessRate);
            _logger.FlushBuffers();

            LogBlockSummary();
        }

        // The block's screen, for its session row: the view's width at the play plane, and the chance rate the
        // screen implies for the block's level. After ApplyLevel, so a scroll block's camera is already back
        // over its origin or on its way to the ship — the width is the same either way.
        void MeasureBlockScreen()
        {
            _blockGuessRate = ScreenGuessRate(Config, out _, out _);
            CameraRig rig = CameraRig.Instance;
            float z = ufo != null ? ufo.fixedZ : 0f;
            _blockViewWidth = rig != null
                ? Mathf.Abs(rig.RestViewportPointAtZ(1f, 0.5f, z).x - rig.RestViewportPointAtZ(0f, 0.5f, z).x)
                : float.NaN;
        }

        /// <summary>
        /// One line per block with the threshold, and a warning for anything that makes it suspect.
        /// The stimulus IS the frame rate, so a machine that could not hold the caps presented a
        /// smaller difference than QUEST+ was told — the threshold it produced is not the
        /// participant's.
        /// </summary>
        void LogBlockSummary()
        {
            QuestPlusStaircase qp = perturbation != null ? perturbation.ActiveStaircase : null;
            float jnd = qp != null ? qp.JndEstimate() : float.NaN;
            float sd  = qp != null ? qp.PosteriorThresholdSD() : float.NaN;
            float thresholdFps = Config.AlteredFpsAt(jnd);

            Debug.Log($"[ExperimentDirector] {MainLabel} (row {Config.blockIndex}, " +
                      $"{Config.Describe()}) complete ({_endReason}) — JND Δ {jnd:0.00} ms (SD {sd:0.00}), " +
                      $"i.e. {Config.baseFps} vs {thresholdFps:0.0} fps. {_stats.Hits}/{_stats.Rounds} hit, " +
                      $"{_stats.RoundsExpired + _stats.RoundsFlipCapped} ran out, {_stats.AvgCrossingsPerRound:0.0} crossings/round. " +
                      $"Delivered: base {_stats.BaseSideMeasuredFps:0.0} fps (cap {Config.baseFps}), " +
                      $"altered {_stats.AlteredFrameTimeErrPct:+0.0;-0.0}% frame time vs cap, " +
                      $"{_stats.SlowFrames} slow frame(s).");

            string edge = qp != null ? qp.RangeEdge() : "";
            if (edge == "top")
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: the threshold estimate is pressed " +
                                  $"against the LARGEST difference tested ({Config.maxDiffMs:0.##} ms). The participant " +
                                  "rarely located the boundary even there, so the true threshold is probably above " +
                                  "the range and the stop rule 'converged' on the edge. Treat this block's " +
                                  "threshold as a lower bound (jndRangeEdge = top in the session log).");
            else if (edge == "bottom")
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: the threshold estimate is pressed " +
                                  $"against the SMALLEST difference tested ({Config.minDiffMs:0.##} ms), so the true " +
                                  "threshold may be below the range (jndRangeEdge = bottom in the session log).");

            if (_stats.BaseFrames > 0 && _stats.BaseSideMeasuredFps < 0.97f * Config.baseFps)
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: the base side ran at " +
                                  $"{_stats.BaseSideMeasuredFps:0.0} fps against a {Config.baseFps} fps cap — " +
                                  "this machine did not hold the base frame rate, so the differences QUEST+ " +
                                  "presented were not the ones shown. Run a build, or lower baseFps.");

            if (_stats.AlteredFrames > 0 && _stats.AlteredFrameTimeErrPct > 3f)
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: altered-side frames ran " +
                                  $"{_stats.AlteredFrameTimeErrPct:0.0}% longer than their cap on average — the " +
                                  "altered frame rate was not fully delivered.");

            if (_stats.LiveFrames > 0 && _stats.SlowFrames > 0.01f * _stats.LiveFrames)
                Debug.LogWarning($"[ExperimentDirector] {MainLabel}: {_stats.SlowFrames} of " +
                                  $"{_stats.LiveFrames} in-round frames took over 1.5× their cap's period " +
                                  $"(longest {_stats.MaxLiveFrameTimeMs:0.0} ms). Hitches are a frame-rate " +
                                  "change of their own.");
        }

        /// <summary>Puts a prompt up and blocks until the start key is pressed.</summary>
        IEnumerator WaitForStartKey(string title, string body)
        {
            if (laser != null) laser.SetFiringEnabled(false);
            // Released at the end of PhaseStartingGun, which always follows.
            if (gameManager != null) gameManager.HoldForPhaseStart();
            overlay.Show(title, body);

            while (Keyboard.current == null || !Keyboard.current[startKey].wasPressedThisFrame)
                yield return null;

            overlay.Hide();

            // Space is also LaserFirer's alt-fire key, so give the press a frame to clear before
            // firing is enabled — otherwise starting would spend the first shot.
            yield return null;
        }

        /// <summary>
        /// GO! before a phase's first round, so it starts the way every later round does — and opens
        /// that round's frame-rate split, exactly as GameManager's GO! does between rounds. Runs
        /// after BeginPhase and before ArmPhase, so the beat is on the phase clock and in its frame
        /// log, like every later GO!, and firing is handed back only once it is over.
        /// </summary>
        IEnumerator PhaseStartingGun()
        {
            // An auto level's ship is in plain view here (Space was only just pressed), so it starts from where it
            // is rather than jumping: unlike at a later round's GO!, nothing is hiding it.
            UfoAutopilot pilot = gameManager != null ? gameManager.Autopilot : null;
            if (pilot != null && pilot.Engaged) pilot.BeginFromHere();

            // The ship holds still through GO!, as it does at every later round's GO! (GameManager.StartingGun):
            // the split is already open, so a crossing here would count in the round before firing is handed
            // back and before the phase's frame log starts. A scroll level holds it already (HoldForPhaseStart).
            bool hold = ufo != null && !ufo.FreezeMovement;
            if (hold) ufo.FreezeMovement = true;

            if (perturbation != null)
            {
                perturbation.ResetSideTracking();
                perturbation.BeginRound();
            }
            if (overlay != null) yield return overlay.GoBeat(Config.readyBeatSec);

            if (hold && ufo != null) ufo.FreezeMovement = false;
            if (gameManager != null) gameManager.ReleasePhaseStart();
        }

        // Per-phase frame index and clock, both restarting at zero — the phase column tells the two
        // apart. Main round numbers continue across blocks; practice restarts at 1.
        void BeginPhase(bool isPractice)
        {
            _frameIndex         = 0;
            _roundNumber        = isPractice ? 0 : _mainRoundsCompleted;
            _frameRound         = _roundNumber + 1;
            _shotFiredThisFrame = false;   // nothing from a previous phase may stamp frame 0
            _hasPrevUfoPos      = false;
            _startRealtime      = Time.unscaledTime;
            _startRealtimeD     = Time.unscaledTimeAsDouble;
            _trailCount         = 0;       // a velocity never spans two phases
            _shotsThisRound = _nEarly = _nLate = _nLockout = _nCooldown = _nNotLive = _blockRounds = 0;
            _roundArmed         = false;
            ResetRoundMotion();

            // Within a frame the two clocks can only differ by the time elapsed since it started, so
            // anything more is a genuine origin mismatch.
            float rss  = Time.realtimeSinceStartup;
            float skew = rss - _startRealtime;
            if (skew >= 0f && skew < 0.25f)
            {
                _phaseClockOrigin = _startRealtime;
            }
            else
            {
                _phaseClockOrigin = rss;
                Debug.LogWarning($"[ExperimentDirector] realtimeSinceStartup and unscaledTime differ by " +
                                 $"{skew:0.###}s — round timings (roundStartSec/firedAtSec/lastCrossingAtSec) " +
                                 "are rebased on their own stamp and may sit up to one frame off the " +
                                 "frame log's timeSinceStartSec.");
            }

            if (_logger != null) _logger.PhaseStartedAt = DateTime.Now;

            // Live for logging from here, but firing stays off until the phase's GO! is over (ArmPhase).
            PhaseOpen = true;
            if (laser != null) laser.SetFiringEnabled(false);

            RefreshProgressBar();
        }

        // Hands firing back once the phase's first GO! is over: its first round arms.
        void ArmPhase()
        {
            if (laser != null) laser.SetFiringEnabled(true);
        }

        void RefreshProgressBar()
        {
            if (gameManager != null && gameManager.uiManager != null)
                gameManager.uiManager.RefreshProgress();
        }

        // Stops the phase and lets any shot fired on the buzzer finish its reveal.
        IEnumerator EndPhase(bool isPractice)
        {
            // Firing goes first, and the phase stays open for the rest of this frame: the response
            // that ended the phase was recorded from this frame's Update, and its frame row is only
            // written in this frame's LateUpdate.
            if (laser != null) laser.SetFiringEnabled(false);
            yield return null;

            PhaseOpen = false;
            if (perturbation != null) perturbation.CancelRound();

            if (!isPractice) _mainRoundsCompleted = _roundNumber;

            float waitStart = Time.unscaledTime;
            while (gameManager != null && gameManager.IsRevealing &&
                   Time.unscaledTime - waitStart < maxRevealWaitSec)
                yield return null;
        }

        /// <summary>Writes everything buffered so far to disk. Called by GameManager from under the
        /// between-round veil, where a write's long frame cannot be mistaken for the stimulus.</summary>
        public void FlushLogs() => _logger?.FlushBuffers();

        // Which of QUEST+'s two stop rules fired. A run that hit maxTrials never reached its
        // precision target, so its estimate is the weaker one.
        static string StopRuleFor(QuestPlusStaircase sc) =>
            sc.TrialCount >= sc.Config.maxTrials ? "maxTrials" : "converged";

        /// <summary>Closing screen: a per-second countdown, then quit. The logs are already written,
        /// so quitting here — or the participant force-quitting — cannot lose data.</summary>
        IEnumerator ThankYouAndQuit(System.Diagnostics.Process dbBuild = null)
        {
            int shownSecond = -1;
            for (float remaining = thankYouCountdownSec; remaining > 0f; remaining -= Time.unscaledDeltaTime)
            {
                int second = Mathf.CeilToInt(remaining);
                if (second != shownSecond)
                {
                    shownSecond = second;
                    overlay.Show("Thank you for participating",
                                  quitOnSessionComplete ? $"Closing in {second}…" : $"{second}…");
                }
                yield return null;
            }

            if (!quitOnSessionComplete)
            {
                overlay.Show("Thank you for participating", "That's the end of the study.");
                yield break;
            }

            // A bounded wait for the database build — never unbounded: the CSVs are the data and the
            // database can be rebuilt by hand.
            if (!PostSessionHook.HasFinished(dbBuild))
            {
                overlay.Show("Thank you for participating", "Saving…");
                float deadline = Time.unscaledTime + databaseWaitSec;
                while (!PostSessionHook.HasFinished(dbBuild) && Time.unscaledTime < deadline)
                    yield return null;

                if (PostSessionHook.HasFinished(dbBuild))
                    Debug.Log($"[PostSessionHook] Database build finished (exit {PostSessionHook.ExitCodeText(dbBuild)}).");
                else
                    Debug.LogWarning("[PostSessionHook] Database build did not finish within " +
                                     $"{databaseWaitSec:0.#}s; it keeps running after quit, or " +
                                     "re-run Analysis/build_db.py by hand.");
            }

            overlay.Show("Thank you for participating", "Closing…");
            Debug.Log("[ExperimentDirector] Quitting.");
            yield return null;   // let the final frame render before the app goes away

            Quit();
        }

        static void Quit()
        {
#if UNITY_EDITOR
            // Application.Quit is a no-op in the Editor, so stop Play mode instead.
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ── Per-frame sampling ───────────────────────────────────────────────

        void LateUpdate()
        {
            if (!PhaseOpen || _logger == null || _stats == null) return;

            var t = new TickSample
            {
                roundNumber       = _frameRound,
                frameIndex        = _frameIndex++,
                timeSinceStartSec = (float)(Time.unscaledTimeAsDouble - _startRealtimeD),
                unscaledDeltaMs   = Time.unscaledDeltaTime * 1000f,
                alteredSide       = "",
                side              = "",
                diffMs            = float.NaN,
            };

            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                t.mousePos             = mouse.position.ReadValue();
                t.mouseDelta           = mouse.delta.ReadValue();
                t.leftDown             = mouse.leftButton.isPressed;
                t.leftPressedThisFrame = mouse.leftButton.wasPressedThisFrame;
            }

            Keyboard kb = Keyboard.current;
            if (kb != null) t.fireKeyDown = kb[_fireKey].isPressed;

            if (ufo != null)
            {
                Vector3 p = ufo.transform.position;
                t.ufoX = p.x;
                t.ufoY = p.y;
                if (_hasPrevUfoPos) _stats.AddUfoDisplacement(Vector3.Distance(p, _prevUfoPos));
                _prevUfoPos    = p;
                _hasPrevUfoPos = true;
                PushTrail(t.timeSinceStartSec, p.x);
            }

            CameraRig rig = CameraRig.Instance;
            t.camX = rig != null ? rig.RestX : float.NaN;

            // A round arms when firing is handed back after its GO!: where the UFO starts it from, and its
            // crossings from then on. Preserved across a shockwave round's TRY AGAIN!.
            bool retrying = ShockwaveController.Instance != null && ShockwaveController.Instance.Retrying;
            bool armed    = (laser != null && laser.FiringEnabled) || (_roundArmed && retrying);
            if (armed && !_roundArmed)
            {
                ResetRoundMotion();
                _roundUfoXAtStart = t.ufoX;
            }
            _roundArmed = armed;
            t.armed     = laser != null && laser.FiringEnabled;

            // The endless world repeats the boundary once a period, the frame rates swapping sides at every copy.
            // Each row is written as if the copy nearest the UFO were the only boundary: towerX is that copy, and
            // where the rates run the other way round across it, side and alteredSide are turned too — so ufoX -
            // towerX is the distance to a real boundary and side == alteredSide still says the UFO was on the
            // altered side, exactly as without the scroll. Without it, the boundary itself, nothing turned.
            bool turned = false;
            if (towerManager != null) t.towerX = towerManager.BoundaryXNear(t.ufoX, out turned);

            if (perturbation != null)
            {
                t.baseFps   = perturbation.CurrentBaseFps;
                t.targetFps = perturbation.PacedFps;
                t.pacedBy   = perturbation.PacedBy;
                t.side      = SideCell(Turned(perturbation.CurrentSide, turned));
                t.crossed   = perturbation.CrossedThisFrame;
                if (perturbation.SplitActive)
                {
                    t.diffFps     = perturbation.CurrentRound.diffFps;
                    t.diffMs      = perturbation.CurrentRound.diffMs;
                    t.alteredSide = SideCell(Turned(perturbation.CurrentRound.alteredSide, turned));
                }
            }
            ShockwaveController shockwave = ShockwaveController.Instance;
            if (shockwave != null && shockwave.Active)
            {
                t.shockwaveFastPhase = shockwave.FastPhase;
                t.shockwaveWindow    = shockwave.WindowOpen;
                t.shockwaveFlips     = shockwave.FlipCount;
            }

            // How fast the boundary was crossed: the UFO's speed over the last ~0.1 s, this frame included.
            if (t.crossed)
            {
                float speed = Mathf.Abs(TrailVelocity());
                if (!float.IsNaN(speed))
                {
                    _roundCrossSpeedSum += speed;
                    _roundCrossSpeedN++;
                    _roundLastCrossSpeed = speed;
                    _stats.AddCrossingSpeed(speed);
                }
            }

            t.shotFired = _shotFiredThisFrame;
            t.shotHitX  = _shotFiredThisFrame ? _shotHitXThisFrame : 0f;
            _shotFiredThisFrame = false;

            _stats.AddTick(t);
            _logger.QueueTick(t);

            // Only now, with this frame's row out, may the frame label follow a round that closed
            // during the frame — see _frameRound.
            _frameRound = _roundNumber + 1;

            DrivePracticeHint(t.crossed);
        }

        void PushTrail(float t, float x)
        {
            _trailHead = (_trailHead + 1) % TrailLength;
            _trailT[_trailHead] = t;
            _trailX[_trailHead] = x;
            if (_trailCount < TrailLength) _trailCount++;
        }

        /// <summary>The UFO's velocity along X (world units a second, + = right) over the last
        /// <see cref="VelocityWindowSec"/> of logged frames, or as far back as the trail goes; NaN with fewer
        /// than two frames.</summary>
        float TrailVelocity()
        {
            if (_trailCount < 2) return float.NaN;
            float t1 = _trailT[_trailHead], x1 = _trailX[_trailHead];
            int   i  = _trailHead;
            float t0 = t1, x0 = x1;
            for (int n = 1; n < _trailCount; n++)
            {
                i  = (i - 1 + TrailLength) % TrailLength;
                t0 = _trailT[i];
                x0 = _trailX[i];
                if (t1 - t0 >= VelocityWindowSec) break;
            }
            float dt = t1 - t0;
            return dt > 1e-5f ? (x1 - x0) / dt : float.NaN;
        }

        void ResetRoundMotion()
        {
            _roundUfoXAtStart    = float.NaN;
            _roundCrossSpeedSum  = 0f;
            _roundCrossSpeedN    = 0;
            _roundLastCrossSpeed = float.NaN;
        }

        /// <summary>
        /// The practice prompt: a standing "what to look for" line while the round is live, slammed
        /// into the alert state the instant the UFO crosses the boundary (or the frame rate flips
        /// on a shockwave block). Gated on PracticeMode — the same flag that keeps responses away
        /// from the posterior — because in a main round the alert would hand the participant the
        /// stimulus they are being tested on.
        /// </summary>
        void DrivePracticeHint(bool crossed)
        {
            if (overlay == null) return;

            if (!showPracticeHints || perturbation == null || !perturbation.PracticeMode)
            {
                overlay.HideHint();
                return;
            }

            if (Config != null && Config.IsShockwave)
            {
                ShockwaveController sw = ShockwaveController.Instance;
                if (sw != null && sw.FlippedThisFrame)
                {
                    overlay.FlashHint(shockwaveFlipHintText, Mathf.Max(0.4f, Config.swWindowSec));
                    return;
                }
                if (sw != null && !sw.WindowOpen)
                {
                    overlay.ReleaseAlertHint();
                }
                if (laser != null && !laser.FiringEnabled) { overlay.HideIdleHint(); return; }
                overlay.ShowIdleHint(shockwaveStandingHintText);
                return;
            }

            if (crossed)
            {
                overlay.FlashHint(crossingHintText, practiceHintSec);
                return;
            }

            // Withdraw the standing prompt whenever the participant cannot act — the reveal, the
            // reset, the pan. An alert in flight is left to finish.
            if (laser != null && !laser.FiringEnabled) { overlay.HideIdleHint(); return; }

            overlay.ShowIdleHint(standingHintText);
        }

        // ── Round intake (called by GameManager) ─────────────────────────────

        /// <summary>
        /// A press of the fire button that fired nothing: inside the cooldown, the early lockout, or with firing off. Its
        /// row says so (outcome cooldown / lockout / notLive) and carries the state of the game as for any other press.
        /// </summary>
        void RecordIgnoredPress(ResponseOutcome kind)
        {
            if (!PhaseOpen || _logger == null || _stats == null || perturbation == null) return;

            PerturbationController.RoundRecord round =
                perturbation.SplitActive ? perturbation.SnapshotRound() : default;
            RoundTiming timing = RoundClock.Instance != null ? RoundClock.Instance.Snapshot(false) : default;
            timing.firedAt = Time.realtimeSinceStartup;
            Vector3 ufoPos = ufo != null ? ufo.transform.position : Vector3.zero;
            Vector3 tower  = towerManager != null ? towerManager.MainTowerPosition : Vector3.zero;
            float   total  = scoreManager != null ? scoreManager.TotalScore : 0f;
            RecordShot(ufoPos, tower, false, total, kind, round, timing, counted: false, roundEnded: false);
        }

        /// <summary>
        /// Records one press, or a round running out: its row in the shot log, with the state of the game at that instant.
        /// A shot is recorded once the split has been closed (or snapshotted, for a press that leaves the round going), the
        /// shot scored, and QUEST+ told the outcome, so the posterior columns include this response.
        /// </summary>
        /// <param name="counted">Whether this response reached QUEST+.</param>
        /// <param name="roundEnded">Whether this row closes the round.</param>
        public void RecordShot(Vector3 hitPoint, Vector3 towerBase, bool isHit, float totalScore,
                                ResponseOutcome outcome, in PerturbationController.RoundRecord round,
                                in RoundTiming timing, bool counted = true, bool roundEnded = true)
        {
            if (!PhaseOpen || _logger == null || _stats == null) return;

            bool  playerFired = outcome.IsPlayerShot();
            bool  ignored     = outcome == ResponseOutcome.PressCooldown || outcome == ResponseOutcome.PressLockout ||
                                outcome == ResponseOutcome.PressNotLive;
            bool  pressed     = playerFired || ignored;
            bool  isShockwave = Config != null && Config.IsShockwave;
            float now         = (float)(Time.unscaledTimeAsDouble - _startRealtimeD);
            float firedAt     = ToPhaseClock(timing.firedAt);
            float lastCross   = round.valid ? ToPhaseClock(round.lastCrossingRealtime)  : float.NaN;
            float firstCross  = round.valid ? ToPhaseClock(round.firstCrossingRealtime) : float.NaN;
            float sinceCross  = pressed && !float.IsNaN(firedAt) && !float.IsNaN(lastCross)
                ? firedAt - lastCross : float.NaN;

            if      (outcome == ResponseOutcome.Early)         _nEarly++;
            else if (outcome == ResponseOutcome.Late)          _nLate++;
            else if (outcome == ResponseOutcome.PressLockout)  _nLockout++;
            else if (outcome == ResponseOutcome.PressCooldown) _nCooldown++;
            else if (outcome == ResponseOutcome.PressNotLive)  _nNotLive++;

            Vector3 ufoPos = ufo != null ? ufo.transform.position : hitPoint;
            QuestPlusStaircase sc = perturbation != null ? perturbation.ActiveStaircase : null;

            // Practice never reaches QUEST+, so a practice row has no posterior to report — its staircase
            // columns are left blank rather than repeating the main block's untouched prior.
            bool practiceRow = round.practice || _logger.CurrentPhase == "practice";

            // Where on screen, on the view at rest: in a scroll level the camera follows the ship, so world X
            // alone does not say where on the screen the shot was.
            CameraRig rig = CameraRig.Instance;
            float camX = float.NaN, ufoVx = float.NaN, towerVx = float.NaN;
            if (rig != null)
            {
                float z = ufo != null ? ufo.fixedZ : 0f;
                float l = rig.RestViewportPointAtZ(0f, 0.5f, z).x;
                float w = rig.RestViewportPointAtZ(1f, 0.5f, z).x - l;
                camX = rig.RestX;
                if (Mathf.Abs(w) > 1e-4f)
                {
                    ufoVx   = ((playerFired ? hitPoint.x : ufoPos.x) - l) / w;
                    towerVx = (towerBase.x - l) / w;
                }
            }

            // Picked up by this frame's LateUpdate row. A press that fired nothing is no response.
            if (!ignored)
            {
                _shotFiredThisFrame = true;
                _shotHitXThisFrame  = hitPoint.x;
            }

            bool  changed = round.valid && round.changeToFps > 0 && round.changeFromFps > 0;
            float fromMs  = changed ? 1000f / round.changeFromFps : float.NaN;
            float toMs    = changed ? 1000f / round.changeToFps   : float.NaN;
            int   capFps  = perturbation != null ? perturbation.PacedFps : 0;
            ShockwaveController wave = ShockwaveController.Instance;

            var s = new ShotSample
            {
                roundNumber       = _roundNumber + 1,
                timeSinceStartSec = now,
                baseFps           = round.valid ? round.baseFps
                                  : perturbation != null ? perturbation.CurrentBaseFps : 0,

                diffFps            = round.diffFps,
                diffMs             = round.valid ? round.diffMs : float.NaN,
                alteredFps         = round.alteredFps,
                // A shockwave block alternates in time: there is no altered side, and no side of a tower to fire from.
                alteredSide        = round.valid && !isShockwave ? SideCell(round.alteredSide) : "",
                measuredBaseFps    = round.MeasuredBaseFps,
                measuredAlteredFps = round.MeasuredAlteredFps,
                baseSideSec        = round.baseSec,
                alteredSideSec     = round.alteredSec,
                lateFrames         = round.lateFrames,

                crossings            = round.crossings,
                lastCrossingAtSec    = lastCross,
                sinceLastCrossingSec = sinceCross,

                outcome            = outcome,
                playerFired        = playerFired,
                isHit              = isHit,
                countedByStaircase = counted && !practiceRow,
                totalScore         = totalScore,

                roundStartSec    = ToPhaseClock(timing.armedAt),
                firedAtSec       = firedAt,
                roundDurationSec = timing.endedAt - timing.armedAt,

                // A round that ran out has no landing point: blank rather than the UFO's resting
                // position, which would be indistinguishable from a shot that happened to land there.
                // Shockwave has no target tower, so missDistX is left NaN.
                hitX      = playerFired ? hitPoint.x : float.NaN,
                missDistX = isShockwave ? float.NaN : (playerFired ? hitPoint.x - towerBase.x : float.NaN),
                towerX    = towerBase.x,
                ufoY      = ufoPos.y,
                side      = isShockwave || perturbation == null ? "" : SideCell(perturbation.SideOf(playerFired ? hitPoint : ufoPos)),

                threshDiffMs  = perturbation != null && !practiceRow ? perturbation.ThresholdEstimate  : float.NaN,
                threshDiffFps = perturbation != null && !practiceRow ? Config.DiffFpsAt(perturbation.ThresholdEstimate) : float.NaN,
                sd            = perturbation != null && !practiceRow ? perturbation.PosteriorSD        : float.NaN,
                slopeEstimate = perturbation != null && !practiceRow ? perturbation.SlopeEstimateValue : float.NaN,
                lapseEstimate = perturbation != null && !practiceRow ? perturbation.LapseEstimateValue : float.NaN,

                camX               = camX,
                ufoViewportX       = ufoVx,
                towerViewportX     = towerVx,
                ufoVelX            = TrailVelocity(),
                ufoXAtStart        = _roundUfoXAtStart,
                firstCrossingAtSec = firstCross,
                meanCrossingSpeed  = _roundCrossSpeedN > 0 ? _roundCrossSpeedSum / _roundCrossSpeedN : float.NaN,
                lastCrossingSpeed  = _roundLastCrossSpeed,

                shotInRound    = _shotsThisRound + 1,
                roundEnded     = roundEnded,
                inputDevice    = !pressed || laser == null ? "" : laser.LastPressDevice == 1 ? "mouse" : "key",
                firingEnabled  = playerFired || (laser != null && laser.FiringEnabled),
                capFps         = capFps,
                capFrameMs     = capFps > 0 ? 1000f / capFps : float.NaN,
                pacedBy        = perturbation != null ? perturbation.PacedBy : PerturbationController.Pace.None,
                windowOpen     = outcome == ResponseOutcome.Detected || (wave != null && wave.WindowOpen),

                reactionSec           = sinceCross,
                framesSinceLastChange = pressed && round.valid && round.changeFrame >= 0 ? Time.frameCount - round.changeFrame : -1,
                changesSinceLastPress = round.changesSincePress,
                changeFromFps  = round.changeFromFps,
                changeToFps    = round.changeToFps,
                changeFromMs   = fromMs,
                changeToMs     = toMs,
                changeDeltaMs  = toMs - fromMs,
                changeDir      = !changed ? "" : toMs > fromMs ? "slower" : "faster",
                foreperiodSec  = round.valid ? round.foreperiodSec : float.NaN,
                windowSec      = isShockwave && Config != null ? Config.swWindowSec : float.NaN,
                changeTimesSec = round.valid && perturbation != null ? perturbation.ChangeTimesCell(_phaseClockOrigin) : "",
                changeUfoX     = round.valid ? round.changeUfoX : float.NaN,
                changeUfoY     = round.valid ? round.changeUfoY : float.NaN,

                earlyPresses    = _nEarly,
                latePresses     = _nLate,
                lockoutPresses  = _nLockout,
                cooldownPresses = _nCooldown,
                notLivePresses  = _nNotLive,

                ufoX              = ufoPos.x,
                baseFrameMs       = round.valid && round.baseFps > 0 ? 1000f / round.baseFps : float.NaN,
                alteredFrameMs    = round.valid && round.alteredFps > 0 ? 1000f / round.alteredFps : float.NaN,
                measuredBaseMs    = 1000f / round.MeasuredBaseFps,
                measuredAlteredMs = 1000f / round.MeasuredAlteredFps,
            };

            _stats.AddShot(s);
            _logger.QueueShot(s);

            if (playerFired)
            {
                _shotsThisRound++;
                if (perturbation != null) perturbation.NotePress();
            }
            if (ignored) return;

            LogRound(s, round, sc, isShockwave);

            if (roundEnded)
            {
                _roundNumber++;
                _shotsThisRound = _nEarly = _nLate = _nLockout = _nCooldown = _nNotLive = 0;
                _blockRounds++;
            }

            // A response is the only thing that can advance the staircase, so this is the one place
            // IsFinished needs evaluating — the run loop just waits on the result.
            if (sc != null && sc.IsFinished && _endReason == null && _logger.CurrentPhase == "main")
                _endReason = StopRuleFor(sc);

            // Shots fired before any change never reach QUEST+, so in principle a block could go on for ever: a backstop.
            if (roundEnded && _endReason == null && _logger.CurrentPhase == "main" && Config != null &&
                _blockRounds >= 3 * Config.maxTrials)
                _endReason = "roundCap";
        }

        // One console line per round — enough to follow a pilot session live without opening a log.
        void LogRound(in ShotSample s, in PerturbationController.RoundRecord round,
                      QuestPlusStaircase sc, bool isShockwave)
        {
            string what = s.outcome switch
            {
                ResponseOutcome.Shot        => s.isHit ? "HIT" : $"miss by {Mathf.Abs(s.missDistX):0.0}",
                ResponseOutcome.Detected    => "DETECTED",
                ResponseOutcome.Early       => "too early",
                ResponseOutcome.Late        => "too late",
                ResponseOutcome.FlipCap     => "chances missed",
                ResponseOutcome.Expired     => "ran out of time",
                _                        => "too many crossings",
            };
            string staircase = round.practice
                ? "practice"
                : sc != null ? $"T{sc.TrialCount} θ̂ {s.threshDiffMs:0.00} ms SD {s.sd:0.00}" : "";

            if (isShockwave)
            {
                Debug.Log($"[Round] {BlockOrdinal}/{BlockOrder.Length} {_logger.CurrentPhase} #{s.roundNumber}: " +
                          $"Δ{s.diffMs:0.00} ms — {round.baseFps} ↔ {round.alteredFps} fps " +
                          $"(measured {Fmt(round.MeasuredBaseFps)} ↔ {Fmt(round.MeasuredAlteredFps)}) → {what}, " +
                          $"{s.crossings} flip(s), {s.roundDurationSec:0.0}s. {staircase}");
            }
            else
            {
                Debug.Log($"[Round] {BlockOrdinal}/{BlockOrder.Length} {_logger.CurrentPhase} #{s.roundNumber}: " +
                          $"Δ{s.diffMs:0.00} ms — L {round.LeftFps} | R {round.RightFps} fps " +
                          $"(measured {Fmt(round.MeasuredLeftFps)} | {Fmt(round.MeasuredRightFps)}) → {what}, " +
                          $"{s.crossings} crossing(s), {s.roundDurationSec:0.0}s. {staircase}");
            }
        }

        static string Fmt(float v) => float.IsNaN(v) ? "—" : v.ToString("0.0");

        /// <summary>
        /// Rebases a <see cref="Time.realtimeSinceStartup"/> instant onto the phase clock the rest of
        /// the logs use. NaN passes straight through, which is how "this never happened" reaches the
        /// CSV as an empty cell.
        /// </summary>
        float ToPhaseClock(float realtimeInstant) =>
            float.IsNaN(realtimeInstant) ? float.NaN : realtimeInstant - _phaseClockOrigin;
    }
}
