using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    public enum PaintTool
    {
        Brush,
        Eraser,
        Fill,
        Picker,
        Image,
        Select,
        Transform,
    }

    public enum FillMode
    {
        UVIsland,
        MeshPiece,
        Material,
        WholeTexture,
        SimilarColor,
    }

    public enum PoseTool
    {
        Rotate,
        Move,
    }

    /// <summary>Recent colors and saved swatches, kept in EditorPrefs.</summary>
    public class ColorLibrary
    {
        const string HistoryKey = "AviCreatorEssentials.ColorHistory";
        const string SwatchKey = "AviCreatorEssentials.Swatches";
        public const int MaxHistory = 24;

        public readonly List<Color> history = new List<Color>();
        public readonly List<Color> swatches = new List<Color>();
        public event Action Changed;

        public ColorLibrary()
        {
            Read(HistoryKey, history);
            Read(SwatchKey, swatches);
            if (swatches.Count == 0)
            {
                foreach (var hex in new[] { "FFFFFF", "000000", "F2D7C9", "E8B4A0", "FF6B9A", "9B6BFF", "4FC3F7", "69F0AE", "FFD54F", "FF7043" })
                    if (APUtil.TryParseHex(hex, out var c)) swatches.Add(c);
            }
        }

        static void Read(string key, List<Color> into)
        {
            into.Clear();
            foreach (var hex in EditorPrefs.GetString(key, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (APUtil.TryParseHex(hex, out var c)) into.Add(c);
        }

        static void Write(string key, List<Color> from)
        {
            var parts = new List<string>();
            foreach (var c in from) parts.Add(APUtil.ToHex(c, true));
            EditorPrefs.SetString(key, string.Join(";", parts));
        }

        public void Use(Color c)
        {
            string hex = APUtil.ToHex(c, true);
            for (int i = history.Count - 1; i >= 0; i--)
                if (APUtil.ToHex(history[i], true) == hex) history.RemoveAt(i);
            history.Insert(0, c);
            while (history.Count > MaxHistory) history.RemoveAt(history.Count - 1);
            Write(HistoryKey, history);
            Changed?.Invoke();
        }

        public void AddSwatch(Color c)
        {
            string hex = APUtil.ToHex(c, true);
            foreach (var s in swatches)
                if (APUtil.ToHex(s, true) == hex) return;
            swatches.Add(c);
            Write(SwatchKey, swatches);
            Changed?.Invoke();
        }

        public void RemoveSwatch(int index)
        {
            if (index < 0 || index >= swatches.Count) return;
            swatches.RemoveAt(index);
            Write(SwatchKey, swatches);
            Changed?.Invoke();
        }

        public void ClearHistory()
        {
            history.Clear();
            Write(HistoryKey, history);
            Changed?.Invoke();
        }
    }

    /// <summary>State shared by the viewports and panels: current tool, colors and brush.</summary>
    public class PainterContext
    {
        public APSession session;
        public BrushSettings brush;
        public ViewSettings view;
        public readonly ColorLibrary colors = new ColorLibrary();

        PaintTool m_Tool = PaintTool.Brush;
        bool m_PoseMode;
        public PoseTool poseTool = PoseTool.Rotate;
        public Color primary = Color.white;
        public Color secondary = Color.black;
        public FillMode fillMode = FillMode.UVIsland;
        public float fillTolerance = 0.12f;
        public bool fillContiguous = true;
        public bool fillSampleLayer;
        public bool selectIslands = true;   // false: individual polygons
        public bool selectLasso;            // false: box
        public bool transformWarp;          // Transform tool: warp grid instead of move/scale/rotate handles
        public bool transformCopy;          // lift a copy and leave the original in place
        public float transformTolerance = 0.12f;
        public bool transformContiguous = true;
        public bool strokeActive;

        public event Action ToolChanged;
        public event Action ColorChanged;
        public event Action BrushChanged;
        public event Action<string> Hint;
        public Action RequestRender;
        public Action RequestRender2D;
        public Action<Vector2> OpenQuickColor;   // world (panel) position of the right-click

        public PaintTool Tool
        {
            get => m_Tool;
            set
            {
                if (m_Tool == value) return;
                m_Tool = value;
                ToolChanged?.Invoke();
            }
        }

        public bool PoseMode
        {
            get => m_PoseMode;
            set
            {
                if (m_PoseMode == value) return;
                m_PoseMode = value;
                ToolChanged?.Invoke();
                RequestRender?.Invoke();
            }
        }

        public BrushTip Tip => BrushLibrary.Find(brush.tip);

        public void SetPrimary(Color c, bool record = false)
        {
            primary = c;
            if (record) colors.Use(c);
            ColorChanged?.Invoke();
        }

        public void SetSecondary(Color c)
        {
            secondary = c;
            ColorChanged?.Invoke();
        }

        public void SwapColors()
        {
            var t = primary;
            primary = secondary;
            secondary = t;
            ColorChanged?.Invoke();
        }

        public void NotifyBrushChanged()
        {
            brush.Save();
            BrushChanged?.Invoke();
        }

        public void ShowHint(string text) => Hint?.Invoke(text);

        public void Render()
        {
            RequestRender?.Invoke();
            RequestRender2D?.Invoke();
        }
    }
}
