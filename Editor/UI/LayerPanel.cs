using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.UIElements;

namespace AviCreatorEssentials
{
    /// <summary>Small display copies of layer contents and masks for the layer list.</summary>
    public class ThumbnailCache : IDisposable
    {
        const int Size = 56;
        readonly Dictionary<RenderTexture, RenderTexture> m_Map = new Dictionary<RenderTexture, RenderTexture>();
        readonly HashSet<RenderTexture> m_Masks = new HashSet<RenderTexture>();

        public RenderTexture Get(RenderTexture src, bool mask)
        {
            if (src == null) return null;
            if (!m_Map.TryGetValue(src, out var thumb) || thumb == null)
            {
                float aspect = src.height / (float)Mathf.Max(1, src.width);
                int w = aspect <= 1f ? Size : Mathf.Max(8, Mathf.RoundToInt(Size / aspect));
                int h = aspect <= 1f ? Mathf.Max(8, Mathf.RoundToInt(Size * aspect)) : Size;
                var fmt = APUtil.LinearProject ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
                thumb = APUtil.CreateRT(w, h, fmt, "Thumb");
                m_Map[src] = thumb;
                if (mask) m_Masks.Add(src);
                Update(src, thumb, mask);
            }
            return thumb;
        }

        static void Update(RenderTexture src, RenderTexture thumb, bool mask)
        {
            var comp = APResources.Composite;
            var prev = RenderTexture.active;
            var tmp = RenderTexture.GetTemporary(thumb.width, thumb.height, 0, GraphicsFormat.R8G8B8A8_UNorm);
            comp.SetFloat(APIds.CopyMode, mask ? 1f : 0f);
            comp.SetFloat(APIds.SrcToGamma, 0f);
            Graphics.Blit(src, tmp, comp, CompPass.Copy);
            comp.SetFloat(APIds.ViewMode, 0f);
            comp.SetFloat(APIds.ToLinear, GraphicsFormatUtility.IsSRGBFormat(thumb.graphicsFormat) ? 1f : 0f);
            Graphics.Blit(tmp, thumb, comp, CompPass.Output);
            RenderTexture.ReleaseTemporary(tmp);
            RenderTexture.active = prev;
        }

        public void RefreshAll()
        {
            var dead = new List<RenderTexture>();
            foreach (var kv in m_Map)
            {
                if (kv.Key == null || !kv.Key.IsCreated()) { dead.Add(kv.Key); continue; }
                Update(kv.Key, kv.Value, m_Masks.Contains(kv.Key));
            }
            foreach (var d in dead)
            {
                var t = m_Map[d];
                APUtil.Release(ref t);
                m_Map.Remove(d);
            }
        }

        public void Dispose()
        {
            foreach (var kv in m_Map)
            {
                var t = kv.Value;
                APUtil.Release(ref t);
            }
            m_Map.Clear();
            m_Masks.Clear();
        }
    }

    /// <summary>The layer stack of the active material: properties, list with drag reordering, and actions.</summary>
    public class LayerPanel : VisualElement
    {
        readonly PainterContext m_Ctx;
        readonly ThumbnailCache m_Thumbs;
        readonly VisualElement m_Props;
        readonly VisualElement m_List;
        readonly VisualElement m_DropLine;
        DropdownField m_Blend;
        Slider m_Opacity;
        Label m_OpacityValue;
        Toggle m_LockAlpha;
        VisualElement m_FillColorRow;
        VisualElement m_FillSwatch;

        // drag reorder
        PaintLayer m_DragLayer;
        Vector2 m_DragStart;
        bool m_Dragging;
        int m_DropIndex = -1;

        static readonly List<string> s_BlendNames = new List<string>
        {
            "Normal", "Multiply", "Screen", "Overlay", "Soft light", "Hard light", "Darken", "Lighten", "Color dodge",
            "Color burn", "Add", "Subtract", "Difference", "Hue", "Saturation", "Color", "Luminosity", "Normal map combine",
        };

        APSession S => m_Ctx.session;
        TextureSet Set => S?.ActiveSet;

