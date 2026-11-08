#ifndef MW2_ENHANCED_INCLUDED
#define MW2_ENHANCED_INCLUDED
float _Mw2Enhanced;
float _Mw2EnhancedDamage;
float _Mw2EnhancedFlags, _Mw2EnhancedOverride;
// Material_Shade 1001afb0 mode1 then FUN_1005581d. -1 passthrough, 0 skipped, positive black+boundary.
float Mw2EnhancedBoundary(float2 data)
{
    int m = (int)(data.x + 0.5), f = (int)(data.y + 0.5), t = m & 0xf000;
    if (_Mw2EnhancedOverride > 0.5) f = (int)(_Mw2EnhancedFlags + 0.5);
    if (t != 0x8000 && t != 0x9000)
    {
        if ((f & 0x200) != 0) m = 7;
        else if ((f & 0x400) != 0) { if (t == 0 || t == 0x1000) m = 11; }
        else if ((f & 0x100) == 0) m = (f & 0x70) == 0 && m != 0 ? 8 : 0;
        else m = _Mw2EnhancedDamage == 0 ? 7 : _Mw2EnhancedDamage < 12 ? 3 : 11;
    }
    t = m & 0xf000;
    if (m == 0 || t == 0x9000) return 0;
    if (t == 0x3000 || t == 0x5000 || t == 0x6000 || t == 0x7000 || t == 0x8000) return -1;
    return m & 255;
}
#endif
