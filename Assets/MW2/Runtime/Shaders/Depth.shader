// Replacement shader of a graphics mod's lighting (Mw2Graphics, never drawn without such a mod). Target 0: the distance
// from the camera along its view divided by its far plane, for the sun's shadow map, the ambient occlusion, the sun rays
// and the screen-space reflections. Target 1 (bound only for the view, Mw2GraphicsDepth.DrawView): the normal in view
// space with z towards the view (x, y, -z of Unity's view space, 0..1 packed) and how strongly the surface mirrors,
// from the surfaces of Mw2Surface.cginc (unit parts by class, structures, the ground). It replaces the world shaders
// by their "Mw2Depth" tag. The sky (_Unlit), the backdrop without depth (_ZWrite 0) and the cockpit shell (_ShadeFull)
// are left out; textures with their own colours are cut where they are see-through.
// Port addition with no counterpart in the original program.
Shader "MW2/Depth"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex;
    float4 _MainTex_ST;
    float _Paletted, _Unlit, _ZWrite, _ShadeFull, _Ground, _Mw2OpaqueUnitGlass;
    #include "Mw2Surface.cginc"

    struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float2 enhanced : TEXCOORD4; };
    struct v2f { float4 pos : SV_POSITION; float depth : TEXCOORD0; float2 uv : TEXCOORD1; float3 normal : TEXCOORD2; float mirror : TEXCOORD3; float solid : TEXCOORD4; };
    struct Out { float4 depth : SV_Target0; float4 surface : SV_Target1; };

    v2f vert(appdata v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.depth = -UnityObjectToViewPos(v.vertex.xyz).z * _ProjectionParams.w;
        o.uv = TRANSFORM_TEX(v.uv, _MainTex);
        o.normal = mul((float3x3)UNITY_MATRIX_V, UnityObjectToWorldNormal(v.normal));
        float4 s = Mw2ModernSurface(v.enhanced, _Ground);
        float smooth = 1 - saturate(s.y);
        o.mirror = lerp(0.04, 1, saturate(s.x)) * smooth * smooth;
        o.solid = _Mw2OpaqueUnitGlass > 0.5 && Mw2UnitGlass(v.enhanced) ? 1 : 0;   // graphics/detail.json "opaqueGlass"
        return o;
    }

    Out Write(v2f i, float facing)
    {
        Out o;
        float3 n = normalize(i.normal) * (facing > 0 ? 1 : -1);
        n.z = -n.z;
        o.depth = float4(i.depth, 0, 0, 1);
        o.surface = float4(n * 0.5 + 0.5, i.mirror);
        return o;
    }

    Out fragOpaque(v2f i, float facing : VFACE)
    {
        if (_Unlit > 0.5 || _ZWrite < 0.5 || _ShadeFull > 0.5) discard;
        return Write(i, facing);
    }

    Out fragAlpha(v2f i, float facing : VFACE)
    {
        if (_Unlit > 0.5 || _ZWrite < 0.5 || _ShadeFull > 0.5) discard;
        if (_Paletted < 0.5 && i.solid < 0.5 && tex2D(_MainTex, i.uv).a < 0.5) discard;
        return Write(i, facing);
    }
    ENDCG
    SubShader
    {
        Tags { "Mw2Depth" = "Opaque" }
        Pass { Cull Off ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOpaque
            #pragma target 3.0
            ENDCG }
    }
    SubShader
    {
        Tags { "Mw2Depth" = "Alpha" }
        Pass { Cull Off ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragAlpha
            #pragma target 3.0
            ENDCG }
    }
}
