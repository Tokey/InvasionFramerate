using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JndUfo
{
    public class LaserFirer : MonoBehaviour
    {
        [Header("Fire Point")]
        public Transform firePoint;
        public Vector3 autoFirePointOffset = new Vector3(0f, 1.0f, 0f);

        [Header("Raycast")]
        public LayerMask groundMask = ~0;
        public float maxRange = 200f;
        [Tooltip("Fire direction in world space. Straight down at the city: the default matches the scene, " +
                 "so a component that loses its saved values still fires the right way.")]
        public Vector3 fireDirection = Vector3.down;
        [Tooltip("Where a shot ends when the raycast hits nothing and it is not heading down to the " +
                 "ground line: this far along the fire direction, world units.")]
        public float beamLength = 30f;

        [Header("Shot")]
        [Tooltip("Draws the shot — bolt, flashes, shockwave, sparks. Auto-created on this object if " +
                 "left empty; place one by hand to retune the look from the Inspector.")]
        public ShotFx shotFx;

        [Header("Legacy Beam")]
        [Tooltip("Optional: a renderer (a Cylinder, say) stretched from the muzzle to the impact and " +
                 "flashed through its emission on every shot, as this weapon drew its bolt before " +
                 "ShotFx. Nothing is created if left empty.")]
        public Renderer beamRenderer;
        [Tooltip("Legacy beam diameter in world units.")]
        [Min(0.001f)] public float beamWidth = 0.2f;
        [Tooltip("Seconds the legacy beam stays visible, fading out.")]
        public float beamDuration = 0.35f;
        [Tooltip("Emission intensity multiplier for the legacy beam on fire.")]
        [Min(0f)] public float beamFlashIntensity = 3f;

        [Header("Sight")]
        [Tooltip("Draws the laser's targeting beam on the ship between shots. Auto-created on this " +
                 "object if left empty; place one by hand to retune the look from the Inspector.")]
        public WeaponSight sight;

        [Header("Input")]
        public bool fireOnLeftClick = true;
        public Key altFireKey = Key.Space;

        [Header("Pointer Glow")]
        [Tooltip("Renderer on the UFO pointer/nozzle tip.")]
        public Renderer pointerRenderer;
        [Tooltip("Pulse the pointer's glow between pulseMin and pulseMax. Off, the default, it holds " +
                 "steady at steadyLevel: nothing on the weapon moves of its own accord, so the only " +
                 "motion for the eye to follow is the ship's.")]
        public bool pulsePointer = false;
        [Tooltip("Where the steady glow sits between pulseMin (0) and pulseMax (1).")]
        [Range(0f, 1f)] public float steadyLevel = 0.5f;
        [Min(0f)] public float pulseMin          = 1.0f;
        [Min(0f)] public float pulseMax          = 3.5f;
        [Min(0.1f)] public float pulseSpeed      = 1.6f;
        [Min(0f)] public float fireFlashIntensity = 14f;
        [Min(0.01f)] public float fireFlashDecay  = 0.15f;

        public event Action<Vector3, bool> OnShotFired;

        /// <summary>Where the pointer glow sits this frame, 0 at pulseMin and 1 at pulseMax:
        /// <see cref="steadyLevel"/> while it holds steady, its pulse with <see cref="pulsePointer"/>
        /// on. Published so the sight can breathe on the same clock.</summary>
        public float PointerPulse01 { get; private set; }

        /// <summary>The muzzle flash this frame: 1 on the frame a shot fires, decaying to 0 over
        /// fireFlashDecay exactly as the pointer's own flash does.</summary>
        public float FireFlash01 { get; private set; }

        float     _lastFireTime  = -999f;
        float     _cooldown      = 0.25f;
        bool      _firingEnabled = true;
        float     _flashCurrent  = 0f;
        Color     _baseEmission  = Color.white;
        float     _legacyBeamAge = -1f;   // seconds the legacy beam has been up; < 0 while it is down

        TowerManager          _tower;
        MaterialPropertyBlock _propBlock;
        MaterialPropertyBlock _beamPropBlock;

        static readonly int EmissionId  = Shader.PropertyToID("_EmissionColor");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId     = Shader.PropertyToID("_Color");

        public void SetCooldown(float c)         => _cooldown      = c;
        public void SetFiringEnabled(bool value) => _firingEnabled = value;

        /// <summary>
        /// Whether the participant may act right now. This is the project's single "a round is live"
        /// signal — off during the reveal, between phases, and once the round has ended — so
        /// <see cref="RoundClock"/> takes its round boundaries from its edges.
        /// </summary>
        public bool FiringEnabled => _firingEnabled;

        void Awake()
        {
            if (firePoint == null)
            {
                var fp = new GameObject("FirePoint").transform;
                fp.SetParent(transform, false);
                fp.localPosition = autoFirePointOffset;
                firePoint = fp;
            }

            // The ground line a downward miss lands on: the same line the sight ends on.
            _tower = FindAnyObjectByType<TowerManager>();

            // Built up front: creating the sight's renderers on the first frame they are needed would
            // land as a long frame. It lives here, on the muzzle, so hiding the ship between rounds
            // takes it with it (UfoController.SetHidden walks the children).
            if (sight == null) sight = GetComponent<WeaponSight>();
            if (sight == null) sight = gameObject.AddComponent<WeaponSight>();

            // Built up front for the same reason. Its renderers live at the scene root rather than
            // here (see ShotFx), so a shot stays where it was fired and the reset cannot cut it off.
            if (shotFx == null) shotFx = GetComponent<ShotFx>();
            if (shotFx == null) shotFx = gameObject.AddComponent<ShotFx>();

            _propBlock     = new MaterialPropertyBlock();
            _beamPropBlock = new MaterialPropertyBlock();

            // Cache emission colour from the material — hue never changes, only intensity is animated
            if (pointerRenderer != null)
            {
                var mat = pointerRenderer.sharedMaterial;
                if (mat != null && mat.HasProperty(EmissionId))
                    _baseEmission = mat.GetColor(EmissionId);
                if (_baseEmission.maxColorComponent < 0.001f)
                    _baseEmission = Color.white;
            }

            if (beamRenderer != null) beamRenderer.enabled = false;
        }

        void Update()
        {
            AnimatePointer();
            StepLegacyBeam();

            if (!_firingEnabled) return;
            bool wantsFire = (fireOnLeftClick && Mouse.current  != null && Mouse.current.leftButton.wasPressedThisFrame)
                          || (Keyboard.current != null && Keyboard.current[altFireKey].wasPressedThisFrame);
            if (!wantsFire || Time.time - _lastFireTime < _cooldown) return;

            Fire();
        }

        void AnimatePointer()
        {
            float phase     = pulsePointer
                ? (Mathf.Sin(Time.unscaledTime * pulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f
                : steadyLevel;
            float pulse     = Mathf.Lerp(pulseMin, pulseMax, phase);
            _flashCurrent   = Mathf.MoveTowards(_flashCurrent, 0f,
                                                  fireFlashIntensity / Mathf.Max(0.001f, fireFlashDecay)
                                                  * Time.unscaledDeltaTime);
            float intensity = Mathf.Max(pulse, _flashCurrent);

            // Published before the early-out below: the sight reads these even when the scene has
            // no pointer renderer to glow.
            PointerPulse01 = phase;
            FireFlash01    = fireFlashIntensity > 0f ? Mathf.Clamp01(_flashCurrent / fireFlashIntensity) : 0f;

            if (pointerRenderer == null) return;

            pointerRenderer.GetPropertyBlock(_propBlock);
            _propBlock.SetColor(EmissionId, _baseEmission * intensity);
            pointerRenderer.SetPropertyBlock(_propBlock);
        }

        public void Fire()
        {
            _lastFireTime = Time.time;
            _flashCurrent = fireFlashIntensity;
            // AnimatePointer already ran this frame, so publish the flash now rather than let the
            // sight see it a frame after the shot appears.
            FireFlash01   = 1f;

            Vector3 origin = firePoint.position;
            Vector3 dir    = fireDirection.sqrMagnitude > 1e-6f ? fireDirection.normalized : Vector3.up;
            bool    hit    = false;
            Vector3 endPt  = origin + dir * beamLength;

            if (Physics.Raycast(origin, dir, out RaycastHit info, maxRange, groundMask, QueryTriggerInteraction.Ignore))
            {
                endPt = info.point;
                hit   = true;
            }
            else if (dir.y < 0f && _tower != null && origin.y > _tower.groundY)
            {
                // A miss heading down lands on the ground line, where the sight showed it would,
                // instead of stopping in mid-air beamLength below the ship. Only y moves: x and z
                // are what the round is scored and logged on, and they stay exactly where the fire
                // direction put them — the muzzle's own x and z for this scene's straight-down shot.
                endPt.y = _tower.groundY;
            }

            // The shot's visuals, then the muzzle sound, then the shot itself — so the sound leads
            // the impact sounds that GameManager triggers off the same shot.
            if (shotFx != null)       shotFx.Play(origin, endPt);
            if (beamRenderer != null) ShowLegacyBeam(origin, endPt);

            if (AudioManager.Instance != null) AudioManager.Instance.PlayLaserFire();

            OnShotFired?.Invoke(endPt, hit);
        }

        // ── Legacy beam ──────────────────────────────────────────────────────

        // Spans the muzzle → impact at full length, holds, and fades its emission out over
        // beamDuration. Unity's Cylinder is two units tall along its Y, so scale.y = length / 2.
        void ShowLegacyBeam(Vector3 from, Vector3 to)
        {
            Vector3   span = to - from;
            float     len  = span.magnitude;
            Transform bt   = beamRenderer.transform;

            bt.position   = from + span * 0.5f;
            bt.rotation   = len > 1e-6f ? Quaternion.FromToRotation(Vector3.up, span / len) : Quaternion.identity;
            bt.localScale = new Vector3(beamWidth, Mathf.Max(0.01f, len * 0.5f), beamWidth);
            beamRenderer.enabled = true;

            _legacyBeamAge = 0f;
            TintLegacyBeam(1f);
        }

        // Stepped from Update rather than run as a coroutine, which would allocate on every shot.
        void StepLegacyBeam()
        {
            if (_legacyBeamAge < 0f) return;
            if (beamRenderer == null) { _legacyBeamAge = -1f; return; }

            _legacyBeamAge += Time.unscaledDeltaTime;
            float t = 1f - Mathf.Clamp01(_legacyBeamAge / Mathf.Max(0.01f, beamDuration)); // 1 → 0
            if (t <= 0f)
            {
                beamRenderer.enabled = false;
                _legacyBeamAge = -1f;
                return;
            }

            TintLegacyBeam(t);
        }

        void TintLegacyBeam(float t)
        {
            Color emit  = _baseEmission * (beamFlashIntensity * t);
            Color solid = new Color(emit.r, emit.g, emit.b, 1f);
            _beamPropBlock.SetColor(EmissionId,  emit);
            _beamPropBlock.SetColor(BaseColorId, solid);
            _beamPropBlock.SetColor(ColorId,     solid);
            beamRenderer.SetPropertyBlock(_beamPropBlock);
        }
    }
}
