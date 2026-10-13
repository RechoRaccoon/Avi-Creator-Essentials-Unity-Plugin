using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Finds clothing armatures that sit beside the body armature (each outfit with its own Armature/Hips/...),
    /// pairs their bones with the body's by name the way VRCFury's Armature Link does, and keeps them following
    /// the body. Bones that only exist on the clothing (hood strings, skirt bones...) are "extras": they stay
    /// children of their matched clothing parent, so they follow the body too.
    /// </summary>
    public class ArmatureLinker
    {
        public class Link
        {
            public Transform body;
            public Transform cloth;
            public Vector3 offsetPos;        // cloth position in body bone space
            public Quaternion offsetRot = Quaternion.identity;
            public int depth;
        }

        public class Clothing
        {
            public string name;              // top-level object under the avatar, e.g. "Hoodie"
            public Transform hips;
            public string prefix = "";
            public string suffix = "";
            public readonly List<Link> links = new List<Link>();
            public readonly List<Transform> extras = new List<Transform>();
        }

        public Transform root;
        public Transform bodyHips;
        public readonly List<Clothing> clothing = new List<Clothing>();
        readonly List<Link> m_All = new List<Link>();
        readonly HashSet<Transform> m_LinkedCloth = new HashSet<Transform>();

        public int LinkCount => m_All.Count;
        public bool HasClothing => clothing.Count > 0;

        /// <summary>True for clothing bones that duplicate a body bone (hidden while linked).</summary>
        public bool IsLinkedClothBone(Transform t) => t != null && m_LinkedCloth.Contains(t);

        public IEnumerable<Link> Links => m_All;

        // ------------------------------------------------------------------ detection

        public static ArmatureLinker Detect(Transform root, Transform bodyHips)
        {
            var l = new ArmatureLinker { root = root };
            if (root == null) return l;
            if (bodyHips == null) bodyHips = GuessBodyHips(root);
            l.bodyHips = bodyHips;
            if (bodyHips == null) return l;

            var boneSet = new HashSet<Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.bones == null) continue;
                foreach (var b in smr.bones)
                    if (b != null) boneSet.Add(b);
            }

            string hipsKey = Base(bodyHips.name);
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == bodyHips || t.IsChildOf(bodyHips) || bodyHips.IsChildOf(t)) continue;
                if (!LooksLikeHips(t.name, hipsKey, out string prefix, out string suffix)) continue;
                // Must actually drive a mesh, and must not be inside another clothing armature we already found.
                if (!DrivesMesh(t, boneSet)) continue;
                bool nested = false;
                foreach (var c in l.clothing)
                    if (t.IsChildOf(c.hips)) { nested = true; break; }
                if (nested) continue;

                var cl = new Clothing { hips = t, prefix = prefix, suffix = suffix, name = TopName(t, root) };
                l.MatchRecursive(cl, bodyHips, t, 0);
                // A real outfit armature matches more than just its hips.
                if (cl.links.Count > 1) l.clothing.Add(cl);
            }
            foreach (var c in l.clothing)
            {
                foreach (var link in c.links)
                {
                    l.m_All.Add(link);
                    l.m_LinkedCloth.Add(link.cloth);
                }
            }
            // Parents first, so a parent's correction is in place before its children are placed.
            l.m_All.Sort((a, b) => a.depth.CompareTo(b.depth));
            l.CaptureOffsets(null);
            return l;
        }

        static string TopName(Transform t, Transform root)
        {
            var p = t;
            while (p.parent != null && p.parent != root) p = p.parent;
            return p.name;
        }

        static bool DrivesMesh(Transform t, HashSet<Transform> bones)
        {
            if (bones.Contains(t)) return true;
            foreach (var c in t.GetComponentsInChildren<Transform>(true))
                if (bones.Contains(c)) return true;
            return false;
        }

        static Transform GuessBodyHips(Transform root)
        {
            Transform best = null;
            int bestCount = -1;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (Base(t.name) != "hips") continue;
                int n = t.GetComponentsInChildren<Transform>(true).Length;
                if (n > bestCount) { best = t; bestCount = n; }
            }
            return best;
        }

        /// <summary>Recognises "Hips", "Hoodie_Hips", "Hips.001", "J_Bip_C_Hips_Shirt"... and returns the extra text.</summary>
        static bool LooksLikeHips(string name, string hipsKey, out string prefix, out string suffix)
        {
            prefix = suffix = "";
            string n = Base(name);
            int i = n.IndexOf(hipsKey, StringComparison.Ordinal);
            if (i < 0) return false;
            prefix = n.Substring(0, i);
            suffix = n.Substring(i + hipsKey.Length);
            return prefix.Length <= 24 && suffix.Length <= 24;
        }

        /// <summary>Lower case, Blender-style ".001" numbers removed, separators removed.</summary>
        static string Base(string name)
        {
            string s = name.ToLowerInvariant().Trim();
            int end = s.Length;
            while (end > 0 && char.IsDigit(s[end - 1])) end--;
            if (end < s.Length && end > 0 && "._ ".IndexOf(s[end - 1]) >= 0) s = s.Substring(0, end - 1);
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (ch != '_' && ch != ' ' && ch != '.' && ch != '-') sb.Append(ch);
            return sb.ToString();
        }

        /// <summary>Comparable bone name: Base() without the clothing's own prefix/suffix.</summary>
        public static string Norm(string name, string prefix, string suffix)
        {
            string s = Base(name);
            if (prefix.Length > 0 && s.StartsWith(prefix, StringComparison.Ordinal) && s.Length > prefix.Length) s = s.Substring(prefix.Length);
            if (suffix.Length > 0 && s.EndsWith(suffix, StringComparison.Ordinal) && s.Length > suffix.Length) s = s.Substring(0, s.Length - suffix.Length);
            return s;
        }

        void MatchRecursive(Clothing cl, Transform body, Transform cloth, int depth)
        {
            cl.links.Add(new Link { body = body, cloth = cloth, depth = depth });
            for (int i = 0; i < cloth.childCount; i++)
            {
                var cc = cloth.GetChild(i);
                string key = Norm(cc.name, cl.prefix, cl.suffix);
                var match = FindBodyChild(body, key);
                if (match != null) MatchRecursive(cl, match, cc, depth + 1);
                else cl.extras.Add(cc);   // stays under its matched clothing parent and follows it
            }
        }

        /// <summary>Same-named child of the body bone; looks two levels down when the outfit skips a bone (e.g. no UpperChest).</summary>
        static Transform FindBodyChild(Transform body, string key)
        {
            for (int i = 0; i < body.childCount; i++)
                if (Norm(body.GetChild(i).name, "", "") == key) return body.GetChild(i);
            for (int i = 0; i < body.childCount; i++)
            {
                var c = body.GetChild(i);
                for (int j = 0; j < c.childCount; j++)
                    if (Norm(c.GetChild(j).name, "", "") == key) return c.GetChild(j);
            }
            return null;
        }

        // ------------------------------------------------------------------ syncing

        /// <summary>
        /// Records where each clothing bone sits relative to its body bone. With restOf, both armatures are first
        /// put in their rest pose so the offsets are right even if the avatar is currently posed.
        /// </summary>
        public void CaptureOffsets(Func<Transform, (Quaternion rot, Vector3 pos)?> restOf)
        {
            var saved = new List<(Transform t, Quaternion r, Vector3 p)>();
            if (restOf != null)
            {
                var touched = new HashSet<Transform>();
                foreach (var l in m_All)
                {
                    foreach (var t in new[] { l.body, l.cloth })
                    {
                        if (t == null || !touched.Add(t)) continue;
                        var rest = restOf(t);
                        if (!rest.HasValue) continue;
                        saved.Add((t, t.localRotation, t.localPosition));
                        t.localRotation = rest.Value.rot;
                        t.localPosition = rest.Value.pos;
                    }
                }
            }
            foreach (var l in m_All)
            {
                if (l.body == null || l.cloth == null) continue;
                l.offsetPos = l.body.InverseTransformPoint(l.cloth.position);
                l.offsetRot = Quaternion.Inverse(l.body.rotation) * l.cloth.rotation;
            }
            for (int i = saved.Count - 1; i >= 0; i--)
            {
                saved[i].t.localRotation = saved[i].r;
                saved[i].t.localPosition = saved[i].p;
            }
        }

        /// <summary>Moves every linked clothing bone to follow its body bone. Returns true if anything moved.</summary>
        public bool Apply()
        {
            bool moved = false;
            foreach (var l in m_All)
            {
                if (l.body == null || l.cloth == null) continue;
                var pos = l.body.TransformPoint(l.offsetPos);
                var rot = l.body.rotation * l.offsetRot;
                if ((l.cloth.position - pos).sqrMagnitude > 1e-12f || Quaternion.Angle(l.cloth.rotation, rot) > 0.001f)
                {
                    l.cloth.SetPositionAndRotation(pos, rot);
                    moved = true;
                }
            }
            return moved;
        }

        public string Summary()
        {
            if (clothing.Count == 0) return "No separate clothing armatures found.";
            var names = new List<string>();
            int extras = 0;
            foreach (var c in clothing)
            {
                names.Add(c.name);
                extras += c.extras.Count;
            }
            return clothing.Count + (clothing.Count == 1 ? " outfit armature" : " outfit armatures") + " (" + string.Join(", ", names) + "), " +
                   LinkCount + " matching bones" + (extras > 0 ? ", " + extras + " extra bone chains" : "");
        }
    }
}
