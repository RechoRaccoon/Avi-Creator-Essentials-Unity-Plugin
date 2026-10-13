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

            public static LayerState Of(PaintLayer l) => new LayerState
            {
                name = l.name,
                visible = l.visible,
                opacity = l.opacity,
                blend = l.blend,
                lockAlpha = l.lockAlpha,
                fillColor = l.fillColor,
                maskEnabled = l.maskEnabled,
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
            var before = layer.placement.Clone();
            change(layer.placement);
            var after = layer.placement.Clone();
            engine.RenderImageLayer(set, layer);
            string key = layer.id + "/placement/" + what;
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            void Restore(ImagePlacement p)
            {
                if (layer.placement == null) return;
                var img = layer.placement.image;
                layer.placement = p.Clone();
                layer.placement.image = img;
                engine.RenderImageLayer(set, layer);
                Changed(set);
            }
            if (history.Top is MergeableEntry top && top.key == key && now - top.time < 1.5)
            {
                top.time = now;
                top.redo = () => Restore(after);
            }
            else
            {
                Push(new MergeableEntry { Name = "Move image", key = key, time = now, undo = () => Restore(before), redo = () => Restore(after) });
            }
            Changed(set);
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

        public PaintLayer AddFillLayer(TextureSet set, Color color, bool blackMask)
        {
            if (!set.initialized) InitializeSet(set);
            var layer = new PaintLayer { name = UniqueName(set, "Fill"), kind = LayerKind.Fill, fillColor = color };
            if (blackMask) layer.mask = engine.NewMaskRT(set, 0f);
            InsertLayer(set, layer, InsertIndex(set), "New fill layer");
            if (blackMask) set.editingMask = true;
            return layer;
        }

        /// <summary>Adds an image layer. The image must be a gamma-value texture (see APUtil.ToGammaTexture).</summary>
        public PaintLayer AddImageLayer(TextureSet set, Texture2D image, ImagePlacement placement)
        {
            if (!set.initialized) InitializeSet(set);
            placement.image = image;
            var layer = new PaintLayer
            {
                name = image != null ? image.name : UniqueName(set, "Image"),
                kind = LayerKind.Image,
                placement = placement,
            };
            layer.content = engine.NewContentRT(set);
            engine.RenderImageLayer(set, layer);
            InsertLayer(set, layer, InsertIndex(set), "Add image");
            return layer;
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
                if (l.kind == LayerKind.Image && l.placement != null && l.placement.projected) engine.RenderImageLayer(set, l);
            Changed(set);
        }
    }
}
