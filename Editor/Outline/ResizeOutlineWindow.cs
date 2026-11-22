using System;
using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>Right-click an outline (or the object it outlines) in the Hierarchy to change just that outline's width.</summary>
    public class ResizeOutlineWindow : EditorWindow
    {
        [MenuItem("GameObject/Avi Creator Essentials/Resize Outline…", false, 23)]
        static void FromHierarchy(MenuCommand cmd) { Open(cmd.context as GameObject); }

        [MenuItem("GameObject/Avi Creator Essentials/Resize Outline…", true)]
        static bool FromHierarchyValid() { return FindOutline(Selection.activeGameObject) != null; }

        static GameObject FindOutline(GameObject selection)
        {
            if (selection == null) return null;
            if (IsOutline(selection)) return selection;
            foreach (Transform child in selection.transform)
                if (IsOutline(child.gameObject)) return child.gameObject;
            return null;
        }

        static bool IsOutline(GameObject go)
        {
            return go != null && go.name.EndsWith(OutlineGenerator.Suffix, StringComparison.Ordinal) && go.GetComponent<Renderer>() != null;
        }

        public static void Open(GameObject selection)
        {
            var outline = FindOutline(selection);
            if (outline == null) return;
            var w = GetWindow<ResizeOutlineWindow>(false, "Resize Outline", true);
            w.minSize = new Vector2(380, 230);
            w.m_Outline = outline;
            w.m_Source = outline.transform.parent != null ? outline.transform.parent.gameObject : null;
            w.m_Sidecar = OutlineGenerator.LoadSidecar(outline);
            if (w.m_Sidecar != null)
            {
                w.m_WidthCm = w.m_Sidecar.widthCm;
                w.m_WidthMultiplier = w.m_Sidecar.widthMultiplier;
            }
            else
            {
                w.m_WidthCm = 0.15f;
                w.m_WidthMultiplier = 1f;
            }
            w.m_Status = null;
            w.Show();
        }

        GameObject m_Outline;
        GameObject m_Source;
        OutlineGenerator.OutlineSidecar m_Sidecar;
        float m_WidthCm = 0.15f;
        float m_WidthMultiplier = 1f;
        string m_Status;

        void OnGUI()
        {
            if (m_Outline == null)
            {
                EditorGUILayout.HelpBox("No outline selected.", MessageType.Info);
                return;
            }
            EditorGUILayout.LabelField("Outline", m_Outline.name, EditorStyles.boldLabel);
            if (m_Source != null) EditorGUILayout.LabelField("Source", m_Source.name);
            EditorGUILayout.Space(4);
            if (m_Sidecar == null)
            {
                EditorGUILayout.HelpBox("This outline was made before width info was saved. Re-run Generate in the Outline window once to enable resizing.", MessageType.Warning);
                using (new EditorGUI.DisabledScope(true))
                {
                    m_WidthCm = EditorGUILayout.Slider("Width (cm)", m_WidthCm, 0.02f, 1.0f);
                    m_WidthMultiplier = EditorGUILayout.Slider("Width ×", m_WidthMultiplier, 0.1f, 4f);
                    GUILayout.Button("Regenerate Outline", GUILayout.Height(28));
                }
                return;
            }
            m_WidthCm = EditorGUILayout.Slider(new GUIContent("Width (cm)", "How thick the outline is, in real centimetres on the avatar"), m_WidthCm, 0.02f, 1.0f);
            m_WidthMultiplier = EditorGUILayout.Slider(new GUIContent("Width ×", "Multiplier for this object only"), m_WidthMultiplier, 0.1f, 4f);
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Regenerate Outline", GUILayout.Height(28)))
            {
                string error = OutlineGenerator.ResizeOutline(m_Outline, m_WidthCm, m_WidthMultiplier);
                if (error == null)
                {
                    m_Sidecar = OutlineGenerator.LoadSidecar(m_Outline);
                    m_Status = "Outline rebuilt at " + m_WidthCm.ToString("0.00") + " cm.";
                }
                else m_Status = error;
            }
            if (!string.IsNullOrEmpty(m_Status)) EditorGUILayout.HelpBox(m_Status, MessageType.Info);
        }
    }
}
