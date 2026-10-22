// The water in front of the embankment (CitySkyline's waterfront). A flat surface at the waterline,
// alpha-blended over the city's reflection — the skyline drawn again, mirrored under the water — so how
// much of the reflection shows is this surface's opacity: nearly opaque right in front of the camera,
// more and more of a mirror toward the embankment, where the view grazes it, as real water does.
//
// Slow ripples travel across it, broken up along its length, thinning the reflection on their troughs
// and catching a faint glint of the city's light on their crests. Where the ripples would shrink below a
// couple of pixels (far out, at grazing angles) they fade away rather than shimmer.
//
// A pure function of position and _Time. Built-in-style CG, no LightMode tag (URP's SRPDefaultUnlit
// pass), in Resources so builds include it.
Shader "JndUfo/Water"
{
    Properties
    {
        _Deep ("Deep Water", Color) = (0.006, 0.009, 0.016, 1)
        _AlphaNear ("Opacity Near (in front of the camera)", Range(0, 1)) = 0.9
        _AlphaFar ("Opacity Far (at the embankment)", Range(0, 1)) = 0.4
        _NearZ ("Near Z (world)", Float) = -60
        _FarZ ("Far Z (embankment, world)", Float) = -2.5
        [HDR] _Glint ("Glint", Color) = (0.55, 0.42, 0.3, 1)
        _RippleAlpha ("Ripple Strength", Range(0, 1)) = 0.22
        _RippleScale ("Ripple Frequency (per world unit)", Float) = 2.2
        _RippleSpeed ("Ripple Speed", Float) = 0.8
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

            half4 _Deep;
            half  _AlphaNear;
            half  _AlphaFar;
            float _NearZ;
            float _FarZ;
            half4 _Glint;
            half  _RippleAlpha;
            float _RippleScale;
            float _RippleSpeed;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos  : SV_POSITION;
                float3 wpos : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                // 0 in front of the camera, 1 at the embankment.
                float far = saturate((i.wpos.z - _NearZ) / (_FarZ - _NearZ));

                // Travelling ripples, broken up along the shore so they never read as stripes.
                float zr = i.wpos.z * _RippleScale;
                float w1 = sin(zr + _Time.y * _RippleSpeed + sin(i.wpos.x * 0.23 + _Time.y * 0.31) * 1.7);
                float w2 = sin(zr * 2.3 - _Time.y * _RippleSpeed * 1.4 + i.wpos.x * 0.11);
                float ripple = w1 * 0.6 + w2 * 0.4;

                // Fade them out where a ripple gets thinner than a couple of pixels.
                float bandPx = max(fwidth(zr) * 2.3, 1e-4);
                float aa     = saturate(1.2 - bandPx * 1.5);
                ripple *= aa;

                float a   = lerp(_AlphaNear, _AlphaFar, far) + ripple * _RippleAlpha * (0.35 + 0.65 * far);
                half3 col = _Deep.rgb + _Glint.rgb * pow(saturate(ripple), 6.0) * far;
                return half4(col, saturate(a));
            }
            ENDCG
        }
    }

    Fallback Off
}
