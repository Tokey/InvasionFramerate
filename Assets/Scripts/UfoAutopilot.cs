using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The autopilot's flight path as a pure function of flight time: where the ship is across its band, which way
    /// it is going, and how far it has bobbed. Shared by <see cref="UfoAutopilot"/>, which flies it, and
    /// <see cref="GuessRateCalculator"/>, which works out the chance a blind click lands on the tower.
    ///
    /// Across a band of width L:
    ///   Linear  an infinity sign lying on its side: two long straights crossing at the middle of the band, and a
    ///           round lobe at each end. The ship flies it at <see cref="speed"/> V at every moment of the lap, in the
    ///           lobes too, so every frame moves it the same distance on screen and a change of frame rate shows the
    ///           same wherever the ship is. Slowing down to turn is what would hide it. A lobe lasts
    ///           <see cref="turnSec"/> T; through it the heading eases round (phi = a - (pi + 2a)*smoothstep(t/T)), so
    ///           curvature starts and ends at zero and the ship never lurches into a turn. The straights' angle a is
    ///           solved so the lobes end exactly at the band's ends. A longer T makes taller lobes: about 4 units either
    ///           side of the flight height at 20 u/s and 1 s.
    ///   Sine    x = L/2 - (L/2)*cos(2 pi tau/P) with P = 2L/V: the mean speed is V, the peak pi V/2 mid-band, and the
    ///           ship slows to a stop at either end. Level.
    /// Linear starts (tau = 0) at the crossing, heading right and up. The wobble is a sine of its own, on time alone.
    /// </summary>
    public struct AutoFlightPath
    {
        public AutoPathMode mode;
        /// <summary>World units a second: the speed all the way round (Linear) or the mean speed (Sine).</summary>
        public float speed;
        /// <summary>Linear: seconds a lobe at either end takes.</summary>
        public float turnSec;
        /// <summary>The slight sine wobble: amplitude (world units) and frequency (Hz).</summary>
        public float bobAmp, bobHz;

        float V => Mathf.Max(1e-3f, speed);

        // The shape solved for one speed, lobe time and band width: the lobe's track for V*T = 1, and the straights'
        // angle and length. It has no closed form, so it is integrated once and kept until the band or the path changes.
        const int TurnSteps = 256;
        static readonly float[] s_lobeX = new float[TurnSteps + 1];
        static readonly float[] s_lobeY = new float[TurnSteps + 1];
        static float s_forV = -1f, s_forT = -1f, s_forW = -1f;
        static float s_t, s_alpha, s_straight, s_lobeDepth, s_halfHeight, s_cosA, s_sinA;

        // Integrates a lobe for a straights' angle: its total sideways travel and its deepest reach, for V*T = 1.
        static void Lobe(float alpha, bool store, out double across, out double depth, out double top)
        {
            const int Sub = 4;
            int n = TurnSteps * Sub;
            double x = 0.0, y = 0.0, h = 1.0 / n, swing = System.Math.PI + 2.0 * alpha;
            depth = 0.0; top = 0.0;
            for (int i = 0; i < n; i++)
            {
                double u   = (i + 0.5) * h;
                double phi = alpha - swing * u * u * (3.0 - 2.0 * u);
                x += System.Math.Cos(phi) * h;
                y += System.Math.Sin(phi) * h;
                if (x > depth) depth = x;
                if (y > top)   top   = y;
                if (store && (i + 1) % Sub == 0)
                {
                    s_lobeX[(i + 1) / Sub] = (float)x;
                    s_lobeY[(i + 1) / Sub] = (float)y;
                }
            }
            across = -y;
        }

        void Solve(float width)
        {
            float v = V, t = Mathf.Max(0f, turnSec);
            if (v == s_forV && turnSec == s_forT && width == s_forW) return;
            s_forV = v; s_forT = turnSec; s_forW = width;

            // A lobe too long for the band is shortened until the shape fits.
            float half = 0.5f * width, alpha = 0.1f;
            for (int tries = 0; tries < 40; tries++)
            {
                float vt = v * t;
                double lo = 1e-3, hi = 1.3;
                for (int k = 0; k < 40; k++)
                {
                    double a = 0.5 * (lo + hi);
                    Lobe((float)a, false, out double across, out double depth, out _);
                    double straight = vt * across / System.Math.Sin(a);
                    double reach = 0.5 * straight * System.Math.Cos(a) + vt * depth;
                    if (reach > half) lo = a; else hi = a;
                }
                alpha = (float)(0.5 * (lo + hi));
                Lobe(alpha, false, out double ac, out double dp, out _);
                if (t < 1e-4f || 0.5 * vt * ac / System.Math.Sin(alpha) * System.Math.Cos(alpha) + vt * dp <= half * 1.01) break;
                t *= 0.85f;
            }

            Lobe(alpha, true, out double acr, out double dep, out double tp);
            s_t          = t;
            s_alpha      = alpha;
            s_cosA       = Mathf.Cos(alpha);
            s_sinA       = Mathf.Sin(alpha);
            s_straight   = t > 1e-4f ? (float)(v * t * acr / System.Math.Sin(alpha)) : width;
            s_lobeDepth  = (float)(v * t * dep);
            s_halfHeight = 0.5f * s_straight * s_sinA + (float)(v * t * tp);
        }

        /// <summary>How far before each end of a band of width <paramref name="width"/> the lobe begins.</summary>
        public float TurnDepth(float width) { Solve(width); return s_lobeDepth; }

        /// <summary>How far above and below the flight height the path reaches on a band of this width.</summary>
        public float HalfHeight(float width) { if (mode == AutoPathMode.Sine) return 0f; Solve(width); return s_halfHeight; }

        /// <summary>Seconds for one lap of a band of width <paramref name="width"/>.</summary>
        public float Period(float width)
        {
            if (width <= 1e-4f) return 1f;
            if (mode == AutoPathMode.Sine) return 2f * width / V;
            Solve(width);
            return 2f * s_straight / V + 2f * s_t;
        }

        /// <summary>
        /// Where the ship is on a band of width <paramref name="width"/> after <paramref name="tau"/> seconds of flight:
        /// <paramref name="x"/> across it (0 at the left end), its velocity along X (<paramref name="vx"/>, world units
        /// a second, + = right), and <paramref name="yLane"/>, how far above (+) or below (-) the flight height it is.
        /// </summary>
        public void Sample(float tau, float width, out float x, out float vx, out float yLane)
        {
            yLane = 0f;
            if (width <= 1e-4f) { x = 0f; vx = 0f; return; }

            float phi = Mathf.Repeat(tau, Period(width));
            float v   = V;

            if (mode == AutoPathMode.Sine)
            {
                float w = 2f * Mathf.PI / Period(width);
                x  = 0.5f * width - 0.5f * width * Mathf.Cos(w * phi);
                vx = 0.5f * width * w * Mathf.Sin(w * phi);
                return;
            }

            float c = 0.5f * width, t = s_t, l = s_straight, ca = s_cosA, sa = s_sinA;
            float halfRun = 0.5f * l / v;
            if (phi < halfRun)                                                  // crossing to the right lobe
            {
                x = c + v * phi * ca; vx = v * ca; yLane = v * phi * sa;
                return;
            }
            phi -= halfRun;
            if (phi < t)                                                        // round the right lobe, clockwise
            {
                Lobe01(phi / t, out float ax, out float ay, out float cos);
                x = c + 0.5f * l * ca + v * t * ax; vx = v * cos; yLane = 0.5f * l * sa + v * t * ay;
                return;
            }
            phi -= t;
            if (phi < 2f * halfRun)                                             // back across, through the middle
            {
                x = c + 0.5f * l * ca - v * phi * ca; vx = -v * ca; yLane = -0.5f * l * sa + v * phi * sa;
                return;
            }
            phi -= 2f * halfRun;
            if (phi < t)                                                        // round the left lobe, anticlockwise
            {
                Lobe01(phi / t, out float ax, out float ay, out float cos);
                x = c - 0.5f * l * ca - v * t * ax; vx = -v * cos; yLane = 0.5f * l * sa + v * t * ay;
                return;
            }
            phi -= t;
            x = c - 0.5f * l * ca + v * phi * ca; vx = v * ca; yLane = -0.5f * l * sa + v * phi * sa;   // on to the middle
        }

        /// <summary>X and its velocity only, for what the height does not concern.</summary>
        public void Sample(float tau, float width, out float x, out float vx) =>
            Sample(tau, width, out x, out vx, out _);

        static void Lobe01(float u, out float along, out float across, out float cos)
        {
            float q  = Mathf.Clamp01(u);
            float f  = q * TurnSteps;
            int   i  = Mathf.Min((int)f, TurnSteps - 1);
            float w  = f - i;
            along    = s_lobeX[i] + (s_lobeX[i + 1] - s_lobeX[i]) * w;
            across   = s_lobeY[i] + (s_lobeY[i + 1] - s_lobeY[i]) * w;
            cos      = Mathf.Cos(s_alpha - (Mathf.PI + 2f * s_alpha) * q * q * (3f - 2f * q));
        }

        /// <summary>
        /// The moment of the lap nearest a place on the band (<paramref name="x"/> across it, <paramref name="yLane"/>
        /// from the flight height): where a ship already there joins the path. Only ever called between rounds.
        /// </summary>
        public float NearestTau(float x, float yLane, float width)
        {
            float period = Period(width), best = 0f, bestD = float.PositiveInfinity;
            const int Steps = 512;
            for (int i = 0; i < Steps; i++)
            {
                float tau = period * i / Steps;
                Sample(tau, width, out float px, out _, out float py);
                float dx = px - x, dy = py - yLane;
                float dist = dx * dx + dy * dy;
                if (dist < bestD) { bestD = dist; best = tau; }
            }
            return best;
        }

        /// <summary>How far the wobble has the ship above (or below) its path after <paramref name="tau"/> seconds of
        /// flight.</summary>
        public float Bob(float tau) => bobAmp * Mathf.Sin(2f * Mathf.PI * bobHz * tau);
    }

    /// <summary>
    /// The auto level's pilot (<see cref="LevelType.Auto"/>, and the shockwave level): flies the UFO in an infinity sign
    /// across the screen by itself, wobbling on a very slight sine wave, so the participant has only to fire. The pointer
    /// still moves (UfoController keeps tracking it) but flies nothing.
    ///
    /// The flight is a function of flight time alone (<see cref="AutoFlightPath"/>), and flight time advances by the
    /// real time each frame took, so the ship is in exactly the same place at the same moment at any frame rate: both
    /// frame rates show one and the same motion, sampled at two rates. The frame rate is the only thing that differs.
    ///
    /// It flies only while a round is live, from the end of GO! (firing handed back) to the response, and holds the
    /// ship still otherwise: through the reveal, so the ship hovers where it fired; under the reset veil; at the
    /// prompts; through GO!.
    ///
    /// <b>It never jumps in view.</b> At a later round's GO! the ship is still hidden from the reset, so it takes up a
    /// random point of its sweep (<see cref="BeginRound"/>), and the place a round starts from says nothing about the
    /// last one. At a phase's first GO! (the participant has just pressed Space) the ship is in plain view, so it starts
    /// from where it is (<see cref="BeginFromHere"/>): flight begins at the nearest point of the lap, and whatever is
    /// left between the ship and that point melts away over <see cref="MergeSec"/>. Either way it eases off the mark
    /// over <see cref="LaunchEaseSec"/> rather than going from still to full speed in one frame.
    ///
    /// <b>The height reads as depth.</b> With <see cref="depthScale"/> the hull is drawn a little bigger high on the path
    /// and smaller low on it, the way a craft circling overhead looks bigger near and sits lower on the sky far away, so
    /// the lap reads as flight over the city rather than a shape drawn on the screen. Only the look changes (UfoFlair
    /// applies it): the shot, the score and the logs go by the ship's centre.
    ///
    /// The sweep is laid on the camera at rest (UfoController.FlightEnvelope): its ends are fractions of the view's
    /// width, the way the flight envelope is, so the pan between rounds carries the sweep with the view as it carries a
    /// mouse-flown ship, and the shake or the parallax swing cannot. The ship's own transform is moved, exactly as the
    /// pointer moves it, so the split, the shot, the score and the logs read it as they always do. Added by
    /// <see cref="UfoController"/>; engaged per block by GameManager.ApplyLevel.
    /// </summary>
    // Before LaserFirer (0), so a shot fired this frame leaves from where the ship is this frame; after the round
    // clock (−100) and the split (−200), which only read the ship in LateUpdate.
    [DefaultExecutionOrder(-20)]
    [RequireComponent(typeof(UfoController))]
    public sealed class UfoAutopilot : MonoBehaviour
    {
        /// <summary>Seconds the ship takes to ease from still to full speed each time it sets off.</summary>
        public const float LaunchEaseSec = 0.2f;
        /// <summary>How far under the pointer's floor the path may dip at the bottom of a lobe. The floor keeps the shot
        /// clear of the tallest tower; the autopilot's lobes reach it only at the far ends of the band.</summary>
        const float BelowFloor = 0.6f;
        /// <summary>Seconds a ship starting from where it is takes to settle onto the path.</summary>
        public const float MergeSec = 0.8f;

        [Tooltip("How much bigger the hull is drawn at the top of the path, and smaller at the bottom, as a fraction, so " +
                 "the lap reads as flying in depth. Look only: the shot, the score and the logs go by the ship's centre. " +
                 "0 keeps it one size.")]
        [Range(0f, 0.15f)] public float depthScale = 0.05f;

        /// <summary>True while an auto level is flying the ship.</summary>
        public bool Engaged { get; private set; }

        /// <summary>The path being flown, as the block set it.</summary>
        public AutoFlightPath Path => _path;

        /// <summary>Seconds of flight so far along the path.</summary>
        public double FlightTime => _tau;

        /// <summary>The ship's velocity along X on the path, world units a second, as of its last placing.</summary>
        public float VelocityX { get; private set; }

        /// <summary>The hull's size for its height on the path, for <see cref="UfoFlair"/>: 1 off the path.</summary>
        public float HullScale { get; private set; } = 1f;

        UfoController _ufo;
        LaserFirer    _laser;
        bool          _laserSearched;
        AutoFlightPath _path;
        float _sweep = 0.98f, _height = 0.4f;
        AutoStartMode _start = AutoStartMode.Random;

        // Flight time, in double: a session flies for many minutes, and a float clock that far along would quantise
        // the path. Never wrapped, because the sweep and the bob each have a period of their own; each is taken
        // modulo its own period when the ship is placed.
        double _tau;

        // The current leg: flight time when the ship set off, and seconds since. Solved from these rather than stepped,
        // so the ease off the mark is the same at any frame rate.
        bool   _onLeg;
        double _legTau;
        double _legSec;

        // What is left between a ship starting from where it is and the path; it melts away over MergeSec of flight.
        float  _mergeX, _mergeY;
        double _mergeSec = double.PositiveInfinity;

        // Its own generator, never UnityEngine.Random: the game draws the towers, the pan and the altered side from
        // that stream, and the autopilot must not change them.
        readonly System.Random _rng = new System.Random(4721);

        void Awake()
        {
            _ufo = GetComponent<UfoController>();
        }

        /// <summary>Takes the ship over for an auto level with <paramref name="config"/>'s path. Called under the block's
        /// prompt. The ship stays where it is, in plain view, until <see cref="BeginFromHere"/> at the prompt's Space;
        /// only before its very first frame, when nobody has seen it, is it put on the path here.</summary>
        public void Engage(StudyConfig config)
        {
            if (config == null) return;
            _path   = config.AutoPath;
            _sweep  = Mathf.Clamp(config.autoSweep, 0.2f, 1f);
            _height = Mathf.Clamp01(config.autoHeight);
            _start  = config.autoStart;
            Engaged = true;
            _onLeg  = false;

            if (_ufo != null && !_ufo.TrackingPointer)
            {
                _tau = _start == AutoStartMode.Random ? _rng.NextDouble() * _path.Period(BandWidth(out _)) : 0.0;
                ClearMerge();
                Place();
            }
        }

        /// <summary>Hands the ship back to the pointer, where it is.</summary>
        public void Disengage()
        {
            Engaged   = false;
            _onLeg    = false;
            VelocityX = 0f;
            HullScale = 1f;
        }

        /// <summary>
        /// A later round's starting gun, with the ship still hidden from the reset: the flight starts from a random point
        /// of its sweep (autoStart = random) or from where it stopped (continue), and the ship is put there now, before
        /// the split opens on this frame, so the side it starts on is the side it is on.
        /// </summary>
        public void BeginRound()
        {
            if (!Engaged) return;
            if (_start == AutoStartMode.Random)
            {
                float width = BandWidth(out _);
                _tau = _rng.NextDouble() * _path.Period(width);
            }
            _onLeg = false;
            ClearMerge();
            Place();
        }

        /// <summary>
        /// A phase's first starting gun: the participant has just pressed Space with the ship in plain view, so it does
        /// not move now. Flight will begin at the nearest point of the lap, and the difference melts away as it flies.
        /// </summary>
        public void BeginFromHere()
        {
            if (!Engaged || _ufo == null) return;

            float   width = BandWidth(out float left);
            Vector3 here  = transform.position;
            _tau = _path.NearestTau(here.x - left, here.y - CruiseY(), width);

            PathPoint(width, left, out float x, out float y, out _, out _);
            _mergeX   = here.x - x;
            _mergeY   = here.y - y;
            _mergeSec = 0.0;
            _onLeg    = false;
            VelocityX = 0f;
        }

        void Update()
        {
            if (!Engaged || _ufo == null) return;
            if (!Flying) { _onLeg = false; return; }

            if (!_onLeg)
            {
                _onLeg  = true;
                _legTau = _tau;
                _legSec = 0.0;
            }

            double dt = Time.unscaledDeltaTime;
            _legSec += dt;
            if (_mergeSec < MergeSec) _mergeSec += dt;
            _tau = _legTau + EasedFlight(_legSec);
            Place();
        }

        // Flight time gained in the first s seconds of a leg: its rate eases from 0 to 1 (a smoothstep) over the ease.
        static double EasedFlight(double s)
        {
            const double E = LaunchEaseSec;
            if (s >= E) return s - 0.5 * E;
            double u = s / E;
            return E * u * u * u * (1.0 - 0.5 * u);
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
            float sweep = Mathf.Max(0.2f, _sweep);
            float half  = 0.5f * sweep;
            left = Mathf.Lerp(minX, maxX, 0.5f - half);
            return Mathf.Max(0f, Mathf.Lerp(minX, maxX, 0.5f + half) - left);
        }

        float CruiseY()
        {
            _ufo.FlightEnvelope(out _, out _, out float floorY, out float ceilY);
            return Mathf.Lerp(floorY, ceilY, _height);
        }

        void ClearMerge()
        {
            _mergeX = _mergeY = 0f;
            _mergeSec = double.PositiveInfinity;
        }

        // Where the path has the ship now, in world X and Y, with its velocity along X and its lane.
        void PathPoint(float width, float left, out float x, out float y, out float vx, out float yLane)
        {
            _path.Sample((float)(_tau % _path.Period(width)), width, out float across, out vx, out yLane);
            float bobTau = _path.bobHz > 0f ? (float)(_tau % (1.0 / _path.bobHz)) : 0f;
            x = left + across;
            y = CruiseY() + yLane + _path.Bob(bobTau);
        }

        // Puts the ship where the path has it now, within the flight envelope.
        void Place()
        {
            if (_ufo == null) return;
            float width = BandWidth(out float left);
            _ufo.FlightEnvelope(out _, out _, out float floorY, out float ceilY);
            PathPoint(width, left, out float x, out float y, out float vx, out float yLane);

            // 1 → 0 over the merge: still where it started, then entirely on the path.
            float left01 = 0f;
            if (_mergeSec < MergeSec)
            {
                float u = (float)(_mergeSec / MergeSec);
                left01 = 1f - u * u * (3f - 2f * u);
            }

            transform.position = new Vector3(x + left01 * _mergeX,
                                             Mathf.Clamp(y + left01 * _mergeY, floorY - BelowFloor, Mathf.Max(floorY, ceilY)),
                                             _ufo.fixedZ);
            VelocityX = vx;

            float reach = _path.HalfHeight(width);
            HullScale = reach > 1e-4f ? 1f + depthScale * (1f - left01) * (yLane / reach) : 1f;
        }
    }
}
