using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// One press of the fire button, or one round running out. Every press in an open phase has its row, those that
    /// fired nothing included (outcome cooldown / lockout / notLive), with the state of the game at that instant, so a
    /// round has as many rows as it had presses; <see cref="roundEnded"/> marks the row that closed it. It carries: the
    /// frame-rate split it presented, how faithfully each side was delivered, how
    /// the participant explored it, where the shot landed, and the QUEST+ posterior immediately
    /// after the response was folded in.
    ///
    /// Derivable quantities are deliberately absent — <c>hitX</c> is <c>towerX + missDistX</c>, the
    /// left/right frame rates follow from <c>alteredSide</c>, <c>baseFps</c> and <c>alteredFps</c>,
    /// and whether the shot came from the altered side is <c>side == alteredSide</c>.
    /// </summary>
    public struct ShotSample
    {
        public int   roundNumber;          // 1-based; main rounds continue across blocks
        public float timeSinceStartSec;    // phase clock at the response
        /// <summary>The round's base frame rate — the block's in a main round, the pair's higher
        /// rate in practice. Written into the identity's baseFps column.</summary>
        public int   baseFps;

        // ── The stimulus ─────────────────────────────────────────────────────
        /// <summary>Δ: how far the altered side's frame time is from the base's (ms), as its whole-number
        /// cap really delivers it. The stimulus QUEST+ (or the practice ladder) chose for this round.</summary>
        public float  diffMs;
        /// <summary>The same Δ as a frame-rate difference (fps).</summary>
        public int    diffFps;
        /// <summary>The altered side's frame-rate cap: base ∓ diffFps.</summary>
        public int    alteredFps;
        /// <summary>"Left" or "Right" of the tower — drawn per round unless the config pins it.</summary>
        public string alteredSide;

        // ── What was actually delivered ──────────────────────────────────────
        /// <summary>Frames over frame time while the UFO was on each side this round. NaN when it
        /// never spent a frame there. Compare against baseFps / alteredFps: a shortfall means the
        /// machine could not hold the cap and the stimulus was smaller than intended.</summary>
        public float measuredBaseFps;
        public float measuredAlteredFps;
        /// <summary>Seconds the UFO spent on each side while the split was in force.</summary>
        public float baseSideSec;
        public float alteredSideSec;
        /// <summary>Precise limiter: frames this round that overran their slot and were released
        /// with no wait. 0 under Unity's limiter, which does not report it.</summary>
        public int   lateFrames;

        // ── How the participant explored it ──────────────────────────────────
        /// <summary>Times the UFO crossed the tower — the frame-rate boundary — this round.</summary>
        public int   crossings;
        /// <summary>Phase clock of the last crossing this round; NaN if there was none.</summary>
        public float lastCrossingAtSec;
        /// <summary>firedAtSec − lastCrossingAtSec: how long after the last boundary crossing the
        /// shot came. NaN with no shot or no crossing.</summary>
        public float sinceLastCrossingSec;

        // ── Outcome ──────────────────────────────────────────────────────────
        public TrialOutcome outcome;
        /// <summary>False only for a round that ran out with no shot. hitX and missDistX are blank
        /// then rather than filled with the UFO's resting position.</summary>
        public bool  playerFired;
        /// <summary>Landed within closeRadius of the tower base — the detection response.</summary>
        public bool  isHit;
        /// <summary>Whether this response reached the QUEST+ posterior. False for practice rounds;
        /// any analysis that reconstructs the staircase must keep only phase=main and this = 1.</summary>
        public bool  countedByStaircase;
        public float totalScore;           // running score after this response

        // ── Timing (phase clock) ─────────────────────────────────────────────
        /// <summary>When the round armed — firing handed back at its GO!.</summary>
        public float trialStartSec;
        /// <summary>When the participant fired. NaN for a round that ran out.</summary>
        public float firedAtSec;
        /// <summary>From the round arming to its end, shot or not (s).</summary>
        public float roundDurationSec;

        // ── Geometry ─────────────────────────────────────────────────────────
        /// <summary>Where the shot landed on X — the UFO's X at the press.</summary>
        public float  hitX;
        /// <summary>Signed horizontal miss: hitX − towerX. Negative = left of the tower.</summary>
        public float  missDistX;
        public float  towerX;
        public float  ufoY;
        /// <summary>Which side of the tower the shot was fired from ("Left"/"Right").</summary>
        public string side;

        // ── QUEST+ posterior after this response ─────────────────────────────
        // Blank (NaN) on practice rows: practice never reaches QUEST+.
        public float threshDiffMs;         // θ̂, frame-time difference (ms): what QUEST+ estimates
        public float threshDiffFps;        // θ̂ restated as a frame-rate difference (fps)
        public float sd;                   // SD of the θ marginal (ms)
        public float slopeEstimate;        // β̂
        public float lapseEstimate;        // λ̂ — the curve's ceiling is 1 − λ

        // ── Where on screen, and how the UFO was moving ──────────────────────
        /// <summary>The camera's rest X at the response (no shake or swing). World X less this is screen
        /// position: it matters in a scroll level, where the camera follows the ship.</summary>
        public float camX;
        /// <summary>The UFO and the tower across the view at rest at the response: 0 = left edge, 1 = right.</summary>
        public float ufoViewportX;
        public float towerViewportX;
        /// <summary>The UFO's velocity along X at the response, world units a second (+ = moving right), over the
        /// last ~0.1 s of frames. With missDistX it says whether the shot came before the boundary or after it in
        /// the direction of travel — the auto level's reaction-time signature.</summary>
        public float ufoVelX;
        /// <summary>The UFO's X when the round armed (firing handed back after GO!).</summary>
        public float ufoXAtStart;
        /// <summary>Phase clock of this round's first crossing; NaN if there was none.</summary>
        public float firstCrossingAtSec;
        /// <summary>The UFO's mean, and last, horizontal speed at this round's crossings (world units a second);
        /// NaN with no crossing. In an auto level it is the autopilot's speed; flying by hand, the participant's.</summary>
        public float meanCrossingSpeed;
        public float lastCrossingSpeed;

        // ── The press, and the state it met ──────────────────────────────────
        /// <summary>Which press of the round this is, counting shots fired: 1, 2, ... A press that fired nothing carries
        /// the number the next shot will have.</summary>
        public int   attemptInRound;
        /// <summary>This row closed the round (the next row is a new round).</summary>
        public bool  roundEnded;
        /// <summary>What made the press: "mouse", "key", or blank when a round ran out.</summary>
        public string inputDevice;
        /// <summary>Whether firing was enabled when the press came.</summary>
        public bool  firingEnabled;
        /// <summary>The cap pacing this frame, as a rate and as a frame time, and whose it was.</summary>
        public int   capFps;
        public float capFrameMs;
        public PerturbationController.Pace pacedBy;
        /// <summary>Shockwave: whether the response window was open at the press.</summary>
        public bool  windowOpen;

        // ── The last change of frame rate before it ──────────────────────────
        /// <summary>How long after the change the press came (= sinceLastCrossingSec), and how many frames.</summary>
        public float reactionSec;
        public int   framesSinceLastChange;   // -1 = no change yet
        /// <summary>Changes of frame rate since the last press.</summary>
        public int   changesSinceLastPress;
        /// <summary>How much it changed: the caps either side of it, as rates and frame times, and the signed change of
        /// frame time (+ = frames got longer). 0 / NaN before the round's first change.</summary>
        public int   changeFromFps, changeToFps;
        public float changeFromMs, changeToMs, changeDeltaMs;
        /// <summary>"slower", "faster", or blank.</summary>
        public string changeDir;
        /// <summary>Shockwave: the random wait that ended in the last flip, and the response window.</summary>
        public float foreperiodSec, windowSec;
        /// <summary>Every change of frame rate so far this round, phase clock, ';'-separated.</summary>
        public string changeTimesSec;
        /// <summary>Where the ship was at the change.</summary>
        public float changeUfoX, changeUfoY;

        // ── Presses so far this round, this row included ─────────────────────
        public int   earlyPresses, latePresses, lockoutPresses, cooldownPresses, notLivePresses;

        /// <summary>The ship's X at the press, whether or not anything fired (hitX is blank if nothing did).</summary>
        public float ufoX;

        // ── The stimulus in frame time ───────────────────────────────────────
        public float baseFrameMs, alteredFrameMs, measuredBaseMs, measuredAlteredMs;
    }

    /// <summary>
    /// One rendered frame of input and world state. Written every frame, so its row rate follows
    /// the frame rate by design.
    ///
    /// <see cref="unscaledDeltaMs"/>, <see cref="targetFps"/> and <see cref="pacedBy"/> all describe
    /// the SAME frame period: the time since the previous row, the cap that paced it, and which
    /// side's cap that was. (The cap is chosen at the end of a frame and paces the next one, so the
    /// side on a row and the cap on a row can disagree for the single frame after a crossing.)
    /// </summary>
    public struct TickSample
    {
        public int   roundNumber;          // same numbering as the shot log, so the two join on it
        /// <summary>The base frame rate in force on this frame — the block's in a main phase, the
        /// current practice pair's higher rate in practice. Written into the baseFps column.</summary>
        public int   baseFps;
        public int   frameIndex;           // 0-based within the phase
        public float timeSinceStartSec;
        public float unscaledDeltaMs;      // this frame's real duration

        public int   targetFps;            // the cap that paced this frame
        public PerturbationController.Pace pacedBy;
        public bool   shockwaveFastPhase;
        public bool   shockwaveWindow;
        public int    shockwaveFlips;

        public int    diffFps;             // the round's Δ; written blank between rounds
        public float  diffMs;              // the same in frame time (ms); NaN between rounds
        public bool   armed;               // firing was enabled on this frame
        public string alteredSide;         // the round's altered side; empty between rounds
        public string side;                // side of the tower the UFO is on
        public bool   crossed;             // the UFO crossed the tower on this frame, mid-round

        public Vector2 mousePos;           // pointer position, screen px
        public Vector2 mouseDelta;         // raw hardware delta this frame
        public float   ufoX;
        public float   ufoY;
        public float   towerX;

        public bool  leftDown;
        public bool  leftPressedThisFrame;
        public bool  fireKeyDown;          // the alt-fire key (Space)

        /// <summary>A response was logged on this frame — a shot, or a round that ran out. Not the
        /// same as leftPressedThisFrame: presses during the cooldown or between rounds are dropped.</summary>
        public bool  shotFired;
        public float shotHitX;             // where it landed on X (0 when shotFired is false)

        /// <summary>The camera's rest X this frame. The UFO's place on screen is 0.5 + (ufoX − camX) /
        /// viewWidthWorld (SessionLog) in every level.</summary>
        public float camX;
    }
}
