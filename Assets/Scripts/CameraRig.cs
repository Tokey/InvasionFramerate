using System.Collections;
using UnityEngine;

namespace JndUfo
{
    // Early, so the camera is posed before anything that reads it in LateUpdate — the fog follows RestX, and
    // with GameManager.worldScroll on RestX is written in this LateUpdate rather than by a pan coroutine.
    [DefaultExecutionOrder(-50)]
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

        // World scroll (GameManager.worldScroll): its ease in and out.
        float      _scrollWeight;

        // The hand-held follow (GameManager.worldScrollHandheldFollow): its ease in and out; the damped spring the
        // camera rides after the ship — position, velocity, and the ship's X last frame; and the extra wobble it puts
        // on the camera this frame.
        float      _followWeight, _followX, _followV, _followPrev, _followShake;
        bool       _followPrimed;

        // The hand-held follow's limits: the ship never leads the camera by more than this share of half the view
        // (past it the camera is dragged along at the ship's speed); its wobble is full at this pan speed (world
        // units a second), and wanders at this rate.
        const float FollowMaxLead    = 0.6f;
        const float FollowShakeSpeed = 25f;
        const float FollowShakeHz    = 0.9f;

        public float OriginX => _originX;

        /// <summary>
        /// True while the camera is following the UFO across. The UFO's X is then bounded by
        /// <see cref="ScrollMinX"/> and <see cref="ScrollMaxX"/> rather than by the view, which moves with it.
        /// </summary>
        public bool ScrollFollowing => _scrollWeight >= 1f && WorldScroll;

        /// <summary>The farthest the scroll can take the camera's rest X, either side: without end over the endless
        /// city, else the edge of the city that was built (see GameManager.ScrollReach).</summary>
        public float ScrollMinX => _originX - ScrollReach;
        public float ScrollMaxX => _originX + ScrollReach;

        float ScrollReach => GameManager.Instance != null ? GameManager.Instance.ScrollReach : 0f;

        /// <summary>
        /// Drops the world scroll at once, without its ease out, and puts the camera back at its origin. For
        /// the command-line checks (<see cref="QaRunner"/>), which place things by fractions of a view that
        /// must hold still. Switch GameManager.worldScroll off first, or it starts again next frame.
        /// </summary>
        public void StopScrollNow()
        {
            _scrollWeight   = 0f;
            _restPosition.x = _originX;
        }

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
            // The same ceiling as UfoController.Clamped: a set maxY up to the top of the view, else just under it.
            float top   = _restPosition.y + RestHalfHeight();
            _flightHigh = _ufo != null && _ufo.maxY > 0f ? Mathf.Min(_ufo.maxY, top) : top - RestHalfHeight() * 0.05f;
            if (_flightHigh <= _flightLow + 0.01f) _flightHigh = _flightLow + 1f;

