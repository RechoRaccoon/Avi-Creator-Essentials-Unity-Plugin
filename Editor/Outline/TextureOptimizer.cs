using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Lists every texture an avatar's materials use and changes their import settings (max size, compression,
    /// Crunch, Quest/Android override). Only import settings change; the source image files are never touched,
    /// so every change can be reverted by setting the size back.
    /// </summary>
    public static class TextureOptimizer
    {
        public class Entry
        {
            public Texture2D texture;
            public string path;
            public TextureImporter importer;
            public bool include = true;
            public bool isNormal;
            public bool hasAlpha;
            public int width, height;
            public int currentMax;
            public int newMax;
            public long memoryBytes;
            public long fileBytes;
            public string compression;
            public readonly List<string> usedBy = new List<string>();
        }

        public class Options
        {
            public bool crunch = true;
            public int quality = 50;
            public bool questOverride;
            public int questMax = 1024;
            public bool streamingMipmaps;
            public bool disableReadWrite;
        }

        public static readonly int[] Sizes = { 128, 256, 512, 1024, 2048, 4096, 8192 };
        public static readonly string[] SizeNames = { "128", "256", "512", "1024", "2048", "4096", "8192" };

        public static List<Entry> Scan(GameObject root)
        {
            var map = new Dictionary<string, Entry>();
            if (root == null) return new List<Entry>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    var shader = mat.shader;
                    int count = shader.GetPropertyCount();
                    for (int i = 0; i < count; i++)
                    {
                        if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                        var tex = mat.GetTexture(shader.GetPropertyName(i)) as Texture2D;
                        if (tex == null) continue;
                        string path = AssetDatabase.GetAssetPath(tex);
                        if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) continue; // packages are read-only
                        Entry e;
                        if (!map.TryGetValue(path, out e))
                        {
                            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                            if (imp == null) continue;
                            e = new Entry
                            {
                                texture = tex,
                                path = path,
                                importer = imp,
                                isNormal = imp.textureType == TextureImporterType.NormalMap,
                                hasAlpha = imp.DoesSourceTextureHaveAlpha(),
                                width = tex.width,
                                height = tex.height,
                                currentMax = imp.maxTextureSize,
                                memoryBytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex),
                                fileBytes = FileSize(path),
                                compression = imp.textureCompression.ToString() + (imp.crunchedCompression ? " + Crunch" : ""),
                            };
                            e.newMax = e.currentMax;
                            map[path] = e;
                        }
                        if (!e.usedBy.Contains(mat.name)) e.usedBy.Add(mat.name);
                    }
                }
            }
            var list = new List<Entry>(map.Values);
            list.Sort((a, b) => b.memoryBytes.CompareTo(a.memoryBytes));
            return list;
        }

        /// <summary>Largest dimension of the imported texture, rounded to the nearest size in the list.</summary>
        public static int SizeIndex(int size)
        {
            int best = 0;
            for (int i = 0; i < Sizes.Length; i++) if (Sizes[i] <= size) best = i;
            return best;
        }

        /// <summary>Estimated VRAM in bytes for a texture at a given size, including mipmaps.</summary>
        public static long EstimateVram(Entry e, int maxSize, bool android)
        {
            int w = Mathf.Max(1, e.width), h = Mathf.Max(1, e.height);
            int big = Mathf.Max(w, h);
            if (big > maxSize)
            {
                float k = maxSize / (float)big;
                w = Mathf.Max(4, Mathf.RoundToInt(w * k));
                h = Mathf.Max(4, Mathf.RoundToInt(h * k));
            }
            float bytesPerPixel = android ? 0.45f : (e.hasAlpha || e.isNormal ? 1f : 0.5f);
            return (long)(w * (double)h * bytesPerPixel * 1.33);
        }

        public static void Apply(List<Entry> entries, Options o)
        {
            int n = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var e in entries)
                {
                    if (!e.include || e.importer == null) continue;
                    n++;
                    EditorUtility.DisplayProgressBar("Optimizing textures", e.path, n / (float)entries.Count);
                    var imp = e.importer;
                    imp.maxTextureSize = e.newMax;
                    imp.textureCompression = TextureImporterCompression.Compressed;
                    imp.crunchedCompression = o.crunch;
                    if (o.crunch) imp.compressionQuality = o.quality;
                    imp.streamingMipmaps = o.streamingMipmaps;
                    if (o.disableReadWrite) imp.isReadable = false;

                    if (o.questOverride)
                    {
                        var ps = imp.GetPlatformTextureSettings("Android");
                        ps.overridden = true;
                        ps.maxTextureSize = Mathf.Min(e.newMax, o.questMax);
                        ps.format = TextureImporterFormat.ASTC_6x6;
                        ps.compressionQuality = 50;
                        imp.SetPlatformTextureSettings(ps);
                    }
                    EditorUtility.SetDirty(imp);
                    imp.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }

        static long FileSize(string assetPath)
        {
            try
            {
                string full = Path.GetFullPath(assetPath);
                return File.Exists(full) ? new FileInfo(full).Length : 0;
            }
            catch { return 0; }
        }

        public static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0") + " MB";
        }
    }
}
