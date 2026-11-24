// The colour pass of a graphics mod (Mw2GraphicsPost, file graphics/quality.json) on the finished 3D frame.
// Port addition with no counterpart in the original program; without such a mod it is never used.
// Every step keeps black black, so the palette fades from and to black (Palette_Fade 0x1006b46f) still reach black.
Shader "MW2/ModPost"
{
    Properties { _MainTex ("Frame", 2D) = "black" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    sampler2D _BloomTex;
    float4 _Bloom;    // x = threshold, y = knee, z = intensity
    float4 _Grade;    // x = brightness, y = contrast, z = saturation, w = 1 / gamma
    float4 _Extra;    // x = sharpen, y = vignette, z = ambient occlusion strength, w = its radius in metres
    float4 _Contact;  // xyz = towards the sun in view space (z ahead), w = strength of the contact shadows (0 = off)
    float4 _ContactLength; // x = how far they look, in metres
    sampler2D _Mw2ViewDepth;     // Mw2GraphicsDepth: view distance / far, 1 where nothing stands
    float4 _Mw2ViewParams;       // x = projection m00, y = m11, z = far, w = 1 while the depth is drawn
    sampler2D _Mw2ViewSurface;   // MW2/Depth target 1: view normal (z towards the view) packed 0..1, w = how it mirrors
    float4 _Ssr;                 // x = intensity, y = distance in metres, z = thickness in metres
    float4 _Hdr;                 // x = exposure, y = tonemapping (0 none, 1 ACES, 2 Reinhard)
    float4 _Rays;                // xy = the sun on the screen (0..1), z = intensity (0 = off), w = decay per sample
    float4 _RaysColor;           // rgb = the sun's colour, w = length (part of the way to the sun)

    // Ambient occlusion from the depth of the view (Mw2GraphicsDepth, while a graphics mod lights the world): 16 points on
    // a spiral around the pixel, within the radius in metres; the closer a point stands in front of the surface, the
    // darker. Far away (beyond 25 radii) and on the sky it fades out.
    float Depth01(float2 uv) { return tex2Dlod(_Mw2ViewDepth, float4(uv, 0, 0)).r; }

    float3 ViewPos(float2 uv)
    {
        float d = Depth01(uv) * _Mw2ViewParams.z;
        return float3((uv * 2 - 1) / _Mw2ViewParams.xy * d, d);
    }

    float Occlusion(float2 uv)
    {
        if (_Mw2ViewParams.w < 0.5 || Depth01(uv) >= 0.9999) return 1;
        float3 p = ViewPos(uv);
        float2 t = 1 / _ScreenParams.xy;
        float3 px = ViewPos(uv + float2(t.x, 0)) - p, nx = p - ViewPos(uv - float2(t.x, 0));
        float3 py = ViewPos(uv + float2(0, t.y)) - p, ny = p - ViewPos(uv - float2(0, t.y));
        float3 n = normalize(cross(abs(px.z) < abs(nx.z) ? px : nx, abs(py.z) < abs(ny.z) ? py : ny));
        if (dot(n, p) > 0) n = -n;
        float radius = _Extra.w;
        float pixels = radius * _Mw2ViewParams.x * 0.5 * _ScreenParams.x / p.z;
        if (pixels < 1) return 1;
        float2 step = pixels / _ScreenParams.xy;
        float occ = 0;
        [unroll] for (int k = 0; k < 16; k++)
        {
            float a = k * 2.39996;
            float r = (k + 0.5) / 16;
            float3 q = ViewPos(uv + float2(cos(a), sin(a)) * r * step);
            float3 v = q - p;
            float len2 = dot(v, v);
            float range = saturate(1 - len2 / (radius * radius));
            occ += max(0, dot(v, n) - 0.02 * p.z) * rsqrt(len2 + 1e-4) * range;
        }
        float far = saturate(1 - p.z / (radius * 25));
        return 1 - _Extra.z * saturate(occ / 16 * 2) * far;
    }

    float2 ViewUv(float3 q) { return q.xy * _Mw2ViewParams.xy / q.z * 0.5 + 0.5; }

    // Contact shadows: from the pixel 12 steps towards the sun through the depth of the view; the first surface in front
    // of the way within the length darkens the pixel, less the farther away it stands. Faces turned from the sun are
    // already in shadow and are left as they are. Fills the small shadows the sun's map is too coarse for: under feet, in
    // the joints of armour, below weapons.
    float ContactShadow(float2 uv)
    {
        float result = 1;
        if (_Mw2ViewParams.w > 0.5 && Depth01(uv) < 0.9999)
        {
            float3 p = ViewPos(uv);
            float3 n = normalize(tex2Dlod(_Mw2ViewSurface, float4(uv, 0, 0)).xyz * 2 - 1);
            float3 L = _Contact.xyz;
            if (dot(n, L) > 0.05)
            {
                float len = _ContactLength.x;
                float3 start = p + n * (0.02 + p.z * 0.002);
                float hit = 0;
                [loop] for (int s = 1; s <= 12; s++)
                {
                    float3 q = start + L * (len * s / 12);
                    if (q.z <= 0.1) break;
                    float2 u = ViewUv(q);
                    if (any(u < 0) || any(u > 1)) break;
                    float behind = q.z - Depth01(u) * _Mw2ViewParams.z;
                    if (behind > 0.03 + q.z * 0.003 && behind < len)
                    {
                        hit = 1 - (s - 1) / 12.0;
                        break;
                    }
                }
                float far = saturate(1 - p.z / 300);
                result = 1 - _Contact.w * hit * far;
            }
        }
        return result;
    }

    // Screen-space reflections: from the pixel along the mirrored view, 24 growing steps through the depth of the view
    // and 4 halvings at the first step behind a surface (within the thickness). The colour found is mixed in by how the
    // surface mirrors, fading towards the screen's edges, with distance and for rays turned to the camera.
    float3 Reflect(float2 uv, float3 c)
    {
        float4 surface = tex2Dlod(_Mw2ViewSurface, float4(uv, 0, 0));
        float k = surface.w * _Ssr.x;
        if (k < 0.01 || Depth01(uv) >= 0.9999) return c;
        float3 p = ViewPos(uv);
        float3 n = normalize(surface.xyz * 2 - 1);
        float3 v = normalize(p);
        float3 r = reflect(v, n);
        if (r.z < -0.2) return c;
        float stepLen = _Ssr.y / 24 * 0.15;
        float3 q = p + n * 0.05 * p.z * 0.01;
        float3 prev = q;
        float found = 0;
        float2 hit = 0;
        [loop] for (int s = 0; s < 24; s++)
        {
            prev = q;
            q += r * stepLen;
            stepLen *= 1.25;
            if (q.z <= 0.1) break;
            float2 u = ViewUv(q);
            if (any(u < 0) || any(u > 1)) break;
            float d = Depth01(u) * _Mw2ViewParams.z;
            if (q.z > d && q.z - d < _Ssr.z + q.z * 0.02)
            {
                float3 a = prev, b = q;
                [unroll] for (int h = 0; h < 4; h++)
                {
                    float3 m = (a + b) * 0.5;
                    float2 mu = ViewUv(m);
                    if (m.z > Depth01(mu) * _Mw2ViewParams.z) b = m; else a = m;
                }
                hit = ViewUv(b);
                found = 1;
                break;
            }
            if (length(q - p) > _Ssr.y) break;
        }
        if (found < 0.5) return c;
        float2 edge = saturate((0.5 - abs(hit - 0.5)) * 8);
        float fade = edge.x * edge.y * saturate(1 - length(q - p) / _Ssr.y) * saturate(r.z * 4 + 0.8);
        return lerp(c, tex2Dlod(_MainTex, float4(hit, 0, 0)).rgb, saturate(k * fade));
    }

    // Sun rays: the light of the sky near the sun (depth 1: no surface in front of it; the nearer the sun on the screen,
    // the more it counts) gathered along the way to the sun, each sample weaker by the decay, tinted by the sun's colour.
    float3 Rays(float2 uv)
    {
        float2 delta = (uv - _Rays.xy) * _RaysColor.w / 48;
        float2 u = uv;
        float weight = 1, sum = 0;
        [loop] for (int s = 0; s < 48; s++)
        {
            u -= delta;
            if (Depth01(u) >= 0.9999)
            {
                float3 sky = tex2Dlod(_MainTex, float4(u, 0, 0)).rgb;
                float2 fromSun = (u - _Rays.xy) * float2(_ScreenParams.x / _ScreenParams.y, 1);
                float nearSun = saturate(1 - length(fromSun) * 1.6);
                sum += smoothstep(0.55, 1.0, dot(sky, float3(0.299, 0.587, 0.114))) * nearSun * nearSun * weight;
            }
            weight *= _Rays.w;
        }
        float2 d = (uv - _Rays.xy) * float2(_ScreenParams.x / _ScreenParams.y, 1);
        float near = saturate(1 - length(d) * 0.8);
        return _RaysColor.rgb * sum / 48 * _Rays.z * (0.5 + near);
    }

    // The project draws in gamma space (UNITY_COLORSPACE_GAMMA): the curves are made for linear light, so the colour goes
    // to linear (power 2.2) for the exposure and the curve and back to gamma after it.
    float3 Tonemap(float3 c)
    {
        if (_Hdr.x == 1 && _Hdr.y < 0.5) return c;
        c = pow(max(c, 0), 2.2) * _Hdr.x;
        if (_Hdr.y > 1.5) c = c / (1 + c);
        else if (_Hdr.y > 0.5) c = saturate((c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14));
        return pow(c, 1 / 2.2);
    }

    float3 Box4(float2 uv)
    {
        float4 d = _MainTex_TexelSize.xyxy * float4(-1, -1, 1, 1);
        return (tex2D(_MainTex, uv + d.xy).rgb + tex2D(_MainTex, uv + d.zy).rgb +
                tex2D(_MainTex, uv + d.xw).rgb + tex2D(_MainTex, uv + d.zw).rgb) * 0.25;
    }

    // Soft threshold: the part of the brightness above the threshold, with a quadratic knee below it.
    float3 Prefilter(float3 c)
    {
        float b = max(c.r, max(c.g, c.b));
        float soft = clamp(b - _Bloom.x + _Bloom.y, 0, 2 * _Bloom.y);
        soft = soft * soft / (4 * _Bloom.y + 1e-5);
        float w = max(soft, b - _Bloom.x) / max(b, 1e-5);
        return c * w;
    }

    // Contrast around the middle grey that keeps 0 and 1 where they are.
    float3 Contrast(float3 c, float k)
    {
        c = saturate(c);
        float3 lo = 0.5 * pow(2 * c, k);
        float3 hi = 1 - 0.5 * pow(2 - 2 * c, k);
        return lerp(lo, hi, step(0.5, c));
    }

    fixed4 FragPrefilter(v2f_img i) : SV_Target { return fixed4(Prefilter(Box4(i.uv)), 1); }
    fixed4 FragDown(v2f_img i) : SV_Target { return fixed4(Box4(i.uv), 1); }
    fixed4 FragUp(v2f_img i) : SV_Target
    {
        float4 d = _MainTex_TexelSize.xyxy * float4(-0.5, -0.5, 0.5, 0.5);
        float3 c = (tex2D(_MainTex, i.uv + d.xy).rgb + tex2D(_MainTex, i.uv + d.zy).rgb +
                    tex2D(_MainTex, i.uv + d.xw).rgb + tex2D(_MainTex, i.uv + d.zw).rgb) * 0.25;
        return fixed4(c, 1);
    }

    fixed4 FragFinal(v2f_img i) : SV_Target
    {
        float3 c = tex2D(_MainTex, i.uv).rgb;
        float occlusion = _Extra.z > 0 ? Occlusion(i.uv) : 1;
        if (_Extra.x > 0)
        {
            float2 t = _MainTex_TexelSize.xy;
            float3 around = (tex2D(_MainTex, i.uv + float2(t.x, 0)).rgb + tex2D(_MainTex, i.uv - float2(t.x, 0)).rgb +
                             tex2D(_MainTex, i.uv + float2(0, t.y)).rgb + tex2D(_MainTex, i.uv - float2(0, t.y)).rgb) * 0.25;
            c = max(c + (c - around) * _Extra.x, 0);
        }
        c *= occlusion;
        if (_Contact.w > 0) c *= ContactShadow(i.uv);
        if (_Ssr.x > 0) c = Reflect(i.uv, c);
        if (_Rays.z > 0) c += Rays(i.uv);
        if (_Bloom.z > 0) c += tex2D(_BloomTex, i.uv).rgb * _Bloom.z;
        c = Tonemap(c);
        c *= _Grade.x;
        if (_Grade.y != 1) c = Contrast(c, _Grade.y);
        float grey = dot(c, float3(0.299, 0.587, 0.114));
        c = max(lerp(grey.xxx, c, _Grade.z), 0);
        c = pow(saturate(c), _Grade.w);
        if (_Extra.y > 0)
        {
            float2 r = i.uv * 2 - 1;
            c *= saturate(1 - _Extra.y * dot(r, r) * 0.5);
        }
        return fixed4(c, 1);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragPrefilter
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragDown
            ENDCG }
        Pass { Blend One One
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragUp
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment FragFinal
            ENDCG }
    }
}
