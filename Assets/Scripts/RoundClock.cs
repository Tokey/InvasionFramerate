using UnityEngine;

namespace JndUfo
{
    /// <summary>How a round ended or how a response was classified.</summary>
    public enum TrialOutcome
    {
        /// <summary>The participant fired. Hit or miss is decided by aim.</summary>
        Shot,
        /// <summary>Shockwave: fired inside the detection window after a frame-rate flip (hit).</summary>
        Detected,
        /// <summary>Shockwave: fired before the first frame-rate flip since the starting gun.</summary>
        Early,
        /// <summary>Shockwave: fired after a flip's detection window had already closed (miss + re-gate).</summary>
        Late,
        /// <summary>Shockwave: all allowed flips (maxFlipsPerRound) passed unanswered.</summary>
        Timeout,
        /// <summary>The round's wall clock ran out with no shot.</summary>
        Expired,
        /// <summary>The UFO crossed the tower more times than the round allows, with no shot.</summary>
        CrossingCap,
        /// <summary>A press inside the fire cooldown: nothing fired. Logged, never scored.</summary>
        PressCooldown,
        /// <summary>A press inside the lockout after a forgiven TOO EARLY: nothing fired. Logged, never scored.</summary>
        PressLockout,
        /// <summary>A press with firing off (reveal, reset, GO!, TRY AGAIN!): nothing fired. Logged, never scored.</summary>
        PressNotLive,
    }

    public static class TrialOutcomeExtensions
    {
        /// <summary>True when the participant pulled the trigger (as opposed to a round running out).</summary>
        public static bool IsPlayerShot(this TrialOutcome outcome) =>
            outcome == TrialOutcome.Shot ||
            outcome == TrialOutcome.Detected ||
            outcome == TrialOutcome.Early ||
            outcome == TrialOutcome.Late;
    }

    /// <summary>
    /// Full verdict for a trial event (used by shockwave to distinguish forgiven early presses and
    /// re-gated late presses from round-ending responses).
    /// </summary>
    public struct TrialVerdict
    {
        public TrialOutcome outcome;
        public bool         endsRound;
        public bool         countedByStaircase;
        public bool         regate;
    }

    /// <summary>When a round armed and when it was answered, for the shot log.</summary>
    public struct RoundTiming
    {
        /// <summary>Time.realtimeSinceStartup at the round's starting gun (firing handed back).</summary>
        public float armedAt;
        /// <summary>Time.realtimeSinceStartup of the shot; NaN when the round ended without one.</summary>
        public float firedAt;
        /// <summary>Time.realtimeSinceStartup the round ended, shot or not.</summary>
        public float endedAt;
    }

    /// <summary>
    /// The round's watchdog. The participant decides when to cross the tower and when to shoot, so
    /// the only clock a round needs is a backstop: a round that runs past <c>roundTimeoutSec</c>, or
    /// whose UFO crosses the tower more than <c>maxCrossingsPerRound</c> times, closes as a miss with
    /// no shot. Both come from the block's CSV row; 0 turns either off.
    ///
    /// A round is bounded exactly by the firing-enabled window. <see cref="LaserFirer.FiringEnabled"/>
    /// is already the project's single "the participant may act now" signal — off during the reveal,
    /// between phases, and once the round ends — so arming off its rising edge means the clock can
    /// never run while the participant has no way to answer, without every call site having to
    /// remember to start and stop it.
    ///
    /// Auto-created by <see cref="GameManager"/>, so no scene wiring is needed.
    /// </summary>
    [DefaultExecutionOrder(-100)]   // after PerturbationController (−200), before LaserFirer reads input
    public class RoundClock : MonoBehaviour
    {
        public static RoundClock Instance { get; private set; }

        [Header("References  (auto-found)")]
        public PerturbationController perturbation;
        public LaserFirer             laser;

        StudyConfig _config;
        bool        _running;
        float       _armedAt;
        float       _firedAt = float.NaN;
        float       _endedAt = float.NaN;
        int         _shownSecond = -1;   // debug HUD throttle: refresh once per whole second

        /// <summary>True while a round is armed and waiting for its response.</summary>
        public bool TrialRunning => _running;

        /// <summary>Seconds since the round's starting gun; 0 when no round is running. For the debug
        /// HUD only — the participant is never shown a round clock, which would be a second stimulus
        /// to time against.</summary>
        public float RoundElapsedSec => _running ? Time.realtimeSinceStartup - _armedAt : 0f;

        float RoundTimeout => _config != null ? _config.roundTimeoutSec      : 0f;
        int   MaxCrossings => _config != null ? _config.maxCrossingsPerRound : 0;

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

        /// <summary>Applies the block's round bounds. Called from PerturbationController.BeginBlock so
        /// the clock and the staircase always describe the same block.</summary>
        public void BeginBlock(StudyConfig config)
        {
            _config = config;
            Stop();
        }

        void Update()
        {
            bool regating = ShockwaveController.Instance != null && ShockwaveController.Instance.Regating;
            bool live = ((laser != null && laser.FiringEnabled) || (_running && regating)) && perturbation != null;

            if (live && !_running)      Arm();
            else if (!live && _running) Stop();
            if (!_running) return;

            float elapsed = Time.realtimeSinceStartup - _armedAt;
            RefreshDebugClock(elapsed);

            float timeout = RoundTimeout;
            if (timeout > 0f && elapsed >= timeout) { ResolveWithoutShot(TrialOutcome.Expired); return; }

            // "The cap is how many crossings the round may contain": the first one past it closes it.
            // Shockwave blocks have no spatial tower boundary, so crossings do not apply.
            int cap = MaxCrossings;
            if (_config != null && !_config.IsShockwave && cap > 0 && perturbation.TrialCrossings > cap)
                ResolveWithoutShot(TrialOutcome.CrossingCap);
        }

        void Arm()
        {
            _running     = true;
            _armedAt     = Time.realtimeSinceStartup;
            _firedAt     = float.NaN;
            _endedAt     = float.NaN;
            _shownSecond = -1;
        }

        void Stop()
        {
            if (_running && float.IsNaN(_endedAt)) _endedAt = Time.realtimeSinceStartup;
            _running = false;
            if (perturbation != null) perturbation.RefreshDebugText();
        }

        void ResolveWithoutShot(TrialOutcome outcome)
        {
            Stop();
            if (GameManager.Instance != null) GameManager.Instance.ResolveWithoutShot(outcome);
        }

        /// <summary>Stamps a shot and closes the round, so the watchdog cannot also fire for a round
        /// the participant already answered. Called once per shot by <see cref="GameManager"/>.</summary>
        public void NoteShot(bool closeRound = true)
        {
            _firedAt = Time.realtimeSinceStartup;
            if (closeRound)
            {
                _endedAt = _firedAt;
                Stop();
            }
        }

        /// <summary>When the round that just resolved armed and was answered.</summary>
        public RoundTiming Snapshot(bool playerFired) => new RoundTiming
        {
            armedAt = _armedAt,
            firedAt = playerFired ? _firedAt : float.NaN,
            endedAt = float.IsNaN(_endedAt) ? Time.realtimeSinceStartup : _endedAt,
        };

        // Once per whole second, never per frame: the HUD line is a string, and building one every
        // frame is garbage in exactly the place this study measures frame times.
        void RefreshDebugClock(float elapsed)
        {
            int second = Mathf.FloorToInt(elapsed);
            if (second == _shownSecond) return;
            _shownSecond = second;
            if (perturbation != null) perturbation.RefreshDebugText();
        }
    }
}
