using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Mute toggle: shows or hides objects on an avatar depending on whether the wearer is muted in VRChat.
    /// It uses the built-in MuteSelf parameter, a bool VRChat sets itself, so nothing is added to the avatar's
    /// Expression Parameters asset and it costs no sync budget. MuteSelf is sent to other players, so they see
    /// the objects change too (it can take a moment to show up for them).
    ///
    /// How it is wired: a layer called "ACE Mute Toggle" is added to the avatar's FX playable layer, with two
    /// states (Unmuted and Muted) that switch on MuteSelf. Each state plays a clip that sets m_IsActive on the
    /// picked objects. If the avatar already has a custom FX controller, the layer is added to it and nothing
    /// else is changed. If it still uses VRChat's default FX layer, a copy of the SDK sample FX controller is
    /// used when it is in the project, otherwise a new controller with only the mute layer is made and the
    /// window warns about it. The avatar descriptor is edited through SerializedObject, so this code does not
    /// need the VRChat SDK assemblies at compile time.
    /// </summary>
    public static class MuteToggleGenerator
    {
        public const string LayerName = "ACE Mute Toggle";
        public const string ParamName = "MuteSelf";
        public const string DataFolder = "Assets/Avi Creator Essentials Data/MuteToggle";

        const string ClipPrefix = "ACE Mute ";
        const string SampleFxName = "vrc_AvatarV3FaceLayer";

        /// <summary>One object on the avatar, and whether it shows while muted and while unmuted.</summary>
        public class Entry
        {
            public GameObject target;
            public bool showWhenMuted = true;
            public bool showWhenUnmuted = false;
        }

        public class Result
        {
            public int objects;
            public string controllerPath;
            public bool usedExistingController;
            public bool usedSampleFx;
            public bool createdFreshController;
            public readonly List<string> warnings = new List<string>();
        }

        /// <summary>What the avatar's FX layer looks like right now, for the window to report.</summary>
        public class FxInfo
        {
            public bool hasDescriptor;
            public bool usesDefaultFx;
            public string controllerName;
            public bool hasMuteLayer;
            public int layerCount;
        }

        // ------------------------------------------------------------------ inspect

        /// <summary>Reads the avatar descriptor and FX layer without changing anything.</summary>
        public static FxInfo Inspect(GameObject root)
        {
            var info = new FxInfo();
            if (root == null) return info;
            var descriptor = FindDescriptor(root);
            info.hasDescriptor = descriptor != null;
            if (descriptor == null) return info;
            var so = new SerializedObject(descriptor);
            var fx = FindFxLayer(so);
            if (fx == null) return info;
            info.usesDefaultFx = fx.FindPropertyRelative("isDefault").boolValue;
            var controller = fx.FindPropertyRelative("animatorController").objectReferenceValue as AnimatorController;
            info.controllerName = controller != null ? controller.name : null;
            if (controller != null)
            {
                info.layerCount = controller.layers.Length;
                foreach (var l in controller.layers)
                    if (l.name == LayerName) info.hasMuteLayer = true;
            }
            return info;
        }

        // ------------------------------------------------------------------ apply

        /// <summary>
        /// Builds (or rebuilds) the mute layer for the given entries. Re-running replaces the layer this
        /// tool made before, so tweaking and re-applying is safe. Everything is validated first, so a bad
        /// entry stops the whole apply before anything is changed.
        /// </summary>
        public static Result Apply(GameObject root, List<Entry> entries)
        {
            var result = new Result();
            if (root == null) throw new ArgumentNullException("root");
            var descriptor = FindDescriptor(root);
            if (descriptor == null)
                throw new InvalidOperationException("'" + root.name + "' has no VRC Avatar Descriptor. Pick the avatar's root object (the one with the descriptor).");

            var clean = Validate(root, entries, result);
            if (clean.Count == 0)
                throw new InvalidOperationException("Nothing to toggle. Add at least one object with different muted and unmuted settings.");

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Mute toggle");
            int group = Undo.GetCurrentGroup();
            try
            {
                bool needsAssign;
                var controller = ResolveFxController(root, descriptor, result, out needsAssign);
                EnsureMuteParameter(controller);
                if (needsAssign) AssignFxController(descriptor, controller);
                BuildLayer(controller, clean, root);
                result.objects = clean.Count;
                AssetDatabase.SaveAssets();
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
            return result;
        }

        /// <summary>
        /// Removes the mute layer this tool made. If the FX controller was created by this tool and has no
        /// layers left afterwards, the avatar goes back to VRChat's default FX layer and the file is deleted.
        /// Returns true when a mute setup was removed.
        /// </summary>
        public static bool Remove(GameObject root)
        {
            if (root == null) return false;
            var descriptor = FindDescriptor(root);
            if (descriptor == null) return false;
            var fx = FindFxLayer(new SerializedObject(descriptor));
            if (fx == null) return false;
            bool isDefault = fx.FindPropertyRelative("isDefault").boolValue;
            var controller = fx.FindPropertyRelative("animatorController").objectReferenceValue as AnimatorController;
            if (isDefault || controller == null) return false;

            bool hadLayer = false;
            foreach (var l in controller.layers)
                if (l.name == LayerName) { hadLayer = true; break; }
            if (!hadLayer) return false;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Remove mute toggle");
            int group = Undo.GetCurrentGroup();
            try
            {
                RemoveLayerAndClips(controller);
                string path = AssetDatabase.GetAssetPath(controller);
                if (controller.layers.Length == 0 && IsToolControllerPath(root, path))
                {
                    Undo.RecordObject(descriptor, "Remove mute toggle");
                    var so = new SerializedObject(descriptor);
                    var fx2 = FindFxLayer(so);
                    if (fx2 != null)
                    {
                        fx2.FindPropertyRelative("isDefault").boolValue = true;
                        fx2.FindPropertyRelative("animatorController").objectReferenceValue = null;
                        so.ApplyModifiedProperties();
                        EditorUtility.SetDirty(descriptor);
                    }
                    AssetDatabase.DeleteAsset(path);
                }
                AssetDatabase.SaveAssets();
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }
            return true;
        }

        // ------------------------------------------------------------------ validation

        static List<Entry> Validate(GameObject root, List<Entry> entries, Result result)
        {
            var clean = new List<Entry>();
            var seen = new HashSet<GameObject>();
            if (entries == null) return clean;
            foreach (var e in entries)
            {
                if (e == null || e.target == null) continue;
                if (e.target == root)
                    throw new InvalidOperationException("'" + e.target.name + "' is the avatar itself. Pick objects under the avatar, not the avatar root.");
                if (!e.target.transform.IsChildOf(root.transform))
                    throw new InvalidOperationException("'" + e.target.name + "' is not under '" + root.name + "'. Only objects on this avatar can be toggled.");
                if (!seen.Add(e.target))
                {
                    result.warnings.Add("'" + e.target.name + "' was listed twice, using the first row.");
                    continue;
                }
                if (e.showWhenMuted == e.showWhenUnmuted)
                {
                    result.warnings.Add("'" + e.target.name + "' shows the same muted and unmuted, so it never changes. It was left out.");
                    continue;
                }
                clean.Add(e);
            }
            return clean;
        }

        // ------------------------------------------------------------------ FX controller

        /// <summary>
        /// Finds the FX controller to use. An existing custom FX controller is used as-is. Otherwise a
        /// controller is made (a copy of the SDK sample FX when it is in the project, else a fresh one with
        /// only the mute layer) and needsAssign is set so the caller points the descriptor at it.
        /// </summary>
        static AnimatorController ResolveFxController(GameObject root, Component descriptor, Result result, out bool needsAssign)
        {
            needsAssign = false;
            var fx = FindFxLayer(new SerializedObject(descriptor));
            if (fx == null)
                throw new InvalidOperationException("Couldn't find the FX layer on the avatar descriptor.");
            bool isDefault = fx.FindPropertyRelative("isDefault").boolValue;
            var existing = fx.FindPropertyRelative("animatorController").objectReferenceValue as AnimatorController;
            if (!isDefault && existing != null)
            {
                string path = AssetDatabase.GetAssetPath(existing);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal))
                    throw new InvalidOperationException("The FX controller '" + existing.name + "' lives outside your project, so it can't be edited. Duplicate it into your Assets folder first, then run this again.");
                result.usedExistingController = true;
                result.controllerPath = path;
                return existing;
            }

            string folder = DataFolder + "/" + APUtil.SafeFileName(root.name);
            EnsureFolder(folder);
            string destPath = folder + "/" + APUtil.SafeFileName(root.name) + " FX.controller";

            var atDest = AssetDatabase.LoadAssetAtPath<AnimatorController>(destPath);
            if (atDest != null)
            {
                result.usedExistingController = true;
                result.controllerPath = destPath;
                needsAssign = true;
                return atDest;
            }

            AnimatorController created;
            string samplePath = FindSampleFx();
            if (samplePath != null)
            {
                if (!AssetDatabase.CopyAsset(samplePath, destPath))
                    throw new InvalidOperationException("Couldn't copy the SDK sample FX controller.");
                created = AssetDatabase.LoadAssetAtPath<AnimatorController>(destPath);
                if (created == null)
                    throw new InvalidOperationException("Couldn't load the copied FX controller.");
                result.usedSampleFx = true;
            }
            else
            {
                created = new AnimatorController { name = APUtil.SafeFileName(root.name) + " FX" };
                AssetDatabase.CreateAsset(created, destPath);
                result.createdFreshController = true;
                result.warnings.Add("The SDK sample FX controller wasn't in this project, so the new FX controller only has the mute layer. The avatar's default face expressions are replaced until you remove this setup.");
            }
            Undo.RegisterCreatedObjectUndo(created, "Mute toggle");
            result.controllerPath = destPath;
            needsAssign = true;
            return created;
        }

        static void AssignFxController(Component descriptor, AnimatorController controller)
        {
            Undo.RecordObject(descriptor, "Mute toggle");
            var so = new SerializedObject(descriptor);
            var fx = FindFxLayer(so);
            if (fx == null)
                throw new InvalidOperationException("Couldn't find the FX layer on the avatar descriptor.");
            fx.FindPropertyRelative("isDefault").boolValue = false;
            fx.FindPropertyRelative("animatorController").objectReferenceValue = controller;
            so.FindProperty("customizeAnimationLayers").boolValue = true;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(descriptor);
        }

        /// <summary>
        /// MuteSelf is a built-in parameter: it goes on the FX controller only, never in the avatar's
        /// Expression Parameters asset (built-ins don't belong there and don't count toward the budget).
        /// </summary>
        static void EnsureMuteParameter(AnimatorController controller)
        {
            foreach (var p in controller.parameters)
            {
                if (p.name != ParamName) continue;
                if (p.type != AnimatorControllerParameterType.Bool)
                    throw new InvalidOperationException("The FX controller already has a parameter called '" + ParamName + "' that isn't a bool. Rename or remove it, then run this again.");
                return;
            }
            Undo.RecordObject(controller, "Mute toggle");
            controller.AddParameter(ParamName, AnimatorControllerParameterType.Bool);
        }

        // ------------------------------------------------------------------ layer

        static void BuildLayer(AnimatorController controller, List<Entry> entries, GameObject root)
        {
            RemoveLayerAndClips(controller);
            Undo.RecordObject(controller, "Mute toggle");

            var unmutedClip = BuildClip(ClipPrefix + "Unmuted", entries, root, false);
            var mutedClip = BuildClip(ClipPrefix + "Muted", entries, root, true);
            AssetDatabase.AddObjectToAsset(unmutedClip, controller);
            AssetDatabase.AddObjectToAsset(mutedClip, controller);
            Undo.RegisterCreatedObjectUndo(unmutedClip, "Mute toggle");
            Undo.RegisterCreatedObjectUndo(mutedClip, "Mute toggle");

            var sm = new AnimatorStateMachine { name = LayerName };
            var layer = new AnimatorControllerLayer
            {
                name = LayerName,
                stateMachine = sm,
                defaultWeight = 1f,
                blendingMode = AnimatorLayerBlendingMode.Override,
            };
            controller.AddLayer(layer);

            var unmuted = sm.AddState("Unmuted");
            unmuted.motion = unmutedClip;
            unmuted.writeDefaultValues = false;
            var muted = sm.AddState("Muted");
            muted.motion = mutedClip;
            muted.writeDefaultValues = false;
            sm.defaultState = unmuted;

            var toMuted = unmuted.AddTransition(muted);
            toMuted.AddCondition(AnimatorConditionMode.If, 0f, ParamName);
            toMuted.hasExitTime = false;
            toMuted.duration = 0f;
            toMuted.hasFixedDuration = true;

            var toUnmuted = muted.AddTransition(unmuted);
            toUnmuted.AddCondition(AnimatorConditionMode.IfNot, 0f, ParamName);
            toUnmuted.hasExitTime = false;
            toUnmuted.duration = 0f;
            toUnmuted.hasFixedDuration = true;
        }

        static AnimationClip BuildClip(string name, List<Entry> entries, GameObject root, bool muted)
        {
            var clip = new AnimationClip { name = name };
            foreach (var e in entries)
            {
                string path = APUtil.GetPath(e.target.transform, root.transform);
                bool show = muted ? e.showWhenMuted : e.showWhenUnmuted;
                var binding = new EditorCurveBinding
                {
                    type = typeof(GameObject),
                    path = path,
                    propertyName = "m_IsActive",
                };
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0f, 1f / 60f, show ? 1f : 0f));
            }
            return clip;
        }

        /// <summary>Removes the layer this tool made, and its clips, so a re-run starts clean.</summary>
        static void RemoveLayerAndClips(AnimatorController controller)
        {
            string path = AssetDatabase.GetAssetPath(controller);
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    var clip = obj as AnimationClip;
                    if (clip != null && clip.name.StartsWith(ClipPrefix, StringComparison.Ordinal))
                        Undo.DestroyObjectImmediate(clip);
                }
            }
            for (int i = 0; i < controller.layers.Length; i++)
            {
                if (controller.layers[i].name != LayerName) continue;
                Undo.RecordObject(controller, "Mute toggle");
                controller.RemoveLayer(i);
                break;
            }
        }

        // ------------------------------------------------------------------ descriptor

        static Component FindDescriptor(GameObject root)
        {
            if (root == null) return null;
            return root.GetComponent("VRCAvatarDescriptor");
        }

        /// <summary>
        /// Finds the FX entry in the descriptor's baseAnimationLayers. The type is the AnimLayerType enum
        /// (Base 0, Additive 1, Gesture 2, Action 3, FX 4), so searching by value works for humanoid and
        /// generic avatars alike. Read through SerializedObject so this needs no VRChat SDK reference.
        /// </summary>
        static SerializedProperty FindFxLayer(SerializedObject so)
        {
            var layers = so.FindProperty("baseAnimationLayers");
            if (layers == null || !layers.isArray) return null;
            for (int i = 0; i < layers.arraySize; i++)
            {
                var el = layers.GetArrayElementAtIndex(i);
                var type = el.FindPropertyRelative("type");
                if (type != null && type.intValue == 4) return el;
            }
            return null;
        }

        static string FindSampleFx()
        {
            foreach (var guid in AssetDatabase.FindAssets(SampleFxName + " t:AnimatorController"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) return path;
            }
            return null;
        }

        static bool IsToolControllerPath(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string prefix = DataFolder + "/" + APUtil.SafeFileName(root.name) + "/";
            return path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(" FX.controller", StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ helpers

        static void EnsureFolder(string path)
        {
            var parts = path.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
