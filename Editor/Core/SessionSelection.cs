using System;
using System.Collections.Generic;
using UnityEngine;

namespace AviCreatorEssentials
{
    public enum SelectOp
    {
        Replace,
        Add,
        Subtract,
    }

    /// <summary>A triangle of a texture set, identified by renderer entry, submesh and first index.</summary>
    public struct TriRef
    {
        public int entry;
        public int submesh;
        public int first;

        public TriRef(int entry, int submesh, int first)
        {
            this.entry = entry;
            this.submesh = submesh;
            this.first = first;
        }
    }


    /// <summary>
    /// Quads recovered from a triangle mesh (two triangles that share a diagonal), with their neighbours, so a
    /// loop of polygons can be walked across opposite edges the way Blender's Alt+click does.
    /// </summary>
    public class QuadTopology
    {
        public int[] quadOfTri;                                   // quad id per triangle (index / 3), -1 for a lone triangle
        public List<int> quadTriA = new List<int>();              // the two triangles (index / 3) of each quad
        public List<int> quadTriB = new List<int>();
        public List<int> ring = new List<int>();                  // 4 welded vertex ids per quad, in order around it
        public List<Vector3> ringPos = new List<Vector3>();       // matching object-space positions
        public Dictionary<long, List<int>> edgeQuads = new Dictionary<long, List<int>>();

        public static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        public static QuadTopology Build(int[] idx, Vector3[] pos)
        {
            var topo = new QuadTopology();
            int triCount = idx.Length / 3;
            topo.quadOfTri = new int[triCount];
            for (int i = 0; i < triCount; i++) topo.quadOfTri[i] = -1;

            // Weld vertices by position so UV seams don't split the surface.
            var weld = new Dictionary<Vector3Int, int>();
            int W(int vi)
            {
                var p = pos[vi];
                var k = new Vector3Int(Mathf.RoundToInt(p.x * 100000f), Mathf.RoundToInt(p.y * 100000f), Mathf.RoundToInt(p.z * 100000f));
                if (!weld.TryGetValue(k, out int id)) { id = weld.Count; weld[k] = id; }
                return id;
            }
            var w = new int[idx.Length];
            for (int i = 0; i < idx.Length; i++) w[i] = W(idx[i]);

            var triEdges = new Dictionary<long, List<int>>();
            for (int t = 0; t < triCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    long key = EdgeKey(w[t * 3 + k], w[t * 3 + (k + 1) % 3]);
                    if (!triEdges.TryGetValue(key, out var l)) { l = new List<int>(2); triEdges[key] = l; }
                    l.Add(t);
                }

            // For each triangle pick the neighbour that forms the most square-looking, convex quad.
            var best = new int[triCount];
            var bestScore = new float[triCount];
            var bestEdge = new int[triCount];
            for (int t = 0; t < triCount; t++)
            {
                best[t] = -1;
                bestScore[t] = float.MaxValue;
                for (int k = 0; k < 3; k++)
                {
                    int a0 = w[t * 3 + k], a1 = w[t * 3 + (k + 1) % 3];
                    if (!triEdges.TryGetValue(EdgeKey(a0, a1), out var l) || l.Count != 2) continue;
                    int u = l[0] == t ? l[1] : l[0];
                    if (u == t) continue;
                    if (!QuadRing(idx, w, pos, t, k, u, out _, out _, out float score)) continue;
                    if (score < bestScore[t]) { bestScore[t] = score; best[t] = u; bestEdge[t] = k; }
                }
            }

            for (int t = 0; t < triCount; t++)
            {
                int u = best[t];
                if (u < 0 || u < t || best[u] != t || topo.quadOfTri[t] >= 0 || topo.quadOfTri[u] >= 0) continue;
                if (!QuadRing(idx, w, pos, t, bestEdge[t], u, out var rw, out var rp, out _)) continue;
                int q = topo.quadTriA.Count;
                topo.quadTriA.Add(t);
                topo.quadTriB.Add(u);
                topo.quadOfTri[t] = q;
                topo.quadOfTri[u] = q;
                for (int k = 0; k < 4; k++)
                {
                    topo.ring.Add(rw[k]);
                    topo.ringPos.Add(rp[k]);
                }
                for (int k = 0; k < 4; k++)
                {
                    long key = EdgeKey(rw[k], rw[(k + 1) % 4]);
                    if (!topo.edgeQuads.TryGetValue(key, out var l)) { l = new List<int>(2); topo.edgeQuads[key] = l; }
                    l.Add(q);
                }
            }
            return topo;
        }

