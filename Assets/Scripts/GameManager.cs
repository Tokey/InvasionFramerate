using System.Collections;
using UnityEngine;

namespace JndUfo
{
    /// <summary>
    /// Wires the subsystems together and orchestrates a round:
    /// GO! (split opens) → fly and shoot → split closes → score → log → reveal the tower and both
    /// sides' frame rates → reset under the veil → GO! again.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        [Header("References")]
        public LaserFirer laser;
        public TowerManager towerManager;
        public UfoController ufo;
        public ScoreManager scoreManager;
        public UIManager uiManager;
        public PerturbationController perturbation;
        [Tooltip("The round's watchdog (time limit, crossing cap). Auto-created on this object if empty.")]
        public RoundClock roundClock;
        [Tooltip("Shows each side's frame rate after a round. Auto-created on this object if empty.")]
        public FpsRevealOverlay fpsReveal;

        [Header("Reveal Timing")]
        [Tooltip("How long the fog takes to fade out after a shot (seconds).")]
        public float fogRetreatDuration = 0.4f;
        [Tooltip("How long the hit marker, the tower and the frame-rate reveal stay up. Pushed from " +
                 "StudyConfig.revealHoldSec every block.")]
        public float hitDisplayDuration = 2f;
        [Tooltip("How long the fog takes to fade back in before resetting.")]
        public float fogRestoreDuration = 0.4f;

        [Tooltip("Seconds GO! holds and shakes after the scene has reset, before firing is handed " +
                 "back. Pushed from StudyConfig.readyBeatSec every block.")]
        [Min(0f)] public float readyBeatDuration = 0.5f;

        [Tooltip("How fast the UFO blinks while GO! is up, in blinks per second. The UFO is hidden " +
                 "for the whole reset and reappears on the starting gun where the last round left " +
                 "it, blinking, so the eye is pulled back to it before play begins.")]
        [Min(1f)] public float ufoFlashHz = 8f;

        [Header("Camera Pan")]
        [Tooltip("Seconds the camera takes to slide to its new vantage point after a shot. " +
                 "Crossings are not counted during the pan.")]
        public float cameraPanDuration = 0.55f;

        [Tooltip("How far (world units, either side of the rig's starting X) the camera may pan " +
                 "between rounds. Relative to the origin, never to the tower — panning onto the tower " +
                 "would centre it on screen every round. 0 holds the camera still.")]
        [Min(0f)] public float cameraPanRange = 6f;

        [Header("World Scroll")]
        [Tooltip("Move the world instead of the UFO: the camera follows the ship across, so it holds the " +
                 "middle of the screen and the city slides past under it, without end. The city, the towers, " +
                 "the grass and the sky are built endless at start — one stretch of them repeating seamlessly — " +
                 "so set this before pressing Play. The mouse flies the ship exactly as before, and everything " +
                 "stays in world space: the ship's X never jumps, and the logs read as they do without the " +
                 "scroll. The boundary repeats too, once every worldScrollPeriod, and the frame rate swaps " +
                 "sides at each one (base | altered | base | altered ...), so wherever the ship goes it meets " +
                 "one. Each round's boundary is dealt as far from the ship, in mouse travel, as it would be " +
                 "without the scroll.\n\nWhole-screen motion shows the frame rate far more than a ship crossing " +
                 "a still scene: decide on this before collecting data and keep it the same for a whole study.")]
        public bool worldScroll = false;

        [Tooltip("World units from one boundary to the next with the world scroll on: the length of the stretch " +
                 "of towers and grass that repeats. The screen is about 47 units wide at the play plane, so 96 " +
                 "is about two screens — enough that the boundary dealt for a round is always the nearest one to " +
                 "the ship. Rounded to a whole number of the tower field's spacing. The city behind repeats every " +
                 "few periods, so no building ever shows twice on screen. Read at start.")]
        [Min(10f)] public float worldScrollPeriod = 96f;

