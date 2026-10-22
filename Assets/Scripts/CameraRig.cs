using System.Collections;
using UnityEngine;

namespace JndUfo
{
    public class CameraRig : MonoBehaviour
    {
        public static CameraRig Instance { get; private set; }

        [Tooltip("Camera used for camera-relative math. Defaults to a Camera on this object, else Camera.main.")]
        public Camera cam;

        [Header("Optional Follow")]
        public bool follow = false;
        public Transform followTarget;
        public Vector3 followOffset = Vector3.zero;
        public float followLerp = 10f;

        [Header("Shake")]
        public float shakeDuration    = 0.3f;
        public float shakeMagnitude   = 0.15f;
        [Tooltip("Exponential decay rate — higher = snappier ring-down.")]
        public float shakeDecay       = 14f;
        [Tooltip("Oscillation frequency in Hz.")]
        public float shakeFrequency   = 9f;
        [Tooltip("How far off-centre a hit must be before the shake tilts sideways (0 = pure vertical).")]
        [Range(0f, 1f)]
        public float shakeLateralBias = 0.45f;
        [Tooltip("0 = pure damped-sine (robotic), 1 = equal Perlin noise on top (organic). 0.35 is a good start.")]
        [Range(0f, 1f)]
        public float shakeNoiseBlend  = 0.35f;

        [Header("Handheld")]
        [Tooltip("A faint, slow wander of the view, as if the camera were held by hand, so the scene is never " +
                 "quite still. It moves the camera only: the UFO, the towers and every shot keep their world " +
                 "positions, the mouse moves the UFO exactly as before (by deltas, which a camera that only " +
                 "drifts and barely turns does not change), and the pan and the shake work on top of it. Like " +
                 "everything that moves on screen it shows the frame rate, so keep it the same for a whole study.")]
        public bool handheld = true;

        [Tooltip("Peak drift of the camera's position, world units. At the scene's lens about 40 px of a 1080p " +
                 "screen per unit on the play plane and less on the city behind it, so near things sway a " +
                 "touch more than far ones. Most of the time it is within a third of this.")]
        [Min(0f)] public float handheldDrift = 0.08f;

        [Tooltip("Peak turn of the camera, degrees. It moves the whole picture, the sky included: at the " +
                 "scene's 15° lens a degree is about 70 px of a 1080p screen, so this stays tiny. Most of " +
                 "the time it is within a third of this.")]
        [Min(0f)] public float handheldTurn = 0.06f;

        [Tooltip("The wander's main rhythm, cycles per second. Slow reads as breathing; fast as nerves.")]
        [Range(0.05f, 2f)] public float handheldHz = 0.3f;

        [Header("Parallax")]
        [Tooltip("Swing the camera a little around the play plane as the UFO moves, the way a camera operator " +
                 "follows a subject: toward whichever side of the screen the UFO is on, easing after it. The " +
                 "swing pivots ON the play plane, so the UFO, the towers, every shot and the mouse's feel stay " +
                 "where they were on screen, to a pixel or two at the edges — only what is nearer or farther " +
                 "moves: the city and the sky slide with the UFO, the foreground grass against it, and the " +
                 "scene reads in depth. It is whole-screen motion that follows the UFO, so it shows the frame " +
                 "rate more than anything else in the scene does; decide on it before collecting data and keep " +
                 "it the same for a whole study.")]
        public bool parallax = true;

        [Tooltip("Degrees the camera swings around the play plane with the UFO at either side of the screen. " +
                 "At 0.65, as the UFO crosses the whole screen at 1080p, the sky slides about 80 px with it, the " +
                 "main skyline about 30 px, and the grass line about 40 px the other way (the nearest, darkest " +
                 "grass about 180 px).")]
        [Range(0f, 2f)] public float parallaxYaw = 0.65f;

        [Tooltip("Degrees the camera swings up or down with the UFO at the top or bottom of its flight.")]
        [Range(0f, 1f)] public float parallaxPitch = 0.22f;

        [Tooltip("How briskly the swing follows the UFO: the stiffness of a critically damped spring, in " +
                 "radians per second. At 4 it is halfway after about 0.4 s and settles in about a second — a " +
                 "smooth, slightly lagging follow, never a jolt.")]
        [Range(0.5f, 20f)] public float parallaxResponse = 4f;

        [Tooltip("What the camera follows. Defaults to the UFO.")]
        public Transform parallaxTarget;

        Vector3    _restPosition;
        Quaternion _restRotation;
        Vector3    _shakeOffset;
        Coroutine  _shakeCoroutine;
        float      _originX;
        float      _handheldWeight;   // eases the wander in at start and in and out when toggled

