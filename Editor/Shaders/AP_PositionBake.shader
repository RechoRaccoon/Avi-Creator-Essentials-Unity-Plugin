// Avi Creator Essentials - bakes a position-mapped texture (Poiyomi "Local Pos" / "World Pos") into UV space.
// Editor-only; never used by an uploaded avatar.
Shader "Hidden/AviCreatorEssentials/PositionBake"
{
    Properties
    {
        _PosTex ("", 2D) = "black" {}
        _SrcTex ("", 2D) = "white" {}
    }

    CGINCLUDE
    #include "APCommon.cginc"

    // ---------------- pass 0: position map ----------------
    // Draws the avatar's mesh at its UVs and stores, per texel, the position the avatar's shader sees.
    float _UseWorld;

    struct appdata_pos
    {
        float4 vertex : POSITION;
        float2 uv0 : TEXCOORD0;
    };

    struct v2f_pos
    {
        float4 pos : SV_POSITION;
        float3 p : TEXCOORD0;
    };

    v2f_pos vert_pos(appdata_pos v)
    {
        v2f_pos o;
        o.pos = AP_UVToClip(v.uv0);
        o.p = _UseWorld > 0.5 ? mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz : v.vertex.xyz;
        return o;
    }

    float4 frag_pos(v2f_pos i) : SV_Target
    {
        return float4(i.p, 1.0);
    }

    // ---------------- pass 1: sample the texture the way the shader did ----------------
    sampler2D _PosTex;
    sampler2D _SrcTex;
    float4 _SrcTex_TexelSize;
    float4 _SrcST;          // the material's tiling (xy) and offset (zw) for that texture
    float _AxisU, _AxisV;   // which position component becomes u / v (0 X, 1 Y, 2 Z)
    float _IsNormal;        // 1: tangent-space normal map
    float _Strength;        // normal strength baked into the result (1 = unchanged)
    float _ToGamma;         // 1: write gamma values for an sRGB color texture in a linear project

    float4 frag_sample(v2f_img i) : SV_Target
    {
        float4 P = tex2Dlod(_PosTex, float4(i.uv, 0, 0));
        if (P.a < 0.5) return float4(0, 0, 0, 0);
        float2 uv = float2(P[(int)_AxisU], P[(int)_AxisV]) * _SrcST.xy + _SrcST.zw;

        // Filter like the shader would at this size; across UV island edges the neighbour is empty, so fall back to full detail.
        float2 gx = ddx(uv), gy = ddy(uv);
        float lim = 8.0 * max(_SrcTex_TexelSize.x, _SrcTex_TexelSize.y);
        if (dot(gx, gx) > lim * lim) gx = 0;
        if (dot(gy, gy) > lim * lim) gy = 0;
        float4 c = tex2Dgrad(_SrcTex, uv, gx, gy);

        if (_IsNormal > 0.5)
        {
            float3 n;
            n.xy = UnpackNormal(c).xy * _Strength;
            n.z = sqrt(1.0 - saturate(dot(n.xy, n.xy)));
            n = normalize(n);
            return float4(n * 0.5 + 0.5, 1.0);
        }
        if (_ToGamma > 0.5) c.rgb = LinearToGammaSpace(c.rgb);
        return c;
    }
    ENDCG

    SubShader
    {
        ZTest Always ZWrite Off Cull Off

        // 0 - position map
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_pos
            #pragma fragment frag_pos
            ENDCG
        }

        // 1 - sample through the position map
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment frag_sample
            ENDCG
        }
    }
}
