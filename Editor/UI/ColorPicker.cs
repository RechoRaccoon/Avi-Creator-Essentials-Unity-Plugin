using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>Hue ring + saturation/value square, hex entry, RGB/HSV sliders, alpha, swatches and recent colors.</summary>
    public class ColorPicker : VisualElement
    {
        readonly PainterContext m_Ctx;
        float m_H, m_S = 0f, m_V = 1f, m_A = 1f;
        bool m_Updating;

        readonly VisualElement m_Wheel;
        readonly Image m_Ring;
        readonly Image m_Square;
        readonly VisualElement m_HueMarker;
        readonly VisualElement m_SVMarker;
        Texture2D m_RingTex;
        Texture2D m_SquareTex;
        float m_SquareHue = -1f;

        readonly VisualElement m_Primary;
        readonly VisualElement m_Secondary;
        readonly TextField m_Hex;
        readonly VisualElement m_SliderBox;
        readonly VisualElement m_History;
        readonly VisualElement m_Swatches;
        readonly Slider[] m_Sliders = new Slider[4];
        readonly IntegerField[] m_Fields = new IntegerField[4];
        readonly Label[] m_SliderLabels = new Label[4];
        bool m_HsvMode;

        enum WheelDrag { None, Hue, SV }
        WheelDrag m_Drag;

        const float WheelSize = 196f;
        const float RingWidth = 18f;

        public ColorPicker(PainterContext ctx)
        {
            m_Ctx = ctx;
            AddToClassList("ap-color-picker");

            var top = W.Row("ap-color-top");
            Add(top);

            m_Wheel = new VisualElement();
            m_Wheel.AddToClassList("ap-wheel");
            m_Wheel.style.width = WheelSize;
            m_Wheel.style.height = WheelSize;
            top.Add(m_Wheel);

            m_Ring = new Image { scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            m_Ring.AddToClassList("ap-wheel-ring");
            m_Wheel.Add(m_Ring);
            m_Square = new Image { scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            m_Square.AddToClassList("ap-wheel-square");
            float inner = WheelSize * 0.5f - RingWidth - 6f;
            float side = Mathf.Floor(inner * 1.41421f);
            m_Square.style.width = side;
            m_Square.style.height = side;
            m_Square.style.left = (WheelSize - side) * 0.5f;
            m_Square.style.top = (WheelSize - side) * 0.5f;
            m_Wheel.Add(m_Square);

            m_HueMarker = new VisualElement { pickingMode = PickingMode.Ignore };
            m_HueMarker.AddToClassList("ap-wheel-marker");
            m_HueMarker.AddToClassList("ap-wheel-marker-hue");
            m_Wheel.Add(m_HueMarker);
            m_SVMarker = new VisualElement { pickingMode = PickingMode.Ignore };
            m_SVMarker.AddToClassList("ap-wheel-marker");
            m_Wheel.Add(m_SVMarker);

            m_Wheel.RegisterCallback<PointerDownEvent>(OnWheelDown);
            m_Wheel.RegisterCallback<PointerMoveEvent>(OnWheelMove);
            m_Wheel.RegisterCallback<PointerUpEvent>(OnWheelUp);

            // primary / secondary + actions
            var side2 = W.Column("ap-color-side");
            top.Add(side2);
            var pair = new VisualElement();
            pair.AddToClassList("ap-color-pair");
            m_Secondary = W.Swatch(ctx.secondary, 30, "Secondary color (X to swap)");
            m_Secondary.AddToClassList("ap-color-secondary");
            m_Secondary.RegisterCallback<ClickEvent>(_ => m_Ctx.SwapColors());
            pair.Add(m_Secondary);
            m_Primary = W.Swatch(ctx.primary, 38, "Paint color");
            m_Primary.AddToClassList("ap-color-primary");
            pair.Add(m_Primary);
            side2.Add(pair);
            side2.Add(W.IconButton(IconKind.Swap, "Swap colors (X)", () => m_Ctx.SwapColors()));
            side2.Add(W.IconButton(IconKind.Picker, "Pick a color from the model or texture (I)", () => m_Ctx.Tool = PaintTool.Picker));
            side2.Add(W.IconButton(IconKind.Plus, "Save this color as a swatch", () => m_Ctx.colors.AddSwatch(m_Ctx.primary)));

            // hex + mode
            var hexRow = W.Row("ap-hex-row");
            hexRow.Add(W.Text("#", "ap-hex-hash"));
            m_Hex = new TextField { maxLength = 9, isDelayed = true };
            m_Hex.AddToClassList("ap-hex");
            m_Hex.RegisterValueChangedCallback(e =>
            {
                if (m_Updating) return;
                if (APUtil.TryParseHex(e.newValue, out var c))
                {
                    if (e.newValue.Trim().TrimStart('#').Length <= 6) c.a = m_A;
                    m_Ctx.SetPrimary(c);
                }
                else Refresh();
            });
            hexRow.Add(m_Hex);
            hexRow.Add(W.Spacer());
            hexRow.Add(new W.Segmented(new[] { "RGB", "HSV" }, () => m_HsvMode ? 1 : 0, i => { m_HsvMode = i == 1; Refresh(); }));
            Add(hexRow);

            m_SliderBox = W.Column("ap-color-sliders");
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                var row = W.Row("ap-color-slider-row");
                m_SliderLabels[i] = W.Text("R", "ap-color-slider-label");
                row.Add(m_SliderLabels[i]);
                m_Sliders[i] = new Slider(0, 1);
                m_Sliders[i].AddToClassList("ap-color-slider");
                m_Sliders[i].RegisterValueChangedCallback(e => OnSlider(idx, e.newValue));
                row.Add(m_Sliders[i]);
                m_Fields[i] = new IntegerField { isDelayed = true };
                m_Fields[i].AddToClassList("ap-color-field");
                m_Fields[i].RegisterValueChangedCallback(e => OnField(idx, e.newValue));
                row.Add(m_Fields[i]);
                m_SliderBox.Add(row);
            }
            Add(m_SliderBox);

            Add(W.Text("Recent", "ap-subtle-heading"));
            m_History = W.Row("ap-color-history");
            Add(m_History);
            var swHead = W.Row();
            swHead.Add(W.Text("Swatches", "ap-subtle-heading"));
            swHead.Add(W.Spacer());
            swHead.Add(W.Text("Right-click a swatch to remove it", "ap-hint-inline"));
            Add(swHead);
            m_Swatches = W.Row("ap-color-history");
            Add(m_Swatches);

            BuildRing();
            m_Ctx.ColorChanged += OnContextColor;
            m_Ctx.colors.Changed += RebuildLists;
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                m_Ctx.ColorChanged -= OnContextColor;
                m_Ctx.colors.Changed -= RebuildLists;
                if (m_RingTex != null) Object.DestroyImmediate(m_RingTex);
                if (m_SquareTex != null) Object.DestroyImmediate(m_SquareTex);
            });
            OnContextColor();
            RebuildLists();
        }

        void OnContextColor()
        {
            if (m_Updating) return;
            var c = m_Ctx.primary;
            Color.RGBToHSV(c, out float h, out float s, out float v);
            // Keep hue/saturation when the color is gray or black so the wheel does not jump.
            if (s > 0.0001f && v > 0.0001f) m_H = h;
            if (v > 0.0001f) m_S = s;
            m_V = v;
            m_A = c.a;
            Refresh();
        }

        void Commit(bool fromWheel = true)
        {
            var c = Color.HSVToRGB(m_H, m_S, m_V);
            c.a = m_A;
            m_Ctx.primary = c;
            m_Updating = true;
            m_Ctx.SetPrimary(c);
            m_Updating = false;
            Refresh();
        }

        void Refresh()
        {
            m_Updating = true;
            var c = Color.HSVToRGB(m_H, m_S, m_V);
            c.a = m_A;
            W.SetSwatchColor(m_Primary, c);
            W.SetSwatchColor(m_Secondary, m_Ctx.secondary);
            m_Hex.SetValueWithoutNotify(APUtil.ToHex(c, m_A < 0.999f));
            UpdateSquare();
            float cx = WheelSize * 0.5f;
            float rMid = WheelSize * 0.5f - RingWidth * 0.5f - 1f;
            float a = m_H * Mathf.PI * 2f;
            SetMarker(m_HueMarker, new Vector2(cx + Mathf.Cos(a) * rMid, cx - Mathf.Sin(a) * rMid), Color.HSVToRGB(m_H, 1, 1));
            var sq = SquareRect();
            SetMarker(m_SVMarker, new Vector2(sq.x + m_S * sq.width, sq.y + (1f - m_V) * sq.height), c);

            string[] labels = m_HsvMode ? new[] { "H", "S", "V", "A" } : new[] { "R", "G", "B", "A" };
            float[] values = m_HsvMode ? new[] { m_H, m_S, m_V, m_A } : new[] { c.r, c.g, c.b, m_A };
            int[] maxes = m_HsvMode ? new[] { 360, 100, 100, 100 } : new[] { 255, 255, 255, 100 };
            for (int i = 0; i < 4; i++)
            {
                m_SliderLabels[i].text = labels[i];
                m_Sliders[i].SetValueWithoutNotify(values[i]);
                m_Fields[i].SetValueWithoutNotify(Mathf.RoundToInt(values[i] * maxes[i]));
            }
            m_Updating = false;
        }

        static void SetMarker(VisualElement m, Vector2 pos, Color fill)
        {
            m.style.left = pos.x - 7f;
            m.style.top = pos.y - 7f;
            m.style.backgroundColor = fill;
        }

        Rect SquareRect()
        {
            float inner = WheelSize * 0.5f - RingWidth - 6f;
            float side = Mathf.Floor(inner * 1.41421f);
            float o = (WheelSize - side) * 0.5f;
            return new Rect(o, o, side, side);
        }

        void OnSlider(int i, float v)
        {
            if (m_Updating) return;
            if (i == 3) m_A = v;
            else if (m_HsvMode)
            {
                if (i == 0) m_H = v;
                else if (i == 1) m_S = v;
                else m_V = v;
            }
            else
            {
                var c = Color.HSVToRGB(m_H, m_S, m_V);
                if (i == 0) c.r = v;
                else if (i == 1) c.g = v;
                else c.b = v;
                Color.RGBToHSV(c, out float h, out float s, out float vv);
                if (s > 0.0001f && vv > 0.0001f) m_H = h;
                m_S = s;
                m_V = vv;
            }
            Commit();
        }

        void OnField(int i, int value)
        {
            if (m_Updating) return;
            int[] maxes = m_HsvMode ? new[] { 360, 100, 100, 100 } : new[] { 255, 255, 255, 100 };
            OnSlider(i, Mathf.Clamp01(value / (float)maxes[i]));
        }

        // ------------------------------------------------------------------ wheel input

        void OnWheelDown(PointerDownEvent e)
        {
            var c = new Vector2(WheelSize * 0.5f, WheelSize * 0.5f);
            float d = Vector2.Distance(e.localPosition, c);
            if (d >= WheelSize * 0.5f - RingWidth - 3f && d <= WheelSize * 0.5f + 2f) m_Drag = WheelDrag.Hue;
            else if (SquareRect().Contains(e.localPosition)) m_Drag = WheelDrag.SV;
            else return;
            m_Wheel.CapturePointer(e.pointerId);
            Apply(e.localPosition);
            e.StopPropagation();
        }

        void OnWheelMove(PointerMoveEvent e)
        {
            if (m_Drag == WheelDrag.None) return;
            Apply(e.localPosition);
        }

        void OnWheelUp(PointerUpEvent e)
        {
            if (m_Drag == WheelDrag.None) return;
            m_Drag = WheelDrag.None;
            if (m_Wheel.HasPointerCapture(e.pointerId)) m_Wheel.ReleasePointer(e.pointerId);
        }

        void Apply(Vector2 local)
        {
            if (m_Drag == WheelDrag.Hue)
            {
                var c = new Vector2(WheelSize * 0.5f, WheelSize * 0.5f);
                float a = Mathf.Atan2(-(local.y - c.y), local.x - c.x);
                m_H = Mathf.Repeat(a / (Mathf.PI * 2f), 1f);
                if (m_S < 0.02f) m_S = 1f;
                if (m_V < 0.02f) m_V = 1f;
            }
            else if (m_Drag == WheelDrag.SV)
            {
                var r = SquareRect();
                m_S = Mathf.Clamp01((local.x - r.x) / r.width);
                m_V = Mathf.Clamp01(1f - (local.y - r.y) / r.height);
            }
            Commit();
        }

        // ------------------------------------------------------------------ textures

        void BuildRing()
        {
            const int size = 256;
            m_RingTex = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[size * size];
            float outer = size * 0.5f - 1f;
            float inner = outer - RingWidth * size / WheelSize;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - size * 0.5f, dy = y + 0.5f - size * 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - inner + 0.5f);
                float h = Mathf.Repeat(Mathf.Atan2(dy, dx) / (Mathf.PI * 2f), 1f);
                Color c = Color.HSVToRGB(h, 1f, 1f);
                c.a = alpha;
                px[y * size + x] = c;
            }
            m_RingTex.SetPixels32(px);
            m_RingTex.Apply(false, false);
            m_Ring.image = m_RingTex;
        }

        void UpdateSquare()
        {
            if (Mathf.Abs(m_SquareHue - m_H) < 0.0005f && m_SquareTex != null) return;
            m_SquareHue = m_H;
            const int size = 64;
            if (m_SquareTex == null)
            {
                m_SquareTex = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                m_Square.image = m_SquareTex;
            }
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = Color.HSVToRGB(m_H, x / (size - 1f), y / (size - 1f));
            m_SquareTex.SetPixels32(px);
            m_SquareTex.Apply(false, false);
            m_Square.MarkDirtyRepaint();
        }

        // ------------------------------------------------------------------ swatch lists

        void RebuildLists()
        {
            m_History.Clear();
            if (m_Ctx.colors.history.Count == 0)
                m_History.Add(W.Text("Colors you paint with show up here.", "ap-hint-inline"));
            foreach (var c in m_Ctx.colors.history)
            {
                var sw = W.Swatch(c, 18, "#" + APUtil.ToHex(c, c.a < 0.999f));
                var col = c;
                sw.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button == 0) m_Ctx.SetPrimary(col);
                    else if (e.button == 1) m_Ctx.SetSecondary(col);
                    e.StopPropagation();
                });
                m_History.Add(sw);
            }
            m_Swatches.Clear();
            for (int i = 0; i < m_Ctx.colors.swatches.Count; i++)
            {
                var c = m_Ctx.colors.swatches[i];
                int idx = i;
                var sw = W.Swatch(c, 18, "#" + APUtil.ToHex(c, c.a < 0.999f));
                sw.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button == 0) m_Ctx.SetPrimary(c);
                    else if (e.button == 1) m_Ctx.colors.RemoveSwatch(idx);
                    e.StopPropagation();
                });
                m_Swatches.Add(sw);
            }
        }
    }
}