        public LayerPanel(PainterContext ctx, ThumbnailCache thumbs)
        {
            m_Ctx = ctx;
            m_Thumbs = thumbs;
            AddToClassList("ap-layer-panel");

            m_Props = W.Column("ap-layer-props");
            Add(m_Props);
            BuildProps();

            m_List = W.Column("ap-layer-list");
            Add(m_List);
            m_DropLine = new VisualElement { pickingMode = PickingMode.Ignore };
            m_DropLine.AddToClassList("ap-drop-line");

            var actions = W.Row("ap-layer-actions");
            actions.Add(W.IconButton(IconKind.Plus, "New paint layer", () => S.AddPaintLayer(Set)));
            actions.Add(W.IconButton(IconKind.FillLayer, "New fill layer (solid color). Painting on it reveals it through a mask.", () =>
                S.AddFillLayer(Set, m_Ctx.primary, true)));
            actions.Add(W.IconButton(IconKind.Theme, "New adjustment layer: recolor, hue/saturation, or match colors. Uses the current UV selection as its area.", AdjustmentMenu));
            actions.Add(W.IconButton(IconKind.Image, "Import an image: place it on the texture, project it onto the model, or wrap a tiling texture (like a normal map) around the whole model", ImageMenu));
            actions.Add(W.IconButton(IconKind.Mask, "Add a mask to the selected layer", AddMaskMenu));
            actions.Add(W.IconButton(IconKind.Duplicate, "Duplicate layer", () => { if (Set?.ActiveLayer != null) S.DuplicateLayer(Set, Set.ActiveLayer); }));
            actions.Add(W.IconButton(IconKind.Merge, "Merge down", () => { if (Set?.ActiveLayer != null) S.MergeDown(Set, Set.ActiveLayer); }));
            actions.Add(W.Spacer());
            actions.Add(W.IconButton(IconKind.More, "More layer actions", MoreMenu));
            actions.Add(W.IconButton(IconKind.Trash, "Delete layer", () => { if (Set?.ActiveLayer != null) S.DeleteLayer(Set, Set.ActiveLayer); }));
            Add(actions);

            RegisterCallback<PointerMoveEvent>(OnDragMove);
            RegisterCallback<PointerUpEvent>(OnDragUp);
        }

        void BuildProps()
        {
            var row1 = W.Row("ap-layer-prop-row");
            m_Blend = new DropdownField(s_BlendNames, 0) { tooltip = "How this layer mixes with the layers under it" };
            m_Blend.AddToClassList("ap-dropdown");
            m_Blend.AddToClassList("ap-blend");
            m_Blend.RegisterValueChangedCallback(_ =>
            {
                var l = Set?.ActiveLayer;
                if (l == null) return;
                S.EditLayer(Set, l, "Blend mode", x => x.blend = (LayerBlendMode)m_Blend.index);
            });
            row1.Add(m_Blend);
            m_LockAlpha = new Toggle("Lock alpha") { tooltip = "Paint only where this layer already has pixels" };
            m_LockAlpha.AddToClassList("ap-toggle");
            m_LockAlpha.RegisterValueChangedCallback(e =>
            {
                var l = Set?.ActiveLayer;
                if (l == null) return;
                S.EditLayer(Set, l, "Lock alpha", x => x.lockAlpha = e.newValue);
            });
            row1.Add(m_LockAlpha);
            m_Props.Add(row1);

            var row2 = W.Row("ap-layer-prop-row");
            row2.Add(W.Text("Opacity", "ap-slider-label"));
            m_Opacity = new Slider(0f, 1f);
            m_Opacity.AddToClassList("ap-slider");
            m_Opacity.RegisterValueChangedCallback(e =>
            {
                var l = Set?.ActiveLayer;
                if (l == null) return;
                S.EditLayer(Set, l, "Layer opacity", x => x.opacity = e.newValue);
                m_OpacityValue.text = Mathf.RoundToInt(e.newValue * 100f) + "%";
            });
            row2.Add(m_Opacity);
            m_OpacityValue = W.Text("100%", "ap-value");
            row2.Add(m_OpacityValue);
            m_Props.Add(row2);

            m_FillColorRow = W.Row("ap-layer-prop-row");
            m_FillColorRow.Add(W.Text("Fill color", "ap-slider-label"));
            m_FillSwatch = W.Swatch(Color.white, 20, "Click to use the current paint color");
            m_FillSwatch.RegisterCallback<ClickEvent>(_ =>
            {
                var l = Set?.ActiveLayer;
                if (l == null || l.kind != LayerKind.Fill) return;
                S.EditLayer(Set, l, "Fill color", x => x.fillColor = m_Ctx.primary);
            });
            m_FillColorRow.Add(m_FillSwatch);
            m_FillColorRow.Add(W.TextButton("Use paint color", () =>
            {
                var l = Set?.ActiveLayer;
                if (l == null || l.kind != LayerKind.Fill) return;
                S.EditLayer(Set, l, "Fill color", x => x.fillColor = m_Ctx.primary);
            }, "ap-small"));
            m_Props.Add(m_FillColorRow);
        }

