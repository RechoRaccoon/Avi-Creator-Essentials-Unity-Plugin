using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Overlays;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Which avatar the Scene view bone tool is working on, and its armature.
    /// Edits go through Unity's Undo, so the Animation window records them while in record mode.
    /// </summary>
    [InitializeOnLoad]
    public static class ScenePoseState
    {
        const string PrefKey = "AviCreatorEssentials.ScenePose.";
        const string SessionAvatarKey = "AviCreatorEssentials.ScenePose.Avatar";
        const string SessionMergeKey = "AviCreatorEssentials.ScenePose.Merged.";

        static Transform s_Avatar;
        static PoseSystem s_Pose;
        static ArmatureLinker s_Linker;
        static bool s_Dirty = true;
        static readonly HashSet<Transform> s_MergedCloth = new HashSet<Transform>();

        /// <summary>Clothing armatures follow the body bones live, without changing the hierarchy.</summary>
        public static bool SyncClothing
        {
            get => EditorPrefs.GetBool(PrefKey + "SyncClothing", true);
            set
            {
                EditorPrefs.SetBool(PrefKey + "SyncClothing", value);
                s_Dirty = true;
                Changed?.Invoke();
                SceneView.RepaintAll();
            }
        }

        public static ArmatureLinker Linker
        {
            get
            {
                var p = Pose;
                return p != null ? s_Linker : null;
            }
        }

        public static bool RotateMode
        {
            get => EditorPrefs.GetBool(PrefKey + "Rotate", true);
            set => EditorPrefs.SetBool(PrefKey + "Rotate", value);
        }

        public static bool HumanoidOnly
        {
            get => EditorPrefs.GetBool(PrefKey + "Humanoid", false);
            set => EditorPrefs.SetBool(PrefKey + "Humanoid", value);
        }

        public static float BoneSize
        {
            get => EditorPrefs.GetFloat(PrefKey + "BoneSize", 1f);
            set => EditorPrefs.SetFloat(PrefKey + "BoneSize", value);
        }

        public static float BoneOpacity
        {
            get => EditorPrefs.GetFloat(PrefKey + "Opacity", 0.55f);
            set => EditorPrefs.SetFloat(PrefKey + "Opacity", value);
        }

        public static event Action Changed;

        static ScenePoseState()
        {
            EditorApplication.hierarchyChanged += () => s_Dirty = true;
            Selection.selectionChanged += OnSelectionChanged;
            Undo.undoRedoPerformed += () => { s_Dirty = true; Changed?.Invoke(); };
            EditorApplication.update += SyncTick;
            EditorApplication.delayCall += () =>
            {
                int id = SessionState.GetInt(SessionAvatarKey, 0);
                if (id != 0 && s_Avatar == null) s_Avatar = EditorUtility.InstanceIDToObject(id) as Transform;
            };
        }

        /// <summary>Keeps clothing bones on their body bones every editor frame (also while scrubbing animations).</summary>
        static void SyncTick()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || s_Avatar == null || !SyncClothing) return;
            var linker = Linker;
            if (linker == null || !linker.HasClothing) return;
            if (linker.Apply()) SceneView.RepaintAll();
        }

        public static Transform Avatar => s_Avatar;

        public static PoseSystem Pose
        {
            get
            {
                if (s_Avatar == null) return null;
                if (s_Dirty || s_Pose == null || s_Pose.Root != s_Avatar) Rebuild();
                return s_Pose;
            }
        }

        public static Bone Selected
        {
            get
            {
                var pose = Pose;
                if (pose == null) return null;
                var t = Selection.activeTransform;
                if (t == null) return null;
                foreach (var b in pose.bones)
                    if (b.t == t) return b;
                return null;
            }
        }

        static void OnSelectionChanged()
        {
            var t = Selection.activeTransform;
            if (t == null) { Changed?.Invoke(); return; }
            if (s_Avatar != null && t.IsChildOf(s_Avatar)) { Changed?.Invoke(); return; }
            var root = FindAvatarRoot(t);
            if (root != null && root != s_Avatar)
            {
                s_Avatar = root;
                SessionState.SetInt(SessionAvatarKey, root.GetInstanceID());
                s_Dirty = true;
            }
            Changed?.Invoke();
        }

        /// <summary>Nearest ancestor (or self) with an Animator, else the top of the hierarchy if it has skinned meshes.</summary>
        public static Transform FindAvatarRoot(Transform t)
        {
            if (t == null) return null;
            for (var p = t; p != null; p = p.parent)
                if (p.GetComponent<Animator>() != null) return p;
            var top = t.root;
            return top.GetComponentInChildren<SkinnedMeshRenderer>(true) != null ? top : null;
        }

        public static void SetAvatar(Transform root)
        {
            s_Avatar = root;
            SessionState.SetInt(SessionAvatarKey, root != null ? root.GetInstanceID() : 0);
            s_Dirty = true;
            Changed?.Invoke();
        }

        public static void EnsureFromSelection()
        {
            if (s_Avatar == null && Selection.activeTransform != null) SetAvatar(FindAvatarRoot(Selection.activeTransform));
        }

        static void Rebuild()
        {
            s_Dirty = false;
            if (s_Avatar == null) { s_Pose = null; return; }
            var human = new Dictionary<string, HumanBodyBones>();
            var animator = s_Avatar.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    Transform bt = null;
                    try { bt = animator.GetBoneTransform((HumanBodyBones)i); }
                    catch (Exception) { /* ignore */ }
                    if (bt != null && bt.IsChildOf(s_Avatar)) human[APUtil.GetPath(bt, s_Avatar)] = (HumanBodyBones)i;
                }
            }
            s_Pose = new PoseSystem();
            s_Pose.Build(s_Avatar, human);
            s_Pose.SetRest(RestOf);

            Transform hips = null;
            foreach (var kv in human)
                if (kv.Value == HumanBodyBones.Hips) hips = APUtil.FindPath(s_Avatar, kv.Key);
            s_Linker = ArmatureLinker.Detect(s_Avatar, hips);
            s_Linker.CaptureOffsets(t =>
            {
                var o = PrefabUtility.GetCorrespondingObjectFromOriginalSource(t);
                if (o == null || o == t) return null;
                return (o.localRotation, o.localPosition);
            });
            LoadMergeRecord();
            bool sync = SyncClothing && s_Linker.HasClothing;
            s_Pose.SetLinkHidden(t => (sync && s_Linker.IsLinkedClothBone(t)) || IsMergedCloth(t));
        }

        static bool IsMergedCloth(Transform t)
        {
            if (t == null) return false;
            if (s_MergedCloth.Contains(t)) return true;
            // A bone sitting directly under a body bone of the same name was merged earlier (by us or by hand).
            var p = t.parent;
            return p != null && ArmatureLinker.Norm(t.name, "", "") == ArmatureLinker.Norm(p.name, "", "") &&
                   p.parent != null && t.GetComponent<SkinnedMeshRenderer>() == null;
        }

        // ------------------------------------------------------------------ merging

        [Serializable]
        class MergeEntry
        {
            public int child;
            public int oldParent;
            public int siblingIndex;
        }

        [Serializable]
        class MergeRecord
        {
            public List<MergeEntry> entries = new List<MergeEntry>();
        }

        static string MergeKey => SessionMergeKey + (s_Avatar != null ? s_Avatar.GetInstanceID().ToString() : "none");

        static void LoadMergeRecord()
        {
            s_MergedCloth.Clear();
            var json = SessionState.GetString(MergeKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var rec = JsonUtility.FromJson<MergeRecord>(json);
            foreach (var e in rec.entries)
                if (EditorUtility.InstanceIDToObject(e.child) is Transform t) s_MergedCloth.Add(t);
        }

        public static bool CanUnmerge => !string.IsNullOrEmpty(SessionState.GetString(MergeKey, ""));

        /// <summary>
        /// Really parents every matching clothing bone under its body bone (what VRCFury does at upload time).
        /// Extra clothing-only bones keep their clothing parent, which now lives on the body. Undoable.
        /// </summary>
        public static void MergeClothing()
        {
            var linker = Linker;
            if (linker == null || !linker.HasClothing) return;
            var root = s_Avatar.gameObject;
            bool isPrefab = PrefabUtility.IsPartOfPrefabInstance(root) ||
                            root.GetComponentsInChildren<Transform>(true).Length > 0 && AnyPrefabInstance(root);
            string msg = "Parent " + linker.LinkCount + " clothing bones under the matching bones of the body armature?\n\n" +
                         "Clothing-only bones (strings, skirt bones...) stay attached to their clothing parent, which moves onto the body.";
            if (isPrefab)
                msg += "\n\nThis avatar is (or contains) a prefab instance. Unity doesn't allow re-parenting inside prefabs, so it will be unpacked first.";
            msg += "\n\nCtrl+Z undoes all of it. If you use VRCFury Armature Link, you don't need this: VRCFury merges at upload.";
            if (!EditorUtility.DisplayDialog("Merge clothing armatures", msg, "Merge", "Cancel")) return;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Merge clothing armatures");
            if (isPrefab && PrefabUtility.IsPartOfPrefabInstance(root))
                PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(root), PrefabUnpackMode.Completely, InteractionMode.UserAction);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!PrefabUtility.IsPartOfPrefabInstance(t.gameObject)) continue;
                var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject);
                if (outer != null) PrefabUtility.UnpackPrefabInstance(outer, PrefabUnpackMode.Completely, InteractionMode.UserAction);
            }

            var rec = new MergeRecord();
            foreach (var l in linker.Links)
            {
                if (l.body == null || l.cloth == null || l.cloth.parent == l.body) continue;
                rec.entries.Add(new MergeEntry
                {
                    child = l.cloth.GetInstanceID(),
                    oldParent = l.cloth.parent != null ? l.cloth.parent.GetInstanceID() : 0,
                    siblingIndex = l.cloth.GetSiblingIndex(),
                });
            }
            foreach (var l in linker.Links)
            {
                if (l.body == null || l.cloth == null || l.cloth.parent == l.body) continue;
                // Snap onto the body bone's current pose first so the outfit sits exactly where it should.
                Undo.RecordObject(l.cloth, "Merge clothing armatures");
                l.cloth.SetPositionAndRotation(l.body.TransformPoint(l.offsetPos), l.body.rotation * l.offsetRot);
                Undo.SetTransformParent(l.cloth, l.body, "Merge clothing armatures");
            }
            Undo.CollapseUndoOperations(group);
            SessionState.SetString(MergeKey, JsonUtility.ToJson(rec));
            s_Dirty = true;
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        static bool AnyPrefabInstance(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (PrefabUtility.IsPartOfPrefabInstance(t.gameObject)) return true;
            return false;
        }

        /// <summary>Puts merged clothing bones back under their own armatures (this editor session only; otherwise use Undo).</summary>
        public static void UnmergeClothing()
        {
            var json = SessionState.GetString(MergeKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var rec = JsonUtility.FromJson<MergeRecord>(json);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            for (int i = rec.entries.Count - 1; i >= 0; i--)
            {
                var e = rec.entries[i];
                var child = EditorUtility.InstanceIDToObject(e.child) as Transform;
                var parent = EditorUtility.InstanceIDToObject(e.oldParent) as Transform;
                if (child == null || parent == null) continue;
                Undo.SetTransformParent(child, parent, "Unmerge clothing armatures");
                child.SetSiblingIndex(Mathf.Clamp(e.siblingIndex, 0, parent.childCount - 1));
            }
            Undo.CollapseUndoOperations(group);
            SessionState.EraseString(MergeKey);
            s_Dirty = true;
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        /// <summary>Rest pose of a bone: the value from the avatar's prefab or model file when there is one.</summary>
        static (Quaternion rot, Vector3 pos)? RestOf(Bone b)
        {
            var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(b.t);
            if (src != null && src != b.t) return (src.localRotation, src.localPosition);
            return null;
        }

        public static Transform[] BoneTransforms()
        {
            var pose = Pose;
            if (pose == null) return Array.Empty<Transform>();
            var list = new List<Transform>();
            foreach (var b in pose.bones)
                if (b.t != null) list.Add(b.t);
            return list.ToArray();
        }

        /// <summary>Applies a whole-armature pose as one undo step (and as keys when the Animation window records).</summary>
        public static void ApplySnapshot(PoseSnapshot snap, string undoName)
        {
            var pose = Pose;
            if (pose == null || snap == null) return;
            var transforms = BoneTransforms();
            Undo.RecordObjects(transforms, undoName);
            for (int i = 0; i < pose.bones.Count && i < snap.rotations.Length; i++)
            {
                var b = pose.bones[i];
                if (b.t == null) continue;
                if (Quaternion.Angle(b.t.localRotation, snap.rotations[i]) > 0.0001f) b.t.localRotation = snap.rotations[i];
                if ((b.t.localPosition - snap.positions[i]).sqrMagnitude > 1e-12f) b.t.localPosition = snap.positions[i];
            }
            foreach (var t in transforms) PrefabUtility.RecordPrefabInstancePropertyModifications(t);
            SceneView.RepaintAll();
            Changed?.Invoke();
        }

        public static void ResetBone(Bone b, bool rotation, bool position)
        {
            if (b == null || b.t == null) return;
            Undo.RecordObject(b.t, "Reset bone");
            if (rotation) b.t.localRotation = b.restLocalRot;
            if (position) b.t.localPosition = b.restLocalPos;
            PrefabUtility.RecordPrefabInstancePropertyModifications(b.t);
            SceneView.RepaintAll();
            Changed?.Invoke();
        }

        public static void NotifyChanged() => Changed?.Invoke();
    }

    /// <summary>
    /// Scene view tool: draws the armature over the avatar, click bones to select them, rotate/move with Unity's handles.
    /// </summary>
    [EditorTool("Bone Posing (Avi Creator Essentials)")]
    public class ScenePoseTool : EditorTool
    {
        static readonly int s_Hash = "AviCreatorEssentialsBone".GetHashCode();
        GUIContent m_Icon;
        Quaternion m_GlobalHandleRot = Quaternion.identity;
        readonly List<KeyValuePair<Bone, float>> m_Order = new List<KeyValuePair<Bone, float>>();
        readonly Vector3[] m_Tri = new Vector3[3];
        readonly Vector3[] m_Outline = new Vector3[5];

        public override GUIContent toolbarIcon
        {
            get
            {
                if (m_Icon == null)
                {
                    var c = EditorGUIUtility.IconContent("AvatarSelector");
                    m_Icon = new GUIContent(c != null ? c.image : null, "Bone Posing (Avi Creator Essentials)");
                    if (m_Icon.image == null) m_Icon.text = "Bones";
                }
                return m_Icon;
            }
        }

        [MenuItem("Tools/Avi Creator Essentials/Bone Posing (Scene View)", false, 101)]
        public static void Activate()
        {
            ScenePoseState.EnsureFromSelection();
            ToolManager.SetActiveTool<ScenePoseTool>();
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.Focus();
        }

        [MenuItem("GameObject/Avi Creator Essentials/Pose In Scene View", false, 21)]
        static void PoseSelected()
        {
            if (Selection.activeTransform != null)
                ScenePoseState.SetAvatar(ScenePoseState.FindAvatarRoot(Selection.activeTransform) ?? Selection.activeTransform);
            Activate();
        }

        public override void OnActivated()
        {
            ScenePoseState.EnsureFromSelection();
            SceneView.RepaintAll();
        }

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView sv)) return;
            var pose = ScenePoseState.Pose;
            if (pose == null) return;
            var cam = sv.camera;
            if (cam == null) return;
            var e = Event.current;
            bool humanoidOnly = ScenePoseState.HumanoidOnly;
            var selected = ScenePoseState.Selected;

            // ---- picking: one control per bone
            var ids = new Dictionary<Bone, int>();
            foreach (var b in pose.bones)
            {
                if (!PoseOverlay.ShowBone(b, humanoidOnly)) continue;
                int id = GUIUtility.GetControlID(s_Hash, FocusType.Passive);
                ids[b] = id;
                if (e.type == EventType.Layout || e.type == EventType.MouseMove)
                {
                    float d = HandleUtility.DistanceToLine(b.Head, TailOf(b, humanoidOnly));
                    HandleUtility.AddControl(id, Mathf.Max(1.5f, d - 3f));
                }
            }

            if (e.type == EventType.Repaint) DrawBones(pose, cam, ids, selected, humanoidOnly);

            // ---- handles for the selected bone (drawn over the bones; they also win picking ties)
            if (selected != null && selected.t != null)
            {
                var t = selected.t;
                bool local = Tools.pivotRotation == PivotRotation.Local;
                if (GUIUtility.hotControl == 0) m_GlobalHandleRot = Quaternion.identity;
                var handleRot = local ? t.rotation : m_GlobalHandleRot;
                if (ScenePoseState.RotateMode)
                {
                    EditorGUI.BeginChangeCheck();
                    var r = Handles.RotationHandle(handleRot, t.position);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(t, "Rotate bone");
                        t.rotation = r * Quaternion.Inverse(handleRot) * t.rotation;
                        if (!local) m_GlobalHandleRot = r;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(t);
                        ScenePoseState.NotifyChanged();
                    }
                }
                else
                {
                    EditorGUI.BeginChangeCheck();
                    var p = Handles.PositionHandle(t.position, local ? t.rotation : Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(t, "Move bone");
                        t.position = p;
                        PrefabUtility.RecordPrefabInstancePropertyModifications(t);
                        ScenePoseState.NotifyChanged();
                    }
                }
            }

            // ---- clicking a bone selects it
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                foreach (var kv in ids)
                {
                    if (HandleUtility.nearestControl != kv.Value) continue;
                    if (e.shift || EditorGUI.actionKey)
                    {
                        var list = new List<UnityEngine.Object>(Selection.objects);
                        if (list.Contains(kv.Key.t.gameObject)) list.Remove(kv.Key.t.gameObject);
                        else list.Add(kv.Key.t.gameObject);
                        Selection.objects = list.ToArray();
                    }
                    else
                    {
                        Selection.activeTransform = kv.Key.t;
                    }
                    e.Use();
                    break;
                }
            }

            // ---- keyboard: Alt+R / Alt+G reset like Blender
            if (e.type == EventType.KeyDown && e.alt && selected != null)
            {
                if (e.keyCode == KeyCode.R) { ScenePoseState.ResetBone(selected, true, false); e.Use(); }
                else if (e.keyCode == KeyCode.G) { ScenePoseState.ResetBone(selected, false, true); e.Use(); }
            }

        }

        static Vector3 TailOf(Bone b, bool humanoidOnly)
        {
            if (!humanoidOnly) return b.Tail;
            var stack = new Stack<Bone>(b.children);
            Bone best = null;
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (c.isHuman)
                {
                    if (best == null || c.depth < best.depth || (c.depth == best.depth && c.descendants > best.descendants)) best = c;
                    continue;
                }
                foreach (var cc in c.children) stack.Push(cc);
            }
            return best != null ? best.t.position : b.Tail;
        }

        void DrawBones(PoseSystem pose, Camera cam, Dictionary<Bone, int> ids, Bone selected, bool humanoidOnly)
        {
            var camPos = cam.transform.position;
            m_Order.Clear();
            foreach (var kv in ids) m_Order.Add(new KeyValuePair<Bone, float>(kv.Key, (kv.Key.Head - camPos).sqrMagnitude));
            m_Order.Sort((a, b) => b.Value.CompareTo(a.Value));

            var oldZ = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            float alpha = Mathf.Clamp01(ScenePoseState.BoneOpacity);
            float size = ScenePoseState.BoneSize;
            var selectedSet = new HashSet<Transform>(Selection.transforms);
            var corners = new Vector3[6];
            int[,] tri =
            {
                { 0, 2, 3 }, { 0, 3, 4 }, { 0, 4, 5 }, { 0, 5, 2 },
                { 1, 3, 2 }, { 1, 4, 3 }, { 1, 5, 4 }, { 1, 2, 5 },
            };
            foreach (var kv in m_Order)
            {
                var b = kv.Key;
                var head = b.Head;
                var tail = TailOf(b, humanoidOnly);
                var axis = tail - head;
                float len = axis.magnitude;
                if (len < 1e-5f) continue;
                var dir = axis / len;
                var side = Vector3.ProjectOnPlane(b.t.right, dir);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.ProjectOnPlane(Vector3.up, dir);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.ProjectOnPlane(Vector3.forward, dir);
                side.Normalize();
                var side2 = Vector3.Cross(dir, side);
                float rad = len * 0.1f * size;
                var c = head + dir * len * 0.1f;
                corners[0] = head;
                corners[1] = tail;
                corners[2] = c + side * rad;
                corners[3] = c + side2 * rad;
                corners[4] = c - side * rad;
                corners[5] = c - side2 * rad;

                bool isSel = selectedSet.Contains(b.t);
                bool hot = ids.TryGetValue(b, out int id) && HandleUtility.nearestControl == id && GUIUtility.hotControl == 0;
                Color baseCol = isSel ? new Color(0.25f, 0.78f, 1f) : hot ? new Color(0.85f, 0.9f, 1f) : new Color(0.55f, 0.6f, 0.7f);

                for (int f = 0; f < 8; f++)
                {
                    var a0 = corners[tri[f, 0]];
                    var a1 = corners[tri[f, 1]];
                    var a2 = corners[tri[f, 2]];
                    var n = Vector3.Cross(a1 - a0, a2 - a0).normalized;
                    var fc = (a0 + a1 + a2) / 3f;
                    var outward = fc - (head + dir * Vector3.Dot(fc - head, dir));
                    if (Vector3.Dot(n, outward) < 0f) n = -n;
                    float facing = cam.orthographic ? Vector3.Dot(n, -cam.transform.forward) : Vector3.Dot(n, (camPos - fc).normalized);
                    if (facing < 0f) continue;
                    float shade = 0.55f + 0.45f * facing;
                    Handles.color = new Color(baseCol.r * shade, baseCol.g * shade, baseCol.b * shade, alpha);
                    m_Tri[0] = a0; m_Tri[1] = a1; m_Tri[2] = a2;
                    Handles.DrawAAConvexPolygon(m_Tri);
                }
                Handles.color = isSel ? new Color(0.85f, 0.97f, 1f, 0.95f) : new Color(0.05f, 0.06f, 0.08f, Mathf.Min(1f, alpha + 0.2f));
                float w = isSel ? 2.2f : 1.4f;
                m_Outline[0] = corners[0]; m_Outline[1] = corners[2]; m_Outline[2] = corners[1]; m_Outline[3] = corners[4]; m_Outline[4] = corners[0];
                Handles.DrawAAPolyLine(w, m_Outline);
                m_Outline[0] = corners[0]; m_Outline[1] = corners[3]; m_Outline[2] = corners[1]; m_Outline[3] = corners[5]; m_Outline[4] = corners[0];
                Handles.DrawAAPolyLine(w, m_Outline);
                m_Outline[0] = corners[2]; m_Outline[1] = corners[3]; m_Outline[2] = corners[4]; m_Outline[3] = corners[5]; m_Outline[4] = corners[2];
                Handles.DrawAAPolyLine(w, m_Outline);
            }
            Handles.zTest = oldZ;
        }
    }

    /// <summary>Scene view panel for the bone tool: bone info, resets, mirroring and saved poses.</summary>
    [Overlay(typeof(SceneView), "avi-creator-essentials-bone-posing", "Bone Posing")]
    public class ScenePoseOverlay : Overlay, ITransientOverlay
    {
        VisualElement m_Root;

        public bool visible => ToolManager.activeToolType == typeof(ScenePoseTool);

        public override VisualElement CreatePanelContent()
        {
            m_Root = new VisualElement();
            m_Root.style.width = 250;
            m_Root.style.paddingLeft = 4;
            m_Root.style.paddingRight = 4;
            m_Root.style.paddingBottom = 4;
            ScenePoseState.Changed += Rebuild;
            m_Root.RegisterCallback<DetachFromPanelEvent>(_ => ScenePoseState.Changed -= Rebuild);
            Rebuild();
            return m_Root;
        }

        static VisualElement Row()
        {
            var r = new VisualElement();
            r.style.flexDirection = FlexDirection.Row;
            r.style.flexWrap = Wrap.Wrap;
            r.style.marginTop = 3;
            return r;
        }

        static Button Btn(string text, Action a, string tip = null)
        {
            var b = new Button(a) { text = text, tooltip = tip };
            b.style.marginLeft = 0;
            b.style.marginRight = 3;
            b.style.marginBottom = 2;
            return b;
        }

        static Label Hint(string text)
        {
            var l = new Label(text);
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.opacity = 0.75f;
            l.style.fontSize = 11;
            l.style.marginTop = 4;
            return l;
        }

        void Rebuild()
        {
            if (m_Root == null) return;
            m_Root.Clear();
            var avatar = ScenePoseState.Avatar;
            var avatarField = new ObjectField("Avatar") { objectType = typeof(Transform), allowSceneObjects = true, value = avatar };
            avatarField.labelElement.style.minWidth = 50;
            avatarField.RegisterValueChangedCallback(e => ScenePoseState.SetAvatar(e.newValue as Transform));
            m_Root.Add(avatarField);

            var pose = ScenePoseState.Pose;
            if (pose == null)
            {
                m_Root.Add(Hint("Select your avatar (or any of its bones) in the Hierarchy to show its armature."));
                return;
            }

            var top = Row();
            top.Add(Btn("Exit posing", () => ToolManager.RestorePreviousPersistentTool(), "Back to Unity's normal tools (or press W / E / R)"));
            m_Root.Add(top);

            var mode = Row();
            var rot = Btn("Rotate", () => { ScenePoseState.RotateMode = true; SceneView.RepaintAll(); Rebuild(); }, "Rotate the selected bone");
            var mov = Btn("Move", () => { ScenePoseState.RotateMode = false; SceneView.RepaintAll(); Rebuild(); }, "Move the selected bone");
            (ScenePoseState.RotateMode ? rot : mov).style.unityFontStyleAndWeight = FontStyle.Bold;
            mode.Add(rot);
            mode.Add(mov);
            m_Root.Add(mode);

            var sel = ScenePoseState.Selected;
            if (sel != null)
            {
                var name = new Label(sel.Name + (sel.isHuman ? "  (" + ObjectNames.NicifyVariableName(sel.human.ToString()) + ")" : ""));
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.style.marginTop = 4;
                m_Root.Add(name);
                var nav = Row();
                nav.Add(Btn("Reset rotation", () => ScenePoseState.ResetBone(sel, true, false), "Alt+R"));
                nav.Add(Btn("Reset position", () => ScenePoseState.ResetBone(sel, false, true), "Alt+G"));
                if (sel.parent != null) nav.Add(Btn("Parent", () => Selection.activeTransform = sel.parent.t));
                if (sel.tailChild != null) nav.Add(Btn("Child", () => Selection.activeTransform = sel.tailChild.t));
                if (sel.mirror != null) nav.Add(Btn("Other side", () => Selection.activeTransform = sel.mirror.t));
                m_Root.Add(nav);
            }
            else
            {
                m_Root.Add(Hint("Click a bone in the Scene view to select it."));
            }

            var whole = Row();
            whole.Add(Btn("Reset pose", () => ScenePoseState.ApplySnapshot(ScenePoseState.Pose.RestSnapshot(), "Reset pose"),
                "Back to the model's rest pose"));
            whole.Add(Btn("Mirror pose", () => ScenePoseState.ApplySnapshot(ScenePoseState.Pose.MirroredSnapshot(), "Mirror pose"),
                "Swap left and right"));
            m_Root.Add(whole);

            var humanoid = new Toggle("Humanoid bones only") { value = ScenePoseState.HumanoidOnly };
            humanoid.RegisterValueChangedCallback(e => { ScenePoseState.HumanoidOnly = e.newValue; SceneView.RepaintAll(); });
            m_Root.Add(humanoid);
            var size = new Slider("Bone size", 0.3f, 3f) { value = ScenePoseState.BoneSize };
            size.RegisterValueChangedCallback(e => { ScenePoseState.BoneSize = e.newValue; SceneView.RepaintAll(); });
            m_Root.Add(size);
            var opacity = new Slider("Opacity", 0.1f, 1f) { value = ScenePoseState.BoneOpacity };
            opacity.RegisterValueChangedCallback(e => { ScenePoseState.BoneOpacity = e.newValue; SceneView.RepaintAll(); });
            m_Root.Add(opacity);

            // clothing armatures
            var clothHeader = new Label("Clothing armatures");
            clothHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            clothHeader.style.marginTop = 6;
            m_Root.Add(clothHeader);
            var linker = ScenePoseState.Linker;
            m_Root.Add(Hint(linker != null ? linker.Summary() : "No separate clothing armatures found."));
            if (linker != null && linker.HasClothing)
            {
                var sync = new Toggle("Keep clothing in sync") { value = ScenePoseState.SyncClothing };
                sync.tooltip = "Clothing bones follow the body bones with the same names, live, without changing your hierarchy (like VRCFury's Armature Link in the editor).";
                sync.RegisterValueChangedCallback(e => ScenePoseState.SyncClothing = e.newValue);
                m_Root.Add(sync);
                var mergeRow = Row();
                mergeRow.Add(Btn("Merge into body armature…", ScenePoseState.MergeClothing,
                    "Really parent the clothing bones under the body bones. Undoable."));
                m_Root.Add(mergeRow);
            }
            if (ScenePoseState.CanUnmerge)
            {
                var unRow = Row();
                unRow.Add(Btn("Unmerge clothing", ScenePoseState.UnmergeClothing, "Put the clothing bones back under their own armatures"));
                m_Root.Add(unRow);
            }

            // saved poses
            var poses = new List<AvatarPose>();
            foreach (var g in AssetDatabase.FindAssets("t:AvatarPose"))
            {
                var a = AssetDatabase.LoadAssetAtPath<AvatarPose>(AssetDatabase.GUIDToAssetPath(g));
                if (a != null) poses.Add(a);
            }
            var poseHeader = new Label("Saved poses");
            poseHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            poseHeader.style.marginTop = 6;
            m_Root.Add(poseHeader);
            var list = Row();
            foreach (var p in poses)
            {
                var asset = p;
                list.Add(Btn(asset.name, () =>
                    ScenePoseState.ApplySnapshot(ScenePoseState.Pose.SnapshotFrom(asset.bones), "Apply pose " + asset.name),
                    "Apply this pose. While the Animation window is recording, every bone it moves gets a key."));
            }
            if (poses.Count == 0) list.Add(Hint("Poses you save here or in the Avatar Painter show up here."));
            m_Root.Add(list);
            var saveRow = Row();
            var nameField = new TextField { value = "" };
            nameField.style.flexGrow = 1;
            saveRow.Add(nameField);
            saveRow.Add(Btn("Save pose", () =>
            {
                string n = string.IsNullOrWhiteSpace(nameField.value) ? "Pose " + DateTime.Now.ToString("MMdd HHmm") : nameField.value.Trim();
                APUtil.EnsureAssetFolder(APUtil.PosesFolder);
                var asset = ScriptableObject.CreateInstance<AvatarPose>();
                asset.avatarName = ScenePoseState.Avatar.name;
                asset.bones = ScenePoseState.Pose.ToPoseBones();
                AssetDatabase.CreateAsset(asset, AssetDatabase.GenerateUniqueAssetPath(APUtil.PosesFolder + "/" + APUtil.SafeFileName(n) + ".asset"));
                AssetDatabase.SaveAssets();
                Rebuild();
            }));
            m_Root.Add(saveRow);

            m_Root.Add(Hint(AnimationMode.InAnimationMode()
                ? "The Animation window is previewing. Press its record button and bone edits become keyframes."
                : "To animate: open Window › Animation › Animation, select the avatar, press record, then pose bones here."));
        }
    }
}
