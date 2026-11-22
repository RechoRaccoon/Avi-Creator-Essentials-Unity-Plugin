using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>Tools › Avi Creator Essentials › Outline &amp; Optimize: add outlines to an avatar, and shrink its textures.</summary>
    public class OutlineWindow : EditorWindow
    {
        [MenuItem("Tools/Avi Creator Essentials/Outline && Optimize…", false, 105)]
        static void FromToolsMenu() { Open(Selection.activeGameObject); }

        [MenuItem("GameObject/Avi Creator Essentials/Add Outline To This Avatar…", false, 22)]
        static void FromHierarchy(MenuCommand cmd) { Open(cmd.context as GameObject); }

        [MenuItem("GameObject/Avi Creator Essentials/Add Outline To This Avatar…", true)]
        static bool FromHierarchyValid() { return Selection.activeGameObject != null; }

        public static void Open(GameObject root)
        {
            var w = GetWindow<OutlineWindow>(false, "Outline & Optimize", true);
            w.minSize = new Vector2(520, 560);
            if (root != null) w.SetRoot(root);
            w.Show();
        }

        GameObject m_Root;
        int m_Tab;
        Vector2 m_Scroll;
        bool m_AutoExclude = true;
        readonly OutlineGenerator.Settings m_S = new OutlineGenerator.Settings();
        List<OutlineGenerator.RendererInfo> m_Renderers = new List<OutlineGenerator.RendererInfo>();
        string m_Report;
        MessageType m_ReportType = MessageType.Info;

        // textures
        List<TextureOptimizer.Entry> m_Textures = new List<TextureOptimizer.Entry>();
        readonly TextureOptimizer.Options m_TexOpts = new TextureOptimizer.Options();

        // widths tab
        List<WidthRow> m_WidthRows = new List<WidthRow>();
        bool m_WidthRowsDirty = true;
        OutlineGenerator.WidthPreview m_Preview;
        int m_PreviewControl;
        string m_WidthStatus;
        int m_LastTab;

        static readonly string[] s_Tabs = { "Outline", "Widths", "Textures" };
        static readonly string[] s_Shaders =
        {
            "VRChat Mobile Toon Lit (Quest + PC)", "VRChat Mobile Standard Lite (Quest + PC)", "Unlit color (PC only)",
            "Custom shader…", "My own outline material…",
        };
        static readonly string[] s_Placement = { "Separate object (easy to remove, one extra skinned mesh each)", "Merge into the mesh (fewest renderers, best for Quest)" };

        void SetRoot(GameObject root)
        {
            EndActivePreview(true);
            m_Root = root;
            m_Renderers = OutlineGenerator.Scan(root, m_AutoExclude);
            m_Textures = TextureOptimizer.Scan(root);
            m_WidthRowsDirty = true;
            m_Report = null;
        }

        void OnEnable()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        // Never save a scene while a live preview mesh is on an outline: commit it first,
        // so the scene always references the saved hull asset and never the temp mesh.
        void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            EndActivePreview(true);
        }

        void OnDisable()
        {
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EndActivePreview(true);
        }

        void OnLostFocus()
        {
            EndActivePreview(true);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            var root = (GameObject)EditorGUILayout.ObjectField("Avatar", m_Root, typeof(GameObject), true);
            if (root != m_Root) SetRoot(root);
            if (m_Root == null)
            {
                EditorGUILayout.HelpBox("Drop your avatar (the object in the scene or Hierarchy) here, or select it and open this window from Tools › Avi Creator Essentials › Outline & Optimize.", MessageType.Info);
                return;
            }
            m_Tab = GUILayout.Toolbar(m_Tab, s_Tabs);
            if (m_Tab != m_LastTab)
            {
                m_LastTab = m_Tab;
                if (m_Tab == 1) m_WidthRowsDirty = true;
                else if (m_Preview != null) EndActivePreview(true);
            }
            EditorGUILayout.Space(4);
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            if (m_Tab == 0) OutlineTab();
            else if (m_Tab == 1) WidthsTab();
            else TexturesTab();
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ outline tab

        void OutlineTab()
        {
            EditorGUILayout.LabelField(
                "Adds an outline by making a slightly bigger copy of the mesh with its faces flipped, in its own color. " +
                "The copy is baked into the mesh, so it works with any shader on your avatar (Poiyomi, lilToon, Standard, VRChat Mobile…) " +
                "and on Quest. Your original meshes and materials are not changed unless you choose \"Merge into the mesh\", and Ctrl+Z undoes it.",
                EditorStyles.wordWrappedMiniLabel);

            Section("Look");
            m_S.widthCm = EditorGUILayout.Slider(new GUIContent("Width (cm)", "How thick the outline is, in real centimetres on the avatar"), m_S.widthCm, 0.02f, 1.0f);
            m_S.exactWorldWidth = EditorGUILayout.ToggleLeft(new GUIContent("Even width on scaled objects",
                "Keeps the outline the same thickness everywhere, even when an object or its bones are scaled unevenly. Turn it off to go back to the older width if it looks wrong."), m_S.exactWorldWidth);
            m_S.color = EditorGUILayout.ColorField("Color", m_S.color);
            int sc = EditorGUILayout.Popup("Outline shader", (int)m_S.shader, s_Shaders);
            m_S.shader = (OutlineGenerator.ShaderChoice)sc;
            if (m_S.shader == OutlineGenerator.ShaderChoice.Custom)
                m_S.customShader = (Shader)EditorGUILayout.ObjectField("Shader", m_S.customShader, typeof(Shader), false);
            if (m_S.shader == OutlineGenerator.ShaderChoice.ExistingMaterial)
                m_S.existingMaterial = (Material)EditorGUILayout.ObjectField("Material", m_S.existingMaterial, typeof(Material), false);
            else
                m_S.flatLook = EditorGUILayout.ToggleLeft(new GUIContent("Flat look (ignore lighting)", "Makes lit shaders show the outline as a solid color"), m_S.flatLook);
            if (m_S.shader == OutlineGenerator.ShaderChoice.Custom || m_S.shader == OutlineGenerator.ShaderChoice.UnlitColor)
                EditorGUILayout.HelpBox("This shader won't work on Quest. Use a VRChat Mobile shader for Quest avatars.", MessageType.None);

            Section("Where it goes");
            m_S.placement = (OutlineGenerator.Placement)EditorGUILayout.Popup("Placement", (int)m_S.placement, s_Placement);
            m_S.replaceExisting = EditorGUILayout.ToggleLeft(new GUIContent("Replace outlines made earlier", "Removes this tool's old outlines on the avatar first, so you can tweak and run again"), m_S.replaceExisting);

            Section("What gets an outline");
            using (new EditorGUILayout.HorizontalScope())
            {
                bool auto = EditorGUILayout.ToggleLeft(new GUIContent("Skip eyes, lashes, brows, mouth, cutout and transparent materials",
                    "Re-scans the avatar when changed. You can still tick anything back on below."), m_AutoExclude);
                if (auto != m_AutoExclude) { m_AutoExclude = auto; Rescan(); }
                if (GUILayout.Button("Re-scan", GUILayout.Width(70))) Rescan();
            }
            DrawRenderers();

            Section("Keep it off certain areas");
            EditorGUILayout.LabelField("Bones to leave out. Vertices weighted to these bones fade out of the outline (for example the head, or a hand).", EditorStyles.wordWrappedMiniLabel);
            for (int i = 0; i < m_S.excludedBones.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    m_S.excludedBones[i] = (Transform)EditorGUILayout.ObjectField(m_S.excludedBones[i], typeof(Transform), true);
                    if (GUILayout.Button("✕", GUILayout.Width(24))) { m_S.excludedBones.RemoveAt(i); GUIUtility.ExitGUI(); }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add bone"))
                {
                    var t = Selection.activeTransform;
                    if (t != null && !m_S.excludedBones.Contains(t)) m_S.excludedBones.Add(t);
                    else if (t == null) m_S.excludedBones.Add(null);
                }
                if (GUILayout.Button("Add head bone"))
                {
                    var animator = m_Root.GetComponentInChildren<Animator>(true);
                    var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                    if (head != null && !m_S.excludedBones.Contains(head)) m_S.excludedBones.Add(head);
                }
            }
            m_S.excludedBonesIncludeChildren = EditorGUILayout.ToggleLeft("Also leave out everything under those bones", m_S.excludedBonesIncludeChildren);
            m_S.useVertexColorWidth = EditorGUILayout.ToggleLeft(new GUIContent("Use the mesh's vertex color (red) as width",
                "Paint vertex colors in Blender: black = no outline, white = full width. Only used on meshes that have vertex colors."), m_S.useVertexColorWidth);

            Section("Smoothing and blend shapes");
            m_S.smoothNormals = EditorGUILayout.ToggleLeft(new GUIContent("Smooth normals across hard edges", "Stops the outline from splitting open at UV seams and sharp edges"), m_S.smoothNormals);
            m_S.copyBlendShapes = EditorGUILayout.ToggleLeft(new GUIContent("Make the outline follow blend shapes",
                "Needed so the outline stays on the face when visemes and expressions move it. Costs mesh size."), m_S.copyBlendShapes);
            using (new EditorGUI.DisabledScope(!m_S.copyBlendShapes))
            {
                m_S.blendShapeMinMoveMm = EditorGUILayout.Slider(new GUIContent("Skip shapes moving less than (mm)", "Blend shapes that barely move the outline are left out to save size"), m_S.blendShapeMinMoveMm, 0f, 5f);
                m_S.linkBlendShapes = EditorGUILayout.ToggleLeft(new GUIContent("Link blend shapes with VRCFury",
                    "Adds a VRCFury BlendShape Link to each outline, so toggles and sliders that animate the source's blend shapes drive the outline's too. Needs VRCFury installed; without it the outline is made anyway, with a warning."), m_S.linkBlendShapes);
            }

            Section("Optimize the outline (optional)");
            m_S.weldVertices = EditorGUILayout.ToggleLeft(new GUIContent("Weld duplicate vertices", "The outline needs no UVs, so UV seam copies can be merged. Safe, usually saves 30–50% of its vertices."), m_S.weldVertices);
            m_S.simplifyMm = EditorGUILayout.Slider(new GUIContent("Simplify (mm)", "Merges outline vertices that are within this distance. 0 = off. 1–3 mm is usually invisible."), m_S.simplifyMm, 0f, 8f);
            m_S.triangleBudget = EditorGUILayout.IntField(new GUIContent("Triangle budget", "0 = no limit. Otherwise the outline is simplified automatically until it fits."), m_S.triangleBudget);
            if (m_S.triangleBudget < 0) m_S.triangleBudget = 0;

            DrawStats();

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Remove outlines", GUILayout.Height(28))) DoRemove();
                if (GUILayout.Button("Generate outline", GUILayout.Height(28))) DoGenerate();
            }
            if (!string.IsNullOrEmpty(m_Report)) EditorGUILayout.HelpBox(m_Report, m_ReportType);
            EditorGUILayout.Space(8);
        }

        void DrawRenderers()
        {
            if (m_Renderers.Count == 0) { EditorGUILayout.LabelField("No meshes found on this avatar.", EditorStyles.miniLabel); return; }
            foreach (var info in m_Renderers)
            {
                if (info.renderer == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    info.expanded = EditorGUILayout.Foldout(info.expanded, GUIContent.none, true);
                    info.include = EditorGUILayout.ToggleLeft(info.renderer.name, info.include, GUILayout.MinWidth(120));
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(info.Triangles + " tris", EditorStyles.miniLabel, GUILayout.Width(70));
                    if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                    {
                        Selection.activeGameObject = info.renderer.gameObject;
                        EditorGUIUtility.PingObject(info.renderer.gameObject);
                    }
                }
                if (!info.expanded) continue;
                EditorGUI.indentLevel++;
                using (new EditorGUI.DisabledScope(!info.include))
                {
                    info.widthMultiplier = EditorGUILayout.Slider("Width ×", info.widthMultiplier, 0.1f, 4f);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        info.overrideColor = EditorGUILayout.ToggleLeft("Own color", info.overrideColor, GUILayout.Width(100));
                        using (new EditorGUI.DisabledScope(!info.overrideColor))
                            info.color = EditorGUILayout.ColorField(info.color);
                    }
                    foreach (var sl in info.slots)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            string label = (sl.material != null ? sl.material.name : "(no material)") + "  · " + sl.triangles + " tris";
                            sl.include = EditorGUILayout.ToggleLeft(label, sl.include);
                            if (!string.IsNullOrEmpty(sl.note)) EditorGUILayout.LabelField(sl.note, EditorStyles.miniLabel, GUILayout.Width(170));
                        }
                    }
                }
                EditorGUI.indentLevel--;
            }
        }

        void DrawStats()
        {
            int outline = 0, all = 0, verts = 0;
            foreach (var info in m_Renderers)
            {
                if (info.renderer == null) continue;
                foreach (var sl in info.slots) all += sl.triangles;
                if (!info.include) continue;
                outline += info.Triangles;
                verts += info.mesh != null ? info.mesh.vertexCount : 0;
            }
            Section("Estimate before optimizing");
            EditorGUILayout.LabelField("Outline triangles", outline.ToString("N0"));
            EditorGUILayout.LabelField("Avatar triangles with outline", (all + outline).ToString("N0") + "   (Quest: " + OutlineGenerator.Quest(all + outline) + ")");
            long bytes = (long)verts * OutlineGenerator.BytesPerVertexEstimate + (long)outline * 12;
            EditorGUILayout.LabelField("Mesh size (rough)", TextureOptimizer.Mb(bytes) + "  — welding roughly halves it; blend shapes add more");
            EditorGUILayout.LabelField("Extra material slots", "1 (or 1 per own color)");
        }

        void Rescan()
        {
            if (m_Root == null) return;
            m_Renderers = OutlineGenerator.Scan(m_Root, m_AutoExclude);
        }

        void DoGenerate()
        {
            try
            {
                var r = OutlineGenerator.Generate(m_Root, m_Renderers, m_S);
                m_Report = "Made outlines for " + r.renderers + " mesh(es): " + r.triangles.ToString("N0") + " triangles, " +
                           r.vertices.ToString("N0") + " vertices, " + r.blendShapes + " blend shapes followed, " +
                           TextureOptimizer.Mb(r.bytes) + " of mesh files in " + OutlineGenerator.DataFolder + "." +
                           (r.warnings.Count > 0 ? "\n\n" + string.Join("\n", r.warnings) : "");
                m_ReportType = r.warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
                Rescan();
                m_WidthRowsDirty = true;
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogException(e);
                m_Report = "Outline failed: " + e.Message;
                m_ReportType = MessageType.Error;
            }
        }

        void DoRemove()
        {
            int n = OutlineGenerator.RemoveAll(m_Root);
            m_Report = n > 0 ? "Removed " + n + " outline object(s) / merged mesh(es)." : "No outlines from this tool were found on the avatar.";
            m_ReportType = MessageType.Info;
            Rescan();
            m_WidthRowsDirty = true;
        }

        // ------------------------------------------------------------------ widths tab

        class WidthRow
        {
            public GameObject outline;
            public GameObject source;
            public float widthCm;
            public float widthMultiplier;
            public bool hasSidecar;
        }

        List<WidthRow> ScanWidths(GameObject root)
        {
            var rows = new List<WidthRow>();
            if (root == null) return rows;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.name.EndsWith(OutlineGenerator.Suffix, StringComparison.Ordinal)) continue;
                var go = r.gameObject;
                var sidecar = OutlineGenerator.LoadSidecar(go);
                rows.Add(new WidthRow
                {
                    outline = go,
                    source = go.transform.parent != null ? go.transform.parent.gameObject : null,
                    widthCm = sidecar != null ? sidecar.widthCm : 0.15f,
                    widthMultiplier = sidecar != null ? sidecar.widthMultiplier : 1f,
                    hasSidecar = sidecar != null,
                });
            }
            return rows;
        }

        void WidthsTab()
        {
            if (m_WidthRowsDirty)
            {
                m_WidthRows = ScanWidths(m_Root);
                m_WidthRowsDirty = false;
            }
            EditorGUILayout.LabelField(
                "Every separate outline on this avatar, in one place. Drag a slider and the outline changes live in the scene; letting go saves it. " +
                "Outlines merged into the mesh have no separate object, so they are not listed here.",
                EditorStyles.wordWrappedMiniLabel);

            if (m_WidthRows.Count == 0)
            {
                EditorGUILayout.LabelField("No separate outlines on this avatar yet. Generate some on the Outline tab first.", EditorStyles.miniLabel);
            }
            for (int i = 0; i < m_WidthRows.Count; i++)
            {
                var row = m_WidthRows[i];
                if (row.outline == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(row.source != null ? row.source.name : row.outline.name, GUILayout.MinWidth(140));
                    using (new EditorGUI.DisabledScope(!row.hasSidecar))
                    {
                        EditorGUI.BeginChangeCheck();
                        float w = EditorGUILayout.Slider(row.widthCm, 0.02f, 1.0f);
                        if (EditorGUI.EndChangeCheck()) WidthSliderChanged(row, w);
                    }
                    if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                    {
                        Selection.activeGameObject = row.outline;
                        EditorGUIUtility.PingObject(row.outline);
                    }
                }
                if (!row.hasSidecar)
                    EditorGUILayout.LabelField("Made before width info was saved. Re-run Generate once to enable resizing.", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Re-scan", GUILayout.Height(28))) m_WidthRowsDirty = true;
            }
            if (!string.IsNullOrEmpty(m_WidthStatus)) EditorGUILayout.HelpBox(m_WidthStatus, MessageType.Info);

            // Commit the live preview once the drag ends.
            if (m_Preview != null && GUIUtility.hotControl != m_PreviewControl)
                EndActivePreview(true);
            EditorGUILayout.Space(8);
        }

        void WidthSliderChanged(WidthRow row, float w)
        {
            row.widthCm = w;
            int hot = GUIUtility.hotControl;
            if (hot == 0)
            {
                // Changed by keyboard, not a drag: nothing to track, save straight away.
                EndActivePreview(true);
                string err = OutlineGenerator.ResizeOutline(row.outline, row.widthCm, row.widthMultiplier);
                m_WidthStatus = err != null ? err : "Saved " + (row.source != null ? row.source.name : row.outline.name) + " at " + row.widthCm.ToString("0.00") + " cm.";
                return;
            }
            if (m_Preview == null || m_Preview.outlineObject != row.outline)
            {
                EndActivePreview(true);
                string err = OutlineGenerator.BeginWidthPreview(row.outline, out m_Preview);
                if (err != null)
                {
                    m_WidthStatus = err;
                    m_Preview = null;
                    m_PreviewControl = 0;
                    return;
                }
                m_PreviewControl = hot;
                m_WidthStatus = "Live preview, let go to save.";
            }
            OutlineGenerator.SetPreviewWidth(m_Preview, row.widthCm);
        }

        void EndActivePreview(bool commit)
        {
            if (m_Preview == null) return;
            var p = m_Preview;
            m_Preview = null;
            m_PreviewControl = 0;
            string err = OutlineGenerator.EndWidthPreview(p, commit);
            if (err != null) m_WidthStatus = err;
            else if (commit) m_WidthStatus = "Saved " + p.sourceName + " at " + p.widthCm.ToString("0.00") + " cm.";
        }

        // ------------------------------------------------------------------ textures tab

        void TexturesTab()
        {
            EditorGUILayout.LabelField(
                "Textures are almost always the biggest part of an avatar's download and memory size. This only changes each texture's import " +
                "settings (max size and compression); your image files are never edited, so you can always set a size back.",
                EditorStyles.wordWrappedMiniLabel);

            Section("Settings");
            m_TexOpts.crunch = EditorGUILayout.ToggleLeft(new GUIContent("Crunch compression", "Makes the files much smaller to download and store. Slightly lossy."), m_TexOpts.crunch);
            using (new EditorGUI.DisabledScope(!m_TexOpts.crunch))
                m_TexOpts.quality = EditorGUILayout.IntSlider("Compression quality", m_TexOpts.quality, 0, 100);
            m_TexOpts.streamingMipmaps = EditorGUILayout.ToggleLeft(new GUIContent("Mipmap streaming", "Loads lower detail first; saves memory"), m_TexOpts.streamingMipmaps);
            m_TexOpts.disableReadWrite = EditorGUILayout.ToggleLeft(new GUIContent("Turn off Read/Write", "Halves memory for textures that don't need to be read by scripts. Leave off if you use runtime tools."), m_TexOpts.disableReadWrite);
            m_TexOpts.questOverride = EditorGUILayout.ToggleLeft(new GUIContent("Quest / Android override (ASTC 6x6)", "Also sets the Android platform, which is what Quest builds use"), m_TexOpts.questOverride);
            using (new EditorGUI.DisabledScope(!m_TexOpts.questOverride))
            {
                int qi = TextureOptimizer.SizeIndex(m_TexOpts.questMax);
                qi = EditorGUILayout.Popup("Quest max size", qi, TextureOptimizer.SizeNames);
                m_TexOpts.questMax = TextureOptimizer.Sizes[qi];
            }

            Section("Quick sizes");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Cap at 4096")) CapAll(4096);
                if (GUILayout.Button("Cap at 2048")) CapAll(2048);
                if (GUILayout.Button("Cap at 1024")) CapAll(1024);
                if (GUILayout.Button("Cap at 512")) CapAll(512);
                if (GUILayout.Button("Reset")) foreach (var e in m_Textures) e.newMax = e.currentMax;
            }

            Section("Textures on this avatar");
            if (m_Textures.Count == 0)
            {
                EditorGUILayout.LabelField("No project textures found (textures inside packages can't be changed).", EditorStyles.miniLabel);
            }
            long before = 0, after = 0;
            foreach (var e in m_Textures)
            {
                before += e.memoryBytes;
                after += e.include ? TextureOptimizer.EstimateVram(e, m_TexOpts.questOverride ? Mathf.Min(e.newMax, m_TexOpts.questMax) : e.newMax, m_TexOpts.questOverride) : e.memoryBytes;
            }
            foreach (var e in m_Textures)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    e.include = EditorGUILayout.ToggleLeft(new GUIContent(e.texture.name, e.path + "\nUsed by: " + string.Join(", ", e.usedBy)), e.include, GUILayout.MinWidth(120));
                    EditorGUILayout.LabelField(e.width + "×" + e.height, EditorStyles.miniLabel, GUILayout.Width(70));
                    EditorGUILayout.LabelField(TextureOptimizer.Mb(e.memoryBytes), EditorStyles.miniLabel, GUILayout.Width(60));
                    int si = TextureOptimizer.SizeIndex(e.newMax);
                    using (new EditorGUI.DisabledScope(!e.include))
                        si = EditorGUILayout.Popup(si, TextureOptimizer.SizeNames, GUILayout.Width(60));
                    e.newMax = TextureOptimizer.Sizes[si];
                    if (GUILayout.Button("↗", EditorStyles.miniButton, GUILayout.Width(24))) EditorGUIUtility.PingObject(e.texture);
                }
            }
            if (m_Textures.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Texture memory now", TextureOptimizer.Mb(before));
                EditorGUILayout.LabelField("After (estimate)", TextureOptimizer.Mb(after));
            }

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Re-scan", GUILayout.Height(28))) m_Textures = TextureOptimizer.Scan(m_Root);
                if (GUILayout.Button("Apply to selected textures", GUILayout.Height(28)))
                {
                    if (EditorUtility.DisplayDialog("Optimize textures", "Change the import settings of the ticked textures? Your image files are not edited.", "Apply", "Cancel"))
                    {
                        TextureOptimizer.Apply(m_Textures, m_TexOpts);
                        m_Textures = TextureOptimizer.Scan(m_Root);
                    }
                }
            }
            EditorGUILayout.Space(8);
        }

        void CapAll(int size)
        {
            foreach (var e in m_Textures) e.newMax = Mathf.Min(e.currentMax, size);
        }

        static void Section(string title)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }
    }
}
