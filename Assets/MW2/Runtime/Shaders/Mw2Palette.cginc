// The palette of the original renderer (mercsw.dll Palette_Fade 0x1006b46f), driven by Mw2PaletteFade.
// Flat polygons and the CEL textures of the world hold palette indices; the colour is looked up in the palette on the
// screen, so the fades of the engine (mission start, time of day, infrared, flashes) run on one 256 x 1 texture.
// _Mw2Palette / _Mw2PaletteLive: the live palette (global); _Palette: the mission's palette on the material, used while
// no Mw2PaletteFade drives it (editor previews); _Paletted = 1: _MainTex is an R8 texture of indices.
#ifndef MW2_PALETTE_INCLUDED
#define MW2_PALETTE_INCLUDED

sampler2D _Mw2Palette;
float _Mw2PaletteLive;
sampler2D _Palette;
float _Paletted;

float3 Mw2PaletteColor(float index)
{
    float4 uv = float4((index + 0.5) / 256, 0.5, 0, 0);
    return _Mw2PaletteLive > 0.5 ? tex2Dlod(_Mw2Palette, uv).rgb : tex2Dlod(_Palette, uv).rgb;
}

float Mw2TexelIndex(sampler2D tex, float2 uv)
{
    return floor(tex2Dlod(tex, float4(uv, 0, 0)).r * 255 + 0.5);
}

// Glide filters palette textures after the palette lookup (grTexFilterMode BILINEAR): four texels, four colours, mixed.
// texel = _MainTex_TexelSize (x, y = 1 / size, z, w = size). A texture of colours (own palette CELs) is sampled as it is.
float4 Mw2SampleMain(sampler2D tex, float4 texel, float2 uv)
{
    if (_Paletted < 0.5) return tex2D(tex, uv);
    float2 p = uv * texel.zw - 0.5;
    float2 f = frac(p);
    float2 b = (floor(p) + 0.5) * texel.xy;
    float3 c00 = Mw2PaletteColor(Mw2TexelIndex(tex, b));
    float3 c10 = Mw2PaletteColor(Mw2TexelIndex(tex, b + float2(texel.x, 0)));
    float3 c01 = Mw2PaletteColor(Mw2TexelIndex(tex, b + float2(0, texel.y)));
    float3 c11 = Mw2PaletteColor(Mw2TexelIndex(tex, b + texel.xy));
    return float4(lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y), 1);
}

#endif
