using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// The night sky above the city: faint twinkling stars, the occasional soft shooting star, a faint aurora
    /// folding slowly in the upper sky, and thin clouds drifting slowly across. Three large quads far behind
    /// the skyline — the stars on <c>JndUfo/SkyStars</c> (additive), the aurora in front of them on
    /// <c>JndUfo/SkyAurora</c> (additive), the clouds in front of both on <c>JndUfo/SkyClouds</c>
    /// (alpha-blended) — all behind the city's horizon glow, which lights the low clouds warm.
    ///
    /// Everything that moves is computed on the GPU from <c>_Time</c>: nothing runs here per frame. It is
    /// kept faint on purpose. The sky sits behind the UFO for the whole session, and every moving thing
    /// on screen also shows the frame rate, so these are slow, dim and soft: they should be felt more than
    /// watched. Nothing here knows where the hidden boundary is.
    ///
    /// Created and built by <see cref="TowerManager"/> at Start, on the City object.
    /// </summary>
    public class NightSky : MonoBehaviour
    {
        [Header("Stars")]
        public bool stars = true;
        [Tooltip("Share of the sky's cells that hold a star.")]
        [Range(0f, 1f)] public float starDensity = 0.22f;
        [Tooltip("Brightness of the brightest stars. Most are much dimmer.")]
        [Min(0f)] public float starBrightness = 0.5f;
        [Tooltip("How hard the stars twinkle, 0-1: at 1 each one dips to nothing and flares to twice its " +
                 "brightness, a few times a second, on its own irregular beat.")]
        [Range(0f, 1f)] public float twinkle = 0.85f;

        [Header("Shooting Stars")]
        public bool shootingStars = true;
        [Tooltip("Average seconds between shooting stars.")]
        [Min(2f)] public float shootingStarEvery = 12f;
        [ColorUsage(false, true)] public Color shootingStarColor = new Color(1.1f, 1.2f, 1.4f, 1f);

        [Header("Aurora")]
        [Tooltip("Faint, soft curtains of green light hanging in the upper sky, their lower edges folding slowly, " +
                 "soft rays rising from them into violet, bright and faint stretches coming and going along them.")]
        public bool aurora = true;
        [Tooltip("How bright the curtains are at their brightest. Keep it low: the UFO flies in front of them.")]
        [Range(0f, 0.5f)] public float auroraBrightness = 0.085f;
        [Tooltip("How fast the curtains fold and shimmer. 1 is slow, as a real aurora moves.")]
        [Range(0f, 3f)] public float auroraSpeed = 1f;
        [Tooltip("Colour of the curtains' lower edges (linear).")]
        [ColorUsage(false, true)] public Color auroraLow = new Color(0.10f, 0.85f, 0.42f, 1f);
        [Tooltip("Colour of the rays' upper reaches (linear).")]
        [ColorUsage(false, true)] public Color auroraHigh = new Color(0.32f, 0.20f, 0.80f, 1f);

        [Header("Clouds")]
        public bool clouds = true;
        [Tooltip("Opacity of the thickest cloud. Keep it low: they are a veil, not weather.")]
        [Range(0f, 1f)] public float cloudOpacity = 0.16f;
        [Tooltip("Drift speed, world units per second at the clouds' distance (about 0.6% of the " +
                 "screen's width per second at the default).")]
        public float cloudDrift = 2f;

        // Inside the camera's far plane (1000 from a camera at z -100, so z 900) with room to spare:
        // the parallax swing turns the camera, and a quad on the far plane would clip at its edges.
        const float StarsZ  = 860f;
        const float AuroraZ = 800f;
        const float CloudsZ = 650f;

        // Render queues: after the skybox, before the backdrop's composite (Transparent-400, see Backdrop).
        const int StarsQueue  = 2510;
        const int AuroraQueue = 2515;
        const int CloudsQueue = 2520;

        // Two shooting-star slots, the second's period this much longer than the first's, each firing
        // in a given period with this chance.
        const float SlotRatio     = 1.53f;
        const float MeteorChance  = 0.75f;

        Material _starMat, _auroraMat, _cloudMat;
        Mesh     _quad;
        bool     _built;

        // The endless world: the sky follows the camera across (see Build). The rest X it was built around, how far
        // it has been carried from there, and the shooting stars' start rectangle as built.
        bool     _follow;
        float    _originX, _carried;
        Vector4  _skyRect;

        /// <summary>Builds the sky for <paramref name="cam"/> at rest, wide enough for pans of
        /// ±<paramref name="panRange"/> around <paramref name="originX"/>. With <paramref name="followCamera"/> — the
        /// endless world — it then follows the camera across without end: every layer is drawn from world position
        /// (JndUfo/SkyStars, SkyAurora, SkyClouds), so carrying the quads along leaves the sky itself where it is in
        /// the world, sliding by as slowly as its distance makes it. Call once; with <paramref name="followCamera"/>,
        /// on an object of its own (this one is moved).</summary>
        public void Build(Camera cam, float originX, float panRange, bool followCamera = false)
        {
            if (_built || cam == null) return;
            _built   = true;
            _follow  = followCamera;
            _originX = originX;

            float camY = cam.transform.position.y, camZ = cam.transform.position.z;
            float tan  = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float asp  = Mathf.Max(cam.aspect, 16f / 9f);
            _quad = BuildQuad();

            if (stars || shootingStars)
            {
                Shader s = Shader.Find("JndUfo/SkyStars");
                if (s != null && s.isSupported)
                {
                    // Drawn just after the skybox and before the out-of-focus city is laid over the sky (see
                    // Backdrop), whatever their distance.
                    _starMat = new Material(s) { name = "SkyStars", hideFlags = HideFlags.DontSave, renderQueue = StarsQueue };
                    _starMat.SetFloat("_Density", stars ? starDensity : 0f);
                    _starMat.SetFloat("_Brightness", starBrightness);
                    _starMat.SetFloat("_Twinkle", twinkle);
                    _starMat.SetFloat("_Cell", 4.5f);

                    // Two slots, periods chosen so their combined average gap is shootingStarEvery.
                    float p1 = (1f + 1f / SlotRatio) * MeteorChance * shootingStarEvery;
                    _starMat.SetVector("_MeteorPeriods", new Vector4(p1, p1 * SlotRatio, 0f, 0f));
                    _starMat.SetFloat("_MeteorChance", shootingStars ? MeteorChance : 0f);
                    _starMat.SetColor("_MeteorColor", shootingStarColor);

                    // Shooting stars start inside the view at rest, high in the sky.
                    float hw = tan * (StarsZ - camZ) * asp;
                    _skyRect = new Vector4(originX - hw, YAt(0.3f, StarsZ, camY, camZ, tan),
                                           originX + hw, YAt(1.0f, StarsZ, camY, camZ, tan));
                    _starMat.SetVector("_SkyRect", _skyRect);
                    AddQuad("SkyStars", _starMat, StarsZ, 0.26f, 1.12f, originX, panRange, camY, camZ, tan, asp,
                            Backdrop.SortingOrder - 3);
                }
                else Debug.LogWarning("[NightSky] JndUfo/SkyStars is missing or unsupported — no stars.");
            }

            if (aurora)
            {
                Shader s = Shader.Find("JndUfo/SkyAurora");
                if (s != null && s.isSupported)
                {
                    const float v0 = 0.3f, v1 = 1.12f;
                    _auroraMat = new Material(s) { name = "SkyAurora", hideFlags = HideFlags.DontSave, renderQueue = AuroraQueue };
                    _auroraMat.SetVector("_Low",  (Vector4)auroraLow);
                    _auroraMat.SetVector("_High", (Vector4)auroraHigh);
                    _auroraMat.SetFloat("_Brightness", auroraBrightness);
                    _auroraMat.SetFloat("_Speed", auroraSpeed);
                    _auroraMat.SetFloat("_Span", 1f / Mathf.Max(1f, 2f * tan * (AuroraZ - camZ) * asp));
                    _auroraMat.SetVector("_VRange", new Vector4(v0, v1, 0f, 0f));
                    AddQuad("SkyAurora", _auroraMat, AuroraZ, v0, v1, originX, panRange, camY, camZ, tan, asp,
                            Backdrop.SortingOrder - 2);
                }
                else Debug.LogWarning("[NightSky] JndUfo/SkyAurora is missing or unsupported — no aurora.");
            }

            if (clouds)
            {
                Shader s = Shader.Find("JndUfo/SkyClouds");
                if (s != null && s.isSupported)
                {
                    _cloudMat = new Material(s) { name = "SkyClouds", hideFlags = HideFlags.DontSave, renderQueue = CloudsQueue };
                    _cloudMat.SetFloat("_Opacity", cloudOpacity);
                    _cloudMat.SetFloat("_Speed", cloudDrift);
                    AddQuad("SkyClouds", _cloudMat, CloudsZ, 0.3f, 1.1f, originX, panRange, camY, camZ, tan, asp,
                            Backdrop.SortingOrder - 1);
                }
                else Debug.LogWarning("[NightSky] JndUfo/SkyClouds is missing or unsupported — no clouds.");
            }
        }

        // The endless world: carries the quads to stay centred on the camera's rest X (CameraRig runs first, at
        // -50), and the shooting stars' start rectangle with them, so meteors still begin inside the view. Only
        // a change is written.
        void LateUpdate()
        {
            if (!_follow) return;
            CameraRig rig = CameraRig.Instance;
            float camX = rig != null ? rig.RestX : Camera.main != null ? Camera.main.transform.position.x : _originX;
            float carried = camX - _originX;
            if (carried == _carried) return;
            _carried = carried;

            Vector3 p = transform.position;
            p.x = carried;
            transform.position = p;
            if (_starMat != null)
                _starMat.SetVector("_SkyRect", _skyRect + new Vector4(carried, 0f, carried, 0f));
        }

        void OnDestroy()
        {
            if (_starMat  != null) Destroy(_starMat);
            if (_auroraMat != null) Destroy(_auroraMat);
            if (_cloudMat != null) Destroy(_cloudMat);
            if (_quad     != null) Destroy(_quad);
        }

        static float YAt(float v, float z, float camY, float camZ, float tan) =>
            camY + (2f * v - 1f) * tan * (z - camZ);

        // A camera-facing quad at depth z spanning screen heights v0..v1, as wide as the view at every pan,
        // drawn at the given sorting order: the sky goes before everything else that is transparent, the
        // out-of-focus city's composite included (see Backdrop.SortingOrder).
        void AddQuad(string goName, Material mat, float z, float v0, float v1, float originX, float panRange,
                     float camY, float camZ, float tan, float asp, int sortingOrder)
        {
            float d     = z - camZ;
            float halfW = tan * d * asp + (panRange + 10f) * d / Mathf.Max(1f, -camZ);
            float y0    = YAt(v0, z, camY, camZ, tan);
            float y1    = YAt(v1, z, camY, camZ, tan);

            var go = new GameObject(goName);
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position   = new Vector3(originX, (y0 + y1) * 0.5f, z);
            go.transform.localScale = new Vector3(halfW * 2f, y1 - y0, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = _quad;

            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial             = mat;
            r.shadowCastingMode          = ShadowCastingMode.Off;
            r.receiveShadows             = false;
            r.lightProbeUsage            = LightProbeUsage.Off;
            r.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.sortingOrder               = sortingOrder;
        }

        static Mesh BuildQuad()
        {
            var m = new Mesh { name = "SkyQuad", hideFlags = HideFlags.DontSave };
            m.vertices  = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                                  new Vector3(0.5f, 0.5f, 0f),   new Vector3(-0.5f, 0.5f, 0f) };
            m.uv        = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }
    }
}
