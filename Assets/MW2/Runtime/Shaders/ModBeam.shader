// Laser beam of mod weapon effects (Mw2ModLaser): a bright core in an outline of its colour, with more strands, all
// wavering about the straight line and still at its two ends; going out (progress 1 -> 0) it thins and breaks up.
// Adapted from "Energy Beams" by pend00 (godotshaders.com/shader/energy-beams, CC0) for the built-in render pipeline:
// the strip is a mesh in world space whose vertices lie on the line (POSITION), carry the line's direction (NORMAL) and
// their place on the strip (TEXCOORD0: x along 0..1, y across 0..1); the vertex program turns each cross-section about
// the line to face the eye. Added to the picture (additive), faded by the mission's distance fog and near the eye.
Shader "MW2/ModBeam"
{
    Properties
    {
        _Color ("Core", Color) = (0.91, 1, 1, 1)
        _OutlineColor ("Halo", Color) = (0.5, 1, 0.96, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Blend One One
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "Mw2Fog.cginc"

            float4 _Color, _OutlineColor;
            float _HalfHeight;          // metres, half the strip's height (width x room / 2)
            float _Thickness;           // core, as a part of the strip's height (1 / room)
            float _OutlineThickness;    // halo, as a part of the strip's height
            float _Glow, _OutlineGlow;
            float _Energy;              // waver
            float _Roughness;           // octaves
            float _Frequency, _Speed;
            float _Strands, _StrandDifference;
            float _Progress;            // 1 whole, towards 0 thin and broken
            float _Thin, _BreakUp, _Dim;
            float _EdgeSize;
            float _NoiseScaleX;         // length / ripple
            float _Seed;
            float _Intensity;           // the flash's brightness (appearing)
            float4 _NearFade;           // x from, y to (metres from the eye)

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float depth : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                float3 onLine = v.vertex.xyz;
                float3 eye = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 across = cross(v.normal, eye - onLine);
                float len = length(across);
                float3 p = onLine;
                if (len > 1e-4) p += across / len * ((v.uv.y - 0.5) * 2 * _HalfHeight);
                o.pos = UnityObjectToClipPos(float4(p, 1));
                o.depth = -UnityObjectToViewPos(p).z;
                o.uv = v.uv;
                return o;
            }

            float random(float2 uv) { return frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453123); }

            float noise(float2 uv)
            {
                float2 i = floor(uv), f = frac(uv);
                float a = random(i), b = random(i + float2(1, 0)), c = random(i + float2(0, 1)), d = random(i + float2(1, 1));
                float2 blur = smoothstep(0, 1, f);
                return lerp(a, b, blur.x) + (c - a) * blur.y * (1 - blur.x) + (d - b) * blur.x * blur.y;
            }

            float fbm(float2 uv, float time)
            {
                float amp = 0.01 * _Energy * _Progress;
                float freq = _Frequency;
                float value = 0;
                [loop] for (int i = 0; i < 6; i++)
                {
                    if (i >= (int)_Roughness) break;
                    value += amp * noise(freq * float2(uv.x, uv.y + time));
                    amp *= 0.5;
                    freq *= 2;
                }
                return value;
            }

            float3 bolt(float2 uv, float time, float i)
            {
                float2 scale = float2(_NoiseScaleX, 1);
                // held to the line at its ends
                float falloff = smoothstep(0, _EdgeSize, uv.x) * smoothstep(0, _EdgeSize, 1 - uv.x);
                float clouds = fbm((uv + i + _Seed) * scale, time * _Speed) * falloff;
                float diff = abs(clouds - (uv.y - 0.5 + uv.y * falloff * 0.02 * _Energy * _Progress));
                // a second noise breaks the beam up as it goes out
                float clouds2 = fbm(uv * 2 * scale, time) * 5;
                diff += smoothstep(0, 0.8, clouds2) * 0.1 * _BreakUp * (1 - _Progress);
                // the first strand is of the full thickness, the others thinner
                float shrink = lerp(1, _Progress, _Thin);
                float t2 = 1 - (_Thickness / (min(i + _StrandDifference, 1) + (1 - _StrandDifference))) * shrink;
                float beam = saturate(smoothstep(t2, t2 + 0.005 * shrink + 1e-4, 1 - diff));
                float outline = t2 - _OutlineThickness * shrink;
                float edge = saturate(smoothstep(outline, outline + 0.04, 1 - diff));
                edge = saturate(edge - beam);
                return beam * _Color.rgb * _Glow + edge * _OutlineColor.rgb * _OutlineGlow;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float time = _Time.y;
                float3 c = 0;
                [loop] for (int k = 0; k < 4; k++)
                {
                    if (k >= (int)_Strands) break;
                    c = max(c, bolt(i.uv, time, k));
                }
                // right by the eye it would fill the view: a beam from a barrel beside the cockpit comes up out of nothing
                float nearFade = _NearFade.y > _NearFade.x ? smoothstep(_NearFade.x, _NearFade.y, i.depth) : step(_NearFade.x, i.depth);
                float fog = 1;
                if (_Mw2FogParams.z > 0.5) fog = saturate((1 - min(i.depth * _Mw2FogParams.x, 1)) * _Mw2FogParams.y);
                float bright = _Intensity * lerp(1, _Progress, _Dim);
                return fixed4(c * nearFade * fog * bright, 1);
            }
            ENDCG
        }
    }
}
