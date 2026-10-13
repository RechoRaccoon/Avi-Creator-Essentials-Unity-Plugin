using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>A small modal window with Unity's texture field, for picking a texture asset from the project.</summary>
    public class TexturePickWindow : EditorWindow
    {
        static Texture s_Result;
        Texture m_Texture;
        string m_Message;

        /// <summary>Shows the window and waits; returns the chosen texture, or null when cancelled.</summary>
        public static Texture Pick(string title, string message, Texture initial = null)
        {
            s_Result = null;
            var w = CreateInstance<TexturePickWindow>();
            w.titleContent = new GUIContent(title);
            w.m_Message = message;
            w.m_Texture = initial;
            var size = new Vector2(360, 150);
            w.minSize = w.maxSize = size;
            var main = EditorGUIUtility.GetMainWindowPosition();
            w.position = new Rect(main.center - size * 0.5f, size);
            w.ShowModalUtility();
            return s_Result;
        }

        void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(m_Message, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(4);
            m_Texture = (Texture)EditorGUILayout.ObjectField("Texture", m_Texture, typeof(Texture), false);
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(80))) Close();
                using (new EditorGUI.DisabledScope(m_Texture == null))
                {
                    if (GUILayout.Button("Wrap", GUILayout.Width(80)))
                    {
                        s_Result = m_Texture;
                        Close();
                    }
                }
            }
            EditorGUILayout.Space(6);
        }
    }
}
