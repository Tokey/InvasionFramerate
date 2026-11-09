using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JndUfo
{
    /// <summary>
    /// The round's answer, shown after every response:
    /// - On spatial laser blocks (<see cref="Show"/>): the two frame rates split left and right at
    ///   the hidden tower's beam, each in its side's colour — cool for the faster side, warm for
    ///   the slower — while each side of the screen slowly takes on its colour.
    /// - On temporal shockwave blocks (<see cref="ShowShockwave"/>): <b>no left/right split</b>.
    ///   Instead, one full-sky wash in the faster or slower colour and a readout in the upper sky of the
    ///   round's last change: the rate it WAS and the rate it is NOW.
    ///
    /// Under every rate, two small lines: the rate actually delivered ("measured 487" — frames over
    /// frame time as the round counted them; "not visited" for a side the UFO never entered), and
    /// under that the cap's frame time ("2.00 ms"), the unit the study steps in, in a paler tint so
    /// the two read apart.
    /// </summary>
    public class FpsRevealOverlay : MonoBehaviour
    {
        [Header("Colour")]
        [Tooltip("The side that ran at the HIGHER frame rate.")]
        public Color fasterColor = new Color(0.30f, 0.86f, 1.00f, 1f);
        [Tooltip("The side that ran at the LOWER frame rate. Blue against orange stays distinct for " +
                 "the common forms of colour blindness.")]
        public Color slowerColor = new Color(1.00f, 0.58f, 0.22f, 1f);
        [Tooltip("Opacity the tint deepens to at its strongest, in the band just beyond the hit-zone " +
                 "lines. It starts fully transparent.")]
        [Range(0f, 1f)] public float washAlpha = 0.15f;
        [Tooltip("The tint inside the hit zone, as a fraction of its strongest: enough to show which " +
                 "side is which, faint enough that the zone stays a clear window.")]
        [Range(0f, 1f)] public float washInsideZone = 0.3f;
        [Tooltip("Opacity of the soft shadow behind each number, for contrast over a bright sky.")]
        [Range(0f, 1f)] public float shadowAlpha = 0.3f;
        [Tooltip("How far the 'measured N' line — the frame rate actually delivered — is mixed from its side's " +
                 "colour toward white. Less than the frame-time line under it, so the two small lines read apart.")]
        [Range(0f, 1f)] public float measuredWhiteMix = 0.3f;
        [Tooltip("How far the frame-time line (the cap's frame time, e.g. '2.00 ms') under the measured rate is " +
                 "mixed from its side's colour toward white: paler than the measured line above it.")]
        [Range(0f, 1f)] public float frameTimeWhiteMix = 0.55f;
        [Tooltip("Opacity of the frame-time line once it has faded in.")]
        [Range(0f, 1f)] public float frameTimeAlpha = 0.9f;

        [Header("Type  (1920×1080 reference units)")]
        [Min(20f)] public float numberSize   = 128f;
        [Min(8f)]  public float unitSize     = 30f;
        [Min(8f)]  public float measuredSize = 30f;
        [Tooltip("The frame-time line under the measured rate.")]
        [Min(8f)]  public float frameTimeSize = 30f;
        [Tooltip("Height of the numerals' centre as a fraction of the screen, from the bottom: high in " +
                 "the sky, clear of the city, the centre callout and the practice hint.")]
        [Range(0.5f, 0.9f)] public float numberHeight = 0.78f;

        [Header("Layout")]
        [Tooltip("Gap between a number and the tower line at full size; it shrinks with the number.")]
        [Min(0f)] public float beamGap    = 26f;
        [Tooltip("Closest a number comes to a hit-zone line.")]
        [Min(0f)] public float zoneGap    = 10f;
        [Tooltip("Closest a number comes to the screen edge.")]
        [Min(0f)] public float edgeMargin = 14f;
        [Tooltip("When the screen edge squeezes one side's number, the other shrinks with it so the " +
                 "two stay the same size — but never below this scale. Past it, the squeezed side " +
                 "alone goes smaller.")]
        [Range(0.2f, 1f)] public float pairFloor = 0.6f;
        [Tooltip("Smallest scale a number ever takes. Only a tower right against the screen edge, " +
                 "closer than the game ever places it, needs less; there it is clipped at the edge.")]
        [Range(0.1f, 1f)] public float minScale = 0.3f;

        [Header("Motion")]
        [Tooltip("Seconds for a number to slide out from behind the beam, overshoot and settle.")]
        [Min(0.05f)] public float popSec  = 0.32f;
        [Tooltip("Seconds to fade everything out when the reveal ends.")]
        [Min(0.05f)] public float hideSec = 0.25f;
        [Tooltip("How long the reveal stays up when the caller doesn't say. The roll, the measured " +
                 "rates and the tint are paced to fit it.")]
        [Min(0.3f)] public float defaultHoldSec = 2f;

        const float WashSpanZones   = 8f;
        const float WashTail        = 0.3f;
        const int   WashTexW        = 512;
        const int   WashTexH        = 128;
        const float SlideIn         = 28f;
        const float MeasuredFadeSec = 0.25f;
        const float FrameTimeLagSec = 0.06f;   // the frame-time line lands just after the measured one
        const float InsideMinScale  = 0.75f;

        // ── Construction ─────────────────────────────────────────────────────

        /// <summary>Everything one side of the screen is made of (spatial laser blocks).</summary>
        sealed class Half
        {
            public float           side;     // −1: left of the tower, +1: right of it
            public RectTransform   area;     // this side of the tower line, clipped by its own mask
            public RectMask2D      mask;
            public RawImage        wash;
            public RectTransform   block;    // the numbers' anchor point: beside the beam, high up
            public RectTransform   shadowRect;
            public RawImage        shadow;
            public TextMeshProUGUI number, unit, measured, frameTime;

            public int   targetFps, startFps;
            public bool  rolls;              // the altered side rolls away from the base
            public Color color;
            public float naturalWidth;       // the block's widest line at scale 1
            public float scale = 1f;
            public float gap;                // the block's inner edge, out from the tower line
            public int   shownFps = int.MinValue;
        }

        Canvas        _canvas;
        CanvasScaler  _scaler;
        CanvasGroup   _group;
        RectTransform _root;
        Half          _left, _right;
        Texture2D     _washTex, _shadowTex, _swSkyWashTex;
        float         _washTexInside = -1f;
        readonly float[] _numberWidth = new float[5];
        char          _widestDigit;

        // Shockwave unified temporal reveal elements (centred in upper sky, floating typography like laser mode)
        bool            _shockwaveMode;
        RectTransform   _swRoot;
        RawImage        _swSkyWash;
        RectTransform   _swBlock;
        RectTransform   _swWasBlock;
        RectTransform   _swWasShadowRect;
        RawImage        _swWasShadow;
        TextMeshProUGUI _swWasSub;
        TextMeshProUGUI _swWasNumber;
        TextMeshProUGUI _swWasUnit;
        TextMeshProUGUI _swWasMeasured;
        TextMeshProUGUI _swWasFrameTime;
        TextMeshProUGUI _swArrow;
        RectTransform   _swNowBlock;
        RectTransform   _swNowShadowRect;
        RawImage        _swNowShadow;
        TextMeshProUGUI _swNowSub;
        TextMeshProUGUI _swNowNumber;
        TextMeshProUGUI _swNowUnit;
        TextMeshProUGUI _swNowMeasured;
        TextMeshProUGUI _swNowFrameTime;
        int             _swWasFps;
        int             _swNowFps;
        Color           _swWasColor;
        Color           _swNowColor;
        int             _swShownNowFps = int.MinValue;
        bool            _swRolls;

        // Vertical rhythm inside a block, from the type sizes (see Build).
        float _unitY, _measuredY, _frameTimeY, _blockMidY;

        enum State { Hidden, Showing, Hiding }
        State _state = State.Hidden;
        float _shownAt;     // realtime the reveal (after its delay) begins
        float _hideAt;

        // The reveal's pacing, in seconds after it begins, fitted to how long it stays up (Pace).
        float _rollStart, _rollSec, _measuredAt, _washStart, _washEnd;

        // Tracking (Show's track): the boundary in the world, and where it was last laid out on screen.
        bool    _track;
        Vector3 _towerBase;
        float   _hitRadius, _split, _zone;

        void Awake() => Build();

        void OnDestroy()
        {
            if (_washTex      != null) Destroy(_washTex);
            if (_shadowTex    != null) Destroy(_shadowTex);
            if (_swSkyWashTex != null) Destroy(_swSkyWashTex);
        }

        // ── API ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Spatial reveal for laser blocks (<c>manual</c>, <c>scroll</c>, <c>auto</c>): splits the
        /// screen left and right at <paramref name="towerBase"/>.
        /// </summary>
        public void Show(PerturbationController.RoundRecord round, Vector3 towerBase, float hitRadius,
                         float delaySec = 0f, float holdSec = 0f, bool track = false, float settleShiftX = 0f)
        {
            if (_root == null) Build();
            if (_root == null || !round.valid) return;

            _shockwaveMode = false;
            _track         = track;
            _towerBase     = towerBase;
            _hitRadius     = hitRadius;
            Project(towerBase, hitRadius, out float split, out float zone);
            _split = split;
            _zone  = zone;

            _root.gameObject.SetActive(true);
            _left.area.gameObject.SetActive(true);
            _right.area.gameObject.SetActive(true);
            if (_swRoot != null) _swRoot.gameObject.SetActive(false);

            RefreshWashTexture();

            bool leftFaster = round.LeftFps >= round.RightFps;
            Configure(_left,  round.LeftFps,  round.MeasuredLeftFps,
                      round.alteredSide == PerturbationController.Side.Left,  round.baseFps,
                      leftFaster ? fasterColor : slowerColor);
            Configure(_right, round.RightFps, round.MeasuredRightFps,
                      round.alteredSide == PerturbationController.Side.Right, round.baseFps,
                      leftFaster ? slowerColor : fasterColor);

            Place(split, zone);
            if (track && settleShiftX != 0f)
            {
                Project(towerBase - Vector3.right * settleShiftX, hitRadius, out float settled, out float settledZone);
                Fit(settled, settledZone);
            }
            else Fit(split, zone);
            Pace(holdSec);

            _shownAt = Time.realtimeSinceStartup + Mathf.Max(0f, delaySec);
            _state   = State.Showing;
            _group.alpha = 1f;
            Animate(-1f);
        }

        /// <summary>
        /// Temporal reveal for shockwave blocks (<c>shockwave</c>): does NOT split the screen into
        /// left and right sides. Instead, shows a clean, floating side-by-side transition readout
        /// in the upper sky matching the exact typography and minimalist elegance of laser mode.
        /// </summary>
        public void ShowShockwave(PerturbationController.RoundRecord round, float delaySec = 0f, float holdSec = 0f)
        {
            if (_root == null) Build();
            if (_root == null || !round.valid) return;

            _shockwaveMode = true;
            _track         = false;

            _root.gameObject.SetActive(true);
            _left.area.gameObject.SetActive(false);
            _right.area.gameObject.SetActive(false);
            _swRoot.gameObject.SetActive(true);

            // The last change the participant met: what it was and what it became. Before any, just the base.
            bool changed = round.changeToFps > 0 && round.changeFromFps > 0;
            int  wasFps  = changed ? round.changeFromFps : round.baseFps;
            int  nowFps  = changed ? round.changeToFps   : round.baseFps;
            _swRolls = wasFps != nowFps;

            _swWasFps   = wasFps;
            _swNowFps   = nowFps;
            _swWasColor = wasFps >= nowFps ? fasterColor : slowerColor;
            _swNowColor = nowFps >= wasFps ? fasterColor : slowerColor;
            _swShownNowFps = int.MinValue;

            _swWasSub.SetText("WAS");
            _swWasNumber.SetText("{0}", wasFps);
            _swWasUnit.SetText("FPS");
            _swWasMeasured.SetText(MeasuredLine(MeasuredPhaseFps(round, wasFps), "not measured"));
            _swWasFrameTime.SetText(FrameTimeLine(wasFps));

            _swArrow.SetText("→");

            _swNowSub.SetText("NOW");
            _swNowNumber.SetText("{0}", _swRolls ? wasFps : nowFps);
            _swNowUnit.SetText("FPS");
            _swNowMeasured.SetText(MeasuredLine(MeasuredPhaseFps(round, nowFps), "not measured"));
            _swNowFrameTime.SetText(FrameTimeLine(nowFps));

            Pace(holdSec);

            _shownAt = Time.realtimeSinceStartup + Mathf.Max(0f, delaySec);
            _state   = State.Showing;
            _group.alpha = 1f;
            Animate(-1f);
        }

        /// <summary>Fades the reveal out. Safe to call when it is not showing.</summary>
        public void Hide()
        {
            if (_state != State.Showing) return;
            _state  = State.Hiding;
            _hideAt = Time.realtimeSinceStartup;
        }

        // ── Editor preview hooks (Tools ▸ JND ▸ Render FPS Reveal Previews) ───

        public void PreviewOnCamera(Camera cam)
        {
            if (_root == null) Build();
            _canvas.renderMode    = RenderMode.ScreenSpaceCamera;
            _canvas.worldCamera   = cam;
            _canvas.planeDistance = 5f;
            RefreshScaler();
        }

        public void RefreshScaler()
        {
            if (_scaler == null) return;
            _scaler.enabled = false;
            _scaler.enabled = true;
            Canvas.ForceUpdateCanvases();
        }

        public void PreviewPose(float t) => Animate(t);

        public void PreviewHidePose(float h)
        {
            Animate(10f);
            _group.alpha = 1f - EaseInQuad(Mathf.Clamp01(h));
            if (_shockwaveMode)
            {
                DriftShockwave(h);
            }
            else
            {
                Drift(_left,  h);
                Drift(_right, h);
            }
        }

        void Project(Vector3 towerBase, float hitRadius, out float split, out float zone)
        {
            split = 0.5f;
            zone  = 0.12f;
            Camera cam = _canvas.renderMode == RenderMode.ScreenSpaceCamera && _canvas.worldCamera != null
                ? _canvas.worldCamera
                : CameraRig.Instance != null ? CameraRig.Instance.ActiveCamera : Camera.main;
            if (cam == null) return;

            Vector3 vp = cam.WorldToViewportPoint(towerBase);
            Vector3 ve = cam.WorldToViewportPoint(towerBase + Vector3.right * hitRadius);
            if (vp.z > 0f)
            {
                split = Mathf.Clamp(vp.x, 0.02f, 0.98f);
                zone  = Mathf.Clamp(Mathf.Abs(ve.x - vp.x), 0.01f, 0.45f);
            }
        }

        // ── Per frame ────────────────────────────────────────────────────────

        void LateUpdate()
        {
            if (_state == State.Hidden) return;

            if (!_shockwaveMode && _track)
            {
                Project(_towerBase, _hitRadius, out float split, out float zone);
                if (!Mathf.Approximately(split, _split) || !Mathf.Approximately(zone, _zone))
                {
                    _split = split;
                    _zone  = zone;
                    Place(split, zone);
                }
            }

            float now = Time.realtimeSinceStartup;

            if (_state == State.Hiding)
            {
                float h = Mathf.Clamp01((now - _hideAt) / hideSec);
                _group.alpha = 1f - EaseInQuad(h);
                if (_shockwaveMode)
                {
                    DriftShockwave(h);
                }
                else
                {
                    Drift(_left,  h);
                    Drift(_right, h);
                }
                if (h >= 1f)
                {
                    _state = State.Hidden;
                    _root.gameObject.SetActive(false);
                }
                return;
            }

            Animate(now - _shownAt);
        }

        void Animate(float t)
        {
            if (_shockwaveMode)
            {
                AnimateShockwave(t);
                return;
            }
            AnimateHalf(_left,  t);
            AnimateHalf(_right, t);
        }

        void AnimateShockwave(float t)
        {
            // 1. Subtle sky glow wash (matches laser mode washAlpha)
            float w    = Mathf.Clamp01((t - _washStart) / (_washEnd - _washStart));
            float glow = w * w * (3f - 2f * w);
            _swSkyWash.color = WithAlpha(_swNowColor, washAlpha * glow);

            // Pop-in scale and punch (matches laser mode AnimateHalf)
            float tp   = t - 0.04f;
            float pop  = Mathf.Clamp01(tp / popSec);
            float grow = Mathf.LerpUnclamped(0.6f, 1f, EaseOutBack(pop));
            float fade = Mathf.Clamp01(tp / (popSec * 0.45f));

            float tr   = t - _rollStart;
            float roll = EaseOutCubic(Mathf.Clamp01(tr / _rollSec));
            int   fps  = _swRolls ? Mathf.RoundToInt(Mathf.Lerp(_swWasFps, _swNowFps, roll)) : _swNowFps;
            if (fps != _swShownNowFps)
            {
                _swShownNowFps = fps;
                _swNowNumber.SetText("{0}", fps);
            }

            float punch = 0f;
            if (_swRolls && tr >= _rollSec)
                punch = Mathf.Sin(Mathf.Clamp01((tr - _rollSec) / 0.2f) * Mathf.PI) * 0.06f;

            _swBlock.localScale       = Vector3.one * (grow + punch * 0.5f);
            _swBlock.anchoredPosition = new Vector2(0f, 0f);

            // WAS block colors & fade
            Color wasCol = Color.Lerp(_swWasColor, Color.white, 0.18f);
            _swWasSub.color       = new Color(0.85f, 0.88f, 0.95f, 0.65f * fade);
            _swWasNumber.color    = WithAlpha(wasCol, fade);
            _swWasUnit.color      = WithAlpha(wasCol, 0.85f * fade);
            _swWasShadow.color    = new Color(0f, 0f, 0f, shadowAlpha * fade);

            // Arrow
            Color arrowCol = Color.Lerp(Color.white, _swNowColor, 0.35f);
            _swArrow.color = WithAlpha(arrowCol, 0.55f * fade);

            // NOW block colors & fade
            Color nowCol = Color.Lerp(_swNowColor, Color.white, 0.18f);
            _swNowSub.color       = new Color(0.95f, 0.98f, 1f, 0.85f * fade);
            _swNowNumber.color    = WithAlpha(nowCol, fade);
            _swNowUnit.color      = WithAlpha(nowCol, 0.85f * fade);
            _swNowShadow.color    = new Color(0f, 0f, 0f, shadowAlpha * fade);

            // Measured rates, then frame times, fade in as the numbers settle
            float m = EaseOutCubic(Mathf.Clamp01((t - _measuredAt) / MeasuredFadeSec));
            _swWasMeasured.color = WithAlpha(Color.Lerp(_swWasColor, Color.white, measuredWhiteMix), 0.95f * m);
            _swNowMeasured.color = WithAlpha(Color.Lerp(_swNowColor, Color.white, measuredWhiteMix), 0.95f * m);
            _swWasMeasured.rectTransform.anchoredPosition = new Vector2(0f, _measuredY - 8f * (1f - m));
            _swNowMeasured.rectTransform.anchoredPosition = new Vector2(0f, _measuredY - 8f * (1f - m));

            float f = EaseOutCubic(Mathf.Clamp01((t - _measuredAt - FrameTimeLagSec) / MeasuredFadeSec));
            _swWasFrameTime.color = WithAlpha(Color.Lerp(_swWasColor, Color.white, frameTimeWhiteMix), frameTimeAlpha * f);
            _swNowFrameTime.color = WithAlpha(Color.Lerp(_swNowColor, Color.white, frameTimeWhiteMix), frameTimeAlpha * f);
            _swWasFrameTime.rectTransform.anchoredPosition = new Vector2(0f, _frameTimeY - 8f * (1f - f));
            _swNowFrameTime.rectTransform.anchoredPosition = new Vector2(0f, _frameTimeY - 8f * (1f - f));
        }

        void DriftShockwave(float h)
        {
            float e = EaseInQuad(h);
            _swBlock.localScale       = Vector3.one * (1f + 0.05f * e);
            _swBlock.anchoredPosition = new Vector2(0f, 18f * e);
        }

        void AnimateHalf(Half s, float t)
        {
            float w    = Mathf.Clamp01((t - _washStart) / (_washEnd - _washStart));
            float glow = w * w * (3f - 2f * w);
            s.wash.color = WithAlpha(s.color, washAlpha * glow);

            float tp   = t - 0.04f;
            float pop  = Mathf.Clamp01(tp / popSec);
            float grow = Mathf.LerpUnclamped(0.6f, 1f, EaseOutBack(pop));
            float fade = Mathf.Clamp01(tp / (popSec * 0.45f));

            float tr   = t - _rollStart;
            float roll = EaseOutCubic(Mathf.Clamp01(tr / _rollSec));
            int   fps  = s.rolls ? Mathf.RoundToInt(Mathf.Lerp(s.startFps, s.targetFps, roll)) : s.targetFps;
            if (fps != s.shownFps)
            {
                s.shownFps = fps;
                s.number.SetText("{0}", fps);
            }

            float punch = 0f;
            if (s.rolls && tr >= _rollSec)
                punch = Mathf.Sin(Mathf.Clamp01((tr - _rollSec) / 0.2f) * Mathf.PI) * 0.06f;

            s.block.localScale       = Vector3.one * (s.scale * (grow + punch));
            s.block.anchoredPosition = new Vector2(
                s.side * (s.gap - SlideIn * s.scale * (1f - EaseOutCubic(pop))), 0f);

            Color c = Color.Lerp(s.color, Color.white, 0.18f);
            s.number.color = WithAlpha(c, fade);
            s.unit.color   = WithAlpha(c, 0.85f * fade);
            s.shadow.color = new Color(0f, 0f, 0f, shadowAlpha * fade);

            // Measured: quieter than the number, and only once the numbers have landed; the frame time just after.
            float m = EaseOutCubic(Mathf.Clamp01((t - _measuredAt) / MeasuredFadeSec));
            s.measured.color = WithAlpha(Color.Lerp(s.color, Color.white, measuredWhiteMix), 0.95f * m);
            s.measured.rectTransform.anchoredPosition = new Vector2(0f, _measuredY - 8f * (1f - m));

            float f = EaseOutCubic(Mathf.Clamp01((t - _measuredAt - FrameTimeLagSec) / MeasuredFadeSec));
            s.frameTime.color = WithAlpha(Color.Lerp(s.color, Color.white, frameTimeWhiteMix), frameTimeAlpha * f);
            s.frameTime.rectTransform.anchoredPosition = new Vector2(0f, _frameTimeY - 8f * (1f - f));
        }

        void Drift(Half s, float h)
        {
            float e = EaseInQuad(h);
            s.block.localScale       = Vector3.one * (s.scale * (1f + 0.05f * e));
            s.block.anchoredPosition = new Vector2(s.side * s.gap, 18f * e);
        }

        // ── Layout ───────────────────────────────────────────────────────────

        // The first small line under a frame rate: the rate actually delivered, frames over frame time as the round
        // counted them. `none` when nothing was counted (a side the UFO never entered).
        static string MeasuredLine(float measured, string none) =>
            float.IsNaN(measured) || float.IsInfinity(measured) || measured <= 0f
                ? none
                : "measured " + Mathf.RoundToInt(measured).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // The second: the cap's frame time, the unit the study steps in.
        static string FrameTimeLine(int fps) =>
            (1000f / Mathf.Max(1, fps)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " ms";

        // What a shockwave phase really ran at. The whole screen runs one cap at a time, so a phase's delivered rate is
        // the round's tally for that cap: base or altered. A rate from an earlier attempt of the round (before a TOO LATE
        // re-gate started the tally afresh) has no tally left: NaN.
        static float MeasuredPhaseFps(PerturbationController.RoundRecord round, int fps)
        {
            if (fps == round.baseFps)    return round.MeasuredBaseFps;
            if (fps == round.alteredFps) return round.MeasuredAlteredFps;
            return float.NaN;
        }

        // The soft shadow's height: tall enough for the number and every line under it.
        float ShadowHeight => 1.9f * numberSize + (_measuredY - _frameTimeY);

        void Configure(Half s, int fps, float measured, bool isAltered, int baseFps, Color color)
        {
            s.targetFps = fps;
            s.startFps  = isAltered ? baseFps : fps;
            s.rolls     = isAltered && baseFps != fps;
            s.color     = color;
            s.shownFps  = int.MinValue;

            string m  = MeasuredLine(measured, "not visited");
            string ft = FrameTimeLine(fps);
            s.unit.SetText("FPS");
            s.measured.SetText(m);
            s.frameTime.SetText(ft);

            float numW  = NumberWidth(s.number, DigitCount(Mathf.Max(s.startFps, s.targetFps)));
            float unitW = s.unit.GetPreferredValues("FPS").x;
            float measW = s.measured.GetPreferredValues(m).x;
            float ftW   = s.frameTime.GetPreferredValues(ft).x;
            s.naturalWidth = Mathf.Max(Mathf.Max(numW, unitW), Mathf.Max(measW, ftW));

            s.shadowRect.sizeDelta        = new Vector2(s.naturalWidth + 0.9f * numberSize, ShadowHeight);
            s.shadowRect.anchoredPosition = new Vector2(s.side * s.naturalWidth * 0.5f, _blockMidY);
        }

        void Place(float split, float zoneHalf)
        {
            float W = CanvasWidth;
            float z = zoneHalf * W;

            SetArea(_left,  0f,    split);
            SetArea(_right, split, 1f);
            SetWash(_left,  split * W, z);
            SetWash(_right, W - split * W, z);
        }

        void Fit(float split, float zoneHalf)
        {
            float W  = CanvasWidth;
            float xs = split * W;
            float z  = zoneHalf * W;
            float wl = xs, wr = W - xs;

            float inL  = Mathf.Min(z - zoneGap, wl - edgeMargin) / Mathf.Max(1f, _left.naturalWidth  + beamGap);
            float inR  = Mathf.Min(z - zoneGap, wr - edgeMargin) / Mathf.Max(1f, _right.naturalWidth + beamGap);
            float outL = (wl - z - zoneGap - edgeMargin) / Mathf.Max(1f, _left.naturalWidth);
            float outR = (wr - z - zoneGap - edgeMargin) / Mathf.Max(1f, _right.naturalWidth);

            float kIn   = Mathf.Min(inL,  inR);
            float kOut  = Mathf.Min(outL, outR);
            bool inside = kIn >= InsideMinScale || kIn >= kOut;

            float fitL  = inside ? inL : outL;
            float fitR  = inside ? inR : outR;
            float pair  = Mathf.Min(1f, Mathf.Min(fitL, fitR));
            float floor = Mathf.Max(pair, pairFloor);
            _left.scale  = Mathf.Clamp(Mathf.Min(fitL, floor), minScale, 1f);
            _right.scale = Mathf.Clamp(Mathf.Min(fitR, floor), minScale, 1f);

            _left.gap  = inside ? beamGap * _left.scale  : z + zoneGap;
            _right.gap = inside ? beamGap * _right.scale : z + zoneGap;
        }

        void Pace(float holdSec)
        {
            float hold  = holdSec > 0f ? holdSec : defaultHoldSec;
            _rollStart  = 0.1f;
            _rollSec    = Mathf.Clamp(0.4f * hold, 0.3f, 0.7f);
            _measuredAt = _rollStart + _rollSec - 0.1f;
            _washStart  = 0.08f;
            _washEnd    = Mathf.Clamp(0.9f * hold, _washStart + 0.4f, 2.2f);
        }

        void SetArea(Half s, float xMin, float xMax)
        {
            s.area.anchorMin = new Vector2(xMin, 0f);
            s.area.anchorMax = new Vector2(xMax, 1f);
            s.area.offsetMin = Vector2.zero;
            s.area.offsetMax = Vector2.zero;
        }

        void SetWash(Half s, float width, float zoneHalf)
        {
            s.wash.enabled = width > 2f;
            if (!s.wash.enabled) return;

            float u = width / Mathf.Max(1f, WashSpanZones * zoneHalf);
            s.wash.uvRect = s.side < 0f ? new Rect(u, 0f, -u, 1f) : new Rect(0f, 0f, u, 1f);
        }

        float CanvasWidth
        {
            get
            {
                var rt = _canvas != null ? (RectTransform)_canvas.transform : null;
                float w = rt != null ? rt.rect.width : 0f;
                return w > 1f ? w : 1920f;
            }
        }

        float NumberWidth(TextMeshProUGUI t, int digits)
        {
            digits = Mathf.Clamp(digits, 1, _numberWidth.Length - 1);
            if (_numberWidth[digits] > 0f) return _numberWidth[digits];
            if (_widestDigit == '\0')
            {
                float best = -1f;
                for (char d = '0'; d <= '9'; d++)
                {
                    float w = t.GetPreferredValues(d.ToString()).x;
                    if (w > best) { best = w; _widestDigit = d; }
                }
            }
            return _numberWidth[digits] = t.GetPreferredValues(new string(_widestDigit, digits)).x;
        }

        static int DigitCount(int n)
        {
            n = Mathf.Abs(n);
            int digits = 1;
            while (n >= 10) { n /= 10; digits++; }
            return digits;
        }

        void Build()
        {
            if (_root != null) return;

            var canvasGo = new GameObject("FpsRevealCanvas");
            canvasGo.transform.SetParent(transform, false);

            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = -10;

            _scaler = canvasGo.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = new Vector2(1920f, 1080f);
            _scaler.matchWidthOrHeight  = 0.5f;

            var rootGo = new GameObject("Reveal", typeof(RectTransform));
            rootGo.transform.SetParent(canvasGo.transform, false);
            _root = (RectTransform)rootGo.transform;
            Stretch(_root);
            _group = rootGo.AddComponent<CanvasGroup>();
            _group.interactable   = false;
            _group.blocksRaycasts = false;

            float numHalf  = 0.35f * numberSize;
            float unitHalf = 0.35f * unitSize;
            float measHalf = 0.35f * measuredSize;
            float ftHalf   = 0.35f * frameTimeSize;
            _unitY      = -(numHalf + 0.13f * numberSize + unitHalf);
            _measuredY  = _unitY - (unitHalf + 0.4f * unitSize + measHalf);
            // The two small lines sit a little closer to each other than to the unit: one pair of details.
            _frameTimeY = _measuredY - (measHalf + 0.3f * measuredSize + ftHalf);
            _blockMidY  = (numHalf + _frameTimeY - ftHalf) * 0.5f;

            _shadowTex    = BuildShadowTexture(128);
            _swSkyWashTex = BuildShockwaveSkyWashTexture(256, 128);

            _left  = BuildHalf("Left",  -1f);
            _right = BuildHalf("Right", +1f);
            BuildShockwaveReveal();
            RefreshWashTexture();

            rootGo.SetActive(false);
        }

        void BuildShockwaveReveal()
        {
            _swRoot = NewRect("ShockwaveReveal", _root);
            Stretch(_swRoot);

            var washRect = NewRect("SkyWash", _swRoot);
            Stretch(washRect);
            _swSkyWash = washRect.gameObject.AddComponent<RawImage>();
            _swSkyWash.texture       = _swSkyWashTex;
            _swSkyWash.raycastTarget = false;

            _swBlock = NewRect("CenterBlock", _swRoot);
            _swBlock.anchorMin = _swBlock.anchorMax = new Vector2(0.5f, numberHeight);
            _swBlock.pivot     = new Vector2(0.5f, 0.5f);
            _swBlock.sizeDelta = Vector2.zero;

            float numHalf  = 0.35f * numberSize;
            float subY     = numHalf + 22f;
            float colSpread = 240f;

            // ── Left (WAS) Block ─────────────────────────────────────────────
            _swWasBlock = NewRect("WasBlock", _swBlock);
            _swWasBlock.anchorMin = _swWasBlock.anchorMax = new Vector2(0.5f, 0.5f);
            _swWasBlock.pivot     = new Vector2(0.5f, 0.5f);
            _swWasBlock.anchoredPosition = new Vector2(-colSpread, 0f);

            _swWasShadowRect = NewRect("Shadow", _swWasBlock);
            _swWasShadowRect.anchorMin = _swWasShadowRect.anchorMax = new Vector2(0.5f, 0.5f);
            _swWasShadowRect.pivot     = new Vector2(0.5f, 0.5f);
            _swWasShadowRect.sizeDelta = new Vector2(400f, 260f + (_measuredY - _frameTimeY));
            _swWasShadowRect.anchoredPosition = new Vector2(0f, _blockMidY);
            _swWasShadow = _swWasShadowRect.gameObject.AddComponent<RawImage>();
            _swWasShadow.texture       = _shadowTex;
            _swWasShadow.raycastTarget = false;

            _swWasSub = MakeSubText("Sub", _swWasBlock, 0f, subY, 20f, FontStyles.Bold, 6f);
            _swWasNumber = MakeSubText("Number", _swWasBlock, 0f, 0f, numberSize, FontStyles.Bold, 0f);
            _swWasUnit = MakeSubText("Unit", _swWasBlock, 0f, _unitY, unitSize, FontStyles.Bold, 14f);
            _swWasMeasured = MakeSubText("Measured", _swWasBlock, 0f, _measuredY, measuredSize, FontStyles.Normal, 2f);
            _swWasFrameTime = MakeSubText("FrameTime", _swWasBlock, 0f, _frameTimeY, frameTimeSize, FontStyles.Normal, 2f);

            _swWasNumber.outlineWidth = 0.12f;
            _swWasNumber.outlineColor = new Color32(0, 0, 0, 160);

            // ── Center Arrow ─────────────────────────────────────────────────
            _swArrow = MakeSubText("Arrow", _swBlock, 0f, 0f, 44f, FontStyles.Bold, 0f);

            // ── Right (NOW) Block ────────────────────────────────────────────
            _swNowBlock = NewRect("NowBlock", _swBlock);
            _swNowBlock.anchorMin = _swNowBlock.anchorMax = new Vector2(0.5f, 0.5f);
            _swNowBlock.pivot     = new Vector2(0.5f, 0.5f);
            _swNowBlock.anchoredPosition = new Vector2(colSpread, 0f);

            _swNowShadowRect = NewRect("Shadow", _swNowBlock);
            _swNowShadowRect.anchorMin = _swNowShadowRect.anchorMax = new Vector2(0.5f, 0.5f);
            _swNowShadowRect.pivot     = new Vector2(0.5f, 0.5f);
            _swNowShadowRect.sizeDelta = new Vector2(400f, 260f + (_measuredY - _frameTimeY));
            _swNowShadowRect.anchoredPosition = new Vector2(0f, _blockMidY);
            _swNowShadow = _swNowShadowRect.gameObject.AddComponent<RawImage>();
            _swNowShadow.texture       = _shadowTex;
            _swNowShadow.raycastTarget = false;

            _swNowSub = MakeSubText("Sub", _swNowBlock, 0f, subY, 20f, FontStyles.Bold, 6f);
            _swNowNumber = MakeSubText("Number", _swNowBlock, 0f, 0f, numberSize, FontStyles.Bold, 0f);
            _swNowUnit = MakeSubText("Unit", _swNowBlock, 0f, _unitY, unitSize, FontStyles.Bold, 14f);
            _swNowMeasured = MakeSubText("Measured", _swNowBlock, 0f, _measuredY, measuredSize, FontStyles.Normal, 2f);
            _swNowFrameTime = MakeSubText("FrameTime", _swNowBlock, 0f, _frameTimeY, frameTimeSize, FontStyles.Normal, 2f);

            _swNowNumber.outlineWidth = 0.12f;
            _swNowNumber.outlineColor = new Color32(0, 0, 0, 160);

            _swRoot.gameObject.SetActive(false);
        }

        static TextMeshProUGUI MakeSubText(string name, RectTransform parent, float x, float y, float size,
                                           FontStyles style, float spacing)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin        = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(500f, size * 1.35f);
            rt.anchoredPosition = new Vector2(x, y);

            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.fontSize         = size;
            t.fontStyle        = style;
            t.alignment        = TextAlignmentOptions.Center;
            t.characterSpacing = spacing;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode     = TextOverflowModes.Overflow;
            t.raycastTarget    = false;
            HudFont.Apply(t);
            return t;
        }

        static TextMeshProUGUI MakeCenterText(string name, RectTransform parent, float size,
                                              FontStyles style, float y, float spacing)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin        = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(1100f, size * 1.35f);
            rt.anchoredPosition = new Vector2(0f, y);

            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.fontSize         = size;
            t.fontStyle        = style;
            t.alignment        = TextAlignmentOptions.Center;
            t.characterSpacing = spacing;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode     = TextOverflowModes.Overflow;
            t.raycastTarget    = false;
            HudFont.Apply(t);
            return t;
        }

        Half BuildHalf(string name, float side)
        {
            var s = new Half { side = side };
            bool left = side < 0f;

            s.area = NewRect(name, _root);
            s.mask = s.area.gameObject.AddComponent<RectMask2D>();
            s.mask.softness = new Vector2Int(12, 0);

            var washRect = NewRect("Tint", s.area);
            Stretch(washRect);
            s.wash = washRect.gameObject.AddComponent<RawImage>();
            s.wash.raycastTarget = false;

            s.block = NewRect("Block", s.area);
            s.block.anchorMin = s.block.anchorMax = new Vector2(left ? 1f : 0f, numberHeight);
            s.block.pivot     = new Vector2(left ? 1f : 0f, 0.5f);
            s.block.sizeDelta = Vector2.zero;

            s.shadowRect = NewRect("Shadow", s.block);
            s.shadowRect.anchorMin = s.shadowRect.anchorMax = new Vector2(0.5f, 0.5f);
            s.shadowRect.pivot     = new Vector2(0.5f, 0.5f);
            s.shadow = s.shadowRect.gameObject.AddComponent<RawImage>();
            s.shadow.texture       = _shadowTex;
            s.shadow.raycastTarget = false;

            s.number   = MakeText("Number",   s.block, side, numberSize,   FontStyles.Bold,   0f,         0f);
            s.unit     = MakeText("Unit",     s.block, side, unitSize,     FontStyles.Bold,   _unitY,     14f);
            s.measured  = MakeText("Measured",  s.block, side, measuredSize,  FontStyles.Normal, _measuredY,  2f);
            s.frameTime = MakeText("FrameTime", s.block, side, frameTimeSize, FontStyles.Normal, _frameTimeY, 2f);

            s.number.outlineWidth = 0.12f;
            s.number.outlineColor = new Color32(0, 0, 0, 160);

            return s;
        }

        static TextMeshProUGUI MakeText(string name, RectTransform parent, float side, float size,
                                        FontStyles style, float y, float spacing)
        {
            bool left = side < 0f;
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot     = new Vector2(left ? 1f : 0f, 0.5f);
            rt.sizeDelta = new Vector2(900f, size * 1.3f);
            rt.anchoredPosition = new Vector2(0f, y);

            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.fontSize         = size;
            t.fontStyle        = style;
            t.alignment        = left ? TextAlignmentOptions.Right : TextAlignmentOptions.Left;
            t.characterSpacing = spacing;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode     = TextOverflowModes.Overflow;
            t.raycastTarget    = false;
            HudFont.Apply(t);
            return t;
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // ── Textures ─────────────────────────────────────────────────────────

        void RefreshWashTexture()
        {
            if (_washTex != null && Mathf.Approximately(_washTexInside, washInsideZone)) return;
            if (_washTex == null)
                _washTex = new Texture2D(WashTexW, WashTexH, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode   = TextureWrapMode.Clamp,
                };
            PaintWash(_washTex, washInsideZone);
            _washTexInside = washInsideZone;
            _left.wash.texture = _right.wash.texture = _washTex;
        }

        static void PaintWash(Texture2D tex, float inside)
        {
            int w = tex.width, h = tex.height;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float v   = y / (float)(h - 1);
                float sky = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, 0.5f, v));
                float top = 1f - 0.45f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.85f, 1f, v));
                for (int x = 0; x < w; x++)
                {
                    float r = x / (float)(w - 1) * WashSpanZones;
                    float level = r < 1.25f
                        ? Mathf.Lerp(inside, 1f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.8f, 1.25f, r)))
                        : Mathf.Lerp(1f, WashTail, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.25f, 5f, r)));
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(255f * level * sky * top));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
        }

        /// <summary>
        /// Unified horizontal sky glow for the shockwave reveal: symmetric across the entire screen
        /// width (strongest in a wide band across the upper sky, clear over the city below, never
        /// divided into left and right halves).
        /// </summary>
        static Texture2D BuildShockwaveSkyWashTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float v    = y / (float)(h - 1);
                float band = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.32f, 0.68f, v)) *
                             (1f - 0.55f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.86f, 1f, v)));
                for (int x = 0; x < w; x++)
                {
                    float u     = Mathf.Abs(2f * x / (w - 1) - 1f);
                    float horiz = Mathf.Lerp(1f, 0.35f, Mathf.SmoothStep(0.15f, 0.95f, u));
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(255f * band * horiz));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        static Texture2D BuildShadowTexture(int res)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };

            var px = new Color32[res * res];
            for (int y = 0; y < res; y++)
            for (int x = 0; x < res; x++)
            {
                float nx = 2f * x / (res - 1) - 1f;
                float ny = 2f * y / (res - 1) - 1f;
                float r  = Mathf.Sqrt(nx * nx + ny * ny);
                float a  = Mathf.Clamp01(1f - r);
                a = a * a * (3f - 2f * a);
                px[y * res + x] = new Color32(255, 255, 255, (byte)(255f * a));
            }

            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        // ── Easing ───────────────────────────────────────────────────────────

        static float EaseOutCubic(float t) { t = 1f - t; return 1f - t * t * t; }
        static float EaseInQuad(float t)   => t * t;

        static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
    }
}
