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
        public bool maskEdited;          // the user painted this row's mask by hand — re-Detect keeps the mask as-is instead of rebuilding it
    }

    public partial class APSession
    {
        struct MajorCell
        {
            public int count;
            public float sumR, sumG, sumB, sumV, minV, maxV;
            public float sumHue; // linear mean hue of the sampled pixels (0-1)
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
            return MajorCellKeyFromHSV(hue, sat, val);
        }

        static int MajorCellKeyFromHSV(float hue, float sat, float val)
        {
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
            int sw = (w + step - 1) / step, sh = (h + step - 1) / step;
            // Cell key of each sampled pixel (-1 when transparent): the
            // plurality reassignment below reads adjacency off this grid.
            var sampleKeys = new int[sw * sh];
            for (int i = 0; i < sampleKeys.Length; i++) sampleKeys[i] = -1;
            var cells = new Dictionary<int, MajorCell>();
            int opaque = 0;
            for (int y = 0, sy = 0; y < h; y += step, sy++)
            {
                for (int x = 0, sx = 0; x < w; x += step, sx++)
                {
                    var c = px[y * w + x];
                    if (c.a < 128) continue;
                    opaque++;
                    Color.RGBToHSV((Color)c, out float hue, out float sat, out float val);
                    int key = MajorCellKeyFromHSV(hue, sat, val);
                    sampleKeys[sy * sw + sx] = key;
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    if (!cells.TryGetValue(key, out var cell))
                        cell = new MajorCell { minV = val, maxV = val };
                    cell.count++;
                    cell.sumR += r; cell.sumG += g; cell.sumB += b; cell.sumV += val; cell.sumHue += hue;
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

            // A spatially-connected gradient band can split across two rows when its
            // palest cells sit within merge distance of a different color's cells:
            // the band's pale end joins the other row and stays pale while the rest
            // recolors. Reassign a muted cell to the cluster it touches most, when
            // that cluster is close in hue and brightness. Runs before the dark-print
            // merge, so the merge sees the corrected rows.
            clusters = ReassignPluralityCells(clusters, cells, sampleKeys, sw, sh);

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

        /// <summary>
        /// A muted cell joins the cluster it touches most on the sampled grid, when
        /// that cluster is close in hue (within 35 degrees of the cluster's mean hue)
        /// and brightness (mean value within 0.35), and both the cell and the winning
        /// touch count are big enough to trust (at least 100 sampled pixels). A cell
        /// only moves when it touches the other cluster MORE than its own, so a cell
        /// embedded in its own color stays put. Decided from the pre-move clusters,
        /// then applied all at once.
        /// </summary>
        static List<List<int>> ReassignPluralityCells(List<List<int>> clusters, Dictionary<int, MajorCell> cells, int[] sampleKeys, int sw, int sh)
        {
            int n = clusters.Count;
            if (n < 2) return clusters;
            var cellCluster = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
                foreach (var k in clusters[i]) cellCluster[k] = i;
            var rowHue = new float[n];
            var rowVal = new float[n];
            for (int i = 0; i < n; i++)
            {
                int count = 0;
                double sh2 = 0, sv = 0;
                foreach (var k in clusters[i])
                {
                    var cell = cells[k];
                    count += cell.count;
                    sh2 += cell.sumHue;
                    sv += cell.sumV;
                }
                rowHue[i] = count == 0 ? 0f : (float)(sh2 / count);
                rowVal[i] = count == 0 ? 0f : (float)(sv / count);
            }
            // Touch counts: for each sampled adjacent pair with different cell keys,
            // each cell records a touch with the other cell's cluster.
            var touch = new Dictionary<int, int[]>();
            for (int sy = 0; sy < sh; sy++)
            {
                for (int sx = 0; sx < sw; sx++)
                {
                    int ka = sampleKeys[sy * sw + sx];
                    if (ka < 0) continue;
                    if (sx + 1 < sw)
                    {
                        int kb = sampleKeys[sy * sw + sx + 1];
                        if (kb >= 0 && kb != ka) RecordTouch(touch, cellCluster, ka, kb, n);
                    }
                    if (sy + 1 < sh)
                    {
                        int kb = sampleKeys[(sy + 1) * sw + sx];
                        if (kb >= 0 && kb != ka) RecordTouch(touch, cellCluster, ka, kb, n);
                    }
                }
            }
            var moves = new List<(int cell, int to)>();
            foreach (var kv in cells)
            {
                int cell = kv.Key;
                if (cell < 2000) continue; // muted cells only
                var c = kv.Value;
                if (c.count < 100) continue;
                if (!cellCluster.TryGetValue(cell, out int own)) continue;
                if (!touch.TryGetValue(cell, out var t)) continue;
                int ownT = t[own];
                int best = -1, bestN = 0;
                for (int i = 0; i < n; i++)
                {
                    if (i == own) continue;
                    if (t[i] > bestN) { bestN = t[i]; best = i; }
                }
                if (best < 0 || bestN <= ownT || bestN < 100) continue;
                float cellHue = c.sumHue / c.count;
                float cellVal = c.sumV / c.count;
                if (Mathf.Abs(HueDiff(cellHue, rowHue[best])) > 35f) continue;
                if (Mathf.Abs(cellVal - rowVal[best]) > 0.35f) continue;
                moves.Add((cell, best));
            }
            foreach (var m in moves)
            {
                int from = cellCluster[m.cell];
                clusters[from].Remove(m.cell);
                clusters[m.to].Add(m.cell);
                cellCluster[m.cell] = m.to;
            }
            clusters.RemoveAll(c => c.Count == 0);
            return clusters;
        }

        static void RecordTouch(Dictionary<int, int[]> touch, Dictionary<int, int> cellCluster, int ka, int kb, int n)
        {
            if (!cellCluster.TryGetValue(ka, out int ra) || !cellCluster.TryGetValue(kb, out int rb)) return;
            if (!touch.TryGetValue(ka, out var ta)) { ta = new int[n]; touch[ka] = ta; }
            if (!touch.TryGetValue(kb, out var tb)) { tb = new int[n]; touch[kb] = tb; }
            ta[rb]++;
            tb[ra]++;
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
        /// With cleanup on (the default), the masks are then cleaned up together: thin speck
        /// lines and fringes join the color that surrounds them, small holes fill, and small
        /// isolated components are dropped. There used to be a final step here that eroded
        /// every mask 3px away from dark pixels of other rows, to keep a recolor from
        /// bleeding onto dark text or print edges — removed in 0.14.20, because it did the
        /// opposite on real prints: it cut a pale halo around the paw-box frame (the beige
        /// must touch the dark frame), punched black squares into the paws and frame (the
        /// print's own dark crease pixels sit in other rows, so the mask was eroded around
        /// them), and stripped the pale background's mask ringing the pads, leaving a white
        /// fringe. The other cleanup steps already keep the 0.14.18 wins (readable text,
        /// no fringe in the box, shoulder band whole) without it, and the Edit mask button
        /// is the hand fallback for anything left. Returns null when the material can't be
        /// read, or when nothing of the cluster is left in it (base changed since Detect).
        /// </summary>
        RenderTexture MajorColorMask(TextureSet set, MajorColor mc, List<MajorColor> all, bool cleanup)
        {
            var px = ReadBasePixels(set);
            if (px == null) return null;
            int w = set.width, h = set.height;
            var rows = all != null && all.Count > 0 ? all : new List<MajorColor> { mc };
            int targetRow = rows.IndexOf(mc);
            if (targetRow < 0) targetRow = 0;
            var keyToRow = new Dictionary<int, int>();
            for (int r = 0; r < rows.Count; r++)
                foreach (var k in rows[r].cellKeys) keyToRow[k] = r;
            // Which cluster each pixel belongs to (-1 when its color is not in any
            // detected cluster).
            var pixelRow = new int[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                if (c.a < 128) { pixelRow[i] = -1; continue; }
                int key = MajorCellKey(c, out _);
                pixelRow[i] = keyToRow.TryGetValue(key, out int r) ? r : -1;
            }
            int n = rows.Count;
            var masks = new bool[n][];
            for (int r = 0; r < n; r++)
            {
                var member = new bool[px.Length];
                for (int i = 0; i < px.Length; i++) member[i] = pixelRow[i] == r;
                masks[r] = MajorMaskSeed(member, w, h, cleanup);
            }
            if (cleanup)
            {
                masks = ReassignMasks(masks, w, h, 2, 3, 1);
                masks = ReassignMasks(masks, w, h, 1, 10, 2);
                ReassignSmallComponents(masks, pixelRow, w, h, 400, 0.40f);
            }
            for (int r = 0; r < n; r++)
            {
                if (!cleanup) continue;
                masks[r] = RemoveSmallComponents(masks[r], w, h, 25);
            }
            var bytes = new byte[px.Length];
            int included = 0;
            for (int i = 0; i < px.Length; i++)
                if (masks[targetRow][i]) { bytes[i] = 255; included++; }
            if (included == 0) return null;
            var tex = new Texture2D(w, h, TextureFormat.R8, false, true) { hideFlags = HideFlags.HideAndDontSave };
            tex.LoadRawTextureData(bytes);
            tex.Apply(false);
            var maskRT = engine.NewMaskRT(set, 0f);
            CopySelectionToMask(tex, maskRT);
            Object.DestroyImmediate(tex);
            return maskRT;
        }

        /// <summary>Seed mask for one row: members with at least 3 same-row neighbours,
        /// then (cleanup only) a morphological open and close to drop thin speck lines
        /// and fill small holes.</summary>
        static bool[] MajorMaskSeed(bool[] member, int w, int h, bool cleanup)
        {
            var counts = NeighborCount(member, w, h, 1);
            var mask = new bool[w * h];
            for (int i = 0; i < mask.Length; i++)
                if (member[i] && counts[i] >= 3) mask[i] = true;
            if (!cleanup) return mask;
            mask = MorphDilate(MorphErode(mask, w, h), w, h);
            mask = MorphErode(MorphDilate(mask, w, h), w, h);
            return mask;
        }

        /// <summary>Drops connected components smaller than minSize from a mask.</summary>
        static bool[] RemoveSmallComponents(bool[] mask, int w, int h, int minSize)
        {
            var labels = LabelComponents(mask, w, h, out int compCount);
            if (compCount == 0) return mask;
            var sizes = new int[compCount + 1];
            for (int p = 0; p < labels.Length; p++)
                if (labels[p] != 0) sizes[labels[p]]++;
            var result = new bool[w * h];
            for (int p = 0; p < labels.Length; p++)
            {
                int c = labels[p];
                if (c != 0 && sizes[c] >= minSize) result[p] = true;
            }
            return result;
        }

        /// <summary>
        /// Reassigns each pixel to the row that owns a strict plurality of its
        /// neighbours (ties stay put): thin lines and fringes join the color that
        /// surrounds them instead of the one their own hue landed them in, and
        /// unmasked gaps fill with the surrounding color. Runs on all rows at once
        /// so a pixel can move between any two rows.
        /// </summary>
        static bool[][] ReassignMasks(bool[][] masks, int w, int h, int iters, int thresh, int radius)
        {
            int n = masks.Length;
            var cur = new bool[n][];
            for (int i = 0; i < n; i++) cur[i] = (bool[])masks[i].Clone();
            for (int it = 0; it < iters; it++)
            {
                var counts = new int[n][];
                for (int i = 0; i < n; i++) counts[i] = NeighborCount(cur[i], w, h, radius);
                var next = new bool[n][];
                for (int i = 0; i < n; i++) next[i] = new bool[w * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int p = y * w + x;
                        int bestN = 0;
                        for (int i = 0; i < n; i++)
                            if (counts[i][p] > bestN) bestN = counts[i][p];
                        int best = -1, nMax = 0;
                        for (int i = 0; i < n; i++)
                            if (counts[i][p] == bestN) { nMax++; if (best < 0) best = i; }
                        int row = -1;
                        if (bestN >= thresh && nMax == 1) row = best;
                        else
                        {
                            // Stay: last mask wins when a pixel is in more than one.
                            for (int i = n - 1; i >= 0; i--)
                                if (cur[i][p]) { row = i; break; }
                        }
                        if (row >= 0) next[row][p] = true;
                    }
                }
                cur = next;
            }
            return cur;
        }

        /// <summary>
        /// Small mask components that sit inside a different color's area join that
        /// color: a thin pale line inside the paw-box background (beige by hue, but
        /// surrounded by the pale background) recolors with the background instead of
        /// showing up as a blue stripe. Only components under maxSize move, and only
        /// when one other row owns at least minFrac of their boundary.
        /// </summary>
        static void ReassignSmallComponents(bool[][] masks, int[] pixelRow, int w, int h, int maxSize, float minFrac)
        {
            int n = masks.Length;
            for (int i = 0; i < n; i++)
            {
                var labels = LabelComponents(masks[i], w, h, out int compCount);
                if (compCount == 0) continue;
                var sizes = new int[compCount + 1];
                for (int p = 0; p < labels.Length; p++)
                    if (labels[p] != 0) sizes[labels[p]]++;
                // Boundary row counts per small component: each boundary pixel votes
                // once per component it touches, with its own cluster row.
                var boundary = new int[compCount + 1][];
                for (int c = 1; c <= compCount; c++)
                    if (sizes[c] < maxSize) boundary[c] = new int[n];
                var seen = new int[8];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int p = y * w + x;
                        int row = pixelRow[p];
                        if (row < 0) continue;
                        // Distinct neighbour labels of this pixel.
                        int found = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int c = labels[ny * w + nx];
                                if (c == 0 || sizes[c] >= maxSize || boundary[c] == null) continue;
                                if (labels[p] == c) continue;
                                bool dup = false;
                                for (int k = 0; k < found; k++)
                                    if (seen[k] == c) { dup = true; break; }
                                if (dup) continue;
                                seen[found++] = c;
                                boundary[c][row]++;
                            }
                        }
                    }
                }
                var moveTo = new int[compCount + 1];
                for (int c = 1; c <= compCount; c++)
                {
                    moveTo[c] = -1;
                    if (sizes[c] >= maxSize || boundary[c] == null) continue;
                    int best = -1, bestN = 0, total = 0;
                    for (int r = 0; r < n; r++)
                    {
                        total += boundary[c][r];
                        if (boundary[c][r] > bestN) { bestN = boundary[c][r]; best = r; }
                    }
                    if (best < 0 || best == i || total == 0) continue;
                    if ((float)bestN / total < minFrac) continue;
                    moveTo[c] = best;
                }
                for (int p = 0; p < labels.Length; p++)
                {
                    int c = labels[p];
                    if (c != 0 && moveTo[c] >= 0) { masks[i][p] = false; masks[moveTo[c]][p] = true; }
                }
            }
        }

        /// <summary>8-connected component labels for a bool mask (0 = background).</summary>
        static int[] LabelComponents(bool[] m, int w, int h, out int compCount)
        {
            var labels = new int[w * h];
            compCount = 0;
            var stack = new Stack<int>();
            for (int p = 0; p < m.Length; p++)
            {
                if (!m[p] || labels[p] != 0) continue;
                compCount++;
                labels[p] = compCount;
                stack.Push(p);
                while (stack.Count > 0)
                {
                    int q = stack.Pop();
                    int qx = q % w, qy = q / w;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = qx + dx, ny = qy + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int r = ny * w + nx;
                            if (m[r] && labels[r] == 0) { labels[r] = compCount; stack.Push(r); }
                        }
                    }
                }
            }
            return labels;
        }

        /// <summary>Integral image (summed-area table) of a bool mask, (w+1)x(h+1),
        /// so a window sum is four lookups whatever the radius.</summary>
        static int[] IntegralImage(bool[] m, int w, int h)
        {
            var ii = new int[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                int rowSum = 0;
                for (int x = 0; x < w; x++)
                {
                    if (m[y * w + x]) rowSum++;
                    ii[(y + 1) * (w + 1) + x + 1] = ii[y * (w + 1) + x + 1] + rowSum;
                }
            }
            return ii;
        }

        /// <summary>Sum of m over the (2r+1)x(2r+1) window centred at (x,y), clamped to the image.</summary>
        static int WindowSum(int[] ii, int w, int h, int x, int y, int r)
        {
            int x0 = Mathf.Max(0, x - r), x1 = Mathf.Min(w - 1, x + r);
            int y0 = Mathf.Max(0, y - r), y1 = Mathf.Min(h - 1, y + r);
            int stride = w + 1;
            return ii[(y1 + 1) * stride + x1 + 1] - ii[y0 * stride + x1 + 1]
                 - ii[(y1 + 1) * stride + x0] + ii[y0 * stride + x0];
        }

        /// <summary>For each pixel, how many of its neighbours (within radius, excluding
        /// itself) are set in m.</summary>
        static int[] NeighborCount(bool[] m, int w, int h, int radius)
        {
            var ii = IntegralImage(m, w, h);
            var counts = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    counts[i] = WindowSum(ii, w, h, x, y, radius) - (m[i] ? 1 : 0);
                }
            return counts;
        }

        /// <summary>3x3 erosion: a pixel stays only when all 8 neighbours are set
        /// (out-of-bounds counts as unset, so edge pixels are cleared).</summary>
        static bool[] MorphErode(bool[] m, int w, int h)
        {
            var ii = IntegralImage(m, w, h);
            var result = new bool[w * h];
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    if (WindowSum(ii, w, h, x, y, 1) == 9) result[i] = true;
                }
            return result;
        }

        /// <summary>3x3 dilation: a pixel is set when any neighbour (or itself) is set.</summary>
        static bool[] MorphDilate(bool[] m, int w, int h)
        {
            var ii = IntegralImage(m, w, h);
            var result = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (WindowSum(ii, w, h, x, y, 1) > 0) result[i] = true;
                }
            return result;
        }

        /// <summary>
        /// Recolors one major color cluster: the first change makes a Recolor layer for the
        /// cluster (masked by rasterizing the cluster's bins at full resolution, so the mask is
        /// exactly what detection found), later changes just move that layer's target color.
        /// The layer sits on top of the stack and is not clipped to one layer below, so it
        /// covers the whole material.
        /// </summary>
        public string ApplyMajorColor(TextureSet set, MajorColor mc, Color target, List<MajorColor> all, bool cleanup)
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
                // Cleanup (when on) smooths all rows' masks together before this one is
                // used, so speckles and fringes are fixed the first time a color changes.
                var maskRT = MajorColorMask(set, mc, all, cleanup);
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

        /// <summary>
        /// Adopts the previous Detect's "Major: &lt;name&gt;" Recolor layers onto the new rows, by name.
        /// Without this, a re-Detect makes fresh rows with no layer, and the next color change stacks a
        /// NEW layer over the old one — the old mask keeps showing through the new mask's gaps.
        /// With adoption there is exactly one "Major: &lt;name&gt;" layer per row.
        /// A row whose mask was hand-edited (maskEdited) keeps its layer's mask exactly as the user left it,
        /// so manual fixes survive re-Detect. No separate delta store: the layer's mask IS the edited mask,
        /// and ProjectIO saves it with the project. A delta recorded against the old auto mask would misapply
        /// once the base changed and the auto mask moved; keeping the edited mask verbatim is exact, and
        /// Reset is the escape hatch for a fresh mask. A row that was never hand-edited gets a fresh auto
        /// mask rebuilt into its adopted layer (undoable), so re-Detect after a base change still refreshes it.
        /// </summary>
        public void AdoptMajorLayers(TextureSet set, List<MajorColor> fresh, List<MajorColor> previous, bool cleanup)
        {
            if (set == null || fresh == null) return;
            var oldByName = new Dictionary<string, MajorColor>();
            if (previous != null)
                foreach (var o in previous)
                    if (!oldByName.ContainsKey(o.name)) oldByName[o.name] = o;
            foreach (var mc in fresh)
            {
                PaintLayer layer = null;
                foreach (var l in set.layers)
                    if (l.name == "Major: " + mc.name) { layer = l; break; }
                if (layer == null) continue;
                // Only adopt our own Recolor layers: a user-renamed layer that happens to share the name isn't ours.
                if (layer.kind != LayerKind.Adjustment || layer.adjMode != AdjustMode.Recolor) continue;
                mc.layerId = layer.id;
                mc.target = layer.adjColor;
                bool edited = oldByName.TryGetValue(mc.name, out var old) && old.maskEdited;
                mc.maskEdited = edited;
                if (edited) continue; // keep the hand-fixed mask exactly as the user left it
                // Fresh auto mask into the adopted layer, so the old mask can't show through the new one.
                var newMask = MajorColorMask(set, mc, fresh, cleanup);
                if (newMask == null) continue; // cluster gone from the material: keep the old mask rather than wiping it
                if (layer.mask == null)
                {
                    // The mask was removed from the layer: put the rebuilt one back directly.
                    layer.mask = newMask;
                    layer.maskEnabled = true;
                    MeasureSource(set, layer);
                    Changed(set);
                    continue;
                }
                var rebuilt = newMask;
                PixelEdit(set, layer.mask, "Rebuild major mask", () => Graphics.Blit(rebuilt, layer.mask));
                APUtil.Release(ref newMask);
                MeasureSource(set, layer);
            }
        }

        /// <summary>
        /// Called when a brush stroke ends while a Major mask is being edited: if the stroke painted
        /// that row's layer mask, the row counts as hand-edited (so a later Detect keeps its mask
        /// instead of rebuilding it), and the Recolor stats are re-fit to the edited area — the same
        /// re-measure ColorRecolorClick does after changing a Recolor mask. One undo entry per stroke
        /// comes from the stroke itself; this only records that an edit happened.
        /// </summary>
        public void NoteMajorMaskStroke(MajorColor mc, StrokeContext stroke)
        {
            if (mc == null || stroke == null) return;
            foreach (var t in stroke.targets)
            {
                if (!t.mask || t.layer == null || t.layer.id != mc.layerId) continue;
                mc.maskEdited = true;
                if (t.layer.adjMode == AdjustMode.Recolor) MeasureSource(t.set, t.layer);
                return;
            }
        }
    }
}
