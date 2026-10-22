// The night city's buildings: one opaque, unlit pass with every window drawn procedurally, so a
// whole depth layer of the skyline — hundreds of buildings, thousands of windows — is one mesh and
// one draw call, with no textures to sample.
//
// Everything a building needs rides on its vertices (CitySkyline bakes it):
//   COLOR      rgb = facade colour (face shading baked in), a = how much of the layer's haze reaches
//              this vertex (1 at the street, less up the facade — the haze sits low)
//   TEXCOORD0  x, y = facade coordinates in world units (u across the face, v up), z = the roofline's
//              v above this point, w = 1 if this face has windows
//   TEXCOORD1  window cell width, cell height, lit fraction, building seed
//   TEXCOORD2  glass width and height as fractions of the cell, warm(0)..cool(1) tone, brightness
//   TEXCOORD3  damage 0..1, fraction of windows burning near the break, depth of the scorched band,
//              w = window level (1 everywhere except toward the foot of the foreground rooftops, where
//              the windows fade out with the facade toward the bottom edge of the screen)
//
// The city is lived in: a few windows switch on or off now and then, each fading over most of a second,
// never popping; a handful of televisions glow, drifting slowly brighter and dimmer; now and then someone
// walks past a lit window; and the fires on damaged buildings glow. All of it is computed from _Time every
// frame, so it moves smoothly at any frame rate. Nothing flickers: the whole scene sits behind the UFO for
// a frame-rate study, and anything that flickers draws the eye. None of it knows where the tower is.
//
// Built-in-style CG with no LightMode tag, so URP draws it in its SRPDefaultUnlit pass. In Resources so
// player builds include it; CitySkyline finds it by name.
Shader "JndUfo/City"
{
    Properties
    {
        [HDR] _WindowWarm ("Window Warm", Color) = (1.55, 1.12, 0.62, 1)
        [HDR] _WindowCool ("Window Cool", Color) = (0.85, 1.02, 1.30, 1)
        [HDR] _FireColor ("Fire", Color) = (3.0, 1.15, 0.28, 1)
        _GlassColor ("Dark Glass", Color) = (0.030, 0.038, 0.060, 1)
        _RimColor ("Roof Rim (moonlight)", Color) = (0.10, 0.12, 0.19, 1)
        _HazeColor ("Haze", Color) = (0.11, 0.105, 0.18, 1)
        _Haze ("Haze Amount", Range(0, 1)) = 0
        _WindowLevel ("Window Brightness", Float) = 1

        // The waterfront. The water's surface lies at _ClipY, out to _WaterEdgeZ. A layer drawn normally
        // (_ClipSide +1) keeps everything except what the camera would see THROUGH the water; its
        // mirrored copy, the reflection (_ClipSide -1), keeps only that, at _ReflectDim brightness, its
        // windows wavering by _Ripple world units. So the buildings can reach down out of sight behind
        // the quay — no ground has to show between them — and still never overlap their own reflection.
        // 0 clips nothing.
        _ClipY ("Water Surface (world Y)", Float) = 0
        _WaterEdgeZ ("Water's Far Edge (world Z)", Float) = 0
        _ClipSide ("Clip Side (+1 the city, -1 its reflection, 0 off)", Float) = 0
        _ReflectDim ("Brightness (reflection)", Float) = 1
        _Ripple ("Window Ripple (reflection, world)", Float) = 0
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
            #include "UnityCG.cginc"

            half4 _WindowWarm;
            half4 _WindowCool;
            half4 _FireColor;
            half4 _GlassColor;
            half4 _RimColor;
            half4 _HazeColor;
            half  _Haze;
            half  _WindowLevel;
            float _ClipY;
            float _WaterEdgeZ;
            float _ClipSide;
            half  _ReflectDim;
            float _Ripple;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float4 uv0    : TEXCOORD0;
                float4 uv1    : TEXCOORD1;
                float4 uv2    : TEXCOORD2;
                float4 uv3    : TEXCOORD3;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float4 color : COLOR;
                float4 uv0   : TEXCOORD0;
                float4 uv1   : TEXCOORD1;
                float4 uv2   : TEXCOORD2;
                float4 uv3   : TEXCOORD3;
                float3 wpos  : TEXCOORD4;   // world position, for the water
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv0   = v.uv0;
                o.uv1   = v.uv1;
                o.uv2   = v.uv2;
                o.uv3   = v.uv3;
                o.wpos  = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float hash21 (float2 p)
            {
                p = frac(p * float2(233.34, 851.73));
                p += dot(p, p + 23.45);
                return frac(p.x * p.y);
            }

            // True where the camera sees this point through the water: below the surface, on a line of
            // sight that meets the surface over the water rather than beyond its far edge.
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

                float2 uvw       = i.uv0.xy;
                // A reflection's windows waver, slowly, as if seen through moving water.
                uvw.x += _Ripple * sin(i.wpos.y * 4.7 + _Time.y * 0.9) * sin(i.wpos.y * 1.3 - _Time.y * 0.4);
                float  roofV     = i.uv0.z;
                float  windowsOn = i.uv0.w;
                float2 cell      = max(i.uv1.xy, 0.05);
                float  litFrac   = i.uv1.z;
                // A whole number, rounded back to one: interpolation across the triangle leaves it a rounding
                // error off here and there, and the hashes below magnify any difference into a different
                // window — speckled glass that crawls as the camera moves.
                float  seed      = floor(i.uv1.w + 0.5);

                // ── Window grid ────────────────────────────────────────────────
                float2 g  = uvw / cell;
                float2 id = floor(g);
                float2 f  = frac(g);
                float2 fw = max(fwidth(g), 1e-4);

                // The glass rectangle, anti-aliased over about a pixel.
                float2 halfGlass = i.uv2.xy * 0.5;
                float2 d         = abs(f - 0.5);
                float2 inside    = saturate((halfGlass - d) / fw + 0.5);
                float  glass     = inside.x * inside.y * windowsOn;

                // Lit or dark. People come and go: now and then a window in forty or so reconsiders —
                // every twenty seconds to over a minute, each on its own clock — and fades to its new state
                // over most of a second, the way a room's light reads from far away, rather than popping.
                float on   = step(hash21(id + seed), litFrac);
                float hDyn = hash21(id + seed + 17.13);
                if (hDyn < 0.025)
                {
                    float period = 20.0 + 50.0 * hash21(id + seed + 3.7);
                    float tt     = _Time.y + period * hash21(id + seed + 23.9);
                    float epoch  = floor(tt / period);
                    float onNow  = step(hash21(id + seed + fmod(epoch, 211.0) * 0.731), litFrac);
                    float onWas  = step(hash21(id + seed + fmod(epoch - 1.0, 211.0) * 0.731), litFrac);
                    on = lerp(onWas, onNow, smoothstep(0.0, 0.8, tt - epoch * period));
                }

                float bright = (0.55 + 0.75 * hash21(id + seed + 5.31)) * i.uv2.w;
                float tone   = saturate(i.uv2.z + (hash21(id + seed + 9.77) - 0.5) * 0.7);
                half3 win    = lerp(_WindowWarm.rgb, _WindowCool.rgb, tone) * bright;

                // A television here and there: a cooler, dimmer light that drifts slowly, brighter and
                // dimmer, bluer and whiter, as the picture changes — over seconds, never a flicker.
                float hTv = hash21(id + seed + 29.1);
                if (hTv < 0.02)
                {
                    float tvT = _Time.y + hTv * 400.0;
                    float tvL = 0.5 + 0.12 * sin(tvT * 1.3) + 0.08 * sin(tvT * 2.9 + 1.7) + 0.06 * sin(tvT * 0.7 + 4.1);
                    win = lerp(_WindowCool.rgb, _WindowWarm.rgb * 0.8, 0.25 + 0.25 * sin(tvT * 0.37)) * tvL;
                }

                // Someone crossing the room: now and then a dark figure walks past a lit window, from one
                // side to the other over three to six seconds. Measured in glass heights from the sill, so
                // the figure keeps its proportions whatever the window's shape: head and shoulders, the
                // rest below the sill.
                float hP = hash21(id + seed + 61.7);
                if (hP < 0.08)
                {
                    float period = 9.0 + 16.0 * hash21(id + seed + 7.3);
                    float tt     = _Time.y + period * hash21(id + seed + 11.9);
                    float k      = fmod(floor(tt / period), 97.0);
                    float local  = tt - floor(tt / period) * period;
                    float dur    = 3.0 + 3.0 * hash21(id + seed + k * 1.37);
                    if (local < dur)
                    {
                        float2 gh   = i.uv2.xy * 0.5 * cell;                // the glass's half-size, world
                        float  H    = 2.0 * gh.y;
                        float2 q    = (f - 0.5) * cell;                     // from the window's centre, world
                        float  sx   = q.x / H;
                        float  sy   = (q.y + gh.y) / H;                     // glass heights up from the sill
                        float  span = gh.x / H + 0.3;
                        float  cx   = lerp(-span, span, local / dur);
                        if (hash21(id + seed + k * 2.11) < 0.5) cx = -cx;   // either way across

                        float2 pa = float2(sx - cx, sy + 0.3);              // torso: a capsule up to the shoulders
                        float  hc = saturate(pa.y / 0.85);
                        float  dT = length(pa - float2(0.0, 0.85 * hc)) - 0.17;
                        float  dH = length(float2(sx - cx, sy - 0.74)) - 0.1; // head
                        float  px = max(fw.y * cell.y / H, 1e-4);           // a pixel, in glass heights
                        on *= 1.0 - 0.85 * saturate(0.5 - min(dT, dH) / px);
                    }
                }

                // ── Damage ────────────────────────────────────────────────────
                float damage  = i.uv3.x;
                float fireFr  = i.uv3.y;
                float scorchD = max(i.uv3.z, 0.01);
                float below   = roofV - uvw.y;                       // distance below the roofline
                float nearTop = saturate(1.0 - below / scorchD);     // 1 at the break, 0 a scorch-depth down
                float fire    = 0.0;
                float flick   = 1.0;
                if (damage > 0.001)
                {
                    float hf = hash21(id + seed + 41.3);
                    fire  = step(hf, fireFr * nearTop) * glass;
                    on   *= step(nearTop * damage, 0.3);             // blown-out windows near the break
                    // A slow, shallow glow — embers breathing, not flames strobing.
                    flick = 0.8 + 0.2 * sin(_Time.y * (1.1 + hf * 1.2) + id.x * 1.7) * sin(_Time.y * (0.6 + hf * 0.5) + id.y);
                }

                // ── Facade ────────────────────────────────────────────────────
                half3 facade = i.color.rgb;
                float rim    = saturate(1.0 - below / 0.6);
                facade      += _RimColor.rgb * rim * rim * 0.6;
                facade      *= 1.0 - 0.7 * damage * nearTop;

                half  level = saturate(i.uv3.w) * _WindowLevel;
                half3 col = lerp(facade, facade * 0.55 + _GlassColor.rgb * saturate(i.uv3.w), glass);
                col = lerp(col, win * level, glass * on);
                col = lerp(col, _FireColor.rgb * flick, fire);

                // Embers along a broken roofline.
                col += _FireColor.rgb * (0.35 * damage) * pow(rim, 6.0) * (0.75 + 0.25 * sin(_Time.y * 1.3 + uvw.x * 2.1));

                // Where the cells shrink below a few pixels (the far layers) the grid would shimmer
                // as the camera pans; fade it to its average instead.
                float coarse = saturate((max(fw.x, fw.y) - 0.22) * 3.0);
                half3 avg    = lerp(facade, win * level, saturate(litFrac * i.uv2.x * i.uv2.y * windowsOn));
                col = lerp(col, avg, coarse);

                // ── Haze ──────────────────────────────────────────────────────
                col = lerp(col, _HazeColor.rgb, saturate(_Haze * i.color.a));
                return half4(col * _ReflectDim, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