        // Parallax: the swing's spring state (x across, y up, each -1..1 of its range), its ease in and out,
        // and the play plane's depth and the UFO's flight band, read once from the UFO.
        Vector2    _swing, _swingVelocity;
        bool       _swingStarted;
        float      _parallaxWeight;
        UfoController _ufo;
        float      _planeZ, _flightLow, _flightHigh;

        public float OriginX => _originX;

        /// <summary>
        /// The camera's settled X, excluding shake and the handheld wander. Anything that follows
        /// the camera should track this rather than transform.position, or it inherits the shake
        /// jitter too.
        /// </summary>
        public float RestX => _restPosition.x;

        void Awake()
        {
            Instance = this;
            if (cam == null) cam = GetComponent<Camera>();
            if (cam == null) cam = Camera.main;
            _restPosition = transform.position;
            _restRotation = transform.rotation;
            _originX      = _restPosition.x;
        }

        void Start()
        {
            _ufo = parallaxTarget != null ? parallaxTarget.GetComponent<UfoController>()
                                          : FindAnyObjectByType<UfoController>();
            if (parallaxTarget == null && _ufo != null) parallaxTarget = _ufo.transform;

            // The swing pivots on the play plane, the plane the UFO flies in; the UFO's flight band is the
            // range its height is read against.
            _planeZ     = _ufo != null ? _ufo.fixedZ : 0f;
            _flightLow  = _ufo != null ? _ufo.minY : _restPosition.y - 5f;
            float top   = _restPosition.y + RestHalfHeight() * 0.95f;
            _flightHigh = _ufo != null && _ufo.maxY > 0f ? Mathf.Min(_ufo.maxY, top) : top;
            if (_flightHigh <= _flightLow + 0.01f) _flightHigh = _flightLow + 1f;
        }

        void LateUpdate()
        {
            if (follow && followTarget != null)
            {
                Vector3 desired = followTarget.position + followOffset;
                _restPosition = Vector3.Lerp(_restPosition, desired,
                    1f - Mathf.Exp(-followLerp * Time.unscaledDeltaTime));
            }

            // The parallax swing: a turn of the camera about a point on the play plane straight ahead of it.
            Quaternion swing = Quaternion.identity;
            Vector3    pivot = _restPosition;
            _parallaxWeight = Mathf.MoveTowards(_parallaxWeight, parallax && parallaxTarget != null ? 1f : 0f,
                                                Time.unscaledDeltaTime / HandheldEaseSec);
            if (_parallaxWeight > 0f && parallaxTarget != null)
            {
                Vector2 aim = SwingTarget();
                if (!_swingStarted)
                {
                    // Start where the UFO already is, rather than swinging over to it.
                    _swing         = aim;
                    _swingVelocity = Vector2.zero;
                    _swingStarted  = true;
                }
                float dt = Time.unscaledDeltaTime;
                Spring(ref _swing.x, ref _swingVelocity.x, aim.x, parallaxResponse, dt);
                Spring(ref _swing.y, ref _swingVelocity.y, aim.y, parallaxResponse, dt);

                float w = _parallaxWeight * _parallaxWeight * (3f - 2f * _parallaxWeight);
                // With the UFO to the right the camera swings round to the right of the pivot (and turns
                // left to keep facing it): the far scene slides right with the UFO, the near grass left.
                swing = Quaternion.Euler(_swing.y * parallaxPitch * w, -_swing.x * parallaxYaw * w, 0f);
                Vector3 ahead = _restRotation * Vector3.forward;
                float   along = Mathf.Abs(ahead.z) > 1e-4f ? (_planeZ - _restPosition.z) / ahead.z : 100f;
                pivot = _restPosition + ahead * along;
            }

            Vector3    drift = Vector3.zero;
            Quaternion turn  = Quaternion.identity;
            _handheldWeight = Mathf.MoveTowards(_handheldWeight, handheld ? 1f : 0f,
                                                Time.unscaledDeltaTime / HandheldEaseSec);
            if (_handheldWeight > 0f)
            {
                // A function of time alone, so it wanders the same way at any frame rate. Each axis
                // reads its own row of the noise field, so none moves in step with another.
                float t = Time.unscaledTime * handheldHz;
                float w = _handheldWeight * _handheldWeight * (3f - 2f * _handheldWeight);
                drift = new Vector3(Wander(t, 0.37f), Wander(t, 3.71f), 0f) * (handheldDrift * w);
                float a = handheldTurn * w;
                turn  = Quaternion.Euler(Wander(t, 7.13f) * a, Wander(t, 11.9f) * a, Wander(t, 15.3f) * a * 0.5f);
            }

            transform.position = pivot + swing * (_restPosition - pivot) + _shakeOffset + drift;
            transform.rotation = swing * _restRotation * turn;
        }

