using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>Tools › Avi Creator Essentials › Bake Shaders for Quest: bake how PC shaders make an avatar look into textures, and rebuild it with Quest shaders.</summary>
    public class ShaderBakeWindow : EditorWindow
    {
        [MenuItem("Tools/Avi Creator Essentials/Bake Shaders for Quest…", false, 106)]
        static void FromToolsMenu() { Open(Selection.activeGameObject); }

        [MenuItem("GameObject/Avi Creator Essentials/Bake Shaders for Quest…", false, 24)]
        static void FromHierarchy(MenuCommand cmd) { Open(cmd.context as GameObject); }

        [MenuItem("GameObject/Avi Creator Essentials/Bake Shaders for Quest…", true)]
        static bool FromHierarchyValid() { return Selection.activeGameObject != null; }

        public static void Open(GameObject root)
        {
            var w = GetWindow<ShaderBakeWindow>(false, "Bake Shaders for Quest", true);
            w.minSize = new Vector2(560, 620);
            if (root != null) w.SetRoot(root);
            w.Show();
        }

        GameObject m_Root;
        Vector2 m_Scroll;
        List<ShaderBaker.MaterialEntry> m_Entries = new List<ShaderBaker.MaterialEntry>();
        readonly ShaderBaker.Options m_Opts = new ShaderBaker.Options();
        string m_Report;
        MessageType m_ReportType = MessageType.Info;
        GameObject m_LastQuestCopy;

        static readonly string[] s_Resolutions = { "512", "1024", "2048" };
        static readonly int[] s_ResolutionValues = { 512, 1024, 2048 };
        static readonly string[] s_Targets = { "VRChat/Mobile/Toon Standard (closest to PC)", "VRChat/Mobile/Toon Lit (simplest)", "VRChat/Mobile/Standard Lite" };

        void SetRoot(GameObject root)
        {
            m_Root = root;
            m_Entries = ShaderBaker.Scan(root);
            m_Report = null;
            m_LastQuestCopy = null;
        }

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            var root = (GameObject)EditorGUILayout.ObjectField("Avatar", m_Root, typeof(GameObject), true);
            if (root != m_Root) SetRoot(root);
            if (m_Root == null)
            {
                EditorGUILayout.HelpBox("Drop your PC avatar (the object in the scene or Hierarchy) here, or select it and open this window from Tools › Avi Creator Essentials › Bake Shaders for Quest.", MessageType.Info);
                return;
            }
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            EditorGUILayout.LabelField(
                "Bakes how each material looks with its PC shader (Poiyomi, lilToon, Standard) into a plain texture, " +
                "then builds a Quest material from it, so a Quest copy of the avatar looks closer to the PC one. Your PC avatar is never changed. " +
                "The bake freezes one light and one view direction into the texture, so view-dependent effects (matcap, rim light, specular) and animated effects " +
                "(hue shift, scrolling emission, AudioLink) bake at their current values and will not move afterwards. " +
                "The Quest shader still adds its own lighting on top, so the result is closer to the PC look, not identical.",
                EditorStyles.wordWrappedMiniLabel);

            Section("Settings");
            int ri = Array.IndexOf(s_ResolutionValues, m_Opts.resolution);
            if (ri < 0) ri = 1;
            ri = EditorGUILayout.Popup("Bake resolution", ri, s_Resolutions);
            m_Opts.resolution = s_ResolutionValues[ri];
            m_Opts.target = (ShaderBaker.TargetShader)EditorGUILayout.Popup("Quest shader", (int)m_Opts.target, s_Targets);
            m_Opts.brightness = EditorGUILayout.Slider(new GUIContent("Brightness", "Multiplies the baked texture. Toon Lit brightens with ambient light, so try 0.83 if the Quest copy looks washed out."), m_Opts.brightness, 0.5f, 1.2f);
            m_Opts.lightIntensity = EditorGUILayout.Slider(new GUIContent("Bake light", "Strength of the fixed directional light the bake uses. The bake freezes this light into the texture."), m_Opts.lightIntensity, 0f, 2f);
            m_Opts.ambient = EditorGUILayout.Slider(new GUIContent("Bake ambient", "Soft light from all sides in the bake. Higher values wash out the toon shading, lower values make the dark side black."), m_Opts.ambient, 0f, 1f);
            m_Opts.bakeEmission = EditorGUILayout.ToggleLeft(new GUIContent("Bake emission into its own texture", "Glow is baked separately so the Quest shader can still glow it. Switched off, glow stays in the main texture and will not glow."), m_Opts.bakeEmission);
            m_Opts.dilate = EditorGUILayout.ToggleLeft(new GUIContent("Spread colors past UV island edges", "Stops dark seams where mipmaps blend in empty texels. Leave on."), m_Opts.dilate);
            m_Opts.makeQuestCopy = EditorGUILayout.ToggleLeft(new GUIContent("Make a Quest copy of the avatar", "Duplicates the avatar as <name>_Quest with the baked materials swapped in. Your PC avatar is left alone."), m_Opts.makeQuestCopy);
            m_Opts.androidOverride = EditorGUILayout.ToggleLeft(new GUIContent("Android override on baked textures (ASTC 6x6)", "Sets the Android platform on the new textures, which is what Quest builds use."), m_Opts.androidOverride);

            Section("Materials to bake");
            if (m_Entries.Count == 0) EditorGUILayout.LabelField("No meshes found on this avatar.", EditorStyles.miniLabel);
            foreach (var e in m_Entries)
            {
                if (e.renderer == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = e.renderer.name + " / " + (e.material != null ? e.material.name : "(no material)") + "  ·  " + e.kind + "  ·  " + e.triangles + " tris";
                    e.include = EditorGUILayout.ToggleLeft(label, e.include, GUILayout.MinWidth(120));
                    if (!string.IsNullOrEmpty(e.note)) EditorGUILayout.LabelField(e.note, EditorStyles.miniLabel, GUILayout.Width(220));
                    if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                    {
                        Selection.activeGameObject = e.renderer.gameObject;
                        EditorGUIUtility.PingObject(e.renderer.gameObject);
                    }
                }
            }

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Bake shaders for Quest", GUILayout.Height(30))) DoBake();
            if (m_LastQuestCopy != null)
            {
                EditorGUILayout.Space(4);
                if (GUILayout.Button("Select the Quest copy", GUILayout.Height(26)))
                {
                    Selection.activeGameObject = m_LastQuestCopy;
                    EditorGUIUtility.PingObject(m_LastQuestCopy);
                }
            }
            if (!string.IsNullOrEmpty(m_Report)) EditorGUILayout.HelpBox(m_Report, m_ReportType);
            EditorGUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        void DoBake()
        {
            try
            {
                var r = ShaderBaker.Bake(m_Root, m_Entries, m_Opts);
                m_LastQuestCopy = r.questCopy;
                m_Report = "Baked " + r.baked + " material(s)" + (r.reused > 0 ? ", reused " + r.reused + " shared one(s)" : "") +
                    (r.skipped > 0 ? ", skipped " + r.skipped : "") + ". Textures and Quest materials are in " + r.folder + "." +
                    (r.questCopy != null ? " Quest copy: " + r.questCopy.name + "." : "") +
                    (r.targetShaderName != null ? " Quest shader: " + r.targetShaderName + "." : "") +
                    (r.warnings.Count > 0 ? "\n\n" + string.Join("\n", r.warnings) : "");
                m_ReportType = r.warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogException(e);
                m_Report = "Bake failed: " + e.Message;
                m_ReportType = MessageType.Error;
            }
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }
    }
}
