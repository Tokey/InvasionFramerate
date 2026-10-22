// The city's own lights, in two additive meshes (the near layer's, drawn sharp, and the rest, drawn out of
// focus with the far city — see Backdrop): aviation beacons on the tall buildings, rows of crown lights, a
// few neon bars, the warm glow of the streets at the foot of the nearest buildings, the lamps along the quay,
// the light-pollution glow along the horizon, and searchlights sweeping the sky.
//
// Per vertex (CitySkyline bakes it):
//   TEXCOORD0  x, y = position inside the quad (-1..1), z = shape, w = falloff exponent
//                shape 0  dot with a hot core           (beacons, crown lights)
//                shape 1  vertical band, bright at y=-1 (street glow, horizon glow)
//                shape 2  horizontal bar                (neon)
//                shape 3  searchlight beam, foot at y=-1
//   TEXCOORD1  rgb = HDR colour
//   TEXCOORD2  x = pulse rate in Hz (0 = steady), y = phase 0..1, z = pulse sharpness, w = flicker 0..1;
//              for a searchlight: sweep rate (rad/s), phase, sweep half-range and resting lean (rad)
//   TEXCOORD3  searchlights only: length, half-width at the foot, half-width at the far end (world)
//
// A searchlight's four corners all sit at its foot; the vertex shader swings the beam about that point from
// _Time — two slow sines at unrelated rates, so its sweep never quite repeats — and spreads the corners out
// along it. Everything that moves here is computed from _Time every frame, smooth at any frame rate.
//
// Depth-tested against the buildings, so a beacon behind a nearer tower is hidden by it. Nothing here
// knows where the hidden tower is. Built-in-style CG, no LightMode tag (URP's SRPDefaultUnlit pass), in
// Resources so player builds include it.
Shader "JndUfo/CityLights"
{
    Properties
    {
        // The waterfront, as in JndUfo/City: the lights drawn normally keep everything except what the
        // camera would see through the water, their mirrored copy keeps only that, dimmed, each light
        // drawn out into a vertical streak of _Streak world units either way — a light's reflection on
        // rippling water.
        _ClipY ("Water Surface (world Y)", Float) = 0
        _WaterEdgeZ ("Water's Far Edge (world Z)", Float) = 0
        _ClipSide ("Clip Side (+1 the lights, -1 their reflection, 0 off)", Float) = 0
        _ReflectDim ("Brightness (reflection)", Float) = 1
        _Streak ("Vertical Streak (reflection, world)", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        // Light adds colour and never coverage: the destination's alpha is left alone, so in the backdrop's
        // texture (where alpha is the skyline's coverage) a glow over the sky lets the sky show through.
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

            float _ClipY;
            float _WaterEdgeZ;
            float _ClipSide;
            half  _ReflectDim;
            float _Streak;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 uv0    : TEXCOORD0;
                float4 uv1    : TEXCOORD1;
                float4 uv2    : TEXCOORD2;
                float4 uv3    : TEXCOORD3;
            };

            struct v2f
            {
                float4 pos  : SV_POSITION;
                float4 uv0  : TEXCOORD0;
                float4 uv1  : TEXCOORD1;
                float4 uv2  : TEXCOORD2;
                float3 wpos : TEXCOORD3;
            };

            v2f vert (appdata v)
            {
                v2f o;
                float4 p = v.vertex;
                if (v.uv0.z > 2.5)
                {
                    // A searchlight, swung about its foot.
                    float  t     = _Time.y;
                    float  ang   = v.uv2.w + v.uv2.z * (0.65 * sin(t * v.uv2.x + v.uv2.y)
                                                      + 0.35 * sin(t * v.uv2.x * 1.618 + v.uv2.y * 2.3));
                    float2 dir   = float2(sin(ang), cos(ang));
                    float2 side  = float2(dir.y, -dir.x);
                    float  along = v.uv0.y * 0.5 + 0.5;
                    p.xy += dir * (v.uv3.x * along) + side * (lerp(v.uv3.y, v.uv3.z, along) * v.uv0.x);
                }
                else
                {
                    p.y += v.uv0.y * _Streak;   // each quad's corners pushed apart vertically
                }
                o.pos  = UnityObjectToClipPos(p);
                o.wpos = mul(unity_ObjectToWorld, p).xyz;
                o.uv0  = v.uv0;
                o.uv1  = v.uv1;
                o.uv2  = v.uv2;
                return o;
            }

            // As in JndUfo/City: true where the camera sees this point through the water.
            bool ThroughWater (float3 p)
            {
                float camY = _WorldSpaceCameraPos.y;
                if (p.y >= _ClipY || camY <= _ClipY) return false;
                float t = (camY - _ClipY) / (camY - p.y);
                return _WorldSpaceCameraPos.z + (p.z - _WorldSpaceCameraPos.z) * t <= _WaterEdgeZ;
            }

            half4 frag (v2f i) : SV_Target
            {
                if (_ClipSide != 0) clip(ThroughWater(i.wpos) == (_ClipSide < 0) ? 1 : -1);

                float2 p     = i.uv0.xy;
                float  shape = i.uv0.z;
                float  power = max(i.uv0.w, 0.01);

                float a = 0.0;
                if (shape < 0.5)
                {
                    float r = length(p);
                    a = pow(saturate(1.0 - r), power) + 2.0 * saturate(1.0 - r * 4.0);
                }
                else if (shape < 1.5)
                {
                    float t = saturate(p.y * 0.5 + 0.5);
                    a = pow(1.0 - t, power) * saturate((1.0 - abs(p.x)) * 6.0);
                }
                else if (shape < 2.5)
                {
                    a = pow(saturate(1.0 - abs(p.y)), power) * saturate((1.0 - abs(p.x)) * 10.0);
                }
                else
                {
                    // A searchlight: brightest down its middle, fading along its length, its foot eased in.
                    float along = p.y * 0.5 + 0.5;
                    return half4(i.uv1.rgb * (pow(saturate(1.0 - abs(p.x)), power) * pow(1.0 - along, 1.6)
                                              * smoothstep(0.0, 0.04, along) * _ReflectDim), 1.0);
                }

                float level = 1.0;
                float rate  = i.uv2.x;
                if (rate > 0.0)
                {
                    float ph = frac(_Time.y * rate + i.uv2.y);
                    level = pow(saturate(sin(ph * 6.2831853) * 0.5 + 0.5), max(i.uv2.z, 0.01));
                }
                float flicker = i.uv2.w;
                if (flicker > 0.0)
                {
                    // Slow: a fire's glow breathing on the smoke, not a strobe.
                    float n = 0.5 + 0.5 * sin(_Time.y * 2.3 + i.uv2.y * 40.0) * sin(_Time.y * 1.3 + i.uv2.y * 13.0);
                    level *= 1.0 - flicker * n;
                }

                return half4(i.uv1.rgb * (a * level * _ReflectDim), 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
