using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>A texture slot of a material, and how its shader maps it onto the model.</summary>
    public class MaterialTextureSlot
    {
        public string property;
        public string displayName;
        public Texture texture;
        public int space;               // 0 normal UVs, 1 local position, 2 world position
        public int axisU, axisV = 1;
        public Vector4 st = new Vector4(1, 1, 0, 0);

        public string SpaceName => space == 1 ? "Local Pos" : space == 2 ? "World Pos" : null;
    }

    /// <summary>
    /// Reads texture slots and their position-based mapping (Poiyomi's Local Pos / World Pos UV modes) from a
    /// material, so a wrap layer can bake exactly what the shader shows.
    /// </summary>
    public static class ShaderMapping
    {
        public static List<MaterialTextureSlot> TextureSlots(Material mat)
        {
            var list = new List<MaterialTextureSlot>();
            if (mat == null || mat.shader == null) return list;
            var shader = mat.shader;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0) continue;
                string prop = shader.GetPropertyName(i);
                var tex = mat.GetTexture(prop);
                if (tex == null) continue;
                var slot = new MaterialTextureSlot
                {
                    property = prop,
                    displayName = CleanName(shader.GetPropertyDescription(i), prop),
                    texture = tex,
                };
                var scale = mat.GetTextureScale(prop);
                var offset = mat.GetTextureOffset(prop);
                slot.st = new Vector4(scale.x, scale.y, offset.x, offset.y);
                ReadMapping(mat, prop, slot);
                list.Add(slot);
            }
            return list;
        }

        /// <summary>Poiyomi-style "&lt;texture&gt;UV" dropdowns: finds whether the slot uses Local Pos or World Pos and which axes.</summary>
        static void ReadMapping(Material mat, string prop, MaterialTextureSlot slot)
        {
            var shader = mat.shader;
            int uvIndex = shader.FindPropertyIndex(prop + "UV");
            if (uvIndex < 0 || !mat.HasProperty(prop + "UV")) return;
            int value = Mathf.RoundToInt(mat.GetFloat(prop + "UV"));
            foreach (var (name, v) in EnumOptions(shader.GetPropertyAttributes(uvIndex)))
            {
                if (v != value) continue;
                string n = Normalize(name);
                if (n == "localpos" || n == "localposition") slot.space = 1;
                else if (n == "worldpos" || n == "worldposition") slot.space = 2;
                break;
            }
            if (slot.space == 0) return;

            // Which position axes become u and v (Poiyomi: "_UVModLocalPos0/1", "_UVModWorldPos0/1"); X and Y otherwise.
            string key = slot.space == 1 ? "localpos" : "worldpos";
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string pn = shader.GetPropertyName(i);
                string norm = Normalize(pn);
                if (!norm.Contains(key)) continue;
                var type = shader.GetPropertyType(i);
                if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range && type != ShaderPropertyType.Int) continue;
                int axis = Mathf.Clamp(Mathf.RoundToInt(mat.GetFloat(pn)), 0, 2);
                if (norm.EndsWith("0")) slot.axisU = axis;
                else if (norm.EndsWith("1")) slot.axisV = axis;
            }
        }

        /// <summary>Name/value pairs from an enum attribute such as Enum(UV0,0,UV1,1,Local Pos,6) or ThryWideEnum(...).</summary>
        static IEnumerable<(string name, int value)> EnumOptions(string[] attributes)
        {
            if (attributes == null) yield break;
            foreach (var a in attributes)
            {
                if (a.IndexOf("Enum", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int open = a.IndexOf('('), close = a.LastIndexOf(')');
                if (open < 0 || close <= open) continue;
                var parts = a.Substring(open + 1, close - open - 1).Split(',');
                if (parts.Length < 2) continue;
                for (int i = 0; i + 1 < parts.Length; i += 2)
                    if (int.TryParse(parts[i + 1].Trim(), out int v))
                        yield return (parts[i].Trim(), v);
            }
        }

        static string Normalize(string s) => s.Replace(" ", "").Replace("_", "").ToLowerInvariant();

        static string CleanName(string description, string prop)
        {
            if (string.IsNullOrEmpty(description)) return prop.TrimStart('_');
            // Poiyomi/Thry descriptions carry layout tags like "Main Texture--{reference_property:...}".
            int cut = description.IndexOf("--", StringComparison.Ordinal);
            if (cut > 0) description = description.Substring(0, cut);
            return description.Trim();
        }

        /// <summary>
        /// A readable copy of a texture holding gamma values, the form image layers use. PNG/JPG assets are read from
        /// their files; imported normal maps are unpacked to plain RGB normals.
        /// </summary>
        public static Texture2D ToImage(Texture tex, out bool isNormalMap)
        {
            isNormalMap = false;
            if (tex == null) return null;
            string path = AssetDatabase.GetAssetPath(tex);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            isNormalMap = importer != null && importer.textureType == TextureImporterType.NormalMap;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            Texture2D result = null;
            if (!isNormalMap && (ext == ".png" || ext == ".jpg" || ext == ".jpeg"))
                result = APUtil.LoadImageFile(APUtil.AssetPathToFull(path));
            if (result == null && isNormalMap)
            {
                var rt = APUtil.CreateRT(tex.width, tex.height, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, "unpack");
                var comp = APResources.Composite;
                var prev = RenderTexture.active;
                comp.SetFloat(APIds.CopyMode, 4f);
                comp.SetFloat(APIds.SrcToGamma, 0f);
                Graphics.Blit(tex, rt, comp, CompPass.Copy);
                comp.SetFloat(APIds.CopyMode, 0f);
                RenderTexture.active = prev;
                var read = APUtil.ReadToTexture(rt);
                APUtil.Release(ref rt);
                result = new Texture2D(read.width, read.height, TextureFormat.RGBA32, true, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Repeat,
                };
                result.SetPixels32(read.GetPixels32());
                result.Apply(true);
                Object.DestroyImmediate(read);
            }
            if (result == null) result = APUtil.ToGammaTexture(tex);
            if (result != null) result.name = tex.name;
            return result;
        }
    }
}
