using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>Result of a ray cast against the posed avatar.</summary>
    public struct MeshHit
    {
        public int entry;
        public int submesh;
        public int triangle;       // index of the first index in the submesh index list
        public int v0, v1, v2;
        public Vector3 barycentric;
        public Vector3 point;
        public Vector3 normal;     // interpolated, normalised
        public Vector3 faceNormal;
        public float distance;
    }

    /// <summary>A renderer of the preview clone plus the CPU/GPU mesh data used for painting and picking.</summary>
    public class RendererEntry
    {
        public int index;
        public Renderer renderer;
        public SkinnedMeshRenderer skinned;
        public Mesh sharedMesh;
        public Mesh paintMesh;            // posed vertices + original UVs/indices, rendered into UV space
        public Mesh bakeTemp;
        public Matrix4x4 matrix = Matrix4x4.identity;
        public Vector3[] worldVerts = Array.Empty<Vector3>();
        public Vector3[] worldNormals = Array.Empty<Vector3>();
        public int[][] submeshIndices = Array.Empty<int[]>();
        public Bounds worldBounds;
        public string path;
        public string displayName;
        readonly Vector2[][] m_UVs = new Vector2[4][];
        Vector3[] m_ObjectPositions;

        public bool Visible => renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
        public int SubmeshCount => submeshIndices.Length;

        public Vector2[] GetUVs(int channel)
        {
            channel = Mathf.Clamp(channel, 0, 3);
            if (m_UVs[channel] == null)
            {
                var list = new List<Vector2>();
                sharedMesh.GetUVs(channel, list);
                if (list.Count != sharedMesh.vertexCount)
                {
                    list.Clear();
                    for (int i = 0; i < sharedMesh.vertexCount; i++) list.Add(Vector2.zero);
                }
                m_UVs[channel] = list.ToArray();
            }
            return m_UVs[channel];
        }

        public bool HasUVs(int channel)
        {
            var list = new List<Vector2>();
            sharedMesh.GetUVs(Mathf.Clamp(channel, 0, 3), list);
            return list.Count == sharedMesh.vertexCount && list.Count > 0;
        }

        /// <summary>Object-space rest positions (used to find connected mesh pieces).</summary>
        public Vector3[] ObjectPositions => m_ObjectPositions ?? (m_ObjectPositions = sharedMesh.vertices);

        public Vector2 InterpolateUV(MeshHit hit, int channel)
        {
            var uv = GetUVs(channel);
            return uv[hit.v0] * hit.barycentric.x + uv[hit.v1] * hit.barycentric.y + uv[hit.v2] * hit.barycentric.z;
        }
    }

    /// <summary>
    /// Keeps posed (skinned + blendshape) copies of every mesh in the clone, a BVH for picking, and island data.
    /// </summary>
    public class MeshCache : IDisposable
    {
        public readonly List<RendererEntry> entries = new List<RendererEntry>();
        bool m_PoseDirty = true;
        bool m_BvhDirty = true;
        readonly Bvh m_Bvh = new Bvh();
        readonly Dictionary<long, int[]> m_IslandCache = new Dictionary<long, int[]>();

        public int Version { get; private set; }

        public void Build(GameObject root)
        {
            Dispose();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = null;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) mesh = smr.sharedMesh;
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                if (mesh == null || mesh.vertexCount == 0) continue;

                var e = new RendererEntry
                {
                    index = entries.Count,
                    renderer = r,
                    skinned = smr,
                    sharedMesh = mesh,
                    path = APUtil.GetPath(r.transform, root.transform),
                    displayName = r.name,
                };
                e.paintMesh = CreatePaintMesh(mesh);
                if (smr != null) e.bakeTemp = new Mesh { name = "AP Bake", hideFlags = HideFlags.HideAndDontSave };
                var subs = new int[mesh.subMeshCount][];
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    subs[s] = mesh.GetTopology(s) == MeshTopology.Triangles ? mesh.GetIndices(s, true) : Array.Empty<int>();
                }
                e.submeshIndices = subs;
                entries.Add(e);
            }
            m_PoseDirty = true;
            m_BvhDirty = true;
        }

        static Mesh CreatePaintMesh(Mesh src)
        {
            var m = new Mesh { name = "AP " + src.name, hideFlags = HideFlags.HideAndDontSave };
            m.indexFormat = src.vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.vertices = src.vertices;
            var normals = src.normals;
            if (normals == null || normals.Length != src.vertexCount)
            {
                m.RecalculateNormals();
            }
            else m.normals = normals;
            var uv = new List<Vector4>();
            for (int ch = 0; ch < 4; ch++)
            {
                src.GetUVs(ch, uv);
                if (uv.Count == src.vertexCount) m.SetUVs(ch, uv);
            }
            m.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                if (src.GetTopology(s) == MeshTopology.Triangles)
                    m.SetIndices(src.GetIndices(s, true), MeshTopology.Triangles, s, false, 0);
                else
                    m.SetIndices(Array.Empty<int>(), MeshTopology.Triangles, s, false, 0);
            }
            m.RecalculateBounds();
            m.MarkDynamic();
            return m;
        }

        /// <summary>Call after bones move or blendshapes change.</summary>
        public void MarkPoseDirty()
        {
            m_PoseDirty = true;
            m_BvhDirty = true;
        }

        public void MarkVisibilityDirty() => m_BvhDirty = true;

        public void EnsurePosed()
        {
            if (!m_PoseDirty) return;
            m_PoseDirty = false;
            Version++;
            foreach (var e in entries)
            {
                if (e.renderer == null) continue;
                Vector3[] local;
                Vector3[] localN;
                if (e.skinned != null)
                {
                    e.skinned.BakeMesh(e.bakeTemp, true);
                    local = e.bakeTemp.vertices;
                    localN = e.bakeTemp.normals;
                    var t = e.skinned.transform;
                    e.matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                    if (local.Length == e.paintMesh.vertexCount)
                    {
                        e.paintMesh.vertices = local;
                        if (localN != null && localN.Length == local.Length) e.paintMesh.normals = localN;
                        e.paintMesh.RecalculateBounds();
                    }
                }
                else
                {
                    local = e.paintMesh.vertices;
                    localN = e.paintMesh.normals;
                    e.matrix = e.renderer.transform.localToWorldMatrix;
                }

                int n = local.Length;
                if (e.worldVerts.Length != n) e.worldVerts = new Vector3[n];
                if (e.worldNormals.Length != n) e.worldNormals = new Vector3[n];
                var m = e.matrix;
                var nm = m.inverse.transpose;
                bool hasN = localN != null && localN.Length == n;
                Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = -min;
                for (int i = 0; i < n; i++)
                {
                    var w = m.MultiplyPoint3x4(local[i]);
                    e.worldVerts[i] = w;
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                    e.worldNormals[i] = hasN ? nm.MultiplyVector(localN[i]).normalized : Vector3.up;
                }
                e.worldBounds = new Bounds();
                e.worldBounds.SetMinMax(min, max);
            }
        }

        public void EnsureBvh()
        {
            EnsurePosed();
            if (!m_BvhDirty) return;
            m_BvhDirty = false;
            m_Bvh.Build(entries);
        }

        public bool Raycast(Ray ray, out MeshHit hit, float maxDistance = float.MaxValue)
        {
            EnsureBvh();
            return m_Bvh.Raycast(ray, entries, out hit, maxDistance);
        }

        public Bounds VisibleBounds()
        {
            EnsurePosed();
            bool any = false;
            var b = new Bounds();
            foreach (var e in entries)
            {
                if (!e.Visible) continue;
                if (!any) { b = e.worldBounds; any = true; }
                else b.Encapsulate(e.worldBounds);
            }
            if (!any) b = new Bounds(Vector3.up, Vector3.one);
            return b;
        }

        // ------------------------------------------------------------------ islands

        public enum IslandMode
        {
            UV,     // connected in texture space
            Mesh,   // connected on the 3D surface
        }

        /// <summary>Component id per triangle of a submesh.</summary>
        public int[] GetIslands(RendererEntry e, int submesh, int uvChannel, IslandMode mode)
        {
            long key = ((long)e.index << 32) | ((long)submesh << 8) | ((long)uvChannel << 2) | (long)mode;
            if (m_IslandCache.TryGetValue(key, out var cached)) return cached;
            var idx = e.submeshIndices[submesh];
            int triCount = idx.Length / 3;
            var uf = new UnionFind(triCount);
            var keyToTri = new Dictionary<long, int>();
            Vector2[] uvs = mode == IslandMode.UV ? e.GetUVs(uvChannel) : null;
            Vector3[] pos = mode == IslandMode.Mesh ? e.ObjectPositions : null;
            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int vi = idx[t * 3 + k];
                    long vkey;
                    if (mode == IslandMode.UV)
                    {
                        var uv = uvs[vi];
                        long qx = (long)Mathf.Round(uv.x * 262144f);
                        long qy = (long)Mathf.Round(uv.y * 262144f);
                        vkey = (qx * 73856093L) ^ (qy * 19349663L);
                    }
                    else
                    {
                        var p = pos[vi];
                        long qx = (long)Mathf.Round(p.x * 100000f);
                        long qy = (long)Mathf.Round(p.y * 100000f);
                        long qz = (long)Mathf.Round(p.z * 100000f);
                        vkey = (qx * 73856093L) ^ (qy * 19349663L) ^ (qz * 83492791L);
                    }
                    if (keyToTri.TryGetValue(vkey, out int other)) uf.Union(t, other);
                    else keyToTri[vkey] = t;
                }
            }
            var result = new int[triCount];
            for (int t = 0; t < triCount; t++) result[t] = uf.Find(t);
            m_IslandCache[key] = result;
            return result;
        }

        public void Dispose()
        {
            foreach (var e in entries)
            {
                if (e.paintMesh != null) Object.DestroyImmediate(e.paintMesh);
                if (e.bakeTemp != null) Object.DestroyImmediate(e.bakeTemp);
            }
            entries.Clear();
            m_IslandCache.Clear();
            m_Bvh.Clear();
        }

        class UnionFind
        {
            readonly int[] m_Parent;

            public UnionFind(int n)
            {
                m_Parent = new int[n];
                for (int i = 0; i < n; i++) m_Parent[i] = i;
            }

            public int Find(int x)
            {
                while (m_Parent[x] != x)
                {
                    m_Parent[x] = m_Parent[m_Parent[x]];
                    x = m_Parent[x];
                }
                return x;
            }

            public void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a != b) m_Parent[a] = b;
            }
        }
    }

    /// <summary>Bounding volume hierarchy over all visible triangles, for fast and exact mouse picking.</summary>
    public class Bvh
    {
        struct Tri
        {
            public int entry, submesh, first;
            public Vector3 a, b, c;
            public Vector3 centroid;
        }

        struct Node
        {
            public Vector3 min, max;
            public int left;    // left child index, or first triangle when leaf
            public int right;   // right child index
            public int count;   // > 0 for leaves
        }

        Tri[] m_Tris = Array.Empty<Tri>();
        Node[] m_Nodes = Array.Empty<Node>();
        int m_NodeCount;
        const int LeafSize = 4;

        public void Clear()
        {
            m_Tris = Array.Empty<Tri>();
            m_Nodes = Array.Empty<Node>();
            m_NodeCount = 0;
        }

        public void Build(List<RendererEntry> entries)
        {
            var tris = new List<Tri>();
            foreach (var e in entries)
            {
                if (!e.Visible) continue;
                var v = e.worldVerts;
                for (int s = 0; s < e.submeshIndices.Length; s++)
                {
                    var idx = e.submeshIndices[s];
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
                        if (i0 >= v.Length || i1 >= v.Length || i2 >= v.Length) continue;
                        var t = new Tri { entry = e.index, submesh = s, first = i, a = v[i0], b = v[i1], c = v[i2] };
                        t.centroid = (t.a + t.b + t.c) / 3f;
                        tris.Add(t);
                    }
                }
            }
            m_Tris = tris.ToArray();
            m_Nodes = new Node[Mathf.Max(1, m_Tris.Length * 2)];
            m_NodeCount = 0;
            if (m_Tris.Length == 0) return;
            BuildNode(0, m_Tris.Length);
        }

        int BuildNode(int start, int count)
        {
            int ni = m_NodeCount++;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = -min;
            Vector3 cmin = min, cmax = max;
            for (int i = start; i < start + count; i++)
            {
                var t = m_Tris[i];
                min = Vector3.Min(min, Vector3.Min(t.a, Vector3.Min(t.b, t.c)));
                max = Vector3.Max(max, Vector3.Max(t.a, Vector3.Max(t.b, t.c)));
                cmin = Vector3.Min(cmin, t.centroid);
                cmax = Vector3.Max(cmax, t.centroid);
            }
            m_Nodes[ni].min = min;
            m_Nodes[ni].max = max;

            if (count <= LeafSize)
            {
                m_Nodes[ni].left = start;
                m_Nodes[ni].count = count;
                return ni;
            }

            var ext = cmax - cmin;
            int axis = ext.x > ext.y ? (ext.x > ext.z ? 0 : 2) : (ext.y > ext.z ? 1 : 2);
            float split = (cmin[axis] + cmax[axis]) * 0.5f;
            int mid = Partition(start, count, axis, split);
            if (mid == start || mid == start + count)
            {
                // Degenerate split: fall back to an even split by sorting.
                Array.Sort(m_Tris, start, count, new AxisComparer(axis));
                mid = start + count / 2;
            }

            int left = BuildNode(start, mid - start);
            int right = BuildNode(mid, start + count - mid);
            m_Nodes[ni].left = left;
            m_Nodes[ni].right = right;
            m_Nodes[ni].count = 0;
            return ni;
        }

        int Partition(int start, int count, int axis, float split)
        {
            int i = start, j = start + count - 1;
            while (i <= j)
            {
                if (m_Tris[i].centroid[axis] < split) i++;
                else
                {
                    var tmp = m_Tris[i];
                    m_Tris[i] = m_Tris[j];
                    m_Tris[j] = tmp;
                    j--;
                }
            }
            return i;
        }

        class AxisComparer : IComparer<Tri>
        {
            readonly int m_Axis;
            public AxisComparer(int axis) { m_Axis = axis; }
            public int Compare(Tri x, Tri y) => x.centroid[m_Axis].CompareTo(y.centroid[m_Axis]);
        }

        static bool RayBox(Vector3 o, Vector3 invD, Vector3 min, Vector3 max, float tMax, out float tNear)
        {
            float t1 = (min.x - o.x) * invD.x, t2 = (max.x - o.x) * invD.x;
            float tmin = Mathf.Min(t1, t2), tmax = Mathf.Max(t1, t2);
            t1 = (min.y - o.y) * invD.y; t2 = (max.y - o.y) * invD.y;
            tmin = Mathf.Max(tmin, Mathf.Min(t1, t2)); tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));
            t1 = (min.z - o.z) * invD.z; t2 = (max.z - o.z) * invD.z;
            tmin = Mathf.Max(tmin, Mathf.Min(t1, t2)); tmax = Mathf.Min(tmax, Mathf.Max(t1, t2));
            tNear = tmin;
            return tmax >= Mathf.Max(tmin, 0f) && tmin < tMax;
        }

        static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t, out float u, out float v)
        {
            t = u = v = 0;
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            var s = o - a;
            u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 1e-6f;
        }

        public bool Raycast(Ray ray, List<RendererEntry> entries, out MeshHit hit, float maxDistance)
        {
            hit = default;
            if (m_NodeCount == 0) return false;
            var o = ray.origin;
            var d = ray.direction;
            var inv = new Vector3(1f / (Mathf.Abs(d.x) > 1e-12f ? d.x : 1e-12f),
                1f / (Mathf.Abs(d.y) > 1e-12f ? d.y : 1e-12f),
                1f / (Mathf.Abs(d.z) > 1e-12f ? d.z : 1e-12f));
            float best = maxDistance;
            int bestTri = -1;
            float bu = 0, bv = 0;
            var stack = new Stack<int>(64);
            stack.Push(0);
            while (stack.Count > 0)
            {
                int ni = stack.Pop();
                var n = m_Nodes[ni];
                if (!RayBox(o, inv, n.min, n.max, best, out _)) continue;
                if (n.count > 0)
                {
                    for (int i = n.left; i < n.left + n.count; i++)
                    {
                        var t = m_Tris[i];
                        if (RayTri(o, d, t.a, t.b, t.c, out float dist, out float u, out float v) && dist < best)
                        {
                            best = dist;
                            bestTri = i;
                            bu = u;
                            bv = v;
                        }
                    }
                }
                else
                {
                    stack.Push(n.right);
                    stack.Push(n.left);
                }
            }
            if (bestTri < 0) return false;

            var tri = m_Tris[bestTri];
            var e = entries[tri.entry];
            var idx = e.submeshIndices[tri.submesh];
            hit.entry = tri.entry;
            hit.submesh = tri.submesh;
            hit.triangle = tri.first;
            hit.v0 = idx[tri.first];
            hit.v1 = idx[tri.first + 1];
            hit.v2 = idx[tri.first + 2];
            hit.barycentric = new Vector3(1f - bu - bv, bu, bv);
            hit.distance = best;
            hit.point = o + d * best;
            hit.faceNormal = Vector3.Cross(tri.b - tri.a, tri.c - tri.a).normalized;
            var nrm = e.worldNormals[hit.v0] * hit.barycentric.x + e.worldNormals[hit.v1] * hit.barycentric.y +
                      e.worldNormals[hit.v2] * hit.barycentric.z;
            hit.normal = nrm.sqrMagnitude > 1e-10f ? nrm.normalized : hit.faceNormal;
            return true;
        }
    }
}
