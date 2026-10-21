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
        public PaintLayer AddAdjustmentLayer(TextureSet set, AdjustMode mode, Color color)
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
            layer.mask = MaskFromSelection(set) ?? engine.NewMaskRT(set, 1f);
            int index = InsertIndex(set);
            InsertLayer(set, layer, index, "New " + baseName.ToLowerInvariant() + " layer");
            set.editingMask = false;
            MeasureSource(set, layer);
            return layer;
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

        public PaintLayer DuplicateLayer(TextureSet set, PaintLayer layer)
        {
            var copy = new PaintLayer
            {
                name = layer.name + " copy",
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
            InsertLayer(set, copy, set.layers.IndexOf(layer) + 1, "Duplicate layer");
            return copy;
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
