using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AviCreatorEssentials
{
    /// <summary>The 2D texture view: pan/zoom, UV wireframe, painting directly on the texture, image placement.</summary>
    public class Viewport2D : ViewportBase
    {
        enum Drag
        {
            None,
            Pan,
            Paint,
            BrushSize,
            Picker,
            ImageMove,
            ImageScale,
            ImageRotate,
            Select,
            Transform,
        }

        Vector2 m_Center = new Vector2(0.5f, 0.5f);
        float m_Scale = 400f;
        bool m_Fitted;
        TextureSet m_FittedSet;

        Drag m_Drag;
        int m_PointerId = -1;
        Vector2 m_Mouse;
        Vector2 m_DragStart;
        Vector2 m_DragLast;
        bool m_Inside;
        float m_StartRadius;
        float m_StartHardness;

        StrokeContext m_Stroke;
        TextureSet m_StrokeSet;
        readonly StrokeSampler m_Sampler = new StrokeSampler();
        readonly System.Random m_Rng = new System.Random();
        readonly List<Dab2D> m_Dabs = new List<Dab2D>();
        readonly StrokeTaper<Dab2D> m_Taper = new StrokeTaper<Dab2D>();
        readonly List<Dab2D> m_ScaledDabs = new List<Dab2D>();

        // image drag
        Vector2 m_ImgStartCenter;
        Vector2 m_ImgStartSize;
        float m_ImgStartRot;
        float m_ImgStartDist;
        float m_ImgStartAngle;

        public bool MouseInside => m_Inside;
        readonly List<Vector2> m_SelPoints = new List<Vector2>();
        SelectOp m_SelOp;

        public Viewport2D(PainterContext context) : base(context)
        {
            AddToClassList("ap-viewport-2d");
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag());
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<PointerEnterEvent>(_ => { m_Inside = true; RepaintOverlay(); });
            RegisterCallback<PointerLeaveEvent>(_ => { m_Inside = false; RepaintOverlay(); UpdateHud(); });
        }

        TextureSet Set => ctx.session?.ActiveSet;

        float TexW
        {
            get
            {
                var s = Set;
                if (s == null) return 1024f;
                return s.width;
            }
        }

        float TexH
        {
            get
            {
                var s = Set;
                if (s == null) return 1024f;
                return s.height;
            }
        }

        float SX => m_Scale;
        float SY => m_Scale * TexH / Mathf.Max(1f, TexW);

        Vector2 UVToLocal(Vector2 uv) => new Vector2((uv.x - m_Center.x) * SX + W * 0.5f, H * 0.5f - (uv.y - m_Center.y) * SY);
        Vector2 LocalToUV(Vector2 l) => new Vector2((l.x - W * 0.5f) / SX + m_Center.x, (H * 0.5f - l.y) / SY + m_Center.y);
        Vector2 UVToTexel(Vector2 uv) => new Vector2(uv.x * TexW, uv.y * TexH);
        Vector2 TexelToUV(Vector2 t) => new Vector2(t.x / TexW, t.y / TexH);

        public void Fit()
        {
            if (W < 4 || H < 4) return;
            float aspect = TexH / Mathf.Max(1f, TexW);
            m_Scale = Mathf.Min(W, H / aspect) * 0.92f;
            m_Center = new Vector2(0.5f, 0.5f);
            m_Fitted = true;
            m_FittedSet = Set;
            RequestRender();
            RepaintOverlay();
        }

        public void ZoomToActualPixels()
        {
            m_Scale = TexW / PPP;
            RequestRender();
            RepaintOverlay();
        }

        // ================================================================== rendering

        protected override void DoRender(RenderTexture target)
        {
            var s = ctx.session;
            if (!m_Fitted || m_FittedSet != Set) Fit();
            s.UpdateComposites();
            var set = Set;
            float xyX = W / SX, xyY = H / SY;
            float zwX = m_Center.x - W * 0.5f / SX, zwY = m_Center.y - H * 0.5f / SY;
            var p = new View2DParams
            {
                uvMap = new Vector4(xyX, xyY, zwX, zwY),
                showAlpha = ctx.view.showAlpha2D,
                pointSample = m_Scale * PPP / Mathf.Max(1f, TexW) > 2f,
                tile = ctx.view.tile2D,
                maskOverlay = set != null && set.editingMask && ctx.view.showMaskOverlay,
                checkerA = ctx.view.LightTheme ? new Color(0.78f, 0.78f, 0.78f) : new Color(0.22f, 0.22f, 0.22f),
                checkerB = ctx.view.LightTheme ? new Color(0.88f, 0.88f, 0.88f) : new Color(0.30f, 0.30f, 0.30f),
                outside = ctx.view.LightTheme ? new Color(0.6f, 0.6f, 0.6f) : new Color(0.12f, 0.12f, 0.12f),
            };
            s.engine.RenderView2D(set, target, p);
            if (set != null && ctx.view.showWire2D)
            {
                var t = new Vector4(1f / xyX, 1f / xyY, -zwX / xyX, -zwY / xyY);
                s.engine.RenderWire(set, target, t, new Color(1f, 1f, 1f, 0.22f));
            }
        }

        // ================================================================== input

        void OnPointerDown(PointerDownEvent evt)
        {
            if (ctx.session == null || Set == null) return;
            Focus();
            m_Mouse = evt.localPosition;
            if (m_Drag != Drag.None) return;
            m_DragStart = m_DragLast = m_Mouse;
            bool alt = evt.altKey, ctrl = evt.actionKey, shift = evt.shiftKey;

            if (evt.button == 2 || (evt.button == 0 && alt) || (evt.button == 1 && !ctrl))
            {
                m_Drag = Drag.Pan;
                rightClickPending = evt.button == 1 && !alt && !shift && !ctx.PoseMode;
            }
            else if (evt.button == 1 && ctrl)
            {
                m_Drag = Drag.BrushSize;
                m_StartRadius = ctx.brush.radius2D;
                m_StartHardness = ctx.brush.hardness;
            }
            else if (evt.button == 0 && !ctx.PoseMode)
            {
                switch (ctx.Tool)
                {
                    case PaintTool.Brush:
                    case PaintTool.Eraser:
                        BeginStroke(PressureOf(evt));
                        break;
                    case PaintTool.Fill:
                        FillAt(LocalToUV(m_Mouse), shift);
                        break;
                    case PaintTool.Picker:
                        Pick(shift);
                        m_Drag = Drag.Picker;
                        break;
                    case PaintTool.Image:
                        BeginImageDrag();
                        break;
                    case PaintTool.Transform:
                        BeginTransform(shift);
                        break;
                    case PaintTool.Gradient:
                        ctx.ShowHint("The gradient tool works on the model: drag across it in the 3D view.");
                        break;
                    case PaintTool.Select:
                        m_Drag = Drag.Select;
                        m_SelOp = shift ? SelectOp.Add : ctrl ? SelectOp.Subtract : SelectOp.Replace;
                        m_SelPoints.Clear();
                        m_SelPoints.Add(m_Mouse);
                        break;
                }
            }
            if (m_Drag != Drag.None)
            {
                m_PointerId = evt.pointerId;
                this.CapturePointer(evt.pointerId);
            }
            evt.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (ctx.session == null) return;
            m_Mouse = evt.localPosition;
            if (rightClickPending)
            {
                if (Vector2.Distance(m_Mouse, m_DragStart) < ClickSlop) return;
                rightClickPending = false;
            }
            var delta = m_Mouse - m_DragLast;
            switch (m_Drag)
            {
                case Drag.Pan:
                    m_Center -= new Vector2(delta.x / SX, -delta.y / SY);
                    RequestRender();
                    break;
                case Drag.Paint:
                    ContinueStroke(PressureOf(evt));
                    break;
                case Drag.BrushSize:
                {
                    var d = m_Mouse - m_DragStart;
                    ctx.brush.radius2D = Mathf.Clamp(m_StartRadius * Mathf.Exp(d.x * 0.01f), 0.0002f, 0.5f);
                    ctx.brush.hardness = Mathf.Clamp01(m_StartHardness - d.y * 0.005f);
                    ctx.NotifyBrushChanged();
                    break;
                }
                case Drag.Picker:
                    Pick(evt.shiftKey);
                    break;
                case Drag.ImageMove:
                case Drag.ImageScale:
                case Drag.ImageRotate:
                    DragImage();
                    break;
                case Drag.Transform:
                    DragTransform(evt.shiftKey);
                    break;
                case Drag.Select:
                    if (ctx.selectLasso)
                    {
                        if (Vector2.Distance(m_SelPoints[m_SelPoints.Count - 1], m_Mouse) > 3f) m_SelPoints.Add(m_Mouse);
                    }
                    break;
            }
            m_DragLast = m_Mouse;
            RepaintOverlay();
            UpdateHud();
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerId != m_PointerId) return;
            bool click = rightClickPending;
            EndDrag();
            if (this.HasPointerCapture(evt.pointerId)) this.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
            if (click) OpenQuickColor(evt.localPosition);
        }

        void EndDrag()
        {
            if (m_Drag == Drag.Paint) EndStroke();
            else if (m_Drag == Drag.Picker) ctx.colors.Use(ctx.primary);
            else if (m_Drag == Drag.BrushSize) ctx.NotifyBrushChanged();
            else if (m_Drag == Drag.Select) FinishSelect();
            else if (m_Drag == Drag.Transform) EndTransform();
            m_Drag = Drag.None;
            m_PointerId = -1;
            rightClickPending = false;
            RepaintOverlay();
        }

        void OnWheel(WheelEvent evt)
        {
            if (ctx.session == null) return;
            float d = evt.delta.y;
            if (evt.actionKey)
            {
                ctx.brush.radius2D = Mathf.Clamp(ctx.brush.radius2D * (d > 0 ? 1.1f : 0.9f), 0.0002f, 0.5f);
                ctx.NotifyBrushChanged();
            }
            else
            {
                var before = LocalToUV(evt.localMousePosition);
                m_Scale = Mathf.Clamp(m_Scale * (d > 0 ? 1f / 1.15f : 1.15f), 20f, 200000f);
                var after = LocalToUV(evt.localMousePosition);
                m_Center += before - after;
                RequestRender();
            }
            RepaintOverlay();
            UpdateHud();
            evt.StopPropagation();
        }

        // ================================================================== painting

        void BeginStroke(float pressure)
        {
            var s = ctx.session;
            var set = Set;
            if (!set.initialized) s.InitializeSet(set);
            if (set.islandDirty) s.engine.BuildIsland(set);
            var tool = ctx.Tool == PaintTool.Eraser ? StrokeTool.Erase : StrokeTool.Paint;
            // Other selected textures of the same material share its UV layout, so they take the same strokes.
            var targets = new List<TextureSet> { set };
            foreach (var other in s.SelectedSets)
            {
                if (other == set || other.originalMaterial != set.originalMaterial || other.uvChannel != set.uvChannel) continue;
                if (!other.initialized) s.InitializeSet(other);
                if (other.islandDirty) s.engine.BuildIsland(other);
                targets.Add(other);
            }
            m_Stroke = s.engine.BeginStroke(targets, tool, ctx.primary, ctx.brush.opacity,
                tool == StrokeTool.Erase ? "Erase" : "Paint", Matrix4x4.identity, false);
            m_Stroke.paintMode = ctx.brush.paintMode;
            m_StrokeSet = set;
            if (tool == StrokeTool.Paint && !set.editingMask) ctx.colors.Use(ctx.primary);
            var t = UVToTexel(LocalToUV(m_Mouse));
            m_Sampler.Begin(t, pressure);
            m_Taper.Begin(ctx.brush);
            m_Drag = Drag.Paint;
            ctx.strokeActive = true;
            m_Dabs.Clear();
            Emit(t, pressure, Vector2.right);
            Flush();
        }

        void ContinueStroke(float pressure)
        {
            if (m_Stroke == null) return;
            var t = UVToTexel(LocalToUV(m_Mouse));
            m_Sampler.Advance(t, pressure, StrokeStep, ctx.brush, 1f / Mathf.Max(1e-4f, SX / TexW), Emit);
            Flush();
        }

        float StrokeStep => Mathf.Max(0.5f, ctx.brush.spacing * 2f * ctx.brush.radius2D * TexW);

        void Emit(Vector2 texel, float pressure, Vector2 dir)
        {
            var b = ctx.brush;
            float sizeK = (b.pressureSize ? Mathf.Lerp(0.15f, 1f, pressure) : 1f) * (1f - b.sizeJitter * (float)m_Rng.NextDouble());
            sizeK *= m_Taper.Next();
            float r = b.radius2D * TexW * sizeK;
            if (b.scatter > 0f)
            {
                float a = (float)m_Rng.NextDouble() * Mathf.PI * 2f;
                float rr = Mathf.Sqrt((float)m_Rng.NextDouble()) * b.scatter * 2f * r;
                texel += new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr;
            }
            // texel space has v pointing up, screen y points down
            float angle = b.angle + (b.angleJitter > 0f ? RandomRange(m_Rng, -b.angleJitter, b.angleJitter) : 0f);
            if (ctx.FollowStroke) angle += Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            float strength = b.flow * (b.pressureOpacity ? pressure : 1f) * (1f - b.opacityJitter * (float)m_Rng.NextDouble());
            var across = dir.sqrMagnitude > 1e-10f ? new Vector2(-dir.y, dir.x).normalized : Vector2.up;
            m_Taper.Add(new Dab2D { uv = TexelToUV(texel), radiusPx = r, angle = angle * Mathf.Deg2Rad, strength = strength, across = across }, m_Dabs);
            if (b.symmetry) EmitMirrored(texel, r, angle * Mathf.Deg2Rad, strength, across);
        }

        /// <summary>
        /// Symmetry in the texture view: the dab is repeated where the mirrored point of the model sits on the texture,
        /// just like the 3D brush. Size and angle follow the other side's texture layout.
        /// </summary>
        void EmitMirrored(Vector2 texel, float r, float angle, float strength, Vector2 across)
        {
            var s = ctx.session;
            var set = m_StrokeSet ?? Set;
            int axis = ctx.brush.symmetryAxis;
            if (!s.MirrorUV(set, axis, TexelToUV(texel), out var muv)) return;
            float eps = Mathf.Max(1f, r * 0.5f);
            var side = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * eps;
            float mr = r, mAngle = angle;
            if (s.MirrorUV(set, axis, TexelToUV(texel + side), out var muv2))
            {
                var d = UVToTexel(muv2) - UVToTexel(muv);
                float len = d.magnitude;
                if (len > 1e-4f && len < eps * 8f)
                {
                    mr = r * Mathf.Clamp(len / eps, 0.25f, 4f);
                    mAngle = Mathf.Atan2(d.y, d.x);
                }
            }
            if ((UVToTexel(muv) - texel).sqrMagnitude < 0.25f) return;
            // The bristle direction turns the same way the dab does on the other side.
            float turn = mAngle - angle;
            float c = Mathf.Cos(turn), sn = Mathf.Sin(turn);
            var mAcross = new Vector2(across.x * c - across.y * sn, across.x * sn + across.y * c);
            m_Taper.Add(new Dab2D { uv = muv, radiusPx = mr, angle = mAngle, strength = strength, across = mAcross }, m_Dabs);
        }

        void Flush()
        {
            if (m_Stroke == null) return;
            m_Taper.Release(m_Dabs);
            if (m_Dabs.Count == 0) return;
            foreach (var t in m_Stroke.targets)
            {
                if (t.set == m_StrokeSet)
                {
                    ctx.session.engine.Dabs2D(m_Stroke, t.set, m_Dabs, ctx.Tip, ctx.brush);
                    continue;
                }
                // Same UV layout, maybe another resolution: keep the brush the same size on the texture.
                float k = t.set.width / (float)Mathf.Max(1, m_StrokeSet.width);
                m_ScaledDabs.Clear();
                foreach (var d in m_Dabs)
                {
                    var c = d;
                    c.radiusPx *= k;
                    m_ScaledDabs.Add(c);
                }
                ctx.session.engine.Dabs2D(m_Stroke, t.set, m_ScaledDabs, ctx.Tip, ctx.brush);
            }
            m_Dabs.Clear();
            ctx.session.engine.Apply(m_Stroke);
            ctx.Render();
        }

        void EndStroke()
        {
            if (m_Stroke == null) return;
            if (ctx.brush.stabilizer > 0f && ctx.brush.stabilizerCatchUp)
            {
                m_Sampler.Finish(StrokeStep, Emit);
                Flush();
            }
            m_Taper.Finish(m_Dabs, (d, k) => { d.radiusPx *= k; return d; });
            Flush();
            var entry = ctx.session.engine.EndStroke(m_Stroke);
            if (entry != null) ctx.session.Push(entry);
            m_Stroke = null;
            ctx.strokeActive = false;
            ctx.session.NotifyStructureChanged();
            ctx.Render();
        }

        public void CancelStroke()
        {
            m_Taper.Clear();
            m_Dabs.Clear();
            if (m_Stroke == null) return;
            ctx.session.engine.CancelStroke(m_Stroke);
            m_Stroke = null;
            ctx.strokeActive = false;
            m_Drag = Drag.None;
            ctx.Render();
        }

        // ================================================================== fill & pick

        bool FindTriangle(TextureSet set, Vector2 uv, out RendererEntry entry, out int submesh, out int first)
        {
            entry = null;
            submesh = 0;
            first = -1;
            var s = ctx.session;
            uv.x -= Mathf.Floor(uv.x);
            uv.y -= Mathf.Floor(uv.y);
            foreach (var u in set.users)
            {
                var e = s.meshes.entries[u.renderer];
                if (u.submesh >= e.SubmeshCount) continue;
                var uvs = e.GetUVs(set.uvChannel);
                var idx = e.submeshIndices[u.submesh];
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    var a = uvs[idx[i]];
                    var b = uvs[idx[i + 1]];
                    var c = uvs[idx[i + 2]];
                    if (InTriangle(uv, a, b, c))
                    {
                        entry = e;
                        submesh = u.submesh;
                        first = i;
                        return true;
                    }
                }
            }
            return false;
        }

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        void FillAt(Vector2 uv, bool erase)
        {
            var s = ctx.session;
            var set = Set;
            if (!set.initialized) s.InitializeSet(set);
            var stroke = s.engine.BeginStroke(new[] { set }, erase ? StrokeTool.Erase : StrokeTool.Paint, ctx.primary, 1f,
                "Fill", Matrix4x4.identity, false);
            switch (ctx.fillMode)
            {
                case FillMode.UVIsland:
                case FillMode.MeshPiece:
                    if (FindTriangle(set, uv, out var e, out int sub, out int first))
                    {
                        var mode = ctx.fillMode == FillMode.UVIsland ? MeshCache.IslandMode.UV : MeshCache.IslandMode.Mesh;
                        var islands = s.meshes.GetIslands(e, sub, set.uvChannel, mode);
                        int id = islands[first / 3];
                        var tris = new List<int>();
                        for (int t = 0; t < islands.Length; t++)
                            if (islands[t] == id) tris.Add(t * 3);
                        s.engine.CoverTriangles(stroke, set, e, sub, tris);
                    }
                    else
                    {
                        ctx.ShowHint("No UV island under the cursor.");
                    }
                    break;
                case FillMode.Material:
                    s.engine.CoverIslands(stroke, set);
                    break;
                case FillMode.WholeTexture:
                    s.engine.CoverAll(stroke, set);
                    break;
                case FillMode.SimilarColor:
                {
                    var mask = s.engine.FloodSelect(set, uv, ctx.fillTolerance, ctx.fillContiguous, ctx.fillSampleLayer);
                    s.engine.CoverFromTexture(stroke, set, mask);
                    if (mask != null) Object.DestroyImmediate(mask);
                    break;
                }
            }
            var entry = s.engine.EndStroke(stroke);
            if (entry != null) s.Push(entry);
            if (!erase && !set.editingMask) ctx.colors.Use(ctx.primary);
            s.NotifyStructureChanged();
            ctx.Render();
        }

        void Pick(bool secondary)
        {
            var set = Set;
            if (set == null) return;
            if (!set.initialized) ctx.session.InitializeSet(set);
            var c = ctx.session.engine.SampleComposite(set, LocalToUV(m_Mouse));
            c.a = 1f;
            if (secondary) ctx.SetSecondary(c);
            else ctx.SetPrimary(c);
        }

        // ================================================================== UV selection

        void FinishSelect()
        {
            var s = ctx.session;
            var set = Set;
            if (s == null || set == null) return;
            if (!set.initialized) s.InitializeSet(set);
            if (Vector2.Distance(m_DragStart, m_Mouse) < 4f)
            {
                if (FindTriangle(set, LocalToUV(m_Mouse), out var e, out int sub, out int first))
                    s.Select(set, new[] { new TriRef(e.index, sub, first) }, m_SelOp, ctx.selectIslands);
                else if (m_SelOp == SelectOp.Replace)
                    s.ClearSelection(set);
            }
            else
            {
                var poly = new List<Vector2>();
                if (ctx.selectLasso)
                {
                    m_SelPoints.Add(m_Mouse);
                    foreach (var p in m_SelPoints) poly.Add(LocalToUV(p));
                }
                else
                {
                    var a = LocalToUV(m_DragStart);
                    var b = LocalToUV(m_Mouse);
                    poly.Add(new Vector2(a.x, a.y));
                    poly.Add(new Vector2(b.x, a.y));
                    poly.Add(new Vector2(b.x, b.y));
                    poly.Add(new Vector2(a.x, b.y));
                }
                s.Select(set, s.TrianglesInUV(set, poly), m_SelOp, ctx.selectIslands);
            }
            m_SelPoints.Clear();
            ctx.Render();
        }

        // ================================================================== images

        ImagePlacement ActiveUVImage(out PaintLayer layer)
        {
            layer = Set?.ActiveLayer;
            if (layer == null || layer.kind != LayerKind.Image || layer.placement == null || layer.placement.projected || layer.placement.wrap) return null;
            return layer.placement;
        }

        void ImageCorners(ImagePlacement p, Vector2[] outLocal)
        {
            var c = UVToTexel(p.uvCenter);
            float hx = p.uvSize.x * 0.5f * TexW, hy = p.uvSize.y * 0.5f * TexH;
            float rad = p.rotation * Mathf.Deg2Rad;
            float cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
            var o = new[] { new Vector2(-hx, -hy), new Vector2(hx, -hy), new Vector2(hx, hy), new Vector2(-hx, hy) };
            for (int i = 0; i < 4; i++)
            {
                var r = new Vector2(o[i].x * cs - o[i].y * sn, o[i].x * sn + o[i].y * cs);
                outLocal[i] = UVToLocal(TexelToUV(c + r));
            }
        }

        Vector2 RotateHandle(ImagePlacement p)
        {
            var corners = new Vector2[4];
            ImageCorners(p, corners);
            var top = (corners[2] + corners[3]) * 0.5f;
            var center = UVToLocal(p.uvCenter);
            var dir = (top - center);
            if (dir.sqrMagnitude < 1e-4f) dir = Vector2.down;
            return top + dir.normalized * 22f;
        }

        void BeginImageDrag()
        {
            var p = ActiveUVImage(out var layer);
            if (p == null)
            {
                ctx.ShowHint(Set?.ActiveLayer != null && Set.ActiveLayer.kind == LayerKind.Image
                    ? (Set.ActiveLayer.placement != null && Set.ActiveLayer.placement.wrap
                        ? "This image is wrapped around the model. Slide, scale and rotate it in the 3D view or the Image panel."
                        : "This image is projected onto the model. Move it in the 3D view, or switch it to texture placement in the Image panel.")
                    : "Select an image layer, or use Import Image in the Layers panel.");
                return;
            }
            m_ImgStartCenter = p.uvCenter;
            m_ImgStartSize = p.uvSize;
            m_ImgStartRot = p.rotation;
            var center = UVToLocal(p.uvCenter);
            m_ImgStartDist = Mathf.Max(1f, Vector2.Distance(m_Mouse, center));
            m_ImgStartAngle = Mathf.Atan2(-(m_Mouse.y - center.y), m_Mouse.x - center.x) * Mathf.Rad2Deg;
            if (Vector2.Distance(m_Mouse, RotateHandle(p)) < 9f)
            {
                m_Drag = Drag.ImageRotate;
                return;
            }
            var corners = new Vector2[4];
            ImageCorners(p, corners);
            foreach (var c in corners)
            {
                if (Vector2.Distance(m_Mouse, c) < 9f)
                {
                    m_Drag = Drag.ImageScale;
                    return;
                }
            }
            m_Drag = Drag.ImageMove;
        }

        void DragImage()
        {
            var p = ActiveUVImage(out var layer);
            if (p == null) return;
            var s = ctx.session;
            var center = UVToLocal(m_ImgStartCenter);
            switch (m_Drag)
            {
                case Drag.ImageMove:
                {
                    var d = LocalToUV(m_Mouse) - LocalToUV(m_DragStart);
                    s.EditPlacement(Set, layer, "move", pl => pl.uvCenter = m_ImgStartCenter + d);
                    break;
                }
                case Drag.ImageScale:
                {
                    float k = Vector2.Distance(m_Mouse, center) / m_ImgStartDist;
                    s.EditPlacement(Set, layer, "scale", pl => pl.uvSize = m_ImgStartSize * Mathf.Max(0.01f, k));
                    break;
                }
                case Drag.ImageRotate:
                {
                    float a = Mathf.Atan2(-(m_Mouse.y - center.y), m_Mouse.x - center.x) * Mathf.Rad2Deg;
                    float rot = m_ImgStartRot + Mathf.DeltaAngle(m_ImgStartAngle, a);
                    s.EditPlacement(Set, layer, "rotate", pl => pl.rotation = Mathf.Repeat(rot + 180f, 360f) - 180f);
                    break;
                }
            }
            ctx.Render();
        }

        // ================================================================== transform tool

        enum TfOp { Lasso, Move, Scale, Stretch, Rotate, Warp }
        TfOp m_TfOp;
        int m_TfHandle;                 // corner / edge / warp point index
        ImagePlacement m_TfStart;       // placement when the drag began
        readonly List<Vector2> m_TfPoints = new List<Vector2>();

        static readonly Vector2[] s_Corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
        static readonly Vector2[] s_Edges = { new Vector2(0, -1), new Vector2(1, 0), new Vector2(0, 1), new Vector2(-1, 0) };

        static Vector2 WarpNode(int k) => new Vector2(k % 3 - 1, k / 3 - 1);

        /// <summary>A point of the image's -1..1 box (optionally bent by its warp) in view coordinates.</summary>
        Vector2 BoxToLocal(ImagePlacement p, Vector2 box, bool warped)
        {
            var w = warped ? p.Warp(box) : box;
            var c = UVToTexel(p.uvCenter);
            float hx = p.uvSize.x * 0.5f * TexW, hy = p.uvSize.y * 0.5f * TexH;
            float rad = p.rotation * Mathf.Deg2Rad;
            float cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
            var o = new Vector2(w.x * hx, w.y * hy);
            return UVToLocal(TexelToUV(c + new Vector2(o.x * cs - o.y * sn, o.x * sn + o.y * cs)));
        }

        /// <summary>A view point in the image's (unwarped) -1..1 box coordinates.</summary>
        Vector2 LocalToBox(ImagePlacement p, Vector2 local)
        {
            var d = UVToTexel(LocalToUV(local)) - UVToTexel(p.uvCenter);
            float rad = -p.rotation * Mathf.Deg2Rad;
            float cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
            var r = new Vector2(d.x * cs - d.y * sn, d.x * sn + d.y * cs);
            float hx = Mathf.Max(1e-5f, p.uvSize.x * 0.5f * TexW), hy = Mathf.Max(1e-5f, p.uvSize.y * 0.5f * TexH);
            return new Vector2(r.x / hx, r.y / hy);
        }

        Vector2 TfRotateHandle(ImagePlacement p)
        {
            var top = BoxToLocal(p, new Vector2(0, 1), true);
            var dir = top - BoxToLocal(p, Vector2.zero, true);
            if (dir.sqrMagnitude < 1e-4f) dir = Vector2.down;
            return top + dir.normalized * 22f;
        }

        void BeginTransform(bool shift)
        {
            var s = ctx.session;
            var set = Set;
            var p = ActiveUVImage(out var layer);
            if (p != null)
            {
                m_TfStart = p.Clone();
                if (ctx.transformWarp)
                {
                    for (int k = 0; k < 9; k++)
                        if (Vector2.Distance(m_Mouse, BoxToLocal(p, WarpNode(k), true)) < 9f) { StartTf(TfOp.Warp, k); return; }
                }
                else
                {
                    if (Vector2.Distance(m_Mouse, TfRotateHandle(p)) < 9f) { StartTf(TfOp.Rotate, 0); return; }
                    for (int k = 0; k < 4; k++)
                        if (Vector2.Distance(m_Mouse, BoxToLocal(p, s_Corners[k], true)) < 9f) { StartTf(TfOp.Scale, k); return; }
                    for (int k = 0; k < 4; k++)
                        if (Vector2.Distance(m_Mouse, BoxToLocal(p, s_Edges[k], true)) < 9f) { StartTf(TfOp.Stretch, k); return; }
                }
                var b = LocalToBox(p, m_Mouse);
                if (Mathf.Abs(b.x) <= 1f && Mathf.Abs(b.y) <= 1f) { StartTf(TfOp.Move, 0); return; }
                if (!layer.IsFloating)
                {
                    ctx.ShowHint("Drag inside the image to move it. To move painted pixels, select a paint layer first.");
                    return;
                }
                // Clicking outside the piece puts it down; the same click starts the next lift.
                s.ApplyFloating(set, layer);
                ctx.Render();
            }
            var active = set.ActiveLayer;
            if (active == null || active.kind != LayerKind.Paint || set.editingMask)
            {
                ctx.ShowHint("Transform: select a paint layer, then drag a shape around what to move, or click a color.");
                return;
            }
            m_TfPoints.Clear();
            m_TfPoints.Add(m_Mouse);
            StartTf(TfOp.Lasso, 0);
        }

        void StartTf(TfOp op, int handle)
        {
            m_TfOp = op;
            m_TfHandle = handle;
            m_Drag = Drag.Transform;
        }

        void DragTransform(bool shift)
        {
            if (m_TfOp == TfOp.Lasso)
            {
                if (Vector2.Distance(m_TfPoints[m_TfPoints.Count - 1], m_Mouse) > 3f) m_TfPoints.Add(m_Mouse);
                return;
            }
            var p = ActiveUVImage(out var layer);
            if (p == null || m_TfStart == null) return;
            var s = ctx.session;
            var st = m_TfStart;
            switch (m_TfOp)
            {
                case TfOp.Move:
                {
                    var d = LocalToUV(m_Mouse) - LocalToUV(m_DragStart);
                    s.EditPlacement(Set, layer, "move", pl => pl.uvCenter = st.uvCenter + d);
                    break;
                }
                case TfOp.Scale:
                {
                    var b = LocalToBox(st, m_Mouse);
                    var c = s_Corners[m_TfHandle];
                    Vector2 size;
                    if (shift)
                    {
                        // Shift: stretch freely.
                        size = new Vector2(st.uvSize.x * Mathf.Max(0.01f, b.x * c.x), st.uvSize.y * Mathf.Max(0.01f, b.y * c.y));
                    }
                    else
                    {
                        float k = Mathf.Max(0.01f, Vector2.Dot(b, c) / 2f);
                        size = st.uvSize * k;
                    }
                    s.EditPlacement(Set, layer, "scale", pl => pl.uvSize = size);
                    break;
                }
                case TfOp.Stretch:
                {
                    var b = LocalToBox(st, m_Mouse);
                    var e = s_Edges[m_TfHandle];
                    var size = st.uvSize;
                    if (e.x != 0f) size.x = st.uvSize.x * Mathf.Max(0.01f, b.x * e.x);
                    else size.y = st.uvSize.y * Mathf.Max(0.01f, b.y * e.y);
                    s.EditPlacement(Set, layer, "scale", pl => pl.uvSize = size);
                    break;
                }
                case TfOp.Rotate:
                {
                    var center = UVToLocal(st.uvCenter);
                    float a0 = Mathf.Atan2(-(m_DragStart.y - center.y), m_DragStart.x - center.x) * Mathf.Rad2Deg;
                    float a1 = Mathf.Atan2(-(m_Mouse.y - center.y), m_Mouse.x - center.x) * Mathf.Rad2Deg;
                    float rot = st.rotation + Mathf.DeltaAngle(a0, a1);
                    if (shift) rot = Mathf.Round(rot / 15f) * 15f;
                    s.EditPlacement(Set, layer, "rotate", pl => pl.rotation = Mathf.Repeat(rot + 180f, 360f) - 180f);
                    break;
                }
                case TfOp.Warp:
                {
                    var d = LocalToBox(st, m_Mouse) - LocalToBox(st, m_DragStart);
                    int k = m_TfHandle;
                    s.EditPlacement(Set, layer, "warp", pl =>
                    {
                        pl.warp = st.warp != null && st.warp.Length == 9 ? (Vector2[])st.warp.Clone() : new Vector2[9];
                        pl.warp[k] += d;
                    });
                    break;
                }
            }
            ctx.Render();
        }

        void EndTransform()
        {
            if (m_TfOp != TfOp.Lasso) return;
            var s = ctx.session;
            var set = Set;
            string msg;
            float travelled = 0f;
            for (int i = 1; i < m_TfPoints.Count; i++) travelled += Vector2.Distance(m_TfPoints[i - 1], m_TfPoints[i]);
            if (m_TfPoints.Count < 3 || travelled < 6f)
            {
                // A click picks up the area of similar color under the cursor.
                var mask = s.ColorMask(set, LocalToUV(m_DragStart), ctx.transformTolerance, ctx.transformContiguous);
                msg = s.Lift(set, set.ActiveLayer, mask, ctx.transformCopy);
            }
            else
            {
                var poly = new List<Vector2>();
                foreach (var pt in m_TfPoints) poly.Add(LocalToUV(pt));
                msg = s.Lift(set, set.ActiveLayer, APSession.PolygonMask(set, poly), ctx.transformCopy);
            }
            m_TfPoints.Clear();
            if (msg != null) ctx.ShowHint(msg);
            else ctx.ShowHint("Drag to move, corners scale (Shift: stretch), edges stretch, top handle rotates. Enter applies, Esc cancels.");
            ctx.Render();
        }

        void DrawTransform(Painter2D p)
        {
            if (m_Drag == Drag.Transform && m_TfOp == TfOp.Lasso && m_TfPoints.Count > 1)
            {
                var pts = new List<Vector2>(m_TfPoints) { m_Mouse };
                var arr = pts.ToArray();
                Polyline(p, arr, arr.Length, true, new Color(0, 0, 0, 0.6f), 3f);
                Polyline(p, arr, arr.Length, true, new Color(1f, 0.85f, 0.4f, 1f), 1.25f);
                return;
            }
            var img = ActiveUVImage(out _);
            if (img == null) return;
            var accent = new Color(1f, 0.85f, 0.4f, 1f);
            // Warped outline: the box edge sampled along its length.
            const int seg = 12;
            var outline = new Vector2[seg * 4];
            for (int e = 0; e < 4; e++)
            for (int i = 0; i < seg; i++)
                outline[e * seg + i] = BoxToLocal(img, Vector2.Lerp(s_Corners[e], s_Corners[(e + 1) % 4], i / (float)seg), true);
            Polyline(p, outline, outline.Length, true, new Color(0, 0, 0, 0.5f), 3f);
            Polyline(p, outline, outline.Length, true, accent, 1.25f);
            void Handle(Vector2 at, Color c)
            {
                Disc(p, at, 5f, new Color(0, 0, 0, 0.6f));
                Disc(p, at, 4f, c);
            }
            if (ctx.transformWarp)
            {
                for (int j = 0; j < 3; j++)
                {
                    var row = new Vector2[seg + 1];
                    var col = new Vector2[seg + 1];
                    for (int i = 0; i <= seg; i++)
                    {
                        float t = i / (float)seg * 2f - 1f;
                        row[i] = BoxToLocal(img, new Vector2(t, j - 1), true);
                        col[i] = BoxToLocal(img, new Vector2(j - 1, t), true);
                    }
                    Polyline(p, row, row.Length, false, new Color(1f, 0.85f, 0.4f, 0.45f), 1f);
                    Polyline(p, col, col.Length, false, new Color(1f, 0.85f, 0.4f, 0.45f), 1f);
                }
                for (int k = 0; k < 9; k++) Handle(BoxToLocal(img, WarpNode(k), true), Color.white);
            }
            else
            {
                foreach (var c in s_Corners) Handle(BoxToLocal(img, c, true), Color.white);
                foreach (var e in s_Edges) Handle(BoxToLocal(img, e, true), new Color(0.85f, 0.85f, 0.85f, 1f));
                var rh = TfRotateHandle(img);
                Line(p, BoxToLocal(img, new Vector2(0, 1), true), rh, accent, 1.25f);
                Handle(rh, accent);
            }
        }

        // ================================================================== overlay

        readonly Vector2[] m_Box = new Vector2[4];

        protected override void DrawOverlay(Painter2D p)
        {
            var set = Set;
            if (set == null) return;
            // texture border
            var a = UVToLocal(Vector2.zero);
            var b = UVToLocal(Vector2.one);
            var border = new[] { new Vector2(a.x, a.y), new Vector2(b.x, a.y), new Vector2(b.x, b.y), new Vector2(a.x, b.y) };
            Polyline(p, border, 4, true, new Color(0f, 0f, 0f, 0.5f), 1f);

            if (ctx.brush.mirrorU2D && (ctx.Tool == PaintTool.Brush || ctx.Tool == PaintTool.Eraser))
            {
                Line(p, UVToLocal(new Vector2(0.5f, 0f)), UVToLocal(new Vector2(0.5f, 1f)), new Color(1f, 0.6f, 0.2f, 0.5f), 1f);
            }

            var img = ActiveUVImage(out _);
            if (img != null && ctx.Tool == PaintTool.Image)
            {
                ImageCorners(img, m_Box);
                Polyline(p, m_Box, 4, true, new Color(0, 0, 0, 0.5f), 3f);
                Polyline(p, m_Box, 4, true, new Color(0.35f, 0.8f, 1f, 1f), 1.25f);
                foreach (var c in m_Box)
                {
                    Disc(p, c, 5f, new Color(0, 0, 0, 0.6f));
                    Disc(p, c, 4f, Color.white);
                }
                var rh = RotateHandle(img);
                var top = (m_Box[2] + m_Box[3]) * 0.5f;
                Line(p, top, rh, new Color(0.35f, 0.8f, 1f, 1f), 1.25f);
                Disc(p, rh, 5.5f, new Color(0, 0, 0, 0.6f));
                Disc(p, rh, 4.5f, new Color(0.35f, 0.8f, 1f, 1f));
            }

            if (ctx.Tool == PaintTool.Transform && !ctx.PoseMode) DrawTransform(p);

            if (m_Drag == Drag.Select)
            {
                if (ctx.selectLasso && m_SelPoints.Count > 1)
                {
                    var pts = new List<Vector2>(m_SelPoints) { m_Mouse };
                    var arr = pts.ToArray();
                    Polyline(p, arr, arr.Length, true, new Color(0, 0, 0, 0.6f), 3f);
                    Polyline(p, arr, arr.Length, true, new Color(0.45f, 0.75f, 1f, 1f), 1.25f);
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
            }

            if (!m_Inside && m_Drag != Drag.Paint) return;
            if (ctx.Tool == PaintTool.Brush || ctx.Tool == PaintTool.Eraser || m_Drag == Drag.BrushSize)
            {
                var center = m_Drag == Drag.BrushSize ? m_DragStart : m_Mouse;
                if (m_Drag == Drag.Paint && ctx.brush.stabilizer > 0f)
                {
                    var bp = UVToLocal(TexelToUV(m_Sampler.Position));
                    Line(p, bp, m_Mouse, new Color(1f, 1f, 1f, 0.4f), 1f);
                    center = bp;
                }
                float r = ctx.brush.radius2D * SX;
                Circle(p, center, r, new Color(0, 0, 0, 0.55f), 3f);
                Circle(p, center, r, new Color(1, 1, 1, 0.95f), 1.25f);
                if (m_Drag == Drag.BrushSize)
                    Circle(p, center, r * ctx.brush.hardness, new Color(1f, 0.85f, 0.4f, 0.8f), 1f);
                if (ctx.brush.mirrorU2D)
                {
                    var uv = LocalToUV(center);
                    var mc = UVToLocal(new Vector2(1f - uv.x, uv.y));
                    Circle(p, mc, r, new Color(1, 1, 1, 0.4f), 1f);
                }
                if (ctx.brush.symmetry && m_Drag != Drag.BrushSize && ctx.session.MirrorUV(set, ctx.brush.symmetryAxis, LocalToUV(center), out var symUV))
                    Circle(p, UVToLocal(symUV), r, new Color(1, 1, 1, 0.4f), 1f);
            }
            else if (ctx.Tool == PaintTool.Fill || ctx.Tool == PaintTool.Picker || ctx.Tool == PaintTool.Select ||
                     (ctx.Tool == PaintTool.Transform && ActiveUVImage(out _) == null))
            {
                var c = m_Mouse;
                Line(p, c + new Vector2(-9, 0), c + new Vector2(-3, 0), Color.white, 1.5f);
                Line(p, c + new Vector2(3, 0), c + new Vector2(9, 0), Color.white, 1.5f);
                Line(p, c + new Vector2(0, -9), c + new Vector2(0, -3), Color.white, 1.5f);
                Line(p, c + new Vector2(0, 3), c + new Vector2(0, 9), Color.white, 1.5f);
            }
        }

        public void UpdateHud()
        {
            try { UpdateHudText(); }
            finally { SyncHud(); }
        }

        void UpdateHudText()
        {
            var set = Set;
            if (set == null) { hudLeft.text = hudRight.text = hudCenter.text = ""; return; }
            hudLeft.text = set.width + " × " + set.height + "   ·   " + Mathf.RoundToInt(m_Scale * PPP / Mathf.Max(1f, set.width) * 100f) + "%" +
                           (set.HasSelection ? "   ·   " + set.SelectedCount + " polygons selected" : "");
            if (m_Inside)
            {
                var uv = LocalToUV(m_Mouse);
                hudRight.text = "UV " + uv.x.ToString("0.000") + ", " + uv.y.ToString("0.000") +
                                "   px " + Mathf.FloorToInt(uv.x * set.width) + ", " + Mathf.FloorToInt(uv.y * set.height);
            }
            else hudRight.text = (ctx.brush.radius2D * set.width).ToString("0.#") + " px brush";
        }
    }
}
