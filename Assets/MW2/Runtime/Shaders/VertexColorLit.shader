// Flat vertex-colour lighting for imported MW2 POLY meshes (built-in render pipeline).
// Material types 1 and 4 (TEXCOORD1 = low byte, high byte of the POLY material) pick their colour from a
// 16-entry palette row like Material_Shade (mercsw.dll 0x1001afb0) and its lighting 0x1002f543:
// shade = light * (low byte >> 1) / 0x440, clamped to 0..15, with the mission lights of Mw2Light.cginc; without them
// (mech preview outside a mission) the level is approximated as 0x7F * (ambient + diffuse) of Unity's light.
Shader "MW2/VertexColorLit"
{
    Properties
    {
        [HideInInspector] _ZWrite ("ZWrite", Float) = 1   // 0 for the sky backdrop (Mw2FollowCameraXZ)
        _Ambient ("Ambient", Range(0, 1)) = 0.45
        [NoScaleOffset] _Palette ("Palette (256x1)", 2D) = "black" {}
        [Toggle] _UsePalette ("Use palette rows", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Back   // one-sided POLY faces, as Render_Polygon 0x10056eb6 culls back faces
        ZWrite [_ZWrite]
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Mw2Fog.cginc"
            #include "Mw2Light.cginc"
            #include "Mw2Palette.cginc"
            #include "Mw2Enhanced.cginc"

            float _Ambient;
            float4 _LightColor0;
            float _UsePalette;
            float _ShadeFull;     // 1 on the cockpit shell: its lit rows are drawn at the brightest shade

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; float2 material : TEXCOORD1; float2 enhanced : TEXCOORD4; };
            struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; float4 color : COLOR; float2 material : TEXCOORD1; float2 enhanced : TEXCOORD4; float fogDepth : TEXCOORD5; float3 worldPos : TEXCOORD6; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.fogDepth = Mw2FogDepth(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.color = v.color;
                o.material = v.material;
                o.enhanced = v.enhanced;
                return o;
            }

            fixed4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                if (_Mw2Enhanced > 0.5)
                {
                    float2 data = i.enhanced;
                    if (_ShadeFull > 0.5) data.y = 1024; // SecondRender1001bd1e sets cockpit modelFlags400.
                    float mode = Mw2EnhancedBoundary(data);
                    if (mode == 0) discard;
                    if (mode > 0) return fixed4(Mw2PaletteColor(0), 1);
                }
                float3 n = normalize(i.normal) * (facing > 0 ? 1 : -1);
                float diffuse = saturate(dot(n, normalize(_WorldSpaceLightPos0.xyz)));
                float light = _Ambient + (1 - _Ambient) * diffuse;

                float high = floor(i.material.y + 0.5);
                float type = floor(high / 16);
                if (_UsePalette > 0.5 && (type == 1 || type == 4))
                {
                    float shade;
                    if (_Mw2LightParams.w > 0.5) shade = Mw2PaletteShade(n, i.worldPos, floor(i.material.x + 0.5));
                    else shade = clamp(floor(floor(127 * light) * floor(floor(i.material.x + 0.5) / 2) / 1088), 0, 15);
                    if (_ShadeFull > 0.5) shade = 15;
                    float index = (high - type * 16) * 16 + shade;
                    // The cockpit shell keeps the colours of the mission's palette (it does not turn with the infrared one).
                    float3 rowColor = _ShadeFull > 0.5 ? tex2Dlod(_Palette, float4((index + 0.5) / 256, 0.5, 0, 0)).rgb : Mw2PaletteColor(index);
                    return fixed4(Mw2Fog(rowColor, i.fogDepth), 1);
                }
                // Other flat types are not lit by Material_Shade: UV1.x is the palette index itself (PolyMeshBuilder.MaterialId),
                // read from the live palette; without a palette material the baked vertex colour stays.
                if (_UsePalette > 0.5 && type != 3)
                    return fixed4(Mw2Fog(Mw2PaletteColor(floor(i.material.x + 0.5)), i.fogDepth), 1);
                if (_Mw2LightParams.w > 0.5) light = 1;
                return fixed4(Mw2Fog(i.color.rgb * light, i.fogDepth), 1);
            }
            ENDCG
        }
    }
}
