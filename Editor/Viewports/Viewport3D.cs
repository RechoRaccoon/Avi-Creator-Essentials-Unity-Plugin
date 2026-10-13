using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AviCreatorEssentials
{
    /// <summary>The 3D view: navigation, painting on the model, fills, picking, image placement and posing.</summary>
    public class Viewport3D : ViewportBase
    {
        enum Drag
        {
            None,
            Orbit,
            Pan,
            Zoom,
            Paint,
            BrushSize,
            Picker,
            Image,
            Gizmo,
            BoneMove,
            Select,
        }

        enum ImageDrag
        {
            Move,
            Scale,
            Rotate,
        }

        enum Modal
        {
            None,
            Rotate,
            Grab,
        }

        // camera
        Vector3 m_CamPos = new Vector3(0, 1, 2);
        float m_Yaw = 180f;
        float m_Pitch;
        float m_Focus = 2f;
        bool m_Framed;

        // pointer
        Drag m_Drag;
        int m_PointerId = -1;
        Vector2 m_Mouse;
        Vector2 m_DragStart;
        Vector2 m_DragLast;
        Vector3 m_OrbitPivot;
        bool m_Inside;
        bool m_Snap;
        bool m_HasHit;
        MeshHit m_Hit;

        // painting
        StrokeContext m_Stroke;
        readonly StrokeSampler m_Sampler = new StrokeSampler();
        readonly System.Random m_Rng = new System.Random();
        readonly List<Dab3D> m_Dabs = new List<Dab3D>();
        float m_LastDepth = 1f;
        float m_StartRadius;
        float m_StartHardness;

        // images
        ImageDrag m_ImageDrag;
        PaintLayer m_ImageLayer;
        TextureSet m_ImageSet;

        // pose
        readonly PoseOverlay m_PoseOverlay = new PoseOverlay();
        Bone m_HoverBone;
        GizmoHandle m_HoverHandle;
        GizmoHandle m_ActiveHandle;
        Quaternion m_StartRot;
        Vector3 m_StartPos;
        Vector2 m_Tangent;
        Vector3 m_Axis;
        PoseSnapshot m_PoseBefore;
        Modal m_Modal;
        int m_ModalAxis = -1;     // -1 none, 0 X, 1 Y, 2 Z
        bool m_ModalLocal;
        Vector2 m_ModalStart;

        public bool MouseInside => m_Inside;
        readonly List<Vector2> m_SelPoints = new List<Vector2>();
        SelectOp m_SelOp;
        public bool InModal => m_Modal != Modal.None;

        public Viewport3D(PainterContext context) : base(context)
        {
            AddToClassList("ap-viewport-3d");
            msaa = 4;
            depthBits = 24;
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag(true));
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<PointerEnterEvent>(_ => { m_Inside = true; RepaintOverlay(); });
            RegisterCallback<PointerLeaveEvent>(_ => { m_Inside = false; RepaintOverlay(); });
        }

        // ================================================================== camera

        public ViewCamera Cam
        {
            get
            {
                float near = Mathf.Clamp(m_Focus * 0.01f, 0.002f, 0.1f);
                return new ViewCamera
                {
                    position = m_CamPos,
                    rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f),
                    fov = ctx.view.fov,
                    near = near,
                    far = Mathf.Max(50f, m_Focus * 100f),
                };
            }
        }

        float Aspect => W / Mathf.Max(1f, H);
        Matrix4x4 ViewM => Cam.View;
        Matrix4x4 ProjM => Cam.Projection(Aspect);

        Projector MakeProjector()
        {
            var cam = Cam;
            return new Projector
            {
                vp = cam.Projection(Aspect) * cam.View,
                w = W,
                h = H,
                camPos = cam.position,
                camRot = cam.rotation,
                fov = cam.fov,
            };
        }

        Ray RayAt(Vector2 local)
        {
            float nx = local.x / Mathf.Max(1f, W) * 2f - 1f;
            float ny = 1f - local.y / Mathf.Max(1f, H) * 2f;
            var inv = (ProjM * ViewM).inverse;
            var a = inv.MultiplyPoint(new Vector3(nx, ny, -1f));
            var b = inv.MultiplyPoint(new Vector3(nx, ny, 1f));
            return new Ray(a, (b - a).normalized);
        }

        bool Raycast(Vector2 local, out MeshHit hit)
        {
            hit = default;
            var s = ctx.session;
            if (s == null) return false;
            return s.meshes.Raycast(RayAt(local), out hit);
        }

        public void Frame()
        {
            var s = ctx.session;
            if (s == null) return;
            var b = s.meshes.VisibleBounds();
            if (ctx.PoseMode && s.pose.selected != null)
                b = new Bounds(s.pose.selected.Head, Vector3.one * 0.25f);
            float radius = Mathf.Max(0.05f, b.extents.magnitude);
            float dist = radius / Mathf.Sin(ctx.view.fov * 0.5f * Mathf.Deg2Rad) * 0.9f;
            var rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            m_CamPos = b.center - rot * Vector3.forward * dist;
            m_Focus = dist;
            m_Framed = true;
            RequestRender();
        }

        public void FrameFace()
        {
            var s = ctx.session;
            if (s == null) return;
            var head = FindHuman(HumanBodyBones.Head);
            if (head == null) { Frame(); return; }
            m_Yaw = 180f;
            m_Pitch = 0f;
            var rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            var center = head.position + Vector3.up * 0.05f;
            m_Focus = 0.55f;
            m_CamPos = center - rot * Vector3.forward * m_Focus;
            RequestRender();
        }

        Transform FindHuman(HumanBodyBones hb)
        {
            var s = ctx.session;
            foreach (var kv in s.HumanPaths)
                if (kv.Value == hb) return APUtil.FindPath(s.clone.transform, kv.Key);
            return null;
        }

        public void SetViewAngle(float yaw, float pitch)
        {
            var pivot = m_CamPos + Quaternion.Euler(m_Pitch, m_Yaw, 0f) * Vector3.forward * m_Focus;
            m_Yaw = yaw;
            m_Pitch = pitch;
            m_CamPos = pivot - Quaternion.Euler(m_Pitch, m_Yaw, 0f) * Vector3.forward * m_Focus;
            RequestRender();
        }

        public void ResetFraming() => m_Framed = false;

        void Orbit(Vector2 delta, Vector3 pivot)
        {
            float dYaw = delta.x * 0.35f;
            float dPitch = delta.y * 0.35f;
            float newPitch = Mathf.Clamp(m_Pitch + dPitch, -89.5f, 89.5f);
            dPitch = newPitch - m_Pitch;
            var qYaw = Quaternion.AngleAxis(dYaw, Vector3.up);
            m_CamPos = pivot + qYaw * (m_CamPos - pivot);
            m_Yaw += dYaw;
            var right = Quaternion.Euler(0f, m_Yaw, 0f) * Vector3.right;
            var qPitch = Quaternion.AngleAxis(dPitch, right);
            m_CamPos = pivot + qPitch * (m_CamPos - pivot);
            m_Pitch = newPitch;
            m_Focus = Mathf.Max(0.02f, Vector3.Distance(m_CamPos, pivot));
            RequestRender();
        }

        void Pan(Vector2 delta)
        {
            var rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            float wpp = 2f * m_Focus * Mathf.Tan(ctx.view.fov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1f, H);
            m_CamPos += (rot * Vector3.left * delta.x + rot * Vector3.up * delta.y) * wpp;
            RequestRender();
        }

        void Dolly(float amount, Vector2 towards)
        {
            var ray = RayAt(towards);
            float dist = m_Focus;
            if (Raycast(towards, out var h)) dist = h.distance;
            float step = dist * amount;
            if (amount > 0f) step = Mathf.Min(step, dist - Cam.near * 2f);
            m_CamPos += ray.direction * step;
            m_Focus = Mathf.Max(0.02f, m_Focus - step);
            RequestRender();
        }

        // ================================================================== rendering

        protected override void DoRender(RenderTexture target)
        {
            if (!m_Framed) Frame();
            ctx.session.Render(target, Cam);
        }

        // ================================================================== input

        void OnPointerDown(PointerDownEvent evt)
        {
            if (ctx.session == null) return;
            Focus();
            m_Mouse = evt.localPosition;
            if (m_Drag != Drag.None) return;

            if (m_Modal != Modal.None)
            {
                if (evt.button == 0) ConfirmModal();
                else CancelModal();
                evt.StopPropagation();
                return;
            }

            bool alt = evt.altKey, shift = evt.shiftKey, ctrl = evt.actionKey;
            float pressure = PressureOf(evt);
            m_DragStart = m_DragLast = m_Mouse;

            if (evt.button == 0 && alt) BeginNav(Drag.Orbit);
            else if (evt.button == 2) BeginNav(alt ? Drag.Orbit : ctrl ? Drag.Zoom : Drag.Pan);
            else if (evt.button == 1)
            {
                if (ctrl && !ctx.PoseMode) BeginBrushSize();
                else
                {
                    BeginNav(alt ? Drag.Zoom : shift ? Drag.Pan : Drag.Orbit);
                    rightClickPending = !alt && !shift && !ctx.PoseMode;
                }
            }
            else if (evt.button == 0)
            {
                if (ctx.PoseMode) PoseDown(shift);
                else ToolDown(pressure, shift, ctrl);
            }

            if (m_Drag != Drag.None)
            {
                m_PointerId = evt.pointerId;
                this.CapturePointer(evt.pointerId);
            }
            evt.StopPropagation();
        }

        void BeginNav(Drag d)
        {
            m_Drag = d;
            if (d == Drag.Orbit)
            {
                var fwd = Quaternion.Euler(m_Pitch, m_Yaw, 0f) * Vector3.forward;
                m_OrbitPivot = m_CamPos + fwd * m_Focus;
                if (ctx.view.orbitAroundCursor && Raycast(m_Mouse, out var h))
                {
                    m_OrbitPivot = h.point;
                    m_Focus = h.distance;
                }
                else if (ctx.PoseMode && ctx.session.pose.selected != null)
                {
                    m_OrbitPivot = ctx.session.pose.selected.Head;
                }
            }
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (ctx.session == null) return;
            var prev = m_Mouse;
            m_Mouse = evt.localPosition;
            m_Snap = evt.actionKey;
            var delta = m_Mouse - m_DragLast;

            if (m_Modal != Modal.None)
            {
                UpdateModal();
                evt.StopPropagation();
                return;
            }

            if (rightClickPending)
            {
                // Hold the orbit until the mouse really moves, so a plain right-click leaves the camera alone.
                if (Vector2.Distance(m_Mouse, m_DragStart) < ClickSlop) return;
                rightClickPending = false;
            }

            switch (m_Drag)
            {
                case Drag.Orbit:
                    Orbit(delta, m_OrbitPivot);
                    break;
                case Drag.Pan:
                    Pan(delta);
                    break;
                case Drag.Zoom:
                    Dolly(-delta.y * 0.01f + delta.x * 0.01f, m_DragStart);
                    break;
                case Drag.Paint:
                    ContinueStroke(PressureOf(evt));
                    break;
                case Drag.BrushSize:
                    UpdateBrushSize();
                    break;
                case Drag.Picker:
                    PickAt(m_Mouse, false, evt.shiftKey);
                    break;
                case Drag.Image:
                    DragImage(delta);
                    break;
                case Drag.Gizmo:
                    DragGizmo(delta);
                    break;
                case Drag.BoneMove:
                    DragBoneMove();
                    break;
                case Drag.Select:
                    if (ctx.selectLasso && Vector2.Distance(m_SelPoints[m_SelPoints.Count - 1], m_Mouse) > 3f) m_SelPoints.Add(m_Mouse);
                    break;
                default:
                    UpdateHover();
                    break;
            }
            m_DragLast = m_Mouse;
            if (!ctx.PoseMode && (m_Drag == Drag.None || m_Drag == Drag.Paint || m_Drag == Drag.Picker)) UpdateCursorHit();
            if (prev != m_Mouse) RepaintOverlay();
            UpdateHud();
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerId != m_PointerId) return;
            bool click = rightClickPending;
            EndDrag(false);
            if (this.HasPointerCapture(evt.pointerId)) this.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
            if (click) OpenQuickColor(evt.localPosition);
        }

        void EndDrag(bool cancelled)
        {
            switch (m_Drag)
            {
                case Drag.Paint:
                    EndStroke();
                    break;
                case Drag.Picker:
                    ctx.colors.Use(ctx.primary);
                    break;
                case Drag.Gizmo:
                case Drag.BoneMove:
                    CommitPose(m_Drag == Drag.Gizmo ? "Rotate bone" : "Move bone");
                    m_ActiveHandle = GizmoHandle.None;
                    break;
                case Drag.BrushSize:
                    ctx.NotifyBrushChanged();
                    break;
                case Drag.Select:
                    if (!cancelled) FinishSelect();
                    break;
            }
            m_Drag = Drag.None;
            m_PointerId = -1;
            rightClickPending = false;
            RepaintOverlay();
        }

        void OnWheel(WheelEvent evt)
        {
            if (ctx.session == null) return;
            float d = evt.delta.y;
            if (evt.actionKey && !ctx.PoseMode)
            {
                ctx.brush.radius = Mathf.Clamp(ctx.brush.radius * (d > 0 ? 1.1f : 0.9f), 0.0005f, 1f);
                ctx.NotifyBrushChanged();
            }
            else if (evt.shiftKey && !ctx.PoseMode)
            {
                ctx.brush.hardness = Mathf.Clamp01(ctx.brush.hardness + (d > 0 ? -0.05f : 0.05f));
                ctx.NotifyBrushChanged();
            }
            else
            {
                Dolly(d > 0 ? -0.15f : 0.15f, evt.localMousePosition);
            }
            RepaintOverlay();
            UpdateHud();
            evt.StopPropagation();
        }

        void UpdateCursorHit()
        {
            m_HasHit = Raycast(m_Mouse, out m_Hit);
            if (m_HasHit) m_LastDepth = Vector3.Dot(m_Hit.point - m_CamPos, Quaternion.Euler(m_Pitch, m_Yaw, 0f) * Vector3.forward);
        }

        void UpdateHover()
        {
            if (ctx.PoseMode)
            {
                var pr = MakeProjector();
                var pose = ctx.session.pose;
                m_HoverHandle = ctx.poseTool == PoseTool.Rotate && pose.selected != null
                    ? m_PoseOverlay.HitGizmo(pose.selected, pr, m_Mouse, ctx.view.gizmoLocal)
                    : GizmoHandle.None;
                m_HoverBone = m_HoverHandle == GizmoHandle.None ? m_PoseOverlay.Pick(pose, pr, m_Mouse, ctx.view.humanoidOnly) : null;
            }
            else
            {
                m_HoverBone = null;
                m_HoverHandle = GizmoHandle.None;
            }
        }

        /// <summary>Called by the window after the pose or visibility changed so the cursor stays on the surface.</summary>
        public void RefreshHit()
        {
            if (m_Inside && !ctx.PoseMode) UpdateCursorHit();
            RepaintOverlay();
        }

        // ================================================================== tools

        void ToolDown(float pressure, bool shift, bool ctrl)
        {
            switch (ctx.Tool)
            {
                case PaintTool.Brush:
                case PaintTool.Eraser:
                    BeginStroke(pressure);
                    break;
                case PaintTool.Fill:
                    FillAt(m_Mouse, shift);
                    break;
                case PaintTool.Picker:
                    PickAt(m_Mouse, true, shift);
                    m_Drag = Drag.Picker;
                    break;
                case PaintTool.Image:
                    BeginImageDrag(shift, ctrl);
                    break;
                case PaintTool.Select:
                    m_Drag = Drag.Select;
                    m_SelOp = shift ? SelectOp.Add : ctrl ? SelectOp.Subtract : SelectOp.Replace;
                    m_SelPoints.Clear();
                    m_SelPoints.Add(m_Mouse);
                    break;
            }
        }

        // ------------------------------------------------------------------ strokes

        void BeginStroke(float pressure)
        {
            var s = ctx.session;
            s.UpdateBounds();
            bool hit = Raycast(m_Mouse, out var h);
            if (hit)
            {
                var hitSet = s.SetForUser(h.entry, h.submesh, s.ActiveSet != null ? s.ActiveSet.property : null);
                if (hitSet != null && ctx.brush.autoSelectMaterial && hitSet != s.ActiveSet) s.ActiveSet = hitSet;
            }
            var active = s.ActiveSet;
            if (active == null)
            {
                ctx.ShowHint("Pick a material to paint on first.");
                return;
            }
            if (!active.initialized) s.InitializeSet(active);
            s.UpdateBounds();

            var cam = Cam;
            var view = cam.View;
            var proj = cam.Projection(Aspect);
            bool occlusion = ctx.brush.occlusion && rt != null;
            if (occlusion) s.engine.RenderDepth(view, proj, Mathf.Max(64, rt.width / 2), Mathf.Max(64, rt.height / 2));

            var tool = ctx.Tool == PaintTool.Eraser ? StrokeTool.Erase : StrokeTool.Paint;
            string name = tool == StrokeTool.Erase ? "Erase" : "Paint";
            m_Stroke = s.engine.BeginStroke(new[] { active }, tool, ctx.primary, ctx.brush.opacity, name, proj * view, occlusion);
            if (tool == StrokeTool.Paint && !active.editingMask) ctx.colors.Use(ctx.primary);
            m_Sampler.Begin(m_Mouse, pressure);
            m_Drag = Drag.Paint;
            ctx.strokeActive = true;
            m_Dabs.Clear();
            EmitDab(m_Mouse, pressure, Vector2.right);
            FlushDabs();
        }

        void ContinueStroke(float pressure)
        {
            if (m_Stroke == null) return;
            m_Sampler.Advance(m_Mouse, pressure, StrokeStep, ctx.brush, 1f, (pos, pr, dir) => EmitDab(pos, pr, dir));
            FlushDabs();
        }

        float StrokeStep => Mathf.Max(0.5f, ctx.brush.spacing * 2f * RadiusPoints(ctx.brush.radius));

        float RadiusPoints(float worldRadius)
        {
            float depth = Mathf.Max(0.01f, m_LastDepth);
            return worldRadius / (2f * depth * Mathf.Tan(ctx.view.fov * 0.5f * Mathf.Deg2Rad)) * H;
        }

        void EmitDab(Vector2 pos, float pressure, Vector2 dir)
        {
            var b = ctx.brush;
            var s = ctx.session;
            float sizeK = (b.pressureSize ? Mathf.Lerp(0.15f, 1f, pressure) : 1f) * (1f - b.sizeJitter * (float)m_Rng.NextDouble());
            float radius = b.radius * sizeK;
            if (b.scatter > 0f)
            {
                float a = (float)m_Rng.NextDouble() * Mathf.PI * 2f;
                float r = Mathf.Sqrt((float)m_Rng.NextDouble()) * b.scatter * 2f * RadiusPoints(radius);
                pos += new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            if (!Raycast(pos, out var h)) return;
            var cam = Cam;
            m_LastDepth = Vector3.Dot(h.point - cam.position, cam.Forward);

            Vector3 fwd = (cam.position - h.point).normalized;
            if (b.alignToSurface)
            {
                fwd = Vector3.Dot(h.normal, fwd) >= 0f ? h.normal : -h.normal;
            }
            var camUp = cam.rotation * Vector3.up;
            var up0 = Vector3.ProjectOnPlane(camUp, fwd);
            if (up0.sqrMagnitude < 1e-6f) up0 = Vector3.ProjectOnPlane(cam.rotation * Vector3.forward, fwd);
            up0.Normalize();
            var right0 = Vector3.Cross(fwd, up0);
            float angle = b.angle + (b.angleJitter > 0f ? RandomRange(m_Rng, -b.angleJitter, b.angleJitter) : 0f);
            if (b.followStroke) angle += Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg;
            var q = Quaternion.AngleAxis(-angle, fwd);
            float strength = b.flow * (b.pressureOpacity ? pressure : 1f) * (1f - b.opacityJitter * (float)m_Rng.NextDouble());

            var dab = new Dab3D
            {
                position = h.point,
                radius = radius,
                right = q * right0,
                up = q * up0,
                forward = fwd,
                strength = strength,
                camPos = cam.position,
                occlusion = b.occlusion,
                backface = b.backfaceCulling,
            };
            AddDab(dab, h);
            if (b.symmetry) AddDab(Mirror(dab), h, true);
        }

        void AddDab(Dab3D dab, MeshHit h, bool mirrored = false)
        {
            var s = ctx.session;
            if (ctx.brush.paintAllMaterials)
            {
                foreach (var set in s.sets)
                {
                    if (m_Stroke.Find(set) != null) continue;
                    if (set.property != s.ActiveSet.property) continue;
                    if (!set.initialized) s.UpdateBounds();
                    var bnd = set.worldBounds;
                    bnd.Expand(dab.radius * 2f);
                    if (set.initialized && !bnd.Contains(dab.position)) continue;
                    if (!set.initialized)
                    {
                        // Only initialise a set when the dab actually lands on it.
                        var hs = mirrored ? null : s.SetForUser(h.entry, h.submesh, set.property);
                        if (hs != set) continue;
                        s.InitializeSet(set);
                        s.UpdateBounds();
                    }
                    s.engine.AddTarget(m_Stroke, set);
                }
            }
            m_Dabs.Add(dab);
        }

        Dab3D Mirror(Dab3D d)
        {
            var root = ctx.session.clone.transform;
            int axis = Mathf.Clamp(ctx.brush.symmetryAxis, 0, 2);
            Vector3 MP(Vector3 p)
            {
                var l = root.InverseTransformPoint(p);
                l[axis] = -l[axis];
                return root.TransformPoint(l);
            }
            Vector3 MD(Vector3 v)
            {
                var l = root.InverseTransformDirection(v);
                l[axis] = -l[axis];
                return root.TransformDirection(l);
            }
            return new Dab3D
            {
                position = MP(d.position),
                radius = d.radius,
                right = MD(d.right),
                up = MD(d.up),
                forward = MD(d.forward),
                strength = d.strength,
                camPos = MP(d.camPos),
                occlusion = false,
                backface = true,
            };
        }

        void FlushDabs()
        {
            if (m_Stroke == null || m_Dabs.Count == 0) return;
            ctx.session.engine.Dabs3D(m_Stroke, m_Dabs, ctx.Tip, ctx.brush);
            m_Dabs.Clear();
            ctx.session.engine.Apply(m_Stroke);
            ctx.Render();
        }

        void EndStroke()
        {
            if (m_Stroke == null) return;
            if (ctx.brush.stabilizer > 0f && ctx.brush.stabilizerCatchUp)
            {
                m_Sampler.Finish(StrokeStep, (pos, pr, dir) => EmitDab(pos, pr, dir));
                FlushDabs();
            }
            var entry = ctx.session.engine.EndStroke(m_Stroke);
            if (entry != null) ctx.session.Push(entry);
            m_Stroke = null;
            ctx.strokeActive = false;
            ctx.session.NotifyStructureChanged();
            ctx.Render();
        }

        public void CancelStroke()
        {
            if (m_Stroke == null) return;
            ctx.session.engine.CancelStroke(m_Stroke);
            m_Stroke = null;
            ctx.strokeActive = false;
            m_Drag = Drag.None;
            ctx.Render();
        }

        // ------------------------------------------------------------------ brush size drag

        void BeginBrushSize()
        {
            m_Drag = Drag.BrushSize;
            m_StartRadius = ctx.brush.radius;
            m_StartHardness = ctx.brush.hardness;
            UpdateCursorHit();
        }

        void UpdateBrushSize()
        {
            var d = m_Mouse - m_DragStart;
            ctx.brush.radius = Mathf.Clamp(m_StartRadius * Mathf.Exp(d.x * 0.01f), 0.0005f, 1f);
            ctx.brush.hardness = Mathf.Clamp01(m_StartHardness - d.y * 0.005f);
            ctx.NotifyBrushChanged();
        }

        // ------------------------------------------------------------------ fill & picker

        void FillAt(Vector2 local, bool erase)
        {
            var s = ctx.session;
            if (!Raycast(local, out var h)) return;
            var set = s.SetForUser(h.entry, h.submesh, s.ActiveSet != null ? s.ActiveSet.property : null);
            if (set == null) return;
            if (set != s.ActiveSet) s.ActiveSet = set;
            if (!set.initialized) s.InitializeSet(set);
            var e = s.meshes.entries[h.entry];
            var stroke = s.engine.BeginStroke(new[] { set }, erase ? StrokeTool.Erase : StrokeTool.Paint, ctx.primary, 1f,
                "Fill", Matrix4x4.identity, false);
            switch (ctx.fillMode)
            {
                case FillMode.UVIsland:
                case FillMode.MeshPiece:
                {
                    var mode = ctx.fillMode == FillMode.UVIsland ? MeshCache.IslandMode.UV : MeshCache.IslandMode.Mesh;
                    var islands = s.meshes.GetIslands(e, h.submesh, set.uvChannel, mode);
                    int id = islands[h.triangle / 3];
                    var tris = new List<int>();
                    for (int t = 0; t < islands.Length; t++)
                        if (islands[t] == id) tris.Add(t * 3);
                    s.engine.CoverTriangles(stroke, set, e, h.submesh, tris);
                    if (ctx.brush.symmetry) FillMirrored(stroke, set, h, mode);
                    break;
                }
                case FillMode.Material:
                    s.engine.CoverIslands(stroke, set);
                    break;
                case FillMode.WholeTexture:
                    s.engine.CoverAll(stroke, set);
                    break;
                case FillMode.SimilarColor:
                {
                    var uv = e.InterpolateUV(h, set.uvChannel);
                    var mask = s.engine.FloodSelect(set, uv, ctx.fillTolerance, ctx.fillContiguous, ctx.fillSampleLayer);
                    s.engine.CoverFromTexture(stroke, set, mask);
                    if (mask != null) UnityEngine.Object.DestroyImmediate(mask);
                    break;
                }
            }
            var entry = s.engine.EndStroke(stroke);
            if (entry != null) s.Push(entry);
            if (!erase && !set.editingMask) ctx.colors.Use(ctx.primary);
            s.NotifyStructureChanged();
            ctx.Render();
        }

        void FillMirrored(StrokeContext stroke, TextureSet set, MeshHit h, MeshCache.IslandMode mode)
        {
            var s = ctx.session;
            var root = s.clone.transform;
            var l = root.InverseTransformPoint(h.point);
            int axis = Mathf.Clamp(ctx.brush.symmetryAxis, 0, 2);
            l[axis] = -l[axis];
            var mp = root.TransformPoint(l);
            var n = root.InverseTransformDirection(h.normal);
            n[axis] = -n[axis];
            var mn = root.TransformDirection(n);
            var ray = new Ray(mp + mn * 0.02f, -mn);
            if (!s.meshes.Raycast(ray, out var mh, 0.05f)) return;
            var ms = s.SetForUser(mh.entry, mh.submesh, set.property);
            if (ms != set) return;
            var e = s.meshes.entries[mh.entry];
            var islands = s.meshes.GetIslands(e, mh.submesh, set.uvChannel, mode);
            int id = islands[mh.triangle / 3];
            var tris = new List<int>();
            for (int t = 0; t < islands.Length; t++)
                if (islands[t] == id) tris.Add(t * 3);
            s.engine.CoverTriangles(stroke, set, e, mh.submesh, tris);
        }

        void PickAt(Vector2 local, bool first, bool secondary)
        {
            var s = ctx.session;
            if (!Raycast(local, out var h)) return;
            var set = s.SetForUser(h.entry, h.submesh, s.ActiveSet != null ? s.ActiveSet.property : null);
            if (set == null) return;
            var e = s.meshes.entries[h.entry];
            var uv = e.InterpolateUV(h, set.uvChannel);
            Color c;
            if (set.initialized) c = s.engine.SampleComposite(set, uv);
            else
            {
                s.InitializeSet(set);
                c = s.engine.SampleComposite(set, uv);
            }
            c.a = 1f;
            if (secondary) ctx.SetSecondary(c);
            else ctx.SetPrimary(c);
        }

        // ------------------------------------------------------------------ UV selection

        void FinishSelect()
        {
            var s = ctx.session;
            if (Vector2.Distance(m_DragStart, m_Mouse) < 4f)
            {
                if (Raycast(m_Mouse, out var h))
                {
                    var hitSet = s.SetForUser(h.entry, h.submesh, s.ActiveSet != null ? s.ActiveSet.property : null);
                    if (hitSet != null && hitSet != s.ActiveSet)
                    {
                        if (m_SelOp == SelectOp.Replace || ctx.brush.autoSelectMaterial) s.ActiveSet = hitSet;
                        else return;
                    }
                    var set = s.ActiveSet;
                    if (!set.initialized) s.InitializeSet(set);
                    s.Select(set, new[] { new TriRef(h.entry, h.submesh, h.triangle) }, m_SelOp, ctx.selectIslands);
                }
                else if (m_SelOp == SelectOp.Replace && s.ActiveSet != null)
                {
                    s.ClearSelection(s.ActiveSet);
                }
            }
            else if (s.ActiveSet != null)
            {
                var poly = new List<Vector2>();
                if (ctx.selectLasso)
                {
                    poly.AddRange(m_SelPoints);
                    poly.Add(m_Mouse);
                }
                else
                {
                    poly.Add(m_DragStart);
                    poly.Add(new Vector2(m_Mouse.x, m_DragStart.y));
                    poly.Add(m_Mouse);
                    poly.Add(new Vector2(m_DragStart.x, m_Mouse.y));
                }
                var pr = MakeProjector();
                var set = s.ActiveSet;
                if (!set.initialized) s.InitializeSet(set);
                var tris = s.TrianglesOnScreen(set, poly, w => pr.ToLocal(w, out var l, out _) ? l : (Vector2?)null, pr.camPos, true);
                s.Select(set, tris, m_SelOp, ctx.selectIslands);
            }
            m_SelPoints.Clear();
            ctx.Render();
        }

        // ------------------------------------------------------------------ images

        void BeginImageDrag(bool shift, bool ctrl)
        {
            var s = ctx.session;
            var set = s.ActiveSet;
            var layer = set?.ActiveLayer;
            if (layer == null || layer.kind != LayerKind.Image || layer.placement == null)
            {
                ctx.ShowHint("Select an image layer, or use Import Image in the Layers panel.");
                return;
            }
            m_ImageLayer = layer;
            m_ImageSet = set;
            m_ImageDrag = shift ? ImageDrag.Scale : ctrl ? ImageDrag.Rotate : ImageDrag.Move;
            m_Drag = Drag.Image;
            if (m_ImageDrag == ImageDrag.Move && !layer.placement.wrap) MoveImageTo(m_Mouse);
        }

        void MoveImageTo(Vector2 local)
        {
            if (!Raycast(local, out var h)) return;
            var s = ctx.session;
            var p = m_ImageLayer.placement;
            if (p.projected)
            {
                var cam = Cam;
                var n = Vector3.Dot(h.normal, cam.position - h.point) >= 0f ? h.normal : -h.normal;
                s.EditPlacement(m_ImageSet, m_ImageLayer, "move", pl =>
                {
                    pl.worldPos = h.point;
                    pl.normal = n;
                    pl.up = cam.rotation * Vector3.up;
                    if (pl.depth <= 0f) pl.depth = pl.worldWidth * 0.5f;
                });
            }
            else
            {
                var hs = s.SetForUser(h.entry, h.submesh, m_ImageSet.property);
                if (hs != m_ImageSet) return;
                var uv = s.meshes.entries[h.entry].InterpolateUV(h, m_ImageSet.uvChannel);
                s.EditPlacement(m_ImageSet, m_ImageLayer, "move", pl => pl.uvCenter = uv);
            }
            ctx.Render();
        }

        void DragImage(Vector2 delta)
        {
            var s = ctx.session;
            if (m_ImageLayer.placement.wrap)
            {
                DragWrapped(delta);
                return;
            }
            switch (m_ImageDrag)
            {
                case ImageDrag.Move:
                    MoveImageTo(m_Mouse);
                    break;
                case ImageDrag.Scale:
                    float k = Mathf.Exp((delta.x - delta.y) * 0.006f);
                    s.EditPlacement(m_ImageSet, m_ImageLayer, "scale", pl =>
                    {
                        pl.worldWidth = Mathf.Clamp(pl.worldWidth * k, 0.002f, 5f);
                        pl.depth = Mathf.Max(pl.depth, pl.worldWidth * 0.25f);
                        pl.uvSize *= k;
                    });
                    break;
                case ImageDrag.Rotate:
                    s.EditPlacement(m_ImageSet, m_ImageLayer, "rotate", pl => pl.rotation = Mathf.Repeat(pl.rotation + delta.x * 0.5f + 180f, 360f) - 180f);
                    break;
            }
            ctx.Render();
        }

        /// <summary>Wrapped images: drag slides the texture over the model, Shift scales, Ctrl rotates.</summary>
        void DragWrapped(Vector2 delta)
        {
            var s = ctx.session;
            switch (m_ImageDrag)
            {
                case ImageDrag.Move:
                {
                    var cam = Cam;
                    var root = s.meshes.projectionRoot;
                    var inv = root != null ? Quaternion.Inverse(root.rotation) : Quaternion.identity;
                    var right = inv * (cam.rotation * Vector3.right);
                    var up = inv * (cam.rotation * Vector3.up);
                    s.EditPlacement(m_ImageSet, m_ImageLayer, "slide", pl =>
                    {
                        float k = pl.tileSize * 0.004f;
                        pl.wrapOffset -= (right * delta.x - up * delta.y) * k;
                    });
                    break;
                }
                case ImageDrag.Scale:
                    float f = Mathf.Exp((delta.x - delta.y) * 0.006f);
                    s.EditPlacement(m_ImageSet, m_ImageLayer, "tile size", pl => pl.tileSize = Mathf.Clamp(pl.tileSize * f, 0.005f, 5f));
                    break;
                case ImageDrag.Rotate:
                    s.EditPlacement(m_ImageSet, m_ImageLayer, "rotate", pl => pl.rotation = Mathf.Repeat(pl.rotation + delta.x * 0.5f + 180f, 360f) - 180f);
                    break;
            }
            ctx.Render();
        }

        /// <summary>Places a new projected image where the user dropped or clicked.</summary>
        public bool TryHit(Vector2 local, out MeshHit hit) => Raycast(local, out hit);

        public ViewCamera CurrentCamera => Cam;

        // ================================================================== posing

        void PoseDown(bool shift)
        {
            var s = ctx.session;
            var pose = s.pose;
            var pr = MakeProjector();
            pose.FinishTransition();
            if (ctx.poseTool == PoseTool.Rotate && pose.selected != null)
            {
                var hnd = m_PoseOverlay.HitGizmo(pose.selected, pr, m_Mouse, ctx.view.gizmoLocal);
                if (hnd != GizmoHandle.None)
                {
                    BeginGizmo(hnd, pr);
                    return;
                }
            }
            var b = m_PoseOverlay.Pick(pose, pr, m_Mouse, ctx.view.humanoidOnly);
            pose.Select(b);
            if (b != null && ctx.poseTool == PoseTool.Move)
            {
                m_PoseBefore = pose.Capture();
                m_StartPos = b.t.position;
                m_Drag = Drag.BoneMove;
            }
            RepaintOverlay();
            UpdateHud();
        }

        void BeginGizmo(GizmoHandle h, Projector pr)
        {
            var b = ctx.session.pose.selected;
            m_PoseBefore = ctx.session.pose.Capture();
            m_ActiveHandle = h;
            m_StartRot = b.t.rotation;
            m_Drag = Drag.Gizmo;
            if (h == GizmoHandle.X || h == GizmoHandle.Y || h == GizmoHandle.Z)
            {
                m_Axis = PoseOverlay.AxisOf(b, h, ctx.view.gizmoLocal, pr);
                m_Tangent = m_PoseOverlay.RingTangent(b, pr, m_Axis, m_Mouse, out _);
            }
            else if (h == GizmoHandle.View)
            {
                m_Axis = pr.Forward;
            }
        }

        void DragGizmo(Vector2 delta)
        {
            var b = ctx.session.pose.selected;
            if (b == null) return;
            var pr = MakeProjector();
            var total = m_Mouse - m_DragStart;
            switch (m_ActiveHandle)
            {
                case GizmoHandle.X:
                case GizmoHandle.Y:
                case GizmoHandle.Z:
                {
                    float angle = Vector2.Dot(total, m_Tangent) / PoseOverlay.GizmoRadius * Mathf.Rad2Deg;
                    if (m_Snap) angle = Mathf.Round(angle / 5f) * 5f;
                    b.t.rotation = Quaternion.AngleAxis(angle, m_Axis) * m_StartRot;
                    break;
                }
                case GizmoHandle.View:
                {
                    pr.ToLocal(b.Head, out var c, out _);
                    float a0 = Mathf.Atan2(-(m_DragStart.y - c.y), m_DragStart.x - c.x);
                    float a1 = Mathf.Atan2(-(m_Mouse.y - c.y), m_Mouse.x - c.x);
                    float angle = Mathf.DeltaAngle(a0 * Mathf.Rad2Deg, a1 * Mathf.Rad2Deg);
                    b.t.rotation = Quaternion.AngleAxis(angle, m_Axis) * m_StartRot;
                    break;
                }
                case GizmoHandle.Trackball:
                {
                    var q = Quaternion.AngleAxis(-total.x * 0.5f, pr.Up) * Quaternion.AngleAxis(-total.y * 0.5f, pr.Right);
                    b.t.rotation = q * m_StartRot;
                    break;
                }
            }
            ctx.session.pose.NotifyChanged();
            RenderNow();
            ctx.RequestRender2D?.Invoke();
        }

        void DragBoneMove()
        {
            var b = ctx.session.pose.selected;
            if (b == null) return;
            var pr = MakeProjector();
            pr.ToLocal(m_StartPos, out _, out float depth);
            float wpp = pr.WorldPerPoint(depth);
            var d = m_Mouse - m_DragStart;
            b.t.position = m_StartPos + (pr.Right * d.x - pr.Up * d.y) * wpp;
            ctx.session.pose.NotifyChanged();
            RenderNow();
            ctx.RequestRender2D?.Invoke();
        }

        void CommitPose(string name)
        {
            var pose = ctx.session.pose;
            if (m_PoseBefore == null) return;
            var before = m_PoseBefore;
            var after = pose.Capture();
            m_PoseBefore = null;
            ctx.session.Push(new ActionEntry(name, () => pose.Restore(before), () => pose.Restore(after)));
        }

        // ------------------------------------------------------------------ Blender-style modal R / G

        public void StartModal(bool rotate)
        {
            var pose = ctx.session?.pose;
            if (pose == null || pose.selected == null) return;
            pose.FinishTransition();
            CancelModal();
            m_Modal = rotate ? Modal.Rotate : Modal.Grab;
            m_ModalAxis = -1;
            m_ModalLocal = false;
            m_ModalStart = m_Mouse;
            m_PoseBefore = pose.Capture();
            m_StartRot = pose.selected.t.rotation;
            m_StartPos = pose.selected.t.position;
            UpdateHud();
            RepaintOverlay();
        }

        public void ModalAxis(int axis)
        {
            if (m_Modal == Modal.None) return;
            if (m_ModalAxis == axis && !m_ModalLocal) m_ModalLocal = true;
            else if (m_ModalAxis == axis && m_ModalLocal) { m_ModalAxis = -1; m_ModalLocal = false; }
            else { m_ModalAxis = axis; m_ModalLocal = false; }
            UpdateModal();
        }

        Vector3 ModalAxisVector(Bone b)
        {
            Transform frame = m_ModalLocal ? b.t : null;
            Vector3 v = m_ModalAxis == 0 ? Vector3.right : m_ModalAxis == 1 ? Vector3.up : Vector3.forward;
            if (frame == null) return v;
            // Local axes of the bone at the start of the operation.
            return m_StartRot * v;
        }

        void UpdateModal()
        {
            var b = ctx.session.pose.selected;
            if (b == null) { CancelModal(); return; }
            var pr = MakeProjector();
            if (m_Modal == Modal.Rotate)
            {
                pr.ToLocal(m_StartPos, out var c, out _);
                float a0 = Mathf.Atan2(-(m_ModalStart.y - c.y), m_ModalStart.x - c.x);
                float a1 = Mathf.Atan2(-(m_Mouse.y - c.y), m_Mouse.x - c.x);
                float angle = Mathf.DeltaAngle(a0 * Mathf.Rad2Deg, a1 * Mathf.Rad2Deg);
                Vector3 axis = pr.Forward;
                if (m_ModalAxis >= 0)
                {
                    axis = ModalAxisVector(b);
                    if (Vector3.Dot(axis, pr.Forward) < 0f) angle = -angle;
                }
                b.t.rotation = Quaternion.AngleAxis(angle, axis) * m_StartRot;
            }
            else if (m_Modal == Modal.Grab)
            {
                pr.ToLocal(m_StartPos, out _, out float depth);
                float wpp = pr.WorldPerPoint(depth);
                var d = m_Mouse - m_ModalStart;
                var move = (pr.Right * d.x - pr.Up * d.y) * wpp;
                if (m_ModalAxis >= 0)
                {
                    var axis = ModalAxisVector(b).normalized;
                    move = axis * Vector3.Dot(move, axis) * 1.5f;
                }
                b.t.position = m_StartPos + move;
            }
            ctx.session.pose.NotifyChanged();
            UpdateHud();
            RenderNow();
            ctx.RequestRender2D?.Invoke();
            RepaintOverlay();
        }

        public void ConfirmModal()
        {
            if (m_Modal == Modal.None) return;
            var name = m_Modal == Modal.Rotate ? "Rotate bone" : "Move bone";
            m_Modal = Modal.None;
            CommitPose(name);
            UpdateHud();
            RepaintOverlay();
        }

        public void CancelModal()
        {
            if (m_Modal == Modal.None) return;
            m_Modal = Modal.None;
            if (m_PoseBefore != null) ctx.session.pose.Restore(m_PoseBefore);
            m_PoseBefore = null;
            UpdateHud();
            ctx.Render();
            RepaintOverlay();
        }

        // ================================================================== overlay & HUD

        readonly Vector2[] m_Circle = new Vector2[65];

        protected override void DrawOverlay(Painter2D p)
        {
            var s = ctx.session;
            if (s == null) return;
            var pr = MakeProjector();

            if (!ctx.PoseMode && ctx.Tool == PaintTool.Image) DrawImageOutline(p, pr);

            if (ctx.view.showArmature || ctx.PoseMode)
                m_PoseOverlay.DrawBones(p, s.pose, pr, ctx.PoseMode, m_HoverBone, ctx.view.boneSize, ctx.view.humanoidOnly);

            if (ctx.PoseMode)
            {
                if (s.pose.selected != null && ctx.poseTool == PoseTool.Rotate && m_Modal == Modal.None)
                    m_PoseOverlay.DrawGizmo(p, s.pose.selected, pr, ctx.view.gizmoLocal, m_HoverHandle, m_ActiveHandle);
                if (m_Modal != Modal.None && s.pose.selected != null)
                {
                    pr.ToLocal(m_StartPos, out var c, out _);
                    Line(p, c, m_Mouse, new Color(1f, 1f, 1f, 0.6f), 1f);
                    if (m_ModalAxis >= 0)
                    {
                        var axis = ModalAxisVector(s.pose.selected).normalized;
                        var a = pr.ToLocal(m_StartPos - axis * 2f);
                        var b = pr.ToLocal(m_StartPos + axis * 2f);
                        var col = m_ModalAxis == 0 ? new Color(1f, 0.35f, 0.35f) : m_ModalAxis == 1 ? new Color(0.45f, 0.9f, 0.35f) : new Color(0.35f, 0.6f, 1f);
                        Line(p, a, b, col, 1.5f);
                    }
                }
                return;
            }

            if (m_Drag == Drag.Select)
            {
                if (ctx.selectLasso && m_SelPoints.Count > 1)
                {
                    var pts = new List<Vector2>(m_SelPoints) { m_Mouse }.ToArray();
                    Polyline(p, pts, pts.Length, true, new Color(0, 0, 0, 0.6f), 3f);
                    Polyline(p, pts, pts.Length, true, new Color(0.45f, 0.75f, 1f, 1f), 1.25f);
                }
                else if (!ctx.selectLasso)
                {
                    var box = new[] { m_DragStart, new Vector2(m_Mouse.x, m_DragStart.y), m_Mouse, new Vector2(m_DragStart.x, m_Mouse.y) };
                    p.fillColor = new Color(0.35f, 0.6f, 1f, 0.12f);
                    p.BeginPath();
                    p.MoveTo(box[0]); p.LineTo(box[1]); p.LineTo(box[2]); p.LineTo(box[3]); p.ClosePath();
                    p.Fill();
                    Polyline(p, box, 4, true, new Color(0.45f, 0.75f, 1f, 1f), 1.25f);
                }
                return;
            }

            bool navigating = m_Drag == Drag.Orbit || m_Drag == Drag.Pan || m_Drag == Drag.Zoom;
            if (!m_Inside && m_Drag != Drag.Paint && m_Drag != Drag.BrushSize) return;
            if (navigating) return;

            if (ctx.Tool == PaintTool.Brush || ctx.Tool == PaintTool.Eraser || m_Drag == Drag.BrushSize)
            {
                if (m_HasHit)
                {
                    DrawSurfaceRing(p, pr, m_Hit.point, m_Hit.normal, ctx.brush.radius, 1f);
                    if (ctx.brush.symmetry)
                    {
                        var root = s.clone.transform;
                        int axis = Mathf.Clamp(ctx.brush.symmetryAxis, 0, 2);
                        var l = root.InverseTransformPoint(m_Hit.point);
                        l[axis] = -l[axis];
                        var n = root.InverseTransformDirection(m_Hit.normal);
                        n[axis] = -n[axis];
                        DrawSurfaceRing(p, pr, root.TransformPoint(l), root.TransformDirection(n), ctx.brush.radius, 0.45f);
                    }
                }
                else
                {
                    float rp = RadiusPoints(ctx.brush.radius);
                    Circle(p, m_Mouse, rp, new Color(0, 0, 0, 0.4f), 3f);
                    Circle(p, m_Mouse, rp, new Color(1, 1, 1, 0.55f), 1f);
                }
                Disc(p, m_HasHit ? pr.ToLocal(m_Hit.point) : m_Mouse, 1.5f, Color.white);
                if (m_Drag == Drag.Paint && ctx.brush.stabilizer > 0f)
                {
                    Line(p, m_Sampler.Position, m_Mouse, new Color(1f, 1f, 1f, 0.4f), 1f);
                    Disc(p, m_Sampler.Position, 2.5f, new Color(1f, 0.85f, 0.4f, 0.9f));
                }
                if (m_Drag == Drag.BrushSize)
                {
                    float rp = RadiusPoints(ctx.brush.radius);
                    float inner = rp * Mathf.Clamp01(ctx.brush.hardness);
                    Circle(p, m_DragStart, inner, new Color(1f, 0.85f, 0.4f, 0.8f), 1f);
                }
            }
            else if (ctx.Tool == PaintTool.Fill || ctx.Tool == PaintTool.Picker || ctx.Tool == PaintTool.Select)
            {
                var c = m_Mouse;
                var col = Color.white;
                Line(p, c + new Vector2(-9, 0), c + new Vector2(-3, 0), col, 1.5f);
                Line(p, c + new Vector2(3, 0), c + new Vector2(9, 0), col, 1.5f);
                Line(p, c + new Vector2(0, -9), c + new Vector2(0, -3), col, 1.5f);
                Line(p, c + new Vector2(0, 3), c + new Vector2(0, 9), col, 1.5f);
                if (ctx.Tool == PaintTool.Picker)
                {
                    Disc(p, c + new Vector2(14, 14), 7f, new Color(0, 0, 0, 0.6f));
                    Disc(p, c + new Vector2(14, 14), 6f, ctx.primary);
                }
            }
        }

        void DrawSurfaceRing(Painter2D p, Projector pr, Vector3 center, Vector3 normal, float radius, float alpha)
        {
            var n = normal.sqrMagnitude > 1e-8f ? normal.normalized : Vector3.up;
            var u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(n, u);
            int count = 64;
            for (int i = 0; i <= count; i++)
            {
                float a = i / (float)count * Mathf.PI * 2f;
                m_Circle[i] = pr.ToLocal(center + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius + n * radius * 0.02f);
            }
            CursorRing(p, m_Circle, count + 1, alpha);
        }

        void DrawImageOutline(Painter2D p, Projector pr)
        {
            var set = ctx.session.ActiveSet;
            var layer = set?.ActiveLayer;
            if (layer == null || layer.kind != LayerKind.Image || layer.placement == null || !layer.placement.projected) return;
            var pl = layer.placement;
            var n = pl.normal.normalized;
            var up = Vector3.ProjectOnPlane(pl.up, n).normalized;
            up = Quaternion.AngleAxis(-pl.rotation, n) * up;
            var right = Vector3.Cross(n, up).normalized;
            float hw = pl.worldWidth * 0.5f, hh = hw * pl.Aspect;
            var pts = new[]
            {
                pr.ToLocal(pl.worldPos - right * hw - up * hh),
                pr.ToLocal(pl.worldPos + right * hw - up * hh),
                pr.ToLocal(pl.worldPos + right * hw + up * hh),
                pr.ToLocal(pl.worldPos - right * hw + up * hh),
            };
            Polyline(p, pts, 4, true, new Color(0, 0, 0, 0.5f), 3f);
            Polyline(p, pts, 4, true, new Color(0.35f, 0.8f, 1f, 0.95f), 1.25f);
            Line(p, pr.ToLocal(pl.worldPos), pr.ToLocal(pl.worldPos + up * hh * 1.25f), new Color(0.35f, 0.8f, 1f, 0.95f), 1.25f);
        }

        public void UpdateHud()
        {
            try { UpdateHudText(); }
            finally { SyncHud(); }
        }

        void UpdateHudText()
        {
            var s = ctx.session;
            if (s == null) { hudLeft.text = hudRight.text = hudCenter.text = ""; return; }
            if (ctx.PoseMode)
            {
                var sel = s.pose.selected;
                hudLeft.text = "POSE MODE" + (sel != null ? "  ·  " + sel.Name : "  ·  click a bone");
                hudRight.text = ctx.poseTool == PoseTool.Rotate ? "Rotate (R)" : "Move (G)";
                if (m_Modal == Modal.Rotate || m_Modal == Modal.Grab)
                {
                    string axis = m_ModalAxis < 0 ? "view" : (m_ModalLocal ? "local " : "global ") + "XYZ"[m_ModalAxis];
                    hudCenter.text = (m_Modal == Modal.Rotate ? "Rotate" : "Move") + " along " + axis +
                                     "   ·   X / Y / Z constrain   ·   click or Enter to confirm   ·   right-click or Esc to cancel";
                }
                else hudCenter.text = "";
                return;
            }
            hudCenter.text = "";
            var set = s.ActiveSet;
            string target = set == null ? "No material" : set.displayName;
            if (set != null && set.ActiveLayer != null)
                target += "  ›  " + set.ActiveLayer.name + (set.editingMask ? " (mask)" : "");
            if (set != null && set.HasSelection) target += "   ·   painting inside " + set.SelectedCount + " selected polygons";
            hudLeft.text = target;
            string size = (ctx.brush.radius * 200f).ToString(ctx.brush.radius < 0.005f ? "0.00" : "0.0") + " cm";
            switch (ctx.Tool)
            {
                case PaintTool.Brush: hudRight.text = "Brush  " + size + "  ·  " + Mathf.RoundToInt(ctx.brush.opacity * 100) + "%"; break;
                case PaintTool.Eraser: hudRight.text = "Eraser  " + size; break;
                case PaintTool.Fill: hudRight.text = "Fill  ·  " + ObjectNames.NicifyVariableName(ctx.fillMode.ToString()); break;
                case PaintTool.Picker: hudRight.text = "Color picker"; break;
                case PaintTool.Image: hudRight.text = "Image  ·  drag to move, Shift scale, Ctrl rotate"; break;
                case PaintTool.Select: hudRight.text = "Select " + (ctx.selectIslands ? "UV islands" : "polygons") + "  ·  Shift add, Ctrl remove"; break;
            }
        }
    }
}