        VisualElement m_Channel;

        void BuildChannelSwitch()
        {
            if (m_Channel != null) m_Channel.RemoveFromHierarchy();
            m_Channel = W.Column("ap-channel");
            Insert(0, m_Channel);
            var set = Set;
            var s = S;
            if (set == null || s == null) return;
            var baseSet = s.Sibling(set, false);
            var emSet = s.Sibling(set, true);
            if (baseSet != null && emSet != null)
            {
                m_Channel.Add(new W.Segmented(new[] { "Base color", "Emission" }, () => APSession.IsEmission(set) ? 1 : 0,
                    i => { s.ActiveSet = i == 1 ? emSet : baseSet; m_Ctx.Render(); },
                    new[] { "Paint the color texture (" + baseSet.property + ")", "Paint the glow texture (" + emSet.property + ")" }));
            }
            if (APSession.IsEmission(set))
            {
                m_Channel.Add(W.Text(m_Ctx.view.emissionOnly
                    ? "Painting the emission map. Black doesn't glow; brighter colors glow more."
                    : "Painting the emission map. Turn on the emission view (sun button in the toolbar) to see only what glows.", "ap-hint"));
            }
            if (set.HasSelection)
            {
                var row = W.Row("ap-row-gap", "ap-selection-note");
                row.Add(new Icon(IconKind.Select, 14));
                row.Add(W.Text(set.SelectedCount + " polygons selected", "ap-grow"));
                var mirror = W.TextButton("Mirror…", () => MirrorMenu(set), "ap-small");
                mirror.tooltip = "Copy the selected area to the other side, flipped (e.g. one eye onto the other), as a new layer";
                row.Add(mirror);
                row.Add(W.TextButton("Clear", () => s.ClearSelection(set), "ap-small"));
                m_Channel.Add(row);
            }
        }

        public void Rebuild()
        {
            BuildChannelSwitch();
            m_List.Clear();
            var set = Set;
            if (set == null)
            {
                m_List.Add(W.Text("Pick a material in the Materials section to see its layers.", "ap-empty"));
                SetEnabled(true);
                m_Props.SetEnabled(false);
                return;
            }
            if (!set.initialized)
            {
                m_List.Add(W.Text("This material hasn't been painted yet. Paint on it, or add a layer to start.", "ap-empty"));
                m_Props.SetEnabled(false);
                return;
            }
            m_Props.SetEnabled(true);
            for (int i = set.layers.Count - 1; i >= 0; i--)
                m_List.Add(MakeRow(set, set.layers[i], i));
            RefreshProps();
        }

        public void RefreshProps()
        {
            var l = Set?.ActiveLayer;
            if (l == null) return;
            m_Blend.SetValueWithoutNotify(s_BlendNames[(int)l.blend]);
            m_Opacity.SetValueWithoutNotify(l.opacity);
            m_OpacityValue.text = Mathf.RoundToInt(l.opacity * 100f) + "%";
            m_LockAlpha.SetValueWithoutNotify(l.lockAlpha);
            m_LockAlpha.SetEnabled(l.kind == LayerKind.Paint);
            m_FillColorRow.style.display = l.kind == LayerKind.Fill ? DisplayStyle.Flex : DisplayStyle.None;
            W.SetSwatchColor(m_FillSwatch, l.fillColor);
        }

