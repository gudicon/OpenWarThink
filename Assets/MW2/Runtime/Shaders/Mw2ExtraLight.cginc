// Extra lights of port extensions (Mw2ModLight): soft round point lights that the original renderer does not have. They are
// set only while an extension asks for one, so without one the lighting is the original's. Each light adds
// colour * intensity * falloff * wrap to the lit colour: falloff = (1 - (d / range)^2)^2, wrap = 0.25 + 0.75 * max(n . l, 0).
#ifndef MW2_EXTRA_LIGHT_INCLUDED
#define MW2_EXTRA_LIGHT_INCLUDED

float4 _Mw2ExtraLightPos[16];     // Unity space metres, w = range in metres
float4 _Mw2ExtraLightColor[16];   // rgb already multiplied by the intensity
float _Mw2ExtraLightCount;

float3 Mw2ExtraLight(float3 n, float3 worldPos)
{
    float3 sum = 0;
    for (int k = 0; k < 16; k++)
    {
        if (k >= (int)_Mw2ExtraLightCount) break;
        float3 toLight = _Mw2ExtraLightPos[k].xyz - worldPos;
        float d2 = dot(toLight, toLight);
        float r = _Mw2ExtraLightPos[k].w;
        if (d2 >= r * r) continue;
        float f = 1 - d2 / (r * r);
        float wrap = 0.25 + 0.75 * saturate(dot(n, toLight * rsqrt(max(d2, 1e-6))));
        sum += _Mw2ExtraLightColor[k].rgb * (f * f * wrap);
    }
    return sum;
}

#endif
