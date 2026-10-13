using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>The Avi Creator Essentials window: start screen, workspace layout, shortcuts and the update loop.</summary>
    public class AviCreatorWindow : EditorWindow
    {
        const string StyleGuid = "a3e1c7d94b2f4e6a8c0d1f3b5e7a9c21";
        const string LastAvatarKey = "AviCreatorEssentials.LastAvatar";
        const string RecoveryKey = "AviCreatorEssentials.Recovery";

        [MenuItem("Tools/Avi Creator Essentials/Avatar Painter", false, 100)]
        public static void Open()
        {
            var w = GetWindow<AviCreatorWindow>();
            w.titleContent = new GUIContent("Avi Creator Essentials");
            w.minSize = new Vector2(900, 560);
            w.Show();
        }

        [MenuItem("GameObject/Avi Creator Essentials/Paint This Avatar", false, 20)]
        static void OpenForSelection()
        {
            var go = Selection.activeGameObject;
            Open();
            var w = GetWindow<AviCreatorWindow>();
            if (go != null) w.StartSession(go);
        }

        [MenuItem("GameObject/Avi Creator Essentials/Paint This Avatar", true)]
        static bool ValidateOpenForSelection() => Selection.activeGameObject != null;

        PainterContext m_Ctx;
        APSession m_Session;
        ViewSettings m_View;
        BrushSettings m_Brush;
        ThumbnailCache m_Thumbs;

        VisualElement m_Root;
        Viewport3D m_V3;
        Viewport2D m_V2;
        VisualElement m_ViewArea;
        VisualElement m_Splitter;
        VisualElement m_RightPanel;
        VisualElement m_ToolRail;
        Label m_Status;
        Label m_Memory;
        Button m_UndoButton;
        Button m_RedoButton;
        VisualElement m_ModalLayer;

        LayerPanel m_LayerPanel;
        BrushPanel m_BrushPanel;
        MaterialsPanel m_MaterialsPanel;
        MeshesPanel m_MeshesPanel;
        ImagePanel m_ImagePanel;
        AdjustPanel m_AdjustPanel;
        W.Section m_AdjustSection;
        string m_LastActiveLayerId;
        bool m_SuppressLayerColor;
        PosePanel m_PosePanel;
        W.Section m_ColorSection, m_BrushSection, m_LayerSection, m_ImageSection, m_MaterialSection, m_MeshSection, m_ViewSection, m_PoseSection;

        readonly Dictionary<PaintTool, Button> m_ToolButtons = new Dictionary<PaintTool, Button>();
        readonly Dictionary<PoseTool, Button> m_PoseButtons = new Dictionary<PoseTool, Button>();
        readonly List<Action> m_TopBarRefresh = new List<Action>();

        bool m_StructureDirty;
        bool m_ThumbsDirty;
        double m_LastThumbs;
        double m_HintUntil;
        GameObject m_PendingAvatar;

        // ================================================================== lifecycle

        void OnEnable()
        {
            titleContent = new GUIContent("Avi Creator Essentials");
            m_View = ViewSettings.Load();
            m_Brush = BrushSettings.Load();
            m_Ctx = new PainterContext { brush = m_Brush, view = m_View };
            m_Ctx.RequestRender = () => m_V3?.RequestRender();
            m_Ctx.RequestRender2D = () => m_V2?.RequestRender();
            m_Ctx.OpenQuickColor = pos => QuickColorPopup.Show(m_Root, m_Ctx, pos);
            m_Ctx.ToolChanged += OnToolChanged;
            m_Ctx.BrushChanged += () => { m_V3?.UpdateHud(); m_V3?.RepaintOverlay(); m_V2?.RepaintOverlay(); };
            m_Ctx.ColorChanged += OnPaintColorChanged;
            UpdateChecker.Changed += OnUpdateInfo;
            UpdateChecker.Check(false);
            m_Ctx.Hint += ShowHint;
            m_Thumbs = new ThumbnailCache();
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorSceneManagerHooks(true);
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            UpdateChecker.Changed -= OnUpdateInfo;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorSceneManagerHooks(false);
            DisposeSession();
            m_Thumbs?.Dispose();
        }

        void EditorSceneManagerHooks(bool on)
        {
            if (on) UnityEditor.SceneManagement.EditorSceneManager.sceneClosing += OnSceneClosing;
            else UnityEditor.SceneManagement.EditorSceneManager.sceneClosing -= OnSceneClosing;
        }

        void OnSceneClosing(Scene scene, bool removing)
        {
            if (m_Session == null || m_Session.sourceAvatar == null) return;
            if (m_Session.sourceAvatar.scene == scene)
            {
                if (m_Session.dirty) SaveRecovery();
                DisposeSession();
                ShowStart();
            }
        }

        void OnBeforeReload()
        {
            if (m_Session != null && m_Session.dirty) SaveRecovery();
            if (m_Session != null) RememberAvatar(m_Session.sourceAvatar);
        }

        void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode && m_Session != null)
            {
                if (m_Session.dirty) SaveRecovery();
                RememberAvatar(m_Session.sourceAvatar);
                DisposeSession();
                ShowStart();
            }
        }

        void SaveRecovery()
        {
            try
            {
                string folder = ProjectIO.RecoveryFolder(m_Session.avatarName);
                ProjectIO.Save(m_Session, folder, false);
                EditorPrefs.SetString(RecoveryKey, folder);
                RememberAvatar(m_Session.sourceAvatar);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Avi Creator Essentials: could not write the recovery copy: " + e.Message);
            }
        }

        static void RememberAvatar(GameObject go)
        {
            if (go == null) return;
            EditorPrefs.SetString(LastAvatarKey, GlobalObjectId.GetGlobalObjectIdSlow(go).ToString());
        }

        static GameObject RememberedAvatar()
        {
            string id = EditorPrefs.GetString(LastAvatarKey, "");
            if (string.IsNullOrEmpty(id) || !GlobalObjectId.TryParse(id, out var gid)) return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as GameObject;
        }

        public override void SaveChanges()
        {
            if (m_Session != null) Save(false);
            base.SaveChanges();
        }

        void DisposeSession()
        {
            if (m_Session == null) return;
            m_Ctx.session = null;
            try { m_Session.Dispose(); }
            catch (Exception e) { Debug.LogException(e); }
            m_Session = null;
            m_Thumbs?.Dispose();
            hasUnsavedChanges = false;
        }

        // ================================================================== UI construction

        void CreateGUI()
        {
            m_Root = rootVisualElement;
            m_Root.Clear();
            var sheet = LoadStyle();
            if (sheet != null) m_Root.styleSheets.Add(sheet);
            m_Root.AddToClassList("ap-root");
            ApplyTheme();
            m_Root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            if (m_Session != null) BuildWorkspace();
            else ShowStart();
        }

        public void ApplyTheme()
        {
            if (m_Root == null) return;
            bool light = m_View.LightTheme;
            m_Root.EnableInClassList("ace-light", light);
            m_Root.EnableInClassList("ace-dark", !light);
            if (m_Session != null && m_RightPanel != null)
            {
                BuildRightPanel();
                RebuildPanels();
            }
            m_V3?.RequestRender();
            m_V2?.RequestRender();
        }

        static StyleSheet LoadStyle()
        {
            var path = AssetDatabase.GUIDToAssetPath(StyleGuid);
            StyleSheet s = null;
            if (!string.IsNullOrEmpty(path)) s = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (s == null)
            {
                foreach (var g in AssetDatabase.FindAssets("AviCreatorEssentials t:StyleSheet"))
                {
                    s = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(g));
                    if (s != null) break;
                }
            }
            return s;
        }

        // ------------------------------------------------------------------ start screen

        void ShowStart()
        {
            if (m_Root == null) return;
            m_Root.Clear();
            var screen = W.Column("ap-start");
            m_Root.Add(screen);
            var card = W.Column("ap-start-card");
            screen.Add(card);

            var brand = W.Row("ap-start-brand");
            brand.Add(new Icon(IconKind.Brush, 28) { name = "brand-icon" });
            brand.Add(W.Text("Avi Creator Essentials", "ap-start-title"));
            card.Add(brand);
            card.Add(W.Text("Paint straight onto your avatar in 3D or on the texture in 2D, with layers, masks and posing. " +
                            "Your scene and original textures stay untouched until you bake.", "ap-start-lead"));

            if (UpdateChecker.UpdateAvailable)
            {
                var up = W.Row("ap-banner");
                up.Add(new Icon(IconKind.Bake, 16));
                up.Add(W.Text("Version " + UpdateChecker.Latest + " is available (you have " + UpdateChecker.Version + ").", "ap-grow"));
                up.Add(W.TextButton("Update", UpdateChecker.Update, "ap-accent"));
                card.Add(up);
            }

            if (!APResources.ShadersAvailable)
            {
                card.Add(W.Text("The painter's shaders weren't found. Reimport the Avi Creator Essentials folder (right-click it › Reimport).", "ap-error"));
            }

            if (m_PendingAvatar == null) m_PendingAvatar = RememberedAvatar();
            if (m_PendingAvatar == null && Selection.activeGameObject != null && Selection.activeGameObject.scene.IsValid())
                m_PendingAvatar = Selection.activeGameObject;

            var field = new ObjectField("Avatar") { objectType = typeof(GameObject), allowSceneObjects = true, value = m_PendingAvatar };
            field.AddToClassList("ap-avatar-field");
            field.RegisterValueChangedCallback(e => m_PendingAvatar = e.newValue as GameObject);
            card.Add(field);

            var row = W.Row("ap-row-gap", "ap-start-actions");
            row.Add(W.TextButton("Use selected object", () =>
            {
                if (Selection.activeGameObject != null) field.value = Selection.activeGameObject;
            }, "ap-ghost"));
            row.Add(W.Spacer());
            row.Add(W.TextButton("Start painting", () =>
            {
                if (m_PendingAvatar == null)
                {
                    ShowNotification(new GUIContent("Drop your avatar into the Avatar field first"));
                    return;
                }
                StartSession(m_PendingAvatar);
            }, "ap-accent", "ap-large"));
            card.Add(row);

            string recovery = EditorPrefs.GetString(RecoveryKey, "");
            if (!string.IsNullOrEmpty(recovery) && File.Exists(Path.Combine(recovery, ProjectIO.FileName)))
            {
                var banner = W.Row("ap-banner");
                banner.Add(new Icon(IconKind.Reset, 16));
                banner.Add(W.Text("Unsaved painting for " + ProjectIO.ReadAvatarName(recovery) + " was kept when Unity reloaded scripts.", "ap-grow"));
                banner.Add(W.TextButton("Restore", () =>
                {
                    var avatar = m_PendingAvatar ?? FindAvatarByName(ProjectIO.ReadAvatarName(recovery));
                    if (avatar == null)
                    {
                        ShowNotification(new GUIContent("Pick the avatar in the Avatar field, then press Restore"));
                        return;
                    }
                    if (StartSession(avatar, false)) LoadProject(recovery);
                    m_Session?.NotifyStructureChanged();
                    if (m_Session != null) m_Session.dirty = true;
                }, "ap-accent"));
                banner.Add(W.TextButton("Discard", () =>
                {
                    EditorPrefs.DeleteKey(RecoveryKey);
                    ShowStart();
                }, "ap-ghost"));
                card.Add(banner);
            }

            var projects = ProjectIO.List();
            projects.RemoveAll(p => p.recovery);
            if (projects.Count > 0)
            {
                card.Add(W.Text("Saved projects", "ap-subtle-heading"));
                var list = W.Column("ap-project-list");
                foreach (var p in projects)
                {
                    var pr = W.Row("ap-project-row");
                    var info = W.Column("ap-grow");
                    info.Add(W.Text(p.avatarName, "ap-layer-name"));
                    info.Add(W.Text(p.savedAt.ToString("MMM d, HH:mm") + "  ·  " + p.setCount + (p.setCount == 1 ? " material" : " materials"), "ap-layer-sub"));
                    pr.Add(info);
                    var proj = p;
                    pr.Add(W.TextButton("Open", () =>
                    {
                        var avatar = m_PendingAvatar != null && m_PendingAvatar.name == proj.avatarName ? m_PendingAvatar : FindAvatarByName(proj.avatarName) ?? m_PendingAvatar;
                        if (avatar == null)
                        {
                            ShowNotification(new GUIContent("Couldn't find '" + proj.avatarName + "' in the open scenes. Pick it in the Avatar field."));
                            return;
                        }
                        if (StartSession(avatar, false)) LoadProject(proj.folder);
                    }, "ap-small"));
                    pr.Add(W.IconButton(IconKind.Folder, "Show folder", () => EditorUtility.RevealInFinder(Path.Combine(proj.folder, ProjectIO.FileName))));
                    list.Add(pr);
                }
                card.Add(list);
            }

            card.Add(W.Text("Tip: right-click an avatar in the Hierarchy › Avi Creator Essentials › Paint This Avatar.", "ap-hint"));
        }

        static GameObject FindAvatarByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name == name) return root;
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (t.name == name && t.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) return t.gameObject;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ sessions

        public bool StartSession(GameObject avatar, bool offerProject = true)
        {
            if (avatar == null) return false;
            if (!APResources.ShadersAvailable)
            {
                EditorUtility.DisplayDialog("Avi Creator Essentials", "The painter's shaders weren't found. Reimport the Avi Creator Essentials folder.", "OK");
                return false;
            }
            if (avatar.GetComponentInChildren<Renderer>(true) == null)
            {
                EditorUtility.DisplayDialog("Avi Creator Essentials", "'" + avatar.name + "' has no meshes to paint. Pick the avatar's root object.", "OK");
                return false;
            }
            if (m_Session != null && !ConfirmClose()) return false;
            DisposeSession();
            try
            {
                EditorUtility.DisplayProgressBar("Avi Creator Essentials", "Preparing " + avatar.name, 0.3f);
                m_Session = APSession.Create(avatar, m_View, m_Brush);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Avi Creator Essentials", "Couldn't open '" + avatar.name + "': " + e.Message, "OK");
                m_Session = null;
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            RememberAvatar(avatar);
            m_Ctx.session = m_Session;
            m_Thumbs = new ThumbnailCache();
            m_Session.StructureChanged += () => { m_StructureDirty = true; m_V3?.RequestRender(); m_V2?.RequestRender(); };
            m_Session.PixelsChanged += _ => { m_ThumbsDirty = true; m_V3?.RequestRender(); m_V2?.RequestRender(); };
            m_Session.ActiveSetChanged += () => { m_V2?.Fit(); m_StructureDirty = true; };
            m_Session.PoseEdited += () => { m_V3?.RequestRender(); m_V3?.RefreshHit(); m_PosePanel?.RefreshValues(); };
            m_Session.pose.SelectionChanged += () => { m_PosePanel?.RefreshBone(); m_V3?.UpdateHud(); m_V3?.RepaintOverlay(); };
            if (m_View.emissionOnly) m_Session.SetEmissionView(true);
            BuildWorkspace();

            if (offerProject)
            {
                string folder = ProjectIO.DefaultFolder(avatar.name);
                if (File.Exists(Path.Combine(folder, ProjectIO.FileName)))
                {
                    int choice = EditorUtility.DisplayDialogComplex("Avi Creator Essentials",
                        "There's a saved project for '" + avatar.name + "'. Continue where you left off?", "Load saved project", "Start fresh", "");
                    if (choice == 0) LoadProject(folder);
                    else m_Session.projectFolder = null;
                }
            }
            return true;
        }

        void LoadProject(string folder)
        {
            if (m_Session == null) return;
            try
            {
                var warnings = ProjectIO.Load(m_Session, folder);
                if (warnings.Count > 0)
                    EditorUtility.DisplayDialog("Avi Creator Essentials", "Loaded with notes:\n\n" + string.Join("\n", warnings), "OK");
                if (folder == EditorPrefs.GetString(RecoveryKey, "")) EditorPrefs.DeleteKey(RecoveryKey);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Avi Creator Essentials", "Couldn't load the project: " + e.Message, "OK");
            }
            m_StructureDirty = true;
            m_ThumbsDirty = true;
            m_V3?.RequestRender();
            m_V2?.RequestRender();
        }

        bool ConfirmClose()
        {
            if (m_Session == null || !m_Session.dirty) return true;
            int choice = EditorUtility.DisplayDialogComplex("Avi Creator Essentials",
                "Save your painting on '" + m_Session.avatarName + "' before closing it?", "Save", "Cancel", "Don't save");
            if (choice == 1) return false;
            if (choice == 0) Save(false);
            return true;
        }

        void CloseSession()
        {
            if (!ConfirmClose()) return;
            DisposeSession();
            ShowStart();
        }

        void Save(bool chooseFolder)
        {
            if (m_Session == null) return;
            string folder = m_Session.projectFolder;
            if (chooseFolder || string.IsNullOrEmpty(folder))
            {
                folder = ProjectIO.DefaultFolder(m_Session.avatarName);
                if (chooseFolder)
                {
                    Directory.CreateDirectory(APUtil.ProjectsRoot);
                    string picked = EditorUtility.SaveFolderPanel("Save painting project", APUtil.ProjectsRoot, APUtil.SafeFileName(m_Session.avatarName));
                    if (string.IsNullOrEmpty(picked)) return;
                    folder = picked;
                }
            }
            try
            {
                ProjectIO.Save(m_Session, folder);
                m_Session.projectFolder = folder;
                m_Session.dirty = false;
                if (EditorPrefs.GetString(RecoveryKey, "") != "" && ProjectIO.ReadAvatarName(EditorPrefs.GetString(RecoveryKey, "")) == m_Session.avatarName)
                    EditorPrefs.DeleteKey(RecoveryKey);
                ShowNotification(new GUIContent("Project saved"));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Avi Creator Essentials", "Couldn't save: " + e.Message, "OK");
            }
        }

        // ------------------------------------------------------------------ workspace

        void BuildWorkspace()
        {
            if (m_Root == null || m_Session == null) return;
            m_Root.Clear();
            m_ToolButtons.Clear();
            m_PoseButtons.Clear();
            m_TopBarRefresh.Clear();

            m_Root.Add(BuildTopBar());

            var body = W.Row("ap-body");
            m_Root.Add(body);

            m_ToolRail = W.Column("ap-tool-rail");
            body.Add(m_ToolRail);

            m_ViewArea = W.Row("ap-view-area");
            body.Add(m_ViewArea);
            m_V3 = new Viewport3D(m_Ctx);
            m_V2 = new Viewport2D(m_Ctx);
            m_Splitter = new VisualElement();
            m_Splitter.AddToClassList("ap-splitter");
            SetupSplitter();
            m_ViewArea.Add(m_V3);
            m_ViewArea.Add(m_Splitter);
            m_ViewArea.Add(m_V2);

            var panelEdge = new VisualElement();
            panelEdge.AddToClassList("ap-panel-edge");
            body.Add(panelEdge);
            SetupPanelResize(panelEdge);

            m_RightPanel = new ScrollView(ScrollViewMode.Vertical);
            m_RightPanel.AddToClassList("ap-right-panel");
            m_RightPanel.style.width = m_View.rightPanelWidth;
            body.Add(m_RightPanel);
            BuildRightPanel();

            var status = W.Row("ap-status-bar");
            m_Status = W.Text("", "ap-status");
            status.Add(m_Status);
            status.Add(W.Spacer());
            m_Memory = W.Text("", "ap-status-dim");
            status.Add(m_Memory);
            m_Root.Add(status);

            m_ModalLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            m_ModalLayer.AddToClassList("ap-modal-layer");
            m_Root.Add(m_ModalLayer);

            ApplyLayout();
            BuildToolRail();
            RebuildPanels();
            m_V3.UpdateHud();
            m_V2.UpdateHud();
            ShowHint(DefaultHint());
        }

        VisualElement BuildTopBar()
        {
            var bar = W.Row("ap-top-bar");
            var brand = W.Row("ap-brand");
            brand.Add(new Icon(IconKind.Brush, 16));
            brand.Add(W.Text(m_Session.avatarName, "ap-brand-name"));
            brand.tooltip = "Painting a private copy of " + m_Session.avatarName;
            bar.Add(brand);

            var mode = new W.Segmented(new[] { "Paint", "Pose" }, () => m_Ctx.PoseMode ? 1 : 0, i => m_Ctx.PoseMode = i == 1,
                new[] { "Paint textures (Tab)", "Pose the armature (Tab)" });
            mode.AddToClassList("ap-mode-switch");
            bar.Add(mode);
            m_TopBarRefresh.Add(mode.Refresh);

            var layout = W.Row("ap-layout-buttons");
            void LayoutButton(IconKind icon, ViewLayout l, string tip)
            {
                var b = W.IconButton(icon, tip, () => { m_View.layout = l; m_View.Save(); ApplyLayout(); });
                layout.Add(b);
                m_TopBarRefresh.Add(() => W.SetOn(b, m_View.layout == l));
            }
            LayoutButton(IconKind.View3D, ViewLayout.View3D, "3D view only");
            LayoutButton(IconKind.Split, ViewLayout.Split, "3D and texture side by side");
            LayoutButton(IconKind.View2D, ViewLayout.View2D, "Texture (2D) view only");
            bar.Add(layout);

            bar.Add(W.Spacer());

            var toggles = W.Row("ap-top-toggles");
            void Toggle(IconKind icon, string tip, Func<bool> get, Action<bool> set)
            {
                Button b = null;
                b = W.IconButton(icon, tip, () => { set(!get()); W.SetOn(b, get()); m_V3?.RequestRender(); m_V3?.RepaintOverlay(); m_V2?.RequestRender(); });
                toggles.Add(b);
                m_TopBarRefresh.Add(() => W.SetOn(b, get()));
            }
            Toggle(IconKind.Symmetry, "Mirror painting left ↔ right", () => m_Brush.symmetry, v => { m_Brush.symmetry = v; m_Ctx.NotifyBrushChanged(); });
            Toggle(IconKind.Armature, "Show the armature over the model (H)", () => m_View.showArmature, v => { m_View.showArmature = v; m_View.Save(); });
            Toggle(IconKind.Emission, "Emission only: switch the lighting off and show just what glows", () => m_View.emissionOnly, v =>
            {
                m_Session.SetEmissionView(v);
                m_View.Save();
            });
            Toggle(IconKind.View2D, "Show UV wireframe in the texture view", () => m_View.showWire2D, v => { m_View.showWire2D = v; m_View.Save(); });
            bar.Add(toggles);

            var lighting = new DropdownField(new List<string> { "Studio", "Soft", "Flat", "Dramatic", "Night" }, (int)m_View.lighting)
            {
                tooltip = "Viewport lighting (doesn't affect your avatar)",
            };
            lighting.AddToClassList("ap-dropdown");
            lighting.AddToClassList("ap-lighting");
            lighting.RegisterValueChangedCallback(_ => { m_View.lighting = (LightingPreset)lighting.index; m_View.Save(); m_V3?.RequestRender(); });
            m_TopBarRefresh.Add(() => lighting.SetValueWithoutNotify(lighting.choices[(int)m_View.lighting]));
            bar.Add(lighting);

            var hist = W.Row("ap-top-history");
            m_UndoButton = W.IconButton(IconKind.Undo, "Undo (Ctrl+Z)", () => m_Session?.history.PerformUndo());
            m_RedoButton = W.IconButton(IconKind.Redo, "Redo (Ctrl+Y)", () => m_Session?.history.PerformRedo());
            hist.Add(m_UndoButton);
            hist.Add(m_RedoButton);
            bar.Add(hist);

            var save = W.IconTextButton(IconKind.Save, "Save", () => Save(false));
            save.tooltip = "Save the layered project (Ctrl+S). Right-click for Save As.";
            save.AddManipulator(new ContextualMenuManipulator(e => e.menu.AppendAction("Save as…", _ => Save(true))));
            bar.Add(save);
            var bake = W.IconTextButton(IconKind.Bake, "Bake", ShowBakeDialog, "ap-accent");
            bake.tooltip = "Write the final textures as PNGs and put them on your avatar";
            bar.Add(bake);
            m_UpdateButton = W.TextButton("Update", UpdateChecker.Update, "ap-accent");
            m_UpdateButton.tooltip = "A newer version of Avi Creator Essentials is available";
            bar.Add(m_UpdateButton);
            OnUpdateInfo();
            bar.Add(W.IconButton(IconKind.Theme, "Switch between light and dark", () =>
            {
                m_View.theme = m_View.LightTheme ? 1 : 2;
                m_View.Save();
                ApplyTheme();
            }));
            bar.Add(W.IconButton(IconKind.Help, "Shortcuts", ShowHelp));
            bar.Add(W.IconButton(IconKind.Close, "Close this avatar", CloseSession));
            return bar;
        }

        void BuildToolRail()
        {
            m_ToolRail.Clear();
            m_ToolButtons.Clear();
            m_PoseButtons.Clear();
            if (!m_Ctx.PoseMode)
            {
                void T(PaintTool t, IconKind icon, string tip)
                {
                    var b = W.IconButton(icon, tip, () => m_Ctx.Tool = t, "ap-tool");
                    m_ToolButtons[t] = b;
                    m_ToolRail.Add(b);
                }
                T(PaintTool.Brush, IconKind.Brush, "Brush (B)");
                T(PaintTool.Eraser, IconKind.Eraser, "Eraser (E)");
                T(PaintTool.Fill, IconKind.Fill, "Fill (G) — fill a UV island, mesh part, material or similar colors");
                T(PaintTool.Picker, IconKind.Picker, "Color picker (I). Shift-click picks the secondary color.");
                T(PaintTool.Image, IconKind.Image, "Image (T) — move, scale and rotate image layers");
                T(PaintTool.Select, IconKind.Select, "Select UV islands or polygons (S). Painting then stays inside the selection.");
                T(PaintTool.Transform, IconKind.Move, "Transform (V) — pick up part of a layer (draw around it, or click a color) and move, scale, rotate or warp it");
            }
            else
            {
                void P(PoseTool t, IconKind icon, string tip)
                {
                    var b = W.IconButton(icon, tip, () => { m_Ctx.poseTool = t; OnToolChanged(); }, "ap-tool");
                    m_PoseButtons[t] = b;
                    m_ToolRail.Add(b);
                }
                P(PoseTool.Rotate, IconKind.Rotate, "Rotate bones (R)");
                P(PoseTool.Move, IconKind.Move, "Move bones (G)");
            }
            var sep = new VisualElement();
            sep.AddToClassList("ap-rail-sep");
            m_ToolRail.Add(sep);
            m_ToolRail.Add(W.IconButton(IconKind.Frame, "Frame the avatar (F)", () => { m_V3?.Frame(); m_V2?.Fit(); }, "ap-tool"));
            m_ToolRail.Add(W.TextButton("F", () => m_V3?.SetViewAngle(180f, 0f), "ap-tool", "ap-tool-text"));
            m_ToolRail[m_ToolRail.childCount - 1].tooltip = "Front view";
            m_ToolRail.Add(W.TextButton("S", () => m_V3?.SetViewAngle(270f, 0f), "ap-tool", "ap-tool-text"));
            m_ToolRail[m_ToolRail.childCount - 1].tooltip = "Side view";
            m_ToolRail.Add(W.TextButton("B", () => m_V3?.SetViewAngle(0f, 0f), "ap-tool", "ap-tool-text"));
            m_ToolRail[m_ToolRail.childCount - 1].tooltip = "Back view";
            m_ToolRail.Add(W.IconButton(IconKind.Eye, "Zoom to the face", () => m_V3?.FrameFace(), "ap-tool"));
            RefreshToolButtons();
        }

        void RefreshToolButtons()
        {
            foreach (var kv in m_ToolButtons) W.SetOn(kv.Value, kv.Key == m_Ctx.Tool);
            foreach (var kv in m_PoseButtons) W.SetOn(kv.Value, kv.Key == m_Ctx.poseTool);
        }

        void BuildRightPanel()
        {
            m_RightPanel.Clear();

            m_PoseSection = new W.Section("Pose", IconKind.Bone, "pose");
            m_PosePanel = new PosePanel(m_Ctx, CapturePoseThumbnail);
            m_PoseSection.Body.Add(m_PosePanel);
            m_RightPanel.Add(m_PoseSection);

            m_ColorSection = new W.Section("Color", IconKind.Picker, "color");
            m_ColorSection.Body.Add(new ColorPicker(m_Ctx));
            m_RightPanel.Add(m_ColorSection);

            m_BrushSection = new W.Section("Brush", IconKind.Brush, "brush");
            m_BrushPanel = new BrushPanel(m_Ctx);
            m_BrushSection.Body.Add(m_BrushPanel);
            m_RightPanel.Add(m_BrushSection);

            m_LayerSection = new W.Section("Layers", IconKind.Layers, "layers");
            m_LayerPanel = new LayerPanel(m_Ctx, m_Thumbs);
            m_LayerSection.Body.Add(m_LayerPanel);
            m_RightPanel.Add(m_LayerSection);

            m_AdjustSection = new W.Section("Adjustment", IconKind.Theme, "adjust");
            m_AdjustPanel = new AdjustPanel(m_Ctx);
            m_AdjustSection.Body.Add(m_AdjustPanel);
            m_RightPanel.Add(m_AdjustSection);

            m_ImageSection = new W.Section("Image", IconKind.Image, "image");
            m_ImagePanel = new ImagePanel(m_Ctx);
            m_ImageSection.Body.Add(m_ImagePanel);
            m_RightPanel.Add(m_ImageSection);

            m_MaterialSection = new W.Section("Materials", IconKind.FillLayer, "materials");
            m_MaterialsPanel = new MaterialsPanel(m_Ctx);
            m_MaterialSection.Body.Add(m_MaterialsPanel);
            m_RightPanel.Add(m_MaterialSection);

            m_MeshSection = new W.Section("Meshes", IconKind.View3D, "meshes", false);
            m_MeshesPanel = new MeshesPanel(m_Ctx);
            m_MeshSection.Body.Add(m_MeshesPanel);
            m_RightPanel.Add(m_MeshSection);

            m_ViewSection = new W.Section("Viewport", IconKind.Light, "view", false);
            m_ViewSection.Body.Add(new ViewPanel(m_Ctx));
            m_RightPanel.Add(m_ViewSection);
        }

        void RebuildPanels()
        {
            if (m_Session == null || m_RightPanel == null) return;
            bool pose = m_Ctx.PoseMode;
            void Show(VisualElement e, bool on) { if (e != null) e.style.display = on ? DisplayStyle.Flex : DisplayStyle.None; }
            Show(m_PoseSection, pose);
            Show(m_ColorSection, !pose);
            Show(m_BrushSection, !pose);
            Show(m_LayerSection, !pose);
            Show(m_MaterialSection, !pose);
            var layer = m_Session.ActiveSet?.ActiveLayer;
            Show(m_ImageSection, !pose && layer != null && layer.kind == LayerKind.Image);
            Show(m_AdjustSection, !pose && layer != null && layer.kind == LayerKind.Adjustment);
            SyncPickerToLayer(layer);

            if (pose)
            {
                // Posing only needs the bone details; the paint panels are hidden.
                m_PosePanel?.RefreshBone();
            }
            else
            {
                m_LayerPanel?.Rebuild();
                m_MaterialsPanel?.Rebuild();
                m_ImagePanel?.Rebuild();
                m_AdjustPanel?.Rebuild();
            }
            m_MeshesPanel?.Rebuild();
            var title = m_BrushSection?.Q<Label>(className: "ap-section-title");
            if (title != null) title.text = m_Ctx.Tool == PaintTool.Fill ? "Fill" : m_Ctx.Tool == PaintTool.Select ? "Select" : m_Ctx.Tool == PaintTool.Transform ? "Transform" : "Brush";
            foreach (var r in m_TopBarRefresh) r();
            RefreshToolButtons();
            RefreshHistoryButtons();
            m_V3?.UpdateHud();
            m_V2?.UpdateHud();
        }

        /// <summary>
        /// When a fill or recolor layer is opened (selected, or switched to Recolor), the brush takes its color,
        /// so painting continues in the color that layer uses.
        /// </summary>
        void SyncPickerToLayer(PaintLayer layer)
        {
            string id = layer != null ? layer.id + "|" + layer.kind + "|" + (layer.kind == LayerKind.Adjustment ? layer.adjMode.ToString() : "") : null;
            if (id == m_LastActiveLayerId) return;
            m_LastActiveLayerId = id;
            if (layer == null || !layer.HasEditableColor) return;
            if (layer.EditableColor == m_Ctx.primary) return;
            m_SuppressLayerColor = true;
            try { m_Ctx.SetPrimary(layer.EditableColor, true); }
            finally { m_SuppressLayerColor = false; }
        }

        /// <summary>The color wheel edits the selected fill or recolor layer, like Substance's per-layer color pickers.</summary>
        void OnPaintColorChanged()
        {
            if (m_SuppressLayerColor || m_Session == null || !m_View.pickerEditsLayer || m_Ctx.strokeActive || m_Ctx.PoseMode) return;
            var set = m_Session.ActiveSet;
            var layer = set?.ActiveLayer;
            if (layer == null || !layer.HasEditableColor || set.editingMask) return;
            if (layer.EditableColor == m_Ctx.primary) return;
            var c = m_Ctx.primary;
            m_Session.EditLayer(set, layer, "Layer color", l => l.EditableColor = c);
            m_V3?.RequestRender();
            m_V2?.RequestRender();
        }

        Button m_UpdateButton;

        void OnUpdateInfo()
        {
            if (m_UpdateButton != null)
            {
                m_UpdateButton.style.display = UpdateChecker.UpdateAvailable ? DisplayStyle.Flex : DisplayStyle.None;
                m_UpdateButton.text = "Update to " + UpdateChecker.Latest;
            }
            if (m_Session == null && m_Root != null) ShowStart();
        }

        void RefreshHistoryButtons()
        {
            if (m_Session == null || m_UndoButton == null) return;
            var h = m_Session.history;
            m_UndoButton.SetEnabled(h.CanUndo);
            m_RedoButton.SetEnabled(h.CanRedo);
            m_UndoButton.tooltip = h.CanUndo ? "Undo " + h.UndoName + " (Ctrl+Z)" : "Nothing to undo";
            m_RedoButton.tooltip = h.CanRedo ? "Redo " + h.RedoName + " (Ctrl+Y)" : "Nothing to redo";
        }

        void ApplyLayout()
        {
            if (m_V3 == null) return;
            var l = m_View.layout;
            m_V3.style.display = l == ViewLayout.View2D ? DisplayStyle.None : DisplayStyle.Flex;
            m_V2.style.display = l == ViewLayout.View3D ? DisplayStyle.None : DisplayStyle.Flex;
            m_Splitter.style.display = l == ViewLayout.Split ? DisplayStyle.Flex : DisplayStyle.None;
            if (l == ViewLayout.Split)
            {
                m_V3.style.flexGrow = m_View.splitRatio;
                m_V2.style.flexGrow = 1f - m_View.splitRatio;
            }
            else
            {
                m_V3.style.flexGrow = 1f;
                m_V2.style.flexGrow = 1f;
            }
            m_V3.RequestRender();
            m_V2.RequestRender();
            foreach (var r in m_TopBarRefresh) r();
        }

        void SetupSplitter()
        {
            bool dragging = false;
            m_Splitter.RegisterCallback<PointerDownEvent>(e =>
            {
                dragging = true;
                m_Splitter.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            m_Splitter.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging) return;
                var local = m_ViewArea.WorldToLocal(e.position);
                float w = m_ViewArea.contentRect.width;
                if (w < 10) return;
                m_View.splitRatio = Mathf.Clamp(local.x / w, 0.2f, 0.8f);
                ApplyLayout();
            });
            m_Splitter.RegisterCallback<PointerUpEvent>(e =>
            {
                dragging = false;
                if (m_Splitter.HasPointerCapture(e.pointerId)) m_Splitter.ReleasePointer(e.pointerId);
                m_View.Save();
            });
        }

        void SetupPanelResize(VisualElement edge)
        {
            bool dragging = false;
            float startX = 0, startW = 0;
            edge.RegisterCallback<PointerDownEvent>(e =>
            {
                dragging = true;
                startX = e.position.x;
                startW = m_RightPanel.resolvedStyle.width;
                edge.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            edge.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging) return;
                m_View.rightPanelWidth = Mathf.Clamp(startW - (e.position.x - startX), 280f, 560f);
                m_RightPanel.style.width = m_View.rightPanelWidth;
            });
            edge.RegisterCallback<PointerUpEvent>(e =>
            {
                dragging = false;
                if (edge.HasPointerCapture(e.pointerId)) edge.ReleasePointer(e.pointerId);
                m_View.Save();
            });
        }

        // ================================================================== update loop

        void Tick()
        {
            if (m_Session == null) return;
            if (m_Session.clone == null)
            {
                DisposeSession();
                ShowStart();
                return;
            }
            if (m_Session.pose.Animating)
            {
                m_Session.Tick();
                m_V3?.RequestRender();
            }
            if (m_StructureDirty && !m_Ctx.strokeActive)
            {
                m_StructureDirty = false;
                RebuildPanels();
            }
            m_V3?.RenderIfNeeded();
            m_V2?.RenderIfNeeded();
            double now = EditorApplication.timeSinceStartup;
            if (m_ThumbsDirty && !m_Ctx.strokeActive && now - m_LastThumbs > 0.3)
            {
                m_ThumbsDirty = false;
                m_LastThumbs = now;
                m_LayerPanel?.RefreshThumbnails();
            }
            if (m_HintUntil > 0 && now > m_HintUntil)
            {
                m_HintUntil = 0;
                if (m_Status != null) m_Status.text = DefaultHint();
            }
            hasUnsavedChanges = m_Session.dirty;
            saveChangesMessage = "Save your painting on " + m_Session.avatarName + "?";
            if (m_Memory != null && ((int)(now * 2) % 2 == 0))
                m_Memory.text = m_Session.sets.Count + " materials  ·  " + APUtil.FormatBytes(m_Session.MemoryBytes) + " GPU memory" + (m_Session.dirty ? "  ·  unsaved" : "");
        }

        void ShowHint(string text)
        {
            if (m_Status == null) return;
            m_Status.text = text;
            m_HintUntil = EditorApplication.timeSinceStartup + 5.0;
        }

        string DefaultHint()
        {
            if (m_Ctx.PoseMode)
                return "Click a bone, then drag the rings. R / G rotate or move with the mouse; Alt+R / Alt+G reset. Tab returns to painting.";
            switch (m_Ctx.Tool)
            {
                case PaintTool.Fill: return "Click a part of the model or texture to fill it. Change what gets filled in the Fill section.";
                case PaintTool.Picker: return "Click to pick a color. Shift-click sets the secondary color.";
                case PaintTool.Image: return "Import an image from the Layers section, then drag it on the model or in the texture view.";
                case PaintTool.Transform: return "Draw around part of the layer in the texture view, or click a color, to pick it up. Then drag, scale, rotate or warp it. Enter applies, Esc cancels.";
                default:
                    return "Left-drag to paint. Right-drag orbits, middle-drag pans, scroll zooms. Right-click for the color wheel. Ctrl+right-drag resizes the brush.";
            }
        }

        PaintTool m_LastTool = PaintTool.Brush;

        void OnToolChanged()
        {
            if (m_Session == null) return;
            // Leaving the Transform tool puts any floating piece down, like committing a transform in Photoshop.
            if (m_LastTool == PaintTool.Transform && (m_Ctx.Tool != PaintTool.Transform || m_Ctx.PoseMode))
                m_Session.ApplyAllFloating();
            m_LastTool = m_Ctx.PoseMode ? PaintTool.Brush : m_Ctx.Tool;
            if (m_ToolRail != null)
            {
                bool poseRail = m_PoseButtons.Count > 0;
                if (poseRail != m_Ctx.PoseMode)
                {
                    BuildToolRail();
                    if (m_Ctx.PoseMode) m_PosePanel?.RebuildLibrary();
                }
            }
            RefreshToolButtons();
            m_StructureDirty = true;
            m_V3?.UpdateHud();
            m_V3?.RepaintOverlay();
            m_V2?.RepaintOverlay();
            if (m_Status != null) m_Status.text = DefaultHint();
            m_HintUntil = 0;
        }

        // ================================================================== shortcuts

        void OnKeyDown(KeyDownEvent evt)
        {
            if (m_Session == null) return;
            if (W.IsEditingText(m_Root.panel?.focusController?.focusedElement)) return;

            bool handled = true;
            var key = evt.keyCode;
            bool ctrl = evt.actionKey, alt = evt.altKey, shift = evt.shiftKey;

            if (key == KeyCode.None)
            {
                return;
            }

            if (key == KeyCode.Escape && QuickColorPopup.IsOpen)
            {
                QuickColorPopup.Close();
                evt.StopPropagation();
                return;
            }

            if (ctrl && key == KeyCode.S)
            {
                Save(shift);
            }
            else if (key == KeyCode.Tab)
            {
                m_V3?.CancelModal();
                m_Ctx.PoseMode = !m_Ctx.PoseMode;
            }
            else if (m_V3 != null && m_V3.InModal)
            {
                switch (key)
                {
                    case KeyCode.X: m_V3.ModalAxis(0); break;
                    case KeyCode.Y: m_V3.ModalAxis(1); break;
                    case KeyCode.Z: m_V3.ModalAxis(2); break;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                    case KeyCode.Space: m_V3.ConfirmModal(); break;
                    case KeyCode.Escape: m_V3.CancelModal(); break;
                    default: handled = false; break;
                }
            }
            else if (ctrl && !m_Ctx.PoseMode && (key == KeyCode.A || key == KeyCode.D || key == KeyCode.I))
            {
                var set = m_Session.ActiveSet;
                if (key == KeyCode.A) m_Session.SelectAll(set);
                else if (key == KeyCode.D) m_Session.ClearSelection(set);
                else m_Session.InvertSelection(set);
            }
            else if (ctrl)
            {
                handled = false;
            }
            else if (m_Ctx.PoseMode)
            {
                var pose = m_Session.pose;
                switch (key)
                {
                    case KeyCode.R when alt:
                        ResetSelected(true, false);
                        break;
                    case KeyCode.G when alt:
                        ResetSelected(false, true);
                        break;
                    case KeyCode.R:
                        m_Ctx.poseTool = PoseTool.Rotate;
                        if (m_V3 != null && m_V3.MouseInside && pose.selected != null) m_V3.StartModal(true);
                        OnToolChanged();
                        break;
                    case KeyCode.G:
                        m_Ctx.poseTool = PoseTool.Move;
                        if (m_V3 != null && m_V3.MouseInside && pose.selected != null) m_V3.StartModal(false);
                        OnToolChanged();
                        break;
                    case KeyCode.F: m_V3?.Frame(); break;
                    case KeyCode.H: m_View.showArmature = !m_View.showArmature; m_View.Save(); m_StructureDirty = true; m_V3?.RepaintOverlay(); break;
                    case KeyCode.Escape: pose.Select(null); break;
                    case KeyCode.UpArrow: if (pose.selected?.parent != null) pose.Select(pose.selected.parent); break;
                    case KeyCode.DownArrow: if (pose.selected?.tailChild != null) pose.Select(pose.selected.tailChild); break;
                    default: handled = false; break;
                }
            }
            else
            {
                bool in2D = m_V2 != null && m_V2.MouseInside;
                switch (key)
                {
                    case KeyCode.B: m_Ctx.Tool = PaintTool.Brush; break;
                    case KeyCode.E: m_Ctx.Tool = PaintTool.Eraser; break;
                    case KeyCode.G: m_Ctx.Tool = PaintTool.Fill; break;
                    case KeyCode.I: m_Ctx.Tool = PaintTool.Picker; break;
                    case KeyCode.T: m_Ctx.Tool = PaintTool.Image; break;
                    case KeyCode.S: m_Ctx.Tool = PaintTool.Select; break;
                    case KeyCode.V: m_Ctx.Tool = PaintTool.Transform; break;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                    {
                        var set = m_Session.ActiveSet;
                        var floating = m_Session.FloatingLayer(set);
                        if (floating == null) { handled = false; break; }
                        m_Session.ApplyFloating(set, floating);
                        ShowHint("Transform applied.");
                        break;
                    }
                    case KeyCode.X: m_Ctx.SwapColors(); break;
                    case KeyCode.H: m_View.showArmature = !m_View.showArmature; m_View.Save(); m_StructureDirty = true; m_V3?.RepaintOverlay(); break;
                    case KeyCode.M:
                    {
                        var set = m_Session.ActiveSet;
                        var layer = set?.ActiveLayer;
                        if (layer != null)
                        {
                            if (!layer.HasMask) m_Session.AddMask(set, layer, 1f);
                            else m_Session.SetActiveLayer(set, layer, !set.editingMask);
                        }
                        break;
                    }
                    case KeyCode.LeftBracket:
                    case KeyCode.RightBracket:
                    {
                        float k = key == KeyCode.LeftBracket ? 1f / 1.15f : 1.15f;
                        if (shift) m_Brush.hardness = Mathf.Clamp01(m_Brush.hardness + (key == KeyCode.LeftBracket ? -0.1f : 0.1f));
                        else if (in2D) m_Brush.radius2D = Mathf.Clamp(m_Brush.radius2D * k, 0.0002f, 0.5f);
                        else m_Brush.radius = Mathf.Clamp(m_Brush.radius * k, 0.0005f, 1f);
                        m_Ctx.NotifyBrushChanged();
                        m_BrushPanel?.Refresh();
                        break;
                    }
                    case KeyCode.F:
                        if (in2D) m_V2.Fit();
                        else m_V3?.Frame();
                        break;
                    case KeyCode.Escape:
                    {
                        m_V3?.CancelStroke();
                        m_V2?.CancelStroke();
                        var set = m_Session.ActiveSet;
                        var floating = m_Ctx.Tool == PaintTool.Transform ? m_Session.FloatingLayer(set) : null;
                        if (floating != null)
                        {
                            m_Session.CancelFloating(set, floating);
                            ShowHint("Transform cancelled.");
                        }
                        break;
                    }
                    default:
                        if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                        {
                            int n = key - KeyCode.Alpha0;
                            m_Brush.opacity = n == 0 ? 1f : n / 10f;
                            m_Ctx.NotifyBrushChanged();
                            m_BrushPanel?.Refresh();
                            ShowHint("Brush opacity " + Mathf.RoundToInt(m_Brush.opacity * 100) + "%");
                        }
                        else handled = false;
                        break;
                }
            }

            if (handled)
            {
                evt.StopPropagation();
                evt.PreventDefault();
            }
        }

        void ResetSelected(bool rotation, bool position)
        {
            var pose = m_Session.pose;
            var b = pose.selected;
            if (b == null) return;
            var before = pose.Capture();
            pose.ResetBone(b, rotation, position);
            var after = pose.Capture();
            m_Session.Push(new ActionEntry("Reset bone", () => pose.Restore(before), () => pose.Restore(after)));
            m_V3?.RequestRender();
        }

        // ================================================================== pose thumbnails

        Texture2D CapturePoseThumbnail()
        {
            if (m_Session == null || m_V3 == null) return null;
            const int size = 160;
            var fmt = APUtil.LinearProject ? UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB
                : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm;
            var rt = APUtil.CreateRT(size, size, fmt, "PoseThumb", false, FilterMode.Bilinear, TextureWrapMode.Clamp, 24, 4);
            var cam = m_V3.CurrentCamera;
            var b = m_Session.meshes.VisibleBounds();
            float dist = b.extents.magnitude / Mathf.Sin(cam.fov * 0.5f * Mathf.Deg2Rad) * 0.85f;
            cam.position = b.center - cam.rotation * Vector3.forward * dist;
            m_Session.Render(rt, cam);
            var resolved = APUtil.CreateRT(size, size, fmt, "PoseThumbResolved");
            Graphics.Blit(rt, resolved);
            var tex = APUtil.ReadToTexture(resolved, TextureFormat.RGBA32, !APUtil.LinearProject);
            tex.hideFlags = HideFlags.None;
            APUtil.Release(ref rt);
            APUtil.Release(ref resolved);
            return tex;
        }

        // ================================================================== bake dialog

        void ShowBakeDialog()
        {
            if (m_Session == null) return;
            var painted = new List<TextureSet>();
            foreach (var s in m_Session.sets)
                if (s.initialized) painted.Add(s);
            m_ModalLayer.Clear();
            m_ModalLayer.pickingMode = PickingMode.Position;
            m_ModalLayer.AddToClassList("ap-modal-open");
            var dlg = W.Column("ap-dialog");
            m_ModalLayer.Add(dlg);
            dlg.Add(W.Text("Bake textures", "ap-dialog-title"));
            if (painted.Count == 0)
            {
                dlg.Add(W.Text("Nothing has been painted yet. Paint on a material first, then bake it.", "ap-hint"));
                var close = W.Row("ap-row-gap");
                close.Add(W.Spacer());
                close.Add(W.TextButton("Close", CloseModal, "ap-ghost"));
                dlg.Add(close);
                return;
            }
            dlg.Add(W.Text("Each painted material is flattened into a PNG with padding around the UV islands so no seams show, " +
                           "even on distant mip levels.", "ap-hint"));
            var selected = new HashSet<TextureSet>(painted);
            var list = W.Column("ap-bake-list");
            foreach (var s in painted)
            {
                var set = s;
                var t = new Toggle(s.displayName + "   " + s.width + "×" + s.height) { value = true };
                t.AddToClassList("ap-toggle");
                t.RegisterValueChangedCallback(e => { if (e.newValue) selected.Add(set); else selected.Remove(set); });
                list.Add(t);
            }
            dlg.Add(list);
            var opts = new Baker.Options { padding = m_View.bakePadding, fillAll = m_View.bakeFillAll, assignToAvatar = true };
            dlg.Add(new W.ToggleRow("Extend colors into all empty space", () => opts.fillAll, v => { opts.fillAll = v; m_View.bakeFillAll = v; m_View.Save(); },
                "Recommended: every unused pixel takes the nearest painted color, which compresses well and never bleeds"));
            dlg.Add(new W.SliderRow("Padding (px)", 2f, 128f, () => opts.padding, v => { opts.padding = Mathf.RoundToInt(v); m_View.bakePadding = opts.padding; m_View.Save(); }, 1f,
                "Used when the option above is off"));
            dlg.Add(new W.ToggleRow("Put the textures on my avatar", () => opts.assignToAvatar, v => opts.assignToAvatar = v,
                "Creates '_Painted' copies of the materials holding the new textures and assigns them to the avatar in the scene (undoable). Your original materials are not changed."));
            dlg.Add(W.Text("Output: " + APUtil.OutputFolder + "/" + APUtil.SafeFileName(m_Session.avatarName), "ap-hint"));
            var buttons = W.Row("ap-row-gap");
            buttons.Add(W.Spacer());
            buttons.Add(W.TextButton("Cancel", CloseModal, "ap-ghost"));
            buttons.Add(W.TextButton("Bake", () =>
            {
                var chosen = new List<TextureSet>();
                foreach (var s in painted)
                    if (selected.Contains(s)) chosen.Add(s);
                CloseModal();
                if (chosen.Count == 0) return;
                try
                {
                    var files = Baker.Bake(m_Session, chosen, opts);
                    if (files.Count > 0)
                    {
                        var first = AssetDatabase.LoadAssetAtPath<Object>(files[0]);
                        if (first != null) EditorGUIUtility.PingObject(first);
                    }
                    ShowNotification(new GUIContent("Baked " + files.Count + (files.Count == 1 ? " texture" : " textures")));
                    m_StructureDirty = true;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    EditorUtility.DisplayDialog("Avi Creator Essentials", "Baking failed: " + e.Message, "OK");
                }
            }, "ap-accent"));
            dlg.Add(buttons);
        }

        void CloseModal()
        {
            m_ModalLayer.Clear();
            m_ModalLayer.pickingMode = PickingMode.Ignore;
            m_ModalLayer.RemoveFromClassList("ap-modal-open");
        }

        void ShowHelp()
        {
            m_ModalLayer.Clear();
            m_ModalLayer.pickingMode = PickingMode.Position;
            m_ModalLayer.AddToClassList("ap-modal-open");
            var dlg = W.Column("ap-dialog", "ap-help");
            m_ModalLayer.Add(dlg);
            dlg.Add(W.Text("Shortcuts", "ap-dialog-title"));
            void Group(string title, string[,] rows)
            {
                dlg.Add(W.Text(title, "ap-subtle-heading"));
                for (int i = 0; i < rows.GetLength(0); i++)
                {
                    var r = W.Row("ap-help-row");
                    r.Add(W.Text(rows[i, 0], "ap-kbd"));
                    r.Add(W.Text(rows[i, 1], "ap-help-text"));
                    dlg.Add(r);
                }
            }
            Group("View", new[,]
            {
                { "Right-drag / Alt+Left-drag", "Orbit (around the point under the cursor)" },
                { "Middle-drag (with or without Alt) / Shift+Right-drag", "Pan" },
                { "Scroll / Alt+Right-drag / Ctrl+Middle-drag", "Zoom" },
                { "F", "Frame the avatar (or fit the texture in the 2D view)" },
                { "Tab", "Switch between painting and posing" },
                { "H", "Show or hide the armature" },
            });
            Group("Painting", new[,]
            {
                { "B  E  G  I  T  S  V", "Brush, eraser, fill, color picker, image, select, transform" },
                { "Enter  /  Esc (transform)", "Apply / cancel the floating piece" },
                { "Ctrl+A  /  Ctrl+D  /  Ctrl+I", "Select all / clear / invert the UV selection" },
                { "[  ]", "Brush size (Shift: hardness)" },
                { "Ctrl+Right-drag / Ctrl+scroll", "Brush size (drag up/down for hardness; scroll down grows)" },
                { "Right-click", "Quick color wheel with recent colors under the cursor" },
                { "1 … 0", "Opacity 10 … 100%" },
                { "X", "Swap paint colors" },
                { "M", "Add a mask, or switch between the layer and its mask" },
                { "Shift+click (fill)", "Fill with transparency" },
                { "Ctrl+Z  /  Ctrl+Y", "Undo / redo" },
                { "Ctrl+S", "Save the project" },
            });
            Group("Posing", new[,]
            {
                { "Click", "Select a bone" },
                { "R  /  G", "Rotate / move the bone with the mouse; X Y Z to lock an axis (twice for local)" },
                { "Alt+R  /  Alt+G", "Reset the bone's rotation / position" },
                { "↑  /  ↓", "Select parent / child bone" },
                { "Ctrl while dragging a ring", "Snap to 5°" },
            });
            var close = W.Row("ap-row-gap");
            close.Add(W.Spacer());
            close.Add(W.TextButton("Close", CloseModal, "ap-ghost"));
            dlg.Add(close);
        }
    }
}
