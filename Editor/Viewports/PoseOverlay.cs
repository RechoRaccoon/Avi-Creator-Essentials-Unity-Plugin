using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AviCreatorEssentials
{
    /// <summary>Projection helper for drawing 3D things in viewport points (top-left origin).</summary>
    public struct Projector
    {
        public Matrix4x4 vp;
        public float w, h;
        public Vector3 camPos;
        public Quaternion camRot;
        public float fov;

        public bool ToLocal(Vector3 world, out Vector2 local, out float depth)
        {
            var c = vp * new Vector4(world.x, world.y, world.z, 1f);
            depth = c.w;
            if (c.w <= 1e-5f)
            {
                local = Vector2.zero;
                return false;
            }
            float nx = c.x / c.w, ny = c.y / c.w;
            local = new Vector2((nx * 0.5f + 0.5f) * w, (0.5f - ny * 0.5f) * h);
            return true;
        }

        public Vector2 ToLocal(Vector3 world)
        {
            ToLocal(world, out var l, out _);
            return l;
        }

        /// <summary>World units per viewport point at the given eye depth.</summary>
        public float WorldPerPoint(float depth) => 2f * depth * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, h);

        public Vector3 Right => camRot * Vector3.right;
        public Vector3 Up => camRot * Vector3.up;
        public Vector3 Forward => camRot * Vector3.forward;
    }

    public enum GizmoHandle
    {
        None,
        X,
        Y,
        Z,
        View,
        Trackball,
    }

    /// <summary>Draws the armature over the model (Blender-style octahedral bones) and the rotation gizmo.</summary>
    public class PoseOverlay
    {
        public const float GizmoRadius = 70f;
        static readonly Color BoneColor = new Color(0.62f, 0.64f, 0.68f);
        static readonly Color BonePoseColor = new Color(0.45f, 0.62f, 0.85f);
        static readonly Color BoneSelected = new Color(0.25f, 0.78f, 1f);
        static readonly Color BoneHover = new Color(0.85f, 0.9f, 1f);
        static readonly Color AxisX = new Color(0.95f, 0.3f, 0.32f);
        static readonly Color AxisY = new Color(0.45f, 0.86f, 0.3f);
        static readonly Color AxisZ = new Color(0.3f, 0.55f, 1f);

        struct BoneDraw
        {
            public Bone bone;
            public float depth;
        }

        readonly List<BoneDraw> m_Order = new List<BoneDraw>();
        readonly Vector2[] m_Ring = new Vector2[97];

        public static bool ShowBone(Bone b, bool humanoidOnly) => b.t != null && (!humanoidOnly || b.isHuman);

        public void DrawBones(Painter2D p, PoseSystem pose, Projector pr, bool poseMode, Bone hover, float sizeMul, bool humanoidOnly)
        {
            if (pose == null) return;
            m_Order.Clear();
            foreach (var b in pose.bones)
            {
                if (!ShowBone(b, humanoidOnly)) continue;
                if (!pr.ToLocal(b.Head, out _, out float d)) continue;
                m_Order.Add(new BoneDraw { bone = b, depth = d });
            }
            m_Order.Sort((a, c) => c.depth.CompareTo(a.depth));

            var faces = new Vector3[8, 3];
            var corners = new Vector3[6];
            var pts = new Vector2[6];
            foreach (var bd in m_Order)
            {
                var b = bd.bone;
                Vector3 head = b.Head;
                Vector3 tail = humanoidOnly ? HumanTail(b) : b.Tail;
                var axis = tail - head;
                float len = axis.magnitude;
                if (len < 1e-5f) continue;
                var dir = axis / len;
                var side = Vector3.ProjectOnPlane(b.t.right, dir);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.ProjectOnPlane(Vector3.up, dir);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.ProjectOnPlane(Vector3.forward, dir);
                side.Normalize();
                var side2 = Vector3.Cross(dir, side);
                float rad = len * 0.1f * sizeMul;
                var c = head + dir * len * 0.1f;
                corners[0] = head;
                corners[1] = tail;
                corners[2] = c + side * rad;
                corners[3] = c + side2 * rad;
                corners[4] = c - side * rad;
                corners[5] = c - side2 * rad;
                bool ok = true;
                for (int i = 0; i < 6; i++) ok &= pr.ToLocal(corners[i], out pts[i], out _);
                if (!ok) continue;

                Color baseCol = b == pose.selected ? BoneSelected : b == hover ? BoneHover : poseMode ? BonePoseColor : BoneColor;
                float alpha = poseMode ? 0.62f : 0.42f;
                int[,] tri =
                {
                    { 0, 2, 3 }, { 0, 3, 4 }, { 0, 4, 5 }, { 0, 5, 2 },
                    { 1, 3, 2 }, { 1, 4, 3 }, { 1, 5, 4 }, { 1, 2, 5 },
                };
                for (int f = 0; f < 8; f++)
                {
                    var a0 = corners[tri[f, 0]];
                    var a1 = corners[tri[f, 1]];
                    var a2 = corners[tri[f, 2]];
                    var n = Vector3.Cross(a1 - a0, a2 - a0).normalized;
                    var fc = (a0 + a1 + a2) / 3f;
                    var outward = fc - (head + dir * Vector3.Dot(fc - head, dir));
                    if (Vector3.Dot(n, outward) < 0f) n = -n;
                    float facing = Vector3.Dot(n, (pr.camPos - fc).normalized);
                    if (facing < 0f) continue;
                    float shade = 0.55f + 0.45f * facing;
                    p.fillColor = new Color(baseCol.r * shade, baseCol.g * shade, baseCol.b * shade, alpha);
                    p.BeginPath();
                    p.MoveTo(pts[tri[f, 0]]);
                    p.LineTo(pts[tri[f, 1]]);
                    p.LineTo(pts[tri[f, 2]]);
                    p.ClosePath();
                    p.Fill();
                }
                // outline
                var outline = b == pose.selected ? new Color(0.85f, 0.97f, 1f, 0.95f) : new Color(0.05f, 0.06f, 0.08f, poseMode ? 0.75f : 0.5f);
                p.strokeColor = outline;
                p.lineWidth = b == pose.selected ? 1.6f : 1f;
                p.lineJoin = LineJoin.Round;
                p.BeginPath();
                p.MoveTo(pts[0]); p.LineTo(pts[2]); p.LineTo(pts[1]); p.LineTo(pts[4]); p.ClosePath();
                p.Stroke();
                p.BeginPath();
                p.MoveTo(pts[0]); p.LineTo(pts[3]); p.LineTo(pts[1]); p.LineTo(pts[5]); p.ClosePath();
                p.Stroke();
                p.BeginPath();
                p.MoveTo(pts[2]); p.LineTo(pts[3]); p.LineTo(pts[4]); p.LineTo(pts[5]); p.ClosePath();
                p.Stroke();

                // joint
                float jr = Mathf.Clamp(rad / Mathf.Max(1e-5f, pr.WorldPerPoint(bd.depth)) * 0.6f, 1.5f, 6f);
                p.fillColor = new Color(baseCol.r, baseCol.g, baseCol.b, Mathf.Min(1f, alpha + 0.25f));
                p.BeginPath();
                p.Arc(pts[0], jr, Angle.Degrees(0), Angle.Degrees(360));
                p.Fill();
            }
        }

        static Vector3 HumanTail(Bone b)
        {
            // When only humanoid bones are shown, point at the nearest humanoid descendant.
            var stack = new Stack<Bone>(b.children);
            Bone best = null;
            int bestDepth = int.MaxValue;
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (c.isHuman)
                {
                    int d = c.depth;
                    if (d < bestDepth || (d == bestDepth && best != null && c.descendants > best.descendants)) { best = c; bestDepth = d; }
                    continue;
                }
                foreach (var cc in c.children) stack.Push(cc);
            }
            return best != null ? best.t.position : b.Tail;
        }

        public Bone Pick(PoseSystem pose, Projector pr, Vector2 local, bool humanoidOnly)
        {
            if (pose == null) return null;
            Bone best = null;
            float bestScore = float.MaxValue;
            foreach (var b in pose.bones)
            {
                if (!ShowBone(b, humanoidOnly)) continue;
                if (!pr.ToLocal(b.Head, out var h, out float dh)) continue;
                if (!pr.ToLocal(humanoidOnly ? HumanTail(b) : b.Tail, out var t, out _)) continue;
                float dist = DistanceToSegment(local, h, t);
                float tol = 7f;
                if (dist > tol) continue;
                float score = dist + dh * 0.01f;
                if (score < bestScore) { bestScore = score; best = b; }
            }
            return best;
        }

        public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float l2 = ab.sqrMagnitude;
            if (l2 < 1e-6f) return Vector2.Distance(p, a);
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2);
            return Vector2.Distance(p, a + ab * t);
        }

        // ------------------------------------------------------------------ gizmo

        public static Vector3 AxisOf(Bone b, GizmoHandle h, bool local, Projector pr)
        {
            switch (h)
            {
                case GizmoHandle.X: return local ? b.t.right : Vector3.right;
                case GizmoHandle.Y: return local ? b.t.up : Vector3.up;
                case GizmoHandle.Z: return local ? b.t.forward : Vector3.forward;
                case GizmoHandle.View: return pr.Forward;
            }
            return Vector3.zero;
        }

        int RingPoints(Bone b, Vector3 axis, float radiusPts, Projector pr, Vector2[] outPts, bool[] front)
        {
            var center = b.Head;
            pr.ToLocal(center, out _, out float depth);
            float r = radiusPts * pr.WorldPerPoint(depth);
            var u = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            var v = Vector3.Cross(axis, u);
            var toCam = (pr.camPos - center).normalized;
            int n = 96;
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                var wp = center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                outPts[i] = pr.ToLocal(wp);
                if (front != null) front[i] = Vector3.Dot(wp - center, toCam) >= -r * 0.02f;
            }
            return n + 1;
        }

        readonly bool[] m_Front = new bool[97];

        public void DrawGizmo(Painter2D p, Bone b, Projector pr, bool local, GizmoHandle hover, GizmoHandle active)
        {
            if (b == null || b.t == null) return;
            if (!pr.ToLocal(b.Head, out var c, out _)) return;

            // trackball disc
            var tb = active == GizmoHandle.Trackball || hover == GizmoHandle.Trackball;
            p.fillColor = new Color(1f, 1f, 1f, tb ? 0.08f : 0.03f);
            p.BeginPath();
            p.Arc(c, GizmoRadius, Angle.Degrees(0), Angle.Degrees(360));
            p.Fill();

            // view ring
            var vcol = hover == GizmoHandle.View || active == GizmoHandle.View ? new Color(1f, 0.95f, 0.5f) : new Color(1f, 1f, 1f, 0.7f);
            p.strokeColor = new Color(0, 0, 0, 0.4f);
            p.lineWidth = 3.5f;
            p.BeginPath();
            p.Arc(c, GizmoRadius * 1.22f, Angle.Degrees(0), Angle.Degrees(360));
            p.Stroke();
            p.strokeColor = vcol;
            p.lineWidth = 1.8f;
            p.BeginPath();
            p.Arc(c, GizmoRadius * 1.22f, Angle.Degrees(0), Angle.Degrees(360));
            p.Stroke();

            DrawAxisRing(p, b, pr, GizmoHandle.X, local, AxisX, hover, active);
            DrawAxisRing(p, b, pr, GizmoHandle.Y, local, AxisY, hover, active);
            DrawAxisRing(p, b, pr, GizmoHandle.Z, local, AxisZ, hover, active);

            Disc(p, c, 3f, Color.white);
        }

        void DrawAxisRing(Painter2D p, Bone b, Projector pr, GizmoHandle h, bool local, Color col, GizmoHandle hover, GizmoHandle active)
        {
            var axis = AxisOf(b, h, local, pr);
            int n = RingPoints(b, axis, GizmoRadius, pr, m_Ring, m_Front);
            bool hot = hover == h || active == h;
            if (hot) col = Color.Lerp(col, Color.white, 0.45f);
            // back half
            p.lineJoin = LineJoin.Round;
            p.strokeColor = new Color(col.r, col.g, col.b, 0.22f);
            p.lineWidth = 1.5f;
            DrawRingPart(p, n, false);
            p.strokeColor = new Color(0, 0, 0, 0.45f);
            p.lineWidth = hot ? 5f : 4f;
            DrawRingPart(p, n, true);
            p.strokeColor = col;
            p.lineWidth = hot ? 3f : 2.2f;
            DrawRingPart(p, n, true);
        }

        void DrawRingPart(Painter2D p, int n, bool front)
        {
            bool open = false;
            for (int i = 0; i < n; i++)
            {
                bool f = m_Front[i] == front;
                if (f && !open)
                {
                    p.BeginPath();
                    p.MoveTo(m_Ring[i]);
                    open = true;
                }
                else if (f)
                {
                    p.LineTo(m_Ring[i]);
                }
                else if (open)
                {
                    p.Stroke();
                    open = false;
                }
            }
            if (open) p.Stroke();
        }

        static void Disc(Painter2D p, Vector2 c, float r, Color col)
        {
            p.fillColor = col;
            p.BeginPath();
            p.Arc(c, r, Angle.Degrees(0), Angle.Degrees(360));
            p.Fill();
        }

        public GizmoHandle HitGizmo(Bone b, Projector pr, Vector2 local, bool localSpace)
        {
            if (b == null || b.t == null) return GizmoHandle.None;
            if (!pr.ToLocal(b.Head, out var c, out _)) return GizmoHandle.None;
            GizmoHandle best = GizmoHandle.None;
            float bestD = 7f;
            foreach (var h in new[] { GizmoHandle.X, GizmoHandle.Y, GizmoHandle.Z })
            {
                int n = RingPoints(b, AxisOf(b, h, localSpace, pr), GizmoRadius, pr, m_Ring, m_Front);
                for (int i = 0; i < n - 1; i++)
                {
                    if (!m_Front[i]) continue;
                    float d = DistanceToSegment(local, m_Ring[i], m_Ring[i + 1]);
                    if (d < bestD) { bestD = d; best = h; }
                }
            }
            if (best != GizmoHandle.None) return best;
            float dc = Vector2.Distance(local, c);
            if (Mathf.Abs(dc - GizmoRadius * 1.22f) < 7f) return GizmoHandle.View;
            if (dc < GizmoRadius) return GizmoHandle.Trackball;
            return GizmoHandle.None;
        }

        /// <summary>Screen direction of the ring tangent at the point nearest the mouse, for drag-to-rotate.</summary>
        public Vector2 RingTangent(Bone b, Projector pr, Vector3 axis, Vector2 local, out Vector3 grabWorld)
        {
            var center = b.Head;
            pr.ToLocal(center, out _, out float depth);
            float r = GizmoRadius * pr.WorldPerPoint(depth);
            var u = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            var v = Vector3.Cross(axis, u);
            float best = float.MaxValue;
            Vector3 g = center + u * r;
            for (int i = 0; i < 96; i++)
            {
                float a = i / 96f * Mathf.PI * 2f;
                var wp = center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                float d = Vector2.Distance(pr.ToLocal(wp), local);
                if (d < best) { best = d; g = wp; }
            }
            grabWorld = g;
            var tangent = Vector3.Cross(axis, g - center).normalized;
            var s0 = pr.ToLocal(g);
            var s1 = pr.ToLocal(g + tangent * r * 0.2f);
            var t2 = s1 - s0;
            if (t2.sqrMagnitude < 1e-6f) return Vector2.right;
            return t2.normalized;
        }
    }
}
