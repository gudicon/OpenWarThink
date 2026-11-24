// Surfaces of a graphics mod's lighting (GraphicsQuality "lighting", Mw2Graphics), shared by Mw2Modern.cginc and the
// MW2/Depth replacement shader. Port addition with no counterpart in the original program.
// A surface is x = metallic, y = roughness, z = a factor on the albedo.
#ifndef MW2_SURFACE_INCLUDED
#define MW2_SURFACE_INCLUDED

float4 _Mw2ModernUnits;        // parts of units (model flag 0x100) that no class of "unitParts" takes
float4 _Mw2ModernStructures;   // the rest of the world
float4 _Mw2ModernGround;       // the mission ground

// "unitParts": the class (1..5) of each texture key 0x100 + x of the mission, by its CEL name; 0 keeps "units".
sampler2D _Mw2ModernClassMap;
float4 _Mw2ModernClasses[8];   // the surface of each class, w = 1 when the mod names the class
float _Mw2ModernRowClass;      // the class of faces coloured by a palette row (types 1 and 4)

// The surface of a face from TEXCOORD4 (PolyMeshBuilder: x = the material word after the team's paint, y = the model
// flags, 0x100 for unit parts). Textured faces (types 5..7) take the class of their key, palette-row faces "rows".
float4 Mw2ModernSurface(float2 enhanced, float ground)
{
    float4 s = _Mw2ModernStructures;
    if (ground > 0.5) s = _Mw2ModernGround;
    else if ((((int)(enhanced.y + 0.5)) & 0x100) != 0)
    {
        s = _Mw2ModernUnits;
        int material = (int)(enhanced.x + 0.5);
        int type = material >> 12;
        float cls = 0;
        if (type >= 5 && type <= 7)
            cls = tex2Dlod(_Mw2ModernClassMap, float4(((material & 0xff) + 0.5) / 256, 0.5, 0, 0)).r * 255;
        else if (type == 1 || type == 4) cls = _Mw2ModernRowClass;
        int c = (int)(cls + 0.5);
        if (c > 0 && c < 8)
        {
            float4 part = _Mw2ModernClasses[c];
            if (part.w > 0.5) s = part;
        }
    }
    return s;
}

// graphics/detail.json "opaqueGlass": a textured face (types 5..7) of a unit part (flag 0x100) whose key shows the
// windshield glass (*CLOU, G of the class map).
bool Mw2UnitGlass(float2 enhanced)
{
    if ((((int)(enhanced.y + 0.5)) & 0x100) == 0) return false;
    int material = (int)(enhanced.x + 0.5);
    int type = material >> 12;
    if (type < 5 || type > 7) return false;
    return tex2Dlod(_Mw2ModernClassMap, float4(((material & 0xff) + 0.5) / 256, 0.5, 0, 0)).g > 0.5;
}

#endif
