// The out-of-focus city, laid in behind the play plane (Backdrop). A quad just inside the main camera's far
// plane, facing it, that shows the backdrop camera's blurred picture of the city pixel for pixel: it samples
// the texture at its own screen position, so the quad's exact size never matters.
//
// The texture is premultiplied (colour already weighted by coverage, additive lights with none), so it is
// laid over whatever is behind it — the sky, the stars, the clouds, drawn just before it — with
// One / OneMinusSrcAlpha. Depth-tested against the depth buffer, never writing to it: anything the main camera
// has drawn solid in front (the towers, the near buildings, the UFO) covers it, and everything drawn after it
// (lights, effects, the grass) goes over it.
//
// Queue Transparent-400: after the skybox and the sky's own quads (2510, 2520), before every ordinary
// transparent. Built-in-style CG, no LightMode tag (URP's SRPDefaultUnlit pass), in Resources so builds
// include it.
Shader "JndUfo/Backdrop"
{
    Properties
    {
        _MainTex ("Backdrop", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "Queue" = "Transparent-400" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Blend One OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float4 screen : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos    = UnityObjectToClipPos(v.vertex);
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.screen.xy / i.screen.w);
            }
            ENDCG
        }
    }

    Fallback Off
}
