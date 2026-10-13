using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AviCreatorEssentials
{
    /// <summary>Small building blocks for the painter's UI.</summary>
    public static class W
    {
        public static VisualElement Row(params string[] classes)
        {
            var v = new VisualElement();
            v.AddToClassList("ap-row");
            foreach (var c in classes) v.AddToClassList(c);
            return v;
        }

        public static VisualElement Column(params string[] classes)
        {
            var v = new VisualElement();
            v.AddToClassList("ap-col");
            foreach (var c in classes) v.AddToClassList(c);
            return v;
        }

        public static VisualElement Spacer()
        {
            var v = new VisualElement();
            v.AddToClassList("ap-spacer");
            return v;
        }

        public static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        public static Button IconButton(IconKind icon, string tooltip, Action onClick, params string[] classes)
        {
            var b = new Button(onClick) { tooltip = tooltip };
            b.AddToClassList("ap-icon-button");
            foreach (var c in classes) b.AddToClassList(c);
            b.Add(new Icon(icon));
            return b;
        }

        public static Button TextButton(string text, Action onClick, params string[] classes)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("ap-button");
            foreach (var c in classes) b.AddToClassList(c);
            return b;
        }

        public static Button IconTextButton(IconKind icon, string text, Action onClick, params string[] classes)
        {
            var b = new Button(onClick);
            b.AddToClassList("ap-button");
            b.AddToClassList("ap-icon-text-button");
            foreach (var c in classes) b.AddToClassList(c);
            b.Add(new Icon(icon, 14));
            b.Add(new Label(text));
            return b;
        }

        public static void SetOn(VisualElement e, bool on, string cls = "ap-on")
        {
            if (on) e.AddToClassList(cls);
            else e.RemoveFromClassList(cls);
        }

        /// <summary>A labelled slider with an editable value. Value text uses the formatter; editing accepts plain numbers.</summary>
        public class SliderRow : VisualElement
        {
            readonly Slider m_Slider;
            readonly FloatField m_Field;
            readonly Func<float> m_Get;
            readonly Action<float> m_Set;
            readonly float m_DisplayScale;
            bool m_Updating;

            public SliderRow(string label, float min, float max, Func<float> get, Action<float> set, float displayScale = 1f,
                string tooltipText = null, bool logarithmic = false)
            {
                AddToClassList("ap-slider-row");
                m_Get = get;
                m_Set = set;
                m_DisplayScale = displayScale;
                tooltip = tooltipText;
                var l = new Label(label);
                l.AddToClassList("ap-slider-label");
                Add(l);
                m_Logarithmic = logarithmic && min > 0f;
                m_Slider = m_Logarithmic ? new Slider(Mathf.Log(min), Mathf.Log(max)) : new Slider(min, max);
                m_Slider.AddToClassList("ap-slider");
                m_Slider.RegisterValueChangedCallback(e =>
                {
                    if (m_Updating) return;
                    float v = m_Logarithmic ? Mathf.Exp(e.newValue) : e.newValue;
                    m_Set(v);
                    Refresh();
                });
                Add(m_Slider);
                m_Field = new FloatField();
                m_Field.AddToClassList("ap-slider-field");
                m_Field.isDelayed = true;
                m_Field.RegisterValueChangedCallback(e =>
                {
                    if (m_Updating) return;
                    float v = e.newValue / m_DisplayScale;
                    if (m_Logarithmic) v = Mathf.Clamp(v, Mathf.Exp(m_Slider.lowValue), Mathf.Exp(m_Slider.highValue));
                    else v = Mathf.Clamp(v, m_Slider.lowValue, m_Slider.highValue);
                    m_Set(v);
                    Refresh();
                });
                Add(m_Field);
                Refresh();
            }

            readonly bool m_Logarithmic;

            public void Refresh()
            {
                m_Updating = true;
                float v = m_Get();
                m_Slider.SetValueWithoutNotify(m_Logarithmic ? Mathf.Log(Mathf.Max(1e-6f, v)) : v);
                float shown = v * m_DisplayScale;
                m_Field.SetValueWithoutNotify((float)Math.Round(shown, shown < 10f ? 2 : shown < 100f ? 1 : 0));
                m_Updating = false;
            }
        }

        public class ToggleRow : VisualElement
        {
            readonly Toggle m_Toggle;
            readonly Func<bool> m_Get;

            public ToggleRow(string label, Func<bool> get, Action<bool> set, string tooltipText = null)
            {
                AddToClassList("ap-toggle-row");
                m_Get = get;
                tooltip = tooltipText;
                m_Toggle = new Toggle(label);
                m_Toggle.AddToClassList("ap-toggle");
                m_Toggle.RegisterValueChangedCallback(e => set(e.newValue));
                Add(m_Toggle);
                Refresh();
            }

            public void Refresh() => m_Toggle.SetValueWithoutNotify(m_Get());
        }

        /// <summary>A row of mutually exclusive buttons.</summary>
        public class Segmented : VisualElement
        {
            readonly List<Button> m_Buttons = new List<Button>();
            readonly Func<int> m_Get;

            public Segmented(string[] labels, Func<int> get, Action<int> set, string[] tooltips = null)
            {
                AddToClassList("ap-segmented");
                m_Get = get;
                for (int i = 0; i < labels.Length; i++)
                {
                    int idx = i;
                    var b = new Button(() => { set(idx); Refresh(); }) { text = labels[i] };
                    if (tooltips != null && i < tooltips.Length) b.tooltip = tooltips[i];
                    b.AddToClassList("ap-segment");
                    if (i == 0) b.AddToClassList("ap-segment-first");
                    if (i == labels.Length - 1) b.AddToClassList("ap-segment-last");
                    m_Buttons.Add(b);
                    Add(b);
                }
                Refresh();
            }

            public void Refresh()
            {
                int v = m_Get();
                for (int i = 0; i < m_Buttons.Count; i++) SetOn(m_Buttons[i], i == v);
            }
        }

        public class DropdownRow : VisualElement
        {
            readonly DropdownField m_Field;
            readonly Func<int> m_Get;

            public DropdownRow(string label, List<string> choices, Func<int> get, Action<int> set, string tooltipText = null)
            {
                AddToClassList("ap-dropdown-row");
                m_Get = get;
                tooltip = tooltipText;
                if (!string.IsNullOrEmpty(label))
                {
                    var l = new Label(label);
                    l.AddToClassList("ap-slider-label");
                    Add(l);
                }
                m_Field = new DropdownField(choices, Mathf.Clamp(get(), 0, Mathf.Max(0, choices.Count - 1)));
                m_Field.AddToClassList("ap-dropdown");
                m_Field.RegisterValueChangedCallback(_ => set(m_Field.index));
                Add(m_Field);
            }

            public void SetChoices(List<string> choices)
            {
                m_Field.choices = choices;
                Refresh();
            }

            public void Refresh()
            {
                int i = m_Get();
                if (i >= 0 && i < m_Field.choices.Count) m_Field.SetValueWithoutNotify(m_Field.choices[i]);
            }
        }

        /// <summary>Collapsible panel section; its open state is remembered.</summary>
        public class Section : VisualElement
        {
            readonly VisualElement m_Body;
            readonly VisualElement m_Header;
            readonly Label m_Chevron;
            readonly string m_Key;
            public VisualElement Body => m_Body;
            public VisualElement HeaderExtras { get; }

            public bool Open
            {
                get => !ClassListContains("ap-closed");
                set
                {
                    SetOn(this, !value, "ap-closed");
                    m_Chevron.text = value ? "▾" : "▸";
                    EditorPrefs.SetBool(m_Key, value);
                }
            }

            public Section(string title, IconKind icon, string key, bool defaultOpen = true)
            {
                AddToClassList("ap-section");
                m_Key = "AviCreatorEssentials.Section." + key;
                m_Header = Row("ap-section-header");
                m_Chevron = new Label("▾");
                m_Chevron.AddToClassList("ap-chevron");
                m_Header.Add(m_Chevron);
                m_Header.Add(new Icon(icon, 14));
                var t = new Label(title);
                t.AddToClassList("ap-section-title");
                m_Header.Add(t);
                m_Header.Add(Spacer());
                HeaderExtras = Row("ap-section-extras");
                m_Header.Add(HeaderExtras);
                m_Header.RegisterCallback<ClickEvent>(e =>
                {
                    if (e.target is VisualElement ve && HeaderExtras.Contains(ve)) return;
                    Open = !Open;
                });
                Add(m_Header);
                m_Body = Column("ap-section-body");
                Add(m_Body);
                Open = EditorPrefs.GetBool(m_Key, defaultOpen);
            }
        }

        /// <summary>True when keyboard input belongs to a text or number field.</summary>
        public static bool IsEditingText(Focusable f)
        {
            var ve = f as VisualElement;
            while (ve != null)
            {
                if (ve is TextField || ve is FloatField || ve is IntegerField || ve is Vector3Field ||
                    ve is UnityEditor.UIElements.ObjectField)
                    return true;
                ve = ve.parent;
            }
            return false;
        }

        public static VisualElement Swatch(Color c, float size, string tooltipText = null)
        {
            var outer = new VisualElement { tooltip = tooltipText };
            outer.AddToClassList("ap-swatch");
            outer.style.width = size;
            outer.style.height = size;
            var checker = new Image { image = APResources.Checker, scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
            checker.AddToClassList("ap-swatch-checker");
            outer.Add(checker);
            var fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList("ap-swatch-fill");
            fill.style.backgroundColor = c;
            outer.Add(fill);
            return outer;
        }

        public static void SetSwatchColor(VisualElement swatch, Color c)
        {
            var fill = swatch.Q(className: "ap-swatch-fill");
            if (fill != null) fill.style.backgroundColor = c;
        }
    }
}
