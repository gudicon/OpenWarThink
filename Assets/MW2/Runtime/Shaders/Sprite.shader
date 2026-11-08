// Material type 3 of POLY (Plugin_DrawPolygon 0x10053040 -> 0x1004ecb0 -> 0x1004ed0c): a screen-facing textured square.
// Of the polygon's vertices, the one with texture coordinates (0, 0) is C and the one with (0, b != 0) is E; both are
// projected and the square runs from C to E with half width |CE| / 2 on screen (constant 2 at 0x100881c8), corners
// (0,0) C, (0,1) E, (1,1) E, (1,0) C of the texture (mirrored in u when b < 0), vertex colour 200,200,200 and alpha 0x80,
// C's corners at C's depth and E's at E's. Each mesh vertex carries C (position), E (TEXCOORD2) and the corner's
// side (TEXCOORD3.x: +1 or -1) and end (TEXCOORD3.y: 0 = C, 1 = E).
// The vertex alpha 0x80 is not applied (the sprites come out washed out with it; the zglide alpha source of this draw
// is not decoded); the depth is pulled towards the camera by |CE| so that a wall at the explosion point does not cut
// the square in half (the original's sprite depth is not decoded).
// Approximation: the corners are C/E +- the perpendicular of CE on screen; the decompiled corner formula matches this
// for an upright CE (all explosion sprites) and looks skewed otherwise.
Shader "MW2/Sprite"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [NoScaleOffset] _Palette ("Palette (256x1)", 2D) = "black" {}
        [HideInInspector] _Paletted ("Texture holds palette indices", Float) = 0
        _Color ("Vertex colour", Color) = (0.784, 0.784, 0.784, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Mw2Fog.cginc"
            #include "Mw2Palette.cginc"
            #include "Mw2Enhanced.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _Color;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float3 e : TEXCOORD2; float2 corner : TEXCOORD3;
                float2 enhanced : TEXCOORD4; float3 original : TEXCOORD5; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float fogDepth : TEXCOORD5; float enhanced : TEXCOORD6; };

            v2f vert (appdata v)
            {
                v2f o;
                float4 c = UnityObjectToClipPos(v.vertex);
                float4 e = UnityObjectToClipPos(float4(v.e, 1));
                // Screen pixels of C and E.
                float2 cs = c.xy / c.w * 0.5 * _ScreenParams.xy;
                float2 es = e.xy / e.w * 0.5 * _ScreenParams.xy;
                float2 d = es - cs;
                float2 perp = float2(-d.y, d.x) * 0.5 * v.corner.x;
                float4 end = v.corner.y > 0.5 ? e : c;
                float2 s = (v.corner.y > 0.5 ? es : cs) + perp;
                // Depth pulled towards the camera by the sprite's length, so a wall at the point does not cut it.
                float3 vc = UnityObjectToViewPos(v.vertex);
                float3 ve = UnityObjectToViewPos(v.e);
                float3 vEnd = v.corner.y > 0.5 ? ve : vc;
                vEnd.z += length(ve - vc);
                float4 near = mul(UNITY_MATRIX_P, float4(vEnd, 1));
                o.pos = float4(s / (0.5 * _ScreenParams.xy) * near.w, near.z, near.w);
                o.uv = v.uv;
                o.fogDepth = Mw2FogDepth(v.corner.y > 0.5 ? float4(v.e, 1) : v.vertex);
                o.enhanced = Mw2EnhancedBoundary(v.enhanced);
                if (_Mw2Enhanced > 0.5 && o.enhanced >= 0)
                    o.pos = UnityObjectToClipPos(float4(v.original, 1));
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                if (_Mw2Enhanced > 0.5 && i.enhanced >= 0)
                {
                    if (i.enhanced == 0) discard;
                    return fixed4(Mw2PaletteColor(0), 1);
                }
                fixed4 tex = Mw2SampleMain(_MainTex, _MainTex_TexelSize, i.uv);
                clip(tex.a - 1.0 / 255.0);
                return fixed4(Mw2Fog(tex.rgb * _Color.rgb, i.fogDepth), tex.a * _Color.a);
            }
            ENDCG
        }
    }
}
