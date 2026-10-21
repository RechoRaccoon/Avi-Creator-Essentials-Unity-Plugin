using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    public enum BrushProjection
    {
        Sphere,     // everything within the radius in 3D
        Projected,  // camera-aligned footprint, like painting through the screen (Substance style)
    }

    public enum StabilizerMode
    {
        Rope,       // lazy mouse: the brush trails the cursor on a string and stays put inside it
        Smooth,     // the brush follows an average of the recent path, ironing out wobble
    }

    /// <summary>Brush behaviour, saved in EditorPrefs between sessions.</summary>
    [Serializable]
    public class BrushSettings
    {
        const string PrefKey = "AviCreatorEssentials.Brush.v1";

        public string tip = "Round";
        public float radius = 0.015f;          // world units (meters) in the 3D view
        public float radius2D = 0.01f;         // fraction of the texture width in the 2D view
        public float hardness = 0.65f;
        public float opacity = 1f;
        public float flow = 1f;
        public float spacing = 0.1f;           // fraction of the diameter
        public float angle;                    // degrees
        public float angleJitter;              // degrees
        public float sizeJitter;               // 0..1
        public float opacityJitter;            // 0..1
        public float scatter;                  // 0..1 of the diameter
        public bool followStroke;
        public bool pressureSize = true;
        public bool pressureOpacity;
        public float stabilizer;               // 0..1 intensity, 0 = off
        public StabilizerMode stabilizerMode = StabilizerMode.Smooth;
        public bool stabilizerCatchUp = true;  // finish the line to the cursor on release
        public BrushProjection projection = BrushProjection.Projected;
        public float depthRange = 1f;          // projected mode thickness in radii
        public bool occlusion = true;          // only paint surfaces visible from the camera
        public bool backfaceCulling = true;
        public float backfaceAngle = 88f;      // faces turned further than this from the camera are skipped
        public bool alignToSurface;            // tip orientation follows the surface instead of the screen
        public bool symmetry;
        public int symmetryAxis;               // 0 X, 1 Y, 2 Z (avatar space)
        public bool mirrorU2D;
        public bool clipToIslands2D;
        public bool wholeModel = true;         // strokes, gradients and stars carry on into the same kind of texture on every material
        public bool autoSelectMaterial = true; // a stroke starting on another material switches to it

        // Paint mode and brush texture
        public bool paintMode;                 // wet paint: dabs build up and carry color instead of stamping one flat color
        public float bristles;                 // 0..1 how much bristle streaks show along the stroke
        public float bristleDensity = 0.5f;    // 0..1 few thick to many fine bristles
        public float colorMixing;              // 0..1 paint mode: the brush picks up colors it passes over
        public float colorVariation;           // 0..1 paint mode: each bristle carries a slightly different shade
        public float grain;                    // 0..1 canvas grain
        public float taperIn;                  // stroke start grows over this many brush diameters
        public float taperOut;                 // stroke end thins out over this many brush diameters

        public static BrushSettings Load()
        {
            var s = new BrushSettings();
            string json = EditorPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, s); }
                catch (Exception) { /* keep defaults */ }
            }
            return s;
        }

        public void Save() => EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(this));
    }

    public class BrushTip
    {
        public string name;
        public bool procedural;     // round tip computed in the shader (uses hardness)
        public Texture2D texture;   // R8, white = paint
        public Texture2D preview;   // RGBA, white with alpha, for the UI
        public string filePath;
        public bool builtIn;
        public bool followsStroke;  // bristle-style tips always turn with the stroke
    }

    /// <summary>Built-in and user brush tips. User tips are PNG/JPG files in Assets/Avi Creator Essentials Data/Brushes.</summary>
    public static class BrushLibrary
    {
        static List<BrushTip> s_Tips;
        public static event Action Changed;

        public static IReadOnlyList<BrushTip> Tips
        {
            get
            {
                if (s_Tips == null) Reload();
                return s_Tips;
            }
        }

        public static BrushTip Find(string name)
        {
            foreach (var t in Tips)
                if (t.name == name) return t;
            return Tips[0];
        }

        public static void Reload()
        {
            DisposeTips();
            s_Tips = new List<BrushTip>();
            s_Tips.Add(MakeProcedural("Round", (x, y) =>
            {
                float d = Mathf.Sqrt(x * x + y * y);
                return Mathf.Clamp01((1f - d) * 3f);
            }, true));
            s_Tips.Add(MakeProcedural("Square", (x, y) =>
            {
                float d = Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
                return Mathf.Clamp01((0.92f - d) * 40f);
            }));
            s_Tips.Add(MakeProcedural("Soft Square", (x, y) =>
            {
                float d = Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
                float t = Mathf.Clamp01((1f - d) / 0.6f);
                return t * t * (3f - 2f * t);
            }));
            var rng = new System.Random(1234);
            var grain = new float[128 * 128];
            for (int i = 0; i < grain.Length; i++) grain[i] = (float)rng.NextDouble();
            s_Tips.Add(MakeProcedural("Grain", (x, y) =>
            {
                float d = Mathf.Sqrt(x * x + y * y);
                float r = Mathf.Clamp01((1f - d) * 2.5f);
                int gx = Mathf.Clamp((int)((x * 0.5f + 0.5f) * 127), 0, 127);
                int gy = Mathf.Clamp((int)((y * 0.5f + 0.5f) * 127), 0, 127);
                return r * Mathf.Clamp01(grain[gy * 128 + gx] * 1.6f - 0.3f);
            }));
            var dots = new List<Vector3>();
            for (int i = 0; i < 40; i++)
            {
                float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                float r = Mathf.Sqrt((float)rng.NextDouble()) * 0.85f;
                dots.Add(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0.03f + (float)rng.NextDouble() * 0.09f));
            }
            s_Tips.Add(MakeProcedural("Splatter", (x, y) =>
            {
                float v = 0f;
                foreach (var dot in dots)
                {
                    float dx = x - dot.x, dy = y - dot.y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    v = Mathf.Max(v, Mathf.Clamp01((dot.z - d) / 0.015f));
                }
                return v;
            }));
            s_Tips.Add(MakeProcedural("Star", (x, y) =>
            {
                float a = Mathf.Atan2(y, x);
                float d = Mathf.Sqrt(x * x + y * y);
                float r = 0.55f + 0.4f * Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 2.5f)), 3f);
                return Mathf.Clamp01((r - d) * 30f);
            }));
            s_Tips.Add(MakeProcedural("Heart", (x, y) =>
            {
                // Signed distance to a heart (two round lobes meeting in a point), so the tip and the dip between
                // the lobes stay solid. The heart spans y 0..1.1 in its own units; scale it to fill the tip.
                const float s = 0.68f;
                float d = HeartDistance(new Vector2(x * s, y * s + 0.55f));
                return Mathf.Clamp01(-d / s * 40f + 0.5f);
            }));

            AddPaintTips(rng);

            LoadUserTips();
            Changed?.Invoke();
        }

        /// <summary>
        /// Tips made of many thin lines, for painterly strokes and fur. They point along the stroke (x), so the
        /// strands across them (y) drag out into streaks as the brush moves.
        /// </summary>
        static void AddPaintTips(System.Random rng)
        {
            float R() => (float)rng.NextDouble();

            // Bristles: a row of small round bristle ends across the stroke.
            var bristles = new List<Vector4>();
            for (int i = 0; i < 18; i++)
            {
                float y = Mathf.Lerp(-0.86f, 0.86f, (i + 0.5f) / 18f) + (R() - 0.5f) * 0.05f;
                bristles.Add(new Vector4((R() - 0.5f) * 0.25f, y, 0.035f + R() * 0.045f, 0.5f + R() * 0.5f));
            }
            AddTip(MakeProcedural("Bristles", (x, y) => Dots(bristles, x, y, 1.6f)));

            // Dry brush: fewer, uneven bristles with gaps, for broken scratchy strokes.
            var dry = new List<Vector4>();
            for (int i = 0; i < 26; i++)
            {
                if (R() < 0.3f) continue;
                dry.Add(new Vector4((R() - 0.5f) * 0.5f, (R() * 2f - 1f) * 0.9f, 0.015f + R() * 0.035f, 0.35f + R() * 0.65f));
            }
            AddTip(MakeProcedural("Dry Brush", (x, y) => Dots(dry, x, y, 2.2f)));

            // Fur strands: thin hairs lying along the stroke, tapered at both ends, of different lengths.
            var strands = new List<Strand>();
            for (int i = 0; i < 11; i++)
            {
                float y = Mathf.Lerp(-0.7f, 0.7f, (i + 0.5f) / 11f) + (R() - 0.5f) * 0.08f;
                float len = 0.55f + R() * 0.4f;
                float x0 = -0.95f + R() * (1.9f - 2f * len);
                strands.Add(new Strand
                {
                    a = new Vector2(x0, y),
                    b = new Vector2(x0 + 2f * len, y + (R() - 0.5f) * 0.25f),
                    bend = (R() - 0.5f) * 0.25f,
                    width = 0.022f + R() * 0.02f,
                    strength = 0.6f + R() * 0.4f,
                });
            }
            AddTip(MakeProcedural("Fur Strands", (x, y) => Strands(strands, x, y)));

            // Fur tuft: a clump of pointed hairs fanning out from one root, like the spiky tufts on a fluffy chest.
            var tuft = new List<Strand>();
            for (int i = 0; i < 5; i++)
            {
                float spread = (i - 2f) / 2f;
                var root = new Vector2(-0.85f, spread * 0.18f);
                var tip = new Vector2(0.55f + R() * 0.38f - Mathf.Abs(spread) * 0.25f, spread * 0.62f + (R() - 0.5f) * 0.12f);
                tuft.Add(new Strand { a = root, b = tip, bend = spread * 0.18f + (R() - 0.5f) * 0.1f, width = 0.11f, strength = 1f, pointed = true });
            }
            AddTip(MakeProcedural("Fur Tuft", (x, y) => Strands(tuft, x, y)));
        }

        static void AddTip(BrushTip tip)
        {
            tip.followsStroke = true;
            s_Tips.Add(tip);
        }

        struct Strand
        {
            public Vector2 a, b;
            public float bend;      // sideways bow at the middle, in tip units
            public float width;     // half width at the thickest point
            public float strength;
            public bool pointed;    // thick at the root (a), sharp at the tip (b); otherwise tapered at both ends
        }

        static float Dots(List<Vector4> dots, float x, float y, float stretch)
        {
            float v = 0f;
            foreach (var d in dots)
            {
                // Slightly stretched along the stroke so overlapping dabs merge into smooth streaks.
                float dx = (x - d.x) / stretch, dy = y - d.y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                v = Mathf.Max(v, Mathf.Clamp01((d.z - dist) / 0.012f + 0.5f) * d.w);
            }
            return v;
        }

        static float Strands(List<Strand> strands, float x, float y)
        {
            float v = 0f;
            var p = new Vector2(x, y);
            foreach (var s in strands)
            {
                // Closest point on a gently bowed line, by sampling: cheap enough for a one-off 256² tip.
                float reach = Mathf.Abs(s.bend) + s.width + 0.02f;
                if (x < Mathf.Min(s.a.x, s.b.x) - reach || x > Mathf.Max(s.a.x, s.b.x) + reach ||
                    y < Mathf.Min(s.a.y, s.b.y) - reach || y > Mathf.Max(s.a.y, s.b.y) + reach) continue;
                const int n = 24;
                var dir = s.b - s.a;
                var side = new Vector2(-dir.y, dir.x).normalized;
                float best = float.MaxValue, bestT = 0f;
                for (int i = 0; i <= n; i++)
                {
                    float t = i / (float)n;
                    var q = s.a + dir * t + side * (s.bend * 4f * t * (1f - t));
                    float d = (p - q).sqrMagnitude;
                    if (d < best) { best = d; bestT = t; }
                }
                float taper = s.pointed ? Mathf.Pow(1f - bestT, 0.8f) : Mathf.Sin(bestT * Mathf.PI);
                float w = s.width * taper;
                if (w <= 0f) continue;
                float dist = Mathf.Sqrt(best);
                v = Mathf.Max(v, Mathf.Clamp01((w - dist) / 0.012f + 0.5f) * s.strength);
            }
            return v;
        }

        static void LoadUserTips()
        {
            string folder = APUtil.AssetPathToFull(APUtil.BrushesFolder);
            if (!Directory.Exists(folder)) return;
            var files = new List<string>();
            files.AddRange(Directory.GetFiles(folder, "*.png"));
            files.AddRange(Directory.GetFiles(folder, "*.jpg"));
            files.AddRange(Directory.GetFiles(folder, "*.jpeg"));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                try
                {
                    var tip = LoadTipFile(f);
                    if (tip != null) s_Tips.Add(tip);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("Avi Creator Essentials: could not load brush '" + f + "': " + e.Message);
                }
            }
        }

        static BrushTip LoadTipFile(string path)
        {
            var src = APUtil.LoadImageFile(path, false);
            if (src == null) return null;
            const int maxSize = 512;
            int w = src.width, h = src.height;
            float scale = Mathf.Min(1f, maxSize / (float)Mathf.Max(w, h));
            int tw = Mathf.Max(8, Mathf.RoundToInt(w * scale));
            int th = Mathf.Max(8, Mathf.RoundToInt(h * scale));
            // Fit into a square so the brush keeps its aspect ratio.
            int size = Mathf.Max(tw, th);
            var px = src.GetPixels32();

            bool hasAlpha = false;
            foreach (var p in px)
                if (p.a < 230) { hasAlpha = true; break; }

            // Decide whether dark or light means "paint" when there is no transparency: use the border brightness.
            float border = 0f;
            int borderCount = 0;
            for (int x = 0; x < w; x++)
            {
                border += Lum(px[x]) + Lum(px[(h - 1) * w + x]);
                borderCount += 2;
            }
            for (int y = 0; y < h; y++)
            {
                border += Lum(px[y * w]) + Lum(px[y * w + w - 1]);
                borderCount += 2;
            }
            bool invert = !hasAlpha && border / Mathf.Max(1, borderCount) > 0.5f;

            var values = new float[size * size];
            int ox = (size - tw) / 2, oy = (size - th) / 2;
            for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                float u = (x + 0.5f) / tw, v = (y + 0.5f) / th;
                var c = src.GetPixelBilinear(u, v);
                float val;
                if (hasAlpha) val = c.a;
                else val = invert ? 1f - (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f) : (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f);
                values[(y + oy) * size + (x + ox)] = Mathf.Clamp01(val);
            }
            Object.DestroyImmediate(src);
            var tip = MakeTip(Path.GetFileNameWithoutExtension(path), values, size, false);
            tip.filePath = path;
            return tip;
        }

        /// <summary>Signed distance to a heart with its point at the origin and lobes reaching about y = 1.1 (after Inigo Quilez).</summary>
        static float HeartDistance(Vector2 p)
        {
            p.x = Mathf.Abs(p.x);
            if (p.y + p.x > 1f) return (p - new Vector2(0.25f, 0.75f)).magnitude - Mathf.Sqrt(2f) / 4f;
            float a = (p - new Vector2(0f, 1f)).sqrMagnitude;
            float b = (p - Vector2.one * (0.5f * Mathf.Max(p.x + p.y, 0f))).sqrMagnitude;
            return Mathf.Sqrt(Mathf.Min(a, b)) * Mathf.Sign(p.x - p.y);
        }

        static float Lum(Color32 c) => (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f) / 255f;

        static BrushTip MakeProcedural(string name, Func<float, float, float> f, bool procedural = false)
        {
            const int size = 256;
            var values = new float[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float fx = (x + 0.5f) / size * 2f - 1f;
                float fy = (y + 0.5f) / size * 2f - 1f;
                values[y * size + x] = Mathf.Clamp01(f(fx, fy));
            }
            var tip = MakeTip(name, values, size, procedural);
            tip.builtIn = true;
            return tip;
        }

        static BrushTip MakeTip(string name, float[] values, int size, bool procedural)
        {
            var tex = new Texture2D(size, size, TextureFormat.R8, true, true)
            {
                name = "AP Tip " + name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            var px = new Color[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(values[i], values[i], values[i], 1f);
            // Keep the outermost texels empty so clamped sampling never smears the edge.
            for (int i = 0; i < size; i++)
            {
                px[i] = px[(size - 1) * size + i] = px[i * size] = px[i * size + size - 1] = Color.black;
            }
            tex.SetPixels(px);
            tex.Apply(true, false);

            const int ps = 64;
            var preview = new Texture2D(ps, ps, TextureFormat.RGBA32, false, false)
            {
                name = "AP Tip Preview " + name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
            };
            var pp = new Color[ps * ps];
            for (int y = 0; y < ps; y++)
            for (int x = 0; x < ps; x++)
            {
                int sx = Mathf.Clamp((int)((x + 0.5f) / ps * size), 0, size - 1);
                int sy = Mathf.Clamp((int)((y + 0.5f) / ps * size), 0, size - 1);
                pp[y * ps + x] = new Color(1f, 1f, 1f, values[sy * size + sx]);
            }
            preview.SetPixels(pp);
            preview.Apply(false, false);

            return new BrushTip { name = name, procedural = procedural, texture = tex, preview = preview };
        }

        /// <summary>Copies an image into the brushes folder and reloads. Returns the new tip name.</summary>
        public static string Import(string sourcePath)
        {
            APUtil.EnsureAssetFolder(APUtil.BrushesFolder);
            string folder = APUtil.AssetPathToFull(APUtil.BrushesFolder);
            string dest = Path.Combine(folder, Path.GetFileName(sourcePath));
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, dest, true);
            AssetDatabase.Refresh();
            Reload();
            return Path.GetFileNameWithoutExtension(dest);
        }

        public static void Delete(BrushTip tip)
        {
            if (tip == null || tip.builtIn || string.IsNullOrEmpty(tip.filePath)) return;
            string assetPath = APUtil.BrushesFolder + "/" + Path.GetFileName(tip.filePath);
            if (!AssetDatabase.DeleteAsset(assetPath) && File.Exists(tip.filePath)) File.Delete(tip.filePath);
            Reload();
        }

        static void DisposeTips()
        {
            if (s_Tips == null) return;
            foreach (var t in s_Tips)
            {
                if (t.texture != null) Object.DestroyImmediate(t.texture);
                if (t.preview != null) Object.DestroyImmediate(t.preview);
            }
            s_Tips = null;
        }

        public static void Dispose() => DisposeTips();
    }
}
