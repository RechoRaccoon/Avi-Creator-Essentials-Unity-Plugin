using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace AviCreatorEssentials
{
    public enum IconKind
    {
        Brush, Eraser, Fill, Picker, Image, Bone, Rotate, Move, Undo, Redo, Save, Bake, Eye, EyeOff, Plus, Trash,
        Duplicate, Mask, Merge, Gear, Frame, Symmetry, Emission, Armature, Layers, Swap, Folder, More, Close, Light,
        View3D, View2D, Split, Reset, Mirror, Lock, Unlock, FillLayer, Help, Select, Theme, Record, Key, Gradient, Stars,
    }

    /// <summary>Vector icons drawn with Painter2D, so the tool needs no image assets and stays crisp at any DPI.</summary>
    public class Icon : VisualElement
    {
        IconKind m_Kind;
        Color? m_Override;

        public IconKind Kind
        {
            get => m_Kind;
            set { m_Kind = value; MarkDirtyRepaint(); }
        }

        public Color? Tint
        {
            get => m_Override;
            set { m_Override = value; MarkDirtyRepaint(); }
        }

        public Icon(IconKind kind, float size = 16f)
        {
            m_Kind = kind;
            AddToClassList("ap-icon");
            style.width = size;
            style.height = size;
            style.flexShrink = 0;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext mgc)
        {
            var r = contentRect;
            if (r.width < 2) return;
            var p = mgc.painter2D;
            var col = m_Override ?? resolvedStyle.color;
            float s = Mathf.Min(r.width, r.height) / 16f;
            var o = new Vector2(r.x + (r.width - 16f * s) * 0.5f, r.y + (r.height - 16f * s) * 0.5f);
            Vector2 P(float x, float y) => o + new Vector2(x, y) * s;
            p.strokeColor = col;
            p.fillColor = col;
            p.lineWidth = 1.4f * Mathf.Max(1f, s * 0.9f);
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            void L(params float[] xy)
            {
                p.BeginPath();
                p.MoveTo(P(xy[0], xy[1]));
                for (int i = 2; i < xy.Length; i += 2) p.LineTo(P(xy[i], xy[i + 1]));
                p.Stroke();
            }

            void Closed(bool fill, params float[] xy)
            {
                p.BeginPath();
                p.MoveTo(P(xy[0], xy[1]));
                for (int i = 2; i < xy.Length; i += 2) p.LineTo(P(xy[i], xy[i + 1]));
                p.ClosePath();
                if (fill) p.Fill();
                else p.Stroke();
            }

            void Circ(float x, float y, float rad, bool fill)
            {
                p.BeginPath();
                p.Arc(P(x, y), rad * s, Angle.Degrees(0), Angle.Degrees(360));
                p.ClosePath();
                if (fill) p.Fill();
                else p.Stroke();
            }

            void Arc(float x, float y, float rad, float a0, float a1)
            {
                p.BeginPath();
                p.Arc(P(x, y), rad * s, Angle.Degrees(a0), Angle.Degrees(a1));
                p.Stroke();
            }

            switch (m_Kind)
            {
                case IconKind.Brush:
                    L(13.5f, 2.5f, 7.5f, 8.5f);
                    Closed(true, 6.2f, 9.2f, 8.3f, 11.3f, 6.5f, 13.8f, 2.2f, 14.3f, 3.2f, 11.8f);
                    break;
                case IconKind.Eraser:
                    Closed(false, 9.5f, 2.5f, 14f, 7f, 7.5f, 13.5f, 3f, 9f);
                    L(6f, 6f, 10.5f, 10.5f);
                    L(7.5f, 13.5f, 14f, 13.5f);
                    break;
                case IconKind.Fill:
                    Closed(false, 7f, 2.5f, 12.5f, 8f, 7.5f, 13f, 2f, 7.5f);
                    L(2f, 7.5f, 12.5f, 8f);
                    p.BeginPath();
                    p.MoveTo(P(13.5f, 10f));
                    p.LineTo(P(15f, 12.5f));
                    p.Arc(P(13.5f, 12.5f), 1.5f * s, Angle.Degrees(0), Angle.Degrees(180));
                    p.ClosePath();
                    p.Fill();
                    break;
                case IconKind.Picker:
                    L(3f, 13f, 9.5f, 6.5f);
                    L(2.5f, 13.5f, 3f, 13f);
                    Closed(true, 8.5f, 5f, 11f, 2.5f, 13.5f, 5f, 11f, 7.5f);
                    L(7.8f, 4.3f, 11.7f, 8.2f);
                    break;
                case IconKind.Image:
                    Closed(false, 2f, 3f, 14f, 3f, 14f, 13f, 2f, 13f);
                    Closed(true, 3.5f, 11.5f, 7f, 7f, 9.5f, 10f, 11f, 8.5f, 12.5f, 11.5f);
                    Circ(10.8f, 5.6f, 1.2f, true);
                    break;
                case IconKind.Bone:
                case IconKind.Armature:
                    Closed(false, 3f, 13f, 6f, 6.5f, 13f, 3f, 9.5f, 10f);
                    L(3f, 13f, 9.5f, 10f);
                    L(6f, 6.5f, 9.5f, 10f);
                    Circ(3f, 13f, 1.3f, true);
                    break;
                case IconKind.Rotate:
                    Arc(8f, 8f, 5.5f, 200f, 470f);
                    Closed(true, 2.1f, 5.2f, 5.6f, 6.6f, 2.6f, 9.2f);
                    break;
                case IconKind.Move:
                    L(8f, 1.5f, 8f, 14.5f);
                    L(1.5f, 8f, 14.5f, 8f);
                    L(6f, 3.5f, 8f, 1.5f, 10f, 3.5f);
                    L(6f, 12.5f, 8f, 14.5f, 10f, 12.5f);
                    L(3.5f, 6f, 1.5f, 8f, 3.5f, 10f);
                    L(12.5f, 6f, 14.5f, 8f, 12.5f, 10f);
                    break;
                case IconKind.Undo:
                    Arc(8.5f, 9f, 4.5f, 180f, 405f);
                    L(1.5f, 6.5f, 4f, 9.5f, 7f, 7.2f);
                    break;
                case IconKind.Redo:
                    Arc(7.5f, 9f, 4.5f, 135f, 360f);
                    L(14.5f, 6.5f, 12f, 9.5f, 9f, 7.2f);
                    break;
                case IconKind.Save:
                    Closed(false, 2.5f, 2.5f, 11.5f, 2.5f, 13.5f, 4.5f, 13.5f, 13.5f, 2.5f, 13.5f);
                    Closed(false, 5f, 2.5f, 10.5f, 2.5f, 10.5f, 5.5f, 5f, 5.5f);
                    Closed(false, 4.5f, 9f, 11.5f, 9f, 11.5f, 13.5f, 4.5f, 13.5f);
                    break;
                case IconKind.Bake:
                    L(8f, 2f, 8f, 10f);
                    L(4.5f, 6.5f, 8f, 10f, 11.5f, 6.5f);
                    L(2.5f, 10.5f, 2.5f, 13.5f, 13.5f, 13.5f, 13.5f, 10.5f);
                    break;
                case IconKind.Eye:
                    p.BeginPath();
                    p.MoveTo(P(1.5f, 8f));
                    p.BezierCurveTo(P(4f, 3.5f), P(12f, 3.5f), P(14.5f, 8f));
                    p.BezierCurveTo(P(12f, 12.5f), P(4f, 12.5f), P(1.5f, 8f));
                    p.Stroke();
                    Circ(8f, 8f, 2f, true);
                    break;
                case IconKind.EyeOff:
                    p.BeginPath();
                    p.MoveTo(P(1.5f, 8f));
                    p.BezierCurveTo(P(4f, 3.5f), P(12f, 3.5f), P(14.5f, 8f));
                    p.BezierCurveTo(P(12f, 12.5f), P(4f, 12.5f), P(1.5f, 8f));
                    p.Stroke();
                    L(3f, 13.5f, 13f, 2.5f);
                    break;
                case IconKind.Plus:
                    L(8f, 3f, 8f, 13f);
                    L(3f, 8f, 13f, 8f);
                    break;
                case IconKind.Trash:
                    L(2.5f, 4f, 13.5f, 4f);
                    L(6f, 4f, 6.5f, 2f, 9.5f, 2f, 10f, 4f);
                    Closed(false, 3.8f, 4f, 12.2f, 4f, 11.3f, 14f, 4.7f, 14f);
                    L(6.8f, 6.5f, 7f, 11.5f);
                    L(9.2f, 6.5f, 9f, 11.5f);
                    break;
                case IconKind.Duplicate:
                    Closed(false, 5.5f, 5.5f, 13.5f, 5.5f, 13.5f, 13.5f, 5.5f, 13.5f);
                    L(3.5f, 10.5f, 2.5f, 10.5f, 2.5f, 2.5f, 10.5f, 2.5f, 10.5f, 3.5f);
                    break;
                case IconKind.Mask:
                    Closed(false, 2.5f, 2.5f, 13.5f, 2.5f, 13.5f, 13.5f, 2.5f, 13.5f);
                    p.BeginPath();
                    p.Arc(P(8f, 8f), 3.5f * s, Angle.Degrees(90), Angle.Degrees(270));
                    p.ClosePath();
                    p.Fill();
                    Circ(8f, 8f, 3.5f, false);
                    break;
                case IconKind.Merge:
                    L(4f, 2.5f, 4f, 6f, 8f, 10f, 12f, 6f, 12f, 2.5f);
                    L(8f, 10f, 8f, 14f);
                    L(5.5f, 11.5f, 8f, 14f, 10.5f, 11.5f);
                    break;
                case IconKind.Gear:
                    Circ(8f, 8f, 2.3f, false);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI / 4f;
                        L(8f + Mathf.Cos(a) * 4.3f, 8f + Mathf.Sin(a) * 4.3f, 8f + Mathf.Cos(a) * 6.3f, 8f + Mathf.Sin(a) * 6.3f);
                    }
                    Circ(8f, 8f, 4.3f, false);
                    break;
                case IconKind.Frame:
                    L(2f, 5.5f, 2f, 2f, 5.5f, 2f);
                    L(10.5f, 2f, 14f, 2f, 14f, 5.5f);
                    L(14f, 10.5f, 14f, 14f, 10.5f, 14f);
                    L(5.5f, 14f, 2f, 14f, 2f, 10.5f);
                    Circ(8f, 8f, 2f, true);
                    break;
                case IconKind.Symmetry:
                    L(8f, 1.5f, 8f, 14.5f);
                    Closed(false, 6f, 4f, 2f, 8f, 6f, 12f);
                    Closed(true, 10f, 4f, 14f, 8f, 10f, 12f);
                    break;
                case IconKind.Emission:
                case IconKind.Light:
                    Circ(8f, 8f, 3f, m_Kind == IconKind.Emission);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI / 4f;
                        L(8f + Mathf.Cos(a) * 5f, 8f + Mathf.Sin(a) * 5f, 8f + Mathf.Cos(a) * 6.8f, 8f + Mathf.Sin(a) * 6.8f);
                    }
                    break;
                case IconKind.Layers:
                    Closed(false, 8f, 2f, 14f, 5.5f, 8f, 9f, 2f, 5.5f);
                    L(2f, 8.5f, 8f, 12f, 14f, 8.5f);
                    L(2f, 11f, 8f, 14.5f, 14f, 11f);
                    break;
                case IconKind.Swap:
                    L(3f, 5f, 12f, 5f);
                    L(9.5f, 2.5f, 12f, 5f, 9.5f, 7.5f);
                    L(13f, 11f, 4f, 11f);
                    L(6.5f, 8.5f, 4f, 11f, 6.5f, 13.5f);
                    break;
                case IconKind.Folder:
                    Closed(false, 1.5f, 4f, 6f, 4f, 7.5f, 5.5f, 14.5f, 5.5f, 14.5f, 13f, 1.5f, 13f);
                    break;
                case IconKind.More:
                    Circ(3.5f, 8f, 1.3f, true);
                    Circ(8f, 8f, 1.3f, true);
                    Circ(12.5f, 8f, 1.3f, true);
                    break;
                case IconKind.Close:
                    L(3.5f, 3.5f, 12.5f, 12.5f);
                    L(12.5f, 3.5f, 3.5f, 12.5f);
                    break;
                case IconKind.View3D:
                    Closed(false, 8f, 1.5f, 14f, 4.8f, 14f, 11.2f, 8f, 14.5f, 2f, 11.2f, 2f, 4.8f);
                    L(2f, 4.8f, 8f, 8f, 14f, 4.8f);
                    L(8f, 8f, 8f, 14.5f);
                    break;
                case IconKind.View2D:
                    Closed(false, 2f, 2f, 14f, 2f, 14f, 14f, 2f, 14f);
                    L(2f, 14f, 14f, 2f);
                    L(2f, 8f, 8f, 2f);
                    L(8f, 14f, 14f, 8f);
                    break;
                case IconKind.Split:
                    Closed(false, 1.5f, 2.5f, 14.5f, 2.5f, 14.5f, 13.5f, 1.5f, 13.5f);
                    L(8f, 2.5f, 8f, 13.5f);
                    break;
                case IconKind.Reset:
                    Arc(8f, 8f, 5.5f, 140f, 420f);
                    L(2.5f, 3f, 2.8f, 7f, 6.8f, 6.6f);
                    break;
                case IconKind.Mirror:
                    L(8f, 2f, 8f, 4f);
                    L(8f, 6f, 8f, 10f);
                    L(8f, 12f, 8f, 14f);
                    L(5.5f, 4.5f, 2f, 8f, 5.5f, 11.5f);
                    L(10.5f, 4.5f, 14f, 8f, 10.5f, 11.5f);
                    break;
                case IconKind.Lock:
                case IconKind.Unlock:
                    Closed(false, 3.5f, 7.5f, 12.5f, 7.5f, 12.5f, 14f, 3.5f, 14f);
                    if (m_Kind == IconKind.Lock) Arc(8f, 6f, 3f, 180f, 360f);
                    else Arc(10.5f, 5f, 3f, 180f, 330f);
                    if (m_Kind == IconKind.Lock) { L(5f, 6f, 5f, 7.5f); L(11f, 6f, 11f, 7.5f); }
                    else L(7.5f, 5f, 7.5f, 7.5f);
                    break;
                case IconKind.FillLayer:
                    Closed(true, 2.5f, 2.5f, 13.5f, 2.5f, 13.5f, 13.5f, 2.5f, 13.5f);
                    break;
                case IconKind.Select:
                    for (int i = 0; i < 4; i++)
                    {
                        float a0 = 2f + i * 3.2f;
                        L(a0, 2.5f, a0 + 1.8f, 2.5f);
                        L(a0, 13.5f, a0 + 1.8f, 13.5f);
                        L(2.5f, a0, 2.5f, a0 + 1.8f);
                        L(13.5f, a0, 13.5f, a0 + 1.8f);
                    }
                    Closed(true, 6f, 6f, 10.5f, 8f, 8.5f, 8.7f, 10.5f, 11f, 9.6f, 11.8f, 7.7f, 9.5f, 6.8f, 11f);
                    break;
                case IconKind.Theme:
                    Circ(8f, 8f, 6f, false);
                    p.BeginPath();
                    p.Arc(P(8f, 8f), 6f * s, Angle.Degrees(-90), Angle.Degrees(90));
                    p.ClosePath();
                    p.Fill();
                    break;
                case IconKind.Record:
                    Circ(8f, 8f, 5f, true);
                    break;
                case IconKind.Key:
                    Closed(true, 8f, 2.5f, 13.5f, 8f, 8f, 13.5f, 2.5f, 8f);
                    break;
                case IconKind.Gradient:
                    Closed(false, 2.5f, 2.5f, 13.5f, 2.5f, 13.5f, 13.5f, 2.5f, 13.5f);
                    Closed(true, 2.5f, 2.5f, 6f, 2.5f, 6f, 13.5f, 2.5f, 13.5f);
                    L(8f, 3f, 8f, 13f);
                    L(10.5f, 4f, 10.5f, 12f);
                    break;
                case IconKind.Stars:
                    Closed(true, 6f, 1.5f, 7.2f, 5f, 10.5f, 6.2f, 7.2f, 7.4f, 6f, 11f, 4.8f, 7.4f, 1.5f, 6.2f, 4.8f, 5f);
                    Closed(true, 12f, 8.5f, 12.7f, 10.6f, 14.8f, 11.3f, 12.7f, 12f, 12f, 14.3f, 11.3f, 12f, 9.2f, 11.3f, 11.3f, 10.6f);
                    Circ(12.5f, 3.5f, 0.9f, true);
                    break;
                case IconKind.Help:
                    Circ(8f, 8f, 6f, false);
                    Arc(8f, 6.3f, 2f, 180f, 405f);
                    L(8f, 8.3f, 8f, 9.5f);
                    Circ(8f, 11.8f, 0.8f, true);
                    break;
            }
        }
    }
}
