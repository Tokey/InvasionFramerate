using UnityEngine;

namespace JndUfo
{
    /// <summary>How <see cref="GlowMaterial"/> shapes its light across a mesh. Values match the
    /// shader's <c>_Shape</c>.</summary>
    public enum GlowShape
    {
        /// <summary>Radial falloff from the quad's centre: particles, flares, beacons.</summary>
        Dot = 0,
        /// <summary>Falloff across a line's width: LineRenderer and TrailRenderer.</summary>
        Line = 1,
        /// <summary>No falloff: a whole mesh lit evenly.</summary>
        Flat = 2,
        /// <summary>A soft band at the quad's rim: shockwave rings.</summary>
        Ring = 3,
    }

    /// <summary>
    /// Makes additive light materials on the game's own <c>JndUfo/Glow</c> shader (Resources/Shaders),
    /// so every effect — the laser, its sight, sparks, trails, beacons, the reveal — shares one
    /// look and one blend: additive, depth-tested, never writing depth.
    ///
    /// Nothing is cached here. Each caller owns the materials it makes and destroys them in its own
    /// OnDestroy, so no material outlives the play session that made it, with or without a domain
    /// reload between sessions.
    ///
    /// Should the shader ever be missing or fail to compile on the machine, materials fall back to
    /// Sprites/Default — every effect still draws, as flat alpha-blended shapes instead of soft
    /// additive light — and the Console says so.
    /// </summary>
    public static class GlowMaterial
    {
        public const string ShaderName = "JndUfo/Glow";

        public static readonly int ColorId     = Shader.PropertyToID("_Color");
        public static readonly int ShapeId     = Shader.PropertyToID("_Shape");
        public static readonly int SoftnessId  = Shader.PropertyToID("_Softness");
        public static readonly int RingWidthId = Shader.PropertyToID("_RingWidth");

        /// <summary>
        /// A new additive material. <paramref name="color"/> may be HDR — anything above 1 blooms.
        /// <paramref name="softness"/> is the falloff exponent: 1 is a linear ramp to the edge,
        /// higher pulls the light into the centre.
        /// </summary>
        public static Material Create(GlowShape shape, Color color, float softness = 1.5f, string name = null)
        {
            Shader shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning($"[GlowMaterial] Shader '{ShaderName}' is missing or unsupported here — " +
                                 "light effects fall back to Sprites/Default.");
                shader = Shader.Find("Sprites/Default");
            }

            var m = new Material(shader)
            {
                name      = name ?? "Glow_" + shape,
                hideFlags = HideFlags.DontSave,
            };
            m.SetColor(ColorId, color);
            m.SetFloat(ShapeId, (float)shape);
            m.SetFloat(SoftnessId, softness);
            return m;
        }

        /// <summary>Sets a glow material's colour, HDR included. A thin wrapper so callers never
        /// have to know the property name.</summary>
        public static void SetColor(Material m, Color color)
        {
            if (m != null) m.SetColor(ColorId, color);
        }

        /// <summary>A colour scaled in brightness, alpha untouched — for driving HDR intensity.</summary>
        public static Color Scaled(Color c, float intensity) =>
            new Color(c.r * intensity, c.g * intensity, c.b * intensity, c.a);
    }
}
