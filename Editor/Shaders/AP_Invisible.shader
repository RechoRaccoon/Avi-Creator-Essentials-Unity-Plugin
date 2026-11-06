// Avi Creator Essentials - material for hidden material slots: it writes no
// colour and no depth, so the polygons are truly invisible in the 3D view.
// Editor-only. Hiding must not depend on alpha blending at all: an alpha-0
// Unlit/Transparent material still showed the hidden polygons white in the
// painter's 3D view, so this shader uses ColorMask 0 instead, which cannot
// show up regardless of how the view treats blending or alpha.
Shader "Hidden/AviCreatorEssentials/Invisible"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        ZWrite Off
        ColorMask 0
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return fixed4(0, 0, 0, 0); }
            ENDCG
        }
    }
}
