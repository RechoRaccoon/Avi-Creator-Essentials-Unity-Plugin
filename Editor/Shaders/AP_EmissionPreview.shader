// Avi Creator Essentials - shows only what a material emits. Used by the "Emission only" view.
Shader "Hidden/AviCreatorEssentials/EmissionPreview"
{
    Properties
    {
        _EmissionMap ("Emission", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (0, 0, 0, 1)
        _EmissionStrength ("Strength", Float) = 1
        _BaseDim ("Base Dim", Float) = 0
        _MainTex ("Base", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "APCommon.cginc"

            AP_TEX(_EmissionMap);
            float4 _EmissionMap_ST;
            float4 _EmissionColor;
            float _EmissionStrength;
            AP_TEX(_MainTex);
            float4 _MainTex_ST;
            float _BaseDim;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 uvBase : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord.xy, _EmissionMap);
                o.uvBase = TRANSFORM_TEX(v.texcoord.xy, _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 e = AP_SAMPLE(_EmissionMap, i.uv).rgb * _EmissionColor.rgb * _EmissionStrength;
                float3 b = AP_SAMPLE(_MainTex, i.uvBase).rgb * _BaseDim;
                return float4(e + b, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
