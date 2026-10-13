using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    // ====================================================================== brush

    public class BrushPanel : VisualElement
    {
        readonly PainterContext m_Ctx;
        readonly VisualElement m_Tips;
        readonly List<VisualElement> m_Refreshables = new List<VisualElement>();
        readonly VisualElement m_FillBox;
        readonly VisualElement m_SelectBox;
        readonly VisualElement m_TransformBox;
        readonly VisualElement m_BrushBox;

        BrushSettings B => m_Ctx.brush;

        public BrushPanel(PainterContext ctx)
        {
            m_Ctx = ctx;
            AddToClassList("ap-brush-panel");

            m_FillBox = W.Column("ap-fill-options");
            m_FillBox.Add(W.Text("Fill fills one area with the paint color in one click. Shift-click fills with transparency.", "ap-hint"));
            var modes = new List<string> { "UV island", "Connected mesh part", "Whole material", "Entire texture", "Similar color" };
            m_FillBox.Add(Track(new W.DropdownRow("Fill", modes, () => (int)m_Ctx.fillMode, i => m_Ctx.fillMode = (FillMode)i,
                "What one click fills: a UV island, a connected piece of the mesh, every part using this material, the whole texture, or pixels of a similar color")));
            m_FillBox.Add(Track(new W.SliderRow("Tolerance", 0f, 1f, () => m_Ctx.fillTolerance, v => m_Ctx.fillTolerance = v, 100f,
                "Similar color: how different a pixel may be and still be filled")));
            m_FillBox.Add(Track(new W.ToggleRow("Only touching pixels", () => m_Ctx.fillContiguous, v => m_Ctx.fillContiguous = v,
                "Similar color: off fills every matching pixel in the texture")));
            m_FillBox.Add(Track(new W.ToggleRow("Sample this layer only", () => m_Ctx.fillSampleLayer, v => m_Ctx.fillSampleLayer = v,
                "Similar color: compare against the selected layer instead of the finished texture")));
            m_FillBox.Add(Track(new W.ToggleRow("Mirror fill (symmetry)", () => B.symmetry, v => { B.symmetry = v; Changed(); })));
            Add(m_FillBox);

            m_SelectBox = W.Column("ap-select-options");
            m_SelectBox.Add(W.Text("Pick UV islands or polygons on the texture or the model. While anything is selected, brushes, " +
                                   "erasers and fills only paint inside it. Click to pick, drag for a box or lasso. Shift adds, Ctrl removes.", "ap-hint"));
            m_SelectBox.Add(Track(new W.Segmented(new[] { "UV islands", "Polygons" }, () => m_Ctx.selectIslands ? 0 : 1,
                i => { m_Ctx.selectIslands = i == 0; Changed(); },
                new[] { "Each pick selects the whole connected UV island", "Each pick selects single triangles" })));
            m_SelectBox.Add(Track(new W.Segmented(new[] { "Box", "Lasso" }, () => m_Ctx.selectLasso ? 1 : 0,
                i => { m_Ctx.selectLasso = i == 1; Changed(); },
                new[] { "Drag a rectangle", "Draw a free shape" })));
            var selButtons = W.Row("ap-row-gap", "ap-wrap", "ap-select-buttons");
            selButtons.Add(W.TextButton("Select all", () => m_Ctx.session?.SelectAll(m_Ctx.session.ActiveSet), "ap-small"));
            selButtons.Add(W.TextButton("Invert", () => m_Ctx.session?.InvertSelection(m_Ctx.session.ActiveSet), "ap-small"));
            selButtons.Add(W.TextButton("Clear", () => m_Ctx.session?.ClearSelection(m_Ctx.session.ActiveSet), "ap-small"));
            m_SelectBox.Add(selButtons);
            m_SelectBox.Add(Track(new W.ToggleRow("Dim unselected parts on the model", () => m_Ctx.view.highlightSelection, v =>
            {
                m_Ctx.view.highlightSelection = v;
                m_Ctx.view.Save();
                if (m_Ctx.session != null)
                {
                    m_Ctx.session.engine.highlightSelection = v;
                    foreach (var s in m_Ctx.session.sets) s.dirty = true;
                }
                m_Ctx.Render();
            })));
            Add(m_SelectBox);

            m_TransformBox = W.Column("ap-transform-options");
            m_TransformBox.Add(W.Text("Pick up part of the selected paint layer and move it. In the texture view, draw a shape around " +
                                      "what to move, or click a color to take every touching pixel of that color (in the 3D view, click). " +
                                      "Then drag it, pull the corners to scale (Shift: stretch), the edges to stretch, and the top handle to rotate.", "ap-hint"));
            m_TransformBox.Add(Track(new W.Segmented(new[] { "Move / scale / rotate", "Warp" }, () => m_Ctx.transformWarp ? 1 : 0,
                i => { m_Ctx.transformWarp = i == 1; Changed(); },
                new[] { "Handles for moving, scaling, stretching and rotating", "A 3 × 3 grid of points: drag them to bend and distort the piece" })));
            m_TransformBox.Add(Track(new W.ToggleRow("Keep the original (copy)", () => m_Ctx.transformCopy, v => m_Ctx.transformCopy = v,
                "Pick up a copy and leave the original pixels where they are")));
            m_TransformBox.Add(Track(new W.SliderRow("Color tolerance", 0f, 1f, () => m_Ctx.transformTolerance, v => m_Ctx.transformTolerance = v, 100f,
                "Click-to-pick: how different a pixel may be and still be picked up")));
            m_TransformBox.Add(Track(new W.ToggleRow("Only touching pixels", () => m_Ctx.transformContiguous, v => m_Ctx.transformContiguous = v,
                "Click-to-pick: off picks up every pixel of that color on the layer")));
            var liftRow = W.Row("ap-row-gap", "ap-wrap");
            liftRow.Add(W.TextButton("Pick up UV selection", () => LiftWith(set => m_Ctx.session.SelectionMask(set),
                "Select UV islands or polygons first (Select tool, S)."), "ap-small"));
            liftRow.Add(W.TextButton("Pick up whole layer", () => LiftWith(set =>
            {
                var all = new byte[set.width * set.height];
                for (int i = 0; i < all.Length; i++) all[i] = 255;
                return all;
            }, null), "ap-small"));
            m_TransformBox.Add(liftRow);
            var tfRow = W.Row("ap-row-gap", "ap-wrap");
            tfRow.Add(W.TextButton("Apply (Enter)", () => WithFloating((s, set, f) => s.ApplyFloating(set, f), true), "ap-small", "ap-accent"));
            tfRow.Add(W.TextButton("Cancel (Esc)", () => WithFloating((s, set, f) => s.CancelFloating(set, f), true), "ap-small"));
            tfRow.Add(W.TextButton("Flip", () => WithFloating((s, set, f) => s.EditPlacement(set, f, "flip", pl => pl.flipX = !pl.flipX)), "ap-small"));
            tfRow.Add(W.TextButton("Reset warp", () => WithFloating((s, set, f) => s.EditPlacement(set, f, "warp reset", pl => pl.warp = null)), "ap-small"));
            m_TransformBox.Add(tfRow);
            Add(m_TransformBox);

            m_BrushBox = W.Column();
            Add(m_BrushBox);

            m_Tips = W.Row("ap-tips");
            m_BrushBox.Add(m_Tips);

            m_BrushBox.Add(Track(new W.SliderRow("Size (cm)", 0.0005f, 0.5f, () => B.radius * 2f, v => { B.radius = v * 0.5f; Changed(); }, 100f,
                "Brush diameter on the model, in centimetres. Ctrl+right-drag or Ctrl+scroll in the view also resizes.", true)));
            m_BrushBox.Add(Track(new W.SliderRow("2D size (px)", 0.0002f, 0.5f, () => B.radius2D, v => { B.radius2D = v; Changed(); }, 2048f,
                "Brush radius in the texture view, in pixels of a 2048 texture", true)));
            m_BrushBox.Add(Track(new W.SliderRow("Hardness", 0f, 1f, () => B.hardness, v => { B.hardness = v; Changed(); }, 100f, "Edge softness of the round brush")));
            m_BrushBox.Add(Track(new W.SliderRow("Opacity", 0f, 1f, () => B.opacity, v => { B.opacity = v; Changed(); }, 100f,
                "Most a single stroke can cover. Keys 1–0 set 10–100%.")));
            m_BrushBox.Add(Track(new W.SliderRow("Flow", 0.01f, 1f, () => B.flow, v => { B.flow = v; Changed(); }, 100f,
                "How much each dab adds; low flow builds up gradually within a stroke")));
            m_BrushBox.Add(Track(new W.SliderRow("Spacing", 0.02f, 2f, () => B.spacing, v => { B.spacing = v; Changed(); }, 100f,
                "Distance between dabs as a percentage of the brush size")));

            var stab = new Foldout { text = "Stabilizer", value = EditorPrefs.GetBool("AviCreatorEssentials.Fold.Stabilizer", true) };
            stab.AddToClassList("ap-foldout");
            stab.RegisterValueChangedCallback(e => EditorPrefs.SetBool("AviCreatorEssentials.Fold.Stabilizer", e.newValue));
            stab.Add(Track(new W.SliderRow("Intensity", 0f, 1f, () => B.stabilizer, v => { B.stabilizer = v; Changed(); }, 100f,
                "How strongly wobble is smoothed out of your strokes. 0 turns the stabilizer off.")));
            stab.Add(Track(new W.Segmented(new[] { "Smooth", "Rope" }, () => B.stabilizerMode == StabilizerMode.Smooth ? 0 : 1,
                i => { B.stabilizerMode = i == 0 ? StabilizerMode.Smooth : StabilizerMode.Rope; Changed(); }, new[]
                {
                    "The brush follows an average of your recent movement, ironing out shaky lines",
                    "Lazy mouse: the brush trails the cursor on a string and only moves once the string is pulled tight",
                })));
            stab.Add(Track(new W.ToggleRow("Finish line on release", () => B.stabilizerCatchUp, v => { B.stabilizerCatchUp = v; Changed(); },
                "When you let go, the stroke continues to where the cursor is instead of stopping behind it")));
            m_BrushBox.Add(stab);

            var dyn = new Foldout { text = "Dynamics", value = EditorPrefs.GetBool("AviCreatorEssentials.Fold.Dynamics", false) };
            dyn.AddToClassList("ap-foldout");
            dyn.RegisterValueChangedCallback(e => EditorPrefs.SetBool("AviCreatorEssentials.Fold.Dynamics", e.newValue));
            dyn.Add(Track(new W.ToggleRow("Pen pressure → size", () => B.pressureSize, v => { B.pressureSize = v; Changed(); })));
            dyn.Add(Track(new W.ToggleRow("Pen pressure → flow", () => B.pressureOpacity, v => { B.pressureOpacity = v; Changed(); })));
            dyn.Add(Track(new W.ToggleRow("Rotate with stroke", () => B.followStroke, v => { B.followStroke = v; Changed(); },
                "Turns textured brushes to follow the stroke direction")));
            dyn.Add(Track(new W.SliderRow("Angle", -180f, 180f, () => B.angle, v => { B.angle = v; Changed(); })));
            dyn.Add(Track(new W.SliderRow("Angle jitter", 0f, 180f, () => B.angleJitter, v => { B.angleJitter = v; Changed(); })));
            dyn.Add(Track(new W.SliderRow("Size jitter", 0f, 1f, () => B.sizeJitter, v => { B.sizeJitter = v; Changed(); }, 100f)));
            dyn.Add(Track(new W.SliderRow("Flow jitter", 0f, 1f, () => B.opacityJitter, v => { B.opacityJitter = v; Changed(); }, 100f)));
            dyn.Add(Track(new W.SliderRow("Scatter", 0f, 2f, () => B.scatter, v => { B.scatter = v; Changed(); }, 100f)));
            m_BrushBox.Add(dyn);

            var proj = new Foldout { text = "Projection", value = EditorPrefs.GetBool("AviCreatorEssentials.Fold.Projection", false) };
            proj.AddToClassList("ap-foldout");
            proj.RegisterValueChangedCallback(e => EditorPrefs.SetBool("AviCreatorEssentials.Fold.Projection", e.newValue));
            proj.Add(Track(new W.DropdownRow("Mode", new List<string> { "Sphere (3D distance)", "Through the screen" },
                () => (int)B.projection, i => { B.projection = (BrushProjection)i; Changed(); },
                "Sphere paints everything within the brush in 3D. Through the screen paints what's under the cursor like a stencil.")));
            proj.Add(Track(new W.ToggleRow("Only visible surfaces", () => B.occlusion, v => { B.occlusion = v; Changed(); },
                "Don't paint parts hidden behind other geometry (inside of sleeves, behind hair)")));
            proj.Add(Track(new W.ToggleRow("Skip back-facing surfaces", () => B.backfaceCulling, v => { B.backfaceCulling = v; Changed(); })));
            proj.Add(Track(new W.SliderRow("Facing limit", 30f, 120f, () => B.backfaceAngle, v => { B.backfaceAngle = v; Changed(); }, 1f,
                "Surfaces turned further away from the camera than this angle are not painted")));
            proj.Add(Track(new W.ToggleRow("Align brush to surface", () => B.alignToSurface, v => { B.alignToSurface = v; Changed(); },
                "Textured brushes lie flat on the surface instead of facing the screen")));
            proj.Add(Track(new W.SliderRow("Depth", 0.1f, 4f, () => B.depthRange, v => { B.depthRange = v; Changed(); }, 100f,
                "Through the screen: how deep the brush reaches, in brush radii")));
            proj.Add(Track(new W.DropdownRow("Mirror axis", new List<string> { "X (left ↔ right)", "Y (up ↔ down)", "Z (front ↔ back)" },
                () => B.symmetryAxis, i => { B.symmetryAxis = i; Changed(); })));
            proj.Add(Track(new W.ToggleRow("Paint every material", () => B.paintAllMaterials, v => { B.paintAllMaterials = v; Changed(); },
                "Strokes continue across body parts that use different materials (each gets painted on its selected layer)")));
            proj.Add(Track(new W.ToggleRow("Switch to material under cursor", () => B.autoSelectMaterial, v => { B.autoSelectMaterial = v; Changed(); })));
            proj.Add(Track(new W.ToggleRow("2D: mirror left ↔ right", () => B.mirrorU2D, v => { B.mirrorU2D = v; Changed(); })));
            proj.Add(Track(new W.ToggleRow("2D: stay inside UV islands", () => B.clipToIslands2D, v => { B.clipToIslands2D = v; Changed(); })));
            m_BrushBox.Add(proj);

            BrushLibrary.Changed += RebuildTips;
            m_Ctx.BrushChanged += Refresh;
            m_Ctx.ToolChanged += RefreshMode;
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                BrushLibrary.Changed -= RebuildTips;
                m_Ctx.BrushChanged -= Refresh;
                m_Ctx.ToolChanged -= RefreshMode;
            });
            RebuildTips();
            RefreshMode();
        }

        T Track<T>(T e) where T : VisualElement
        {
            m_Refreshables.Add(e);
            return e;
        }

        void Changed() => m_Ctx.NotifyBrushChanged();

        /// <summary>Runs an action on the floating piece (or the selected image layer) of the active material.</summary>
        void WithFloating(Action<APSession, TextureSet, PaintLayer> action, bool floatingOnly = false)
        {
            var s = m_Ctx.session;
            var set = s?.ActiveSet;
            var layer = s?.FloatingLayer(set);
            if (layer == null && !floatingOnly && set?.ActiveLayer != null && set.ActiveLayer.kind == LayerKind.Image) layer = set.ActiveLayer;
            if (layer == null)
            {
                m_Ctx.ShowHint("Nothing is picked up. Draw around part of a paint layer in the texture view, or click a color.");
                return;
            }
            action(s, set, layer);
            m_Ctx.Render();
        }

        void LiftWith(Func<TextureSet, byte[]> makeMask, string emptyHint)
        {
            var s = m_Ctx.session;
            var set = s?.ActiveSet;
            if (set == null) return;
            var floating = s.FloatingLayer(set);
            if (floating != null) s.ApplyFloating(set, floating);
            var mask = makeMask(set);
            if (mask == null)
            {
                if (emptyHint != null) m_Ctx.ShowHint(emptyHint);
                return;
            }
            var msg = s.Lift(set, set.ActiveLayer, mask, m_Ctx.transformCopy);
            if (msg != null) m_Ctx.ShowHint(msg);
            m_Ctx.Tool = PaintTool.Transform;
            m_Ctx.Render();
        }

        public void Refresh()
        {
            foreach (var e in m_Refreshables)
            {
                if (e is W.SliderRow s) s.Refresh();
                else if (e is W.ToggleRow t) t.Refresh();
                else if (e is W.DropdownRow d) d.Refresh();
                else if (e is W.Segmented sg) sg.Refresh();
            }
            foreach (var tile in m_Tips.Children())
                W.SetOn(tile, tile.userData as string == B.tip, "ap-on");
        }

        void RefreshMode()
        {
            bool fill = m_Ctx.Tool == PaintTool.Fill;
            bool select = m_Ctx.Tool == PaintTool.Select;
            bool transform = m_Ctx.Tool == PaintTool.Transform;
            m_FillBox.style.display = fill ? DisplayStyle.Flex : DisplayStyle.None;
            m_SelectBox.style.display = select ? DisplayStyle.Flex : DisplayStyle.None;
            m_TransformBox.style.display = transform ? DisplayStyle.Flex : DisplayStyle.None;
            m_BrushBox.style.display = fill || select || transform ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void RebuildTips()
        {
            m_Tips.Clear();
            foreach (var tip in BrushLibrary.Tips)
            {
                var t = tip;
                var tile = new VisualElement { tooltip = tip.name + (tip.builtIn ? "" : "  (right-click to delete)"), userData = tip.name };
                tile.AddToClassList("ap-tip");
                var img = new Image { image = tip.preview, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore, tintColor = m_Ctx.view.LightTheme ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.85f, 0.85f, 0.85f) };
                img.AddToClassList("ap-tip-image");
                tile.Add(img);
                tile.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button == 0)
                    {
                        B.tip = t.name;
                        Changed();
                    }
                    else if (e.button == 1 && !t.builtIn)
                    {
                        if (EditorUtility.DisplayDialog("Delete brush", "Delete the brush '" + t.name + "'? Its image file will be removed.", "Delete", "Cancel"))
                        {
                            if (B.tip == t.name) B.tip = "Round";
                            BrushLibrary.Delete(t);
                        }
                    }
                    e.StopPropagation();
                });
                m_Tips.Add(tile);
            }
            var add = new VisualElement { tooltip = "Add a custom brush from a PNG or JPG. White (or opaque) areas paint." };
            add.AddToClassList("ap-tip");
            add.AddToClassList("ap-tip-add");
            add.Add(new Icon(IconKind.Plus, 18));
            add.RegisterCallback<ClickEvent>(_ =>
            {
                string path = EditorUtility.OpenFilePanelWithFilters("Add brush", "", new[] { "Images", "png,jpg,jpeg" });
                if (string.IsNullOrEmpty(path)) return;
                B.tip = BrushLibrary.Import(path);
                Changed();
            });
            m_Tips.Add(add);
            Refresh();
        }
    }

    // ====================================================================== materials

    public class MaterialsPanel : VisualElement
    {
        readonly PainterContext m_Ctx;
        readonly VisualElement m_List;
        readonly VisualElement m_Options;
        static readonly int[] s_Sizes = { 512, 1024, 2048, 4096, 8192 };

        public MaterialsPanel(PainterContext ctx)
        {
            m_Ctx = ctx;
            AddToClassList("ap-materials-panel");
            m_List = W.Column("ap-material-list");
            Add(m_List);
            m_Options = W.Column("ap-material-options");
            Add(m_Options);
        }

        public void Rebuild()
        {
            m_List.Clear();
            m_Options.Clear();
            var s = m_Ctx.session;
            if (s == null) return;
            if (s.sets.Count == 0)
            {
                m_List.Add(W.Text("This avatar has no paintable materials.", "ap-empty"));
                return;
            }
            foreach (var set in s.sets)
            {
                var row = W.Row("ap-material-row");
                W.SetOn(row, set == s.ActiveSet, "ap-active");
                W.SetOn(row, APSession.IsEmission(set) && s.Sibling(set, false) != null, "ap-material-child");
                var thumb = new VisualElement();
                thumb.AddToClassList("ap-thumb");
                thumb.AddToClassList("ap-thumb-small");
                thumb.Add(new Image { image = APResources.Checker, scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore });
                Texture t = set.initialized ? (Texture)set.display : set.originalTexture;
                if (t != null) thumb.Add(new Image { image = t, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore });
                row.Add(thumb);
                var info = W.Column("ap-layer-info");
                info.Add(W.Text(set.displayName, "ap-layer-name"));
                string sub = set.initialized
                    ? set.width + "×" + set.height + " · " + set.layers.Count + (set.layers.Count == 1 ? " layer" : " layers")
                    : (set.originalTexture != null ? set.originalTexture.width + "×" + set.originalTexture.height + " · not painted yet" : "Empty slot · not painted yet");
                info.Add(W.Text(sub, "ap-layer-sub"));
                row.Add(info);
                var captured = set;
                row.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button == 0) s.ActiveSet = captured;
                });
                m_List.Add(row);
            }

            var active = s.ActiveSet;
            if (active == null) return;
            m_Options.Add(W.Text("Selected material", "ap-subtle-heading"));
            var props = APSession.TextureProperties(active.cloneMaterial);
            var labels = new List<string>();
            foreach (var p in props) labels.Add(APSession.PropertyDescription(active.cloneMaterial, p) + "  (" + p + ")");
            m_Options.Add(new W.DropdownRow("Texture slot", labels, () => Mathf.Max(0, props.IndexOf(active.property)), i =>
            {
                if (i < 0 || i >= props.Count || props[i] == active.property) return;
                s.AddPropertySet(active, props[i]);
            }, "Paint a different texture of this material, for example its emission map. Each slot keeps its own layers."));

            var sizes = new List<string>();
            foreach (var sz in s_Sizes) sizes.Add(sz + " × " + sz);
            int current = Array.IndexOf(s_Sizes, Mathf.Max(active.width, active.height));
            if (current < 0)
            {
                sizes.Insert(0, active.width + " × " + active.height + " (original)");
            }
            m_Options.Add(new W.DropdownRow("Resolution", sizes, () => current < 0 ? 0 : current, i =>
            {
                int idx = current < 0 ? i - 1 : i;
                if (idx < 0) return;
                int size = s_Sizes[idx];
                if (active.initialized && !EditorUtility.DisplayDialog("Change resolution",
                        "Resample every layer of " + active.displayName + " to " + size + "×" + size + "? Undo history will be cleared.", "Resample", "Cancel"))
                {
                    Rebuild();
                    return;
                }
                s.ResizeSet(active, size, size);
                Rebuild();
            }, "Texture size of the painted result"));

            var uvLabels = new List<string>();
            var uvIdx = new List<int>();
            for (int ch = 0; ch < 4; ch++)
            {
                bool has = false;
                foreach (var u in active.users)
                    if (s.meshes.entries[u.renderer].HasUVs(ch)) { has = true; break; }
                if (has || ch == 0)
                {
                    uvLabels.Add("UV" + ch);
                    uvIdx.Add(ch);
                }
            }
            if (uvLabels.Count > 1)
            {
                m_Options.Add(new W.DropdownRow("UV map", uvLabels, () => Mathf.Max(0, uvIdx.IndexOf(active.uvChannel)),
                    i => { s.SetUVChannel(active, uvIdx[i]); m_Ctx.Render(); }, "Which UV map this texture is laid out on (almost always UV0)"));
            }
            var used = new List<string>();
            foreach (var u in active.users)
            {
                var name = s.meshes.entries[u.renderer].displayName;
                if (!used.Contains(name)) used.Add(name);
            }
            m_Options.Add(W.Text("Used by " + string.Join(", ", used), "ap-hint"));
        }
    }

    // ====================================================================== meshes

    public class MeshesPanel : VisualElement
    {
        readonly PainterContext m_Ctx;

        public MeshesPanel(PainterContext ctx)
        {
            m_Ctx = ctx;
            AddToClassList("ap-meshes-panel");
        }

        public void Rebuild()
        {
            Clear();
            var s = m_Ctx.session;
            if (s == null) return;
            Add(W.Text("Hide meshes to reach what's underneath. This only affects the painter, not your avatar.", "ap-hint"));
            var actions = W.Row("ap-row-gap");
            actions.Add(W.TextButton("Show all", () =>
            {
                foreach (var e in s.meshes.entries) s.SetRendererVisible(e, true);
                m_Ctx.Render();
            }, "ap-small"));
            Add(actions);
            foreach (var e in s.meshes.entries)
            {
                var entry = e;
                var row = W.Row("ap-mesh-row");
                bool vis = e.Visible;
                row.Add(W.IconButton(vis ? IconKind.Eye : IconKind.EyeOff, vis ? "Hide" : "Show", () =>
                {
                    s.SetRendererVisible(entry, !entry.Visible);
                    m_Ctx.Render();
                }, "ap-eye"));
                var label = W.Text(e.displayName, "ap-mesh-name");
                W.SetOn(row, !vis, "ap-hidden");
                row.Add(label);
                row.Add(W.Spacer());
                row.Add(W.TextButton("Solo", () =>
                {
                    foreach (var o in s.meshes.entries) s.SetRendererVisible(o, o == entry);
                    m_Ctx.Render();
                }, "ap-small", "ap-ghost"));
                Add(row);
            }
        }
    }

    // ====================================================================== image placement

    public class ImagePanel : VisualElement
    {
        readonly PainterContext m_Ctx;

        public ImagePanel(PainterContext ctx)
        {
            m_Ctx = ctx;
            AddToClassList("ap-image-panel");
        }

        public void Rebuild()
        {
            Clear();
            var s = m_Ctx.session;
            var set = s?.ActiveSet;
            var layer = set?.ActiveLayer;
            if (layer == null || layer.kind != LayerKind.Image || layer.placement == null)
            {
                style.display = DisplayStyle.None;
                return;
            }
            style.display = DisplayStyle.Flex;
            var p = layer.placement;

            Add(new W.Segmented(new[] { "On texture", "Projected", "Wrapped" }, () => p.Mode, i =>
            {
                if (i == p.Mode) return;
                s.EditPlacement(set, layer, "mode", pl =>
                {
                    pl.wrap = i == 2;
                    pl.projected = i == 1;
                    if (i == 1 && pl.worldPos == Vector3.zero)
                    {
                        var b = s.meshes.VisibleBounds();
                        pl.worldPos = b.center + Vector3.forward * b.extents.z;
                        pl.normal = Vector3.forward;
                        pl.up = Vector3.up;
                        pl.worldWidth = b.extents.x * 0.5f;
                        pl.depth = pl.worldWidth * 0.5f;
                    }
                });
                m_Ctx.Render();
                Rebuild();
            }, new[]
            {
                "Lay the image out in texture space (like an overlay in the 2D view)",
                "Shine the image onto the model like a projector, across seams",
                "Tile the image around the whole model from every side (like a shader's world/local position mapping), baked so it sticks to the model",
            }));

            if (p.wrap)
            {
                BuildWrap(s, set, layer, p);
                return;
            }

            Add(W.Text(p.projected
                ? "With the Image tool, drag on the model to place it. Shift-drag scales, Ctrl-drag rotates."
                : "With the Image tool, drag the box in the 2D view: corners scale, the top handle rotates.", "ap-hint"));

            if (p.projected)
            {
                Add(new W.SliderRow("Width (cm)", 0.002f, 2f, () => p.worldWidth, v => { s.EditPlacement(set, layer, "size", pl => pl.worldWidth = v); m_Ctx.Render(); }, 100f, null, true));
                Add(new W.SliderRow("Depth (cm)", 0.001f, 1f, () => p.depth, v => { s.EditPlacement(set, layer, "depth", pl => pl.depth = v); m_Ctx.Render(); }, 100f,
                    "How far in front of and behind the image surfaces still receive it", true));
                Add(new W.SliderRow("Facing limit", 5f, 175f, () => p.angleLimit, v => { s.EditPlacement(set, layer, "angle", pl => pl.angleLimit = v); m_Ctx.Render(); }, 1f,
                    "Surfaces turned away further than this don't receive the image"));
            }
            else
            {
                Add(new W.SliderRow("Size", 0.01f, 4f, () => p.uvSize.x, v =>
                {
                    s.EditPlacement(set, layer, "size", pl =>
                    {
                        float k = v / Mathf.Max(1e-5f, pl.uvSize.x);
                        pl.uvSize *= k;
                    });
                    m_Ctx.Render();
                }, 100f, "Width as a percentage of the texture", true));
                Add(new W.ToggleRow("Stay inside UV islands", () => p.clipToIslands, v => { s.EditPlacement(set, layer, "clip", pl => pl.clipToIslands = v); m_Ctx.Render(); }));
            }
            Add(new W.SliderRow("Rotation", -180f, 180f, () => p.rotation, v => { s.EditPlacement(set, layer, "rotate", pl => pl.rotation = v); m_Ctx.Render(); }));
            Add(new W.ToggleRow("Flip horizontally", () => p.flipX, v => { s.EditPlacement(set, layer, "flip", pl => pl.flipX = v); m_Ctx.Render(); }));

            var row = W.Row("ap-row-gap");
            if (!p.projected)
            {
                row.Add(W.TextButton("Stretch to texture", () =>
                {
                    s.EditPlacement(set, layer, "fit", pl => { pl.uvCenter = new Vector2(0.5f, 0.5f); pl.uvSize = Vector2.one; pl.rotation = 0f; });
                    m_Ctx.Render();
                    Rebuild();
                }, "ap-small"));
                row.Add(W.TextButton("Fit inside", () =>
                {
                    s.EditPlacement(set, layer, "fit", pl =>
                    {
                        float texAspect = set.height / (float)Mathf.Max(1, set.width);
                        float imgAspect = pl.Aspect;
                        pl.uvCenter = new Vector2(0.5f, 0.5f);
                        pl.rotation = 0f;
                        float wv = 1f, hv = imgAspect / texAspect;
                        if (hv > 1f) { wv /= hv; hv = 1f; }
                        pl.uvSize = new Vector2(wv, hv);
                    });
                    m_Ctx.Render();
                    Rebuild();
                }, "ap-small"));
            }
            row.Add(W.TextButton("Rasterize", () => s.RasterizeImage(set, layer), "ap-small"));
            Add(row);
        }

        void BuildWrap(APSession s, TextureSet set, PaintLayer layer, ImagePlacement p)
        {
            Add(W.Text("Tiles the image around the model from every side, like Poiyomi's World Pos / Local Pos mapping, but " +
                       "worked out on the rest pose and written into this texture's UVs, so it sticks to the model when it moves. " +
                       "With the Image tool: drag on the model to slide it, Shift-drag to scale, Ctrl-drag to rotate.", "ap-hint"));

            void Edit(string what, Action<ImagePlacement> change)
            {
                s.EditPlacement(set, layer, what, change);
                m_Ctx.Render();
            }

            Add(new W.Segmented(new[] { "All sides", "Side", "Top", "Front" }, () => p.axis + 1, i =>
            {
                Edit("wrap axis", pl => pl.axis = i - 1);
                Rebuild();
            }, new[]
            {
                "Project from all six sides and blend between them (triplanar). Best for most textures.",
                "Project only from the left/right (X), like a planar local-position mapping",
                "Project only from above/below (Y)",
                "Project only from the front/back (Z)",
            }));
            Add(new W.SliderRow("Tile size (cm)", 0.005f, 2f, () => p.tileSize, v => Edit("tile size", pl => pl.tileSize = v), 100f,
                "How much of the model one copy of the image covers", true));
            if (p.axis < 0)
                Add(new W.SliderRow("Blend sharpness", 1f, 32f, () => p.sharpness, v => Edit("wrap blend", pl => pl.sharpness = v), 1f,
                    "Low values blend the sides softly; high values give crisp hand-overs", true));
            Add(new W.SliderRow("Rotation", -180f, 180f, () => p.rotation, v => Edit("rotate", pl => pl.rotation = v)));
            Add(new W.ToggleRow("Flip horizontally", () => p.flipX, v => Edit("flip", pl => pl.flipX = v)));
            Add(new W.ToggleRow("Image is a normal map", () => p.normalMap, v => { Edit("normal map", pl => pl.normalMap = v); Rebuild(); },
                "Treat the image as a tangent-space normal map: it's re-oriented for every part of the model so the bumps light correctly"));
            if (p.normalMap)
            {
                Add(new W.SliderRow("Bump strength", 0f, 4f, () => p.normalStrength, v => Edit("bump strength", pl => pl.normalStrength = v), 100f));
                Add(new W.ToggleRow("Green channel down (DirectX)", () => p.greenDown, v => Edit("green", pl => pl.greenDown = v),
                    "Turn on if the bumps look like dents. Normal maps made for DirectX/Unreal have green flipped compared to Unity."));
                if (!APSession.IsNormalMap(set))
                    Add(W.Text("This layer is on a color texture, so you'll see the normal map's colors instead of bumps. " +
                               "Wrap it again from the Layers image button to put it in the normal map slot.", "ap-hint"));
                else if (layer.blend != LayerBlendMode.NormalCombine)
                    Add(W.Text("Tip: blend mode \"Normal map combine\" keeps the material's own normal map detail underneath.", "ap-hint"));
            }

            var siblings = s.WrapSiblings(layer);
            if (siblings.Count > 0)
                Add(new W.ToggleRow("Same settings on all " + (siblings.Count + 1) + " materials", () => p.linked, v =>
                {
                    // Turning linking back on lines every copy up with this one.
                    p.linked = v;
                    foreach (var sib in siblings) sib.layer.placement.linked = v;
                    if (v) s.SyncWrapSettings(layer);
                    m_Ctx.Render();
                }, "Sliding, scaling or changing this layer changes the same texture on the other materials too, so seams between body and clothes line up"));
            var row = W.Row("ap-row-gap");
            row.Add(W.TextButton("Rasterize", () => s.RasterizeImage(set, layer), "ap-small"));
            Add(row);
        }
    }

    // ====================================================================== adjustment layers

    public class AdjustPanel : VisualElement
    {
        readonly PainterContext m_Ctx;

        public AdjustPanel(PainterContext ctx)
        {
            m_Ctx = ctx;
            AddToClassList("ap-adjust-panel");
        }

        public void Rebuild()
        {
            Clear();
            var s = m_Ctx.session;
            var set = s?.ActiveSet;
            var layer = set?.ActiveLayer;
            if (layer == null || layer.kind != LayerKind.Adjustment) return;

            Add(new W.Segmented(new[] { "Recolor", "Hue / Sat", "Match" }, () => (int)layer.adjMode, i =>
            {
                s.EditLayer(set, layer, "Adjustment type", l => l.adjMode = (AdjustMode)i);
                m_Ctx.Render();
                Rebuild();
            }, new[]
            {
                "Give the area a new overall color while keeping its shading and detail",
                "Shift hue, saturation and lightness",
                "Make this area's colors match another area (e.g. one eye to the other)",
            }));

            switch (layer.adjMode)
            {
                case AdjustMode.Recolor:
                {
                    Add(W.Text("The area's average color becomes this color; highlights, shadows and patterns stay. " +
                               "While this layer is selected, the color wheel above edits it.", "ap-hint"));
                    var row = W.Row("ap-row-gap");
                    var sw = W.Swatch(layer.adjColor, 24, "Recolor target");
                    row.Add(sw);
                    row.Add(W.TextButton("Use paint color", () =>
                    {
                        s.EditLayer(set, layer, "Layer color", l => l.adjColor = m_Ctx.primary);
                        m_Ctx.Render();
                        Rebuild();
                    }, "ap-small"));
                    Add(row);
                    break;
                }
                case AdjustMode.HueSatLight:
                    Add(new W.SliderRow("Hue", -180f, 180f, () => layer.hue, v => { s.EditLayer(set, layer, "Hue", l => l.hue = v); m_Ctx.Render(); }));
                    Add(new W.SliderRow("Saturation", -1f, 1f, () => layer.saturation, v => { s.EditLayer(set, layer, "Saturation", l => l.saturation = v); m_Ctx.Render(); }, 100f));
                    Add(new W.SliderRow("Lightness", -1f, 1f, () => layer.lightness, v => { s.EditLayer(set, layer, "Lightness", l => l.lightness = v); m_Ctx.Render(); }, 100f));
                    break;
                case AdjustMode.Match:
                {
                    Add(W.Text(layer.refValid
                        ? "Matching the reference area. To pick a different one, select it and press the button again."
                        : "1. This layer's area is what gets changed (it was the selection when you added it).\n" +
                          "2. Now select the area to copy colors from (Select tool, S) and press the button below.", "ap-hint"));
                    var b = W.TextButton(layer.refValid ? "Use selected area as reference again" : "Use selected area as reference", () =>
                    {
                        if (!s.SetMatchReference(set, layer))
                            m_Ctx.ShowHint("Select the area to copy colors from first (Select tool, S).");
                        m_Ctx.Render();
                        Rebuild();
                    }, "ap-accent");
                    b.SetEnabled(set.HasSelection);
                    Add(b);
                    if (layer.refValid)
                    {
                        var ref0 = new Color(layer.refMean.x, layer.refMean.y, layer.refMean.z);
                        var src0 = new Color(layer.srcMean.x, layer.srcMean.y, layer.srcMean.z);
                        var row = W.Row("ap-row-gap");
                        row.Add(W.Swatch(src0, 18, "This area's average color before matching"));
                        row.Add(W.Text("→", "ap-hint-inline"));
                        row.Add(W.Swatch(ref0, 18, "Reference area's average color"));
                        Add(row);
                    }
                    break;
                }
            }

            var tools = W.Row("ap-row-gap", "ap-wrap");
            tools.Add(W.TextButton("Re-measure area", () => { s.MeasureSource(set, layer); m_Ctx.Render(); Rebuild(); }, "ap-small"));
            Add(tools);
            Add(W.Text("Painting on this layer paints its mask: white areas are affected, black are not. Use the layer opacity for strength.", "ap-hint"));
        }
    }

    // ====================================================================== pose

    public class PosePanel : VisualElement
    {
        readonly PainterContext m_Ctx;
        readonly Func<Texture2D> m_Capture;
        readonly VisualElement m_BoneBox;
        readonly VisualElement m_Library;
        Vector3Field m_Euler;
        string m_NewName = "";

        public PosePanel(PainterContext ctx, Func<Texture2D> captureThumbnail)
        {
            m_Ctx = ctx;
            m_Capture = captureThumbnail;
            AddToClassList("ap-pose-panel");

            Add(new W.Segmented(new[] { "Rotate", "Move" }, () => (int)m_Ctx.poseTool, i => { m_Ctx.poseTool = (PoseTool)i; m_Ctx.Render(); },
                new[] { "Rotate bones with the gizmo (R)", "Drag bones to move them (G)" }));

            m_BoneBox = W.Column("ap-bone-box");
            Add(m_BoneBox);

            var tools = W.Row("ap-row-gap", "ap-wrap");
            tools.Add(W.IconTextButton(IconKind.Reset, "Reset pose", () => Apply("Reset pose", m_Ctx.session.pose.RestSnapshot(), true)));
            tools.Add(W.IconTextButton(IconKind.Mirror, "Mirror pose", () => Apply("Mirror pose", m_Ctx.session.pose.MirroredSnapshot(), true)));
            Add(tools);

            Add(new W.ToggleRow("Local gizmo axes", () => m_Ctx.view.gizmoLocal, v => { m_Ctx.view.gizmoLocal = v; m_Ctx.view.Save(); m_Ctx.Render(); }));
            Add(new W.ToggleRow("Humanoid bones only", () => m_Ctx.view.humanoidOnly, v => { m_Ctx.view.humanoidOnly = v; m_Ctx.view.Save(); m_Ctx.Render(); },
                "Hide hair, clothing and accessory bones"));
            Add(new W.SliderRow("Bone size", 0.3f, 3f, () => m_Ctx.view.boneSize, v => { m_Ctx.view.boneSize = v; m_Ctx.view.Save(); m_Ctx.Render(); }, 100f));
            m_LinkRow = W.Column();
            Add(m_LinkRow);

            Add(W.Text("Saved poses", "ap-subtle-heading"));
            var saveRow = W.Row("ap-row-gap");
            var nameField = new TextField { value = "" };
            nameField.AddToClassList("ap-grow");
            nameField.RegisterValueChangedCallback(e => m_NewName = e.newValue);
            saveRow.Add(nameField);
            saveRow.Add(W.TextButton("Save pose", () =>
            {
                SavePose(string.IsNullOrWhiteSpace(m_NewName) ? "Pose " + DateTime.Now.ToString("MMdd HHmm") : m_NewName.Trim(), null);
                nameField.SetValueWithoutNotify("");
                m_NewName = "";
            }, "ap-accent"));
            Add(saveRow);
            m_Library = W.Row("ap-pose-library");
            Add(m_Library);

            var sceneRow = W.Row("ap-row-gap");
            sceneRow.Add(W.TextButton("Send pose to scene", () =>
            {
                var s = m_Ctx.session;
                if (s.sourceAvatar == null) return;
                s.pose.CopyToSceneAvatar(s.sourceAvatar.transform);
            }, "ap-small"));
            sceneRow.Add(W.TextButton("Get pose from scene", () =>
            {
                var s = m_Ctx.session;
                if (s.sourceAvatar == null) return;
                var pose = s.pose;
                var before = pose.Capture();
                var root = s.sourceAvatar.transform;
                foreach (var b in pose.bones)
                {
                    var t = APUtil.FindPath(root, b.path);
                    if (t == null || b.t == null) continue;
                    b.t.localRotation = t.localRotation;
                    b.t.localPosition = t.localPosition;
                }
                var after = pose.Capture();
                pose.NotifyChanged();
                s.Push(new ActionEntry("Get pose from scene", () => pose.Restore(before), () => pose.Restore(after)));
                m_Ctx.Render();
            }, "ap-small"));
            Add(sceneRow);
            var animRow = W.Row("ap-row-gap");
            animRow.Add(W.IconTextButton(IconKind.Record, "Animate in the Scene view", () =>
            {
                var s = m_Ctx.session;
                if (s.sourceAvatar == null) return;
                ScenePoseState.SetAvatar(s.sourceAvatar.transform);
                Selection.activeGameObject = s.sourceAvatar;
                ScenePoseTool.Activate();
            }));
            Add(animRow);
            Add(W.Text("Posing here only changes the painter's copy, and painting works in any pose. To make animations, " +
                       "use Animate in the Scene view: the same bone overlay appears on your real avatar and works with the Animation window's record mode.", "ap-hint"));
        }

        void Apply(string name, PoseSnapshot target, bool animate)
        {
            var pose = m_Ctx.session.pose;
            pose.FinishTransition();
            var before = pose.Capture();
            if (animate) pose.TransitionTo(target);
            else pose.Restore(target);
            m_Ctx.session.Push(new ActionEntry(name, () => { pose.FinishTransition(); pose.Restore(before); }, () => { pose.FinishTransition(); pose.Restore(target); }));
            m_Ctx.Render();
            RefreshBone();
        }

        VisualElement m_LinkRow;

        void RefreshLink()
        {
            if (m_LinkRow == null) return;
            m_LinkRow.Clear();
            var linker = m_Ctx.session?.linker;
            if (linker == null || !linker.HasClothing) return;
            m_LinkRow.Add(new W.ToggleRow("Clothing follows the body (like VRCFury)", () => m_Ctx.view.linkClothing, v =>
            {
                m_Ctx.view.linkClothing = v;
                m_Ctx.view.Save();
                m_Ctx.session.ApplyLinkSetting();
                m_Ctx.Render();
            }, "Bones of separate clothing armatures follow the body bones with the same names. Clothing-only bones follow their parents."));
            m_LinkRow.Add(W.Text(linker.Summary(), "ap-hint"));
        }

        public void RefreshBone()
        {
            RefreshLink();
            m_BoneBox.Clear();
            var s = m_Ctx.session;
            var b = s?.pose.selected;
            if (b == null)
            {
                m_BoneBox.Add(W.Text("Click a bone in the 3D view to select it. Tab switches between painting and posing.", "ap-hint"));
                return;
            }
            var title = W.Row("ap-row-gap");
            title.Add(new Icon(IconKind.Bone, 14));
            title.Add(W.Text(b.Name, "ap-bone-name"));
            if (b.isHuman) title.Add(W.Text(ObjectNames.NicifyVariableName(b.human.ToString()), "ap-badge"));
            m_BoneBox.Add(title);
            m_Euler = new Vector3Field("Rotation") { value = b.t.localEulerAngles };
            m_Euler.AddToClassList("ap-euler");
            m_Euler.RegisterValueChangedCallback(e =>
            {
                var pose = s.pose;
                var before = pose.Capture();
                b.t.localEulerAngles = e.newValue;
                var after = pose.Capture();
                s.Push(new ActionEntry("Rotate bone", () => pose.Restore(before), () => pose.Restore(after)));
                pose.NotifyChanged();
                m_Ctx.Render();
            });
            m_BoneBox.Add(m_Euler);
            var row = W.Row("ap-row-gap", "ap-wrap");
            row.Add(W.TextButton("Reset rotation", () => ResetBone(b, true, false), "ap-small"));
            row.Add(W.TextButton("Reset position", () => ResetBone(b, false, true), "ap-small"));
            if (b.parent != null) row.Add(W.TextButton("Parent", () => { s.pose.Select(b.parent); m_Ctx.Render(); }, "ap-small", "ap-ghost"));
            if (b.tailChild != null) row.Add(W.TextButton("Child", () => { s.pose.Select(b.tailChild); m_Ctx.Render(); }, "ap-small", "ap-ghost"));
            if (b.mirror != null) row.Add(W.TextButton("Other side", () => { s.pose.Select(b.mirror); m_Ctx.Render(); }, "ap-small", "ap-ghost"));
            m_BoneBox.Add(row);
        }

        public void RefreshValues()
        {
            var b = m_Ctx.session?.pose.selected;
            if (b != null && m_Euler != null) m_Euler.SetValueWithoutNotify(b.t.localEulerAngles);
        }

        void ResetBone(Bone b, bool rot, bool pos)
        {
            var pose = m_Ctx.session.pose;
            var before = pose.Capture();
            pose.ResetBone(b, rot, pos);
            var after = pose.Capture();
            m_Ctx.session.Push(new ActionEntry("Reset bone", () => pose.Restore(before), () => pose.Restore(after)));
            m_Ctx.Render();
            RefreshValues();
        }

        public void RebuildLibrary()
        {
            m_Library.Clear();
            var guids = AssetDatabase.FindAssets("t:AvatarPose");
            if (guids.Length == 0)
            {
                m_Library.Add(W.Text("No saved poses yet. Pose the avatar and press Save pose; click a saved pose to switch to it.", "ap-hint"));
                return;
            }
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var asset = AssetDatabase.LoadAssetAtPath<AvatarPose>(path);
                if (asset == null) continue;
                var card = new VisualElement { tooltip = asset.name + (string.IsNullOrEmpty(asset.avatarName) ? "" : "\nSaved from " + asset.avatarName) + "\nClick to apply · right-click for options" };
                card.AddToClassList("ap-pose-card");
                var img = new Image { image = asset.thumbnail != null ? asset.thumbnail : null, scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
                img.AddToClassList("ap-pose-thumb");
                if (asset.thumbnail == null) img.Add(new Icon(IconKind.Bone, 22));
                card.Add(img);
                card.Add(W.Text(asset.name, "ap-pose-name"));
                card.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0) return;
                    Apply("Apply pose " + asset.name, m_Ctx.session.pose.SnapshotFrom(asset.bones), true);
                });
                card.AddManipulator(new ContextualMenuManipulator(evt =>
                {
                    evt.menu.AppendAction("Apply", _ => Apply("Apply pose " + asset.name, m_Ctx.session.pose.SnapshotFrom(asset.bones), true));
                    evt.menu.AppendAction("Overwrite with current pose", _ => SavePose(asset.name, asset));
                    evt.menu.AppendAction("Rename…", _ => RenamePose(asset));
                    evt.menu.AppendAction("Show in Project", _ => EditorGUIUtility.PingObject(asset));
                    evt.menu.AppendAction("Delete", _ =>
                    {
                        if (EditorUtility.DisplayDialog("Delete pose", "Delete the pose '" + asset.name + "'?", "Delete", "Cancel"))
                        {
                            AssetDatabase.DeleteAsset(path);
                            RebuildLibrary();
                        }
                    });
                }));
                m_Library.Add(card);
            }
        }

        void SavePose(string name, AvatarPose existing)
        {
            var s = m_Ctx.session;
            APUtil.EnsureAssetFolder(APUtil.PosesFolder);
            var asset = existing;
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<AvatarPose>();
                string path = AssetDatabase.GenerateUniqueAssetPath(APUtil.PosesFolder + "/" + APUtil.SafeFileName(name) + ".asset");
                AssetDatabase.CreateAsset(asset, path);
            }
            asset.avatarName = s.avatarName;
            asset.bones = s.pose.ToPoseBones();
            var thumb = m_Capture?.Invoke();
            if (thumb != null)
            {
                if (asset.thumbnail != null) AssetDatabase.RemoveObjectFromAsset(asset.thumbnail);
                thumb.name = "Thumbnail";
                thumb.hideFlags = HideFlags.None;
                AssetDatabase.AddObjectToAsset(thumb, asset);
                asset.thumbnail = thumb;
            }
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            RebuildLibrary();
        }

        void RenamePose(AvatarPose asset)
        {
            var popup = new TextInputPopup("Rename pose", asset.name, newName =>
            {
                if (string.IsNullOrWhiteSpace(newName)) return;
                AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(asset), APUtil.SafeFileName(newName.Trim()));
                AssetDatabase.SaveAssets();
                RebuildLibrary();
            });
            var anchor = worldBound;
            UnityEditor.PopupWindow.Show(new Rect(anchor.x, anchor.y, 10, 10), popup);
        }
    }

    /// <summary>Tiny popup with one text field.</summary>
    public class TextInputPopup : PopupWindowContent
    {
        readonly string m_Title;
        string m_Value;
        readonly Action<string> m_Done;
        bool m_Focused;

        public TextInputPopup(string title, string value, Action<string> done)
        {
            m_Title = title;
            m_Value = value;
            m_Done = done;
        }

        public override Vector2 GetWindowSize() => new Vector2(260, 74);

        public override void OnGUI(Rect rect)
        {
            GUILayout.Label(m_Title, EditorStyles.boldLabel);
            GUI.SetNextControlName("ap_text");
            m_Value = EditorGUILayout.TextField(m_Value);
            if (!m_Focused)
            {
                EditorGUI.FocusTextInControl("ap_text");
                m_Focused = true;
            }
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (GUILayout.Button("OK", GUILayout.Width(70)) || enter)
            {
                m_Done?.Invoke(m_Value);
                editorWindow.Close();
            }
            GUILayout.EndHorizontal();
        }
    }

    // ====================================================================== view settings

    public class ViewPanel : VisualElement
    {
        readonly PainterContext m_Ctx;

        public ViewPanel(PainterContext ctx)
        {
            m_Ctx = ctx;
            AddToClassList("ap-view-panel");
            var v = ctx.view;
            void Save() { v.Save(); m_Ctx.Render(); }

            Add(new W.DropdownRow("Theme", new List<string> { "Follow Unity", "Dark", "Light" },
                () => v.theme, i =>
                {
                    v.theme = i;
                    v.Save();
                    var w = EditorWindow.HasOpenInstances<AviCreatorWindow>() ? EditorWindow.GetWindow<AviCreatorWindow>(false, null, false) : null;
                    w?.ApplyTheme();
                    m_Ctx.Render();
                }));
            Add(new W.DropdownRow("Lighting", new List<string> { "Studio", "Soft", "Flat (base colors)", "Dramatic", "Night" },
                () => (int)v.lighting, i => { v.lighting = (LightingPreset)i; Save(); }));
            Add(new W.SliderRow("Light angle", -180f, 180f, () => v.lightYaw, x => { v.lightYaw = x; Save(); }));
            Add(new W.ToggleRow("Light turns with the camera", () => v.lightFollowsCamera, x => { v.lightFollowsCamera = x; Save(); }));
            Add(new W.SliderRow("Light strength", 0f, 2f, () => v.lightIntensity, x => { v.lightIntensity = x; Save(); }, 100f));
            Add(new W.SliderRow("Ambient", 0f, 1.5f, () => v.ambient, x => { v.ambient = x; Save(); }, 100f));
            Add(new W.SliderRow("Field of view", 10f, 80f, () => v.fov, x => { v.fov = x; Save(); }));
            Add(new W.SliderRow("Emission view base", 0f, 0.5f, () => v.emissionBaseDim, x => { v.emissionBaseDim = x; Save(); m_Ctx.session?.RefreshEmissionView(); }, 100f,
                "How much of the base color stays faintly visible in the emission-only view"));
            Add(new W.ToggleRow("Orbit around the point under the cursor", () => v.orbitAroundCursor, x => { v.orbitAroundCursor = x; Save(); }));
            Add(new W.ToggleRow("Show transparency in 2D view", () => v.showAlpha2D, x => { v.showAlpha2D = x; Save(); }));
            Add(new W.ToggleRow("Repeat texture in 2D view", () => v.tile2D, x => { v.tile2D = x; Save(); }));
            Add(new W.ToggleRow("Tint hidden areas while painting a mask", () => v.showMaskOverlay, x => { v.showMaskOverlay = x; Save(); }));
            Add(new W.SliderRow("Seam padding (px)", 0f, 64f, () => v.displayPadding, x =>
            {
                v.displayPadding = Mathf.RoundToInt(x);
                if (m_Ctx.session != null) foreach (var s in m_Ctx.session.sets) s.dirty = true;
                Save();
            }, 1f, "Colors are extended this far past UV island edges so seams never show a line"));
            Add(new W.ToggleRow("Start from full-resolution source files", () => v.preferSourceFiles, x => { v.preferSourceFiles = x; Save(); },
                "Read the original PNG/JPG instead of the compressed, size-limited imported texture"));
            var ver = W.Row("ap-row-gap");
            ver.Add(W.Text("Version " + UpdateChecker.Version + " · " + UpdateChecker.InstallDescription, "ap-hint-inline", "ap-grow"));
            ver.Add(W.TextButton("Check for updates", () =>
            {
                UpdateChecker.Check(true);
                EditorApplication.delayCall += () => { };
            }, "ap-small"));
            Add(ver);
            Add(new W.ToggleRow("Color wheel edits the selected fill/recolor layer", () => v.pickerEditsLayer, x => { v.pickerEditsLayer = x; v.Save(); }));
            Add(new W.SliderRow("Undo steps", 5f, 200f, () => v.undoSteps, x =>
            {
                v.undoSteps = Mathf.RoundToInt(x);
                if (m_Ctx.session != null) m_Ctx.session.history.MaxSteps = v.undoSteps;
                v.Save();
            }));
        }
    }
}
