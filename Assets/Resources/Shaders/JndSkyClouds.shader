// Faint, soft clouds drifting slowly across the night sky (NightSky): fractal noise on one large quad
// behind the city, scrolling sideways and slowly changing shape, moonlit grey above and lit warm from
// below by the city near the horizon. Alpha-blended at a low opacity, so they veil the stars a little and
// never hide anything.
//
// A pure function of position and _Time: nothing to update on the CPU. Built-in-style CG, no LightMode
// tag (URP's SRPDefaultUnlit pass), in Resources so builds include it.
Shader "JndUfo/SkyClouds"
{
    Properties
    {
        _CloudColor ("Cloud (moonlit, high)", Color) = (0.20, 0.21, 0.28, 1)
        _LitColor ("Cloud (city-lit, low)", Color) = (0.42, 0.27, 0.20, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.16
        _Scale ("Noise Scale (per world unit)", Float) = 0.02
        _Stretch ("Horizontal Stretch", Float) = 2.6
        _Cover ("Coverage Threshold", Range(0, 1)) = 0.5
        _Softness ("Edge Softness", Range(0.01, 1)) = 0.28
        _Speed ("Drift (world units / s)", Float) = 2.0
        _Morph ("Shape Change Rate", Float) = 0.015
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Blend SrcAlpha OneMinusSrcAlpha
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

            half4 _CloudColor;
            half4 _LitColor;
            half  _Opacity;
            float _Scale;
            float _Stretch;
            half  _Cover;
            half  _Softness;
            float _Speed;
            float _Morph;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float2 w   : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                o.w   = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            float hash12 (float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float vnoise (float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(hash12(i),                 hash12(i + float2(1, 0)), u.x),
                            lerp(hash12(i + float2(0, 1)),  hash12(i + float2(1, 1)), u.x), u.y);
            }

            float fbm (float2 p)
            {
                float s = 0.0, a = 0.5;
                for (int k = 0; k < 4; k++)
                {
                    s += a * vnoise(p);
                    p  = p * 2.03 + 17.1;
                    a *= 0.5;
                }
                return s;
            }

            half4 frag (v2f i) : SV_Target
            {
                // Drift sideways in world units; the shape changes slowly as it goes.
                float2 w = i.w - float2(_Time.y * _Speed, 0.0);
                float2 p = float2(w.x / _Stretch, w.y) * _Scale;
                float  n = fbm(p + float2(0.0, _Time.y * _Morph));
                n = 0.6 * n + 0.4 * fbm(p * 1.7 - float2(_Time.y * _Morph, 3.1));

                float shape = smoothstep(_Cover, _Cover + _Softness, n);

                // Thickest in the middle of the sky, thinning toward the top and down into the glow.
                float band = smoothstep(0.02, 0.35, i.uv.y) * (1.0 - smoothstep(0.7, 1.0, i.uv.y));

                half3 col = lerp(_LitColor.rgb, _CloudColor.rgb, saturate(i.uv.y * 1.6));
                return half4(col, shape * band * _Opacity);
            }
            ENDCG
        }
    }

    Fallback Off
}