        [Tooltip("Seconds the ship takes, while scrolling, to fly over to the boundary for the reveal when the " +
                 "round ended with it off to one side. The camera follows, so the ship stays in the middle.")]
        [Min(0.05f)] public float worldScrollRevealPan = 0.4f;

        [Tooltip("Follow the ship as a camera operator would, by hand, instead of locked on: the camera speeds up " +
                 "after the ship and slows down after it, so the ship leads a little while it flies and drifts back " +
                 "to the middle when it stops, overshooting a touch, and the camera wobbles a little more while it " +
                 "pans. Off: locked on, the ship always exactly in the middle of the screen. Solved exactly, so it " +
                 "moves the same at any frame rate — on both sides of the boundary alike. Eases in and out when " +
                 "switched mid-play.")]
        public bool worldScrollHandheldFollow = true;

        [Tooltip("How briskly the hand-held follow catches up with the ship (its natural frequency, radians a " +
                 "second). Higher keeps the ship nearer the middle: at a steady speed the ship leads by about " +
                 "2 × damping × speed / this — at 5 and 0.7, a ship flying 20 units a second leads by 5.6 (an " +
                 "eighth of the screen). The lead never passes 60% of half the screen.")]
        [Range(1f, 20f)] public float followResponse = 5f;

        [Tooltip("The hand-held follow's damping: 1 settles without overshoot; lower swings a little past the ship " +
                 "when it stops or turns, as a camera on someone's shoulder does.")]
        [Range(0.4f, 1f)] public float followDamping = 0.7f;

        [Tooltip("The hand-held follow's extra wobble while the camera pans, growing with its speed (full at 25 " +
                 "units a second, about 8 px of drift). 0: none. The handheld wander (CameraRig) is on top of it.")]
        [Range(0f, 2f)] public float followShake = 1f;

        /// <summary>How far either side of the rig's origin the scenery is built to reach (world units) when it is
        /// not endless: every pan between rounds.</summary>
        public float SceneryRange => cameraPanRange;

        /// <summary>How far either side of the rig's origin the camera may scroll: without end over the endless
        /// city (<see cref="worldScroll"/> on at start), else no farther than the city that was built, so switching
        /// the scroll on mid-play can never run the view off its edge.</summary>
        public float ScrollReach =>
            towerManager == null || towerManager.EndlessScenery ? float.PositiveInfinity :
            towerManager.SceneryBuilt ? towerManager.BuiltPanRange : SceneryRange;

        Coroutine _revealGlide;

        [Header("Reset Skybox Spin")]
        [Tooltip("Degrees the skybox rotates during reset. Negative = left, positive = right. Randomised each round.")]
        public float skyboxSpinAmount = 60f;

        [Tooltip("Seconds after the shot before the frame-rate reveal starts, so the hit/miss callout " +
                 "lands first and the two do not arrive as one blur.")]
        [Min(0f)] public float fpsRevealDelay = 0.12f;

        Coroutine _revealCoroutine;
        Coroutine _blinkCoroutine;

        /// <summary>True while a round's reveal sequence is playing. ExperimentDirector waits on this
        /// at the end of a phase so a shot fired on the buzzer still gets its reveal.</summary>
        public bool IsRevealing => _revealCoroutine != null;

        void Awake()
        {
            Instance = this;

            if (scoreManager == null) scoreManager = FindAnyObjectByType<ScoreManager>();
            if (laser        == null) laser        = FindAnyObjectByType<LaserFirer>();
            if (towerManager == null) towerManager = FindAnyObjectByType<TowerManager>();
            if (uiManager    == null) uiManager    = FindAnyObjectByType<UIManager>();
            if (perturbation == null) perturbation = FindAnyObjectByType<PerturbationController>();

            // Built here rather than wired in the scene, and before anything is measured: creating
            // components or canvases mid-round would land as a long frame.
            if (roundClock == null) roundClock = FindAnyObjectByType<RoundClock>();
            if (roundClock == null) roundClock = gameObject.AddComponent<RoundClock>();

            if (fpsReveal == null) fpsReveal = FindAnyObjectByType<FpsRevealOverlay>();
            if (fpsReveal == null) fpsReveal = gameObject.AddComponent<FpsRevealOverlay>();
        }

