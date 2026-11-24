// Modern lighting of a graphics mod (GraphicsQuality "lighting", Mw2Graphics). Port addition with no counterpart in the
// original program: without such a mod _Mw2Modern is 0 and every shader keeps the original's lighting (Mw2Light.cginc).
// The lights are the mission's own (SKYGND.PAR amb / wa / wb, the slots of Mw2Light.cginc), so a night mission stays dark;
// they light a surface with a physically based model: Lambert diffuse, GGX specular with Schlick Fresnel, a sky/ground
// hemisphere for the ambient slot and a reflection of the sky. Slot A is the sun of the Unity light and casts shadows.
// The albedo comes from the live palette, so the palette fades reach it; the specular and the reflection, which do not
// come from the palette, are scaled by the fade level, so a fade to black still ends black.
#ifndef MW2_MODERN_INCLUDED
#define MW2_MODERN_INCLUDED
#include "Mw2Surface.cginc"

float _Mw2Modern;              // 1 while a graphics mod turns the lighting on
float4 _Mw2ModernParams;       // x = sun factor, y = sky factor, z = ground bounce, w = reflections
float4 _Mw2ModernFade;         // x = fade level of the live palette (0 black .. 1 the period's palette)
float4 _Mw2ModernLights;       // x = intensity, y = range factor of the point lights (shots, explosions), z = fill (slot B)
float _Mw2PointCountModern;    // point lights this lighting uses (the original's first, then the ones made for it alone)

// The reflection probe (Mw2GraphicsDepth): a cube of the world around the camera, its mips blurred for rough surfaces.
// While the probe itself is drawn (_Mw2EnvPass = 1) the cockpit shell stays out of it.
samplerCUBE _Mw2EnvCube;
float4 _Mw2EnvParams;          // x = 1 while the cube is ready, y = its last mip level
float _Mw2EnvPass;

// The sun's shadow map of the mod (Mw2Graphics: an orthographic camera along light slot A renders the depth of the world
// with MW2/Depth): the distance along the light divided by the camera's far plane, 1 where nothing stands.
sampler2D _Mw2SunShadowMap;
float4x4 _Mw2SunShadowVP;      // world -> clip of the sun camera
float4 _Mw2SunShadowView;      // the row of its world-to-camera matrix that gives the view z
float4 _Mw2SunShadowParams;    // x = 1 / far, y = depth bias, z = strength, w = 1 while the map is valid
float4 _Mw2SunShadowTexel;     // x = 1 / size, y = normal offset in metres

static const float2 Mw2Poisson[12] = {
    float2(-0.326, -0.406), float2(-0.840, -0.074), float2(-0.696, 0.457), float2(-0.203, 0.621),
    float2(0.962, -0.195), float2(0.473, -0.480), float2(0.519, 0.767), float2(0.185, -0.893),
    float2(0.507, 0.064), float2(0.896, 0.412), float2(-0.322, -0.933), float2(-0.792, -0.598) };

float Mw2SunShadow(float3 worldPos, float3 n)
{
    float result = 1;
    if (_Mw2SunShadowParams.w > 0.5)
    {
        float3 p = worldPos + n * _Mw2SunShadowTexel.y;
        float4 c = mul(_Mw2SunShadowVP, float4(p, 1));
        float2 uv = c.xy / c.w * 0.5 + 0.5;
        float2 edge2 = saturate((0.5 - abs(uv - 0.5)) * 20);
        float edge = min(edge2.x, edge2.y);
        if (edge > 0)
        {
            float d = -(dot(_Mw2SunShadowView.xyz, p) + _Mw2SunShadowView.w) * _Mw2SunShadowParams.x - _Mw2SunShadowParams.y;
            float lit = 0;
            [unroll] for (int k = 0; k < 12; k++)
                lit += d <= tex2Dlod(_Mw2SunShadowMap, float4(uv + Mw2Poisson[k] * _Mw2SunShadowTexel.x * 1.6, 0, 0)).r ? 1 : 0;
            result = lerp(1, lit / 12, _Mw2SunShadowParams.z * edge);
        }
    }
    return result;
}

// The original path stays for the infrared palette group, for previews without mission lights and without the mod.
bool Mw2ModernOn()
{
    return _Mw2Modern > 0.5 && _Mw2LightParams.w > 0.5 && _Mw2Infrared < 0.5;
}

// "lighting.cockpit": the cockpit shell (_ShadeFull, drawn at full shade by the original's cockpit pass 0x1001bea2) is lit
// like the world with this surface while w >= 1, keeping w - 1 ("light") of the flat full shade under it; it stays out of
// the probe and of the depth passes either way.
float4 _Mw2ModernCockpit;

// Whether a face takes the modern lighting: the world while the lighting is on, the shell only when the mod asks for it.
bool Mw2ModernFor(float shadeFull)
{
    return Mw2ModernOn() && (shadeFull < 0.5 || _Mw2ModernCockpit.w > 0.5);
}

