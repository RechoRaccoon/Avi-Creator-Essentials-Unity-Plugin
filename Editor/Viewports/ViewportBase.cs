using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UIElements;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Turns mouse/pen movement into evenly spaced dabs, with an optional stabiliser
    /// (a lazy-mouse rope, or a smoothing average over the recent path).
    /// Works in any 2D space (viewport points for 3D painting, texels for 2D painting).
    /// </summary>
    public class StrokeSampler
    {
        Vector2 m_Brush;      // stabilised brush position
        Vector2 m_Pointer;    // latest raw pointer position
        float m_Carry;
        float m_LastPressure = 1f;
        float m_PointerPressure = 1f;
        readonly List<Vector2> m_Path = new List<Vector2>();   // pointer path resampled at even spacing (smooth mode)
        public Vector2 Position => m_Brush;
        public Vector2 Direction { get; private set; } = Vector2.right;

        public void Begin(Vector2 p, float pressure)
        {
            m_Brush = p;
            m_Pointer = p;
            m_Carry = 0f;
            m_LastPressure = pressure;
            m_PointerPressure = pressure;
            Direction = Vector2.right;
            m_Path.Clear();
            m_Path.Add(p);
        }

        /// <summary>
        /// Moves toward the pointer and calls emit for every dab position along the way.
        /// unit is the size of one screen point in this space, so the stabiliser feels the same at any zoom.
        /// </summary>
        public void Advance(Vector2 pointer, float pressure, float step, BrushSettings b, float unit, Action<Vector2, float, Vector2> emit)
        {
            m_Pointer = pointer;
            m_PointerPressure = pressure;
            float strength = Mathf.Clamp01(b.stabilizer);
            Vector2 target = pointer;
            if (strength > 0f && b.stabilizerMode == StabilizerMode.Rope)
            {
                float rope = strength * 80f * unit;
                var d = pointer - m_Brush;
                float len = d.magnitude;
                if (len <= rope) return;
                target = pointer - d / len * rope;
            }
            else if (strength > 0f && b.stabilizerMode == StabilizerMode.Smooth)
            {
                // Average the last stretch of the path, measured in distance rather than events,
                // so the result does not depend on how fast the mouse reports.
                float spacing = 2f * unit;
                var last = m_Path[m_Path.Count - 1];
                float dist = Vector2.Distance(last, pointer);
                if (dist >= spacing)
                {
                    int n = Mathf.FloorToInt(dist / spacing);
                    for (int i = 1; i <= n; i++) m_Path.Add(Vector2.Lerp(last, pointer, i * spacing / dist));
                }
                int window = Mathf.Max(1, Mathf.RoundToInt(strength * strength * 120f));
                while (m_Path.Count > window) m_Path.RemoveAt(0);
                var sum = Vector2.zero;
                foreach (var q in m_Path) sum += q;
                target = sum / m_Path.Count;
            }
            MoveTo(target, pressure, step, emit);
        }

        /// <summary>Draws the rest of the line up to where the pointer was released.</summary>
        public void Finish(float step, Action<Vector2, float, Vector2> emit)
        {
            MoveTo(m_Pointer, m_PointerPressure, Mathf.Max(0.25f, step), emit);
        }

        void MoveTo(Vector2 target, float pressure, float step, Action<Vector2, float, Vector2> emit)
        {
            step = Mathf.Max(0.25f, step);
            var from = m_Brush;
            var seg = target - from;
            float segLen = seg.magnitude;
            if (segLen < 1e-4f) return;
            m_Brush = target;
            var dir = seg / segLen;
            Direction = dir;
            float t = step - m_Carry;
            float p0 = m_LastPressure;
            while (t <= segLen)
            {
                var pos = from + dir * t;
                float k = t / segLen;
                emit(pos, Mathf.Lerp(p0, pressure, k), dir);
                t += step;
            }
            m_Carry = segLen - (t - step);
            m_LastPressure = pressure;
        }
    }

    /// <summary>Shared plumbing for the 3D and 2D views: a render texture shown in an Image, a vector overlay, HUD text.</summary>
    public abstract class ViewportBase : VisualElement
    {
        protected readonly PainterContext ctx;
        protected readonly Image image;
        protected readonly VisualElement overlay;
        protected readonly Label hudLeft;
        protected readonly Label hudRight;
        protected readonly Label hudCenter;
        protected RenderTexture rt;
        bool m_RenderRequested = true;
        protected int msaa = 1;
        protected int depthBits;

        protected float W => contentRect.width;
        protected float H => contentRect.height;
        protected static float PPP => EditorGUIUtility.pixelsPerPoint;

        protected ViewportBase(PainterContext context)
        {
            ctx = context;
            AddToClassList("ap-viewport");
            focusable = true;
            pickingMode = PickingMode.Position;

            image = new Image { scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            image.AddToClassList("ap-viewport-image");
            Add(image);

            overlay = new VisualElement { pickingMode = PickingMode.Ignore };
            overlay.AddToClassList("ap-viewport-overlay");
            overlay.generateVisualContent += mgc =>
            {
                try { DrawOverlay(mgc.painter2D); }
                catch (Exception e) { Debug.LogException(e); }
            };
            Add(overlay);

            hudLeft = new Label { pickingMode = PickingMode.Ignore };
            hudLeft.AddToClassList("ap-hud");
            hudLeft.AddToClassList("ap-hud-left");
            Add(hudLeft);
            hudRight = new Label { pickingMode = PickingMode.Ignore };
            hudRight.AddToClassList("ap-hud");
            hudRight.AddToClassList("ap-hud-right");
            Add(hudRight);
            hudCenter = new Label { pickingMode = PickingMode.Ignore };
            hudCenter.AddToClassList("ap-hud");
            hudCenter.AddToClassList("ap-hud-center");
            Add(hudCenter);

            RegisterCallback<GeometryChangedEvent>(_ => RequestRender());
            RegisterCallback<DetachFromPanelEvent>(_ => ReleaseRT());
            RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (!AviCreatorEssentials.W.IsEditingText(panel?.focusController?.focusedElement)) Focus();
            });
        }

        public void RequestRender()
        {
            m_RenderRequested = true;
        }

        public void RepaintOverlay() => overlay.MarkDirtyRepaint();

        /// <summary>Renders immediately instead of waiting for the next editor update (used while posing).</summary>
        public void RenderNow()
        {
            m_RenderRequested = true;
            RenderIfNeeded();
        }

        /// <summary>Called from the window's update loop.</summary>
        public void RenderIfNeeded()
        {
            if (!m_RenderRequested || panel == null || ctx.session == null) return;
            if (resolvedStyle.display == DisplayStyle.None || W < 4 || H < 4) return;
            m_RenderRequested = false;
            EnsureRT();
            try
            {
                DoRender(rt);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            image.MarkDirtyRepaint();
            overlay.MarkDirtyRepaint();
        }

        void EnsureRT()
        {
            int w = Mathf.Max(4, Mathf.RoundToInt(W * PPP));
            int h = Mathf.Max(4, Mathf.RoundToInt(H * PPP));
            if (rt != null && rt.width == w && rt.height == h) return;
            ReleaseRT();
            var format = APUtil.LinearProject ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
            rt = APUtil.CreateRT(w, h, format, GetType().Name, false, FilterMode.Bilinear, TextureWrapMode.Clamp, depthBits, msaa);
            image.image = rt;
        }

        protected void ReleaseRT()
        {
            image.image = null;
            APUtil.Release(ref rt);
        }

        protected abstract void DoRender(RenderTexture target);
        protected abstract void DrawOverlay(Painter2D p);

        // ------------------------------------------------------------------ painter helpers

        protected static void Circle(Painter2D p, Vector2 c, float r, Color color, float width)
        {
            p.strokeColor = color;
            p.lineWidth = width;
            p.BeginPath();
            p.Arc(c, Mathf.Max(0.5f, r), Angle.Degrees(0), Angle.Degrees(360));
            p.ClosePath();
            p.Stroke();
        }

        protected static void Disc(Painter2D p, Vector2 c, float r, Color color)
        {
            p.fillColor = color;
            p.BeginPath();
            p.Arc(c, Mathf.Max(0.5f, r), Angle.Degrees(0), Angle.Degrees(360));
            p.ClosePath();
            p.Fill();
        }

        protected static void Line(Painter2D p, Vector2 a, Vector2 b, Color color, float width)
        {
            p.strokeColor = color;
            p.lineWidth = width;
            p.BeginPath();
            p.MoveTo(a);
            p.LineTo(b);
            p.Stroke();
        }

        protected static void Polyline(Painter2D p, Vector2[] pts, int count, bool closed, Color color, float width)
        {
            if (count < 2) return;
            p.strokeColor = color;
            p.lineWidth = width;
            p.lineJoin = LineJoin.Round;
            p.BeginPath();
            p.MoveTo(pts[0]);
            for (int i = 1; i < count; i++) p.LineTo(pts[i]);
            if (closed) p.ClosePath();
            p.Stroke();
        }

        /// <summary>A ring drawn twice (dark then light) so it reads on any background.</summary>
        protected static void CursorRing(Painter2D p, Vector2[] pts, int count, float alpha = 1f)
        {
            Polyline(p, pts, count, true, new Color(0f, 0f, 0f, 0.55f * alpha), 3f);
            Polyline(p, pts, count, true, new Color(1f, 1f, 1f, 0.95f * alpha), 1.25f);
        }

        /// <summary>Hides HUD labels that have nothing to say.</summary>
        protected void SyncHud()
        {
            foreach (var l in new[] { hudLeft, hudRight, hudCenter })
                l.style.display = string.IsNullOrEmpty(l.text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>A right-button press that has not moved far enough to count as a drag yet.</summary>
        protected bool rightClickPending;
        protected const float ClickSlop = 4f;

        /// <summary>A plain right-click (no drag) opens the quick color wheel under the cursor.</summary>
        protected void OpenQuickColor(Vector2 local)
        {
            if (ctx.PoseMode) return;
            ctx.OpenQuickColor?.Invoke(this.LocalToWorld(local));
        }

        protected static float PressureOf(IPointerEvent evt)
        {
            if (evt.pointerType == UnityEngine.UIElements.PointerType.pen) return Mathf.Clamp01(evt.pressure);
            return 1f;
        }

        protected float RandomRange(System.Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);
    }
}
