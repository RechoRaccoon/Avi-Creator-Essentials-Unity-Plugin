using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>Small helpers shared by every part of the painter.</summary>
    internal static class APUtil
    {
        public const string DataRoot = "Assets/Avi Creator Essentials Data";
        public const string PosesFolder = DataRoot + "/Poses";
        public const string BrushesFolder = DataRoot + "/Brushes";
        public const string OutputFolder = DataRoot + "/Baked";

        public static bool LinearProject => QualitySettings.activeColorSpace == ColorSpace.Linear;

        /// <summary>Folder outside Assets where layered projects are kept (never imported by Unity).</summary>
        public static string ProjectsRoot
        {
            get
            {
                string root = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(root, "AviCreatorEssentialsProjects");
            }
        }

        // ------------------------------------------------------------------ render textures

        public static RenderTexture CreateRT(int width, int height, GraphicsFormat format, string name,
            bool mips = false, FilterMode filter = FilterMode.Bilinear, TextureWrapMode wrap = TextureWrapMode.Clamp,
            int depthBits = 0, int msaa = 1)
        {
            var desc = new RenderTextureDescriptor(Mathf.Max(1, width), Mathf.Max(1, height), format, depthBits)
            {
                useMipMap = mips,
                autoGenerateMips = mips,
                msaaSamples = Mathf.Max(1, msaa),
                sRGB = GraphicsFormatUtility.IsSRGBFormat(format),
            };
            var rt = new RenderTexture(desc)
            {
                name = "ACE " + name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = filter,
                wrapMode = wrap,
                anisoLevel = mips ? 4 : 0,
            };
            rt.Create();
            return rt;
        }

        public static void Release(ref RenderTexture rt)
        {
            if (rt != null)
            {
                if (RenderTexture.active == rt) RenderTexture.active = null;
                rt.Release();
                Object.DestroyImmediate(rt);
            }
            rt = null;
        }

        public static void Destroy<T>(ref T obj) where T : Object
        {
            if (obj != null) Object.DestroyImmediate(obj);
            obj = null;
        }

        public static void Clear(RenderTexture rt, Color color)
        {
            if (rt == null) return;
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(rt);
            GL.Clear(rt.depth > 0, true, color);
            RenderTexture.active = prev;
        }

        public static bool SameLayout(Texture a, Texture b)
        {
            return a != null && b != null && a.width == b.width && a.height == b.height &&
                   a.graphicsFormat == b.graphicsFormat && a.mipmapCount == b.mipmapCount;
        }

        /// <summary>Exact copy between two textures of identical layout, or a shader copy otherwise.</summary>
        public static void Copy(Texture src, RenderTexture dst)
        {
            if (src == null || dst == null) return;
            if (SameLayout(src, dst) && (SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.RTToTexture) != 0
                && !(dst.antiAliasing > 1))
            {
                Graphics.CopyTexture(src, dst);
                return;
            }
            var m = APResources.Composite;
            m.SetFloat(APIds.CopyMode, 0);
            m.SetFloat(APIds.SrcToGamma, 0);
            Graphics.Blit(src, dst, m, CompPass.Copy);
        }

        /// <summary>Copies a texture into a gamma-data render texture, converting sRGB-decoded sources back to gamma.</summary>
        public static void CopyToGammaData(Texture src, RenderTexture dst, bool srcIsColorData)
        {
            var m = APResources.Composite;
            m.SetFloat(APIds.CopyMode, 0);
            m.SetFloat(APIds.SrcToGamma, (srcIsColorData && IsSRGB(src) && LinearProject) ? 1f : 0f);
            Graphics.Blit(src, dst, m, CompPass.Copy);
        }

        public static bool IsSRGB(Texture t)
        {
            if (t == null) return false;
            return GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat);
        }

        public static Texture2D ReadToTexture(RenderTexture rt, TextureFormat format = TextureFormat.RGBA32, bool linear = true)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, format, false, linear) { hideFlags = HideFlags.HideAndDontSave };
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;
            return tex;
        }

        public static Color ReadPixel(RenderTexture rt, Vector2 uv)
        {
            if (rt == null) return Color.clear;
            int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * rt.width), 0, rt.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(uv.y * rt.height), 0, rt.height - 1);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(x, y, 1, 1), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;
            Color c = tex.GetPixel(0, 0);
            Object.DestroyImmediate(tex);
            return c;
        }

        public static byte[] EncodePNG(RenderTexture rt, bool grayscale = false)
        {
            var tex = ReadToTexture(rt);
            try
            {
                if (grayscale)
                {
                    var px = tex.GetPixels32();
                    var g = new Texture2D(tex.width, tex.height, TextureFormat.R8, false, true);
                    var bytes = new byte[px.Length];
                    for (int i = 0; i < px.Length; i++) bytes[i] = px[i].r;
                    g.LoadRawTextureData(bytes);
                    g.Apply(false);
                    var png = g.EncodeToPNG();
                    Object.DestroyImmediate(g);
                    return png;
                }
                return tex.EncodeToPNG();
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        /// <summary>Loads a PNG/JPG from disk as a linear texture holding the raw (gamma) values.</summary>
        public static Texture2D LoadImageFile(string path, bool mips = true)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mips, true)
            {
                name = Path.GetFileNameWithoutExtension(path),
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!tex.LoadImage(File.ReadAllBytes(path), false))
            {
                Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }

        /// <summary>Converts any texture (asset or runtime) to a readable linear RGBA32 texture holding gamma values.</summary>
        public static Texture2D ToGammaTexture(Texture src, int maxSize = 8192)
        {
            if (src == null) return null;
            int w = src.width, h = src.height;
            float scale = Mathf.Min(1f, maxSize / (float)Mathf.Max(w, h));
            w = Mathf.Max(1, Mathf.RoundToInt(w * scale));
            h = Mathf.Max(1, Mathf.RoundToInt(h * scale));
            var rt = CreateRT(w, h, GraphicsFormat.R8G8B8A8_UNorm, "convert");
            CopyToGammaData(src, rt, true);
            var tex = ReadToTexture(rt);
            Release(ref rt);
            var result = new Texture2D(w, h, TextureFormat.RGBA32, true, true)
            {
                name = src.name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            result.SetPixels32(tex.GetPixels32());
            result.Apply(true);
            Object.DestroyImmediate(tex);
            return result;
        }

        // ------------------------------------------------------------------ assets & paths

        public static void EnsureAssetFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            string parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            string leaf = Path.GetFileName(assetPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        public static string AssetPathToFull(string assetPath)
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(root, assetPath);
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Untitled";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        public static string GetPath(Transform t, Transform root)
        {
            if (t == null) return "";
            if (t == root) return "";
            string path = t.name;
            var p = t.parent;
            while (p != null && p != root)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }

        public static Transform FindPath(Transform root, string path)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(path)) return root;
            return root.Find(path);
        }

        // ------------------------------------------------------------------ colors

        public static Vector4 V(Color c) => new Vector4(c.r, c.g, c.b, c.a);

        public static string ToHex(Color c, bool withAlpha)
        {
            Color32 c32 = c;
            return withAlpha
                ? $"{c32.r:X2}{c32.g:X2}{c32.b:X2}{c32.a:X2}"
                : $"{c32.r:X2}{c32.g:X2}{c32.b:X2}";
        }

        public static bool TryParseHex(string text, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim().TrimStart('#');
            if (text.Length == 3 || text.Length == 4)
            {
                var expanded = "";
                foreach (char ch in text) expanded += new string(ch, 2);
                text = expanded;
            }
            if (text.Length != 6 && text.Length != 8) return false;
            try
            {
                byte r = Convert.ToByte(text.Substring(0, 2), 16);
                byte g = Convert.ToByte(text.Substring(2, 2), 16);
                byte b = Convert.ToByte(text.Substring(4, 2), 16);
                byte a = text.Length == 8 ? Convert.ToByte(text.Substring(6, 2), 16) : (byte)255;
                color = new Color32(r, g, b, a);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>Colors are handled in gamma space; UI Toolkit expects colors in the active color space.</summary>
        public static Color ToUI(Color gamma) => gamma;

        public static float Luminance(Color c) => c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0") + " MB";
            if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0") + " KB";
            return bytes + " B";
        }

        public static long EstimateBytes(RenderTexture rt)
        {
            if (rt == null) return 0;
            long bpp;
            switch (rt.graphicsFormat)
            {
                case GraphicsFormat.R8_UNorm: bpp = 1; break;
                case GraphicsFormat.R16_SFloat: bpp = 2; break;
                case GraphicsFormat.R32_SFloat: bpp = 4; break;
                case GraphicsFormat.R16G16B16A16_SFloat: bpp = 8; break;
                default: bpp = 4; break;
            }
            long b = rt.width * (long)rt.height * bpp;
            if (rt.useMipMap) b = b * 4 / 3;
            return b;
        }
    }
}
