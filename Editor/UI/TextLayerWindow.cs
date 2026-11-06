using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>
    /// A small modal window for typing the words of a text layer: the text, size, colour and bold.
    /// Shows a live preview of what the layer will look like. Returns the typed values (or null when
    /// cancelled); the caller renders the text and adds it as an image layer.
    /// </summary>
    public class TextLayerWindow : EditorWindow
    {
        public class Result
        {
            public string text;
            public int fontSize;
            public Color color;
            public bool bold;
        }

        static Result s_Result;
        string m_Text = "";
        int m_FontSize = 64;
        Color m_Color = Color.white;
        bool m_Bold;
        Texture2D m_Preview;
        string m_PreviewKey;

        /// <summary>Shows the window and waits; returns the typed values, or null when cancelled.</summary>
        public static Result Pick(string text = "", int fontSize = 64, Color color = default, bool bold = false)
        {
            s_Result = null;
            var w = CreateInstance<TextLayerWindow>();
            w.titleContent = new GUIContent("Text layer");
            w.m_Text = text ?? "";
            w.m_FontSize = Mathf.Clamp(fontSize, 8, 512);
            w.m_Color = color == default ? Color.white : color;
            w.m_Bold = bold;
            var size = new Vector2(420, 340);
            w.minSize = size;
            var main = EditorGUIUtility.GetMainWindowPosition();
            w.position = new Rect(main.center - size * 0.5f, size);
            w.ShowModalUtility();
            return s_Result;
        }

        void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Type the words to put on the model. They become an image layer, " +
                "so drag it on the model or in the texture view, and scale/rotate it with the Image tool.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();
            m_Text = EditorGUILayout.TextArea(m_Text, GUILayout.Height(64));
            m_FontSize = EditorGUILayout.IntSlider("Size", m_FontSize, 16, 256);
            m_Color = EditorGUILayout.ColorField("Colour", m_Color);
            m_Bold = EditorGUILayout.Toggle("Bold", m_Bold);
            if (EditorGUI.EndChangeCheck()) m_PreviewKey = null;

            EditorGUILayout.Space(4);
            string key = m_Text + "|" + m_FontSize + "|" + ColorUtility.ToHtmlStringRGBA(m_Color) + "|" + m_Bold;
            if (m_PreviewKey != key)
            {
                m_PreviewKey = key;
                if (m_Preview != null) Object.DestroyImmediate(m_Preview);
                m_Preview = TextImage.Render(m_Text, m_FontSize, m_Color, m_Bold);
                // Emoji pictures are downloaded once and cached; re-render the preview when they land.
                // Only start that wait while emoji are actually still downloading — otherwise the
                // callback would clear the key and re-render on every repaint, forever.
                if (EmojiArt.NotReadyCount(m_Text) > 0)
                {
                    var self = this;
                    EmojiArt.Prepare(m_Text, () =>
                    {
                        if (self == null) return;
                        self.m_PreviewKey = null;
                        self.Repaint();
                    });
                }
            }
            if (EmojiArt.NotReadyCount(m_Text) > 0)
            {
                EditorGUILayout.LabelField("Downloading emoji… the preview updates when they're ready.",
                    EditorStyles.wordWrappedMiniLabel);
            }
            if (EmojiArt.HasFailures(m_Text))
            {
                EditorGUILayout.LabelField("Some emoji couldn't be downloaded (no internet?). They'll stay blank in the preview and the layer.",
                    EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button("Retry emoji download", GUILayout.Width(160)))
                {
                    EmojiArt.RetryFailed();
                    var self = this;
                    EmojiArt.Prepare(m_Text, () =>
                    {
                        if (self == null) return;
                        self.m_PreviewKey = null;
                        self.Repaint();
                    });
                    m_PreviewKey = null;
                }
            }
            if (m_Preview != null)
            {
                float aspect = m_Preview.height / (float)Mathf.Max(1, m_Preview.width);
                float pw = Mathf.Min(position.width - 24f, 396f);
                float ph = Mathf.Min(pw * aspect, 120f);
                var r = GUILayoutUtility.GetRect(pw, ph, GUILayout.ExpandWidth(false));
                EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.12f));
                GUI.DrawTexture(r, m_Preview, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUILayout.LabelField("Nothing to preview yet — type some words above.", EditorStyles.wordWrappedLabel);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Emoji render as colour pictures (" + EmojiArt.Attribution +
                " downloaded once from the Twemoji CDN and cached in the project's Library folder). " +
                "Plain symbols the font does have (♥ ★ ☺) render from the font.",
                EditorStyles.wordWrappedMiniLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(80))) Close();
                // Don't let the layer be added while its emoji are still downloading — the pictures
                // would be missing from the layer. They are cached after the first download.
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_Text) || EmojiArt.NotReadyCount(m_Text) > 0))
                {
                    if (GUILayout.Button("Add text layer", GUILayout.Width(120)))
                    {
                        s_Result = new Result { text = m_Text, fontSize = m_FontSize, color = m_Color, bold = m_Bold };
                        Close();
                    }
                }
            }
            EditorGUILayout.Space(6);
        }

        void OnDestroy()
        {
            if (m_Preview != null) Object.DestroyImmediate(m_Preview);
        }
    }
}
