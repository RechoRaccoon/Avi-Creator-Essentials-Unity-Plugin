using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Bakes how PC shaders (Poiyomi, lilToon, Standard, and anything else) make an avatar look into plain
    /// textures, then builds Quest materials from them (VRChat/Mobile/Toon Standard by default). The PC avatar is
    /// never changed; the baked textures and Quest materials are new assets, and a Quest copy of the avatar can be
    /// made with the new materials swapped in.
    /// </summary>
    public static class ShaderBaker
    {
        public const string DataFolder = "Assets/Avi Creator Essentials Data/ShaderBake";

        public enum TargetShader { ToonStandard, ToonLit, StandardLite }

        public class Options
        {
            public int resolution = 1024;
            public TargetShader target = TargetShader.ToonStandard;
            public float brightness = 1f;
            public float lightIntensity = 1f;
            public float ambient = 0.55f;
            public bool bakeEmission = true;
            public bool dilate = true;
            public bool makeQuestCopy = true;
            public bool androidOverride = true;
        }

        public class MaterialEntry
        {
            public Renderer renderer;
            public int slot;
            public Material material;
            public Mesh mesh;
            public bool include = true;
            public string kind;
            public string note;
            public int triangles;
            public BakedMaterial baked;
        }

        public class BakedMaterial
        {
            public Material source;
            public Texture2D baseTexture;
            public Texture2D emissionTexture;
            public Material questMaterial;
        }

        public class Result
        {
            public int baked;
            public int skipped;
            public int reused;
            public string folder;
            public GameObject questCopy;
            public string targetShaderName;
            public readonly List<string> warnings = new List<string>();
        }

        /// <summary>Lists every material slot on the avatar, with a best guess at what kind of shader it is.</summary>
        public static List<MaterialEntry> Scan(GameObject root)
        {
            var list = new List<MaterialEntry>();
            if (root == null) return list;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name.EndsWith(OutlineGenerator.Suffix, StringComparison.Ordinal)) continue;
                Mesh mesh = MeshOf(r);
                if (mesh == null) continue;
                bool hasUV = mesh.uv != null && mesh.uv.Length > 0;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    var desc = mesh.GetSubMesh(i);
                    int tris = desc.topology == MeshTopology.Triangles ? (int)(desc.indexCount / 3) : 0;
                    var mat = i < mats.Length ? mats[i] : null;
                    var e = new MaterialEntry { renderer = r, slot = i, material = mat, mesh = mesh, triangles = tris };
                    e.kind = KindOf(mat);
                    if (mat == null) { e.include = false; e.note = "no material"; }
                    else if (tris == 0) { e.include = false; e.note = "not triangles"; }
                    else if (!hasUV) { e.include = false; e.note = "mesh has no UVs"; }
                    else if (e.kind == "Mobile") { e.include = false; e.note = "already a Quest shader"; }
                    else if (IsTransparent(mat)) e.note = "transparent: Quest has no real transparency, the bake keeps its alpha";
                    list.Add(e);
                }
            }
            return list;
        }

        /// <summary>Bakes every included entry and, if asked, makes a Quest copy of the avatar.</summary>
        public static Result Bake(GameObject root, List<MaterialEntry> entries, Options o)
        {
            var result = new Result();
            if (root == null) return result;
            string folder = DataFolder + "/" + APUtil.SafeFileName(root.name);
            APUtil.EnsureAssetFolder(folder);
            result.folder = folder;

            var rig = new ShaderBakeRender.BakeRig
            {
                lightIntensity = o.lightIntensity,
                ambient = new Color(o.ambient, o.ambient, o.ambient),
            };
            var targetShader = ResolveTargetShader(o.target, result);

            var byMaterial = new Dictionary<Material, BakedMaterial>();
            int total = 0;
            foreach (var e in entries) if (e.include && e.material != null) total++;
            int done = 0;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Bake shaders for Quest");
            int group = Undo.GetCurrentGroup();
            try
            {
                foreach (var e in entries)
                {
                    if (!e.include || e.material == null || e.renderer == null) continue;
                    done++;
                    EditorUtility.DisplayProgressBar("Baking shaders", e.renderer.name + " / " + e.material.name, done / (float)Mathf.Max(1, total));
                    BakedMaterial baked;
                    if (byMaterial.TryGetValue(e.material, out baked))
                    {
                        e.baked = baked;
                        result.reused++;
                        continue;
                    }
                    baked = BakeOne(e, folder, o, rig, targetShader, result);
                    if (baked == null) { result.skipped++; continue; }
                    byMaterial[e.material] = baked;
                    e.baked = baked;
                    result.baked++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var meshesByMaterial = new Dictionary<Material, HashSet<Mesh>>();
            foreach (var e in entries)
            {
                if (!e.include || e.material == null) continue;
                HashSet<Mesh> set;
                if (!meshesByMaterial.TryGetValue(e.material, out set)) { set = new HashSet<Mesh>(); meshesByMaterial[e.material] = set; }
                set.Add(e.mesh);
            }
            foreach (var kv in meshesByMaterial)
                if (kv.Value.Count > 1)
                    result.warnings.Add("Material \"" + kv.Key.name + "\" is used by " + kv.Value.Count +
                        " different meshes. It was baked once, from the first mesh, so it only matches that mesh's UVs.");

            if (o.makeQuestCopy) result.questCopy = BuildQuestCopy(root, entries, result);
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return result;
        }

        static BakedMaterial BakeOne(MaterialEntry e, string folder, Options o, ShaderBakeRender.BakeRig rig, Shader targetShader, Result result)
        {
            string baseName = APUtil.SafeFileName(e.renderer.name) + "_s" + e.slot + "_" + APUtil.SafeFileName(e.material.name);
            var pair = ShaderBakeRender.Bake(e.material, e.mesh, e.slot, o.resolution, rig, out string warning);
            if (pair == null)
            {
                result.warnings.Add(e.renderer.name + " / " + e.material.name + ": " + warning);
                return null;
            }
            if (!string.IsNullOrEmpty(warning)) result.warnings.Add(e.renderer.name + " / " + e.material.name + ": " + warning);
            try
            {
                if (o.dilate) ShaderBakeRender.Dilate(pair.baseTex, pair.covered, 8);
                if (o.dilate && pair.emission != null) ShaderBakeRender.Dilate(pair.emission, pair.covered, 8);
                if (!Mathf.Approximately(o.brightness, 1f)) ShaderBakeRender.Multiply(pair.baseTex, o.brightness);

                string basePath = folder + "/" + baseName + "_Baked.png";
                var baseTex = ShaderBakeRender.SavePNG(pair.baseTex, basePath, o.resolution, o.androidOverride);
                Texture2D emissionTex = null;
                if (o.bakeEmission && pair.emission != null)
                {
                    string emPath = folder + "/" + baseName + "_Emission.png";
                    emissionTex = ShaderBakeRender.SavePNG(pair.emission, emPath, o.resolution, o.androidOverride);
                }
                var questMat = targetShader != null
                    ? MakeQuestMaterial(e.material, baseTex, emissionTex, folder, baseName, targetShader)
                    : null;
                return new BakedMaterial { source = e.material, baseTexture = baseTex, emissionTexture = emissionTex, questMaterial = questMat };
            }
            finally
            {
                Object.DestroyImmediate(pair.baseTex);
                if (pair.emission != null) Object.DestroyImmediate(pair.emission);
            }
        }

        /// <summary>Finds the requested Quest shader, falling back down the VRChat/Mobile list and then to Standard.</summary>
        static Shader ResolveTargetShader(TargetShader target, Result result)
        {
            string want = target == TargetShader.ToonStandard ? "VRChat/Mobile/Toon Standard"
                : target == TargetShader.ToonLit ? "VRChat/Mobile/Toon Lit" : "VRChat/Mobile/Standard Lite";
            var shader = Shader.Find(want);
            if (shader != null) { result.targetShaderName = want; return shader; }
            foreach (var name in new[] { "VRChat/Mobile/Toon Lit", "VRChat/Mobile/Standard Lite", "Standard" })
            {
                shader = Shader.Find(name);
                if (shader != null)
                {
                    result.targetShaderName = name;
                    result.warnings.Add("Quest shader \"" + want + "\" was not found in this project (the VRChat SDK may not be imported), so \"" + name + "\" was used instead.");
                    return shader;
                }
            }
            result.targetShaderName = null;
            result.warnings.Add("No usable Quest shader was found in this project. Import the VRChat SDK and bake again.");
            return null;
        }

        static Material MakeQuestMaterial(Material source, Texture2D baseTex, Texture2D emissionTex, string folder, string baseName, Shader targetShader)
        {
            string path = folder + "/" + baseName + "_Quest.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            var m = new Material(targetShader) { name = source.name + " (Quest)" };
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", baseTex);
            foreach (var p in new[] { "_Color", "_BaseColor", "_MainColor" })
                if (m.HasProperty(p)) { m.SetColor(p, Color.white); break; }
            var normal = FindNormalMap(source);
            if (normal != null && m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", normal);
            if (emissionTex != null)
            {
                if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", emissionTex);
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.white);
                m.EnableKeyword("_EMISSION");
            }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        /// <summary>Duplicates the avatar and swaps the baked Quest materials into the matching slots.</summary>
        static GameObject BuildQuestCopy(GameObject root, List<MaterialEntry> entries, Result result)
        {
            var copy = Object.Instantiate(root);
            copy.name = root.name + "_Quest";
            Undo.RegisterCreatedObjectUndo(copy, "Make Quest copy");
            var srcRenderers = root.GetComponentsInChildren<Renderer>(true);
            var dstRenderers = copy.GetComponentsInChildren<Renderer>(true);
            var byRenderer = new Dictionary<Renderer, List<MaterialEntry>>();
            foreach (var e in entries)
            {
                if (e.baked == null || e.baked.questMaterial == null) continue;
                List<MaterialEntry> list;
                if (!byRenderer.TryGetValue(e.renderer, out list)) { list = new List<MaterialEntry>(); byRenderer[e.renderer] = list; }
                list.Add(e);
            }
            int swapped = 0;
            for (int i = 0; i < srcRenderers.Length && i < dstRenderers.Length; i++)
            {
                List<MaterialEntry> list;
                if (!byRenderer.TryGetValue(srcRenderers[i], out list)) continue;
                var mats = dstRenderers[i].sharedMaterials;
                foreach (var e in list)
                {
                    if (e.slot < mats.Length) { mats[e.slot] = e.baked.questMaterial; swapped++; }
                }
                dstRenderers[i].sharedMaterials = mats;
            }
            result.warnings.Add(swapped + " material slot(s) swapped on the Quest copy. It still needs a manual Quest pass: " +
                "remove or replace Quest-rejected components (PhysBones, constraints, PC-only scripts) and switch the build target to Android before uploading.");
            return copy;
        }

        // ------------------------------------------------------------------ helpers

        static string KindOf(Material mat)
        {
            if (mat == null || mat.shader == null) return "?";
            string n = mat.shader.name;
            if (n.StartsWith("VRChat/Mobile", StringComparison.Ordinal)) return "Mobile";
            if (n.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0) return "Poiyomi";
            if (n.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) >= 0) return "lilToon";
            if (n.IndexOf("standard", StringComparison.OrdinalIgnoreCase) >= 0) return "Standard";
            return mat.shader.name;
        }

        static bool IsTransparent(Material mat)
        {
            if (mat.renderQueue >= 2450) return true;
            string rt = mat.GetTag("RenderType", true, "");
            return rt == "Transparent" || rt == "TransparentCutout";
        }

        static Texture FindNormalMap(Material mat)
        {
            foreach (var slot in ShaderMapping.TextureSlots(mat))
                if (slot.isNormalMap && slot.texture != null) return slot.texture;
            return mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
        }

        static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }
    }
}
