using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Transform tool: lifts part of a paint layer (a lasso shape, a similar-color area or the UV selection) into a
    /// floating image layer that can be moved, scaled, rotated and warped, then applied back onto the layer.
    /// </summary>
    public partial class APSession
    {
        /// <summary>The floating piece being transformed in this set, if any.</summary>
        public PaintLayer FloatingLayer(TextureSet set)
        {
            if (set == null) return null;
            foreach (var l in set.layers)
                if (l.IsFloating) return l;
            return null;
        }

        /// <summary>Mask (one byte per texel, 255 = inside) of a polygon given in UV space.</summary>
        public static byte[] PolygonMask(TextureSet set, IList<Vector2> uvPolygon)
        {
            int w = set.width, h = set.height;
            var mask = new byte[w * h];
            int n = uvPolygon.Count;
            if (n < 3) return mask;
            var pts = new Vector2[n];
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                pts[i] = new Vector2(uvPolygon[i].x * w, uvPolygon[i].y * h);
                minY = Mathf.Min(minY, pts[i].y);
                maxY = Mathf.Max(maxY, pts[i].y);
            }
            int y0 = Mathf.Clamp(Mathf.FloorToInt(minY), 0, h - 1), y1 = Mathf.Clamp(Mathf.CeilToInt(maxY), 0, h - 1);
            var xs = new List<float>();
            for (int y = y0; y <= y1; y++)
            {
                // Scanline fill (even-odd) through texel centres.
                float cy = y + 0.5f;
                xs.Clear();
                for (int i = 0; i < n; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % n];
                    if ((a.y <= cy && b.y > cy) || (b.y <= cy && a.y > cy))
                        xs.Add(a.x + (cy - a.y) / (b.y - a.y) * (b.x - a.x));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int xa = Mathf.Clamp(Mathf.CeilToInt(xs[k] - 0.5f), 0, w);
                    int xb = Mathf.Clamp(Mathf.FloorToInt(xs[k + 1] - 0.5f), -1, w - 1);
                    for (int x = xa; x <= xb; x++) mask[y * w + x] = 255;
                }
            }
            return mask;
        }

        /// <summary>Mask of pixels similar in color to the one at uv, on the given layer.</summary>
        public byte[] ColorMask(TextureSet set, Vector2 uv, float tolerance, bool contiguous)
        {
            var tex = engine.FloodSelect(set, uv, tolerance, contiguous, true);
            if (tex == null) return null;
            var bytes = tex.GetRawTextureData();
            Object.DestroyImmediate(tex);
            return bytes;
        }

        /// <summary>Mask of the current UV selection, or null when nothing is selected.</summary>
        public byte[] SelectionMask(TextureSet set)
        {
            if (set == null || !set.HasSelection) return null;
            if (set.selectionDirty || set.selectionRT == null) engine.BuildSelection(set);
            if (set.selectionRT == null) return null;
            var tex = APUtil.ReadToTexture(set.selectionRT);
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            var mask = new byte[set.width * set.height];
            if (px.Length != mask.Length) return null;
            for (int i = 0; i < px.Length; i++) mask[i] = px[i].r;
            return mask;
        }

        /// <summary>
        /// Cuts (or copies) the masked pixels of a paint layer into a new floating image layer just above it.
        /// Returns a message for the user when nothing could be lifted, otherwise null.
        /// </summary>
        public string Lift(TextureSet set, PaintLayer source, byte[] mask, bool copy)
        {
            if (set == null || source == null) return "Pick a material first.";
            if (source.kind != LayerKind.Paint || source.content == null)
                return "The Transform tool moves pixels of a paint layer. Select a paint layer (or rasterize this one) first.";
            if (mask == null || mask.Length != set.width * set.height) return "Nothing to move there.";
            int w = set.width, h = set.height;

            var tex = APUtil.ReadToTexture(source.content);
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);

            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (mask[row + x] == 0 || px[row + x].a == 0) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
            if (x1 < 0) return "There's nothing painted on this layer there. Pick the layer that holds the pixels you want to move.";

            // A transparent border keeps clamped sampling from smearing the edge pixels when the piece is moved.
            const int pad = 2;
            int cw = x1 - x0 + 1 + pad * 2, ch = y1 - y0 + 1 + pad * 2;
            var piece = new Color32[cw * ch];
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int i = y * w + x;
                byte m = mask[i];
                if (m == 0) continue;
                var c = px[i];
                var lifted = c;
                lifted.a = (byte)(c.a * m / 255);
                piece[(y - y0 + pad) * cw + (x - x0 + pad)] = lifted;
                if (!copy) px[i].a = (byte)(c.a * (255 - m) / 255);
            }

            var image = new Texture2D(cw, ch, TextureFormat.RGBA32, false, true)
            {
                name = "Floating",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            image.SetPixels32(piece);
            image.Apply(false);

            var entries = new List<IHistoryEntry>();
            RenderTexture snapshot = null;
            if (!copy)
            {
                snapshot = engine.CloneRT(source.content, "Lift snapshot");
                var undoSnap = engine.CloneRT(source.content, "Undo");
                var cut = new Texture2D(w, h, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
                cut.SetPixels32(px);
                cut.Apply(false);
                APUtil.CopyToGammaData(cut, source.content, false);
                Object.DestroyImmediate(cut);
                entries.Add(new PixelEntry("Cut", source.content, undoSnap, () => Changed(set)));
            }

            var placement = new ImagePlacement
            {
                projected = false,
                clipToIslands = false,
                uvCenter = new Vector2((x0 - pad + cw * 0.5f) / w, (y0 - pad + ch * 0.5f) / h),
                uvSize = new Vector2(cw / (float)w, ch / (float)h),
                image = image,
            };
            var layer = new PaintLayer
            {
                name = copy ? "Floating copy" : "Floating",
                kind = LayerKind.Image,
                placement = placement,
                blend = source.blend,
                liftedFrom = source,
                liftSnapshot = snapshot,
            };
            layer.content = engine.NewContentRT(set);
            engine.RenderImageLayer(set, layer);

            int index = set.layers.IndexOf(source) + 1;
            int prevActive = set.activeLayer;
            set.layers.Insert(index, layer);
            set.activeLayer = index;
            set.editingMask = false;
            entries.Add(new ActionEntry("Lift",
                () => { set.layers.Remove(layer); set.activeLayer = Mathf.Clamp(prevActive, 0, set.layers.Count - 1); Changed(set); },
                () => { set.layers.Insert(Mathf.Clamp(index, 0, set.layers.Count), layer); set.activeLayer = set.layers.IndexOf(layer); Changed(set); },
                applied => { if (!applied && !set.layers.Contains(layer)) layer.Release(); },
                layer.Bytes + APUtil.EstimateBytes(snapshot)));
            Push(new CompoundEntry(copy ? "Copy for transform" : "Lift for transform", entries));
            Changed(set);
            return null;
        }

        /// <summary>Paints the floating piece back onto the layer it came from, in its new place.</summary>
        public void ApplyFloating(TextureSet set, PaintLayer floating)
        {
            if (set == null || floating == null || !floating.IsFloating) return;
            var source = floating.liftedFrom;
            if (!set.layers.Contains(source) || source.content == null)
            {
                // The original layer is gone; the piece simply becomes a paint layer of its own.
                floating.liftedFrom = null;
                APUtil.Release(ref floating.liftSnapshot);
                RasterizeImage(set, floating);
                return;
            }

            var merged = engine.NewContentRT(set);
            var under = new PaintLayer { kind = LayerKind.Paint, content = source.content };
            var over = new PaintLayer { kind = LayerKind.Paint, content = floating.content, opacity = floating.opacity, blend = floating.blend };
            engine.CompositeSubset(set, new List<PaintLayer> { under, over }, merged);

            var oldContent = source.content;
            int index = set.layers.IndexOf(floating);
            void Do()
            {
                source.content = merged;
                set.layers.Remove(floating);
                set.activeLayer = Mathf.Max(0, set.layers.IndexOf(source));
                set.editingMask = false;
                Changed(set);
            }
            void UndoIt()
            {
                source.content = oldContent;
                set.layers.Insert(Mathf.Clamp(index, 0, set.layers.Count), floating);
                set.activeLayer = set.layers.IndexOf(floating);
                Changed(set);
            }
            Do();
            Push(new ActionEntry("Apply transform", UndoIt, Do, applied =>
            {
                if (applied)
                {
                    APUtil.Release(ref oldContent);
                    floating.Release();
                }
                else APUtil.Release(ref merged);
            }, APUtil.EstimateBytes(merged)));
        }

        /// <summary>Drops the floating piece and puts the layer back the way it was before the lift.</summary>
        public void CancelFloating(TextureSet set, PaintLayer floating)
        {
            if (set == null || floating == null || !floating.IsFloating) return;
            var source = floating.liftedFrom;
            var entries = new List<IHistoryEntry>();
            if (floating.liftSnapshot != null && set.layers.Contains(source) && source.content != null)
            {
                var undoSnap = engine.CloneRT(source.content, "Undo");
                APUtil.Copy(floating.liftSnapshot, source.content);
                entries.Add(new PixelEntry("Restore", source.content, undoSnap, () => Changed(set)));
            }
            int index = set.layers.IndexOf(floating);
            set.layers.Remove(floating);
            set.activeLayer = Mathf.Max(0, set.layers.IndexOf(source));
            set.editingMask = false;
            entries.Add(new ActionEntry("Remove floating",
                () => { set.layers.Insert(Mathf.Clamp(index, 0, set.layers.Count), floating); set.activeLayer = set.layers.IndexOf(floating); Changed(set); },
                () => { set.layers.Remove(floating); set.activeLayer = Mathf.Max(0, set.layers.IndexOf(source)); Changed(set); },
                applied => { if (applied && !set.layers.Contains(floating)) floating.Release(); }));
            Push(new CompoundEntry("Cancel transform", entries));
            Changed(set);
        }

        /// <summary>Applies every floating piece (when leaving the Transform tool or saving).</summary>
        public void ApplyAllFloating()
        {
            foreach (var set in sets)
            {
                var f = FloatingLayer(set);
                while (f != null)
                {
                    ApplyFloating(set, f);
                    var next = FloatingLayer(set);
                    if (next == f) break;
                    f = next;
                }
            }
        }
    }
}
