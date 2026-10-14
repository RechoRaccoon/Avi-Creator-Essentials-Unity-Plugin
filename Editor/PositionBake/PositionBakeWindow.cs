using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>
    /// "Bake Local Pos to UV": opened from a material's right-click / ⋮ menu. Lists the material's textures, marks
    /// those its shader maps by Local Pos or World Pos, and bakes them into ordinary UV textures.
    /// </summary>
    public class PositionBakeWindow : EditorWindow
    {
        const string MenuName = "Bake Local Pos to UV (Avi Creator Essentials)…";

        [MenuItem("CONTEXT/Material/" + MenuName, false, 2000)]
        static void FromMaterialMenu(MenuCommand cmd) => Open(cmd.context as Material);

        [MenuItem("Assets/Avi Creator Essentials/" + MenuName, false, 2000)]
        static void FromAssetsMenu() => Open(Selection.activeObject as Material);

        [MenuItem("Assets/Avi Creator Essentials/" + MenuName, true)]
        static bool FromAssetsMenuValid() => Selection.activeObject is Material;

        [MenuItem("Tools/Avi Creator Essentials/Bake Local Pos to UV…", false, 102)]
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
            var w = GetWindow<PositionBakeWindow>(true, "Bake Local Pos to UV", true);
            w.minSize = new Vector2(440, 320);
            w.SetMaterial(mat);
        }

        class Row
        {
            public MaterialTextureSlot slot;
            public bool bake;
            public int space;      // 0 skip, 1 local, 2 world
            public int axisU, axisV;
        }

        Material m_Material;
        readonly List<Row> m_Rows = new List<Row>();
        int m_Users;
        int m_Size = 4096;
        bool m_BakeStrength = true;
        bool m_Apply = true;
        Vector2 m_Scroll;

        static readonly int[] s_Sizes = { 1024, 2048, 4096, 8192 };
        static readonly string[] s_SizeNames = { "1024", "2048", "4096", "8192" };
        static readonly string[] s_Spaces = { "Don't bake", "Local Pos", "World Pos" };
        static readonly string[] s_Axes = { "X", "Y", "Z" };

        void SetMaterial(Material mat)
        {
            m_Material = mat;
            m_Rows.Clear();
            m_Users = 0;
            if (mat == null) return;
            foreach (var slot in ShaderMapping.TextureSlots(mat))
                m_Rows.Add(new Row { slot = slot, bake = slot.space > 0, space = slot.space, axisU = slot.axisU, axisV = slot.axisV });
            m_Users = PositionBaker.Users(mat).Count;
            // Default to the resolution of the material's main texture.
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
                "Bakes textures your shader maps by Local Pos or World Pos into normal UV textures. The result looks exactly " +
                "like the shader shows it now (with the avatar standing still), but stays stuck to the model when it moves, " +
                "and works with any shader.", EditorStyles.wordWrappedMiniLabel);
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
            else
                EditorGUILayout.LabelField("Used by " + m_Users + (m_Users == 1 ? " mesh" : " meshes") + " in the scene.", EditorStyles.miniLabel);
            if (m_Material.shader != null && m_Material.shader.name.IndexOf("Locked", StringComparison.OrdinalIgnoreCase) >= 0)
                EditorGUILayout.HelpBox("This material is locked (optimized). Baking works, but unlock it before letting the plugin change its settings.", MessageType.Info);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Textures", EditorStyles.boldLabel);
            if (m_Rows.Count == 0) EditorGUILayout.LabelField("This material has no textures.", EditorStyles.miniLabel);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.MaxHeight(220));
            foreach (var row in m_Rows)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.bake = EditorGUILayout.ToggleLeft(row.slot.displayName + "  (" + row.slot.texture.name + ")", row.bake && row.space > 0, GUILayout.MinWidth(180));
                    if (row.bake && row.space == 0) row.space = 1;
                    row.space = EditorGUILayout.Popup(row.space, s_Spaces, GUILayout.Width(90));
                    if (row.space == 0) row.bake = false;
                    using (new EditorGUI.DisabledScope(row.space == 0))
                    {
                        GUILayout.Label("U", GUILayout.Width(12));
                        row.axisU = EditorGUILayout.Popup(row.axisU, s_Axes, GUILayout.Width(34));
                        GUILayout.Label("V", GUILayout.Width(12));
                        row.axisV = EditorGUILayout.Popup(row.axisV, s_Axes, GUILayout.Width(34));
                    }
                }
                if (row.slot.space > 0)
                    EditorGUILayout.LabelField("      Set to " + row.slot.SpaceName + " in the material" + (row.slot.isNormalMap ? " · normal map" : ""), EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            int si = Array.IndexOf(s_Sizes, m_Size);
            si = EditorGUILayout.Popup(new GUIContent("Resolution", "Size of the baked texture. Fine patterns like cardboard need 4096."), si < 0 ? 2 : si, s_SizeNames);
            m_Size = s_Sizes[si];
            m_BakeStrength = EditorGUILayout.ToggleLeft(new GUIContent("Put the normal strength into the file",
                "The material's normal strength (e.g. 10) is baked in, and the material's slider is set to 1, so the map looks the same anywhere."), m_BakeStrength);
            m_Apply = EditorGUILayout.ToggleLeft(new GUIContent("Use the baked texture on this material",
                "Puts the new texture in the slot and switches the slot back to UV0 (undoable). Off: only the file is created."), m_Apply);

            GUILayout.FlexibleSpace();
            int count = 0;
            foreach (var row in m_Rows) if (row.bake && row.space > 0) count++;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(count == 0 || m_Users == 0))
                {
                    if (GUILayout.Button(count <= 1 ? "Bake" : "Bake " + count + " textures", GUILayout.Width(140), GUILayout.Height(26)))
                        BakeAll();
                }
            }
            EditorGUILayout.Space(6);
        }

        void BakeAll()
        {
            var made = new List<string>();
            try
            {
                int n = 0;
                foreach (var row in m_Rows)
                {
                    if (!row.bake || row.space == 0) continue;
                    EditorUtility.DisplayProgressBar("Bake Local Pos to UV", "Baking " + row.slot.texture.name + "…", n++ / (float)m_Rows.Count);
                    made.Add(PositionBaker.Bake(new PositionBaker.Job
                    {
                        material = m_Material,
                        slot = row.slot,
                        space = row.space,
                        axisU = row.axisU,
                        axisV = row.axisV,
                        size = m_Size,
                        bakeStrength = m_BakeStrength,
                        applyToMaterial = m_Apply,
                    }));
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Bake Local Pos to UV", "Baking failed: " + e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (made.Count == 0) return;
            var first = AssetDatabase.LoadAssetAtPath<Texture2D>(made[0]);
            if (first != null) EditorGUIUtility.PingObject(first);
            ShowNotification(new GUIContent("Baked " + made.Count + (made.Count == 1 ? " texture" : " textures")));
            Debug.Log("Avi Creator Essentials: baked " + string.Join(", ", made));
            SetMaterial(m_Material);
        }
    }
}