        /// <summary>The quad made of triangle t and u across t's edge k: its 4 corners in order, and how far from square it is.</summary>
        static bool QuadRing(int[] idx, int[] w, Vector3[] pos, int t, int k, int u, out int[] rw, out Vector3[] rp, out float score)
        {
            rw = null; rp = null; score = 0f;
            int pV = idx[t * 3 + k], qV = idx[t * 3 + (k + 1) % 3], rV = idx[t * 3 + (k + 2) % 3];
            int pW = w[t * 3 + k], qW = w[t * 3 + (k + 1) % 3], rW = w[t * 3 + (k + 2) % 3];
            int sV = -1, sW = -1;
            for (int i = 0; i < 3; i++)
            {
                int ww = w[u * 3 + i];
                if (ww != pW && ww != qW) { sV = idx[u * 3 + i]; sW = ww; }
            }
            if (sV < 0 || sW == rW) return false;
            var P = pos[pV]; var Q = pos[qV]; var R = pos[rV]; var S = pos[sV];
            // Convex: the two diagonals must separate the other two corners.
            var d1 = Q - P;
            if (Vector3.Dot(Vector3.Cross(d1, R - P), Vector3.Cross(d1, S - P)) >= 0f) return false;
            var d2 = S - R;
            if (Vector3.Dot(Vector3.Cross(d2, P - R), Vector3.Cross(d2, Q - R)) >= 0f) return false;
            rw = new[] { pW, rW, qW, sW };
            rp = new[] { P, R, Q, S };
            for (int i = 0; i < 4; i++)
            {
                var a = rp[(i + 3) % 4] - rp[i];
                var b = rp[(i + 1) % 4] - rp[i];
                if (a.sqrMagnitude < 1e-14f || b.sqrMagnitude < 1e-14f) return false;
                score += Mathf.Abs(Vector3.Angle(a, b) - 90f);
            }
            return true;
        }
    }

    /// <summary>UV selection: picking islands or polygons that painting is then limited to.</summary>
    public partial class APSession
    {
        public event Action SelectionChanged;

        void SelectionEdited(TextureSet set)
        {
            set.selectionDirty = true;
            set.dirty = true;
            SelectionChanged?.Invoke();
            StructureChanged?.Invoke();
            PixelsChanged?.Invoke(set);
        }

        /// <summary>Adds, removes or replaces the selection with the given triangles (expanded to whole islands if asked).</summary>
        public void Select(TextureSet set, IList<TriRef> tris, SelectOp op, bool islands)
        {
            if (set == null) return;
            if (op == SelectOp.Replace) set.selection.Clear();
            var expanded = islands ? ExpandIslands(set, tris) : tris;
            foreach (var t in expanded)
            {
                int key = TextureSet.SelKey(t.entry, t.submesh);
                if (!set.selection.TryGetValue(key, out var hs))
                {
                    hs = new HashSet<int>();
                    set.selection[key] = hs;
                }
                if (op == SelectOp.Subtract) hs.Remove(t.first);
                else hs.Add(t.first);
            }
            SelectionEdited(set);
        }

