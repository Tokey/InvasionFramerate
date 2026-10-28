using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>A place on the skyline where sparks and embers come off a damaged building.</summary>
    public struct DamageSite
    {
        /// <summary>World point on the broken roofline.</summary>
        public Vector3 Position;
        /// <summary>Width of the break, world units.</summary>
        public float Width;
        /// <summary>How far away the site is relative to the play plane (distance ratio, 1 = the play
        /// plane). Effects scale their sizes and speeds by it so they read the same on screen at any
        /// depth.</summary>
        public float DepthScale;
        /// <summary>A shorting cable rather than a fire: blue-white crackle instead of orange sparks.</summary>
        public bool Electric;
    }

    /// <summary>
    /// The night city along the bottom of the screen, built once, procedurally, when the scene starts.
    ///
    /// Five depth layers, back to front: a sprawl packed to the horizon, a far skyline sunk in haze, a
    /// mid-far layer, the main skyline (landmark towers with beacons, and a few buildings broken open by the
    /// invasion, burning and throwing sparks), and a near layer of big dark blocks. Every building reaches
    /// down out of sight, so no ground shows anywhere — past one building there is always another — and the
    /// bottom of the screen is the foreground's (see <see cref="Foreground"/>). Every layer is ONE mesh drawn
    /// with <c>JndUfo/City</c>, which draws every window procedurally; every light in the city — beacons,
    /// crown lights, neon, street glow, the horizon's light pollution — is in one of two additive meshes on
    /// <c>JndUfo/CityLights</c>. A handful of draw calls for the whole skyline, nothing to update per frame
    /// on the CPU: the few things that move (a window fading on or off, a beacon pulsing, a fire glowing) are
    /// computed on the GPU from <c>_Time</c>. Nothing flickers fast.
    ///
    /// Out of focus: everything past the near layer — those layers, their lights and the horizon's glow — is
    /// on <see cref="Backdrop.Layer"/>, which <see cref="Backdrop"/> draws blurred behind the play plane. The
    /// near layer and its lights, just behind the towers, stay sharp with them, and so do the sparks (see
    /// <see cref="CitySparks"/>). Without a Backdrop the main camera draws the backdrop layer too, and the whole
    /// city is sharp.
    ///
    /// Optionally (<see cref="waterfront"/>), a narrow stone quay along the play plane where the towers stand,
    /// and dark water in front of it reflecting the whole city.
    ///
    /// The layout is designed around the study:
    ///   • Every building sits BEHIND the play plane (z &gt; 0), so none can ever pass in front of the UFO:
    ///     its motion is the stimulus and must always be fully visible.
    ///   • Heights are laid out in SCREEN terms (fractions of the screen's height at each building's
    ///     depth), so the composition holds whatever the camera's exact placement.
    ///   • The city is built wide enough to cover every camera pan and shake, and it never moves or
    ///     changes between rounds — it carries no information about where the hidden boundary is.
    ///   • The layout comes from <see cref="seed"/> through a private System.Random, so every participant
    ///     sees the same city and the experiment's own UnityEngine.Random sequence is never touched.
    ///
    /// Created and built by <see cref="TowerManager"/> at Start. Sparks and embers at the damage sites
    /// are <see cref="CitySparks"/>' job; it reads <see cref="DamageSites"/>.
    ///
    /// The endless world (the world scroll; see <see cref="WorldRepeat"/>): built with a period, every layer is
    /// one period of city exactly, its last building flush with the period's end, its rolling height profile
    /// wrapping round, so that copies laid a period apart join like any two neighbours. TowerManager copies the
    /// whole city a period to either side and keeps it under the camera. The period is the smallest whole number
    /// of the scroll's that is wider than the widest layer's view, so no building ever shows twice on screen. The
    /// few wide, even pieces — the two glow bands, the water and its sky — are built once, wide enough for every
    /// copy, and left uncopied (<see cref="EndlessSingles"/>): additive glows would show three times over.
    /// </summary>
    public class CitySkyline : MonoBehaviour
    {
        [Header("Layout")]
        [Tooltip("Seed for the city's layout. Fixed, so every participant sees the same city. Change it for a " +
                 "different city.")]
        public int seed = 1729;

        [Tooltip("Extra world units built past the widest view any camera pan or shake can reach, each side, at " +
                 "the play plane's depth (scaled up for deeper layers).")]
        [Min(0f)] public float sideMargin = 8f;

        [Header("Windows")]
        [ColorUsage(false, true)] public Color windowWarm = new Color(1.55f, 1.12f, 0.62f, 1f);
        [ColorUsage(false, true)] public Color windowCool = new Color(0.85f, 1.02f, 1.30f, 1f);
        [Tooltip("Overall window brightness. Above ~1 the brightest windows start to bloom.")]
        [Range(0f, 2f)] public float windowLevel = 1f;

        [Header("Atmosphere")]
        [Tooltip("The haze the deeper layers fade into. Close to the night sky's colour near the horizon.")]
        public Color hazeColor = new Color(0.11f, 0.105f, 0.18f, 1f);
        [Tooltip("Light pollution along the horizon, behind the far skyline.")]
        [ColorUsage(false, true)] public Color horizonGlow = new Color(0.40f, 0.21f, 0.14f, 1f);
        [Tooltip("Warm light from the streets at the foot of the nearest buildings, just behind the quay.")]
        [ColorUsage(false, true)] public Color streetGlow = new Color(0.46f, 0.24f, 0.09f, 1f);
        [Tooltip("Moonlight catching the top edge of every roof.")]
        public Color roofRim = new Color(0.10f, 0.12f, 0.19f, 1f);

        [Header("Lights")]
        [ColorUsage(false, true)] public Color beaconColor = new Color(4.2f, 0.32f, 0.2f, 1f);
        [Tooltip("Pulses per second of the aviation beacons on tall buildings.")]
        [Min(0f)] public float beaconHz = 0.8f;

        [Header("Searchlights")]
        [Tooltip("A few searchlight beams rising from behind the skyline and sweeping the sky, slowly, each on its " +
                 "own clock. Faint, out of focus with the far city, and behind the play plane: they never cross " +
                 "in front of the UFO.")]
        public bool searchlights = true;
        [Tooltip("The beams' light at their foot (linear, HDR); they fade along their length.")]
        [ColorUsage(false, true)] public Color searchlightColor = new Color(0.07f, 0.08f, 0.10f, 1f);

        [Header("Damage")]
        [Tooltip("Buildings broken open by the invasion: jagged rooflines, scorched, a few windows burning, " +
                 "sparks and embers coming off the break.")]
        [Range(0, 6)] public int damagedBuildings = 4;
        [ColorUsage(false, true)] public Color fireColor = new Color(3.0f, 1.15f, 0.28f, 1f);

        [Header("Waterfront")]
        [Tooltip("Frame the bottom of the screen with a waterfront: a narrow stone quay with lamps along the " +
                 "play plane — the towers stand on it, the city rises straight up behind it — and dark water " +
                 "in front of it reflecting the whole skyline. The reflection is the city drawn again, " +
                 "mirrored under the water, so it is exact and costs a few draw calls, no extra camera. Off by " +
                 "default: the foreground's grass covers the bottom of the screen instead (see Foreground), " +
                 "and the towers stand in it.")]
        public bool waterfront = false;
        [Tooltip("How far below the ground line the water lies, world units: the height of the embankment.")]
        [Min(0.05f)] public float waterDrop = 0.55f;
        [Tooltip("Brightness of the reflection relative to the city itself.")]
        [Range(0f, 1f)] public float reflectionLevel = 0.55f;
        [Tooltip("How far reflected windows waver, world units.")]
        [Min(0f)] public float reflectionRipple = 0.08f;
        [Tooltip("How far each reflected light is drawn out up and down, world units — a light's reflection " +
                 "on moving water is a streak, not a dot.")]
        [Min(0f)] public float lightStreak = 1.2f;
        [Tooltip("Distance between the lamps along the embankment, world units.")]
        [Min(0.5f)] public float lampSpacing = 3.2f;
        [ColorUsage(false, true)] public Color lampColor = new Color(1.9f, 1.25f, 0.6f, 1f);

        /// <summary>Where the damaged buildings are broken open — read by <see cref="CitySparks"/>.</summary>
        public IReadOnlyList<DamageSite> DamageSites => _sites;

        /// <summary>True once <see cref="Build"/> has run.</summary>
        public bool Built { get; private set; }

        /// <summary>The length of city that repeats in the endless world (world units); 0 when not endless.</summary>
        public float EndlessPeriod { get; private set; }

        /// <summary>In the endless world, the city's pieces that must not be copied a period to either side: they are
        /// built wide enough for every copy already. Empty when not endless.</summary>
        public IReadOnlyList<GameObject> EndlessSingles => _singles;

        // ── Layers ───────────────────────────────────────────────────────────

        enum Role { Near, Main, Far }

        struct Layer
        {
            public string Name;
            public Role   Role;
            public float  Z, ZJitter;            // front face depth, plus up to this much more per building
            public float  VMin, VMax;            // roofline, as a fraction of the screen's height
            public float  WMin, WMax;            // building width, as a fraction of the screen's width there
            public float  GapMin, GapMax;        // gap to the next building, same units (negative = overlap)
            public float  DepthMin, DepthMax;    // building depth, world units
            public Color  FacadeA, FacadeB;
            public float  Haze;                  // how far the layer fades into the haze colour
            public float  HazeTop;               // haze weight at the roof (1 at the street — haze sits low)
            public float  Cell;                  // window cell height, world units
            public float  LitMin, LitMax;
            public float  Brightness;
            public float  Profile;               // how strongly the skyline's rolling height profile applies
            public bool   Sides;                 // draw the side walls (the foreground shows flat fronts)
            public float  BaseV;                 // screen height the buildings stand on (below the screen)
            public float  BaseShade;             // facade brightness at the base, fading up to full at the roof
            public float  BaseWindow;            // window brightness at the base, likewise
            public bool   Details;               // roof furniture: antennas (some with beacons), tanks, signs
        }

        // Back to front, then the sprawl — built last so the rest of the city keeps the layout its seed has
        // always given it. Every building reaches down out of sight (BaseV below the screen), so there is no
        // ground anywhere: whatever shows past one building — over its roof or through a gap — is another,
        // further back, and the back of the city is the sprawl, packed with no gaps at all, its roofs just
        // under the horizon.
        static readonly Layer[] Layers =
        {
            new Layer { Name = "Far", Role = Role.Far, Z = 235f, ZJitter = 30f, VMin = 0.36f, VMax = 0.49f,
                        WMin = 0.020f, WMax = 0.050f, GapMin = -0.004f, GapMax = 0.022f, DepthMin = 10f, DepthMax = 22f,
                        FacadeA = new Color(0.060f, 0.060f, 0.090f), FacadeB = new Color(0.078f, 0.074f, 0.105f),
                        Haze = 0.70f, HazeTop = 0.60f, Cell = 1.25f, LitMin = 0.10f, LitMax = 0.30f, Brightness = 0.65f,
                        Profile = 0.8f, Sides = true, BaseV = -0.3f, BaseShade = 1f, BaseWindow = 1f, Details = true },
            new Layer { Name = "MidFar", Role = Role.Far, Z = 115f, ZJitter = 18f, VMin = 0.33f, VMax = 0.46f,
                        WMin = 0.024f, WMax = 0.060f, GapMin = -0.004f, GapMax = 0.020f, DepthMin = 8f, DepthMax = 15f,
                        FacadeA = new Color(0.045f, 0.046f, 0.070f), FacadeB = new Color(0.062f, 0.060f, 0.086f),
                        Haze = 0.44f, HazeTop = 0.50f, Cell = 0.95f, LitMin = 0.16f, LitMax = 0.40f, Brightness = 0.82f,
                        Profile = 0.7f, Sides = true, BaseV = -0.3f, BaseShade = 1f, BaseWindow = 1f, Details = true },
            new Layer { Name = "Main", Role = Role.Main, Z = 48f, ZJitter = 10f, VMin = 0.28f, VMax = 0.43f,
                        WMin = 0.030f, WMax = 0.072f, GapMin = -0.003f, GapMax = 0.018f, DepthMin = 6f, DepthMax = 12f,
                        FacadeA = new Color(0.030f, 0.033f, 0.050f), FacadeB = new Color(0.050f, 0.050f, 0.070f),
                        Haze = 0.20f, HazeTop = 0.35f, Cell = 0.78f, LitMin = 0.22f, LitMax = 0.55f, Brightness = 1.0f,
                        Profile = 0.6f, Sides = true, BaseV = -0.3f, BaseShade = 1f, BaseWindow = 1f, Details = true },
            new Layer { Name = "Near", Role = Role.Near, Z = 14f, ZJitter = 6f, VMin = 0.25f, VMax = 0.36f,
                        WMin = 0.045f, WMax = 0.100f, GapMin = -0.010f, GapMax = 0.016f, DepthMin = 5f, DepthMax = 9f,
                        FacadeA = new Color(0.020f, 0.022f, 0.034f), FacadeB = new Color(0.034f, 0.034f, 0.050f),
                        Haze = 0.10f, HazeTop = 0.40f, Cell = 0.92f, LitMin = 0.14f, LitMax = 0.36f, Brightness = 1.0f,
                        Profile = 0.5f, Sides = true, BaseV = -0.3f, BaseShade = 1f, BaseWindow = 1f, Details = true },
            new Layer { Name = "Sprawl", Role = Role.Far, Z = 400f, ZJitter = 50f, VMin = 0.44f, VMax = 0.485f,
                        WMin = 0.012f, WMax = 0.032f, GapMin = -0.008f, GapMax = -0.001f, DepthMin = 10f, DepthMax = 25f,
                        FacadeA = new Color(0.070f, 0.068f, 0.100f), FacadeB = new Color(0.085f, 0.080f, 0.112f),
                        Haze = 0.93f, HazeTop = 0.85f, Cell = 1.8f, LitMin = 0.06f, LitMax = 0.16f, Brightness = 0.5f,
                        Profile = 0.9f, Sides = true, BaseV = -0.3f, BaseShade = 1f, BaseWindow = 1f, Details = false },
        };

        // The quay the towers stand on runs from this far in front of the play plane (its stone face, the
        // water's far edge) to this far behind it.
        const float QuayFront = 2.5f;
        const float QuayBack  = 3f;

        // Where, as fractions of the screen's width from the left at the starting camera position, the
        // main layer's landmarks and broken buildings stand — spread so no part of the screen is special.
        static readonly float[] LandmarkAt = { 0.17f, 0.53f, 0.86f };
        static readonly float[] DamageAt   = { 0.36f, 0.71f, 0.07f, 0.95f, 0.24f, 0.62f };

        // ── Private ──────────────────────────────────────────────────────────
        readonly List<DamageSite> _sites     = new List<DamageSite>();
        readonly List<Material>   _materials = new List<Material>();
        readonly List<Mesh>       _meshes    = new List<Mesh>();
        readonly List<GameObject> _singles   = new List<GameObject>();

        // How far past the view, at the play plane's depth, the endless world's single pieces reach: the camera's
        // shake, handheld drift and parallax swing, with room to spare. Scaled up with depth like the view.
        const float EndlessMargin = 12f;

        System.Random _rng;
        float _camY, _camZ, _tanHalf, _aspect, _groundY, _playZ;
        float _waterEdgeZ;   // the water's far edge, at the foot of the quay's stone face

        static readonly int WindowWarmId  = Shader.PropertyToID("_WindowWarm");
        static readonly int WindowCoolId  = Shader.PropertyToID("_WindowCool");
        static readonly int FireColorId   = Shader.PropertyToID("_FireColor");
        static readonly int RimColorId    = Shader.PropertyToID("_RimColor");
        static readonly int HazeColorId   = Shader.PropertyToID("_HazeColor");
        static readonly int HazeId        = Shader.PropertyToID("_Haze");
        static readonly int WindowLevelId = Shader.PropertyToID("_WindowLevel");

        /// <summary>The window and facade look of one building, baked into its vertices.</summary>
        struct Facade
        {
            public Color Rgb;
            public float CellW, CellH, Lit, Seed;
            public float GlassW, GlassH, Tone, Brightness;
            public float Damage, Fire, Scorch;
            public float HazeTop;
            public float UOffset, VOffset;
            public bool  Sides;
            public float BaseShade, BaseWindow;
        }

        /// <summary>
        /// Builds the city for <paramref name="cam"/> as it stands now (its rest position), wide enough for
        /// pans of ±<paramref name="panRange"/> around <paramref name="originX"/>. The ground line sits at
        /// <paramref name="groundY"/> on the play plane <paramref name="playZ"/>. With
        /// <paramref name="endlessBasePeriod"/>, one period of an endless city instead (see the class summary),
        /// its period a whole number of that one. Runs once; later calls do nothing.
        /// </summary>
        public void Build(Camera cam, float groundY, float playZ, float originX, float panRange,
                          float endlessBasePeriod = 0f)
        {
            if (Built || cam == null) return;
            Built = true;

            _rng     = new System.Random(seed);
            _camY    = cam.transform.position.y;
            _camZ    = cam.transform.position.z;
            _tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            _aspect  = Mathf.Max(cam.aspect, 16f / 9f);   // never build narrower than 16:9
            _groundY = groundY;
            _playZ   = playZ;
            _waterEdgeZ = playZ - QuayFront;
            _sites.Clear();

            // The endless world's period: the smallest whole number of the scroll's that is wider than the widest
            // layer's view, so no layer ever shows the same building twice on screen.
            EndlessPeriod = 0f;
            if (endlessBasePeriod > 0f)
            {
                float widest = 0f;
                foreach (Layer layer in Layers) widest = Mathf.Max(widest, ScreenWidthAt(layer.Z + layer.ZJitter));
                EndlessPeriod = endlessBasePeriod * Mathf.Max(1, Mathf.CeilToInt(widest / endlessBasePeriod));
            }
            bool endless = EndlessPeriod > 0f;

            Shader cityShader  = Shader.Find("JndUfo/City");
            Shader lightShader = Shader.Find("JndUfo/CityLights");
            bool cityOk  = cityShader  != null && cityShader.isSupported;
            bool lightOk = lightShader != null && lightShader.isSupported;
            if (!cityOk || !lightOk)
                Debug.LogWarning("[CitySkyline] The city shaders are missing or unsupported here — the skyline " +
                                 "falls back to flat silhouettes.");

            // Two meshes of lights: those of the near layer (and the street glow at its foot), drawn sharp with
            // it, and all the rest, drawn out of focus with the far city on the backdrop layer.
            var lights     = new MeshBuilder(2048);
            var nearLights = new MeshBuilder(512);
            int buildings = 0;

            // The waterfront needs the city's own shader for its mirrored copy; without it, no water.
            bool water  = waterfront && cityOk && lightOk;
            float waterY = groundY - waterDrop;

            foreach (Layer layer in Layers)
            {
                bool far = layer.Role != Role.Near;
                var mb = new MeshBuilder(8192);
                buildings += BuildLayer(layer, mb, far ? lights : nearLights, originX, panRange);

                Material mat = cityOk ? new Material(cityShader) : new Material(Shader.Find("Sprites/Default"));
                mat.name      = "City_" + layer.Name;
                mat.hideFlags = HideFlags.DontSave;
                if (cityOk)
                {
                    mat.SetColor(WindowWarmId, windowWarm);
                    mat.SetColor(WindowCoolId, windowCool);
                    mat.SetColor(FireColorId, fireColor);
                    mat.SetColor(RimColorId, roofRim);
                    mat.SetColor(HazeColorId, hazeColor);
                    mat.SetFloat(HazeId, layer.Haze);
                    mat.SetFloat(WindowLevelId, windowLevel);
                }
                _materials.Add(mat);

                Mesh mesh = mb.ToMesh("City_" + layer.Name);
                if (water) SetClip(mat, waterY, reflection: false);   // the feet, out of sight, must not show through the water
                AddRenderer("City_" + layer.Name, mesh, mat, far);
                if (water) AddReflection("Reflect_" + layer.Name, mesh, ReflectionOf(mat, waterY, reflectionRipple), waterY, far);
            }

            if (water) BuildEmbankment(nearLights, cityShader, originX, panRange, waterY);

            // Endless, the glows go in meshes of their own, left uncopied (see EndlessSingles).
            MeshBuilder glowFar  = endless ? new MeshBuilder(4) : lights;
            MeshBuilder glowNear = endless ? new MeshBuilder(4) : nearLights;
            AddGlowBands(glowFar, glowNear, originX, panRange);
            if (searchlights) AddSearchlights(lights, originX);

            if (lightOk)
            {
                var lightMat = new Material(lightShader) { name = "CityLights", hideFlags = HideFlags.DontSave };
                _materials.Add(lightMat);
                if (water) SetClip(lightMat, waterY, reflection: false);
                Material refl = null;
                if (water)
                {
                    refl = ReflectionOf(lightMat, waterY, 0f);
                    refl.SetFloat(StreakId, lightStreak);
                }

                Mesh farMesh  = lights.ToMesh("City_Lights");
                Mesh nearMesh = nearLights.ToMesh("City_LightsNear");
                AddRenderer("City_Lights",     farMesh,  lightMat, far: true);
                AddRenderer("City_LightsNear", nearMesh, lightMat, far: false);
                if (water)
                {
                    AddReflection("Reflect_Lights",     farMesh,  refl, waterY, far: true);
                    AddReflection("Reflect_LightsNear", nearMesh, refl, waterY, far: false);
                }

                if (endless)
                {
                    Mesh glowFarMesh  = glowFar.ToMesh("City_GlowFar");
                    Mesh glowNearMesh = glowNear.ToMesh("City_GlowNear");
                    _singles.Add(AddRenderer("City_GlowFar",  glowFarMesh,  lightMat, far: true));
                    _singles.Add(AddRenderer("City_GlowNear", glowNearMesh, lightMat, far: false));
                    if (water)
                    {
                        _singles.Add(AddReflection("Reflect_GlowFar",  glowFarMesh,  refl, waterY, far: true));
                        _singles.Add(AddReflection("Reflect_GlowNear", glowNearMesh, refl, waterY, far: false));
                    }
                }
            }

            if (water) AddWater(originX, panRange, waterY);

            Debug.Log($"[CitySkyline] Built {buildings} buildings in {Layers.Length} layers, " +
                      $"{(lights.VertexCount + nearLights.VertexCount) / 4} lights, {_sites.Count} damage sites " +
                      $"(seed {seed}){(water ? ", with the waterfront" : "")}" +
                      (endless ? $", one {EndlessPeriod:0.0}-unit period of an endless city." : "."));
        }

        // The half-width a single piece of the endless city needs at depth z: the period, the view and room for the
        // camera's shake, drift and swing either side of the strip's middle, which the strip keeps within half a
        // period of the camera.
        float EndlessHalfWidth(float z) =>
            EndlessPeriod * 0.5f + ScreenWidthAt(z) * 0.5f + EndlessMargin * DepthScale(z);

        // ── Waterfront ───────────────────────────────────────────────────────

        static readonly int ClipYId      = Shader.PropertyToID("_ClipY");
        static readonly int WaterEdgeZId = Shader.PropertyToID("_WaterEdgeZ");
        static readonly int ClipSideId   = Shader.PropertyToID("_ClipSide");
        static readonly int ReflectDimId = Shader.PropertyToID("_ReflectDim");
        static readonly int RippleId     = Shader.PropertyToID("_Ripple");
        static readonly int StreakId     = Shader.PropertyToID("_Streak");

        // The city keeps everything the camera does not see through the water; its reflection keeps only
        // what it does (see JndUfo/City). The two never overlap, so the buildings can reach down out of
        // sight behind the quay and still never fight their own reflection.
        void SetClip(Material m, float waterY, bool reflection)
        {
            m.SetFloat(ClipYId, waterY);
            m.SetFloat(WaterEdgeZId, _waterEdgeZ);
            m.SetFloat(ClipSideId, reflection ? -1f : 1f);
        }

        // The material for a layer's reflection: the same look, seen only in the water, dimmer, its windows
        // wavering.
        Material ReflectionOf(Material source, float waterY, float ripple)
        {
            var m = new Material(source) { name = source.name + "_Reflection", hideFlags = HideFlags.DontSave };
            SetClip(m, waterY, reflection: true);
            m.SetFloat(ReflectDimId, reflectionLevel);
            m.SetFloat(RippleId, ripple);
            _materials.Add(m);
            return m;
        }

        // A layer's mesh drawn again, mirrored in the plane of the water: its exact reflection. It lies under
        // the water's surface, so the surface (drawn over it, part-transparent) decides how much of it shows,
        // and its material keeps only what is seen through the water (see SetClip). A far layer's reflection
        // is as far away, optically, as the layer: out of focus with it.
        GameObject AddReflection(string goName, Mesh mesh, Material mat, float waterY, bool far)
        {
            var go = new GameObject(goName) { layer = far ? Backdrop.Layer : 0 };
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position   = new Vector3(0f, 2f * waterY, 0f);
            go.transform.localScale = new Vector3(1f, -1f, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial             = mat;
            r.shadowCastingMode          = ShadowCastingMode.Off;
            r.receiveShadows             = false;
            r.lightProbeUsage            = LightProbeUsage.Off;
            r.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.allowOcclusionWhenDynamic  = false;
            return go;
        }

        /// <summary>
        /// The quay the towers stand on: a narrow stone ledge along the play plane, its face dropping to the
        /// water just in front, a row of lamps along its edge, and — far behind and below everything — the
        /// night sky as the water reflects it, so a view down into the water never meets the skybox's
        /// underside. There is no ground beyond the ledge: the buildings rise straight up behind it, reaching
        /// down out of sight, so no pavement shows anywhere in the city.
        /// </summary>
        void BuildEmbankment(MeshBuilder lights, Shader cityShader, float originX, float panRange, float waterY)
        {
            var mb = new MeshBuilder(1024);
            float edgeZ   = _waterEdgeZ;   // in front of every tower's base
            float coping  = 0.07f;

            // The ledge: dark stone, a touch warmer at its front edge where the lamps fall on it. Just deep
            // enough for the widest tower's footing.
            float backZ  = _playZ + QuayBack;
            float nearHW = ScreenWidthAt(edgeZ) * 0.5f + panRange + sideMargin;
            float backHW = ScreenWidthAt(backZ) * 0.5f + (panRange + sideMargin) * DepthScale(backZ);
            if (EndlessPeriod > 0f) nearHW = backHW = EndlessPeriod * 0.5f;   // one period, copied a period apart
            var ledgeFront = new Color(0.034f, 0.030f, 0.032f);
            var ledgeBack  = new Color(0.010f, 0.011f, 0.018f);
            mb.Quad(Plain(mb, new Vector3(originX - nearHW, _groundY, edgeZ), ledgeFront),
                    Plain(mb, new Vector3(originX + nearHW, _groundY, edgeZ), ledgeFront),
                    Plain(mb, new Vector3(originX + backHW, _groundY, backZ), ledgeBack),
                    Plain(mb, new Vector3(originX - backHW, _groundY, backZ), ledgeBack));

            // The stone face, lit warm at the top by the lamps, darkening toward the water; a pale coping
            // along its edge.
            var faceTop = new Color(0.030f, 0.026f, 0.028f);
            var faceLow = new Color(0.008f, 0.009f, 0.015f);
            mb.Quad(Plain(mb, new Vector3(originX - nearHW, waterY, edgeZ), faceLow),
                    Plain(mb, new Vector3(originX + nearHW, waterY, edgeZ), faceLow),
                    Plain(mb, new Vector3(originX + nearHW, _groundY - coping, edgeZ), faceTop),
                    Plain(mb, new Vector3(originX - nearHW, _groundY - coping, edgeZ), faceTop));
            var copingCol = new Color(0.085f, 0.075f, 0.072f);
            mb.Quad(Plain(mb, new Vector3(originX - nearHW, _groundY - coping, edgeZ - 0.02f), copingCol),
                    Plain(mb, new Vector3(originX + nearHW, _groundY - coping, edgeZ - 0.02f), copingCol),
                    Plain(mb, new Vector3(originX + nearHW, _groundY, edgeZ - 0.02f), copingCol),
                    Plain(mb, new Vector3(originX - nearHW, _groundY, edgeZ - 0.02f), copingCol));

            // Lamps along the edge: slim posts, warm heads.
            var post = new Color(0.010f, 0.011f, 0.016f);
            const float postH = 0.95f, postW = 0.05f;
            float lampZ = edgeZ + 0.25f;
            // Endless, a whole number of lamps to the period, so the row runs on evenly across every copy.
            float spacing = EndlessPeriod > 0f
                ? EndlessPeriod / Mathf.Max(1, Mathf.RoundToInt(EndlessPeriod / lampSpacing))
                : lampSpacing;
            float phase = Rand(0f, spacing);
            for (float lx = originX - nearHW + phase; lx < originX + nearHW; lx += spacing)
            {
                mb.Quad(Plain(mb, new Vector3(lx - postW, _groundY, lampZ), post),
                        Plain(mb, new Vector3(lx + postW, _groundY, lampZ), post),
                        Plain(mb, new Vector3(lx + postW * 0.6f, _groundY + postH, lampZ), post),
                        Plain(mb, new Vector3(lx - postW * 0.6f, _groundY + postH, lampZ), post));
                AddLight(lights, new Vector3(lx, _groundY + postH + 0.05f, lampZ - 0.05f), 0.2f, 0.2f, 0f, 2.4f,
                         lampColor * 0.55f, 0f, 0f, 1f, 0f);
            }

            var mat = new Material(cityShader) { name = "City_Embankment", hideFlags = HideFlags.DontSave };
            _materials.Add(mat);
            AddRenderer("City_Embankment", mb.ToMesh("City_Embankment"), mat, far: false);

            // The reflected sky: far behind everything and all below the water, dark navy deepening with
            // depth. The horizon glow's own reflection (in the mirrored lights) lies over its top. Its own mesh,
            // out of focus with the far city's reflections that lie over it.
            var sky = new MeshBuilder(4);
            const float skyZ = 700f;
            float skyHW = EndlessPeriod > 0f ? EndlessHalfWidth(skyZ)
                                             : ScreenWidthAt(skyZ) * 0.5f + (panRange + sideMargin) * DepthScale(skyZ);
            var skyTop = new Color(0.040f, 0.040f, 0.060f);
            var skyLow = new Color(0.008f, 0.010f, 0.018f);
            sky.Quad(Plain(sky, new Vector3(originX - skyHW, waterY - 900f, skyZ), skyLow),
                     Plain(sky, new Vector3(originX + skyHW, waterY - 900f, skyZ), skyLow),
                     Plain(sky, new Vector3(originX + skyHW, waterY, skyZ), skyTop),
                     Plain(sky, new Vector3(originX - skyHW, waterY, skyZ), skyTop));
            GameObject skyGo = AddRenderer("City_SkyReflection", sky.ToMesh("City_SkyReflection"), mat, far: true);
            if (EndlessPeriod > 0f) _singles.Add(skyGo);   // even across, and wide enough for every copy
        }

        /// <summary>The water's surface: from just under the embankment out past the bottom of the screen.</summary>
        void AddWater(float originX, float panRange, float waterY)
        {
            Shader s = Shader.Find("JndUfo/Water");
            if (s == null || !s.isSupported)
            {
                Debug.LogWarning("[CitySkyline] JndUfo/Water is missing or unsupported — the reflection shows bare.");
                return;
            }

            float edgeZ = _waterEdgeZ;
            const float nearZ = -85f;   // past the bottom of the screen, camera shake included
            float hw = EndlessPeriod > 0f ? EndlessHalfWidth(edgeZ) : ScreenWidthAt(edgeZ) * 0.5f + panRange + sideMargin;

            var mb = new MeshBuilder(8);
            int a = mb.AddLight(new Vector3(originX - hw, waterY, nearZ), Vector4.zero, Vector4.zero, Vector4.zero);
            int b = mb.AddLight(new Vector3(originX + hw, waterY, nearZ), Vector4.zero, Vector4.zero, Vector4.zero);
            int c = mb.AddLight(new Vector3(originX + hw, waterY, edgeZ), Vector4.zero, Vector4.zero, Vector4.zero);
            int d = mb.AddLight(new Vector3(originX - hw, waterY, edgeZ), Vector4.zero, Vector4.zero, Vector4.zero);
            mb.Quad(a, b, c, d);

            var mat = new Material(s) { name = "Water", hideFlags = HideFlags.DontSave };
            mat.SetFloat("_NearZ", -60f);
            mat.SetFloat("_FarZ", edgeZ);
            _materials.Add(mat);
            GameObject waterGo = AddRenderer("City_Water", mb.ToMesh("City_Water"), mat, far: false);
            if (EndlessPeriod > 0f) _singles.Add(waterGo);   // its ripples go by world position: one wide quad
        }

        // A vertex with a flat colour and no windows, rim or haze — for the embankment's plain surfaces.
        static int Plain(MeshBuilder mb, Vector3 p, Color c)
        {
            c.a = 0f;   // no haze
            return mb.Add(p, c,
                          new Vector4(0f, 0f, 1e6f, 0f),     // roof far above: no rim; no windows
                          new Vector4(1f, 1f, 0f, 0f),
                          Vector4.zero,
                          new Vector4(0f, 0f, 0f, 1f));
        }

        void OnDestroy()
        {
            foreach (Material m in _materials) if (m != null) Destroy(m);
            foreach (Mesh m in _meshes) if (m != null) Destroy(m);
            _materials.Clear();
            _meshes.Clear();
        }

        // ── Geometry helpers ─────────────────────────────────────────────────

        float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);
        bool  Chance(float p)        => _rng.NextDouble() < p;

        /// <summary>World y at screen height <paramref name="v"/> (0 bottom, 1 top) for depth z.</summary>
        float YAt(float v, float z) => _camY + (2f * v - 1f) * _tanHalf * (z - _camZ);

        /// <summary>Width of the screen in world units at depth z.</summary>
        float ScreenWidthAt(float z) => 2f * _tanHalf * (z - _camZ) * _aspect;

        /// <summary>Distance ratio of depth z to the play plane: 1 there, larger further back.</summary>
        float DepthScale(float z) => (z - _camZ) / Mathf.Max(0.01f, _playZ - _camZ);

        // far: on the backdrop layer, drawn out of focus (see Backdrop).
        GameObject AddRenderer(string goName, Mesh mesh, Material mat, bool far)
        {
            _meshes.Add(mesh);
            var go = new GameObject(goName) { layer = far ? Backdrop.Layer : 0 };
            go.transform.SetParent(transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial             = mat;
            r.shadowCastingMode          = ShadowCastingMode.Off;
            r.receiveShadows             = false;
            r.lightProbeUsage            = LightProbeUsage.Off;
            r.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.allowOcclusionWhenDynamic  = false;
            return go;
        }

        // ── Layers ───────────────────────────────────────────────────────────

        int BuildLayer(in Layer L, MeshBuilder mb, MeshBuilder lights, float originX, float panRange)
        {
            float screenW  = ScreenWidthAt(L.Z);
            float ds       = DepthScale(L.Z);
            float margin   = (panRange + 2f + sideMargin) * ds;
            float xMin     = originX - screenW * 0.5f - margin;
            float xMax     = originX + screenW * 0.5f + margin;
            float profileX = Rand(0f, 1000f);
            float profileY = Rand(0f, 1000f);

            // Endless: exactly one period, so the copies laid a period to either side join it like any two
            // neighbours (see the class summary).
            bool endless = EndlessPeriod > 0f;
            if (endless)
            {
                xMin = originX - EndlessPeriod * 0.5f;
                xMax = originX + EndlessPeriod * 0.5f;
            }

            // Landmarks and broken buildings stand at fixed fractions of the starting view.
            bool[] landmarkDone = new bool[LandmarkAt.Length];
            int damageWanted = L.Role == Role.Main ? Mathf.Max(0, damagedBuildings - 1)
                             : L.Role == Role.Near ? Mathf.Min(1, damagedBuildings) : 0;
            bool[] damageDone = new bool[DamageAt.Length];
            int damageMade = 0;

            int count = 0;
            float x = endless ? xMin : xMin - Rand(L.WMin, L.WMax) * screenW;
            while (x < xMax)
            {
                // Endless, too little room left for a building is left as a gap like any other.
                if (endless && xMax - x < L.WMin * screenW * 0.5f) break;

                float w = Rand(L.WMin, L.WMax) * screenW;
                // Endless, the period's last building ends exactly at its edge, so the next copy's first stands flush
                // with it: stretched to the edge when it would leave too little room for another, cut to it when it
                // would pass it. It is the last: the gaps can be negative (overlaps), so the walk must stop on it
                // rather than step back under the edge.
                bool last = endless && (x + w > xMax || xMax - (x + w) < L.WMin * screenW);
                if (last) w = xMax - x;
                float z = L.Z + Rand(0f, L.ZJitter);

                float profile = endless ? WrappedProfile(x - xMin, ds, profileX, profileY)
                                        : Mathf.PerlinNoise(profileX + x * 0.045f / ds, profileY);
                float v = Mathf.Lerp(L.VMin, L.VMax, Mathf.Lerp((float)_rng.NextDouble(), profile, L.Profile));

                // Is this the building at one of the landmark or damage positions?
                bool landmark = false, damaged = false;
                if (L.Role == Role.Main)
                {
                    for (int i = 0; i < LandmarkAt.Length; i++)
                    {
                        float lx = originX + (LandmarkAt[i] - 0.5f) * screenW;
                        if (!landmarkDone[i] && lx >= x && lx < x + w * 1.6f)
                        {
                            landmarkDone[i] = true;
                            landmark = true;
                            break;
                        }
                    }
                }
                if (!landmark && damageMade < damageWanted)
                {
                    int start = L.Role == Role.Near ? DamageAt.Length - 1 : 0;
                    for (int i = start; i < DamageAt.Length && damageMade < damageWanted; i++)
                    {
                        float dx = originX + (DamageAt[i] - 0.5f) * screenW * 0.92f;
                        if (!damageDone[i] && dx >= x && dx < x + w)
                        {
                            damageDone[i] = true;
                            damaged = true;
                            damageMade++;
                            break;
                        }
                    }
                }

                if (landmark)
                {
                    w = Mathf.Max(w, Rand(0.055f, 0.075f) * screenW);
                    v = Rand(0.47f, 0.55f);
                    if (endless && x + w > xMax) w = xMax - x;
                }

                float depth = Rand(L.DepthMin, L.DepthMax);
                float yTop  = YAt(v, z);
                float yBase = YAt(L.BaseV, z);   // below the screen even with the camera shaking
                Facade f    = MakeFacade(L, damaged);

                if (landmark)      EmitLandmark(mb, lights, x, x + w, yBase, yTop, z, depth, f, ds);
                else if (damaged)  EmitBroken(mb, lights, x, x + w, yBase, yTop, z, depth, f, ds, L.Role == Role.Near);
                else
                {
                    EmitBox(mb, x, x + w, yBase, yTop, z, depth, f);
                    if (L.Details) EmitRoofDetails(mb, lights, L, x, x + w, yTop, z, f, ds);
                }

                count++;
                if (last) break;
                x += w + Rand(L.GapMin, L.GapMax) * screenW;
            }

            return count;
        }

        // The skyline's rolling height profile wrapped round the endless city's period: the same noise, sampled round a
        // circle whose circumference is the period in the noise's own units, so it rolls as it does along a line and
        // meets itself where the period does. `along` is world units from the period's left end.
        float WrappedProfile(float along, float ds, float px, float py)
        {
            float a = 2f * Mathf.PI * along / EndlessPeriod;
            float r = EndlessPeriod * 0.045f / ds / (2f * Mathf.PI);
            return Mathf.PerlinNoise(px + r * Mathf.Cos(a), py + r * Mathf.Sin(a));
        }

        Facade MakeFacade(in Layer L, bool damaged)
        {
            var f = new Facade
            {
                Rgb        = Color.Lerp(L.FacadeA, L.FacadeB, (float)_rng.NextDouble()),
                CellH      = L.Cell * Rand(0.85f, 1.25f),
                Seed       = _rng.Next(0, 900),   // a whole number: JndUfo/City rounds it, see there
                GlassW     = Rand(0.42f, 0.70f),
                GlassH     = Rand(0.40f, 0.66f),
                Tone       = Chance(0.18f) ? Rand(0.6f, 1f) : Rand(0f, 0.35f),
                Brightness = L.Brightness * Rand(0.8f, 1.15f),
                Lit        = Rand(L.LitMin, L.LitMax),
                HazeTop    = L.HazeTop,
                UOffset    = Rand(0f, 3f),
                VOffset    = Rand(0f, 3f),
                Sides      = L.Sides,
                BaseShade  = L.BaseShade,
                BaseWindow = L.BaseWindow,
            };
            f.CellW = f.CellH * Rand(0.6f, 1.0f);

            // A few office blocks with ribbon windows or vertical strips; a few nearly dark, a few blazing.
            int style = _rng.Next(10);
            if (style == 0)      f.GlassW = 0.94f;
            else if (style == 1) f.GlassH = 0.92f;
            if (Chance(0.10f))      f.Lit *= 0.25f;
            else if (Chance(0.10f)) f.Lit = Mathf.Min(0.85f, f.Lit * 1.7f);

            if (damaged)
            {
                f.Damage = 1f;
                f.Fire   = 0.32f;
                f.Lit   *= 0.6f;
            }
            return f;
        }

        // ── Buildings ────────────────────────────────────────────────────────

        /// <summary>A plain block: front face, both sides, flat roof.</summary>
        void EmitBox(MeshBuilder mb, float x0, float x1, float yBase, float yTop, float z, float depth, in Facade f,
                     bool roof = true)
        {
            float z1 = z + depth;
            float roofV = V(yTop, f);

            // Front.
            int a = Vert(mb, new Vector3(x0, yBase, z), U(x0 - x0, f), V(yBase, f), roofV, 1f, 1f, f, 1f, atBase: true);
            int b = Vert(mb, new Vector3(x1, yBase, z), U(x1 - x0, f), V(yBase, f), roofV, 1f, 1f, f, 1f, atBase: true);
            int c = Vert(mb, new Vector3(x1, yTop,  z), U(x1 - x0, f), roofV,       roofV, f.HazeTop, 1f, f, 1f);
            int d = Vert(mb, new Vector3(x0, yTop,  z), U(x0 - x0, f), roofV,       roofV, f.HazeTop, 1f, f, 1f);
            mb.Quad(a, b, c, d);

            // Sides — whichever one faces the camera after a pan is the one that shows; the other is
            // hidden behind the front face.
            if (f.Sides)
            {
                SideFace(mb, x0, yBase, yTop, z, z1, roofV, 0.62f, f);
                SideFace(mb, x1, yBase, yTop, z, z1, roofV, 0.50f, f);
            }

            if (!roof) return;
            int e = Vert(mb, new Vector3(x0, yTop, z),  0f, roofV, roofV, f.HazeTop, 1.25f, f, 0f);
            int g = Vert(mb, new Vector3(x1, yTop, z),  0f, roofV, roofV, f.HazeTop, 1.25f, f, 0f);
            int h = Vert(mb, new Vector3(x1, yTop, z1), 0f, roofV, roofV, f.HazeTop, 1.25f, f, 0f);
            int k = Vert(mb, new Vector3(x0, yTop, z1), 0f, roofV, roofV, f.HazeTop, 1.25f, f, 0f);
            mb.Quad(e, g, h, k);
        }

        void SideFace(MeshBuilder mb, float x, float yBase, float yTop, float z0, float z1, float roofV, float shade,
                      in Facade f)
        {
            int a = Vert(mb, new Vector3(x, yBase, z1), U(z1 - z0, f), V(yBase, f), roofV, 1f,        shade, f, 1f, atBase: true);
            int b = Vert(mb, new Vector3(x, yBase, z0), U(0f, f),      V(yBase, f), roofV, 1f,        shade, f, 1f, atBase: true);
            int c = Vert(mb, new Vector3(x, yTop,  z0), U(0f, f),      roofV,       roofV, f.HazeTop, shade, f, 1f);
            int d = Vert(mb, new Vector3(x, yTop,  z1), U(z1 - z0, f), roofV,       roofV, f.HazeTop, shade, f, 1f);
            mb.Quad(a, b, c, d);
        }

        /// <summary>
        /// A landmark tower: two or three setback tiers, a spire with an aviation beacon, and a crown of
        /// small lights along the top tier.
        /// </summary>
        void EmitLandmark(MeshBuilder mb, MeshBuilder lights, float x0, float x1, float yBase, float yTop, float z,
                          float depth, Facade f, float ds)
        {
            f.Lit   = Mathf.Max(f.Lit, 0.45f);
            f.GlassW = Rand(0.55f, 0.8f);
            f.Tone  = Chance(0.5f) ? Rand(0.55f, 0.9f) : Rand(0.05f, 0.3f);

            int tiers  = Chance(0.5f) ? 3 : 2;
            float w    = x1 - x0;
            float cx   = (x0 + x1) * 0.5f;
            float yAt  = yBase;
            float span = yTop - yBase;
            float lo   = x0, hi = x1;
            float tierTop = yTop;

            for (int t = 0; t < tiers; t++)
            {
                float top = t == tiers - 1 ? yTop : yAt + span * (t == 0 ? Rand(0.72f, 0.82f) : Rand(0.88f, 0.94f));
                Facade tf = f;
                tf.UOffset = f.UOffset + (lo - x0);   // keep the window grid aligned across tiers
                EmitBox(mb, lo, hi, yAt, top, z + t * 0.4f, depth - t * 0.8f, tf);
                tierTop = top;
                yAt  = top;
                span = yTop - yAt;
                float inset = w * Rand(0.10f, 0.16f);
                lo += inset;
                hi -= inset;
                if (hi - lo < w * 0.25f) break;
            }

            // Spire.
            float spireH = (yTop - yBase) * Rand(0.04f, 0.08f) + 1.5f * ds;
            float spireW = w * 0.10f;
            EmitSpike(mb, cx, yTop, z + 0.6f, spireW, 0.03f * ds, spireH, f);
            AddLight(lights, new Vector3(cx, yTop + spireH, z + 0.5f), 0.55f * ds, 0.55f * ds, 0f, 2.2f,
                     beaconColor, beaconHz, Rand(0f, 1f), 3f, 0f);

            // Crown lights along the top tier.
            float crownY = tierTop - 0.25f * ds;
            float step   = 0.55f * ds;
            Color crown  = Chance(0.5f) ? new Color(1.4f, 1.6f, 1.9f) : new Color(1.9f, 1.4f, 0.8f);
            for (float cxl = lo + step * 0.5f; cxl < hi; cxl += step)
                AddLight(lights, new Vector3(cxl, crownY, z - 0.05f), 0.16f * ds, 0.16f * ds, 0f, 2.5f,
                         crown * 0.55f, 0f, 0f, 1f, 0f);
        }

        /// <summary>
        /// A building broken open: a jagged roofline torn down one side, the facade scorched below it, a few
        /// windows burning, bare beams sticking out of the break, and a fire glow over it. Registers a
        /// <see cref="DamageSite"/> for the sparks.
        /// </summary>
        void EmitBroken(MeshBuilder mb, MeshBuilder lights, float x0, float x1, float yBase, float yTop, float z,
                        float depth, Facade f, float ds, bool electric)
        {
            float w     = x1 - x0;
            float tear  = (yTop - YAt(0.20f, z)) * Rand(0.18f, 0.32f);   // how far down the break goes
            int strips  = 9;
            float lean  = Chance(0.5f) ? 1f : -1f;                        // which side is torn away
            var xs = new float[strips + 1];
            var ys = new float[strips + 1];
            for (int i = 0; i <= strips; i++)
            {
                float s = i / (float)strips;
                float slope = lean > 0f ? s : 1f - s;
                xs[i] = x0 + w * s;
                ys[i] = yTop - tear * (0.15f + 0.85f * slope * slope) * Rand(0.75f, 1.15f)
                             - (i % 2 == 0 ? 0f : tear * Rand(0.04f, 0.14f));
            }
            f.Scorch = tear * 1.6f;

            // Front face as vertical strips following the broken roofline.
            for (int i = 0; i < strips; i++)
            {
                float rv0 = V(ys[i], f), rv1 = V(ys[i + 1], f);
                int a = Vert(mb, new Vector3(xs[i],     yBase, z), U(xs[i] - x0, f),     V(yBase, f), rv0, 1f, 1f, f, 1f, atBase: true);
                int b = Vert(mb, new Vector3(xs[i + 1], yBase, z), U(xs[i + 1] - x0, f), V(yBase, f), rv1, 1f, 1f, f, 1f, atBase: true);
                int c = Vert(mb, new Vector3(xs[i + 1], ys[i + 1], z), U(xs[i + 1] - x0, f), rv1, rv1, f.HazeTop, 1f, f, 1f);
                int d = Vert(mb, new Vector3(xs[i],     ys[i],     z), U(xs[i] - x0, f),     rv0, rv0, f.HazeTop, 1f, f, 1f);
                mb.Quad(a, b, c, d);
            }
            SideFace(mb, x0, yBase, ys[0],      z, z + depth, V(ys[0], f),      0.62f, f);
            SideFace(mb, x1, yBase, ys[strips], z, z + depth, V(ys[strips], f), 0.50f, f);

            // Bare beams poking out of the break.
            Facade beam = f;
            beam.Rgb *= 0.6f;
            for (int i = 1; i < strips; i += 2)
            {
                if (!Chance(0.6f)) continue;
                float bh = tear * Rand(0.18f, 0.45f);
                EmitSpike(mb, xs[i] + Rand(-0.2f, 0.2f) * w / strips, ys[i] - 0.05f, z - 0.02f,
                          0.07f * ds, 0.05f * ds, bh, beam);
            }

            // Fire glow over the break and a register entry for the sparks.
            float midY = 0f;
            for (int i = 0; i <= strips; i++) midY += ys[i];
            midY /= strips + 1;
            float lowSide = lean > 0f ? x0 + w * 0.7f : x0 + w * 0.3f;
            AddLight(lights, new Vector3(lowSide, midY, z - 0.1f), w * 0.55f, tear * 0.9f, 0f, 1.6f,
                     fireColor * (electric ? 0.10f : 0.22f), 0f, Rand(0f, 1f), 1f, 0.55f);

            _sites.Add(new DamageSite
            {
                Position   = new Vector3(lowSide, midY + tear * 0.15f, z - 0.15f),
                Width      = w * 0.5f,
                DepthScale = ds,
                Electric   = electric,
            });
        }

        /// <summary>Roof furniture: antennas (some with beacons), water tanks, plant rooms, parapets.</summary>
        void EmitRoofDetails(MeshBuilder mb, MeshBuilder lights, in Layer L, float x0, float x1, float yTop, float z,
                             in Facade f, float ds)
        {
            float w = x1 - x0;
            Facade dark = f;
            dark.Rgb *= 0.8f;

            switch (L.Role)
            {
                case Role.Near:
                {
                    if (Chance(0.45f)) EmitWaterTank(mb, x0 + Rand(0.2f, 0.8f) * w, yTop, z + 0.6f, 0.75f * ds, dark);
                    if (Chance(0.35f))
                    {
                        float pw = w * Rand(0.2f, 0.35f);
                        float px = x0 + Rand(0.1f, 0.9f) * (w - pw);
                        EmitBox(mb, px, px + pw, yTop - 0.1f, yTop + Rand(0.6f, 1.1f) * ds, z + 0.5f, 1.5f, dark);
                    }
                    if (Chance(0.30f)) EmitAntenna(mb, lights, x0 + Rand(0.2f, 0.8f) * w, yTop, z + 0.4f,
                                                   Rand(1.2f, 2.6f) * ds, ds, dark, beacon: Chance(0.4f));
                    break;
                }
                case Role.Main:
                {
                    if (Chance(0.30f)) EmitAntenna(mb, lights, x0 + Rand(0.25f, 0.75f) * w, yTop, z + 0.5f,
                                                   Rand(1.5f, 3.5f) * ds, ds, dark, beacon: Chance(0.6f));
                    if (Chance(0.30f))
                    {
                        float pw = w * Rand(0.25f, 0.45f);
                        float px = x0 + Rand(0.1f, 0.9f) * (w - pw);
                        EmitBox(mb, px, px + pw, yTop - 0.1f, yTop + Rand(0.8f, 1.6f) * ds, z + 0.6f, 2f, dark);
                    }
                    if (Chance(0.12f))
                    {
                        // A neon sign on the facade.
                        Color neon = Chance(0.5f) ? new Color(2.0f, 0.35f, 1.5f) : new Color(0.35f, 1.5f, 2.0f);
                        float ny   = yTop - Rand(1.2f, 3.0f) * ds;
                        AddLight(lights, new Vector3((x0 + x1) * 0.5f, ny, z - 0.08f), w * 0.36f, 0.14f * ds, 2f, 1.5f,
                                 neon * 0.5f, Chance(0.3f) ? 0.25f : 0f, Rand(0f, 1f), 1f, 0f);
                    }
                    break;
                }
                case Role.Far:
                {
                    if (Chance(0.12f)) EmitAntenna(mb, lights, x0 + Rand(0.3f, 0.7f) * w, yTop, z + 0.5f,
                                                   Rand(2f, 4f) * ds, ds, dark, beacon: Chance(0.7f));
                    break;
                }
            }
        }

        void EmitAntenna(MeshBuilder mb, MeshBuilder lights, float x, float yTop, float z, float height, float ds,
                         in Facade f, bool beacon)
        {
            EmitSpike(mb, x, yTop - 0.05f, z, 0.07f * ds, 0.025f * ds, height, f);
            if (beacon)
                AddLight(lights, new Vector3(x, yTop + height, z - 0.1f), 0.4f * ds, 0.4f * ds, 0f, 2.2f,
                         beaconColor * 0.8f, beaconHz, Rand(0f, 1f), 3f, 0f);
        }

        void EmitWaterTank(MeshBuilder mb, float cx, float yTop, float z, float size, in Facade f)
        {
            float half = size * 0.5f;
            float legH = size * 0.45f;
            float leg  = size * 0.08f;
            EmitFlat(mb, cx - half * 0.8f, cx - half * 0.8f + leg, yTop, yTop + legH, z, f);
            EmitFlat(mb, cx + half * 0.8f - leg, cx + half * 0.8f, yTop, yTop + legH, z, f);
            EmitFlat(mb, cx - half, cx + half, yTop + legH, yTop + legH + size, z, f);
            EmitSpike(mb, cx, yTop + legH + size, z, size * 1.05f, 0.02f, size * 0.35f, f);
        }

        /// <summary>A flat camera-facing rectangle with no windows (roof furniture).</summary>
        void EmitFlat(MeshBuilder mb, float x0, float x1, float y0, float y1, float z, in Facade f)
        {
            float rv = V(y1, f);
            int a = Vert(mb, new Vector3(x0, y0, z), 0f, V(y0, f), rv, f.HazeTop, 1f, f, 0f);
            int b = Vert(mb, new Vector3(x1, y0, z), 0f, V(y0, f), rv, f.HazeTop, 1f, f, 0f);
            int c = Vert(mb, new Vector3(x1, y1, z), 0f, rv,       rv, f.HazeTop, 1f, f, 0f);
            int d = Vert(mb, new Vector3(x0, y1, z), 0f, rv,       rv, f.HazeTop, 1f, f, 0f);
            mb.Quad(a, b, c, d);
        }

        /// <summary>A tapering spike — spire, antenna, beam — from base width to tip width.</summary>
        void EmitSpike(MeshBuilder mb, float cx, float y0, float z, float baseW, float tipW, float height, in Facade f)
        {
            float rv = V(y0 + height, f);
            int a = Vert(mb, new Vector3(cx - baseW * 0.5f, y0, z),          0f, V(y0, f), rv, f.HazeTop, 0.9f, f, 0f);
            int b = Vert(mb, new Vector3(cx + baseW * 0.5f, y0, z),          0f, V(y0, f), rv, f.HazeTop, 0.9f, f, 0f);
            int c = Vert(mb, new Vector3(cx + tipW * 0.5f, y0 + height, z),  0f, rv,       rv, f.HazeTop, 0.9f, f, 0f);
            int d = Vert(mb, new Vector3(cx - tipW * 0.5f, y0 + height, z),  0f, rv,       rv, f.HazeTop, 0.9f, f, 0f);
            mb.Quad(a, b, c, d);
        }

        float U(float local, in Facade f) => local + f.UOffset;
        float V(float y, in Facade f)     => y - _groundY + f.VOffset;

        // atBase marks the vertices along a building's foot, far below the screen: they carry the layer's
        // base shade and window level, which fade up to full at the roof across the visible facade.
        int Vert(MeshBuilder mb, Vector3 p, float u, float v, float roofV, float hazeW, float shade, in Facade f,
                 float windowsOn, bool atBase = false)
        {
            Color rgb = f.Rgb * (shade * (atBase ? f.BaseShade : 1f));
            rgb.a = hazeW;
            return mb.Add(p, rgb,
                          new Vector4(u, v, roofV, windowsOn),
                          new Vector4(f.CellW, f.CellH, f.Lit, f.Seed),
                          new Vector4(f.GlassW, f.GlassH, f.Tone, f.Brightness),
                          new Vector4(f.Damage, f.Fire, f.Scorch, atBase ? f.BaseWindow : 1f));
        }

        // ── Lights ───────────────────────────────────────────────────────────

        /// <summary>One camera-facing light quad. Shape 0 dot, 1 vertical glow band, 2 horizontal bar.</summary>
        static void AddLight(MeshBuilder lights, Vector3 c, float halfW, float halfH, float shape, float power,
                             Color hdr, float rateHz, float phase, float sharpness, float flicker)
        {
            var col   = new Vector4(hdr.r, hdr.g, hdr.b, 0f);
            var pulse = new Vector4(rateHz, phase, sharpness, flicker);
            int a = lights.AddLight(new Vector3(c.x - halfW, c.y - halfH, c.z), new Vector4(-1f, -1f, shape, power), col, pulse);
            int b = lights.AddLight(new Vector3(c.x + halfW, c.y - halfH, c.z), new Vector4( 1f, -1f, shape, power), col, pulse);
            int d = lights.AddLight(new Vector3(c.x + halfW, c.y + halfH, c.z), new Vector4( 1f,  1f, shape, power), col, pulse);
            int e = lights.AddLight(new Vector3(c.x - halfW, c.y + halfH, c.z), new Vector4(-1f,  1f, shape, power), col, pulse);
            lights.Quad(a, b, d, e);
        }

        /// <summary>
        /// The two big glows: light pollution along the horizon behind everything (behind the sprawl too, so
        /// it lights the sky over the city's far edge), with the far lights; and the warm wash of the streets
        /// at the foot of the nearest buildings (in front of the near layer, behind the towers), with the near.
        /// </summary>
        void AddGlowBands(MeshBuilder farLights, MeshBuilder nearLights, float originX, float panRange)
        {
            // Endless, each band is one piece for every copy of the city, its even middle wide enough for the view at
            // every jump of the strip: a band fades out over the outer sixth of its half-width (JndUfo/CityLights).
            bool endless = EndlessPeriod > 0f;
            const float horizonZ = 520f;
            float hw = endless ? EndlessHalfWidth(horizonZ) * 1.25f
                               : ScreenWidthAt(horizonZ) * 0.5f + (panRange + sideMargin) * DepthScale(horizonZ);
            float y0 = YAt(0.22f, horizonZ), y1 = YAt(0.70f, horizonZ);
            AddLight(farLights, new Vector3(originX, (y0 + y1) * 0.5f, horizonZ), hw, (y1 - y0) * 0.5f, 1f, 1.7f,
                     horizonGlow, 0f, 0f, 1f, 0f);

            // From just below the ground line on the screen up past the near rooftops' feet.
            const float streetZ = 6f;
            float groundV = 0.5f + (_groundY - _camY) / (2f * _tanHalf * Mathf.Max(1f, _playZ - _camZ));
            float sw = endless ? EndlessHalfWidth(streetZ) * 1.25f
                               : ScreenWidthAt(streetZ) * 0.5f + (panRange + sideMargin) * DepthScale(streetZ);
            float s0 = YAt(groundV - 0.026f, streetZ), s1 = YAt(groundV + 0.134f, streetZ);
            AddLight(nearLights, new Vector3(originX, (s0 + s1) * 0.5f, streetZ), sw, (s1 - s0) * 0.5f, 1f, 2.2f,
                     streetGlow, 0f, 0f, 1f, 0f);
        }

        // Where, as fractions of the starting view's width, the searchlights stand; their resting lean from
        // the vertical (degrees), how far either way they sweep, and the seconds a sweep takes.
        static readonly Vector4[] Searchlights =
        {
            new Vector4(0.21f, -10f, 16f, 29f),
            new Vector4(0.57f,   4f, 20f, 37f),
            new Vector4(0.84f,  13f, 15f, 23f),
        };

        /// <summary>
        /// Searchlight beams standing behind the far skyline (their feet hidden by it), reaching up past the
        /// top of the screen. Each is one quad whose corners all sit at its foot: JndUfo/CityLights swings it
        /// about that point from _Time, so the mesh never changes. Drawn with the far lights.
        /// </summary>
        void AddSearchlights(MeshBuilder lights, float originX)
        {
            const float z = 330f;
            float screenW = ScreenWidthAt(z);
            float footY   = YAt(0.35f, z);
            float length  = (YAt(1.1f, z) - footY) * 1.15f;
            var   col     = new Vector4(searchlightColor.r, searchlightColor.g, searchlightColor.b, 0f);
            foreach (Vector4 sl in Searchlights)
            {
                var foot  = new Vector3(originX + (sl.x - 0.5f) * screenW, footY, z);
                var sweep = new Vector4(2f * Mathf.PI / sl.w, Rand(0f, 6.283f), sl.z * Mathf.Deg2Rad, sl.y * Mathf.Deg2Rad);
                var size  = new Vector4(length, 0.5f, 7f, 0f);   // length, half-width at the foot, at the far end
                int a = lights.AddLight(foot, new Vector4(-1f, -1f, 3f, 2.2f), col, sweep, size);
                int b = lights.AddLight(foot, new Vector4( 1f, -1f, 3f, 2.2f), col, sweep, size);
                int c = lights.AddLight(foot, new Vector4( 1f,  1f, 3f, 2.2f), col, sweep, size);
                int d = lights.AddLight(foot, new Vector4(-1f,  1f, 3f, 2.2f), col, sweep, size);
                lights.Quad(a, b, c, d);
            }
        }

        // ── Mesh assembly ────────────────────────────────────────────────────

        /// <summary>Accumulates one mesh's vertices and quads. Used only while building.</summary>
        sealed class MeshBuilder
        {
            readonly List<Vector3> _v;
            readonly List<Color>   _c;
            readonly List<Vector4> _u0, _u1, _u2, _u3;
            readonly List<int>     _t;

            public MeshBuilder(int capacity)
            {
                _v  = new List<Vector3>(capacity);
                _c  = new List<Color>(capacity);
                _u0 = new List<Vector4>(capacity);
                _u1 = new List<Vector4>(capacity);
                _u2 = new List<Vector4>(capacity);
                _u3 = new List<Vector4>(capacity);
                _t  = new List<int>(capacity * 2);
            }

            public int VertexCount => _v.Count;

            public int Add(Vector3 p, Color c, Vector4 u0, Vector4 u1, Vector4 u2, Vector4 u3)
            {
                _v.Add(p); _c.Add(c); _u0.Add(u0); _u1.Add(u1); _u2.Add(u2); _u3.Add(u3);
                return _v.Count - 1;
            }

            public int AddLight(Vector3 p, Vector4 u0, Vector4 u1, Vector4 u2) => AddLight(p, u0, u1, u2, Vector4.zero);

            public int AddLight(Vector3 p, Vector4 u0, Vector4 u1, Vector4 u2, Vector4 u3)
            {
                _v.Add(p); _u0.Add(u0); _u1.Add(u1); _u2.Add(u2); _u3.Add(u3);
                return _v.Count - 1;
            }

            public void Quad(int a, int b, int c, int d)
            {
                _t.Add(a); _t.Add(b); _t.Add(c);
                _t.Add(a); _t.Add(c); _t.Add(d);
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name, hideFlags = HideFlags.DontSave };
                if (_v.Count > 65000) m.indexFormat = IndexFormat.UInt32;
                m.SetVertices(_v);
                if (_c.Count == _v.Count) m.SetColors(_c);
                m.SetUVs(0, _u0);
                m.SetUVs(1, _u1);
                m.SetUVs(2, _u2);
                if (_u3.Count == _v.Count) m.SetUVs(3, _u3);
                m.SetTriangles(_t, 0);
                m.RecalculateBounds();
                m.UploadMeshData(markNoLongerReadable: true);
                return m;
            }
        }
    }
}
