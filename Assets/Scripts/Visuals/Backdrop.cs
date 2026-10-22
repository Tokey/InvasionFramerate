using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// The city behind the play plane, out of focus, as if the camera were focused on the UFO and the towers.
    ///
    /// Everything on <see cref="Layer"/> — the city's layers past the nearest one, and their lights (see
    /// <see cref="CitySkyline"/>) — is taken out of the main camera. Just before the main camera renders, this
    /// draws those few meshes itself, from the main camera's exact viewpoint, into a half-resolution texture
    /// with the sky left transparent, and blurs it (Hidden/JndUfo/BackdropBlur, two passes). The main camera
    /// then lays the result in behind the play plane on a quad at its far plane (JndUfo/Backdrop), over the
    /// sky, the stars and the clouds, which stay sharp. The near buildings, the towers, the UFO, the shots and
    /// the foreground are drawn by the main camera as before, in front of it — so nothing that matters to the
    /// study is softened, and nothing drawn over the city (the laser sight, a beam, a trail) is blurred along
    /// with it, as a depth-of-field post effect would.
    ///
    /// Every frame, at every frame rate: the city moves and animates exactly as before. It costs a handful of
    /// draw calls into a quarter of the pixels and two cheap blur passes, recorded into one command buffer —
    /// no second camera, which in URP costs a full camera's overhead every frame and showed up as periodic
    /// long frames at 500 fps.
    ///
    /// Switch <see cref="blur"/> off and the city is drawn by the main camera again, sharp, as it was.
    /// Created and built by <see cref="TowerManager"/> at Start, after the city.
    /// </summary>
    [DefaultExecutionOrder(1000)]   // after CameraRig has posed the main camera for the frame
    public class Backdrop : MonoBehaviour
    {
        /// <summary>The layer of everything drawn out of focus. An unnamed user layer that nothing else in
        /// the project uses; the main camera stops drawing it while the blur is on.</summary>
        public const int Layer = 29;

        /// <summary>The sorting order the backdrop's composite is drawn at, and the sky's quads just below it
        /// (see NightSky). URP sorts transparents by sorting order before render queue, so anything at an order
        /// below the default 0 — the weapon sight, at −1 — would otherwise be drawn first and then covered by
        /// the blurred city. The background goes first, whatever order the rest of the scene uses.</summary>
        public const int SortingOrder = -30000;

        [Tooltip("Draw the city behind the play plane out of focus. Off: the main camera draws it sharp, as before.")]
        public bool blur = true;

        [Tooltip("How far out of focus: the blur's spread (sigma), in pixels of a 1080p screen — scaled with " +
                 "the screen, so it looks the same at any resolution. 2-3 reads as a lens focused on the play " +
                 "plane; much more and the city turns to mush.")]
        [Range(0f, 8f)] public float blurPx = 2.4f;

        [Tooltip("The backdrop's resolution, as a fraction of the screen's. Half is plenty under the blur and " +
                 "costs a quarter of the pixels.")]
        [Range(0.25f, 1f)] public float resolution = 0.5f;

        const float ReferenceHeight = 1080f;
        static readonly int SigmaId     = Shader.PropertyToID("_Sigma");
        static readonly int TimeId      = Shader.PropertyToID("_Time");
        static readonly int CameraPosId = Shader.PropertyToID("_WorldSpaceCameraPos");

        Camera         _main;
        bool           _mainHadLayer;
        Renderer[]     _draw;          // the backdrop's renderers, solid ones first, then lights back to front
        Material       _blurMat, _compositeMat;
        Mesh           _quad;
        MeshRenderer   _composite;
        RenderTexture  _rtA, _rtB;    // A: the city, drawn and then blurred; B: halfway through the blur
        CommandBuffer  _cmd;
        int            _w, _h;
        bool           _built, _on;

        /// <summary>Sets the backdrop up behind <paramref name="main"/>, for everything already on
        /// <see cref="Layer"/>. Call once, after the city is built.</summary>
        public void Build(Camera main)
        {
            if (_built || main == null) return;

            Shader composite  = Shader.Find("JndUfo/Backdrop");
            Shader blurShader = Shader.Find("Hidden/JndUfo/BackdropBlur");
            if (composite == null || !composite.isSupported || blurShader == null || !blurShader.isSupported ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Debug.LogWarning("[Backdrop] The backdrop shaders or a half-float render texture are unavailable " +
                                 "here — the city stays in focus.");
                return;
            }

            _main         = main;
            _mainHadLayer = (main.cullingMask & (1 << Layer)) != 0;
            _draw         = Gather(main.transform.position);
            _compositeMat = new Material(composite)  { name = "Backdrop",     hideFlags = HideFlags.DontSave };
            _blurMat      = new Material(blurShader) { name = "BackdropBlur", hideFlags = HideFlags.DontSave };

            // The quad the main camera shows it on, riding just inside its far plane.
            _quad = MakeQuad();
            var quadGo = new GameObject("BackdropComposite");
            quadGo.transform.SetParent(main.transform, worldPositionStays: false);
            quadGo.AddComponent<MeshFilter>().sharedMesh = _quad;
            _composite = quadGo.AddComponent<MeshRenderer>();
            _composite.sharedMaterial             = _compositeMat;
            _composite.shadowCastingMode          = ShadowCastingMode.Off;
            _composite.receiveShadows             = false;
            _composite.lightProbeUsage            = LightProbeUsage.Off;
            _composite.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            _composite.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            _composite.allowOcclusionWhenDynamic  = false;
            _composite.sortingOrder               = SortingOrder;
            _composite.enabled                    = false;

            _cmd = new CommandBuffer { name = "Backdrop" };
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            _built = true;

            Debug.Log($"[Backdrop] {_draw.Length} renderers behind the play plane drawn out of focus: blur " +
                      $"{blurPx:0.0} px at 1080p, {resolution:0.##} resolution.");
            LateUpdate();   // allocate and pose before the first frame
        }

        // Every renderer on the backdrop layer: solid ones first, then the transparent ones (the lights) back to
        // front from the camera, the order the main camera would have drawn them in.
        static Renderer[] Gather(Vector3 eye)
        {
            var solid = new List<Renderer>();
            var clear = new List<Renderer>();
            foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                if (r.gameObject.layer != Layer || r.sharedMaterial == null) continue;
                if (r is ParticleSystemRenderer)
                {
                    Debug.LogWarning($"[Backdrop] '{r.name}' is a particle system — those are drawn by the main " +
                                     "camera only; move it off the backdrop layer.");
                    continue;
                }
                (r.sharedMaterial.renderQueue <= (int)RenderQueue.GeometryLast ? solid : clear).Add(r);
            }
            clear.Sort((a, b) => (b.bounds.center - eye).sqrMagnitude.CompareTo((a.bounds.center - eye).sqrMagnitude));
            solid.AddRange(clear);
            return solid.ToArray();
        }

        void OnEnable()
        {
            if (_built) LateUpdate();
        }

        void OnDisable()
        {
            if (_built) SetOn(false);
        }

        void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            if (_built) SetOn(false);
            if (_composite != null)    Destroy(_composite.gameObject);
            if (_compositeMat != null) Destroy(_compositeMat);
            if (_blurMat != null)      Destroy(_blurMat);
            if (_quad != null)         Destroy(_quad);
            _cmd?.Release();
        }

        void LateUpdate()
        {
            if (!_built) return;
            bool want = blur && _main != null && isActiveAndEnabled;
            if (want != _on) SetOn(want);
            if (!_on) return;

            EnsureTargets();

            // The quad fills the view with room to spare; the shader samples by screen position.
            float dist = _main.farClipPlane * 0.9f;
            float h    = 2f * dist * Mathf.Tan(_main.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f;
            Transform q = _composite.transform;
            q.localPosition = new Vector3(0f, 0f, dist);
            q.localRotation = Quaternion.identity;
            q.localScale    = new Vector3(h * _main.aspect, h, 1f);

            _blurMat.SetFloat(SigmaId, blurPx * _h / ReferenceHeight);
        }

        // Draws and blurs the backdrop, from the main camera's viewpoint this frame, just before it renders.
        // Allocates nothing: the command buffer is reused.
        void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!_on || camera != _main || _rtA == null) return;

            // The time and the eye the city's shaders read, as URP will set them for this frame.
            float t = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;

            _cmd.Clear();
            _cmd.SetRenderTarget(_rtA);
            _cmd.ClearRenderTarget(true, true, Color.clear);
            // The camera's own projection: SetViewProjectionMatrices makes the platform's adjustments for a render
            // texture target itself (flipping it where the platform needs that), as for a camera rendering into one.
            _cmd.SetViewProjectionMatrices(_main.worldToCameraMatrix, _main.projectionMatrix);
            _cmd.SetGlobalVector(TimeId, new Vector4(t / 20f, t, t * 2f, t * 3f));
            _cmd.SetGlobalVector(CameraPosId, _main.transform.position);
            for (int i = 0; i < _draw.Length; i++)
            {
                Renderer r = _draw[i];
                if (r != null && r.enabled && r.gameObject.activeInHierarchy)
                    _cmd.DrawRenderer(r, r.sharedMaterial, 0, 0);
            }
            _cmd.Blit(_rtA, _rtB, _blurMat, 0);
            _cmd.Blit(_rtB, _rtA, _blurMat, 1);
            context.ExecuteCommandBuffer(_cmd);
        }

        void SetOn(bool on)
        {
            _on = on;
            if (_main != null)
            {
                if (on)                 _main.cullingMask &= ~(1 << Layer);
                else if (_mainHadLayer) _main.cullingMask |= 1 << Layer;
            }
            if (_composite != null) _composite.enabled = on;
            if (!on) ReleaseTargets();
        }

        // (Re)allocates the textures for the screen's current size. Allocates nothing while it stays the same.
        void EnsureTargets()
        {
            int w = Mathf.Max(16, Mathf.RoundToInt(_main.pixelWidth  * resolution));
            int h = Mathf.Max(16, Mathf.RoundToInt(_main.pixelHeight * resolution));
            if (_rtA != null && w == _w && h == _h && _rtA.IsCreated() && _rtB.IsCreated()) return;

            ReleaseTargets();
            _w = w;
            _h = h;
            _rtA = NewTarget("BackdropA", 24);
            _rtB = NewTarget("BackdropB", 0);
            _compositeMat.mainTexture = _rtA;
        }

        RenderTexture NewTarget(string targetName, int depthBits)
        {
            var desc = new RenderTextureDescriptor(_w, _h, RenderTextureFormat.ARGBHalf, depthBits)
            {
                msaaSamples = 1,
                useMipMap   = false,
                sRGB        = false,
            };
            var rt = new RenderTexture(desc)
            {
                name       = targetName,
                hideFlags  = HideFlags.DontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        void ReleaseTargets()
        {
            if (_rtA != null) { _rtA.Release(); Destroy(_rtA); _rtA = null; }
            if (_rtB != null) { _rtB.Release(); Destroy(_rtB); _rtB = null; }
            _w = _h = 0;
        }

        static Mesh MakeQuad()
        {
            var m = new Mesh { name = "BackdropQuad", hideFlags = HideFlags.DontSave };
            m.vertices  = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                                  new Vector3(0.5f, 0.5f, 0f),   new Vector3(-0.5f, 0.5f, 0f) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }
    }
}
