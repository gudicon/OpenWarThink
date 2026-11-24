// Flat vertex-colour lighting for imported MW2 POLY meshes (built-in render pipeline).
// Material type 4 (0x4000) has TEXCOORD1.x = the palette index of the vertex (POLY vertex value a): Plugin_DrawPolygon
// 0x10053040 draws it unlit and unfogged with the colour of that index in the mission's first palette, interpolated over the face.
// Material type 1 (TEXCOORD1 = low byte, high byte of the POLY material) picks its colour from a
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
        [NoScaleOffset] _LightTable ("Light table palette (256x1, PALG[0])", 2D) = "black" {}
        [Toggle] _HasLightTable ("Has light table", Float) = 0
    }
    SubShader
    {
        Tags { "Mw2Depth" = "Opaque" "RenderType" = "Opaque" }
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
            #include "Mw2ExtraLight.cginc"
            #include "Mw2Modern.cginc"

            float _Ambient;
            float4 _LightColor0;
            float _UsePalette;
            sampler2D _LightTable;
            float _HasLightTable;
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
                // Type 4: colour of the vertex = light table at its index a (Plugin_DrawPolygon 0x10053040).
                if (_HasLightTable > 0.5 && floor(floor(v.material.y + 0.5) / 16) == 4)
                    o.color = float4(tex2Dlod(_LightTable, float4((floor(v.material.x + 0.5) + 0.5) / 256, 0.5, 0, 0)).rgb, 1);
                o.material = v.material;
                o.enhanced = v.enhanced;
                return o;
            }

            fixed4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                if (_Mw2EnvPass > 0.5 && _ShadeFull > 0.5) discard;   // graphics mods only: the cockpit is not in the probe
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
                float3 extraLight = _ShadeFull > 0.5 ? 0 : Mw2ExtraLight(n, i.worldPos);   // port extensions only (0 without them)

                float high = floor(i.material.y + 0.5);
                float type = floor(high / 16);
                // Type 4: the table 0x102880c0 is filled once from the mission's first palette (Palette_FillLightTable
                // 0x10004cc0), so it does not follow the live palette; the vertex colours are interpolated RGB.
                // UV1.x is the vertex index a; Glide interpolates the RGB, so the table is read per vertex in vert().
                // Graphics mods only (Mw2Modern.cginc): types 1 and 4 lit as surfaces. The albedo of a type 1 face is its row
                // at the shade full light gives it (0x1002f543 with level 127); type 4 keeps its vertex colour.
                bool modern = Mw2ModernFor(_ShadeFull) && (type == 1 || type == 4 || _UsePalette < 0.5);
                if (modern)
                {
                    float3 albedo = i.color.rgb;
                    if (_UsePalette > 0.5 && type == 1)
                    {
                        float full = clamp(floor(127 * floor(floor(i.material.x + 0.5) / 2) / 1088), 0, 15);
                        albedo = Mw2PaletteColor((high - type * 16) * 16 + full);
                        // The shell keeps the colour the cockpit pass gives it: the row's brightest shade.
                        if (_ShadeFull > 0.5) albedo = Mw2PaletteColor((high - type * 16) * 16 + 15);
                    }
                    // The shell sits inside the player's own mech, whose body would shade it from the sun.
                    float shadow = _ShadeFull > 0.5 ? 1 : Mw2SunShadow(i.worldPos, n);
                    float3 lit = Mw2ModernLightFor(albedo, n, i.worldPos, Mw2ModernSurfaceFor(i.enhanced, 0, _ShadeFull), shadow, _ShadeFull);
                    return fixed4(Mw2Fog(lit + albedo * extraLight, i.fogDepth), 1);
                }
                if (_UsePalette > 0.5 && type == 4) return fixed4(i.color.rgb * (1 + extraLight), 1);
                if (_UsePalette > 0.5 && type == 1)
                {
                    float shade;
                    if (_Mw2LightParams.w > 0.5) shade = Mw2PaletteShade(n, i.worldPos, floor(i.material.x + 0.5));
                    else shade = clamp(floor(floor(127 * light) * floor(floor(i.material.x + 0.5) / 2) / 1088), 0, 15);
                    if (_ShadeFull > 0.5) shade = 15;
                    float index = (high - type * 16) * 16 + shade;
                    // The cockpit shell keeps the colours of the mission's palette (it does not turn with the infrared one).
                    float3 rowColor = _ShadeFull > 0.5 ? tex2Dlod(_Palette, float4((index + 0.5) / 256, 0.5, 0, 0)).rgb : Mw2PaletteColor(index);
                    return fixed4(Mw2Fog(rowColor * (1 + extraLight), i.fogDepth), 1);
                }
                // Other flat types are not lit by Material_Shade: UV1.x is the palette index itself (PolyMeshBuilder.MaterialId),
                // read from the live palette; without a palette material the baked vertex colour stays.
                if (_UsePalette > 0.5 && type != 3)
                    return fixed4(Mw2Fog(Mw2PaletteColor(floor(i.material.x + 0.5)) * (1 + extraLight), i.fogDepth), 1);
                if (_Mw2LightParams.w > 0.5) light = 1;
                return fixed4(Mw2Fog(i.color.rgb * (light + extraLight), i.fogDepth), 1);
            }
            ENDCG
        }
    }
}
