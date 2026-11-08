// Replacement shader for the cockpit target window (window 17, map view mode 1 of mercsw.dll 0x1006606e).
// The original renders the target in material mode 3 lit from the camera, a blue silhouette; here the colour runs
// from _Mw2TargetDark to _Mw2TargetLight with the angle to the camera (approximation of that mode).
Shader "MW2/TargetView"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Back
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Mw2TargetDark;
            float4 _Mw2TargetLight;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; float3 view : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.view = WorldSpaceViewDir(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float facing = saturate(dot(normalize(i.normal), normalize(i.view)));
                return fixed4(lerp(_Mw2TargetDark.rgb, _Mw2TargetLight.rgb, facing), 1);
            }
            ENDCG
        }
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" }
        Cull Back
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Mw2TargetDark;
            float4 _Mw2TargetLight;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; float3 view : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.view = WorldSpaceViewDir(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float facing = saturate(dot(normalize(i.normal), normalize(i.view)));
                return fixed4(lerp(_Mw2TargetDark.rgb, _Mw2TargetLight.rgb, facing), 1);
            }
            ENDCG
        }
    }
}
