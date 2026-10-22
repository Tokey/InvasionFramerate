// The backdrop's depth-of-field blur (Backdrop): a separable Gaussian, run on the city the backdrop camera
// has just drawn — horizontally into a second texture (pass 0), then vertically back (pass 1).
//
// The texture holds the city with premultiplied coverage in alpha (buildings 1, the sky they stand against
// 0, additive lights 0), so blurring all four channels together softens the skyline's edges correctly:
// Backdrop.shader lays the result over the sky with that alpha.
//
// _Sigma is the blur in texels; the kernel reaches three sigmas, up to twelve texels each way. Built-in-style
// CG, used only through CommandBuffer.Blit; in Resources so builds include it.
Shader "Hidden/JndUfo/BackdropBlur"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
        _Sigma ("Sigma (texels)", Float) = 1
    }

    SubShader
    {
        ZTest Always
        ZWrite Off
        Cull Off

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4    _MainTex_TexelSize;
        float     _Sigma;

        half4 Blur (float2 uv, float2 dir)
        {
            float s = max(_Sigma, 0.05);
            float n = min(ceil(s * 3.0), 12.0);
            float k = -0.5 / (s * s);
            float2 step = dir * _MainTex_TexelSize.xy;

            // tex2Dlod: no derivatives inside the loop (the texture has no mip levels anyway).
            half4 acc  = tex2Dlod(_MainTex, float4(uv, 0.0, 0.0));
            float wsum = 1.0;
            [loop] for (float i = 1.0; i <= n; i += 1.0)
            {
                float  w = exp(i * i * k);
                float2 o = step * i;
                acc  += (tex2Dlod(_MainTex, float4(uv + o, 0.0, 0.0)) + tex2Dlod(_MainTex, float4(uv - o, 0.0, 0.0))) * w;
                wsum += 2.0 * w;
            }
            return acc / wsum;
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            half4 frag (v2f_img i) : SV_Target { return Blur(i.uv, float2(1.0, 0.0)); }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            half4 frag (v2f_img i) : SV_Target { return Blur(i.uv, float2(0.0, 1.0)); }
            ENDCG
        }
    }

    Fallback Off
}
