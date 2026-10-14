using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>"Flatten to main + emission": opened from a material's right-click / ⋮ menu.</summary>
    public class FlattenWindow : EditorWindow
    {
        const string MenuName = "Flatten to Main + Emission Textures (Avi Creator Essentials)…";

        [MenuItem("CONTEXT/Material/" + MenuName, false, 2001)]
        static void FromMaterialMenu(MenuCommand cmd) => Open(cmd.context as Material);

        [MenuItem("Assets/Avi Creator Essentials/" + MenuName, false, 2001)]
        static void FromAssetsMenu() => Open(Selection.activeObject as Material);

        [MenuItem("Assets/Avi Creator Essentials/" + MenuName, true)]
        static bool FromAssetsMenuValid() => Selection.activeObject is Material;

        [MenuItem("Tools/Avi Creator Essentials/Flatten Material…", false, 103)]
        static void FromToolsMenu()
        {
            var mat = Selection.activeObject as Material;
            if (mat == null && Selection.activeGameObject != null)
            {
                var r = Selection.activeGameObject.GetComponentInChildren<Renderer>(true);
                if (r != null) mat = r.sharedMaterial;
            }
            Open(mat);
        }

        public static void Open(Material mat)
        {
            var w = GetWindow<FlattenWindow>(true, "Flatten Material", true);
            w.minSize = new Vector2(480, 380);
            w.SetMaterial(mat);
        }

        Material m_Material;
        readonly List<MaterialFlattener.SlotJob> m_Slots = new List<MaterialFlattener.SlotJob>();
        int m_Users;
        int m_Size = 4096;
        float m_Shade = 1f;
        bool m_MakeMaterial = true;
        bool m_UseOnAvatar = true;
        Vector2 m_Scroll;

        static readonly int[] s_Sizes = { 1024, 2048, 4096, 8192 };
        static readonly string[] s_SizeNames = { "1024", "2048", "4096", "8192" };
        static readonly string[] s_Roles = { "Leave out", "Main color", "Normal map → shading", "Emission", "Emission mask" };
        static readonly string[] s_Spaces = { "UV0", "Local Pos", "World Pos" };
        static readonly string[] s_Axes = { "X", "Y", "Z" };

        void SetMaterial(Material mat)
        {
            m_Material = mat;
            m_Slots.Clear();
            m_Users = 0;
            if (mat == null) return;
            foreach (var slot in ShaderMapping.TextureSlots(mat))
                m_Slots.Add(new MaterialFlattener.SlotJob
                {
                    slot = slot,
                    role = MaterialFlattener.GuessRole(slot),
                    space = slot.space,
                    axisU = slot.axisU,
                    axisV = slot.axisV,
                });
            m_Users = PositionBaker.Users(mat).Count;
            var main = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            if (main != null) m_Size = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(main.width, main.height)), 1024, 8192);
            Repaint();
        }

        void OnFocus()
        {
            if (m_Material != null) m_Users = PositionBaker.Users(m_Material).Count;
        }

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Combines the material's textures into one main texture and one emission texture that line up with the " +
                "avatar's UVs: the main texture × main color with every normal map baked in as shading, and the emission map × " +
                "its color and mask. Effects the shader computes live (decals, color masking, hue shift, matcaps, rim light) " +
                "aren't textures and aren't included.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            var mat = (Material)EditorGUILayout.ObjectField("Material", m_Material, typeof(Material), false);
            if (mat != m_Material) SetMaterial(mat);
            if (m_Material == null)
            {
                EditorGUILayout.HelpBox("Pick the material, or right-click a material in the Inspector and choose \"" + MenuName + "\".", MessageType.Info);
                return;
            }
            if (m_Users == 0)
                EditorGUILayout.HelpBox("No object in the open scene uses this material. Put the avatar in the scene, standing in its normal pose.", MessageType.Warning);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Textures", EditorStyles.boldLabel);
            if (m_Slots.Count == 0) EditorGUILayout.LabelField("This material has no textures.", EditorStyles.miniLabel);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.MaxHeight(240));
            foreach (var sj in m_Slots)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(sj.slot.displayName, sj.slot.texture.name + "  (" + sj.slot.property + ")"), GUILayout.MinWidth(120));
                    sj.role = (MaterialFlattener.Role)EditorGUILayout.Popup((int)sj.role, s_Roles, GUILayout.Width(150));
                    using (new EditorGUI.DisabledScope(sj.role == MaterialFlattener.Role.Ignore))
                    {
                        sj.space = EditorGUILayout.Popup(sj.space, s_Spaces, GUILayout.Width(78));
                        using (new EditorGUI.DisabledScope(sj.space == 0))
                        {
                            sj.axisU = EditorGUILayout.Popup(sj.axisU, s_Axes, GUILayout.Width(32));
                            sj.axisV = EditorGUILayout.Popup(sj.axisV, s_Axes, GUILayout.Width(32));
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            int si = Array.IndexOf(s_Sizes, m_Size);
            si = EditorGUILayout.Popup("Resolution", si < 0 ? 2 : si, s_SizeNames);
            m_Size = s_Sizes[si];
            m_Shade = EditorGUILayout.Slider(new GUIContent("Bump shading", "How strongly the normal maps' bumps are shaded into the main texture"), m_Shade, 0f, 3f);
            m_MakeMaterial = EditorGUILayout.ToggleLeft(new GUIContent("Make a flattened copy of the material",
                "Creates \"<name> (Flattened)\" using the new textures, without the normal maps and textures that went into them. The original isn't changed."), m_MakeMaterial);
            using (new EditorGUI.DisabledScope(!m_MakeMaterial))
                m_UseOnAvatar = EditorGUILayout.ToggleLeft(new GUIContent("Put it on the avatar in the scene", "Undoable with Ctrl+Z"), m_UseOnAvatar);

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(m_Users == 0))
                {
                    if (GUILayout.Button("Flatten", GUILayout.Width(140), GUILayout.Height(26))) Run();
                }
            }
            EditorGUILayout.Space(6);
        }

        void Run()
        {
            var job = new MaterialFlattener.Job
            {
                material = m_Material,
                size = m_Size,
                shade = m_Shade,
                makeMaterial = m_MakeMaterial,
                useOnAvatar = m_MakeMaterial && m_UseOnAvatar,
            };
            job.slots.AddRange(m_Slots);
            try
            {
                EditorUtility.DisplayProgressBar("Flatten Material", "Baking " + m_Material.name + "…", 0.5f);
                var result = MaterialFlattener.Flatten(job);
                var ping = AssetDatabase.LoadAssetAtPath<Object>(result.materialPath ?? result.mainPath);
                if (ping != null) EditorGUIUtility.PingObject(ping);
                ShowNotification(new GUIContent(result.emissionPath != null ? "Made main + emission textures" : "Made the main texture"));
                Debug.Log("Avi Creator Essentials: flattened " + m_Material.name + " → " + result.mainPath +
                          (result.emissionPath != null ? ", " + result.emissionPath : "") + (result.materialPath != null ? ", " + result.materialPath : ""));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Flatten Material", "Flattening failed: " + e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}
