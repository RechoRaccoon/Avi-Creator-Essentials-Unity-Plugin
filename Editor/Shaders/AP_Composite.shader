// Avi Creator Essentials - full-screen image operations (layer compositing, stroke application,
// seam padding, display output). Editor-only.
Shader "Hidden/AviCreatorEssentials/Composite"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
        _LayerTex ("", 2D) = "white" {}
        _MaskTex ("", 2D) = "white" {}
        _StrokeTex ("", 2D) = "black" {}
        _ValidTex ("", 2D) = "white" {}
        _WireTex ("", 2D) = "black" {}
        _SeedTex ("", 2D) = "white" {}
        _SelTex ("", 2D) = "white" {}
    }

    CGINCLUDE
    #include "APCommon.cginc"

    AP_TEX(_MainTex);
    float4 _MainTex_TexelSize;
    AP_TEX(_LayerTex);
    AP_TEX(_MaskTex);
    AP_TEX(_StrokeTex);
    AP_TEX(_ValidTex);
    AP_TEX(_SelTex);
    float _UseSel;        // apply: limit the stroke to the UV selection
    float _SelOverlay;    // output / 2D view: dim everything outside the UV selection

    float3 AP_DimUnselected(float3 c, float2 uv)
    {
        float sel = AP_SAMPLE_LOD(_SelTex, uv, 0).r;
        float l = dot(c, float3(0.3, 0.59, 0.11));
        float3 dim = lerp(c, l.xxx, 0.5) * 0.38;
        return lerp(dim, c, sel);
    }

    // ---------------- copy ----------------
    float _CopyMode;     // 0 rgba, 1 red -> all, 2 invert red, 3 luminance*alpha -> all
    float _SrcToGamma;   // 1: source was decoded to linear by the sampler, re-encode to gamma

    float4 frag_copy(v2f_img i) : SV_Target
    {
        float4 c = AP_SAMPLE_LOD(_MainTex, i.uv, 0);
        if (_SrcToGamma > 0.5) c.rgb = LinearToGammaSpace(c.rgb);
        if (_CopyMode > 3.5)
        {
            // packed normal map (DXT5nm / BC5) -> plain RGB normal
            float3 n = UnpackNormalmapRGorAG(c);
            return float4(n * 0.5 + 0.5, 1);
        }
        if (_CopyMode > 2.5)
        {
            float l = dot(c.rgb, float3(0.299, 0.587, 0.114)) * c.a;
            return float4(l, l, l, 1);
        }
        if (_CopyMode > 1.5) { float v = 1.0 - c.r; return float4(v, v, v, 1); }
        if (_CopyMode > 0.5) return float4(c.r, c.r, c.r, 1);
        return c;
    }

    // ---------------- blend modes (gamma space, W3C definitions) ----------------
    float AP_Lum(float3 c) { return dot(c, float3(0.3, 0.59, 0.11)); }

    float3 AP_ClipColor(float3 c)
    {
        float l = AP_Lum(c);
        float n = min(c.r, min(c.g, c.b));
        float x = max(c.r, max(c.g, c.b));
        if (n < 0.0) c = l + (c - l) * l / max(l - n, 1e-5);
        if (x > 1.0) c = l + (c - l) * (1.0 - l) / max(x - l, 1e-5);
        return c;
    }

    float3 AP_SetLum(float3 c, float l) { return AP_ClipColor(c + (l - AP_Lum(c))); }

    float AP_Sat(float3 c) { return max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b)); }

    float3 AP_SetSat(float3 c, float s)
    {
        float mx = max(c.r, max(c.g, c.b));
        float mn = min(c.r, min(c.g, c.b));
        float range = mx - mn;
        if (range <= 1e-5) return float3(0, 0, 0);
        return (c - mn) * s / range;
    }

    float3 AP_SoftLightD(float3 b)
    {
        float3 lo = ((16.0 * b - 12.0) * b + 4.0) * b;
        float3 hi = sqrt(b);
        return lerp(lo, hi, step(0.25, b));
    }

    float3 AP_HardLight(float3 b, float3 s)
    {
        float3 m = 2.0 * b * s;
        float3 sc = 1.0 - 2.0 * (1.0 - b) * (1.0 - s);
        return lerp(m, sc, step(0.5, s));
    }

    float3 AP_Blend(float3 b, float3 s, int mode)
    {
        if (mode == 1) return b * s;
        if (mode == 2) return 1.0 - (1.0 - b) * (1.0 - s);
        if (mode == 3) return AP_HardLight(s, b);
        if (mode == 4)
        {
            float3 lo = b - (1.0 - 2.0 * s) * b * (1.0 - b);
            float3 hi = b + (2.0 * s - 1.0) * (AP_SoftLightD(b) - b);
            return lerp(lo, hi, step(0.5, s));
        }
        if (mode == 5) return AP_HardLight(b, s);
        if (mode == 6) return min(b, s);
        if (mode == 7) return max(b, s);
        if (mode == 8)
        {
            float3 r = min(1.0, b / max(1.0 - s, 1e-5));
            r = lerp(r, 0.0, step(b, 0.0));
            return lerp(r, 1.0, step(1.0, s) * step(1e-5, b));
        }
        if (mode == 9)
        {
            float3 r = 1.0 - min(1.0, (1.0 - b) / max(s, 1e-5));
            r = lerp(r, 0.0, step(s, 0.0));
            return lerp(r, 1.0, step(1.0, b));
        }
        if (mode == 10) return min(1.0, b + s);
        if (mode == 11) return max(0.0, b - s);
        if (mode == 12) return abs(b - s);
        if (mode == 13) return AP_SetLum(AP_SetSat(s, AP_Sat(b)), AP_Lum(b));
        if (mode == 14) return AP_SetLum(AP_SetSat(b, AP_Sat(s)), AP_Lum(b));
        if (mode == 15) return AP_SetLum(s, AP_Lum(b));
        if (mode == 16) return AP_SetLum(b, AP_Lum(s));
        if (mode == 17)
        {
            // Reoriented normal mapping: the layer's bumps follow the normal map underneath.
            float3 t = b * float3(2, 2, 2) + float3(-1, -1, 0);
            float3 u = s * float3(-2, -2, 2) + float3(1, 1, -1);
            float3 r = t * dot(t, u) / max(t.z, 1e-4) - u;
            return normalize(r) * 0.5 + 0.5;
        }
        return s;
    }

    // ---------------- layer compositing ----------------
    float _IsFill;
    float4 _FillColor;
    float _HasMask;
    float _Opacity;
    int _BlendMode;

    float4 frag_layer(v2f_img i) : SV_Target
    {
        float4 dst = AP_SAMPLE_LOD(_MainTex, i.uv, 0);
        float4 src = (_IsFill > 0.5) ? _FillColor : AP_SAMPLE_LOD(_LayerTex, i.uv, 0);
        float m = (_HasMask > 0.5) ? AP_SAMPLE_LOD(_MaskTex, i.uv, 0).r : 1.0;
        float as = saturate(src.a * _Opacity * m);
        float ab = dst.a;
        float3 blended = AP_Blend(dst.rgb, src.rgb, _BlendMode);
        float3 cs = lerp(src.rgb, blended, ab);
        float ao = as + ab * (1.0 - as);
        float3 co = (ao > 1e-5) ? (as * cs + ab * (1.0 - as) * dst.rgb) / ao : float3(0, 0, 0);
        return float4(saturate(co), ao);
    }

    // ---------------- stroke application ----------------
    float4 _BrushColor;   // gamma rgb, straight alpha
    float _ApplyMode;     // 0 paint, 1 erase, 2 mask value
    float _LockAlpha;
    float _MaskValue;

    float4 frag_apply(v2f_img i) : SV_Target
    {
        float4 b = AP_SAMPLE_LOD(_MainTex, i.uv, 0);
        float s = saturate(AP_SAMPLE_LOD(_StrokeTex, i.uv, 0).r) * _Opacity;
        if (_UseSel > 0.5) s *= AP_SAMPLE_LOD(_SelTex, i.uv, 0).r;
        if (_ApplyMode > 1.5)
        {
            float v = lerp(b.r, _MaskValue, s);
            return float4(v, v, v, 1);
        }
        if (_ApplyMode > 0.5)
        {
            return float4(b.rgb, b.a * (1.0 - s));
        }
        float k = s * _BrushColor.a;
        if (_LockAlpha > 0.5)
            return float4(lerp(b.rgb, _BrushColor.rgb, k), b.a);
        float a = k + b.a * (1.0 - k);
        float3 rgb = (a > 1e-5) ? (_BrushColor.rgb * k + b.rgb * b.a * (1.0 - k)) / a : _BrushColor.rgb;
        return float4(rgb, a);
    }

    // ---------------- stroke accumulation (flow) ----------------
    float4 frag_accum(v2f_img i) : SV_Target
    {
        float s = AP_SAMPLE_LOD(_MainTex, i.uv, 0).r;
        return float4(s, s, s, s);
    }

    // ---------------- seam padding (jump flood nearest-island lookup) ----------------
    // The seed map stores, for every texel, the UV of the nearest texel covered by the mesh.
    // (1,1) means "no seed yet". Built once per texture set; reused for every composite.
    AP_TEX(_SeedTex);
    float _JumpStep;     // in texels
    float _MaxPad;       // padding distance in texels

    float4 frag_jfa_init(v2f_img i) : SV_Target
    {
        float v = AP_SAMPLE_LOD(_ValidTex, i.uv, 0).r;
        return (v > 0.5) ? float4(i.uv, 0, 1) : float4(1, 1, 0, 1);
    }

    float4 frag_jfa_step(v2f_img i) : SV_Target
    {
        float2 texSize = _MainTex_TexelSize.zw;
        float2 best = float2(1, 1);
        float bestD = 1e20;
        [unroll] for (int y = -1; y <= 1; y++)
        {
            [unroll] for (int x = -1; x <= 1; x++)
            {
                float2 o = float2(x, y) * _JumpStep * _MainTex_TexelSize.xy;
                float2 q = i.uv + o;
                if (q.x < 0.0 || q.y < 0.0 || q.x > 1.0 || q.y > 1.0) continue;
                float2 s = AP_SAMPLE_LOD(_MainTex, q, 0).xy;
                if (s.x > 0.99999 && s.y > 0.99999) continue;
                float2 dp = (s - i.uv) * texSize;
                float d = dot(dp, dp);
                if (d < bestD) { bestD = d; best = s; }
            }
        }
        return float4(best, 0, 1);
    }

    float4 frag_remap(v2f_img i) : SV_Target
    {
        float4 c = AP_SAMPLE_LOD(_MainTex, i.uv, 0);
        if (AP_SAMPLE_LOD(_ValidTex, i.uv, 0).r > 0.5) return c;
        float2 s = AP_SAMPLE_LOD(_SeedTex, i.uv, 0).xy;
        if (s.x > 0.99999 && s.y > 0.99999) return c;
        float2 dp = (s - i.uv) * _MainTex_TexelSize.zw;
        if (dot(dp, dp) > _MaxPad * _MaxPad) return c;
        return AP_SAMPLE_LOD(_MainTex, s, 0);
    }

    // ---------------- display output ----------------
    float _ViewMode;     // 0 normal, 1 mask (grayscale)
    float _ToLinear;     // write into an sRGB target from gamma data

    float4 frag_output(v2f_img i) : SV_Target
    {
        float4 c = AP_SAMPLE_LOD(_MainTex, i.uv, 0);
        if (_ViewMode > 0.5)
        {
            float m = AP_SAMPLE_LOD(_MaskTex, i.uv, 0).r;
            c = float4(m, m, m, 1);
        }
        if (_SelOverlay > 0.5) c.rgb = AP_DimUnselected(c.rgb, i.uv);
        if (_ToLinear > 0.5) c.rgb = GammaToLinearSpace(c.rgb);
        return c;
    }

    // ---------------- 2D texture view ----------------
    float4 _UVMap;       // uv = viewportUV * xy + zw
    float4 _ViewSize;    // viewport size in pixels
    float4 _TexSize;     // texture size in texels
    float _ShowAlpha;
    float _PointSample;
    float _Tile;
    float _CheckerSize;
    float4 _CheckerA;
    float4 _CheckerB;
    float4 _OutsideColor;
    float _OverlayMaskView;
    float4 _OverlayColor;

    float4 frag_view2d(v2f_img i) : SV_Target
    {
        float2 uv = i.uv * _UVMap.xy + _UVMap.zw;
        bool inside = uv.x >= 0.0 && uv.y >= 0.0 && uv.x <= 1.0 && uv.y <= 1.0;
        float2 px = i.uv * _ViewSize.xy;
        float2 cell = floor(px / max(_CheckerSize, 1.0));
        float chk = fmod(cell.x + cell.y, 2.0);
        float3 bg = lerp(_CheckerA.rgb, _CheckerB.rgb, chk);
        if (!inside && _Tile < 0.5) return float4(_OutsideColor.rgb, 1);
        float2 suv = frac(uv);
        if (inside) suv = uv;
        float4 c;
        if (_PointSample > 0.5)
        {
            float2 t = (floor(suv * _TexSize.xy) + 0.5) / _TexSize.xy;
            c = AP_SAMPLE_LOD(_MainTex, t, 0);
        }
        else
        {
            c = AP_SAMPLE(_MainTex, suv);
        }
        float3 rgb = (_ShowAlpha > 0.5) ? lerp(bg, c.rgb, c.a) : c.rgb;
        if (_OverlayMaskView > 0.5)
        {
            float m = AP_SAMPLE_LOD(_MaskTex, suv, 0).r;
            rgb = lerp(rgb, _OverlayColor.rgb, (1.0 - m) * _OverlayColor.a);
        }
        if (_SelOverlay > 0.5) rgb = AP_DimUnselected(rgb, suv);
        if (!inside) rgb *= 0.55;
        return float4(rgb, 1);
    }

    // ---------------- adjustment layers ----------------
    float _AdjMode;       // 0 recolor, 1 hue/sat/light, 2 match
    float4 _AdjColor;
    float4 _AdjHSL;       // x hue shift (0..1 turns), y saturation, z lightness
    float4 _SrcMean;
    float4 _SrcStd;
    float4 _RefMean;
    float4 _RefStd;
    float _RefValid;

    float3 AP_RGBtoHSV(float3 c)
    {
        float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
        float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
        float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
        float d = q.x - min(q.w, q.y);
        float e = 1.0e-10;
        return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
    }

    float3 AP_HSVtoRGB(float3 c)
    {
        float3 p = abs(frac(c.xxx + float3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
        return c.z * lerp(float3(1, 1, 1), saturate(p - 1.0), c.y);
    }

    float _UseLut;
    AP_TEX(_LutTex);

    float3 AP_ToYCC(float3 c)
    {
        float y = dot(c, float3(0.299, 0.587, 0.114));
        return float3(y, (c.b - y) * 0.564 + 0.5, (c.r - y) * 0.713 + 0.5);
    }

    float3 AP_FromYCC(float3 q)
    {
        float y = q.x;
        float r = y + (q.z - 0.5) / 0.713;
        float b = y + (q.y - 0.5) / 0.564;
        float g = (y - 0.299 * r - 0.114 * b) / 0.587;
        return float3(r, g, b);
    }

    // Looks a 0..1 value up in the 256-entry table (texel centres line up with the histogram bins).
    float AP_Lut(float x, int ch)
    {
        float u = saturate(x) * (255.0 / 256.0) + 0.5 / 256.0;
        float4 t = AP_SAMPLE_LOD(_LutTex, float2(u, 0.5), 0);
        return ch == 0 ? t.r : (ch == 1 ? t.g : t.b);
    }

    float4 frag_adjust(v2f_img i) : SV_Target
    {
        float4 d = AP_SAMPLE_LOD(_MainTex, i.uv, 0);
        float m = (_HasMask > 0.5) ? AP_SAMPLE_LOD(_MaskTex, i.uv, 0).r : 1.0;
        float k = saturate(m * _Opacity);
        float3 c = d.rgb;
        float3 a = c;
        if (_AdjMode < 0.5)
        {
            // Recolor: the area's average becomes the chosen color, light and dark detail is kept.
            float srcL = max(AP_Lum(_SrcMean.rgb), 0.03);
            float l = AP_Lum(c) * AP_Lum(_AdjColor.rgb) / srcL;
            a = AP_SetLum(_AdjColor.rgb, l);
        }
        else if (_AdjMode < 1.5)
        {
            float3 hsv = AP_RGBtoHSV(c);
            hsv.x = frac(hsv.x + _AdjHSL.x + 1.0);
            hsv.y = saturate(hsv.y * (1.0 + _AdjHSL.y));
            a = AP_HSVtoRGB(hsv);
            a = (_AdjHSL.z >= 0.0) ? lerp(a, float3(1, 1, 1), _AdjHSL.z) : a * (1.0 + _AdjHSL.z);
        }
        else if (_RefValid > 0.5 && _UseLut > 0.5)
        {
            // Match: histogram matching in luma/chroma, so every shade lands on the reference's shade at the same percentile.
            float3 q = AP_ToYCC(c);
            q = float3(AP_Lut(q.x, 0), AP_Lut(q.y, 1), AP_Lut(q.z, 2));
            a = AP_FromYCC(q);
        }
        else if (_RefValid > 0.5)
        {
            // Match: shift and scale each channel so this area's color statistics equal the reference area's.
            a = (c - _SrcMean.rgb) / max(_SrcStd.rgb, 0.02) * _RefStd.rgb + _RefMean.rgb;
        }
        a = saturate(a);
        return float4(lerp(c, a, k), d.a);
    }

    // ---------------- mirror (flip half the texture, or the selection, onto the other side) ----------------
    float _MirrorMode;    // 0 left->right, 1 right->left, 2 bottom->top, 3 top->bottom

    float4 frag_mirror(v2f_img i) : SV_Target
    {
        bool horizontal = _MirrorMode < 1.5;
        float2 suv = horizontal ? float2(1.0 - i.uv.x, i.uv.y) : float2(i.uv.x, 1.0 - i.uv.y);
        float keep;
        if (_UseSel > 0.5)
        {
            // Selected area -> its flipped spot.
            keep = step(0.5, AP_SAMPLE_LOD(_SelTex, suv, 0).r);
        }
        else
        {
            float d = horizontal ? i.uv.x : i.uv.y;
            keep = (_MirrorMode < 0.5 || (_MirrorMode > 1.5 && _MirrorMode < 2.5)) ? step(0.5, d) : step(d, 0.5);
        }
        if (keep < 0.5) return 0;
        float4 c = AP_SAMPLE_LOD(_MainTex, suv, 0);
        return float4(c.rgb, 1.0);
    }

    // ---------------- misc ----------------
    float4 frag_unpremultiply(v2f_img i) : SV_Target
    {
        float4 c = AP_SAMPLE_LOD(_MainTex, i.uv, 0);
        return (c.a > 1e-5) ? float4(saturate(c.rgb / c.a), c.a) : float4(0, 0, 0, 0);
    }

    float4 _TopColor;
    float4 _BottomColor;
    float4 frag_gradient(v2f_img i) : SV_Target
    {
        float t = i.uv.y;
        t = t * t * (3.0 - 2.0 * t);
        return float4(lerp(_BottomColor.rgb, _TopColor.rgb, t), 1);
    }

    float4 frag_fill(v2f_img i) : SV_Target
    {
        return _FillColor;
    }

    // Composites a texture over the destination using its own alpha.
    float4 frag_over(v2f_img i) : SV_Target
    {
        return AP_SAMPLE_LOD(_MainTex, i.uv, 0);
    }
    ENDCG

    SubShader
    {
        ZTest Always ZWrite Off Cull Off

        // 0 copy
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_copy
            ENDCG
        }
        // 1 layer
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_layer
            ENDCG
        }
        // 2 apply stroke
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_apply
            ENDCG
        }
        // 3 accumulate dab into stroke (screen blend)
        Pass
        {
            Blend OneMinusDstColor One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_accum
            ENDCG
        }
        // 4 jump flood: init
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_jfa_init
            ENDCG
        }
        // 5 jump flood: step
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_jfa_step
            ENDCG
        }
        // 6 output to display
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_output
            ENDCG
        }
        // 7 2D view
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_view2d
            ENDCG
        }
        // 8 unpremultiply
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_unpremultiply
            ENDCG
        }
        // 9 gradient
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_gradient
            ENDCG
        }
        // 10 constant fill
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_fill
            ENDCG
        }
        // 11 alpha-blended overlay
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_over
            ENDCG
        }

        // 12 seam padding: pull colors from the nearest covered texel
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_remap
            ENDCG
        }

        // 13 adjustment layer
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_adjust
            ENDCG
        }

        // 14 mirror
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_mirror
            ENDCG
        }
    }
    Fallback Off
}
