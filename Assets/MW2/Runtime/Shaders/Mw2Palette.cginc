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
float4 Mw2SampleBilinear(sampler2D tex, float4 texel, float2 uv)
{
    float4 result;
    if (_Paletted < 0.5) result = tex2Dlod(tex, float4(uv, 0, 0));    // the colour textures have no mipmaps
    else
    {
        float2 p = uv * texel.zw - 0.5;
        float2 f = frac(p);
        float2 b = (floor(p) + 0.5) * texel.xy;
        float3 c00 = Mw2PaletteColor(Mw2TexelIndex(tex, b));
        float3 c10 = Mw2PaletteColor(Mw2TexelIndex(tex, b + float2(texel.x, 0)));
        float3 c01 = Mw2PaletteColor(Mw2TexelIndex(tex, b + float2(0, texel.y)));
        float3 c11 = Mw2PaletteColor(Mw2TexelIndex(tex, b + texel.xy));
        result = float4(lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y), 1);
    }
    return result;
}

// Graphics mods only (GraphicsQuality "textureSamples", Mw2Graphics): up to n bilinear samples along each screen axis,
// spread over the pixel's footprint in the texture, are averaged. 1 (the default) is one sample, as Glide.
float _Mw2TexSamples;

float4 Mw2SampleMain(sampler2D tex, float4 texel, float2 uv)
{
    float2 dx = ddx(uv), dy = ddy(uv);
    float4 result;
    if (_Mw2TexSamples < 1.5) result = _Paletted < 0.5 ? tex2D(tex, uv) : Mw2SampleBilinear(tex, texel, uv);
    else
    {
        int nx = (int)clamp(ceil(length(dx * texel.zw)), 1, _Mw2TexSamples);
        int ny = (int)clamp(ceil(length(dy * texel.zw)), 1, _Mw2TexSamples);
        float4 sum = 0;
        [loop] for (int a = 0; a < nx; a++)
            [loop] for (int b = 0; b < ny; b++)
                sum += Mw2SampleBilinear(tex, texel, uv + ((a + 0.5) / nx - 0.5) * dx + ((b + 0.5) / ny - 0.5) * dy);
        result = sum / (nx * ny);
    }
    return result;
}

#endif
