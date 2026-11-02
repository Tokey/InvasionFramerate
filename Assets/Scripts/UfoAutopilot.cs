using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The autopilot's flight path as a pure function of flight time: where the ship is across its band and how
    /// fast it is going, and how far it has bobbed. Shared by <see cref="UfoAutopilot"/>, which flies it, and
    /// <see cref="GuessRateCalculator"/>, which works out the chance a blind click lands on the tower.
    ///
    /// Across a band of width L (from 0 at its left end to L at its right):
    ///   Linear  the ship runs at <see cref="speed"/> V from end to end and turns round on a half sine of radius
    ///           R = V·turnSec/π at each end — at the same speed, so its velocity eases through zero rather than
    ///           flipping, and its acceleration starts from zero. One period: P = 2(L − 2R)/V + 2πR/V.
    ///   Sine    x = L/2 − (L/2)·cos(2πτ/P) with P = 2L/V: the mean speed is V, the peak πV/2 mid-band, and
    ///           the ship slows to a stop at either end.
    /// Both start (τ = 0) at the left of the band heading right. The bob is a sine of its own, on time alone.
    /// </summary>
    public struct AutoFlightPath
    {
        public AutoPathMode mode;
        /// <summary>World units a second: the cruise speed (Linear) or the mean speed (Sine).</summary>
        public float speed;
        /// <summary>Linear: seconds a turn at either end takes.</summary>
        public float turnSec;
        /// <summary>The vertical bob: amplitude (world units) and frequency (Hz).</summary>
        public float bobAmp, bobHz;

        float V => Mathf.Max(1e-3f, speed);

        /// <summary>The radius of the turn at either end of a band of width <paramref name="width"/>.</summary>
        public float TurnRadius(float width) => Mathf.Clamp(V * Mathf.Max(0f, turnSec) / Mathf.PI, 0f, 0.5f * width);

        /// <summary>Seconds for one sweep there and back across a band of width <paramref name="width"/>.</summary>
        public float Period(float width)
        {
            if (width <= 1e-4f) return 1f;
            if (mode == AutoPathMode.Sine) return 2f * width / V;
            float r = TurnRadius(width);
            return 2f * (width - 2f * r) / V + 2f * Mathf.PI * r / V;
        }

        /// <summary>
        /// Where the ship is across a band of width <paramref name="width"/> after <paramref name="tau"/> seconds of
        /// flight (<paramref name="x"/>, 0 at the band's left end), and its velocity along X (<paramref name="vx"/>,
        /// world units a second, positive to the right).
        /// </summary>
        public void Sample(float tau, float width, out float x, out float vx)
        {
            float v   = V;
            float p   = Period(width);
            float phi = Mathf.Repeat(tau, p);

            if (width <= 1e-4f) { x = 0f; vx = 0f; return; }

            if (mode == AutoPathMode.Sine)
            {
                float w = 2f * Mathf.PI / p;
                x  = 0.5f * width - 0.5f * width * Mathf.Cos(w * phi);
                vx = 0.5f * width * w * Mathf.Sin(w * phi);
                return;
            }

            float r  = TurnRadius(width);
            float tl = (width - 2f * r) / v;    // one straight run
            float tc = Mathf.PI * r / v;        // one turn

            if (phi < tl) { x = r + v * phi; vx = v; return; }                 // running right
            phi -= tl;
            if (phi < tc)                                                       // turning at the right end
            {
                float th = phi * v / r;
                x  = width - r + r * Mathf.Sin(th);
                vx = v * Mathf.Cos(th);
                return;
            }
            phi -= tc;
            if (phi < tl) { x = width - r - v * phi; vx = -v; return; }        // running left
            phi -= tl;
            {                                                                   // turning at the left end
                float th = r > 1e-6f ? phi * v / r : 0f;
                x  = r - r * Mathf.Sin(th);
                vx = -v * Mathf.Cos(th);
            }
        }

        /// <summary>How far the bob has the ship above (or below) its flight height after <paramref name="tau"/>
        /// seconds of flight.</summary>
        public float Bob(float tau) => bobAmp * Mathf.Sin(2f * Mathf.PI * bobHz * tau);
    }

    /// <summary>
    /// The auto level's pilot (<see cref="LevelType.Auto"/>): flies the UFO back and forth across the screen by
    /// itself, bobbing on a slight sine wave, so the participant has only to fire — when the ship is over the place
    /// where the frame rate changes. The pointer still moves (UfoController keeps tracking it) but flies nothing.
    ///
    /// The flight is a function of flight time alone (<see cref="AutoFlightPath"/>), and flight time advances by
    /// the real time each frame took, so the ship is in exactly the same place at the same moment at any frame
    /// rate: the two sides of the boundary show one and the same motion, sampled at two rates — the frame rate is
    /// the only thing that differs.
    ///
    /// It flies only while a round is live, from the end of GO! (firing handed back) to the response, and holds the
    /// ship still otherwise: through the reveal, so the ship hovers where it fired; under the reset veil; at the
    /// prompts; through GO!. At each round's starting gun (<see cref="BeginRound"/>) it takes up a random point of
    /// its sweep, or where it stopped, with the ship still hidden — so the place a round starts from says nothing
    /// about the last one.
    ///
    /// The sweep is laid on the camera at rest (UfoController.FlightEnvelope): its ends are fractions of the
    /// view's width, the way the flight envelope is, so the pan between rounds carries the sweep with the view as
    /// it carries a mouse-flown ship, and the shake or the parallax swing cannot. The ship's own transform is moved,
    /// exactly as the pointer moves it, so the split, the shot, the score and the logs read it as they always do.
    /// Added by <see cref="UfoController"/>; engaged per block by GameManager.ApplyLevel.
    /// </summary>
    // Before LaserFirer (0), so a shot fired this frame leaves from where the ship is this frame; after the round
    // clock (−100) and the split (−200), which only read the ship in LateUpdate.
    [DefaultExecutionOrder(-20)]
    [RequireComponent(typeof(UfoController))]
    public sealed class UfoAutopilot : MonoBehaviour
    {
        /// <summary>True while an auto level is flying the ship.</summary>
        public bool Engaged { get; private set; }

        /// <summary>The path being flown, as the block set it.</summary>
        public AutoFlightPath Path => _path;

        /// <summary>Seconds of flight so far along the path.</summary>
        public double FlightTime => _tau;

        /// <summary>The ship's velocity along X on the path, world units a second, as of its last placing.</summary>
        public float VelocityX { get; private set; }

        UfoController _ufo;
        LaserFirer    _laser;
        bool          _laserSearched;
        AutoFlightPath _path;
        float _sweep = 0.9f, _height = 0.4f;
        AutoStartMode _start = AutoStartMode.Random;

        // Flight time, in double: a session flies for many minutes, and a float clock that far along would quantise
        // the path. Never wrapped, because the sweep and the bob each have a period of their own; each is taken
        // modulo its own period when the ship is placed.
        double _tau;

        // Its own generator, never UnityEngine.Random: the game draws the towers, the pan and the altered side from
        // that stream, and the autopilot must not change them.
        readonly System.Random _rng = new System.Random(4721);

        void Awake()
        {
            _ufo = GetComponent<UfoController>();
        }

        /// <summary>Takes the ship over for an auto level with <paramref name="config"/>'s path, and puts it on that
        /// path at once. Called under the block's prompt.</summary>
        public void Engage(StudyConfig config)
        {
            if (config == null) return;
            _path   = config.AutoPath;
            _sweep  = Mathf.Clamp(config.autoSweep, 0.2f, 1f);
            _height = Mathf.Clamp01(config.autoHeight);
            _start  = config.autoStart;
            if (!Engaged) _tau = 0f;
            Engaged = true;
            Place();
        }

        /// <summary>Hands the ship back to the pointer, where it is.</summary>
        public void Disengage()
        {
            Engaged   = false;
            VelocityX = 0f;
        }

        /// <summary>
        /// A round's starting gun: the flight starts from a random point of its sweep (autoStart = random) or from
        /// where it stopped (continue), and the ship is put there now — before the split opens on this frame, so
        /// the side it starts on is the side it is on. Called at both kinds of GO!.
        /// </summary>
        public void BeginRound()
        {
            if (!Engaged) return;
            if (_start == AutoStartMode.Random)
            {
                float width = BandWidth(out _);
                _tau = _rng.NextDouble() * _path.Period(width);
            }
            Place();
        }

        void Update()
        {
            if (!Engaged || _ufo == null) return;
            if (!Flying) return;
            _tau += Time.unscaledDeltaTime;
            Place();
        }

        // A round is live — the participant may fire — and nothing else is holding the ship (a pan, GO!).
        bool Flying
        {
            get
            {
                if (_ufo.FreezeMovement) return false;
                if (_laser == null && !_laserSearched)
                {
                    _laserSearched = true;
                    _laser = GetComponentInChildren<LaserFirer>();
                    if (_laser == null) _laser = FindAnyObjectByType<LaserFirer>();
                }
                return _laser != null && _laser.FiringEnabled;
            }
        }

        // The sweep's band on the view at rest: its width, and its left end in world X.
        float BandWidth(out float left)
        {
            left = 0f;
            if (_ufo == null) return 0f;
            _ufo.FlightEnvelope(out float minX, out float maxX, out _, out _);
            float half = 0.5f * _sweep;
            left = Mathf.Lerp(minX, maxX, 0.5f - half);
            return Mathf.Max(0f, Mathf.Lerp(minX, maxX, 0.5f + half) - left);
        }

        // Puts the ship where the path has it now, within the flight envelope.
        void Place()
        {
            if (_ufo == null) return;
            float width = BandWidth(out float left);
            _ufo.FlightEnvelope(out _, out _, out float floorY, out float ceilY);
            _path.Sample((float)(_tau % _path.Period(width)), width, out float x, out float vx);

            float bobTau = _path.bobHz > 0f ? (float)(_tau % (1.0 / _path.bobHz)) : 0f;
            float y = Mathf.Lerp(floorY, ceilY, _height) + _path.Bob(bobTau);
            transform.position = new Vector3(left + x, Mathf.Clamp(y, floorY, Mathf.Max(floorY, ceilY)), _ufo.fixedZ);
            VelocityX = vx;
        }
    }
}
