// Unit shadow of the original renderer (Glow type 1: Glow_Take 0x1005c2eb from Mech_InitMovement 0x100380d0,
// drawn by FUN_1005d52c -> FUN_1005d44e -> FUN_1005d2ec): the polygons of the unit's object tree, its transformed
// vertices kept in x and z and put at the height _ShadowY (ground under the unit, gamepiece +0x74, plus 5 units when it
// is not 0), drawn black with alpha 0x80 through FUN_1005c979 (plugin mode 0x11), the vertex fog of Plugin_DrawPolygon
// still applied. Shadow_DrawPolygon 0x1005d2ec takes only the polygons whose world normal y (+0x18, turned by
// Model_TransformVerts 0x10004934) is 0 or more: the faces that look up. For a closed body they cover its outline once,
// so the shadow is one even layer at alpha 0x80; FUN_10053eff then only clips and projects, so where up-facing faces of
// separate parts overlap they still darken each other.
// Here the facing comes from the winding: flattening keeps x and z, so the flattened triangle is front-facing from above
// exactly when the face looked up (the port's front faces are the original's outer faces, see PolyMeshBuilder).
// The original has no depth buffer; here the shadow tests the world's depth and is pulled forward one depth step so
// that it does not fight the walkable surfaces at its own height.
Shader "MW2/Shadow"
{
    Properties
    {
        _ShadowY ("Shadow height (metres)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        Offset -1, -1
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "Mw2Fog.cginc"

            float _ShadowY;
            float _Mw2ModernHideUnitShadows;   // 1 while a graphics mod casts real sun shadows (Mw2Graphics)

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float fogDepth : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                float4 world = mul(unity_ObjectToWorld, v.vertex);
                world.y = _ShadowY;
                o.pos = mul(UNITY_MATRIX_VP, world);
                o.fogDepth = -mul(UNITY_MATRIX_V, world).z;
                return o;
            }

            fixed4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                if (_Mw2ModernHideUnitShadows > 0.5) discard;
                // 0x1005d2ec: only faces with world normal y >= 0. Seen from above the shadow height they are the
                // front faces of the flattened mesh, seen from below the back faces.
                bool above = _WorldSpaceCameraPos.y >= _ShadowY;
                if ((facing > 0) != above) discard;
                return fixed4(Mw2Fog(float3(0, 0, 0), i.fogDepth), 128.0 / 255.0);
            }
            ENDCG
        }
    }
}
