// Linear distance fog of the original renderer, driven by Mw2Atmosphere (SKYGND.PAR).
// visibility = (1 - min(depth / distance, 1)) * strength (mercsw.dll Plugin_DrawPolygon 0x10053040, 0x100019c1);
// Glide (zglide.dll grFogMode(1)) blends the colour towards the fog colour by 1 - visibility.
// _Mw2FogParams: x = 1 / distance in metres (0 = no distance fog), y = strength, z = 1 when a mission sets fog.
#ifndef MW2_FOG_INCLUDED
#define MW2_FOG_INCLUDED

float4 _Mw2FogColor;
float4 _Mw2FogParams;

float Mw2FogDepth(float4 vertex)
{
    return -UnityObjectToViewPos(vertex.xyz).z;
}

float3 Mw2Fog(float3 color, float depth)
{
    if (_Mw2FogParams.z < 0.5) return color;
    float visibility = (1 - min(depth * _Mw2FogParams.x, 1)) * _Mw2FogParams.y;
    return lerp(_Mw2FogColor.rgb, color, saturate(visibility));
}

#endif
