using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace JndUfo
{
    /// <summary>
    /// Sparks and embers coming off the damaged buildings of the <see cref="CitySkyline"/>: bursts of orange
    /// sparks falling from each break at irregular intervals, a slow drift of embers rising off the fires,
    /// and blue-white crackle from a shorting cable on one building.
    ///
    /// Everything is a ParticleSystem configured here in code and left to run on its own — bursts with a
    /// probability give the irregular rhythm with no per-frame script at all. Every system is world-space,
    /// sized and paced by its site's depth so it reads the same on screen at any distance, and drawn with
    /// the shared additive glow material, so the brightest sparks bloom.
    ///
    /// It is ambient, and it is everywhere at once: the sites are fixed parts of the city, nothing here
    /// knows where the hidden boundary is, and the sparks look the same on both sides of it.
    /// </summary>
    public class CitySparks : MonoBehaviour
    {
        [Header("Fire Sparks")]
        public bool sparks = true;
        [ColorUsage(false, true)] public Color sparkColor = new Color(3.4f, 2.0f, 0.95f, 1f);
        [Tooltip("Seconds between chances of a burst at each site.")]
        [Min(0.05f)] public float burstInterval = 0.3f;
        [Tooltip("Chance of a burst each interval — low, so the rhythm is irregular.")]
        [Range(0f, 1f)] public float burstChance = 0.28f;
        [Tooltip("Sparks per burst (min, max).")]
        public Vector2Int burstCount = new Vector2Int(4, 12);

        [Header("Embers")]
        public bool embers = true;
        [ColorUsage(false, true)] public Color emberColor = new Color(2.6f, 1.0f, 0.3f, 1f);
        [Tooltip("Embers per second rising off each fire.")]
        [Min(0f)] public float emberRate = 3.5f;

        [Header("Electric")]
        public bool electric = true;
        [ColorUsage(false, true)] public Color electricColor = new Color(1.6f, 2.6f, 4.0f, 1f);

        readonly List<Material> _materials = new List<Material>();

        /// <summary>Creates the systems for <paramref name="sites"/>. Call once.</summary>
        public void Build(IReadOnlyList<DamageSite> sites)
        {
            if (sites == null) return;

            Material sparkMat = null, emberMat = null, electricMat = null;
            if (sparks)   sparkMat    = Own(GlowMaterial.Create(GlowShape.Dot, sparkColor,    1.3f, "CitySparks"));
            if (embers)   emberMat    = Own(GlowMaterial.Create(GlowShape.Dot, emberColor,    1.8f, "CityEmbers"));
            if (electric) electricMat = Own(GlowMaterial.Create(GlowShape.Dot, electricColor, 1.2f, "CityCrackle"));

            for (int i = 0; i < sites.Count; i++)
            {
                DamageSite s = sites[i];
                if (s.Electric)
                {
                    if (electricMat != null) MakeCrackle(s, electricMat, i);
                    if (sparkMat != null)    MakeSparks(s, sparkMat, i, scale: 0.6f);
                }
                else
                {
                    if (sparkMat != null) MakeSparks(s, sparkMat, i, scale: 1f);
                    if (emberMat != null) MakeEmbers(s, emberMat, i);
                }
            }
        }

        void OnDestroy()
        {
            foreach (Material m in _materials) if (m != null) Destroy(m);
            _materials.Clear();
        }

        Material Own(Material m)
        {
            _materials.Add(m);
            return m;
        }

        // ── Systems ──────────────────────────────────────────────────────────

        void MakeSparks(in DamageSite s, Material mat, int index, float scale)
        {
            float k = s.DepthScale;
            ParticleSystem ps = NewSystem("Sparks", s, index, mat, ParticleSystemRenderMode.Stretch, 64);

            var main = ps.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.55f, 1.3f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(2.0f * k, 5.5f * k);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.05f * k, 0.10f * k);
            main.startColor      = new ParticleSystem.MinMaxGradient(Color.white, new Color(1f, 0.85f, 0.6f));
            main.gravityModifier = 0.55f * k;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, (short)Mathf.Max(1, Mathf.RoundToInt(burstCount.x * scale)),
                                             (short)Mathf.Max(1, Mathf.RoundToInt(burstCount.y * scale)),
                                             0, burstInterval) { probability = burstChance },
            });

            var shape = ps.shape;
            shape.enabled   = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle     = 38f;
            shape.radius    = Mathf.Max(0.05f, s.Width * 0.4f);
            shape.rotation  = new Vector3(-90f, 0f, 0f);   // the cone's axis from +Z to straight up

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color   = Fade(new Color(1f, 0.95f, 0.85f), new Color(1f, 0.5f, 0.15f), new Color(0.7f, 0.12f, 0.04f));

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size    = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.35f));

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.velocityScale = 0.045f;
            r.lengthScale   = 1.2f;

            ps.Play();
        }

        void MakeEmbers(in DamageSite s, Material mat, int index)
        {
            float k = s.DepthScale;
            ParticleSystem ps = NewSystem("Embers", s, index, mat, ParticleSystemRenderMode.Billboard, 32);

            var main = ps.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(1.8f, 3.4f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(0.25f * k, 0.8f * k);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.04f * k, 0.085f * k);
            main.startColor      = new ParticleSystem.MinMaxGradient(Color.white, new Color(1f, 0.75f, 0.5f));
            main.gravityModifier = -0.035f * k;   // drawn upward by the fire's heat

            var emission = ps.emission;
            emission.rateOverTime = emberRate;

            var shape = ps.shape;
            shape.enabled   = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale     = new Vector3(Mathf.Max(0.1f, s.Width), 0.2f * k, 0.2f);
            shape.rotation  = new Vector3(-90f, 0f, 0f);

            var noise = ps.noise;
            noise.enabled     = true;
            noise.strength    = new ParticleSystem.MinMaxCurve(0.35f * k);
            noise.frequency   = 0.5f;
            noise.scrollSpeed = 0.25f;
            noise.quality     = ParticleSystemNoiseQuality.Low;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color   = Fade(new Color(1f, 0.8f, 0.45f), new Color(1f, 0.45f, 0.12f), new Color(0.6f, 0.1f, 0.03f));

            ps.Play();
        }

        void MakeCrackle(in DamageSite s, Material mat, int index)
        {
            float k = s.DepthScale;
            ParticleSystem ps = NewSystem("Crackle", s, index, mat, ParticleSystemRenderMode.Stretch, 48);

            var main = ps.main;
            main.startLifetime   = new ParticleSystem.MinMaxCurve(0.10f, 0.28f);
            main.startSpeed      = new ParticleSystem.MinMaxCurve(5f * k, 11f * k);
            main.startSize       = new ParticleSystem.MinMaxCurve(0.03f * k, 0.06f * k);
            main.gravityModifier = 0.4f * k;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, 5, 13, 0, 0.17f) { probability = 0.14f },
            });

            var shape = ps.shape;
            shape.enabled   = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius    = 0.08f * k;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color   = Fade(Color.white, new Color(0.6f, 0.85f, 1f), new Color(0.3f, 0.5f, 1f));

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.velocityScale = 0.03f;
            r.lengthScale   = 1f;

            ps.Play();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        // Drawn by the main camera, in focus, even at a site the backdrop blurs (see Backdrop): sparks are small
        // and brief, and particle systems can only be drawn by a camera.
        ParticleSystem NewSystem(string kind, in DamageSite site, int index, Material mat,
                                 ParticleSystemRenderMode mode, int maxParticles)
        {
            var go = new GameObject($"{kind}_{index}");
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.position = site.Position;

            var ps = go.AddComponent<ParticleSystem>();
            // A fresh system starts playing with stock settings; stop it before anything is drawn.
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake     = false;
            main.loop            = true;
            main.duration        = 5f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles    = maxParticles;
            main.useUnscaledTime = true;
            main.scalingMode     = ParticleSystemScalingMode.Local;
            main.prewarm         = false;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode                 = mode;
            r.sharedMaterial             = mat;
            r.shadowCastingMode          = ShadowCastingMode.Off;
            r.receiveShadows             = false;
            r.lightProbeUsage            = LightProbeUsage.Off;
            r.reflectionProbeUsage       = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            return ps;
        }

        /// <summary>A colour-over-lifetime that runs start → mid → end and fades out.</summary>
        static ParticleSystem.MinMaxGradient Fade(Color a, Color b, Color c)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 0.3f), new GradientColorKey(c, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.55f), new GradientAlphaKey(0f, 1f) });
            return new ParticleSystem.MinMaxGradient(g);
        }
    }
}