            // On from the first frame rather than easing in, so the scene opens with the ship already centred.
            if (WorldScroll) _scrollWeight = 1f;
        }

        void LateUpdate()
        {
            if (follow && followTarget != null)
            {
                Vector3 desired = followTarget.position + followOffset;
                _restPosition = Vector3.Lerp(_restPosition, desired,
                    1f - Mathf.Exp(-followLerp * Time.unscaledDeltaTime));
            }

            ApplyWorldScroll();

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

            // The hand-held follow's wobble: while the camera pans after the ship, a quicker, larger wander of its own,
            // growing with the pan's speed — an operator's hands on a camera on the move. A function of time and the
            // follow's speed alone, so the same at any frame rate; on top of the handheld wander, not instead of it.
            if (_followShake > 0f)
            {
                float t = Time.unscaledTime * FollowShakeHz;
                float a = _followShake;
                drift += new Vector3(Wander(t, 21.7f) * 0.2f, Wander(t, 25.3f) * 0.14f, 0f) * a;
                turn  *= Quaternion.Euler(Wander(t, 29.1f) * 0.1f * a, Wander(t, 33.7f) * 0.1f * a, Wander(t, 37.3f) * 0.08f * a);
            }

            transform.position = pivot + swing * (_restPosition - pivot) + _shakeOffset + drift;
            transform.rotation = swing * _restRotation * turn;
        }

        bool WorldScroll => GameManager.Instance != null && GameManager.Instance.worldScroll && _ufo != null;

        // The world scroll: the camera's rest X follows the UFO, so the city slides past — without end over the endless
        // city, else as far as the city was built (ScrollMinX..ScrollMaxX). Locked on, the ship holds the middle of the
        // screen; with the hand-held follow the camera rides a damped spring after it instead (see StepFollow). Both
        // eased in and out when toggled mid-play, so switching never jumps the view; the scroll on from the start is on
        // from the first frame.
        void ApplyWorldScroll()
        {
            float dt = Time.unscaledDeltaTime;
            _scrollWeight = Mathf.MoveTowards(_scrollWeight, WorldScroll ? 1f : 0f, dt / HandheldEaseSec);
            _followShake  = 0f;
            if (_scrollWeight <= 0f || _ufo == null)
            {
                _followPrimed = false;
                return;
            }

            GameManager gm = GameManager.Instance;
            float target = Mathf.Clamp(_ufo.transform.position.x, ScrollMinX, ScrollMaxX);
            float x      = target;

            // The spring rides along even while the follow is off, so switching it on starts from where the ship is.
            if (!_followPrimed)
            {
                _followX      = target;
                _followV      = 0f;
                _followPrimed = true;
            }
            else if (dt > 0f && gm != null)
            {
                float u = (target - _followPrev) / dt;   // the ship's speed across the frame
                StepFollow(target, u, dt, gm.followResponse, gm.followDamping);

                // Never so far behind that the ship leaves the middle of the view: past the limit the camera is
                // dragged along at the ship's own speed.
                float lead = FollowMaxLead * RestHalfHeight() * (cam != null ? cam.aspect : 16f / 9f);
                if (Mathf.Abs(_followX - target) > lead)
                {
                    _followX = Mathf.Clamp(_followX, target - lead, target + lead);
                    _followV = u;
                }
            }
            _followPrev = target;

            _followWeight = Mathf.MoveTowards(_followWeight, gm != null && gm.worldScrollHandheldFollow ? 1f : 0f,
                                              dt / HandheldEaseSec);
            if (_followWeight > 0f)
            {
                float fw = _followWeight * _followWeight * (3f - 2f * _followWeight);
                x = Mathf.Lerp(target, _followX, fw);
                _followShake = fw * gm.followShake * Mathf.Clamp01(Mathf.Abs(_followV) / FollowShakeSpeed);
            }

            float w = _scrollWeight * _scrollWeight * (3f - 2f * _scrollWeight);
            _restPosition.x = Mathf.Lerp(_restPosition.x, x, w);
        }

        // One frame of the hand-held follow: a damped spring (natural frequency omega, damping zeta) pulling the camera
        // after the ship, solved EXACTLY for a ship moving steadily across the frame — from `target - u * dt` to
        // `target`. So the camera takes the same path at any frame rate: the frame rate changes where the boundary is,
        // and a follow that lagged by more at one rate than another would give it away by moving the picture. At a
        // steady speed the camera trails by 2 zeta u / omega, settling after the ship stops (overshooting a touch with
        // zeta below 1).
        void StepFollow(float target, float u, float dt, float omega, float zeta)
        {
            omega = Mathf.Max(0.1f, omega);
            zeta  = Mathf.Clamp(zeta, 0.05f, 1f);

            // Relative to the path the camera would settle onto at this speed: there the spring is at rest.
            float settle = -2f * zeta * u / omega;
            float z      = _followX - (target - u * dt) - settle;
            float zv     = _followV - u;
            float a      = zeta * omega;
            float e      = Mathf.Exp(-a * dt);

            float z1, zv1;
            if (zeta >= 0.999f)
            {
                float k = zv + omega * z;              // critically damped
                z1  = (z + k * dt) * e;
                zv1 = (zv - omega * k * dt) * e;
            }
            else
            {
                float b = omega * Mathf.Sqrt(1f - zeta * zeta);
                float c = Mathf.Cos(b * dt), s = Mathf.Sin(b * dt);
                float B = (zv + a * z) / b;
                z1  = e * (z * c + B * s);
                zv1 = e * (zv * c - (a * B + b * z) * s);
            }

            _followX = target + settle + z1;
            _followV = u + zv1;
        }

        // Where the swing is heading: the UFO's place across the view at rest (x, -1 at the left edge to 1 at
        // the right, where the UFO's centre can reach) and in its flight band (y, -1 at the bottom to 1 at the
        // top). Read against the camera at rest, never the swung one, so the swing cannot feed back into itself.
        Vector2 SwingTarget()
        {
            Vector3 p     = parallaxTarget.position;
            float   halfH = RestHalfHeight();
            float   halfW = halfH * (cam != null ? cam.aspect : 16f / 9f);
            float   x     = halfW > 1e-4f ? (p.x - _restPosition.x) / halfW : 0f;
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
        /// <see cref="ViewportPointAtZ"/> for the camera at rest: its settled position and aim, without the
        /// shake, the handheld wander or the parallax swing. The UFO's flight envelope is measured on this,
        /// so none of them can carry the ship past the edge of the screen. Perspective only; otherwise, or
        /// when this rig does not own the camera's transform, it measures the live camera.
        /// </summary>
        public Vector3 RestViewportPointAtZ(float vx, float vy, float worldZ)
        {
            Camera c = ActiveCamera;
            if (c == null) return new Vector3(0f, 0f, worldZ);
            if (!AxesFromRest || c.orthographic) return ViewportPointAtZ(c, vx, vy, worldZ);

            float   tanV = Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad);
            Vector3 dir  = _restRotation * new Vector3((2f * vx - 1f) * tanV * c.aspect, (2f * vy - 1f) * tanV, 1f);
            if (Mathf.Abs(dir.z) < 1e-6f) return new Vector3(_restPosition.x, _restPosition.y, worldZ);
            float t = Mathf.Abs((worldZ - _restPosition.z) / dir.z);
            return _restPosition + dir * t;
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