        VisualElement MakeRow(TextureSet set, PaintLayer layer, int index)
        {
            bool active = index == set.activeLayer;
            var row = W.Row("ap-layer-row");
            W.SetOn(row, active, "ap-active");
            W.SetOn(row, !layer.visible, "ap-hidden");

            var eye = W.IconButton(layer.visible ? IconKind.Eye : IconKind.EyeOff, layer.visible ? "Hide layer" : "Show layer",
                () => S.EditLayer(set, layer, "Layer visibility", x => x.visible = !x.visible), "ap-eye");
            row.Add(eye);

            var thumb = new VisualElement();
            thumb.AddToClassList("ap-thumb");
            W.SetOn(thumb, active && !set.editingMask, "ap-thumb-active");
            thumb.Add(new Image { image = APResources.Checker, scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore });
            if (layer.kind == LayerKind.Fill || (layer.kind == LayerKind.Adjustment && layer.adjMode == AdjustMode.Recolor))
            {
                var fill = new VisualElement { pickingMode = PickingMode.Ignore };
                fill.AddToClassList("ap-thumb-fill");
                fill.style.backgroundColor = layer.kind == LayerKind.Fill ? layer.fillColor : layer.adjColor;
                thumb.Add(fill);
                if (layer.kind == LayerKind.Adjustment)
                {
                    var ic = new Icon(IconKind.Theme, 18) { Tint = APUtil.Luminance(layer.adjColor) > 0.5f ? Color.black : Color.white };
                    ic.style.position = Position.Absolute;
                    ic.style.left = 9;
                    ic.style.top = 9;
                    thumb.Add(ic);
                }
            }
            else if (layer.kind == LayerKind.Adjustment)
            {
                var ic = new Icon(layer.adjMode == AdjustMode.Match ? IconKind.Swap : IconKind.Theme, 22);
                ic.style.position = Position.Absolute;
                ic.style.left = 6;
                ic.style.top = 6;
                thumb.Add(ic);
            }
            else if (layer.content != null)
            {
                thumb.Add(new Image { image = m_Thumbs.Get(layer.content, false), scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore });
            }
            thumb.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                S.SetActiveLayer(set, layer, false);
            });
            row.Add(thumb);

