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

        a = saturate(a * _Strength);
        return float4(a, a, a, a);
    }

    // ---------------- 2D (texture space) dab ----------------
    float4 frag_stamp2d(ap_v2f_quad i) : SV_Target
    {
        float dist = length(i.local);
        float aa = max(fwidth(dist), 1e-4);
        float a = BrushShape(i.local, dist, aa);
        if (_ClipIslands > 0.5)
            a *= step(0.5, AP_SAMPLE_LOD(_IslandTex, i.uv, 0).r);
        a = saturate(a * _Strength * i.strength);
        return float4(a, a, a, a);
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
            #pragma vertex ap_vert_quad
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
    }
    Fallback Off
}
