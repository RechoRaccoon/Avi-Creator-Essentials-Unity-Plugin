using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>Layer stack operations, every one undoable.</summary>
    public partial class APSession
    {
        public struct LayerState
        {
            public string name;
            public bool visible;
            public float opacity;
            public LayerBlendMode blend;
            public bool lockAlpha;
            public Color fillColor;
            public bool maskEnabled;
            public AdjustMode adjMode;
            public bool clipBelow;
            public Color adjColor;
            public float hue, saturation, lightness;

            public static LayerState Of(PaintLayer l) => new LayerState
            {
                name = l.name,
                visible = l.visible,
                opacity = l.opacity,
                blend = l.blend,
                lockAlpha = l.lockAlpha,
                fillColor = l.fillColor,
                maskEnabled = l.maskEnabled,
                adjMode = l.adjMode,
                clipBelow = l.clipBelow,
                adjColor = l.adjColor,
                hue = l.hue,
                saturation = l.saturation,
                lightness = l.lightness,
            };

            public void ApplyTo(PaintLayer l)
            {
                l.name = name;
                l.visible = visible;
                l.opacity = opacity;
                l.blend = blend;
                l.lockAlpha = lockAlpha;
                l.fillColor = fillColor;
                l.maskEnabled = maskEnabled;
                l.adjMode = adjMode;
                l.clipBelow = clipBelow;
                l.adjColor = adjColor;
                l.hue = hue;
                l.saturation = saturation;
                l.lightness = lightness;
            }
        }

        class MergeableEntry : IHistoryEntry
        {
            public string Name { get; set; }
            public int Sequence { get; set; }
            public long Bytes => 0;
            public string key;
            public double time;
            public Action undo;
            public Action redo;
            public void Undo() => undo?.Invoke();
            public void Redo() => redo?.Invoke();
            public void Dispose(bool applied) { }
        }

        void Changed(TextureSet set)
        {
            set.dirty = true;
            dirty = true;
            StructureChanged?.Invoke();
            PixelsChanged?.Invoke(set);
        }

        public void Push(IHistoryEntry entry)
        {
            if (entry == null) return;
            history.Push(entry);
            dirty = true;
        }

        // ------------------------------------------------------------------ properties (merged while dragging)

        /// <summary>Changes layer properties. Rapid edits with the same key merge into one undo step.</summary>
        public void EditLayer(TextureSet set, PaintLayer layer, string what, Action<PaintLayer> change)
        {
            // A live-mirrored set's own layers are ignored: editing them would
            // change nothing visible. Edit the source material instead.
            if (set != null && set.IsMirrored) return;
            var before = LayerState.Of(layer);
            change(layer);
            var after = LayerState.Of(layer);
            string key = layer.id + "/" + what;
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (history.Top is MergeableEntry top && top.key == key && now - top.time < 1.5)
            {
                top.time = now;
                top.redo = () => { after.ApplyTo(layer); Changed(set); };
            }
            else
            {
                var e = new MergeableEntry
                {
                    Name = what,
                    key = key,
                    time = now,
                    undo = () => { before.ApplyTo(layer); Changed(set); },
                    redo = () => { after.ApplyTo(layer); Changed(set); },
                };
                Push(e);
            }
            Changed(set);
        }

        public void EditPlacement(TextureSet set, PaintLayer layer, string what, Action<ImagePlacement> change)
        {
            if (layer.placement == null) return;
            // Linked wrapped layers (the same texture wrapped around several materials) change together.
            var targets = new List<(TextureSet set, PaintLayer layer)> { (set, layer) };
            if (layer.placement.wrap && layer.placement.linked)
                foreach (var sib in WrapSiblings(layer))
                    if (sib.layer.placement.linked) targets.Add(sib);
            var before = new ImagePlacement[targets.Count];
            var after = new ImagePlacement[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                var (ts, tl) = targets[i];
                before[i] = tl.placement.Clone();
                change(tl.placement);
                after[i] = tl.placement.Clone();
                engine.RenderImageLayer(ts, tl);
            }
            string key = layer.id + "/placement/" + what;
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            void Restore(ImagePlacement[] states)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var (ts, tl) = targets[i];
                    if (tl.placement == null) continue;
                    var img = tl.placement.image;
                    tl.placement = states[i].Clone();
                    tl.placement.image = img;
                    engine.RenderImageLayer(ts, tl);
                    Changed(ts);
                }
            }
            if (history.Top is MergeableEntry top && top.key == key && now - top.time < 1.5)
            {
                top.time = now;
                top.redo = () => Restore(after);
            }
            else
            {
                Push(new MergeableEntry { Name = layer.placement.wrap ? "Edit wrapped texture" : "Move image", key = key, time = now, undo = () => Restore(before), redo = () => Restore(after) });
            }
            foreach (var t in targets) Changed(t.set);
        }

        // ------------------------------------------------------------------ add / remove

        void InsertLayer(TextureSet set, PaintLayer layer, int index, string name)
        {
            index = Mathf.Clamp(index, 0, set.layers.Count);
            set.layers.Insert(index, layer);
            int prevActive = set.activeLayer;
            set.activeLayer = index;
            set.editingMask = false;
            Push(new ActionEntry(name,
                () =>
                {
                    set.layers.Remove(layer);
                    set.activeLayer = Mathf.Clamp(prevActive, 0, set.layers.Count - 1);
                    Changed(set);
                },
                () =>
                {
                    set.layers.Insert(Mathf.Clamp(index, 0, set.layers.Count), layer);
                    set.activeLayer = set.layers.IndexOf(layer);
                    Changed(set);
                },
                applied => { if (!applied && !set.layers.Contains(layer)) layer.Release(); },
                layer.Bytes));
            Changed(set);
        }

        int InsertIndex(TextureSet set) => set.layers.Count == 0 ? 0 : Mathf.Clamp(set.activeLayer + 1, 0, set.layers.Count);

        string UniqueName(TextureSet set, string baseName)
        {
            int n = 1;
            string name = baseName + " " + n;
            bool exists = true;
            while (exists)
            {
                exists = false;
                foreach (var l in set.layers)
                    if (l.name == name) { exists = true; n++; name = baseName + " " + n; break; }
            }
            return name;
        }

        public PaintLayer AddPaintLayer(TextureSet set, string name = null)
        {
            if (!set.initialized) InitializeSet(set);
            var layer = new PaintLayer { name = name ?? UniqueName(set, "Layer"), kind = LayerKind.Paint };
            layer.content = engine.NewContentRT(set);
            InsertLayer(set, layer, InsertIndex(set), "New layer");
            return layer;
        }

        public void AddPaintLayerToAll(string name)
        {
            var entries = new List<IHistoryEntry>();
            foreach (var set in sets)
            {
                if (!set.initialized) continue;
                var layer = new PaintLayer { name = name, kind = LayerKind.Paint };
                layer.content = engine.NewContentRT(set);
                int index = InsertIndex(set);
                set.layers.Insert(index, layer);
                set.activeLayer = index;
                set.editingMask = false;
                var s = set;
                entries.Add(new ActionEntry("New layer",
                    () => { s.layers.Remove(layer); s.activeLayer = Mathf.Clamp(index - 1, 0, s.layers.Count - 1); Changed(s); },
                    () => { s.layers.Insert(Mathf.Clamp(index, 0, s.layers.Count), layer); s.activeLayer = s.layers.IndexOf(layer); Changed(s); },
                    applied => { if (!applied && !s.layers.Contains(layer)) layer.Release(); }, layer.Bytes));
                Changed(set);
            }
            if (entries.Count > 0) Push(new CompoundEntry("New layer in all materials", entries));
        }

        /// <summary>A mask copied from the current UV selection, or null when nothing is selected.</summary>
        RenderTexture MaskFromSelection(TextureSet set)
        {
            if (!set.HasSelection) return null;
            if (set.selectionDirty || set.selectionRT == null) engine.BuildSelection(set);
            return set.selectionRT != null ? engine.CloneRT(set.selectionRT, "Mask") : null;
        }

        public PaintLayer AddFillLayer(TextureSet set, Color color, bool blackMask)
        {
            if (!set.initialized) InitializeSet(set);
            var layer = new PaintLayer { name = UniqueName(set, "Fill"), kind = LayerKind.Fill, fillColor = color };
            var fromSel = MaskFromSelection(set);
            if (fromSel != null)
            {
                layer.mask = fromSel;
                InsertLayer(set, layer, InsertIndex(set), "New fill layer");
                set.editingMask = false;
                return layer;
            }
            if (blackMask) layer.mask = engine.NewMaskRT(set, 0f);
            InsertLayer(set, layer, InsertIndex(set), "New fill layer");
            if (blackMask) set.editingMask = true;
            return layer;
        }

        /// <summary>
        /// Adds an adjustment layer that changes the colors below it. It is limited to the current UV selection
        /// (or covers everything when nothing is selected); paint its mask to refine it.
        /// </summary>
        public PaintLayer AddAdjustmentLayer(TextureSet set, AdjustMode mode, Color color) =>
            AddAdjustmentLayer(set, mode, color, MaskFromSelection(set) ?? engine.NewMaskRT(set, 1f));

        /// <summary>Adds an adjustment layer limited to the given mask instead of the current UV selection.</summary>
        public PaintLayer AddAdjustmentLayer(TextureSet set, AdjustMode mode, Color color, RenderTexture mask)
        {
            if (!set.initialized) InitializeSet(set);
            string baseName = mode == AdjustMode.Recolor ? "Recolor" : mode == AdjustMode.Match ? "Match colors" : "Hue/Saturation";
            var layer = new PaintLayer
            {
                name = UniqueName(set, baseName),
                kind = LayerKind.Adjustment,
                adjMode = mode,
                adjColor = color,
            };
            layer.mask = mask;
            int index = InsertIndex(set);
            InsertLayer(set, layer, index, "New " + baseName.ToLowerInvariant() + " layer");
            set.editingMask = false;
            MeasureSource(set, layer);
            return layer;
        }

        /// <summary>The active layer when the Color Recolor tool can extend it instead of making a new layer:
        /// an adjustment layer (Recolor, or Hue/Saturation from before 0.14.9) with a mask.</summary>
        PaintLayer ColorRecolorTarget(TextureSet set)
        {
            var layer = set?.ActiveLayer;
            if (layer == null || layer.kind != LayerKind.Adjustment) return null;
            if (layer.adjMode != AdjustMode.Recolor && layer.adjMode != AdjustMode.HueSatLight) return null;
            if (!layer.HasMask || layer.mask == null) return null;
            return layer;
        }

        /// <summary>
        /// Select-by-color recolor, driven by the Color Recolor tool (C).
        /// Plain click picks a new color for the active recolor layer, or makes a new Recolor layer when there
        /// is none — the layer takes the color wheel's current color, so it matches whatever is selected there.
        /// Shift+click adds the clicked color's region to the same layer's mask; Ctrl/Cmd+click takes it away.
        /// The mask is shown tinted red in the texture view. Returns the hint to show the user.
        /// </summary>
        public string ColorRecolorClick(TextureSet set, Vector2 uv, float tolerance, bool contiguous, bool sampleLayer, Color targetColor, bool add, bool subtract)
        {
            if (set == null) return "Pick a material first.";
            if (set.IsMirrored) return "This material live-mirrors another — recolor on that material instead.";
            if (!set.initialized) InitializeSet(set);
            var layer = set.ActiveLayer;
            if (layer == null) return "Pick a material and a layer first.";
            if (layer.kind == LayerKind.Image && layer.content == null)
            {
                if (layer.placement == null) return "That layer has no pixels to recolor.";
                engine.RenderImageLayer(set, layer);
                if (layer.content == null) return "That layer has no pixels to recolor.";
            }
            var tex = engine.FloodSelect(set, uv, tolerance, contiguous, sampleLayer);
            if (tex == null) return "Couldn't sample that color.";
            RenderTexture src = sampleLayer && layer.content != null ? layer.content : set.lastComposite;
            var wrapped = new Vector2(uv.x - Mathf.Floor(uv.x), uv.y - Mathf.Floor(uv.y));
            var seed = APUtil.ReadPixel(src, wrapped);
            if (seed.a < 0.02f)
            {
                Object.DestroyImmediate(tex);
                return "There's nothing painted on this layer there — click a colored pixel.";
            }

            var target = ColorRecolorTarget(set);
            if (target == null)
            {
                var maskRT = engine.NewMaskRT(set, 0f);
                CopySelectionToMask(tex, maskRT);
                Object.DestroyImmediate(tex);
                // Recolor by default: the area's average becomes the color wheel's color, shading kept.
                AddAdjustmentLayer(set, AdjustMode.Recolor, targetColor, maskRT);
                // The texture view tints the mask red while it is being edited, so the selection is visible.
                set.editingMask = true;
                return "Recolor layer added for that color — it follows the color wheel.";
            }

            // Extend the same layer's mask instead of making a new layer.
            if (add)
                PixelEdit(set, target.mask, "Add to recolor selection", () => MergeSelectionIntoMask(tex, target.mask, true));
            else if (subtract)
                PixelEdit(set, target.mask, "Remove from recolor selection", () => MergeSelectionIntoMask(tex, target.mask, false));
            else
                PixelEdit(set, target.mask, "Recolor selection", () => CopySelectionToMask(tex, target.mask));
            Object.DestroyImmediate(tex);
            // Recolor maps the masked area's average onto the target color, so re-measure after the mask changes.
            if (target.adjMode == AdjustMode.Recolor) MeasureSource(set, target);
            set.editingMask = true;
            return add ? "Added to the recolor selection."
                : subtract ? "Removed from the recolor selection."
                : target.adjMode == AdjustMode.Recolor
                    ? "Recolor layer updated for that color — it follows the color wheel."
                    : "Selection updated — shift Hue in the Adjust section.";
        }

        /// <summary>Copies a FloodSelect result (R8, white = selected) into a mask RenderTexture.</summary>
        static void CopySelectionToMask(Texture2D sel, RenderTexture mask)
        {
            var comp = APResources.Composite;
            comp.SetFloat(APIds.CopyMode, 1f);
            comp.SetFloat(APIds.SrcToGamma, 0f);
            var prev = RenderTexture.active;
            Graphics.Blit(sel, mask, comp, CompPass.Copy);
            RenderTexture.active = prev;
        }

        /// <summary>Merges a FloodSelect result into an existing mask: add keeps the brighter of the two per
        /// pixel, subtract keeps only what the new selection does not cover.</summary>
        static void MergeSelectionIntoMask(Texture2D sel, RenderTexture mask, bool add)
        {
            int w = sel.width, h = sel.height;
            var cur = APUtil.ReadToTexture(mask);
            var curPx = cur.GetPixels32();
            var selPx = sel.GetPixels32();
            var merged = new byte[w * h];
            for (int i = 0; i < merged.Length; i++)
            {
                int a = curPx[i].r, b = selPx[i].r;
                merged[i] = add ? (byte)Mathf.Max(a, b) : (byte)(a * (255 - b) / 255);
            }
            Object.DestroyImmediate(cur);
            var tex = new Texture2D(w, h, TextureFormat.R8, false, true) { hideFlags = HideFlags.HideAndDontSave };
            tex.LoadRawTextureData(merged);
            tex.Apply(false);
            CopySelectionToMask(tex, mask);
            Object.DestroyImmediate(tex);
        }

        /// <summary>Measures the current colors under the layer's mask (what Recolor and Match start from).</summary>
        public void MeasureSource(TextureSet set, PaintLayer layer)
        {
            int idx = set.layers.IndexOf(layer);
            if (idx < 0) return;
            if (engine.MeasureRegion(set, idx, layer.mask != null ? (Texture)layer.mask : APResources.White, out var mean, out var std, out var hist, true))
            {
                layer.srcMean = mean;
                layer.srcStd = std;
                layer.srcHist = hist;
                ResetLut(layer);
            }
            Changed(set);
        }

        static void ResetLut(PaintLayer layer)
        {
            if (layer.matchLut != null) Object.DestroyImmediate(layer.matchLut);
            layer.matchLut = null;
        }

        /// <summary>Match mode: the current UV selection becomes the area whose colors this layer copies.</summary>
        public bool SetMatchReference(TextureSet set, PaintLayer layer)
        {
            if (!set.HasSelection) return false;
            if (set.selectionDirty || set.selectionRT == null) engine.BuildSelection(set);
            int idx = set.layers.IndexOf(layer);
            if (!engine.MeasureRegion(set, idx, set.selectionRT, out var mean, out var std, out var hist)) return false;
            MeasureSource(set, layer);
            var oldMean = layer.refMean; var oldStd = layer.refStd; bool oldValid = layer.refValid; var oldHist = layer.refHist;
            void Set(Vector4 m, Vector4 s, bool v, float[] h)
            {
                layer.refMean = m; layer.refStd = s; layer.refValid = v; layer.refHist = h;
                ResetLut(layer);
                Changed(set);
            }
            Set(mean, std, true, hist);
            Push(new ActionEntry("Set match reference", () => Set(oldMean, oldStd, oldValid, oldHist), () => Set(mean, std, true, hist)));
            return true;
        }

        /// <summary>Adds an image layer. The image must be a gamma-value texture (see APUtil.ToGammaTexture).</summary>
        public PaintLayer AddImageLayer(TextureSet set, Texture2D image, ImagePlacement placement, LayerBlendMode blend = LayerBlendMode.Normal)
        {
            if (!set.initialized) InitializeSet(set);
            placement.image = image;
            var layer = new PaintLayer
            {
                name = image != null ? image.name : UniqueName(set, "Image"),
                kind = LayerKind.Image,
                placement = placement,
                blend = blend,
            };
            layer.content = engine.NewContentRT(set);
            engine.RenderImageLayer(set, layer);
            InsertLayer(set, layer, InsertIndex(set), "Add image");
            return layer;
        }

        /// <summary>Swaps an image layer's picture (e.g. re-rendered text after editing it), keeping its placement. Undoable.</summary>
        public void ReplaceImage(TextureSet set, PaintLayer layer, Texture2D image, string text, int textSize, Color textColor, bool textBold)
        {
            if (layer?.placement == null) return;
            var oldImage = layer.placement.image;
            var oldText = layer.placement.text;
            var oldSize = layer.placement.textSize;
            var oldColor = layer.placement.textColor;
            var oldBold = layer.placement.textBold;
            var oldName = layer.name;
            void Apply(Texture2D img, string t, int s, Color c, bool b, string n)
            {
                layer.placement.image = img;
                layer.placement.text = t;
                layer.placement.textSize = s;
                layer.placement.textColor = c;
                layer.placement.textBold = b;
                layer.name = n;
                engine.RenderImageLayer(set, layer);
                Changed(set);
            }
            Apply(image, text, textSize, textColor, textBold, image != null ? image.name : oldName);
            Push(new ActionEntry("Edit text",
                () => Apply(oldImage, oldText, oldSize, oldColor, oldBold, oldName),
                () => Apply(image, text, textSize, textColor, textBold, image != null ? image.name : oldName)));
        }

        /// <summary>
        /// The textures a wrapped image should go into: each material's normal map slot (created if the material has
        /// none yet) for normal maps, otherwise its main texture.
        /// </summary>
        public List<TextureSet> WrapTargets(TextureSet from, bool allMaterials, bool normal, out int skipped)
        {
            skipped = 0;
            var reps = new List<TextureSet>();
            var seen = new HashSet<Material>();
            foreach (var s in allMaterials ? new List<TextureSet>(sets) : new List<TextureSet> { from })
                if (s != null && s.originalMaterial != null && seen.Add(s.originalMaterial)) reps.Add(s);
            var result = new List<TextureSet>();
            bool created = false;
            foreach (var rep in reps)
            {
                string prop = normal ? NormalProperty(rep.cloneMaterial) : DefaultProperty(rep.cloneMaterial);
                if (prop == null)
                {
                    skipped++;
                    continue;
                }
                TextureSet target = null;
                foreach (var s in sets)
                    if (s.originalMaterial == rep.originalMaterial && s.property == prop) { target = s; break; }
                if (target == null)
                {
                    target = CreateSiblingSet(rep, prop);
                    created = true;
                }
                if (!target.initialized) InitializeSet(target);
                result.Add(target);
            }
            if (created) StructureChanged?.Invoke();
            return result;
        }

        /// <summary>Adds one wrapped image layer per texture. Normal maps are layered over the existing normal map.</summary>
        public List<PaintLayer> AddWrapLayers(IList<TextureSet> targets, Texture2D image, ImagePlacement template)
        {
            var added = new List<PaintLayer>();
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                var img = image;
                if (i > 0)
                {
                    img = Object.Instantiate(image);
                    img.name = image.name;
                    img.hideFlags = HideFlags.HideAndDontSave;
                }
                var p = template.Clone();
                p.wrap = true;
                p.projected = false;
                var blend = p.normalMap && IsNormalMap(t) ? LayerBlendMode.NormalCombine : LayerBlendMode.Normal;
                added.Add(AddImageLayer(t, img, p, blend));
            }
            return added;
        }

        /// <summary>Other wrapped layers made from the same image (one per material).</summary>
        public List<(TextureSet set, PaintLayer layer)> WrapSiblings(PaintLayer layer)
        {
            var list = new List<(TextureSet, PaintLayer)>();
            if (layer?.placement == null || !layer.placement.wrap) return list;
            foreach (var s in sets)
                foreach (var l in s.layers)
                    if (l != layer && l.kind == LayerKind.Image && l.placement != null && l.placement.wrap && l.name == layer.name)
                        list.Add((s, l));
            return list;
        }

        /// <summary>Copies a wrapped layer's size, rotation, offset and normal settings to its siblings.</summary>
        public void SyncWrapSettings(PaintLayer layer)
        {
            if (layer?.placement == null) return;
            var v = layer.placement.Clone();
            void Copy(ImagePlacement p)
            {
                p.tileSize = v.tileSize;
                p.sharpness = v.sharpness;
                p.axis = v.axis;
                p.wrapOffset = v.wrapOffset;
                p.rotation = v.rotation;
                p.flipX = v.flipX;
                p.normalMap = v.normalMap;
                p.normalStrength = v.normalStrength;
                p.greenDown = v.greenDown;
                p.linked = v.linked;
            }
            if (v.linked)
            {
                // A linked edit reaches every sibling at once.
                TextureSet owner = null;
                foreach (var s in sets)
                    if (s.layers.Contains(layer)) { owner = s; break; }
                if (owner != null) EditPlacement(owner, layer, "wrap sync", Copy);
                return;
            }
            foreach (var (s, l) in WrapSiblings(layer)) EditPlacement(s, l, "wrap sync", Copy);
        }

        public void DeleteLayer(TextureSet set, PaintLayer layer)
        {
            int index = set.layers.IndexOf(layer);
            if (index < 0 || set.layers.Count <= 1) return;
            set.layers.RemoveAt(index);
            int prevActive = set.activeLayer;
            set.activeLayer = Mathf.Clamp(index - 1, 0, set.layers.Count - 1);
            set.editingMask = false;
            Push(new ActionEntry("Delete layer",
                () => { set.layers.Insert(Mathf.Clamp(index, 0, set.layers.Count), layer); set.activeLayer = prevActive; Changed(set); },
                () => { set.layers.Remove(layer); set.activeLayer = Mathf.Clamp(index - 1, 0, set.layers.Count - 1); Changed(set); },
                applied => { if (applied && !set.layers.Contains(layer)) layer.Release(); },
                layer.Bytes));
            Changed(set);
        }

        /// <summary>An independent copy of a layer: its own content and mask textures, placement and settings.</summary>
        public PaintLayer CloneLayer(PaintLayer layer)
        {
            var copy = new PaintLayer
            {
                name = layer.name,
                kind = layer.kind,
                visible = layer.visible,
                opacity = layer.opacity,
                blend = layer.blend,
                lockAlpha = layer.lockAlpha,
                fillColor = layer.fillColor,
                maskEnabled = layer.maskEnabled,
                adjMode = layer.adjMode,
                clipBelow = layer.clipBelow,
                adjColor = layer.adjColor,
                hue = layer.hue,
                saturation = layer.saturation,
                lightness = layer.lightness,
                srcMean = layer.srcMean,
                srcStd = layer.srcStd,
                refMean = layer.refMean,
                refStd = layer.refStd,
                refValid = layer.refValid,
                srcHist = layer.srcHist,
                refHist = layer.refHist,
                content = engine.CloneRT(layer.content, "Layer"),
                mask = engine.CloneRT(layer.mask, "Mask"),
            };
            if (layer.placement != null)
            {
                copy.placement = layer.placement.Clone();
                copy.placement.image = layer.placement.image != null ? Object.Instantiate(layer.placement.image) : null;
                if (copy.placement.image != null) copy.placement.image.hideFlags = HideFlags.HideAndDontSave;
            }
            return copy;
        }

        public PaintLayer DuplicateLayer(TextureSet set, PaintLayer layer)
        {
            var copy = CloneLayer(layer);
            copy.name = layer.name + " copy";
            InsertLayer(set, copy, set.layers.IndexOf(layer) + 1, "Duplicate layer");
            return copy;
        }

        /// <summary>
        /// Copies a layer onto the same material's emission texture, so what it draws glows, as a new layer on top
        /// of the emission stack. Returns null when the material has no emission texture (or the layer is already
        /// on one). Undoable, like every other layer operation.
        /// </summary>
        public PaintLayer CopyLayerToEmission(TextureSet set, PaintLayer layer)
        {
            if (set == null || layer == null || !set.layers.Contains(layer)) return null;
            var emSet = EmissionSibling(set);
            if (emSet == null) return null;
            if (!emSet.initialized) InitializeSet(emSet);
            var copy = CloneLayer(layer);
            bool nameTaken = false;
            foreach (var l in emSet.layers)
                if (l.name == copy.name) { nameTaken = true; break; }
            if (nameTaken) copy.name = UniqueName(emSet, layer.name);
            InsertLayer(emSet, copy, emSet.layers.Count, "Add layer to emission");
            return copy;
        }

        /// <summary>
        /// Copies the selected area's finished colors onto the same material's emission texture, as a new layer
        /// on top of the emission stack, so the selected parts glow. Returns null when nothing is selected or
        /// the material has no emission texture (or the selection is already on one). Undoable, like every
        /// other layer operation. The base selection is kept.
        /// </summary>
        public PaintLayer CopySelectionToEmission(TextureSet set)
        {
            if (set == null || !set.HasSelection || !set.initialized) return null;
            var emSet = EmissionSibling(set);
            if (emSet == null) return null;
            if (!emSet.initialized) InitializeSet(emSet);
            if (set.selectionDirty || set.selectionRT == null) engine.BuildSelection(set);
            if (set.selectionRT == null) return null;
            var composite = engine.CompositeForExport(set, 0);
            var layer = new PaintLayer { name = UniqueName(emSet, "Selection"), kind = LayerKind.Paint };
            layer.content = engine.NewContentRT(emSet);
            // The layer stack's own Layer pass, with the finished texture as the source and the selection as
            // the mask: the selected pixels and nothing else. Both are sampled in normalised UV space, so this
            // works even when the two textures differ in resolution (assuming the usual shared UV layout).
            var comp = APResources.Composite;
            comp.SetFloat(APIds.IsFill, 0f);
            comp.SetTexture(APIds.LayerTex, composite);
            comp.SetFloat(APIds.HasMask, 1f);
            comp.SetTexture(APIds.MaskTex, set.selectionRT);
            comp.SetFloat(APIds.Opacity, 1f);
            comp.SetInt(APIds.BlendMode, (int)LayerBlendMode.Normal);
            var clear = engine.NewContentRT(emSet);
            var prev = RenderTexture.active;
            Graphics.Blit(clear, layer.content, comp, CompPass.Layer);
            RenderTexture.active = prev;
            APUtil.Release(ref composite);
            APUtil.Release(ref clear);
            InsertLayer(emSet, layer, emSet.layers.Count, "Add selection to emission");
            return layer;
        }

        /// <summary>
        /// Renders one layer on its own into a fresh texture: its pixels with its mask applied, at full opacity
        /// and Normal blend (blend and opacity only mean something against the layers below, so they are not
        /// baked in), even when the layer is hidden. Adjustment layers have no pixels of their own, so this
        /// returns null for them. The caller must release the returned texture.
        /// </summary>
        public RenderTexture RenderLayerAlone(TextureSet set, PaintLayer layer)
        {
            if (set == null || layer == null || !set.initialized || !set.layers.Contains(layer)) return null;
            if (layer.kind == LayerKind.Adjustment) return null;
            var alone = new PaintLayer
            {
                kind = layer.kind,
                visible = true,
                opacity = 1f,
                blend = LayerBlendMode.Normal,
                fillColor = layer.fillColor,
                content = layer.content,
                mask = layer.mask,
                maskEnabled = layer.maskEnabled,
            };
            var rt = engine.NewContentRT(set);
            engine.CompositeSubset(set, new List<PaintLayer> { alone }, rt);
            return rt;
        }

        public void MoveLayer(TextureSet set, PaintLayer layer, int newIndex)
        {
            int old = set.layers.IndexOf(layer);
            newIndex = Mathf.Clamp(newIndex, 0, set.layers.Count - 1);
            if (old < 0 || old == newIndex) return;
            void Move(int from, int to)
            {
                set.layers.RemoveAt(from);
                set.layers.Insert(to, layer);
                set.activeLayer = to;
                Changed(set);
            }
            Move(old, newIndex);
            Push(new ActionEntry("Move layer", () => Move(newIndex, old), () => Move(old, newIndex)));
        }

        public void SetActiveLayer(TextureSet set, PaintLayer layer, bool mask)
        {
            int i = set.layers.IndexOf(layer);
            if (i < 0) return;
            set.activeLayer = i;
            set.editingMask = mask && layer.HasMask;
            set.dirty = true;
            StructureChanged?.Invoke();
        }

        public void MergeDown(TextureSet set, PaintLayer upper)
        {
            int i = set.layers.IndexOf(upper);
            if (i <= 0) return;
            var lower = set.layers[i - 1];
            var merged = engine.NewContentRT(set);
            engine.CompositeSubset(set, new List<PaintLayer> { lower, upper }, merged);

            var oldContent = lower.content;
            var oldMask = lower.mask;
            var oldState = LayerState.Of(lower);
            var oldKind = lower.kind;
            var oldPlacement = lower.placement;

            void Do()
            {
                lower.content = merged;
                lower.mask = null;
                lower.kind = LayerKind.Paint;
                lower.placement = null;
                lower.opacity = 1f;
                lower.blend = LayerBlendMode.Normal;
                lower.lockAlpha = false;
                lower.visible = true;
                set.layers.Remove(upper);
                set.activeLayer = set.layers.IndexOf(lower);
                set.editingMask = false;
                Changed(set);
            }

            void UndoMerge()
            {
                lower.content = oldContent;
                lower.mask = oldMask;
                lower.kind = oldKind;
                lower.placement = oldPlacement;
                oldState.ApplyTo(lower);
                set.layers.Insert(Mathf.Clamp(set.layers.IndexOf(lower) + 1, 0, set.layers.Count), upper);
                set.activeLayer = set.layers.IndexOf(upper);
                Changed(set);
            }

            Do();
            Push(new ActionEntry("Merge down", UndoMerge, Do, applied =>
            {
                if (applied)
                {
                    APUtil.Release(ref oldContent);
                    APUtil.Release(ref oldMask);
                    if (oldPlacement != null && oldPlacement.image != null) Object.DestroyImmediate(oldPlacement.image);
                    upper.Release();
                }
                else
                {
                    APUtil.Release(ref merged);
                }
            }, APUtil.EstimateBytes(merged)));
        }

        /// <summary>Snapshot-based undo for an operation that rewrites a layer's pixels.</summary>
        void PixelEdit(TextureSet set, RenderTexture target, string name, Action edit)
        {
            if (target == null) return;
            var snap = engine.CloneRT(target, "Undo");
            edit();
            Push(new PixelEntry(name, target, snap, () => Changed(set)));
            Changed(set);
        }

        public void ClearLayer(TextureSet set, PaintLayer layer)
        {
            if (set.editingMask && layer.HasMask)
            {
                PixelEdit(set, layer.mask, "Clear mask", () => APUtil.Clear(layer.mask, Color.white));
                return;
            }
            if (layer.content == null) return;
            PixelEdit(set, layer.content, "Clear layer", () => APUtil.Clear(layer.content, new Color(0, 0, 0, 0)));
        }

        /// <summary>Luminance in the shader's terms (AP_Composite AP_Lum), on 0..1 colour values.</summary>
        static float Lum(float r, float g, float b) => 0.3f * r + 0.59f * g + 0.11f * b;

        static float Lum(Color32 p) => (0.3f * p.r + 0.59f * p.g + 0.11f * p.b) / 255f;

        /// <summary>AP_Composite AP_ClipColor / AP_SetLum, in C#: shift a colour to a target luminance, keeping it in gamut.</summary>
        static Vector3 ClipColor(Vector3 c)
        {
            float l = Lum(c.x, c.y, c.z);
            float n = Mathf.Min(c.x, Mathf.Min(c.y, c.z));
            float x = Mathf.Max(c.x, Mathf.Max(c.y, c.z));
            if (n < 0f) c = new Vector3(l, l, l) + (c - new Vector3(l, l, l)) * (l / Mathf.Max(l - n, 1e-5f));
            if (x > 1f) c = new Vector3(l, l, l) + (c - new Vector3(l, l, l)) * ((1f - l) / Mathf.Max(x - l, 1e-5f));
            return c;
        }

        static Vector3 SetLum(Vector3 c, float l) => ClipColor(c + new Vector3(l - Lum(c.x, c.y, c.z), l - Lum(c.x, c.y, c.z), l - Lum(c.x, c.y, c.z)));

        /// <summary>
        /// Lasso recolor: recolors only the pixels inside a freehand loop on the active layer, to the given colour.
        /// Transparent pixels are left alone and alpha is preserved. This is NOT a multiply tint (black x any colour
        /// is still black, which is why the normal Recolor adjustment never changes black text): the colour is
        /// replaced while the pixel's shading is kept, using the same HSV math as the Recolor adjustment
        /// (AP_Composite frag_adjust: the target's hue and saturation, the pixel's value scaled so the area's
        /// mean value becomes the target's value, detail kept) —
        /// and pixels that are essentially black ink get the new colour at full strength, because black text's shape
        /// lives in its alpha, not its luminance. An image layer is rasterized first (the same as painting on one),
        /// as part of the same undo step. Returns a message for the user when nothing could be recoloured.
        /// </summary>
        public string LassoRecolor(TextureSet set, byte[] mask, Color target)
        {
            if (set == null || set.ActiveLayer == null) return "Pick a material and a layer first.";
            if (set.IsMirrored) return "This material live-mirrors another — recolor on that material instead.";
            if (set.editingMask) return "Lasso recolor paints the layer, not its mask — turn mask editing off first.";
            var layer = set.ActiveLayer;
            if (layer.content == null)
            {
                if (layer.kind != LayerKind.Image || layer.placement == null) return "That layer has no pixels to recolor.";
                engine.RenderImageLayer(set, layer);
                if (layer.content == null) return "That layer has no pixels to recolor.";
            }
            if (mask == null || mask.Length != set.width * set.height) return "Draw a loop around the area to recolor first.";
            int w = set.width, h = set.height;
            var tex = APUtil.ReadToTexture(layer.content);
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            // First pass: how much shading is there to keep? Mean value (brightest channel) of the affected
            // pixels, like the Recolor adjustment's _SrcMean.
            float meanV = 0f;
            int count = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (mask[i] == 0 || px[i].a == 0) continue;
                meanV += Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b)) / 255f;
                count++;
            }
            if (count == 0) return "There's nothing painted on this layer inside the loop.";
            meanV = Mathf.Max(meanV / count, 0.03f);
            Color.RGBToHSV(target, out float tH, out float tS, out float tV);
            // Second pass: recolor. Black ink (luminance ~0) gets the new colour at full strength — its shape is
            // in the alpha, so there is no shading to scale. Everything else takes the target's hue and
            // saturation with its own value scaled so the mean becomes the target's value, the same as the
            // Recolor adjustment: in HSV the brightest pixels stay the target colour at full brightness instead
            // of clipping to white. Alpha is never touched.
            var targetVec = new Vector3(target.r, target.g, target.b);
            for (int i = 0; i < px.Length; i++)
            {
                if (mask[i] == 0 || px[i].a == 0) continue;
                var p = px[i];
                float lum = Lum(p);
                float v = Mathf.Max(p.r, Mathf.Max(p.g, p.b)) / 255f;
                Vector3 recoloured;
                if (lum <= 0.02f) recoloured = targetVec;
                else
                {
                    Color rc = Color.HSVToRGB(tH, tS, Mathf.Clamp01(v * tV / meanV));
                    recoloured = new Vector3(rc.r, rc.g, rc.b);
                }
                p.r = (byte)Mathf.Clamp(Mathf.RoundToInt(recoloured.x * 255f), 0, 255);
                p.g = (byte)Mathf.Clamp(Mathf.RoundToInt(recoloured.y * 255f), 0, 255);
                p.b = (byte)Mathf.Clamp(Mathf.RoundToInt(recoloured.z * 255f), 0, 255);
                px[i] = p;
            }
            var cut = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
            cut.SetPixels32(px);
            cut.Apply(false);
            var entries = new List<IHistoryEntry>();
            if (layer.kind == LayerKind.Image)
            {
                var placement = layer.placement;
                entries.Add(new ActionEntry("Rasterize image",
                    () => { layer.kind = LayerKind.Image; layer.placement = placement; Changed(set); },
                    () => { layer.kind = LayerKind.Paint; layer.placement = null; Changed(set); },
                    applied => { if (applied && placement != null && placement.image != null) Object.DestroyImmediate(placement.image); }));
                layer.kind = LayerKind.Paint;
                layer.placement = null;
            }
            var snap = engine.CloneRT(layer.content, "Undo");
            APUtil.CopyToGammaData(cut, layer.content, false);
            Object.DestroyImmediate(cut);
            entries.Add(new PixelEntry("Lasso recolor", layer.content, snap, () => Changed(set)));
            Push(entries.Count == 1 ? entries[0] : new CompoundEntry("Lasso recolor", entries));
            Changed(set);
            return null;
        }

        // ------------------------------------------------------------------ masks

        public void AddMask(TextureSet set, PaintLayer layer, float value)
        {
            if (layer.HasMask) return;
            var mask = engine.NewMaskRT(set, value);
            layer.mask = mask;
            layer.maskEnabled = true;
            set.editingMask = true;
            Push(new ActionEntry("Add mask",
                () => { layer.mask = null; set.editingMask = false; Changed(set); },
                () => { layer.mask = mask; set.editingMask = true; Changed(set); },
                applied => { if (!applied && layer.mask != mask) APUtil.Release(ref mask); },
                APUtil.EstimateBytes(mask)));
            Changed(set);
        }

        public void DeleteMask(TextureSet set, PaintLayer layer)
        {
            if (!layer.HasMask) return;
            var mask = layer.mask;
            layer.mask = null;
            set.editingMask = false;
            Push(new ActionEntry("Delete mask",
                () => { layer.mask = mask; Changed(set); },
                () => { layer.mask = null; set.editingMask = false; Changed(set); },
                applied => { if (applied && layer.mask != mask) APUtil.Release(ref mask); }));
            Changed(set);
        }

        public void InvertMask(TextureSet set, PaintLayer layer)
        {
            if (!layer.HasMask) return;
            PixelEdit(set, layer.mask, "Invert mask", () =>
            {
                var tmp = engine.CloneRT(layer.mask, "tmp");
                var comp = APResources.Composite;
                comp.SetFloat(APIds.CopyMode, 2f);
                comp.SetFloat(APIds.SrcToGamma, 0f);
                var prev = RenderTexture.active;
                Graphics.Blit(tmp, layer.mask, comp, CompPass.Copy);
                RenderTexture.active = prev;
                APUtil.Release(ref tmp);
            });
        }

        /// <summary>Bakes the mask into the layer's own transparency and removes it.</summary>
        public void ApplyMask(TextureSet set, PaintLayer layer)
        {
            if (!layer.HasMask) return;
            if (layer.kind == LayerKind.Fill) ConvertFillToPaint(set, layer);
            if (layer.kind == LayerKind.Image) RasterizeImage(set, layer);
            var mask = layer.mask;
            var oldContent = layer.content;
            var newContent = engine.NewContentRT(set);
            var single = new PaintLayer { content = oldContent, mask = mask, maskEnabled = true, kind = LayerKind.Paint };
            engine.CompositeSubset(set, new List<PaintLayer> { single }, newContent);
            void Do() { layer.content = newContent; layer.mask = null; set.editingMask = false; Changed(set); }
            void UndoIt() { layer.content = oldContent; layer.mask = mask; Changed(set); }
            Do();
            Push(new ActionEntry("Apply mask", UndoIt, Do, applied =>
            {
                if (applied) { APUtil.Release(ref oldContent); APUtil.Release(ref mask); }
                else APUtil.Release(ref newContent);
            }));
        }

        public void ConvertFillToPaint(TextureSet set, PaintLayer layer)
        {
            if (layer.kind != LayerKind.Fill) return;
            var content = engine.NewContentRT(set, layer.fillColor);
            var oldContent = layer.content;
            void Do() { layer.kind = LayerKind.Paint; layer.content = content; Changed(set); }
            void UndoIt() { layer.kind = LayerKind.Fill; layer.content = oldContent; Changed(set); }
            Do();
            Push(new ActionEntry("Convert to paint layer", UndoIt, Do, applied =>
            {
                if (!applied) APUtil.Release(ref content);
            }));
        }

        public void RasterizeImage(TextureSet set, PaintLayer layer)
        {
            if (layer.kind != LayerKind.Image) return;
            var placement = layer.placement;
            void Do() { layer.kind = LayerKind.Paint; layer.placement = null; Changed(set); }
            void UndoIt() { layer.kind = LayerKind.Image; layer.placement = placement; Changed(set); }
            Do();
            Push(new ActionEntry("Rasterize image", UndoIt, Do, applied =>
            {
                if (applied && placement != null && placement.image != null) Object.DestroyImmediate(placement.image);
            }));
        }

        // ------------------------------------------------------------------ resolution

        public void ResizeSet(TextureSet set, int width, int height)
        {
            if (!set.initialized) { set.width = width; set.height = height; return; }
            history.Clear();
            engine.Resize(set, width, height);
            AssignDisplay(set);
            Changed(set);
        }

        public void SetUVChannel(TextureSet set, int channel)
        {
            if (set.uvChannel == channel) return;
            set.uvChannel = channel;
            set.islandDirty = true;
            set.selection.Clear();
            set.selectionDirty = true;
            foreach (var l in set.layers)
                if (l.kind == LayerKind.Image && l.placement != null && (l.placement.projected || l.placement.wrap)) engine.RenderImageLayer(set, l);
            Changed(set);
        }
    }
}
