using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>One major color of a material: the average color of a cluster of similar shades, with its coverage and brightness spread.</summary>
    public class MajorColor
    {
        public string name = "Color";
        public Color color;              // mean color of the cluster (gamma)
        public float coverage;           // share of the material's opaque pixels
        public float valMean, valMin, valMax;  // brightness spread (value = brightest channel)
        public Vector2[] seedUVs = new Vector2[0]; // up to 3 FloodSelect seeds: darkest, middle, lightest
        public Color target;             // color the user picked for this cluster (starts as color)
        public string layerId;           // Recolor layer for this cluster, if one was made
        public bool HasLayer => !string.IsNullOrEmpty(layerId);
    }

    public partial class APSession
    {
        struct MajorCell
        {
            public int count;
            public float sumR, sumG, sumB, sumV, minV, maxV;
            public int firstX, firstY;   // a representative pixel position
        }

        /// <summary>
        /// Detects the major colors of a material: clusters the base texture (everything below the
        /// first adjustment layer, so already-recolored layers don't change the answer) by hue, with
        /// near-gray colors grouped by brightness instead. All the shades of one hue — the dark folds
        /// and the lit parts of the green — count as one color. Specks under 1% are ignored.
        /// </summary>
        public List<MajorColor> DetectMajorColors(TextureSet set, int maxColors = 8)
        {
            var found = new List<MajorColor>();
            if (set == null || set.IsMirrored) return found;
            if (!set.initialized) InitializeSet(set);
            var below = new List<PaintLayer>();
            foreach (var l in set.layers)
            {
                if (l.kind == LayerKind.Adjustment) break;
                below.Add(l);
            }
            if (below.Count == 0) return found;
            var rt = engine.NewContentRT(set);
            engine.CompositeSubset(set, below, rt);
            var tex = APUtil.ReadToTexture(rt);
            APUtil.Release(ref rt);
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            int w = set.width, h = set.height;
            int step = Mathf.Max(1, (int)Mathf.Sqrt(px.Length / 200000f));
            var cells = new Dictionary<int, MajorCell>();
            int opaque = 0;
            for (int y = 0; y < h; y += step)
            {
                for (int x = 0; x < w; x += step)
                {
                    var c = px[y * w + x];
                    if (c.a < 128) continue;
                    opaque++;
                    Color.RGBToHSV((Color)c, out float hue, out float sat, out float val);
                    int band = val < 0.35f ? 0 : val < 0.7f ? 1 : 2;
                    int key;
                    if (sat < 0.03f) key = 1000 + band;
                    else if (sat < 0.15f) key = 2000 + (int)(hue * 6f) * 3 + band;
                    else key = (int)(hue * 36f) * 3 + band;
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    if (!cells.TryGetValue(key, out var cell))
                        cell = new MajorCell { minV = val, maxV = val, firstX = x, firstY = y };
                    cell.count++;
                    cell.sumR += r; cell.sumG += g; cell.sumB += b; cell.sumV += val;
                    if (val < cell.minV) cell.minV = val;
                    if (val > cell.maxV) cell.maxV = val;
                    cells[key] = cell;
                }
            }
            if (opaque == 0) return found;

            // Merge cells into clusters: one hue family across its brightness bands, with a
            // one-bin gap bridged so a thin hole doesn't split a color in two. Muted colors
            // only merge within their hue sector, so the dark paw prints stay separate from
            // the green; neutral bands never merge with each other.
            var claimed = new HashSet<int>();
            var clusters = new List<List<int>>();
            var keys = new List<int>(cells.Keys);
            keys.Sort((a, b) => cells[b].count.CompareTo(cells[a].count));
            foreach (var key in keys)
            {
                if (claimed.Contains(key)) continue;
                var cluster = new List<int>();
                var stack = new Stack<int>();
                stack.Push(key);
                claimed.Add(key);
                while (stack.Count > 0)
                {
                    int k = stack.Pop();
                    cluster.Add(k);
                    foreach (var n in MajorNeighbors(k))
                    {
                        if (cells.ContainsKey(n) && !claimed.Contains(n))
                        {
                            claimed.Add(n);
                            stack.Push(n);
                        }
                    }
                }
                clusters.Add(cluster);
            }

            foreach (var cluster in clusters)
            {
                int count = 0;
                double sr = 0, sg = 0, sb = 0, sv = 0;
                float minV = 1f, maxV = 0f;
                int darkCell = -1, lightCell = -1;
                foreach (var k in cluster)
                {
                    var cell = cells[k];
                    count += cell.count;
                    sr += cell.sumR; sg += cell.sumG; sb += cell.sumB; sv += cell.sumV;
                    if (cell.minV < minV) { minV = cell.minV; darkCell = k; }
                    if (cell.maxV > maxV) { maxV = cell.maxV; lightCell = k; }
                }
                float coverage = (float)count / opaque;
                if (coverage < 0.01f) continue;
                float meanV = (float)(sv / count);
                int midCell = -1;
                float midDiff = float.MaxValue;
                foreach (var k in cluster)
                {
                    var cell = cells[k];
                    float d = Mathf.Abs(cell.sumV / cell.count - meanV);
                    if (d < midDiff) { midDiff = d; midCell = k; }
                }
                var mean = new Color((float)(sr / count), (float)(sg / count), (float)(sb / count));
                Color.RGBToHSV(mean, out float mh, out float ms, out float mv);
                var mc = new MajorColor
                {
                    name = MajorColorName(mh, ms, meanV),
                    color = mean,
                    coverage = coverage,
                    valMean = meanV,
                    valMin = minV,
                    valMax = maxV,
                    target = mean,
                    seedUVs = MajorSeedUVs(cells, darkCell, midCell, lightCell, w, h),
                };
                found.Add(mc);
            }
            found.Sort((a, b) => b.coverage.CompareTo(a.coverage));
            var used = new HashSet<string>();
            foreach (var mc in found)
            {
                string n = mc.name;
                if (used.Contains(n)) n = "Dark " + n;
                if (used.Contains(n)) n += " 2";
                mc.name = n;
                used.Add(n);
            }
            if (found.Count > maxColors) found.RemoveRange(maxColors, found.Count - maxColors);
            return found;
        }

        static IEnumerable<int> MajorNeighbors(int key)
        {
            if (key < 1000)
            {
                int hueBin = key / 3, band = key % 3;
                for (int d = -2; d <= 2; d++)
                {
                    if (d == 0) continue;
                    yield return ((hueBin + d + 36) % 36) * 3 + band;
                }
                if (band > 0) yield return hueBin * 3 + band - 1;
                if (band < 2) yield return hueBin * 3 + band + 1;
            }
            else if (key >= 2000)
            {
                int sector = (key - 2000) / 3, band = (key - 2000) % 3;
                if (band > 0) yield return 2000 + sector * 3 + band - 1;
                if (band < 2) yield return 2000 + sector * 3 + band + 1;
            }
            // Neutral bands (1000-1002) never merge: black, grey and white are different colors.
        }

        static Vector2[] MajorSeedUVs(Dictionary<int, MajorCell> cells, int darkCell, int midCell, int lightCell, int w, int h)
        {
            var uvs = new List<Vector2>();
            foreach (var k in new[] { darkCell, midCell, lightCell })
            {
                if (k < 0 || !cells.TryGetValue(k, out var cell)) continue;
                var uv = new Vector2((cell.firstX + 0.5f) / w, (cell.firstY + 0.5f) / h);
                bool dupe = false;
                foreach (var u in uvs) if ((u - uv).sqrMagnitude < 0.0001f) dupe = true;
                if (!dupe) uvs.Add(uv);
            }
            return uvs.ToArray();
        }

        static string MajorColorName(float h, float s, float v)
        {
            if (s < 0.03f) return v > 0.85f ? "White" : v < 0.15f ? "Black" : "Grey";
            float deg = h * 360f;
            string hueName = deg < 15f || deg >= 345f ? "Red" : deg < 45f ? "Orange" : deg < 70f ? "Yellow" :
                deg < 160f ? "Green" : deg < 200f ? "Teal" : deg < 260f ? "Blue" : deg < 300f ? "Purple" : "Pink";
            if (s < 0.15f)
            {
                // Muted warm colors read as brown/beige rather than orange/yellow.
                if (deg >= 15f && deg < 70f) return v > 0.6f ? "Beige" : v > 0.3f ? "Brown" : "Dark brown";
                return (v < 0.35f ? "Dark " : "") + hueName;
            }
            return (v < 0.35f ? "Dark " : v > 0.8f ? "Light " : "") + hueName;
        }

        /// <summary>
        /// Recolors one major color cluster: the first change makes a Recolor layer for the
        /// cluster (masked by FloodSelect from seed pixels spread across its brightness range),
        /// later changes just move that layer's target color. The layer sits on top of the stack
        /// and is not clipped to one layer below, so it covers the whole material.
        /// </summary>
        public string ApplyMajorColor(TextureSet set, MajorColor mc, Color target, float tolerance)
        {
            if (set == null) return "Pick a material first.";
            if (set.IsMirrored) return "This material live-mirrors another — recolor on that material instead.";
            if (!set.initialized) InitializeSet(set);
            mc.target = target;
            PaintLayer layer = null;
            if (mc.HasLayer)
                foreach (var l in set.layers)
                    if (l.id == mc.layerId) { layer = l; break; }
            if (layer == null)
            {
                // One seed can't cover a wide shade spread, so sample up to three seeds —
                // darkest, middle, lightest — and union the matches.
                var maskRT = engine.NewMaskRT(set, 0f);
                bool any = false;
                foreach (var uv in mc.seedUVs)
                {
                    var sel = engine.FloodSelect(set, uv, tolerance, false, false);
                    if (sel == null) continue;
                    MergeSelectionIntoMask(sel, maskRT, true);
                    Object.DestroyImmediate(sel);
                    any = true;
                }
                if (!any) return "Couldn't sample that color.";
                layer = AddAdjustmentLayer(set, AdjustMode.Recolor, target, maskRT, false, true);
                layer.name = UniqueMajorName(set, mc.name);
                mc.layerId = layer.id;
                set.editingMask = true;
                return "Recolor layer added for " + mc.name + ".";
            }
            EditLayer(set, layer, "Major color " + mc.name, l =>
            {
                l.adjMode = AdjustMode.Recolor;
                l.adjColor = target;
            });
            return "Updated " + mc.name + ".";
        }

        static string UniqueMajorName(TextureSet set, string name)
        {
            string baseName = "Major: " + name;
            string n = baseName;
            int i = 2;
            while (true)
            {
                bool taken = false;
                foreach (var l in set.layers)
                    if (l.name == n) { taken = true; break; }
                if (!taken) return n;
                n = baseName + " " + i++;
            }
        }

        /// <summary>Removes the cluster's Recolor layer, if it has one, and puts its target back to the detected color.</summary>
        public void ResetMajorColor(TextureSet set, MajorColor mc)
        {
            mc.target = mc.color;
            if (set == null || !mc.HasLayer) return;
            PaintLayer layer = null;
            foreach (var l in set.layers)
                if (l.id == mc.layerId) { layer = l; break; }
            mc.layerId = null;
            if (layer != null) DeleteLayer(set, layer);
        }
    }
}
