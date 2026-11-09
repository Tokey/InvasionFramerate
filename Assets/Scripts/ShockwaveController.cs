using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Runs the shockwave task (<see cref="LevelType.Shockwave"/>): the UFO flies automatically
    /// while the whole screen's frame rate alternates between the round's two frame rates
    /// (<c>baseFps</c> ↔ <c>alteredFps</c>) at random intervals of <c>swFlipDelayMinSec</c> to
    /// <c>swFlipDelayMaxSec</c>.
    ///
    /// <b>No visual or audio cue ever fires when the frame rate flips.</b> The frame-rate change
    /// on the moving UFO and scenery IS the stimulus; the shockwave cannon animation plays only
    /// when the participant fires.
    /// </summary>
    [DefaultExecutionOrder(-250)]   // before PerturbationController (-200), RoundClock (-100), LaserFirer (0)
    public sealed class ShockwaveController : MonoBehaviour
    {
        public static ShockwaveController Instance { get; private set; }

        [Header("References (auto-found)")]
        public PerturbationController perturbation;
        public LaserFirer             laser;

        StudyConfig _config;
        bool        _active;
        bool        _armed;
        bool        _retrying;

        int   _baseFps    = 60;
        int   _alteredFps = 60;
        bool  _alteredPhase;

        float _nextFlipAt      = float.PositiveInfinity;
        float _windowClosesAt;
        float _earlyLockoutUntil;
        float _waitStartedAt;

        int   _flipsThisRound;
        int   _flipsThisAttempt;
        int   _earliesThisRound;

        public bool  Active           => _active;
        public bool  Retrying         => _retrying;
        public bool  AlteredPhase     => _alteredPhase;
        public bool  FastPhase        => CurrentFps >= (_alteredPhase ? _baseFps : _alteredFps);
        public bool  WindowOpen       => _active && _windowClosesAt > 0f && Time.realtimeSinceStartup < _windowClosesAt;
        public bool  FlippedThisFrame { get; private set; }
        public int   CurrentFps       => _alteredPhase ? _alteredFps : _baseFps;
        public int   BaseFps          => _baseFps;
        public int   AlteredFps       => _alteredFps;
        public int   FlipCount        => _flipsThisRound;
        /// <summary>Seconds of the wait that ended in the last flip: from the gun, the last closed window or the last
        /// forgiven early press, to the flip.</summary>
        public float LastForeperiodSec { get; private set; } = float.NaN;
        public float FirstFlipAt      { get; private set; } = float.NaN;
        public float LastFlipAt       { get; private set; } = float.NaN;

        int FlipCap
        {
            get
            {
                if (_config == null) return 5;
                if (perturbation != null && perturbation.PracticeMode)
                    return _config.practiceMaxFlipsPerRound;
                return _config.maxFlipsPerRound;
            }
        }

        void Awake()
        {
            Instance = this;
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();
            if (laser        == null) laser        = FindAnyObjectByType<LaserFirer>();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void BeginBlock(StudyConfig config)
        {
            _config = config;
            StopRound();
        }

        /// <summary>
        /// Configures the round's two alternating frame rates (<paramref name="baseFps"/> and
        /// <paramref name="alteredFps"/>). The round starts at <paramref name="baseFps"/> and
        /// toggles between the two on each scheduled flip.
        /// </summary>
        public void BeginRound(StudyConfig config, int baseFps, int alteredFps)
        {
            _config = config;
            if (_config == null || !_config.IsShockwave)
            {
                StopRound();
                return;
            }

            _active           = true;
            _armed            = false;
            _retrying         = false;
            _baseFps          = Mathf.Max(1, baseFps);
            _alteredFps       = Mathf.Max(1, alteredFps);
            _alteredPhase     = false;
            _flipsThisRound   = 0;
            _flipsThisAttempt = 0;
            _earliesThisRound = 0;
            _windowClosesAt   = 0f;
            _earlyLockoutUntil = 0f;
            _nextFlipAt       = float.PositiveInfinity;
            FirstFlipAt       = float.NaN;
            LastFlipAt        = float.NaN;
            LastForeperiodSec = float.NaN;
            FlippedThisFrame  = false;
        }

        /// <summary>A new attempt after a counted TOO LATE: the rate QUEST+ now wants, starting from the base again. The
        /// next flip is drawn at the end of TRY AGAIN! (ArmAfterGun), so nothing changes while firing is off.</summary>
        public void BeginAttempt(int alteredFps)
        {
            _alteredFps   = Mathf.Max(1, alteredFps);
            _alteredPhase = false;
        }

        public void StopRound()
        {
            _active            = false;
            _armed             = false;
            _retrying          = false;
            _alteredPhase      = false;
            _windowClosesAt    = 0f;
            _earlyLockoutUntil = 0f;
            _nextFlipAt        = float.PositiveInfinity;
            FlippedThisFrame   = false;
        }

        void Update()
        {
            FlippedThisFrame = false;
            if (!_active || _config == null) return;

            bool canFire = laser != null && laser.FiringEnabled;
            if (canFire && !_armed)
            {
                ArmAfterGun();
            }
            else if (!canFire && _armed)
            {
                _armed = false;
                if (!_retrying)
                {
                    _windowClosesAt    = 0f;
                    _earlyLockoutUntil = 0f;
                    _nextFlipAt        = float.PositiveInfinity;
                }
            }

            if (!_armed) return;

            float now = Time.realtimeSinceStartup;

            // 1. Check if an open detection window just expired unanswered.
            if (_windowClosesAt > 0f && now >= _windowClosesAt)
            {
                _windowClosesAt = 0f;
                int cap = FlipCap;
                if (cap > 0 && _flipsThisRound >= cap)
                {
                    StopRound();
                    if (GameManager.Instance != null)
                        GameManager.Instance.ResolveWithoutShot(ResponseOutcome.FlipCap);
                    return;
                }

                _waitStartedAt = now;
                _nextFlipAt    = now + DrawDelay(_config.swFlipDelayMinSec, _config.swFlipDelayMaxSec);
            }

            // 2. Check if the scheduled frame-rate flip has arrived.
            if (_windowClosesAt <= 0f && now >= _nextFlipAt)
            {
                _alteredPhase      = !_alteredPhase;
                _flipsThisRound++;
                _flipsThisAttempt++;
                if (float.IsNaN(FirstFlipAt)) FirstFlipAt = now;
                LastFlipAt         = now;
                LastForeperiodSec  = now - _waitStartedAt;
                FlippedThisFrame   = true;
                _windowClosesAt    = now + Mathf.Max(0.05f, _config.swWindowSec);
                _nextFlipAt        = float.PositiveInfinity;
                _earlyLockoutUntil = 0f;
            }
        }

        void ArmAfterGun()
        {
            _armed             = true;
            _flipsThisAttempt  = 0;
            _windowClosesAt    = 0f;
            _earlyLockoutUntil = 0f;

            if (_retrying)
            {
                _retrying = false;
            }
            else
            {
                _alteredPhase     = false;
                _flipsThisRound   = 0;
                _earliesThisRound = 0;
            }

            _waitStartedAt = Time.realtimeSinceStartup;
            _nextFlipAt    = _waitStartedAt + DrawDelay(_config.swFlipDelayMinSec, _config.swFlipDelayMaxSec);
        }

        /// <summary>
        /// Read by <see cref="LaserFirer"/> before firing. True during the 1 s lockout after a forgiven TOO EARLY press,
        /// so spam-clicking does not immediately forfeit the round. The press is logged with outcome lockout.
        /// </summary>
        public bool InEarlyLockout => _active && Time.realtimeSinceStartup < _earlyLockoutUntil;

        /// <summary>
        /// Classifies a player fire press on a shockwave block:
        /// - <see cref="ResponseOutcome.Detected"/> if pressed inside the <c>swWindowSec</c> window after a flip.
        /// - <see cref="ResponseOutcome.Early"/> if pressed before the first flip since the starting gun
        ///   (forgiven up to <c>swMaxEarlyPerRound</c> in main rounds and always in practice; redraws the delay).
        ///   Never reaches QUEST+: the frame rate has not changed yet, so the press answers nothing.
        /// - <see cref="ResponseOutcome.Late"/> if pressed after a flip's window closed (counted miss, then TRY AGAIN!).
        /// </summary>
        public PressVerdict ClassifyPlayerFire()
        {
            float now = Time.realtimeSinceStartup;

            if (WindowOpen)
            {
                _windowClosesAt = 0f;
                StopRound();
                return new PressVerdict
                {
                    outcome            = ResponseOutcome.Detected,
                    endsRound          = true,
                    countedByStaircase = true,
                    retry              = false,
                };
            }

            if (_flipsThisAttempt == 0)
            {
                _earliesThisRound++;
                bool practice = perturbation != null && perturbation.PracticeMode;
                int  maxEarly = _config != null ? _config.swMaxEarlyPerRound : 1;
                bool forgiven = practice || _earliesThisRound <= maxEarly;

                if (forgiven)
                {
                    float dMin    = _config != null ? _config.swFlipDelayMinSec : 1f;
                    float dMax    = _config != null ? _config.swFlipDelayMaxSec : 3.5f;
                    float lockout = _config != null ? _config.swEarlyLockoutSec  : 1f;
                    _waitStartedAt     = now;
                    _nextFlipAt        = now + DrawDelay(dMin, dMax);
                    _earlyLockoutUntil = now + lockout;

                    return new PressVerdict
                    {
                        outcome            = ResponseOutcome.Early,
                        endsRound          = false,
                        countedByStaircase = false,
                        retry              = false,
                    };
                }

                StopRound();
                return new PressVerdict
                {
                    outcome            = ResponseOutcome.Early,
                    endsRound          = true,
                    countedByStaircase = false,
                    retry              = false,
                };
            }

            // Pressed after at least one flip's window already closed: TOO LATE -> counted miss, then TRY AGAIN!.
            _windowClosesAt    = 0f;
            _nextFlipAt        = float.PositiveInfinity;
            _earlyLockoutUntil = 0f;
            _retrying          = true;

            return new PressVerdict
            {
                outcome            = ResponseOutcome.Late,
                endsRound          = false,
                countedByStaircase = true,
                retry              = true,
            };
        }

        static float DrawDelay(float minSec, float maxSec)
        {
            float lo = Mathf.Max(0.05f, Mathf.Min(minSec, maxSec));
            float hi = Mathf.Max(lo, Mathf.Max(minSec, maxSec));
            return Random.Range(lo, hi);
        }
    }
}
