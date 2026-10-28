// Draws one row of the foreground's grass (Foreground), once, at startup, into a texture: the silhouette of
// everything in the row — a dark undulating mass at the bottom, a short dense turf over its top, fine blades,
// broad arching leaves, and tall stems carrying seed heads — already out of focus by _Area.w. JndUfo/Grass
// shows it, swaying.
//
// Coordinates are "reference pixels": pixels of a 1080p screen, X across the row from its left end, Y up from
// the bottom edge of the screen with the camera at rest. The texture covers X 0.._Area.x and the band Y
// _Area.y.._Area.y+_Area.z; below the band the row is solid, above it empty.
//
// The blur is exact for a box blur of half-width b: a blade of half-width w seen at distance d from its
// centre line covers clamp((w + b - d) / 2b, 0, min(1, w / b)) of the pixel — so a blade thinner than the blur
// fades out the way a thin stalk does in front of a lens focused far behind it. Blades are united as
// independent coverages (1 - the product of what each leaves uncovered).
//
// Output: r = coverage, g = height relative to the local top of the mass, 0 at 60 px below it to 1 at 80 px
// above (the grass lightens a touch, and catches the city's light, toward its tips). Built-in-style CG, used
// only through Graphics.Blit; in Resources so builds include it.
//
// With _Period (reference px) above 0 — the endless world, where the row's texture holds one period and repeats —
// the silhouette is exactly periodic in X: every lattice of blades has a whole number of cells to the period (its
// cell size eased to fit) and draws each cell's blade from its place in the period, and every noise has a whole
// number of lattice steps to the period and wraps; so a blade near either end leans and blurs across onto the
// other, and the texture tiles without a seam. At 0 everything is exactly as without it.
Shader "Hidden/JndUfo/GrassBake"
{
    Properties
    {
        _Area ("Area (width, band bottom, band height, blur)", Vector) = (1024, 0, 256, 2)
        _Mass ("Mass (top, amplitude, frequency, seed)", Vector) = (200, 10, 0.007, 1)
        _Clump ("Clumps (frequency, strength)", Vector) = (0.01, 0.4, 0, 0)
        _TurfA ("Turf (cell, chance, height min, height max)", Vector) = (2, 1, 6, 18)
        _TurfB ("Turf (half-width min, max, lean, reach)", Vector) = (1, 1.8, 0.3, 6)
        _GrassA ("Grass (cell, chance, height min, height max)", Vector) = (5, 0.9, 15, 50)
        _GrassB ("Grass (half-width min, max, lean, reach)", Vector) = (0.8, 1.5, 0.4, 8)
        _LeafA ("Leaves (cell, chance, height min, height max)", Vector) = (30, 0.4, 15, 35)
        _LeafB ("Leaves (half-width min, max, lean, reach)", Vector) = (2.5, 4, 0.85, 3)
        _StemA ("Stems (cell, chance, height min, height max)", Vector) = (25, 0.45, 45, 90)
        _StemB ("Stems (half-width, head half-width, head length, reach)", Vector) = (0.55, 2.2, 12, 4)
        _StemLean ("Stem Lean", Float) = 0.45
        _Period ("Period (reference px, 0 = off)", Float) = 0
    }

    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            float4 _Area;
            float4 _Mass;
            float4 _Clump;
            float4 _TurfA,  _TurfB;
            float4 _GrassA, _GrassB;
            float4 _LeafA,  _LeafB;
            float4 _StemA,  _StemB;
            float  _StemLean;
            float  _Period;

            uint Pcg (uint v)
            {
                uint state = v * 747796405u + 2891336453u;
                uint word  = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
                return (word >> 22u) ^ word;
            }

            // A random number in 0..1 for cell i of stream s. Exact on the integers, whatever their size.
            float Rnd (int i, uint s)
            {
                return Pcg(asuint(i) * 2654435761u ^ Pcg(s)) * (1.0 / 4294967296.0);
            }

            float Noise1 (float x, uint s)
            {
                float i = floor(x);
                float f = x - i;
                float u = f * f * (3.0 - 2.0 * f);
                return lerp(Rnd((int)i, s), Rnd((int)i + 1, s), u);
            }

            // Noise1 at frequency f, wrapping round _Period: a whole number of lattice steps to the period, the
            // frequency eased to fit, the lattice index taken round it. Only used with _Period above 0.
            float NoiseP (float x, float f, uint s)
            {
                float n = max(1.0, round(_Period * f));
                float u = x * (n / _Period);
                float i = floor(u);
                float t = u - i;
                float w = t * t * (3.0 - 2.0 * t);
                float a = i - n * floor(i / n);           // i round the period, for negative i too
                float b = a + 1.0 >= n ? 0.0 : a + 1.0;
                return lerp(Rnd((int)a, s), Rnd((int)b, s), w);
            }

            uint Seed () { return (uint)_Mass.w * 977u; }

            // The top of the row's solid mass at X: rolling, like the tops of low bushes and tussocks.
            float MassTop (float x)
            {
                uint  s = Seed();
                float f;
                if (_Period > 0.0)   // each octave wraps round the period on its own lattice, so no offsets needed
                    f = 0.55 * NoiseP(x, _Mass.z, s) + 0.30 * NoiseP(x, _Mass.z * 2.31, s + 101u)
                      + 0.15 * NoiseP(x, _Mass.z * 5.13, s + 211u);
                else
                    f = 0.55 * Noise1(x * _Mass.z, s) + 0.30 * Noise1(x * _Mass.z * 2.31 + 17.7, s + 101u)
                      + 0.15 * Noise1(x * _Mass.z * 5.13 + 3.1, s + 211u);
                return _Mass.x + _Mass.y * clamp((f - 0.5) * 2.6, -1.0, 1.0);
            }

            // Clumps: the grass is taller and denser in some places than others.
            float Clump (float x)
            {
                float n = _Period > 0.0 ? NoiseP(x, _Clump.x, Seed() + 7u) : Noise1(x * _Clump.x, Seed() + 7u);
                return 1.0 + _Clump.y * (n * 2.0 - 1.0);
            }

            // A lattice of cells of size `cell` along X: with _Period above 0, eased to a whole number of cells to the
            // period. `count` is that number, 0 without a period.
            void Lattice (float cell, out float size, out int count)
            {
                size  = cell;
                count = 0;
                if (_Period > 0.0)
                {
                    count = (int)max(1.0, round(_Period / cell));
                    size  = _Period / count;
                }
            }

            // The cell a blade's draw comes from: its own, or with a period its place in the period.
            int Place (int id, int count)
            {
                if (count <= 0) return id;
                return id - count * (int)floor(id / (float)count);
            }

            // How much of a pixel at distance d from a blade's centre line the blade covers, half-width w, blur b.
            float Cover (float w, float d, float b)
            {
                return clamp((w + b - d) / (2.0 * b), 0.0, min(1.0, w / b));
            }

            // Tapering blades — fine grass or broad leaves. A: cell, chance, height min, max;
            // B: half-width min, max, lean (tip offset over height), reach (cells either side to look at).
            float BladesKeep (float X, float Y, float b, float4 A, float4 B, float bend, uint s)
            {
                float cell;
                int   count;
                Lattice(A.x, cell, count);
                float keep  = 1.0;
                int   c     = (int)floor(X / cell);
                int   reach = (int)B.w;
                [loop] for (int j = -reach; j <= reach; j++)
                {
                    int   id    = c + j;
                    int   p     = Place(id, count);                      // the blade's draw: its place in the period
                    float rootX = (id + 0.1 + 0.8 * Rnd(p, s)) * cell;
                    float cl    = Clump(rootX);
                    if (Rnd(p, s + 1u) > A.y * saturate(cl)) continue;    // sparser between the clumps

                    float rh    = Rnd(p, s + 2u);
                    float H     = lerp(A.z, A.w, rh * rh) * cl;          // mostly short, a few tall
                    float rootY = MassTop(rootX) - 0.12 * H - 2.0;       // rooted inside the mass
                    float t     = (Y - rootY) / H;
                    if (t <= 0.0 || t >= 1.0) continue;

                    float L     = (Rnd(p, s + 3u) * 2.0 - 1.0) * B.z;
                    float tb    = pow(t, bend);
                    float xc    = rootX + L * H * tb;                    // bends over more toward the tip
                    float slope = L * bend * tb / max(t, 1e-3);
                    float w     = lerp(B.x, B.y, Rnd(p, s + 4u)) * pow(1.0 - t, 0.7);
                    float d     = abs(X - xc) * rsqrt(1.0 + slope * slope);
                    keep *= 1.0 - Cover(w, d, b);
                }
                return keep;
            }

            // Tall thin stems with a seed head at the top, nodding over. A: cell, chance, height min, max;
            // B: stem half-width, head half-width, head length, reach.
            float StemsKeep (float X, float Y, float b, float4 A, float4 B, float lean, uint s)
            {
                float cell;
                int   count;
                Lattice(A.x, cell, count);
                float keep  = 1.0;
                int   c     = (int)floor(X / cell);
                int   reach = (int)B.w;
                [loop] for (int j = -reach; j <= reach; j++)
                {
                    int   id    = c + j;
                    int   p     = Place(id, count);
                    float rootX = (id + 0.1 + 0.8 * Rnd(p, s)) * cell;
                    float cl    = Clump(rootX);
                    if (Rnd(p, s + 1u) > A.y * saturate(cl)) continue;

                    float H     = lerp(A.z, A.w, Rnd(p, s + 2u)) * cl;
                    float rootY = MassTop(rootX) - 0.1 * H - 2.0;
                    float top   = rootY + H;
                    if (Y <= rootY || Y >= top + 2.0 * b) continue;

                    float t     = saturate((Y - rootY) / H);
                    float L     = (Rnd(p, s + 3u) * 2.0 - 1.0) * lean;
                    float tb    = pow(t, 2.4);
                    float xc    = rootX + L * H * tb;
                    float slope = L * 2.4 * tb / max(t, 1e-3);

                    // The head: a slim ellipse over the top of the stem.
                    float hl = B.z * lerp(0.75, 1.25, Rnd(p, s + 4u));
                    float e  = (Y - (top - 0.5 * hl)) / (0.5 * hl);
                    float w  = B.x;
                    if (abs(e) < 1.0) w = max(w, B.y * lerp(0.8, 1.15, Rnd(p, s + 5u)) * sqrt(1.0 - e * e));

                    float d = abs(X - xc) * rsqrt(1.0 + slope * slope);
                    keep *= 1.0 - Cover(w, d, b) * saturate((top - Y) / (2.0 * b) + 0.5);
                }
                return keep;
            }

            float4 frag (v2f_img i) : SV_Target
            {
                float X = i.uv.x * _Area.x;
                float Y = _Area.y + i.uv.y * _Area.z;
                float b = max(_Area.w, 0.6);
                uint  s = Seed();

                // The mass's top is softer than the blur alone would make it, and the blades' bases crowd over
                // it, so it never reads as a line: the grass thickens into the ground rather than standing on it.
                float top  = MassTop(X);
                float mb   = 2.5 * b + 3.0;
                float keep = 1.0 - saturate((top - Y) / (2.0 * mb) + 0.5);
                if (_TurfA.y > 0.0) keep *= BladesKeep(X, Y, b, _TurfA, _TurfB, 1.5, s + 5u);   // the short, dense turf
                keep *= BladesKeep(X, Y, b, _GrassA, _GrassB, 1.7, s + 11u);
                keep *= BladesKeep(X, Y, b, _LeafA,  _LeafB,  2.1, s + 23u);
                keep *= StemsKeep (X, Y, b, _StemA,  _StemB,  _StemLean, s + 37u);

                // g: 0 well down in the mass, rising through its top to 1 at the tips of the grass.
                return float4(1.0 - keep, saturate((Y - top + 60.0) / 140.0), 0.0, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
