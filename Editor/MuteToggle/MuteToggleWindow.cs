using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>Tools › Avi Creator Essentials › Mute Toggle: show or hide objects when the wearer is muted in VRChat.</summary>
    public class MuteToggleWindow : EditorWindow
    {
        [MenuItem("Tools/Avi Creator Essentials/Mute Toggle…", false, 110)]
        static void FromToolsMenu() { Open(Selection.activeGameObject); }

        [MenuItem("GameObject/Avi Creator Essentials/Mute Toggle For This Avatar…", false, 24)]
        static void FromHierarchy(MenuCommand cmd) { Open(cmd.context as GameObject); }

        [MenuItem("GameObject/Avi Creator Essentials/Mute Toggle For This Avatar…", true)]
        static bool FromHierarchyValid() { return Selection.activeGameObject != null; }

        public static void Open(GameObject root)
        {
            var w = GetWindow<MuteToggleWindow>(false, "Mute Toggle", true);
            w.minSize = new Vector2(520, 480);
            if (root != null) w.SetRoot(root);
            w.Show();
        }

        GameObject m_Root;
        Vector2 m_Scroll;
        readonly List<MuteToggleGenerator.Entry> m_Entries = new List<MuteToggleGenerator.Entry>();
        MuteToggleGenerator.FxInfo m_Fx;
        string m_Report;
        MessageType m_ReportType = MessageType.Info;

        void SetRoot(GameObject root)
        {
            m_Root = root;
            m_Fx = MuteToggleGenerator.Inspect(root);
            m_Report = null;
        }

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            var root = (GameObject)EditorGUILayout.ObjectField("Avatar", m_Root, typeof(GameObject), true);
            if (root != m_Root) SetRoot(root);
            if (m_Root == null)
            {
                EditorGUILayout.HelpBox("Drop your avatar (the object in the scene or Hierarchy) here, or select it and open this window from Tools › Avi Creator Essentials › Mute Toggle.", MessageType.Info);
                return;
            }
            if (m_Fx == null || !m_Fx.hasDescriptor)
            {
                EditorGUILayout.HelpBox("'" + m_Root.name + "' has no VRC Avatar Descriptor. Pick the avatar's root object (the one with the descriptor).", MessageType.Error);
                return;
            }

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            EditorGUILayout.LabelField(
                "Pick objects on the avatar and say whether each one shows while you are muted and while you are not. " +
                "The tool adds a layer to the avatar's FX controller that flips them with VRChat's built-in MuteSelf parameter, " +
                "so nothing is added to your Expression Parameters and it costs no sync budget. Other players will see the change too, " +
                "since VRChat sends your muted state to everyone. It can take a moment to show up for them.",
                EditorStyles.wordWrappedMiniLabel);

            Section("FX layer");
            if (m_Fx.usesDefaultFx)
            {
                EditorGUILayout.HelpBox(
                    "This avatar still uses VRChat's default FX layer. Applying will replace it with a copy of the SDK sample FX controller " +
                    "plus the mute layer, or with a mute-only controller if the sample isn't in this project (then the default face expressions " +
                    "are replaced until you remove this setup).", MessageType.Warning);
            }
            else
            {
                string fx = "FX layer: " + (m_Fx.controllerName ?? "(none)") + " (" + m_Fx.layerCount + (m_Fx.layerCount == 1 ? " layer" : " layers") + ").";
                EditorGUILayout.LabelField(fx + " The mute layer is added to it, nothing else is changed.", EditorStyles.wordWrappedMiniLabel);
            }
            if (m_Fx.hasMuteLayer)
                EditorGUILayout.LabelField("This avatar already has a mute setup from this tool. Applying again replaces it.", EditorStyles.miniLabel);

            Section("Objects to show or hide");
            for (int i = 0; i < m_Entries.Count; i++)
            {
                var e = m_Entries[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    e.target = (GameObject)EditorGUILayout.ObjectField(e.target, typeof(GameObject), true);
                    e.showWhenMuted = EditorGUILayout.ToggleLeft("Show when muted", e.showWhenMuted, GUILayout.Width(125));
                    e.showWhenUnmuted = EditorGUILayout.ToggleLeft("Show when unmuted", e.showWhenUnmuted, GUILayout.Width(140));
                    if (GUILayout.Button("✕", GUILayout.Width(24)))
                    {
                        m_Entries.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add selected objects"))
                {
                    foreach (var go in Selection.gameObjects)
                    {
                        if (go == null || go == m_Root || !go.transform.IsChildOf(m_Root.transform)) continue;
                        bool dup = false;
                        foreach (var e in m_Entries) if (e.target == go) { dup = true; break; }
                        if (!dup) m_Entries.Add(new MuteToggleGenerator.Entry { target = go });
                    }
                }
                if (GUILayout.Button("Add object")) m_Entries.Add(new MuteToggleGenerator.Entry());
            }
            if (m_Entries.Count == 0)
                EditorGUILayout.LabelField("Nothing yet. Select objects on the avatar in the Hierarchy and press Add selected objects.", EditorStyles.miniLabel);

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Remove mute setup", GUILayout.Height(28))) DoRemove();
                if (GUILayout.Button("Apply mute setup", GUILayout.Height(28))) DoApply();
            }
            if (!string.IsNullOrEmpty(m_Report)) EditorGUILayout.HelpBox(m_Report, m_ReportType);
            EditorGUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        void DoApply()
        {
            try
            {
                var r = MuteToggleGenerator.Apply(m_Root, m_Entries);
                string msg = "Mute setup applied: " + r.objects + (r.objects == 1 ? " object" : " objects") + " on the FX controller '" +
                             System.IO.Path.GetFileNameWithoutExtension(r.controllerPath) + "'.";
                if (r.usedExistingController)
                    msg += " The layer was added to your existing FX controller.";
                if (r.usedSampleFx)
                    msg += " The FX controller is a copy of the SDK sample FX controller, so the default face expressions still work.";
                if (r.createdFreshController)
                    msg += " The FX controller only has the mute layer, so the default face expressions are replaced until you remove this setup.";
                if (r.warnings.Count > 0) msg += "\n\n" + string.Join("\n", r.warnings);
                m_Report = msg;
                m_ReportType = r.warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
                m_Fx = MuteToggleGenerator.Inspect(m_Root);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                m_Report = "Mute setup failed: " + e.Message;
                m_ReportType = MessageType.Error;
            }
        }

        void DoRemove()
        {
            bool removed = MuteToggleGenerator.Remove(m_Root);
            m_Report = removed
                ? "Mute setup removed. If the FX controller was made by this tool and is now empty, the avatar is back on VRChat's default FX layer."
                : "No mute setup from this tool was found on the avatar.";
            m_ReportType = MessageType.Info;
            m_Fx = MuteToggleGenerator.Inspect(m_Root);
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }
    }
}
