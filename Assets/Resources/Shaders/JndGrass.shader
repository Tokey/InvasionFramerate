// One row of the foreground's grass (Foreground), swaying: the silhouette Hidden/JndUfo/GrassBake drew once
// at startup — already out of focus — bent by the wind, near black, its tips edged with the city's light,
// with a few soft motes of lit dust drifting over it.
//
// The wind is a sideways shift of the silhouette that grows from nothing at the roots to its full size at the
// tips. Gusts come and go at random — calm stretches, then a gust bending a stretch of grass downwind and
// setting it swaying — and roll across the field with the breeze, the same wind through every row, so the
// nearer rows' gusts cross the screen faster. A smaller, quicker flutter on top keeps neighbouring blades
// from moving as one, and a gust lights the tips a touch as they bend toward the city's glow. The motes
// rise slowly, drift downwind and fade in and out over several seconds each, then come back somewhere else.
// Everything is a function of _Time: computed afresh every frame, the same at any frame rate, nothing to
// update on the CPU. The randomness is sums of sines at unrelated rates, so it never visibly repeats and
// keeps its precision however long the session runs.
//
// The endless world (_WorldX = 1): the row is one wide quad over a texture that repeats (see Foreground), and the
// row jumps whole periods to stay under the camera. Everything that moves is then a function of WORLD position —
// the gusts and buffeting always are, and the flutter and the motes become so, at _Area.w reference px per world
// unit — so a jump changes nothing on screen. Off, all as before.
//
// Premultiplied: alpha is the grass's coverage, colour already weighted by it, the motes and the backlit
// edges added on top. Transparent, depth-tested, sorted back to front by distance with every other
// transparent, so the play plane's effects (drawn first, being farther) go behind it. Built-in-style CG, no
// LightMode tag (URP's SRPDefaultUnlit pass), in Resources so builds include it.
Shader "JndUfo/Grass"
{
    Properties
    {
        _MainTex ("Silhouette (baked)", 2D) = "black" {}
        _Base ("Colour, Bottom of the Screen", Color) = (0.04, 0.04, 0.052, 1)
        _Upper ("Colour, at the Grass Line", Color) = (0.095, 0.082, 0.088, 1)
        _Ground ("Colour Ramp (screen height from, to)", Vector) = (0.04, 0.3, 0, 0)
        [HDR] _Rim ("Backlit Edges", Color) = (0.1, 0.06, 0.03, 1)
        _Shade ("Shade (1 = the shared colour)", Float) = 1
        _Area ("Area (width, quad bottom, quad top: reference px)", Vector) = (1024, -400, 300, 0)
        _Band ("Band (bottom, top, sway base, sway height: reference px)", Vector) = (100, 300, 100, 100)
        _Wind ("Wind (sway px, flutter px, phase, strength)", Vector) = (4, 1, 0, 1)
        _Motes ("Motes (cell px, chance, low px, high px)", Vector) = (300, 0.3, 200, 260)
        _MoteLook ("Mote Look (radius px, softness px, level)", Vector) = (4, 1.5, 0.3, 0)
        [HDR] _MoteColor ("Mote Colour", Color) = (1, 0.72, 0.42, 1)
        _WorldX ("Flutter and Motes by World X (endless world)", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Blend One OneMinusSrcAlpha
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

            sampler2D _MainTex;
            half4  _Base;
            half4  _Upper;
            float4 _Ground;
            half4  _Rim;
            half   _Shade;
            float4 _Area;
            float4 _Band;
            float4 _Wind;
            float4 _Motes;
            float4 _MoteLook;
            half4  _MoteColor;
            float  _WorldX;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float2 uv     : TEXCOORD0;
                float  wx     : TEXCOORD1;   // world x, for the wind
                float4 screen : TEXCOORD2;   // for the colour, which goes by height on the screen
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos    = UnityObjectToClipPos(v.vertex);
                o.uv     = v.uv;
                o.wx     = mul(unity_ObjectToWorld, v.vertex).x;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            float hash21 (float2 p)
            {
                p = frac(p * float2(233.34, 851.73));
                p += dot(p, p + 23.45);
                return frac(p.x * p.y);
            }

            // Soft discs of lit dust, like out-of-focus lights: brighter toward their rim, as a lens draws
            // them. One cell in a few holds one; each lives 8-15 s, fading in and out, rising a little and
            // drifting, then turns up somewhere else in its cell.
            half3 Motes (float X, float Y, float t)
            {
                half3 acc = 0;
                if (_Motes.y <= 0.0) return acc;

                float cell = _Motes.x;
                float c    = floor(X / cell);
                for (int j = -1; j <= 1; j++)
                {
                    float id = c + j;
                    if (hash21(float2(id, 3.1)) > _Motes.y) continue;

                    float period = 8.0 + 7.0 * hash21(float2(id, 5.7));
                    float tt     = t / period + hash21(float2(id, 9.2));
                    float epoch  = floor(tt);
                    float p      = tt - epoch;
                    float e      = fmod(epoch, 97.0);   // keeps the hashes' inputs small in a long session
                    float e1     = hash21(float2(id, e + 1.3));
                    float e2     = hash21(float2(id, e + 7.9));
                    float e3     = hash21(float2(id, e + 13.7));

                    float mx = (id + 0.2 + 0.6 * e1) * cell + 0.22 * cell * sin(t * (0.23 + 0.17 * e3) + e2 * 6.283)
                             + 0.3 * cell * (p - 0.5);   // drifting downwind over its life
                    float my = lerp(_Motes.z, _Motes.w, e2) + 0.2 * (_Motes.w - _Motes.z) * p;
                    float r  = _MoteLook.x * (0.7 + 0.6 * e3);
                    float d  = length(float2(X - mx, Y - my));

                    float disc = saturate((r - d) / max(_MoteLook.y, 0.5) + 0.5);
                    float edge = smoothstep(r * 0.45, r, d) * disc;
                    float env  = sin(p * 3.14159265);
                    env *= env;
                    acc += _MoteColor.rgb * ((0.65 * disc + 0.45 * edge) * env * _MoteLook.z * (0.6 + 0.8 * e1));
                }
                return acc;
            }

            // How hard the wind is gusting at world x and time t, 0 (calm) to 1: an irregular pattern of
            // gusts, carried downwind (left to right) at about four world units a second, changing as it goes.
            float Gust (float x, float t)
            {
                float u = x * 0.12 - t * 0.5;
                float g = sin(u + 1.4 * sin(u * 0.41 + t * 0.11))
                        + 0.6 * sin(u * 1.93 + 0.8 + 0.9 * sin(t * 0.23))
                        + 0.4 * sin(u * 0.57 - t * 0.17 + 2.3);
                return smoothstep(0.0, 1.4, g);   // g spans about -2..2: long calms, gusts of every size
            }

            half4 frag (v2f i) : SV_Target
            {
                float X = i.uv.x * _Area.x;
                float Y = lerp(_Area.y, _Area.z, i.uv.y);
                float t = _Time.y;

                // Where the flutter and the motes are drawn from: along the row, or in the endless world along the
                // world, in the same reference px (see the header).
                float Xw = _WorldX > 0.5 ? i.wx * _Area.w : X;

                // The wind: a lean downwind that a gust deepens, buffeting that a gust stirs up, and a quick
                // flutter. Nothing moves at the roots; the tips move most.
                float sway   = saturate((Y - _Band.z) / _Band.w);
                sway *= sway;
                float gust   = Gust(i.wx + _Wind.z, t);
                float buffet = 0.6 * sin(t * 3.1 + i.wx * 0.7 + _Wind.z) + 0.4 * sin(t * 4.7 + i.wx * 1.6);
                float flut   = sin(Xw * 0.19 + t * 5.0 + 3.0 * sin(Xw * 0.031 + t * 0.6));
                float dx     = (_Wind.x * (0.25 + 0.75 * gust + 0.55 * buffet * (0.35 + 0.65 * gust))
                              + _Wind.y * flut * (0.5 + 0.5 * gust)) * sway * _Wind.w;

                // The silhouette. The texture clamps: below its band the bottom row (solid), above it the
                // top row (empty).
                float2 uv  = float2((X - dx) / _Area.x, (Y - _Band.x) / (_Band.y - _Band.x));
                half2  s   = tex2D(_MainTex, uv).rg;
                half   cov = s.r;
                half   up  = s.g;

                // Near black, by height on the screen alone — the same at any height in every row, so where
                // rows overlap they merge into one dark mass, and only their silhouettes against the city's light
                // show. The soft edges of the tips catch that light.
                float sy   = i.screen.y / i.screen.w;
                half3 col  = lerp(_Base.rgb, _Upper.rgb, smoothstep(_Ground.x, _Ground.y, sy)) * _Shade;
                half  edge = cov * (1.0 - cov) * 4.0;
                half3 rgb  = col * cov + _Rim.rgb * (edge * up * up * (0.6 + 0.8 * gust * _Wind.w));
                rgb += Motes(Xw, Y, t);
                return half4(rgb, cov);
            }
            ENDCG
        }
    }

    Fallback Off
}