            if (layer.HasMask)
            {
                var mthumb = new VisualElement { tooltip = "Mask: white shows the layer, black hides it. Click to paint the mask; Shift-click to turn it off." };
                mthumb.AddToClassList("ap-thumb");
                mthumb.AddToClassList("ap-mask-thumb");
                W.SetOn(mthumb, active && set.editingMask, "ap-thumb-active");
                W.SetOn(mthumb, !layer.maskEnabled, "ap-mask-off");
                mthumb.Add(new Image { image = m_Thumbs.Get(layer.mask, true), scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore });
                mthumb.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0) return;
                    if (e.shiftKey) S.EditLayer(set, layer, "Mask on/off", x => x.maskEnabled = !x.maskEnabled);
                    else S.SetActiveLayer(set, layer, true);
                    e.StopPropagation();
                });
                row.Add(mthumb);
            }

            var info = W.Column("ap-layer-info");
            var name = W.Text(layer.name, "ap-layer-name");
            info.Add(name);
            string sub = layer.kind == LayerKind.Fill ? "Fill" : layer.kind == LayerKind.Image ? (layer.IsFloating ? "Floating · Enter applies, Esc cancels" : layer.placement != null && layer.placement.projected ? "Image · projected" : "Image") :
                layer.kind == LayerKind.Adjustment ? (layer.adjMode == AdjustMode.Recolor ? "Recolor" : layer.adjMode == AdjustMode.Match ? (layer.refValid ? "Match colors" : "Match colors · no reference yet") : "Hue / saturation") :
                "Paint";
            if (layer.blend != LayerBlendMode.Normal) sub += " · " + s_BlendNames[(int)layer.blend];
            if (layer.opacity < 0.999f) sub += " · " + Mathf.RoundToInt(layer.opacity * 100f) + "%";
            if (layer.lockAlpha) sub += " · alpha locked";
            if (active && set.editingMask) sub = "Painting the mask";
            info.Add(W.Text(sub, "ap-layer-sub"));
            row.Add(info);

            name.RegisterCallback<MouseDownEvent>(e =>
            {
                if (e.clickCount == 2) BeginRename(set, layer, info, name);
            });

            row.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                if (set.activeLayer != index || set.editingMask) S.SetActiveLayer(set, layer, false);
                // Opening a fill or recolor layer loads its color into the brush (after it is active,
                // so the wheel's edit-the-layer link doesn't write the color onto the previous layer).
                if (layer.HasEditableColor && set.ActiveLayer == layer && !set.editingMask && layer.EditableColor != m_Ctx.primary)
                    m_Ctx.SetPrimary(layer.EditableColor, true);
                m_DragLayer = layer;
                m_DragStart = e.position;
                m_Dragging = false;
            });

            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                S.SetActiveLayer(set, layer, set.editingMask && set.ActiveLayer == layer);
                FillMenu(evt.menu, set, layer);
            }));
            row.userData = index;
            return row;
        }

        void BeginRename(TextureSet set, PaintLayer layer, VisualElement info, Label name)
        {
            var field = new TextField { value = layer.name };
            field.AddToClassList("ap-rename");
            int i = info.IndexOf(name);
            info.Insert(i, field);
            name.style.display = DisplayStyle.None;
            field.Focus();
            field.SelectAll();
            bool done = false;
            void Commit()
            {
                if (done) return;
                done = true;
                string v = field.value.Trim();
                if (v.Length > 0 && v != layer.name) S.EditLayer(set, layer, "Rename layer", x => x.name = v);
                else Rebuild();
            }
            field.RegisterCallback<FocusOutEvent>(_ => Commit());
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Commit(); e.StopPropagation(); }
                else if (e.keyCode == KeyCode.Escape) { done = true; Rebuild(); e.StopPropagation(); }
            }, TrickleDown.TrickleDown);
        }

        // ------------------------------------------------------------------ drag reorder

        void OnDragMove(PointerMoveEvent e)
        {
            if (m_DragLayer == null || (e.pressedButtons & 1) == 0)
            {
                if (m_DragLayer != null && (e.pressedButtons & 1) == 0) CancelDrag();
                return;
            }
            if (!m_Dragging)
            {
                if (Vector2.Distance(e.position, m_DragStart) < 5f) return;
                m_Dragging = true;
                this.CapturePointer(e.pointerId);
                m_List.Add(m_DropLine);
            }
            // Find the gap under the pointer.
            var local = m_List.WorldToLocal(e.position);
            int rows = 0;
            int gap = 0;
            foreach (var child in m_List.Children())
            {
                if (child == m_DropLine || !(child.userData is int)) continue;
                if (local.y > child.layout.center.y) gap = rows + 1;
                rows++;
            }
            float lineY = 0f;
            int k = 0;
            foreach (var child in m_List.Children())
            {
                if (child == m_DropLine || !(child.userData is int)) continue;
                if (k == gap) { lineY = child.layout.yMin; break; }
                lineY = child.layout.yMax;
                k++;
            }
            m_DropLine.style.top = lineY - 1f;
            int count = Set.layers.Count;
            m_DropIndex = count - gap;   // list is top-first
        }

        void OnDragUp(PointerUpEvent e)
        {
            if (m_DragLayer == null) return;
            if (m_Dragging && Set != null)
            {
                int from = Set.layers.IndexOf(m_DragLayer);
                int to = m_DropIndex;
                if (to > from) to -= 1;
                to = Mathf.Clamp(to, 0, Set.layers.Count - 1);
                if (to != from) S.MoveLayer(Set, m_DragLayer, to);
            }
            if (this.HasPointerCapture(e.pointerId)) this.ReleasePointer(e.pointerId);
            CancelDrag();
        }

        void CancelDrag()
        {
            m_DragLayer = null;
            m_Dragging = false;
            if (m_DropLine.parent != null) m_DropLine.RemoveFromHierarchy();
        }

        // ------------------------------------------------------------------ menus

        void FillMenu(DropdownMenu menu, TextureSet set, PaintLayer layer)
        {
            var s = S;
            menu.AppendAction("Rename", _ =>
            {
                foreach (var child in m_List.Children())
                {
                    if (!(child.userData is int idx) || set.layers.IndexOf(layer) != idx) continue;
                    var info = child.Q(className: "ap-layer-info");
                    var name = info?.Q<Label>(className: "ap-layer-name");
                    if (name != null) BeginRename(set, layer, info, name);
                }
            });
            menu.AppendAction("Duplicate", _ => s.DuplicateLayer(set, layer));
            menu.AppendAction("Merge down", _ => s.MergeDown(set, layer),
                set.layers.IndexOf(layer) > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction(set.editingMask && layer.HasMask ? "Clear mask (show everything)" : "Clear layer", _ => s.ClearLayer(set, layer));
            menu.AppendSeparator();
            if (!layer.HasMask)
            {
                menu.AppendAction("Add mask/White (show all)", _ => s.AddMask(set, layer, 1f));
                menu.AppendAction("Add mask/Black (hide all)", _ => s.AddMask(set, layer, 0f));
            }
            else
            {
                menu.AppendAction("Paint the mask", _ => s.SetActiveLayer(set, layer, true));
                menu.AppendAction(layer.maskEnabled ? "Turn mask off" : "Turn mask on", _ => s.EditLayer(set, layer, "Mask on/off", x => x.maskEnabled = !x.maskEnabled));
                menu.AppendAction("Invert mask", _ => s.InvertMask(set, layer));
                menu.AppendAction("Apply mask to layer", _ => s.ApplyMask(set, layer),
                    layer.kind == LayerKind.Paint ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                menu.AppendAction("Delete mask", _ => s.DeleteMask(set, layer));
            }
            menu.AppendSeparator();
            if (layer.kind == LayerKind.Fill) menu.AppendAction("Convert to paint layer", _ => s.ConvertFillToPaint(set, layer));
            if (layer.kind == LayerKind.Image) menu.AppendAction("Rasterize image", _ => s.RasterizeImage(set, layer));
            menu.AppendAction("New layer in every material", _ => s.AddPaintLayerToAll("Layer " + DateTime.Now.ToString("HHmmss")));
            menu.AppendSeparator();
            menu.AppendAction("Delete layer", _ => s.DeleteLayer(set, layer),
                set.layers.Count > 1 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        void MoreMenu()
        {
            var set = Set;
            var layer = set?.ActiveLayer;
            if (layer == null) return;
            var menu = new GenericDropdownMenu();
            var dm = new DropdownMenu();
            FillMenu(dm, set, layer);
            foreach (var item in dm.MenuItems())
            {
                if (item is DropdownMenuAction a)
                {
                    a.UpdateActionStatus(null);
                    bool disabled = (a.status & DropdownMenuAction.Status.Disabled) != 0;
                    if (disabled) menu.AddDisabledItem(a.name, false);
                    else menu.AddItem(a.name, false, () => a.Execute());
                }
                else if (item is DropdownMenuSeparator)
                {
                    menu.AddSeparator("");
                }
            }
            menu.AddSeparator("");
            AddMirrorItems(menu, set);
            var anchor = this.Q(className: "ap-layer-actions");
            menu.DropDown(anchor != null ? anchor.worldBound : worldBound, this, true);
        }

        void AdjustmentMenu()
        {
            var set = Set;
            if (set == null) return;
            var menu = new GenericDropdownMenu();
            string where = set.HasSelection ? " (selected area)" : " (whole texture)";
            menu.AddItem("Recolor" + where, false, () => { S.AddAdjustmentLayer(set, AdjustMode.Recolor, m_Ctx.primary); m_Ctx.Render(); });
            menu.AddItem("Hue / Saturation / Lightness" + where, false, () => { S.AddAdjustmentLayer(set, AdjustMode.HueSatLight, Color.white); m_Ctx.Render(); });
            menu.AddItem("Match colors" + where, false, () => { S.AddAdjustmentLayer(set, AdjustMode.Match, Color.white); m_Ctx.Render(); });

            var anchor = this.Q(className: "ap-layer-actions");
            menu.DropDown(anchor != null ? anchor.worldBound : worldBound, this, true);
        }

        /// <summary>Mirror options: flip half the texture (or the selection) in 2D, or match the other side through 3D.</summary>
        void MirrorMenu(TextureSet set)
        {
            if (set == null) return;
            var menu = new GenericDropdownMenu();
            AddMirrorItems(menu, set);
            if (set.HasSelection)
            {
                menu.AddSeparator("");
                menu.AddItem("Selected area → other side of the model (3D)", false, () => MirrorSelection(set));
            }
            var anchor = this.Q(className: "ap-layer-actions");
            menu.DropDown(anchor != null ? anchor.worldBound : worldBound, this, true);
        }

        void AddMirrorItems(GenericDropdownMenu menu, TextureSet set)
        {
            if (set.HasSelection)
            {
                menu.AddItem("Copy selected area to the mirrored spot (flip left ↔ right)", false, () => MirrorHalf(set, APSession.TextureMirror.LeftToRight));
                menu.AddItem("Copy selected area to the mirrored spot (flip top ↔ bottom)", false, () => MirrorHalf(set, APSession.TextureMirror.BottomToTop));
                return;
            }
            menu.AddItem("Mirror texture: left half → right half", false, () => MirrorHalf(set, APSession.TextureMirror.LeftToRight));
            menu.AddItem("Mirror texture: right half → left half", false, () => MirrorHalf(set, APSession.TextureMirror.RightToLeft));
            menu.AddItem("Mirror texture: bottom half → top half", false, () => MirrorHalf(set, APSession.TextureMirror.BottomToTop));
            menu.AddItem("Mirror texture: top half → bottom half", false, () => MirrorHalf(set, APSession.TextureMirror.TopToBottom));
        }

        void MirrorHalf(TextureSet set, APSession.TextureMirror mode)
        {
            string msg = S.MirrorTextureHalf(set, mode);
            m_Ctx.Render();
            m_Ctx.ShowHint(msg);
        }

        void MirrorSelection(TextureSet set)
        {
            string msg;
            try
            {
                EditorUtility.DisplayProgressBar("Avi Creator Essentials", "Mirroring to the other side…", 0.5f);
                msg = S.MirrorSelectionToOtherSide(set);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            m_Ctx.Render();
            m_Ctx.ShowHint(msg);
        }

        void AddMaskMenu()
        {
            var set = Set;
            var layer = set?.ActiveLayer;
            if (layer == null) return;
            if (layer.HasMask)
            {
                S.SetActiveLayer(set, layer, !set.editingMask);
                return;
            }
            var menu = new GenericDropdownMenu();
            menu.AddItem("White mask (show all)", false, () => S.AddMask(set, layer, 1f));
            menu.AddItem("Black mask (hide all)", false, () => S.AddMask(set, layer, 0f));
            var anchor = this.Q(className: "ap-layer-actions");
            menu.DropDown(anchor != null ? anchor.worldBound : worldBound, this, true);
        }

        void ImageMenu()
        {
            if (Set == null) return;
            var menu = new GenericDropdownMenu();
            menu.AddItem("Image layer (place on texture or model)…", false, ImportImage);
            menu.AddItem("Wrap tiling texture around the model…", false, ImportWrapTexture);
            var anchor = this.Q(className: "ap-layer-actions");
            menu.DropDown(anchor != null ? anchor.worldBound : worldBound, this, true);
        }

        /// <summary>
        /// Wraps a tiling image (cardboard, fabric, a normal map...) around the model from every side and bakes it into
        /// the UV layout, like a shader's world/local position mapping but stuck to the model.
        /// </summary>
        public void ImportWrapTexture()
        {
            var set = Set;
            if (set == null) return;
            string path = EditorUtility.OpenFilePanelWithFilters("Wrap a texture around the model", EditorPrefs.GetString("AviCreatorEssentials.LastImageDir", ""),
                new[] { "Images", "png,jpg,jpeg", "All files", "*" });
            if (string.IsNullOrEmpty(path)) return;
            EditorPrefs.SetString("AviCreatorEssentials.LastImageDir", Path.GetDirectoryName(path));
            var tex = APUtil.LoadImageFile(path);
            if (tex == null)
            {
                EditorUtility.DisplayDialog("Avi Creator Essentials", "That file couldn't be read as a PNG or JPG image.", "OK");
                return;
            }
            bool normal = APUtil.LooksLikeNormalMap(tex) || APSession.IsNormalMap(set);
            string what = normal
                ? "This looks like a normal map, so it goes into the materials' normal map slots (one is added where a material has none), on top of any normal map already there."
                : "The image goes into the materials' main textures as a new layer.";
            int choice = EditorUtility.DisplayDialogComplex("Wrap texture around the model",
                what + "\n\nWrap it around every material, or only " + (set.originalMaterial != null ? set.originalMaterial.name : "this material") + "?",
                "Every material", "Cancel", "Only this material");
            if (choice == 1)
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return;
            }
            var targets = S.WrapTargets(set, choice == 0, normal, out int skipped);
            if (targets.Count == 0)
            {
                UnityEngine.Object.DestroyImmediate(tex);
                EditorUtility.DisplayDialog("Avi Creator Essentials", normal
                    ? "None of these materials' shaders has a normal map slot."
                    : "None of these materials has a texture slot to paint.", "OK");
                return;
            }
            var p = new ImagePlacement { wrap = true, normalMap = normal };
            var b = S.meshes.VisibleBounds();
            p.tileSize = Mathf.Clamp(b.size.y * 0.15f, 0.02f, 1f);
            S.AddWrapLayers(targets, tex, p);
            var show = targets.Find(t => t.originalMaterial == set.originalMaterial) ?? targets[0];
            S.ActiveSet = show;
            m_Ctx.Tool = PaintTool.Image;
            m_Ctx.Render();
            m_Ctx.ShowHint("Wrapped onto " + targets.Count + (targets.Count == 1 ? " texture" : " textures") +
                           (skipped > 0 ? " (" + skipped + " skipped: no " + (normal ? "normal map" : "texture") + " slot)" : "") +
                           ". Drag on the model to slide it, Shift-drag to scale, Ctrl-drag to rotate, then Bake.");
        }

        public void ImportImage()
        {
            var set = Set;
            if (set == null) return;
            string path = EditorUtility.OpenFilePanelWithFilters("Import image", EditorPrefs.GetString("AviCreatorEssentials.LastImageDir", ""),
                new[] { "Images", "png,jpg,jpeg", "All files", "*" });
            if (string.IsNullOrEmpty(path)) return;
            EditorPrefs.SetString("AviCreatorEssentials.LastImageDir", Path.GetDirectoryName(path));
            var tex = APUtil.LoadImageFile(path);
            if (tex == null)
            {
                EditorUtility.DisplayDialog("Avi Creator Essentials", "That file couldn't be read as a PNG or JPG image.", "OK");
                return;
            }
            AddImage(set, tex, null);
        }

        /// <summary>Adds an image centred in texture space, or projected at a hit point when one is given.</summary>
        public void AddImage(TextureSet set, Texture2D tex, MeshHit? hit, ViewCamera? cam = null)
        {
            var p = new ImagePlacement();
            float aspectTex = set.height / (float)Mathf.Max(1, set.width);
            float imgAspect = tex.height / (float)Mathf.Max(1, tex.width);
            float w = 0.5f;
            p.uvSize = new Vector2(w, w * imgAspect / aspectTex);
            if (hit.HasValue && cam.HasValue)
            {
                var h = hit.Value;
                var c = cam.Value;
                p.projected = true;
                p.worldPos = h.point;
                p.normal = Vector3.Dot(h.normal, c.position - h.point) >= 0f ? h.normal : -h.normal;
                p.up = c.rotation * Vector3.up;
                p.worldWidth = Mathf.Clamp(h.distance * 0.25f, 0.02f, 0.5f);
                p.depth = p.worldWidth * 0.5f;
            }
            S.AddImageLayer(set, tex, p);
            m_Ctx.Tool = PaintTool.Image;
        }

        public void RefreshThumbnails()
        {
            m_Thumbs.RefreshAll();
            foreach (var img in m_List.Query<Image>().ToList()) img.MarkDirtyRepaint();
        }
    }
}
