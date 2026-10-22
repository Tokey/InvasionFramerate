// Rain or snow (Weather): one layer of it at one depth, on a quad facing the camera, drawn procedurally — no
// particles, nothing on the CPU but one number a frame.
//
// Every drop falls along the same slanted line at the same speed, so in coordinates that move with the fall —
// u across the slant, v up it, v = y + the fall so far — every drop stands still, and they can sit on a fixed
// grid: one drop at most per cell, at a random place in it, with a random length and brightness. A pixel looks
// only at its own cell and the one a drop's tail can reach down from. Snow adds a gentle sideways wobble to
// each flake, so a pixel looks at the cells around it too.
//
// The fall so far (_Motion.x) comes from the CPU, in double precision and wrapped at exactly 4096 cells, so it
// stays precise however long the session runs and the wrap never shows. It is recomputed every frame from the
// clock: the drops move every frame, at the same speed whatever the frame rate — which is the point of them,
// as fast motion across the whole screen is where a frame rate shows.
//
// Additive light (rain catching the city's glow), alpha left alone. Built-in-style CG, no LightMode tag (URP's
// SRPDefaultUnlit pass), in Resources so builds include it.
Shader "JndUfo/Precipitation"
{
    Properties
    {
        _Mode ("Mode (0 rain, 1 snow)", Float) = 0
        _Color ("Colour (linear)", Vector) = (0.55, 0.62, 0.75, 0)
        _Brightness ("Brightness", Float) = 0.1
        _Shape ("Cell Width, Cell Height (world), Chance, Size (px)", Vector) = (0.6, 4, 0.5, 1.2)
        _Motion ("Fall So Far, Slant, Streak Length, Wobble (world)", Vector) = (0, 0.1, 1.6, 0)
        _Seed ("Seed", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Blend One One, Zero One
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

            float  _Mode;
            float4 _Color;
            float  _Brightness;
            float4 _Shape;
            float4 _Motion;
            float  _Seed;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 w   : TEXCOORD0;   // world x, y on the quad
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.w   = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            float hash21 (float2 p)
            {
                p = frac(p * float2(233.34, 851.73));
                p += dot(p, p + 23.45);
                return frac(p.x * p.y);
            }

            // A cell's key: small numbers whatever the cell, so the hash keeps its precision.
            float2 Key (float c, float r)
            {
                return float2(fmod(c + 8192.0, 4096.0), fmod(r + 8192.0, 4096.0)) + _Seed;
            }

            half4 frag (v2f i) : SV_Target
            {
                float slant = _Motion.y;
                float u     = i.w.x + slant * i.w.y;    // the same all along a drop's path
                float v     = i.w.y + _Motion.x;        // likewise, the fall taken out
                float cw    = _Shape.x;
                float ch    = _Shape.y;
                float pxU   = max(fwidth(u), 1e-5);     // world units per pixel, across and up
                float pxY   = max(fwidth(i.w.y), 1e-5);
                float col   = floor(u / cw);
                float row   = floor(v / ch);
                float acc   = 0.0;

                if (_Mode < 0.5)
                {
                    // Rain: a thin streak from each drop's head up along its path, brightest at the head.
                    for (int j = -1; j <= 0; j++)
                    {
                        float  r   = row + j;
                        float2 key = Key(col, r);
                        if (hash21(key) > _Shape.z) continue;
                        float uc    = (col + 0.15 + 0.7 * hash21(key + 3.1)) * cw;
                        float vc    = (r + hash21(key + 7.3)) * ch;
                        float len   = _Motion.z * (0.7 + 0.6 * hash21(key + 11.9));
                        float along = (v - vc) / len;
                        if (along < 0.0 || along > 1.0) continue;
                        float across = saturate(1.0 - abs(u - uc) / (pxU * _Shape.w));
                        acc += across * (1.0 - along) * smoothstep(0.0, 0.1, along) * (0.45 + 0.55 * hash21(key + 5.7));
                    }
                }
                else
                {
                    // Snow: soft round flakes, each wobbling gently sideways as it falls.
                    for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        float  c   = col + di;
                        float  r   = row + dj;
                        float2 key = Key(c, r);
                        if (hash21(key) > _Shape.z) continue;
                        float h1 = hash21(key + 3.1);
                        float h2 = hash21(key + 5.7);
                        float h3 = hash21(key + 9.2);
                        float uc = (c + 0.1 + 0.8 * h1) * cw + _Motion.w * sin(_Time.y * (0.5 + 0.7 * h2) + h3 * 6.283);
                        float vc = (r + hash21(key + 13.4)) * ch;
                        float du = u - uc;
                        float dv = v - vc;
                        float d  = length(float2(du - slant * dv, dv)) / pxY;   // distance in the world, in pixels
                        float rad = _Shape.w * (0.6 + 0.8 * h2);
                        acc += saturate((rad - d) / 1.2 + 0.5) * (0.5 + 0.5 * h3);
                    }
                }

                return half4(_Color.rgb * (acc * _Brightness), 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
