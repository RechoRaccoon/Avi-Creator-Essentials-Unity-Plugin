using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>
    /// "Mirror to other side": copies the selected area's pixels onto its mirror-image counterpart on the other side
    /// of the model (e.g. one eye onto the other), matching points through 3D space on the rest pose, so it works
    /// however the two areas are laid out in the UVs.
    /// </summary>
    public partial class APSession
    {
        struct Tri
        {
            public Vector3 a, b, c;
            public Vector2 ua, ub, uc;
            public Vector3 n;
        }

        /// <summary>
        /// Mirrors the current selection across the avatar's centre onto a new layer.
        /// Returns a message for the user; the layer is only added when something was copied.
        /// </summary>
        public string MirrorSelectionToOtherSide(TextureSet set)
        {
            if (set == null || !set.HasSelection) return "Select the area that looks right first (Select tool, S).";
            if (!set.initialized) InitializeSet(set);
            meshes.EnsureRestMeshes();

            // ---- source triangles (the selection), rest pose, avatar-root space
            var source = new List<Tri>();
            var sourceKeys = new HashSet<long>();
            foreach (var kv in set.selection)
            {
                int entry = kv.Key >> 8, submesh = kv.Key & 0xFF;
                if (entry < 0 || entry >= meshes.entries.Count) continue;
                var e = meshes.entries[entry];
                if (e.restMesh == null || submesh >= e.SubmeshCount) continue;
                var v = e.restMesh.vertices;
                var uv = e.GetUVs(set.uvChannel);
                var idx = e.submeshIndices[submesh];
                foreach (int first in kv.Value)
                {
                    if (first + 2 >= idx.Length) continue;
                    source.Add(MakeTri(v, uv, idx[first], idx[first + 1], idx[first + 2]));
                    sourceKeys.Add(((long)kv.Key << 32) | (uint)first);
                }
            }
            if (source.Count == 0) return "The selection has no triangles on this texture.";

            Vector3 cs = Vector3.zero;
            float areaSum = 0f;
            var bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var bmax = -bmin;
            foreach (var t in source)
            {
                float ar = Vector3.Cross(t.b - t.a, t.c - t.a).magnitude * 0.5f + 1e-12f;
                cs += (t.a + t.b + t.c) / 3f * ar;
                areaSum += ar;
                bmin = Vector3.Min(bmin, Vector3.Min(t.a, Vector3.Min(t.b, t.c)));
                bmax = Vector3.Max(bmax, Vector3.Max(t.a, Vector3.Max(t.b, t.c)));
            }
            cs /= areaSum;
            float radius = Mathf.Max(1e-4f, (bmax - bmin).magnitude * 0.5f);
            var grid = new TriGrid(source, bmin, bmax);

            // ---- candidate target triangles: everything else on this texture
            var candidates = new List<(Tri tri, int entry, int submesh, int first)>();
            foreach (var u in set.users)
            {
                if (u.renderer < 0 || u.renderer >= meshes.entries.Count) continue;
                var e = meshes.entries[u.renderer];
                if (e.restMesh == null || u.submesh >= e.SubmeshCount) continue;
                var v = e.restMesh.vertices;
                var uv = e.GetUVs(set.uvChannel);
                var idx = e.submeshIndices[u.submesh];
                int key = TextureSet.SelKey(u.renderer, u.submesh);
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    if (sourceKeys.Contains(((long)key << 32) | (uint)i)) continue;
                    candidates.Add((MakeTri(v, uv, idx[i], idx[i + 1], idx[i + 2]), u.renderer, u.submesh, i));
                }
            }

            // ---- find the counterpart: mirror across the avatar's centre (X = 0), then align the two areas' centres
            Vector3 offset = Vector3.zero;
            List<(Tri tri, int entry, int submesh, int first)> targets = null;
            float[] tolerances = { radius * 0.35f, radius * 0.12f };
            for (int pass = 0; pass < tolerances.Length; pass++)
            {
                float tol = Mathf.Max(tolerances[pass], 0.0015f);
                var found = new List<(Tri, int, int, int)>();
                Vector3 ct = Vector3.zero;
                float at = 0f;
                foreach (var c in candidates)
                {
                    var t = c.tri;
                    var mc = Mirror((t.a + t.b + t.c) / 3f) + offset;
                    if (mc.x < bmin.x - tol || mc.y < bmin.y - tol || mc.z < bmin.z - tol ||
                        mc.x > bmax.x + tol || mc.y > bmax.y + tol || mc.z > bmax.z + tol) continue;
                    // The whole mirrored triangle must lie on the selected surface, its centre especially closely,
                    // so the skin around the area isn't picked up.
                    if (!Near(grid, mc, tol * 0.5f) || !Near(grid, Mirror(t.a) + offset, tol) ||
                        !Near(grid, Mirror(t.b) + offset, tol) || !Near(grid, Mirror(t.c) + offset, tol)) continue;
                    found.Add(c);
                    float ar = Vector3.Cross(t.b - t.a, t.c - t.a).magnitude * 0.5f + 1e-12f;
                    ct += (t.a + t.b + t.c) / 3f * ar;
                    at += ar;
                }
                if (found.Count == 0) break;
                targets = found;
                // Line the mirrored target centre up with the source centre (avatars are rarely perfectly centred).
                if (pass == 0) offset = cs - Mirror(ct / at);
            }
            if (targets == null || targets.Count == 0)
                return "Couldn't find a matching area on the other side of the model. This works for parts that mirror left and right, like eyes, ears or hands.";

            // ---- copy pixels: every texel of the target looks up the mirrored point on the source
            var exportRT = engine.CompositeForExport(set, 16);
            var srcTex = APUtil.ReadToTexture(exportRT);
            APUtil.Release(ref exportRT);
            var src = srcTex.GetPixels32();
            Object.DestroyImmediate(srcTex);
            int w = set.width, h = set.height;
            var outPx = new Color32[w * h];
            float maxDist = radius * 0.25f;
            int written = 0;

            foreach (var c in targets)
            {
                var t = c.tri;
                var pa = new Vector2(t.ua.x * w - 0.5f, t.ua.y * h - 0.5f);
                var pb = new Vector2(t.ub.x * w - 0.5f, t.ub.y * h - 0.5f);
                var pc = new Vector2(t.uc.x * w - 0.5f, t.uc.y * h - 0.5f);
                float area = Cross2(pb - pa, pc - pa);
                if (Mathf.Abs(area) < 1e-8f) continue;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x)) - 1));
                int x1 = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x)) + 1));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y)) - 1));
                int y1 = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y)) + 1));
                // Texels up to ~0.7 px outside the triangle count too, so neighbouring triangles leave no gaps.
                float edgePad = 0.7f;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = new Vector2(x, y);
                        float wa = Cross2(pb - p, pc - p) / area;
                        float wb = Cross2(pc - p, pa - p) / area;
                        float wc = 1f - wa - wb;
                        if (!InsideWithPad(wa, wb, wc, pa, pb, pc, edgePad)) continue;
                        // Clamp to the triangle for the 3D position.
                        float ca = Mathf.Max(0, wa), cb = Mathf.Max(0, wb), cc = Mathf.Max(0, wc);
                        float s = ca + cb + cc;
                        if (s <= 0f) continue;
                        ca /= s; cb /= s; cc /= s;
                        var P = Mirror(t.a * ca + t.b * cb + t.c * cc) + offset;
                        if (!grid.Closest(P, maxDist, out int si, out Vector3 bary)) continue;
                        var st = source[si];
                        var suv = st.ua * bary.x + st.ub * bary.y + st.uc * bary.z;
                        outPx[y * w + x] = SampleBilinear(src, w, h, suv);
                        outPx[y * w + x].a = 255;
                        written++;
                    }
                }
            }
            if (written == 0) return "Found the other side, but none of its pixels lined up with the selection.";

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixels32(outPx);
            tex.Apply(false);
            var layer = new PaintLayer { name = UniqueName(set, "Mirrored"), kind = LayerKind.Paint };
            layer.content = engine.NewContentRT(set);
            APUtil.Copy(tex, layer.content);
            Object.DestroyImmediate(tex);
            InsertLayer(set, layer, set.layers.Count, "Mirror to other side");
            Changed(set);
            return "Mirrored onto the other side as a new layer (" + targets.Count + " triangles). Hide or delete the layer to undo it, or erase parts of it.";
        }

        static Tri MakeTri(Vector3[] v, Vector2[] uv, int i0, int i1, int i2)
        {
            var t = new Tri { a = v[i0], b = v[i1], c = v[i2] };
            if (uv != null && uv.Length == v.Length) { t.ua = uv[i0]; t.ub = uv[i1]; t.uc = uv[i2]; }
            t.n = Vector3.Cross(t.b - t.a, t.c - t.a).normalized;
            return t;
        }

        static Vector3 Mirror(Vector3 p) => new Vector3(-p.x, p.y, p.z);

        static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool Near(TriGrid grid, Vector3 p, float tol) => grid.Closest(p, tol, out _, out _);

        static bool InsideWithPad(float wa, float wb, float wc, Vector2 pa, Vector2 pb, Vector2 pc, float pad)
        {
            if (wa >= 0f && wb >= 0f && wc >= 0f) return true;
            // Distance (in texels) outside each edge: barycentric * 2*area / edge length.
            float area2 = Mathf.Abs(Cross2(pb - pa, pc - pa));
            float da = wa < 0 ? -wa * area2 / Mathf.Max(1e-6f, (pc - pb).magnitude) : 0f;
            float db = wb < 0 ? -wb * area2 / Mathf.Max(1e-6f, (pa - pc).magnitude) : 0f;
            float dc = wc < 0 ? -wc * area2 / Mathf.Max(1e-6f, (pb - pa).magnitude) : 0f;
            return Mathf.Max(da, Mathf.Max(db, dc)) <= pad;
        }

        static Color32 SampleBilinear(Color32[] px, int w, int h, Vector2 uv)
        {
            float x = uv.x * w - 0.5f, y = uv.y * h - 0.5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            Color32 P(int xi, int yi)
            {
                xi = ((xi % w) + w) % w;
                yi = ((yi % h) + h) % h;
                return px[yi * w + xi];
            }
            var c00 = P(x0, y0); var c10 = P(x0 + 1, y0); var c01 = P(x0, y0 + 1); var c11 = P(x0 + 1, y0 + 1);
            byte L(byte a, byte b, byte c, byte d) =>
                (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy)), 0, 255);
            return new Color32(L(c00.r, c10.r, c01.r, c11.r), L(c00.g, c10.g, c01.g, c11.g), L(c00.b, c10.b, c01.b, c11.b), 255);
        }

        /// <summary>Uniform grid over triangles for closest-point queries.</summary>
        class TriGrid
        {
            readonly List<Tri> m_Tris;
            readonly Vector3 m_Min;
            readonly float m_Cell;
            readonly int m_N;
            readonly List<int>[] m_Cells;

            public TriGrid(List<Tri> tris, Vector3 min, Vector3 max)
            {
                m_Tris = tris;
                var size = max - min;
                float extent = Mathf.Max(size.x, Mathf.Max(size.y, size.z), 1e-4f);
                m_N = Mathf.Clamp(Mathf.CeilToInt(Mathf.Pow(tris.Count, 1f / 3f) * 1.5f), 1, 48);
                m_Cell = extent / m_N * 1.0001f;
                m_Min = min;
                m_Cells = new List<int>[m_N * m_N * m_N];
                for (int i = 0; i < tris.Count; i++)
                {
                    var t = tris[i];
                    var lo = Cell(Vector3.Min(t.a, Vector3.Min(t.b, t.c)));
                    var hi = Cell(Vector3.Max(t.a, Vector3.Max(t.b, t.c)));
                    for (int z = lo.z; z <= hi.z; z++)
                        for (int y = lo.y; y <= hi.y; y++)
                            for (int x = lo.x; x <= hi.x; x++)
                            {
                                int k = (z * m_N + y) * m_N + x;
                                (m_Cells[k] ?? (m_Cells[k] = new List<int>())).Add(i);
                            }
                }
            }

            Vector3Int Cell(Vector3 p)
            {
                var q = (p - m_Min) / m_Cell;
                return new Vector3Int(Mathf.Clamp((int)q.x, 0, m_N - 1), Mathf.Clamp((int)q.y, 0, m_N - 1), Mathf.Clamp((int)q.z, 0, m_N - 1));
            }

            public bool Closest(Vector3 p, float maxDist, out int tri, out Vector3 bary)
            {
                tri = -1;
                bary = Vector3.zero;
                float best = maxDist * maxDist;
                var lo = Cell(p - Vector3.one * maxDist);
                var hi = Cell(p + Vector3.one * maxDist);
                // Points outside the grid's box by more than maxDist can't be near anything.
                var gmax = m_Min + Vector3.one * (m_Cell * m_N);
                if (p.x < m_Min.x - maxDist || p.y < m_Min.y - maxDist || p.z < m_Min.z - maxDist ||
                    p.x > gmax.x + maxDist || p.y > gmax.y + maxDist || p.z > gmax.z + maxDist) return false;
                HashSet<int> seen = null;
                for (int z = lo.z; z <= hi.z; z++)
                    for (int y = lo.y; y <= hi.y; y++)
                        for (int x = lo.x; x <= hi.x; x++)
                        {
                            var list = m_Cells[(z * m_N + y) * m_N + x];
                            if (list == null) continue;
                            foreach (int i in list)
                            {
                                if (seen == null) seen = new HashSet<int>();
                                if (!seen.Add(i)) continue;
                                var t = m_Tris[i];
                                var q = ClosestOnTriangle(p, t.a, t.b, t.c, out var b);
                                float d = (q - p).sqrMagnitude;
                                if (d <= best)
                                {
                                    best = d;
                                    tri = i;
                                    bary = b;
                                }
                            }
                        }
                return tri >= 0;
            }
        }

        /// <summary>Closest point on a triangle (Ericson, Real-Time Collision Detection) with its barycentric weights.</summary>
        static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new Vector3(1, 0, 0); return a; }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new Vector3(0, 1, 0); return b; }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                bary = new Vector3(1 - v, v, 0);
                return a + v * ab;
            }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new Vector3(0, 0, 1); return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                bary = new Vector3(1 - w, 0, w);
                return a + w * ac;
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                bary = new Vector3(0, 1 - w, w);
                return b + w * (c - b);
            }
            float denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww = vc * denom;
            bary = new Vector3(1 - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }
    }
}
