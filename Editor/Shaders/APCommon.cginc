// Avi Creator Essentials - shared shader helpers.
#ifndef AP_COMMON_INCLUDED
#define AP_COMMON_INCLUDED

#include "UnityCG.cginc"

// DX11-style texture declarations (work on D3D11, Vulkan, Metal and GLCore via HLSLcc).
#define AP_TEX(t) Texture2D t; SamplerState sampler##t
#define AP_SAMPLE(t, uv) t.Sample(sampler##t, uv)
#define AP_SAMPLE_LOD(t, uv, l) t.SampleLevel(sampler##t, uv, l)

// ---------------------------------------------------------------------------
// UV-space rasterisation: a mesh is drawn so that each triangle lands where its
// UVs are in the target texture, while world position/normal are interpolated.
// ---------------------------------------------------------------------------
int _UVChannel;
float4 _UVTransform; // xy = scale, zw = offset (identity = 1,1,0,0)

struct ap_appdata_uv
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 uv0 : TEXCOORD0;
    float4 uv1 : TEXCOORD1;
    float4 uv2 : TEXCOORD2;
    float4 uv3 : TEXCOORD3;
};

struct ap_v2f_uv
{
    float4 pos : SV_POSITION;
    float3 wpos : TEXCOORD0;
    float3 wnrm : TEXCOORD1;
    float2 uv : TEXCOORD2;
};

float2 AP_SelectUV(ap_appdata_uv v)
{
    if (_UVChannel == 1) return v.uv1.xy;
    if (_UVChannel == 2) return v.uv2.xy;
    if (_UVChannel == 3) return v.uv3.xy;
    return v.uv0.xy;
}

// Maps a texture coordinate to clip space so the rasterised texel matches what a
// later texture sample at that coordinate reads, on every graphics API.
float4 AP_UVToClip(float2 uv)
{
    float2 p = uv * _UVTransform.xy + _UVTransform.zw;
    float4 c = float4(p * 2.0 - 1.0, 0.5, 1.0);
    #if UNITY_UV_STARTS_AT_TOP
    c.y = -c.y;
    #endif
    return c;
}

ap_v2f_uv ap_vert_uv(ap_appdata_uv v)
{
    ap_v2f_uv o;
    float2 uv = AP_SelectUV(v);
    o.pos = AP_UVToClip(uv);
    o.wpos = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
    o.wnrm = UnityObjectToWorldNormal(v.normal);
    o.uv = uv;
    return o;
}

// Quads generated on the CPU directly in UV space (2D brush dabs, placed images).
struct ap_appdata_quad
{
    float4 vertex : POSITION;   // xy = position in UV space, z = per-quad strength
    float2 local : TEXCOORD0;   // -1..1 brush/image local coordinates
};

struct ap_v2f_quad
{
    float4 pos : SV_POSITION;
    float2 local : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float strength : TEXCOORD2;
};

ap_v2f_quad ap_vert_quad(ap_appdata_quad v)
{
    ap_v2f_quad o;
    o.pos = AP_UVToClip(v.vertex.xy);
    o.local = v.local;
    o.uv = v.vertex.xy;
    o.strength = v.vertex.z;
    return o;
}

// Smooth round falloff. d = normalised distance (0 centre, 1 edge).
float AP_RoundFalloff(float d, float hardness, float aa)
{
    float edge = max(1.0 - hardness, aa * 1.5);
    float t = saturate((1.0 - d) / max(edge, 1e-5));
    return t * t * (3.0 - 2.0 * t);
}

// ---------------------------------------------------------------------------
// Hashes and value noise (after Dave Hoskins' "hash without sine").
// ---------------------------------------------------------------------------
float AP_Hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return frac(p);
}

float AP_Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float AP_Hash31(float3 p3)
{
    p3 = frac(p3 * 0.1031);
    p3 += dot(p3, p3.zyx + 31.32);
    return frac((p3.x + p3.y) * p3.z);
}

float3 AP_Hash33(float3 p3)
{
    p3 = frac(p3 * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yxz + 33.33);
    return frac((p3.xxy + p3.yxx) * p3.zyx);
}

// Smooth 3D value noise in 0..1.
float AP_ValueNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = AP_Hash31(i);
    float b = AP_Hash31(i + float3(1, 0, 0));
    float c = AP_Hash31(i + float3(0, 1, 0));
    float d = AP_Hash31(i + float3(1, 1, 0));
    float e = AP_Hash31(i + float3(0, 0, 1));
    float g = AP_Hash31(i + float3(1, 0, 1));
    float h = AP_Hash31(i + float3(0, 1, 1));
    float k = AP_Hash31(i + float3(1, 1, 1));
    float x0 = lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
    float x1 = lerp(lerp(e, g, f.x), lerp(h, k, f.x), f.y);
    return lerp(x0, x1, f.z);
}

#endif
