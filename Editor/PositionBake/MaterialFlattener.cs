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
    /// Flattens a material into one main texture and one emission texture: the main texture times the main color,
    /// every normal map baked in as bump shading, and the emission map times its color and mask. Textures mapped by
    /// Local Pos / World Pos are sampled the way the shader does, so the result lines up with the avatar's UVs.
    /// </summary>
    public static class MaterialFlattener
    {
        public const string ShaderName = "Hidden/AviCreatorEssentials/Flatten";

        public enum Role { Ignore, Main, Normal, Emission, EmissionMask }

        public class SlotJob
        {
            public MaterialTextureSlot slot;
            public Role role;
            public int space;              // 0 UV0, 1 local position, 2 world position
            public int axisU, axisV = 1;
        }

        public class Job
        {
            public Material material;
            public readonly List<SlotJob> slots = new List<SlotJob>();
            public int size = 4096;
            public float shade = 1f;               // how strongly the bumps' shading goes into the main texture
            public bool makeMaterial = true;       // create "<name> (Flattened)" using the new textures
            public bool useOnAvatar = true;        // and put it on the avatar in the scene
        }

        public class Result
        {
            public string mainPath, emissionPath, materialPath;
        }

        /// <summary>A sensible first guess of what each texture slot is for.</summary>
        public static Role GuessRole(MaterialTextureSlot slot)
        {
            string p = slot.property.ToLowerInvariant();
            if (p == "_maintex" || p == "_basemap" || p == "_basecolormap") return Role.Main;
            if (slot.isNormalMap) return Role.Normal;
            if (p.Contains("emission"))
            {
                if (p.Contains("mask")) return Role.EmissionMask;
                if (p.Contains("scroll") || p.Contains("noise") || p.Contains("audiolink")) return Role.Ignore;
                return Role.Emission;
            }
            return Role.Ignore;
        }

        public static Result Flatten(Job job)
        {
            var mat = job.material;
            var users = PositionBaker.Users(mat);
            if (users.Count == 0) throw new InvalidOperationException("No object in the open scene uses this material. Put the avatar in the scene first.");
            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new InvalidOperationException("The flatten shader is missing. Reimport Avi Creator Essentials.");
            var fm = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            fm.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));
            int size = Mathf.Clamp(job.size, 64, 8192);
            var temps = new List<RenderTexture>();
            RenderTexture RT(GraphicsFormat f, string name)
            {
                var rt = APUtil.CreateRT(size, size, f, name);
                rt.filterMode = FilterMode.Point;
                APUtil.Clear(rt, new Color(0, 0, 0, 0));
                temps.Add(rt);
                return rt;
            }
            var prev = RenderTexture.active;
            var meshes = new List<Mesh>();
            try
            {
                // ---- geometry maps: local position, world position, world normal, world tangent per texel
                foreach (var (r, submesh) in users)
                {
                    var m = GeometryMesh(r, submesh);
                    if (m != null) meshes.Add(m);
                }
                if (meshes.Count == 0) throw new InvalidOperationException("The meshes using this material have no UVs.");
                var geo = new RenderTexture[4];
                for (int o = 0; o < 4; o++)
                {
                    geo[o] = RT(GraphicsFormat.R32G32B32A32_SFloat, "AP Flatten Geo " + o);
                    fm.SetFloat("_Out", o);
                    Graphics.SetRenderTarget(geo[o]);
                    foreach (var m in meshes)
                    {
                        fm.SetPass(0);
                        Graphics.DrawMeshNow(m, Matrix4x4.identity, 0);
                    }
                }

                // ---- sample each texture the way the shader maps it
                RenderTexture Sample(SlotJob sj, bool normal)
                {
                    var outRT = RT(GraphicsFormat.R16G16B16A16_SFloat, "AP Flatten Sample");
                    fm.SetTexture("_PosTex", sj.space == 2 ? geo[1] : geo[0]);
                    fm.SetTexture("_SrcTex", sj.slot.texture);
                    fm.SetVector("_SrcST", sj.slot.st);
                    fm.SetFloat("_Mapping", sj.space > 0 ? 1f : 0f);
                    fm.SetFloat("_AxisU", Mathf.Clamp(sj.axisU, 0, 2));
                    fm.SetFloat("_AxisV", Mathf.Clamp(sj.axisV, 0, 2));
                    fm.SetFloat("_IsNormal", normal ? 1f : 0f);
                    float strength = 1f;
                    if (normal && sj.slot.strengthProperty != null) strength = mat.GetFloat(sj.slot.strengthProperty);
                    fm.SetFloat("_Strength", strength);
                    Graphics.Blit(geo[0], outRT, fm, 1);
                    return outRT;
                }

                RenderTexture mainS = null, normS = null, emS = null, emMaskS = null;
                foreach (var sj in job.slots)
                {
                    switch (sj.role)
                    {
                        case Role.Main:
                            if (mainS == null) mainS = Sample(sj, false);
                            break;
                        case Role.Normal:
                        {
                            var n = Sample(sj, true);
                            if (normS == null) normS = n;
                            else
                            {
                                var c = RT(GraphicsFormat.R16G16B16A16_SFloat, "AP Flatten Normals");
                                fm.SetTexture("_NormA", normS);
                                fm.SetTexture("_NormB", n);
                                Graphics.Blit(n, c, fm, 2);
                                normS = c;
                            }
                            break;
                        }
                        case Role.Emission:
                            if (emS == null) emS = Sample(sj, false);
                            break;
                        case Role.EmissionMask:
                            if (emMaskS == null) emMaskS = Sample(sj, false);
                            break;
                    }
                }

                var coverage = Coverage(geo[0]);
                string folder = OutputFolder(mat);
                bool linear = APUtil.LinearProject;
                var result = new Result();

                // ---- main texture
                var mainOut = RT(GraphicsFormat.R8G8B8A8_UNorm, "AP Flatten Main");
                var color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                fm.SetTexture("_MainS", mainS != null ? (Texture)mainS : Texture2D.whiteTexture);
                fm.SetTexture("_NormS", normS != null ? (Texture)normS : Texture2D.whiteTexture);
                fm.SetTexture("_GeoN", geo[2]);
                fm.SetTexture("_GeoT", geo[3]);
                fm.SetFloat("_HasMain", mainS != null ? 1f : 0f);
                fm.SetFloat("_HasNormal", normS != null ? 1f : 0f);
                fm.SetVector("_MainColor", linear ? color.linear : color);
                fm.SetFloat("_Shade", job.shade);
                fm.SetVector("_LightDir", new Vector3(0.25f, 1f, 0.45f));
                fm.SetFloat("_ToGamma", linear ? 1f : 0f);
                Graphics.Blit(geo[0], mainOut, fm, 3);
                result.mainPath = Save(mainOut, coverage, folder, mat.name + " Flattened Main", false);

                // ---- emission texture; its brightness above 1 stays in the material's emission color
                float emBrightness = 1f;
                if (emS != null)
                {
                    var emColor = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor") : Color.white;
                    float strength = mat.HasProperty("_EmissionStrength") ? mat.GetFloat("_EmissionStrength") : 1f;
                    var lin = (linear ? emColor.linear : emColor) * strength;
                    emBrightness = Mathf.Max(1f, Mathf.Max(lin.r, Mathf.Max(lin.g, lin.b)));
                    var emOut = RT(GraphicsFormat.R8G8B8A8_UNorm, "AP Flatten Emission");
                    fm.SetTexture("_EmS", emS);
                    fm.SetTexture("_EmMaskS", emMaskS != null ? (Texture)emMaskS : Texture2D.whiteTexture);
                    fm.SetFloat("_HasEm", 1f);
                    fm.SetFloat("_HasEmMask", emMaskS != null ? 1f : 0f);
                    fm.SetVector("_EmColor", lin / emBrightness);
                    Graphics.Blit(geo[0], emOut, fm, 4);
                    result.emissionPath = Save(emOut, coverage, folder, mat.name + " Flattened Emission", true);
                }

                if (job.makeMaterial) result.materialPath = MakeMaterial(job, result, emBrightness, users);
                return result;
            }
            finally
            {
                RenderTexture.active = prev;
                foreach (var m in meshes) Object.DestroyImmediate(m);
                foreach (var rt in temps)
                {
                    var t = rt;
                    APUtil.Release(ref t);
                }
                Object.DestroyImmediate(fm);
            }
        }

        /// <summary>
        /// One renderer's submesh in its current pose: world positions, world normals and tangents, UV0 for the layout
        /// and the position the shader calls "local" in UV1.
        /// </summary>
        static Mesh GeometryMesh(Renderer r, int submesh)
        {
            Mesh source;
            var posed = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            Matrix4x4 meshToWorld;
            if (r is SkinnedMeshRenderer smr)
            {
                source = smr.sharedMesh;
                if (source == null) { Object.DestroyImmediate(posed); return null; }
                smr.BakeMesh(posed, true);
                var t = smr.transform;
                meshToWorld = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                source = mf != null ? mf.sharedMesh : null;
                if (source == null) { Object.DestroyImmediate(posed); return null; }
                posed.vertices = source.vertices;
                posed.normals = source.normals;
                posed.tangents = source.tangents;
                meshToWorld = r.transform.localToWorldMatrix;
            }
            var uv = source.uv;
            var v = posed.vertices;
            if (uv == null || uv.Length != v.Length) { Object.DestroyImmediate(posed); return null; }
            var n = posed.normals;
            var tg = posed.tangents;
            var normalM = meshToWorld.inverse.transpose;
            var toLocal = r.worldToLocalMatrix;
            var wv = new Vector3[v.Length];
            var wn = new Vector3[v.Length];
            var wt = new Vector4[v.Length];
            var lp = new List<Vector3>(v.Length);
            bool hasN = n != null && n.Length == v.Length;
            bool hasT = tg != null && tg.Length == v.Length;
            float handed = meshToWorld.determinant < 0f ? -1f : 1f;
            for (int i = 0; i < v.Length; i++)
            {
                wv[i] = meshToWorld.MultiplyPoint3x4(v[i]);
                lp.Add(toLocal.MultiplyPoint3x4(wv[i]));
                wn[i] = hasN ? normalM.MultiplyVector(n[i]).normalized : Vector3.up;
                if (hasT)
                {
                    var d = meshToWorld.MultiplyVector(new Vector3(tg[i].x, tg[i].y, tg[i].z)).normalized;
                    wt[i] = new Vector4(d.x, d.y, d.z, (tg[i].w < 0f ? -1f : 1f) * handed);
                }
                else wt[i] = new Vector4(1, 0, 0, 1);
            }
            var draw = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            draw.vertices = wv;
            draw.normals = wn;
            draw.tangents = wt;
            draw.uv = uv;
            draw.SetUVs(1, lp);
            if (submesh >= source.subMeshCount) submesh = source.subMeshCount - 1;
            draw.SetTriangles(source.GetTriangles(submesh), 0, false);
            Object.DestroyImmediate(posed);
            return draw;
        }

        static bool[] Coverage(RenderTexture posRT)
        {
            var tex = APUtil.ReadToTexture(posRT, TextureFormat.RGBAFloat);
            var px = tex.GetPixels();
            Object.DestroyImmediate(tex);
            var covered = new bool[px.Length];
            for (int i = 0; i < px.Length; i++) covered[i] = px[i].a > 0.5f;
            return covered;
        }

        static string Save(RenderTexture rt, bool[] coverage, string folder, string name, bool emission)
        {
            var tex = APUtil.ReadToTexture(rt);
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            PositionBaker.Dilate(px, (bool[])coverage.Clone(), rt.width, rt.height, emission ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255));
            var outTex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            outTex.SetPixels32(px);
            outTex.Apply(false);
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + APUtil.SafeFileName(name) + ".png");
            File.WriteAllBytes(APUtil.AssetPathToFull(path), outTex.EncodeToPNG());
            Object.DestroyImmediate(outTex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter imp)
            {
                imp.textureType = TextureImporterType.Default;
                imp.sRGBTexture = true;
                imp.maxTextureSize = Mathf.Max(2048, rt.width);
                imp.mipmapEnabled = true;
                imp.SaveAndReimport();
            }
            return path;
        }

        static string OutputFolder(Material mat)
        {
            string matPath = AssetDatabase.GetAssetPath(mat);
            string folder = Path.GetDirectoryName(matPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets", StringComparison.Ordinal))
            {
                folder = APUtil.OutputFolder;
                APUtil.EnsureAssetFolder(folder);
            }
            return folder;
        }

        /// <summary>A copy of the material that uses the flattened textures, with its normal maps and position mappings removed.</summary>
        static string MakeMaterial(Job job, Result result, float emBrightness, List<(Renderer renderer, int submesh)> users)
        {
            var mat = job.material;
            string folder = OutputFolder(mat);
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + APUtil.SafeFileName(mat.name + " (Flattened)") + ".mat");
            var copy = new Material(mat) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(copy, path);
            var main = AssetDatabase.LoadAssetAtPath<Texture2D>(result.mainPath);
            string mainProp = copy.HasProperty("_MainTex") ? "_MainTex" : null;
            foreach (var sj in job.slots)
            {
                if (sj.role == Role.Ignore) continue;
                var prop = sj.slot.property;
                if (sj.role == Role.Main) mainProp = prop;
                // Everything that went into the flattened textures leaves the copy, so nothing is applied twice.
                copy.SetTexture(prop, null);
                copy.SetTextureScale(prop, Vector2.one);
                copy.SetTextureOffset(prop, Vector2.zero);
                if (sj.slot.uvProperty != null) copy.SetFloat(sj.slot.uvProperty, sj.slot.uv0Value);
            }
            if (mainProp != null && main != null)
            {
                copy.SetTexture(mainProp, main);
                copy.SetTextureScale(mainProp, Vector2.one);
                copy.SetTextureOffset(mainProp, Vector2.zero);
            }
            if (copy.HasProperty("_Color")) copy.SetColor("_Color", Color.white);
            if (result.emissionPath != null)
            {
                var em = AssetDatabase.LoadAssetAtPath<Texture2D>(result.emissionPath);
                if (copy.HasProperty("_EmissionMap")) copy.SetTexture("_EmissionMap", em);
                if (copy.HasProperty("_EmissionColor")) copy.SetColor("_EmissionColor", Color.white * (APUtil.LinearProject ? emBrightness : Mathf.LinearToGammaSpace(emBrightness)));
                if (copy.HasProperty("_EmissionStrength")) copy.SetFloat("_EmissionStrength", 1f);
            }
            EditorUtility.SetDirty(copy);
            AssetDatabase.SaveAssets();

            if (job.useOnAvatar)
            {
                foreach (var (r, submesh) in users)
                {
                    Undo.RecordObject(r, "Use flattened material");
                    var mats = r.sharedMaterials;
                    if (submesh < mats.Length) mats[submesh] = copy;
                    r.sharedMaterials = mats;
                }
            }
            return path;
        }
    }
}
