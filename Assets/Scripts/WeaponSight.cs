using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// Draws the UFO's weapon sight:
    /// - On laser blocks (<c>manual</c>, <c>scroll</c>, <c>auto</c>): a vertical targeting line
    ///   from the UFO's belly turret down to the ground, with a soft halo, a travelling glint,
    ///   and a crisp ground dot.
    /// - On <c>shockwave</c> blocks: <b>no vertical laser reticle or ground dot</b>. Instead, a
    ///   subtle purple under-hull charge arc and ripple cradle the turret, dropping out during
    ///   the shockwave discharge handoff when the player fires.
    ///
    /// Built once in <see cref="Awake"/>; nothing allocates per frame, and every pulse runs on
    /// <see cref="Time.unscaledTime"/> so nothing reacts to the frame-rate stimulus.
    /// </summary>
    public class WeaponSight : MonoBehaviour
    {
        [Header("References  (auto-resolved if left empty)")]
        [Tooltip("Where the sight emanates from. Defaults to LaserFirer.firePoint on this object.")]
        public Transform firePoint;
        public PerturbationController perturbation;

        [Header("Laser Sight  (vertical targeting line on manual/scroll/auto)")]
        [Tooltip("Match the UFO's livery tint automatically.")]
        public bool matchUfoLivery = true;
        public Color laserColor = new Color(0.35f, 1f, 0.85f, 1f);

        [Tooltip("World-space width of the crisp inner line.")]
        public float beamCoreWidth = 0.035f;
        [Tooltip("World-space width of the soft outer halo.")]
        public float beamHaloWidth = 0.22f;

        [Range(0f, 1f)] public float beamAlpha     = 0.50f;
        [Range(0f, 1f)] public float beamHaloAlpha = 0.14f;

        [Tooltip("HDR multiplier on the core line (< 1 stays below the scene's bloom threshold).")]
        public float beamIntensity = 0.95f;

        [Tooltip("Diameter of the ground aim dot (world units).")]
        public float groundDotSize = 0.42f;
        [Range(0f, 1f)] public float groundDotAlpha = 0.70f;

        [Header("Laser Glint  (travels down the beam)")]
        [Tooltip("Length of the bright bead, as a fraction of the beam.")]
        [Range(0.02f, 0.4f)] public float glintLength = 0.11f;
        [Tooltip("Trips down the beam per second.")]
        public float glintRate = 0.65f;
        [Range(0f, 1f)] public float glintAlpha = 0.38f;

        [Header("Shockwave Charge Sight  (curved arc under the hull on shockwave blocks)")]
        public Color chargeColor = new Color(0.22f, 0.03f, 0.98f, 1f);
        [Tooltip("Radius of the crescent, world units (~half the saucer's width).")]
        public float arcRadius = 0.68f;
        [Tooltip("Angular span of the crescent in degrees (180 = full lower semicircle).")]
        [Range(60f, 220f)] public float arcSpanDeg = 150f;
        [Tooltip("Vertical offset from the muzzle so the crescent hugs the belly.")]
        public float arcYOffset = 0.08f;
        [Tooltip("World-space thickness of the primary crescent.")]
        public float arcWidth = 0.07f;
        [Range(0f, 1f)] public float arcAlpha = 0.55f;
        public float arcIntensity = 1.05f;

        [Tooltip("How much further out the faint echo ripple expands before vanishing, as a multiple of arcRadius.")]
        [Range(1.05f, 2f)] public float rippleExpand = 1.45f;
        [Tooltip("Echo ripples per second.")]
        public float rippleRate = 0.55f;
        [Range(0f, 1f)] public float rippleAlpha = 0.28f;

        [Tooltip("How long the charge sight drops out after firing so the shockwave blast owns the muzzle, " +
                 "then fades back in over the second half of this window.")]
        public float blastHandoffSec = 0.72f;

        [Header("Idle Breathing  (wall-clock only — never driven by frame rate)")]
        public float pulseHz = 1.6f;
        [Range(0f, 0.5f)] public float pulseDepth = 0.12f;

        const int ArcSegments = 40;

        UfoController _ufo;
        LaserFirer    _firer;

        // Laser sight primitives
        LineRenderer _beamCore;
        LineRenderer _beamHalo;
        LineRenderer _glint;
        Transform    _dot;
        Renderer     _dotRenderer;
        Material     _beamCoreMat;
        Material     _beamHaloMat;
        Material     _glintMat;
        Material     _dotMat;

        // Shockwave charge sight primitives
        LineRenderer      _arc;
        LineRenderer      _ripple;
        Material          _arcMat;
        Material          _rippleMat;
        readonly Vector3[] _arcPts = new Vector3[ArcSegments + 1];

        bool  _laserShown;
        bool  _chargeShown;
        float _firedAt = -999f;

        bool IsShockwave =>
            (perturbation != null && perturbation.CurrentConfig != null && perturbation.CurrentConfig.IsShockwave) ||
            (ExperimentDirector.Instance != null && ExperimentDirector.Instance.Config != null &&
             ExperimentDirector.Instance.Config.IsShockwave);

        void Awake()
        {
            _ufo   = GetComponentInParent<UfoController>();
            _firer = GetComponent<LaserFirer>();
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();

            BuildLaserPrimitives();
            BuildChargePrimitives();
            HideAll();
        }

        void Start()
        {
            if (firePoint == null && _firer != null) firePoint = _firer.firePoint;
            if (firePoint == null)                   firePoint = transform;

            if (matchUfoLivery && _ufo != null && _ufo.tintHull)
                laserColor = _ufo.liveryTint;
        }

        public void SyncLivery(Color color)
        {
            if (matchUfoLivery) laserColor = color;
        }

        /// <summary>
        /// Called by <see cref="LaserFirer"/> when the shockwave cannon fires so the under-hull
        /// charge arc yields to the expanding shockwave blast and eases back in afterwards.
        /// </summary>
        public void NotifyFired() => _firedAt = Time.unscaledTime;

        void OnDisable() => HideAll();

        void OnDestroy()
        {
            if (_beamCoreMat != null) Destroy(_beamCoreMat);
            if (_beamHaloMat != null) Destroy(_beamHaloMat);
            if (_glintMat    != null) Destroy(_glintMat);
            if (_dotMat      != null) Destroy(_dotMat);
            if (_arcMat      != null) Destroy(_arcMat);
            if (_rippleMat   != null) Destroy(_rippleMat);
            if (_dot         != null) Destroy(_dot.gameObject);
        }

        // ── Construction ─────────────────────────────────────────────────────

        void BuildLaserPrimitives()
        {
            _beamCoreMat = GlowMaterial.Create(GlowShape.Line, laserColor, 2.5f, "SightBeamCore");
            _beamHaloMat = GlowMaterial.Create(GlowShape.Line, laserColor, 1.3f, "SightBeamHalo");
            _glintMat    = GlowMaterial.Create(GlowShape.Line, Color.white, 2.2f, "SightGlint");
            _dotMat      = GlowMaterial.Create(GlowShape.Dot,  laserColor, 2.0f, "SightGroundDot");

            _beamHalo = MakeLine("SightBeamHalo", _beamHaloMat, 2);
            _beamCore = MakeLine("SightBeamCore", _beamCoreMat, 2);
            _glint    = MakeLine("SightGlint",    _glintMat,    2);
            TaperEnds(_glint);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "SightGroundDot";
            var col = quad.GetComponent<Collider>();
            if (col != null) Destroy(col);

            _dot = quad.transform;
            _dotRenderer = quad.GetComponent<Renderer>();
            _dotRenderer.sharedMaterial            = _dotMat;
            _dotRenderer.shadowCastingMode         = ShadowCastingMode.Off;
            _dotRenderer.receiveShadows            = false;
            _dotRenderer.lightProbeUsage           = LightProbeUsage.Off;
            _dotRenderer.reflectionProbeUsage      = ReflectionProbeUsage.Off;
            _dotRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        void BuildChargePrimitives()
        {
            _arcMat    = GlowMaterial.Create(GlowShape.Line, chargeColor, 2.0f, "SightChargeArc");
            _rippleMat = GlowMaterial.Create(GlowShape.Line, chargeColor, 1.5f, "SightChargeRipple");

            _arc    = MakeLine("SightChargeArc",    _arcMat,    ArcSegments + 1);
            _ripple = MakeLine("SightChargeRipple", _rippleMat, ArcSegments + 1);
            TaperEnds(_arc);
            TaperEnds(_ripple);
        }

        LineRenderer MakeLine(string goName, Material mat, int positions)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, false);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace              = true;
            lr.alignment                  = LineAlignment.View;
            lr.textureMode                = LineTextureMode.Stretch;
            lr.numCapVertices             = 4;
            lr.numCornerVertices          = 2;
            lr.positionCount              = positions;
            lr.shadowCastingMode          = ShadowCastingMode.Off;
            lr.receiveShadows             = false;
            lr.lightProbeUsage            = LightProbeUsage.Off;
            lr.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            lr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            lr.sharedMaterial             = mat;
            return lr;
        }

        static void TaperEnds(LineRenderer lr)
        {
            lr.widthCurve = new AnimationCurve(
                new Keyframe(0.00f, 0.0f),
                new Keyframe(0.18f, 1.0f),
                new Keyframe(0.50f, 1.0f),
                new Keyframe(0.82f, 1.0f),
                new Keyframe(1.00f, 0.0f));
        }

        // ── Per-frame update ─────────────────────────────────────────────────

        void LateUpdate()
        {
            if (_ufo != null && _ufo.Hidden)
            {
                HideAll();
                return;
            }

            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();

            Transform fp = firePoint != null ? firePoint : transform;
            Vector3 origin = fp.position;
            float t     = Time.unscaledTime;
            float pulse = 1f + pulseDepth * Mathf.Sin(t * pulseHz * Mathf.PI * 2f);

            if (IsShockwave)
            {
                SetLaserVisible(false);
                SetChargeVisible(true);
                DrawCharge(origin, t, pulse);
            }
            else
            {
                SetChargeVisible(false);
                SetLaserVisible(true);
                DrawLaser(origin, t, pulse);
            }
        }

        void SetLaserVisible(bool on)
        {
            // Read from the renderers, not remembered: UfoController.SetHidden puts them back as they were when the ship
            // was hidden, which can be a sight from the block before.
            if (_beamCore.enabled == on && _glint.enabled == on) return;
            _laserShown        = on;
            _beamCore.enabled  = on;
            _beamHalo.enabled  = on;
            _glint.enabled     = on;
            if (_dotRenderer != null) _dotRenderer.enabled = on;
        }

        void SetChargeVisible(bool on)
        {
            if (_arc.enabled == on && _ripple.enabled == on) return;
            _chargeShown    = on;
            _arc.enabled    = on;
            _ripple.enabled = on;
        }

        void HideAll()
        {
            _laserShown  = false;
            _chargeShown = false;
            if (_beamCore    != null) _beamCore.enabled    = false;
            if (_beamHalo    != null) _beamHalo.enabled    = false;
            if (_glint       != null) _glint.enabled       = false;
            if (_dotRenderer != null) _dotRenderer.enabled = false;
            if (_arc         != null) _arc.enabled         = false;
            if (_ripple      != null) _ripple.enabled      = false;
        }

        void DrawLaser(Vector3 origin, float t, float pulse)
        {
            float groundY = _firer != null ? _firer.groundY : 0f;
            var   ground  = new Vector3(origin.x, groundY, origin.z);

            _beamCore.widthMultiplier = beamCoreWidth;
            _beamCore.SetPosition(0, origin);
            _beamCore.SetPosition(1, ground);

            _beamHalo.widthMultiplier = beamHaloWidth;
            _beamHalo.SetPosition(0, origin);
            _beamHalo.SetPosition(1, ground);

            Color core = GlowMaterial.Scaled(laserColor, beamIntensity, beamAlpha * pulse);
            Color halo = GlowMaterial.Scaled(laserColor, 1f,            beamHaloAlpha * pulse);
            GlowMaterial.SetColor(_beamCoreMat, core);
            GlowMaterial.SetColor(_beamHaloMat, halo);

            float phase = Mathf.Repeat(t * glintRate, 1f);
            float half  = glintLength * 0.5f;
            float a0    = Mathf.Clamp01(phase - half);
            float a1    = Mathf.Clamp01(phase + half);
            float env   = Mathf.Sin(phase * Mathf.PI);

            _glint.widthMultiplier = beamCoreWidth * 2.2f;
            _glint.SetPosition(0, Vector3.Lerp(origin, ground, a0));
            _glint.SetPosition(1, Vector3.Lerp(origin, ground, a1));

            Color glintTint = Color.Lerp(laserColor, Color.white, 0.55f);
            GlowMaterial.SetColor(_glintMat,
                GlowMaterial.Scaled(glintTint, beamIntensity * 1.15f, glintAlpha * env));

            if (_dot != null)
            {
                Camera cam = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
                Vector3 toCam = cam != null
                    ? (cam.transform.position - ground).normalized
                    : Vector3.back;

                _dot.position = ground + toCam * 0.08f;
                if (cam != null)
                    _dot.rotation = Quaternion.LookRotation(-toCam, cam.transform.up);

                float s = groundDotSize * (0.94f + 0.06f * pulse);
                _dot.localScale = new Vector3(s, s, 1f);

                GlowMaterial.SetColor(_dotMat,
                    GlowMaterial.Scaled(laserColor, beamIntensity * 1.05f, groundDotAlpha * pulse));
            }
        }

        void DrawCharge(Vector3 origin, float t, float pulse)
        {
            float sinceShot = t - _firedAt;
            float handoff = blastHandoffSec <= 0f ? 1f
                : Mathf.SmoothStep(0f, 1f, (sinceShot - blastHandoffSec * 0.45f) / (blastHandoffSec * 0.55f));

            Vector3 center = origin + new Vector3(0f, arcYOffset, 0f);

            WriteArc(_arc, center, arcRadius);
            _arc.widthMultiplier = arcWidth;
            GlowMaterial.SetColor(_arcMat,
                GlowMaterial.Scaled(chargeColor, arcIntensity, arcAlpha * pulse * handoff));

            float rPhase = Mathf.Repeat(t * rippleRate, 1f);
            float rRad   = arcRadius * Mathf.Lerp(1.02f, rippleExpand, rPhase);
            float rEnv   = Mathf.Sin(rPhase * Mathf.PI) * (1f - rPhase * 0.35f);

            WriteArc(_ripple, center, rRad);
            _ripple.widthMultiplier = arcWidth * Mathf.Lerp(0.85f, 0.35f, rPhase);
            GlowMaterial.SetColor(_rippleMat,
                GlowMaterial.Scaled(chargeColor, 1f, rippleAlpha * rEnv * handoff));
        }

        void WriteArc(LineRenderer lr, Vector3 center, float radius)
        {
            CameraRig rig   = CameraRig.Instance;
            Vector3   right = rig != null ? rig.Right : Vector3.right;
            Vector3   up    = Vector3.up;

            float halfRad = arcSpanDeg * 0.5f * Mathf.Deg2Rad;
            for (int i = 0; i <= ArcSegments; i++)
            {
                float u = (float)i / ArcSegments;
                float a = Mathf.Lerp(-halfRad, halfRad, u);
                _arcPts[i] = center + (right * Mathf.Sin(a) - up * Mathf.Cos(a)) * radius;
            }
            lr.SetPositions(_arcPts);
        }
    }
}