        void OnApplicationFocus(bool focused)
        {
            // Between rounds the director owns the screen; don't grab the cursor back until a round is
            // actually live again.
            if (ExperimentDirector.Instance != null && !ExperimentDirector.Instance.RoundActive) return;

            Cursor.visible = !focused;
            Cursor.lockState = focused ? CursorLockMode.Confined : CursorLockMode.None;
        }

        void Start()
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Confined;

            if (laser != null)
            {
                laser.SetCooldown(0.25f);
                laser.OnShotFired += HandleShotFired;
            }

            if (scoreManager != null) scoreManager.ResetScore();
            if (uiManager != null) uiManager.RefreshDisplayFromManagers(scoreManager);
        }

        void OnDestroy()
        {
            if (laser != null) laser.OnShotFired -= HandleShotFired;
        }

        /// <param name="showHitMarker">False for a round that ended with no shot — there is no
        /// landing point to mark. The fog still blows open, from where the UFO was.</param>
        IEnumerator RevealSequence(Vector3 hitPoint, bool showHitMarker,
                                    PerturbationController.TrialRecord trial)
        {
            // 1. Hit marker at the shot's landing point.
            if (showHitMarker) towerManager.ShowHitMarker(hitPoint);
            else               towerManager.SetFogBlastOrigin(hitPoint);

            // Scrolling the world, the round can end with the boundary off to one side, or off the screen
            // altogether: the ship flies over to it as the fog retreats, the camera with it, and the frame rates
            // follow it.
            float framing = worldScroll ? FrameBoundary() : 0f;

            // 2. Fog retreats; a beat later both sides' frame rates come up over the split. The
            //    reveal is told how long it will stay up (fog retreat + hold, from its own start) so
            //    it can pace itself to land well inside that.
            if (fpsReveal != null && trial.valid)
                fpsReveal.Show(trial, towerManager.TowerBase, towerManager.hitZoneRadius, fpsRevealDelay,
                               holdSec: fogRetreatDuration + hitDisplayDuration - fpsRevealDelay,
                               track: worldScroll, settleShiftX: framing);
            yield return StartCoroutine(towerManager.FadeFog(0f, fogRetreatDuration));

            // 3. Show the tower in its real colours.
            towerManager.ShowTower();

            // 4. Hold so the participant can see where the boundary was and what each side ran at.
            yield return new WaitForSecondsRealtime(hitDisplayDuration);

            // 5. Hide tower, marker and frame rates before the fog returns.
            towerManager.HideTowerAndMarker();
            if (fpsReveal != null) fpsReveal.Hide();

            // 6. The reset gate goes up and stays up until firing is handed back. It tells the
            //    participant not to act yet and masks the reset: fog, skybox, tower move and camera
            //    slide are all scene churn, and a long frame anywhere in it would be a frame-rate
            //    change with nothing to do with the stimulus. It opens on "SAVING…" because the
            //    round's log rows are written here — the one deliberate long frame in the sequence.
            //    Words only if there is a GO! to follow; a round that closed the phase gets a bare
            //    veil.
            bool live = RoundLive;
            if (live) Overlay?.ShowGateSaving();
            else      Overlay?.ShowGateVeil();
            if (ufo != null)
            {
                ufo.SetHidden(true);
                ufo.FreezeMovement = true;
            }

            // The flush below is a synchronous disk write; cut any sound still ringing rather than
            // let the audio thread underrun and crackle through it. The veil is up anyway.
            if (AudioManager.Instance != null)
            {
                AudioManager.Instance.StopOneShots();
                AudioManager.Instance.PauseEngine();
            }

            yield return null;
            if (ExperimentDirector.Instance != null) ExperimentDirector.Instance.FlushLogs();

            if (AudioManager.Instance != null) AudioManager.Instance.ResumeEngine();

            if (live) Overlay?.ShowGateHold();

            // 7. One change of scene, under the veil: the camera pans to a fresh vantage point while
            //    the reveal fades out and the skybox turns, and the next round is dealt as the pan
            //    begins, so the old row of towers dissolves into the new one during the pan instead
            //    of after it. The tower's X is drawn from the spawn band of the view the pan lands on,
            //    exactly as when it was placed after the pan. The random draws keep their order:
            //    spin direction, pan target, tower position. With the legacy ground fog the change
            //    hides in the fog instead, and the tower moves once the fog has closed.
            float spinDir = Random.value > 0.5f ? 1f : -1f;
            CameraRig rig = PanRig;
            float panX    = rig != null ? rig.OriginX + Random.Range(-cameraPanRange, cameraPanRange) : 0f;
            // Scrolling the world, the camera belongs to the UFO, which stays where it is: the world is endless,
            // so there is nothing to come back to, and nothing jumps. The pan target is still drawn, so the
            // tower's draw after it is unchanged, but the pan goes nowhere (it takes its usual time). The next
            // boundary is dealt as far from the ship as it would be without the scroll.
            bool scrolling = worldScroll && rig != null && ufo != null;
            if (scrolling)
            {
                StopRevealGlide();
                panX = rig.RestX;
            }
            bool  inFog   = towerManager.groundFog;
            if (!inFog) towerManager.MoveTowerToRandomPosition(scrolling ? ScrollDealShift(ufo.transform.position.x)
                                                               : rig != null ? panX - rig.RestX : 0f);
            StartCoroutine(SpinSkybox(skyboxSpinAmount * spinDir, fogRestoreDuration));
            Coroutine fogReturn = StartCoroutine(towerManager.FadeFog(1f, fogRestoreDuration));
            yield return StartCoroutine(PanScene(rig, panX));
            yield return fogReturn;
            if (inFog) towerManager.MoveTowerToRandomPosition(scrolling ? ScrollDealShift(ufo.transform.position.x) : 0f);
            towerManager.ApplyVisibility();

            // Don't hand firing back if the phase closed while this reveal was playing. The UFO
            // still has to come back, or the next phase would open on an empty sky.
            if (!RoundLive)
            {
                Overlay?.HideGate();
                RestoreUfo();
                _revealCoroutine = null;
                yield break;
            }

            yield return StartingGun();

            if (laser != null) laser.SetFiringEnabled(true);
            _revealCoroutine = null;
        }

        /// <summary>
        /// The round's starting gun, and the moment its frame-rate split opens. The UFO reappears
        /// where the last round left it, blinking for as long as GO! is up, frozen until the word
        /// leaves; the side baseline is taken afresh because the tower has moved. The split opens
        /// here rather than when firing is handed back, so the change of frame rate happens while
        /// the veil is still lifting instead of as a jolt the instant play starts.
        /// </summary>
        IEnumerator StartingGun()
        {
            if (perturbation != null)
            {
                perturbation.ResetSideTracking();
                perturbation.BeginTrial();
            }

            if (ufo != null)
            {
                StopBlink();
                _blinkCoroutine = StartCoroutine(BlinkUfo());
            }

            if (Overlay != null) yield return Overlay.GoBeat(readyBeatDuration);

            RestoreUfo();
        }

        // Whether the director still has a phase open for firing to come back to. True with no
        // director at all — a hand-played scene has nothing to close it.
        bool RoundLive =>
            ExperimentDirector.Instance == null || ExperimentDirector.Instance.RoundActive;

        // Steady, visible, and back under the pointer's control — the one exit from the reset gate
        // for the UFO, whichever way the sequence ended.
        void RestoreUfo()
        {
            StopBlink();
            StopRevealGlide();
            if (ufo == null) return;
            ufo.SetHidden(false);
            ufo.FreezeMovement = false;
        }

        // Flies the ship just far enough to put the boundary inside the middle of the view, as the fog retreats. The
        // camera follows, so the ship stays in the middle of the screen and the city slides under it; the pointer's
        // own motion adds to the glide as usual. Nothing moves when the boundary is in view already, or as far over
        // as the city reaches when it is not endless. Framed about the ship, where the camera settles — a hand-held
        // follow may still be catching up — and returns how far the view goes from where it is now.
        float FrameBoundary()
        {
            CameraRig rig = PanRig;
            if (rig == null || towerManager == null || ufo == null) return 0f;

            const float Inset = 0.2f;   // viewport fraction kept clear either side
            float z      = towerManager.fixedZ;
            float halfW  = 0.5f * Mathf.Abs(rig.RestViewportPointAtZ(1f, 0.5f, z).x - rig.RestViewportPointAtZ(0f, 0.5f, z).x);
            float shipX  = ufo.transform.position.x;
            float towerX = towerManager.TowerBase.x;
            float left   = shipX - (1f - 2f * Inset) * halfW;
            float right  = shipX + (1f - 2f * Inset) * halfW;
            float shift  = towerX < left ? towerX - left : towerX > right ? towerX - right : 0f;
            float settle = Mathf.Clamp(shipX + shift, rig.ScrollMinX, rig.ScrollMaxX);
            shift        = settle - shipX;

            if (Mathf.Abs(shift) >= 1e-3f)
            {
                StopRevealGlide();
                _revealGlide = StartCoroutine(GlideUfo(shift, worldScrollRevealPan));
            }
            return settle - rig.RestX;
        }

        // Moves the ship `shift` along X over `duration`, eased as CameraRig.PanTo eases a pan (smootherstep). In
        // steps added to wherever the ship is, so the pointer keeps flying it meanwhile. The ship flies it — UfoFlair
        // banks it into the move — and the logs see a continuous path: nothing is ever teleported.
        IEnumerator GlideUfo(float shift, float duration)
        {
            float moved = 0f, elapsed = 0f;
            while (ufo != null)
            {
                elapsed += Time.unscaledDeltaTime;
                float t    = Mathf.Clamp01(elapsed / duration);
                float step = shift * (t * t * t * (t * (t * 6f - 15f) + 10f)) - moved;
                moved += step;
                ufo.transform.position += new Vector3(step, 0f, 0f);
                if (t >= 1f) break;
                yield return null;
            }
            _revealGlide = null;
        }

        // Scrolling, where the next boundary is dealt: the spawn band of the view the ship would have about it
        // without the scroll (UfoController.VirtualViewportX), with the ship at shipX, as a shift from the
        // camera's view now. So the boundary lies as far from the ship, in mouse travel, as it would without
        // the scroll.
        float ScrollDealShift(float shipX)
        {
            CameraRig rig = PanRig;
            if (rig == null || ufo == null || towerManager == null) return 0f;
            float l = rig.RestViewportPointAtZ(0f, 0.5f, towerManager.fixedZ).x;
            float r = rig.RestViewportPointAtZ(1f, 0.5f, towerManager.fixedZ).x;
            float centre = shipX + (0.5f - ufo.VirtualViewportX) * Mathf.Abs(r - l);
            return centre - rig.RestX;
        }

        bool _phaseHold;

        /// <summary>
        /// Scrolling the world, holds the ship still from a phase's start prompt to its GO!, and deals the
        /// phase's first boundary about it, as every later round's is dealt. Otherwise the ship, and the camera
        /// with it, would wander off while the prompt is up, and the first round could open with the boundary
        /// screens away. Called by ExperimentDirector; does nothing without the scroll.
        /// </summary>
        public void HoldForPhaseStart()
        {
            if (!worldScroll || ufo == null) return;
            _phaseHold = true;
            ufo.FreezeMovement = true;

            if (towerManager == null) return;
            towerManager.MoveTowerToRandomPosition(ScrollDealShift(ufo.transform.position.x));
            towerManager.ApplyVisibility();
        }

        /// <summary>Hands the ship back after <see cref="HoldForPhaseStart"/>.</summary>
        public void ReleasePhaseStart()
        {
            if (!_phaseHold) return;
            _phaseHold = false;
            if (ufo != null) ufo.FreezeMovement = false;
        }

        void StopRevealGlide()
        {
            if (_revealGlide == null) return;
            StopCoroutine(_revealGlide);
            _revealGlide = null;
        }

        void StopBlink()
        {
            if (_blinkCoroutine == null) return;
            StopCoroutine(_blinkCoroutine);
            _blinkCoroutine = null;
        }

        // Square-wave blink at ufoFlashHz, starting visible, until stopped. Unscaled time throughout.
        IEnumerator BlinkUfo()
        {
            float elapsed = 0f;
            while (true)
            {
                bool visible = ((int)(elapsed * ufoFlashHz * 2f) & 1) == 0;
                ufo.SetHidden(!visible);
                yield return null;
                elapsed += Time.unscaledDeltaTime;
            }
        }

        /// <summary>The director's message board, resolved lazily — a real null rather than Unity's
        /// fake-null, so the <c>?.</c> calls above short-circuit properly.</summary>
        ExperimentOverlay Overlay
        {
            get
            {
                ExperimentDirector d = ExperimentDirector.Instance;
                if (d == null || d.overlay == null) return null;
                return d.overlay;
            }
        }

        // The rig the reset pans, if there is one.
        CameraRig PanRig
        {
            get
            {
                CameraRig rig = towerManager != null ? towerManager.rig : null;
                return rig != null ? rig : CameraRig.Instance;
            }
        }

        /// <summary>
        /// Slides the camera to <paramref name="targetX"/> between rounds, with crossing counting
        /// suppressed throughout. The caller draws the target as a random offset around the rig's
        /// ORIGIN, never the tower, so the tower cannot land at the centre of the screen every round
        /// and the view cannot random-walk away over a long session. The UFO is carried with the
        /// camera so it holds its place on screen.
        /// </summary>
        IEnumerator PanScene(CameraRig rig, float targetX)
        {
            if (rig == null) yield break;

            if (perturbation != null) perturbation.SuppressCrossings = true;

            // Restored to what it was rather than to false: the reset gate freezes the UFO for
            // longer than the pan, and this must not thaw it early.
            bool wasFrozen = ufo != null && ufo.FreezeMovement;
            if (ufo != null) ufo.FreezeMovement = true;

            try
            {
                bool panning = true;
                StartCoroutine(RunPan(rig, targetX, () => panning = false));

                float prevCamX = rig.RestX;
                while (panning)
                {
                    yield return null;
                    float camX = rig.RestX;
                    if (ufo != null)
                        ufo.transform.position += new Vector3(camX - prevCamX, 0f, 0f);
                    prevCamX = camX;
                }
            }
            finally
            {
                if (ufo != null) ufo.FreezeMovement = wasFrozen;
                if (perturbation != null)
                {
                    perturbation.ResetSideTracking();
                    perturbation.SuppressCrossings = false;
                }
            }
        }

        // Wraps CameraRig.PanTo so the caller can run its own per-frame work alongside it.
        IEnumerator RunPan(CameraRig rig, float targetX, System.Action onDone)
        {
            yield return rig.PanTo(targetX, cameraPanDuration);
            onDone();
        }

        IEnumerator SpinSkybox(float degrees, float duration)
        {
            Material sky = RenderSettings.skybox;
            if (sky == null || !sky.HasProperty("_Rotation")) yield break;

            float startRot = sky.GetFloat("_Rotation");
            float endRot   = startRot + degrees;
            float elapsed  = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t     = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t); // smoothstep
                sky.SetFloat("_Rotation", Mathf.Lerp(startRot, endRot, eased));
                yield return null;
            }

            sky.SetFloat("_Rotation", endRot);
        }

        void HandleShotFired(Vector3 hitPoint, bool hitGround)
        {
            // Stamped and closed before anything else touches the round, so the watchdog cannot also
            // fire for a round the participant already answered.
            if (roundClock != null) roundClock.NoteShot();
            ResolveTrial(hitPoint, TrialOutcome.Shot);
        }

        /// <summary>
        /// Ends a round that ran out with no shot — its time limit, or its crossing cap. Called by
        /// <see cref="RoundClock"/>.
        /// </summary>
        public void ResolveWithoutShot(TrialOutcome outcome)
        {
            // No landing point. The UFO's own position stands in as the centre the fog blows open
            // from; the shot log records the absence explicitly (playerFired = 0).
            Vector3 where = ufo != null ? ufo.transform.position : Vector3.zero;
            ResolveTrial(where, outcome);
        }

        /// <summary>
        /// The single place a round is resolved: close the split, score it, tell QUEST+, show the
        /// result, log it, and start the reveal. Landing the shot within closeRadius of the hidden
        /// tower is the detection response — the participant located the boundary between the two
        /// frame rates. A round that ran out is a miss.
        /// </summary>
        void ResolveTrial(Vector3 hitPoint, TrialOutcome outcome)
        {
            if (scoreManager == null) scoreManager = FindAnyObjectByType<ScoreManager>();

            bool playerFired = outcome == TrialOutcome.Shot;
            if (laser != null) laser.SetFiringEnabled(false);

            // First, before anything is scored: the round is judged against the split that was on
            // screen, and from here on the participant is back at the base frame rate.
            PerturbationController.TrialRecord trial =
                perturbation != null ? perturbation.EndTrial() : default;
            RoundTiming timing = roundClock != null ? roundClock.Snapshot(playerFired) : default;

            // The endless world repeats the boundary once a period, the frame rates swapping sides at every copy.
            // The round is scored, logged and revealed against the copy nearest where it ended — the boundary the
            // participant was at — and its record is turned to match that copy when the rates run the other way
            // round there, so the shot row's tower, side and altered side all read as they would around a single
            // boundary. Nothing happens without the endless world.
            if (towerManager != null && towerManager.FocusBoundaryNear(hitPoint.x)) trial = trial.Flipped();

            Vector3 towerBase = towerManager != null ? towerManager.MainTowerPosition : Vector3.zero;

            float shotScore = 0f, total = 0f, dist = float.NaN;
            bool  isHit = false;

            if (playerFired)
            {
                if (towerManager != null && towerManager.rig != null)
                    towerManager.rig.Shake(hitPoint);

                if (scoreManager != null)
                {
                    shotScore = scoreManager.ScoreShot(hitPoint, towerBase);
                    total     = scoreManager.TotalScore;
                    dist      = scoreManager.LastShotDistance;
                    isHit     = shotScore >= scoreManager.HitPoints;
                }
            }
            else if (scoreManager != null)
            {
                scoreManager.ScoreOutcome(outcome);
                total = scoreManager.TotalScore;
            }

            if (uiManager != null)
            {
                if (playerFired) uiManager.DisplayShotResult(shotScore, total, dist, hitPoint, towerBase);
                else             uiManager.DisplayOutcomeCallout(outcome, total);
            }

            if (perturbation != null) perturbation.ReportResponse(isHit);

            // The miss sting rides with the callout in UIManager.
            if (playerFired && AudioManager.Instance != null)
                AudioManager.Instance.PlayShotOutcome(isHit);

            // Logged after ReportResponse so the QUEST+ figures in the shot row are the posterior
            // including this response.
            if (ExperimentDirector.Instance != null)
                ExperimentDirector.Instance.RecordShot(hitPoint, towerBase, isHit, total, outcome,
                                                       trial, timing);

            // After both of the above: the bar reads the posterior this response just moved, and the
            // practice count the director just advanced.
            if (uiManager != null) uiManager.RefreshProgress();

            if (towerManager != null)
            {
                if (_revealCoroutine != null) StopCoroutine(_revealCoroutine);
                _revealCoroutine = StartCoroutine(RevealSequence(hitPoint, playerFired, trial));
            }
        }
    }
}
