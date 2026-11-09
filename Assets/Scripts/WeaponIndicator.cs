using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JndUfo
{
    /// <summary>
    /// Top-right HUD chip that names the active weapon ("LASER" or "SHOCKWAVE CANNON") and draws a
    /// live animated icon next to it — a cyan bolt sliding down a vertical rail for the laser, and
    /// three expanding purple shock arcs for the shockwave cannon.
    ///
    /// Self-constructs under the HUD canvas in <see cref="Awake"/>. The animation runs on
    /// <see cref="Time.unscaledTime"/> so it never stalls or hiccups during low-framerate rounds.
    /// </summary>
    public class WeaponIndicator : MonoBehaviour
    {
        [Header("References  (auto-resolved if left empty)")]
        public PerturbationController perturbation;

        [Header("Placement")]
        [Tooltip("Match the top-left hit/miss tally's vertical offset so both top corners sit on one row.")]
        public bool matchScoreRowY = true;
        [Tooltip("Inset from the top-right corner of the canvas (pixels). X is always used; Y is used when matchScoreRowY is off.")]
        public Vector2 margin = new Vector2(28f, 20f);
        public float   height = 54f;

        [Header("Typography")]
        public float fontSize          = 36f;
        public float characterSpacing  = 5f;
        public string laserText        = "LASER";
        public string shockwaveText    = "SHOCKWAVE";
        [Tooltip("Only show the word during practice rounds, leaving the animated icon alone during main rounds. " +
                 "Off by default so the weapon name and icon are always visible in the top-right corner.")]
        public bool labelDuringPracticeOnly = false;

        [Header("Palette")]
        public Color laserColor     = new Color(0.35f, 1.00f, 0.85f, 1f);
        public Color shockwaveColor = new Color(0.25f, 0.05f, 0.98f, 1f);
        public Color plateColor     = new Color(0.03f, 0.05f, 0.09f, 0.58f);

        [Header("Animation  (wall-clock — unaffected by frame-rate caps)")]
        [Tooltip("Laser bolt trips down the rail per second.")]
        public float laserBoltRate = 0.9f;
        [Tooltip("Shockwave rings emitted per second.")]
        public float shockwaveWaveRate = 0.75f;

        const int ArcCount = 3;
        const int TexSize  = 96;

        Canvas        _canvas;
        RectTransform _root;
        Image         _plate;
        Image         _accentBar;
        TextMeshProUGUI _label;

        // Laser icon parts
        RectTransform _laserGroup;
        Image         _laserEmitter;
        Image         _laserRail;
        Image         _laserBolt;
        Image         _laserGround;

        // Shockwave icon parts
        RectTransform   _shockGroup;
        Image           _shockOrigin;
        RectTransform[] _arcRects;
        Image[]         _arcImages;

        Texture2D _discTex;
        Texture2D _arcTex;
        Sprite    _discSprite;
        Sprite    _arcSprite;

        int  _shownMode     = -1;   // -1 = uninitialised, 0 = laser, 1 = shockwave
        int  _shownPractice = -1;   // -1 = uninitialised, 0 = main,  1 = practice
        bool _yAligned;

        void Awake()
        {
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();
            BuildSprites();
            BuildHud();
        }

        void Start()
        {
            AlignWithScoreRow();
            ApplyMode(IsShockwave ? 1 : 0, IsPractice);
        }

        void OnDestroy()
        {
            if (_root    != null) Destroy(_root.gameObject);
            if (_discTex != null) Destroy(_discTex);
            if (_arcTex  != null) Destroy(_arcTex);
        }

        bool IsShockwave =>
            (perturbation != null && perturbation.CurrentConfig != null && perturbation.CurrentConfig.IsShockwave) ||
            (ExperimentDirector.Instance != null && ExperimentDirector.Instance.Config != null &&
             ExperimentDirector.Instance.Config.IsShockwave);

        bool IsPractice => perturbation != null && perturbation.PracticeMode;

        public void SetLevel(LevelType level)
        {
            if (_root == null) return;
            ApplyMode(level == LevelType.Shockwave ? 1 : 0, IsPractice);
        }

        // ── Construction ─────────────────────────────────────────────────────

        void BuildSprites()
        {
            _discTex    = MakeDiscTexture(TexSize);
            _arcTex     = MakeDownwardArcTexture(TexSize);
            _discSprite = Sprite.Create(_discTex, new Rect(0, 0, TexSize, TexSize), new Vector2(0.5f, 0.5f));
            _arcSprite  = Sprite.Create(_arcTex,  new Rect(0, 0, TexSize, TexSize), new Vector2(0.5f, 0.5f));
        }

        void BuildHud()
        {
            _canvas = GetComponentInParent<Canvas>();
            if (_canvas == null)
            {
                var ui = FindAnyObjectByType<UIManager>();
                if (ui != null) _canvas = ui.GetComponentInParent<Canvas>();
            }
            if (_canvas == null) _canvas = FindAnyObjectByType<Canvas>();
            if (_canvas == null) return;

            var rootGo = new GameObject("WeaponIndicator", typeof(RectTransform));
            rootGo.transform.SetParent(_canvas.transform, false);
            // Sit below full-screen overlays (VignetteFlash, HitMissCallout, ExperimentOverlay)
            rootGo.transform.SetAsFirstSibling();

            _root = rootGo.GetComponent<RectTransform>();
            _root.anchorMin        = new Vector2(1f, 1f);
            _root.anchorMax        = new Vector2(1f, 1f);
            _root.pivot            = new Vector2(1f, 1f);
            _root.anchoredPosition = new Vector2(-margin.x, -margin.y);
            _root.sizeDelta        = new Vector2(300f, height);

            // Backing plate
            _plate = rootGo.AddComponent<Image>();
            _plate.color         = plateColor;
            _plate.raycastTarget = false;

            // Coloured vertical bar along the right edge
            var barGo = new GameObject("AccentBar", typeof(RectTransform));
            barGo.transform.SetParent(_root, false);
            var barRt = barGo.GetComponent<RectTransform>();
            barRt.anchorMin        = new Vector2(1f, 0f);
            barRt.anchorMax        = new Vector2(1f, 1f);
            barRt.pivot            = new Vector2(1f, 0.5f);
            barRt.anchoredPosition = Vector2.zero;
            barRt.sizeDelta        = new Vector2(4f, 0f);
            _accentBar = barGo.AddComponent<Image>();
            _accentBar.raycastTarget = false;

            // Icon slot on the left of the chip
            var iconGo = new GameObject("IconSlot", typeof(RectTransform));
            iconGo.transform.SetParent(_root, false);
            var iconRt = iconGo.GetComponent<RectTransform>();
            iconRt.anchorMin        = new Vector2(0f, 0.5f);
            iconRt.anchorMax        = new Vector2(0f, 0.5f);
            iconRt.pivot            = new Vector2(0.5f, 0.5f);
            iconRt.anchoredPosition = new Vector2(height * 0.55f, 0f);
            iconRt.sizeDelta        = new Vector2(height * 0.80f, height * 0.80f);

            BuildLaserIcon(iconRt);
            BuildShockwaveIcon(iconRt);

            // Weapon name label to the right of the icon
            var labelGo = new GameObject("WeaponLabel", typeof(RectTransform));
            labelGo.transform.SetParent(_root, false);
            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.offsetMin = new Vector2(height * 1.02f, 0f);
            labelRt.offsetMax = new Vector2(-16f, 0f);

            _label = labelGo.AddComponent<TextMeshProUGUI>();
            _label.fontSize         = fontSize;
            _label.fontStyle        = FontStyles.Bold;
            _label.characterSpacing = characterSpacing;
            _label.alignment        = TextAlignmentOptions.MidlineLeft;
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.overflowMode     = TextOverflowModes.Overflow;
            _label.raycastTarget    = false;
            HudFont.Apply(_label);
        }

        void AlignWithScoreRow()
        {
            if (_yAligned || _root == null || !matchScoreRowY) return;
            _yAligned = true;

            var ui = FindAnyObjectByType<UIManager>();
            RectTransform scoreRt = ui != null && ui.scoreText != null
                ? ui.scoreText.rectTransform
                : null;
            if (scoreRt == null) return;

            float topOffset = scoreRt.anchoredPosition.y - (1f - scoreRt.pivot.y) * scoreRt.sizeDelta.y;
            _root.anchoredPosition = new Vector2(-margin.x, topOffset);
        }

        void BuildLaserIcon(RectTransform parent)
        {
            var g = new GameObject("LaserIcon", typeof(RectTransform));
            g.transform.SetParent(parent, false);
            _laserGroup = g.GetComponent<RectTransform>();
            _laserGroup.anchorMin = Vector2.zero;
            _laserGroup.anchorMax = Vector2.one;
            _laserGroup.offsetMin = Vector2.zero;
            _laserGroup.offsetMax = Vector2.zero;

            _laserRail = MakePart(_laserGroup, "Rail", null,
                                  new Vector2(0f, 0f), new Vector2(2.4f, 30f));

            _laserEmitter = MakePart(_laserGroup, "Emitter", _discSprite,
                                     new Vector2(0f, 14f), new Vector2(9f, 9f));

            _laserBolt = MakePart(_laserGroup, "Bolt", _discSprite,
                                  new Vector2(0f, 0f), new Vector2(7f, 14f));

            _laserGround = MakePart(_laserGroup, "GroundDot", _discSprite,
                                    new Vector2(0f, -14f), new Vector2(11f, 6f));
        }

        void BuildShockwaveIcon(RectTransform parent)
        {
            var g = new GameObject("ShockwaveIcon", typeof(RectTransform));
            g.transform.SetParent(parent, false);
            _shockGroup = g.GetComponent<RectTransform>();
            _shockGroup.anchorMin = Vector2.zero;
            _shockGroup.anchorMax = Vector2.one;
            _shockGroup.offsetMin = Vector2.zero;
            _shockGroup.offsetMax = Vector2.zero;

            _shockOrigin = MakePart(_shockGroup, "Origin", _discSprite,
                                    new Vector2(0f, 11f), new Vector2(9f, 9f));

            _arcRects  = new RectTransform[ArcCount];
            _arcImages = new Image[ArcCount];
            for (int i = 0; i < ArcCount; i++)
            {
                _arcImages[i] = MakePart(_shockGroup, $"Arc_{i}", _arcSprite,
                                         new Vector2(0f, 11f), new Vector2(28f, 28f));
                _arcRects[i]  = _arcImages[i].rectTransform;
            }
        }

        static Image MakePart(RectTransform parent, string name, Sprite sprite,
                              Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta        = size;

            var img = go.AddComponent<Image>();
            img.sprite        = sprite;
            img.raycastTarget = false;
            return img;
        }

        // ── Mode switch & per-frame animation ────────────────────────────────

        void Update()
        {
            if (_root == null) return;
            if (!_yAligned) AlignWithScoreRow();
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();

            int  mode     = IsShockwave ? 1 : 0;
            bool practice = IsPractice;
            if (mode != _shownMode || (practice ? 1 : 0) != _shownPractice)
                ApplyMode(mode, practice);
        }

        void ApplyMode(int mode, bool practice)
        {
            _shownMode     = mode;
            _shownPractice = practice ? 1 : 0;
            bool sw        = mode == 1;
            Color accent   = sw ? shockwaveColor : laserColor;

            _laserGroup.gameObject.SetActive(!sw);
            _shockGroup.gameObject.SetActive(sw);

            // Drawn once, in one pose. Animating it every frame would rebuild the whole HUD canvas every frame, and put
            // a moving thing on screen that is not the stimulus.
            if (sw) AnimateShockwave(0.2f);
            else    AnimateLaser(0.45f);

            _accentBar.color = new Color(accent.r, accent.g, accent.b, 0.92f);

            bool showLabel = !labelDuringPracticeOnly || practice;
            _label.gameObject.SetActive(showLabel);
            if (showLabel)
            {
                _label.text  = sw ? shockwaveText : laserText;
                _label.color = Color.Lerp(accent, Color.white, 0.28f);

                _label.ForceMeshUpdate();
                float textW = _label.preferredWidth;
                _root.sizeDelta = new Vector2(height * 1.02f + textW + 24f, height);
            }
            else
            {
                _root.sizeDelta = new Vector2(height * 1.10f, height);
            }
        }

        void AnimateLaser(float t)
        {
            Color c = laserColor;
            float phase = Mathf.Repeat(t * laserBoltRate, 1f);
            float env   = Mathf.Sin(phase * Mathf.PI);

            _laserRail.color    = new Color(c.r, c.g, c.b, 0.42f);
            _laserEmitter.color = Color.Lerp(c, Color.white, 0.35f);

            float y = Mathf.Lerp(12f, -12f, phase);
            _laserBolt.rectTransform.anchoredPosition = new Vector2(0f, y);
            Color boltCol = Color.Lerp(c, Color.white, 0.55f);
            _laserBolt.color = new Color(boltCol.r, boltCol.g, boltCol.b, 0.30f + 0.70f * env);

            float groundHit = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(phase - 0.92f) * 5f), 2f);
            float gs = 1f + 0.35f * groundHit;
            _laserGround.rectTransform.localScale = new Vector3(gs, gs, 1f);
            _laserGround.color = new Color(c.r, c.g, c.b, 0.65f + 0.35f * groundHit);
        }

        void AnimateShockwave(float t)
        {
            Color c = shockwaveColor;
            float originPulse = 0.85f + 0.15f * Mathf.Sin(t * shockwaveWaveRate * Mathf.PI * 2f);
            _shockOrigin.color = Color.Lerp(c, Color.white, 0.40f * originPulse);

            for (int i = 0; i < ArcCount; i++)
            {
                float phase = Mathf.Repeat(t * shockwaveWaveRate + (float)i / ArcCount, 1f);
                float size  = Mathf.Lerp(10f, 44f, phase);
                float alpha = Mathf.Sin(phase * Mathf.PI) * (1f - phase * 0.25f);

                _arcRects[i].sizeDelta = new Vector2(size, size);
                Color ac = Color.Lerp(Color.white, c, Mathf.SmoothStep(0f, 0.6f, phase));
                _arcImages[i].color = new Color(ac.r, ac.g, ac.b, alpha * 0.92f);
            }
        }

        // ── Procedural icon textures ─────────────────────────────────────────

        static Texture2D MakeDiscTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode   = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[size * size];
            float inv = 2f / (size - 1);
            for (int y = 0; y < size; y++)
            {
                float dy = y * inv - 1f;
                for (int x = 0; x < size; x++)
                {
                    float dx = x * inv - 1f;
                    float r  = Mathf.Sqrt(dx * dx + dy * dy);
                    float a  = Mathf.Pow(Mathf.Clamp01(1f - r), 1.6f);
                    byte  b  = (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255);
                    px[y * size + x] = new Color32(255, 255, 255, b);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        static Texture2D MakeDownwardArcTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode   = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[size * size];
            float inv     = 2f / (size - 1);
            float halfArc = 68f * Mathf.Deg2Rad;
            for (int y = 0; y < size; y++)
            {
                float dy = y * inv - 1f;
                for (int x = 0; x < size; x++)
                {
                    float dx = x * inv - 1f;
                    float r  = Mathf.Sqrt(dx * dx + dy * dy);
                    float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 0.72f) / 0.13f);
                    ring *= ring;

                    float angle = Mathf.Atan2(dx, -dy);
                    float wedge = dy <= 0.1f
                        ? Mathf.Clamp01(1f - Mathf.Max(0f, Mathf.Abs(angle) - halfArc * 0.75f) / (halfArc * 0.30f))
                        : 0f;

                    float a = ring * wedge;
                    byte  b = (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255);
                    px[y * size + x] = new Color32(255, 255, 255, b);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
