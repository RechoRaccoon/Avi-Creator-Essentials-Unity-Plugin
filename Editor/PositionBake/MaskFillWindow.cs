using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>"Fill mask shape with texture": makes a new texture in the shape of a mask, filled with another texture.</summary>
    public class MaskFillWindow : EditorWindow
    {
        const string MenuName = "Fill Mask Shape With Texture (Avi Creator Essentials)…";

        [MenuItem("Assets/Avi Creator Essentials/" + MenuName, false, 2002)]
        static void FromAssetsMenu() => Open(Selection.activeObject as Texture2D);

        [MenuItem("Assets/Avi Creator Essentials/" + MenuName, true)]
        static bool FromAssetsMenuValid() => Selection.activeObject is Texture2D;

        [MenuItem("CONTEXT/TextureImporter/" + MenuName, false, 2002)]
        static void FromTextureMenu(MenuCommand cmd) =>
            Open(cmd.context is TextureImporter imp ? AssetDatabase.LoadAssetAtPath<Texture2D>(imp.assetPath) : null);

        [MenuItem("Tools/Avi Creator Essentials/Fill Mask Shape With Texture…", false, 104)]
        static void FromToolsMenu() => Open(Selection.activeObject as Texture2D);

        public static void Open(Texture2D mask)
        {
            var w = GetWindow<MaskFillWindow>(true, "Fill Mask Shape", true);
            w.minSize = new Vector2(440, 520);
            if (mask != null) w.m_Mask = mask;
            w.m_PreviewDirty = true;
        }

        public enum Shape { Auto, Alpha, Brightness, Red, Green, Blue }

        static readonly string[] s_Shapes = { "Auto", "Alpha", "Brightness", "Red", "Green", "Blue" };
        const int PreviewSize = 192;

        Texture2D m_Mask;
        Texture m_Fill;
        Shape m_Shape = Shape.Auto;
        bool m_Invert;
        bool m_Tile;
        Vector2 m_Tiling = Vector2.one;
        Vector2 m_Offset = Vector2.zero;
        Color m_Outside = Color.clear;
        Texture2D m_Preview;
        bool m_PreviewDirty = true;
        string m_ShapeNote;

        void OnDisable() => APUtil.Destroy(ref m_Preview);

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Makes a new texture in the shape of the mask, filled with another texture. Where the mask is white (or opaque) " +
                "the new texture shows; elsewhere it's transparent. The result is saved next to the mask; the mask isn't changed.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6);

            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                m_Mask = TextureBox("Mask (shape)", m_Mask, typeof(Texture2D)) as Texture2D;
                GUILayout.Space(8);
                m_Fill = TextureBox("Fill texture", m_Fill, typeof(Texture));
            }

            EditorGUILayout.Space(6);
            m_Shape = (Shape)EditorGUILayout.Popup(new GUIContent("Shape from",
                "Which part of the mask holds the shape. Auto uses the alpha when the mask has transparency, otherwise its brightness."),
                (int)m_Shape, s_Shapes);
            m_Invert = EditorGUILayout.Toggle(new GUIContent("Invert", "Fill outside the shape instead"), m_Invert);
            m_Tile = EditorGUILayout.Toggle(new GUIContent("Tile", "Repeat the fill texture instead of stretching it once over the mask"), m_Tile);
            using (new EditorGUI.DisabledScope(!m_Tile))
            {
                m_Tiling = EditorGUILayout.Vector2Field("Tiling", m_Tiling);
                m_Offset = EditorGUILayout.Vector2Field("Offset", m_Offset);
            }
            m_Outside = EditorGUILayout.ColorField(new GUIContent("Outside the shape", "Color where the mask is empty. Transparent by default."), m_Outside);
            if (EditorGUI.EndChangeCheck()) m_PreviewDirty = true;

            if (m_Mask == null || m_Fill == null)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox("Pick or drop the mask and the texture to fill it with. You can also right-click a mask texture in the " +
                                        "Project window › Avi Creator Essentials › \"" + MenuName + "\".", MessageType.Info);
                APUtil.Destroy(ref m_Preview);
                return;
            }

            if (m_PreviewDirty) UpdatePreview();
            EditorGUILayout.Space(6);
            if (!string.IsNullOrEmpty(m_ShapeNote)) EditorGUILayout.LabelField(m_ShapeNote, EditorStyles.miniLabel);
            if (m_Preview != null)
            {
                var r = GUILayoutUtility.GetRect(PreviewSize, PreviewSize, GUILayout.ExpandWidth(true));
                r = new Rect(r.center.x - PreviewSize * 0.5f, r.y, PreviewSize, PreviewSize);
                EditorGUI.DrawTextureTransparent(r, m_Preview, ScaleMode.ScaleToFit);
            }

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(m_Mask.width + " × " + m_Mask.height, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Create", GUILayout.Width(140), GUILayout.Height(26))) Run();
            }
            EditorGUILayout.Space(6);
        }

        /// <summary>A labelled texture thumbnail you can click to pick from, or drop a texture onto.</summary>
        static Texture TextureBox(string label, Texture tex, Type type)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(200)))
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                return (Texture)EditorGUILayout.ObjectField(tex, type, false, GUILayout.Width(120), GUILayout.Height(120));
            }
        }

        void UpdatePreview()
        {
            m_PreviewDirty = false;
            APUtil.Destroy(ref m_Preview);
            try
            {
                var px = Compose(m_Mask, m_Fill, PreviewSize, PreviewSize, out var used);
                m_ShapeNote = m_Shape == Shape.Auto ? "Using the mask's " + (used == Shape.Alpha ? "alpha" : "brightness") : null;
                m_Preview = new Texture2D(PreviewSize, PreviewSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                m_Preview.SetPixels32(px);
                m_Preview.Apply(false);
            }
            catch (Exception e)
            {
                m_ShapeNote = "Preview failed: " + e.Message;
            }
        }

        /// <summary>Builds the result at the given size, in gamma values.</summary>
        Color32[] Compose(Texture2D mask, Texture fill, int w, int h, out Shape used)
        {
            var maskPx = ReadResized(mask, w, h, Vector2.one, Vector2.zero, false);
            var fillPx = m_Tile ? ReadResized(fill, w, h, m_Tiling, m_Offset, true) : ReadResized(fill, w, h, Vector2.one, Vector2.zero, false);

            used = m_Shape;
            if (used == Shape.Auto) used = HasTransparency(maskPx) ? Shape.Alpha : Shape.Brightness;

            Color32 bg = m_Outside;
            var outPx = new Color32[w * h];
            for (int i = 0; i < outPx.Length; i++)
            {
                var m = maskPx[i];
                int v;
                switch (used)
                {
                    case Shape.Alpha: v = m.a; break;
                    case Shape.Red: v = m.r; break;
                    case Shape.Green: v = m.g; break;
                    case Shape.Blue: v = m.b; break;
                    default: v = (m.r * 299 + m.g * 587 + m.b * 114) / 1000; break;
                }
                if (m_Invert) v = 255 - v;
                var f = fillPx[i];
                outPx[i] = new Color32(
                    (byte)(bg.r + (f.r - bg.r) * v / 255),
                    (byte)(bg.g + (f.g - bg.g) * v / 255),
                    (byte)(bg.b + (f.b - bg.b) * v / 255),
                    (byte)(bg.a + (f.a - bg.a) * v / 255));
            }
            return outPx;
        }

        static bool HasTransparency(Color32[] px)
        {
            int step = Mathf.Max(1, px.Length / 65536);
            for (int i = 0; i < px.Length; i += step)
                if (px[i].a < 250) return true;
            return false;
        }

        /// <summary>Reads a texture's gamma values resized to w × h, optionally tiled.</summary>
        static Color32[] ReadResized(Texture src, int w, int h, Vector2 tiling, Vector2 offset, bool repeat)
        {
            var gamma = APUtil.ToGammaTexture(src);
            gamma.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            gamma.filterMode = FilterMode.Bilinear;
            var rt = APUtil.CreateRT(w, h, GraphicsFormat.R8G8B8A8_UNorm, "mask fill");
            try
            {
                Graphics.Blit(gamma, rt, tiling, offset);
                var tex = APUtil.ReadToTexture(rt);
                var px = tex.GetPixels32();
                Object.DestroyImmediate(tex);
                return px;
            }
            finally
            {
                APUtil.Release(ref rt);
                Object.DestroyImmediate(gamma);
            }
        }

        void Run()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Fill Mask Shape", "Filling " + m_Mask.name + "…", 0.5f);
                int w = Mathf.Min(m_Mask.width, 8192), h = Mathf.Min(m_Mask.height, 8192);
                var px = Compose(m_Mask, m_Fill, w, h, out _);
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                tex.SetPixels32(px);
                tex.Apply(false);
                byte[] png = tex.EncodeToPNG();
                Object.DestroyImmediate(tex);

                string path = AssetDatabase.GenerateUniqueAssetPath(OutputFolder(m_Mask) + "/" +
                    APUtil.SafeFileName(m_Mask.name + " (" + m_Fill.name + ")") + ".png");
                File.WriteAllBytes(APUtil.AssetPathToFull(path), png);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                if (AssetImporter.GetAtPath(path) is TextureImporter imp)
                {
                    imp.textureType = TextureImporterType.Default;
                    imp.sRGBTexture = true;
                    imp.alphaIsTransparency = true;
                    imp.maxTextureSize = Mathf.Max(2048, Mathf.NextPowerOfTwo(Mathf.Max(w, h)));
                    imp.mipmapEnabled = true;
                    imp.SaveAndReimport();
                }
                var made = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (made != null)
                {
                    EditorGUIUtility.PingObject(made);
                    Selection.activeObject = made;
                }
                ShowNotification(new GUIContent("Made " + Path.GetFileName(path)));
                Debug.Log("Avi Creator Essentials: filled the shape of " + m_Mask.name + " with " + m_Fill.name + " → " + path);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Fill Mask Shape", "Creating the texture failed: " + e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static string OutputFolder(Texture mask)
        {
            string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(mask))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets", StringComparison.Ordinal))
            {
                folder = APUtil.OutputFolder;
                APUtil.EnsureAssetFolder(folder);
            }
            return folder;
        }
    }
}
