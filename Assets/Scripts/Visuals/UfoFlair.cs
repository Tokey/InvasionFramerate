using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// The UFO's life: a slow spin, a bank into the motion, a jelly squash-and-stretch and light
    /// trails, so the ship reads as a solid craft flying through the scene rather than a cut-out
    /// sliding across it.
    ///
    /// The ship's own transform is never touched. UfoController moves it, PerturbationController
    /// reads it to tell which side of the tower the ship is on, and the logs record it, so all of
    /// that stays exactly as it was. Instead, in Awake, the hull is lifted into a small rig of child
    /// transforms that animate underneath the root:
    ///
    ///   PlayerUFO            the root: moved by UfoController, never here; its renderer is switched off
    ///   ├─ BottomGun_Holder  the belly turret, left exactly where it was
    ///   └─ FlairNorm         at the hull's visual centre, scaled to undo the root's uneven scale
    ///      └─ FlairTilt      bank (roll, pitch) and squash-and-stretch; the trails hang here
    ///         ├─ TrailCore, TrailRimL, TrailRimR
    ///         └─ FlairSpin   the spin
    ///            ├─ Body     a copy of the root's mesh and materials, at the root's exact pose
    ///            └─ LeftGun_Holder, RightGun_Holder: every other child the root had
    ///
    /// The hull's mesh is on the root object itself, which cannot turn without turning everything
    /// under it, so Body draws the same mesh with the same materials and the root's own renderer is
    /// switched off. At rest the result is pixel-identical; UfoController's livery, applied in Start
    /// after this has run, finds Body and tints it like any other part of the ship.
    ///
    /// <b>Why the turret stays out.</b> The laser fires from FirePoint at the foot of the turret's
    /// branch: its world position is the shot's origin, so it is the X the shot is scored and logged
    /// at, and the sight is drawn from it. A cosmetic wobble must never move the shot, so that branch
    /// stays on the root, untouched: a gimbal-stabilised belly turret that hangs level while the hull
    /// banks and spins above it.
    ///
    /// <b>Why the scale is normalised.</b> The root is scaled unevenly (0.2, 0.15, 0.2), and anything
    /// rotated under an uneven scale is sheared rather than turned: a roll under it would skew the
    /// saucer instead of tilting it. FlairNorm undoes the root's scale down to the ship's size
    /// (UfoController.size), an even scale, so rotations are rotations and the squash is along the
    /// hull's own axes. Every offset and width below is in world units for a ship of size 1, and
    /// grows with a bigger one.
    ///
    /// <b>Why the trails are cleared.</b> A trail joins each new point to the last one, so any jump
    /// the ship makes while one is emitting is drawn as a streak across the screen. And the ship
    /// does jump: it snaps to the pointer on its first frame, and between rounds it rides the camera
    /// pan under the veil. So the trails emit only while the ship is visible and free to fly; they
    /// are cleared on every frame it is hidden or frozen, cleared again as it comes back, and cleared
    /// on any frame it moves further and faster than a hand can move it.
    ///
    /// <b>Why nothing reacts to the stimulus.</b> The frame rate is the stimulus, and a visual that
    /// changed at the tower would be a second, unmeasured cue to it. The spin and the breathing run
    /// on the wall clock; the bank, the jelly and the trails follow the ship's own motion, which is
    /// the participant's hand. Nothing here knows where the tower is, which side the ship is on or
    /// what the frame rate is. Everything integrates on unscaled time, the springs in sub-steps of
    /// at most 1/240 s, so 10 fps and 500 fps give the same motion, and the one discrete test, the
    /// teleport check, is judged on speed so that it trips at the same hand speed at any frame rate.
    /// A trail is a record of where the ship was drawn, so at a low frame rate it is laid down in
    /// fewer, longer segments: it shows no motion the ship itself does not, but it holds that motion
    /// on screen for its lifetime, which is why the trails are short and faint.
    ///
    /// Built once in Awake, from UfoController.Awake. Nothing allocates per frame.
    /// </summary>
    [DisallowMultipleComponent]
    public class UfoFlair : MonoBehaviour
    {
        [Header("Spin")]
        [Tooltip("Turn the hull slowly about its own vertical axis, side pods and all, so the saucer " +
                 "reads as a solid craft rather than a flat cut-out. The belly turret does not turn: " +
                 "the shot leaves from it.")]
        public bool spin = true;

        [Tooltip("Degrees per second. Negative turns it the other way.")]
        public float spinDegreesPerSec = 70f;

        [Tooltip("How much faster it turns flat out, as a fraction of the rate above: 0.4 is 40% " +
                 "faster at full speed. It follows the ship's smoothed speed, so the spin eases up " +
                 "and back down rather than jumping.")]
        [Range(0f, 1f)] public float spinSpeedBoost = 0.4f;

        [Header("Motion")]
        [Tooltip("The speed, in world units per second, that the motion responses treat as flat out. " +
                 "The bank, the steady stretch and the spin boost are about three-quarters of the way " +
                 "to their maximums here and level off beyond it. For scale, the view is about 47 " +
                 "units wide.")]
        [Min(1f)] public float fullSpeed = 25f;

        [Tooltip("Seconds for the motion responses to catch up with a change of speed. Critically " +
                 "damped, so they settle without overshooting; the jelly is the part that overshoots. " +
                 "Shorter is twitchier.")]
        [Range(0.02f, 0.5f)] public float motionSmoothing = 0.12f;

        [Header("Bank")]
        [Tooltip("Lean into the motion: roll so the leading side dips, and tilt the near rim up when " +
                 "climbing and down when diving.")]
        public bool bank = true;

        [Tooltip("The most it rolls, in degrees, at speed.")]
        [Range(0f, 30f)] public float maxRollDeg = 12f;

        [Tooltip("The most it pitches, in degrees, climbing or diving.")]
        [Range(0f, 30f)] public float maxPitchDeg = 7f;

        [Header("Jelly")]
        [Tooltip("Squash and stretch: the hull wobbles when it starts, stops or turns back and then " +
                 "settles, stretches a little along its direction of travel, and breathes at rest.")]
        public bool jelly = true;

        [Tooltip("How fast the wobble swings, in Hz.")]
        [Range(1f, 12f)] public float jellyFrequency = 4.5f;

        [Tooltip("How quickly the wobble dies away: 1 settles without swinging back, lower swings " +
                 "longer. At 0.28 it swings back visibly once or twice.")]
        [Range(0.05f, 1f)] public float jellyDamping = 0.28f;

        [Tooltip("The most the hull may stretch or squash, as a fraction: 0.12 is ±12%. Approached " +
                 "softly, so a hard reversal never pins it flat.")]
        [Range(0f, 0.3f)] public float jellyMaxStretch = 0.12f;

        [Tooltip("How hard a change of speed sets the hull wobbling. Speeding up or slowing down " +
                 "squashes it along that axis; the spring does the rest.")]
        [Range(0f, 30f)] public float jellyKick = 8f;

        [Tooltip("Steady stretch along the direction of travel at full speed, as a fraction.")]
        [Range(0f, 0.15f)] public float jellyMotionStretch = 0.04f;

        [Tooltip("A slow idle squash-and-stretch so the hull never looks rigid, as a fraction. Runs " +
                 "on the wall clock.")]
        [Range(0f, 0.05f)] public float breatheAmount = 0.012f;

        [Tooltip("Breaths per second.")]
        [Range(0.1f, 3f)] public float breatheHz = 0.9f;

        [Header("Trails")]
        [Tooltip("A soft wake behind the hull and a thin trail off each rim. They emit only while the " +
                 "ship is visible and free to fly, and are cleared whenever it is hidden, frozen or " +
                 "placed, so a jump is never drawn as a streak.")]
        public bool trails = true;

        [Tooltip("Take the trails' colour from the ship's livery (UfoController.liveryTint), like the " +
                 "sight does. Uncheck to use the colour below.")]
        public bool matchLivery = true;
        public Color trailColor = new Color(0.35f, 1f, 0.85f, 1f);

        [Tooltip("Width of the wake at the ship, world units at size 1. It tapers to nothing.")]
        [Min(0.001f)] public float coreTrailWidth = 0.55f;
        [Tooltip("Seconds the wake lasts.")]
        [Min(0.01f)] public float coreTrailTime = 0.28f;
        [Tooltip("HDR brightness of the wake. Above 1 starts to bloom.")]
        [Min(0f)] public float coreTrailIntensity = 1.2f;
        [Tooltip("How far behind the hull's centre, away from the camera, the wake starts (world " +
                 "units), so the hull draws over the wake's head rather than the wake over the hull.")]
        public float coreTrailDepth = 0.15f;

        [Tooltip("Width of the two rim trails at the ship, world units at size 1. They taper to nothing.")]
        [Min(0.001f)] public float rimTrailWidth = 0.07f;
        [Tooltip("Seconds the rim trails last.")]
        [Min(0.01f)] public float rimTrailTime = 0.22f;
        [Tooltip("HDR brightness of the rim trails.")]
        [Min(0f)] public float rimTrailIntensity = 2f;
        [Tooltip("Where the rim trails leave the hull, world units from its centre at size 1: X either " +
                 "side, Y up. The default sits just inside the saucer's widest ring (radius about 1.05, " +
                 "0.09 below centre), so the hull draws over the trails' heads.")]
        public Vector2 rimTrailOffset = new Vector2(0.95f, -0.09f);

        [Tooltip("Opacity at the ship. Every trail fades to nothing at its tail.")]
        [Range(0f, 1f)] public float trailAlpha = 0.5f;
        [Tooltip("Minimum distance between the points a trail lays down, world units.")]
        [Min(0.001f)] public float trailVertexSpacing = 0.03f;

        [Tooltip("A single-frame jump longer than this, in world units...")]
        [Min(0f)] public float teleportDistance = 5f;
        [Tooltip("...and faster than this, in world units per second, is a placement rather than " +
                 "flight: the trails are cleared instead of drawn across it, and the frame does not " +
                 "count as motion. It is a speed as well as a distance because a distance alone would " +
                 "depend on the frame rate: at 10 fps a brisk 50 u/s sweep covers 5 units a frame, " +
                 "and clearing the trails there but not at 60 fps would make them behave differently " +
                 "on the two sides of the tower.")]
        [Min(1f)] public float teleportSpeed = 1000f;

        // ── Private ──────────────────────────────────────────────────────────

        // Longest spring sub-step: small enough that 10 fps and 500 fps integrate the same curve.
        const float MaxSubstep = 1f / 240f;
        // Longest frame the springs integrate in full: one second, the slowest frame StudyConfig can
        // present (1 fps), so every stimulus frame is integrated exactly. A longer one, a hitch, is
        // integrated as this much, which caps a single frame's work at 240 sub-steps.
        const float MaxSimStep = 1f;

        UfoController   _ufo;
        Transform       _tilt, _spin;
        TrailRenderer   _coreTrail, _rimTrailL, _rimTrailR;
        TrailRenderer[] _trails;
        Material        _coreMat, _rimMat;

        // Scratch for the trails' fade, set whenever their look is applied.
        Gradient           _gradient;
        GradientColorKey[] _colorKeys;
        GradientAlphaKey[] _alphaKeys;

        Vector3 _lastPos;
        bool    _hasLastPos;
        bool    _trailsLive;

        // The ship's velocity, smoothed, in world units per second, and its rate of change: the
        // smoothed acceleration that kicks the jelly.
        Vector2 _vel, _velRate;
        // The jelly: the log of the hull's X-to-Y stretch, and its rate. Only the balance between X
        // and Y can be shown without shearing the hull, and a damped spring is linear, so springs on
        // the two axes collapse into this one.
        float _aspect, _aspectRate;
        float _yaw;
        float _breathPhase;

        void Awake()
        {
            _ufo       = GetComponent<UfoController>();
            _gradient  = new Gradient();
            _colorKeys = new GradientColorKey[2];
            _alphaKeys = new GradientAlphaKey[2];

            BuildRig();
            BuildTrails();
        }

        // Not Awake, as in WeaponSight: the colour is read off the finished ship.
        void Start()
        {
            if (matchLivery && _ufo != null) trailColor = _ufo.liveryTint;
            ApplyTrailLook();
        }

        // Whatever the ship did while this was off is not motion: start measuring afresh, and have
        // the trails cleared before they emit again.
        void OnEnable()
        {
            _hasLastPos = false;
            _trailsLive = false;
        }

        // Switched off, the ship goes back to its plain look, and the trails stop: left emitting,
        // with nothing here to clear them, they would streak across the next camera pan.
        void OnDisable()
        {
            StopTrails();
            _vel        = Vector2.zero;
            _velRate    = Vector2.zero;
            _aspect     = 0f;
            _aspectRate = 0f;
            _yaw        = 0f;

            if (_tilt != null)
            {
                _tilt.localRotation = Quaternion.identity;
                _tilt.localScale    = Vector3.one;
            }
            if (_spin != null) _spin.localRotation = Quaternion.identity;
        }

        void OnDestroy()
        {
            if (_coreMat != null) Destroy(_coreMat);
            if (_rimMat  != null) Destroy(_rimMat);
        }

        // Live tuning: Inspector edits to the trails' look land straight away in Play mode.
        void OnValidate()
        {
            if (_coreTrail != null) ApplyTrailLook();
        }

        // ── Per frame ────────────────────────────────────────────────────────

        // LateUpdate: after UfoController has moved the ship this frame and after GameManager's pan
        // has carried it, so the motion measured here is the frame's whole motion.
        void LateUpdate()
        {
            float dt     = Time.unscaledDeltaTime;
            bool  hidden = _ufo != null && _ufo.Hidden;
            bool  free   = !hidden && (_ufo == null || !_ufo.FreezeMovement);

            // Where the ship went this frame. Only a visible ship that is free to fly moves under the
            // participant's hand; hidden or frozen, it is being carried or placed, and that is not
            // motion. The reference is taken every frame regardless, so coming back is never a jump.
            Vector3 pos      = transform.position;
            Vector2 velocity = Vector2.zero;
            bool    jumped   = false;
            if (free && _hasLastPos && dt > 0f)
            {
                float dx   = pos.x - _lastPos.x;
                float dy   = pos.y - _lastPos.y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                jumped = dist > teleportDistance && dist > teleportSpeed * dt;
                if (!jumped) velocity = new Vector2(dx / dt, dy / dt);
            }
            _lastPos    = pos;
            _hasLastPos = true;

            // Hidden, nobody sees it: come back level and still rather than mid-wobble.
            if (hidden) ResetMotion();
            else        Simulate(velocity, dt);

            Pose(dt);
            UpdateTrails(free && trails, jumped);
        }

        /// <summary>
        /// Advances the smoothed velocity and the jelly spring over one frame in equal sub-steps of
        /// at most <see cref="MaxSubstep"/>, the frame's measured velocity held across them. The
        /// smoothing is solved exactly per sub-step and the spring is stepped finely, so the same
        /// hand motion gives the same curves at any frame rate.
        /// </summary>
        void Simulate(Vector2 velocity, float dt)
        {
            if (dt <= 0f) return;

            float step  = Mathf.Min(dt, MaxSimStep);
            int   n     = Mathf.CeilToInt(step / MaxSubstep);
            float h     = step / n;

            // Critically damped, so the bank follows speed without ever swinging past it.
            float w     = 2f / motionSmoothing;
            float decay = Mathf.Exp(-w * h);

            float wj      = 2f * Mathf.PI * jellyFrequency;
            float invFull = 1f / fullSpeed;

            for (int i = 0; i < n; i++)
            {
                Follow(ref _vel.x, ref _velRate.x, velocity.x, h, w, decay);
                Follow(ref _vel.y, ref _velRate.y, velocity.y, h, w, decay);
                if (!jelly) continue;

                // Speed per axis on a 0-1 scale that levels off past fullSpeed, and how fast it is
                // changing. The spring is pulled toward a small stretch along the faster axis and
                // kicked into a squash along whichever axis the speed is changing on, so a start, a
                // stop or a reversal sets the hull wobbling and it settles back on its own.
                float mx     = Tanh(_vel.x * invFull);
                float my     = Tanh(_vel.y * invFull);
                float target = jellyMotionStretch * (Mathf.Abs(mx) - Mathf.Abs(my));
                float kick   = jellyKick * invFull * (Mathf.Abs((1f - mx * mx) * _velRate.x)
                                                    - Mathf.Abs((1f - my * my) * _velRate.y));

                float accel  = wj * wj * (target - _aspect) - 2f * jellyDamping * wj * _aspectRate - kick;
                _aspectRate += accel * h;
                _aspect     += _aspectRate * h;
            }

            if (!jelly)
            {
                _aspect     = 0f;
                _aspectRate = 0f;
            }
        }

        // Writes this frame's spin, bank and stretch onto the rig.
        void Pose(float dt)
        {
            float invFull = 1f / fullSpeed;
            float mx      = Tanh(_vel.x * invFull);
            float my      = Tanh(_vel.y * invFull);

            // The wall clock turns it, the ship's speed only quickens it.
            if (spin)
            {
                float pace = Mathf.Min(1f, Mathf.Sqrt(mx * mx + my * my));
                _yaw = Mathf.Repeat(_yaw + spinDegreesPerSec * (1f + spinSpeedBoost * pace) * dt, 360f);
            }
            else _yaw = 0f;
            _spin.localRotation = Quaternion.Euler(0f, _yaw, 0f);

            // A positive roll about the camera axis lifts the right side, so moving right rolls
            // negative and the leading side dips. A positive pitch lifts the near rim.
            float roll  = bank ? -maxRollDeg  * mx : 0f;
            float pitch = bank ?  maxPitchDeg * my : 0f;
            _tilt.localRotation = Quaternion.Euler(pitch, 0f, roll);

            // Stretch along X is squash along Y and the other way round, so the hull keeps its
            // volume. The limit is soft: the stretch eases into it rather than stopping dead.
            float stretch = 0f;
            if (jelly)
            {
                _breathPhase = Mathf.Repeat(_breathPhase + breatheHz * dt, 1f);
                float raw    = _aspect + breatheAmount * Mathf.Sin(_breathPhase * 2f * Mathf.PI);
                float limit  = Mathf.Log(1f + jellyMaxStretch);
                stretch      = limit > 1e-5f ? limit * Tanh(raw / limit) : 0f;
            }
            float s = Mathf.Exp(stretch);
            _tilt.localScale = new Vector3(s, 1f / s, 1f);
        }

        /// <summary>
        /// Emits only while the ship is visible and free to fly. Every frame it is not, the trails
        /// are cleared: hidden, the ship rides the camera pan, and frozen, it may be placed, and a
        /// trail left holding its last point would join it to the next one across the screen. On
        /// the frame it is free again they are cleared before they emit, so the first point laid
        /// down is where the ship is now. The renderers' <c>enabled</c> is left alone:
        /// UfoController.SetHidden owns it and puts it back as it found it.
        /// </summary>
        void UpdateTrails(bool live, bool jumped)
        {
            if (!live)
            {
                if (_trailsLive) SetEmitting(false);
                _trailsLive = false;
                ClearTrails();
            }
            else if (!_trailsLive)
            {
                ClearTrails();
                SetEmitting(true);
                _trailsLive = true;
            }
            else if (jumped)
            {
                ClearTrails();
            }
        }

        void StopTrails()
        {
            SetEmitting(false);
            ClearTrails();
            _trailsLive = false;
        }

        // Null-checked: these also run from OnDisable while the ship is being torn down, when the
        // trails' own objects may already be gone.
        void SetEmitting(bool on)
        {
            if (_trails == null) return;
            for (int i = 0; i < _trails.Length; i++)
                if (_trails[i] != null) _trails[i].emitting = on;
        }

        void ClearTrails()
        {
            if (_trails == null) return;
            for (int i = 0; i < _trails.Length; i++)
                if (_trails[i] != null) _trails[i].Clear();
        }

        void ResetMotion()
        {
            _vel        = Vector2.zero;
            _velRate    = Vector2.zero;
            _aspect     = 0f;
            _aspectRate = 0f;
        }

        /// <summary>
        /// One exact step of a critically damped follower toward a target held for <paramref name="h"/>
        /// seconds: <paramref name="x"/> is the smoothed value and <paramref name="rate"/> its rate of
        /// change. <paramref name="decay"/> is exp(−w·h), shared by every sub-step of a frame.
        /// </summary>
        static void Follow(ref float x, ref float rate, float target, float h, float w, float decay)
        {
            float y = x - target;
            float c = rate + w * y;
            x    = target + (y + c * h) * decay;
            rate = (rate - w * c * h) * decay;
        }

        static float Tanh(float x) => (float)System.Math.Tanh(x);

        // ── Construction ─────────────────────────────────────────────────────

        /// <summary>
        /// Lifts the hull into the rig. Runs once, from UfoController.Awake, so before any Start:
        /// the livery is applied after this and finds the hull where it now draws from.
        /// </summary>
        // The ship's size (UfoController.size): the rig's world scale, and the trails' widths with it.
        float ShipSize => _ufo != null ? Mathf.Max(0.01f, _ufo.size) : 1f;

        void BuildRig()
        {
            Transform    root       = transform;
            MeshFilter   hullFilter = GetComponent<MeshFilter>();
            MeshRenderer hull       = GetComponent<MeshRenderer>();
            Mesh         hullMesh   = hullFilter != null ? hullFilter.sharedMesh : null;

            // The turret's branch: wherever the laser, or the muzzle it fires from, hangs off the
            // root. The muzzle may not exist yet (LaserFirer makes one in its own Awake if the scene
            // gives none), but if so it will be made under the laser, inside the same branch.
            LaserFirer laser  = GetComponentInChildren<LaserFirer>(includeInactive: true);
            Transform  turret = laser != null ? BranchOf(laser.transform) : null;
            Transform  muzzle = laser != null && laser.firePoint != null ? BranchOf(laser.firePoint) : null;

            // Gathered before the rig exists, so the rig is never among them.
            var carried = new Transform[root.childCount];
            int count   = 0;
            for (int i = 0; i < carried.Length; i++)
            {
                Transform child = root.GetChild(i);
                if (child != turret && child != muzzle) carried[count++] = child;
            }

            // Pivot at the hull's visual centre. The mesh's own bounds are already in the root's
            // local space: the same centre the renderer's world bounds are built from.
            Transform norm = NewNode("FlairNorm", root);
            norm.localPosition = hullMesh != null ? hullMesh.bounds.center : Vector3.zero;

            // Undo the root's scale so the rig below has an even world scale, the ship's size. A
            // flattened root (a zero axis) cannot be undone; the rig then works in the root's scale.
            Vector3 s = root.lossyScale;
            float   k = ShipSize;
            if (Mathf.Abs(s.x) > 1e-5f && Mathf.Abs(s.y) > 1e-5f && Mathf.Abs(s.z) > 1e-5f)
                norm.localScale = new Vector3(k / s.x, k / s.y, k / s.z);

            _tilt = NewNode("FlairTilt", norm);
            _spin = NewNode("FlairSpin", _tilt);

            if (hullMesh != null && hull != null)
            {
                // Made in the root's own frame, then moved into the rig keeping its world pose: at
                // rest it sits exactly where the root's mesh drew.
                Transform body = NewNode("Body", root);
                body.SetParent(_spin, worldPositionStays: true);
                body.gameObject.AddComponent<MeshFilter>().sharedMesh = hullMesh;
                CopyRenderer(hull, body.gameObject.AddComponent<MeshRenderer>());
                hull.enabled = false;
            }

            // The side pods and anything else on the hull spin with it. Their rotations are
            // axis-aligned and the rig's scale is 1, so keeping their world pose is exact.
            for (int i = 0; i < count; i++) carried[i].SetParent(_spin, worldPositionStays: true);
        }

        void BuildTrails()
        {
            _coreMat = GlowMaterial.Create(GlowShape.Line, trailColor, 2f, "UfoTrailCore");
            _rimMat  = GlowMaterial.Create(GlowShape.Line, trailColor, 1f, "UfoTrailRim");

            // On the tilt, not the spin: they bank and stretch with the hull but do not turn with it.
            _coreTrail = MakeTrail("TrailCore", _coreMat);
            _rimTrailL = MakeTrail("TrailRimL", _rimMat);
            _rimTrailR = MakeTrail("TrailRimR", _rimMat);
            _trails    = new[] { _coreTrail, _rimTrailL, _rimTrailR };
        }

        TrailRenderer MakeTrail(string goName, Material mat)
        {
            var tr = NewNode(goName, _tilt).gameObject.AddComponent<TrailRenderer>();
            tr.emitting                   = false;   // LateUpdate decides, once the ship is free
            tr.autodestruct               = false;
            tr.alignment                  = LineAlignment.View;
            tr.textureMode                = LineTextureMode.Stretch;
            tr.numCapVertices             = 2;
            tr.numCornerVertices          = 2;
            tr.shadowCastingMode          = ShadowCastingMode.Off;
            tr.receiveShadows             = false;
            tr.lightProbeUsage            = LightProbeUsage.Off;
            tr.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            tr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            tr.sharedMaterial             = mat;
            tr.Clear();
            return tr;
        }

        // Shape, placement, fade and colour of the three trails. From Start, and again on any
        // Inspector edit in Play mode.
        void ApplyTrailLook()
        {
            // White, so the material alone decides the hue; the alpha carries the fade.
            _colorKeys[0] = new GradientColorKey(Color.white, 0f);
            _colorKeys[1] = new GradientColorKey(Color.white, 1f);
            _alphaKeys[0] = new GradientAlphaKey(trailAlpha, 0f);
            _alphaKeys[1] = new GradientAlphaKey(0f, 1f);
            _gradient.SetKeys(_colorKeys, _alphaKeys);

            Shape(_coreTrail, new Vector3(0f, 0f, coreTrailDepth), coreTrailWidth, coreTrailTime);
            Shape(_rimTrailL, new Vector3(-rimTrailOffset.x, rimTrailOffset.y, 0f), rimTrailWidth, rimTrailTime);
            Shape(_rimTrailR, new Vector3( rimTrailOffset.x, rimTrailOffset.y, 0f), rimTrailWidth, rimTrailTime);

            GlowMaterial.SetColor(_coreMat, GlowMaterial.Scaled(trailColor, coreTrailIntensity));
            GlowMaterial.SetColor(_rimMat,  GlowMaterial.Scaled(trailColor, rimTrailIntensity));
        }

        void Shape(TrailRenderer tr, Vector3 localPos, float width, float time)
        {
            tr.transform.localPosition = localPos;
            tr.time              = time;
            tr.startWidth        = width * ShipSize;   // a world width; the rig's scale does not reach it
            tr.endWidth          = 0f;
            tr.minVertexDistance = trailVertexSpacing;
            tr.colorGradient     = _gradient;
        }

        // Everything about how the root's mesh draws, so Body is indistinguishable from it.
        static void CopyRenderer(MeshRenderer from, MeshRenderer to)
        {
            to.sharedMaterials            = from.sharedMaterials;
            to.shadowCastingMode          = from.shadowCastingMode;
            to.receiveShadows             = from.receiveShadows;
            to.lightProbeUsage            = from.lightProbeUsage;
            to.reflectionProbeUsage       = from.reflectionProbeUsage;
            to.probeAnchor                = from.probeAnchor;
            to.motionVectorGenerationMode = from.motionVectorGenerationMode;
            to.allowOcclusionWhenDynamic  = from.allowOcclusionWhenDynamic;
            to.renderingLayerMask         = from.renderingLayerMask;
            to.sortingLayerID             = from.sortingLayerID;
            to.sortingOrder               = from.sortingOrder;
            to.rendererPriority           = from.rendererPriority;
            to.enabled                    = from.enabled;
        }

        Transform NewNode(string nodeName, Transform parent)
        {
            var go = new GameObject(nodeName) { layer = gameObject.layer };
            go.transform.SetParent(parent, worldPositionStays: false);
            return go.transform;
        }

        // The root's direct child whose subtree holds t; null when t is the root itself or is not
        // under it at all.
        Transform BranchOf(Transform t)
        {
            while (t != null && t.parent != transform) t = t.parent;
            return t;
        }
    }
}
