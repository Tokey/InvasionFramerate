// The night sky's stars and shooting stars, drawn procedurally on one large quad far behind the city
// (NightSky). Additive, faint, and depth-tested, so the skyline hides them where it stands.
//
// Stars: a grid of cells in world space on the quad; a cell holds a star with probability _Density, at a
// random spot, of a random brightness and tint. Each star is drawn about a pixel or two across whatever
// the distance (sized through fwidth), and twinkles gently on its own slow clock.
//
// Shooting stars: two slots, each with its own period; every period a slot may (with _MeteorChance)
// send one streak across a random part of the sky for a fraction of a second, its position and heading
// drawn from a hash of the period's index, so no two are alike. Pure function of _Time: nothing to update
// on the CPU.
//
// Built-in-style CG, no LightMode tag (URP's SRPDefaultUnlit pass), in Resources so builds include it.
Shader "JndUfo/SkyStars"
{
    Properties
    {
        _Density ("Star Density (per cell)", Range(0, 1)) = 0.3
        _Cell ("Cell Size (world)", Float) = 3.5
        _Brightness ("Brightness", Float) = 0.55
        _Twinkle ("Twinkle", Range(0, 1)) = 0.85
        _StarColorA ("Star Colour A", Color) = (0.82, 0.88, 1.0, 1)
        _StarColorB ("Star Colour B", Color) = (1.0, 0.9, 0.78, 1)
        _FadeLow ("Horizon Fade Start (uv.y)", Range(0, 1)) = 0.04
        _FadeHigh ("Horizon Fade End (uv.y)", Range(0, 1)) = 0.32
        [HDR] _MeteorColor ("Shooting Star", Color) = (1.1, 1.2, 1.4, 1)
        _MeteorLength ("Shooting Star Travel (world)", Float) = 55
        _MeteorPeriods ("Shooting Star Periods (s)", Vector) = (17, 26, 0, 0)
        _MeteorChance ("Shooting Star Chance per Period", Range(0, 1)) = 0.75
        _SkyRect ("Sky Rect (x0, y0, x1, y1 world)", Vector) = (-100, 0, 100, 100)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Blend One One
        ZWrite Off
        ZTest LEqual
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            half   _Density;
            float  _Cell;
            half   _Brightness;
            half   _Twinkle;
            half4  _StarColorA;
            half4  _StarColorB;
            half   _FadeLow;
            half   _FadeHigh;
            half4  _MeteorColor;
            float  _MeteorLength;
            float4 _MeteorPeriods;
            half   _MeteorChance;
            float4 _SkyRect;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float2 w   : TEXCOORD1;   // world x, y on the quad
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                o.w   = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            float hash21 (float2 p)
            {
                p = frac(p * float2(233.34, 851.73));
                p += dot(p, p + 23.45);
                return frac(p.x * p.y);
            }

            // One shooting-star slot: a streak that crosses part of the sky during the first `dur`
            // seconds of each period it fires in.
            half3 Meteor (float2 w, float px, float period, float offset, float slot)
            {
                float t      = _Time.y + offset;
                float epoch  = floor(t / period);
                float local  = t - epoch * period;
                float dur    = 0.7 + 0.4 * hash21(float2(epoch, slot + 3.3));
                if (local > dur || hash21(float2(epoch, slot + 9.1)) > _MeteorChance) return 0;

                float  e     = epoch * 1.37 + slot * 11.0;
                float2 start = float2(lerp(_SkyRect.x, _SkyRect.z, hash21(float2(e, 1.7))),
                                      lerp(_SkyRect.y, _SkyRect.w, lerp(0.5, 0.95, hash21(float2(e, 4.3)))));
                float  ang   = radians(lerp(205.0, 235.0, hash21(float2(e, 8.9))));
                if (hash21(float2(e, 2.2)) > 0.5) ang = radians(lerp(-55.0, -25.0, hash21(float2(e, 6.6))));
                float2 dir   = float2(cos(ang), sin(ang));

                float  prog  = local / dur;
                float2 head  = start + dir * (_MeteorLength * prog);
                float  tail  = _MeteorLength * 0.22 * smoothstep(0.0, 0.25, prog);

                // Distance from this pixel to the streak, in pixels; brighter toward the head.
                float2 pa = w - (head - dir * tail);
                float  h  = saturate(dot(pa, dir) / max(tail, 1e-3));
                float  d  = length(pa - dir * (h * tail)) / px;
                float  a  = saturate(1.0 - d / 1.3) * h * h;
                a *= sin(prog * 3.14159);
                return _MeteorColor.rgb * a;
            }

            half4 frag (v2f i) : SV_Target
            {
                float px   = max(fwidth(i.w.x), 1e-4);   // world units per pixel
                float fade = smoothstep(_FadeLow, _FadeHigh, i.uv.y);

                // ── Stars ─────────────────────────────────────────────────────
                half3  col = 0;
                float2 g   = i.w / _Cell;
                float2 id  = floor(g);
                float  h   = hash21(id);
                if (h < _Density)
                {
                    // Kept away from the cell's borders, so no neighbour cell can reach in.
                    float2 c    = 0.2 + 0.6 * float2(hash21(id + 3.1), hash21(id + 7.7));
                    float  r    = length((frac(g) - c) * _Cell) / px;          // pixels from the star
                    float  size = lerp(0.7, 1.7, pow(hash21(id + 5.3), 4.0));  // most tiny, a few larger
                    float  core = saturate(1.0 - r / size);
                    // Scintillation: two irregular beats per star, a few times a second, dipping toward
                    // nothing and flaring to about twice its brightness.
                    float  r1   = 4.0 + 10.0 * hash21(id + 9.1);
                    float  r2   = 7.0 + 13.0 * hash21(id + 15.9);
                    float  n    = 0.6 * sin(_Time.y * r1 + h * 60.0) + 0.4 * sin(_Time.y * r2 + h * 37.0);
                    float  tw   = max(0.0, 1.0 + _Twinkle * n);
                    float  b    = _Brightness * lerp(0.2, 1.0, pow(hash21(id + 11.7), 2.0)) * tw;
                    col += lerp(_StarColorA.rgb, _StarColorB.rgb, hash21(id + 13.3)) * (core * core * b);
                }

                // ── Shooting stars ────────────────────────────────────────────
                col += Meteor(i.w, px, _MeteorPeriods.x, 0.0, 0.0);
                col += Meteor(i.w, px, _MeteorPeriods.y, 7.3, 1.0);

                return half4(col * fade, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
