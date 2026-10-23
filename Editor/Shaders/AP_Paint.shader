// Avi Creator Essentials - painting passes. Editor-only; never used by an uploaded avatar.
Shader "Hidden/AviCreatorEssentials/Paint"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
        _TipTex ("", 2D) = "white" {}
        _DepthTex ("", 2D) = "white" {}
        _IslandTex ("", 2D) = "white" {}
        _DecalTex ("", 2D) = "white" {}
        _UVTransform ("", Vector) = (1, 1, 0, 0)
    }

    CGINCLUDE
    #include "APCommon.cginc"

    // ---------------- brush parameters ----------------
    float3 _BrushPos;
    float _BrushRadius;
    float3 _BrushRight;
    float3 _BrushUp;
    float3 _BrushFwd;
    float _Hardness;
    float _Strength;
    float _ProjMode;        // 0 = sphere, 1 = projected (camera aligned cylinder)
    float _DepthRange;      // projected mode: half thickness, in brush radii
    float _UseTip;
    AP_TEX(_TipTex);

    float4x4 _ViewVP;       // GL-convention view-projection of the painting camera
    float3 _CamPos;
    AP_TEX(_DepthTex);
    float _UseOcclusion;
    float _OcclusionBias;
    float _BackfaceCos;
    float _UseBackface;

    AP_TEX(_IslandTex);
    float _ClipIslands;

    // ---------------- paint texture (bristles, canvas grain) ----------------
    float3 _BrushAcross;    // 3D: world direction across the stroke (bristles run along the stroke)
    float _Bristles;        // 0..1 how much the bristle streaks show
    float _BristleScale;    // bristles per brush radius
    float _BristleSeed;
    float _Grain;           // 0..1 canvas grain
    float _GrainScale;      // 3D: world size of one grain cell; 2D: texels
    float2 _GrainTexSize;   // 2D: texture size in texels
    float _PaintAccum;      // 1: output for the paint mode (summed coverage, coverage * bristle id)
    float _PaintGain;

    // One bristle per cell across the stroke, each with its own width, offset and strength. Filtered so bristles
    // thinner than a texel fade to their average instead of flickering. vary returns the bristle's id (0..1),
    // which the paint mode turns into a slight color difference per bristle.
    float AP_Bristles(float across, float acrossW, out float vary)
    {
        float x = across * _BristleScale + _BristleSeed;
        float cell = floor(x);
        float f = frac(x) - 0.5;
        float h1 = AP_Hash11(cell * 1.37 + 0.11);
        float h2 = AP_Hash11(cell * 2.71 + 5.3);
        float h3 = AP_Hash11(cell * 0.73 + 9.1);
        float halfW = lerp(0.16, 0.44, h2);
        float off = (h1 - 0.5) * (0.5 - halfW) * 2.0;
        float w = max(acrossW * _BristleScale, 1e-4);
        float strand = saturate((halfW - abs(f - off)) / w + 0.5);
        strand = lerp(strand, halfW * 2.0, saturate(w * 2.0 - 0.5));
        vary = h3;
        return strand * lerp(0.45, 1.0, h1 * 0.5 + h3 * 0.5);
    }

    float AP_PaintTexture(float across, float acrossW, float3 grainPos, out float vary)
    {
        float m = lerp(1.0, AP_Bristles(across, acrossW, vary), _Bristles);
        if (_Grain > 0.001)
        {
            float g = AP_ValueNoise(grainPos) * 0.6 + AP_ValueNoise(grainPos * 2.7 + 7.1) * 0.4;
            m *= lerp(1.0, saturate(g * 1.8 - 0.35), _Grain);
        }
        return m;
    }

    float4 AP_StampOut(float a, float vary)
    {
        if (_PaintAccum > 0.5)
        {
            float s = max(a, 0.0) * _Strength * _PaintGain;
            return float4(s, s * vary, 0, 0);
        }
        a = saturate(a * _Strength);
        return float4(a, a, a, a);
    }

    float BrushShape(float2 xy, float distForRound, float aa)
    {
        if (_UseTip > 0.5)
        {
            if (abs(xy.x) > 1.0 || abs(xy.y) > 1.0) return 0.0;
            return AP_SAMPLE_LOD(_TipTex, xy * 0.5 + 0.5, 0).r;
        }
        return AP_RoundFalloff(distForRound, _Hardness, aa);
    }

    float4 frag_stamp3d(ap_v2f_uv i) : SV_Target
    {
        float3 d = i.wpos - _BrushPos;
        float invR = 1.0 / max(_BrushRadius, 1e-6);
        float x = dot(d, _BrushRight) * invR;
        float y = dot(d, _BrushUp) * invR;
        float z = dot(d, _BrushFwd) * invR;
        float across = dot(d, _BrushAcross) * invR;
        float acrossW = fwidth(across);

        float dist = (_ProjMode < 0.5) ? length(float3(x, y, z)) : length(float2(x, y));
        float aa = max(fwidth(dist), 1e-4);
        float a = BrushShape(float2(x, y), dist, aa);

        if (_ProjMode > 0.5)
            a *= 1.0 - smoothstep(_DepthRange * 0.85, _DepthRange, abs(z));
        else if (_UseTip > 0.5)
            a *= saturate((1.0 - abs(z)) * 4.0);

        if (a <= 0.0) return 0;

        float3 n = normalize(i.wnrm);
        float3 v = normalize(_CamPos - i.wpos);
        if (_UseBackface > 0.5)
            a *= smoothstep(_BackfaceCos - 0.08, _BackfaceCos + 0.04, dot(n, v));

        if (_UseOcclusion > 0.5)
        {
            float4 c = mul(_ViewVP, float4(i.wpos, 1.0));
            if (c.w > 0.0)
            {
                float2 s = c.xy / c.w * 0.5 + 0.5;
                if (s.x >= 0.0 && s.y >= 0.0 && s.x <= 1.0 && s.y <= 1.0)
                {
                    float stored = AP_SAMPLE_LOD(_DepthTex, s, 0).r;
                    if (c.w > stored + _OcclusionBias + c.w * 0.004) a = 0.0;
                }
            }
        }

        float vary;
        a *= AP_PaintTexture(across, acrossW, i.wpos / max(_GrainScale, 1e-6), vary);
        return AP_StampOut(a, vary);
    }

    // ---------------- 2D (texture space) dab ----------------
    struct ap_appdata_dab
    {
        float4 vertex : POSITION;   // xy = position in UV space, z = per-dab strength
        float2 local : TEXCOORD0;   // -1..1 brush local coordinates
        float2 extra : TEXCOORD1;   // x = position across the stroke, in brush radii
    };

    struct v2f_dab
    {
        float4 pos : SV_POSITION;
        float2 local : TEXCOORD0;
        float2 uv : TEXCOORD1;
        float strength : TEXCOORD2;
        float across : TEXCOORD3;
    };

    v2f_dab ap_vert_dab(ap_appdata_dab v)
    {
        v2f_dab o;
        o.pos = AP_UVToClip(v.vertex.xy);
        o.local = v.local;
        o.uv = v.vertex.xy;
        o.strength = v.vertex.z;
        o.across = v.extra.x;
        return o;
    }

    float4 frag_stamp2d(v2f_dab i) : SV_Target
    {
        float acrossW = fwidth(i.across);
        float dist = length(i.local);
        float aa = max(fwidth(dist), 1e-4);
        float a = BrushShape(i.local, dist, aa);
        if (_ClipIslands > 0.5)
            a *= step(0.5, AP_SAMPLE_LOD(_IslandTex, i.uv, 0).r);
        float vary;
        float3 grainPos = float3(i.uv * _GrainTexSize / max(_GrainScale, 1e-4), 0.37);
        a *= AP_PaintTexture(i.across, acrossW, grainPos, vary);
        return AP_StampOut(a * i.strength, vary);
    }

    // ---------------- 3D gradient (premultiplied color, by world position) ----------------
    float3 _GradStart;
    float3 _GradDir;        // end - start, divided by its squared length
    float _GradLength;
    float _GradShape;       // 0 linear, 1 radial, 2 reflected
    float4 _GradColorA;     // straight alpha, gamma
    float4 _GradColorB;
    float _GradMirror;          // 1: fold the gradient across the avatar's mirror plane (symmetry)
    float _GradMirrorAxis;      // 0 X, 1 Y, 2 Z, in avatar space
    float _GradMirrorSide;      // +1/-1: the side of the plane the drag started on (avatar space)
    float4x4 _GradMirrorMatrix; // world -> mirrored world (through avatar space)
    float4x4 _GradWorldToLocal; // world -> avatar space

    float4 frag_gradient(ap_v2f_uv i) : SV_Target
    {
        float3 p = i.wpos;
        if (_GradMirror > 0.5)
        {
            // Fold the far side onto the drag's side, so it gets the mirror image of the gradient. On the
            // plane itself the folded point is the point, so the two sides meet without a seam.
            float3 lp = mul(_GradWorldToLocal, float4(p, 1.0)).xyz;
            float coord = _GradMirrorAxis < 0.5 ? lp.x : (_GradMirrorAxis < 1.5 ? lp.y : lp.z);
            if (coord * _GradMirrorSide < 0.0)
                p = mul(_GradMirrorMatrix, float4(p, 1.0)).xyz;
        }
        float3 d = p - _GradStart;
        float t;
        if (_GradShape < 0.5) t = dot(d, _GradDir);
        else if (_GradShape < 1.5) t = length(d) / max(_GradLength, 1e-6);
        else t = abs(dot(d, _GradDir));
        t = saturate(t);
        float4 a = float4(_GradColorA.rgb * _GradColorA.a, _GradColorA.a);
        float4 b = float4(_GradColorB.rgb * _GradColorB.a, _GradColorB.a);
        float4 c = lerp(a, b, t);
        // A little dither so smooth gradients do not band once stored in an 8-bit layer.
        c.rgb += (AP_Hash12(i.pos.xy) - 0.5) / 255.0 * c.a;
        return c;
    }

    // ---------------- coverage (island mask / triangle fills) ----------------
    float _Value;
    float4 frag_coverage(ap_v2f_uv i) : SV_Target
    {
        return float4(_Value, _Value, _Value, _Value);
    }

    // ---------------- projected image (decal) ----------------
    float3 _DecalPos;
    float3 _DecalRight;
    float3 _DecalUp;
    float3 _DecalNormal;
    float3 _DecalHalf;      // half width, half height, half depth (world units)
    float _DecalCos;
    float _FlipX;
    AP_TEX(_DecalTex);

    float4 frag_decal(ap_v2f_uv i) : SV_Target
    {
        float3 d = i.wpos - _DecalPos;
        float x = dot(d, _DecalRight) / max(_DecalHalf.x, 1e-6);
        float y = dot(d, _DecalUp) / max(_DecalHalf.y, 1e-6);
        float z = dot(d, _DecalNormal) / max(_DecalHalf.z, 1e-6);
        if (abs(x) > 1.0 || abs(y) > 1.0 || abs(z) > 1.0) return 0;

        float facing = dot(normalize(i.wnrm), _DecalNormal);
        float fade = smoothstep(_DecalCos, _DecalCos + 0.08, facing);
        if (fade <= 0.0) return 0;

        float2 tuv = float2(x, y) * 0.5 + 0.5;
        if (_FlipX > 0.5) tuv.x = 1.0 - tuv.x;
        float4 c = AP_SAMPLE_LOD(_DecalTex, tuv, 0);
        c.a *= fade;
        return float4(c.rgb * c.a, c.a);
    }

    // ---------------- depth of the painting view ----------------
    float4x4 _DepthVP;      // GPU-adjusted projection * view
    float4x4 _DepthV;

    struct v2f_depth
    {
        float4 pos : SV_POSITION;
        float eye : TEXCOORD0;
    };

    v2f_depth vert_depth(appdata_base v)
    {
        v2f_depth o;
        float4 wp = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0));
        o.pos = mul(_DepthVP, wp);
        o.eye = -mul(_DepthV, wp).z;
        return o;
    }

    float4 frag_depth(v2f_depth i) : SV_Target
    {
        return float4(i.eye, 0, 0, 1);
    }

    // ---------------- UV wireframe ----------------
    float4 _WireColor;
    float4 frag_wire(ap_v2f_uv i) : SV_Target
    {
        return _WireColor;
    }

    // ---------------- image placed in UV space ----------------
    AP_TEX(_MainTex);
    float4 frag_quadimage(ap_v2f_quad i) : SV_Target
    {
        float2 tuv = i.local * 0.5 + 0.5;
        if (_FlipX > 0.5) tuv.x = 1.0 - tuv.x;
        float4 c = AP_SAMPLE(_MainTex, tuv);
        if (_ClipIslands > 0.5)
            c.a *= step(0.5, AP_SAMPLE_LOD(_IslandTex, i.uv, 0).r);
        return c;
    }

    // ---------------- tiling image wrapped around the rest-pose model (triplanar) ----------------
    // Same idea as a shader's world/local-position mapping, but evaluated once on the rest pose and written into the
    // texture's own UV layout, so it stays stuck to the model when it moves.
    float _WrapSize;        // world size of one repeat
    float _WrapSharp;       // blend sharpness between the three projections
    float _WrapAxis;        // -1 = all three sides, 0/1/2 = only X/Y/Z
    float3 _WrapOffset;
    float4 _WrapRot;        // cos, sin of the image rotation
    float _WrapNormal;      // 1: the image is a tangent-space normal map
    float _WrapStrength;
    float _WrapGreenDown;
    float _WrapImageAspect; // image width / height
    float _WrapPlanar;      // 1: exact planar mapping copied from the avatar's shader (Local Pos / World Pos)
    float4 _WrapRowU;       // texture u = dot(row, (rest position, 1)), per renderer
    float4 _WrapRowV;

    struct ap_appdata_wrap
    {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float4 tangent : TANGENT;
        float4 uv0 : TEXCOORD0;
        float4 uv1 : TEXCOORD1;
        float4 uv2 : TEXCOORD2;
        float4 uv3 : TEXCOORD3;
    };

    struct v2f_wrap
    {
        float4 pos : SV_POSITION;
        float3 wpos : TEXCOORD0;
        float3 wnrm : TEXCOORD1;
        float4 wtan : TEXCOORD2;
    };

    v2f_wrap vert_wrap(ap_appdata_wrap v)
    {
        v2f_wrap o;
        float2 uv = v.uv0.xy;
        if (_UVChannel == 1) uv = v.uv1.xy;
        else if (_UVChannel == 2) uv = v.uv2.xy;
        else if (_UVChannel == 3) uv = v.uv3.xy;
        o.pos = AP_UVToClip(uv);
        o.wpos = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
        o.wnrm = UnityObjectToWorldNormal(v.normal);
        o.wtan = float4(UnityObjectToWorldDir(v.tangent.xyz), v.tangent.w);
        return o;
    }

    // Plane coordinates -> image coordinates (rotation, aspect, flip).
    float2 WrapUV(float2 pl)
    {
        float2 r = float2(pl.x * _WrapRot.x - pl.y * _WrapRot.y, pl.x * _WrapRot.y + pl.y * _WrapRot.x);
        r.y *= _WrapImageAspect;
        if (_FlipX > 0.5) r.x = -r.x;
        return r;
    }

    // Samples the normal map and expresses it along the plane's own axes (undoing WrapUV's transform).
    float3 WrapSampleNormal(float2 pl)
    {
        float3 n = AP_SAMPLE(_MainTex, WrapUV(pl)).rgb * 2.0 - 1.0;
        if (_WrapGreenDown > 0.5) n.y = -n.y;
        n.xy *= _WrapStrength;
        if (_FlipX > 0.5) n.x = -n.x;
        n.y *= _WrapImageAspect;
        n.xy = float2(_WrapRot.x * n.x + _WrapRot.y * n.y, -_WrapRot.y * n.x + _WrapRot.x * n.y);
        return normalize(float3(n.xy, max(n.z, 1e-4)));
    }

    float4 frag_wrap(v2f_wrap i) : SV_Target
    {
        if (_WrapPlanar > 0.5)
        {
            // The same projection the avatar's shader does, so the result is pixel-for-pixel what it showed.
            // Normal maps come out unchanged: the shader would have used them in the mesh's own tangent space too.
            float4 P = float4(i.wpos, 1.0);
            return AP_SAMPLE(_MainTex, float2(dot(_WrapRowU, P), dot(_WrapRowV, P)));
        }
        float3 N = normalize(i.wnrm);
        float3 p = (i.wpos + _WrapOffset) / max(_WrapSize, 1e-5);

        float3 w;
        if (_WrapAxis < -0.5) w = pow(abs(N), _WrapSharp);
        else w = float3(_WrapAxis < 0.5 ? 1.0 : 0.0, abs(_WrapAxis - 1.0) < 0.5 ? 1.0 : 0.0, _WrapAxis > 1.5 ? 1.0 : 0.0);
        w /= max(w.x + w.y + w.z, 1e-6);

        // Mirror the projection on the back sides so the image never reads backwards.
        float3 sgn = float3(N.x < 0.0 ? -1.0 : 1.0, N.y < 0.0 ? -1.0 : 1.0, N.z < 0.0 ? -1.0 : 1.0);
        float2 plX = float2(p.z * sgn.x, p.y);
        float2 plY = float2(p.x * sgn.y, p.z);
        float2 plZ = float2(-p.x * sgn.z, p.y);

        if (_WrapNormal < 0.5)
        {
            float4 c = AP_SAMPLE(_MainTex, WrapUV(plX)) * w.x
                     + AP_SAMPLE(_MainTex, WrapUV(plY)) * w.y
                     + AP_SAMPLE(_MainTex, WrapUV(plZ)) * w.z;
            return c;
        }

        // Whiteout-blended triplanar normal (world space).
        float3 tX = WrapSampleNormal(plX); tX.x *= sgn.x;
        float3 tY = WrapSampleNormal(plY); tY.x *= sgn.y;
        float3 tZ = WrapSampleNormal(plZ); tZ.x *= -sgn.z;
        tX = float3(tX.xy + N.zy, abs(tX.z) * N.x);
        tY = float3(tY.xy + N.xz, abs(tY.z) * N.y);
        tZ = float3(tZ.xy + N.xy, abs(tZ.z) * N.z);
        float3 W = normalize(tX.zyx * w.x + tY.xzy * w.y + tZ.xyz * w.z);

        // Into this mesh's tangent space, the same frame the avatar shader will decode it with.
        float3 T = i.wtan.xyz - N * dot(N, i.wtan.xyz);
        if (dot(T, T) < 1e-10)
            T = abs(N.y) < 0.99 ? cross(float3(0, 1, 0), N) : cross(float3(1, 0, 0), N);
        T = normalize(T);
        float3 B = cross(N, T) * (i.wtan.w < 0.0 ? -1.0 : 1.0);
        float3 ts = normalize(float3(dot(W, T), dot(W, B), dot(W, N)));
        return float4(ts * 0.5 + 0.5, 1.0);
    }
    // ---------------- star field ----------------
    // Stars are scattered through 3D space in cells; the ones near the surface show. Because the position comes
    // from the mesh (rest pose), they spread evenly across the model whatever its UV layout, without seams.
    float _StarCell;        // size of one cell (world units on the model, or one tile in flat mode)
    float _StarAmount;      // chance a cell holds a star
    float _StarSize;        // core radius, fraction of a cell
    float _StarSizeVar;
    float _StarGlow;        // glow radius, multiple of the core
    float _StarGlowStrength;
    float _StarSparkle;     // share of stars with four-point flares
    float _StarFlare;       // flare length, multiple of the core
    float _StarWhite;       // how white the star cores are
    float _StarSeed;
    float4 _StarColorA;
    float4 _StarColorB;
    float _Nebula;          // soft glowing clouds under the stars
    float _NebulaScale;     // in cells
    float _StarPeriod;      // flat mode: cells across the texture (makes it tile); 0 = no tiling
    float _StarMatcap;      // flat mode: draw as a matcap disc with a glowing rim

    void AP_StarField(float3 p, float3 N, float3 up, float3 rt, float aa, bool isFlat, out float3 color, out float alpha)
    {
        float3 base = floor(p);
        float sumI = 0.0;
        float3 sumC = float3(0, 0, 0);
        int zr = isFlat ? 0 : 1;
        [loop] for (int oz = -zr; oz <= zr; oz++)
        {
        [unroll] for (int oy = -1; oy <= 1; oy++)
        {
        [unroll] for (int ox = -1; ox <= 1; ox++)
        {
            float3 c = base + float3(ox, oy, oz);
            float3 key = c;
            if (_StarPeriod > 0.5) key.xy = c.xy - _StarPeriod * floor(c.xy / _StarPeriod);
            key += _StarSeed * float3(1.731, 2.917, 0.413);
            float3 h = AP_Hash33(key);
            float3 h2 = AP_Hash33(key + 19.19);
            float3 sp = c + 0.15 + h2 * 0.7;
            if (isFlat) sp.z = p.z;
            float3 v = p - sp;
            float hgt = dot(v, N);
            float vis = 1.0 - smoothstep(0.25, 0.5, abs(hgt));
            if (h.x <= _StarAmount && vis > 0.0)
            {
                float2 q = float2(dot(v, rt), dot(v, up));
                float dq = length(q);

                float smallK = h.y * h.y * h.y * 1.6 + 0.15;
                float r = _StarSize * lerp(1.0, smallK, _StarSizeVar);
                float bright = lerp(0.55, 1.0, h.z);

                float core = saturate((r - dq) / max(aa + r * 0.35, 1e-4));
                float gr = min(r * _StarGlow, 0.5);
                float glow = _StarGlowStrength * exp(-dq * dq / max(gr * gr, 1e-6));
                float flare = 0.0;
                if (h2.z < _StarSparkle)
                {
                    float fl = min(r * _StarFlare, 0.95);
                    float w = max(r * 0.2, aa * 0.6);
                    float ax = saturate(abs(q.x) / fl);
                    float ay = saturate(abs(q.y) / fl);
                    float wx = w * (1.0 - ax) + 1e-5;
                    float wy = w * (1.0 - ay) + 1e-5;
                    flare = (1.0 - ax) * (1.0 - ax) * exp(-q.y * q.y / (wx * wx))
                          + (1.0 - ay) * (1.0 - ay) * exp(-q.x * q.x / (wy * wy));
                }
                float window = 1.0 - smoothstep(0.75, 1.0, dq);
                float I = (core + glow + flare) * vis * window * bright;
                float3 tint = lerp(_StarColorA.rgb, _StarColorB.rgb, h2.y);
                float3 col = lerp(tint, float3(1, 1, 1), saturate(core + flare * 0.5) * _StarWhite);
                sumC += col * I;
                sumI += I;
            }
        }
        }
        }
        alpha = saturate(sumI);
        color = sumI > 1e-5 ? saturate(sumC / sumI) : _StarColorA.rgb;

        if (_Nebula > 0.001)
        {
            float3 np = p / max(_NebulaScale, 1e-3) + _StarSeed * 0.137;
            float n = AP_ValueNoise(np) * 0.55 + AP_ValueNoise(np * 2.1 + 3.7) * 0.3 + AP_ValueNoise(np * 4.3 + 9.1) * 0.15;
            float n2 = AP_ValueNoise(np * 0.6 + 31.7);
            float an = saturate((n - 0.42) * 2.6) * _Nebula;
            float3 cn = lerp(_StarColorA.rgb, _StarColorB.rgb, n2);
            float ao = alpha + an * (1.0 - alpha);
            color = ao > 1e-5 ? (color * alpha + cn * an * (1.0 - alpha)) / ao : color;
            alpha = ao;
        }
    }

    // On the model: written into the texture's UV layout, positions from the rest pose (like Local Pos).
    float4 frag_stars(v2f_wrap i) : SV_Target
    {
        float3 p = i.wpos / max(_StarCell, 1e-5);
        float aa = length(fwidth(p));
        float3 N = normalize(i.wnrm);
        float3 up = float3(0, 1, 0) - N * N.y;
        if (dot(up, up) < 1e-4) up = float3(0, 0, 1) - N * N.z;
        up = normalize(up);
        float3 rt = cross(up, N);
        float3 color;
        float alpha;
        AP_StarField(p, N, up, rt, aa, false, color, alpha);
        return float4(color, alpha);
    }

    // Flat: a tiling star texture, or a matcap, for shaders that move the stars themselves (parallax, matcap).
    float4 frag_stars_flat(v2f_img i) : SV_Target
    {
        float2 uv = i.uv;
        float3 p = float3(uv * _StarPeriod, 0.5);
        if (_StarPeriod < 0.5) p = float3(uv / max(_StarCell, 1e-4), 0.5);
        float aa = length(fwidth(p));
        float rim = 0.0;
        if (_StarMatcap > 0.5)
        {
            float2 c = uv * 2.0 - 1.0;
            float r = length(c);
            if (r > 1.0) return float4(0, 0, 0, 1);
            rim = pow(saturate((r - 0.55) / 0.45), 2.2);
        }
        float3 color;
        float alpha;
        AP_StarField(p, float3(0, 0, 1), float3(0, 1, 0), float3(1, 0, 0), aa, true, color, alpha);
        float3 outC = color * alpha;
        if (_StarMatcap > 0.5)
            outC += lerp(_StarColorA.rgb, _StarColorB.rgb, 0.5) * rim * _StarGlowStrength;
        return float4(saturate(outC), 1.0);
    }

    ENDCG

    SubShader
    {
        ZTest Always ZWrite Off Cull Off

        // 0 - 3D brush dab, rasterised in UV space
        Pass
        {
            BlendOp Max
            Blend One One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_stamp3d
            ENDCG
        }

        // 1 - 2D brush dab
        Pass
        {
            BlendOp Max
            Blend One One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_dab
            #pragma fragment frag_stamp2d
            ENDCG
        }

        // 2 - coverage
        Pass
        {
            BlendOp Max
            Blend One One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_coverage
            ENDCG
        }

        // 3 - projected decal (premultiplied, max-combined so mirrored UVs do not flicker)
        Pass
        {
            BlendOp Max
            Blend One One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_decal
            ENDCG
        }

        // 4 - view depth (linear eye depth)
        Pass
        {
            ZTest LEqual ZWrite On Cull Off
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_depth
            #pragma fragment frag_depth
            ENDCG
        }

        // 5 - UV wireframe
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_wire
            ENDCG
        }

        // 6 - image quad in UV space
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_quad
            #pragma fragment frag_quadimage
            ENDCG
        }

        // 7 - 3D brush dab, conservative rasterisation (covers thin/sliver triangles)
        Pass
        {
            BlendOp Max
            Blend One One
            Conservative True
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_stamp3d
            ENDCG
        }

        // 8 - coverage, conservative
        Pass
        {
            BlendOp Max
            Blend One One
            Conservative True
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_coverage
            ENDCG
        }

        // 9 - decal, conservative
        Pass
        {
            BlendOp Max
            Blend One One
            Conservative True
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_decal
            ENDCG
        }

        // 10 - wrapped (triplanar) image on the rest-pose mesh
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_wrap
            #pragma fragment frag_wrap
            ENDCG
        }

        // 11 - wrapped image, conservative
        Pass
        {
            Blend Off
            Conservative True
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_wrap
            #pragma fragment frag_wrap
            ENDCG
        }

        // 12 - 3D brush dab, paint mode: coverage is summed so overlapping dabs build up like real paint
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_stamp3d
            ENDCG
        }

        // 13 - 3D dab, paint mode, conservative and max-combined: only fills texels the exact pass missed
        // (thin triangles, island borders), so shared triangle edges are never counted twice
        Pass
        {
            BlendOp Max
            Blend One One
            Conservative True
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_stamp3d
            ENDCG
        }

        // 14 - 2D dab, paint mode (summed)
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_dab
            #pragma fragment frag_stamp2d
            ENDCG
        }

        // 15 - 3D gradient
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_gradient
            ENDCG
        }

        // 16 - 3D gradient, conservative
        Pass
        {
            Blend Off
            Conservative True
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex ap_vert_uv
            #pragma fragment frag_gradient
            ENDCG
        }

        // 17 - stars on the rest-pose mesh
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_wrap
            #pragma fragment frag_stars
            ENDCG
        }

        // 18 - stars, conservative
        Pass
        {
            Blend Off
            Conservative True
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_wrap
            #pragma fragment frag_stars
            ENDCG
        }

        // 19 - flat star texture (tiling or matcap)
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag_stars_flat
            ENDCG
        }
    }
    Fallback Off
}
