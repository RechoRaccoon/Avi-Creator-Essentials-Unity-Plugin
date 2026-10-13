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
