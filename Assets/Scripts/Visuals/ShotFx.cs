using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// Draws the UFO's weapon fire:
    /// - <see cref="Play"/> (Laser blocks: <c>manual</c>, <c>scroll</c>, <c>auto</c>): a two-layer
    ///   additive beam from the muzzle straight down to the ground, a muzzle flash, a ground
    ///   scorch + expanding shock ring, and a burst of hot sparks and rising embers at the impact X.
    /// - <see cref="PlayShockwave"/> (Shockwave blocks: <c>shockwave</c>): a pressure wave blasting out of the UFO in a
    ///   full circle: a crisp white-hot front with violet light falling away behind it, a slower, softer second front, a
    ///   flash at the ship, a ripple close around it, and, where the wave meets the ground, two bright fronts racing apart
    ///   along the city's foot, kicking up sparks and embers as they pass. A press that lands nothing (too early, too
    ///   late) only fizzles at the muzzle.
    ///
    /// Built once in <see cref="Awake"/>; nothing allocates per frame or per shot, and all timing
    /// runs on <see cref="Time.unscaledDeltaTime"/> so the blast looks identical at any frame rate.
    /// </summary>
    public class ShotFx : MonoBehaviour
    {
        [Header("Palette")]
        [Tooltip("Take the laser colour from UfoController.liveryTint on the parent UFO.")]
        public bool matchUfoLivery = true;
        public Color laserColor = new Color(0.35f, 1f, 0.85f, 1f);
        [Tooltip("Warm secondary tint for the ground scorch and rising embers on laser shots.")]
        public Color scorchColor = new Color(1.00f, 0.58f, 0.22f, 1f);
        [Tooltip("Primary electric-violet tint for the shockwave cannon discharge.")]
        public Color shockwaveColor = new Color(0.22f, 0.03f, 0.98f, 1f);
        [Tooltip("Secondary indigo-violet tint for the shockwave trailing wake and skyline embers.")]
        public Color shockwaveWakeColor = new Color(0.14f, 0.01f, 0.78f, 1f);

        [Header("Beam  (world units / seconds)")]
        public float beamCoreWidth = 0.26f;
        public float beamGlowWidth = 1.35f;
        public float beamDuration  = 0.28f;
        [Tooltip("Peak HDR multiplier on the inner core (>1 drives URP Bloom).")]
        public float coreIntensity = 4.2f;
        [Tooltip("Peak HDR multiplier on the outer halo.")]
        public float glowIntensity = 2.0f;

        [Header("Impact Ring & Flashes")]
        public float muzzleFlashSize = 1.6f;
        public float impactFlashSize = 3.2f;
        public float ringStartSize   = 0.7f;
        public float ringEndSize     = 6.8f;
        public float ringDuration    = 0.48f;

        [Header("Shockwave Discharge  (full-screen radial wave)")]
        [Tooltip("Duration of the expanding shockwave wavefront across the city (seconds).")]
        public float shockwaveDuration = 0.85f;
        [Tooltip("Outer radius the shockwave wavefront reaches (world units; ~58 spans the full 16:9 view).")]
        public float shockwaveMaxRadius = 58f;

        [Header("Sparks & Embers")]
        [Range(4, 32)] public int sparkCount = 32;
        [Range(4, 32)] public int emberCount = 28;
        public float sparkSpeed    = 22f;
        public float sparkGravity  = 30f;
        public float sparkLifetime = 0.55f;
        public float emberLifetime = 0.95f;

        [Header("Point Light")]
        public bool  flashLight     = true;
        public float lightIntensity = 6f;
        public float lightRange     = 14f;

        const int MaxSparks      = 32;
        const int MaxEmbers      = 32;
        const float SwEase       = 2.35f;   // the wave's radius: Rmax*(1 - (1 - t)^SwEase)

        UfoController _ufo;

        // Beam (two additive LineRenderers: crisp core + wide halo)
        LineRenderer _coreLine;
        LineRenderer _glowLine;
        Material     _coreMat;
        Material     _glowMat;

        // Billboards (camera-facing Quads with procedural radial textures)
        Transform _muzzleFlash;
        Transform _impactFlash;
        Transform _scorchFlash;
        Transform _ring;
        Transform _swEchoRing;
        Renderer  _muzzleR, _impactR, _scorchR, _ringR, _swEchoRingR;
        Material  _muzzleMat, _impactMat, _scorchMat, _ringMat, _swEchoRingMat;

        // The shockwave: wave fronts on the Wave shape, a flash, and the fronts along the ground.
        Transform _swFront, _swWake, _swFlash, _swWash, _swSkimL, _swSkimR;
        Renderer  _swFrontR, _swWakeR, _swFlashR, _swWashR, _swSkimLR, _swSkimRR;
        Material  _swFrontMat, _swWakeMat, _swFlashMat, _swWashMat, _swSkimMat;
        bool      _swFull;

        // Sparks (short velocity-aligned LineRenderers)
        LineRenderer[] _sparks;
        Material[]     _sparkMats;
        Vector3[]      _sparkPos;
        Vector3[]      _sparkVel;
        float[]        _sparkDelay;
        float[]        _sparkLife;
        bool[]         _sparkIsCore;

        // Embers (small rising glowing dots)
        Transform[] _embers;
        Renderer[]  _emberR;
        Material[]  _emberMats;
        Vector3[]   _emberPos;
        Vector3[]   _emberVel;
        float[]     _emberDelay;
        float[]     _emberLife;
        float[]     _emberSize;
        float[]     _emberPhase;

        Light _light;

        bool    _playing;
        bool    _shockwaveMode;
        float   _elapsed;
        float   _totalDuration;
        Vector3 _muzzlePos;
        Vector3 _impactPos;
        float   _groundY;

        readonly System.Random _rng = new System.Random(7331);

        void Awake()
        {
            _ufo = GetComponentInParent<UfoController>();
            BuildMaterialsAndMeshes();
            SetAllEnabled(false);
        }

        void Start()
        {
            if (matchUfoLivery && _ufo != null && _ufo.tintHull)
                laserColor = _ufo.liveryTint;
        }

        public void SyncLivery(Color color)
        {
            if (matchUfoLivery) laserColor = color;
        }

        void OnDestroy()
        {
            DestroyObj(_coreLine    != null ? _coreLine.gameObject    : null);
            DestroyObj(_glowLine    != null ? _glowLine.gameObject    : null);
            DestroyObj(_muzzleFlash != null ? _muzzleFlash.gameObject : null);
            DestroyObj(_impactFlash != null ? _impactFlash.gameObject : null);
            DestroyObj(_scorchFlash != null ? _scorchFlash.gameObject : null);
            DestroyObj(_ring        != null ? _ring.gameObject        : null);
            DestroyObj(_swEchoRing  != null ? _swEchoRing.gameObject  : null);
            DestroyObj(_light       != null ? _light.gameObject       : null);

            foreach (Transform t in new[] { _swFront, _swWake, _swFlash, _swWash, _swSkimL, _swSkimR })
                if (t != null) Destroy(t.gameObject);

            if (_sparks != null)
                for (int i = 0; i < _sparks.Length; i++)
                    if (_sparks[i] != null) Destroy(_sparks[i].gameObject);

            if (_embers != null)
                for (int i = 0; i < _embers.Length; i++)
                    if (_embers[i] != null) Destroy(_embers[i].gameObject);

            DestroyObj(_coreMat);
            DestroyObj(_glowMat);
            DestroyObj(_swFrontMat);
            DestroyObj(_swWakeMat);
            DestroyObj(_swFlashMat);
            DestroyObj(_swWashMat);
            DestroyObj(_swSkimMat);
            DestroyObj(_muzzleMat);
            DestroyObj(_impactMat);
            DestroyObj(_scorchMat);
            DestroyObj(_ringMat);
            DestroyObj(_swEchoRingMat);

            if (_sparkMats != null)
                for (int i = 0; i < _sparkMats.Length; i++) DestroyObj(_sparkMats[i]);
            if (_emberMats != null)
                for (int i = 0; i < _emberMats.Length; i++) DestroyObj(_emberMats[i]);
        }

        static void DestroyObj(Object o) { if (o != null) Destroy(o); }

        // ── Construction ─────────────────────────────────────────────────────

        void BuildMaterialsAndMeshes()
        {
            _coreMat       = GlowMaterial.Create(GlowShape.Line, Color.white,        3.0f, "ShotBeamCore");
            _glowMat       = GlowMaterial.Create(GlowShape.Line, laserColor,         1.4f, "ShotBeamGlow");
            _muzzleMat     = GlowMaterial.Create(GlowShape.Dot,  Color.white,        2.2f, "ShotMuzzleFlash");
            _impactMat     = GlowMaterial.Create(GlowShape.Dot,  laserColor,         2.0f, "ShotImpactFlash");
            _scorchMat     = GlowMaterial.Create(GlowShape.Dot,  scorchColor,        1.5f, "ShotScorch");
            _ringMat       = GlowMaterial.Create(GlowShape.Ring, laserColor,         2.5f, "ShotRing");
            _swEchoRingMat = GlowMaterial.Create(GlowShape.Wave, shockwaveWakeColor, 1.6f, "ShotSwRipple");
            _swFrontMat    = GlowMaterial.Create(GlowShape.Wave, Color.white,        2.4f, "ShotSwFront");
            _swWakeMat     = GlowMaterial.Create(GlowShape.Wave, shockwaveWakeColor, 2.2f, "ShotSwWake");
            _swFlashMat    = GlowMaterial.Create(GlowShape.Dot,  shockwaveColor,     2.0f, "ShotSwFlash");
            _swWashMat     = GlowMaterial.Create(GlowShape.Dot,  shockwaveColor,     1.5f, "ShotSwWash");
            _swSkimMat     = GlowMaterial.Create(GlowShape.Dot,  Color.white,        1.3f, "ShotSwSkim");

            _glowLine = MakeLine("ShotBeamGlow", _glowMat, 2);
            _coreLine = MakeLine("ShotBeamCore", _coreMat, 2);

            _muzzleFlash = MakeQuad("ShotMuzzleFlash", _muzzleMat, out _muzzleR);
            _impactFlash = MakeQuad("ShotImpactFlash", _impactMat, out _impactR);
            _scorchFlash = MakeQuad("ShotScorch",      _scorchMat, out _scorchR);
            _ring        = MakeQuad("ShotRing",        _ringMat,   out _ringR);
            _swEchoRing  = MakeQuad("ShotSwEchoRing",  _swEchoRingMat, out _swEchoRingR);

            _swWake  = MakeQuad("ShotSwWake",  _swWakeMat,  out _swWakeR);
            _swFront = MakeQuad("ShotSwFront", _swFrontMat, out _swFrontR);
            _swFlash = MakeQuad("ShotSwFlash", _swFlashMat, out _swFlashR);
            _swWash  = MakeQuad("ShotSwWash",  _swWashMat,  out _swWashR);
            _swSkimL = MakeQuad("ShotSwSkimL", _swSkimMat,  out _swSkimLR);
            _swSkimR = MakeQuad("ShotSwSkimR", _swSkimMat,  out _swSkimRR);

            // Sparks
            _sparks      = new LineRenderer[MaxSparks];
            _sparkMats   = new Material[MaxSparks];
            _sparkPos    = new Vector3[MaxSparks];
            _sparkVel    = new Vector3[MaxSparks];
            _sparkDelay  = new float[MaxSparks];
            _sparkLife   = new float[MaxSparks];
            _sparkIsCore = new bool[MaxSparks];
            for (int i = 0; i < MaxSparks; i++)
            {
                _sparkMats[i] = GlowMaterial.Create(GlowShape.Line, Color.white, 2.5f, "ShotSpark");
                var lr = MakeLine($"ShotSpark_{i}", _sparkMats[i], 2);
                lr.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.15f));
                _sparks[i] = lr;
            }

            // Embers
            _embers     = new Transform[MaxEmbers];
            _emberR     = new Renderer[MaxEmbers];
            _emberMats  = new Material[MaxEmbers];
            _emberPos   = new Vector3[MaxEmbers];
            _emberVel   = new Vector3[MaxEmbers];
            _emberDelay = new float[MaxEmbers];
            _emberLife  = new float[MaxEmbers];
            _emberSize  = new float[MaxEmbers];
            _emberPhase = new float[MaxEmbers];
            for (int i = 0; i < MaxEmbers; i++)
            {
                _emberMats[i] = GlowMaterial.Create(GlowShape.Dot, scorchColor, 1.8f, "ShotEmber");
                _embers[i]    = MakeQuad($"ShotEmber_{i}", _emberMats[i], out _emberR[i]);
            }

            // Point light
            var lightGo = new GameObject("ShotLight");
            _light         = lightGo.AddComponent<Light>();
            _light.type    = LightType.Point;
            _light.shadows = LightShadows.None;
            _light.range   = lightRange;
        }

        static LineRenderer MakeLine(string goName, Material mat, int positions)
        {
            var go = new GameObject(goName);
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

        static Transform MakeQuad(string goName, Material mat, out Renderer rend)
        {
            var go  = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = goName;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            rend = go.GetComponent<Renderer>();
            rend.sharedMaterial             = mat;
            rend.shadowCastingMode          = ShadowCastingMode.Off;
            rend.receiveShadows             = false;
            rend.lightProbeUsage            = LightProbeUsage.Off;
            rend.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            rend.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            return go.transform;
        }

        // ── Triggering: Laser Shot ───────────────────────────────────────────

        /// <summary>Fires the vertical laser bolt from <paramref name="muzzle"/> to <paramref name="impact"/>.</summary>
        public void Play(Vector3 muzzle, Vector3 impact)
        {
            if (matchUfoLivery && _ufo != null && _ufo.tintHull)
                laserColor = _ufo.liveryTint;

            SetAllEnabled(false);

            _shockwaveMode = false;
            _muzzlePos     = muzzle;
            _impactPos     = impact;
            _groundY       = impact.y;
            _elapsed       = 0f;
            _totalDuration = Mathf.Max(beamDuration, Mathf.Max(ringDuration,
                             Mathf.Max(sparkLifetime, emberLifetime)));
            _playing       = true;

            _coreLine.SetPosition(0, muzzle);
            _coreLine.SetPosition(1, impact);
            _glowLine.SetPosition(0, muzzle);
            _glowLine.SetPosition(1, impact);

            CameraRig rig = CameraRig.Instance;
            Vector3 right = rig != null ? rig.Right : Vector3.right;
            Vector3 up    = Vector3.up;

            int nSparks = Mathf.Clamp(sparkCount, 0, MaxSparks);
            for (int i = 0; i < nSparks; i++)
            {
                float angle = Mathf.Lerp(-72f, 72f, (i + Rf(0.15f, 0.85f)) / Mathf.Max(1, nSparks))
                              * Mathf.Deg2Rad;
                Vector3 dir = right * Mathf.Sin(angle) + up * Mathf.Cos(angle);
                float spd   = sparkSpeed * Rf(0.55f, 1.35f);

                _sparkPos[i]    = impact + up * 0.08f + right * Rf(-0.18f, 0.18f);
                _sparkVel[i]    = dir * spd;
                _sparkDelay[i]  = 0f;
                _sparkLife[i]   = sparkLifetime * Rf(0.55f, 1.10f);
                _sparkIsCore[i] = (i & 1) == 0;
            }

            int nEmbers = Mathf.Clamp(emberCount, 0, MaxEmbers);
            for (int i = 0; i < nEmbers; i++)
            {
                _emberPos[i]   = impact + right * Rf(-1.1f, 1.1f) + up * Rf(0.05f, 0.45f);
                _emberVel[i]   = right * Rf(-1.8f, 1.8f) + up * Rf(2.2f, 5.8f);
                _emberDelay[i] = 0f;
                _emberLife[i]  = emberLifetime * Rf(0.65f, 1.15f);
                _emberSize[i]  = Rf(0.22f, 0.52f);
                _emberPhase[i] = Rf(0f, Mathf.PI * 2f);
            }

            if (_light != null && flashLight)
            {
                _light.transform.position = Vector3.Lerp(muzzle, impact, 0.72f) + Vector3.back * 1.2f;
                _light.color     = laserColor;
                _light.range     = lightRange;
                _light.intensity = lightIntensity;
                _light.enabled   = true;
            }

            _coreLine.enabled = true;
            _glowLine.enabled = true;
            _muzzleR.enabled  = true;
            _impactR.enabled  = true;
            _scorchR.enabled  = true;
            _ringR.enabled    = true;
            for (int i = 0; i < nSparks; i++) _sparks[i].enabled = true;
            for (int i = 0; i < nEmbers; i++) _emberR[i].enabled = true;

            StepLaser(0f);
        }

        // ── Triggering: Shockwave Discharge ──────────────────────────────────

        /// <summary>
        /// Fires the shockwave from <paramref name="muzzle"/>. Plays ONLY when the participant pulls the trigger on a
        /// shockwave block. <paramref name="full"/> is the blast proper; without it the press landed nothing and the
        /// weapon only fizzles at the muzzle, leaving the view clear.
        /// </summary>
        public void PlayShockwave(Vector3 muzzle, float groundY, bool full = true)
        {
            SetAllEnabled(false);

            _shockwaveMode = true;
            _swFull        = full;
            _muzzlePos     = muzzle;
            _groundY       = groundY;
            _impactPos     = new Vector3(muzzle.x, groundY, muzzle.z);
            _elapsed       = 0f;
            _totalDuration = full ? Mathf.Max(shockwaveDuration + 0.35f, emberLifetime + 0.35f) : 0.4f;
            _playing       = true;

            _muzzleR.enabled     = true;
            _swEchoRingR.enabled = true;

            if (!full)
            {
                StepShockwave(0f);
                return;
            }

            CameraRig rig   = CameraRig.Instance;
            Vector3   right = rig != null ? rig.Right : Vector3.right;
            Vector3   up    = Vector3.up;

            // The visible width at the UFO's plane, so sparks and embers span the screen around the camera.
            float halfW = 24f;
            float centerX = muzzle.x;
            if (rig != null)
            {
                Vector2 ext = rig.ViewHalfExtentsAtZ(muzzle.z);
                halfW   = Mathf.Max(18f, ext.x * 0.96f);
                centerX = rig.RestX;
            }

            float dy = Mathf.Max(0.5f, muzzle.y - groundY);

            // Sparks kick up across the skyline at the moment the wave reaches them.
            int nSparks = Mathf.Clamp(sparkCount, 0, MaxSparks);
            for (int i = 0; i < nSparks; i++)
            {
                float u  = (i + Rf(0.15f, 0.85f)) / Mathf.Max(1, nSparks);
                float x  = Mathf.Lerp(centerX - halfW, centerX + halfW, u);
                float dx = x - muzzle.x;

                // Tilted outward, away from the ship, as the wave pushes past.
                float outwardSign = dx >= 0f ? 1f : -1f;
                float angle = (outwardSign * Rf(8f, 52f)) * Mathf.Deg2Rad;
                Vector3 dir = right * Mathf.Sin(angle) + up * Mathf.Cos(angle);

                _sparkPos[i]    = new Vector3(x, groundY + Rf(0.05f, 0.45f), muzzle.z);
                _sparkVel[i]    = dir * (sparkSpeed * Rf(0.75f, 1.45f));
                _sparkDelay[i]  = ArrivalSec(Mathf.Sqrt(dx * dx + dy * dy));
                _sparkLife[i]   = sparkLifetime * Rf(0.70f, 1.25f);
                _sparkIsCore[i] = (i & 1) == 0;
                _sparks[i].enabled = true;
            }

            // Embers rise after it.
            int nEmbers = Mathf.Clamp(emberCount, 0, MaxEmbers);
            for (int i = 0; i < nEmbers; i++)
            {
                float u  = (i + Rf(0.1f, 0.9f)) / Mathf.Max(1, nEmbers);
                float x  = Mathf.Lerp(centerX - halfW * 0.92f, centerX + halfW * 0.92f, u);
                float dx = x - muzzle.x;

                _emberPos[i]   = new Vector3(x, groundY + Rf(0.1f, 1.8f), muzzle.z);
                _emberVel[i]   = right * (Mathf.Sign(dx) * Rf(0.6f, 3.2f)) + up * Rf(3.0f, 7.5f);
                _emberDelay[i] = ArrivalSec(Mathf.Sqrt(dx * dx + dy * dy));
                _emberLife[i]  = emberLifetime * Rf(0.80f, 1.30f);
                _emberSize[i]  = Rf(0.32f, 0.72f);
                _emberPhase[i] = Rf(0f, Mathf.PI * 2f);
                _emberR[i].enabled = true;
            }

            if (_light != null && flashLight)
            {
                _light.transform.position = muzzle + Vector3.back * 1.5f;
                _light.color     = shockwaveColor;
                _light.range     = lightRange * 2.6f;
                _light.intensity = lightIntensity * 2.8f;
                _light.enabled   = true;
            }

            _swFrontR.enabled = true;
            _swWakeR.enabled  = true;
            _swFlashR.enabled = true;
            _swWashR.enabled  = true;
            _swSkimLR.enabled = true;
            _swSkimRR.enabled = true;

            StepShockwave(0f);
        }

        // When the front reaches a distance from the ship, seconds: the inverse of its radius over time.
        float ArrivalSec(float dist)
        {
            float r = Mathf.Clamp01(dist / Mathf.Max(1f, shockwaveMaxRadius));
            return shockwaveDuration * (1f - Mathf.Pow(1f - r, 1f / SwEase));
        }

        // ── Per-frame update ─────────────────────────────────────────────────

        void LateUpdate()
        {
            if (!_playing) return;

            float dt = Time.unscaledDeltaTime;
            _elapsed += dt;

            if (_elapsed >= _totalDuration)
            {
                _playing = false;
                SetAllEnabled(false);
                return;
            }

            if (_shockwaveMode) StepShockwave(dt);
            else                StepLaser(dt);
        }

        void StepLaser(float dt)
        {
            Camera cam = CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
            Vector3 toCam = cam != null
                ? (cam.transform.position - _impactPos).normalized
                : Vector3.back;
            Quaternion faceCam = cam != null
                ? Quaternion.LookRotation(-toCam, cam.transform.up)
                : Quaternion.identity;

            // 1. Beam
            float bt = Mathf.Clamp01(_elapsed / Mathf.Max(0.001f, beamDuration));
            if (bt < 1f)
            {
                float punch = bt < 0.14f
                    ? Mathf.Lerp(0.55f, 1.28f, bt / 0.14f)
                    : Mathf.Pow(1f - (bt - 0.14f) / 0.86f, 1.55f);

                float fade = Mathf.Pow(1f - bt, 1.25f);

                _coreLine.widthMultiplier = beamCoreWidth * punch;
                _glowLine.widthMultiplier = beamGlowWidth * (0.65f + 0.55f * punch);

                Color coreTint = Color.Lerp(Color.white, laserColor, Mathf.SmoothStep(0f, 1f, bt * 1.2f));
                GlowMaterial.SetColor(_coreMat, GlowMaterial.Scaled(coreTint, coreIntensity * fade, fade));
                GlowMaterial.SetColor(_glowMat, GlowMaterial.Scaled(laserColor, glowIntensity * fade, fade * 0.9f));
            }
            else
            {
                _coreLine.enabled = false;
                _glowLine.enabled = false;
            }

            // 2. Muzzle flash
            float mt = Mathf.Clamp01(_elapsed / (beamDuration * 0.65f));
            if (mt < 1f)
            {
                float mf = Mathf.Pow(1f - mt, 1.7f);
                PoseQuad(_muzzleFlash, _muzzlePos + toCam * 0.12f, faceCam,
                         muzzleFlashSize * (0.7f + 0.55f * (1f - mt)));
                Color mc = Color.Lerp(Color.white, laserColor, mt * 0.7f);
                GlowMaterial.SetColor(_muzzleMat, GlowMaterial.Scaled(mc, coreIntensity * 0.9f * mf, mf));
            }
            else _muzzleR.enabled = false;

            // 3. Impact flash + warm scorch
            float it = Mathf.Clamp01(_elapsed / (ringDuration * 0.85f));
            if (it < 1f)
            {
                float f = Mathf.Pow(1f - it, 1.5f);
                PoseQuad(_impactFlash, _impactPos + toCam * 0.14f, faceCam,
                         impactFlashSize * (0.55f + 0.75f * Mathf.Sqrt(it)));
                Color ic = Color.Lerp(Color.white, laserColor, Mathf.SmoothStep(0f, 1f, it));
                GlowMaterial.SetColor(_impactMat, GlowMaterial.Scaled(ic, glowIntensity * 1.4f * f, f));
            }
            else _impactR.enabled = false;

            float st = Mathf.Clamp01(_elapsed / ringDuration);
            if (st < 1f)
            {
                float sf = Mathf.Pow(1f - st, 1.2f) * 0.75f;
                PoseQuad(_scorchFlash, _impactPos + toCam * 0.10f, faceCam,
                         impactFlashSize * 0.95f * (0.7f + 0.4f * st));
                GlowMaterial.SetColor(_scorchMat, GlowMaterial.Scaled(scorchColor, 1.5f * sf, sf));
            }
            else _scorchR.enabled = false;

            // 4. Expanding shockwave ring
            if (st < 1f)
            {
                float ease = 1f - Mathf.Pow(1f - st, 2.6f);
                float size = Mathf.Lerp(ringStartSize, ringEndSize, ease);
                float rf   = Mathf.Pow(1f - st, 1.35f);
                PoseQuad(_ring, _impactPos + toCam * 0.16f, faceCam, size);
                GlowMaterial.SetColor(_ringMat, GlowMaterial.Scaled(laserColor, glowIntensity * 1.2f * rf, rf));
            }
            else _ringR.enabled = false;

            // 5. Sparks
            int nSparks = Mathf.Clamp(sparkCount, 0, MaxSparks);
            Vector3 grav = Vector3.down * sparkGravity;
            for (int i = 0; i < nSparks; i++)
            {
                if (!_sparks[i].enabled) continue;
                float lt = _elapsed / _sparkLife[i];
                if (lt >= 1f) { _sparks[i].enabled = false; continue; }

                _sparkVel[i] += grav * dt;
                _sparkPos[i] += _sparkVel[i] * dt;
                if (_sparkPos[i].y < _impactPos.y)
                {
                    _sparkPos[i].y = _impactPos.y;
                    _sparkVel[i].y = Mathf.Abs(_sparkVel[i].y) * 0.35f;
                    _sparkVel[i].x *= 0.7f;
                }

                float sf   = Mathf.Pow(1f - lt, 1.3f);
                float tail = Mathf.Clamp(_sparkVel[i].magnitude * 0.032f, 0.14f, 0.65f) * (1f - lt * 0.5f);
                Vector3 dir = _sparkVel[i].sqrMagnitude > 1e-4f ? _sparkVel[i].normalized : Vector3.up;

                _sparks[i].widthMultiplier = 0.09f * (0.4f + 0.6f * sf);
                _sparks[i].SetPosition(0, _sparkPos[i]);
                _sparks[i].SetPosition(1, _sparkPos[i] - dir * tail);

                Color baseCol = _sparkIsCore[i] ? Color.Lerp(Color.white, laserColor, lt)
                                                : Color.Lerp(laserColor, scorchColor, lt);
                GlowMaterial.SetColor(_sparkMats[i], GlowMaterial.Scaled(baseCol, 2.8f * sf, sf));
            }

            // 6. Embers
            int nEmbers = Mathf.Clamp(emberCount, 0, MaxEmbers);
            Vector3 right = CameraRig.Instance != null ? CameraRig.Instance.Right : Vector3.right;
            for (int i = 0; i < nEmbers; i++)
            {
                if (!_emberR[i].enabled) continue;
                float lt = _elapsed / _emberLife[i];
                if (lt >= 1f) { _emberR[i].enabled = false; continue; }

                float sway = Mathf.Sin(_elapsed * 7f + _emberPhase[i]) * 1.4f;
                _emberPos[i] += (_emberVel[i] + right * sway) * dt;
                _emberVel[i] *= Mathf.Exp(-1.6f * dt);

                float ef = Mathf.Sin(lt * Mathf.PI) * (1f - lt * 0.3f);
                float sz = _emberSize[i] * (0.6f + 0.5f * ef);
                PoseQuad(_embers[i], _emberPos[i] + toCam * 0.12f, faceCam, sz);

                Color ec = Color.Lerp(laserColor, scorchColor, Mathf.SmoothStep(0.15f, 0.85f, lt));
                GlowMaterial.SetColor(_emberMats[i], GlowMaterial.Scaled(ec, 2.2f * ef, ef));
            }

            // 7. Point light
            if (_light != null && _light.enabled)
            {
                float lt = Mathf.Clamp01(_elapsed / (beamDuration * 1.15f));
                if (lt >= 1f) _light.enabled = false;
                else          _light.intensity = lightIntensity * Mathf.Pow(1f - lt, 1.8f);
            }
        }

        void StepShockwave(float dt)
        {
            CameraRig rig = CameraRig.Instance;
            Camera    cam = rig != null ? rig.ActiveCamera : Camera.main;
            Vector3   right = rig != null ? rig.Right : Vector3.right;
            Vector3   up    = Vector3.up;

            // The muzzle stays with the ship for the first instant, so the pulse feels attached to it as it flies,
            // and blends into world space as it expands.
            Vector3 liveMuzzle = _muzzlePos;
            if (_ufo != null)
                liveMuzzle = Vector3.Lerp(_ufo.transform.position + new Vector3(0f, -0.35f, 0f), _muzzlePos,
                                          Mathf.Clamp01(_elapsed / 0.22f));

            Vector3 toCam = cam != null
                ? (cam.transform.position - liveMuzzle).normalized
                : Vector3.back;
            Quaternion faceCam = cam != null
                ? Quaternion.LookRotation(-toCam, cam.transform.up)
                : Quaternion.identity;

            Color bright = Color.Lerp(shockwaveColor, Color.white, 0.38f);

            // 1. The core at the muzzle: white-hot and gone in a blink. Small and dull for a fizzle.
            float coreSec = _swFull ? 0.20f : 0.16f;
            float mt = Mathf.Clamp01(_elapsed / coreSec);
            if (mt < 1f)
            {
                float mf = Mathf.Pow(1f - mt, 1.5f);
                PoseQuad(_muzzleFlash, liveMuzzle + toCam * 0.15f, faceCam,
                         (_swFull ? 6.5f : 2.2f) * (0.55f + 0.6f * Mathf.Sqrt(mt)));
                Color mc = Color.Lerp(Color.white, bright, mt);
                GlowMaterial.SetColor(_muzzleMat, GlowMaterial.Scaled(mc, (_swFull ? coreIntensity * 1.4f : 1.3f) * mf, mf));
            }
            else _muzzleR.enabled = false;

            // 2. A ripple close around the ship, crisp and quick.
            float rippleSec = _swFull ? 0.42f : 0.30f;
            float pt = Mathf.Clamp01(_elapsed / rippleSec);
            if (pt < 1f)
            {
                float ease = 1f - Mathf.Pow(1f - pt, 2.2f);
                float rad  = Mathf.Lerp(0.8f, _swFull ? 15f : 2.8f, ease);
                float rf   = Mathf.Pow(1f - pt, 1.3f);
                PoseWave(_swEchoRing, _swEchoRingMat, liveMuzzle + toCam * 0.13f, faceCam, rad, 0.08f, _swFull ? 0.9f : 0.5f);
                GlowMaterial.SetColor(_swEchoRingMat, GlowMaterial.Scaled(bright, (_swFull ? glowIntensity * 0.9f : 1.0f) * rf, rf));
            }
            else _swEchoRingR.enabled = false;

            if (!_swFull) return;

            // 3. The fronts, in a full circle about the ship: the crisp one, then a softer one catching up behind it.
            float wt = Mathf.Clamp01(_elapsed / Mathf.Max(0.05f, shockwaveDuration));
            float radius = Mathf.Lerp(0.8f, shockwaveMaxRadius, 1f - Mathf.Pow(1f - wt, SwEase));
            if (wt < 1f)
            {
                float fade = Mathf.Pow(1f - wt, 0.9f);
                PoseWave(_swFront, _swFrontMat, liveMuzzle + toCam * 0.16f, faceCam, radius, 0.14f, 3.2f);
                Color front = Color.Lerp(Color.white, bright, Mathf.SmoothStep(0f, 0.3f, wt));
                GlowMaterial.SetColor(_swFrontMat, GlowMaterial.Scaled(front, 2.1f * fade, fade));

                float wakeT = Mathf.Clamp01(wt * 0.94f);
                float wakeR = Mathf.Lerp(0.6f, shockwaveMaxRadius * 0.86f, 1f - Mathf.Pow(1f - wakeT, 2.0f));
                float wf    = Mathf.Sin(Mathf.Min(1f, wt * 1.25f + 0.08f) * Mathf.PI) * (1f - wt * 0.5f);
                PoseWave(_swWake, _swWakeMat, liveMuzzle + toCam * 0.10f, faceCam, wakeR, 0.9f, 15f);
                GlowMaterial.SetColor(_swWakeMat, GlowMaterial.Scaled(shockwaveColor, glowIntensity * 1.1f * wf, 0.8f * wf));
            }
            else
            {
                _swFrontR.enabled = false;
                _swWakeR.enabled  = false;
            }

            // 4. The flash: the whole sky lit for an instant.
            float ft = Mathf.Clamp01(_elapsed / 0.24f);
            if (ft < 1f)
            {
                float ff = Mathf.Pow(1f - ft, 1.7f);
                PoseQuad(_swFlash, liveMuzzle + toCam * 0.05f, faceCam, Mathf.Lerp(12f, 38f, Mathf.Sqrt(ft)));
                GlowMaterial.SetColor(_swFlashMat, GlowMaterial.Scaled(bright, 1.5f * ff, 0.75f * ff));
            }
            else _swFlashR.enabled = false;

            // 5. Where the wave meets the ground, two fronts race apart along the city's foot, a pool of light between.
            float gy    = _groundY + 0.5f;
            float below = liveMuzzle.y - gy;
            float gf    = Mathf.Pow(Mathf.Max(0f, 1f - wt), 1.1f);
            if (wt < 1f && radius > below)
            {
                float   dx   = Mathf.Sqrt(radius * radius - below * below);
                Vector3 mid  = new Vector3(liveMuzzle.x, gy, liveMuzzle.z) + toCam * 0.16f;
                PoseQuadXY(_swSkimL, mid - right * dx, faceCam, 9f, 1.5f);
                PoseQuadXY(_swSkimR, mid + right * dx, faceCam, 9f, 1.5f);
                PoseQuadXY(_swWash,  mid, faceCam, 2f * dx + 8f, 4.5f);
                GlowMaterial.SetColor(_swSkimMat, GlowMaterial.Scaled(Color.Lerp(Color.white, bright, wt), 3.6f * gf, gf));
                GlowMaterial.SetColor(_swWashMat, GlowMaterial.Scaled(shockwaveColor, 1.25f * gf, 0.8f * gf));
            }
            else
            {
                GlowMaterial.SetColor(_swSkimMat, Color.clear);
                GlowMaterial.SetColor(_swWashMat, Color.clear);
            }

            // 6. Sparks kicked up across the skyline as the wave passes
            int nSparks = Mathf.Clamp(sparkCount, 0, MaxSparks);
            Vector3 grav = Vector3.down * (sparkGravity * 0.75f);
            for (int i = 0; i < nSparks; i++)
            {
                if (!_sparks[i].enabled) continue;
                float localElapsed = _elapsed - _sparkDelay[i];
                if (localElapsed < 0f)
                {
                    _sparks[i].widthMultiplier = 0f;
                    continue;
                }

                float lt = localElapsed / _sparkLife[i];
                if (lt >= 1f) { _sparks[i].enabled = false; continue; }

                _sparkVel[i] += grav * dt;
                _sparkPos[i] += _sparkVel[i] * dt;
                if (_sparkPos[i].y < _groundY)
                {
                    _sparkPos[i].y = _groundY;
                    _sparkVel[i].y = Mathf.Abs(_sparkVel[i].y) * 0.4f;
                    _sparkVel[i].x *= 0.75f;
                }

                float sf   = Mathf.Pow(1f - lt, 1.25f);
                float tail = Mathf.Clamp(_sparkVel[i].magnitude * 0.036f, 0.18f, 0.78f) * (1f - lt * 0.45f);
                Vector3 dir = _sparkVel[i].sqrMagnitude > 1e-4f ? _sparkVel[i].normalized : Vector3.up;

                _sparks[i].widthMultiplier = 0.10f * (0.45f + 0.65f * sf);
                _sparks[i].SetPosition(0, _sparkPos[i]);
                _sparks[i].SetPosition(1, _sparkPos[i] - dir * tail);

                Color baseCol = _sparkIsCore[i]
                    ? Color.Lerp(Color.white, shockwaveColor, lt)
                    : Color.Lerp(shockwaveColor, shockwaveWakeColor, lt);
                GlowMaterial.SetColor(_sparkMats[i], GlowMaterial.Scaled(baseCol, 3.0f * sf, sf));
            }

            // 7. Embers rising after it
            int nEmbers = Mathf.Clamp(emberCount, 0, MaxEmbers);
            for (int i = 0; i < nEmbers; i++)
            {
                if (!_emberR[i].enabled) continue;
                float localElapsed = _elapsed - _emberDelay[i];
                if (localElapsed < 0f)
                {
                    GlowMaterial.SetColor(_emberMats[i], Color.clear);
                    continue;
                }

                float lt = localElapsed / _emberLife[i];
                if (lt >= 1f) { _emberR[i].enabled = false; continue; }

                float sway = Mathf.Sin(_elapsed * 6.5f + _emberPhase[i]) * 1.5f;
                _emberPos[i] += (_emberVel[i] + right * sway) * dt;
                _emberVel[i] *= Mathf.Exp(-1.4f * dt);

                float ef = Mathf.Sin(lt * Mathf.PI) * (1f - lt * 0.25f);
                float sz = _emberSize[i] * (0.65f + 0.55f * ef);
                PoseQuad(_embers[i], _emberPos[i] + toCam * 0.12f, faceCam, sz);

                Color ec = Color.Lerp(shockwaveColor, shockwaveWakeColor, Mathf.SmoothStep(0.15f, 0.85f, lt));
                GlowMaterial.SetColor(_emberMats[i], GlowMaterial.Scaled(ec, 2.3f * ef, ef));
            }

            // 8. The light
            if (_light != null && _light.enabled)
            {
                float lt = Mathf.Clamp01(_elapsed / (shockwaveDuration * 0.95f));
                if (lt >= 1f) _light.enabled = false;
                else
                {
                    _light.transform.position = Vector3.Lerp(liveMuzzle, _impactPos + Vector3.up * 2.5f, lt) + Vector3.back * 1.8f;
                    _light.intensity = lightIntensity * 1.35f * Mathf.Pow(1f - lt, 1.55f);
                }
            }
        }

        // A wave quad: its radius in world units, its crisp edge and the light behind it held at a world width as it grows.
        static void PoseWave(Transform t, Material m, Vector3 pos, Quaternion rot, float radius, float edge, float wake)
        {
            PoseQuad(t, pos, rot, 2f * radius);
            float r = Mathf.Max(0.01f, radius);
            m.SetFloat(GlowMaterial.RingEdgeId,  Mathf.Clamp(edge / r, 0.001f, 0.5f));
            m.SetFloat(GlowMaterial.RingWidthId, Mathf.Clamp(wake / r, 0.02f, 1f));
        }

        static void PoseQuadXY(Transform t, Vector3 pos, Quaternion rot, float sizeX, float sizeY)
        {
            t.position   = pos;
            t.rotation   = rot;
            t.localScale = new Vector3(sizeX, sizeY, 1f);
        }

        static void PoseQuad(Transform t, Vector3 pos, Quaternion rot, float size)
        {
            t.position   = pos;
            t.rotation   = rot;
            t.localScale = new Vector3(size, size, 1f);
        }

        void SetAllEnabled(bool on)
        {
            if (_coreLine    != null) _coreLine.enabled    = on;
            if (_glowLine    != null) _glowLine.enabled    = on;
            if (_swFrontR    != null) _swFrontR.enabled    = on;
            if (_swWakeR     != null) _swWakeR.enabled     = on;
            if (_swFlashR    != null) _swFlashR.enabled    = on;
            if (_swWashR     != null) _swWashR.enabled     = on;
            if (_swSkimLR    != null) _swSkimLR.enabled    = on;
            if (_swSkimRR    != null) _swSkimRR.enabled    = on;
            if (_muzzleR     != null) _muzzleR.enabled     = on;
            if (_impactR     != null) _impactR.enabled     = on;
            if (_scorchR     != null) _scorchR.enabled     = on;
            if (_ringR       != null) _ringR.enabled       = on;
            if (_swEchoRingR != null) _swEchoRingR.enabled = on;
            if (_light       != null) _light.enabled       = on;

            if (_sparks != null)
                for (int i = 0; i < _sparks.Length; i++)
                    if (_sparks[i] != null) _sparks[i].enabled = on;

            if (_emberR != null)
                for (int i = 0; i < _emberR.Length; i++)
                    if (_emberR[i] != null) _emberR[i].enabled = on;
        }

        float Rf(float min, float max) => min + (float)_rng.NextDouble() * (max - min);
    }
}
