// An aurora over the city (NightSky): soft curtains of green light hanging in the upper sky. Each has a
// glowing lower edge, a faint rose fringe right along it, that folds slowly and carries ripples along its
// length; above it, soft rays rising into violet, reaching higher in some places than others; and along it,
// bright stretches and faint ones that come and go, so it is never a band right across the sky. Additive,
// on one large quad behind the clouds, depth-tested, so the skyline hides it where it stands.
//
// Everything is a function of _Time, computed afresh every frame — smooth at any frame rate, nothing to
// update on the CPU. The motion is slow, as a real aurora's: folds drifting over tens of seconds, ripples
// running along the edge, rays sliding gently. Nothing flickers. It knows nothing of where the boundary is.
//
// Coordinates: _Span turns world x on the quad into widths of the screen at rest (so the curtains' shapes
// are set in screen terms, whatever the distance), and _VRange maps the quad's uv.y to height on the screen.
// Built-in-style CG, no LightMode tag (URP's SRPDefaultUnlit pass), in Resources so builds include it.
Shader "JndUfo/SkyAurora"
{
    Properties
    {
        _Low ("Colour, Lower Edge (linear)", Vector) = (0.10, 0.85, 0.42, 0)
        _High ("Colour, Upper Rays (linear)", Vector) = (0.32, 0.20, 0.80, 0)
        _Fringe ("Colour, Fringe Along the Edge (linear)", Vector) = (0.55, 0.12, 0.35, 0)
        _Brightness ("Brightness", Float) = 0.085
        _Speed ("Speed", Float) = 1
        _Span ("World x to Screen Widths", Float) = 0.0025
        _VRange ("Screen Height at the Quad's Bottom, Top", Vector) = (0.3, 1.12, 0, 0)
        _Edge ("Lower Edge (screen height from, to)", Vector) = (0.6, 0.74, 0, 0)
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

            float4 _Low;
            float4 _High;
            float4 _Fringe;
            float  _Brightness;
            float  _Speed;
            float  _Span;
            float4 _VRange;
            float4 _Edge;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float  wx  : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                o.wx  = mul(unity_ObjectToWorld, v.vertex).x;
                return o;
            }

            static const float TAU = 6.2831853;

            // One curtain at x (screen widths) and screen height sv; k sets it apart from the others. Returns
            // its light, and in h how far above its lower edge sv is.
            float Curtain (float x, float sv, float t, float k, out float h)
            {
                // The lower edge: slow folds, and a ripple running along it.
                float fold = 0.55 * sin(x * TAU * 0.9 + t * 0.06 + k * 1.7)
                           + 0.30 * sin(x * TAU * 2.1 - t * 0.09 + 1.3 + k * 2.9)
                           + 0.15 * sin(x * TAU * 4.7 + t * 0.31 + k * 4.1);
                h = sv - (lerp(_Edge.x, _Edge.y, 0.5 + 0.5 * fold) + 0.07 * k);

                // How high the rays reach here, changing slowly along the curtain.
                float reach = lerp(0.07, 0.2, 0.5 + 0.5 * sin(x * TAU * 1.7 + t * 0.07 + k * 3.3));

                // A soft lower edge with a glow beneath it; a fade upward.
                float lower = smoothstep(-0.03, 0.012, h);
                float body  = lower * exp(-max(h, 0.0) / reach) + 0.25 * exp(min(h, 0.0) / 0.035);

                // Rays: soft and irregular, sliding slowly along the curtain — a texture, never stripes.
                float r = 0.5 * sin(x * TAU * 23.0 + t * 0.35 + k * 5.0 + 1.5 * sin(x * TAU * 3.1 - t * 0.12))
                        + 0.3 * sin(x * TAU * 37.0 - t * 0.5 + 1.7)
                        + 0.2 * sin(x * TAU * 59.0 + t * 0.8 + k);
                float rays = 0.78 + 0.22 * r;

                // Bright stretches and faint ones along it, coming and going over tens of seconds.
                float patch = smoothstep(0.25, 0.95, 0.5 + 0.5 * sin(x * TAU * 0.55 + t * 0.04 + k * 4.0
                                                                   + 2.0 * sin(x * TAU * 0.23 - t * 0.03)));
                return body * rays * patch;
            }

            float3 Colour (float h)
            {
                return lerp(_Low.rgb, _High.rgb, saturate(h / 0.22)) + _Fringe.rgb * (0.5 * exp(-abs(h + 0.01) / 0.012));
            }

            half4 frag (v2f i) : SV_Target
            {
                float t  = _Time.y * _Speed;
                float x  = i.wx * _Span;
                float sv = lerp(_VRange.x, _VRange.y, i.uv.y);

                float h1, h2;
                float c1 = Curtain(x, sv, t, 0.0, h1);
                float c2 = Curtain(x + 0.41, sv, t * 0.85, 1.0, h2) * 0.55;   // a fainter one, higher

                float breathe = 0.85 + 0.15 * sin(t * 0.09);
                float horizon = smoothstep(_VRange.x + 0.05, _Edge.x, sv);   // nothing down in the city's glow
                return half4((Colour(h1) * c1 + Colour(h2) * c2) * (breathe * horizon * _Brightness), 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
