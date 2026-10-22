// The towers standing along the ground line (TowerField): near-black buildings backlit by the city —
// a faint rim of the city's glow along their edges, a sprinkling of small lit windows — so they read as
// part of the skyline rather than flat cut-outs. Every tower in the field uses this one material, the
// boundary tower included, so nothing about its look can single it out.
//
// Windows are a procedural grid in world space on whichever wall a pixel belongs to (x on walls facing the
// camera, z on side walls), so any model gets them without UVs of its own. The rooms behind them are lived
// in: lamps drifting brighter and dimmer, televisions, people passing, lights going off and on (see below).
//
// Built-in-style CG, no LightMode tag (URP's SRPDefaultUnlit pass), opaque. In Resources so player builds
// include it; TowerManager finds it by name.
Shader "JndUfo/Tower"
{
    Properties
    {
        _Color ("Body", Color) = (0.016, 0.018, 0.028, 1)
        [HDR] _RimColor ("Rim (city glow behind)", Color) = (0.34, 0.20, 0.13, 1)
        _RimPower ("Rim Power", Float) = 4.0
        [HDR] _WindowColor ("Window", Color) = (1.25, 0.92, 0.55, 1)
        _WindowLit ("Lit Fraction", Range(0, 1)) = 0.16
        _WindowCell ("Window Cell (world)", Float) = 0.42
        // 1 solid, 0 gone. Between the two the towers dissolve through a fine ordered dither — opaque
        // all the way, so there is no transparency to sort — which is how the row fades out and back in
        // over the reset between rounds.
        _Fade ("Fade", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            half4 _Color;
            half4 _RimColor;
            half  _RimPower;
            half4 _WindowColor;
            half  _WindowLit;
            half  _WindowCell;
            half  _Fade;

            // A 4x4 ordered (Bayer) dither threshold for a pixel, in [0, 1).
            float Bayer2 (float2 a)
            {
                a = floor(a);
                return frac(dot(a, float2(0.5, a.y * 0.75)));
            }
            float Bayer4 (float2 a) { return Bayer2(0.5 * a) * 0.25 + Bayer2(a); }

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldN   : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;
                o.pos      = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldN   = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float hash21 (float2 p)
            {
                p = frac(p * float2(233.34, 851.73));
                p += dot(p, p + 23.45);
                return frac(p.x * p.y);
            }

            half4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                clip(_Fade - Bayer4(i.pos.xy) - 0.001);

                float3 n = normalize(i.worldN) * (facing > 0 ? 1.0 : -1.0);
                float3 v = normalize(_WorldSpaceCameraPos - i.worldPos);

                // Backlight from the city: brightest where the surface turns away from the camera.
                half rim = pow(saturate(1.0 - abs(dot(n, v))), _RimPower);
                half3 col = _Color.rgb + _RimColor.rgb * rim;

                // Windows: a sparse grid of small rooms lit from inside, on whichever wall this is — and lived
                // in. Each lit room has its own lamp, warm, neutral or cool, drifting gently brighter and dimmer
                // as people move about; some are televisions, bluish, changing with the picture; now and then
                // someone passes a window and it dims for a moment; and every so often a room's light goes off
                // or comes on, fading over half a second. All from _Time, computed every frame — smooth at any
                // frame rate, nothing quick enough to flicker — and every tower alike, so none is singled out.
                float  u    = abs(n.x) > abs(n.z) ? i.worldPos.z : i.worldPos.x;
                float2 g    = float2(u, i.worldPos.y) / max(_WindowCell, 0.05);
                float2 id   = floor(g);
                float2 f    = abs(frac(g) - 0.5);
                float2 fw   = max(fwidth(g), 1e-4);
                float2 ins  = saturate((float2(0.22, 0.2) - f) / fw + 0.5);
                float  t    = _Time.y;

                float lit  = step(hash21(id), _WindowLit);
                float hDyn = hash21(id + 17.13);
                if (hDyn < 0.08)
                {
                    float period = 8.0 + 22.0 * hash21(id + 3.7);
                    float tt     = t + period * hash21(id + 23.9);
                    float epoch  = floor(tt / period);
                    float now    = step(hash21(id + fmod(epoch, 211.0) * 0.731), _WindowLit * 1.6);
                    float was    = step(hash21(id + fmod(epoch - 1.0, 211.0) * 0.731), _WindowLit * 1.6);
                    lit = lerp(was, now, smoothstep(0.0, 0.5, tt - epoch * period));
                }
                lit *= step(abs(n.y), 0.6);

                float h1 = hash21(id + 7.7);
                float h2 = hash21(id + 31.3);
                float h3 = hash21(id + 47.9);
                half3 lamp = h2 < 0.55 ? _WindowColor.rgb
                           : h2 < 0.85 ? half3(1.05, 1.0, 0.9)
                           :             half3(0.75, 0.92, 1.15);
                float life = 0.82 + 0.18 * (0.6 * sin(t * (0.35 + 0.6 * h3) + h1 * 40.0)
                                          + 0.4 * sin(t * (0.9 + 0.7 * h1) + h3 * 25.0));
                if (h3 < 0.12)
                {
                    // A television.
                    lamp = lerp(half3(0.55, 0.75, 1.25), half3(0.9, 0.95, 1.05), 0.5 + 0.5 * sin(t * 0.8 + h1 * 9.0));
                    life = 0.62 + 0.2 * sin(t * 2.3 + h1 * 50.0) + 0.12 * sin(t * 3.7 + h2 * 30.0)
                         + 0.06 * sin(t * 6.1 + h3 * 20.0);
                }
                float hP = hash21(id + 61.7);
                if (hP < 0.15)
                {
                    // Someone passing: a dip of about a second, eased in and out.
                    float period = 6.0 + 14.0 * hash21(id + 5.1);
                    float tt     = t + period * hash21(id + 9.9);
                    float local  = tt - floor(tt / period) * period;
                    life *= 1.0 - 0.6 * smoothstep(0.0, 0.35, local) * (1.0 - smoothstep(0.7, 1.3, local));
                }

                float glow = ins.x * ins.y * lit;
                col = lerp(col, lamp * (life * (0.55 + 0.65 * h1)), glow);

                return half4(col, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