        // Where the swing is heading: the UFO's place across the view at rest (x, -1 at the left edge to 1 at
        // the right) and in its flight band (y, -1 at the bottom to 1 at the top). Read against the camera
        // at rest, never the swung one, so the swing cannot feed back into itself.
        Vector2 SwingTarget()
        {
            Vector3 p     = parallaxTarget.position;
            float   halfH = RestHalfHeight();
            float   halfW = halfH * (cam != null ? cam.aspect : 16f / 9f);
            float   x     = halfW > 1e-4f ? (p.x - _restPosition.x) / (halfW * 0.96f) : 0f;
            float   mid   = (_flightLow + _flightHigh) * 0.5f;
            float   y     = (p.y - mid) / ((_flightHigh - _flightLow) * 0.5f);
            return new Vector2(Mathf.Clamp(x, -1f, 1f), Mathf.Clamp(y, -1f, 1f));
        }

        // Half the view's height at the play plane, for the camera at rest.
        float RestHalfHeight()
        {
            Camera c    = cam != null ? cam : Camera.main;
            float   fov = c != null ? c.fieldOfView : 15f;
            return Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * Mathf.Max(1f, _planeZ - _restPosition.z);
        }

        // One step of a critically damped spring pulling x toward target, solved exactly for a target that
        // holds still over the step — so it follows the same path at any frame rate, a slow one included.
        static void Spring(ref float x, ref float v, float target, float omega, float dt)
        {
            float y = x - target;
            float e = Mathf.Exp(-omega * dt);
            float k = v + omega * y;
            x = target + (y + k * dt) * e;
            v = (v - omega * k * dt) * e;
        }

        // Seconds the handheld wander and the parallax swing take to ease in or out.
        const float HandheldEaseSec = 1.5f;

        // Smooth noise, about ±1 at its rare peaks and mostly within ±0.35: a slow octave of Perlin noise along
        // its own row of the field, with a quicker, fainter one on top for the small corrections of a hand.
        static float Wander(float t, float row) =>
            2.2f * (Mathf.PerlinNoise(t, row) - 0.5f) + 0.7f * (Mathf.PerlinNoise(t * 2.7f + 31.4f, row + 17.3f) - 0.5f);

        // The laser's gritty impact: fast noise sampling, whatever blend the scene is tuned to.
        const float NoiseRate = 22f;

        // Where each shake reads the noise. Its own generator, never UnityEngine.Random: the game draws
        // the towers, the pan and the altered side from that stream, and a shot must not change them.
        readonly System.Random _shakeRng = new System.Random(9157);

        // Shake with no directional bias (pure vertical impact).
        public void Shake() => DoShake(Vector3.zero, false);

        // Shake with direction derived from where the shot landed on screen.
        public void Shake(Vector3 hitWorldPos) => DoShake(hitWorldPos, true);

        void DoShake(Vector3 hitWorldPos, bool useHitPos)
        {
            if (_shakeCoroutine != null) StopCoroutine(_shakeCoroutine);
            _shakeCoroutine = StartCoroutine(ShakeRoutine(shakeDuration, shakeMagnitude, shakeDecay,
                                                           shakeFrequency, shakeNoiseBlend, NoiseRate,
                                                           hitWorldPos, useHitPos));
        }

        IEnumerator ShakeRoutine(float duration, float magnitude, float decay, float frequency,
                                  float noiseBlend, float noiseRate, Vector3 hitWorldPos, bool useHitPos)
        {
            // Compute lateral lean: how far the hit was from screen centre, mapped to [-1, 1].
            float lateral = 0f;
            if (useHitPos && cam != null)
            {
                Vector3 vp = cam.WorldToViewportPoint(hitWorldPos);
                lateral = Mathf.Clamp((vp.x - 0.5f) * 2f, -1f, 1f) * shakeLateralBias;
            }

            // Primary direction is upward (ground-impact shockwave lifts the camera).
            // Lateral component leans toward the side the shot landed on.
            Vector3 dir         = new Vector3(lateral, 1f, 0f).normalized;
            float   angularFreq = frequency * Mathf.PI * 2f;
            float   elapsed     = 0f;
            float   seedX       = (float)_shakeRng.NextDouble() * 100f;
            float   seedY       = (float)_shakeRng.NextDouble() * 100f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;

                // Shared decay envelope so both components ring down together.
                float env = magnitude * Mathf.Exp(-decay * elapsed);

                // Structured component: damped sine along the impact direction.
                Vector3 sine = dir * env * Mathf.Sin(angularFreq * elapsed);

                // Organic component: Perlin noise scaled by the same envelope.
                float nx    = (Mathf.PerlinNoise(seedX + elapsed * noiseRate, 0f) - 0.5f) * 2f * env * noiseBlend;
                float ny    = (Mathf.PerlinNoise(0f, seedY + elapsed * noiseRate) - 0.5f) * 2f * env * noiseBlend;

                _shakeOffset = sine + new Vector3(nx, ny, 0f);

                yield return null;
            }

