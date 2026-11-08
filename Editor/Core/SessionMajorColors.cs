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
        public HashSet<int> cellKeys = new HashSet<int>(); // bin keys of the cluster — the mask is rasterized from these, so it covers exactly what detection found
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
        }

        /// <summary>
        /// The bin a pixel belongs to: hue bin x brightness band, with near-gray rules. Shared by
        /// detection and mask rasterization, so the mask covers exactly what detection found.
        /// Muted colors use the same 36 hue bins as saturated ones: a color sitting on a hue-sector
        /// boundary (the beige at ~60 degrees) must not split in two. Muted means S below 0.17,
        /// not 0.15: darker beige folds reach S 0.15-0.16 and would otherwise flip into the
        /// saturated space and leave their own cluster.
        /// </summary>
        static int MajorCellKey(Color32 c, out float val)
        {
            Color.RGBToHSV((Color)c, out float hue, out float sat, out val);
            int band = val < 0.35f ? 0 : val < 0.7f ? 1 : 2;
            if (sat < 0.03f) return 1000 + band;
            if (sat < 0.17f) return 2000 + (int)(hue * 36f) * 3 + band;
            return (int)(hue * 36f) * 3 + band;
        }

        /// <summary>Mean hue of a bin's pixels, from its mean color.</summary>
        static float MajorCellHue(MajorCell cell)
        {
            var mean = new Color((float)(cell.sumR / cell.count), (float)(cell.sumG / cell.count), (float)(cell.sumB / cell.count));
            Color.RGBToHSV(mean, out float h, out _, out _);
            return h;
        }

        /// <summary>Wrapped hue difference in degrees, -180..180.</summary>
        static float HueDiff(float hue, float anchorHue)
        {
            float d = hue - anchorHue;
            if (d > 0.5f) d -= 1f;
            if (d < -0.5f) d += 1f;
            return d * 360f;
        }

        /// <summary>The base texture's pixels (everything below the first adjustment layer), or null when there is nothing to read.</summary>
        Color32[] ReadBasePixels(TextureSet set)
        {
            if (set == null || set.IsMirrored) return null;
            if (!set.initialized) InitializeSet(set);
            var below = new List<PaintLayer>();
            foreach (var l in set.layers)
            {
                if (l.kind == LayerKind.Adjustment) break;
                below.Add(l);
            }
            if (below.Count == 0) return null;
            var rt = engine.NewContentRT(set);
            engine.CompositeSubset(set, below, rt);
            var tex = APUtil.ReadToTexture(rt);
            APUtil.Release(ref rt);
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            return px;
        }

        /// <summary>
        /// Detects the different colors of a material: clusters the base texture (everything
        /// below the first adjustment layer, so already-recolored layers don't change the answer)
        /// by hue and brightness. Saturated colors merge across their full shade range — the dark
        /// folds and lit parts of the green count as one color. Muted colors merge within their
        /// brightness band and across bands when the brightness is close, within a 35-degree hue
        /// span, so the light beige and its darker shading count as one color while the pale
        /// paw-box background and the dark paw-print outlines stay their own rows. Small but
        /// distinct colors are kept: clusters down to 0.1% are listed, up to 12.
        /// </summary>
        public List<MajorColor> DetectMajorColors(TextureSet set, int maxColors = 12)
        {
            var found = new List<MajorColor>();
            if (set == null || set.IsMirrored) return found;
            var px = ReadBasePixels(set);
            if (px == null) return found;
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
                    int key = MajorCellKey(c, out float val);
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    if (!cells.TryGetValue(key, out var cell))
                        cell = new MajorCell { minV = val, maxV = val };
                    cell.count++;
                    cell.sumR += r; cell.sumG += g; cell.sumB += b; cell.sumV += val;
                    if (val < cell.minV) cell.minV = val;
                    if (val > cell.maxV) cell.maxV = val;
                    cells[key] = cell;
                }
            }
            if (opaque == 0) return found;

            // Merge cells into clusters, largest cells first so the biggest colors claim
            // their cells before the small ones. Saturated cells merge across hue bins and
            // brightness bands (one hue family = one color). Muted cells merge with the hue
            // bins next to them in the same brightness band, and with the same hue bin in the
            // next band up or down when the brightness is close (mean values within 0.15):
            // that keeps a wide color like the beige (bins 4-7, about a 30-degree span) whole
            // across its light parts and darker shading, while the pale paw-box background
            // and the dark paw-print outlines stay apart. Muted merges also stop when the
            // cluster's total hue span would pass 35 degrees. A cell that would stretch a
            // cluster past the span is left unclaimed, so it seeds its own cluster instead.
            // Neutral bands never merge.
            var claimed = new HashSet<int>();
            var clusters = new List<List<int>>();
            var keys = new List<int>(cells.Keys);
            keys.Sort((a, b) => cells[b].count.CompareTo(cells[a].count));
            foreach (var key in keys)
            {
                if (claimed.Contains(key)) continue;
                float seedHue = MajorCellHue(cells[key]);
                float minOff = 0f, maxOff = 0f;
                var cluster = new List<int>();
                var stack = new Stack<int>();
                stack.Push(key);
                claimed.Add(key);
                while (stack.Count > 0)
                {
                    int k = stack.Pop();
                    cluster.Add(k);
                    foreach (var n in MajorNeighbors(k, cells))
                    {
                        if (!cells.ContainsKey(n) || claimed.Contains(n)) continue;
                        if (n >= 2000)
                        {
                            float off = HueDiff(MajorCellHue(cells[n]), seedHue);
                            float lo = Mathf.Min(minOff, off), hi = Mathf.Max(maxOff, off);
                            if (hi - lo > 35f) continue;
                            minOff = lo; maxOff = hi;
                        }
                        claimed.Add(n);
                        stack.Push(n);
                    }
                }
                clusters.Add(cluster);
            }

            // Dark print unification: pads and their outlines (dark, desaturated) can
            // split across brightness bands and hue caps into several sub-floor clusters,
            // so a recolor of one row leaves parts of the print behind. Merge dark
            // desaturated clusters (mean V < 0.6, mean S < 0.17) that agree in value and,
            // for muted members, in hue, into one row per print. Runs after the flood,
            // before the coverage floor, and never touches brighter colors.
            clusters = MergeDarkPrintClusters(clusters, cells);

            foreach (var cluster in clusters)
            {
                int count = 0;
                double sr = 0, sg = 0, sb = 0, sv = 0;
                float minV = 1f, maxV = 0f;
                foreach (var k in cluster)
                {
                    var cell = cells[k];
                    count += cell.count;
                    sr += cell.sumR; sg += cell.sumG; sb += cell.sumB; sv += cell.sumV;
                    if (cell.minV < minV) minV = cell.minV;
                    if (cell.maxV > maxV) maxV = cell.maxV;
                }
                float coverage = (float)count / opaque;
                if (coverage < 0.001f) continue;
                float meanV = (float)(sv / count);
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
                    cellKeys = new HashSet<int>(cluster),
                };
                found.Add(mc);
            }
            found.Sort((a, b) => b.coverage.CompareTo(a.coverage));
            // Name collisions: qualify by the cluster's own brightness, so a pale
            // cluster is never labelled "Dark ...". A second collision falls back to
            // a number. Any "Light "/"Dark " prefix the base name already carries is
            // stripped first, so a re-qualified name never reads "Dark Dark Green".
            var used = new HashSet<string>();
            foreach (var mc in found)
            {
                string n = mc.name;
                if (used.Contains(n))
                {
                    string bare = n;
                    if (bare.StartsWith("Light ")) bare = bare.Substring(6);
                    else if (bare.StartsWith("Dark ")) bare = bare.Substring(5);
                    n = (mc.valMean >= 0.75f ? "Light " : mc.valMean <= 0.45f ? "Dark " : "") + bare;
                }
                string baseName = n;
                int suffix = 2;
                while (used.Contains(n))
                    n = baseName + " " + suffix++;
                mc.name = n;
                used.Add(n);
            }
            if (found.Count > maxColors) found.RemoveRange(maxColors, found.Count - maxColors);
            return found;
        }

        // Group dark, desaturated clusters (paw pads + their outlines) into one row
        // per print, so recoloring it moves the whole print. Candidates are clusters
        // whose mean is dark (V < 0.6) and desaturated (S < 0.17). Processed biggest
        // first, each candidate joins the first existing dark-print group it is
        // compatible with and otherwise starts its own group. A neutral candidate
        // (S < 0.03) joins on value alone (|mean V difference| <= 0.35 vs any member).
        // A muted candidate joins when it is hue-compatible (|hue difference| <= 35
        // degrees) and value-compatible (|mean V difference| <= 0.35) vs at least one
        // muted member of the group, except that the first muted member of a group
        // joins on value alone and sets the group's hue anchor. Bright or saturated
        // clusters pass through unchanged. Tiny warm clusters (far in hue) stay out
        // and drop below the coverage floor as before.
        static List<List<int>> MergeDarkPrintClusters(List<List<int>> clusters, Dictionary<int, MajorCell> cells)
        {
            var stats = new List<(int count, float hue, float sat, float val, bool neutral)>();
            foreach (var cluster in clusters)
            {
                int count = 0;
                double sr = 0, sg = 0, sb = 0, sv = 0;
                foreach (var k in cluster)
                {
                    var cell = cells[k];
                    count += cell.count;
                    sr += cell.sumR; sg += cell.sumG; sb += cell.sumB; sv += cell.sumV;
                }
                float meanV = (float)(sv / count);
                var mean = new Color((float)(sr / count), (float)(sg / count), (float)(sb / count));
                Color.RGBToHSV(mean, out float mh, out float ms, out float mv);
                stats.Add((count, mh, ms, meanV, ms < 0.03f));
            }
            var order = new List<int>();
            for (int i = 0; i < clusters.Count; i++) order.Add(i);
            order.Sort((a, b) => stats[b].count.CompareTo(stats[a].count));
            var groups = new List<List<int>>();
            var groupStats = new List<List<int>>();
            foreach (var i in order)
            {
                var st = stats[i];
                bool candidate = st.val < 0.6f && st.sat < 0.17f;
                if (!candidate)
                {
                    groups.Add(new List<int>(clusters[i]));
                    groupStats.Add(new List<int> { i });
                    continue;
                }
                int target = -1;
                for (int g = 0; g < groups.Count; g++)
                {
                    bool dark = true;
                    foreach (var m in groupStats[g])
                        if (!(stats[m].val < 0.6f && stats[m].sat < 0.17f)) { dark = false; break; }
                    if (!dark) continue;
                    if (st.neutral)
                    {
                        foreach (var m in groupStats[g])
                            if (Mathf.Abs(stats[m].val - st.val) <= 0.35f) { target = g; break; }
                    }
                    else
                    {
                        bool hasMuted = false;
                        foreach (var m in groupStats[g])
                            if (!stats[m].neutral) { hasMuted = true; break; }
                        if (!hasMuted)
                        {
                            foreach (var m in groupStats[g])
                                if (Mathf.Abs(stats[m].val - st.val) <= 0.35f) { target = g; break; }
                        }
                        else
                        {
                            foreach (var m in groupStats[g])
                            {
                                if (stats[m].neutral) continue;
                                if (Mathf.Abs(HueDiff(stats[m].hue, st.hue)) <= 35f
                                    && Mathf.Abs(stats[m].val - st.val) <= 0.35f) { target = g; break; }
                            }
                        }
                    }
                    if (target >= 0) break;
                }
                if (target >= 0)
                {
                    groups[target].AddRange(clusters[i]);
                    groupStats[target].Add(i);
                }
                else
                {
                    groups.Add(new List<int>(clusters[i]));
                    groupStats.Add(new List<int> { i });
                }
            }
            return groups;
        }

        static IEnumerable<int> MajorNeighbors(int key, Dictionary<int, MajorCell> cells)
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
                // Muted: hue bins next to this one, same brightness band — plus the same
                // hue bin in the next brightness band up or down, but only when the two
                // cells are close in brightness (mean values within 0.15). A color's light
                // parts and its darker shading are one color, so the light beige and its
                // folds join up; the pale paw-box background (bright) and the dark paw-print
                // outlines (dark) are too far apart in brightness to join, and the pads are
                // neutral grey, which never merges with anything.
                int hueBin = (key - 2000) / 3, band = (key - 2000) % 3;
                yield return 2000 + ((hueBin + 1) % 36) * 3 + band;
                yield return 2000 + ((hueBin + 35) % 36) * 3 + band;
                if (cells != null && cells.TryGetValue(key, out var cell))
                {
                    float meanV = cell.sumV / cell.count;
                    for (int nb = band - 1; nb <= band + 1; nb += 2)
                    {
                        if (nb < 0 || nb > 2) continue;
                        int nk = 2000 + hueBin * 3 + nb;
                        if (cells.TryGetValue(nk, out var ncell)
                            && Mathf.Abs(ncell.sumV / ncell.count - meanV) <= 0.15f)
                            yield return nk;
                    }
                }
            }
            // Neutral bands (1000-1002) never merge: black, grey and white are different colors.
        }

        static string MajorColorName(float h, float s, float v)
        {
            if (s < 0.03f) return v > 0.85f ? "White" : v < 0.15f ? "Black" : "Grey";
            float deg = h * 360f;
            string hueName = deg < 15f || deg >= 345f ? "Red" : deg < 45f ? "Orange" : deg < 70f ? "Yellow" :
                deg < 160f ? "Green" : deg < 200f ? "Teal" : deg < 260f ? "Blue" : deg < 300f ? "Purple" : "Pink";
            if (s < 0.17f)
            {
                // Muted warm colors read as brown/beige rather than orange/yellow.
                if (deg >= 15f && deg < 70f) return v > 0.6f ? "Beige" : v > 0.3f ? "Brown" : "Dark brown";
                return (v < 0.35f ? "Dark " : "") + hueName;
            }
            return (v < 0.35f ? "Dark " : v > 0.8f ? "Light " : "") + hueName;
        }

        /// <summary>
        /// Builds a mask RenderTexture straight from cluster membership: every pixel of the base
        /// texture whose bin key belongs to the cluster is in, everything else is out. Full
        /// resolution, so fine detail lines are covered exactly as detected — no seed sampling,
        /// no tolerance bands to fall between or over-reach with. A pixel only counts when at
        /// least 3 of its 8 neighbours are in the same cluster: a 1px seam fringe (blend pixels
        /// where two colors meet, which landed in a cluster by hue) has at most 2 same-cluster
        /// neighbours along its line, so it is not claimed, while real areas — weave lines,
        /// blobs, solid regions — are surrounded by their own color and stay covered.
        /// Returns null when the material can't be read, or when nothing of the cluster is left
        /// in it (base changed since Detect).
        /// </summary>
        RenderTexture MajorColorMask(TextureSet set, MajorColor mc)
        {
            var px = ReadBasePixels(set);
            if (px == null) return null;
            int w = set.width, h = set.height;
            var member = new bool[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                if (c.a >= 128 && mc.cellKeys.Contains(MajorCellKey(c, out _)))
                    member[i] = true;
            }
            var bytes = new byte[px.Length];
            int included = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!member[i]) continue;
                    int same = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (member[ny * w + nx]) same++;
                        }
                    if (same >= 3)
                    {
                        bytes[i] = 255;
                        included++;
                    }
                }
            }
            if (included == 0) return null;
            var tex = new Texture2D(w, h, TextureFormat.R8, false, true) { hideFlags = HideFlags.HideAndDontSave };
            tex.LoadRawTextureData(bytes);
            tex.Apply(false);
            var maskRT = engine.NewMaskRT(set, 0f);
            CopySelectionToMask(tex, maskRT);
            Object.DestroyImmediate(tex);
            return maskRT;
        }

        /// <summary>
        /// Recolors one major color cluster: the first change makes a Recolor layer for the
        /// cluster (masked by rasterizing the cluster's bins at full resolution, so the mask is
        /// exactly what detection found), later changes just move that layer's target color.
        /// The layer sits on top of the stack and is not clipped to one layer below, so it
        /// covers the whole material.
        /// </summary>
        public string ApplyMajorColor(TextureSet set, MajorColor mc, Color target)
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
                // Mask is rasterized straight from the cluster's bins: every pixel whose bin
                // belongs to the cluster is in. No seed sampling, so fine lines can't fall
                // between seeds and the mask can't bleed onto neighboring colors.
                var maskRT = MajorColorMask(set, mc);
                if (maskRT == null) return "That color isn't in the material anymore — press Detect major colors again.";
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
