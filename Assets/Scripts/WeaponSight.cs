using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// The laser's targeting beam, drawn on the ship between shots: a slim line of light from the
    /// muzzle straight down to the ground line, ending in a small soft dot where it meets the
    /// ground. It shows exactly where a shot would land, which is the whole of the aiming task.
    ///
    /// Alive, but quietly. A faint glint slides down it from the muzzle to the ground, about one a
    /// second, and the dot at its foot swells and brightens a little as each one lands: the weapon's
    /// charge running down to the spot a shot would hit. It runs on the wall clock and is recomputed
    /// every frame, so it moves the same at any frame rate — sliding at a high one, stepping at a low
    /// one, like the rain. Faint and slow because the participant's eye belongs on the UFO, whose
    /// motion is the stimulus; turn <see cref="glint"/> off and the sight holds still. Besides that it
    /// reacts to the weapon in two ways: it dims while firing is withheld (the reveal, between
    /// phases), and it flares on the muzzle flash, so the sight and the shot read as one weapon. The
    /// breathing is still here too, switched off.
    ///
    /// It says nothing about the stimulus. It knows the muzzle, the fire direction and the ground
    /// line — never the tower, a tower crossing or a frame-rate change; a sight that changed at the
    /// boundary would be a second, unmeasured cue to it. That is also why it ends on the ground line
    /// rather than on a raycast: a ray would stop on the hidden tower and so point it out.
    ///
    /// Light, not paint: every part is additive glow on the game's own shader
    /// (<see cref="GlowMaterial"/>), its edges soft falloff rather than the hard edge of a flat strip.
    /// With nothing in front of it any more its whole length is on show, and a soft-edged line of
    /// light sits in the scene where a hard-edged strip reads as drawn on top of it.
    ///
    /// In front of the towers: the beam runs down through the tower row, and the towers are solid
    /// models standing a few units deep on the play plane, dense enough that one stands under the ship
    /// almost wherever it flies. Drawn where it is, the beam would vanish into the first rooftop below
    /// it. So every point of it is drawn <see cref="drawInFront"/> nearer the camera, along the line of
    /// sight through it: it covers exactly the same pixels, only nearer, past the front of any tower.
    /// The foreground grass is far nearer still, so it keeps covering the beam's foot as before.
    ///
    /// Sorting: every renderer here sits at <see cref="sortingOrder"/> (−1), under the rest of the
    /// scene's transparents. Light added to light does not care about order; this only keeps any
    /// alpha-blended transparent (the grass) drawn over the sight rather than having the sight's light
    /// added on top of it.
    ///
    /// Built once in Awake, from <see cref="LaserFirer"/>, on the muzzle object: parented under the
    /// ship so UfoController.SetHidden takes it down with the hull. Nothing allocates per frame.
    /// </summary>
    public class WeaponSight : MonoBehaviour
    {
        [Header("Laser Sight")]
        [Tooltip("Draw the targeting beam.")]
        public bool showLaserSight = true;

        [Tooltip("Take the beam colour from the ship's livery (UfoController.liveryTint), so the " +
                 "hull, the sight and the shot are never three different teals. Uncheck to use the " +
                 "colour below.")]
        public bool matchLivery = true;
        public Color laserColor = new Color(0.35f, 1f, 0.85f, 1f);

        [Tooltip("HDR brightness of the beam's light. Around 1 the core just catches the scene's " +
                 "bloom, enough to read as light; it is on screen for the whole session, so keep it " +
                 "modest.")]
        [Min(0f)] public float intensity = 1.3f;

        [Tooltip("Width of the beam's core, world units — about two pixels at 1080p. Its light falls " +
                 "off linearly to the edges, so it reads as a hairline about half that wide; and a " +
                 "linear ramp two pixels wide lights the same total wherever it lands between pixels, " +
                 "so the line holds steady as the ship moves. Much narrower and it would shimmer.")]
        [Min(0.001f)] public float coreWidth = 0.05f;
        [Tooltip("Width of the soft halo around the core.")]
        [Min(0.001f)] public float haloWidth = 0.14f;
        [Range(0f, 1f)] public float coreAlpha = 0.85f;
        [Range(0f, 1f)] public float haloAlpha = 0.3f;
        [Tooltip("Brightness at the ground end relative to the muzzle end, so the beam eases off " +
                 "toward the ground rather than ending as a hard bar.")]
        [Range(0f, 1f)] public float groundFade = 0.55f;

        [Tooltip("How much nearer the camera the sight is drawn, world units, each point moved along the " +
                 "line of sight through it: the same pixels, but in front of the towers, whose models stand " +
                 "a few units deep on the play plane and would otherwise hide the beam under the first " +
                 "rooftop. The grass in front is 30 or more units nearer and still covers its foot. 0 draws " +
                 "it on the play plane.")]
        [Range(0f, 20f)] public float drawInFront = 10f;

        [Tooltip("A small soft glow where the beam meets the ground line: the laser's dot on the city.")]
        public bool groundDot = true;
        [Tooltip("Diameter of the ground dot, world units.")]
        [Min(0.01f)] public float dotSize = 0.45f;
        [Range(0f, 1f)] public float dotAlpha = 0.6f;

        [Tooltip("A bright dash sliding down the beam from the muzzle to the ground, over and over, on a " +
                 "wall clock that never reacts to the stimulus. Off, the sight holds still.")]
        public bool glint = true;
        [Tooltip("Seconds for the glint to travel the beam once.")]
        [Min(0.05f)] public float glintSweepSec = 1.2f;
        [Tooltip("Length of the glint as a fraction of the beam.")]
        [Range(0.01f, 0.5f)] public float glintLength = 0.14f;
        [Range(0f, 1f)] public float glintAlpha = 0.6f;
        [Tooltip("How much the ground dot brightens as each glint lands, as a fraction of its " +
                 "brightness; it swells by a third of that. 0 leaves the dot still.")]
        [Range(0f, 2f)] public float glintLanding = 0.8f;
        [Tooltip("Seconds from a glint landing to the dot at its brightest. It fades over a few times that.")]
        [Min(0.01f)] public float landingRiseSec = 0.1f;

        [Header("Shared")]
        [Tooltip("How much the pointer glow's pulse modulates the sight's brightness, 0–1. 0, the " +
                 "default, holds the sight still (and the pointer only pulses at all with " +
                 "LaserFirer.pulsePointer on).")]
        [Range(0f, 1f)] public float breathe = 0f;
        [Tooltip("How hard the sight flares on the muzzle flash, as a multiplier on its brightness " +
                 "and halo width. Follows LaserFirer.FireFlash01.")]
        [Min(0f)] public float fireFlare = 2.5f;
        [Tooltip("Brightness while firing is withheld — the reveal, between phases. The sight dims " +
                 "rather than vanishes: the weapon is still armed, just not yet.")]
        [Range(0f, 1f)] public float disarmedLevel = 0.4f;
        [Min(0.01f)] public float armFadeSec = 0.2f;
        [Tooltip("Sorting order for every renderer here. Negative, so the scene's other " +
                 "transparents draw over the sight rather than under its light.")]
        public int sortingOrder = -1;

        // ── Private ──────────────────────────────────────────────────────────

        // Falloff exponents for each part's glow material. 1 is a straight ramp — a tent, which at
        // about two pixels wide lights the same total wherever it falls between pixels; that is
        // what keeps the hairline core from shimmering as the ship moves. The halo and the dot pull
        // their light into the middle.
        const float CoreSoftness  = 1f;
        const float HaloSoftness  = 1.5f;
        const float GlintSoftness = 1f;
        const float DotSoftness   = 2f;

        // Points along the glint. A line takes its colour from its gradient at its points only, and the
        // glint's is clear at both ends and bright in the middle: drawn with just its two end points it
        // would be clear all the way along.
        const int GlintPoints = 5;

        LaserFirer    _laser;
        UfoController _ufo;
        TowerManager  _tower;
        Camera        _cam;

        LineRenderer _beamCore, _beamHalo, _glint, _dot;
        Material     _coreMat,  _haloMat,  _glintMat, _dotMat;

        // Scratch for the vertex gradients, set once at start.
        Gradient           _gradient;
        GradientColorKey[] _colorKeys2;
        GradientAlphaKey[] _alphaKeys2, _alphaKeys3;

        bool  _shownBeam, _shownGlint, _shownDot;
        bool  _shownOnce;
        float _level = 1f;

        // The glow shader multiplies vertex colour by this tint, so one colour write per frame
        // modulates a whole line — brightness and alpha — with its gradient left alone.
        static void Tint(Material m, Color c, float alpha, float brightness) =>
            GlowMaterial.SetColor(m, new Color(c.r * brightness, c.g * brightness, c.b * brightness, alpha));

        void Awake()
        {
            _laser = GetComponent<LaserFirer>();
            _ufo   = GetComponentInParent<UfoController>();
            _tower = FindAnyObjectByType<TowerManager>();

            _gradient   = new Gradient();
            _colorKeys2 = new GradientColorKey[2];
            _alphaKeys2 = new GradientAlphaKey[2];
            _alphaKeys3 = new GradientAlphaKey[3];

            _beamCore = MakeLine("SightBeamCore",  GlowShape.Line, CoreSoftness,  coreWidth,        out _coreMat);
            _beamHalo = MakeLine("SightBeamHalo",  GlowShape.Line, HaloSoftness,  haloWidth,        out _haloMat);
            _glint    = MakeLine("SightBeamGlint", GlowShape.Line, GlintSoftness, coreWidth * 1.6f, out _glintMat);
            _glint.positionCount = GlintPoints;

            // The ground dot is a line too: two points exactly as far apart as the line is wide,
            // which the glow shader's Dot shape turns into a round glow facing the camera. A quad
            // would need a MeshRenderer under the muzzle, where the ship's scale chain would skew
            // it and UfoController's livery — which repaints every MeshRenderer on the ship through
            // a property block — would pin its colour. A world-space line has neither problem and
            // is still a child of the muzzle, so it hides with the ship.
            _dot      = MakeLine("SightGroundDot", GlowShape.Dot,  DotSoftness,   dotSize,          out _dotMat);

            ApplyGradients();
        }

        // Not Awake: the livery colour is on a component that may not have run its own Awake yet,
        // and nor may the camera rig.
        void Start()
        {
            if (_laser == null) _laser = GetComponent<LaserFirer>();
            if (matchLivery && _ufo != null) laserColor = _ufo.liveryTint;
            _cam = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
        }

        void OnDestroy()
        {
            Destroy(_coreMat);
            Destroy(_haloMat);
            Destroy(_glintMat);
            Destroy(_dotMat);
        }

        // ── Per frame ────────────────────────────────────────────────────────

        // LateUpdate: after UfoController has moved the ship and LaserFirer has published this
        // frame's glow and flash, so the sight is drawn from this frame's muzzle position.
        void LateUpdate()
        {
            if (_laser == null) return;

            // Under the reset veil the hull's renderers are switched off by UfoController and put
            // back exactly as they were. Touching them here would show the sight over an empty sky.
            if (_ufo != null && _ufo.Hidden) return;

            Show();

            float target = _laser.FiringEnabled ? 1f : disarmedLevel;
            _level = Mathf.MoveTowards(_level, target,
                                       Time.unscaledDeltaTime / Mathf.Max(0.01f, armFadeSec));

            if (_shownBeam) DrawBeam(_laser.PointerPulse01, _laser.FireFlash01);
        }

        // Cheap enough to check every frame, which is what lets the Inspector toggles work live; the
        // enabled flags are only written on a change.
        void Show()
        {
            bool beam      = showLaserSight;
            bool showGlint = beam && glint;
            bool showDot   = beam && groundDot;
            if (_shownOnce && _shownBeam == beam && _shownGlint == showGlint && _shownDot == showDot)
                return;

            _beamCore.enabled = beam;
            _beamHalo.enabled = beam;
            _glint.enabled    = showGlint;
            _dot.enabled      = showDot;

            if (!_shownOnce) _level = _laser.FiringEnabled ? 1f : disarmedLevel;

            _shownOnce  = true;
            _shownBeam  = beam;
            _shownGlint = showGlint;
            _shownDot   = showDot;
        }

        Vector3 Muzzle => _laser.firePoint != null ? _laser.firePoint.position : transform.position;

        /// <summary>
        /// Muzzle to ground line along the fire direction. It ends on the tower's ground line rather
        /// than on a raycast — the shot does the raycast, and the sight only has to say where the
        /// shot is going, which is the same X on the same ground.
        /// </summary>
        void DrawBeam(float pulse, float flash)
        {
            Vector3 origin = Muzzle;
            Vector3 dir    = _laser.fireDirection.sqrMagnitude > 1e-6f
                ? _laser.fireDirection.normalized
                : Vector3.down;

            float groundY = _tower != null ? _tower.groundY : origin.y - _laser.beamLength;
            float length  = dir.y < -1e-4f ? (groundY - origin.y) / dir.y : _laser.beamLength;
            length        = Mathf.Max(0.05f, length);
            Vector3 end   = origin + dir * length;

            // Where it is drawn: nearer the camera, on the same pixels (see the class summary). The
            // widths shrink by the same fraction as the distance, so they keep their size on screen.
            Vector3 o = Pull(origin, out float ko);
            Vector3 e = Pull(end,    out float ke);
            float   k = 0.5f * (ko + ke);

            _beamCore.SetPosition(0, o); _beamCore.SetPosition(1, e);
            _beamHalo.SetPosition(0, o); _beamHalo.SetPosition(1, e);

            // Still unless breathe is turned up; flares with the muzzle flash.
            float breath = 1f - breathe + breathe * pulse;
            float flare  = 1f + fireFlare * flash;
            float warm   = 1f + 0.5f * flash;
            float a      = _level * breath;

            _beamHalo.widthMultiplier = haloWidth * flare * k;
            _beamCore.widthMultiplier = coreWidth * warm * k;

            Tint(_coreMat, laserColor, coreAlpha * a, intensity * flare);
            Tint(_haloMat, laserColor, haloAlpha * a, intensity * warm);

            // The glint's place along its sweep, from the wall clock. Its centre runs from a little
            // above the muzzle to a little past the ground, so the dash enters the beam whole rather
            // than popping in at full length, and at the end drains into the dot.
            float p    = Mathf.Repeat(Time.unscaledTime / glintSweepSec, 1f);
            float half = glintLength * 0.5f;
            float c    = Mathf.Lerp(-half, 1f + half, p);

            // The dot's swell as each glint lands: a soft pulse from the moment the dash's centre
            // reaches the ground, at its height landingRiseSec later and all but gone by the next.
            float landing = 0f;
            if (_shownGlint && glintLanding > 0f)
            {
                float landed = (1f + half) / (1f + glintLength);   // where c reaches 1
                float x      = Mathf.Repeat(p - landed, 1f) * glintSweepSec / landingRiseSec;
                landing      = glintLanding * x * Mathf.Exp(1f - x);
            }

            if (_shownDot)
            {
                float size = dotSize * (1f + landing / 3f) * ke;
                float r    = size * 0.5f;
                _dot.SetPosition(0, new Vector3(e.x - r, e.y, e.z));
                _dot.SetPosition(1, new Vector3(e.x + r, e.y, e.z));
                _dot.widthMultiplier = size;
                Tint(_dotMat, laserColor, dotAlpha * a, intensity * warm * (1f + landing));
            }

            if (!_shownGlint) return;

            float t0 = Mathf.Clamp01(c - half);
            float t1 = Mathf.Clamp01(c + half);

            for (int i = 0; i < GlintPoints; i++)
                _glint.SetPosition(i, Vector3.LerpUnclamped(o, e, Mathf.Lerp(t0, t1, i / (GlintPoints - 1f))));
            _glint.widthMultiplier = coreWidth * 1.6f * k;

            // Eases in at the muzzle; at the ground the dot swallows it rather than it fading out.
            float ease = Mathf.SmoothStep(0f, 1f, p / 0.2f);
            Tint(_glintMat, laserColor, glintAlpha * a * ease, intensity * (1f + 0.6f * flash));
        }

        // p moved drawInFront nearer the camera along the line of sight through it, so it lands on the same
        // pixel. k is the fraction of its distance from the camera that is left.
        Vector3 Pull(Vector3 p, out float k)
        {
            k = 1f;
            if (_cam == null || drawInFront <= 0f) return p;
            Transform c = _cam.transform;
            Vector3   v = p - c.position;
            float     d = Vector3.Dot(v, c.forward);
            if (d <= 2f * drawInFront) return p;   // too near the camera to move that far
            k = 1f - drawInFront / d;
            return c.position + v * k;
        }

        // ── Construction ─────────────────────────────────────────────────────

        LineRenderer MakeLine(string goName, GlowShape shape, float softness, float width, out Material mat)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace      = true;
            lr.positionCount      = 2;
            lr.widthMultiplier    = width;
            // Square ends: the muzzle end sits under the pointer's glow and the ground end under the
            // dot. Stretch maps U along the line and V across it, which is what the glow shader's
            // Line and Dot shapes read.
            lr.numCapVertices     = 0;
            lr.numCornerVertices  = 0;
            lr.alignment          = LineAlignment.View;
            lr.textureMode        = LineTextureMode.Stretch;
            lr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows     = false;
            lr.lightProbeUsage    = UnityEngine.Rendering.LightProbeUsage.Off;
            lr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            lr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            lr.sortingOrder       = sortingOrder;

            // Additive light, multiplied by vertex colour and by the _Color tint that Tint() drives
            // per frame.
            mat = GlowMaterial.Create(shape, Color.white, softness, goName);
            lr.sharedMaterial = mat;
            lr.enabled        = false;
            return lr;
        }

        // The vertex gradients carry the SHAPE of each line's colour — where it is bright, where it
        // dissolves — and never change per frame; brightness and alpha ride the tint instead.
        void ApplyGradients()
        {
            // Beam: full at the muzzle, easing off toward the ground. White, so the tint alone
            // decides the hue — which is what lets the flare push it past white. The dot is even.
            SetGradient(_beamCore, 1f, groundFade);
            SetGradient(_beamHalo, 1f, groundFade);
            SetGradient(_dot,      1f, 1f);

            // Glint: a dash that is bright in the middle and soft at both ends.
            _colorKeys2[0] = new GradientColorKey(Color.white, 0f);
            _colorKeys2[1] = new GradientColorKey(Color.white, 1f);
            _alphaKeys3[0] = new GradientAlphaKey(0f, 0f);
            _alphaKeys3[1] = new GradientAlphaKey(1f, 0.5f);
            _alphaKeys3[2] = new GradientAlphaKey(0f, 1f);
            _gradient.SetKeys(_colorKeys2, _alphaKeys3);
            _glint.colorGradient = _gradient;
        }

        void SetGradient(LineRenderer lr, float a0, float a1)
        {
            _colorKeys2[0] = new GradientColorKey(Color.white, 0f);
            _colorKeys2[1] = new GradientColorKey(Color.white, 1f);
            _alphaKeys2[0] = new GradientAlphaKey(a0, 0f);
            _alphaKeys2[1] = new GradientAlphaKey(a1, 1f);
            _gradient.SetKeys(_colorKeys2, _alphaKeys2);
            lr.colorGradient = _gradient;
        }
    }
}