            _shakeOffset    = Vector3.zero;
            _shakeCoroutine = null;
        }

        // ── Pan ──────────────────────────────────────────────────────────────

        public IEnumerator PanTo(float targetWorldX, float duration)
        {
            float startX  = _restPosition.x;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t     = Mathf.Clamp01(elapsed / duration);
                // Smootherstep: eases in and out, so the city glides to its new place instead of
                // lurching off at the start of the reset.
                float eased = t * t * t * (t * (t * 6f - 15f) + 10f);
                _restPosition.x = Mathf.Lerp(startX, targetWorldX, eased);
                yield return null;
            }
            _restPosition.x = targetWorldX;
        }

        // ── Camera-relative axes ─────────────────────────────────────────────
        //
        // The camera at rest: the side of the boundary the UFO is on is measured along Right, and it must not
        // tilt with the handheld wander or the parallax swing.

        public Vector3 Right
        {
            get
            {
                Vector3 r = AxesFromRest ? _restRotation * Vector3.right
                          : cam != null  ? cam.transform.right : Vector3.right;
                r.y = 0f;
                return r.sqrMagnitude > 1e-6f ? r.normalized : Vector3.right;
            }
        }

        public Vector3 Forward
        {
            get
            {
                Vector3 f = AxesFromRest ? _restRotation * Vector3.forward
                          : cam != null  ? cam.transform.forward : Vector3.forward;
                f.y = 0f;
                return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            }
        }

        // True when this rig drives the camera's own transform, so its rest pose is the camera's.
        bool AxesFromRest => cam == null || cam.transform == transform;

        public Vector3 Up => Vector3.up;
        public Camera ActiveCamera => cam != null ? cam : Camera.main;

        // ── View-frustum helpers ─────────────────────────────────────────────

        /// <summary>World centre of the visible frustum on the plane z = worldZ.</summary>
        public Vector3 ViewCenterAtZ(float worldZ) =>
            ViewportPointAtZ(ActiveCamera, 0.5f, 0.5f, worldZ);

        /// <summary>Half-extents (x = half-width, y = half-height) on the plane z = worldZ.</summary>
        public Vector2 ViewHalfExtentsAtZ(float worldZ)
        {
            Camera c = ActiveCamera;
            if (c == null) return new Vector2(15f, 10f);
            Vector3 center = ViewportPointAtZ(c, 0.5f, 0.5f, worldZ);
            Vector3 left   = ViewportPointAtZ(c, 0f,   0.5f, worldZ);
            Vector3 right  = ViewportPointAtZ(c, 1f,   0.5f, worldZ);
            Vector3 bottom = ViewportPointAtZ(c, 0.5f, 0f,   worldZ);
            Vector3 top    = ViewportPointAtZ(c, 0.5f, 1f,   worldZ);
            float hx = Mathf.Max(Mathf.Abs(right.x - center.x), Mathf.Abs(center.x - left.x));
            float hy = Mathf.Max(Mathf.Abs(top.y   - center.y), Mathf.Abs(center.y - bottom.y));
            return new Vector2(hx, hy);
        }

        /// <summary>
        /// Returns the world position of viewport point (vx, vy) projected onto the
        /// plane z = worldZ. Works for both perspective and orthographic cameras.
        /// </summary>
        public static Vector3 ViewportPointAtZ(Camera c, float vx, float vy, float worldZ)
        {
            if (c == null) return new Vector3(0f, 0f, worldZ);
            Ray ray = c.ViewportPointToRay(new Vector3(vx, vy, 0f));
            float dz = ray.direction.z;
            if (Mathf.Abs(dz) < 1e-6f) return new Vector3(ray.origin.x, ray.origin.y, worldZ);
            float t = (worldZ - ray.origin.z) / dz;
            if (t < 0f) t = -t; // plane behind origin — flip to get the forward intersection
            return ray.origin + ray.direction * t;
        }
    }
}
