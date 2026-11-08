// Lighting of the original renderer, driven by Mw2Atmosphere (mission LITE chunk + SKYGND.PAR).
// _Mw2LightParams.w = 1 when a mission sets the lights; otherwise shaders keep their Unity fallback.
//
// Textured polygons: texture * vertex colour (zglide colour combine SCALE_OTHER(texture) by ITERATED), the colour from
// mercsw.dll 0x1002ec0a: slot 0 (amb) added as is, slots 1/2 (wa/wb): type 1 direction -> colour * (-dir . n) when > 0,
// type 2 position -> colour * cos to the light; sum * 255 clamped to 255.
//
// Palette-row polygons (material types 1/4, Material_LightShade 0x1002f543): level = ambient (LITE) +
// trunc(clamp(n . sun, -127, 127) * globalintensity / 128) when positive; shade = level * (reflectivity >> 1) / 0x440,
// clamped to 0..15. Polygon normals are unit vectors (0x10004127); with a direction sun (camera +0x28 = 1) the dot uses
// the raw LITE vector (0x10056cd3), so BET1's (-100000, 20000, 0) saturates to +-127. A position (sun or point light)
// gives trunc(128 * cos) clamped to +-127 (0x10056dd7: dot / length * 128, 0x1008824c), 127 at the light itself.
//
// Point lights (Mw2PointLight, light pool 0x102492e0): inside the cube light +- radius they add that position term to the
// palette level while it is below 0x80 (0x1002f543) and colour * cos to the textured vertex colour (0x1002ec0a).
#ifndef MW2_LIGHT_INCLUDED
#define MW2_LIGHT_INCLUDED

float4 _Mw2Ambient;        // rgb 0..1
float4 _Mw2LightAColor;    // rgb 0..1, w = type (1 direction, 2 position)
float4 _Mw2LightAVector;   // Unity space: normalised direction, or position in metres
float4 _Mw2LightBColor;
float4 _Mw2LightBVector;
float4 _Mw2Sun;            // Unity space, model units: raw direction vector or position; w = 1 when a direction
float4 _Mw2LightParams;    // x = ambient level, y = global intensity (0..128), z = sun enabled, w = lights set
float4 _Mw2PointPos[16];    // Unity space metres, w = cube radius in metres
float4 _Mw2PointColor[16];
float _Mw2PointCount;
float _Mw2Infrared;        // 1 in the infrared palette group (modes 12..15): the light colours are summed to one intensity, orange (I, I / 1.5, 0)

bool Mw2InPointCube(float4 l, float3 worldPos)
{
    float3 d = abs(worldPos - l.xyz);
    return d.x <= l.w && d.y <= l.w && d.z <= l.w;
}

// Light_SunDot 0x10056cd3 in position mode.
float Mw2PositionDot(float3 n, float3 toLight)
{
    if (all(abs(toLight) < 1e-9)) return 127;
    return trunc(clamp(128 * dot(n, toLight) / length(toLight), -127, 127));
}

float3 Mw2SlotLight(float4 color, float4 vec, float3 n, float3 worldPos)
{
    if (color.w > 1.5)
    {
        float3 toLight = vec.xyz - worldPos;
        float d = dot(toLight, n);
        return d > 0 ? color.rgb * d / max(length(toLight), 1e-4) : 0;
    }
    float d = -dot(vec.xyz, n);
    return d > 0 ? color.rgb * d : 0;
}

float3 Mw2TextureLight(float3 n, float3 worldPos)
{
    float3 c = _Mw2Ambient.rgb + Mw2SlotLight(_Mw2LightAColor, _Mw2LightAVector, n, worldPos)
                               + Mw2SlotLight(_Mw2LightBColor, _Mw2LightBVector, n, worldPos);
    // Light_VertexColors 0x1002ec0a with DAT_100b1944 in 12..15: each slot's red + green + blue times its cosine, summed,
    // clamped; green = that / 1.5 (_DAT_10087dfc), blue 0; no point lights.
    if (_Mw2Infrared > 0.5)
    {
        float i = min(c.r + c.g + c.b, 1);
        return float3(i, i / 1.5, 0);
    }
    c = min(c, 1);
    for (int k = 0; k < 16; k++)
    {
        if (k >= (int)_Mw2PointCount) break;
        if (!Mw2InPointCube(_Mw2PointPos[k], worldPos)) continue;
        float3 toLight = _Mw2PointPos[k].xyz - worldPos;
        float d = dot(toLight, n);
        if (d > 0) c = min(c + _Mw2PointColor[k].rgb * d / max(length(toLight), 1e-6), 1);
    }
    return c;
}

// Ground (FUN_1005503e, Ground_Draw 0x1005521b): one colour for the whole ground, the sum of the three slots: a direction
// light (type 1) contributes colour * |direction y|, the others their full colour; each channel clamped to 1.
float3 Mw2GroundLight()
{
    float3 c = _Mw2Ambient.rgb;
    c += _Mw2LightAColor.w > 1.5 ? _Mw2LightAColor.rgb : _Mw2LightAColor.rgb * abs(_Mw2LightAVector.y);
    c += _Mw2LightBColor.w > 1.5 ? _Mw2LightBColor.rgb : _Mw2LightBColor.rgb * abs(_Mw2LightBVector.y);
    if (_Mw2Infrared > 0.5)
    {
        float i = min(c.r + c.g + c.b, 1);     // Ground_Draw 0x1005521b, infrared: the same orange
        return float3(i, i / 1.5, 0);
    }
    return min(c, 1);
}

float Mw2PaletteShade(float3 n, float3 worldPos, float reflectivityByte)
{
    float level = _Mw2LightParams.x;
    if (_Mw2LightParams.z > 0.5)
    {
        float s;
        if (_Mw2Sun.w > 0.5) s = trunc(clamp(dot(n, _Mw2Sun.xyz), -127, 127));
        else s = Mw2PositionDot(n, _Mw2Sun.xyz - worldPos * 100);
        float sun = trunc(s * _Mw2LightParams.y / 128);
        if (sun > 0) level += sun;
    }
    if (level < 128)
        for (int k = 0; k < 16; k++)
        {
            if (k >= (int)_Mw2PointCount) break;
            if (!Mw2InPointCube(_Mw2PointPos[k], worldPos)) continue;
            float p = Mw2PositionDot(n, _Mw2PointPos[k].xyz - worldPos);
            if (p > 0) { level += p; if (level > 127) break; }
        }
    float shade = floor(level * floor(reflectivityByte / 2) / 1088);
    return clamp(shade, 0, 15);
}

#endif
