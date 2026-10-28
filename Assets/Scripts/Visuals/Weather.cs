using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>What falls over the city.</summary>
    public enum WeatherKind
    {
        /// <summary>Nothing.</summary>
        Off,
        /// <summary>A light rain: thin, faint streaks falling fast at a slant, with the wind.</summary>
        Rain,
        /// <summary>A light snow: soft flakes drifting down slowly, wobbling as they go.</summary>
        Snow,
    }

    /// <summary>
    /// Rain, or snow, falling over the city: two layers of it at two depths, each one quad facing the camera
    /// (JndUfo/Precipitation), the drops drawn procedurally. Both layers stand behind the play plane — one just
    /// behind the towers, one among the nearest buildings — so nothing falls in front of the UFO, the towers or
    /// a shot; the nearer layer falls faster on screen and in bigger drops, so the two read in depth.
    ///
    /// Its purpose is as much the study's as the scene's: steady, fast motion across the whole screen is where
    /// a frame rate shows most plainly — at a low one each streak visibly jumps from frame to frame, at a high
    /// one it slides. Every frame the fall is recomputed from the clock (in double precision, here, and handed
    /// to the shader as one number per layer), so the drops move every frame at the same speed at any frame
    /// rate. Rain is the stronger cue; snow is gentler, slower, softer.
    ///
    /// Light on purpose: a few hundred faint streaks or flakes, additive, cheap to draw. Nothing here knows
    /// where the boundary is. Created and built by <see cref="TowerManager"/> at Start.
    /// </summary>
    public class Weather : MonoBehaviour
    {
        [Tooltip("What falls: a light rain, a light snow, or nothing.")]
        public WeatherKind kind = WeatherKind.Rain;

        [Tooltip("How much falls: 1 is light, 2 about twice as much.")]
        [Range(0f, 2f)] public float amount = 1f;

        [Tooltip("How bright the drops or flakes are, as a multiple of their light default.")]
        [Range(0f, 3f)] public float brightness = 1f;

        // One layer's look and motion, in screen terms: SH = the screen's height at the layer's depth, SW its
        // width. Fall and drift per second; cell sizes, streak length and wobble in SH or SW; size in px at 1080p.
        struct Look
        {
            public float FallSH, DriftSH, CellSW, CellSH, Chance, LengthSH, SizePx, WobbleSW, Brightness;
            public Vector4 Color;   // linear
        }

        struct LayerSpec
        {
            public float Z;          // world depth of the layer
            public Look  Rain, Snow;
        }

        // Back to front: among the nearest buildings, then just behind the towers.
        static readonly LayerSpec[] Layers =
        {
            new LayerSpec
            {
                Z = 30f,
                Rain = new Look { FallSH = 1.1f, DriftSH = 0.12f, CellSW = 0.008f, CellSH = 0.14f, Chance = 0.45f,
                                  LengthSH = 0.05f, SizePx = 1.0f, Brightness = 0.07f,
                                  Color = new Vector4(0.55f, 0.62f, 0.75f, 0f) },
                Snow = new Look { FallSH = 0.09f, DriftSH = 0.035f, CellSW = 0.02f, CellSH = 0.07f, Chance = 0.5f,
                                  SizePx = 2.0f, WobbleSW = 0.008f, Brightness = 0.3f,
                                  Color = new Vector4(0.85f, 0.88f, 0.95f, 0f) },
            },
            new LayerSpec
            {
                Z = 6f,
                Rain = new Look { FallSH = 1.4f, DriftSH = 0.15f, CellSW = 0.012f, CellSH = 0.16f, Chance = 0.5f,
                                  LengthSH = 0.065f, SizePx = 1.3f, Brightness = 0.1f,
                                  Color = new Vector4(0.55f, 0.62f, 0.75f, 0f) },
                Snow = new Look { FallSH = 0.13f, DriftSH = 0.05f, CellSW = 0.03f, CellSH = 0.09f, Chance = 0.5f,
                                  SizePx = 3.2f, WobbleSW = 0.012f, Brightness = 0.45f,
                                  Color = new Vector4(0.85f, 0.88f, 0.95f, 0f) },
            },
        };

        const float ReferenceHeight = 1080f;
        const float SideMargin      = 6f;      // world units past the widest view any pan reaches
        const int   WrapCells       = 4096;    // the fall wraps after this many cells; the shader's hash repeats there

        static readonly int ModeId       = Shader.PropertyToID("_Mode");
        static readonly int ColorId      = Shader.PropertyToID("_Color");
        static readonly int BrightnessId = Shader.PropertyToID("_Brightness");
        static readonly int ShapeId      = Shader.PropertyToID("_Shape");
        static readonly int MotionId     = Shader.PropertyToID("_Motion");
        static readonly int SeedId       = Shader.PropertyToID("_Seed");

        sealed class Layer
        {
            public LayerSpec    Spec;
            public float        ScreenH, ScreenW;   // world units, at the layer's depth
            public Material     Mat;
            public Mesh         Quad;
            public MeshRenderer Renderer;
            public float        Fall, Slant, Length, Wobble, CellH;   // world, for the current kind
        }

        Layer[]     _layers;
        bool        _built;
        WeatherKind _kindOn;
        float       _amountOn, _brightnessOn;

        // The endless world: the layers follow the camera across (see Build), carried this far from where they were
        // built.
        bool        _follow;
        float       _originX, _carried;

        /// <summary>Builds the layers for <paramref name="cam"/> at rest, wide enough for pans of
        /// ±<paramref name="panRange"/> around <paramref name="originX"/>. With <paramref name="followCamera"/> — the
        /// endless world — they then follow the camera across without end: the drops are drawn from world position
        /// (JndUfo/Precipitation), so carrying the quads along leaves the rain or snow itself in the world. Call once;
        /// with <paramref name="followCamera"/>, on an object of its own (this one is moved).</summary>
        public void Build(Camera cam, float originX, float panRange, bool followCamera = false)
        {
            if (_built || cam == null) return;
            _follow  = followCamera;
            _originX = originX;
            Shader shader = Shader.Find("JndUfo/Precipitation");
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning("[Weather] JndUfo/Precipitation is missing or unsupported — no rain or snow.");
                return;
            }
            _built = true;

            float camY   = cam.transform.position.y;
            float camZ   = cam.transform.position.z;
            float tan    = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float aspect = Mathf.Max(cam.aspect, 16f / 9f);

            _layers = new Layer[Layers.Length];
            for (int i = 0; i < Layers.Length; i++)
            {
                LayerSpec spec = Layers[i];
                float d  = spec.Z - camZ;
                var layer = new Layer
                {
                    Spec    = spec,
                    ScreenH = 2f * tan * d,
                    ScreenW = 2f * tan * d * aspect,
                };

                // From just below the screen to just above it, as wide as every pan can see.
                float halfW = layer.ScreenW * 0.5f + panRange + SideMargin;
                float y0 = camY - layer.ScreenH * 0.6f, y1 = camY + layer.ScreenH * 0.6f;
                layer.Quad = new Mesh { name = "Weather_" + i, hideFlags = HideFlags.DontSave };
                layer.Quad.vertices  = new[] { new Vector3(originX - halfW, y0, spec.Z), new Vector3(originX + halfW, y0, spec.Z),
                                               new Vector3(originX + halfW, y1, spec.Z), new Vector3(originX - halfW, y1, spec.Z) };
                layer.Quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                layer.Quad.RecalculateBounds();

                layer.Mat = new Material(shader) { name = "Weather_" + i, hideFlags = HideFlags.DontSave };
                layer.Mat.SetFloat(SeedId, 17f + 31f * i);

                var go = new GameObject("Weather_" + i);
                go.transform.SetParent(transform, worldPositionStays: false);
                go.AddComponent<MeshFilter>().sharedMesh = layer.Quad;
                layer.Renderer = go.AddComponent<MeshRenderer>();
                layer.Renderer.sharedMaterial             = layer.Mat;
                layer.Renderer.shadowCastingMode          = ShadowCastingMode.Off;
                layer.Renderer.receiveShadows             = false;
                layer.Renderer.lightProbeUsage            = LightProbeUsage.Off;
                layer.Renderer.reflectionProbeUsage       = ReflectionProbeUsage.Off;
                layer.Renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                layer.Renderer.allowOcclusionWhenDynamic  = false;
                _layers[i] = layer;
            }

            Apply();
            Debug.Log($"[Weather] {kind}, {Layers.Length} layers.");
        }

        void Update()
        {
            if (!_built) return;
            if (kind != _kindOn || !Mathf.Approximately(amount, _amountOn) || !Mathf.Approximately(brightness, _brightnessOn))
                Apply();
            if (kind == WeatherKind.Off) return;

            // The fall so far, from the clock, wrapped at a whole number of cells — so the drops sit exactly
            // where they would unwrapped, and a long session never loses precision.
            double t = Time.timeAsDouble;
            foreach (Layer l in _layers)
            {
                double period = (double)l.CellH * WrapCells;
                float  fall   = (float)((t * l.Fall) % period);
                l.Mat.SetVector(MotionId, new Vector4(fall, l.Slant, l.Length, l.Wobble));
            }
        }

        // The endless world: carries the layers to stay centred on the camera's rest X (CameraRig runs first, at -50).
        // Only a change is written.
        void LateUpdate()
        {
            if (!_follow || !_built) return;
            CameraRig rig = CameraRig.Instance;
            float camX = rig != null ? rig.RestX : Camera.main != null ? Camera.main.transform.position.x : _originX;
            float carried = camX - _originX;
            if (carried == _carried) return;
            _carried = carried;

            Vector3 p = transform.position;
            p.x = carried;
            transform.position = p;
        }

        // Pushes the Inspector's settings to the layers. Only on a change.
        void Apply()
        {
            _kindOn       = kind;
            _amountOn     = amount;
            _brightnessOn = brightness;
            bool on   = kind != WeatherKind.Off;
            bool snow = kind == WeatherKind.Snow;
            foreach (Layer l in _layers)
            {
                l.Renderer.enabled = on;
                if (!on) continue;
                Look look = snow ? l.Spec.Snow : l.Spec.Rain;
                l.Fall   = look.FallSH * l.ScreenH;
                l.Slant  = look.FallSH > 0f ? look.DriftSH / look.FallSH : 0f;
                l.Length = look.LengthSH * l.ScreenH;
                l.Wobble = look.WobbleSW * l.ScreenW;
                l.CellH  = look.CellSH * l.ScreenH;
                l.Mat.SetFloat(ModeId, snow ? 1f : 0f);
                l.Mat.SetVector(ColorId, look.Color);
                l.Mat.SetFloat(BrightnessId, look.Brightness * brightness);
                l.Mat.SetVector(ShapeId, new Vector4(look.CellSW * l.ScreenW, l.CellH,
                                                     Mathf.Clamp01(look.Chance * amount), look.SizePx));
            }
        }

        void OnDestroy()
        {
            if (_layers == null) return;
            foreach (Layer l in _layers)
            {
                if (l == null) continue;
                if (l.Mat != null)  Destroy(l.Mat);
                if (l.Quad != null) Destroy(l.Quad);
            }
        }
    }
}
