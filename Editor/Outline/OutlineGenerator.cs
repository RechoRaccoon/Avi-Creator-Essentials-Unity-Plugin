using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Builds "inverted hull" outlines: a copy of the mesh pushed outwards along its smoothed normals with the
    /// triangle winding flipped, so only its far side shows around the silhouette. Because the winding is flipped in
    /// the mesh itself, the outline material can use any shader (it never needs a "cull front" setting), which is
    /// what makes this work for Quest shaders as well as PC ones.
    /// </summary>
    public static class OutlineGenerator
    {
        public const string Suffix = "_Outline";
        public const string ParentName = "Outlines";
        public const string DataFolder = "Assets/Avi Creator Essentials Data/Outlines";

        public enum Placement { SeparateObject, MergeIntoMesh }
        public enum ShaderChoice { MobileToonLit, MobileStandardLite, UnlitColor, Custom, ExistingMaterial }

        /// <summary>One material slot (submesh) of a renderer, and whether it gets an outline.</summary>
        public class SlotInfo
        {
            public int index;
            public Material material;
            public int triangles;
            public bool include;
            public string note;
        }

        /// <summary>One renderer on the avatar, with its slots and per-renderer overrides.</summary>
        public class RendererInfo
        {
            public Renderer renderer;
            public Mesh mesh;
            public bool include;
            public bool overrideColor;
            public Color color = Color.black;
            public float widthMultiplier = 1f;
            public bool expanded;
            public readonly List<SlotInfo> slots = new List<SlotInfo>();
            public int Triangles { get { int n = 0; foreach (var s in slots) if (s.include) n += s.triangles; return n; } }
            public bool IsSkinned { get { return renderer is SkinnedMeshRenderer; } }
        }

        public class Settings
        {
            public float widthCm = 0.15f;
            public Color color = new Color(0.05f, 0.05f, 0.05f, 1f);
            public Placement placement = Placement.SeparateObject;
            public ShaderChoice shader = ShaderChoice.MobileToonLit;
            public Shader customShader;
            public Material existingMaterial;
            public bool flatLook = true;
            public bool smoothNormals = true;
            public bool useVertexColorWidth;
            public bool copyBlendShapes = true;
            public float blendShapeMinMoveMm = 0.5f;
            public List<Transform> excludedBones = new List<Transform>();
            public bool excludedBonesIncludeChildren = true;
            public bool replaceExisting = true;

            // optimization
            public bool weldVertices = true;
            public float simplifyMm = 0f;       // vertex-cluster cell size, 0 = off
            public int triangleBudget = 0;      // 0 = no limit; simplifies until the outline fits
        }

        public class Result
        {
            public int renderers;
            public int triangles;
            public int vertices;
            public long bytes;
            public int blendShapes;
            public readonly List<string> warnings = new List<string>();
        }

        static readonly string[] s_ExcludeWords =
        {
            "eye", "lash", "brow", "tongue", "teeth", "tooth", "mouth", "tear", "blush", "shadow", "hidden", "invisible", "mask", "glass", "lens",
        };

        // ------------------------------------------------------------------ scanning

        public static List<RendererInfo> Scan(GameObject root, bool autoExclude)
        {
            var list = new List<RendererInfo>();
            if (root == null) return list;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name.EndsWith(Suffix, StringComparison.Ordinal)) continue;
                Mesh mesh = null;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) mesh = smr.sharedMesh;
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mesh = mf.sharedMesh;
                }
                if (mesh == null) continue;

                var info = new RendererInfo { renderer = r, mesh = mesh };
                var mats = r.sharedMaterials;
                bool any = false;
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    var desc = mesh.GetSubMesh(i);
                    var slot = new SlotInfo
                    {
                        index = i,
                        material = i < mats.Length ? mats[i] : null,
                        triangles = desc.topology == MeshTopology.Triangles ? (int)(desc.indexCount / 3) : 0,
                    };
                    slot.include = slot.triangles > 0;
                    if (slot.triangles == 0) slot.note = "not triangles";
                    else if (slot.material == null) { slot.include = false; slot.note = "no material"; }
                    else if (autoExclude)
                    {
                        string why = AutoExcludeReason(r, slot.material);
                        if (why != null) { slot.include = false; slot.note = why; }
                    }
                    any |= slot.include;
                    info.slots.Add(slot);
                }
                info.include = any;
                list.Add(info);
            }
            return list;
        }

        static string AutoExcludeReason(Renderer r, Material m)
        {
            string n = (r.name + " " + m.name).ToLowerInvariant();
            if (n.Contains("outline")) return "already an outline";
            if (m.renderQueue >= 2450) return "cutout / transparent";
            string rt = m.GetTag("RenderType", true, "");
            if (rt == "Transparent" || rt == "TransparentCutout") return "cutout / transparent";
            foreach (var w in s_ExcludeWords)
                if (n.Contains(w)) return "name contains \"" + w + "\"";
            return null;
        }

        // ------------------------------------------------------------------ generation

        public static Result Generate(GameObject root, List<RendererInfo> infos, Settings s)
        {
            var result = new Result();
            if (root == null) return result;
            if (s.replaceExisting)
            {
                RemoveAll(root);
                foreach (var inf in infos) if (inf.renderer != null) inf.mesh = MeshOf(inf.renderer);
            }

            string folder = DataFolder + "/" + Sanitize(root.name);
            EnsureFolder(folder);

            var matCache = new Dictionary<string, Material>();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Add outline");
            int group = Undo.GetCurrentGroup();

            int done = 0;
            Transform outlinesParent = null;
            foreach (var info in infos)
            {
                done++;
                if (!info.include || info.renderer == null || info.mesh == null) continue;
                var included = new List<SlotInfo>();
                foreach (var sl in info.slots) if (sl.include) included.Add(sl);
                if (included.Count == 0) continue;

                EditorUtility.DisplayProgressBar("Outline", info.renderer.name, done / (float)infos.Count);
                Color col = info.overrideColor ? info.color : s.color;
                var mat = GetMaterial(folder, col, s, matCache);

                Mesh hull;
                try { hull = BuildHull(info, included, s, result); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    result.warnings.Add(info.renderer.name + ": " + e.Message);
                    continue;
                }
                if (hull == null) { result.warnings.Add(info.renderer.name + ": nothing left to outline"); continue; }

                string baseName = Sanitize(info.renderer.name);
                if (s.placement == Placement.SeparateObject)
                {
                    string path = folder + "/" + baseName + Suffix + ".asset";
                    hull.name = baseName + Suffix;
                    var saved = SaveMesh(hull, path);
                    if (outlinesParent == null) outlinesParent = GetOrCreateOutlinesParent(root);
                    PlaceSeparate(info, saved, mat, outlinesParent);
                    result.bytes += FileSize(path);
                }
                else
                {
                    var merged = Merge(info, hull, mat, folder, baseName, result);
                    if (merged == null) continue;
                    Object.DestroyImmediate(hull);
                }
                result.renderers++;
            }
            EditorUtility.ClearProgressBar();
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>Removes every outline object this tool made under the avatar, and restores merged meshes.</summary>
        public static int RemoveAll(GameObject root)
        {
            if (root == null) return 0;
            int removed = 0;
            var toDelete = new List<GameObject>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root.transform && t.name.EndsWith(Suffix, StringComparison.Ordinal)) toDelete.Add(t.gameObject);
            foreach (var go in toDelete)
            {
                if (go == null) continue;
                Undo.DestroyObjectImmediate(go);
                removed++;
            }
            // Take the "Outlines" parent away too, once it holds nothing but this tool's outlines.
            // It is only deleted when empty, so anything the user put in it survives.
            var outlinesParent = root.transform.Find(ParentName);
            if (outlinesParent != null && outlinesParent.childCount == 0)
                Undo.DestroyObjectImmediate(outlinesParent.gameObject);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = MeshOf(r);
                if (mesh == null) continue;
                string path = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset", StringComparison.Ordinal)) continue;
                string side = path.Substring(0, path.Length - ".asset".Length) + ".origin.json";
                if (!File.Exists(side)) continue;
                var origin = JsonUtility.FromJson<Origin>(File.ReadAllText(side));
                var orig = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(origin.meshGuid));
                if (orig == null) continue;
                Undo.RecordObject(r, "Remove outline");
                SetMesh(r, orig);
                var mats = r.sharedMaterials;
                if (origin.materialCount > 0 && origin.materialCount < mats.Length)
                {
                    var trimmed = new Material[origin.materialCount];
                    Array.Copy(mats, trimmed, trimmed.Length);
                    r.sharedMaterials = trimmed;
                }
                removed++;
            }
            return removed;
        }

        [Serializable]
        public class Origin { public string meshGuid; public int materialCount; }

        static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        static void SetMesh(Renderer r, Mesh m)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) { smr.sharedMesh = m; return; }
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null) { Undo.RecordObject(mf, "Outline mesh"); mf.sharedMesh = m; }
        }

        // ------------------------------------------------------------------ hull building

        /// <summary>Intermediate outline geometry before optimization and writing.</summary>
        class Hull
        {
            public List<Vector3> pos = new List<Vector3>();
            public List<Vector3> nrm = new List<Vector3>();
            public List<BoneWeight> bw = new List<BoneWeight>();
            public List<int> tris = new List<int>();
            public List<Shape> shapes = new List<Shape>();
        }

        class Shape
        {
            public string name;
            public List<float> weights = new List<float>();
            public List<Vector3[]> deltas = new List<Vector3[]>(); // per frame, per hull vertex
        }

        static Mesh BuildHull(RendererInfo info, List<SlotInfo> included, Settings s, Result result)
        {
            var mesh = info.mesh;
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var colors = mesh.colors32;
            var smr = info.renderer as SkinnedMeshRenderer;
            BoneWeight[] weights = smr != null ? mesh.boneWeights : null;
            bool skinned = smr != null && weights != null && weights.Length == verts.Length && mesh.bindposes.Length > 0;

            // Triangles of the included slots, in original vertex indices.
            var srcTris = new List<int>();
            foreach (var sl in included)
            {
                var idx = mesh.GetTriangles(sl.index);
                srcTris.AddRange(idx);
            }
            if (srcTris.Count == 0) return null;

            // World-space size of one mesh-space unit at every vertex, so the width is a real length.
            var scale = VertexScale(info.renderer, mesh, weights, skinned, verts.Length);

            // Fade-out near excluded bones (0 = excluded, 1 = fully outlined).
            var boneFactor = BoneFactor(smr, mesh, weights, skinned, s, verts.Length);

            // Smoothed normals, computed over the included triangles only.
            Vector3[] smooth = s.smoothNormals || normals.Length != verts.Length
                ? SmoothNormals(verts, srcTris)
                : normals;

            float width = s.widthCm * 0.01f * Mathf.Max(0.01f, info.widthMultiplier);

            // Which vertices are used, and how wide each is.
            var map = new int[verts.Length];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            var hull = new Hull();
            var srcOfHull = new List<int>();
            var alive = new bool[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                float f = boneFactor != null ? boneFactor[i] : 1f;
                if (s.useVertexColorWidth && colors != null && colors.Length == verts.Length) f *= colors[i].r / 255f;
                alive[i] = f > 0.02f;
                if (!alive[i]) continue;
                verts[i] = verts[i] + smooth[i].normalized * (width * f / Mathf.Max(1e-5f, scale[i]));
            }

            for (int t = 0; t + 2 < srcTris.Count; t += 3)
            {
                int a = srcTris[t], b = srcTris[t + 1], c = srcTris[t + 2];
                if (!alive[a] && !alive[b] && !alive[c]) continue;
                int na = Use(a, map, hull, srcOfHull, verts, smooth, weights, skinned);
                int nb = Use(b, map, hull, srcOfHull, verts, smooth, weights, skinned);
                int nc = Use(c, map, hull, srcOfHull, verts, smooth, weights, skinned);
                // Flipped winding: this is what turns the copy into an outline.
                hull.tris.Add(na); hull.tris.Add(nc); hull.tris.Add(nb);
            }
            if (hull.tris.Count == 0) return null;

            if (s.copyBlendShapes && mesh.blendShapeCount > 0) CopyShapes(mesh, srcOfHull, scale, s, hull, result);

            if (s.weldVertices) Weld(hull);
            float cell = s.simplifyMm * 0.001f;
            if (cell > 0f) Cluster(hull, cell);
            if (s.triangleBudget > 0)
            {
                int guard = 0;
                if (cell <= 0f) cell = 0.0005f;
                while (hull.tris.Count / 3 > s.triangleBudget && guard++ < 14)
                {
                    cell *= 1.35f;
                    Cluster(hull, cell);
                }
                if (hull.tris.Count / 3 > s.triangleBudget)
                    result.warnings.Add(info.renderer.name + ": couldn't get under the triangle budget (" + (hull.tris.Count / 3) + " left)");
            }
            if (hull.tris.Count == 0) return null;
            result.triangles += hull.tris.Count / 3;
            result.vertices += hull.pos.Count;
            return ToMesh(hull, mesh, skinned);
        }

        static int Use(int v, int[] map, Hull h, List<int> srcOfHull, Vector3[] verts, Vector3[] smooth, BoneWeight[] bw, bool skinned)
        {
            if (map[v] >= 0) return map[v];
            map[v] = h.pos.Count;
            srcOfHull.Add(v);
            h.pos.Add(verts[v]);
            h.nrm.Add(-smooth[v].normalized);
            h.bw.Add(skinned ? bw[v] : default(BoneWeight));
            return map[v];
        }

        static float[] VertexScale(Renderer r, Mesh mesh, BoneWeight[] bw, bool skinned, int count)
        {
            var scale = new float[count];
            if (!skinned)
            {
                var ls = r.transform.lossyScale;
                float s = (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f;
                for (int i = 0; i < count; i++) scale[i] = s;
                return scale;
            }
            var smr = (SkinnedMeshRenderer)r;
            var bones = smr.bones;
            var bind = mesh.bindposes;
            var boneScale = new float[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || i >= bind.Length) { boneScale[i] = 1f; continue; }
                var ls = (bones[i].localToWorldMatrix * bind[i]).lossyScale;
                boneScale[i] = Mathf.Max(1e-5f, (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f);
            }
            for (int i = 0; i < count; i++)
            {
                var w = bw[i];
                float sum = 0f, acc = 0f;
                Accum(w.boneIndex0, w.weight0, boneScale, ref acc, ref sum);
                Accum(w.boneIndex1, w.weight1, boneScale, ref acc, ref sum);
                Accum(w.boneIndex2, w.weight2, boneScale, ref acc, ref sum);
                Accum(w.boneIndex3, w.weight3, boneScale, ref acc, ref sum);
                scale[i] = sum > 1e-5f ? acc / sum : 1f;
            }
            return scale;
        }

        static void Accum(int bone, float weight, float[] boneScale, ref float acc, ref float sum)
        {
            if (weight <= 0f || bone < 0 || bone >= boneScale.Length) return;
            acc += boneScale[bone] * weight;
            sum += weight;
        }

        static float[] BoneFactor(SkinnedMeshRenderer smr, Mesh mesh, BoneWeight[] bw, bool skinned, Settings s, int count)
        {
            if (!skinned || s.excludedBones == null || s.excludedBones.Count == 0) return null;
            var bones = smr.bones;
            var excluded = new bool[bones.Length];
            bool any = false;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                foreach (var ex in s.excludedBones)
                {
                    if (ex == null) continue;
                    if (bones[i] == ex || (s.excludedBonesIncludeChildren && bones[i].IsChildOf(ex))) { excluded[i] = true; any = true; break; }
                }
            }
            if (!any) return null;
            var f = new float[count];
            for (int i = 0; i < count; i++)
            {
                var w = bw[i];
                float ex = 0f, tot = 0f;
                Tally(w.boneIndex0, w.weight0, excluded, ref ex, ref tot);
                Tally(w.boneIndex1, w.weight1, excluded, ref ex, ref tot);
                Tally(w.boneIndex2, w.weight2, excluded, ref ex, ref tot);
                Tally(w.boneIndex3, w.weight3, excluded, ref ex, ref tot);
                f[i] = tot > 1e-5f ? 1f - ex / tot : 1f;
            }
            return f;
        }

        static void Tally(int bone, float weight, bool[] excluded, ref float ex, ref float tot)
        {
            if (weight <= 0f || bone < 0 || bone >= excluded.Length) return;
            tot += weight;
            if (excluded[bone]) ex += weight;
        }

        /// <summary>Angle-weighted vertex normals, averaged across vertices that share a position (UV seams, hard edges).</summary>
        static Vector3[] SmoothNormals(Vector3[] verts, List<int> tris)
        {
            var acc = new Vector3[verts.Length];
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                Vector3 pa = verts[a], pb = verts[b], pc = verts[c];
                Vector3 n = Vector3.Cross(pb - pa, pc - pa);
                if (n.sqrMagnitude < 1e-20f) continue;
                n.Normalize();
                acc[a] += n * Angle(pb - pa, pc - pa);
                acc[b] += n * Angle(pc - pb, pa - pb);
                acc[c] += n * Angle(pa - pc, pb - pc);
            }
            var groups = new Dictionary<long, Vector3>();
            for (int i = 0; i < verts.Length; i++)
            {
                long k = Key(verts[i]);
                Vector3 cur;
                groups.TryGetValue(k, out cur);
                groups[k] = cur + acc[i];
            }
            var result = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var n = groups[Key(verts[i])];
                result[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
            }
            return result;
        }

        static float Angle(Vector3 a, Vector3 b)
        {
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(a.normalized, b.normalized), -1f, 1f));
        }

        static long Key(Vector3 v)
        {
            const float q = 100000f; // 0.01 mm in metres
            long x = Mathf.RoundToInt(v.x * q) & 0x1FFFFF;
            long y = Mathf.RoundToInt(v.y * q) & 0x1FFFFF;
            long z = Mathf.RoundToInt(v.z * q) & 0x1FFFFF;
            return (x << 42) | (y << 21) | z;
        }

        static void CopyShapes(Mesh mesh, List<int> srcOfHull, float[] scale, Settings s, Hull hull, Result result)
        {
            int n = srcOfHull.Count;
            int total = mesh.vertexCount;
            var dv = new Vector3[total];
            var dn = new Vector3[total];
            var dt = new Vector3[total];
            float minMove = s.blendShapeMinMoveMm * 0.001f;
            for (int bs = 0; bs < mesh.blendShapeCount; bs++)
            {
                int frames = mesh.GetBlendShapeFrameCount(bs);
                var shape = new Shape { name = mesh.GetBlendShapeName(bs) };
                float biggest = 0f;
                for (int f = 0; f < frames; f++)
                {
                    mesh.GetBlendShapeFrameVertices(bs, f, dv, dn, dt);
                    var d = new Vector3[n];
                    for (int i = 0; i < n; i++)
                    {
                        d[i] = dv[srcOfHull[i]];
                        float m = d[i].magnitude * scale[srcOfHull[i]];
                        if (m > biggest) biggest = m;
                    }
                    shape.weights.Add(mesh.GetBlendShapeFrameWeight(bs, f));
                    shape.deltas.Add(d);
                }
                if (biggest < minMove) continue; // doesn't visibly move the outline
                hull.shapes.Add(shape);
            }
            result.blendShapes += hull.shapes.Count;
        }

        // ------------------------------------------------------------------ optimization

        static bool SameWeights(BoneWeight a, BoneWeight b)
        {
            return a.boneIndex0 == b.boneIndex0 && a.boneIndex1 == b.boneIndex1 && a.boneIndex2 == b.boneIndex2 && a.boneIndex3 == b.boneIndex3
                && Mathf.Abs(a.weight0 - b.weight0) < 1e-4f && Mathf.Abs(a.weight1 - b.weight1) < 1e-4f
                && Mathf.Abs(a.weight2 - b.weight2) < 1e-4f && Mathf.Abs(a.weight3 - b.weight3) < 1e-4f;
        }

        /// <summary>Merges vertices at the same position with the same skinning and blend shape movement.</summary>
        static void Weld(Hull h)
        {
            int n = h.pos.Count;
            var remap = new int[n];
            var buckets = new Dictionary<long, List<int>>();
            var keep = new List<int>();
            for (int i = 0; i < n; i++)
            {
                long k = Key(h.pos[i]);
                List<int> list;
                if (!buckets.TryGetValue(k, out list)) { list = new List<int>(); buckets[k] = list; }
                int found = -1;
                foreach (int j in list)
                {
                    if (!SameWeights(h.bw[i], h.bw[j])) continue;
                    if (!SameShapeDeltas(h, i, j)) continue;
                    found = remap[j];
                    break;
                }
                if (found >= 0) { remap[i] = found; continue; }
                remap[i] = keep.Count;
                keep.Add(i);
                list.Add(i);
            }
            Compact(h, remap, keep);
        }

        static bool SameShapeDeltas(Hull h, int a, int b)
        {
            foreach (var sh in h.shapes)
                foreach (var f in sh.deltas)
                    if ((f[a] - f[b]).sqrMagnitude > 1e-10f) return false;
            return true;
        }

        /// <summary>Vertex clustering: vertices in the same small cell (and with the same main bone) become one.</summary>
        static void Cluster(Hull h, float cell)
        {
            int n = h.pos.Count;
            var remap = new int[n];
            var cellMap = new Dictionary<string, int>();
            var keep = new List<int>();
            var sums = new List<Vector3>();
            var counts = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var p = h.pos[i];
                string k = Mathf.FloorToInt(p.x / cell) + "," + Mathf.FloorToInt(p.y / cell) + "," + Mathf.FloorToInt(p.z / cell) + "," + MainBone(h.bw[i]);
                int idx;
                if (!cellMap.TryGetValue(k, out idx))
                {
                    idx = keep.Count;
                    cellMap[k] = idx;
                    keep.Add(i);
                    sums.Add(Vector3.zero);
                    counts.Add(0);
                }
                remap[i] = idx;
                sums[idx] += p;
                counts[idx]++;
            }
            // Averaged position for each cluster; blend shape deltas follow the cluster's first vertex.
            for (int c = 0; c < keep.Count; c++) h.pos[keep[c]] = sums[c] / counts[c];
            Compact(h, remap, keep);
        }

        static int MainBone(BoneWeight w)
        {
            int b = w.boneIndex0; float best = w.weight0;
            if (w.weight1 > best) { best = w.weight1; b = w.boneIndex1; }
            if (w.weight2 > best) { best = w.weight2; b = w.boneIndex2; }
            if (w.weight3 > best) { b = w.boneIndex3; }
            return b;
        }

        static void Compact(Hull h, int[] remap, List<int> keep)
        {
            var pos = new List<Vector3>(keep.Count);
            var nrm = new List<Vector3>(keep.Count);
            var bw = new List<BoneWeight>(keep.Count);
            foreach (int i in keep) { pos.Add(h.pos[i]); nrm.Add(h.nrm[i]); bw.Add(h.bw[i]); }
            foreach (var sh in h.shapes)
                for (int f = 0; f < sh.deltas.Count; f++)
                {
                    var d = new Vector3[keep.Count];
                    for (int c = 0; c < keep.Count; c++) d[c] = sh.deltas[f][keep[c]];
                    sh.deltas[f] = d;
                }
            var tris = new List<int>(h.tris.Count);
            for (int t = 0; t + 2 < h.tris.Count; t += 3)
            {
                int a = remap[h.tris[t]], b = remap[h.tris[t + 1]], c = remap[h.tris[t + 2]];
                if (a == b || b == c || a == c) continue; // collapsed
                tris.Add(a); tris.Add(b); tris.Add(c);
            }
            h.pos = pos; h.nrm = nrm; h.bw = bw; h.tris = tris;
        }

        // ------------------------------------------------------------------ writing

        static Mesh ToMesh(Hull h, Mesh source, bool skinned)
        {
            var m = new Mesh();
            if (h.pos.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(h.pos);
            m.SetNormals(h.nrm);
            m.SetTriangles(h.tris, 0);
            if (skinned)
            {
                m.bindposes = source.bindposes;
                m.boneWeights = h.bw.ToArray();
            }
            foreach (var sh in h.shapes)
                for (int f = 0; f < sh.deltas.Count; f++)
                    m.AddBlendShapeFrame(sh.name, sh.weights[f], sh.deltas[f], null, null);
            m.RecalculateBounds();
            return m;
        }

        static Mesh SaveMesh(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);
                return existing;
            }
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        /// <summary>Finds the "Outlines" parent under the avatar root, or creates it as an empty object with an identity transform.</summary>
        static Transform GetOrCreateOutlinesParent(GameObject root)
        {
            if (root == null) return null;
            var parent = root.transform.Find(ParentName);
            if (parent != null) return parent;
            var go = new GameObject(ParentName);
            Undo.RegisterCreatedObjectUndo(go, "Add outline");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.layer = root.layer;
            return go.transform;
        }

        static void PlaceSeparate(RendererInfo info, Mesh hull, Material mat, Transform outlinesParent)
        {
            var src = info.renderer;
            var go = new GameObject(Sanitize(src.name) + Suffix);
            Undo.RegisterCreatedObjectUndo(go, "Add outline");
            go.transform.SetParent(src.transform.parent, false);
            go.transform.localPosition = src.transform.localPosition;
            go.transform.localRotation = src.transform.localRotation;
            go.transform.localScale = src.transform.localScale;
            go.layer = src.gameObject.layer;
            go.SetActive(src.gameObject.activeSelf);
            // All separate outline objects live under one "Outlines" parent on the avatar, so the hierarchy stays tidy.
            if (outlinesParent != null) go.transform.SetParent(outlinesParent, true);

            var smr = src as SkinnedMeshRenderer;
            if (smr != null)
            {
                var r = go.AddComponent<SkinnedMeshRenderer>();
                r.sharedMesh = hull;
                r.bones = smr.bones;
                r.rootBone = smr.rootBone;
                r.localBounds = smr.localBounds;
                r.updateWhenOffscreen = smr.updateWhenOffscreen;
                r.quality = smr.quality;
                r.sharedMaterials = new[] { mat };
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.probeAnchor = smr.probeAnchor;
                r.lightProbeUsage = smr.lightProbeUsage;
                r.reflectionProbeUsage = smr.reflectionProbeUsage;
                // Follow the original's blend shape weights while editing. (VRChat drives both with the same parameters.)
                for (int i = 0; i < hull.blendShapeCount; i++)
                {
                    int idx = smr.sharedMesh.GetBlendShapeIndex(hull.GetBlendShapeName(i));
                    if (idx >= 0) r.SetBlendShapeWeight(i, smr.GetBlendShapeWeight(idx));
                }
            }
            else
            {
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = hull;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = new[] { mat };
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
        }

        /// <summary>Appends the hull to the original mesh as one more submesh, so it costs no extra renderer.</summary>
        static Mesh Merge(RendererInfo info, Mesh hull, Material mat, string folder, string baseName, Result result)
        {
            var src = info.mesh;
            string srcPath = AssetDatabase.GetAssetPath(src);
            string path = folder + "/" + baseName + "_WithOutline.asset";
            var combined = new Mesh { name = baseName + "_WithOutline" };

            int vCount = src.vertexCount;
            int hCount = hull.vertexCount;
            if (vCount + hCount > 65000) combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            var verts = new List<Vector3>(src.vertices); verts.AddRange(hull.vertices);
            var nrm = new List<Vector3>(); var srcN = src.normals;
            if (srcN.Length == vCount) { nrm.AddRange(srcN); nrm.AddRange(hull.normals); }
            var tan = new List<Vector4>(); var srcT = src.tangents;
            if (srcT.Length == vCount) { tan.AddRange(srcT); for (int i = 0; i < hCount; i++) tan.Add(new Vector4(1, 0, 0, 1)); }
            var col = new List<Color32>(); var srcC = src.colors32;
            if (srcC.Length == vCount) { col.AddRange(srcC); for (int i = 0; i < hCount; i++) col.Add(new Color32(255, 255, 255, 255)); }

            combined.SetVertices(verts);
            if (nrm.Count > 0) combined.SetNormals(nrm);
            if (tan.Count > 0) combined.SetTangents(tan);
            if (col.Count > 0) combined.SetColors(col);

            for (int ch = 0; ch < 4; ch++)
            {
                var attr = UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch;
                if (!src.HasVertexAttribute(attr)) continue;
                var uv = new List<Vector4>();
                src.GetUVs(ch, uv);
                for (int i = 0; i < hCount; i++) uv.Add(Vector4.zero);
                combined.SetUVs(ch, uv);
            }

            combined.subMeshCount = src.subMeshCount + 1;
            for (int i = 0; i < src.subMeshCount; i++)
            {
                var d = src.GetSubMesh(i);
                var idx = src.GetIndices(i);
                combined.SetIndices(idx, d.topology, i, false, 0);
            }
            var hullTris = hull.triangles;
            for (int i = 0; i < hullTris.Length; i++) hullTris[i] += vCount;
            combined.SetTriangles(hullTris, src.subMeshCount, false);

            var smr = info.renderer as SkinnedMeshRenderer;
            if (smr != null)
            {
                combined.bindposes = src.bindposes;
                var bw = new List<BoneWeight>(src.boneWeights);
                if (bw.Count != vCount) { Object.DestroyImmediate(combined); result.warnings.Add(info.renderer.name + ": has no bone weights, can't merge"); return null; }
                bw.AddRange(hull.boneWeights);
                combined.boneWeights = bw.ToArray();
            }

            // Blend shapes: original ones keep their deltas, the hull's copies follow the same names.
            var srcDv = new Vector3[vCount]; var srcDn = new Vector3[vCount]; var srcDt = new Vector3[vCount];
            var hullIndex = new Dictionary<string, int>();
            for (int i = 0; i < hull.blendShapeCount; i++) hullIndex[hull.GetBlendShapeName(i)] = i;
            var hv = new Vector3[hCount]; var hn = new Vector3[hCount]; var ht = new Vector3[hCount];
            var dvAll = new Vector3[vCount + hCount]; var dnAll = new Vector3[vCount + hCount]; var dtAll = new Vector3[vCount + hCount];
            for (int bs = 0; bs < src.blendShapeCount; bs++)
            {
                string name = src.GetBlendShapeName(bs);
                int hs;
                bool has = hullIndex.TryGetValue(name, out hs);
                int frames = src.GetBlendShapeFrameCount(bs);
                for (int f = 0; f < frames; f++)
                {
                    src.GetBlendShapeFrameVertices(bs, f, srcDv, srcDn, srcDt);
                    Array.Clear(dvAll, 0, dvAll.Length); Array.Clear(dnAll, 0, dnAll.Length); Array.Clear(dtAll, 0, dtAll.Length);
                    Array.Copy(srcDv, dvAll, vCount); Array.Copy(srcDn, dnAll, vCount); Array.Copy(srcDt, dtAll, vCount);
                    if (has && f < hull.GetBlendShapeFrameCount(hs))
                    {
                        hull.GetBlendShapeFrameVertices(hs, f, hv, hn, ht);
                        Array.Copy(hv, 0, dvAll, vCount, hCount);
                    }
                    combined.AddBlendShapeFrame(name, src.GetBlendShapeFrameWeight(bs, f), dvAll, dnAll, dtAll);
                }
            }
            combined.bounds = src.bounds;

            var saved = SaveMesh(combined, path);

            // Remember what the original was, so "Remove outlines" can put it back.
            string side = path.Substring(0, path.Length - ".asset".Length) + ".origin.json";
            var origin = new Origin
            {
                meshGuid = AssetDatabase.AssetPathToGUID(srcPath),
                materialCount = info.renderer.sharedMaterials.Length,
            };
            if (!string.IsNullOrEmpty(origin.meshGuid)) File.WriteAllText(side, JsonUtility.ToJson(origin));
            else result.warnings.Add(info.renderer.name + ": the original mesh isn't an asset, so \"Remove outlines\" can't restore it (use Ctrl+Z)");

            Undo.RecordObject(info.renderer, "Add outline");
            SetMesh(info.renderer, saved);
            var mats = info.renderer.sharedMaterials;
            var list = new List<Material>(mats);
            while (list.Count < src.subMeshCount) list.Add(null);
            list.Add(mat);
            info.renderer.sharedMaterials = list.ToArray();
            result.bytes += FileSize(path);
            return saved;
        }

        // ------------------------------------------------------------------ materials

        static Material GetMaterial(string folder, Color color, Settings s, Dictionary<string, Material> cache)
        {
            if (s.shader == ShaderChoice.ExistingMaterial && s.existingMaterial != null) return s.existingMaterial;

            string key = ColorUtility.ToHtmlStringRGBA(color);
            Material m;
            if (cache.TryGetValue(key, out m) && m != null) return m;

            Shader shader = null;
            switch (s.shader)
            {
                case ShaderChoice.MobileToonLit: shader = Shader.Find("VRChat/Mobile/Toon Lit"); break;
                case ShaderChoice.MobileStandardLite: shader = Shader.Find("VRChat/Mobile/Standard Lite"); break;
                case ShaderChoice.UnlitColor: shader = Shader.Find("Unlit/Color"); break;
                case ShaderChoice.Custom: shader = s.customShader; break;
            }
            if (shader == null) shader = Shader.Find("VRChat/Mobile/Standard Lite");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Standard");

            string path = folder + "/Outline_" + key + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            m = new Material(shader) { name = "Outline " + key };
            ApplyColor(m, color, s.flatLook, folder, key);
            m.renderQueue = -1;
            AssetDatabase.CreateAsset(m, path);
            cache[key] = m;
            return m;
        }

        static readonly string[] s_ColorProps = { "_Color", "_BaseColor", "_MainColor", "_TintColor", "_LightColor", "_DiffuseColor" };

        static void ApplyColor(Material m, Color color, bool flat, string folder, string key)
        {
            bool set = false;
            foreach (var p in s_ColorProps)
                if (m.HasProperty(p)) { m.SetColor(p, color); set = true; }
            if (!set)
            {
                // No color property at all: use a tiny solid texture instead.
                string main = null;
                foreach (var p in new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo" })
                    if (m.HasProperty(p)) { main = p; break; }
                if (main != null) m.SetTexture(main, SolidTexture(color, folder, key));
            }
            if (flat)
            {
                // Make lit shaders ignore lighting so the outline stays a flat, solid color.
                if (m.HasProperty("_EmissionColor"))
                {
                    m.EnableKeyword("_EMISSION");
                    m.SetColor("_EmissionColor", color);
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                }
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 0f);
            }
        }

        static Texture2D SolidTexture(Color color, string folder, string key)
        {
            string path = folder + "/Outline_" + key + ".png";
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = color;
            tex.SetPixels(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ------------------------------------------------------------------ helpers

        public static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        static void EnsureFolder(string path)
        {
            var parts = path.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
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

        /// <summary>Rough size of one outline vertex in memory: position, normal and four bone weights.</summary>
        public const int BytesPerVertexEstimate = 56;

        public static string Quest(int tris)
        {
            if (tris <= 7500) return "Excellent";
            if (tris <= 10000) return "Good";
            if (tris <= 15000) return "Medium";
            if (tris <= 20000) return "Poor";
            return "Very Poor";
        }
    }
}
