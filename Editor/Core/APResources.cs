using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    internal static class PaintPass
    {
        public const int Stamp3D = 0;
        public const int Stamp2D = 1;
        public const int Coverage = 2;
        public const int Decal = 3;
        public const int Depth = 4;
        public const int Wire = 5;
        public const int QuadImage = 6;
        public const int Stamp3DConservative = 7;
        public const int CoverageConservative = 8;
        public const int DecalConservative = 9;
        public const int Wrap = 10;
        public const int WrapConservative = 11;

        public static bool UseConservative => SystemInfo.supportsConservativeRaster;
        public static int Stamp3DPass => UseConservative ? Stamp3DConservative : Stamp3D;
        public static int CoveragePass => UseConservative ? CoverageConservative : Coverage;
        public static int DecalPass => UseConservative ? DecalConservative : Decal;
    }

    internal static class CompPass
    {
        public const int Copy = 0;
        public const int Layer = 1;
        public const int Apply = 2;
        public const int Accumulate = 3;
        public const int JfaInit = 4;
        public const int JfaStep = 5;
        public const int Output = 6;
        public const int View2D = 7;
        public const int Unpremultiply = 8;
        public const int Gradient = 9;
        public const int Fill = 10;
        public const int Over = 11;
        public const int Remap = 12;
        public const int Adjust = 13;
    }

    internal static class APIds
    {
        public static readonly int MainTex = Shader.PropertyToID("_MainTex");
        public static readonly int UseLut = Shader.PropertyToID("_UseLut");
        public static readonly int LutTex = Shader.PropertyToID("_LutTex");
        public static readonly int WrapSize = Shader.PropertyToID("_WrapSize");
        public static readonly int WrapSharp = Shader.PropertyToID("_WrapSharp");
        public static readonly int WrapAxis = Shader.PropertyToID("_WrapAxis");
        public static readonly int WrapOffset = Shader.PropertyToID("_WrapOffset");
        public static readonly int WrapRot = Shader.PropertyToID("_WrapRot");
        public static readonly int WrapNormal = Shader.PropertyToID("_WrapNormal");
        public static readonly int WrapStrength = Shader.PropertyToID("_WrapStrength");
        public static readonly int WrapGreenDown = Shader.PropertyToID("_WrapGreenDown");
        public static readonly int WrapImageAspect = Shader.PropertyToID("_WrapImageAspect");
        public static readonly int LayerTex = Shader.PropertyToID("_LayerTex");
        public static readonly int MaskTex = Shader.PropertyToID("_MaskTex");
        public static readonly int StrokeTex = Shader.PropertyToID("_StrokeTex");
        public static readonly int ValidTex = Shader.PropertyToID("_ValidTex");
        public static readonly int TipTex = Shader.PropertyToID("_TipTex");
        public static readonly int DepthTex = Shader.PropertyToID("_DepthTex");
        public static readonly int IslandTex = Shader.PropertyToID("_IslandTex");
        public static readonly int DecalTex = Shader.PropertyToID("_DecalTex");
        public static readonly int SeedTex = Shader.PropertyToID("_SeedTex");
        public static readonly int SelTex = Shader.PropertyToID("_SelTex");
        public static readonly int AdjMode = Shader.PropertyToID("_AdjMode");
        public static readonly int AdjColor = Shader.PropertyToID("_AdjColor");
        public static readonly int AdjHSL = Shader.PropertyToID("_AdjHSL");
        public static readonly int SrcMean = Shader.PropertyToID("_SrcMean");
        public static readonly int SrcStd = Shader.PropertyToID("_SrcStd");
        public static readonly int RefMean = Shader.PropertyToID("_RefMean");
        public static readonly int RefStd = Shader.PropertyToID("_RefStd");
        public static readonly int RefValid = Shader.PropertyToID("_RefValid");
        public static readonly int UseSel = Shader.PropertyToID("_UseSel");
        public static readonly int SelOverlay = Shader.PropertyToID("_SelOverlay");
        public static readonly int JumpStep = Shader.PropertyToID("_JumpStep");
        public static readonly int MaxPad = Shader.PropertyToID("_MaxPad");

        public static readonly int CopyMode = Shader.PropertyToID("_CopyMode");
        public static readonly int SrcToGamma = Shader.PropertyToID("_SrcToGamma");
        public static readonly int IsFill = Shader.PropertyToID("_IsFill");
        public static readonly int FillColor = Shader.PropertyToID("_FillColor");
        public static readonly int HasMask = Shader.PropertyToID("_HasMask");
        public static readonly int Opacity = Shader.PropertyToID("_Opacity");
        public static readonly int BlendMode = Shader.PropertyToID("_BlendMode");
        public static readonly int BrushColor = Shader.PropertyToID("_BrushColor");
        public static readonly int ApplyMode = Shader.PropertyToID("_ApplyMode");
        public static readonly int LockAlpha = Shader.PropertyToID("_LockAlpha");
        public static readonly int MaskValue = Shader.PropertyToID("_MaskValue");
        public static readonly int ViewMode = Shader.PropertyToID("_ViewMode");
        public static readonly int ToLinear = Shader.PropertyToID("_ToLinear");

        public static readonly int UVMap = Shader.PropertyToID("_UVMap");
        public static readonly int ViewSize = Shader.PropertyToID("_ViewSize");
        public static readonly int TexSize = Shader.PropertyToID("_TexSize");
        public static readonly int ShowAlpha = Shader.PropertyToID("_ShowAlpha");
        public static readonly int PointSample = Shader.PropertyToID("_PointSample");
        public static readonly int Tile = Shader.PropertyToID("_Tile");
        public static readonly int CheckerSize = Shader.PropertyToID("_CheckerSize");
        public static readonly int CheckerA = Shader.PropertyToID("_CheckerA");
        public static readonly int CheckerB = Shader.PropertyToID("_CheckerB");
        public static readonly int OutsideColor = Shader.PropertyToID("_OutsideColor");
        public static readonly int OverlayMaskView = Shader.PropertyToID("_OverlayMaskView");
        public static readonly int OverlayColor = Shader.PropertyToID("_OverlayColor");
        public static readonly int TopColor = Shader.PropertyToID("_TopColor");
        public static readonly int BottomColor = Shader.PropertyToID("_BottomColor");

        public static readonly int UVChannel = Shader.PropertyToID("_UVChannel");
        public static readonly int UVTransform = Shader.PropertyToID("_UVTransform");
        public static readonly int BrushPos = Shader.PropertyToID("_BrushPos");
        public static readonly int BrushRadius = Shader.PropertyToID("_BrushRadius");
        public static readonly int BrushRight = Shader.PropertyToID("_BrushRight");
        public static readonly int BrushUp = Shader.PropertyToID("_BrushUp");
        public static readonly int BrushFwd = Shader.PropertyToID("_BrushFwd");
        public static readonly int Hardness = Shader.PropertyToID("_Hardness");
        public static readonly int Strength = Shader.PropertyToID("_Strength");
        public static readonly int ProjMode = Shader.PropertyToID("_ProjMode");
        public static readonly int DepthRange = Shader.PropertyToID("_DepthRange");
        public static readonly int UseTip = Shader.PropertyToID("_UseTip");
        public static readonly int ViewVP = Shader.PropertyToID("_ViewVP");
        public static readonly int CamPos = Shader.PropertyToID("_CamPos");
        public static readonly int UseOcclusion = Shader.PropertyToID("_UseOcclusion");
        public static readonly int OcclusionBias = Shader.PropertyToID("_OcclusionBias");
        public static readonly int BackfaceCos = Shader.PropertyToID("_BackfaceCos");
        public static readonly int UseBackface = Shader.PropertyToID("_UseBackface");
        public static readonly int ClipIslands = Shader.PropertyToID("_ClipIslands");
        public static readonly int Value = Shader.PropertyToID("_Value");
        public static readonly int DecalPos = Shader.PropertyToID("_DecalPos");
        public static readonly int DecalRight = Shader.PropertyToID("_DecalRight");
        public static readonly int DecalUp = Shader.PropertyToID("_DecalUp");
        public static readonly int DecalNormal = Shader.PropertyToID("_DecalNormal");
        public static readonly int DecalHalf = Shader.PropertyToID("_DecalHalf");
        public static readonly int DecalCos = Shader.PropertyToID("_DecalCos");
        public static readonly int FlipX = Shader.PropertyToID("_FlipX");
        public static readonly int DepthVP = Shader.PropertyToID("_DepthVP");
        public static readonly int DepthV = Shader.PropertyToID("_DepthV");
        public static readonly int WireColor = Shader.PropertyToID("_WireColor");

        public static readonly int EmissionMap = Shader.PropertyToID("_EmissionMap");
        public static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        public static readonly int EmissionStrength = Shader.PropertyToID("_EmissionStrength");
        public static readonly int BaseDim = Shader.PropertyToID("_BaseDim");
    }

    /// <summary>Lazily created materials and utility textures.</summary>
    internal static class APResources
    {
        public const string PaintShaderName = "Hidden/AviCreatorEssentials/Paint";
        public const string CompositeShaderName = "Hidden/AviCreatorEssentials/Composite";
        public const string EmissionShaderName = "Hidden/AviCreatorEssentials/EmissionPreview";

        static Material s_Paint;
        static Material s_Composite;
        static Texture2D s_White;
        static Texture2D s_Black;
        static Texture2D s_Checker;
        static Mesh s_Quad;

        public static Material Paint => GetMaterial(ref s_Paint, PaintShaderName);
        public static Material Composite => GetMaterial(ref s_Composite, CompositeShaderName);

        public static bool ShadersAvailable =>
            Shader.Find(PaintShaderName) != null && Shader.Find(CompositeShaderName) != null &&
            Shader.Find(EmissionShaderName) != null;

        public static Shader EmissionShader => Shader.Find(EmissionShaderName);

        static Material GetMaterial(ref Material m, string shaderName)
        {
            if (m == null)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null)
                    throw new InvalidOperationException("Avi Creator Essentials: shader '" + shaderName + "' was not found. Reimport the Avi Creator Essentials folder.");
                m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = shaderName };
            }
            return m;
        }

        public static Texture2D White => GetSolid(ref s_White, Color.white, "white");
        public static Texture2D Black => GetSolid(ref s_Black, new Color(0, 0, 0, 0), "black");

        static Texture2D GetSolid(ref Texture2D t, Color c, string name)
        {
            if (t == null)
            {
                t = new Texture2D(4, 4, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, name = name };
                var px = new Color[16];
                for (int i = 0; i < px.Length; i++) px[i] = c;
                t.SetPixels(px);
                t.Apply(false, false);
            }
            return t;
        }

        /// <summary>Checkerboard used behind transparent thumbnails.</summary>
        public static Texture2D Checker
        {
            get
            {
                if (s_Checker == null)
                {
                    const int size = 16;
                    s_Checker = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Repeat,
                        name = "checker",
                    };
                    var a = new Color32(70, 70, 74, 255);
                    var b = new Color32(98, 98, 104, 255);
                    var px = new Color32[size * size];
                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        px[y * size + x] = ((x / 8) + (y / 8)) % 2 == 0 ? a : b;
                    s_Checker.SetPixels32(px);
                    s_Checker.Apply(false, false);
                }
                return s_Checker;
            }
        }

        /// <summary>A unit quad with positions in [0,1] and local coordinates in [-1,1].</summary>
        public static Mesh Quad
        {
            get
            {
                if (s_Quad == null)
                {
                    s_Quad = new Mesh { name = "AP Quad", hideFlags = HideFlags.HideAndDontSave };
                    s_Quad.MarkDynamic();
                }
                return s_Quad;
            }
        }

        public static GraphicsFormat SeedFormat
        {
            get
            {
                if (SystemInfo.IsFormatSupported(GraphicsFormat.R16G16_UNorm, FormatUsage.Render))
                    return GraphicsFormat.R16G16_UNorm;
                return GraphicsFormat.R32G32_SFloat;
            }
        }

        public static GraphicsFormat MaskStrokeFormat
        {
            get
            {
                if (SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, FormatUsage.Blend))
                    return GraphicsFormat.R16_SFloat;
                return GraphicsFormat.R8G8B8A8_UNorm;
            }
        }

        public static void Dispose()
        {
            APUtil.Destroy(ref s_Paint);
            APUtil.Destroy(ref s_Composite);
            APUtil.Destroy(ref s_White);
            APUtil.Destroy(ref s_Black);
            APUtil.Destroy(ref s_Checker);
            APUtil.Destroy(ref s_Quad);
        }
    }
}
