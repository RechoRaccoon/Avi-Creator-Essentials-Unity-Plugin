using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Renders a mesh in UV space: the mesh is flattened so its UVs become screen positions, then drawn with its
    /// real material into a texture. Because the real shader runs, the texture holds the albedo, decals, toon
    /// shading, matcap, rim light and emission exactly as that shader draws them, for one fixed light rig and one
    /// fixed view direction. That is the honest limit of baking: view-dependent and animated effects are frozen at
    /// the baked values, and the lighting in the texture will not change with the world.
    /// </summary>
    public static class ShaderBakeRender
    {
        /// <summary>Fixed light rig for a bake. The same rig is used for every material, so the baked lighting is consistent.</summary>
        public class BakeRig
        {
            /// <summary>Direction the light comes from. Default is front, above, and to the right.</summary>
            public Vector3 lightDir = new Vector3(0.3f, 0.8f, 0.6f);
            public float lightIntensity = 1f;
            /// <summary>Soft light from all sides. Higher values wash out the toon shading, lower values make the dark side black.</summary>
            public Color ambient = new Color(0.55f, 0.55f, 0.55f);
        }

        /// <summary>One baked material: the lit look with emission switched off, and the glow on its own.</summary>
        public class BakedPair
        {
            public Texture2D baseTex;
            public Texture2D emission;
            public float maxEmission;
            public bool[] covered;
        }

        /// <summary>
        /// Bakes one material slot. Returns null when the mesh has no UVs. The caller owns the returned textures
        /// and must destroy them after saving.
        /// </summary>
        public static BakedPair Bake(Material material, Mesh mesh, int submesh, int size, BakeRig rig, out string warning)
        {
            warning = null;
            Vector4 st = MainST(material);
            var flat = BuildFlatMesh(mesh, submesh, st, out Bounds bounds, out string uvWarning);
            if (flat == null)
            {
                warning = "This mesh has no UVs, so nothing can be baked from it.";
                return null;
            }
            if (!string.IsNullOrEmpty(uvWarning)) warning = uvWarning;

            int layer = FindFreeLayer();
            Texture2D look = null, noEmission = null;
            Material noEmissionMat = null;
            try
            {
                look = RenderOnce(material, flat, bounds, size, rig, layer);
                noEmissionMat = ZeroEmission(material);
                noEmission = RenderOnce(noEmissionMat, flat, bounds, size, rig, layer);
            }
            finally
            {
                if (noEmissionMat != null) Object.DestroyImmediate(noEmissionMat);
                Object.DestroyImmediate(flat);
            }
            if (look == null || noEmission == null)
            {
                if (look != null) Object.DestroyImmediate(look);
                if (noEmission != null) Object.DestroyImmediate(noEmission);
                warning = "The bake render came back empty.";
                return null;
            }

            var pair = new BakedPair { baseTex = noEmission, covered = Coverage(noEmission) };
            pair.emission = Subtract(look, noEmission, out float max);
            pair.maxEmission = max;
            Object.DestroyImmediate(look);
            if (max < 0.02f)
            {
                Object.DestroyImmediate(pair.emission);
                pair.emission = null;
            }
            return pair;
        }

        /// <summary>Pixels the mesh actually drew, from the alpha channel. Used to spread colors past UV island edges.</summary>
        static bool[] Coverage(Texture2D tex)
        {
            var px = tex.GetPixels32();
            var cov = new bool[px.Length];
            for (int i = 0; i < px.Length; i++) cov[i] = px[i].a > 8;
            return cov;
        }

        /// <summary>Full render minus the no-emission render, clamped at zero. The subtraction is done in sRGB space, so it is an approximation.</summary>
        static Texture2D Subtract(Texture2D full, Texture2D noEmission, out float maxDelta)
        {
            int w = full.width, h = full.height;
            var a = full.GetPixels32();
            var b = noEmission.GetPixels32();
            var px = new Color32[a.Length];
            maxDelta = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                int r = a[i].r - b[i].r; if (r < 0) r = 0;
                int g = a[i].g - b[i].g; if (g < 0) g = 0;
                int bl = a[i].b - b[i].b; if (bl < 0) bl = 0;
                px[i] = new Color32((byte)r, (byte)g, (byte)bl, 255);
                float m = Mathf.Max(r, Mathf.Max(g, bl)) / 255f;
                if (m > maxDelta) maxDelta = m;
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

        /// <summary>Spreads each covered texel's color a few pixels outwards, so mipmaps do not bleed black in at UV island edges.</summary>
        public static void Dilate(Texture2D tex, bool[] covered, int iterations)
        {
            if (tex == null || covered == null) return;
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels32();
            var cov = (bool[])covered.Clone();
            for (int it = 0; it < iterations; it++)
            {
                bool any = false;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (cov[i]) continue;
                        int r = 0, g = 0, b = 0, n = 0;
                        if (x > 0 && cov[i - 1]) { r += px[i - 1].r; g += px[i - 1].g; b += px[i - 1].b; n++; }
                        if (x + 1 < w && cov[i + 1]) { r += px[i + 1].r; g += px[i + 1].g; b += px[i + 1].b; n++; }
                        if (y > 0 && cov[i - w]) { r += px[i - w].r; g += px[i - w].g; b += px[i - w].b; n++; }
                        if (y + 1 < h && cov[i + w]) { r += px[i + w].r; g += px[i + w].g; b += px[i + w].b; n++; }
                        if (n == 0) continue;
                        px[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 0);
                        cov[i] = true;
                        any = true;
                    }
                }
                if (!any) break;
            }
            tex.SetPixels32(px);
            tex.Apply(false);
        }

        /// <summary>Multiplies the RGB channels by k. Used for the brightness setting.</summary>
        public static void Multiply(Texture2D tex, float k)
        {
            if (tex == null || Mathf.Approximately(k, 1f)) return;
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                px[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(px[i].r * k), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(px[i].g * k), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(px[i].b * k), 0, 255),
                    px[i].a);
            }
            tex.SetPixels32(px);
            tex.Apply(false);
        }

        /// <summary>Writes a texture to a PNG asset and sets sensible import settings. Returns the imported texture.</summary>
        public static Texture2D SavePNG(Texture2D tex, string assetPath, int maxSize, bool androidOverride)
        {
            string full = APUtil.AssetPathToFull(assetPath);
            File.WriteAllBytes(full, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(assetPath);
            var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp != null)
            {
                imp.sRGBTexture = true;
                imp.maxTextureSize = maxSize;
                imp.textureCompression = TextureImporterCompression.Compressed;
                if (androidOverride)
                {
                    var ps = imp.GetPlatformTextureSettings("Android");
                    ps.overridden = true;
                    ps.maxTextureSize = Mathf.Min(maxSize, 1024);
                    ps.format = TextureImporterFormat.ASTC_6x6;
                    ps.compressionQuality = 50;
                    imp.SetPlatformTextureSettings(ps);
                }
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        // ------------------------------------------------------------------ flattening

        /// <summary>
        /// Builds a copy of one submesh with its UVs as positions, so a camera framing [-1, 1] sees the mesh laid
        /// out in UV space. Normals, tangents and UV channels are kept as they are, so the material's shader
        /// evaluates exactly as it would on the model. Every triangle is emitted in both windings: a triangle whose
        /// UV winding faces away from the camera would otherwise be culled and leave a hole. For single-sided
        /// shaders one of the two copies is always culled, so the result is exact; for double-sided shaders the
        /// back-facing copy can shade with flipped normals, which is noted in the report.
        /// </summary>
        static Mesh BuildFlatMesh(Mesh src, int submesh, Vector4 st, out Bounds bounds, out string warning)
        {
            warning = null;
            bounds = new Bounds();
            var uv = src.uv;
            if (uv == null || uv.Length == 0) return null;
            var tris = src.GetTriangles(submesh);
            if (tris == null || tris.Length == 0) return null;

            int n = src.vertexCount;
            var pos = new Vector3[n];
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float u = uv[i].x * st.x + st.z;
                float v = uv[i].y * st.y + st.w;
                pos[i] = new Vector3(u * 2f - 1f, v * 2f - 1f, 0f);
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            if (maxU - minU > 2.01f || maxV - minV > 2.01f)
                warning = "The UVs repeat outside the 0-1 range, so each repeat gets fewer pixels in the bake.";

            var mesh = new Mesh();
            if (n > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = pos;
            var nrm = src.normals;
            if (nrm != null && nrm.Length == n) mesh.normals = nrm;
            var tan = src.tangents;
            if (tan != null && tan.Length == n) mesh.tangents = tan;
            mesh.uv = uv;
            var uv2 = src.uv2;
            if (uv2 != null && uv2.Length == n) mesh.uv2 = uv2;

            var all = new int[tris.Length * 2];
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                all[t] = tris[t + 2]; all[t + 1] = tris[t + 1]; all[t + 2] = tris[t];
                int o = tris.Length + t;
                all[o] = tris[t]; all[o + 1] = tris[t + 1]; all[o + 2] = tris[t + 2];
            }
            mesh.triangles = all;
            mesh.RecalculateBounds();
            bounds = mesh.bounds;
            return mesh;
        }

        /// <summary>Scale and offset of the material's main texture, so the flattened mesh covers the tiled UV range.</summary>
        static Vector4 MainST(Material mat)
        {
            string prop = null;
            if (mat.HasProperty("_MainTex")) prop = "_MainTex";
            else if (mat.shader != null)
            {
                int count = mat.shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    if (mat.shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    prop = mat.shader.GetPropertyName(i);
                    break;
                }
            }
            if (prop == null) return new Vector4(1, 1, 0, 0);
            var s = mat.GetTextureScale(prop);
            var o = mat.GetTextureOffset(prop);
            return new Vector4(s.x, s.y, o.x, o.y);
        }

        /// <summary>A copy of the material with every emission property switched off, so a second render shows the look without glow.</summary>
        static Material ZeroEmission(Material src)
        {
            var copy = new Material(src);
            var shader = src.shader;
            if (shader == null) return copy;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                if (name.IndexOf("emission", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var type = shader.GetPropertyType(i);
                if (type == ShaderPropertyType.Color) copy.SetColor(name, Color.black);
                else if (type == ShaderPropertyType.Texture) copy.SetTexture(name, null);
                else if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range) copy.SetFloat(name, 0f);
            }
            return copy;
        }

        /// <summary>First layer with no name in the project, so the bake camera only ever sees the flattened mesh.</summary>
        static int FindFreeLayer()
        {
            for (int i = 8; i < 32; i++)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return 31;
        }

        // ------------------------------------------------------------------ rendering

        static Texture2D RenderOnce(Material mat, Mesh flat, Bounds bounds, int size, BakeRig rig, int layer)
        {
            var meshGo = new GameObject("ACE Bake Mesh") { hideFlags = HideFlags.HideAndDontSave };
            meshGo.layer = layer;
            var mf = meshGo.AddComponent<MeshFilter>();
            mf.sharedMesh = flat;
            var mr = meshGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var camGo = new GameObject("ACE Bake Camera") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.orthographic = true;
            cam.aspect = 1f;
            cam.nearClipPlane = 9f;
            cam.farClipPlane = 11f;
            cam.cullingMask = 1 << layer;
            float half = Mathf.Max(bounds.size.x, bounds.size.y) * 0.5f * 1.005f;
            cam.orthographicSize = Mathf.Max(0.001f, half);
            cam.transform.position = new Vector3(bounds.center.x, bounds.center.y, -10f);
            cam.transform.LookAt(bounds.center);

            var lightGo = new GameObject("ACE Bake Light") { hideFlags = HideFlags.HideAndDontSave };
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = rig.lightIntensity;
            Vector3 dir = rig.lightDir.sqrMagnitude > 1e-6f ? rig.lightDir.normalized : Vector3.up;
            light.transform.rotation = Quaternion.LookRotation(-dir);

            var format = APUtil.LinearProject ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
            var rt = APUtil.CreateRT(size, size, format, "bake");
            cam.targetTexture = rt;

            var prevAmbientMode = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            var prevAmbientIntensity = RenderSettings.ambientIntensity;
            var prevFog = RenderSettings.fog;
            Texture2D result = null;
            try
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = rig.ambient;
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.fog = false;
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                result = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                result.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                result.Apply(false);
                RenderTexture.active = prev;
            }
            finally
            {
                RenderSettings.ambientMode = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                RenderSettings.ambientIntensity = prevAmbientIntensity;
                RenderSettings.fog = prevFog;
                APUtil.Release(ref rt);
                Object.DestroyImmediate(meshGo);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
            }
            return result;
        }
    }
}
