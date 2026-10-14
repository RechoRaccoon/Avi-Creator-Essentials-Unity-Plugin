// Avi Creator Essentials - flattens a material's textures into one main texture and one emission texture.
// Editor-only; never used by an uploaded avatar.
Shader "Hidden/AviCreatorEssentials/Flatten"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    CGINCLUDE
    #include "APCommon.cginc"

    // ---------------- pass 0: geometry maps in UV space ----------------
    // Mesh prepared on the CPU: vertex = world position, normal/tangent = world space, uv0 = layout, uv1 = local position.
    float _Out;     // 0 local position, 1 world position, 2 world normal, 3 world tangent (w = handedness)

    struct appdata_geo
    {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float4 tangent : TANGENT;
        float2 uv0 : TEXCOORD0;
        float3 uv1 : TEXCOORD1;
    };

    struct v2f_geo
    {
        float4 pos : SV_POSITION;
        float4 v : TEXCOORD0;
    };

    v2f_geo vert_geo(appdata_geo v)
    {
        v2f_geo o;
        o.pos = AP_UVToClip(v.uv0);
        if (_Out < 0.5) o.v = float4(v.uv1, 1);
        else if (_Out < 1.5) o.v = float4(v.vertex.xyz, 1);
        else if (_Out < 2.5) o.v = float4(v.normal, 1);
        else o.v = v.tangent;
        return o;
    }

    float4 frag_geo(v2f_geo i) : SV_Target
    {
        if (_Out > 2.5) return float4(i.v.xyz, i.v.w < 0 ? 0.25 : 1.0);   // alpha: coverage, 0.25 = mirrored tangent
        if (_Out > 1.5) return float4(normalize(i.v.xyz), 1);
        return float4(i.v.xyz, 1);
    }

    // ---------------- pass 1: sample one texture as the shader maps it ----------------
    sampler2D _PosTex;      // position map (local or world), unused for UV0 mapping
    sampler2D _SrcTex;
    float4 _SrcTex_TexelSize;
    float4 _SrcST;
    float _Mapping;         // 0 UV0, 1 position map
    float _AxisU, _AxisV;
    float _IsNormal;
    float _Strength;

    float4 frag_sample(v2f_img i) : SV_Target
    {
        float2 uv;
        if (_Mapping > 0.5)
        {
            float4 P = tex2Dlod(_PosTex, float4(i.uv, 0, 0));
            if (P.a < 0.5) return float4(0.5, 0.5, 1, 0);
            uv = float2(P[(int)_AxisU], P[(int)_AxisV]);
        }
        else uv = i.uv;
        uv = uv * _SrcST.xy + _SrcST.zw;
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
            return float4(normalize(n) * 0.5 + 0.5, 1);
        }
        return c;   // linear color in a linear project (sRGB textures are decoded when sampled)
    }

    // ---------------- pass 2: combine two tangent-space normals (whiteout blend) ----------------
    sampler2D _NormA, _NormB;

    float4 frag_combine(v2f_img i) : SV_Target
    {
        float3 a = tex2Dlod(_NormA, float4(i.uv, 0, 0)).xyz * 2 - 1;
        float3 b = tex2Dlod(_NormB, float4(i.uv, 0, 0)).xyz * 2 - 1;
        float3 n = normalize(float3(a.xy + b.xy, a.z * b.z));
        return float4(n * 0.5 + 0.5, 1);
    }

    // ---------------- pass 3: main texture ----------------
    sampler2D _MainS;       // sampled main texture
    sampler2D _NormS;       // combined tangent-space normal
    sampler2D _GeoN, _GeoT; // world normal, world tangent
    float4 _MainColor;      // linear
    float _HasMain, _HasNormal, _Shade;
    float3 _LightDir;
    float _ToGamma;

    float4 frag_main(v2f_img i) : SV_Target
    {
        float4 c = _HasMain > 0.5 ? tex2Dlod(_MainS, float4(i.uv, 0, 0)) : float4(1, 1, 1, 1);
        c *= _MainColor;
        if (_HasNormal > 0.5)
        {
            float3 N = normalize(tex2Dlod(_GeoN, float4(i.uv, 0, 0)).xyz);
            float4 Tt = tex2Dlod(_GeoT, float4(i.uv, 0, 0));
            float3 T = Tt.xyz - N * dot(N, Tt.xyz);
            if (dot(T, T) < 1e-8) T = abs(N.y) < 0.99 ? cross(float3(0, 1, 0), N) : cross(float3(1, 0, 0), N);
            T = normalize(T);
            float3 B = cross(N, T) * (Tt.a < 0.5 ? -1.0 : 1.0);
            float3 ts = tex2Dlod(_NormS, float4(i.uv, 0, 0)).xyz * 2 - 1;
            float3 Nm = normalize(T * ts.x + B * ts.y + N * ts.z);
            // Only the bumps' own shading: the bumped light divided by the smooth surface's light, so the overall
            // form of the body isn't baked in, just the detail.
            float3 L = normalize(_LightDir);
            float flat = 0.6 + 0.4 * dot(N, L);
            float bump = 0.6 + 0.4 * dot(Nm, L);
            float k = bump / max(flat, 0.05);
            c.rgb *= max(0, lerp(1.0, k, _Shade));
        }
        if (_ToGamma > 0.5) c.rgb = LinearToGammaSpace(saturate(c.rgb));
        return saturate(c);
    }

    // ---------------- pass 4: emission texture ----------------
    sampler2D _EmS, _EmMaskS;
    float4 _EmColor;        // linear, already divided by the brightness kept in the material
    float _HasEm, _HasEmMask;

    float4 frag_emission(v2f_img i) : SV_Target
    {
        float3 e = _HasEm > 0.5 ? tex2Dlod(_EmS, float4(i.uv, 0, 0)).rgb : float3(1, 1, 1);
        e *= _EmColor.rgb;
        if (_HasEmMask > 0.5) e *= tex2Dlod(_EmMaskS, float4(i.uv, 0, 0)).r;
        if (_ToGamma > 0.5) e = LinearToGammaSpace(saturate(e));
        return float4(saturate(e), 1);
    }
    ENDCG

    SubShader
    {
        ZTest Always ZWrite Off Cull Off Blend Off

        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_geo
            #pragma fragment frag_geo
            ENDCG }
        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment frag_sample
            ENDCG }
        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment frag_combine
            ENDCG }
        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment frag_main
            ENDCG }
        Pass { CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment frag_emission
            ENDCG }
    }
}