        /// <summary>
        /// Blender-style polygon loop (Alt+click): from the clicked polygon, runs along the polygon edge nearest the
        /// click and keeps going through opposite edges, stopping at a boundary, a triangle, a fork or when it
        /// closes into a ring. Works on quads (two triangles sharing a diagonal). Whole polygons are used, not islands.
        /// </summary>
        public void SelectFaceLoop(TextureSet set, TriRef tri, Vector3 bary, SelectOp op)
        {
            if (set == null || tri.entry < 0 || tri.entry >= meshes.entries.Count) return;
            var e = meshes.entries[tri.entry];
            if (tri.submesh >= e.SubmeshCount) return;
            if (!e.quadTopo.TryGetValue(tri.submesh, out var topo))
            {
                topo = QuadTopology.Build(e.submeshIndices[tri.submesh], e.ObjectPositions);
                e.quadTopo[tri.submesh] = topo;
            }
            int t = tri.first / 3;
            int q = t >= 0 && t < topo.quadOfTri.Length ? topo.quadOfTri[t] : -1;
            if (q < 0)
            {
                Select(set, new[] { tri }, op, false);   // a lone triangle has no loop
                return;
            }

            // The edge of the quad nearest the click: the loop runs along it (click near a top/bottom edge for a horizontal loop).
            var idx = e.submeshIndices[tri.submesh];
            var pos = e.ObjectPositions;
            var click = pos[idx[tri.first]] * bary.x + pos[idx[tri.first + 1]] * bary.y + pos[idx[tri.first + 2]] * bary.z;
            int nearest = 0;
            float nd = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float d = DistToSegment(click, topo.ringPos[q * 4 + k], topo.ringPos[q * 4 + (k + 1) % 4]);
                if (d < nd) { nd = d; nearest = k; }
            }

            var quads = new List<int> { q };
            var seen = new HashSet<int> { q };
            for (int dir = 0; dir < 2; dir++)
            {
                int cur = q;
                int exit = dir == 0 ? (nearest + 1) % 4 : (nearest + 3) % 4;   // leave through the sides next to the nearest edge, so the loop runs along it
                while (true)
                {
                    int a = topo.ring[cur * 4 + exit];
                    int b = topo.ring[cur * 4 + (exit + 1) % 4];
                    long key = QuadTopology.EdgeKey(a, b);
                    if (!topo.edgeQuads.TryGetValue(key, out var pair) || pair.Count != 2) break;
                    int next = pair[0] == cur ? pair[1] : pair[0];
                    if (next == cur || seen.Contains(next)) break;
                    int entryEdge = -1;
                    for (int k = 0; k < 4; k++)
                        if (QuadTopology.EdgeKey(topo.ring[next * 4 + k], topo.ring[next * 4 + (k + 1) % 4]) == key) { entryEdge = k; break; }
                    if (entryEdge < 0) break;
                    seen.Add(next);
                    quads.Add(next);
                    cur = next;
                    exit = (entryEdge + 2) % 4;
                }
            }

            var tris = new List<TriRef>(quads.Count * 2);
            foreach (int qi in quads)
            {
                tris.Add(new TriRef(tri.entry, tri.submesh, topo.quadTriA[qi] * 3));
                tris.Add(new TriRef(tri.entry, tri.submesh, topo.quadTriB[qi] * 3));
            }
            Select(set, tris, op, false);
        }

        static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float len = ab.sqrMagnitude;
            float t = len > 1e-14f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        List<TriRef> ExpandIslands(TextureSet set, IList<TriRef> tris)
        {
            var result = new List<TriRef>();
            var done = new HashSet<long>();
            foreach (var t in tris)
            {
                if (t.entry < 0 || t.entry >= meshes.entries.Count) continue;
                var e = meshes.entries[t.entry];
                if (t.submesh >= e.SubmeshCount) continue;
                var ids = meshes.GetIslands(e, t.submesh, set.uvChannel, MeshCache.IslandMode.UV);
                int id = ids[t.first / 3];
                long key = ((long)TextureSet.SelKey(t.entry, t.submesh) << 32) | (uint)id;
                if (!done.Add(key)) continue;
                for (int i = 0; i < ids.Length; i++)
                    if (ids[i] == id) result.Add(new TriRef(t.entry, t.submesh, i * 3));
            }
            return result;
        }

