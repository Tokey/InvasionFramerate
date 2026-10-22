// Additive light for every glowing effect in the game: the laser and its sight, the shot's flash,
// sparks and embers, the UFO's trails, beacons, and the revealed tower. One shader, no textures —
// the falloff is computed from the mesh's UVs, so a LineRenderer, a TrailRenderer, a particle
// quad and a plain quad all get a soft edge from the same code.
//
//   _Shape 0  Dot   radial falloff from the quad's centre (particles, flares, beacons)
//          1  Line  falloff across uv.y — the width of a LineRenderer/TrailRenderer
//          2  Flat  no falloff (a whole mesh lit evenly, e.g. the revealed tower)
//          3  Ring  a soft band at the quad's rim (shockwave rings)
//
// _Color is HDR: anything above 1 is picked up by the scene's bloom. Vertex colour multiplies it,
// so particle colour-over-lifetime and line gradients work as usual; vertex alpha scales the light.
//
// Built-in-style CG with no LightMode tag, so URP draws it in its SRPDefaultUnlit pass. It sits in
// Resources so a player build always includes it — the scripts find it by name at runtime.
Shader "JndUfo/Glow"
{
    Properties
    {
        [HDR] _Color ("Color", Color) = (1, 1, 1, 1)
        _Shape ("Shape (0 dot, 1 line, 2 flat, 3 ring)", Float) = 0
        _Softness ("Softness (falloff exponent)", Float) = 1.5
        _RingWidth ("Ring Width (fraction of radius)", Float) = 0.25
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        // Colour only: the destination's alpha is left alone (in the backdrop's texture it is the
        // skyline's coverage, which the city's sparks must not add to — see Backdrop).
        Blend SrcAlpha One, Zero One
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

            half4 _Color;
            half  _Shape;
            half  _Softness;
            half  _RingWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                half4  color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                half4  color : COLOR;
                float2 uv    : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                o.uv    = v.uv;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                float2 p = i.uv * 2.0 - 1.0;
                half a;
                if (_Shape < 0.5)
                {
                    a = saturate(1.0 - length(p));
                }
                else if (_Shape < 1.5)
                {
                    a = saturate(1.0 - abs(p.y));
                }
                else if (_Shape < 2.5)
                {
                    a = 1.0;
                }
                else
                {
                    half w = max(_RingWidth, 0.001);
                    a = saturate(1.0 - abs(length(p) - (1.0 - w)) / w);
                }

                a = pow(a, max(_Softness, 0.01));

                half4 c = i.color;
                c.a *= a;
                return c;
            }
            ENDCG
        }
    }

    Fallback Off
}
