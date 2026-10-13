using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    public class Bone
    {
        public int index;
        public Transform t;
        public string path;
        public Bone parent;
        public readonly List<Bone> children = new List<Bone>();
        public bool isHuman;
        public HumanBodyBones human = HumanBodyBones.LastBone;
        public Bone mirror;
        public int depth;
        public int descendants;
        public Vector3 restLocalPos;
        public Quaternion restLocalRot;
        public Vector3 restLocalScale;
        public Quaternion restRootRot;   // rest rotation relative to the avatar root
        public Vector3 restRootPos;
        public Bone tailChild;           // child the bone points at, or null for leaves
        public Vector3 leafTail;         // local-space tail offset for leaves

        public string Name => t != null ? t.name : "(missing)";

        public Vector3 Head => t.position;

        public Vector3 Tail
        {
            get
            {
                if (tailChild != null && tailChild.t != null) return tailChild.t.position;
                return t.position + t.rotation * leafTail;
            }
        }
    }

    /// <summary>Full local transform state of every bone, for undo and transitions.</summary>
    public class PoseSnapshot
    {
        public Quaternion[] rotations;
        public Vector3[] positions;
    }

    /// <summary>The armature of the preview clone: bone discovery, rest pose, edits, saving and mirroring.</summary>
    public class PoseSystem
    {
        public readonly List<Bone> bones = new List<Bone>();
        public Bone selected;
        Transform m_Root;

        public event Action PoseChanged;
        public event Action SelectionChanged;

        // transition (hot swapping poses)
        PoseSnapshot m_From;
        PoseSnapshot m_To;
        double m_TransitionStart;
        const double TransitionTime = 0.22;
        public bool Animating => m_To != null;

        public Transform Root => m_Root;

        public void Build(Transform root, Dictionary<string, HumanBodyBones> humanPaths)
        {
            bones.Clear();
            selected = null;
            m_Root = root;
            var set = new HashSet<Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.bones == null) continue;
                foreach (var b in smr.bones)
                    if (b != null && b != root && b.IsChildOf(root)) set.Add(b);
                if (smr.rootBone != null && smr.rootBone != root && smr.rootBone.IsChildOf(root)) set.Add(smr.rootBone);
            }
            foreach (var kv in humanPaths)
            {
                var t = APUtil.FindPath(root, kv.Key);
                if (t != null && t != root) set.Add(t);
            }

            // Keep hierarchy order so parents come first.
            var ordered = new List<Transform>();
            CollectOrdered(root, set, ordered);
            var map = new Dictionary<Transform, Bone>();
            foreach (var t in ordered)
            {
                var b = new Bone
                {
                    index = bones.Count,
                    t = t,
                    path = APUtil.GetPath(t, root),
                    restLocalPos = t.localPosition,
                    restLocalRot = t.localRotation,
                    restLocalScale = t.localScale,
                    restRootRot = Quaternion.Inverse(root.rotation) * t.rotation,
                    restRootPos = root.InverseTransformPoint(t.position),
                };
                if (humanPaths != null)
                {
                    foreach (var kv in humanPaths)
                        if (kv.Key == b.path) { b.isHuman = true; b.human = kv.Value; break; }
                }
                var p = t.parent;
                while (p != null && p != root && !map.ContainsKey(p)) p = p.parent;
                if (p != null && map.TryGetValue(p, out var parentBone))
                {
                    b.parent = parentBone;
                    parentBone.children.Add(b);
                    b.depth = parentBone.depth + 1;
                }
                map[t] = b;
                bones.Add(b);
            }

            for (int i = bones.Count - 1; i >= 0; i--)
            {
                var b = bones[i];
                foreach (var c in b.children) b.descendants += 1 + c.descendants;
            }

            foreach (var b in bones) ComputeTail(b);
            PairMirrors();
        }

        static void CollectOrdered(Transform t, HashSet<Transform> set, List<Transform> list)
        {
            if (set.Contains(t)) list.Add(t);
            for (int i = 0; i < t.childCount; i++) CollectOrdered(t.GetChild(i), set, list);
        }

        void ComputeTail(Bone b)
        {
            b.tailChild = null;
            if (b.children.Count == 1)
            {
                b.tailChild = b.children[0];
            }
            else if (b.children.Count > 1)
            {
                // Prefer the main chain (most descendants), then the child best aligned with the incoming direction.
                Vector3 inDir = b.parent != null ? (b.t.position - b.parent.t.position).normalized : b.t.up;
                Bone best = null;
                float bestScore = float.MinValue;
                foreach (var c in b.children)
                {
                    float align = Vector3.Dot((c.t.position - b.t.position).normalized, inDir);
                    float score = c.descendants * 10f + align;
                    if (c.isHuman) score += 5f;
                    if (score > bestScore) { bestScore = score; best = c; }
                }
                b.tailChild = best;
            }
            if (b.tailChild != null && (b.tailChild.t.position - b.t.position).sqrMagnitude < 1e-8f) b.tailChild = null;

            if (b.tailChild == null)
            {
                float len = 0.04f;
                Vector3 dir = b.t.up;
                if (b.parent != null)
                {
                    var pd = b.t.position - b.parent.t.position;
                    if (pd.sqrMagnitude > 1e-8f)
                    {
                        len = Mathf.Max(0.01f, pd.magnitude * 0.5f);
                        dir = pd.normalized;
                    }
                }
                b.leafTail = Quaternion.Inverse(b.t.rotation) * (dir * len);
            }
        }

        static string SideKey(string name, out int side)
        {
            side = 0;
            string n = name;
            string[][] pairs =
            {
                new[] { ".L", ".R" }, new[] { "_L", "_R" }, new[] { ".l", ".r" }, new[] { "_l", "_r" },
                new[] { " L", " R" }, new[] { "-L", "-R" }, new[] { "Left", "Right" }, new[] { "left", "right" },
                new[] { "LEFT", "RIGHT" },
            };
            foreach (var p in pairs)
            {
                int iL = n.IndexOf(p[0], StringComparison.Ordinal);
                int iR = n.IndexOf(p[1], StringComparison.Ordinal);
                if (iL >= 0 && (p[0].Length > 2 || iL + p[0].Length == n.Length || !char.IsLetter(n[iL + p[0].Length])))
                {
                    side = -1;
                    return n.Remove(iL, p[0].Length).Insert(iL, "<side>");
                }
                if (iR >= 0 && (p[1].Length > 2 || iR + p[1].Length == n.Length || !char.IsLetter(n[iR + p[1].Length])))
                {
                    side = 1;
                    return n.Remove(iR, p[1].Length).Insert(iR, "<side>");
                }
            }
            if (n.StartsWith("L_", StringComparison.Ordinal) || n.StartsWith("L.", StringComparison.Ordinal)) { side = -1; return "<side>" + n.Substring(1); }
            if (n.StartsWith("R_", StringComparison.Ordinal) || n.StartsWith("R.", StringComparison.Ordinal)) { side = 1; return "<side>" + n.Substring(1); }
            return n;
        }

        void PairMirrors()
        {
            var byHuman = new Dictionary<HumanBodyBones, Bone>();
            foreach (var b in bones)
                if (b.isHuman) byHuman[b.human] = b;
            foreach (var b in bones)
            {
                if (!b.isHuman) continue;
                string n = b.human.ToString();
                string other = n.StartsWith("Left") ? "Right" + n.Substring(4) : n.StartsWith("Right") ? "Left" + n.Substring(5) : null;
                if (other != null && Enum.TryParse(other, out HumanBodyBones ob) && byHuman.TryGetValue(ob, out var m))
                    b.mirror = m;
            }
            var lefts = new Dictionary<string, Bone>();
            var rights = new Dictionary<string, Bone>();
            foreach (var b in bones)
            {
                if (b.mirror != null) continue;
                string key = SideKey(b.Name, out int side);
                if (side < 0) lefts[key] = b;
                else if (side > 0) rights[key] = b;
            }
            foreach (var kv in lefts)
            {
                if (rights.TryGetValue(kv.Key, out var r))
                {
                    kv.Value.mirror = r;
                    r.mirror = kv.Value;
                }
            }
        }

        /// <summary>
        /// Replaces the rest pose (used by Reset and Mirror) with the given local transforms, e.g. the avatar's
        /// prefab/FBX defaults. Transforms are restored afterwards.
        /// </summary>
        public void SetRest(Func<Bone, (Quaternion rot, Vector3 pos)?> restOf)
        {
            var current = Capture();
            foreach (var b in bones)
            {
                var r = restOf(b);
                if (r.HasValue)
                {
                    b.restLocalRot = r.Value.rot;
                    b.restLocalPos = r.Value.pos;
                }
                b.t.localRotation = b.restLocalRot;
                b.t.localPosition = b.restLocalPos;
            }
            foreach (var b in bones)
            {
                b.restRootRot = Quaternion.Inverse(m_Root.rotation) * b.t.rotation;
                b.restRootPos = m_Root.InverseTransformPoint(b.t.position);
            }
            foreach (var b in bones) ComputeTail(b);
            Restore(current, false);
        }

        public void Select(Bone b)
        {
            if (selected == b) return;
            selected = b;
            SelectionChanged?.Invoke();
        }

        public void NotifyChanged() => PoseChanged?.Invoke();

        // ------------------------------------------------------------------ snapshots

        public PoseSnapshot Capture()
        {
            var s = new PoseSnapshot { rotations = new Quaternion[bones.Count], positions = new Vector3[bones.Count] };
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i].t == null) continue;
                s.rotations[i] = bones[i].t.localRotation;
                s.positions[i] = bones[i].t.localPosition;
            }
            return s;
        }

        public void Restore(PoseSnapshot s, bool notify = true)
        {
            if (s == null) return;
            for (int i = 0; i < bones.Count && i < s.rotations.Length; i++)
            {
                if (bones[i].t == null) continue;
                bones[i].t.localRotation = s.rotations[i];
                bones[i].t.localPosition = s.positions[i];
            }
            if (notify) PoseChanged?.Invoke();
        }

        public PoseSnapshot RestSnapshot()
        {
            var s = new PoseSnapshot { rotations = new Quaternion[bones.Count], positions = new Vector3[bones.Count] };
            for (int i = 0; i < bones.Count; i++)
            {
                s.rotations[i] = bones[i].restLocalRot;
                s.positions[i] = bones[i].restLocalPos;
            }
            return s;
        }

        public bool IsRest(Bone b)
        {
            return b.t != null && Quaternion.Angle(b.t.localRotation, b.restLocalRot) < 0.01f &&
                   (b.t.localPosition - b.restLocalPos).sqrMagnitude < 1e-10f;
        }

        // ------------------------------------------------------------------ edits

        public void ResetBone(Bone b, bool rotation, bool position)
        {
            if (b == null || b.t == null) return;
            if (rotation) b.t.localRotation = b.restLocalRot;
            if (position) b.t.localPosition = b.restLocalPos;
            PoseChanged?.Invoke();
        }

        public void ResetAll() => Restore(RestSnapshot());

        /// <summary>Pose with left and right swapped and reflected across the avatar's X axis.</summary>
        public PoseSnapshot MirroredSnapshot()
        {
            var cur = Capture();
            var invRoot = Quaternion.Inverse(m_Root.rotation);
            var map = new Dictionary<Transform, Bone>();
            foreach (var b in bones) map[b.t] = b;

            // Change from rest for every bone, in root space.
            var deltas = new Quaternion[bones.Count];
            var posDeltas = new Vector3[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                deltas[i] = (invRoot * b.t.rotation) * Quaternion.Inverse(b.restRootRot);
                posDeltas[i] = m_Root.InverseTransformPoint(b.t.position) - b.restRootPos;
            }

            // Desired root-space rotation of every bone: the mirrored delta of its partner applied to its own rest.
            var target = new Quaternion[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                var src = bones[i].mirror ?? bones[i];
                var d = deltas[src.index];
                target[i] = new Quaternion(d.x, -d.y, -d.z, d.w) * bones[i].restRootRot;
            }

            Quaternion NewRootRot(Transform x)
            {
                if (x == null || x == m_Root) return Quaternion.identity;
                if (map.TryGetValue(x, out var bx)) return target[bx.index];
                var a = x.parent;
                while (a != null && a != m_Root && !map.ContainsKey(a)) a = a.parent;
                if (a == null || a == m_Root) return invRoot * x.rotation;
                var ab = map[a];
                var rel = Quaternion.Inverse(invRoot * a.rotation) * (invRoot * x.rotation);
                return target[ab.index] * rel;
            }

            var result = new PoseSnapshot { rotations = new Quaternion[bones.Count], positions = new Vector3[bones.Count] };
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                result.rotations[i] = Quaternion.Inverse(NewRootRot(b.t.parent)) * target[i];
                var src = b.mirror ?? b;
                var pd = posDeltas[src.index];
                bool srcMoved = (src.t.localPosition - src.restLocalPos).sqrMagnitude > 1e-10f;
                if (srcMoved && b.t.parent != null && !map.ContainsKey(b.t.parent))
                {
                    pd.x = -pd.x;
                    var world = m_Root.TransformPoint(b.restRootPos + pd);
                    result.positions[i] = b.t.parent.InverseTransformPoint(world);
                }
                else if (srcMoved)
                {
                    var lp = src.t.localPosition - src.restLocalPos;
                    result.positions[i] = b.restLocalPos + new Vector3(-lp.x, lp.y, lp.z);
                }
                else result.positions[i] = b.restLocalPos;
            }
            return result;
        }

        // ------------------------------------------------------------------ saved poses

        public List<PoseBone> ToPoseBones()
        {
            var list = new List<PoseBone>();
            foreach (var b in bones)
            {
                if (b.t == null) continue;
                var pb = new PoseBone { path = b.path, name = b.Name, rotation = b.t.localRotation };
                if ((b.t.localPosition - b.restLocalPos).sqrMagnitude > 1e-10f)
                {
                    pb.hasPosition = true;
                    pb.position = b.t.localPosition;
                }
                list.Add(pb);
            }
            return list;
        }

        public PoseSnapshot SnapshotFrom(List<PoseBone> pose)
        {
            var s = RestSnapshot();
            var byPath = new Dictionary<string, int>();
            var byName = new Dictionary<string, int>();
            for (int i = 0; i < bones.Count; i++)
            {
                byPath[bones[i].path] = i;
                byName[bones[i].Name] = i;
            }
            foreach (var pb in pose)
            {
                if (!byPath.TryGetValue(pb.path, out int i) && !byName.TryGetValue(pb.name ?? "", out i)) continue;
                s.rotations[i] = pb.rotation;
                if (pb.hasPosition) s.positions[i] = pb.position;
            }
            return s;
        }

        public void TransitionTo(PoseSnapshot target)
        {
            m_From = Capture();
            m_To = target;
            m_TransitionStart = EditorApplication.timeSinceStartup;
        }

        /// <summary>Advances a running transition. Returns true while animating.</summary>
        public bool Tick()
        {
            if (m_To == null) return false;
            double t = (EditorApplication.timeSinceStartup - m_TransitionStart) / TransitionTime;
            float k = Mathf.Clamp01((float)t);
            k = k * k * (3f - 2f * k);
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i].t == null) continue;
                bones[i].t.localRotation = Quaternion.Slerp(m_From.rotations[i], m_To.rotations[i], k);
                bones[i].t.localPosition = Vector3.Lerp(m_From.positions[i], m_To.positions[i], k);
            }
            if (t >= 1.0)
            {
                m_To = null;
                m_From = null;
            }
            PoseChanged?.Invoke();
            return m_To != null;
        }

        public void FinishTransition()
        {
            if (m_To == null) return;
            Restore(m_To);
            m_To = null;
            m_From = null;
        }

        /// <summary>Copies the current pose onto the real avatar in the scene (undoable).</summary>
        public void CopyToSceneAvatar(Transform sceneRoot)
        {
            if (sceneRoot == null) return;
            var targets = new List<Transform>();
            foreach (var b in bones)
            {
                var t = APUtil.FindPath(sceneRoot, b.path);
                if (t != null) targets.Add(t);
            }
            Undo.RecordObjects(targets.ToArray(), "Copy pose from Avi Creator Essentials");
            foreach (var b in bones)
            {
                var t = APUtil.FindPath(sceneRoot, b.path);
                if (t == null || b.t == null) continue;
                t.localRotation = b.t.localRotation;
                t.localPosition = b.t.localPosition;
            }
        }
    }
}
