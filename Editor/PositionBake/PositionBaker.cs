using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Bakes a texture that a material maps by position (Poiyomi's "Local Pos" / "World Pos" UV modes) into a normal
    /// UV-mapped texture: every texel of the result holds what the shader showed at that spot on the model as it
    /// stands now, so the texture looks the same but stays stuck to the model when it moves.
    /// </summary>
    public static class PositionBaker
    {
        public const string ShaderName = "Hidden/AviCreatorEssentials/PositionBake";

        public class Job
        {
            public Material material;
            public MaterialTextureSlot slot;
            public int space = 1;              // 1 local, 2 world
            public int axisU, axisV = 1;
            public int size = 4096;
            public bool bakeStrength = true;   // normal maps: put the material's strength into the file
            public bool applyToMaterial = true;
        }

        /// <summary>Renderers in the open scenes that draw this material, with the submeshes that use it.</summary>
        public static List<(Renderer renderer, int submesh)> Users(Material mat)
        {
            var list = new List<(Renderer, int)>();
            if (mat == null) return list;
            foreach (var r in Object.FindObjectsOfType<Renderer>(true))
            {
                if (r.hideFlags != HideFlags.None || EditorUtility.IsPersistent(r)) continue;
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] == mat) list.Add((r, i));
            }
            return list;
        }

        /// <summary>Runs the bake and returns the asset path of the new texture (or throws with a readable message).</summary>
        public static string Bake(Job job)
        {
            var mat = job.material;
            var slot = job.slot;
            var users = Users(mat);
            if (users.Count == 0)
                throw new InvalidOperationException("No object in the open scene uses this material. Put the avatar in the scene first.");
            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new InvalidOperationException("The bake shader is missing. Reimport Avi Creator Essentials.");
            var bm = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            int size = Mathf.Clamp(job.size, 64, 8192);

            var posRT = APUtil.CreateRT(size, size, GraphicsFormat.R32G32B32A32_SFloat, "AP Position Bake");
            var outRT = APUtil.CreateRT(size, size, GraphicsFormat.R8G8B8A8_UNorm, "AP Position Bake Out");
            var prev = RenderTexture.active;
            try
            {
                APUtil.Clear(posRT, new Color(0, 0, 0, 0));
                bm.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));

                // ---- position map: the positions the avatar's shader sees, laid out by UV0
                var temp = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                try
                {
                    foreach (var (r, submesh) in users)
                        DrawPositions(r, submesh, job.space == 2, bm, posRT, temp);
                }
                finally
                {
                    Object.DestroyImmediate(temp);
                }

                // ---- sample the texture through the position map, exactly like the shader's Local/World Pos mode
                var src = slot.texture;
                bm.SetTexture("_PosTex", posRT);
                bm.SetTexture("_SrcTex", src);
                bm.SetVector("_SrcST", slot.st);
                bm.SetFloat("_AxisU", Mathf.Clamp(job.axisU, 0, 2));
                bm.SetFloat("_AxisV", Mathf.Clamp(job.axisV, 0, 2));
                bm.SetFloat("_IsNormal", slot.isNormalMap ? 1f : 0f);
                float strength = 1f;
                if (slot.isNormalMap && job.bakeStrength && slot.strengthProperty != null) strength = mat.GetFloat(slot.strengthProperty);
                bm.SetFloat("_Strength", strength);
                bm.SetFloat("_ToGamma", !slot.isNormalMap && APUtil.LinearProject && APUtil.IsSRGB(src) ? 1f : 0f);
                posRT.filterMode = FilterMode.Point;
                Graphics.Blit(posRT, outRT, bm, 1);

                // ---- read back, grow the islands outwards so seams and mip levels never show the empty background
                var colorTex = APUtil.ReadToTexture(outRT);
                var px = colorTex.GetPixels32();
                Object.DestroyImmediate(colorTex);
                var posTex = APUtil.ReadToTexture(posRT, TextureFormat.RGBAFloat);
                var pos = posTex.GetPixels();
                Object.DestroyImmediate(posTex);
                var covered = new bool[px.Length];
                int count = 0;
                for (int i = 0; i < px.Length; i++)
                    if (pos[i].a > 0.5f) { covered[i] = true; count++; }
                if (count == 0) throw new InvalidOperationException("Nothing was drawn. Check that the avatar's mesh has UVs.");
                Dilate(px, covered, size, size, slot.isNormalMap ? new Color32(128, 128, 255, 255) : new Color32(0, 0, 0, 0));

                // ---- save next to the original texture (or in the plugin's output folder for read-only packages)
                string path = OutputPath(mat, slot);
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
                tex.SetPixels32(px);
                tex.Apply(false);
                File.WriteAllBytes(APUtil.AssetPathToFull(path), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                if (AssetImporter.GetAtPath(path) is TextureImporter imp)
                {
                    imp.textureType = slot.isNormalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    imp.sRGBTexture = !slot.isNormalMap;
                    imp.maxTextureSize = Mathf.Max(2048, size);
                    imp.mipmapEnabled = true;
                    imp.SaveAndReimport();
                }

                if (job.applyToMaterial) Apply(job, AssetDatabase.LoadAssetAtPath<Texture2D>(path), strength != 1f);
                return path;
            }
            finally
            {
                RenderTexture.active = prev;
                APUtil.Release(ref posRT);
                APUtil.Release(ref outRT);
                Object.DestroyImmediate(bm);
            }
        }

        /// <summary>
        /// Draws one renderer's submesh at its UV0 coordinates, writing the vertex positions as the avatar's shader sees
        /// them. The skinned mesh is taken in its current pose; "local" is the renderer's own space as Unity draws it.
        /// </summary>
        static void DrawPositions(Renderer r, int submesh, bool world, Material bm, RenderTexture target, Mesh temp)
        {
            Mesh source;
            Matrix4x4 meshToWorld;
            if (r is SkinnedMeshRenderer smr)
            {
                if (smr.sharedMesh == null) return;
                smr.BakeMesh(temp, true);
                source = smr.sharedMesh;
                var t = smr.transform;
                meshToWorld = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return;
                source = mf.sharedMesh;
                temp.Clear();
                temp.vertices = source.vertices;
                meshToWorld = r.transform.localToWorldMatrix;
            }
            if (submesh >= source.subMeshCount) submesh = source.subMeshCount - 1;
            var uv = source.uv;
            var v = temp.vertices;
            if (uv == null || uv.Length != v.Length) return;

            // Into the space the shader calls local: what Unity uses as this renderer's object-to-world matrix.
            var toSpace = world ? meshToWorld : r.worldToLocalMatrix * meshToWorld;
            for (int i = 0; i < v.Length; i++) v[i] = toSpace.MultiplyPoint3x4(v[i]);

            var draw = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            try
            {
                draw.vertices = v;
                draw.uv = uv;
                draw.SetTriangles(source.GetTriangles(submesh), 0, false);
                bm.SetFloat("_UseWorld", 0f);
                Graphics.SetRenderTarget(target);
                bm.SetPass(0);
                Graphics.DrawMeshNow(draw, Matrix4x4.identity, 0);
            }
            finally
            {
                Object.DestroyImmediate(draw);
            }
        }

        /// <summary>Copies the nearest covered texel into empty texels, up to 64 texels out, then fills the rest.</summary>
        internal static void Dilate(Color32[] px, bool[] covered, int w, int h, Color32 background)
        {
            const int maxDist = 64;
            var dist = new byte[px.Length];
            var queue = new Queue<int>();
            for (int i = 0; i < px.Length; i++)
            {
                if (!covered[i]) continue;
                int x = i % w, y = i / w;
                if ((x > 0 && !covered[i - 1]) || (x < w - 1 && !covered[i + 1]) || (y > 0 && !covered[i - w]) || (y < h - 1 && !covered[i + w]))
                    queue.Enqueue(i);
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % w, y = i / w;
                int d = dist[i] + 1;
                if (d > maxDist) continue;
                void Visit(int j)
                {
                    if (covered[j]) return;
                    covered[j] = true;
                    px[j] = px[i];
                    dist[j] = (byte)d;
                    queue.Enqueue(j);
                }
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            for (int i = 0; i < px.Length; i++)
                if (!covered[i]) px[i] = background;
        }

        static string OutputPath(Material mat, MaterialTextureSlot slot)
        {
            string srcPath = AssetDatabase.GetAssetPath(slot.texture);
            string folder = Path.GetDirectoryName(srcPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets", StringComparison.Ordinal))
            {
                folder = APUtil.OutputFolder;
                APUtil.EnsureAssetFolder(folder);
            }
            string name = APUtil.SafeFileName(slot.texture.name + " (" + mat.name + " baked UV)");
            return AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + ".png");
        }

        /// <summary>Puts the baked texture in the slot and switches the slot back to plain UV0 mapping (undoable).</summary>
        static void Apply(Job job, Texture2D baked, bool strengthBaked)
        {
            var mat = job.material;
            var slot = job.slot;
            if (baked == null) return;
            Undo.RecordObject(mat, "Use baked texture");
            mat.SetTexture(slot.property, baked);
            mat.SetTextureScale(slot.property, Vector2.one);
            mat.SetTextureOffset(slot.property, Vector2.zero);
            if (slot.uvProperty != null) mat.SetFloat(slot.uvProperty, slot.uv0Value);
            if (strengthBaked && slot.strengthProperty != null) mat.SetFloat(slot.strengthProperty, 1f);
            EditorUtility.SetDirty(mat);
        }
    }
}
