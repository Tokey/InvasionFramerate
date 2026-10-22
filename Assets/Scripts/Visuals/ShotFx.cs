using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// The laser shot, drawn as light. A white-hot bolt strikes from the muzzle to the ground in a
    /// few hundredths of a second with a bright pulse of energy racing down it; the muzzle flashes;
    /// where it lands a flash, a shockwave ring and a fountain of teal sparks go up, orange embers
    /// are thrown out of the city, and the city around the hit is lit warm for half a second. Then
    /// the bolt narrows to a thread and goes out.
    ///
    /// <code>
    /// Timeline, unscaled seconds from the shot (the Inspector defaults):
    ///   0       muzzle flash pops (out by 0.09); the bolt leaves the muzzle; the pulse sets off
    ///   0.035   the bolt strikes the ground: impact flash (0.16), city glow (0.55), shockwave
    ///           ring (0.38), sparks (live 0.3-0.75) and embers (0.6-1.3); the bolt's afterglow
    ///           begins, narrowing and fading over 0.26
    ///   0.07    the pulse has run into the ground
    ///   ~0.3    the bolt is out
    ///   ~1.3    the last ember is out; every renderer off until the next shot
    /// </code>
    ///
    /// Fast and contained on purpose. The hit should land with some weight and then get out of the
    /// way of the reveal, and nothing in it flashes the whole screen: it plays on every round of an
    /// hour-long session.
    ///
    /// It fires on a SHOT and knows only the two points it is handed — never where the tower is or
    /// which side of it the shot fell — so it cannot become a cue to the boundary; and by the time
    /// any of it is on screen the round has already been answered.
    ///
    /// Everything lives under one root-level object, "ShotFx", not under the ship. The muzzle's
    /// scale chain is wildly non-uniform and would skew anything parented into it; the shot must
    /// stay where it was fired while the ship flies on; and UfoController.SetHidden, which switches
    /// off every renderer under the ship for the reset, must not cut a shot off half way. Every
    /// part is drawn at the depth of the two points it is given — the play plane.
    ///
    /// Built once in Awake and reused for every shot. A new shot restarts the timeline cleanly;
    /// sparks already in the air finish their flight. Nothing allocates per frame or per shot, and
    /// between shots every renderer is off, so it costs nothing while idle.
    /// </summary>
    public class ShotFx : MonoBehaviour
    {
        [Header("Colour")]
        [Tooltip("Take the laser colour from the ship's livery (UfoController.liveryTint), as the " +
                 "sight does, so the hull, the sight and the shot are one teal. Read once, at start.")]
        public bool matchLivery = true;
        public Color laserColor = new Color(0.35f, 1f, 0.85f, 1f);
        [Tooltip("How far the hottest light — the bolt's core, the flashes, a spark's first instant — " +
                 "is pushed from the laser colour toward white.")]
        [Range(0f, 1f)] public float whiteHot = 0.6f;

        [Header("Bolt")]
        [Tooltip("Seconds for the bolt to cross from the muzzle to the ground. Short enough to be " +
                 "instant to the hand, long enough to read as a bolt rather than a line that was " +
                 "simply there. The impact starts when it arrives.")]
        [Min(0f)] public float strikeSec = 0.035f;
        [Tooltip("Width of the white-hot core, world units. Its light falls off linearly to the " +
                 "edges, so it reads about half as wide.")]
        [Min(0.001f)] public float coreWidth = 0.1f;
        [Tooltip("HDR brightness of the core. Well above 1: this is the part that blooms.")]
        [Min(0f)] public float coreIntensity = 6f;
        [Tooltip("Width of the coloured glow around the core, world units.")]
        [Min(0.001f)] public float glowWidth = 0.4f;
        [Min(0f)] public float glowIntensity = 2.2f;
        [Tooltip("Seconds the bolt takes to die once it has struck: it narrows and fades, the glow " +
                 "going a little ahead of the core.")]
        [Min(0.01f)] public float afterglowSec = 0.26f;
        [Tooltip("Width the bolt has narrowed to by the end of the afterglow, as a fraction of full.")]
        [Range(0f, 1f)] public float afterglowEndWidth = 0.25f;

        [Header("Energy Pulse")]
        [Tooltip("Seconds for a bright dash to race down the bolt from the muzzle into the ground, " +
                 "chasing the strike. It never runs ahead of the bolt.")]
        [Min(0.01f)] public float pulseSec = 0.07f;
        [Tooltip("Length of the dash as a fraction of the bolt.")]
        [Range(0.02f, 0.5f)] public float pulseLength = 0.2f;
        [Tooltip("Width of the dash, world units.")]
        [Min(0.001f)] public float pulseWidth = 0.22f;
        [Min(0f)] public float pulseIntensity = 7f;

        [Header("Muzzle Flash")]
        [Tooltip("Diameter of the flash at the muzzle, world units.")]
        [Min(0f)] public float muzzleFlashSize = 0.9f;
        [Min(0.01f)] public float muzzleFlashSec = 0.09f;
        [Min(0f)] public float muzzleFlashIntensity = 4f;

        [Header("Impact")]
        [Tooltip("Diameter of the white-hot flash where the bolt strikes, world units. It is centred " +
                 "on the ground line, so the rooftops in front show only its upper half.")]
        [Min(0f)] public float impactFlashSize = 2.4f;
        [Min(0.01f)] public float impactFlashSec = 0.16f;
        [Min(0f)] public float impactFlashIntensity = 5f;
        [Tooltip("The city lit up by the hit: a larger, dimmer, warmer glow that outlasts the flash.")]
        public Color cityGlowColor = new Color(1f, 0.6f, 0.3f, 1f);
        [Tooltip("Diameter of the city glow, world units.")]
        [Min(0f)] public float cityGlowSize = 5f;
        [Min(0.01f)] public float cityGlowSec = 0.55f;
        [Min(0f)] public float cityGlowIntensity = 0.6f;
        [Tooltip("Radius the shockwave ring spreads to, world units. Its band starts broad and soft " +
                 "and tightens to a thin edge as it goes.")]
        [Min(0f)] public float shockwaveRadius = 2.6f;
        [Min(0.01f)] public float shockwaveSec = 0.38f;
        [Min(0f)] public float shockwaveIntensity = 2.5f;

        [Header("Sparks")]
        [Tooltip("Teal-white sparks thrown up out of the impact, drawn out into streaks along their " +
                 "flight.")]
        [Range(0, 48)] public int sparkCount = 26;
        [Tooltip("Full opening angle of the cone they fly up into, degrees.")]
        [Range(0f, 180f)] public float sparkCone = 70f;
        [Tooltip("Launch speed, world units per second.")]
        [Min(0f)] public float sparkSpeedMin = 4f;
        [Min(0f)] public float sparkSpeedMax = 11f;
        [Tooltip("Lifetime, seconds.")]
        [Min(0.01f)] public float sparkLifeMin = 0.3f;
        [Min(0.01f)] public float sparkLifeMax = 0.75f;
        [Tooltip("Width, world units.")]
        [Min(0.001f)] public float sparkSizeMin = 0.05f;
        [Min(0.001f)] public float sparkSizeMax = 0.09f;
        [Tooltip("Multiplier on Physics.gravity.")]
        public float sparkGravity = 1.2f;
        [Tooltip("How far each spark is drawn out along its velocity, as seconds of travel.")]
        [Min(0f)] public float sparkStretch = 0.035f;
        [Tooltip("HDR brightness of the sparks.")]
        [Min(0f)] public float sparkIntensity = 4f;

        [Header("Embers")]
        [Tooltip("Embers and debris kicked out of the city: slower, longer-lived, cooling from " +
                 "orange to red.")]
        [Range(0, 24)] public int emberCount = 10;
        public Color emberColor = new Color(1f, 0.5f, 0.12f, 1f);
        public Color emberCoolColor = new Color(0.8f, 0.1f, 0.03f, 1f);
        [Tooltip("Full opening angle of the cone they are thrown into, degrees.")]
        [Range(0f, 180f)] public float emberCone = 100f;
        [Tooltip("Launch speed, world units per second.")]
        [Min(0f)] public float emberSpeedMin = 2f;
        [Min(0f)] public float emberSpeedMax = 6f;
        [Tooltip("Lifetime, seconds.")]
        [Min(0.01f)] public float emberLifeMin = 0.6f;
        [Min(0.01f)] public float emberLifeMax = 1.3f;
        [Tooltip("Diameter, world units.")]
        [Min(0.001f)] public float emberSizeMin = 0.06f;
        [Min(0.001f)] public float emberSizeMax = 0.11f;
        [Tooltip("Multiplier on Physics.gravity.")]
        public float emberGravity = 1f;
        [Tooltip("HDR brightness of the embers.")]
        [Min(0f)] public float emberIntensity = 2.5f;

        // ── Private ──────────────────────────────────────────────────────────

        // Particle pools, bounded: past the cap Emit drops particles rather than growing the pool,
        // so even fire at the cooldown's limit cannot allocate.
        const int SparkPool = 96;
        const int EmberPool = 64;

        // Seconds the city glow takes to come up. A five-unit wash cut in at full strength in one
        // frame reads as the screen flashing; this softens its edge without delaying it.
        const float CityRiseSec = 0.04f;

        // Breadth of the shockwave's band at its start and end, as a fraction of its radius.
        const float RingWidthStart = 0.4f;
        const float RingWidthEnd   = 0.08f;

        GameObject             _root;
        Mesh                   _quad;
        LineRenderer           _core, _glow, _pulse;
        Renderer               _muzzleFlash, _impactFlash, _cityGlow, _ring;
        ParticleSystem         _sparks, _embers;
        ParticleSystemRenderer _sparkRenderer, _emberRenderer;
        Material               _coreMat, _glowMat, _pulseMat, _muzzleMat, _impactMat, _cityMat,
                               _ringMat, _sparkMat, _emberMat;

        UfoController _ufo;

        // Spark and ember directions, speeds, lifetimes and sizes are drawn from here — never from
        // UnityEngine.Random, whose sequence the experiment's own randomisation comes from: a few
        // dozen extra draws per shot would move every tower that followed.
        readonly System.Random    _rng = new System.Random(0x5EED);
        ParticleSystem.EmitParams _emit;

        Vector3 _from, _to;      // this shot's muzzle and impact points
        double  _startTime;      // unscaled time it was fired
        double  _endTime;        // unscaled time the last of it — particles included — is out
        bool    _playing;
        bool    _burst;          // this shot's sparks and embers are out

        Color Hot => Color.Lerp(laserColor, Color.white, whiteHot);

        /// <summary>
        /// Fires the effect: a bolt from <paramref name="muzzle"/> striking <paramref name="impact"/>.
        /// Restarts cleanly if a shot is already playing — its light is cut and the new one starts
        /// from the top; sparks already in the air finish their flight.
        /// </summary>
        public void Play(Vector3 muzzle, Vector3 impact)
        {
            if (!isActiveAndEnabled || _root == null) return;

            _from      = muzzle;
            _to        = impact;
            _startTime = Time.unscaledTimeAsDouble;
            _endTime   = _startTime + LightSec();
            _burst     = false;
            _playing   = true;

            // Drawn now as well as from LateUpdate, so a shot fired from anywhere in the frame —
            // even after this component's LateUpdate has run — is on screen this frame.
            Evaluate(0f);
        }

        void Awake()
        {
            _ufo  = GetComponentInParent<UfoController>();
            _root = new GameObject("ShotFx");
            _quad = BuildQuad();

            // Draw order among these is irrelevant — it is all additive light — so the hierarchy
            // simply reads in timeline order. The core's straight-ramp falloff (softness 1) keeps
            // it crisp; the glows pull their light into the middle.
            _muzzleFlash = MakeQuad("MuzzleFlash", GlowShape.Dot, 2f, out _muzzleMat);
            _glow        = MakeLine("BoltGlow", 1.8f, out _glowMat);
            _core        = MakeLine("BoltCore", 1f,   out _coreMat);
            _pulse       = MakeLine("Pulse",    1.2f, out _pulseMat);
            _impactFlash = MakeQuad("ImpactFlash", GlowShape.Dot,  2.2f, out _impactMat);
            _cityGlow    = MakeQuad("CityGlow",    GlowShape.Dot,  1.3f, out _cityMat);
            _ring        = MakeQuad("Shockwave",   GlowShape.Ring, 1f,   out _ringMat);
            _sparks      = MakeParticles("Sparks", SparkPool, 0x5A4Bu, 1.2f, 0.35f,
                                         out _sparkMat, out _sparkRenderer);
            _embers      = MakeParticles("Embers", EmberPool, 0xE3B5u, 1.6f, 0.5f,
                                         out _emberMat, out _emberRenderer);

            // Sparks are streaks drawn out along their flight; embers stay round.
            _sparkRenderer.renderMode          = ParticleSystemRenderMode.Stretch;
            _sparkRenderer.lengthScale         = 1f;
            _sparkRenderer.cameraVelocityScale = 0f;
            _emberRenderer.renderMode          = ParticleSystemRenderMode.Billboard;

            // The pulse is a comet: a fading tail behind a bright head. Its line runs from the
            // muzzle side to the ground side, so the head is the far end.
            var comet = new Gradient();
            comet.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.8f),
                        new GradientAlphaKey(0.3f, 1f) });
            _pulse.colorGradient = comet;
        }

        // Not Awake: the livery colour is on a component that may not have run its own Awake yet.
        // The particles' colours are baked into their lifetime gradients here, once; everything
        // else reads its colour from the fields as it draws, so those follow Inspector edits live.
        void Start()
        {
            if (matchLivery && _ufo != null) laserColor = _ufo.liveryTint;

            SetLifetimeColours(_sparks, Hot, laserColor);
            SetLifetimeColours(_embers, emberColor, emberCoolColor);
        }

        void OnDisable()
        {
            if (_playing) Idle();
        }

        void OnDestroy()
        {
            Release(_root);
            Release(_quad);
            Release(_coreMat);
            Release(_glowMat);
            Release(_pulseMat);
            Release(_muzzleMat);
            Release(_impactMat);
            Release(_cityMat);
            Release(_ringMat);
            Release(_sparkMat);
            Release(_emberMat);
        }

        // ── Per frame ────────────────────────────────────────────────────────

        void LateUpdate()
        {
            if (!_playing) return;
            if (_root == null) { _playing = false; return; }

            double now = Time.unscaledTimeAsDouble;
            if (now >= _endTime) Idle();
            else                 Evaluate((float)(now - _startTime));
        }

        // The whole shot, `t` seconds after it was fired. A function of t alone (bar the one-off
        // burst), so it plays the same at any frame rate — frames only sample it.
        void Evaluate(float t)
        {
            Color hot   = Hot;
            float reach = strikeSec > 0f ? Mathf.Clamp01(t / strikeSec) : 1f;
            float since = t - strikeSec;   // since the bolt struck; negative until it has

            DrawBolt(reach, since, hot);
            DrawPulse(t, reach, hot);
            DrawFlash(_muzzleFlash, _muzzleMat, _from, t, muzzleFlashSec, muzzleFlashSize,
                      0.7f, 0f, GlowMaterial.Scaled(hot, muzzleFlashIntensity), 2f);

            if (since >= 0f && !_burst) Burst(t);

            DrawFlash(_impactFlash, _impactMat, _to, since, impactFlashSec, impactFlashSize,
                      0.55f, 0f, GlowMaterial.Scaled(hot, impactFlashIntensity), 2f);
            DrawFlash(_cityGlow, _cityMat, _to, since, cityGlowSec, cityGlowSize,
                      0.8f, CityRiseSec, GlowMaterial.Scaled(cityGlowColor, cityGlowIntensity), 1.6f);
            DrawShockwave(since);
        }

        // The bolt: grows from the muzzle to the ground over strikeSec, then narrows and fades.
        void DrawBolt(float reach, float since, Color hot)
        {
            float k = since > 0f ? since / afterglowSec : 0f;
            if (reach < 1e-3f || k >= 1f)
            {
                _core.enabled = false;
                _glow.enabled = false;
                return;
            }

            Vector3 head = Vector3.LerpUnclamped(_from, _to, reach);
            _core.SetPosition(0, _from);
            _core.SetPosition(1, head);
            _glow.SetPosition(0, _from);
            _glow.SetPosition(1, head);

            // Narrows fast and then settles; the glow fades ahead of the core, so the bolt's last
            // moments are a thin white-hot thread rather than a smear.
            float width = Mathf.Lerp(1f, afterglowEndWidth, EaseOut(k));
            _core.widthMultiplier = coreWidth * width;
            _glow.widthMultiplier = glowWidth * width;

            Color core = GlowMaterial.Scaled(hot, coreIntensity);
            core.a = Mathf.Pow(1f - k, 1.4f);
            GlowMaterial.SetColor(_coreMat, core);

            Color glow = GlowMaterial.Scaled(laserColor, glowIntensity);
            glow.a = Mathf.Pow(1f - k, 2.2f);
            GlowMaterial.SetColor(_glowMat, glow);

            _core.enabled = true;
            _glow.enabled = true;
        }

        // The energy pulse: a dash running from the muzzle into the ground. It enters and leaves
        // whole, and is clipped to the bolt so it never runs ahead of it.
        void DrawPulse(float t, float reach, Color hot)
        {
            float half = pulseLength * 0.5f;
            float c    = Mathf.Lerp(-half, 1f + half, t / pulseSec);
            float t0   = Mathf.Clamp(c - half, 0f, reach);
            float t1   = Mathf.Clamp(c + half, 0f, reach);
            if (t >= pulseSec || t1 - t0 < 1e-3f)
            {
                _pulse.enabled = false;
                return;
            }

            _pulse.SetPosition(0, Vector3.LerpUnclamped(_from, _to, t0));
            _pulse.SetPosition(1, Vector3.LerpUnclamped(_from, _to, t1));
            _pulse.widthMultiplier = pulseWidth;

            Color p = GlowMaterial.Scaled(Color.Lerp(hot, Color.white, 0.5f), pulseIntensity);
            p.a = 1f;   // the comet gradient shapes it
            GlowMaterial.SetColor(_pulseMat, p);
            _pulse.enabled = true;
        }

        // A camera-facing glow that pops at local time 0 and is out by `sec`: it swells from
        // `swellFrom` of its size to full while its light falls away, after an optional rise.
        // Off outside that window.
        static void DrawFlash(Renderer r, Material m, Vector3 at, float local, float sec, float size,
                              float swellFrom, float riseSec, Color hdr, float fadePower)
        {
            float k = sec > 0f ? local / sec : 1f;
            if (local < 0f || k >= 1f || size <= 0f)
            {
                r.enabled = false;
                return;
            }

            float swell = size * Mathf.Lerp(swellFrom, 1f, EaseOut(k));
            float light = Mathf.Pow(1f - k, fadePower);
            if (riseSec > 0f) light *= Mathf.Clamp01(local / riseSec);

            Transform tr = r.transform;
            tr.position   = at;
            tr.localScale = new Vector3(swell, swell, 1f);

            hdr.a = light;
            GlowMaterial.SetColor(m, hdr);
            r.enabled = true;
        }

        // The shockwave: a ring bursting out from the impact and coasting to its full radius, its
        // band tightening from broad and soft to a thin bright edge as it fades.
        void DrawShockwave(float since)
        {
            float k = since / shockwaveSec;
            if (since < 0f || k >= 1f)
            {
                _ring.enabled = false;
                return;
            }

            float radius = shockwaveRadius * (1f - (1f - k) * (1f - k) * (1f - k));
            if (radius < 1e-3f)
            {
                _ring.enabled = false;
                return;
            }

            Transform tr = _ring.transform;
            tr.position   = _to;
            tr.localScale = new Vector3(radius * 2f, radius * 2f, 1f);

            _ringMat.SetFloat(GlowMaterial.RingWidthId, Mathf.Lerp(RingWidthStart, RingWidthEnd, k));
            Color c = GlowMaterial.Scaled(Color.Lerp(laserColor, Color.white, 0.5f), shockwaveIntensity);
            c.a = Mathf.Pow(1f - k, 1.5f);
            GlowMaterial.SetColor(_ringMat, c);
            _ring.enabled = true;
        }

        // The sparks and embers: once per shot, on the frame the bolt reaches the ground. The
        // physics and brightness are re-read here, so they can be tuned live between shots.
        void Burst(float t)
        {
            _burst = true;

            var sparkMain = _sparks.main;
            sparkMain.gravityModifier    = sparkGravity;
            _sparkRenderer.velocityScale = sparkStretch;
            GlowMaterial.SetColor(_sparkMat, Grey(sparkIntensity));
            Spray(_sparks, sparkCount, sparkCone, sparkSpeedMin, sparkSpeedMax,
                  sparkLifeMin, sparkLifeMax, sparkSizeMin, sparkSizeMax);

            var emberMain = _embers.main;
            emberMain.gravityModifier = emberGravity;
            GlowMaterial.SetColor(_emberMat, Grey(emberIntensity));
            Spray(_embers, emberCount, emberCone, emberSpeedMin, emberSpeedMax,
                  emberLifeMin, emberLifeMax, emberSizeMin, emberSizeMax);

            _sparkRenderer.enabled = true;
            _emberRenderer.enabled = true;

            // The particles outlive the light: the shot is not over until the last of them is.
            float life = Mathf.Max(Mathf.Max(sparkLifeMin, sparkLifeMax),
                                   Mathf.Max(emberLifeMin, emberLifeMax));
            double last = _startTime + t + life;
            if (last > _endTime) _endTime = last;
        }

        // Launches `count` particles from the impact point up and out through a cone around +Y,
        // one EmitParams at a time so every random number comes from _rng.
        void Spray(ParticleSystem ps, int count, float coneDeg, float speedMin, float speedMax,
                   float lifeMin, float lifeMax, float sizeMin, float sizeMax)
        {
            if (count <= 0) return;
            ps.Play();

            // Uniform over the cone's cap: cos θ uniform between the rim's and 1. Upward only —
            // the rooftops in front hide everything below the ground line anyway.
            float cosRim = Mathf.Cos(coneDeg * 0.5f * Mathf.Deg2Rad);
            for (int i = 0; i < count; i++)
            {
                float cosT = Mathf.Lerp(cosRim, 1f, Next());
                float sinT = Mathf.Sqrt(Mathf.Max(0f, 1f - cosT * cosT));
                float az   = Next() * (2f * Mathf.PI);

                _emit.position      = _to;
                _emit.velocity      = new Vector3(sinT * Mathf.Cos(az), cosT, sinT * Mathf.Sin(az))
                                      * Mathf.Lerp(speedMin, speedMax, Next());
                _emit.startLifetime = Mathf.Lerp(lifeMin, lifeMax, Next());
                _emit.startSize     = Mathf.Lerp(sizeMin, sizeMax, Next());
                ps.Emit(_emit, 1);
            }
        }

        // Everything off: the shot has played out, or this component was switched off mid-shot.
        void Idle()
        {
            _playing = false;
            if (_root == null) return;   // torn down with the scene ahead of this component

            _core.enabled          = false;
            _glow.enabled          = false;
            _pulse.enabled         = false;
            _muzzleFlash.enabled   = false;
            _impactFlash.enabled   = false;
            _cityGlow.enabled      = false;
            _ring.enabled          = false;
            _sparkRenderer.enabled = false;
            _emberRenderer.enabled = false;

            // Stopped as well as emptied, so neither system ticks between shots; Spray plays them
            // again.
            _sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _embers.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        // How long the light lasts — everything but the particles, which extend it at the burst.
        float LightSec() =>
            Mathf.Max(Mathf.Max(muzzleFlashSec, pulseSec),
                      strikeSec + Mathf.Max(Mathf.Max(afterglowSec, impactFlashSec),
                                            Mathf.Max(cityGlowSec, shockwaveSec)));

        float Next() => (float)_rng.NextDouble();

        static float EaseOut(float k) => 1f - (1f - k) * (1f - k);

        static Color Grey(float v) => new Color(v, v, v, 1f);

        // ── Construction ─────────────────────────────────────────────────────

        // One unit quad in the XY plane, shared by every flash. The camera never rotates, so a quad
        // facing −Z faces it wherever it is put; its UVs span 0–1 for the glow shader's shapes.
        static Mesh BuildQuad()
        {
            var m = new Mesh { name = "ShotFxQuad", hideFlags = HideFlags.DontSave };
            m.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f), new Vector3(0.5f,  0.5f, 0f),
            };
            m.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 1f), new Vector2(1f, 1f),
            };
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            m.RecalculateBounds();
            return m;
        }

        Renderer MakeQuad(string goName, GlowShape shape, float softness, out Material mat)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(_root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _quad;

            var mr = go.AddComponent<MeshRenderer>();
            mat = GlowMaterial.Create(shape, Color.white, softness, "Shot" + goName);
            mr.sharedMaterial = mat;
            NoLighting(mr);
            mr.enabled = false;
            return mr;
        }

        // A world-space two-point line. Square ends: the muzzle and impact flashes cover them.
        // Stretch maps U along the line and V across it, which is what the Line shape reads.
        LineRenderer MakeLine(string goName, float softness, out Material mat)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(_root.transform, false);

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace     = true;
            lr.positionCount     = 2;
            lr.numCapVertices    = 0;
            lr.numCornerVertices = 0;
            lr.alignment         = LineAlignment.View;
            lr.textureMode       = LineTextureMode.Stretch;
            NoLighting(lr);

            mat = GlowMaterial.Create(GlowShape.Line, Color.white, softness, "Shot" + goName);
            lr.sharedMaterial = mat;
            lr.enabled        = false;
            return lr;
        }

        ParticleSystem MakeParticles(string goName, int pool, uint seed, float softness, float shrinkTo,
                                     out Material mat, out ParticleSystemRenderer pr)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(_root.transform, false);

            var ps = go.AddComponent<ParticleSystem>();
            // A fresh system starts playing with stock settings; stop and clear it before it can.
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            // A fixed seed of its own, so nothing in it ever draws on a shared random sequence.
            ps.useAutoRandomSeed = false;
            ps.randomSeed        = seed;

            var main = ps.main;
            main.playOnAwake     = false;
            main.loop            = false;
            main.duration        = 1f;
            main.maxParticles    = pool;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.useUnscaledTime = true;
            main.cullingMode     = ParticleSystemCullingMode.AlwaysSimulate;
            main.startColor      = Color.white;

            // Every particle is placed and launched by hand (Spray): no emitter, no shape.
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size    = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, shrinkTo));

            mat = GlowMaterial.Create(GlowShape.Dot, Color.white, softness, "Shot" + goName);
            pr  = go.GetComponent<ParticleSystemRenderer>();
            pr.sharedMaterial = mat;
            NoLighting(pr);
            pr.enabled = false;
            return ps;
        }

        // Hot to cool over the first part of a particle's life, then holding while it fades out.
        static void SetLifetimeColours(ParticleSystem ps, Color hot, Color cool)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(hot, 0f), new GradientColorKey(cool, 0.4f),
                        new GradientColorKey(cool, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f),
                        new GradientAlphaKey(0f, 1f) });

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color   = new ParticleSystem.MinMaxGradient(g);
        }

        // Glow is its own light: no shadows, probes or motion vectors.
        static void NoLighting(Renderer r)
        {
            r.shadowCastingMode          = ShadowCastingMode.Off;
            r.receiveShadows             = false;
            r.lightProbeUsage            = LightProbeUsage.Off;
            r.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        static void Release(Object o)
        {
            if (o != null) Destroy(o);
        }
    }
}
