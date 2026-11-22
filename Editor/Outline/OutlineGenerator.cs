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
            public bool exactWorldWidth = true;
            public Color color = new Color(0.05f, 0.05f, 0.05f, 1f);
            public Placement placement = Placement.SeparateObject;
            public ShaderChoice shader = ShaderChoice.MobileToonLit;
            public Shader customShader;
            public Material existingMaterial;
            public bool flatLook = true;
            public bool smoothNormals = true;
            public bool useVertexColorWidth;
            public bool copyBlendShapes = true;
            public bool linkBlendShapes = true;
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
            bool linkWarned = false;
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
                    var outlineGo = PlaceSeparate(info, saved, mat);
                    WriteSidecar(path, root, info, s, included);
                    result.bytes += FileSize(path);
                    if (s.linkBlendShapes && hull.blendShapeCount > 0)
                    {
                        string linkNote = AddBlendShapeLink(outlineGo, info.renderer.gameObject);
                        if (linkNote != null && !linkWarned) { result.warnings.Add(linkNote); linkWarned = true; }
                    }
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

        /// <summary>What one separate-object outline was built from, saved next to its hull asset so its width can be rebuilt later.</summary>
        [Serializable]
        public class OutlineSidecar
        {
            public int version = 1;
            public bool exactWorldWidth = true;
            public string avatarName;
            public string sourcePath;
            public float widthCm;
            public float widthMultiplier;
            public float colorR;
            public float colorG;
            public float colorB;
            public float colorA;
            public bool overrideColor;
            public int[] includedSlots;
            public bool smoothNormals;
            public bool useVertexColorWidth;
            public bool copyBlendShapes;
            public float blendShapeMinMoveMm;
            public string[] excludedBones;
            public bool weldVertices;
            public float simplifyMm;
            public int triangleBudget;
        }

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

        /// <summary>Front half of a hull build: the hull with UNPUSHED base positions, plus per hull vertex the push direction, factor and scale. Pushing a hull vertex is dir * (width * factor / scale), where width is in metres.</summary>
        class HullBase
        {
            public Hull hull;
            public Mesh sourceMesh;
            public bool skinned;
            public Vector3[] pushDir;
            public float[] pushFactor;
            public float[] pushScale;
        }

        static HullBase BuildHullBase(RendererInfo info, List<SlotInfo> included, Settings s, Result result)
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

            // Per source vertex: push direction and width factor (0 when the vertex gets no outline).
            var dir = new Vector3[verts.Length];
            var factor = new float[verts.Length];
            var alive = new bool[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                float f = boneFactor != null ? boneFactor[i] : 1f;
                if (s.useVertexColorWidth && colors != null && colors.Length == verts.Length) f *= colors[i].r / 255f;
                alive[i] = f > 0.02f;
                if (!alive[i]) continue;
                dir[i] = smooth[i].normalized;
                factor[i] = f;
            }

            // Push scale: how many mesh-space units make one world-space metre at each vertex.
            // With exactWorldWidth, the push is recomputed per vertex as the local-space vector that
            // moves it by the exact width along its world-space normal, so the outline stays even
            // when the object or its bones are scaled unevenly. Without it, the width is divided by
            // the average of the scale components, which is only even for uniform scale.
            var pushScale = scale;
            if (s.exactWorldWidth)
            {
                pushScale = (float[])scale.Clone();
                ExactPush(info.renderer, mesh, weights, skinned, smooth, factor, alive, dir, factor, pushScale);
            }

            // Which vertices are used, and how wide each is. Positions stay unpushed here; the caller pushes them.
            var map = new int[verts.Length];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            var hull = new Hull();
            var srcOfHull = new List<int>();
            var pushDir = new List<Vector3>();
            var pushFactor = new List<float>();
            var pushScaleList = new List<float>();
            for (int t = 0; t + 2 < srcTris.Count; t += 3)
            {
                int a = srcTris[t], b = srcTris[t + 1], c = srcTris[t + 2];
                if (!alive[a] && !alive[b] && !alive[c]) continue;
                int na = UseBase(a, map, hull, srcOfHull, pushDir, pushFactor, pushScaleList, verts, smooth, dir, factor, pushScale, weights, skinned);
                int nb = UseBase(b, map, hull, srcOfHull, pushDir, pushFactor, pushScaleList, verts, smooth, dir, factor, pushScale, weights, skinned);
                int nc = UseBase(c, map, hull, srcOfHull, pushDir, pushFactor, pushScaleList, verts, smooth, dir, factor, pushScale, weights, skinned);
                // Flipped winding: this is what turns the copy into an outline.
                hull.tris.Add(na); hull.tris.Add(nc); hull.tris.Add(nb);
            }
            if (hull.tris.Count == 0) return null;

            if (s.copyBlendShapes && mesh.blendShapeCount > 0) CopyShapes(mesh, srcOfHull, scale, s, hull, result);
            return new HullBase
            {
                hull = hull,
                sourceMesh = mesh,
                skinned = skinned,
                pushDir = pushDir.ToArray(),
                pushFactor = pushFactor.ToArray(),
                pushScale = pushScaleList.ToArray(),
            };
        }

        static Mesh BuildHull(RendererInfo info, List<SlotInfo> included, Settings s, Result result)
        {
            var b = BuildHullBase(info, included, s, result);
            if (b == null) return null;
            var hull = b.hull;
            float width = s.widthCm * 0.01f * Mathf.Max(0.01f, info.widthMultiplier);
            for (int i = 0; i < hull.pos.Count; i++)
                hull.pos[i] = hull.pos[i] + b.pushDir[i] * (width * b.pushFactor[i] / b.pushScale[i]);

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
            return ToMesh(hull, b.sourceMesh, b.skinned);
        }

        static int UseBase(int v, int[] map, Hull h, List<int> srcOfHull, List<Vector3> pushDir, List<float> pushFactor, List<float> pushScale,
            Vector3[] verts, Vector3[] smooth, Vector3[] dir, float[] factor, float[] scale, BoneWeight[] bw, bool skinned)
        {
            if (map[v] >= 0) return map[v];
            map[v] = h.pos.Count;
            srcOfHull.Add(v);
            h.pos.Add(verts[v]);
            h.nrm.Add(-smooth[v].normalized);
            h.bw.Add(skinned ? bw[v] : default(BoneWeight));
            pushDir.Add(dir[v]);
            pushFactor.Add(factor[v]);
            pushScale.Add(Mathf.Max(1e-5f, scale[v]));
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

        /// <summary>Rewrites the per-vertex push so the outline is exactly the requested width in world space: for each live vertex, the local-space vector that moves it by width * f along its world-space normal. Vertices whose skinning matrix can't be inverted keep the average-scale push. The scale array is only used for the blend-shape min-move check, so it is left alone; the push scale is rewritten in place.</summary>
        static void ExactPush(Renderer r, Mesh mesh, BoneWeight[] weights, bool skinned, Vector3[] smooth, float[] factor, bool[] alive, Vector3[] dir, float[] outFactor, float[] outScale)
        {
            int count = smooth.Length;
            Matrix4x4 single = new Matrix4x4();
            Matrix4x4[] boneMatrices = null;
            if (!skinned)
            {
                single = r.transform.localToWorldMatrix;
            }
            else
            {
                var smr = (SkinnedMeshRenderer)r;
                var bones = smr.bones;
                var bind = mesh.bindposes;
                boneMatrices = new Matrix4x4[bones.Length];
                for (int j = 0; j < bones.Length; j++)
                {
                    if (bones[j] == null || j >= bind.Length) boneMatrices[j] = Matrix4x4.identity;
                    else boneMatrices[j] = bones[j].localToWorldMatrix * bind[j];
                }
            }
            for (int i = 0; i < count; i++)
            {
                if (!alive[i]) continue;
                Matrix4x4 m;
                if (!skinned)
                {
                    m = single;
                }
                else
                {
                    var w = weights[i];
                    m = new Matrix4x4();
                    AddBone(ref m, boneMatrices, w.boneIndex0, w.weight0);
                    AddBone(ref m, boneMatrices, w.boneIndex1, w.weight1);
                    AddBone(ref m, boneMatrices, w.boneIndex2, w.weight2);
                    AddBone(ref m, boneMatrices, w.boneIndex3, w.weight3);
                }
                Vector3 eDir; float eFactor; float eScale;
                if (!ExactPushVector(m, smooth[i].normalized, factor[i], out eDir, out eFactor, out eScale)) continue;
                dir[i] = eDir;
                outFactor[i] = eFactor;
                outScale[i] = eScale;
            }
        }

        static void AddBone(ref Matrix4x4 m, Matrix4x4[] boneMatrices, int bone, float weight)
        {
            if (weight <= 0f || bone < 0 || bone >= boneMatrices.Length) return;
            m += boneMatrices[bone] * weight;
        }

        /// <summary>The local-space push for one vertex at the exact world-space width: direction, factor and scale such that dir * (width * factor / scale) moves the vertex by width * f along its world-space normal. Returns false when the vertex's matrix can't be inverted, so the caller keeps the average-scale push.</summary>
        static bool ExactPushVector(Matrix4x4 m, Vector3 nLocal, float f, out Vector3 dir, out float factor, out float scale)
        {
            dir = Vector3.zero; factor = 0f; scale = 1f;
            if (Mathf.Abs(m.determinant) < 1e-12f) return false;
            var inv = m.inverse;
            var nWorld = inv.transpose.MultiplyVector(nLocal);
            if (nWorld.sqrMagnitude < 1e-12f) return false;
            nWorld.Normalize();
            var v = inv.MultiplyVector(nWorld);
            if (v.sqrMagnitude < 1e-12f) return false;
            if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) return false;
            dir = v.normalized;
            factor = f * v.magnitude;
            scale = 1f;
            return true;
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

        /// <summary>Saves what one separate-object outline was built from, next to its hull asset, so its width can be rebuilt later.</summary>
        static void WriteSidecar(string hullAssetPath, GameObject root, RendererInfo info, Settings s, List<SlotInfo> included)
        {
            Color col = info.overrideColor ? info.color : s.color;
            var slots = new List<int>();
            foreach (var sl in included) slots.Add(sl.index);
            var sidecar = new OutlineSidecar
            {
                version = 2,
                exactWorldWidth = s.exactWorldWidth,
                avatarName = root != null ? root.name : "",
                sourcePath = root != null ? RelativePath(root.transform, info.renderer.transform) : info.renderer.name,
                widthCm = s.widthCm,
                widthMultiplier = info.widthMultiplier,
                colorR = col.r,
                colorG = col.g,
                colorB = col.b,
                colorA = col.a,
                overrideColor = info.overrideColor,
                includedSlots = slots.ToArray(),
                smoothNormals = s.smoothNormals,
                useVertexColorWidth = s.useVertexColorWidth,
                copyBlendShapes = s.copyBlendShapes,
                blendShapeMinMoveMm = s.blendShapeMinMoveMm,
                excludedBones = BoneNames(s.excludedBones),
                weldVertices = s.weldVertices,
                simplifyMm = s.simplifyMm,
                triangleBudget = s.triangleBudget,
            };
            string sidecarPath = hullAssetPath.Substring(0, hullAssetPath.Length - ".asset".Length) + ".json";
            File.WriteAllText(sidecarPath, JsonUtility.ToJson(sidecar));
        }

        /// <summary>Hierarchy path of a transform relative to the avatar root (empty when it is the root itself).</summary>
        static string RelativePath(Transform root, Transform t)
        {
            if (t == null || t == root) return "";
            string path = t.name;
            Transform cur = t.parent;
            while (cur != null && cur != root)
            {
                path = cur.name + "/" + path;
                cur = cur.parent;
            }
            return path;
        }

        static string[] BoneNames(List<Transform> bones)
        {
            if (bones == null) return new string[0];
            var names = new List<string>();
            foreach (var b in bones) names.Add(b != null ? b.name : "");
            return names.ToArray();
        }

        /// <summary>Loads the width sidecar saved next to an outline's hull asset, or null when there is none.</summary>
        public static OutlineSidecar LoadSidecar(GameObject outlineObject)
        {
            string sidecarPath = SidecarPath(outlineObject);
            if (sidecarPath == null || !File.Exists(sidecarPath)) return null;
            return JsonUtility.FromJson<OutlineSidecar>(File.ReadAllText(sidecarPath));
        }

        static string SidecarPath(GameObject outlineObject)
        {
            if (outlineObject == null) return null;
            var r = outlineObject.GetComponent<Renderer>();
            if (r == null) return null;
            Mesh hull = MeshOf(r);
            if (hull == null) return null;
            string hullPath = AssetDatabase.GetAssetPath(hull);
            if (string.IsNullOrEmpty(hullPath) || !hullPath.EndsWith(".asset", StringComparison.Ordinal)) return null;
            return hullPath.Substring(0, hullPath.Length - ".asset".Length) + ".json";
        }

        static GameObject PlaceSeparate(RendererInfo info, Mesh hull, Material mat)
        {
            var src = info.renderer;
            var go = new GameObject(Sanitize(src.name) + Suffix);
            Undo.RegisterCreatedObjectUndo(go, "Add outline");
            // The outline is a child of the object it outlines, so disabling the source hides its outline too,
            // and accessories that move (for example VRCFury Armature Link) carry their outlines with them.
            // The hull is in the source mesh's local space, so an identity local transform puts it exactly
            // where the source is, for skinned and non-skinned meshes alike.
            go.transform.SetParent(src.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.layer = src.gameObject.layer;
            go.SetActive(src.gameObject.activeSelf);

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
                // Copy the source's blend shape weights so the outline matches in the editor.
                // At runtime VRChat animates the source's weights only, so the outline follows
                // toggles through the VRCFury BlendShape Link added at Generate time (or merged placement).
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
            return go;
        }

        // ------------------------------------------------------------------ VRCFury blend shape link

        /// <summary>Finds a type by its full name in any loaded assembly, or null. Used for VRCFury types so this tool has no compile-time dependency on VRCFury.</summary>
        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>Adds a VRCFury BlendShape Link to an outline object, so the outline's blend shapes follow the source's at runtime. VRCFury rewrites every animation clip at upload, after its own toggles are baked, to drive the linked mesh's same-named blend shapes. That is the only way to follow toggles: the toggle clips don't exist in the editor, so no editor-time clip edit could cover them. The link lives on the outline object, so Remove outlines takes it away too. Field names are from VRCFury's BlendShapeLink model (VF.Model.Feature.BlendShapeLink) and its VRCFury component (VF.Model.VRCFury, content field); re-check them if VRCFury changes the model. Returns null on success, or a short reason it was skipped.</summary>
        public static string AddBlendShapeLink(GameObject outlineObject, GameObject sourceObject)
        {
            var outlineSmr = outlineObject != null ? outlineObject.GetComponent<SkinnedMeshRenderer>() : null;
            if (outlineSmr == null) return null; // not skinned, so no blend shapes to link
            if (outlineSmr.sharedMesh == null || outlineSmr.sharedMesh.blendShapeCount == 0) return null;
            var vrcFuryType = FindType("VF.Model.VRCFury");
            var linkType = FindType("VF.Model.Feature.BlendShapeLink");
            if (vrcFuryType == null || linkType == null)
                return "VRCFury is not installed, so the outline's blend shapes won't follow toggles. Install VRCFury and re-run Generate to link them.";
            // Drop any BlendShape Link already on this outline, so re-running never stacks duplicates.
            var contentField = vrcFuryType.GetField("content");
            if (contentField != null)
            {
                foreach (var c in outlineObject.GetComponents(vrcFuryType))
                {
                    var existing = contentField.GetValue(c);
                    if (existing != null && existing.GetType() == linkType)
                        Undo.DestroyObjectImmediate(c);
                }
            }
            var comp = outlineObject.AddComponent(vrcFuryType);
            Undo.RegisterCreatedObjectUndo(comp, "Add outline");
            object model;
            try { model = Activator.CreateInstance(linkType, nonPublic: true); }
            catch (Exception e)
            {
                Undo.DestroyObjectImmediate(comp);
                return "Couldn't create the VRCFury BlendShape Link: " + e.Message;
            }
            var so = new SerializedObject(comp);
            var content = so.FindProperty("content");
            if (content == null)
            {
                Undo.DestroyObjectImmediate(comp);
                return "VRCFury's component has no content field, so the blend shape link couldn't be added.";
            }
            content.managedReferenceValue = model;
            so.ApplyModifiedProperties();
            so.Update();
            content = so.FindProperty("content");
            if (content == null)
            {
                Undo.DestroyObjectImmediate(comp);
                return "VRCFury's component has no content field, so the blend shape link couldn't be added.";
            }
            var baseProp = content.FindPropertyRelative("baseObj");
            var skinsProp = content.FindPropertyRelative("linkSkins");
            var includeProp = content.FindPropertyRelative("includeAll");
            if (baseProp == null || skinsProp == null || includeProp == null)
            {
                Undo.DestroyObjectImmediate(comp);
                return "VRCFury's BlendShape Link doesn't have the expected fields, so the blend shape link couldn't be added.";
            }
            baseProp.stringValue = sourceObject != null ? sourceObject.name : "";
            includeProp.boolValue = true;
            skinsProp.arraySize = 1;
            var skinProp = skinsProp.GetArrayElementAtIndex(0).FindPropertyRelative("renderer");
            if (skinProp == null)
            {
                Undo.DestroyObjectImmediate(comp);
                return "VRCFury's BlendShape Link doesn't have the expected fields, so the blend shape link couldn't be added.";
            }
            skinProp.objectReferenceValue = outlineSmr;
            so.ApplyModifiedProperties();
            return null;
        }

        /// <summary>Rebuilds one separate-object outline's hull from its sidecar with a new width. Returns null on success, or an error message.</summary>
        public static string ResizeOutline(GameObject outlineObject, float newWidthCm, float newWidthMultiplier)
        {
            if (outlineObject == null) return "No outline selected.";
            var outlineRenderer = outlineObject.GetComponent<Renderer>();
            if (outlineRenderer == null) return "That object has no outline renderer.";
            var sourceObject = outlineObject.transform.parent != null ? outlineObject.transform.parent.gameObject : null;
            if (sourceObject == null) return "The outline has no parent, so its source can't be found.";
            var sourceRenderer = sourceObject.GetComponent<Renderer>();
            if (sourceRenderer == null) return "The outline's parent has no renderer, so its source can't be found.";
            Mesh hull = MeshOf(outlineRenderer);
            if (hull == null) return "The outline has no mesh.";
            string hullPath = AssetDatabase.GetAssetPath(hull);
            if (string.IsNullOrEmpty(hullPath) || !hullPath.EndsWith(".asset", StringComparison.Ordinal))
                return "The outline's mesh is not a saved asset, so it can't be rebuilt.";
            string sidecarPath = hullPath.Substring(0, hullPath.Length - ".asset".Length) + ".json";
            if (!File.Exists(sidecarPath))
                return "This outline was made before width info was saved. Re-run Generate in the Outline window once to enable resizing.";
            var sidecar = JsonUtility.FromJson<OutlineSidecar>(File.ReadAllText(sidecarPath));
            if (sidecar == null) return "The outline's saved width info can't be read.";

            var info = InfoForResize(sourceRenderer, sidecar);
            info.widthMultiplier = newWidthMultiplier;
            if (info.mesh == null) return "The source has no mesh.";
            var included = new List<SlotInfo>();
            foreach (var sl in info.slots) if (sl.include) included.Add(sl);
            if (included.Count == 0) return "No material slots were outlined for this object.";
            var s = SettingsFromSidecar(sidecar, newWidthCm, sourceRenderer);

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Resize outline");
            int group = Undo.GetCurrentGroup();

            var result = new Result();
            Mesh newHull;
            try { newHull = BuildHull(info, included, s, result); }
            catch (Exception e)
            {
                Debug.LogException(e);
                Undo.CollapseUndoOperations(group);
                return "Rebuild failed: " + e.Message;
            }
            if (newHull == null)
            {
                Undo.CollapseUndoOperations(group);
                return "Nothing left to outline with these settings.";
            }
            newHull.name = Path.GetFileNameWithoutExtension(hullPath);
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(hullPath);
            if (existing != null) Undo.RecordObject(existing, "Resize outline");
            var saved = SaveMesh(newHull, hullPath);
            Undo.RecordObject(outlineRenderer, "Resize outline");
            var outlineSmr = outlineRenderer as SkinnedMeshRenderer;
            if (outlineSmr != null)
            {
                outlineSmr.localBounds = saved.bounds;
                var sourceSmr = sourceRenderer as SkinnedMeshRenderer;
                if (sourceSmr != null && sourceSmr.sharedMesh != null)
                {
                    for (int i = 0; i < saved.blendShapeCount; i++)
                    {
                        int idx = sourceSmr.sharedMesh.GetBlendShapeIndex(saved.GetBlendShapeName(i));
                        if (idx >= 0) outlineSmr.SetBlendShapeWeight(i, sourceSmr.GetBlendShapeWeight(idx));
                    }
                }
            }
            sidecar.widthCm = newWidthCm;
            sidecar.widthMultiplier = newWidthMultiplier;
            File.WriteAllText(sidecarPath, JsonUtility.ToJson(sidecar));
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            return null;
        }

        // ------------------------------------------------------------------ live width preview

        /// <summary>A live width preview: a temp mesh on the outline renderer that follows the slider while dragging. Never written to the hull asset.</summary>
        public class WidthPreview
        {
            public GameObject outlineObject;
            public Renderer outlineRenderer;
            public Renderer sourceRenderer;
            public string sourceName;
            public Mesh originalMesh;
            public Bounds originalLocalBounds;
            public Mesh tempMesh;
            public Vector3[] basePos;
            public Vector3[] pushDir;
            public float[] pushFactor;
            public float[] pushScale;
            public Vector3[] scratch;
            public float widthMultiplier;
            public float widthCm;
        }

        /// <summary>Starts a live width preview on one outline: builds a temp mesh from its sidecar and swaps it onto the outline renderer. Returns null on success, or an error message.</summary>
        public static string BeginWidthPreview(GameObject outlineObject, out WidthPreview preview)
        {
            preview = null;
            if (outlineObject == null) return "No outline selected.";
            var outlineRenderer = outlineObject.GetComponent<Renderer>();
            if (outlineRenderer == null) return "That object has no outline renderer.";
            var sourceObject = outlineObject.transform.parent != null ? outlineObject.transform.parent.gameObject : null;
            if (sourceObject == null) return "The outline has no parent, so its source can't be found.";
            var sourceRenderer = sourceObject.GetComponent<Renderer>();
            if (sourceRenderer == null) return "The outline's parent has no renderer, so its source can't be found.";
            Mesh hull = MeshOf(outlineRenderer);
            if (hull == null) return "The outline has no mesh.";
            string hullPath = AssetDatabase.GetAssetPath(hull);
            if (string.IsNullOrEmpty(hullPath) || !hullPath.EndsWith(".asset", StringComparison.Ordinal))
                return "The outline's mesh is not a saved asset, so it can't be rebuilt.";
            var sidecar = LoadSidecar(outlineObject);
            if (sidecar == null)
                return "This outline was made before width info was saved. Re-run Generate in the Outline window once to enable resizing.";

            var info = InfoForResize(sourceRenderer, sidecar);
            if (info.mesh == null) return "The source has no mesh.";
            var included = new List<SlotInfo>();
            foreach (var sl in info.slots) if (sl.include) included.Add(sl);
            if (included.Count == 0) return "No material slots were outlined for this object.";
            var s = SettingsFromSidecar(sidecar, sidecar.widthCm, sourceRenderer);

            var result = new Result();
            HullBase b;
            try { b = BuildHullBase(info, included, s, result); }
            catch (Exception e)
            {
                Debug.LogException(e);
                return "Rebuild failed: " + e.Message;
            }
            if (b == null) return "Nothing left to outline with these settings.";

            var p = new WidthPreview
            {
                outlineObject = outlineObject,
                outlineRenderer = outlineRenderer,
                sourceRenderer = sourceRenderer,
                sourceName = sourceObject.name,
                originalMesh = hull,
                basePos = b.hull.pos.ToArray(),
                pushDir = b.pushDir,
                pushFactor = b.pushFactor,
                pushScale = b.pushScale,
                widthMultiplier = sidecar.widthMultiplier,
                widthCm = sidecar.widthCm,
            };
            p.scratch = new Vector3[p.basePos.Length];

            float width = sidecar.widthCm * 0.01f * Mathf.Max(0.01f, sidecar.widthMultiplier);
            for (int i = 0; i < b.hull.pos.Count; i++)
                b.hull.pos[i] = b.hull.pos[i] + b.pushDir[i] * (width * b.pushFactor[i] / b.pushScale[i]);
            p.tempMesh = ToMesh(b.hull, b.sourceMesh, b.skinned);
            p.tempMesh.name = hull.name + " preview";

            var outlineSmr = outlineRenderer as SkinnedMeshRenderer;
            if (outlineSmr != null) p.originalLocalBounds = outlineSmr.localBounds;
            if (outlineSmr != null)
            {
                outlineSmr.sharedMesh = p.tempMesh;
                outlineSmr.localBounds = p.tempMesh.bounds;
                // Follow the original's blend shape weights while previewing, same as the placed outline does.
                var sourceSmr = sourceRenderer as SkinnedMeshRenderer;
                if (sourceSmr != null && sourceSmr.sharedMesh != null)
                {
                    for (int i = 0; i < p.tempMesh.blendShapeCount; i++)
                    {
                        int idx = sourceSmr.sharedMesh.GetBlendShapeIndex(p.tempMesh.GetBlendShapeName(i));
                        if (idx >= 0) outlineSmr.SetBlendShapeWeight(i, sourceSmr.GetBlendShapeWeight(idx));
                    }
                }
            }
            else
            {
                var mf = outlineRenderer.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = p.tempMesh;
            }
            preview = p;
            return null;
        }

        /// <summary>Re-pushes the preview mesh at a new width, live while dragging. Cheap: only the vertices move.</summary>
        public static void SetPreviewWidth(WidthPreview p, float widthCm)
        {
            if (p == null || p.tempMesh == null) return;
            p.widthCm = widthCm;
            float width = widthCm * 0.01f * Mathf.Max(0.01f, p.widthMultiplier);
            var pos = p.scratch;
            for (int i = 0; i < pos.Length; i++)
                pos[i] = p.basePos[i] + p.pushDir[i] * (width * p.pushFactor[i] / p.pushScale[i]);
            p.tempMesh.SetVertices(pos);
            p.tempMesh.RecalculateBounds();
            var smr = p.outlineRenderer as SkinnedMeshRenderer;
            if (smr != null) smr.localBounds = p.tempMesh.bounds;
        }

        /// <summary>Ends a live width preview: the original mesh is always put back, and the temp mesh is destroyed. When commit is true, the final width is saved with a full rebuild, like Resize Outline.</summary>
        public static string EndWidthPreview(WidthPreview p, bool commit)
        {
            if (p == null) return null;
            var outlineRenderer = p.outlineRenderer;
            if (outlineRenderer != null)
            {
                var smr = outlineRenderer as SkinnedMeshRenderer;
                if (smr != null)
                {
                    smr.sharedMesh = p.originalMesh;
                    smr.localBounds = p.originalLocalBounds;
                }
                else
                {
                    var mf = outlineRenderer.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = p.originalMesh;
                }
            }
            if (p.tempMesh != null) Object.DestroyImmediate(p.tempMesh);
            p.tempMesh = null;
            if (!commit) return null;
            return ResizeOutline(p.outlineObject, p.widthCm, p.widthMultiplier);
        }

        /// <summary>Builds a RendererInfo for one renderer, with slot includes, width and color taken from its sidecar.</summary>
        static RendererInfo InfoForResize(Renderer source, OutlineSidecar sidecar)
        {
            Mesh mesh = MeshOf(source);
            var info = new RendererInfo
            {
                renderer = source,
                mesh = mesh,
                include = true,
                overrideColor = sidecar.overrideColor,
                color = new Color(sidecar.colorR, sidecar.colorG, sidecar.colorB, sidecar.colorA),
                widthMultiplier = sidecar.widthMultiplier,
            };
            if (mesh == null) return info;
            var mats = source.sharedMaterials;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var desc = mesh.GetSubMesh(i);
                var slot = new SlotInfo
                {
                    index = i,
                    material = i < mats.Length ? mats[i] : null,
                    triangles = desc.topology == MeshTopology.Triangles ? (int)(desc.indexCount / 3) : 0,
                };
                slot.include = slot.triangles > 0 && sidecar.includedSlots != null && Array.IndexOf(sidecar.includedSlots, i) >= 0;
                info.slots.Add(slot);
            }
            return info;
        }

        /// <summary>Rebuilds the hull settings from a sidecar, with the new width. Excluded bones are matched by name against the source's bones.</summary>
        static Settings SettingsFromSidecar(OutlineSidecar sidecar, float newWidthCm, Renderer source)
        {
            var s = new Settings
            {
                widthCm = newWidthCm,
                exactWorldWidth = sidecar.version < 2 ? true : sidecar.exactWorldWidth,
                smoothNormals = sidecar.smoothNormals,
                useVertexColorWidth = sidecar.useVertexColorWidth,
                copyBlendShapes = sidecar.copyBlendShapes,
                blendShapeMinMoveMm = sidecar.blendShapeMinMoveMm,
                weldVertices = sidecar.weldVertices,
                simplifyMm = sidecar.simplifyMm,
                triangleBudget = sidecar.triangleBudget,
            };
            var smr = source as SkinnedMeshRenderer;
            if (smr != null && sidecar.excludedBones != null && sidecar.excludedBones.Length > 0)
            {
                var bones = smr.bones;
                foreach (string name in sidecar.excludedBones)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    foreach (var b in bones)
                        if (b != null && b.name == name) { s.excludedBones.Add(b); break; }
                }
            }
            return s;
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
