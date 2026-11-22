using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>Copies the pose from one avatar in the scene onto another, matched by relative bone path. Useful when one copy of an avatar is stuck in the wrong pose while the other is in the right one.</summary>
    public class CopyPoseWindow : EditorWindow
    {
        [MenuItem("Tools/Avi Creator Essentials/Copy Pose Between Avatars…", false, 115)]
        static void Open()
        {
            var w = GetWindow<CopyPoseWindow>(false, "Copy Pose Between Avatars", true);
            w.minSize = new Vector2(380, 200);
            w.Show();
        }

        GameObject m_Source;
        GameObject m_Target;
        string m_Status;

        void OnGUI()
        {
            EditorGUILayout.LabelField("Copies every bone's local position and rotation from the source avatar onto the matching bone in the target avatar, matched by bone path. The roots themselves are left alone.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(4);
            m_Source = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Source avatar", "The avatar whose pose is copied from. Pick the one in the correct pose."), m_Source, typeof(GameObject), true);
            m_Target = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Target avatar", "The avatar whose pose is fixed. Pick the one stuck in the wrong pose."), m_Target, typeof(GameObject), true);
            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(m_Source == null || m_Target == null || m_Source == m_Target))
            {
                if (GUILayout.Button("Copy Pose", GUILayout.Height(28))) CopyPose();
            }
            if (!string.IsNullOrEmpty(m_Status)) EditorGUILayout.HelpBox(m_Status, MessageType.Info);
        }

        void CopyPose()
        {
            var sourceRoot = m_Source.transform;
            var targetRoot = m_Target.transform;
            var pairs = new List<(Transform, Transform)>();
            int missingOnTarget = 0;
            foreach (Transform s in sourceRoot.GetComponentsInChildren<Transform>(true))
            {
                if (s == sourceRoot) continue;
                var t = APUtil.FindPath(targetRoot, APUtil.GetPath(s, sourceRoot));
                if (t == null) { missingOnTarget++; continue; }
                pairs.Add((s, t));
            }
            var targets = new List<Transform>();
            foreach (var p in pairs) targets.Add(p.Item2);
            Undo.RecordObjects(targets.ToArray(), "Copy pose between avatars");
            int copied = 0;
            foreach (var p in pairs)
            {
                p.Item2.localRotation = p.Item1.localRotation;
                p.Item2.localPosition = p.Item1.localPosition;
                PrefabUtility.RecordPrefabInstancePropertyModifications(p.Item2);
                copied++;
            }
            int missingOnSource = 0;
            foreach (Transform t in targetRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t == targetRoot) continue;
                if (APUtil.FindPath(sourceRoot, APUtil.GetPath(t, targetRoot)) == null) missingOnSource++;
            }
            m_Status = "Copied " + copied + " bone" + (copied == 1 ? "" : "s") + " from " + m_Source.name + " to " + m_Target.name + ".";
            if (missingOnTarget > 0) m_Status += " " + missingOnTarget + " source bone" + (missingOnTarget == 1 ? " had" : "s had") + " no match on the target and was skipped.";
            if (missingOnSource > 0) m_Status += " " + missingOnSource + " target bone" + (missingOnSource == 1 ? " had" : "s had") + " no match on the source and was left alone.";
        }
    }
}