// The surface of a face: the shell's own, or the one of Mw2ModernSurface.
float4 Mw2ModernSurfaceFor(float2 enhanced, float ground, float shadeFull)
{
    return shadeFull > 0.5 ? _Mw2ModernCockpit : Mw2ModernSurface(enhanced, ground);
}

float3 Mw2ModernDirect(float3 lightColor, float3 L, float3 n, float3 V, float3 diffuse, float3 f0, float rough, float fade)
{
    float nl = saturate(dot(n, L));
    if (nl <= 0) return 0;
    float nv = saturate(dot(n, V)) + 1e-4;
    float3 h = normalize(L + V);
    float nh = saturate(dot(n, h));
    float a = rough * rough;
    float a2 = a * a;
    float d = nh * nh * (a2 - 1) + 1;
    float D = a2 / (UNITY_PI * d * d + 1e-6);
    float k = (rough + 1) * (rough + 1) / 8;
    float G = nl / (nl * (1 - k) + k) * nv / (nv * (1 - k) + k);
    float3 F = f0 + (1 - f0) * pow(1 - saturate(dot(h, V)), 5);
    float3 spec = D * G * F / (4 * nl * nv) * UNITY_PI;
    return lightColor * nl * (diffuse * (1 - F) + spec * fade);
}

// Direction to a light slot of Mw2Light.cginc (type 1: the vector the light travels; type 2: a position in metres).
float3 Mw2ModernToLight(float4 color, float4 vec, float3 worldPos)
{
    return color.w > 1.5 ? normalize(vec.xyz - worldPos) : normalize(-vec.xyz + 1e-6);
}

float3 Mw2ModernLight(float3 albedo, float3 n, float3 worldPos, float4 surface, float shadow)
{
    float metallic = saturate(surface.x), rough = clamp(surface.y, 0.05, 1);
    albedo *= surface.z;
    float fade = saturate(_Mw2ModernFade.x);
    float3 V = normalize(_WorldSpaceCameraPos - worldPos);
    float3 f0 = lerp(0.04, albedo, metallic);
    float3 diffuse = albedo * (1 - metallic);

    float3 c = 0;
    c += Mw2ModernDirect(_Mw2LightAColor.rgb * _Mw2ModernParams.x, Mw2ModernToLight(_Mw2LightAColor, _Mw2LightAVector, worldPos),
                         n, V, diffuse, f0, rough, fade) * shadow;
    // Slot B: in the missions a second light against the sun (BET3: wa -1,-1,0 and wb 1,1,0), the original's way to
    // light the side away from it. Here a fill without highlights, so the shaded side shows no second sun.
    c += Mw2ModernDirect(_Mw2LightBColor.rgb * _Mw2ModernParams.x * _Mw2ModernLights.z,
                         Mw2ModernToLight(_Mw2LightBColor, _Mw2LightBVector, worldPos), n, V, diffuse, f0, rough, 0);

    // The original's point lights (light pool 0x102492e0: shots in flight, explosions), here round with a soft falloff.
    for (int k = 0; k < 16; k++)
    {
        if (k >= (int)_Mw2PointCountModern) break;
        float3 toLight = _Mw2PointPos[k].xyz - worldPos;
        float range = _Mw2PointPos[k].w * _Mw2ModernLights.y;
        float d2 = dot(toLight, toLight);
        if (d2 >= range * range) continue;
        float f = 1 - d2 / (range * range);
        c += Mw2ModernDirect(_Mw2PointColor[k].rgb * _Mw2ModernLights.x * f * f, toLight * rsqrt(max(d2, 1e-6)),
                             n, V, diffuse, f0, rough, fade);
    }

    float3 sky = _Mw2Ambient.rgb * _Mw2ModernParams.y;
    float3 below = sky * _Mw2ModernParams.z;
    c += diffuse * lerp(below, sky, n.y * 0.5 + 0.5);

    // The sky seen in the surface: the fog colour at the horizon, the ambient light below it.
    float nv = saturate(dot(n, V));
    float3 r = reflect(-V, n);
    // With the probe the world itself (already through the live palette, so no fade factor); without it a sky gradient.
    float3 env = _Mw2EnvParams.x > 0.5 ? texCUBElod(_Mw2EnvCube, float4(r, rough * _Mw2EnvParams.y)).rgb
                                       : lerp(below, _Mw2FogColor.rgb, smoothstep(-0.2, 0.4, r.y)) * fade;
    float3 fr = f0 + (max(1 - rough, f0) - f0) * pow(1 - nv, 5);
    c += env * fr * (1 - rough * 0.75) * _Mw2ModernParams.w;
    return c;
}

// The light of a face: Mw2ModernLight, and for the cockpit shell also its share of the original's flat full shade.
float3 Mw2ModernLightFor(float3 albedo, float3 n, float3 worldPos, float4 surface, float shadow, float shadeFull)
{
    float3 c = Mw2ModernLight(albedo, n, worldPos, surface, shadow);
    if (shadeFull > 0.5) c += albedo * surface.z * (1 - saturate(surface.x)) * saturate(_Mw2ModernCockpit.w - 1);
    return c;
}

#endif
