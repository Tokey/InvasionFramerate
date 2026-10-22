using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// The row of radio towers standing on the embankment in front of the city — and the hidden frame-rate
    /// boundary is always one of them. Any tower can be the one: every round the whole field is dealt
    /// afresh around the boundary, and the tower that stands on it is drawn exactly like every other.
    ///
    /// Why it is built this way. With the fog gone nothing hides the target, so the target must not be
    /// findable by looking. A single special tower model would give it away at a glance; so would a tower
    /// that stands slightly apart, or one that never moves while the rest do. So:
    ///   • Every tower, the boundary's included, is a random pick from the same pool of models (the scene's
    ///     tower prefabs), at a random height from the same range, turned to a random angle, in the same
    ///     material.
    ///   • The towers stand on a jittered lattice that is anchored on the boundary each round: the
    ///     boundary is one of its points, with its own random offset from its slot drawn exactly like every
    ///     other tower's. Every tower is therefore statistically interchangeable with the boundary one —
    ///     nothing about the spacing around it is special.
    ///   • The lattice is dense (<see cref="spacing"/> is small next to the hit radius), so a tower stands
    ///     near any point on the screen: aiming "at a tower" is little better than aiming anywhere.
    ///   • The field is re-dealt between rounds, all at once, so nothing can be learned by remembering which
    ///     tower moved.
    /// The scene's own target tower (RadioTower) is never drawn; it still marks the boundary's position for
    /// the split, the scoring and the logs. In the reveal, the field's boundary tower lights up.
    ///
    /// The change of rows is ONE crossfade: the row on screen stays exactly where it is until the next
    /// one is dealt (as the reset's camera pan begins), and then the two dissolve into each other over
    /// <see cref="crossfadeSec"/>. Each row wears its own copy of the tower material — two groups, A and
    /// B, swapping every deal — so one can fade out while the other fades in. The tower the last reveal
    /// lit keeps its glow as its row leaves, and the glow never passes to the new boundary: that one
    /// lights only in its own reveal.
    ///
    /// Every instance is made at Start; dealing a round only moves, toggles and re-materials them, with
    /// arrays prepared up front, so it allocates nothing. Its own System.Random keeps the experiment's
    /// UnityEngine.Random sequence untouched. Created and driven by <see cref="TowerManager"/>.
    /// </summary>
    public class TowerField : MonoBehaviour
    {
        [Header("Field")]
        [Tooltip("Average distance between neighbouring towers, in world units on the play plane. Keep it well " +
                 "below the hit radius (5.5): the denser the field, the less a tower's position says about " +
                 "where the boundary is.")]
        [Min(1f)] public float spacing = 2.7f;

        [Tooltip("Random offset of each tower from its lattice slot, as a fraction of the spacing.")]
        [Range(0f, 0.45f)] public float jitter = 0.3f;

        [Tooltip("Where the towers top out on average, as a fraction of the screen's height. Below the UFO's " +
                 "lowest altitude even for the tallest towers, so the UFO flies above the field rather than " +
                 "through it.")]
        [Range(0.3f, 0.8f)] public float topViewport = 0.445f;

        [Tooltip("Random variation of each tower's height, ± this fraction.")]
        [Range(0f, 0.5f)] public float heightVariation = 0.2f;

        [Tooltip("Extra world units of towers beyond the widest view any camera pan can reach, each side.")]
        [Min(0f)] public float sideMargin = 3f;

        [Tooltip("Instances kept per tower model. Raised automatically so two whole rows (one fading out, one " +
                 "fading in) always fit.")]
        [Min(1)] public int poolPerModel = 6;

        [Header("Reset")]
        [Tooltip("Seconds the old row and the new one take to dissolve into each other when the field is " +
                 "re-dealt. The deal comes at the start of the reset's camera pan, so the crossfade plays " +
                 "during the pan, under the veil, and is done when GO! lifts it; matching the pan (0.45 s) " +
                 "makes the whole change of scene one movement.")]
        [Min(0.01f)] public float crossfadeSec = 0.45f;

        [Header("Beacons")]
        [Tooltip("Red aviation lights on some of the towers. Which towers get one is random every round, the " +
                 "boundary's included.")]
        public bool beacons = true;
        [ColorUsage(false, true)] public Color beaconColor = new Color(4f, 0.3f, 0.18f, 1f);
        [Tooltip("Pulses per second.")]
        [Min(0.05f)] public float beaconHz = 0.5f;
        [Tooltip("Share of towers that carry a beacon.")]
        [Range(0f, 1f)] public float beaconShare = 0.35f;
        [Tooltip("Beacon size, world units.")]
        [Min(0.01f)] public float beaconSize = 0.34f;

        /// <summary>Renderers of the tower standing on the boundary this round that light up in the reveal
        /// (its flat ground tile is left out).</summary>
        public Renderer[] BoundaryRenderers => _boundary != null ? _boundary.Lit : s_noRenderers;

        /// <summary>World position of the top of the boundary tower this round.</summary>
        public Vector3 BoundaryTop { get; private set; }

        /// <summary>True once <see cref="Build"/> found at least one usable model.</summary>
        public bool Ready => _models.Count > 0;

        // ── Private ──────────────────────────────────────────────────────────

        sealed class Model
        {
            public float BaseY;         // bottom of the tower relative to the pivot, world units at scale 1
            public float Height;        // top above the bottom at scale 1
            public Vector3 Center;      // visual centre relative to the pivot at scale 1 (base rotation)
            public Quaternion BaseRotation;
            public Vector3 BaseScale;
            public readonly List<Instance> Pool = new List<Instance>();
        }

        enum Row { None, Current, Leaving }

        sealed class Instance
        {
            public Model        Model;
            public GameObject   Go;
            public Renderer[]   Lit;          // every renderer that is drawn (flat ground tiles are not)
            public Material[][] GroupA;       // per Lit renderer: every slot in group A's material
            public Material[][] GroupB;       // likewise for group B
            public Material[][] Highlight;    // per Lit renderer: the reveal's glow
            public int          Group;
            public Row          Row;
        }

        static readonly Renderer[] s_noRenderers = new Renderer[0];
        static readonly int        FadeId        = Shader.PropertyToID("_Fade");

        readonly List<Model>      _models  = new List<Model>();
        readonly List<Instance>   _current = new List<Instance>(64);
        readonly List<Instance>   _leaving = new List<Instance>(64);
        readonly List<Renderer>[] _beacons = { new List<Renderer>(), new List<Renderer>() };
        readonly Material[]       _mats        = new Material[2];   // [0] is TowerManager's; [1] is a copy, ours
        readonly Material[]       _beaconMats  = new Material[2];
        readonly float[]          _groupFade   = { 1f, 0f };

        int           _group;          // which group the current row wears
        bool          _crossfading;
        float         _crossStart;
        float         _leavingFrom;    // the leaving row's fade when its crossfade began
        Instance      _boundary;
        bool          _glowOn;         // the reveal's glow is on
        Instance      _lit;            // the tower wearing it, if any (it may be leaving)
        Mesh          _quad;
        System.Random _rng;
        float         _groundY, _playZ, _xMin, _xMax, _targetHeight;

        /// <summary>
        /// Makes the pool from <paramref name="models"/> (prefabs or model assets) in the
        /// <paramref name="body"/> material (and a copy of it for the second group), with
        /// <paramref name="highlight"/> ready for the reveal, and sizes the field to the camera's view at
        /// every pan. Call once, before the first <see cref="Deal"/>.
        /// </summary>
        public void Build(IList<GameObject> models, Material body, Material highlight, float groundY, float playZ,
                          Camera cam, float originX, float panRange)
        {
            if (_models.Count > 0) return;

            _rng     = new System.Random(System.Environment.TickCount);
            _groundY = groundY;
            _playZ   = playZ;

            float halfW = 23.4f;
            if (cam != null)
            {
                float d = playZ - cam.transform.position.z;
                halfW = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * d * Mathf.Max(cam.aspect, 16f / 9f);
                float yTop = cam.transform.position.y +
                             (2f * topViewport - 1f) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * d;
                _targetHeight = Mathf.Max(1f, yTop - groundY);
            }
            else _targetHeight = 8f;

            _xMin = originX - halfW - panRange - sideMargin;
            _xMax = originX + halfW + panRange + sideMargin;

            int modelCount = 0;
            if (models != null) foreach (GameObject m in models) if (m != null) modelCount++;
            if (modelCount == 0 || body == null)
            {
                Debug.LogWarning("[TowerField] No tower models — the field stays empty.");
                return;
            }

            _mats[0] = body;
            _mats[1] = new Material(body) { name = body.name + "_B", hideFlags = HideFlags.DontSave };
            SetGroupFade(0, 1f);
            SetGroupFade(1, 0f);

            // Two whole rows must fit at once: the one fading out and the one fading in.
            int slots   = Mathf.CeilToInt((_xMax - _xMin) / Mathf.Max(1f, spacing)) + 3;
            int perPool = Mathf.Max(poolPerModel, Mathf.CeilToInt(2f * slots / modelCount) + 2);

            foreach (GameObject prefab in models)
            {
                if (prefab == null) continue;
                Model model = Measure(prefab);
                if (model == null) continue;
                for (int i = 0; i < perPool; i++)
                    model.Pool.Add(MakeInstance(prefab, model, highlight, i));
                _models.Add(model);
            }

            if (beacons)
            {
                _quad = MakeQuad();
                for (int g = 0; g < 2; g++)
                {
                    _beaconMats[g] = GlowMaterial.Create(GlowShape.Dot, beaconColor, 1.6f, "TowerBeacon" + g);
                    for (int i = 0; i < slots; i++) _beacons[g].Add(MakeBeacon(g, i));
                }
            }

            Debug.Log($"[TowerField] {_models.Count} tower models x {perPool}, spacing {spacing:0.0}u over " +
                      $"x {_xMin:0.0}..{_xMax:0.0}, towers {_targetHeight:0.0}u tall on average.");
        }

        void OnDestroy()
        {
            if (_mats[1] != null) Destroy(_mats[1]);
            for (int g = 0; g < 2; g++) if (_beaconMats[g] != null) Destroy(_beaconMats[g]);
            if (_quad != null) Destroy(_quad);
        }

        void Update()
        {
            if (_crossfading)
            {
                // A smoothstep in time — a function of time alone, so it plays the same at any frame rate.
                float k = Mathf.Clamp01((Time.unscaledTime - _crossStart) / Mathf.Max(0.01f, crossfadeSec));
                float s = k * k * (3f - 2f * k);
                SetGroupFade(_group, s);
                SetGroupFade(1 - _group, _leavingFrom * (1f - s));
                if (k >= 1f)
                {
                    _crossfading = false;
                    ClearLeaving();
                }
            }

            // A slow, short pulse, all towers together, like a field of aviation lights — each row's beacons
            // fading with their row.
            float ph    = Mathf.Repeat(Time.unscaledTime * beaconHz, 1f);
            float pulse = Mathf.Sin(ph * Mathf.PI);
            pulse *= pulse; pulse *= pulse; pulse *= pulse;   // ^8: brief flash, long rest
            float level = 0.08f + 0.92f * pulse;
            for (int g = 0; g < 2; g++)
                if (_beaconMats[g] != null)
                    GlowMaterial.SetColor(_beaconMats[g], GlowMaterial.Scaled(beaconColor, level * _groupFade[g]));
        }

        // ── Dealing ──────────────────────────────────────────────────────────

        /// <summary>
        /// Deals the field for a round whose boundary is at <paramref name="boundaryX"/>: a fresh lattice
        /// anchored on it, fresh models, heights, angles and beacons for every tower, the boundary's included.
        /// With <paramref name="animate"/> the row on screen and the new one crossfade over
        /// <see cref="crossfadeSec"/>; without it, the new row is simply there.
        /// </summary>
        public void Deal(float boundaryX, bool animate = false)
        {
            if (_models.Count == 0) return;

            // Anything still fading out from an earlier deal goes now.
            ClearLeaving();

            int old = _group;
            if (animate)
            {
                // The row on screen stays exactly as it is and fades out under the new one — a tower lit by
                // the last reveal still lit, going out with the beam over the same pan.
                for (int i = 0; i < _current.Count; i++)
                {
                    _current[i].Row = Row.Leaving;
                    _leaving.Add(_current[i]);
                }
                _current.Clear();
                _leavingFrom = _groupFade[old];
                _group       = 1 - old;
            }
            else
            {
                Unlight();
                for (int i = 0; i < _current.Count; i++) Release(_current[i]);
                _current.Clear();
                foreach (Renderer b in _beacons[old]) b.enabled = false;
            }
            foreach (Renderer b in _beacons[_group]) b.enabled = false;
            _boundary = null;

            float s      = Mathf.Max(1f, spacing);
            float j      = jitter * s;
            float anchor = boundaryX - Rand(-j, j);   // the boundary's own slot, offset like any other
            int   beacon = 0;

            PlaceTower(boundaryX, true, ref beacon);

            int kMin = Mathf.FloorToInt((_xMin - anchor) / s) - 1;
            int kMax = Mathf.CeilToInt((_xMax - anchor) / s) + 1;
            for (int k = kMin; k <= kMax; k++)
            {
                if (k == 0) continue;
                float x = anchor + k * s + Rand(-j, j);
                if (x < _xMin || x > _xMax) continue;
                PlaceTower(x, false, ref beacon);
            }

            if (animate)
            {
                _crossfading = true;
                _crossStart  = Time.unscaledTime;
                SetGroupFade(_group, 0f);   // the new row starts unseen this very frame
            }
            else
            {
                _crossfading = false;
                SetGroupFade(_group, 1f);
            }
        }

        /// <summary>
        /// Turns the reveal's glow on, on this round's boundary tower, or off. Called every frame of a fade,
        /// so only a change does anything. Once on, the glow stays on the tower it lit until it is turned
        /// off — through a new deal, as that tower's row fades out — and never passes to the new boundary,
        /// which lights only when the glow is next turned on: a tower lighting up mid-reset would give the
        /// next round away.
        /// </summary>
        public void SetBoundaryHighlight(bool on)
        {
            if (on == _glowOn) return;
            _glowOn = on;
            if (!on) Unlight();
            else if (_boundary != null)
            {
                _lit = _boundary;
                for (int i = 0; i < _lit.Lit.Length; i++)
                    if (_lit.Lit[i] != null) _lit.Lit[i].sharedMaterials = _lit.Highlight[i];
            }
        }

        // Puts the lit tower, if any, back in its row's material.
        void Unlight()
        {
            if (_lit == null) return;
            Material[][] back = _lit.Group == 0 ? _lit.GroupA : _lit.GroupB;
            for (int i = 0; i < _lit.Lit.Length; i++)
                if (_lit.Lit[i] != null) _lit.Lit[i].sharedMaterials = back[i];
            _lit = null;
        }

        void PlaceTower(float x, bool isBoundary, ref int beacon)
        {
            Instance inst = TakeInstance();
            if (inst == null) return;

            Model m      = inst.Model;
            float height = _targetHeight * (1f + Rand(-heightVariation, heightVariation));
            float scale  = height / m.Height;
            Quaternion yaw = Quaternion.Euler(0f, Rand(0f, 360f), 0f);

            Transform t = inst.Go.transform;
            t.rotation   = yaw * m.BaseRotation;
            t.localScale = m.BaseScale * scale;
            Vector3 c    = yaw * m.Center * scale;   // keep the tower's visual centre exactly on x
            t.position   = new Vector3(x - c.x, _groundY - m.BaseY * scale, _playZ - c.z);

            // This row's material group.
            inst.Group = _group;
            Material[][] mats = _group == 0 ? inst.GroupA : inst.GroupB;
            for (int i = 0; i < inst.Lit.Length; i++)
                if (inst.Lit[i] != null) inst.Lit[i].sharedMaterials = mats[i];

            inst.Go.SetActive(true);
            inst.Row = Row.Current;
            _current.Add(inst);

            if (isBoundary)
            {
                _boundary   = inst;
                BoundaryTop = new Vector3(x, _groundY + height, _playZ);
            }

            List<Renderer> rowBeacons = _beacons[_group];
            if (beacons && beacon < rowBeacons.Count && Rand(0f, 1f) < beaconShare)
            {
                Renderer b = rowBeacons[beacon++];
                b.transform.position = new Vector3(x, _groundY + height + beaconSize * 0.15f, _playZ - 0.4f);
                b.enabled = true;
            }
        }

        // The leaving row is gone: its towers back in the pool, its beacons off, its group unseen.
        void ClearLeaving()
        {
            if (_lit != null && _lit.Row == Row.Leaving) Unlight();   // the glow itself stays on: see SetBoundaryHighlight
            for (int i = 0; i < _leaving.Count; i++) Release(_leaving[i]);
            _leaving.Clear();
            int g = 1 - _group;
            foreach (Renderer b in _beacons[g]) b.enabled = false;
            SetGroupFade(g, 0f);
        }

        static void Release(Instance inst)
        {
            inst.Go.SetActive(false);
            inst.Row = Row.None;
        }

        void SetGroupFade(int g, float f)
        {
            _groupFade[g] = f;
            if (_mats[g] != null) _mats[g].SetFloat(FadeId, f);
        }

        Instance TakeInstance()
        {
            int start = _rng.Next(_models.Count);
            for (int n = 0; n < _models.Count; n++)
            {
                Model m = _models[(start + n) % _models.Count];
                int pick = _rng.Next(m.Pool.Count);
                for (int k = 0; k < m.Pool.Count; k++)
                {
                    Instance inst = m.Pool[(pick + k) % m.Pool.Count];
                    if (inst.Row == Row.None) return inst;
                }
            }
            return null;
        }

        float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

        // ── Construction ─────────────────────────────────────────────────────

        // Measures a model once, at the origin with its own rotation and scale: the bottom and top of the
        // tower itself (its flat ground tile, which on some models sits a little below the mast, is left
        // out), and where its visual centre sits relative to its pivot.
        Model Measure(GameObject prefab)
        {
            GameObject probe = Instantiate(prefab, Vector3.zero, prefab.transform.rotation);
            probe.SetActive(true);

            bool any = false;
            Bounds solid = default;
            foreach (MeshFilter mf in probe.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Bounds b = WorldBounds(mf);
                if (IsFlat(b)) continue;
                if (!any) { solid = b; any = true; }
                else solid.Encapsulate(b);
            }

            Model model = null;
            if (any && solid.size.y > 0.01f)
            {
                model = new Model
                {
                    BaseY        = solid.min.y,
                    Height       = Mathf.Max(0.01f, solid.size.y),
                    Center       = new Vector3(solid.center.x, 0f, solid.center.z),
                    BaseRotation = prefab.transform.rotation,
                    BaseScale    = prefab.transform.localScale,
                };
            }
            else Debug.LogWarning($"[TowerField] '{prefab.name}' has no measurable mesh — skipped.");

            Destroy(probe);
            return model;
        }

        Instance MakeInstance(GameObject prefab, Model model, Material highlight, int index)
        {
            GameObject go = Instantiate(prefab, transform);
            go.name = $"{prefab.name}_{index}";
            go.SetActive(false);

            foreach (Collider col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false;

            var lit = new List<Renderer>();
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode          = ShadowCastingMode.Off;
                r.receiveShadows             = false;
                r.lightProbeUsage            = LightProbeUsage.Off;
                r.reflectionProbeUsage       = ReflectionProbeUsage.Off;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

                // A model's flat ground tile is not drawn at all: the towers stand on the embankment, and a
                // tile spreading under each one would read as a plate lying on it (or sunk in the water).
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && IsFlat(WorldBounds(mf)))
                {
                    r.enabled = false;
                    continue;
                }
                lit.Add(r);
            }

            var inst = new Instance
            {
                Model     = model,
                Go        = go,
                Lit       = lit.ToArray(),
                GroupA    = new Material[lit.Count][],
                GroupB    = new Material[lit.Count][],
                Highlight = new Material[lit.Count][],
                Row       = Row.None,
            };
            for (int i = 0; i < lit.Count; i++)
            {
                int n = Mathf.Max(1, lit[i].sharedMaterials.Length);
                inst.GroupA[i]    = Filled(n, _mats[0]);
                inst.GroupB[i]    = Filled(n, _mats[1]);
                inst.Highlight[i] = Filled(n, highlight != null ? highlight : _mats[0]);
                lit[i].sharedMaterials = inst.GroupA[i];
            }
            return inst;
        }

        static Material[] Filled(int n, Material m)
        {
            var a = new Material[n];
            for (int i = 0; i < n; i++) a[i] = m;
            return a;
        }

        Renderer MakeBeacon(int group, int index)
        {
            var go = new GameObject($"Beacon_{(group == 0 ? 'A' : 'B')}{index}");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localScale = Vector3.one * beaconSize;
            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial             = _beaconMats[group];
            r.shadowCastingMode          = ShadowCastingMode.Off;
            r.receiveShadows             = false;
            r.lightProbeUsage            = LightProbeUsage.Off;
            r.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.enabled = false;
            return r;
        }

        // A unit quad facing the camera (the camera never rotates, so −Z always faces it).
        static Mesh MakeQuad()
        {
            var m = new Mesh { name = "BeaconQuad", hideFlags = HideFlags.DontSave };
            m.vertices  = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                                  new Vector3(0.5f, 0.5f, 0f),   new Vector3(-0.5f, 0.5f, 0f) };
            m.uv        = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }

        static Bounds WorldBounds(MeshFilter mf)
        {
            Bounds lb = mf.sharedMesh.bounds;
            Matrix4x4 m = mf.transform.localToWorldMatrix;
            Vector3 c = lb.center, e = lb.extents;
            Bounds wb = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(c.x + ((i & 1) == 0 ? -e.x : e.x),
                                         c.y + ((i & 2) == 0 ? -e.y : e.y),
                                         c.z + ((i & 4) == 0 ? -e.z : e.z));
                wb.Encapsulate(m.MultiplyPoint3x4(corner));
            }
            return wb;
        }

        // A ground tile: flat in y next to its width.
        static bool IsFlat(Bounds b) => b.size.y < 0.02f * Mathf.Max(b.size.x, b.size.z);
    }
}
