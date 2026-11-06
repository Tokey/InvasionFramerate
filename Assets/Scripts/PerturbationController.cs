using System.Text;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Core JND logic: splits the screen's frame rate at the hidden tower (or alternates it in time
    /// on shockwave blocks).
    ///
    /// While a trial is live on a laser block, the side of the tower the UFO is on decides the
    /// frame-rate cap: one side runs at the block's base frame rate, the other at the rate Δ ms of
    /// frame time away (slower or faster by the block's <c>direction</c>), and which side gets it is
    /// drawn per round (or pinned by the block's <c>alteredSide</c>). On a shockwave block,
    /// <see cref="ShockwaveController"/> alternates the whole screen between <c>baseFps</c> and
    /// <c>alteredFps</c> at random times. QUEST+ picks Δ for every main round; practice walks a fixed
    /// ladder.
    ///
    /// The split is only in force between <see cref="BeginTrial"/> (the round's starting gun) and
    /// <see cref="EndTrial"/> (its response). Everything else — the reveal, the reset under the
    /// veil, the prompts — runs at the base frame rate, so the participant never meets the altered
    /// rate outside a round and the reveal's animation runs smoothly whatever Δ was.
    ///
    /// Timing contract. The cap chosen in frame N's LateUpdate governs the wait at the start of
    /// frame N+1 (see <see cref="FrameLimiter"/>), so frame N+1's unscaledDeltaTime is the frame that
    /// cap produced. Update attributes each frame's delta to the side whose cap governed it, which
    /// is how a trial's measured per-side frame rate is built; it runs at execution order −200 so a
    /// shot fired this frame already sees this frame's delta.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class PerturbationController : MonoBehaviour
    {
        public enum Side { Left, Right }

        /// <summary>
        /// One round's stimulus and how it was actually delivered. Filled in from the round's
        /// starting gun to its response, then latched into <see cref="LastTrial"/> for the shot log
        /// and the reveal.
        /// </summary>
        public struct TrialRecord
        {
            public bool  valid;
            public bool  practice;
            /// <summary>The stimulus, as the two caps really differ: in frame time (ms) and in frame rate.</summary>
            public float diffMs;
            public int   diffFps;
            public int   baseFps;
            public int   alteredFps;
            public Side  alteredSide;

            /// <summary>Frames, and seconds of frame time, delivered under each side's cap.</summary>
            public int   baseFrames, alteredFrames;
            public float baseSec, alteredSec;

            /// <summary>Tower crossings (or frame-rate flips on shockwave) while the split was in force.</summary>
            public int   crossings;

            /// <summary>Precise limiter: frames released late (no spare time to wait out). Always 0
            /// under Unity's limiter, which does not report it.</summary>
            public int   lateFrames;

            /// <summary>The last change of frame rate the participant met (a crossing or a flip): the caps either side
            /// of it, the frame it happened on (-1 = none yet), where the ship was, and how long the wait before it was
            /// (flips only). And how many changes have passed since the last press.</summary>
            public int   changeFromFps, changeToFps, changeFrame, changesSincePress;
            public float changeUfoX, changeUfoY, foreperiodSec;

            // Time.realtimeSinceStartup; NaN = never happened.
            public float startedRealtime;
            public float firstCrossingRealtime;
            public float lastCrossingRealtime;

            public int LeftFps  => alteredSide == Side.Left  ? alteredFps : baseFps;
            public int RightFps => alteredSide == Side.Right ? alteredFps : baseFps;

            /// <summary>Frame rate actually delivered on each side: frames over frame time. NaN when
            /// the UFO never spent a frame there.</summary>
            public float MeasuredBaseFps    => baseSec    > 0f ? baseFrames    / baseSec    : float.NaN;
            public float MeasuredAlteredFps => alteredSec > 0f ? alteredFrames / alteredSec : float.NaN;
            public float MeasuredLeftFps    => alteredSide == Side.Left  ? MeasuredAlteredFps : MeasuredBaseFps;
            public float MeasuredRightFps   => alteredSide == Side.Right ? MeasuredAlteredFps : MeasuredBaseFps;

            /// <summary>The same round seen from a copy of its boundary that the world scroll repeats with the
            /// frame rates the other way round (see TowerManager.BoundaryPeriod): its altered side swapped,
            /// everything else as it was.</summary>
            public TrialRecord Flipped()
            {
                TrialRecord r = this;
                r.alteredSide = Opposite(alteredSide);
                return r;
            }
        }

        /// <summary>The other side.</summary>
        public static Side Opposite(Side s) => s == Side.Left ? Side.Right : Side.Left;

        /// <summary>Which side's cap paced a frame. None between rounds, when the base cap applies but
        /// no split is in force.</summary>
        public enum Pace { None, Base, Altered }

        [Header("Task Parameters  (pushed from the block config)")]
        [Tooltip("Radius (world units) within which a shot counts as a hit. Applied to ScoreManager.")]
        public float closeRadius = 5.5f;
        [Tooltip("Show the hit-zone lines on the tower beam during the reveal.")]
        public bool  showHitZone = true;
        [Tooltip("Half-width dead-zone (world units) around the tower. The UFO keeps its side until " +
                 "it is this far past the boundary, so hovering on the line cannot flicker the frame " +
                 "rate between the two caps every frame.")]
        public float crossingDeadZone = 0.25f;

        [Header("References  (auto-found; drag-override if needed)")]
        public CameraRig           rig;
        public UfoController       ufoController;
        public TowerManager        towerManager;
        public ScoreManager        scoreManager;
        public UIManager           uiManager;
        public ShockwaveController shockwave;

        // ── Read-only state ──────────────────────────────────────────────────

        /// <summary>The side of the tower the UFO is on, as of the last LateUpdate.</summary>
        public Side CurrentSide { get; private set; } = Side.Left;

        /// <summary>The cap applied in the last LateUpdate (fps) — it governs the NEXT frame.</summary>
        public int  CurrentTargetFps { get; private set; }

        /// <summary>The cap that paced THIS frame — the one Time.unscaledDeltaTime measures — and
        /// which side's it was. Captured at the top of Update, before anything can change them.</summary>
        public int  PacedFps { get; private set; }
        public Pace PacedBy  { get; private set; }

        /// <summary>True while the two sides run at different frame rates — from a round's starting
        /// gun to its response.</summary>
        public bool SplitActive { get; private set; }

        /// <summary>Set on the frame the UFO crosses the tower while the split is in force.</summary>
        public bool CrossedThisFrame { get; private set; }

        /// <summary>Crossings while the split was in force, this block.</summary>
        public int  CrossingCount { get; private set; }

        /// <summary>The round in progress. Meaningful only while <see cref="SplitActive"/>.</summary>
        public TrialRecord CurrentTrial => _trial;

        /// <summary>Crossings so far in the round in progress; 0 between rounds.</summary>
        public int  TrialCrossings => SplitActive ? _trial.crossings : 0;

        /// <summary>The last round that ended, latched by <see cref="EndTrial"/>. This is what the
        /// shot log and the reveal describe.</summary>
        public TrialRecord LastTrial { get; private set; }

        /// <summary>True while warm-up rounds are being played: Δ comes from the practice ladder and
        /// responses never reach QUEST+.</summary>
        public bool PracticeMode => _practiceMode;

        public int  BaseFps => _config != null ? _config.baseFps : 0;

        /// <summary>The base frame rate in force right now: the round's while a split is open,
        /// otherwise the rate the game idles at between rounds — the practice pair's higher rate
        /// during practice, the block's base otherwise.</summary>
        public int  CurrentBaseFps => SplitActive ? _trial.baseFps : NeutralFps;

        /// <summary>Shown at the top of the debug readout — "PRACTICE" or "MAIN 2/3". Set by the
        /// director.</summary>
        public string DebugLabel { get; set; } = "";
        public FrameLimiterMode LimiterMode =>
            _config != null ? _config.frameLimiter : FrameLimiterMode.Precise;

        /// <summary>The study config currently applied, or null before the first BeginBlock.</summary>
        public StudyConfig CurrentConfig => _config;

        /// <summary>The live staircase, for the logs. Null before the first BeginBlock.</summary>
        public QuestPlusStaircase ActiveStaircase => _staircase;
        public int  StaircaseTrials => _staircase?.TrialCount ?? 0;

        /// <summary>
        /// Blocks crossing counting while the camera pans. Side is measured along the camera's right
        /// axis, so a pan sweeps the tower across the UFO's apparent position and manufactures
        /// crossings the participant never made.
        /// </summary>
        public bool SuppressCrossings { get; set; }

        /// <summary>Forgets which side the UFO was on, so the next frame establishes a fresh baseline
        /// instead of reporting a crossing. Call after moving the camera or the tower.</summary>
        public void ResetSideTracking() => _lastSideSign = 0;

        /// <summary>Which side of the tower a world position is on, ignoring the dead zone — for
        /// stamping a shot with the side it was actually fired from.</summary>
        public Side SideOf(Vector3 worldPos)
        {
            Vector3 right = rig != null ? rig.Right : Vector3.right;
            Vector3 tower = towerManager != null ? towerManager.TowerBase : Vector3.zero;
            return Vector3.Dot(worldPos - tower, right) >= 0f ? Side.Right : Side.Left;
        }

        // ── Cached QUEST+ readouts ───────────────────────────────────────────
        // Recomputed only when a response updates the posterior: each is a full pass over the
        // parameter grid, and the debug HUD reads them once a second.
        /// <summary>Posterior mean of θ (ms) as of the last response.</summary>
        public float ThresholdEstimate  { get; private set; } = float.NaN;
        /// <summary>SD of the θ marginal (ms) as of the last response.</summary>
        public float PosteriorSD        { get; private set; } = float.NaN;
        /// <summary>Posterior mean of the Weibull slope β as of the last response.</summary>
        public float SlopeEstimateValue { get; private set; } = float.NaN;
        /// <summary>Posterior mean of the lapse rate λ as of the last response.</summary>
        public float LapseEstimateValue { get; private set; } = float.NaN;

        // Kept for StaircaseConvergencePlot.
        public bool  IsQuestPlusStaircase => _staircase != null;
        public float JndEstimate          => _staircase?.JndEstimate() ?? 0f;
        public float StaircaseThresholdSD => _staircase?.PosteriorThresholdSD() ?? 0f;
        public QuestPlusConfig QuestPlusConfigRef => _staircase?.Config;

        void RefreshStaircaseEstimates()
        {
            ThresholdEstimate  = _staircase != null ? _staircase.JndEstimate()          : float.NaN;
            PosteriorSD        = _staircase != null ? _staircase.PosteriorThresholdSD() : float.NaN;
            SlopeEstimateValue = _staircase != null ? _staircase.SlopeEstimate()        : float.NaN;
            LapseEstimateValue = _staircase != null ? _staircase.LapseEstimate()        : float.NaN;
        }

        // ── Private ──────────────────────────────────────────────────────────
        StudyConfig        _config;
        QuestPlusStaircase _staircase;
        bool               _blockActive;
        bool               _practiceMode;
        PracticePair       _practicePair;
        int                _lastSideSign;
        TrialRecord        _trial;
        Pace               _appliedPace;
        float              _prevDiff = float.NaN;
        readonly StringBuilder _debug = new StringBuilder(512);

        // ── Lifecycle ────────────────────────────────────────────────────────

        void Awake()
        {
            if (rig           == null) rig           = CameraRig.Instance;
            if (ufoController == null) ufoController = FindAnyObjectByType<UfoController>();
            if (towerManager  == null) towerManager  = FindAnyObjectByType<TowerManager>();
            if (scoreManager  == null) scoreManager  = FindAnyObjectByType<ScoreManager>();
            if (uiManager     == null) uiManager     = FindAnyObjectByType<UIManager>();
            if (shockwave     == null) shockwave     = FindAnyObjectByType<ShockwaveController>();
            if (shockwave     == null) shockwave     = gameObject.AddComponent<ShockwaveController>();

            QualitySettings.vSyncCount = 0;
        }

        void Start()
        {
            if (rig == null) rig = CameraRig.Instance;

            // ExperimentDirector owns sequencing. With no director — a designer hitting Play on the
            // gameplay scene alone — run block 1 of the config as a free-play round so the split
            // still works.
            if (ExperimentDirector.Instance == null)
            {
                var blocks = StudyConfig.LoadAll();
                if (blocks.Count > 0)
                {
                    Debug.Log("[PerturbationController] No ExperimentDirector in the scene — " +
                              "free play on block 1 of ExperimentConfig.csv.");
                    BeginBlock(blocks[0]);
                    BeginTrial();
                }
            }
        }

        void OnDestroy() => FrameLimiter.Release();

        void Update()
        {
            // Cleared here, set in LateUpdate: the frame log (execution order 1000) reads it after
            // LateUpdate, so it must not survive into the next frame.
            CrossedThisFrame = false;

            // This frame's delta is the period the previous frame's cap produced. Credit it to the
            // side that cap belonged to — before any shot this frame latches the trial.
            PacedFps = CurrentTargetFps;
            PacedBy  = _appliedPace;

            if (!SplitActive) return;
            float dt = Time.unscaledDeltaTime;
            if (_appliedPace == Pace.Base)         { _trial.baseFrames++;    _trial.baseSec    += dt; }
            else if (_appliedPace == Pace.Altered) { _trial.alteredFrames++; _trial.alteredSec += dt; }

            if (_config != null && _config.IsShockwave && shockwave != null && shockwave.FlippedThisFrame)
            {
                CrossedThisFrame             = true;
                _trial.crossings             = shockwave.FlipCount;
                _trial.firstCrossingRealtime = shockwave.FirstFlipAt;
                _trial.lastCrossingRealtime  = shockwave.LastFlipAt;
                _trial.foreperiodSec         = shockwave.LastForeperiodSec;
                NoteChange(shockwave.AlteredPhase ? shockwave.BaseFps : shockwave.AlteredFps, shockwave.CurrentFps);
            }
        }

        // LateUpdate rather than Update: every Update has moved the UFO by now, and coroutines (the
        // pan) have run, so the side decided here is the side the frame about to render shows.
        void LateUpdate()
        {
            UpdateSide();
            ApplyCap();
        }

        void UpdateSide()
        {
            if (ufoController == null || towerManager == null) return;
            if (_config != null && _config.IsShockwave) return;

            // How far past the boundary the UFO is, signed by side — past the nearest of its copies, with the
            // sign turned at every other one, when the world scroll repeats it (see TowerManager.SideAlong).
            Vector3 right = rig != null ? rig.Right : Vector3.right;
            float along = towerManager.SideAlong(ufoController.transform.position, right);

            int sideSign;
            if (_lastSideSign == 0)
            {
                // No baseline yet (start, or just reset): take the side the UFO is on, dead zone or
                // not, and count nothing — a baseline is not a crossing.
                sideSign = along >= 0f ? +1 : -1;
            }
            else
            {
                float dz = Mathf.Max(0f, crossingDeadZone);
                sideSign = _lastSideSign;
                if      (along >  dz) sideSign = +1;
                else if (along < -dz) sideSign = -1;
            }

            bool crossed = _lastSideSign != 0 && sideSign != _lastSideSign;
            _lastSideSign = sideSign;
            CurrentSide   = sideSign >= 0 ? Side.Right : Side.Left;

            // A crossing manufactured by the camera moving under the UFO is not one the participant
            // made, and one outside a trial crosses no frame-rate boundary.
            if (!crossed || SuppressCrossings || !SplitActive) return;

            CrossingCount++;
            CrossedThisFrame            = true;
            _trial.crossings++;
            _trial.lastCrossingRealtime = Time.realtimeSinceStartup;
            if (_trial.crossings == 1) _trial.firstCrossingRealtime = _trial.lastCrossingRealtime;

            bool toAltered = CurrentSide == _trial.alteredSide;
            NoteChange(toAltered ? _trial.baseFps : _trial.alteredFps, toAltered ? _trial.alteredFps : _trial.baseFps);
        }

        // Every change of frame rate the participant meets, for the shot log.
        readonly float[] _changeTimes = new float[64];
        int _changeCount;

        void NoteChange(int fromFps, int toFps)
        {
            _trial.changeFromFps = fromFps;
            _trial.changeToFps   = toFps;
            _trial.changeFrame   = Time.frameCount;
            _trial.changesSincePress++;
            if (ufoController != null)
            {
                Vector3 p = ufoController.transform.position;
                _trial.changeUfoX = p.x;
                _trial.changeUfoY = p.y;
            }
            if (_changeCount < _changeTimes.Length) _changeTimes[_changeCount++] = Time.realtimeSinceStartup;
        }

        /// <summary>This round's changes of frame rate so far, "t1;t2;...", seconds after <paramref name="origin"/>. Built
        /// only for a press's log row.</summary>
        public string ChangeTimesCell(float origin)
        {
            if (_changeCount == 0) return "";
            var sb = new StringBuilder(_changeCount * 9);
            for (int i = 0; i < _changeCount; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append((_changeTimes[i] - origin).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>The round in progress as it stands, for the log row of a press that does not end it.</summary>
        public TrialRecord SnapshotTrial()
        {
            TrialRecord t = _trial;
            t.lateFrames = _config != null && _config.frameLimiter == FrameLimiterMode.Precise ? FrameLimiter.Overruns : 0;
            return t;
        }

        /// <summary>A press has been logged: the count of changes since the last press starts over.</summary>
        public void NotePress() => _trial.changesSincePress = 0;

        /// <summary>
        /// After a counted TOO LATE, on a shockwave block: QUEST+ has moved on, so the rest of the round presents its new
        /// choice, and the measured rates start afresh, so each log row describes the attempt it answers. The round starts
        /// again from the base rate, while firing is off under TRY AGAIN!.
        /// </summary>
        public void BeginNextAttempt()
        {
            if (!SplitActive || _config == null) return;
            int altered = PendingAlteredFps;
            _trial.alteredFps    = altered;
            _trial.diffFps       = Mathf.Abs(altered - _trial.baseFps);
            _trial.diffMs        = StudyConfig.FrameTimeDiffMs(_trial.baseFps, altered);
            _trial.baseFrames    = _trial.alteredFrames = 0;
            _trial.baseSec       = _trial.alteredSec    = 0f;
            _appliedPace         = Pace.None;
            if (shockwave != null) shockwave.BeginAttempt(altered);
            FrameLimiter.ResetOverruns();
            ApplyCap();
            RefreshDebugText();
        }

        void ApplyCap()
        {
            if (_config == null) return;

            int  cap  = NeutralFps;
            Pace pace = Pace.None;
            if (SplitActive)
            {
                if (_config.IsShockwave && shockwave != null && shockwave.Active)
                {
                    cap  = shockwave.CurrentFps;
                    pace = shockwave.AlteredPhase ? Pace.Altered : Pace.Base;
                }
                else
                {
                    bool onAltered = CurrentSide == _trial.alteredSide;
                    cap  = onAltered ? _trial.alteredFps : _trial.baseFps;
                    pace = onAltered ? Pace.Altered      : Pace.Base;
                }
            }

            FrameLimiter.Apply(cap, _config.frameLimiter);
            CurrentTargetFps = cap;
            _appliedPace     = pace;
        }

        // ── Block setup ──────────────────────────────────────────────────────

        /// <summary>
        /// Applies a block's config and builds a fresh QUEST+ run for it. Called by
        /// <see cref="ExperimentDirector"/> at the start of every block. Each block starts from the
        /// uniform prior its config describes: blocks are independent measurements, so carrying a
        /// posterior over would make one a continuation of the last rather than a replication.
        /// </summary>
        public void BeginBlock(StudyConfig config)
        {
            if (config == null) { Debug.LogError("[PerturbationController] BeginBlock(null)"); return; }

            _config          = config;
            closeRadius      = config.closeRadius;
            showHitZone      = config.showHitZone;
            crossingDeadZone = config.crossingDeadZone;

            CancelTrial();
            if (shockwave != null) shockwave.BeginBlock(config);
            LastTrial      = default;
            CrossingCount  = 0;
            _lastSideSign  = 0;
            _prevDiff      = float.NaN;
            _blockActive   = true;

            QuestPlusConfig q = QuestPlusConfig.ForFrameTimeDiff(config);
            _staircase = new QuestPlusStaircase(q);
            RefreshStaircaseEstimates();

            Debug.Log($"[PerturbationController] Block {config.blockIndex}: {config.Describe()}. QUEST+ " +
                      $"Δ grid {q.stimGrid[0]:0.##}–{q.stimGrid[q.stimGrid.Length - 1]:0.##} ms " +
                      $"({q.stimGrid.Length} values), θ grid {q.threshGrid.Length} × β {q.slopeGrid.Length} × " +
                      $"λ {q.lapseGrid.Length}, γ={q.guessRate:0.000}, stop at SD ≤ {q.stopPosteriorSD:0.##} ms " +
                      $"after {q.minTrials} trials or at {q.maxTrials}. Altered side: {config.alteredSide}.");

            // The round clock and the staircase have to describe the same block.
            if (RoundClock.Instance != null) RoundClock.Instance.BeginBlock(config);

            ApplyTaskParams();
            if (scoreManager != null) scoreManager.SetScoring(config.hitPoints, config.missPoints);
            if (uiManager    != null) uiManager.SetDebugVisible(config.showDebug);

            // The base rate from now on, not from the next LateUpdate — the block's first prompt
            // should already be drawn at it.
            ApplyCap();
            RefreshDebugText();
            if (uiManager != null) uiManager.RefreshProgress();   // fresh staircase, empty bar
        }

        /// <summary>Stops the block. The final estimate stays readable through
        /// <see cref="ActiveStaircase"/> until the next BeginBlock replaces it.</summary>
        public void EndBlock()
        {
            _blockActive = false;
            CancelTrial();
        }

        /// <summary>
        /// Switches practice on or off. While on, every round shows <paramref name="pair"/> — its
        /// higher rate as the base, its lower one on the altered side — instead of the block's base
        /// and QUEST+'s choice, the game idles at the pair's higher rate between rounds, and
        /// responses never reach the posterior. The staircase is left untouched, so the main run
        /// starts from its prior.
        /// </summary>
        public void SetPractice(bool on, PracticePair pair = default)
        {
            _practiceMode = on && pair.highFps > 0;
            _practicePair = pair;
            // The director moves on to the next pair the moment a round's response lands, but the round that just
            // ended keeps its own base until the next one opens: its reveal plays at the rate it was played at, and
            // the frame log labels its response frame with that round's base, as the shot log does.
            if (!_practiceRoundEnded) _practiceIdleFps = pair.highFps;
            RefreshDebugText();
        }

        // The cap between rounds, when no split is open: in practice the base of the round that just ended, until
        // the next opens (or the first pair's, before any round); otherwise the block's.
        int NeutralFps => _practiceMode ? _practiceIdleFps
                        : _config != null ? _config.baseFps : 0;

        int  _practiceIdleFps;
        bool _practiceRoundEnded;

        void ApplyTaskParams()
        {
            if (scoreManager != null) scoreManager.SetScoreRadius(closeRadius);

            if (towerManager != null)
            {
                towerManager.hitZoneRadius  = closeRadius;
                towerManager.showHitZone    = showHitZone && (_config == null || !_config.IsShockwave);
                towerManager.hitZoneOpacity = 0.6f;    // the target size has to read clearly in the reveal
                towerManager.RefreshHitZone();
            }
        }

        // ── Trials ───────────────────────────────────────────────────────────

        /// <summary>The base and altered caps the next round will present: the practice pair's during
        /// practice, otherwise the block's base and the cap of QUEST+'s current choice.</summary>
        int PendingBaseFps => _practiceMode ? _practicePair.highFps
                            : _config != null ? _config.baseFps : 0;

        int PendingAlteredFps
        {
            get
            {
                if (_practiceMode) return _practicePair.lowFps;
                if (_config == null) return 0;
                return _config.AlteredFpsFor(_staircase != null ? _staircase.CurrentValue : _config.maxDiffMs);
            }
        }

        /// <summary>
        /// Opens a round's split: draws which side is altered, fixes Δ for the round, and starts
        /// measuring what each side actually delivers. Called at the round's starting gun —
        /// GameManager's GO! between rounds, ExperimentDirector's at a phase's first round — so the
        /// frame rates are already in force as the veil lifts.
        /// </summary>
        public void BeginTrial()
        {
            if (_config == null) return;

            int baseFps    = PendingBaseFps;
            int alteredFps = PendingAlteredFps;
            Side altered = _config.alteredSide switch
            {
                AlteredSideMode.Left  => Side.Left,
                AlteredSideMode.Right => Side.Right,
                _                     => Random.value < 0.5f ? Side.Left : Side.Right,
            };

            _trial = new TrialRecord
            {
                valid                = true,
                practice             = _practiceMode,
                diffMs               = StudyConfig.FrameTimeDiffMs(baseFps, alteredFps),
                diffFps              = Mathf.Abs(alteredFps - baseFps),
                baseFps              = baseFps,
                alteredFps           = alteredFps,
                alteredSide          = altered,
                startedRealtime      = Time.realtimeSinceStartup,
                firstCrossingRealtime = float.NaN,
                lastCrossingRealtime  = float.NaN,
                changeFrame           = -1,
                changeUfoX            = float.NaN,
                changeUfoY            = float.NaN,
                foreperiodSec         = float.NaN,
            };
            _changeCount = 0;

            // The frame that is running now was paced by the base cap; crediting it to either side
            // would put a base-rate frame into the altered side's measurement.
            _appliedPace = Pace.None;
            SplitActive  = true;
            _practiceRoundEnded = false;
            if (_practiceMode) _practiceIdleFps = _trial.baseFps;
            if (_config.IsShockwave && shockwave != null) shockwave.BeginTrial(_config, baseFps, alteredFps);
            FrameLimiter.ResetOverruns();
            ApplyCap();
            RefreshDebugText();
        }

        /// <summary>
        /// Closes the round's split and latches it into <see cref="LastTrial"/>. Called the moment the
        /// response is resolved, before anything is scored or logged. Safe to call twice: a second
        /// call with no round open just returns the latched one.
        /// </summary>
        public TrialRecord EndTrial()
        {
            if (!SplitActive) return LastTrial;

            _trial.lateFrames = _config != null && _config.frameLimiter == FrameLimiterMode.Precise
                ? FrameLimiter.Overruns : 0;
            LastTrial   = _trial;
            SplitActive = false;
            if (_config != null && _config.IsShockwave && shockwave != null) shockwave.StopTrial();
            _practiceRoundEnded = _trial.practice;
            ApplyCap();
            RefreshDebugText();
            return LastTrial;
        }

        /// <summary>Drops a round's split without recording it — the phase or block ended under it.</summary>
        public void CancelTrial()
        {
            _practiceRoundEnded = false;
            if (!SplitActive) return;
            SplitActive = false;
            if (_config != null && _config.IsShockwave && shockwave != null) shockwave.StopTrial();
            ApplyCap();
        }

        /// <summary>
        /// Folds a response into QUEST+. Correct = the shot landed on the hidden tower (or fired inside
        /// the shockwave window). Call after <see cref="EndTrial"/>; the round's Δ is fixed in the
        /// latched trial, so the staircase moving on to the next Δ here cannot change what gets logged
        /// against this response.
        /// </summary>
        public void ReportResponse(bool isHit, bool counted = true)
        {
            if (!counted) return;
            // Practice responses are warm-up only — they must never inform the posterior.
            if (_practiceMode) return;
            if (_staircase == null) return;
            // A response that lands after the block closed must not move the posterior.
            if (ExperimentDirector.Instance != null && !_blockActive) return;

            _prevDiff = _staircase.CurrentValue;
            _staircase.RecordResponse(isHit);
            RefreshStaircaseEstimates();
            RefreshDebugText();
        }

        // ── Debug UI ─────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the debug HUD. Public so the round clock can refresh it once a second; otherwise
        /// it is rebuilt only when a round opens or closes or a response moves the staircase. Off
        /// for participant sessions — see UIManager.showDebug — and nothing here is built then.
        /// </summary>
        public void RefreshDebugText()
        {
            // Plain ASCII throughout: the HUD font (StarShield) has nothing beyond it, and the TMP
            // fallback has no Greek or arrows — a Δ or a θ here would draw as nothing.
            if (uiManager == null || !uiManager.DebugVisible) return;
            if (_config == null) { uiManager.SetDebugText("FPS JND - no block loaded"); return; }

            var sb = _debug;
            sb.Length = 0;

            TrialRecord t = SplitActive ? _trial : LastTrial;

            sb.Append("FPS JND | ").Append(string.IsNullOrEmpty(DebugLabel) ? "block " + _config.blockIndex : DebugLabel)
              .Append(" | ").Append(_config.LevelCell)
              .Append(" | base ").Append(_config.baseFps).Append(' ').Append(_config.DirectionCell)
              .Append(" | diff ").Append(_config.minDiffMs.ToString("0.##")).Append('-')
              .Append(_config.maxDiffMs.ToString("0.##"))
              .Append(" ms step ").Append(_config.diffStepMs.ToString("0.##"))
              .Append(" | ").Append(_config.LimiterCell).Append(" limiter\n");

            if (t.valid)
            {
                if (_config.IsShockwave)
                {
                    sb.Append(SplitActive ? "LIVE  " : "last  ")
                      .Append("diff ").Append(t.diffMs.ToString("0.00")).Append(" ms: ")
                      .Append(t.baseFps).Append(" <-> ").Append(t.alteredFps)
                      .Append(" fps   flips ").Append(t.crossings).Append('\n');
                    sb.Append("measured  base ").Append(Fmt(t.MeasuredBaseFps))
                      .Append(" | altered ").Append(Fmt(t.MeasuredAlteredFps));
                }
                else
                {
                    sb.Append(SplitActive ? "LIVE  " : "last  ")
                      .Append("diff ").Append(t.diffMs.ToString("0.00")).Append(" ms: ")
                      .Append(t.baseFps).Append(" vs ")
                      .Append(t.alteredFps).Append(" fps, altered on ")
                      .Append(t.alteredSide == Side.Left ? "LEFT" : "RIGHT")
                      .Append("   (L ").Append(t.LeftFps).Append(" | R ").Append(t.RightFps).Append(")\n");
                    sb.Append("measured  L ").Append(Fmt(t.MeasuredLeftFps))
                      .Append(" | R ").Append(Fmt(t.MeasuredRightFps))
                      .Append("   crossings ").Append(t.crossings);
                }
                if (_config.frameLimiter == FrameLimiterMode.Precise)
                    sb.Append("   late ").Append(SplitActive ? FrameLimiter.Overruns : t.lateFrames);
                sb.Append('\n');
            }
            else
            {
                sb.Append("next diff ")
                  .Append(StudyConfig.FrameTimeDiffMs(PendingBaseFps, PendingAlteredFps).ToString("0.00"))
                  .Append(" ms: ").Append(PendingBaseFps).Append(" vs ").Append(PendingAlteredFps)
                  .Append(" fps\n");
            }

            if (_config.IsShockwave)
            {
                bool win = shockwave != null && shockwave.WindowOpen;
                sb.Append("now ").Append(CurrentTargetFps).Append(" fps cap")
                  .Append(win ? " [WINDOW OPEN]" : "");
            }
            else
            {
                sb.Append("now ").Append(CurrentSide == Side.Left ? "LEFT" : "RIGHT")
                  .Append(" @ ").Append(CurrentTargetFps).Append(" fps cap");
            }
            if (_config.frameLimiter == FrameLimiterMode.Precise)
                sb.Append(" | spin ").Append(FrameLimiter.LastSpinMs.ToString("0.0")).Append(" ms");
            sb.Append('\n');

            if (_practiceMode)
            {
                sb.Append("PRACTICE ").Append(_practicePair.lowFps).Append(" vs ")
                  .Append(_practicePair.highFps).Append(" fps - responses not counted");
            }
            else if (_staircase != null)
            {
                sb.Append("QUEST+ trial ").Append(_staircase.TrialCount).Append('/').Append(_staircase.Config.maxTrials)
                  .Append("  threshold ").Append(FmtMs(ThresholdEstimate))
                  .Append(" ms  SD ").Append(FmtMs(PosteriorSD))
                  .Append("  slope ").Append(SlopeEstimateValue.ToString("0.00"))
                  .Append("  lapse ").Append(LapseEstimateValue.ToString("0.000"));
                if (!float.IsNaN(_prevDiff)) sb.Append("  prev diff ").Append(_prevDiff.ToString("0.00"));
            }

            RoundClock clock = RoundClock.Instance;
            if (clock != null && clock.TrialRunning)
            {
                sb.Append("\nround ").Append(clock.RoundElapsedSec.ToString("0"));
                if (_config.roundTimeoutSec > 0f) sb.Append('/').Append(_config.roundTimeoutSec.ToString("0"));
                sb.Append(" s");
                if (!_config.IsShockwave && _config.maxCrossingsPerRound > 0)
                    sb.Append("   crossings ").Append(TrialCrossings).Append('/').Append(_config.maxCrossingsPerRound);
            }

            uiManager.SetDebugText(sb.ToString());
        }

        static string Fmt(float v) => float.IsNaN(v) ? "-" : v.ToString("0.0");
        static string FmtMs(float v) => float.IsNaN(v) ? "-" : v.ToString("0.00");
    }
}
