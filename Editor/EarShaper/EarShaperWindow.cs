using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Ear Shaper: reshape ears (for example cat ears into bunny ears) directly on the avatar's mesh.
    /// Only vertex positions change. Bone weights, UVs, bindposes and every other blend shape are left alone,
    /// vertices that sit at the same position (UV seams) always move together so nothing splits open,
    /// and the result is saved as a new mesh asset (as a blend shape by default), never over the original.
    /// Everything is non-destructive: original mesh, then the parts' settings, then a hand-sculpt layer. All of it
    /// is saved next to the result so the shaping can be reopened and adjusted later.
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
            w.minSize = new Vector2(420, 620);
            if (go != null) w.SetFromObject(go);
            w.Show();
        }

        // ───────────────────────── data ─────────────────────────

        enum Mode { Select, Shape, Sculpt }
        enum SelTool { Brush, Linked }
        enum LinkMode { UVIsland, MeshPart }
        enum SculptTool { Grab, Smooth, Inflate }
        enum OutputKind { BlendShape, Bake }

        const int PB = 14;       // profile bins along the ear
        const int LutK = 32;     // centerline samples
        const string Folder = "Assets/AviCreatorEssentials/Generated/EarShaper";

        class ShapeTarget
        {
            public Renderer renderer;
            public SkinnedMeshRenderer smr;
            public MeshFilter filter;
            public Mesh orig, restore, preview, overlay;
            public EarShaperTargetData loaded;
            public float[] weights;
            public int n;
            public int[] tris;
            public BoneWeight[] bw;
            public Vector2[] uv;
            public Vector3[] posOrig, posParam, posWork, nrmOrig, nrmWork, sculpt;
            public Vector4[] tanOrig, tanWork;
            public Matrix4x4[] skin, skinInv;
            public bool[] skinOk;
            public Vector3[] world, strokeStart;
            public int[] group, groupRep, gVertStart, gVerts, gTriStart, gTris;
            public int groupCount;
            public int[][] nbr;
            public Vector3[] gWorld, gOrigWorld, gNsum, gNsumOrig;
            public int[] partOf, maskPart;
            public float[] mask;
            public int[] islandUV, islandMesh;
            public bool HasTan { get { return tanOrig != null && tanOrig.Length == n; } }
        }

        class Ear
        {
            public int side, partIndex;
            public bool ok;
            public Vector3 B, a, w, n;
            public float L0, maxHalfW;
            public float[] halfW = new float[PB], cx = new float[PB], cy = new float[PB];
            // inner fluff
            public Ear parent;
            public float u0, u1, LF;
            // prepared for the current settings
            public Vector3 a2, w2, n2, nb;
            public float nbSign = 1f, slope1 = 1f;
            public Vector3[] lut = new Vector3[LutK + 1];
            public float[] htab = new float[LutK + 1];
        }

        class Snap
        {
            public EarPart[] parts;
            public int active;
            public int[][] partOf;
            public Vector3[][] sculpt;
        }

        struct Entry { public ShapeTarget t; public int g; public float w; public bool mirrored; }

        // setup
        GameObject m_Root;
        readonly List<Renderer> m_Pending = new List<Renderer>();
        readonly List<Transform> m_EarBones = new List<Transform>();
        Mesh m_UnreadableMesh;
        string m_Status;
        Vector2 m_Scroll;
        EarShaperSettings m_Resume;
        bool m_StartOver;

        // session
        bool m_Active;
        List<ShapeTarget> m_Sess = new List<ShapeTarget>();
        EarShaperSettings m_Loaded;
        Mode m_Mode;
        readonly List<Snap> m_Undo = new List<Snap>();
        readonly List<Snap> m_Redo = new List<Snap>();
        EarShaperUndoProxy m_Proxy;
        int m_Rev;
        Snap m_PendingSnap;
        readonly List<Ear> m_Fits = new List<Ear>();
        Material m_OverlayMat;
        Mesh m_HoverMesh;
        bool m_OverlayDirty;
        List<EarPart> m_Parts = new List<EarPart>();
        int m_ActivePart;

        // select / sculpt settings
        float m_BrushCm = 2f, m_BoneThreshold = 0.4f, m_Strength = 0.6f;
        bool m_Symmetry = true, m_LimitToSel = true;
        SelTool m_SelTool = SelTool.Brush;
        LinkMode m_LinkMode = LinkMode.UVIsland;
        SculptTool m_Tool = SculptTool.Grab;

        // output
        OutputKind m_Output = OutputKind.BlendShape;
        string m_ShapeName = "BunnyEars";
        bool m_DefaultOn = true;

        // input state
        bool m_HasHover, m_Stroking, m_StrokeRemove;
        Vector3 m_Hover, m_HoverN, m_GrabHit;
        ShapeTarget m_HoverT;
        int m_HoverTri = -1, m_HoverIsland = -1;
        Plane m_GrabPlane;
        List<Entry> m_Entries = new List<Entry>();

        EarPart Part { get { return m_Parts[Mathf.Clamp(m_ActivePart, 0, m_Parts.Count - 1)]; } }

        // ───────────────────────── lifecycle ─────────────────────────

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.playModeStateChanged += OnPlayMode;
            Undo.undoRedoPerformed += OnUndoRedo;
            RefreshResume();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            Undo.undoRedoPerformed -= OnUndoRedo;
            if (m_Active) EndSession(true);
            if (m_OverlayMat != null) DestroyImmediate(m_OverlayMat);
            if (m_HoverMesh != null) DestroyImmediate(m_HoverMesh);
        }

        void OnPlayMode(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.ExitingEditMode && m_Active) EndSession(true);
        }

        void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (!m_Active) return;
            foreach (var t in m_Sess)
                if (t.renderer != null) { SetMesh(t, t.restore); RestoreWeights(t); }
        }

        void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            if (!m_Active) return;
            foreach (var t in m_Sess) if (t.renderer != null && t.preview != null) SetMesh(t, t.preview);
        }

        static Mesh GetMesh(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var f = r != null ? r.GetComponent<MeshFilter>() : null;
            return f != null ? f.sharedMesh : null;
        }

        static void SetMesh(ShapeTarget t, Mesh m)
        {
            if (t.smr != null) t.smr.sharedMesh = m;
            else if (t.filter != null) t.filter.sharedMesh = m;
        }

        static void RestoreWeights(ShapeTarget t)
        {
            if (t.smr == null || t.weights == null || t.smr.sharedMesh == null) return;
            int c = Mathf.Min(t.weights.Length, t.smr.sharedMesh.blendShapeCount);
            for (int i = 0; i < c; i++) t.smr.SetBlendShapeWeight(i, t.weights[i]);
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
            RefreshResume();
        }

        void AddPending(GameObject go)
        {
            foreach (var r in go.GetComponents<Renderer>())
            {
                bool ok = r is SkinnedMeshRenderer || (r is MeshRenderer && r.GetComponent<MeshFilter>() != null);
                if (ok && !m_Pending.Contains(r)) m_Pending.Add(r);
            }
        }

        static EarShaperSettings FindSettingsFor(Mesh m, out EarShaperTargetData td)
        {
            td = null;
            if (m == null) return null;
            foreach (string guid in AssetDatabase.FindAssets("t:EarShaperSettings"))
            {
                var s = AssetDatabase.LoadAssetAtPath<EarShaperSettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (s == null || s.targets == null) continue;
                foreach (var d in s.targets)
                    if (d != null && d.result == m) { td = d; return s; }
            }
            return null;
        }

        void RefreshResume()
        {
            m_Resume = null;
            foreach (var r in m_Pending)
            {
                if (r == null) continue;
                EarShaperTargetData td;
                var s = FindSettingsFor(GetMesh(r), out td);
                if (s != null) { m_Resume = s; return; }
            }
        }

        // ───────────────────────── session setup ─────────────────────────

        static List<EarPart> DefaultParts()
        {
            var ear = new EarPart { name = "Ear", kind = 0, color = new Color(1f, 0.55f, 0.1f, 1f), featherCm = 1.5f };
            var fluff = new EarPart { name = "Inner fluff", kind = 1, color = new Color(0.3f, 0.85f, 0.35f, 1f), featherCm = 0.5f };
            return new List<EarPart> { ear, fluff };
        }

        void StartSession()
        {
            m_Status = null;
            m_UnreadableMesh = null;
            if (EditorApplication.isPlayingOrWillChangePlaymode) { m_Status = "Leave Play mode first."; return; }
            if (m_Root == null) { m_Status = "Pick the avatar root first."; return; }
            var list = new List<ShapeTarget>();
            EarShaperSettings found = null;
            foreach (var r in m_Pending)
            {
                if (r == null) continue;
                Mesh cur = GetMesh(r);
                EarShaperTargetData td;
                var s = FindSettingsFor(cur, out td);
                if (s != null && found == null) found = s;
                if (s != found) { s = null; td = null; }
                Mesh src = (td != null && td.original != null) ? td.original : cur;
                string err;
                var t = BuildTarget(r, cur, src, out err);
                if (t == null) { m_Status = err; return; }
                t.loaded = td;
                list.Add(t);
            }
            if (list.Count == 0) { m_Status = "Add at least one mesh first (select it in the Hierarchy, then press Add selected)."; return; }
            m_Sess = list;
            m_Loaded = found;
            m_Undo.Clear();
            m_Redo.Clear();
            m_PendingSnap = null;
            m_Proxy = CreateInstance<EarShaperUndoProxy>();
            m_Proxy.hideFlags = HideFlags.HideAndDontSave;
            m_Rev = 0;
            m_Parts = DefaultParts();
            m_ActivePart = 0;
            bool resumed = false;
            if (found != null && !m_StartOver && found.parts != null && found.parts.Count > 0)
            {
                m_Parts = new List<EarPart>();
                foreach (var p in found.parts) m_Parts.Add(p.Clone());
                m_Output = (OutputKind)Mathf.Clamp(found.output, 0, 1);
                m_ShapeName = string.IsNullOrEmpty(found.shapeName) ? "BunnyEars" : found.shapeName;
                m_DefaultOn = found.defaultOn;
                foreach (var t in m_Sess)
                {
                    var td = t.loaded;
                    if (td == null) continue;
                    if (td.partOf != null && td.partOf.Length == t.n)
                        for (int v = 0; v < t.n; v++)
                            if (td.partOf[v] >= 0 && td.partOf[v] < m_Parts.Count) t.partOf[t.group[v]] = td.partOf[v];
                    if (td.sculptIdx != null && td.sculptVal != null && td.sculptIdx.Length == td.sculptVal.Length)
                        for (int k = 0; k < td.sculptIdx.Length; k++)
                            if (td.sculptIdx[k] >= 0 && td.sculptIdx[k] < t.n) t.sculpt[td.sculptIdx[k]] = td.sculptVal[k];
                }
                resumed = true;
            }
            foreach (var t in m_Sess)
            {
                t.preview = Instantiate(t.orig);
                t.preview.name = t.orig.name + " (ear preview)";
                t.preview.hideFlags = HideFlags.HideAndDontSave;
                SetMesh(t, t.preview);
                RestoreWeights(t);
                RebuildMask(t);
            }
            m_Active = true;
            m_Mode = resumed ? Mode.Shape : Mode.Select;
            m_Fits.Clear();
            m_OverlayDirty = true;
            if (resumed) RefitAll();
            Recompute(false);
            m_Status = resumed ? "Picked up where you left off. Everything is still adjustable." : null;
            SceneView.RepaintAll();
        }

        void EndSession(bool restore)
        {
            foreach (var t in m_Sess)
            {
                if (restore && t.renderer != null) { SetMesh(t, t.restore); RestoreWeights(t); }
                if (t.preview != null) DestroyImmediate(t.preview);
                if (t.overlay != null) DestroyImmediate(t.overlay);
            }
            m_Sess.Clear();
            m_Fits.Clear();
            m_Undo.Clear();
            m_Redo.Clear();
            m_PendingSnap = null;
            if (m_Proxy != null) { Undo.ClearUndo(m_Proxy); DestroyImmediate(m_Proxy); m_Proxy = null; }
            m_Rev = 0;
            m_Active = false;
            m_Stroking = false;
            m_HoverT = null;
            RefreshResume();
            Repaint();
            SceneView.RepaintAll();
        }

        ShapeTarget BuildTarget(Renderer r, Mesh restoreMesh, Mesh src, out string err)
        {
            err = null;
            var t = new ShapeTarget { renderer = r };
            t.smr = r as SkinnedMeshRenderer;
            t.filter = r.GetComponent<MeshFilter>();
            if (src == null) { err = r.name + " has no mesh."; return null; }
            if (!src.isReadable)
            {
                m_UnreadableMesh = src;
                err = "The mesh \"" + src.name + "\" is not readable. Turn on Read/Write for it (button below) and try again.";
                return null;
            }
            t.orig = src;
            t.restore = restoreMesh;
            if (t.smr != null && restoreMesh != null)
            {
                t.weights = new float[restoreMesh.blendShapeCount];
                for (int i = 0; i < t.weights.Length; i++) t.weights[i] = t.smr.GetBlendShapeWeight(i);
            }
            int n = src.vertexCount;
            t.n = n;
            t.tris = src.triangles;
            t.posOrig = src.vertices;
            var nrm = src.normals;
            if (nrm.Length != n)
            {
                var tmp = Instantiate(src);
                tmp.RecalculateNormals();
                nrm = tmp.normals;
                DestroyImmediate(tmp);
            }
            t.nrmOrig = nrm;
            var tan = src.tangents;
            t.tanOrig = tan.Length == n ? tan : null;
            t.bw = src.boneWeights;
            var uv = src.uv;
            t.uv = uv.Length == n ? uv : new Vector2[n];
            t.posParam = (Vector3[])t.posOrig.Clone();
            t.posWork = (Vector3[])t.posOrig.Clone();
            t.nrmWork = (Vector3[])t.nrmOrig.Clone();
            if (t.HasTan) t.tanWork = (Vector4[])t.tanOrig.Clone();
            t.sculpt = new Vector3[n];
            t.world = new Vector3[n];
            ComputeSkin(t);
            BuildGroups(t);
            BuildIslands(t);
            t.gWorld = new Vector3[t.groupCount];
            t.gOrigWorld = new Vector3[t.groupCount];
            t.gNsum = new Vector3[t.groupCount];
            t.gNsumOrig = new Vector3[t.groupCount];
            t.partOf = new int[t.groupCount];
            for (int g = 0; g < t.groupCount; g++) t.partOf[g] = -1;
            t.mask = new float[t.groupCount];
            t.maskPart = new int[t.groupCount];
            for (int g = 0; g < t.groupCount; g++) t.maskPart[g] = -1;
            RefreshWorld(t);
            RefreshOrigWorld(t);
            for (int g = 0; g < t.groupCount; g++) t.gNsumOrig[g] = GroupNormalSum(t, t.posOrig, g);
            Array.Copy(t.gNsumOrig, t.gNsum, t.groupCount);
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

        static int Find(int[] p, int x)
        {
            while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
            return x;
        }

        static void Union(int[] p, int a, int b)
        {
            int ra = Find(p, a), rb = Find(p, b);
            if (ra != rb) p[ra] = rb;
        }

        /// <summary>Per-vertex island ids: UV islands (like Blender's L) and connected mesh parts (ignoring UVs).</summary>
        static void BuildIslands(ShapeTarget t)
        {
            int n = t.n;
            var pu = new int[n]; var pm = new int[n];
            for (int i = 0; i < n; i++) { pu[i] = i; pm[i] = i; }
            for (int i = 0; i + 2 < t.tris.Length; i += 3)
            {
                int a = t.tris[i], b = t.tris[i + 1], c = t.tris[i + 2];
                Union(pu, a, b); Union(pu, b, c);
                Union(pm, t.group[a], t.group[b]); Union(pm, t.group[b], t.group[c]);
            }
            var seen = new Dictionary<Vector3Int, int>(n);
            for (int i = 0; i < n; i++)
            {
                var key = new Vector3Int(t.group[i], Mathf.RoundToInt(t.uv[i].x * 8192f), Mathf.RoundToInt(t.uv[i].y * 8192f));
                int first;
                if (seen.TryGetValue(key, out first)) Union(pu, first, i); else seen[key] = i;
            }
            t.islandUV = new int[n];
            t.islandMesh = new int[n];
            for (int i = 0; i < n; i++) { t.islandUV[i] = Find(pu, i); t.islandMesh[i] = Find(pm, t.group[i]); }
        }

        static void RefreshWorld(ShapeTarget t)
        {
            for (int i = 0; i < t.n; i++) t.world[i] = t.skin[i].MultiplyPoint3x4(t.posWork[i]);
            for (int g = 0; g < t.groupCount; g++) t.gWorld[g] = t.world[t.groupRep[g]];
        }

        static void RefreshOrigWorld(ShapeTarget t)
        {
            for (int g = 0; g < t.groupCount; g++) { int v = t.groupRep[g]; t.gOrigWorld[g] = t.skin[v].MultiplyPoint3x4(t.posOrig[v]); }
        }

        static void SetWorld(ShapeTarget t, int v, Vector3 w)
        {
            if (!t.skinOk[v]) return;
            t.world[v] = w;
            t.posWork[v] = t.skinInv[v].MultiplyPoint3x4(w);
            int g = t.group[v];
            if (t.groupRep[g] == v) t.gWorld[g] = w;
        }

        static Vector3 GroupNormalSum(ShapeTarget t, Vector3[] pos, int g)
        {
            Vector3 s = Vector3.zero;
            for (int k = t.gTriStart[g]; k < t.gTriStart[g + 1]; k++)
            {
                int ti = t.gTris[k] * 3;
                Vector3 p0 = pos[t.tris[ti]], p1 = pos[t.tris[ti + 1]], p2 = pos[t.tris[ti + 2]];
                s += Vector3.Cross(p1 - p0, p2 - p0);
            }
            return s;
        }

        /// <summary>Turns each moved group's normals and tangents by however much the faces around it turned (used while sculpting).</summary>
        static void UpdateNormals(ShapeTarget t, List<int> groups)
        {
            foreach (int g in groups)
            {
                Vector3 old = t.gNsum[g];
                Vector3 nw = GroupNormalSum(t, t.posWork, g);
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

        static float S(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        /// <summary>Per group: which part claims it and how strongly. 1 inside a part's selection, fading to 0 over that part's blend distance.</summary>
        void RebuildMask(ShapeTarget t)
        {
            int gc = t.groupCount;
            var best = new float[gc];
            var bestP = new int[gc];
            for (int g = 0; g < gc; g++) bestP[g] = -1;
            var dist = new float[gc];
            var inq = new bool[gc];
            var q = new Queue<int>();
            for (int pi = 0; pi < m_Parts.Count; pi++)
            {
                float feather = m_Parts[pi].featherCm * 0.01f;
                bool any = false;
                for (int g = 0; g < gc; g++)
                {
                    inq[g] = false;
                    if (t.partOf[g] == pi) { dist[g] = 0f; q.Enqueue(g); inq[g] = true; any = true; }
                    else dist[g] = float.MaxValue;
                }
                if (!any) { q.Clear(); continue; }
                if (feather > 1e-6f)
                {
                    while (q.Count > 0)
                    {
                        int g = q.Dequeue();
                        inq[g] = false;
                        foreach (int nb in t.nbr[g])
                        {
                            float nd = dist[g] + Vector3.Distance(t.gOrigWorld[g], t.gOrigWorld[nb]);
                            if (nd < dist[nb] && nd <= feather)
                            {
                                dist[nb] = nd;
                                if (!inq[nb]) { inq[nb] = true; q.Enqueue(nb); }
                            }
                        }
                    }
                }
                else q.Clear();
                for (int g = 0; g < gc; g++)
                {
                    float m;
                    if (t.partOf[g] == pi) m = 1f;
                    else if (t.partOf[g] >= 0) m = 0f;
                    else if (feather > 1e-6f && dist[g] <= feather) m = 1f - S(dist[g] / feather);
                    else m = 0f;
                    if (m > best[g]) { best[g] = m; bestP[g] = pi; }
                }
            }
            t.mask = best;
            t.maskPart = bestP;
            m_OverlayDirty = true;
        }

        void RebuildAllMasks() { foreach (var t in m_Sess) RebuildMask(t); }

        // ───────────────────────── undo ─────────────────────────

        Snap TakeSnap()
        {
            var s = new Snap { parts = new EarPart[m_Parts.Count], active = m_ActivePart, partOf = new int[m_Sess.Count][], sculpt = new Vector3[m_Sess.Count][] };
            for (int i = 0; i < m_Parts.Count; i++) s.parts[i] = m_Parts[i].Clone();
            for (int i = 0; i < m_Sess.Count; i++)
            {
                s.partOf[i] = (int[])m_Sess[i].partOf.Clone();
                s.sculpt[i] = (Vector3[])m_Sess[i].sculpt.Clone();
            }
            return s;
        }

        void PushUndo() { PushSnap(TakeSnap()); }

        /// <summary>Records one undoable step: the state from before the change. Also registers it with Unity's Undo so Ctrl+Z works anywhere.</summary>
        void PushSnap(Snap s)
        {
            m_PendingSnap = null;
            m_Undo.Add(s);
            if (m_Undo.Count > 30) m_Undo.RemoveAt(0);
            m_Redo.Clear();
            if (m_Proxy != null)
            {
                Undo.RegisterCompleteObjectUndo(m_Proxy, "Ear Shaper");
                m_Proxy.rev = ++m_Rev;
            }
        }

        void OnUndoRedo()
        {
            if (!m_Active || m_Proxy == null) return;
            int r = m_Proxy.rev;
            if (r == m_Rev) return;
            bool undo = r < m_Rev;
            while (m_Rev != r)
            {
                if (undo) { InternalUndo(); m_Rev--; }
                else { InternalRedo(); m_Rev++; }
            }
            Repaint();
            SceneView.RepaintAll();
        }

        void InternalUndo()
        {
            if (m_Undo.Count == 0) return;
            var s = m_Undo[m_Undo.Count - 1];
            m_Undo.RemoveAt(m_Undo.Count - 1);
            m_Redo.Add(TakeSnap());
            RestoreSnap(s);
        }

        void InternalRedo()
        {
            if (m_Redo.Count == 0) return;
            var s = m_Redo[m_Redo.Count - 1];
            m_Redo.RemoveAt(m_Redo.Count - 1);
            m_Undo.Add(TakeSnap());
            RestoreSnap(s);
        }

        void RestoreSnap(Snap s)
        {
            m_PendingSnap = null;
            m_Parts = new List<EarPart>(s.parts);
            m_ActivePart = Mathf.Clamp(s.active, 0, m_Parts.Count - 1);
            for (int i = 0; i < m_Sess.Count; i++)
            {
                m_Sess[i].partOf = s.partOf[i];
                m_Sess[i].sculpt = s.sculpt[i];
            }
            RebuildAllMasks();
            RefitAll();
            Recompute(false);
        }

        void ResetAllShape()
        {
            PushUndo();
            foreach (var p in m_Parts) { float f = p.featherCm; p.ResetIdentity(); p.featherCm = f; }
            foreach (var t in m_Sess) Array.Clear(t.sculpt, 0, t.n);
            Recompute(false);
        }

        // ───────────────────────── fitting ─────────────────────────

        int FirstEarPart()
        {
            for (int i = 0; i < m_Parts.Count; i++) if (m_Parts[i].kind == 0) return i;
            return -1;
        }

        Ear FitFor(int part, int side)
        {
            foreach (var e in m_Fits) if (e.partIndex == part && e.side == side && e.ok) return e;
            return null;
        }

        /// <summary>Works out each part's base, direction, width direction, length and profile from its selected vertices (on the original mesh).</summary>
        void RefitAll()
        {
            m_Fits.Clear();
            if (m_Root == null) return;
            var rt = m_Root.transform;
            int pc = m_Parts.Count;
            var pts = new List<Vector3>[pc, 2];
            for (int i = 0; i < pc; i++) { pts[i, 0] = new List<Vector3>(); pts[i, 1] = new List<Vector3>(); }
            foreach (var t in m_Sess)
                for (int g = 0; g < t.groupCount; g++)
                {
                    int p = t.partOf[g];
                    if (p < 0 || p >= pc) continue;
                    Vector3 w = t.gOrigWorld[g];
                    pts[p, rt.InverseTransformPoint(w).x >= 0f ? 0 : 1].Add(w);
                }
            for (int pi = 0; pi < pc; pi++)
            {
                if (m_Parts[pi].kind != 0) continue;
                for (int si = 0; si < 2; si++)
                {
                    var e = FitEar(pts[pi, si], si == 0 ? 1 : -1);
                    if (e != null) { e.partIndex = pi; m_Fits.Add(e); }
                }
            }
            int ep = FirstEarPart();
            for (int pi = 0; pi < pc; pi++)
            {
                if (m_Parts[pi].kind != 1 || ep < 0) continue;
                for (int si = 0; si < 2; si++)
                {
                    int side = si == 0 ? 1 : -1;
                    var parent = FitFor(ep, side);
                    if (parent == null) continue;
                    var f = FitFluff(pts[pi, si], side, parent);
                    if (f != null) { f.partIndex = pi; m_Fits.Add(f); }
                }
            }
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
            FillProfile(pts, e.B, a, w, nn, L0, 0f, 1f, e);
            e.ok = true;
            return e;
        }

        /// <summary>Fits the inner fluff in the ear's own coordinates: where along the ear it sits and where its centre line runs.</summary>
        Ear FitFluff(List<Vector3> pts, int side, Ear parent)
        {
            if (pts.Count < 4) return null;
            int n = pts.Count;
            var us = new float[n];
            for (int i = 0; i < n; i++) us[i] = Vector3.Dot(pts[i] - parent.B, parent.a) / parent.L0;
            var sorted = (float[])us.Clone();
            Array.Sort(sorted);
            float u0 = sorted[Mathf.Clamp((int)(n * 0.01f), 0, n - 1)];
            float u1 = sorted[Mathf.Clamp((int)(n * 0.99f), 0, n - 1)];
            if (u1 - u0 < 0.02f) u1 = u0 + 0.02f;
            var f = new Ear { side = side, parent = parent, B = parent.B, a = parent.a, w = parent.w, n = parent.n, L0 = parent.L0, u0 = u0, u1 = u1 };
            f.LF = parent.L0 * (u1 - u0);
            FillProfile(pts, parent.B, parent.a, parent.w, parent.n, parent.L0, u0, u1 - u0, f);
            f.ok = true;
            return f;
        }

        static void FillProfile(List<Vector3> pts, Vector3 B, Vector3 a, Vector3 w, Vector3 nn, float L0, float uStart, float uSpan, Ear e)
        {
            var minX = new float[PB]; var maxX = new float[PB]; var minY = new float[PB]; var maxY = new float[PB];
            var has = new bool[PB];
            for (int k = 0; k < PB; k++) { minX[k] = float.MaxValue; maxX[k] = float.MinValue; minY[k] = float.MaxValue; maxY[k] = float.MinValue; }
            foreach (var p in pts)
            {
                Vector3 d = p - B;
                float u = (Vector3.Dot(d, a) / L0 - uStart) / Mathf.Max(uSpan, 1e-5f);
                int bi = Mathf.Clamp(Mathf.RoundToInt(u * (PB - 1)), 0, PB - 1);
                float x = Vector3.Dot(d, w), y = Vector3.Dot(d, nn);
                has[bi] = true;
                if (x < minX[bi]) minX[bi] = x;
                if (x > maxX[bi]) maxX[bi] = x;
                if (y < minY[bi]) minY[bi] = y;
                if (y > maxY[bi]) maxY[bi] = y;
            }
            var hw = new float[PB]; var cxa = new float[PB]; var cya = new float[PB];
            for (int k = 0; k < PB; k++)
            {
                if (!has[k]) continue;
                hw[k] = (maxX[k] - minX[k]) * 0.5f;
                cxa[k] = (maxX[k] + minX[k]) * 0.5f;
                cya[k] = (maxY[k] + minY[k]) * 0.5f;
            }
            FillGaps(hw, has); FillGaps(cxa, has); FillGaps(cya, has);
            Smooth(hw); Smooth(cxa); Smooth(cya);
            float mx = 1e-5f;
            for (int k = 0; k < PB; k++) mx = Mathf.Max(mx, hw[k]);
            e.halfW = hw; e.cx = cxa; e.cy = cya; e.maxHalfW = mx;
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

        // ───────────────────────── deformation ─────────────────────────

        static float H(EarPart P, float s)
        {
            float bl = Mathf.Max(P.baseLock, 0.01f);
            return s * (1f + (P.length - 1f) * S(s / bl));
        }

        /// <summary>Builds the bent centerline of an ear for the current settings.</summary>
        void PrepareEar(Ear e, EarPart P)
        {
            var rt = m_Root.transform;
            Quaternion qo = Quaternion.AngleAxis(-e.side * P.outDeg, rt.forward);
            Quaternion qf = Quaternion.AngleAxis(P.fwdDeg, rt.right);
            Quaternion Q = qf * qo;
            e.a2 = Q * e.a; e.w2 = Q * e.w; e.n2 = Q * e.n;
            e.nbSign = Vector3.Dot(e.n2, rt.forward) >= 0f ? 1f : -1f;
            e.nb = e.n2 * e.nbSign;
            float bend = P.bendDeg * Mathf.Deg2Rad;
            e.lut[0] = Vector3.zero;
            e.htab[0] = 0f;
            for (int k = 0; k < LutK; k++)
            {
                float s0 = (float)k / LutK, s1 = (float)(k + 1) / LutK, sm = (s0 + s1) * 0.5f;
                float phi = bend * sm;
                Vector3 T = Mathf.Cos(phi) * e.a2 + Mathf.Sin(phi) * e.nb;
                float h1 = H(P, s1);
                e.lut[k + 1] = e.lut[k] + T * (e.L0 * (h1 - H(P, s0)));
                e.htab[k + 1] = h1;
            }
            e.slope1 = Mathf.Max(0.05f, (H(P, 1f) - H(P, 1f - 1f / LutK)) * LutK);
        }

        /// <summary>Inverse of H: the original ear position (0..1) that ends up at a given stretched distance along the ear.</summary>
        static float HInv(Ear e, float hv)
        {
            if (hv <= 0f) return hv;
            if (hv >= e.htab[LutK]) return 1f + (hv - e.htab[LutK]) / e.slope1;
            int lo = 0, hi = LutK;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (e.htab[mid] <= hv) lo = mid; else hi = mid;
            }
            float f = (hv - e.htab[lo]) / Mathf.Max(e.htab[lo + 1] - e.htab[lo], 1e-8f);
            return (lo + f) / LutK;
        }

        static void EarFrame(Ear e, EarPart P, float u, out Vector3 C, out Vector3 Wx, out Vector3 Ny)
        {
            float uf = Mathf.Clamp01(u);
            float phi = P.bendDeg * Mathf.Deg2Rad * uf;
            float tau = e.side * P.twistDeg * Mathf.Deg2Rad * uf;   // opposite directions on the two ears so they stay mirrored
            Vector3 T = Mathf.Cos(phi) * e.a2 + Mathf.Sin(phi) * e.nb;
            Vector3 Nn = (-Mathf.Sin(phi) * e.a2 + Mathf.Cos(phi) * e.nb) * e.nbSign;
            Wx = Mathf.Cos(tau) * e.w2 + Mathf.Sin(tau) * Nn;
            Ny = -Mathf.Sin(tau) * e.w2 + Mathf.Cos(tau) * Nn;
            if (u <= 0f) C = e.a2 * (e.L0 * u);
            else if (u >= 1f) C = e.lut[LutK] + T * (e.L0 * e.slope1 * (u - 1f));
            else
            {
                float pos = u * LutK;
                int i = Mathf.Min((int)pos, LutK - 1);
                C = Vector3.Lerp(e.lut[i], e.lut[i + 1], pos - i);
            }
        }

        static float WidthScale(Ear e, EarPart P, float u)
        {
            float hw = Samp(e.halfW, u);
            float us = Mathf.Clamp(P.tipRound, 0.05f, 0.95f);
            float prof = u <= us ? 1f : Mathf.Sqrt(Mathf.Max(0.02f, 1f - Mathf.Pow((Mathf.Clamp01(u) - us) / (1f - us), 2f)));
            float target = e.maxHalfW * prof;
            float ratio = Mathf.Clamp(target / Mathf.Max(hw, 0.12f * e.maxHalfW), 0.3f, 8f);
            return P.width * Mathf.Lerp(1f, ratio, P.bunnify * S(u / 0.25f));
        }

        static Vector3 Deform(Ear e, EarPart P, Vector3 p)
        {
            Vector3 d = p - e.B;
            float x = Vector3.Dot(d, e.w), y = Vector3.Dot(d, e.n), t = Vector3.Dot(d, e.a);
            float u = t / e.L0;
            Vector3 C, Wx, Ny;
            EarFrame(e, P, u, out C, out Wx, out Ny);
            float cxu = Samp(e.cx, u), cyu = Samp(e.cy, u);
            float sw = WidthScale(e, P, u);
            float xr = x - cxu;
            float x2 = cxu + xr * sw;
            float y2 = cyu + (y - cyu) * P.thick + P.cup * xr * xr * sw * sw / Mathf.Max(e.maxHalfW, 1e-5f) * 0.5f;
            return e.B + C + Wx * x2 + Ny * y2;
        }

        /// <summary>The inner fluff rides on the ear (so it bends and tilts with it) but has its own range, stretch, width, thickness and inset.</summary>
        static Vector3 FluffDeform(Ear f, EarPart fp, EarPart pp, Vector3 p)
        {
            Ear pe = f.parent;
            Vector3 d = p - pe.B;
            float x = Vector3.Dot(d, pe.w), y = Vector3.Dot(d, pe.n), t = Vector3.Dot(d, pe.a);
            float uo = t / pe.L0;
            float span = Mathf.Max(f.u1 - f.u0, 1e-4f);
            float uf = (uo - f.u0) / span;
            float ufc = Mathf.Clamp01(uf);
            float cxF = Samp(f.cx, ufc), cyF = Samp(f.cy, ufc);
            float uc = Mathf.Lerp(f.u0, f.u1, ufc);
            float dxc = cxF - Samp(pe.cx, uc), dyc = cyF - Samp(pe.cy, uc);
            float earLen = pe.L0 * pp.length;
            float A0, Lprop;
            if (fp.fluffAuto) { A0 = pe.L0 * H(pp, f.u0); Lprop = pe.L0 * (H(pp, f.u1) - H(pp, f.u0)); }
            else { A0 = fp.fStart * earLen; Lprop = Mathf.Max(0.01f, fp.fEnd - fp.fStart) * earLen; }
            float Lnew = Mathf.Lerp(f.LF, Lprop, fp.fStretch);
            float un = HInv(pe, (A0 + uf * Lnew) / pe.L0);
            Vector3 C, Wx, Ny;
            EarFrame(pe, pp, un, out C, out Wx, out Ny);
            float swE = WidthScale(pe, pp, un);
            float follow = Mathf.Lerp(1f, swE, fp.fFollow);
            float thickFollow = Mathf.Lerp(1f, pp.thick, fp.fFollow);
            float x2c = Samp(pe.cx, un), y2c = Samp(pe.cy, un);
            float x2 = x2c + dxc * follow + (x - cxF) * fp.fWidth * follow;
            float y2 = y2c + dyc * thickFollow + (y - cyF) * fp.fThick * thickFollow;
            y2 -= Mathf.Sign(dyc) * fp.fInsetCm * 0.01f;
            y2 += pp.cup * (x2 - x2c) * (x2 - x2c) / Mathf.Max(pe.maxHalfW, 1e-5f) * 0.5f;
            return pe.B + C + Wx * x2 + Ny * y2;
        }

        /// <summary>Rebuilds the shown mesh: original, then every part's settings, then the hand-sculpt layer.
        /// In the Select tab the original shape is shown so picking is precise.</summary>
        void Recompute(bool forceShow)
        {
            if (!m_Active || m_Root == null) return;
            bool show = forceShow || m_Mode != Mode.Select;
            var rt = m_Root.transform;
            if (show)
            {
                foreach (var e in m_Fits)
                    if (m_Parts[e.partIndex].kind == 0) PrepareEar(e, m_Parts[e.partIndex]);
            }
            foreach (var t in m_Sess)
            {
                Array.Copy(t.posOrig, t.posParam, t.n);
                Array.Copy(t.nrmOrig, t.nrmWork, t.n);
                if (t.HasTan) Array.Copy(t.tanOrig, t.tanWork, t.n);
                Array.Copy(t.gNsumOrig, t.gNsum, t.groupCount);
                if (show)
                {
                    for (int g = 0; g < t.groupCount; g++)
                    {
                        float m = t.mask[g];
                        int mp = t.maskPart[g];
                        if (m <= 0f || mp < 0 || mp >= m_Parts.Count) continue;
                        var part = m_Parts[mp];
                        for (int k = t.gVertStart[g]; k < t.gVertStart[g + 1]; k++)
                        {
                            int v = t.gVerts[k];
                            if (!t.skinOk[v]) continue;
                            Vector3 w0 = t.skin[v].MultiplyPoint3x4(t.posOrig[v]);
                            var fit = FitFor(mp, rt.InverseTransformPoint(w0).x >= 0f ? 1 : -1);
                            if (fit == null) continue;
                            Vector3 w1;
                            if (part.kind == 0) w1 = Deform(fit, part, w0);
                            else
                            {
                                if (fit.parent == null) continue;
                                w1 = FluffDeform(fit, part, m_Parts[fit.parent.partIndex], w0);
                            }
                            t.posParam[v] = t.skinInv[v].MultiplyPoint3x4(Vector3.Lerp(w0, w1, m));
                        }
                    }
                    for (int v = 0; v < t.n; v++) t.posWork[v] = t.posParam[v] + t.sculpt[v];
                    for (int g = 0; g < t.groupCount; g++)
                    {
                        bool touch = t.mask[g] > 0f;
                        if (!touch)
                            for (int k = t.gVertStart[g]; k < t.gVertStart[g + 1]; k++)
                                if (t.sculpt[t.gVerts[k]].sqrMagnitude > 0f) { touch = true; break; }
                        if (!touch) continue;
                        Vector3 nw = GroupNormalSum(t, t.posWork, g);
                        Vector3 old = t.gNsumOrig[g];
                        t.gNsum[g] = nw;
                        if (old.sqrMagnitude < 1e-16f || nw.sqrMagnitude < 1e-16f) continue;
                        Quaternion q = Quaternion.FromToRotation(old.normalized, nw.normalized);
                        for (int k = t.gVertStart[g]; k < t.gVertStart[g + 1]; k++)
                        {
                            int v = t.gVerts[k];
                            t.nrmWork[v] = (q * t.nrmOrig[v]).normalized;
                            if (t.HasTan)
                            {
                                Vector4 tv = t.tanOrig[v];
                                Vector3 r = q * new Vector3(tv.x, tv.y, tv.z);
                                t.tanWork[v] = new Vector4(r.x, r.y, r.z, tv.w);
                            }
                        }
                    }
                }
                else Array.Copy(t.posOrig, t.posWork, t.n);
                RefreshWorld(t);
                Upload(t);
            }
            m_OverlayDirty = true;
        }

        void SetMode(Mode m)
        {
            if (m == m_Mode) return;
            if (m_Mode == Mode.Select) RefitAll();
            m_Mode = m;
            m_Stroking = false;
            m_HoverT = null;
            Recompute(false);
            SceneView.RepaintAll();
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

        bool RaycastAll(Ray ray, out Vector3 hit, out Vector3 normal, out ShapeTarget ht, out int htri)
        {
            hit = Vector3.zero; normal = Vector3.up; ht = null; htri = -1;
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
                        ht = t; htri = i / 3;
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
            int ap = m_ActivePart;
            foreach (var t in m_Sess)
            {
                bool changed = false;
                for (int g = 0; g < t.groupCount; g++)
                {
                    foreach (var cc in centers)
                        if ((t.gWorld[g] - cc).sqrMagnitude <= r * r)
                        {
                            if (!m_StrokeRemove) { if (t.partOf[g] != ap) { t.partOf[g] = ap; changed = true; } }
                            else if (t.partOf[g] == ap) { t.partOf[g] = -1; changed = true; }
                            break;
                        }
                }
                if (changed) RebuildMask(t);
            }
        }

        int[] IslandArray(ShapeTarget t) { return m_LinkMode == LinkMode.UVIsland ? t.islandUV : t.islandMesh; }

        /// <summary>Blender-style L: puts the whole connected piece under the cursor into the active part (or takes it out).</summary>
        void LinkedAtHover(bool add)
        {
            if (m_HoverT == null || m_HoverTri < 0) return;
            PushUndo();
            LinkIsland(m_HoverT, IslandArray(m_HoverT)[m_HoverT.tris[m_HoverTri * 3]], add);
            if (m_Symmetry)
            {
                var t = m_HoverT;
                Vector3 mp = MirrorPoint(m_Hover);
                int bestG = -1; float bestD = 0.05f * 0.05f;
                for (int g = 0; g < t.groupCount; g++)
                {
                    float d = (t.gWorld[g] - mp).sqrMagnitude;
                    if (d < bestD) { bestD = d; bestG = g; }
                }
                if (bestG >= 0)
                {
                    int isl = IslandArray(t)[t.groupRep[bestG]];
                    if (isl != IslandArray(t)[t.tris[m_HoverTri * 3]]) LinkIsland(t, isl, add);
                }
            }
            RebuildAllMasks();
            m_Status = null;
            Repaint();
        }

        void LinkIsland(ShapeTarget t, int island, bool add)
        {
            var arr = IslandArray(t);
            for (int v = 0; v < t.n; v++)
            {
                if (arr[v] != island) continue;
                int g = t.group[v];
                if (add) t.partOf[g] = m_ActivePart;
                else if (t.partOf[g] == m_ActivePart) t.partOf[g] = -1;
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
            foreach (var t in m_Sess) foreach (int p in t.partOf) if (p >= 0) return true;
            return false;
        }

        void BeginStroke(SceneView sv, Event e)
        {
            m_Stroking = false;
            if (m_Mode == Mode.Select)
            {
                if (!m_HasHover) return;
                if (m_SelTool == SelTool.Linked) { LinkedAtHover(!(e.control || e.command)); return; }
                m_StrokeRemove = e.control || e.command;
                m_Stroking = true;
                PushUndo();
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
                var deltas = new Vector3[entries.Count];
                if (m_Tool == SculptTool.Smooth)
                {
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
                }
                else // Inflate (hold Shift to deflate)
                {
                    float sign = e.shift ? -1f : 1f;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var en = entries[i]; var t = en.t;
                        Vector3 dir = t.skin[t.groupRep[en.g]].MultiplyVector(t.gNsum[en.g]);
                        if (dir.sqrMagnitude < 1e-16f) continue;
                        deltas[i] = dir.normalized * (sign * m_Strength * Radius * 0.08f * en.w);
                    }
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
            foreach (var kv in touched)
            {
                var affected = new List<int>(kv.Value);
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
                // what the hand sculpting added on top of the parts' settings
                foreach (var t in m_Sess)
                {
                    for (int v = 0; v < t.n; v++)
                    {
                        Vector3 d = t.posWork[v] - t.posParam[v];
                        t.sculpt[v] = d.sqrMagnitude < 1e-16f ? Vector3.zero : d;
                    }
                    RefreshWorld(t);
                }
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
                    int ga = t.group[a], gb = t.group[b], gc = t.group[c];
                    float m = t.mask[ga]; int mp = t.maskPart[ga];
                    if (t.mask[gb] > m) { m = t.mask[gb]; mp = t.maskPart[gb]; }
                    if (t.mask[gc] > m) { m = t.mask[gc]; mp = t.maskPart[gc]; }
                    if (m <= 0f || mp < 0 || mp >= m_Parts.Count) continue;
                    bool full = t.partOf[ga] == mp || t.partOf[gb] == mp || t.partOf[gc] == mp;
                    Color pc = m_Parts[mp].color;
                    Color col = full ? new Color(pc.r, pc.g, pc.b, mp == m_ActivePart ? 0.6f : 0.4f) : new Color(pc.r, pc.g, pc.b, 0.1f + 0.25f * m);
                    int[] idx = { a, b, c };
                    foreach (int v in idx)
                    {
                        Vector3 nrm = t.skin[v].MultiplyVector(t.nrmWork[v]).normalized;
                        verts.Add(t.world[v] + nrm * 0.0006f);
                        cols.Add(col);
                    }
                }
                if (t.overlay == null)
                    t.overlay = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
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

        void RebuildHoverMesh()
        {
            if (m_HoverMesh == null) m_HoverMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
            m_HoverMesh.Clear();
            if (m_HoverT == null || m_HoverIsland < 0) return;
            var t = m_HoverT;
            var arr = IslandArray(t);
            var verts = new List<Vector3>();
            var cols = new List<Color>();
            Color col = new Color(1f, 1f, 1f, 0.35f);
            for (int i = 0; i + 2 < t.tris.Length; i += 3)
            {
                if (arr[t.tris[i]] != m_HoverIsland) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = t.tris[i + k];
                    Vector3 nrm = t.skin[v].MultiplyVector(t.nrmWork[v]).normalized;
                    verts.Add(t.world[v] + nrm * 0.0009f);
                    cols.Add(col);
                }
            }
            if (verts.Count == 0) return;
            var tris = new int[verts.Count];
            for (int i = 0; i < tris.Length; i++) tris[i] = i;
            m_HoverMesh.SetVertices(verts);
            m_HoverMesh.SetColors(cols);
            m_HoverMesh.SetTriangles(tris, 0);
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
                {
                    foreach (var t in m_Sess)
                        if (t.overlay != null && t.overlay.vertexCount > 0)
                            Graphics.DrawMesh(t.overlay, Matrix4x4.identity, m_OverlayMat, 0, sv.camera);
                    if (m_Mode == Mode.Select && m_SelTool == SelTool.Linked && m_HoverMesh != null && m_HoverMesh.vertexCount > 0)
                        Graphics.DrawMesh(m_HoverMesh, Matrix4x4.identity, m_OverlayMat, 0, sv.camera);
                }
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.L && m_Mode == Mode.Select && !e.control && !e.command && !e.alt)
            {
                LinkedAtHover(!e.shift);
                e.Use();
                return;
            }

            if (m_Mode == Mode.Shape)
            {
                if (e.type == EventType.MouseDown) m_PendingSnap = TakeSnap();
                if (e.type == EventType.MouseUp) m_PendingSnap = null;
                ShapeHandles();
                return;
            }

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
                    if (m_HasHover && !(m_Mode == Mode.Select && m_SelTool == SelTool.Linked))
                    {
                        Color pc = m_Mode == Mode.Select ? Part.color : new Color(0.4f, 0.9f, 1f, 1f);
                        Handles.color = pc;
                        Handles.DrawWireDisc(m_Hover, m_HoverN, Radius);
                        if (m_Symmetry)
                        {
                            Handles.color = new Color(pc.r, pc.g, pc.b, 0.45f);
                            Handles.DrawWireDisc(MirrorPoint(m_Hover), MirrorVector(m_HoverN), Radius);
                        }
                    }
                    break;
            }
        }

        void UpdateHover(Event e)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            ShapeTarget ht; int tri;
            m_HasHover = RaycastAll(ray, out m_Hover, out m_HoverN, out ht, out tri);
            m_HoverT = m_HasHover ? ht : null;
            m_HoverTri = m_HasHover ? tri : -1;
            if (m_Mode == Mode.Select && m_SelTool == SelTool.Linked)
            {
                int isl = m_HasHover ? IslandArray(ht)[ht.tris[tri * 3]] : -1;
                if (isl != m_HoverIsland || m_HoverT == null) { m_HoverIsland = isl; RebuildHoverMesh(); }
            }
        }

        void ShapeHandles()
        {
            bool changed = false;
            foreach (var e in m_Fits)
            {
                var P = m_Parts[e.partIndex];
                if (P.kind != 0) continue;
                Vector3 tip = e.B + e.lut[LutK];
                float bend = P.bendDeg * Mathf.Deg2Rad;
                Vector3 T = (Mathf.Cos(bend) * e.a2 + Mathf.Sin(bend) * e.nb).normalized;
                Handles.color = P.color;
                var pts = new Vector3[LutK + 1];
                for (int i = 0; i <= LutK; i++) pts[i] = e.B + e.lut[i];
                Handles.DrawPolyLine(pts);
                Handles.SphereHandleCap(0, e.B, Quaternion.identity, HandleUtility.GetHandleSize(e.B) * 0.06f, EventType.Repaint);
                float hs = HandleUtility.GetHandleSize(tip) * 0.15f;
                EditorGUI.BeginChangeCheck();
                Vector3 nt = Handles.Slider(tip, T, hs, Handles.ConeHandleCap, 0f);
                if (EditorGUI.EndChangeCheck())
                {
                    if (m_PendingSnap != null) { PushSnap(m_PendingSnap); m_PendingSnap = null; }
                    float dl = Vector3.Dot(nt - tip, T);
                    P.length = Mathf.Clamp((e.L0 * P.length + dl) / e.L0, 0.3f, 12f);
                    changed = true;
                }
            }
            if (changed) { Recompute(false); Repaint(); }
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
            Recompute(true);   // make sure the arrays hold the full shaped result, whatever tab we're on
            EnsureFolder(Folder);
            string shape = string.IsNullOrWhiteSpace(m_ShapeName) ? "BunnyEars" : m_ShapeName.Trim();
            var settings = m_Loaded != null ? m_Loaded : CreateInstance<EarShaperSettings>();
            var newTargets = new List<EarShaperTargetData>();
            int count = 0;
            foreach (var t in m_Sess)
            {
                bool any = false;
                for (int i = 0; i < t.n; i++)
                    if ((t.posWork[i] - t.posOrig[i]).sqrMagnitude > 1e-14f) { any = true; break; }

                // put the renderer back on a real mesh before recording the change (it is showing the preview right now)
                SetMesh(t, t.restore);
                RestoreWeights(t);
                if (!any)
                {
                    if (t.restore != t.orig)
                    {
                        Undo.RecordObject(t.renderer, "Apply ear shape");
                        SetMesh(t, t.orig);
                        RestoreWeights(t);
                        EditorUtility.SetDirty(t.renderer);
                    }
                    continue;
                }

                bool blend = m_Output == OutputKind.BlendShape && t.smr != null;
                Mesh m = Instantiate(t.orig);
                m.name = t.orig.name + (blend ? "_EarShape" : "_EarShaped");
                if (blend)
                {
                    var dv = new Vector3[t.n]; var dn = new Vector3[t.n]; var dt = new Vector3[t.n];
                    for (int i = 0; i < t.n; i++)
                    {
                        dv[i] = t.posWork[i] - t.posOrig[i];
                        dn[i] = t.nrmWork[i] - t.nrmOrig[i];
                        if (t.HasTan)
                            dt[i] = new Vector3(t.tanWork[i].x - t.tanOrig[i].x, t.tanWork[i].y - t.tanOrig[i].y, t.tanWork[i].z - t.tanOrig[i].z);
                    }
                    AddBlendShape(m, shape, dv, dn, dt);
                }
                else
                {
                    m.vertices = t.posWork;
                    m.normals = t.nrmWork;
                    if (t.HasTan) m.tangents = t.tanWork;
                }
                // make the bounds cover the longer ears so they don't get culled
                Bounds b = m.bounds;
                for (int i = 0; i < t.n; i++)
                    if ((t.posWork[i] - t.posOrig[i]).sqrMagnitude > 1e-14f) b.Encapsulate(t.posWork[i]);
                m.bounds = b;

                // reopening: overwrite the earlier result in place so everything that points at it keeps working
                Mesh existing = t.loaded != null ? t.loaded.result : null;
                if (existing != null)
                {
                    EditorUtility.CopySerialized(m, existing);
                    DestroyImmediate(m);
                    m = existing;
                    EditorUtility.SetDirty(m);
                }
                else
                {
                    string path = AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + APUtil.SafeFileName(m.name) + ".asset");
                    AssetDatabase.CreateAsset(m, path);
                }

                Undo.RecordObject(t.renderer, "Apply ear shape");
                if (t.filter != null && t.smr == null) Undo.RecordObject(t.filter, "Apply ear shape");
                SetMesh(t, m);
                RestoreWeights(t);
                if (blend && m_DefaultOn && t.smr != null)
                {
                    int idx = m.GetBlendShapeIndex(shape);
                    if (idx >= 0) t.smr.SetBlendShapeWeight(idx, 100f);
                }
                EditorUtility.SetDirty(t.renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(t.renderer);

                var td = new EarShaperTargetData { rendererName = t.renderer.name, original = t.orig, result = m, partOf = new int[t.n] };
                for (int v = 0; v < t.n; v++) td.partOf[v] = t.partOf[t.group[v]];
                var idxs = new List<int>(); var vals = new List<Vector3>();
                for (int v = 0; v < t.n; v++) if (t.sculpt[v].sqrMagnitude > 0f) { idxs.Add(v); vals.Add(t.sculpt[v]); }
                td.sculptIdx = idxs.ToArray(); td.sculptVal = vals.ToArray();
                newTargets.Add(td);
                count++;
            }

            if (count > 0)
            {
                settings.parts = new List<EarPart>();
                foreach (var p in m_Parts) settings.parts.Add(p.Clone());
                settings.targets = newTargets;
                settings.output = (int)m_Output;
                settings.shapeName = shape;
                settings.defaultOn = m_DefaultOn;
                if (m_Loaded == null)
                {
                    string sp = AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + APUtil.SafeFileName(m_Root != null ? m_Root.name : "Avatar") + "_EarShaperSettings.asset");
                    AssetDatabase.CreateAsset(settings, sp);
                }
                else EditorUtility.SetDirty(settings);
            }
            else if (m_Loaded == null) DestroyImmediate(settings);
            AssetDatabase.SaveAssets();
            EndSession(false);
            m_Status = count == 0
                ? "Nothing was changed, so no mesh was made. Your avatar uses its original mesh."
                : "Done. " + count + " mesh" + (count == 1 ? "" : "es") + " saved in " + Folder + " and put on your avatar. The original meshes were not touched. To keep adjusting later, select the mesh again and press Resume. Ctrl+Z undoes the swap.";
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
            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < m_Pending.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                m_Pending[i] = (Renderer)EditorGUILayout.ObjectField(m_Pending[i], typeof(Renderer), true);
                if (GUILayout.Button("×", GUILayout.Width(22))) { m_Pending.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            if (EditorGUI.EndChangeCheck()) RefreshResume();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add selected"))
            {
                foreach (var go in Selection.gameObjects) { AddPending(go); if (m_Root == null) SetFromObject(go); }
                RefreshResume();
            }
            if (GUILayout.Button("Clear")) { m_Pending.Clear(); RefreshResume(); }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Select the body/head mesh in the Hierarchy and press Add selected. If the ear fluff is a separate mesh (hair, ears), add that too.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6);

            if (m_Resume != null)
            {
                EditorGUILayout.HelpBox("These ears were shaped before. Resume picks up exactly where you left off: the same parts, selections, sliders and sculpting, all still adjustable.", MessageType.Info);
                m_StartOver = EditorGUILayout.ToggleLeft("Start over instead (keeps using the same saved mesh)", m_StartOver);
            }
            using (new EditorGUI.DisabledScope(m_Root == null || m_Pending.Count == 0))
                if (GUILayout.Button(m_Resume != null && !m_StartOver ? "Resume shaping" : "Start shaping", GUILayout.Height(30))) StartSession();

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
            string[] names = { "1  Select", "2  Shape", "3  Sculpt" };
            int mi = GUILayout.Toolbar((int)m_Mode, names, GUILayout.Height(26));
            if (mi != (int)m_Mode) SetMode((Mode)mi);
            EditorGUILayout.Space(4);

            DrawParts();
            EditorGUILayout.Space(4);

            if (m_Mode == Mode.Select) DrawSelectPanel();
            else if (m_Mode == Mode.Shape) DrawShapePanel();
            else DrawSculptPanel();

            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(m_Undo.Count == 0)) if (GUILayout.Button("Undo (Ctrl+Z)")) Undo.PerformUndo();
            using (new EditorGUI.DisabledScope(m_Redo.Count == 0)) if (GUILayout.Button("Redo")) Undo.PerformRedo();
            if (GUILayout.Button("Reset all shaping")) { if (EditorUtility.DisplayDialog("Reset shaping", "Reset every part's settings and remove all sculpting? Your selections stay.", "Reset", "Cancel")) ResetAllShape(); }
            if (GUILayout.Button("Refresh pose")) { foreach (var t in m_Sess) { ComputeSkin(t); RefreshWorld(t); RefreshOrigWorld(t); } RebuildAllMasks(); RefitAll(); Recompute(false); }
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
            if (GUILayout.Button(m_Loaded != null ? "Apply changes to avatar" : "Apply to avatar", GUILayout.Height(28))) { ApplyToAvatar(); GUIUtility.ExitGUI(); }
            if (GUILayout.Button("Cancel", GUILayout.Height(28), GUILayout.Width(80)))
            {
                if (EditorUtility.DisplayDialog("Cancel", "Discard this session and put the avatar's meshes back as they were?", "Discard", "Keep working")) { EndSession(true); GUIUtility.ExitGUI(); }
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawParts()
        {
            EditorGUILayout.LabelField("Parts", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Each part has its own selection and settings, e.g. the ear and its inner fluff. The dot picks the part you're editing.", EditorStyles.wordWrappedMiniLabel);
            int remove = -1;
            for (int i = 0; i < m_Parts.Count; i++)
            {
                var p = m_Parts[i];
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Toggle(m_ActivePart == i, GUIContent.none, EditorStyles.radioButton, GUILayout.Width(18)) && m_ActivePart != i) { m_ActivePart = i; m_OverlayDirty = true; SceneView.RepaintAll(); }
                EditorGUI.BeginChangeCheck();
                p.color = EditorGUILayout.ColorField(GUIContent.none, p.color, false, false, false, GUILayout.Width(38));
                p.name = EditorGUILayout.TextField(p.name);
                int nk = EditorGUILayout.Popup(p.kind, new[] { "Ear", "Inner fluff" }, GUILayout.Width(88));
                if (EditorGUI.EndChangeCheck())
                {
                    m_OverlayDirty = true;
                    if (nk != p.kind) { PushUndo(); p.kind = nk; RefitAll(); Recompute(false); }
                    SceneView.RepaintAll();
                }
                using (new EditorGUI.DisabledScope(m_Parts.Count <= 1))
                    if (GUILayout.Button("×", GUILayout.Width(22))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0) RemovePart(remove);
            if (m_Parts.Count < 6 && GUILayout.Button("Add part"))
            {
                PushUndo();
                var cols = new[] { new Color(0.6f, 0.45f, 1f), new Color(1f, 0.35f, 0.5f), new Color(0.2f, 0.8f, 0.95f), new Color(0.95f, 0.85f, 0.2f) };
                var np = new EarPart { name = "Part " + (m_Parts.Count + 1), kind = 1, color = cols[m_Parts.Count % cols.Length], featherCm = 0.5f };
                m_Parts.Add(np);
                m_ActivePart = m_Parts.Count - 1;
                RebuildAllMasks();
            }
        }

        void RemovePart(int idx)
        {
            PushUndo();
            m_Parts.RemoveAt(idx);
            foreach (var t in m_Sess)
                for (int g = 0; g < t.groupCount; g++)
                {
                    if (t.partOf[g] == idx) t.partOf[g] = -1;
                    else if (t.partOf[g] > idx) t.partOf[g]--;
                }
            m_ActivePart = Mathf.Clamp(m_ActivePart > idx ? m_ActivePart - 1 : m_ActivePart, 0, m_Parts.Count - 1);
            RebuildAllMasks();
            RefitAll();
            Recompute(false);
        }

        void DrawSelectPanel()
        {
            EditorGUILayout.HelpBox("This tab shows the original ears so you can pick precisely. Everything you select goes into the part chosen above. Orange (or that part's colour) is selected; the lighter fringe is its soft blend into the surroundings. Ctrl removes.", MessageType.None);
            m_SelTool = (SelTool)GUILayout.Toolbar((int)m_SelTool, new[] { "Paint (brush)", "Linked (L)" }, GUILayout.Height(24));
            if (m_SelTool == SelTool.Brush)
            {
                m_BrushCm = EditorGUILayout.Slider("Brush size (cm)", m_BrushCm, 0.3f, 20f);
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                m_LinkMode = (LinkMode)EditorGUILayout.EnumPopup(new GUIContent("Link by", "UV island: the connected piece of the UV unwrap, like the painter's Select tool. Mesh part: connected geometry, ignoring UVs."), m_LinkMode);
                if (EditorGUI.EndChangeCheck()) { m_HoverIsland = -1; if (m_HoverMesh != null) m_HoverMesh.Clear(); }
                EditorGUILayout.LabelField("Click a piece to add the whole connected piece to the part (white shows what you'd get). Ctrl+click removes. The L key does the same in either tool: L adds, Shift+L removes.", EditorStyles.wordWrappedMiniLabel);
            }
            m_Symmetry = EditorGUILayout.Toggle("Symmetry (left/right)", m_Symmetry);
            var part = Part;
            BeginParamEdit();
            EditorGUI.BeginChangeCheck();
            part.featherCm = EditorGUILayout.Slider(new GUIContent("Blend into surroundings (cm)", "How far this part's change fades out past its selection, along the surface."), part.featherCm, 0f, 8f);
            bool featherChanged = EditorGUI.EndChangeCheck();
            if (featherChanged) { CommitPending(); RebuildAllMasks(); }
            if (Event.current.type == EventType.MouseUp || Event.current.type == EventType.KeyUp) m_PendingSnap = null;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Select by ear bones", EditorStyles.boldLabel);
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
            if (GUILayout.Button("Grow")) { PushUndo(); Grow(); }
            if (GUILayout.Button("Shrink")) { PushUndo(); Shrink(); }
            if (GUILayout.Button("Clear this part")) { PushUndo(); foreach (var t in m_Sess) for (int g = 0; g < t.groupCount; g++) if (t.partOf[g] == m_ActivePart) t.partOf[g] = -1; RebuildAllMasks(); }
            EditorGUILayout.EndHorizontal();
            var counts = new int[m_Parts.Count];
            foreach (var t in m_Sess) foreach (int p in t.partOf) if (p >= 0 && p < counts.Length) counts[p]++;
            string sum = "";
            for (int i = 0; i < m_Parts.Count; i++) sum += (i > 0 ? "   " : "") + m_Parts[i].name + ": " + counts[i];
            EditorGUILayout.LabelField(sum + " points", EditorStyles.miniLabel);
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
            PushUndo();
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
                    if (w >= m_BoneThreshold && t.partOf[t.group[v]] != m_ActivePart) { t.partOf[t.group[v]] = m_ActivePart; picked++; }
                }
            }
            RebuildAllMasks();
            m_Status = picked + " points added to \"" + Part.name + "\" from the bone weights.";
        }

        void Grow()
        {
            foreach (var t in m_Sess)
            {
                var add = new List<int>();
                for (int g = 0; g < t.groupCount; g++)
                {
                    if (t.partOf[g] >= 0) continue;
                    foreach (int nb in t.nbr[g]) if (t.partOf[nb] == m_ActivePart) { add.Add(g); break; }
                }
                foreach (int g in add) t.partOf[g] = m_ActivePart;
            }
            RebuildAllMasks();
        }

        void Shrink()
        {
            foreach (var t in m_Sess)
            {
                var rem = new List<int>();
                for (int g = 0; g < t.groupCount; g++)
                {
                    if (t.partOf[g] != m_ActivePart) continue;
                    foreach (int nb in t.nbr[g]) if (t.partOf[nb] != m_ActivePart) { rem.Add(g); break; }
                }
                foreach (int g in rem) t.partOf[g] = -1;
            }
            RebuildAllMasks();
        }

        void DrawShapePanel()
        {
            var P = Part;
            bool hasPoints = false;
            foreach (var t in m_Sess) foreach (int p in t.partOf) if (p == m_ActivePart) { hasPoints = true; break; }
            if (!hasPoints)
            {
                EditorGUILayout.HelpBox("\"" + P.name + "\" has nothing selected yet. Go to the Select tab and select it first.", MessageType.Warning);
                return;
            }
            if (P.kind == 0) DrawEarSliders(P); else DrawFluffSliders(P);
        }

        // Takes an undo snapshot when an edit starts (a click, or a key typed into a field), so a whole slider drag is one step.
        void BeginParamEdit()
        {
            var t = Event.current.type;
            if ((t == EventType.MouseDown || t == EventType.KeyDown) && m_PendingSnap == null) m_PendingSnap = TakeSnap();
        }

        void CommitPending()
        {
            if (m_PendingSnap != null) PushSnap(m_PendingSnap);
        }

        void EndParamEdit(bool changed)
        {
            if (changed)
            {
                if (m_PendingSnap != null) CommitPending(); else PushUndo();
                Recompute(false);
                SceneView.RepaintAll();
            }
            var t = Event.current.type;
            if (t == EventType.MouseUp || t == EventType.KeyUp) m_PendingSnap = null;
        }

        void DrawEarSliders(EarPart P)
        {
            if (FitFor(m_ActivePart, 1) == null && FitFor(m_ActivePart, -1) == null)
            {
                EditorGUILayout.HelpBox("Couldn't work out the ear's direction from the selection. Select a few more points on the ear (at least a handful on each ear).", MessageType.Warning);
                if (GUILayout.Button("Look again")) { RefitAll(); Recompute(false); }
                return;
            }
            EditorGUILayout.HelpBox("Drag the arrow at the ear tip in the Scene view to set the length, or use the sliders. Changes are live and fully adjustable. You can come back later.", MessageType.None);
            BeginParamEdit();
            EditorGUI.BeginChangeCheck();
            P.length = EditorGUILayout.Slider(new GUIContent("Length", "1 = the current length."), P.length, 0.3f, 12f);
            P.width = EditorGUILayout.Slider("Width", P.width, 0.3f, 2.5f);
            P.thick = EditorGUILayout.Slider("Thickness", P.thick, 0.3f, 3f);
            P.bunnify = EditorGUILayout.Slider(new GUIContent("Bunny profile", "Turns the pointed cat triangle into a long oval ear with a rounded tip."), P.bunnify, 0f, 1f);
            P.tipRound = EditorGUILayout.Slider(new GUIContent("Where the rounding starts", "Lower = rounder, more spoon-shaped. Higher = straighter sides."), P.tipRound, 0.1f, 0.95f);
            P.baseLock = EditorGUILayout.Slider(new GUIContent("Keep base unstretched", "How much of the ear root keeps its original proportions before the stretch kicks in."), P.baseLock, 0.05f, 0.7f);
            P.cup = EditorGUILayout.Slider(new GUIContent("Curl / cup", "Curves the ear across its width."), P.cup, -1f, 1f);
            P.bendDeg = EditorGUILayout.Slider(new GUIContent("Bend", "Positive bends the ear forward, negative back."), P.bendDeg, -120f, 120f);
            P.twistDeg = EditorGUILayout.Slider("Twist", P.twistDeg, -90f, 90f);
            P.outDeg = EditorGUILayout.Slider(new GUIContent("Tilt outward", "Mirrors automatically on the other ear."), P.outDeg, -45f, 90f);
            P.fwdDeg = EditorGUILayout.Slider("Tilt forward", P.fwdDeg, -60f, 60f);
            EndParamEdit(EditorGUI.EndChangeCheck());

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Preset: Standing")) { PushUndo(); P.ResetDefault(); Recompute(false); }
            if (GUILayout.Button("Preset: Tall & slim")) { PushUndo(); P.ResetDefault(); P.length = 4.5f; P.width = 0.85f; P.cup = 0.1f; Recompute(false); }
            if (GUILayout.Button("Preset: Lop")) { PushUndo(); P.ResetDefault(); P.length = 3f; P.bendDeg = 50f; P.outDeg = 35f; P.cup = 0.25f; Recompute(false); }
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button("No change (original ears)")) { PushUndo(); float f = P.featherCm; P.ResetIdentity(); P.featherCm = f; Recompute(false); }
        }

        void DrawFluffSliders(EarPart P)
        {
            int ep = FirstEarPart();
            if (ep < 0)
            {
                EditorGUILayout.HelpBox("Inner fluff follows an ear. Add a part of type \"Ear\" and select the ear shell in it.", MessageType.Warning);
                return;
            }
            if (FitFor(m_ActivePart, 1) == null && FitFor(m_ActivePart, -1) == null)
            {
                EditorGUILayout.HelpBox("The fluff can't find its ear yet. Select the ear shell in the \"" + m_Parts[ep].name + "\" part first, and a few points of the fluff in this part.", MessageType.Warning);
                if (GUILayout.Button("Look again")) { RefitAll(); Recompute(false); }
                return;
            }
            EditorGUILayout.HelpBox("The fluff rides on the ear \"" + m_Parts[ep].name + "\" (it bends and tilts with it) but has its own length, position, width and depth, so it can look right at any ear length.", MessageType.None);
            BeginParamEdit();
            EditorGUI.BeginChangeCheck();
            P.fStretch = EditorGUILayout.Slider(new GUIContent("Stretch with the ear", "0 = the fluff keeps its original length however long the ear gets. 1 = it stretches with the ear."), P.fStretch, 0f, 1f);
            bool autoWas = P.fluffAuto;
            P.fluffAuto = EditorGUILayout.Toggle(new GUIContent("Keep its natural position", "On: the fluff stays where it originally sat along the ear (as a share of the ear). Off: set exactly where it starts and ends."), P.fluffAuto);
            if (autoWas && !P.fluffAuto)
            {
                var f = FitFor(m_ActivePart, 1) ?? FitFor(m_ActivePart, -1);
                var pp = m_Parts[ep];
                if (f != null && f.parent != null) { P.fStart = Mathf.Clamp01(H(pp, f.u0) / Mathf.Max(pp.length, 1e-3f)); P.fEnd = Mathf.Clamp01(H(pp, f.u1) / Mathf.Max(pp.length, 1e-3f)); }
            }
            using (new EditorGUI.DisabledScope(P.fluffAuto))
            {
                P.fStart = EditorGUILayout.Slider(new GUIContent("Starts at (% up the ear)", "0 = the ear base, 1 = the tip."), P.fStart, 0f, 1f);
                P.fEnd = EditorGUILayout.Slider(new GUIContent("Ends at (% up the ear)", "Only matters when the fluff stretches with the ear."), P.fEnd, 0f, 1f);
            }
            P.fWidth = EditorGUILayout.Slider("Width", P.fWidth, 0.2f, 3f);
            P.fFollow = EditorGUILayout.Slider(new GUIContent("Follow ear width", "How much the fluff widens and narrows with the ear's shape, so it stays inside the rounded bunny ear."), P.fFollow, 0f, 1f);
            P.fThick = EditorGUILayout.Slider("Thickness", P.fThick, 0.2f, 3f);
            P.fInsetCm = EditorGUILayout.Slider(new GUIContent("Push into the ear (cm)", "Sinks the fluff into the ear surface. Negative lifts it out. Use it if the fluff pokes through the ear."), P.fInsetCm, -3f, 3f);
            EndParamEdit(EditorGUI.EndChangeCheck());
            if (GUILayout.Button("No change (original fluff)")) { PushUndo(); float fe = P.featherCm; P.ResetIdentity(); P.featherCm = fe; Recompute(false); }
        }

        void DrawSculptPanel()
        {
            EditorGUILayout.HelpBox("Finish by hand on top of the settings. Left-drag in the Scene view. Grab pulls the surface, Smooth relaxes lumps, Inflate puffs it out (hold Shift to deflate). Sculpting is its own layer, so you can still change the sliders afterwards.", MessageType.None);
            m_Tool = (SculptTool)GUILayout.Toolbar((int)m_Tool, new[] { "Grab", "Smooth", "Inflate" }, GUILayout.Height(24));
            m_BrushCm = EditorGUILayout.Slider("Brush size (cm)", m_BrushCm, 0.3f, 20f);
            m_Strength = EditorGUILayout.Slider("Strength", m_Strength, 0.05f, 1f);
            m_Symmetry = EditorGUILayout.Toggle("Symmetry (left/right)", m_Symmetry);
            m_LimitToSel = EditorGUILayout.Toggle(new GUIContent("Only inside the selection", "Protects the rest of the head. All parts and their blend zones are editable."), m_LimitToSel);
            if (m_LimitToSel && !AnySelected())
                EditorGUILayout.HelpBox("Nothing is selected, so nothing can be sculpted. Select the ear in the Select tab, or turn off \"Only inside the selection\".", MessageType.Warning);
        }
    }
}
