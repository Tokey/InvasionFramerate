using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// The foreground: the dark, out-of-focus grass of the hillside the camera stands on, bending and swaying
    /// in a gusty breeze across the bottom of the screen, with a few motes of lit dust drifting over it. It hides the
    /// ground the towers stand on — their feet are in it — and frames the city like a long lens looking out
    /// over a meadow at night.
    ///
    /// Three rows at three distances from the camera, each one quad facing it (JndUfo/Grass): the farthest,
    /// just in front of the towers, nearly in focus, its tips edged by the city's light; the middle one a shade
    /// darker and softer, the next line of grass down the slope; the nearest a darker, soft blur along the
    /// bottom edge. All are coloured by height on the screen, so where they overlap they read as one dark
    /// ground rather than as strips. Being at different depths they slide against each other, and against
    /// the play plane, whenever the camera moves — the parallax swing, the reset's pan, the handheld wander —
    /// which is most of what makes the scene read in depth.
    ///
    /// Each row's silhouette — mass, turf, blades, leaves, seed heads, and its blur — is drawn once, at
    /// startup, into a texture (Hidden/JndUfo/GrassBake); every frame the row only bends it in the wind, so the
    /// whole foreground costs three textured quads and nothing on the CPU. The layout comes from
    /// <see cref="seed"/>: the same meadow for every participant, never re-dealt, and it knows nothing of where
    /// the boundary is.
    ///
    /// Study notes: nothing here can cover the UFO — every blade stays in the bottom third of the screen, far
    /// below the UFO's lowest altitude — but it does cover the bottom of the towers and the very end of a shot
    /// (the impact point itself, on the ground line), the way the old ground fog did. Its sway and motes move,
    /// slowly, so like everything that moves they show the frame rate; keep the settings the same for a whole
    /// study. Created and built by <see cref="TowerManager"/> at Start.
    /// </summary>
    public class Foreground : MonoBehaviour
    {
        [Tooltip("Draw the foreground grass.")]
        public bool grass = true;

        [Tooltip("A few soft motes of lit dust drifting over the grass, fading in and out over several seconds.")]
        public bool motes = true;

        [Tooltip("How hard the wind blows: 1 is a breeze with gusts coming and going at random, 0 is still air.")]
        [Range(0f, 2f)] public float wind = 1f;

        [Tooltip("Seed for the meadow's layout. Fixed, so every participant sees the same grass.")]
        public int seed = 314;

        [Tooltip("The grass's colour at the bottom of the screen. Every row is coloured by its height on the " +
                 "screen alone, so where rows overlap they merge into one dark mass and only their silhouettes " +
                 "against the city, and the faint light on their edges, show.")]
        public Color shadowColor = new Color(0.04f, 0.04f, 0.052f, 1f);

        [Tooltip("The grass's colour up at the grass line, a touch lighter and warmer for the city's glow behind it.")]
        public Color grassLineColor = new Color(0.095f, 0.082f, 0.088f, 1f);

        // Sizes are reference pixels — pixels of a 1080p screen with the camera at rest, Y up from the bottom of
        // the screen — so the rows keep their look at any resolution.
        const float ReferenceHeight = 1080f;
        const float DesignGroundPx  = 222.2f;  // the ground line the rows below are laid out for; they follow the real one
        const float SideMargin      = 4f;      // world units of grass past the widest view any pan reaches
        const float QuadBottom      = -0.4f;   // fraction of the screen's height: far below it, for the shake

        struct RowSpec
        {
            public string  Name;
            public float   Distance;                    // from the camera, world units
            public float   Blur;                        // reference px
            public float   MassTop, MassAmp, MassWave;  // the solid mass: top, its rise and fall, wavelength
            public float   ClumpWave, ClumpStrength;
            public Vector4 Turf,  TurfShape;            // cell, chance, height min, max | half-width min, max, lean
            public Vector4 Grass, GrassShape;
            public Vector4 Leaf,  LeafShape;
            public Vector4 Stem,  StemShape;            // cell, chance, height min, max | stem, head half-width, head length
            public float   StemLean;
            public Color   Rim;                         // the tips' backlit edges (sRGB, as the Inspector shows colours)
            public float   Shade;                       // the row's darkness against the shared colour: 1 the same
            public float   Sway, Flutter;               // reference px at the tips, in a full gust
            public float   MoteCell, MoteChance, MoteSize, MoteSoft, MoteLevel;
        }

        // Back to front. The far row's mass, under its dense turf, stays above the ground line (222 px)
        // everywhere, so the towers' feet are always in it.
        static readonly RowSpec[] Rows =
        {
            // The grass line just in front of the towers, nearly in focus: a dense turf, fine grass in clumps, a
            // few arching leaves, seed heads on tall stems, its tips edged by the city's light.
            new RowSpec
            {
                Name = "Far", Distance = 70f, Blur = 1.3f,
                MassTop = 236f, MassAmp = 9f, MassWave = 150f, ClumpWave = 80f, ClumpStrength = 0.55f,
                Turf  = new Vector4(1.8f, 1f, 6f, 18f),      TurfShape  = new Vector4(1f, 1.8f, 0.3f, 0f),
                Grass = new Vector4(2.6f, 0.9f, 12f, 58f),   GrassShape = new Vector4(0.9f, 1.7f, 0.38f, 0f),
                Leaf  = new Vector4(30f, 0.4f, 14f, 36f),    LeafShape  = new Vector4(2f, 3.6f, 0.9f, 0f),
                Stem  = new Vector4(24f, 0.45f, 40f, 90f),   StemShape  = new Vector4(0.55f, 2.1f, 11f, 0f), StemLean = 0.45f,
                Rim   = new Color(0.16f, 0.11f, 0.07f), Shade = 1f,
                Sway = 12f, Flutter = 2.5f,
                MoteCell = 260f, MoteChance = 0.5f, MoteSize = 3.4f, MoteSoft = 1.3f, MoteLevel = 0.55f,
            },
            // Nearer, softer, a shade darker: the next line of grass down the slope, its tallest blades and seed
            // heads rising into the light.
            new RowSpec
            {
                Name = "Mid", Distance = 50f, Blur = 3.2f,
                MassTop = 150f, MassAmp = 16f, MassWave = 190f, ClumpWave = 120f, ClumpStrength = 0.5f,
                Turf  = new Vector4(2.4f, 1f, 10f, 30f),     TurfShape  = new Vector4(1.4f, 2.4f, 0.32f, 0f),
                Grass = new Vector4(4.5f, 0.85f, 24f, 90f),  GrassShape = new Vector4(1.4f, 2.4f, 0.4f, 0f),
                Leaf  = new Vector4(44f, 0.45f, 30f, 70f),   LeafShape  = new Vector4(4.5f, 7f, 0.9f, 0f),
                Stem  = new Vector4(48f, 0.3f, 80f, 140f),   StemShape  = new Vector4(0.9f, 3.4f, 18f, 0f), StemLean = 0.5f,
                Rim   = Color.black, Shade = 0.62f,
                Sway = 18f, Flutter = 3f,
                MoteCell = 320f, MoteChance = 0.45f, MoteSize = 6f, MoteSoft = 2.8f, MoteLevel = 0.45f,
            },
            // The nearest, a soft blur along the bottom edge, darker than the rest as the nearest grass is, a few
            // tall leaves and stems reaching up.
            new RowSpec
            {
                Name = "Near", Distance = 34f, Blur = 7f,
                MassTop = 40f, MassAmp = 30f, MassWave = 300f, ClumpWave = 260f, ClumpStrength = 0.55f,
                Turf  = Vector4.zero,                        TurfShape  = Vector4.zero,
                Grass = new Vector4(14f, 0.7f, 80f, 200f),   GrassShape = new Vector4(3.5f, 6f, 0.4f, 0f),
                Leaf  = new Vector4(70f, 0.55f, 90f, 180f),  LeafShape  = new Vector4(10f, 16f, 0.95f, 0f),
                Stem  = new Vector4(150f, 0.35f, 160f, 220f), StemShape = new Vector4(2f, 6.5f, 36f, 0f), StemLean = 0.45f,
                Rim   = Color.black, Shade = 0.32f,
                Sway = 28f, Flutter = 4f,
                MoteCell = 420f, MoteChance = 0.4f, MoteSize = 11f, MoteSoft = 5.5f, MoteLevel = 0.35f,
            },
        };

        static readonly int AreaId      = Shader.PropertyToID("_Area");
        static readonly int MassId      = Shader.PropertyToID("_Mass");
        static readonly int ClumpId     = Shader.PropertyToID("_Clump");
        static readonly int TurfAId     = Shader.PropertyToID("_TurfA");
        static readonly int TurfBId     = Shader.PropertyToID("_TurfB");
        static readonly int GrassAId    = Shader.PropertyToID("_GrassA");
        static readonly int GrassBId    = Shader.PropertyToID("_GrassB");
        static readonly int LeafAId     = Shader.PropertyToID("_LeafA");
        static readonly int LeafBId     = Shader.PropertyToID("_LeafB");
        static readonly int StemAId     = Shader.PropertyToID("_StemA");
        static readonly int StemBId     = Shader.PropertyToID("_StemB");
        static readonly int StemLeanId  = Shader.PropertyToID("_StemLean");
        static readonly int BaseId      = Shader.PropertyToID("_Base");
        static readonly int UpperId     = Shader.PropertyToID("_Upper");
        static readonly int GroundId    = Shader.PropertyToID("_Ground");
        static readonly int RimId       = Shader.PropertyToID("_Rim");
        static readonly int ShadeId     = Shader.PropertyToID("_Shade");
        static readonly int BandId      = Shader.PropertyToID("_Band");
        static readonly int WindId      = Shader.PropertyToID("_Wind");
        static readonly int MotesId     = Shader.PropertyToID("_Motes");
        static readonly int MoteLookId  = Shader.PropertyToID("_MoteLook");

        sealed class Row
        {
            public RowSpec       Spec;
            public RenderTexture Silhouette;
            public Material      Bake, Draw;
            public Mesh          Quad;
            public MeshRenderer  Renderer;
            public Vector4       Wind, Motes;
        }

        Row[]  _rows;
        float  _shift;   // reference px the rows are moved by to stand on the scene's ground line
        bool   _built, _grassOn = true, _motesOn = true;
        float  _windOn = 1f;
        Color  _shadowOn, _lineOn;

        /// <summary>Builds the rows for <paramref name="cam"/> at rest, wide enough for pans of
        /// ±<paramref name="panRange"/> around <paramref name="originX"/>, around the ground line at
        /// <paramref name="groundY"/> on the play plane <paramref name="playZ"/>. Call once.</summary>
        public void Build(Camera cam, float originX, float panRange, float groundY, float playZ)
        {
            if (_built || cam == null) return;

            Shader bake = Shader.Find("Hidden/JndUfo/GrassBake");
            Shader draw = Shader.Find("JndUfo/Grass");
            if (bake == null || !bake.isSupported || draw == null || !draw.isSupported)
            {
                Debug.LogWarning("[Foreground] The grass shaders are missing or unsupported here — no foreground.");
                return;
            }
            _built = true;

            float camY   = cam.transform.position.y;
            float camZ   = cam.transform.position.z;
            float tan    = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float aspect = Mathf.Max(cam.aspect, 16f / 9f);   // never narrower than 16:9

            // The rows follow the ground line, wherever the scene puts it.
            float groundPx = (0.5f + (groundY - camY) / (2f * tan * Mathf.Max(1f, playZ - camZ))) * ReferenceHeight;
            _shift = groundPx - DesignGroundPx;

            _rows = new Row[Rows.Length];
            for (int i = 0; i < Rows.Length; i++)
            {
                RowSpec spec = Rows[i];
                spec.MassTop += _shift;
                _rows[i] = BuildRow(spec, i, bake, draw, camY, camZ, tan, aspect, originX, panRange);
            }

            Apply();
            Debug.Log($"[Foreground] {Rows.Length} rows of grass at {Rows[0].Distance:0}-{Rows[Rows.Length - 1].Distance:0} " +
                      $"units from the camera (seed {seed}).");
        }

        Row BuildRow(in RowSpec spec, int index, Shader bake, Shader draw, float camY, float camZ, float tan,
                     float aspect, float originX, float panRange)
        {
            float d      = spec.Distance;
            float k      = ReferenceHeight * 0.5f / (tan * d);       // reference px per world unit, here
            float z      = camZ + d;
            float halfW  = tan * d * aspect + panRange + SideMargin; // world units
            float widthPx = 2f * halfW * k;

            float clumpMax = 1f + spec.ClumpStrength;
            float tallest  = Mathf.Max(spec.Grass.w, Mathf.Max(spec.Leaf.w, spec.Stem.w)) * clumpMax;
            float bandLo   = spec.MassTop - spec.MassAmp - (2.5f * spec.Blur + 3f) - 6f;   // below the mass's soft top (see the bake)
            float bandHi   = spec.MassTop + spec.MassAmp + tallest + 2f * spec.Blur + 4f;
            float quadLo   = QuadBottom * ReferenceHeight;

            // The silhouette, drawn once.
            var row = new Row { Spec = spec };
            row.Bake = new Material(bake) { name = "GrassBake_" + spec.Name, hideFlags = HideFlags.DontSave };
            row.Bake.SetVector(AreaId,  new Vector4(widthPx, bandLo, bandHi - bandLo, spec.Blur));
            row.Bake.SetVector(MassId,  new Vector4(spec.MassTop, spec.MassAmp, 1f / spec.MassWave,
                                                          Mathf.Abs(seed % 100000) * 31 + index * 7 + 1));
            row.Bake.SetVector(ClumpId, new Vector4(1f / spec.ClumpWave, spec.ClumpStrength, 0f, 0f));
            row.Bake.SetVector(TurfAId,  spec.Turf);
            row.Bake.SetVector(TurfBId,  WithReach(spec.TurfShape, spec.Turf, spec.TurfShape.z, spec.TurfShape.y, spec.Blur, clumpMax));
            row.Bake.SetVector(GrassAId, spec.Grass);
            row.Bake.SetVector(GrassBId, WithReach(spec.GrassShape, spec.Grass, spec.GrassShape.z, spec.GrassShape.y, spec.Blur, clumpMax));
            row.Bake.SetVector(LeafAId,  spec.Leaf);
            row.Bake.SetVector(LeafBId,  WithReach(spec.LeafShape, spec.Leaf, spec.LeafShape.z, spec.LeafShape.y, spec.Blur, clumpMax));
            row.Bake.SetVector(StemAId,  spec.Stem);
            row.Bake.SetVector(StemBId,  WithReach(spec.StemShape, spec.Stem, spec.StemLean, spec.StemShape.y, spec.Blur, clumpMax));
            row.Bake.SetFloat(StemLeanId, spec.StemLean);

            int texW = Mathf.Clamp(Mathf.CeilToInt(widthPx), 64, 4096);
            int texH = Mathf.Clamp(Mathf.CeilToInt(bandHi - bandLo), 16, 1024);
            var desc = new RenderTextureDescriptor(texW, texH, RenderTextureFormat.ARGB32, 0)
            {
                msaaSamples      = 1,
                useMipMap        = true,
                autoGenerateMips = false,
                sRGB             = false,
            };
            row.Silhouette = new RenderTexture(desc)
            {
                name       = "Grass_" + spec.Name,
                hideFlags  = HideFlags.DontSave,
                filterMode = FilterMode.Trilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            Bake(row);

            // The quad, from far below the screen to the tips of the tallest stems.
            float y0 = camY + (2f * quadLo / ReferenceHeight - 1f) * tan * d;
            float y1 = camY + (2f * bandHi / ReferenceHeight - 1f) * tan * d;
            row.Quad = new Mesh { name = "GrassRow_" + spec.Name, hideFlags = HideFlags.DontSave };
            row.Quad.vertices  = new[] { new Vector3(originX - halfW, y0, z), new Vector3(originX + halfW, y0, z),
                                         new Vector3(originX + halfW, y1, z), new Vector3(originX - halfW, y1, z) };
            row.Quad.uv        = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            row.Quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            row.Quad.RecalculateBounds();

            row.Draw = new Material(draw) { name = "Grass_" + spec.Name, hideFlags = HideFlags.DontSave };
            row.Draw.mainTexture = row.Silhouette;
            row.Draw.SetColor(RimId,   spec.Rim);
            row.Draw.SetFloat(ShadeId, spec.Shade);
            row.Draw.SetVector(AreaId, new Vector4(widthPx, quadLo, bandHi, 0f));
            float swayBase = spec.MassTop - spec.MassAmp - 6f;
            float swayTop  = spec.MassTop + spec.MassAmp + 0.8f * tallest;
            row.Draw.SetVector(BandId, new Vector4(bandLo, bandHi, swayBase, Mathf.Max(1f, swayTop - swayBase)));
            // Each row's gusts a little out of step with the others', as a breeze crossing at a slant.
            row.Wind  = new Vector4(spec.Sway, spec.Flutter, index * 1.9f + z * 0.03f, 1f);
            row.Motes = new Vector4(spec.MoteCell, spec.MoteChance,
                                    spec.MassTop - 0.5f * spec.MassAmp,
                                    spec.MassTop + spec.MassAmp + 0.45f * spec.Grass.w * clumpMax);
            row.Draw.SetVector(MoteLookId, new Vector4(spec.MoteSize, spec.MoteSoft, spec.MoteLevel, 0f));

            var go = new GameObject("Grass_" + spec.Name);
            go.transform.SetParent(transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = row.Quad;
            row.Renderer = go.AddComponent<MeshRenderer>();
            row.Renderer.sharedMaterial             = row.Draw;
            row.Renderer.shadowCastingMode          = ShadowCastingMode.Off;
            row.Renderer.receiveShadows             = false;
            row.Renderer.lightProbeUsage            = LightProbeUsage.Off;
            row.Renderer.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            row.Renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            row.Renderer.allowOcclusionWhenDynamic  = false;
            return row;
        }

        // A sub-layer's shape vector with its reach — how many cells either side a pixel must look at to find
        // every blade that can lean, bend or blur onto it — in w.
        static Vector4 WithReach(Vector4 shape, Vector4 layer, float lean, float widest, float blur, float clumpMax)
        {
            float span = Mathf.Abs(lean) * layer.w * clumpMax + widest + 2f * blur;
            shape.w = Mathf.Ceil(span / Mathf.Max(0.5f, layer.x)) + 1f;
            return shape;
        }

        static void Bake(Row row)
        {
            if (!row.Silhouette.IsCreated()) row.Silhouette.Create();
            Graphics.Blit(null, row.Silhouette, row.Bake, 0);
            row.Silhouette.GenerateMips();
        }

        void Update()
        {
            if (!_built) return;

            // A render texture can lose its contents (a lost device, a display mode change): draw it again.
            for (int i = 0; i < _rows.Length; i++)
                if (_rows[i].Silhouette != null && !_rows[i].Silhouette.IsCreated()) Bake(_rows[i]);

            if (grass != _grassOn || motes != _motesOn || !Mathf.Approximately(wind, _windOn) ||
                shadowColor != _shadowOn || grassLineColor != _lineOn)
                Apply();
        }

        // Pushes the Inspector's settings to the rows. Only on a change: nothing here runs every frame.
        void Apply()
        {
            _grassOn  = grass;
            _motesOn  = motes;
            _windOn   = wind;
            _shadowOn = shadowColor;
            _lineOn   = grassLineColor;

            foreach (Row r in _rows)
            {
                if (r.Renderer != null) r.Renderer.enabled = _grassOn;
                r.Draw.SetColor(BaseId,  shadowColor);
                r.Draw.SetColor(UpperId, grassLineColor);
                r.Draw.SetVector(GroundId, new Vector4(0.04f + _shift / ReferenceHeight, 0.3f + _shift / ReferenceHeight, 0f, 0f));
                Vector4 w = r.Wind;
                w.w = _windOn;
                r.Draw.SetVector(WindId, w);
                Vector4 m = r.Motes;
                if (!_motesOn) m.y = 0f;
                r.Draw.SetVector(MotesId, m);
            }
        }

        void OnDestroy()
        {
            if (_rows == null) return;
            foreach (Row r in _rows)
            {
                if (r == null) continue;
                if (r.Silhouette != null) { r.Silhouette.Release(); Destroy(r.Silhouette); }
                if (r.Bake != null) Destroy(r.Bake);
                if (r.Draw != null) Destroy(r.Draw);
                if (r.Quad != null) Destroy(r.Quad);
            }
        }
    }
}
