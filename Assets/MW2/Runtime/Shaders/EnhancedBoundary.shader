Shader "MW2/EnhancedBoundary"
{
    Properties { [NoScaleOffset] _Palette ("Palette", 2D) = "black" {} }
    SubShader
    {
        Tags { "Queue"="Overlay" }
        Pass
        {
            ZWrite Off ZTest LEqual Cull Off Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Mw2Palette.cginc"
            struct v2f { float4 pos:SV_POSITION; float4 color:COLOR; };
            v2f vert(appdata_full v) { v2f o; o.pos=UnityObjectToClipPos(v.vertex); o.color=v.color; return o; }
            fixed4 frag(v2f i):SV_Target { return fixed4(Mw2PaletteColor(floor(i.color.r*255+0.5)),1); }
            ENDCG
        }
    }
}
