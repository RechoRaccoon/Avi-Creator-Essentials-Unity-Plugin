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
            public bool expandSilhouette = true;
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
            public bool cancelled;
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

        /// <summary>Remembers an object's local transform so a tool run can never leave it moved.</summary>
        class TransformGuard
        {
            readonly Transform m_T;
            readonly Vector3 m_Pos, m_Scale;
            readonly Quaternion m_Rot;

            public TransformGuard(Transform t)
            {
                m_T = t;
                if (t == null) return;
                m_Pos = t.localPosition; m_Rot = t.localRotation; m_Scale = t.localScale;
            }

            /// <summary>Puts the transform back if anything moved it. Returns true when it had to.</summary>
            public bool Restore()
            {
                if (m_T == null) return false;
                bool changed = (m_T.localPosition - m_Pos).sqrMagnitude > 1e-10f
                               || Quaternion.Angle(m_T.localRotation, m_Rot) > 0.001f
                               || (m_T.localScale - m_Scale).sqrMagnitude > 1e-10f;
                if (!changed) return false;
                Undo.RecordObject(m_T, "Restore avatar position");
                m_T.localPosition = m_Pos; m_T.localRotation = m_Rot; m_T.localScale = m_Scale;
                return true;
            }
        }

        static string UniqueBase(string folder, string baseName, string tail, HashSet<string> used)
        {
            string b = baseName;
            int i = 2;
            while (!used.Add(folder + "/" + b + tail)) b = baseName + "_" + (i++);
            return b;
        }

        static List<SlotInfo> IncludedSlots(RendererInfo info)
        {
            var included = new List<SlotInfo>();
            foreach (var sl in info.slots) if (sl.include) included.Add(sl);
            return included;
        }

        public static Result Generate(GameObject root, List<RendererInfo> infos, Settings s)
        {
            var result = new Result();
            if (root == null) return result;
            if (EditorUtility.IsPersistent(root))
            {
                result.warnings.Add("Use the avatar from the scene (Hierarchy), not the one in the Project window.");
                return result;
            }

            var guard = new TransformGuard(root.transform);
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Add outline");
            int group = Undo.GetCurrentGroup();
            try
            {
                if (s.replaceExisting)
                {
                    RemoveAll(root);
                    foreach (var inf in infos) if (inf.renderer != null) inf.mesh = MeshOf(inf.renderer);
                }

                string folder = DataFolder + "/" + Sanitize(root.name);
                EnsureFolder(folder);

                var todo = new List<RendererInfo>();
                foreach (var inf in infos)
                    if (inf.include && inf.renderer != null && inf.mesh != null && IncludedSlots(inf).Count > 0) todo.Add(inf);

                var matCache = new Dictionary<string, Material>();
                var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var links = new List<KeyValuePair<GameObject, GameObject>>();

                for (int n = 0; n < todo.Count; n++)
                {
                    var info = todo[n];
                    var included = IncludedSlots(info);
                    float p = n / (float)todo.Count;
                    string label = info.renderer.name + " (" + (n + 1) + " of " + todo.Count + ")";
                    if (EditorUtility.DisplayCancelableProgressBar("Outline", "Building " + label, p))
                    {
                        result.cancelled = true;
                        break;
                    }

                    Color col = info.overrideColor ? info.color : s.color;
                    var mat = GetMaterial(folder, col, s, matCache);

                    HullData hull;
                    try { hull = BuildHull(info, included, s, result); }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        result.warnings.Add(info.renderer.name + ": " + e.Message);
                        continue;
                    }
                    if (hull == null) { result.warnings.Add(info.renderer.name + ": nothing left to outline"); continue; }

                    EditorUtility.DisplayProgressBar("Outline", "Saving " + label, p + 0.5f / todo.Count);
                    try
                    {
                        string rawName = Sanitize(info.renderer.name);
                        if (s.placement == Placement.SeparateObject)
                        {
                            string baseName = UniqueBase(folder, rawName, Suffix + ".asset", usedPaths);
                            string path = folder + "/" + baseName + Suffix + ".asset";
                            var saved = WriteMesh(hull, path, baseName + Suffix);
                            var outlineGo = PlaceSeparate(info, saved, mat);
                            WriteSidecar(path, root, info, s, included);
                            AssetDatabase.SaveAssetIfDirty(saved);
                            result.bytes += FileSize(path);
                            if (s.linkBlendShapes && saved.blendShapeCount > 0)
                                links.Add(new KeyValuePair<GameObject, GameObject>(outlineGo, info.renderer.gameObject));
                        }
                        else
                        {
                            string baseName = UniqueBase(folder, rawName, "_WithOutline.asset", usedPaths);
                            var merged = Merge(info, hull, mat, folder, baseName, result);
                            if (merged == null) continue;
                            AssetDatabase.SaveAssetIfDirty(merged);
                        }
                        result.renderers++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        result.warnings.Add(info.renderer.name + ": couldn't save the outline (" + e.Message + ")");
                    }
                }

                // The VRCFury blend shape links go in last, in one pass, so a problem with them can never stop outlines from being made.
                if (links.Count > 0 && !result.cancelled)
                {
                    var noted = new HashSet<string>();
                    for (int i = 0; i < links.Count; i++)
                    {
                        if (links[i].Key == null || links[i].Value == null) continue;
                        EditorUtility.DisplayProgressBar("Outline", "Linking blend shapes (" + (i + 1) + " of " + links.Count + ")", i / (float)links.Count);
                        string note;
                        try { note = AddBlendShapeLink(links[i].Key, links[i].Value); }
                        catch (Exception e) { note = "Couldn't add the VRCFury blend shape link: " + e.Message; }
                        if (note != null && noted.Add(note)) result.warnings.Add(note);
                    }
                }

                EditorUtility.DisplayProgressBar("Outline", "Saving assets…", 1f);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (guard.Restore()) result.warnings.Add("The avatar's position was changed during the run, so it was put back.");
                Undo.CollapseUndoOperations(group);
            }
            return result;
        }

        /// <summary>Copies the source's blend shape weights onto every separate outline of the avatar, by name. Returns how many outlines were updated.</summary>
        public static int SyncWeights(GameObject root)
        {
            if (root == null) return 0;
            int n = 0;
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!r.name.EndsWith(Suffix, StringComparison.Ordinal)) continue;
                var parent = r.transform.parent;
                var source = parent != null ? parent.GetComponent<SkinnedMeshRenderer>() : null;
                if (source == null) continue;
                Undo.RecordObject(r, "Match outline blend shapes");
                CopyWeights(r, source);
                n++;
            }
            return n;
        }

        /// <summary>Sets each of dst's blend shape weights to the same-named weight on srcRenderer.</summary>
        static void CopyWeights(SkinnedMeshRenderer dst, SkinnedMeshRenderer srcRenderer)
        {
            if (dst == null || srcRenderer == null) return;
            var dm = dst.sharedMesh; var sm = srcRenderer.sharedMesh;
            if (dm == null || sm == null) return;
            for (int i = 0; i < dm.blendShapeCount; i++)
            {
                int idx = sm.GetBlendShapeIndex(dm.GetBlendShapeName(i));
                if (idx >= 0) dst.SetBlendShapeWeight(i, srcRenderer.GetBlendShapeWeight(idx));
            }
        }

        /// <summary>Removes every outline object this tool made under the avatar, and restores merged meshes.</summary>
        public static int RemoveAll(GameObject root)
        {
            if (root == null) return 0;
            int removed = 0;
            var guard = new TransformGuard(root.transform);
            var toDelete = new List<GameObject>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root.transform && t.name.EndsWith(Suffix, StringComparison.Ordinal) && t.GetComponent<Renderer>() != null) toDelete.Add(t.gameObject);
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
            guard.Restore();
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
            public bool expandSilhouette = true;
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

        /// <summary>Intermediate outline geometry before optimization and writing. Blend shapes are not held here: each hull vertex remembers the source vertex it came from (src), and the deltas are read straight from the source mesh when the mesh is written.</summary>
        class Hull
        {
            public List<Vector3> pos = new List<Vector3>();
            public List<Vector3> nrm = new List<Vector3>();
            public List<BoneWeight> bw = new List<BoneWeight>();
            public List<int> tris = new List<int>();
            public List<int> src = new List<int>();
        }

        /// <summary>A blend shape of the source mesh that the outline follows.</summary>
        class ShapeRef
        {
            public int index;
            public string name;
            public int frames;
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
            public List<ShapeRef> shapes = new List<ShapeRef>();
            public ulong[] shapeHash; // per SOURCE vertex, a fingerprint of how it moves in the followed shapes; null when there are none
        }

        /// <summary>A finished hull, ready to be written into a mesh asset.</summary>
        class HullData
        {
            public Hull hull;
            public Mesh source;
            public bool skinned;
            public List<ShapeRef> shapes;
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
                srcTris.AddRange(mesh.GetTriangles(sl.index));
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

            if (s.expandSilhouette)
            {
                // A pure normal push never widens the silhouette of a flat surface facing the
                // camera: the whole surface just moves toward it, so straight on there is no rim
                // to see, while the side view still shows the hull's edge. Blend each push toward
                // the surface plane, away from the mesh's centre, so flat surfaces grow outward
                // as well. The blended direction stays unit length, so the total offset is still
                // the width. Curved surfaces barely change: there the radial direction is already
                // along the normal, so the tangent part is near zero.
                Vector3 centre = mesh.bounds.center;
                for (int i = 0; i < verts.Length; i++)
                {
                    if (!alive[i]) continue;
                    Vector3 r = verts[i] - centre;
                    Vector3 t = r - dir[i] * Vector3.Dot(r, dir[i]);
                    if (t.sqrMagnitude > 1e-10f)
                        dir[i] = (dir[i] + t.normalized).normalized;
                }
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
                ExactPush(info.renderer, mesh, weights, skinned, factor, alive, dir, factor, pushScale);
            }

            // Which vertices are used, and how wide each is. Positions stay unpushed here; the caller pushes them.
            var map = new int[verts.Length];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            var hull = new Hull();
            var pushDir = new List<Vector3>();
            var pushFactor = new List<float>();
            var pushScaleList = new List<float>();
            for (int t = 0; t + 2 < srcTris.Count; t += 3)
            {
                int a = srcTris[t], b = srcTris[t + 1], c = srcTris[t + 2];
                if (!alive[a] && !alive[b] && !alive[c]) continue;
                int na = UseBase(a, map, hull, pushDir, pushFactor, pushScaleList, verts, smooth, dir, factor, pushScale, weights, skinned);
                int nb = UseBase(b, map, hull, pushDir, pushFactor, pushScaleList, verts, smooth, dir, factor, pushScale, weights, skinned);
                int nc = UseBase(c, map, hull, pushDir, pushFactor, pushScaleList, verts, smooth, dir, factor, pushScale, weights, skinned);
                // Flipped winding: this is what turns the copy into an outline.
                hull.tris.Add(na); hull.tris.Add(nc); hull.tris.Add(nb);
            }
            if (hull.tris.Count == 0) return null;

            var baseData = new HullBase
            {
                hull = hull,
                sourceMesh = mesh,
                skinned = skinned,
                pushDir = pushDir.ToArray(),
                pushFactor = pushFactor.ToArray(),
                pushScale = pushScaleList.ToArray(),
            };
            if (s.copyBlendShapes && mesh.blendShapeCount > 0)
            {
                baseData.shapeHash = SelectShapes(mesh, hull.src, scale, s, baseData.shapes);
                result.blendShapes += baseData.shapes.Count;
            }
            return baseData;
        }

        static HullData BuildHull(RendererInfo info, List<SlotInfo> included, Settings s, Result result)
        {
            var b = BuildHullBase(info, included, s, result);
            if (b == null) return null;
            var hull = b.hull;
            float width = s.widthCm * 0.01f * Mathf.Max(0.01f, info.widthMultiplier);
            for (int i = 0; i < hull.pos.Count; i++)
                hull.pos[i] = hull.pos[i] + b.pushDir[i] * (width * b.pushFactor[i] / b.pushScale[i]);

            if (s.weldVertices) Weld(hull, b.shapeHash);
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
            return new HullData { hull = hull, source = b.sourceMesh, skinned = b.skinned, shapes = b.shapes };
        }

        static int UseBase(int v, int[] map, Hull h, List<Vector3> pushDir, List<float> pushFactor, List<float> pushScale,
            Vector3[] verts, Vector3[] smooth, Vector3[] dir, float[] factor, float[] scale, BoneWeight[] bw, bool skinned)
        {
            if (map[v] >= 0) return map[v];
            map[v] = h.pos.Count;
            h.src.Add(v);
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

        /// <summary>Rewrites the per-vertex push so the outline is exactly the requested width in world space: for each live vertex, the local-space vector that moves it by width * f along its world-space push direction. The direction comes in through dir (the smoothed normal, blended toward the silhouette when that is on) and is rewritten in place. Vertices whose skinning matrix can't be inverted keep the average-scale push. The scale array is only used for the blend-shape min-move check, so it is left alone; the push scale is rewritten in place.</summary>
        static void ExactPush(Renderer r, Mesh mesh, BoneWeight[] weights, bool skinned, float[] factor, bool[] alive, Vector3[] dir, float[] outFactor, float[] outScale)
        {
            int count = dir.Length;
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
                if (!ExactPushVector(m, dir[i], factor[i], out eDir, out eFactor, out eScale)) continue;
                dir[i] = eDir;
                outFactor[i] = eFactor;
                outScale[i] = eScale;
            }
        }

        static void AddBone(ref Matrix4x4 m, Matrix4x4[] boneMatrices, int bone, float weight)
        {
            if (weight <= 0f || bone < 0 || bone >= boneMatrices.Length) return;
            AddScaled(ref m, boneMatrices[bone], weight);
        }

        /// <summary>Adds a matrix scaled by a float, element by element. Matrix4x4 has no + operator and no *(Matrix4x4, float) operator (only *(Matrix4x4, Matrix4x4), *(Matrix4x4, Vector4), == and !=), so this must stay element-wise.</summary>
        static void AddScaled(ref Matrix4x4 m, Matrix4x4 a, float s)
        {
            m.m00 += a.m00 * s; m.m01 += a.m01 * s; m.m02 += a.m02 * s; m.m03 += a.m03 * s;
            m.m10 += a.m10 * s; m.m11 += a.m11 * s; m.m12 += a.m12 * s; m.m13 += a.m13 * s;
            m.m20 += a.m20 * s; m.m21 += a.m21 * s; m.m22 += a.m22 * s; m.m23 += a.m23 * s;
            m.m30 += a.m30 * s; m.m31 += a.m31 * s; m.m32 += a.m32 * s; m.m33 += a.m33 * s;
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

        /// <summary>A position rounded to 0.01 mm (when the mesh is in metres), usable as an exact dictionary key.</summary>
        struct IntKey : IEquatable<IntKey>
        {
            public readonly int x, y, z;
            public IntKey(Vector3 v)
            {
                const float q = 100000f;
                x = Mathf.RoundToInt(v.x * q);
                y = Mathf.RoundToInt(v.y * q);
                z = Mathf.RoundToInt(v.z * q);
            }
            public bool Equals(IntKey o) { return x == o.x && y == o.y && z == o.z; }
            public override bool Equals(object o) { return o is IntKey && Equals((IntKey)o); }
            public override int GetHashCode() { unchecked { return (x * 73856093) ^ (y * 19349663) ^ (z * 83492791); } }
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
            var groups = new Dictionary<IntKey, Vector3>(verts.Length);
            var keys = new IntKey[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var k = new IntKey(verts[i]);
                keys[i] = k;
                Vector3 cur;
                groups.TryGetValue(k, out cur);
                groups[k] = cur + acc[i];
            }
            var result = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var n = groups[keys[i]];
                result[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
            }
            return result;
        }

        static float Angle(Vector3 a, Vector3 b)
        {
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(a.normalized, b.normalized), -1f, 1f));
        }

        // ------------------------------------------------------------------ blend shapes

        /// <summary>
        /// Decides which of the source's blend shapes the outline follows (the ones that move it by at least the minimum),
        /// and fingerprints how every source vertex moves in them, so welding never merges two vertices that move differently.
        /// Nothing is copied here: the deltas are read from the source again when the mesh is written, which keeps memory flat
        /// however many blend shapes the avatar has.
        /// </summary>
        static ulong[] SelectShapes(Mesh mesh, List<int> srcOfHull, float[] scale, Settings s, List<ShapeRef> kept)
        {
            int total = mesh.vertexCount;
            int n = srcOfHull.Count;
            var dv = new Vector3[total];
            var dn = new Vector3[total];
            var dt = new Vector3[total];
            var hash = new ulong[total];
            var tmp = new ulong[total];
            float minMove = s.blendShapeMinMoveMm * 0.001f;
            for (int bs = 0; bs < mesh.blendShapeCount; bs++)
            {
                int frames = mesh.GetBlendShapeFrameCount(bs);
                float biggest = 0f;
                for (int f = 0; f < frames; f++)
                {
                    mesh.GetBlendShapeFrameVertices(bs, f, dv, dn, dt);
                    for (int i = 0; i < n; i++)
                    {
                        int v = srcOfHull[i];
                        Vector3 d = dv[v];
                        float m = d.magnitude * scale[v];
                        if (m > biggest) biggest = m;
                        tmp[v] = MixDelta(tmp[v], d, f);
                    }
                }
                if (biggest >= minMove)
                {
                    unchecked
                    {
                        ulong salt = (ulong)(bs + 1) * 0x9E3779B97F4A7C15UL;
                        for (int i = 0; i < n; i++)
                        {
                            int v = srcOfHull[i];
                            hash[v] = (hash[v] ^ (tmp[v] + salt)) * 1099511628211UL;
                        }
                    }
                    kept.Add(new ShapeRef { index = bs, name = mesh.GetBlendShapeName(bs), frames = frames });
                }
                for (int i = 0; i < n; i++) tmp[srcOfHull[i]] = 0UL;
            }
            return hash;
        }

        static ulong MixDelta(ulong h, Vector3 d, int frame)
        {
            unchecked
            {
                h = (h ^ (uint)Mathf.RoundToInt(d.x * 100000f) ^ ((ulong)frame << 40)) * 1099511628211UL;
                h = (h ^ (uint)Mathf.RoundToInt(d.y * 100000f)) * 1099511628211UL;
                h = (h ^ (uint)Mathf.RoundToInt(d.z * 100000f)) * 1099511628211UL;
                return h;
            }
        }

        // ------------------------------------------------------------------ optimization

        /// <summary>Two hull vertices are the same when they sit at the same position with the same skinning and the same blend shape movement.</summary>
        struct WeldKey : IEquatable<WeldKey>
        {
            readonly int x, y, z, b0, b1, b2, b3, w0, w1, w2, w3;
            readonly ulong shape;
            public WeldKey(Vector3 p, BoneWeight w, ulong shape)
            {
                const float q = 100000f;
                x = Mathf.RoundToInt(p.x * q);
                y = Mathf.RoundToInt(p.y * q);
                z = Mathf.RoundToInt(p.z * q);
                w0 = Mathf.RoundToInt(w.weight0 * 10000f);
                w1 = Mathf.RoundToInt(w.weight1 * 10000f);
                w2 = Mathf.RoundToInt(w.weight2 * 10000f);
                w3 = Mathf.RoundToInt(w.weight3 * 10000f);
                b0 = w0 > 0 ? w.boneIndex0 : 0;
                b1 = w1 > 0 ? w.boneIndex1 : 0;
                b2 = w2 > 0 ? w.boneIndex2 : 0;
                b3 = w3 > 0 ? w.boneIndex3 : 0;
                this.shape = shape;
            }
            public bool Equals(WeldKey o)
            {
                return x == o.x && y == o.y && z == o.z && shape == o.shape
                    && b0 == o.b0 && b1 == o.b1 && b2 == o.b2 && b3 == o.b3
                    && w0 == o.w0 && w1 == o.w1 && w2 == o.w2 && w3 == o.w3;
            }
            public override bool Equals(object o) { return o is WeldKey && Equals((WeldKey)o); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);
                    h = h * 31 + b0; h = h * 31 + b1; h = h * 31 + w0; h = h * 31 + w1;
                    h = h * 31 + (int)(shape ^ (shape >> 32));
                    return h;
                }
            }
        }

        /// <summary>Merges vertices at the same position with the same skinning and blend shape movement.</summary>
        static void Weld(Hull h, ulong[] shapeHash)
        {
            int n = h.pos.Count;
            var remap = new int[n];
            var keep = new List<int>(n);
            var map = new Dictionary<WeldKey, int>(n);
            for (int i = 0; i < n; i++)
            {
                var k = new WeldKey(h.pos[i], h.bw[i], shapeHash != null ? shapeHash[h.src[i]] : 0UL);
                int found;
                if (map.TryGetValue(k, out found)) { remap[i] = found; continue; }
                remap[i] = keep.Count;
                map[k] = keep.Count;
                keep.Add(i);
            }
            if (keep.Count == n) return;
            Compact(h, remap, keep);
        }

        struct CellKey : IEquatable<CellKey>
        {
            readonly int x, y, z, bone;
            public CellKey(Vector3 p, float cell, int bone)
            {
                x = Mathf.FloorToInt(p.x / cell);
                y = Mathf.FloorToInt(p.y / cell);
                z = Mathf.FloorToInt(p.z / cell);
                this.bone = bone;
            }
            public bool Equals(CellKey o) { return x == o.x && y == o.y && z == o.z && bone == o.bone; }
            public override bool Equals(object o) { return o is CellKey && Equals((CellKey)o); }
            public override int GetHashCode() { unchecked { return (x * 73856093) ^ (y * 19349663) ^ (z * 83492791) ^ (bone * 83492797); } }
        }

        /// <summary>Vertex clustering: vertices in the same small cell (and with the same main bone) become one.</summary>
        static void Cluster(Hull h, float cell)
        {
            int n = h.pos.Count;
            var remap = new int[n];
            var cellMap = new Dictionary<CellKey, int>(n);
            var keep = new List<int>();
            var sums = new List<Vector3>();
            var counts = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var p = h.pos[i];
                var k = new CellKey(p, cell, MainBone(h.bw[i]));
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
            var src = new List<int>(keep.Count);
            foreach (int i in keep) { pos.Add(h.pos[i]); nrm.Add(h.nrm[i]); bw.Add(h.bw[i]); src.Add(h.src[i]); }
            var tris = new List<int>(h.tris.Count);
            for (int t = 0; t + 2 < h.tris.Count; t += 3)
            {
                int a = remap[h.tris[t]], b = remap[h.tris[t + 1]], c = remap[h.tris[t + 2]];
                if (a == b || b == c || a == c) continue; // collapsed
                tris.Add(a); tris.Add(b); tris.Add(c);
            }
            h.pos = pos; h.nrm = nrm; h.bw = bw; h.src = src; h.tris = tris;
        }

        // ------------------------------------------------------------------ writing

        /// <summary>Writes the hull into a mesh. The mesh is cleared first, so reusing an existing asset leaves no stale data behind.</summary>
        static void FillMesh(Mesh m, HullData d)
        {
            var h = d.hull;
            m.Clear(false);
            m.ClearBlendShapes();
            m.indexFormat = h.pos.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(h.pos);
            m.SetNormals(h.nrm);
            m.subMeshCount = 1;
            m.SetTriangles(h.tris, 0);
            if (d.skinned)
            {
                m.bindposes = d.source.bindposes;
                m.boneWeights = h.bw.ToArray();
            }
            else
            {
                m.bindposes = new Matrix4x4[0];
            }
            if (d.shapes != null && d.shapes.Count > 0) AddShapes(m, d.source, d.shapes, h.src.ToArray());
            m.RecalculateBounds();
        }

        /// <summary>Adds the followed blend shapes to the hull mesh, reading each frame's deltas from the source mesh for just the vertices the hull uses.</summary>
        static void AddShapes(Mesh target, Mesh source, List<ShapeRef> shapes, int[] srcOfHull)
        {
            int total = source.vertexCount;
            var dv = new Vector3[total];
            var dn = new Vector3[total];
            var dt = new Vector3[total];
            var d = new Vector3[srcOfHull.Length];
            foreach (var sh in shapes)
            {
                for (int f = 0; f < sh.frames; f++)
                {
                    source.GetBlendShapeFrameVertices(sh.index, f, dv, dn, dt);
                    for (int i = 0; i < d.Length; i++) d[i] = dv[srcOfHull[i]];
                    target.AddBlendShapeFrame(sh.name, source.GetBlendShapeFrameWeight(sh.index, f), d, null, null);
                }
            }
        }

        /// <summary>Writes the hull to a mesh asset. An existing asset is rebuilt in place (same GUID, so every reference survives); no Undo is recorded for it, because snapshotting a mesh with many blend shapes for Undo is what made big avatars crawl.</summary>
        static Mesh WriteMesh(HullData d, string path, string meshName)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            Mesh m = existing != null ? existing : new Mesh();
            FillMesh(m, d);
            m.name = meshName;
            if (existing == null) AssetDatabase.CreateAsset(m, path);
            else EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Saves what one separate-object outline was built from, next to its hull asset, so its width can be rebuilt later.</summary>
        static void WriteSidecar(string hullAssetPath, GameObject root, RendererInfo info, Settings s, List<SlotInfo> included)
        {
            Color col = info.overrideColor ? info.color : s.color;
            var slots = new List<int>();
            foreach (var sl in included) slots.Add(sl.index);
            var sidecar = new OutlineSidecar
            {
                version = 3,
                exactWorldWidth = s.exactWorldWidth,
                expandSilhouette = s.expandSilhouette,
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
                if (hull.bindposes.Length > 0) r.bones = smr.bones;
                r.rootBone = smr.rootBone;
                // The source's bounds, grown a little for the outline's thickness. (The mesh's own bounds
                // are in bind-pose space, not relative to the root bone, so they must not be used here.)
                var bounds = smr.localBounds;
                bounds.Expand(0.02f);
                r.localBounds = bounds;
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
                CopyWeights(r, smr);
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

        static SerializedProperty FindFirst(SerializedProperty parent, params string[] names)
        {
            if (parent == null) return null;
            foreach (var n in names)
            {
                var p = parent.FindPropertyRelative(n);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>Points a serialized field at a GameObject, or at the renderer / transform on it, depending on what type the field is. Strings get the object's name.</summary>
        static bool AssignRef(SerializedProperty p, GameObject go)
        {
            if (p == null || go == null) return false;
            if (p.propertyType == SerializedPropertyType.ObjectReference)
            {
                string t = p.type ?? "";
                UnityEngine.Object value = go;
                if (t.Contains("SkinnedMeshRenderer")) value = go.GetComponent<SkinnedMeshRenderer>();
                else if (t.Contains("Renderer")) value = go.GetComponent<Renderer>();
                else if (t.Contains("Transform")) value = go.transform;
                if (value == null) return false;
                p.objectReferenceValue = value;
                return p.objectReferenceValue == value;
            }
            if (p.propertyType == SerializedPropertyType.String)
            {
                p.stringValue = go.name;
                return true;
            }
            return false;
        }

        static string DiscardLink(Component comp, string message)
        {
            if (comp != null) Undo.DestroyObjectImmediate(comp);
            return message;
        }

        /// <summary>Adds a VRCFury BlendShape Link to an outline object, so the outline's blend shapes follow the source's at runtime. VRCFury rewrites every animation clip at upload, after its own toggles are baked, to drive the linked mesh's same-named blend shapes. That is the only way to follow toggles: the toggle clips don't exist in the editor, so no editor-time clip edit could cover them. The link lives on the outline object, so Remove outlines takes it away too. It is built defensively: field names and types are checked, and if anything doesn't match VRCFury's model, the half-made component is removed again (a VRCFury component with empty content would only cause errors). Returns null on success, or a short reason it was skipped.</summary>
        public static string AddBlendShapeLink(GameObject outlineObject, GameObject sourceObject)
        {
            if (outlineObject == null || sourceObject == null) return null;
            var outlineSmr = outlineObject.GetComponent<SkinnedMeshRenderer>();
            if (outlineSmr == null || outlineSmr.sharedMesh == null || outlineSmr.sharedMesh.blendShapeCount == 0) return null;
            var vrcFuryType = FindType("VF.Model.VRCFury");
            var linkType = FindType("VF.Model.Feature.BlendShapeLink");
            if (vrcFuryType == null || linkType == null)
                return "VRCFury isn't installed, so the outlines won't follow blend shape toggles at runtime. Install VRCFury, or use merged placement.";

            // Replace any link left on this object (including broken ones from earlier versions).
            var contentField = vrcFuryType.GetField("content");
            foreach (var old in outlineObject.GetComponents(vrcFuryType))
            {
                if (old == null) continue;
                object content = contentField != null ? contentField.GetValue(old) : null;
                if (content == null || linkType.IsInstanceOfType(content)) Undo.DestroyObjectImmediate(old);
            }

            Component comp = null;
            try
            {
                comp = Undo.AddComponent(outlineObject, vrcFuryType);
                if (comp == null) return "Couldn't add a VRCFury component to the outline.";
                var so = new SerializedObject(comp);
                var contentProp = so.FindProperty("content");
                if (contentProp == null) return DiscardLink(comp, "VRCFury's component doesn't have the expected content field, so the blend shape link couldn't be added.");
                contentProp.managedReferenceValue = Activator.CreateInstance(linkType, true);
                so.ApplyModifiedPropertiesWithoutUndo();
                so.Update();

                contentProp = so.FindProperty("content");
                var baseProp = FindFirst(contentProp, "baseObj", "baseSkin", "baseMesh", "baseRenderer");
                var skinsProp = FindFirst(contentProp, "linkSkins", "linkedSkins", "skins");
                if (baseProp == null || skinsProp == null || !skinsProp.isArray)
                    return DiscardLink(comp, "VRCFury's BlendShape Link doesn't have the expected fields, so the blend shape link couldn't be added.");
                if (!AssignRef(baseProp, sourceObject))
                    return DiscardLink(comp, "Couldn't point VRCFury's BlendShape Link at the original mesh.");
                var includeProp = FindFirst(contentProp, "includeAll");
                if (includeProp != null && includeProp.propertyType == SerializedPropertyType.Boolean) includeProp.boolValue = true;
                skinsProp.arraySize = 1;
                var skinProp = FindFirst(skinsProp.GetArrayElementAtIndex(0), "renderer", "skin", "smr");
                if (skinProp == null || skinProp.propertyType != SerializedPropertyType.ObjectReference)
                    return DiscardLink(comp, "VRCFury's BlendShape Link doesn't have the expected fields, so the blend shape link couldn't be added.");
                skinProp.objectReferenceValue = outlineSmr;
                so.ApplyModifiedPropertiesWithoutUndo();

                // Read it back: only keep the link if it really took.
                so.Update();
                var checkSkins = FindFirst(so.FindProperty("content"), "linkSkins", "linkedSkins", "skins");
                var checkSkin = checkSkins != null && checkSkins.arraySize == 1 ? FindFirst(checkSkins.GetArrayElementAtIndex(0), "renderer", "skin", "smr") : null;
                if (checkSkin == null || checkSkin.objectReferenceValue != outlineSmr)
                    return DiscardLink(comp, "The blend shape link didn't save correctly, so it was removed.");
                return null;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return DiscardLink(comp, "Couldn't add the VRCFury blend shape link (" + e.Message + ").");
            }
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
            var included = IncludedSlots(info);
            if (included.Count == 0) return "No material slots were outlined for this object.";
            var s = SettingsFromSidecar(sidecar, newWidthCm, sourceRenderer);

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Resize outline");
            int group = Undo.GetCurrentGroup();
            try
            {
                var result = new Result();
                HullData data;
                try { data = BuildHull(info, included, s, result); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    return "Rebuild failed: " + e.Message;
                }
                if (data == null) return "Nothing left to outline with these settings.";

                // Rebuilt in place, with no Undo snapshot of the mesh (see WriteMesh).
                var saved = WriteMesh(data, hullPath, Path.GetFileNameWithoutExtension(hullPath));
                Undo.RecordObject(outlineRenderer, "Resize outline");
                var outlineSmr = outlineRenderer as SkinnedMeshRenderer;
                if (outlineSmr != null)
                {
                    // Re-assign so the renderer picks up the rebuilt mesh's blend shape list.
                    outlineSmr.sharedMesh = null;
                    outlineSmr.sharedMesh = saved;
                    CopyWeights(outlineSmr, sourceRenderer as SkinnedMeshRenderer);
                }
                sidecar.widthCm = newWidthCm;
                sidecar.widthMultiplier = newWidthMultiplier;
                File.WriteAllText(sidecarPath, JsonUtility.ToJson(sidecar));
                AssetDatabase.SaveAssetIfDirty(saved);
                return null;
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
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
            var included = IncludedSlots(info);
            if (included.Count == 0) return "No material slots were outlined for this object.";
            var s = SettingsFromSidecar(sidecar, sidecar.widthCm, sourceRenderer);
            // The drag preview only needs the shape of the hull, so it skips the blend shapes (the expensive part).
            // The real rebuild when the drag ends includes them.
            s.copyBlendShapes = false;

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
            p.tempMesh = new Mesh();
            FillMesh(p.tempMesh, new HullData { hull = b.hull, source = b.sourceMesh, skinned = b.skinned, shapes = null });
            p.tempMesh.name = hull.name + " preview";

            var outlineSmr = outlineRenderer as SkinnedMeshRenderer;
            if (outlineSmr != null)
            {
                outlineSmr.sharedMesh = p.tempMesh;
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
                    CopyWeights(smr, p.sourceRenderer as SkinnedMeshRenderer);
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
                expandSilhouette = sidecar.version < 3 ? true : sidecar.expandSilhouette,
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
        static Mesh Merge(RendererInfo info, HullData data, Material mat, string folder, string baseName, Result result)
        {
            var src = info.mesh;
            var hull = data.hull;
            string srcPath = AssetDatabase.GetAssetPath(src);
            string path = folder + "/" + baseName + "_WithOutline.asset";
            if (srcPath == path)
            {
                result.warnings.Add(info.renderer.name + ": already uses an outline mesh. Turn on \"Replace existing outlines\" (or Remove outlines first).");
                return null;
            }

            int vCount = src.vertexCount;
            int hCount = hull.pos.Count;
            var smr = info.renderer as SkinnedMeshRenderer;
            BoneWeight[] srcBw = null;
            if (smr != null)
            {
                srcBw = src.boneWeights;
                if (srcBw.Length != vCount)
                {
                    result.warnings.Add(info.renderer.name + ": has no bone weights, can't merge");
                    return null;
                }
            }

            // Rebuilt in place when the asset already exists (same GUID), with no Undo snapshot of the mesh.
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            var combined = existing != null ? existing : new Mesh();
            combined.Clear(false);
            combined.ClearBlendShapes();
            combined.name = baseName + "_WithOutline";
            combined.indexFormat = vCount + hCount > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;

            var verts = new List<Vector3>(src.vertices); verts.AddRange(hull.pos);
            var nrm = new List<Vector3>(); var srcN = src.normals;
            if (srcN.Length == vCount) { nrm.AddRange(srcN); nrm.AddRange(hull.nrm); }
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
                var sm = src.GetSubMesh(i);
                var idx = src.GetIndices(i);
                combined.SetIndices(idx, sm.topology, i, false, 0);
            }
            var hullTris = hull.tris.ToArray();
            for (int i = 0; i < hullTris.Length; i++) hullTris[i] += vCount;
            combined.SetTriangles(hullTris, src.subMeshCount, false);

            if (smr != null)
            {
                combined.bindposes = src.bindposes;
                var bw = new List<BoneWeight>(srcBw);
                bw.AddRange(hull.bw);
                combined.boneWeights = bw.ToArray();
            }

            // Blend shapes: every original shape keeps its deltas, and the followed ones also move the hull part,
            // using the deltas of the source vertex each hull vertex came from.
            var followed = new bool[src.blendShapeCount];
            if (data.shapes != null)
                foreach (var sh in data.shapes) if (sh.index >= 0 && sh.index < followed.Length) followed[sh.index] = true;
            var hullSrc = hull.src.ToArray();
            var srcDv = new Vector3[vCount]; var srcDn = new Vector3[vCount]; var srcDt = new Vector3[vCount];
            var dvAll = new Vector3[vCount + hCount]; var dnAll = new Vector3[vCount + hCount]; var dtAll = new Vector3[vCount + hCount];
            for (int bs = 0; bs < src.blendShapeCount; bs++)
            {
                string name = src.GetBlendShapeName(bs);
                int frames = src.GetBlendShapeFrameCount(bs);
                for (int f = 0; f < frames; f++)
                {
                    src.GetBlendShapeFrameVertices(bs, f, srcDv, srcDn, srcDt);
                    Array.Copy(srcDv, dvAll, vCount); Array.Copy(srcDn, dnAll, vCount); Array.Copy(srcDt, dtAll, vCount);
                    if (followed[bs])
                        for (int i = 0; i < hCount; i++) dvAll[vCount + i] = srcDv[hullSrc[i]];
                    else
                        Array.Clear(dvAll, vCount, hCount);
                    combined.AddBlendShapeFrame(name, src.GetBlendShapeFrameWeight(bs, f), dvAll, dnAll, dtAll);
                }
            }
            combined.bounds = src.bounds;

            if (existing == null) AssetDatabase.CreateAsset(combined, path);
            else EditorUtility.SetDirty(combined);

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
            SetMesh(info.renderer, combined);
            var mats = info.renderer.sharedMaterials;
            var list = new List<Material>(mats);
            while (list.Count < src.subMeshCount) list.Add(null);
            list.Add(mat);
            info.renderer.sharedMaterials = list.ToArray();
            result.bytes += FileSize(path);
            return combined;
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
            m = new Material(shader) { name = "Outline " + key };
            ApplyColor(m, color, s.flatLook, folder, key);
            m.renderQueue = -1;
            // An existing material is overwritten in place, not deleted and recreated, so anything
            // still pointing at it (an outline kept with "Replace existing" off) doesn't go pink.
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(m, existing);
                Object.DestroyImmediate(m);
                m = existing;
                EditorUtility.SetDirty(m);
            }
            else AssetDatabase.CreateAsset(m, path);
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