        public void SelectAll(TextureSet set)
        {
            if (set == null) return;
            set.selection.Clear();
            foreach (var u in set.users)
            {
                var e = meshes.entries[u.renderer];
                if (u.submesh >= e.SubmeshCount) continue;
                var hs = new HashSet<int>();
                int count = e.submeshIndices[u.submesh].Length / 3;
                for (int i = 0; i < count; i++) hs.Add(i * 3);
                set.selection[TextureSet.SelKey(u.renderer, u.submesh)] = hs;
            }
            SelectionEdited(set);
        }

        public void ClearSelection(TextureSet set)
        {
            if (set == null || !set.HasSelection) return;
            set.selection.Clear();
            SelectionEdited(set);
        }

        public void InvertSelection(TextureSet set)
        {
            if (set == null) return;
            foreach (var u in set.users)
            {
                var e = meshes.entries[u.renderer];
                if (u.submesh >= e.SubmeshCount) continue;
                int key = TextureSet.SelKey(u.renderer, u.submesh);
                set.selection.TryGetValue(key, out var old);
                var hs = new HashSet<int>();
                int count = e.submeshIndices[u.submesh].Length / 3;
                for (int i = 0; i < count; i++)
                    if (old == null || !old.Contains(i * 3)) hs.Add(i * 3);
                set.selection[key] = hs;
            }
            SelectionEdited(set);
        }

        // ------------------------------------------------------------------ queries

        /// <summary>Triangles of the set whose UV centre lies inside the polygon (UV space).</summary>
        public List<TriRef> TrianglesInUV(TextureSet set, IList<Vector2> polygon)
        {
            var list = new List<TriRef>();
            if (set == null || polygon.Count < 3) return list;
            var min = polygon[0];
            var max = polygon[0];
            foreach (var p in polygon) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            foreach (var u in set.users)
            {
                var e = meshes.entries[u.renderer];
                if (u.submesh >= e.SubmeshCount) continue;
                var uvs = e.GetUVs(set.uvChannel);
                var idx = e.submeshIndices[u.submesh];
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    var c = (uvs[idx[i]] + uvs[idx[i + 1]] + uvs[idx[i + 2]]) / 3f;
                    c.x -= Mathf.Floor(c.x);
                    c.y -= Mathf.Floor(c.y);
                    if (c.x < min.x || c.y < min.y || c.x > max.x || c.y > max.y) continue;
                    if (InPolygon(c, polygon)) list.Add(new TriRef(u.renderer, u.submesh, i));
                }
            }
            return list;
        }

        /// <summary>Triangles of the set whose 3D centre projects inside a viewport polygon.</summary>
        public List<TriRef> TrianglesOnScreen(TextureSet set, IList<Vector2> polygon, Func<Vector3, Vector2?> project, Vector3 camPos, bool frontOnly)
        {
            var list = new List<TriRef>();
            if (set == null || polygon.Count < 3) return list;
            meshes.EnsurePosed();
            var min = polygon[0];
            var max = polygon[0];
            foreach (var p in polygon) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            foreach (var u in set.users)
            {
                var e = meshes.entries[u.renderer];
                if (!e.Visible || u.submesh >= e.SubmeshCount) continue;
                var v = e.worldVerts;
                var idx = e.submeshIndices[u.submesh];
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    var a = v[idx[i]];
                    var b = v[idx[i + 1]];
                    var c = v[idx[i + 2]];
                    var centre = (a + b + c) / 3f;
                    if (frontOnly)
                    {
                        var n = Vector3.Cross(b - a, c - a);
                        if (Vector3.Dot(n, camPos - centre) < 0f) continue;
                    }
                    var s = project(centre);
                    if (!s.HasValue) continue;
                    var p = s.Value;
                    if (p.x < min.x || p.y < min.y || p.x > max.x || p.y > max.y) continue;
                    if (InPolygon(p, polygon)) list.Add(new TriRef(u.renderer, u.submesh, i));
                }
            }
            return list;
        }

        static bool InPolygon(Vector2 p, IList<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y + 1e-12f) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }
    }
}
