using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Ear Shaper: reshape ears (for example cat ears into bunny ears) directly on the avatar's mesh.
    /// Only vertex positions change. Bone weights, UVs, bindposes and every other blend shape are left alone,
    /// vertices that sit at the same position (UV seams) always move together so nothing splits open,
    /// and the result is saved as a new mesh asset (as a blend shape by default), never over the original.
    /// </summary>
    public class EarShaperWindow : EditorWindow
    {
        [MenuItem("Tools/Avi Creator Essentials/Ear Shaper (Bunny Ears)…", false, 116)]
        static void FromToolsMenu() { Open(Selection.activeGameObject); }

        [MenuItem("GameObject/Avi Creator Essentials/Reshape Ears…", false, 25)]
        static void FromHierarchy(MenuCommand cmd) { Open(cmd.context as GameObject); }

        [MenuItem("GameObject/Avi Creator Essentials/Reshape Ears…", true)]
        static bool FromHierarchyValid() { return Selection.activeGameObject != null; }

        public static void Open(GameObject go)
        {
            var w = GetWindow<EarShaperWindow>(false, "Ear Shaper", true);
            w.minSize = new Vector2(400, 600);
            if (go != null) w.SetFromObject(go);
            w.Show();
        }

        // ───────────────────────── data ─────────────────────────

        enum Mode { Select, Bunny, Sculpt }
        enum SculptTool { Grab, Smooth, Inflate }
        enum OutputKind { BlendShape, Bake }

        const int PB = 14;       // profile bins along the ear
        const int LutK = 32;     // centerline samples

        class ShapeTarget
        {
            public Renderer renderer;
            public SkinnedMeshRenderer smr;
            public MeshFilter filter;
            public Mesh orig, preview, overlay;
            public int n;
            public int[] tris;
            public BoneWeight[] bw;
            public Vector3[] posOrig, posBase, posWork, nrmOrig, nrmBase, nrmWork;
            public Vector4[] tanOrig, tanBase, tanWork;
            public Matrix4x4[] skin, skinInv;
            public bool[] skinOk;
            public Vector3[] world, strokeStart;
            public int[] group, groupRep, gVertStart, gVerts, gTriStart, gTris;
            public int groupCount;
            public int[][] nbr;
            public Vector3[] gWorld, gNsum;
            public bool[] sel;
            public float[] mask;
            public bool HasTan { get { return tanOrig != null && tanOrig.Length == n; } }
        }

        class Ear
        {
            public int side;
            public bool ok;
            public Vector3 B, a, w, n;
            public float L0, maxHalfW;
            public float[] halfW = new float[PB], cx = new float[PB], cy = new float[PB];
            // prepared per parameter set
            public Vector3 a2, w2, n2, nb;
            public float nbSign = 1f, slope1 = 1f;
            public Vector3[] lut = new Vector3[LutK + 1];
        }

        class Snap { public Vector3[][] pos, nrm; public Vector4[][] tan; }
        struct Entry { public ShapeTarget t; public int g; public float w; public bool mirrored; }

        // setup
        GameObject m_Root;
        readonly List<Renderer> m_Pending = new List<Renderer>();
        readonly List<Transform> m_EarBones = new List<Transform>();
        Mesh m_UnreadableMesh;
        string m_Status;
        Vector2 m_Scroll;

        // session
        bool m_Active;
        List<ShapeTarget> m_Sess = new List<ShapeTarget>();
        Mode m_Mode;
        readonly List<Snap> m_Undo = new List<Snap>();
        readonly List<Ear> m_Ears = new List<Ear>();
        Material m_OverlayMat;
        bool m_OverlayDirty;

        // select / sculpt settings
        float m_BrushCm = 2f, m_FeatherCm = 1.5f, m_BoneThreshold = 0.4f, m_Strength = 0.6f;
        bool m_Symmetry = true, m_LimitToSel = true;
        SculptTool m_Tool = SculptTool.Grab;

        // bunny parameters
        float m_Length = 3f, m_Width = 1f, m_Thick = 1f, m_Bunnify = 0.8f, m_TipRound = 0.65f, m_BaseLock = 0.2f;
        float m_BendDeg, m_TwistDeg, m_Cup = 0.15f, m_OutDeg, m_FwdDeg;

        // output
        OutputKind m_Output = OutputKind.BlendShape;
        string m_ShapeName = "BunnyEars";
        bool m_DefaultOn = true;

        // input state
        bool m_HasHover, m_Stroking, m_StrokeRemove;
        Vector3 m_Hover, m_HoverN, m_GrabHit;
        Plane m_GrabPlane;
        List<Entry> m_Entries = new List<Entry>();

        // ───────────────────────── lifecycle ─────────────────────────

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            if (m_Active) EndSession(true);
            if (m_OverlayMat != null) DestroyImmediate(m_OverlayMat);
        }

        void OnPlayMode(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.ExitingEditMode && m_Active) EndSession(true);
        }

        void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (!m_Active) return;
            foreach (var t in m_Sess) if (t.renderer != null) SetMesh(t, t.orig);
        }

        void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            if (!m_Active) return;
            foreach (var t in m_Sess) if (t.renderer != null && t.preview != null) SetMesh(t, t.preview);
        }

        static void SetMesh(ShapeTarget t, Mesh m)
        {
            if (t.smr != null) t.smr.sharedMesh = m;
            else if (t.filter != null) t.filter.sharedMesh = m;
        }

        void SetFromObject(GameObject go)
        {
            if (m_Active) return;
            if (m_Root == null)
            {
                var a = go.GetComponentInParent<Animator>();
                m_Root = a != null ? a.gameObject : go.transform.root.gameObject;
            }
            AddPending(go);
        }

        void AddPending(GameObject go)
        {
            foreach (var r in go.GetComponents<Renderer>())
            {
                bool ok = r is SkinnedMeshRenderer || (r is MeshRenderer && r.GetComponent<MeshFilter>() != null);
                if (ok && !m_Pending.Contains(r)) m_Pending.Add(r);
            }
        }

        // ───────────────────────── session setup ─────────────────────────

        void StartSession()
        {
            m_Status = null;
            m_UnreadableMesh = null;
            if (EditorApplication.isPlayingOrWillChangePlaymode) { m_Status = "Leave Play mode first."; return; }
            if (m_Root == null) { m_Status = "Pick the avatar root first."; return; }
            var list = new List<ShapeTarget>();
            foreach (var r in m_Pending)
            {
                if (r == null) continue;
                string err;
                var t = BuildTarget(r, out err);
                if (t == null) { m_Status = err; return; }
                list.Add(t);
            }
            if (list.Count == 0) { m_Status = "Add at least one mesh first (select it in the Hierarchy, then press Add selected)."; return; }
            m_Sess = list;
            foreach (var t in m_Sess)
            {
                t.preview = Instantiate(t.orig);
                t.preview.name = t.orig.name + " (ear preview)";
                t.preview.hideFlags = HideFlags.HideAndDontSave;
                SetMesh(t, t.preview);
                RebuildMask(t);
            }
            m_Active = true;
            m_Mode = Mode.Select;
            m_Undo.Clear();
            m_Ears.Clear();
            m_OverlayDirty = true;
            ResetParams(true);
            SceneView.RepaintAll();
        }

        void EndSession(bool restore)
        {
            foreach (var t in m_Sess)
            {
                if (restore && t.renderer != null) SetMesh(t, t.orig);
                if (t.preview != null) DestroyImmediate(t.preview);
                if (t.overlay != null) DestroyImmediate(t.overlay);
            }
            m_Sess.Clear();
            m_Ears.Clear();
            m_Undo.Clear();
            m_Active = false;
            m_Stroking = false;
            Repaint();
            SceneView.RepaintAll();
        }

        ShapeTarget BuildTarget(Renderer r, out string err)
        {
            err = null;
            var t = new ShapeTarget { renderer = r };
            t.smr = r as SkinnedMeshRenderer;
            t.filter = r.GetComponent<MeshFilter>();
            Mesh m = t.smr != null ? t.smr.sharedMesh : (t.filter != null ? t.filter.sharedMesh : null);
            if (m == null) { err = r.name + " has no mesh."; return null; }
            if (!m.isReadable)
            {
                m_UnreadableMesh = m;
                err = "The mesh \"" + m.name + "\" is not readable. Turn on Read/Write for it (button below) and try again.";
                return null;
            }
            t.orig = m;
            int n = m.vertexCount;
            t.n = n;
            t.tris = m.triangles;
            t.posOrig = m.vertices;
            var nrm = m.normals;
            if (nrm.Length != n)
            {
                var tmp = Instantiate(m);
                tmp.RecalculateNormals();
                nrm = tmp.normals;
                DestroyImmediate(tmp);
            }
            t.nrmOrig = nrm;
            var tan = m.tangents;
            t.tanOrig = tan.Length == n ? tan : null;
            t.bw = m.boneWeights;
            t.posBase = (Vector3[])t.posOrig.Clone();
            t.posWork = (Vector3[])t.posOrig.Clone();
            t.nrmBase = (Vector3[])t.nrmOrig.Clone();
            t.nrmWork = (Vector3[])t.nrmOrig.Clone();
            if (t.HasTan) { t.tanBase = (Vector4[])t.tanOrig.Clone(); t.tanWork = (Vector4[])t.tanOrig.Clone(); }
            ComputeSkin(t);
            BuildGroups(t);
            t.world = new Vector3[n];
            t.gWorld = new Vector3[t.groupCount];
            t.gNsum = new Vector3[t.groupCount];
            t.sel = new bool[t.groupCount];
            t.mask = new float[t.groupCount];
            RefreshWorld(t);
            RecomputeNsum(t);
            return t;
        }

        static void AddScaled(ref Matrix4x4 acc, Matrix4x4 m, float w)
        {
            if (w <= 0f) return;
            for (int k = 0; k < 16; k++) acc[k] += m[k] * w;
        }

        static void ComputeSkin(ShapeTarget t)
        {
            int n = t.n;
            t.skin = new Matrix4x4[n];
            t.skinInv = new Matrix4x4[n];
            t.skinOk = new bool[n];
            Matrix4x4 rootM = t.renderer.transform.localToWorldMatrix;
            Matrix4x4[] bm = null;
            bool skinned = t.smr != null && t.smr.bones != null && t.smr.bones.Length > 0 && t.bw != null && t.bw.Length == n;
            if (skinned)
            {
                var bones = t.smr.bones;
                var bind = t.orig.bindposes;
                bm = new Matrix4x4[bones.Length];
                for (int j = 0; j < bones.Length; j++)
                    bm[j] = (bones[j] != null && j < bind.Length) ? bones[j].localToWorldMatrix * bind[j] : rootM;
            }
            for (int i = 0; i < n; i++)
            {
                Matrix4x4 s = rootM;
                if (skinned)
                {
                    var b = t.bw[i];
                    Matrix4x4 acc = new Matrix4x4();
                    float tot = 0f;
                    if (b.boneIndex0 < bm.Length) { AddScaled(ref acc, bm[b.boneIndex0], b.weight0); tot += Mathf.Max(0f, b.weight0); }
                    if (b.boneIndex1 < bm.Length) { AddScaled(ref acc, bm[b.boneIndex1], b.weight1); tot += Mathf.Max(0f, b.weight1); }
                    if (b.boneIndex2 < bm.Length) { AddScaled(ref acc, bm[b.boneIndex2], b.weight2); tot += Mathf.Max(0f, b.weight2); }
                    if (b.boneIndex3 < bm.Length) { AddScaled(ref acc, bm[b.boneIndex3], b.weight3); tot += Mathf.Max(0f, b.weight3); }
                    if (tot > 1e-5f)
                    {
                        for (int k = 0; k < 16; k++) acc[k] /= tot;
                        s = acc;
                    }
                }
                t.skin[i] = s;
                if (Mathf.Abs(s.determinant) > 1e-12f) { t.skinInv[i] = s.inverse; t.skinOk[i] = true; }
                else { t.skinInv[i] = Matrix4x4.identity; t.skinOk[i] = false; }
            }
        }

        /// <summary>Welds vertices that share a position (UV seams, hard edges) into groups so they always move together.</summary>
        static void BuildGroups(ShapeTarget t)
        {
            int n = t.n;
            float q = Mathf.Max(t.orig.bounds.size.magnitude * 1e-6f, 1e-9f);
            var dict = new Dictionary<Vector3Int, int>(n);
            t.group = new int[n];
            var reps = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var p = t.posOrig[i];
                var key = new Vector3Int(Mathf.RoundToInt(p.x / q), Mathf.RoundToInt(p.y / q), Mathf.RoundToInt(p.z / q));
                int g;
                if (!dict.TryGetValue(key, out g)) { g = reps.Count; dict[key] = g; reps.Add(i); }
                t.group[i] = g;
            }
            int gc = reps.Count;
            t.groupCount = gc;
            t.groupRep = reps.ToArray();

            t.gVertStart = new int[gc + 1];
            for (int i = 0; i < n; i++) t.gVertStart[t.group[i] + 1]++;
            for (int g = 0; g < gc; g++) t.gVertStart[g + 1] += t.gVertStart[g];
            t.gVerts = new int[n];
            var fill = new int[gc];
            for (int i = 0; i < n; i++) { int g = t.group[i]; t.gVerts[t.gVertStart[g] + fill[g]++] = i; }

            int tc = t.tris.Length / 3;
            t.gTriStart = new int[gc + 1];
            for (int k = 0; k < t.tris.Length; k++) t.gTriStart[t.group[t.tris[k]] + 1]++;
            for (int g = 0; g < gc; g++) t.gTriStart[g + 1] += t.gTriStart[g];
            t.gTris = new int[t.tris.Length];
            var fill2 = new int[gc];
            for (int ti = 0; ti < tc; ti++)
                for (int c = 0; c < 3; c++)
                {
                    int g = t.group[t.tris[ti * 3 + c]];
                    t.gTris[t.gTriStart[g] + fill2[g]++] = ti;
                }

            var lists = new List<int>[gc];
            for (int ti = 0; ti < tc; ti++)
            {
                int a = t.group[t.tris[ti * 3]], b = t.group[t.tris[ti * 3 + 1]], c = t.group[t.tris[ti * 3 + 2]];
                Link(lists, a, b); Link(lists, b, c); Link(lists, c, a);
            }
            t.nbr = new int[gc][];
            for (int g = 0; g < gc; g++) t.nbr[g] = lists[g] != null ? lists[g].ToArray() : new int[0];
        }

        static void Link(List<int>[] lists, int a, int b)
        {
            if (a == b) return;
            if (lists[a] == null) lists[a] = new List<int>(6);
            if (lists[b] == null) lists[b] = new List<int>(6);
            if (!lists[a].Contains(b)) lists[a].Add(b);
            if (!lists[b].Contains(a)) lists[b].Add(a);
        }

        static void RefreshWorld(ShapeTarget t)
        {
            for (int i = 0; i < t.n; i++) t.world[i] = t.skin[i].MultiplyPoint3x4(t.posWork[i]);
            for (int g = 0; g < t.groupCount; g++) t.gWorld[g] = t.world[t.groupRep[g]];
        }

        static void SetWorld(ShapeTarget t, int v, Vector3 w)
        {
            if (!t.skinOk[v]) return;
            t.world[v] = w;
            t.posWork[v] = t.skinInv[v].MultiplyPoint3x4(w);
            int g = t.group[v];
            if (t.groupRep[g] == v) t.gWorld[g] = w;
        }

        static Vector3 GroupNormalSum(ShapeTarget t, int g)
        {
            Vector3 s = Vector3.zero;
            for (int k = t.gTriStart[g]; k < t.gTriStart[g + 1]; k++)
            {
                int ti = t.gTris[k] * 3;
                Vector3 p0 = t.posWork[t.tris[ti]], p1 = t.posWork[t.tris[ti + 1]], p2 = t.posWork[t.tris[ti + 2]];
                s += Vector3.Cross(p1 - p0, p2 - p0);
            }
            return s;
        }

        static void RecomputeNsum(ShapeTarget t)
        {
            for (int g = 0; g < t.groupCount; g++) t.gNsum[g] = GroupNormalSum(t, g);
        }

        /// <summary>Turns each moved group's normals and tangents by however much the faces around it turned.
        /// This keeps hard edges hard and smooth parts smooth without recalculating the whole mesh.</summary>
        static void UpdateNormals(ShapeTarget t, List<int> groups)
        {
            foreach (int g in groups)
            {
                Vector3 old = t.gNsum[g];
                Vector3 nw = GroupNormalSum(t, g);
                t.gNsum[g] = nw;
                if (old.sqrMagnitude < 1e-16f || nw.sqrMagnitude < 1e-16f) continue;
                Quaternion q = Quaternion.FromToRotation(old.normalized, nw.normalized);
                for (int k = t.gVertStart[g]; k < t.gVertStart[g + 1]; k++)
                {
                    int v = t.gVerts[k];
                    t.nrmWork[v] = (q * t.nrmWork[v]).normalized;
                    if (t.HasTan)
                    {
                        Vector4 tv = t.tanWork[v];
                        Vector3 r = q * new Vector3(tv.x, tv.y, tv.z);
                        t.tanWork[v] = new Vector4(r.x, r.y, r.z, tv.w);
                    }
                }
            }
        }

        static void Upload(ShapeTarget t)
        {
            if (t.preview == null) return;
            t.preview.vertices = t.posWork;
            t.preview.normals = t.nrmWork;
            if (t.HasTan) t.preview.tangents = t.tanWork;
            t.preview.RecalculateBounds();
        }

        /// <summary>Selection weight per group: 1 inside the selection, fading to 0 over the feather distance measured along the surface.</summary>
        void RebuildMask(ShapeTarget t)
        {
            int gc = t.groupCount;
            var bw = new Vector3[gc];
            for (int g = 0; g < gc; g++) { int v = t.groupRep[g]; bw[g] = t.skin[v].MultiplyPoint3x4(t.posBase[v]); }
            float feather = m_FeatherCm * 0.01f;
            var dist = new float[gc];
            var inq = new bool[gc];
            var q = new Queue<int>();
            for (int g = 0; g < gc; g++)
            {
                if (t.sel[g]) { dist[g] = 0f; q.Enqueue(g); inq[g] = true; }
                else dist[g] = float.MaxValue;
            }
            if (feather > 1e-6f)
            {
                while (q.Count > 0)
                {
                    int g = q.Dequeue();
                    inq[g] = false;
                    foreach (int nb in t.nbr[g])
                    {
                        float nd = dist[g] + Vector3.Distance(bw[g], bw[nb]);
                        if (nd < dist[nb] && nd <= feather)
                        {
                            dist[nb] = nd;
                            if (!inq[nb]) { inq[nb] = true; q.Enqueue(nb); }
                        }
                    }
                }
            }
            for (int g = 0; g < gc; g++)
            {
                if (t.sel[g]) t.mask[g] = 1f;
                else if (feather > 1e-6f && dist[g] <= feather) t.mask[g] = 1f - S(dist[g] / feather);
                else t.mask[g] = 0f;
            }
            m_OverlayDirty = true;
        }

        static float S(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        // ───────────────────────── undo ─────────────────────────

        void PushUndo()
        {
            var s = new Snap { pos = new Vector3[m_Sess.Count][], nrm = new Vector3[m_Sess.Count][], tan = new Vector4[m_Sess.Count][] };
            for (int i = 0; i < m_Sess.Count; i++)
            {
                var t = m_Sess[i];
                s.pos[i] = (Vector3[])t.posBase.Clone();
                s.nrm[i] = (Vector3[])t.nrmBase.Clone();
                s.tan[i] = t.HasTan ? (Vector4[])t.tanBase.Clone() : null;
            }
            m_Undo.Add(s);
            if (m_Undo.Count > 12) m_Undo.RemoveAt(0);
        }

        void SyncBaseToWork()
        {
            foreach (var t in m_Sess)
            {
                Array.Copy(t.posWork, t.posBase, t.n);
                Array.Copy(t.nrmWork, t.nrmBase, t.n);
                if (t.HasTan) Array.Copy(t.tanWork, t.tanBase, t.n);
            }
        }

        void UndoStep()
        {
            if (m_Undo.Count == 0) return;
            var s = m_Undo[m_Undo.Count - 1];
            m_Undo.RemoveAt(m_Undo.Count - 1);
            for (int i = 0; i < m_Sess.Count; i++)
            {
                var t = m_Sess[i];
                t.posBase = s.pos[i]; t.nrmBase = s.nrm[i]; if (t.HasTan) t.tanBase = s.tan[i];
                t.posWork = (Vector3[])t.posBase.Clone();
                t.nrmWork = (Vector3[])t.nrmBase.Clone();
                if (t.HasTan) t.tanWork = (Vector4[])t.tanBase.Clone();
                RecomputeNsum(t);
                RefreshWorld(t);
                RebuildMask(t);
                Upload(t);
            }
            if (m_Mode == Mode.Bunny) { RefitEars(); RefreshBunny(); }
            m_OverlayDirty = true;
            Repaint();
            SceneView.RepaintAll();
        }

        void ResetAllShape()
        {
            PushUndo();
            foreach (var t in m_Sess)
            {
                Array.Copy(t.posOrig, t.posBase, t.n);
                Array.Copy(t.nrmOrig, t.nrmBase, t.n);
                if (t.HasTan) Array.Copy(t.tanOrig, t.tanBase, t.n);
                Array.Copy(t.posBase, t.posWork, t.n);
                Array.Copy(t.nrmBase, t.nrmWork, t.n);
                if (t.HasTan) Array.Copy(t.tanBase, t.tanWork, t.n);
                RecomputeNsum(t);
                RefreshWorld(t);
                RebuildMask(t);
                Upload(t);
            }
            if (m_Mode == Mode.Bunny) { RefitEars(); RefreshBunny(); }
            m_OverlayDirty = true;
        }

        // ───────────────────────── bunny ear deformation ─────────────────────────

        void ResetParams(bool standing)
        {
            m_Width = 1f; m_Thick = 1f; m_BendDeg = 0f; m_TwistDeg = 0f; m_OutDeg = 0f; m_FwdDeg = 0f; m_BaseLock = 0.2f; m_TipRound = 0.65f;
            if (standing) { m_Length = 3f; m_Bunnify = 0.8f; m_Cup = 0.15f; }
            else { m_Length = 1f; m_Bunnify = 0f; m_Cup = 0f; }
        }

        bool IsIdentity()
        {
            return Mathf.Abs(m_Length - 1f) < 1e-3f && Mathf.Abs(m_Width - 1f) < 1e-3f && Mathf.Abs(m_Thick - 1f) < 1e-3f
                && m_Bunnify < 1e-3f && Mathf.Abs(m_BendDeg) < 1e-2f && Mathf.Abs(m_TwistDeg) < 1e-2f
                && Mathf.Abs(m_Cup) < 1e-3f && Mathf.Abs(m_OutDeg) < 1e-2f && Mathf.Abs(m_FwdDeg) < 1e-2f;
        }

        void CommitBunny()
        {
            if (m_Mode != Mode.Bunny) return;
            if (IsIdentity()) return;
            PushUndo();
            SyncBaseToWork();
            ResetParams(false);
            RefitEars();
            RefreshBunny();
            m_OverlayDirty = true;
        }

        void SetMode(Mode m)
        {
            if (m == m_Mode) return;
            if (m_Mode == Mode.Bunny) CommitBunny();
            m_Mode = m;
            if (m == Mode.Bunny) { RefitEars(); RefreshBunny(); }
            m_Stroking = false;
            SceneView.RepaintAll();
        }

        /// <summary>Works out each selected ear's base, direction, width direction, length and width profile from the selected vertices.</summary>
        void RefitEars()
        {
            m_Ears.Clear();
            if (m_Root == null) return;
            var rt = m_Root.transform;
            var ptsPos = new List<Vector3>();
            var ptsNeg = new List<Vector3>();
            foreach (var t in m_Sess)
                for (int g = 0; g < t.groupCount; g++)
                {
                    if (!t.sel[g]) continue;
                    int v = t.groupRep[g];
                    Vector3 w = t.skin[v].MultiplyPoint3x4(t.posBase[v]);
                    if (rt.InverseTransformPoint(w).x >= 0f) ptsPos.Add(w); else ptsNeg.Add(w);
                }
            var e1 = FitEar(ptsPos, 1);
            var e2 = FitEar(ptsNeg, -1);
            if (e1 != null) m_Ears.Add(e1);
            if (e2 != null) m_Ears.Add(e2);
        }

        Ear FitEar(List<Vector3> pts, int side)
        {
            if (pts.Count < 6) return null;
            var rt = m_Root.transform;
            Vector3 up = rt.up, fwd = rt.forward, right = rt.right;
            int n = pts.Count;
            Vector3 c = Vector3.zero;
            foreach (var p in pts) c += p;
            c /= n;
            var ts = new float[n];
            for (int i = 0; i < n; i++) ts[i] = Vector3.Dot(pts[i] - c, up);
            var sorted = (float[])ts.Clone();
            Array.Sort(sorted);
            float lo = sorted[Mathf.Clamp((int)(n * 0.2f), 0, n - 1)];
            float hi = sorted[Mathf.Clamp((int)(n * 0.95f), 0, n - 1)];
            Vector3 baseC = Vector3.zero, tipC = Vector3.zero;
            int nb = 0, nt = 0;
            for (int i = 0; i < n; i++)
            {
                if (ts[i] <= lo) { baseC += pts[i]; nb++; }
                if (ts[i] >= hi) { tipC += pts[i]; nt++; }
            }
            if (nb == 0 || nt == 0) return null;
            baseC /= nb; tipC /= nt;
            Vector3 a = tipC - baseC;
            a = a.sqrMagnitude > 1e-12f ? a.normalized : up;
            if (Vector3.Dot(a, up) < 0.2f) a = up;

            Vector3 u1 = right - a * Vector3.Dot(right, a);
            if (u1.sqrMagnitude < 1e-6f) u1 = fwd - a * Vector3.Dot(fwd, a);
            u1.Normalize();
            Vector3 u2 = Vector3.Cross(a, u1);
            float sxx = 0, syy = 0, sxy = 0;
            foreach (var p in pts)
            {
                float dx = Vector3.Dot(p - c, u1), dy = Vector3.Dot(p - c, u2);
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
            }
            float th = 0.5f * Mathf.Atan2(2f * sxy, sxx - syy);
            Vector3 w = (u1 * Mathf.Cos(th) + u2 * Mathf.Sin(th)).normalized;
            Vector3 nn = Vector3.Cross(a, w).normalized;

            var ts2 = new float[n];
            for (int i = 0; i < n; i++) ts2[i] = Vector3.Dot(pts[i] - baseC, a);
            var s2 = (float[])ts2.Clone();
            Array.Sort(s2);
            float tmin = s2[Mathf.Clamp((int)(n * 0.01f), 0, n - 1)];
            float tmax = s2[Mathf.Clamp((int)(n * 0.99f), 0, n - 1)];
            float L0 = Mathf.Max(tmax - tmin, 1e-4f);

            var e = new Ear { side = side, a = a, w = w, n = nn, L0 = L0, B = baseC + a * tmin };
            var minX = new float[PB]; var maxX = new float[PB]; var minY = new float[PB]; var maxY = new float[PB];
            var has = new bool[PB];
            for (int k = 0; k < PB; k++) { minX[k] = float.MaxValue; maxX[k] = float.MinValue; minY[k] = float.MaxValue; maxY[k] = float.MinValue; }
            for (int i = 0; i < n; i++)
            {
                Vector3 d = pts[i] - e.B;
                float u = Vector3.Dot(d, a) / L0;
                int bi = Mathf.Clamp(Mathf.RoundToInt(u * (PB - 1)), 0, PB - 1);
                float x = Vector3.Dot(d, w), y = Vector3.Dot(d, nn);
                has[bi] = true;
                if (x < minX[bi]) minX[bi] = x; if (x > maxX[bi]) maxX[bi] = x;
                if (y < minY[bi]) minY[bi] = y; if (y > maxY[bi]) maxY[bi] = y;
            }
            var hw = new float[PB]; var cxa = new float[PB]; var cya = new float[PB];
            for (int k = 0; k < PB; k++)
            {
                if (!has[k]) { hw[k] = -1f; continue; }
                hw[k] = (maxX[k] - minX[k]) * 0.5f;
                cxa[k] = (maxX[k] + minX[k]) * 0.5f;
                cya[k] = (maxY[k] + minY[k]) * 0.5f;
            }
            FillGaps(hw, has); FillGaps(cxa, has); FillGaps(cya, has);
            Smooth(hw); Smooth(cxa); Smooth(cya);
            float mx = 1e-5f;
            for (int k = 0; k < PB; k++) mx = Mathf.Max(mx, hw[k]);
            e.halfW = hw; e.cx = cxa; e.cy = cya; e.maxHalfW = mx;
            e.ok = true;
            return e;
        }

        static void FillGaps(float[] a, bool[] has)
        {
            int n = a.Length;
            int first = -1;
            for (int i = 0; i < n; i++) if (has[i]) { first = i; break; }
            if (first < 0) { for (int i = 0; i < n; i++) a[i] = 0f; return; }
            for (int i = 0; i < first; i++) a[i] = a[first];
            int prev = first;
            for (int i = first + 1; i < n; i++)
            {
                if (has[i]) { prev = i; continue; }
                int next = -1;
                for (int j = i + 1; j < n; j++) if (has[j]) { next = j; break; }
                if (next < 0) a[i] = a[prev];
                else { float f = (float)(i - prev) / (next - prev); a[i] = Mathf.Lerp(a[prev], a[next], f); }
            }
        }

        static void Smooth(float[] a)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var b = (float[])a.Clone();
                for (int i = 0; i < a.Length; i++)
                {
                    float l = b[Mathf.Max(i - 1, 0)], r = b[Mathf.Min(i + 1, a.Length - 1)];
                    a[i] = (l + 2f * b[i] + r) * 0.25f;
                }
            }
        }

        static float Samp(float[] a, float u)
        {
            float f = Mathf.Clamp01(u) * (a.Length - 1);
            int i = Mathf.Min((int)f, a.Length - 2);
            return Mathf.Lerp(a[i], a[i + 1], f - i);
        }

        float H(float s)
        {
            float bl = Mathf.Max(m_BaseLock, 0.01f);
            return s * (1f + (m_Length - 1f) * S(s / bl));
        }

        void PrepareEar(Ear e)
        {
            var rt = m_Root.transform;
            Quaternion qo = Quaternion.AngleAxis(-e.side * m_OutDeg, rt.forward);
            Quaternion qf = Quaternion.AngleAxis(m_FwdDeg, rt.right);
            Quaternion Q = qf * qo;
            e.a2 = Q * e.a; e.w2 = Q * e.w; e.n2 = Q * e.n;
            e.nbSign = Vector3.Dot(e.n2, rt.forward) >= 0f ? 1f : -1f;
            e.nb = e.n2 * e.nbSign;
            float bend = m_BendDeg * Mathf.Deg2Rad;
            e.lut[0] = Vector3.zero;
            for (int k = 0; k < LutK; k++)
            {
                float s0 = (float)k / LutK, s1 = (float)(k + 1) / LutK, sm = (s0 + s1) * 0.5f;
                float phi = bend * sm;
                Vector3 T = Mathf.Cos(phi) * e.a2 + Mathf.Sin(phi) * e.nb;
                e.lut[k + 1] = e.lut[k] + T * (e.L0 * (H(s1) - H(s0)));
            }
            e.slope1 = Mathf.Max(0.05f, (H(1f) - H(1f - 1f / LutK)) * LutK);
        }

        Vector3 Deform(Ear e, Vector3 p)
        {
            Vector3 d = p - e.B;
            float x = Vector3.Dot(d, e.w), y = Vector3.Dot(d, e.n), t = Vector3.Dot(d, e.a);
            float u = t / e.L0;
            float uf = Mathf.Clamp01(u);
            float phi = m_BendDeg * Mathf.Deg2Rad * uf;
            float tau = m_TwistDeg * Mathf.Deg2Rad * uf;
            Vector3 T = Mathf.Cos(phi) * e.a2 + Mathf.Sin(phi) * e.nb;
            Vector3 Nn = (-Mathf.Sin(phi) * e.a2 + Mathf.Cos(phi) * e.nb) * e.nbSign;
            Vector3 Wx = Mathf.Cos(tau) * e.w2 + Mathf.Sin(tau) * Nn;
            Vector3 Ny = -Mathf.Sin(tau) * e.w2 + Mathf.Cos(tau) * Nn;
            Vector3 C;
            if (u <= 0f) C = e.a2 * (e.L0 * u);
            else if (u >= 1f) C = e.lut[LutK] + T * (e.L0 * e.slope1 * (u - 1f));
            else
            {
                float pos = u * LutK;
                int i = Mathf.Min((int)pos, LutK - 1);
                C = Vector3.Lerp(e.lut[i], e.lut[i + 1], pos - i);
            }
            float hw = Samp(e.halfW, u), cxu = Samp(e.cx, u), cyu = Samp(e.cy, u);
            float us = Mathf.Clamp(m_TipRound, 0.05f, 0.95f);
            float prof = u <= us ? 1f : Mathf.Sqrt(Mathf.Max(0.02f, 1f - Mathf.Pow((Mathf.Clamp01(u) - us) / (1f - us), 2f)));
            float target = e.maxHalfW * prof;
            float ratio = Mathf.Clamp(target / Mathf.Max(hw, 0.12f * e.maxHalfW), 0.3f, 8f);
            float sw = m_Width * Mathf.Lerp(1f, ratio, m_Bunnify * S(u / 0.25f));
            float xr = x - cxu;
            float x2 = cxu + xr * sw;
            float y2 = cyu + (y - cyu) * m_Thick + m_Cup * xr * xr * sw * sw / Mathf.Max(e.maxHalfW, 1e-5f) * 0.5f;
            return e.B + C + Wx * x2 + Ny * y2;
        }

        Ear EarFor(int side)
        {
            foreach (var e in m_Ears) if (e.side == side && e.ok) return e;
            return null;
        }

        void RefreshBunny()
        {
            if (!m_Active || m_Root == null) return;
            var rt = m_Root.transform;
            foreach (var e in m_Ears) PrepareEar(e);
            foreach (var t in m_Sess)
            {
                Array.Copy(t.posBase, t.posWork, t.n);
                Array.Copy(t.nrmBase, t.nrmWork, t.n);
                if (t.HasTan) Array.Copy(t.tanBase, t.tanWork, t.n);
                RecomputeNsum(t);
                var touched = new List<int>();
                for (int g = 0; g < t.groupCount; g++)
                {
                    float m = t.mask[g];
                    if (m <= 0f) continue;
                    touched.Add(g);
                    for (int k = t.gVertStart[g]; k < t.gVertStart[g + 1]; k++)
                    {
                        int v = t.gVerts[k];
                        if (!t.skinOk[v]) continue;
                        Vector3 w0 = t.skin[v].MultiplyPoint3x4(t.posBase[v]);
                        var e = EarFor(rt.InverseTransformPoint(w0).x >= 0f ? 1 : -1);
                        if (e == null) continue;
                        Vector3 w1 = Deform(e, w0);
                        t.posWork[v] = t.skinInv[v].MultiplyPoint3x4(Vector3.Lerp(w0, w1, m));
                    }
                }
                UpdateNormals(t, touched);
                RefreshWorld(t);
                Upload(t);
            }
            m_OverlayDirty = true;
        }

        // ───────────────────────── picking & brushes ─────────────────────────

        static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (det > -1e-12f && det < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 1e-5f;
        }

        bool RaycastAll(Ray ray, out Vector3 hit, out Vector3 normal)
        {
            hit = Vector3.zero; normal = Vector3.up;
            float best = float.MaxValue;
            foreach (var t in m_Sess)
            {
                for (int i = 0; i < t.tris.Length; i += 3)
                {
                    Vector3 a = t.world[t.tris[i]], b = t.world[t.tris[i + 1]], c = t.world[t.tris[i + 2]];
                    float d;
                    if (RayTri(ray.origin, ray.direction, a, b, c, out d) && d < best)
                    {
                        best = d;
                        Vector3 nrm = Vector3.Cross(b - a, c - a).normalized;
                        if (Vector3.Dot(nrm, ray.direction) > 0f) nrm = -nrm;
                        normal = nrm;
                    }
                }
            }
            if (best == float.MaxValue) return false;
            hit = ray.origin + ray.direction * best;
            return true;
        }

        Vector3 MirrorPoint(Vector3 p)
        {
            var rt = m_Root.transform;
            Vector3 l = rt.InverseTransformPoint(p); l.x = -l.x; return rt.TransformPoint(l);
        }

        Vector3 MirrorVector(Vector3 v)
        {
            var rt = m_Root.transform;
            Vector3 l = rt.InverseTransformDirection(v); l.x = -l.x; return rt.TransformDirection(l);
        }

        float Radius { get { return m_BrushCm * 0.01f; } }

        void PaintSelectAt(Vector3 c)
        {
            var centers = new List<Vector3> { c };
            if (m_Symmetry) centers.Add(MirrorPoint(c));
            float r = Radius;
            foreach (var t in m_Sess)
            {
                bool changed = false;
                for (int g = 0; g < t.groupCount; g++)
                {
                    foreach (var cc in centers)
                        if ((t.gWorld[g] - cc).sqrMagnitude <= r * r)
                        {
                            bool v = !m_StrokeRemove;
                            if (t.sel[g] != v) { t.sel[g] = v; changed = true; }
                            break;
                        }
                }
                if (changed) RebuildMask(t);
            }
        }

        void GatherBrush(Vector3 c, bool mirrored, List<Entry> into)
        {
            float r = Radius;
            foreach (var t in m_Sess)
                for (int g = 0; g < t.groupCount; g++)
                {
                    float d = Vector3.Distance(t.gWorld[g], c);
                    if (d >= r) continue;
                    float f = 1f - d / r;
                    float w = f * f * (3f - 2f * f);
                    if (m_LimitToSel) w *= t.mask[g];
                    if (w > 0.0005f) into.Add(new Entry { t = t, g = g, w = w, mirrored = mirrored });
                }
        }

        bool AnySelected()
        {
            foreach (var t in m_Sess) for (int g = 0; g < t.groupCount; g++) if (t.sel[g]) return true;
            return false;
        }

        void BeginStroke(SceneView sv, Event e)
        {
            m_Stroking = false;
            if (m_Mode == Mode.Select)
            {
                if (!m_HasHover) return;
                m_StrokeRemove = e.control || e.command;
                m_Stroking = true;
                PaintSelectAt(m_Hover);
                return;
            }
            if (m_Mode != Mode.Sculpt || !m_HasHover) return;
            PushUndo();
            m_Stroking = true;
            if (m_Tool == SculptTool.Grab)
            {
                m_GrabHit = m_Hover;
                m_GrabPlane = new Plane(-sv.camera.transform.forward, m_Hover);
                m_Entries.Clear();
                GatherBrush(m_Hover, false, m_Entries);
                if (m_Symmetry) GatherBrush(MirrorPoint(m_Hover), true, m_Entries);
                foreach (var t in m_Sess) t.strokeStart = (Vector3[])t.world.Clone();
            }
        }

        void DragStroke(SceneView sv, Event e)
        {
            if (!m_Stroking) return;
            if (m_Mode == Mode.Select)
            {
                if (m_HasHover) PaintSelectAt(m_Hover);
                return;
            }
            var touched = new Dictionary<ShapeTarget, List<int>>();
            if (m_Tool == SculptTool.Grab)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                float dist;
                if (!m_GrabPlane.Raycast(ray, out dist)) return;
                Vector3 delta = ray.GetPoint(dist) - m_GrabHit;
                Vector3 mdelta = m_Symmetry ? MirrorVector(delta) : delta;
                foreach (var en in m_Entries)
                {
                    Vector3 dv = (en.mirrored ? mdelta : delta) * (en.w * m_Strength);
                    var t = en.t;
                    for (int k = t.gVertStart[en.g]; k < t.gVertStart[en.g + 1]; k++)
                    {
                        int v = t.gVerts[k];
                        SetWorld(t, v, t.strokeStart[v] + dv);
                    }
                    AddTouched(touched, t, en.g);
                }
            }
            else
            {
                if (!m_HasHover) return;
                var entries = new List<Entry>();
                GatherBrush(m_Hover, false, entries);
                if (m_Symmetry) GatherBrush(MirrorPoint(m_Hover), true, entries);
                if (m_Tool == SculptTool.Smooth)
                {
                    var deltas = new Vector3[entries.Count];
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var en = entries[i]; var t = en.t;
                        var nb = t.nbr[en.g];
                        if (nb.Length == 0) continue;
                        Vector3 avg = Vector3.zero;
                        foreach (int g2 in nb) avg += t.gWorld[g2];
                        avg /= nb.Length;
                        deltas[i] = (avg - t.gWorld[en.g]) * (m_Strength * en.w * 0.5f);
                    }
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var en = entries[i]; var t = en.t;
                        for (int k = t.gVertStart[en.g]; k < t.gVertStart[en.g + 1]; k++)
                        {
                            int v = t.gVerts[k];
                            SetWorld(t, v, t.world[v] + deltas[i]);
                        }
                        AddTouched(touched, t, en.g);
                    }
                }
                else // Inflate (hold Shift to deflate)
                {
                    float sign = e.shift ? -1f : 1f;
                    var deltas = new Vector3[entries.Count];
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var en = entries[i]; var t = en.t;
                        Vector3 dir = t.skin[t.groupRep[en.g]].MultiplyVector(t.gNsum[en.g]);
                        if (dir.sqrMagnitude < 1e-16f) continue;
                        deltas[i] = dir.normalized * (sign * m_Strength * Radius * 0.08f * en.w);
                    }
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var en = entries[i]; var t = en.t;
                        for (int k = t.gVertStart[en.g]; k < t.gVertStart[en.g + 1]; k++)
                        {
                            int v = t.gVerts[k];
                            SetWorld(t, v, t.world[v] + deltas[i]);
                        }
                        AddTouched(touched, t, en.g);
                    }
                }
            }
            foreach (var kv in touched)
            {
                var affected = new List<int>(kv.Value);
                // include neighbours so their face normals refresh too
                foreach (int g in kv.Value) foreach (int nb in kv.Key.nbr[g]) if (!affected.Contains(nb)) affected.Add(nb);
                UpdateNormals(kv.Key, affected);
                Upload(kv.Key);
            }
            m_OverlayDirty = true;
        }

        static void AddTouched(Dictionary<ShapeTarget, List<int>> d, ShapeTarget t, int g)
        {
            List<int> l;
            if (!d.TryGetValue(t, out l)) { l = new List<int>(); d[t] = l; }
            if (!l.Contains(g)) l.Add(g);
        }

        void EndStroke()
        {
            if (m_Stroking && m_Mode == Mode.Sculpt)
            {
                SyncBaseToWork();
                foreach (var t in m_Sess) { RefreshWorld(t); RebuildMask(t); }
            }
            m_Stroking = false;
            m_OverlayDirty = true;
            Repaint();
        }

        // ───────────────────────── scene view ─────────────────────────

        void EnsureOverlayMat()
        {
            if (m_OverlayMat != null) return;
            var sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            m_OverlayMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            m_OverlayMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m_OverlayMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m_OverlayMat.SetInt("_Cull", 0);
            m_OverlayMat.SetInt("_ZWrite", 0);
            if (m_OverlayMat.HasProperty("_ZTest")) m_OverlayMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        }

        void RebuildOverlay()
        {
            foreach (var t in m_Sess)
            {
                var verts = new List<Vector3>();
                var cols = new List<Color>();
                for (int i = 0; i < t.tris.Length; i += 3)
                {
                    int a = t.tris[i], b = t.tris[i + 1], c = t.tris[i + 2];
                    float m = Mathf.Max(t.mask[t.group[a]], Mathf.Max(t.mask[t.group[b]], t.mask[t.group[c]]));
                    if (m <= 0f) continue;
                    bool full = t.sel[t.group[a]] || t.sel[t.group[b]] || t.sel[t.group[c]];
                    Color col = full ? new Color(1f, 0.55f, 0.1f, 0.55f) : new Color(0.2f, 0.7f, 1f, 0.15f + 0.4f * m);
                    int[] idx = { a, b, c };
                    foreach (int v in idx)
                    {
                        Vector3 nrm = t.skin[v].MultiplyVector(t.nrmWork[v]).normalized;
                        verts.Add(t.world[v] + nrm * 0.0006f);
                        cols.Add(col);
                    }
                }
                if (t.overlay == null)
                {
                    t.overlay = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                }
                t.overlay.Clear();
                if (verts.Count == 0) continue;
                var tris = new int[verts.Count];
                for (int i = 0; i < tris.Length; i++) tris[i] = i;
                t.overlay.SetVertices(verts);
                t.overlay.SetColors(cols);
                t.overlay.SetTriangles(tris, 0);
            }
            m_OverlayDirty = false;
        }

        void OnSceneGUI(SceneView sv)
        {
            if (!m_Active) return;
            Event e = Event.current;

            if (e.type == EventType.Repaint)
            {
                if (m_OverlayDirty) RebuildOverlay();
                EnsureOverlayMat();
                if (m_OverlayMat != null && sv.camera != null)
                    foreach (var t in m_Sess)
                        if (t.overlay != null && t.overlay.vertexCount > 0)
                            Graphics.DrawMesh(t.overlay, Matrix4x4.identity, m_OverlayMat, 0, sv.camera);
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Z && (e.control || e.command) && !e.shift)
            {
                UndoStep();
                e.Use();
                return;
            }

            if (m_Mode == Mode.Bunny) { BunnyHandles(); return; }

            int id = GUIUtility.GetControlID(FocusType.Passive);
            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(id);
                    break;
                case EventType.MouseMove:
                    UpdateHover(e); sv.Repaint();
                    break;
                case EventType.MouseDown:
                    if (e.button == 0 && !e.alt)
                    {
                        UpdateHover(e);
                        BeginStroke(sv, e);
                        GUIUtility.hotControl = id;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        UpdateHover(e);
                        DragStroke(sv, e);
                        e.Use();
                        sv.Repaint();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id && e.button == 0)
                    {
                        EndStroke();
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    if (m_HasHover)
                    {
                        Handles.color = m_Mode == Mode.Select ? new Color(1f, 0.6f, 0.15f, 1f) : new Color(0.4f, 0.9f, 1f, 1f);
                        Handles.DrawWireDisc(m_Hover, m_HoverN, Radius);
                        if (m_Symmetry)
                        {
                            Handles.color = new Color(Handles.color.r, Handles.color.g, Handles.color.b, 0.45f);
                            Handles.DrawWireDisc(MirrorPoint(m_Hover), MirrorVector(m_HoverN), Radius);
                        }
                    }
                    break;
            }
        }

        void UpdateHover(Event e)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            m_HasHover = RaycastAll(ray, out m_Hover, out m_HoverN);
        }

        void BunnyHandles()
        {
            bool changed = false;
            foreach (var e in m_Ears)
            {
                if (!e.ok) continue;
                Vector3 tip = e.B + e.lut[LutK];
                float bend = m_BendDeg * Mathf.Deg2Rad;
                Vector3 T = (Mathf.Cos(bend) * e.a2 + Mathf.Sin(bend) * e.nb).normalized;
                Handles.color = new Color(1f, 0.55f, 0.1f, 1f);
                Handles.DrawPolyLine(PolyLine(e));
                float sz = HandleUtility.GetHandleSize(e.B) * 0.06f;
                Handles.SphereHandleCap(0, e.B, Quaternion.identity, sz, EventType.Repaint);
                float hs = HandleUtility.GetHandleSize(tip) * 0.15f;
                EditorGUI.BeginChangeCheck();
                Vector3 nt = Handles.Slider(tip, T, hs, Handles.ConeHandleCap, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    float dl = Vector3.Dot(nt - tip, T);
                    m_Length = Mathf.Clamp((e.L0 * m_Length + dl) / e.L0, 0.3f, 12f);
                    changed = true;
                }
            }
            if (changed) { RefreshBunny(); Repaint(); }
        }

        static Vector3[] PolyLine(Ear e)
        {
            var pts = new Vector3[LutK + 1];
            for (int i = 0; i <= LutK; i++) pts[i] = e.B + e.lut[i];
            return pts;
        }

        // ───────────────────────── output ─────────────────────────

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

        class Frame { public float weight; public Vector3[] v, n, t; }

        static void AddBlendShape(Mesh m, string name, Vector3[] dv, Vector3[] dn, Vector3[] dt)
        {
            int nv = m.vertexCount;
            var names = new List<string>();
            var all = new List<List<Frame>>();
            for (int s = 0; s < m.blendShapeCount; s++)
            {
                string sn = m.GetBlendShapeName(s);
                if (sn == name) continue;
                var fl = new List<Frame>();
                for (int f = 0; f < m.GetBlendShapeFrameCount(s); f++)
                {
                    var fr = new Frame { weight = m.GetBlendShapeFrameWeight(s, f), v = new Vector3[nv], n = new Vector3[nv], t = new Vector3[nv] };
                    m.GetBlendShapeFrameVertices(s, f, fr.v, fr.n, fr.t);
                    fl.Add(fr);
                }
                names.Add(sn); all.Add(fl);
            }
            m.ClearBlendShapes();
            for (int i = 0; i < names.Count; i++)
                foreach (var fr in all[i]) m.AddBlendShapeFrame(names[i], fr.weight, fr.v, fr.n, fr.t);
            m.AddBlendShapeFrame(name, 100f, dv, dn, dt);
        }

        void ApplyToAvatar()
        {
            if (m_Mode == Mode.Bunny) CommitBunny();
            string folder = "Assets/AviCreatorEssentials/Generated/EarShaper";
            EnsureFolder(folder);
            string shape = string.IsNullOrWhiteSpace(m_ShapeName) ? "BunnyEars" : m_ShapeName.Trim();
            int count = 0;
            foreach (var t in m_Sess)
            {
                bool any = false;
                for (int i = 0; i < t.n; i++)
                    if ((t.posBase[i] - t.posOrig[i]).sqrMagnitude > 1e-14f) { any = true; break; }
                if (!any) continue;

                bool blend = m_Output == OutputKind.BlendShape && t.smr != null;
                Mesh m = Instantiate(t.orig);
                m.name = t.orig.name + (blend ? "_EarShape" : "_EarShaped");
                if (blend)
                {
                    var dv = new Vector3[t.n]; var dn = new Vector3[t.n]; var dt = new Vector3[t.n];
                    for (int i = 0; i < t.n; i++)
                    {
                        dv[i] = t.posBase[i] - t.posOrig[i];
                        dn[i] = t.nrmBase[i] - t.nrmOrig[i];
                        if (t.HasTan)
                            dt[i] = new Vector3(t.tanBase[i].x - t.tanOrig[i].x, t.tanBase[i].y - t.tanOrig[i].y, t.tanBase[i].z - t.tanOrig[i].z);
                    }
                    AddBlendShape(m, shape, dv, dn, dt);
                }
                else
                {
                    m.vertices = t.posBase;
                    m.normals = t.nrmBase;
                    if (t.HasTan) m.tangents = t.tanBase;
                }
                // make the bounds cover the longer ears so they don't get culled
                Bounds b = m.bounds;
                for (int i = 0; i < t.n; i++)
                    if ((t.posBase[i] - t.posOrig[i]).sqrMagnitude > 1e-14f) b.Encapsulate(t.posBase[i]);
                m.bounds = b;

                string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + APUtil.SafeFileName(m.name) + ".asset");
                AssetDatabase.CreateAsset(m, path);

                Undo.RecordObject(t.renderer, "Apply ear shape");
                if (t.smr != null) t.smr.sharedMesh = m;
                else if (t.filter != null)
                {
                    Undo.RecordObject(t.filter, "Apply ear shape");
                    t.filter.sharedMesh = m;
                }
                if (blend && m_DefaultOn && t.smr != null)
                {
                    int idx = m.GetBlendShapeIndex(shape);
                    if (idx >= 0) t.smr.SetBlendShapeWeight(idx, 100f);
                }
                EditorUtility.SetDirty(t.renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(t.renderer);
                count++;
            }
            AssetDatabase.SaveAssets();
            EndSession(false);
            m_Status = count == 0
                ? "Nothing was changed, so no mesh was made. Your avatar is as it was."
                : "Done. " + count + " mesh" + (count == 1 ? "" : "es") + " saved in " + folder + " and put on your avatar. The original meshes were not touched. Ctrl+Z undoes the swap.";
        }

        // ───────────────────────── window UI ─────────────────────────

        void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            if (!m_Active) DrawSetup(); else DrawSession();
            if (!string.IsNullOrEmpty(m_Status)) EditorGUILayout.HelpBox(m_Status, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        void DrawSetup()
        {
            EditorGUILayout.HelpBox("Reshape ears (or any part) right on the mesh, for example cat ears into bunny ears. Only vertex positions change, so the armature, bone weights and UVs stay exactly as they are, and seams can't split open. The result is saved as a new mesh and your original is never touched.", MessageType.None);
            EditorGUILayout.Space(4);
            m_Root = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Avatar root", "The avatar's top object. Its left/right (X) is used for symmetry."), m_Root, typeof(GameObject), true);
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Meshes that contain the ears", EditorStyles.boldLabel);
            for (int i = 0; i < m_Pending.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                m_Pending[i] = (Renderer)EditorGUILayout.ObjectField(m_Pending[i], typeof(Renderer), true);
                if (GUILayout.Button("×", GUILayout.Width(22))) { m_Pending.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add selected"))
            {
                foreach (var go in Selection.gameObjects) { AddPending(go); if (m_Root == null) SetFromObject(go); }
            }
            if (GUILayout.Button("Clear")) m_Pending.Clear();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Select the body/head mesh in the Hierarchy and press Add selected. If the ear fluff is a separate mesh (hair, ears), add that too.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(m_Root == null || m_Pending.Count == 0))
                if (GUILayout.Button("Start shaping", GUILayout.Height(30))) StartSession();

            if (m_UnreadableMesh != null && GUILayout.Button("Enable Read/Write on \"" + m_UnreadableMesh.name + "\""))
            {
                string p = AssetDatabase.GetAssetPath(m_UnreadableMesh);
                var imp = AssetImporter.GetAtPath(p) as ModelImporter;
                if (imp != null) { imp.isReadable = true; imp.SaveAndReimport(); m_Status = "Read/Write turned on. Press Start shaping again."; m_UnreadableMesh = null; }
                else m_Status = "This mesh isn't from a model file, so Read/Write can't be changed here.";
            }
        }

        void DrawSession()
        {
            string[] names = { "1  Select", "2  Bunny shape", "3  Sculpt" };
            int mi = GUILayout.Toolbar((int)m_Mode, names, GUILayout.Height(26));
            if (mi != (int)m_Mode) SetMode((Mode)mi);
            EditorGUILayout.Space(4);

            if (m_Mode == Mode.Select) DrawSelectPanel();
            else if (m_Mode == Mode.Bunny) DrawBunnyPanel();
            else DrawSculptPanel();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(m_Undo.Count == 0)) if (GUILayout.Button("Undo step (Ctrl+Z)")) UndoStep();
            if (GUILayout.Button("Reset all shaping")) { if (EditorUtility.DisplayDialog("Reset shaping", "Throw away all shaping done in this session?", "Reset", "Cancel")) ResetAllShape(); }
            if (GUILayout.Button("Refresh pose")) { foreach (var t in m_Sess) { ComputeSkin(t); RefreshWorld(t); } m_OverlayDirty = true; }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Finish", EditorStyles.boldLabel);
            m_Output = (OutputKind)EditorGUILayout.EnumPopup(new GUIContent("Save as", "Blend shape keeps the original ears and adds the bunny ears as a shape you can toggle in-game. Bake replaces the vertex positions in the new mesh."), m_Output);
            if (m_Output == OutputKind.BlendShape)
            {
                m_ShapeName = EditorGUILayout.TextField("Blend shape name", m_ShapeName);
                m_DefaultOn = EditorGUILayout.Toggle(new GUIContent("Turn it on by default", "Sets the blend shape to 100 on the avatar."), m_DefaultOn);
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply to avatar", GUILayout.Height(28))) { ApplyToAvatar(); GUIUtility.ExitGUI(); }
            if (GUILayout.Button("Cancel", GUILayout.Height(28), GUILayout.Width(80)))
            {
                if (EditorUtility.DisplayDialog("Cancel", "Discard everything and put the original meshes back?", "Discard", "Keep working")) { EndSession(true); GUIUtility.ExitGUI(); }
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawSelectPanel()
        {
            EditorGUILayout.HelpBox("Paint over the ears in the Scene view (left-drag). Ctrl+drag removes. Pick every part that should become part of the ear, on both sides. Orange is selected; blue is the soft blend into the head.", MessageType.None);
            m_BrushCm = EditorGUILayout.Slider("Brush size (cm)", m_BrushCm, 0.3f, 20f);
            m_Symmetry = EditorGUILayout.Toggle("Symmetry (left/right)", m_Symmetry);
            EditorGUI.BeginChangeCheck();
            m_FeatherCm = EditorGUILayout.Slider(new GUIContent("Blend into head (cm)", "How far the change fades out past the selection, along the surface."), m_FeatherCm, 0f, 8f);
            if (EditorGUI.EndChangeCheck()) foreach (var t in m_Sess) RebuildMask(t);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Or select by ear bones", EditorStyles.boldLabel);
            for (int i = 0; i < m_EarBones.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                m_EarBones[i] = (Transform)EditorGUILayout.ObjectField(m_EarBones[i], typeof(Transform), true);
                if (GUILayout.Button("×", GUILayout.Width(22))) { m_EarBones.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Find ear bones")) FindEarBones();
            if (GUILayout.Button("Add bone slot")) m_EarBones.Add(null);
            EditorGUILayout.EndHorizontal();
            m_BoneThreshold = EditorGUILayout.Slider("Min bone weight", m_BoneThreshold, 0.05f, 1f);
            if (GUILayout.Button("Select vertices weighted to these bones")) SelectByBones();

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Grow")) { Grow(); }
            if (GUILayout.Button("Shrink")) { Shrink(); }
            if (GUILayout.Button("Clear")) { foreach (var t in m_Sess) { Array.Clear(t.sel, 0, t.sel.Length); RebuildMask(t); } }
            EditorGUILayout.EndHorizontal();
            int count = 0;
            foreach (var t in m_Sess) foreach (bool b in t.sel) if (b) count++;
            EditorGUILayout.LabelField(count + " points selected", EditorStyles.miniLabel);
        }

        void FindEarBones()
        {
            m_EarBones.Clear();
            if (m_Root == null) return;
            foreach (var tr in m_Root.GetComponentsInChildren<Transform>(true))
            {
                string n = tr.name.ToLowerInvariant();
                if (!n.Contains("ear") || n.Contains("hear") || n.Contains("bear") || n.Contains("wear") || n.Contains("pear") || n.Contains("year")) continue;
                var p = tr.parent;
                if (p != null && p != m_Root.transform && p.name.ToLowerInvariant().Contains("ear")) continue;
                m_EarBones.Add(tr);
            }
            m_Status = m_EarBones.Count == 0 ? "No bones with \"ear\" in the name were found. Drag your ear bones in manually, or just paint the selection." : "Found " + m_EarBones.Count + " ear bone(s).";
        }

        void SelectByBones()
        {
            var set = new HashSet<Transform>();
            foreach (var b in m_EarBones) if (b != null) foreach (var c in b.GetComponentsInChildren<Transform>(true)) set.Add(c);
            if (set.Count == 0) { m_Status = "Add at least one bone first."; return; }
            int picked = 0;
            foreach (var t in m_Sess)
            {
                if (t.smr == null || t.bw == null || t.bw.Length != t.n) continue;
                var bones = t.smr.bones;
                var isB = new bool[bones.Length];
                for (int j = 0; j < bones.Length; j++) isB[j] = bones[j] != null && set.Contains(bones[j]);
                for (int v = 0; v < t.n; v++)
                {
                    var b = t.bw[v];
                    float w = 0f;
                    if (b.boneIndex0 < isB.Length && isB[b.boneIndex0]) w += b.weight0;
                    if (b.boneIndex1 < isB.Length && isB[b.boneIndex1]) w += b.weight1;
                    if (b.boneIndex2 < isB.Length && isB[b.boneIndex2]) w += b.weight2;
                    if (b.boneIndex3 < isB.Length && isB[b.boneIndex3]) w += b.weight3;
                    if (w >= m_BoneThreshold && !t.sel[t.group[v]]) { t.sel[t.group[v]] = true; picked++; }
                }
                RebuildMask(t);
            }
            m_Status = picked + " points added from the bone weights.";
        }

        void Grow()
        {
            foreach (var t in m_Sess)
            {
                var add = new List<int>();
                for (int g = 0; g < t.groupCount; g++)
                {
                    if (t.sel[g]) continue;
                    foreach (int nb in t.nbr[g]) if (t.sel[nb]) { add.Add(g); break; }
                }
                foreach (int g in add) t.sel[g] = true;
                RebuildMask(t);
            }
        }

        void Shrink()
        {
            foreach (var t in m_Sess)
            {
                var rem = new List<int>();
                for (int g = 0; g < t.groupCount; g++)
                {
                    if (!t.sel[g]) continue;
                    foreach (int nb in t.nbr[g]) if (!t.sel[nb]) { rem.Add(g); break; }
                }
                foreach (int g in rem) t.sel[g] = false;
                RebuildMask(t);
            }
        }

        void DrawBunnyPanel()
        {
            if (m_Ears.Count == 0)
            {
                EditorGUILayout.HelpBox("No ears found. Go to Select and select the ear vertices first (at least a few points on each ear).", MessageType.Warning);
                if (GUILayout.Button("Look again")) { RefitEars(); RefreshBunny(); }
                return;
            }
            EditorGUILayout.HelpBox(m_Ears.Count + " ear" + (m_Ears.Count == 1 ? "" : "s") + " found. Drag the orange arrow in the Scene view to set the length, or use the sliders. Changes are a live preview until you press Apply shape.", MessageType.None);
            EditorGUI.BeginChangeCheck();
            m_Length = EditorGUILayout.Slider(new GUIContent("Length", "1 = the current length."), m_Length, 0.3f, 12f);
            m_Width = EditorGUILayout.Slider("Width", m_Width, 0.3f, 2.5f);
            m_Thick = EditorGUILayout.Slider("Thickness", m_Thick, 0.3f, 3f);
            m_Bunnify = EditorGUILayout.Slider(new GUIContent("Bunny profile", "Turns the pointed cat triangle into a long oval ear with a rounded tip."), m_Bunnify, 0f, 1f);
            m_TipRound = EditorGUILayout.Slider(new GUIContent("Where the rounding starts", "Lower = rounder, more spoon-shaped. Higher = straighter sides."), m_TipRound, 0.1f, 0.95f);
            m_BaseLock = EditorGUILayout.Slider(new GUIContent("Keep base unstretched", "How much of the ear root keeps its original proportions before the stretch kicks in."), m_BaseLock, 0.05f, 0.7f);
            m_Cup = EditorGUILayout.Slider(new GUIContent("Curl / cup", "Curves the ear across its width."), m_Cup, -1f, 1f);
            m_BendDeg = EditorGUILayout.Slider(new GUIContent("Bend", "Positive bends the ear forward, negative back."), m_BendDeg, -120f, 120f);
            m_TwistDeg = EditorGUILayout.Slider("Twist", m_TwistDeg, -90f, 90f);
            m_OutDeg = EditorGUILayout.Slider(new GUIContent("Tilt outward", "Mirrors automatically on the other ear."), m_OutDeg, -45f, 90f);
            m_FwdDeg = EditorGUILayout.Slider("Tilt forward", m_FwdDeg, -60f, 60f);
            if (EditorGUI.EndChangeCheck()) { RefreshBunny(); SceneView.RepaintAll(); }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Preset: Standing")) { ResetParams(true); RefreshBunny(); }
            if (GUILayout.Button("Preset: Tall & slim")) { ResetParams(true); m_Length = 4.5f; m_Width = 0.85f; m_Cup = 0.1f; RefreshBunny(); }
            if (GUILayout.Button("Preset: Lop")) { ResetParams(true); m_Length = 3f; m_BendDeg = 50f; m_OutDeg = 35f; m_Cup = 0.25f; RefreshBunny(); }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("No change")) { ResetParams(false); RefreshBunny(); }
            if (GUILayout.Button("Apply shape", GUILayout.Height(24))) CommitBunny();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Apply shape locks it in so you can sculpt on top or shape again. Switching tabs does it for you.", EditorStyles.wordWrappedMiniLabel);
        }

        void DrawSculptPanel()
        {
            EditorGUILayout.HelpBox("Finish the ears by hand. Left-drag in the Scene view. Grab pulls the surface, Smooth relaxes lumps, Inflate puffs it out (hold Shift to deflate).", MessageType.None);
            m_Tool = (SculptTool)GUILayout.Toolbar((int)m_Tool, new[] { "Grab", "Smooth", "Inflate" }, GUILayout.Height(24));
            m_BrushCm = EditorGUILayout.Slider("Brush size (cm)", m_BrushCm, 0.3f, 20f);
            m_Strength = EditorGUILayout.Slider("Strength", m_Strength, 0.05f, 1f);
            m_Symmetry = EditorGUILayout.Toggle("Symmetry (left/right)", m_Symmetry);
            m_LimitToSel = EditorGUILayout.Toggle(new GUIContent("Only inside the selection", "Protects the rest of the head. The selection and its blend zone from the Select tab are editable."), m_LimitToSel);
            if (m_LimitToSel && !AnySelected())
                EditorGUILayout.HelpBox("Nothing is selected, so nothing can be sculpted. Select the ear in the Select tab, or turn off \"Only inside the selection\".", MessageType.Warning);
        }
    }
}
