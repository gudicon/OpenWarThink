// Textured, vertex-coloured flat lighting for MW2 scenery (built-in render pipeline).
// Textured polygons use white vertex colour; flat-colour polygons use the white default texture.
Shader "MW2/TexturedLit"
{
    Properties
    {
        [HideInInspector] _ZWrite ("ZWrite", Float) = 1   // 0 for the sky backdrop (Mw2FollowCameraXZ)
        [HideInInspector] _Unlit ("Unlit", Float) = 0     // 1 for the sky object: Material_Shade 0x1001afb0 gives it white
        [HideInInspector] _Ground ("Ground", Float) = 0   // 1 for the mission ground: lit by FUN_1005503e
        _MainTex ("Texture", 2D) = "white" {}
        [NoScaleOffset] _Palette ("Palette (256x1)", 2D) = "black" {}
        [HideInInspector] _Paletted ("Texture holds palette indices", Float) = 0
        _Ambient ("Ambient", Range(0, 1)) = 0.45
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

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;
            float _Ambient;
            float _Unlit;
            float _ShadeFull;     // 1 on the cockpit shell: drawn at full light
            float _Ground;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; float2 uv : TEXCOORD0; float2 enhanced : TEXCOORD4; };
            struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; float2 uv : TEXCOORD1; float4 color : COLOR; float2 enhanced : TEXCOORD4; float fogDepth : TEXCOORD5; float3 worldPos : TEXCOORD6; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.fogDepth = Mw2FogDepth(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                o.enhanced = v.enhanced;
                return o;
            }

            fixed4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                if (_Mw2Enhanced > 0.5 && _Ground < 0.5)
                {
                    float2 data = i.enhanced;
                    if (_ShadeFull > 0.5) data.y = 1024;
                    float mode = Mw2EnhancedBoundary(data);
                    if (mode == 0) discard;
                    if (mode > 0) return fixed4(Mw2PaletteColor(0), 1);
                }
                float3 n = normalize(i.normal) * (facing > 0 ? 1 : -1);
                float diffuse = saturate(dot(n, normalize(_WorldSpaceLightPos0.xyz)));
                float3 light = _Ambient + (1 - _Ambient) * diffuse;
                if (_Mw2LightParams.w > 0.5) light = Mw2TextureLight(n, i.worldPos);
                if (_Unlit > 0.5) light = _Mw2Infrared > 0.5 ? float3(1, 170.0 / 255.0, 0) : 1;   // the sky dome, Material_Shade 0x1001afb0
                if (_ShadeFull > 0.5) light = 1;
                if (_Ground > 0.5 && _Mw2LightParams.w > 0.5) light = Mw2GroundLight();
                fixed4 tex = Mw2SampleMain(_MainTex, _MainTex_TexelSize, i.uv);
                return fixed4(Mw2Fog(tex.rgb * i.color.rgb * light, i.fogDepth), 1);
            }
            ENDCG
        }
    }
}
