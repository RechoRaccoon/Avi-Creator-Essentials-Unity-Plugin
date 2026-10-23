using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    public enum StrokeTool
    {
        Paint,
        Erase,
    }

    /// <summary>One brush dab in 3D (world space).</summary>
    public struct Dab3D
    {
        public Vector3 position;
        public float radius;
        public Vector3 right, up, forward;   // tip orientation; forward = projection direction (towards the viewer)
        public Vector3 across;               // world direction across the stroke (bristles run along it)
        public float strength;
        public Vector3 camPos;
        public bool occlusion;
        public bool backface;
    }

    /// <summary>One brush dab in texture space.</summary>
    public struct Dab2D
    {
        public Vector2 uv;
        public float radiusPx;
        public float angle;   // radians
        public float strength;
        public Vector2 across; // texel-space direction across the stroke (bristles run along it)
    }

    public enum GradientShape
    {
        Linear,
        Radial,
        Reflected,
    }

    /// <summary>A gradient laid over the model in 3D, from one point to another.</summary>
    public struct GradientParams
    {
        public Vector3 start, end;
        public GradientShape shape;
        public Color colorA;   // gamma, straight alpha
        public Color colorB;
        // Symmetry (the mirror toggle): the gradient is folded across the avatar's mirror plane, so the side the
        // drag did not touch gets the mirror image of the side it did. mirrorMatrix maps a world point to its
        // mirrored counterpart (through avatar space, like the brush's mirrored dab); worldToLocal is the same
        // avatar space, used to tell which side of the plane a point is on. mirrorSide is +1/-1: the side the
        // drag started on, in that space.
        public bool mirror;
        public int mirrorAxis;          // 0 X, 1 Y, 2 Z (avatar space)
        public float mirrorSide;
        public Matrix4x4 mirrorMatrix;  // world -> mirrored world
        public Matrix4x4 worldToLocal;  // world -> avatar space
    }

    public class StrokeTarget
    {
        public TextureSet set;
        public PaintLayer layer;
        public bool mask;
        public RenderTexture target;
        public RenderTexture snapshot;
        public RenderTexture stroke;
        public RenderTexture stamp;
        public RenderTexture paint;         // paint mode and gradients: premultiplied color laid down by this stroke
        public RenderTexture paintStamp;    // paint mode: summed coverage of one batch of dabs
        public RenderTexture mixSample;     // color mixing: colors under the dabs, averaged by its mips
        public RenderTexture mixColor;      // color mixing: the color the brush carries (1x1), and its ping-pong partner
        public RenderTexture mixColorNext;
        public bool touched;
        public bool pending;
    }

    public class StrokeContext
    {
        public string name = "Paint";
        public StrokeTool tool;
        public Color color = Color.white;   // gamma
        public float opacity = 1f;
        public float maskValue = 1f;
        public readonly List<StrokeTarget> targets = new List<StrokeTarget>();
        public readonly List<IHistoryEntry> prefix = new List<IHistoryEntry>();
        public Matrix4x4 viewVP = Matrix4x4.identity;
        public bool hasDepth;
        public bool paintMode;              // set by the viewport: dabs build up as wet paint (paint tool, not on masks)
        public float seed;                  // varies the bristle pattern from stroke to stroke

        public StrokeTarget Find(TextureSet set)
        {
            foreach (var t in targets)
                if (t.set == set) return t;
            return null;
        }
    }

    public struct View2DParams
    {
        public Vector4 uvMap;        // uv = viewportUV * xy + zw
        public bool showAlpha;
        public bool pointSample;
        public bool tile;
        public bool maskOverlay;
        public Color checkerA, checkerB, outside;
    }

    /// <summary>
    /// All GPU work: initialising texture sets, compositing layer stacks, brush strokes in UV space,
    /// fills, image layers, seam padding and the 2D view.
    /// </summary>
    public class PaintEngine : IDisposable
    {
        readonly MeshCache m_Meshes;
        RenderTexture m_Depth;
        Mesh m_TempMesh;

        public int displayPadding = 16;
        public bool highlightSelection = true;
        public float occlusionBias = 0.004f;
        public Action<TextureSet> SetChanged;

        public PaintEngine(MeshCache meshes)
        {
            m_Meshes = meshes;
        }

        static GraphicsFormat ContentFormat => GraphicsFormat.R8G8B8A8_UNorm;
        static GraphicsFormat MaskFormat => GraphicsFormat.R8_UNorm;

        void NotifyChanged(TextureSet set)
        {
            set.dirty = true;
            SetChanged?.Invoke(set);
        }

        // ================================================================== texture set lifecycle

        /// <summary>Creates the base layer from the material's current texture.</summary>
        public void InitializeSet(TextureSet set, bool preferSourceFile)
        {
            if (set.initialized) return;
            Texture source = set.originalTexture;
            Texture2D fileTex = null;
            if (source != null && preferSourceFile)
            {
                string path = AssetDatabase.GetAssetPath(source);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                {
                    fileTex = APUtil.LoadImageFile(APUtil.AssetPathToFull(path), false);
                    if (fileTex != null) source = fileTex;
                }
            }

            if (source != null)
            {
                set.width = Mathf.Clamp(source.width, 16, 8192);
                set.height = Mathf.Clamp(source.height, 16, 8192);
            }

            var baseLayer = new PaintLayer { name = source != null ? "Base (" + set.originalTexture.name + ")" : "Base" };
            baseLayer.content = NewContentRT(set);
            if (source != null)
            {
                var prev = RenderTexture.active;
                // fileTex holds raw gamma values (linear texture); imported textures are sRGB-decoded when sampled.
                if (fileTex == null && IsPackedNormalMap(source))
                {
                    // Imported normal maps sample as packed data (DXT5nm/BC5); unpack to plain RGB normals.
                    var comp = APResources.Composite;
                    comp.SetFloat(APIds.CopyMode, 4f);
                    comp.SetFloat(APIds.SrcToGamma, 0f);
                    Graphics.Blit(source, baseLayer.content, comp, CompPass.Copy);
                    comp.SetFloat(APIds.CopyMode, 0f);
                }
                else APUtil.CopyToGammaData(source, baseLayer.content, !set.linearData);
                RenderTexture.active = prev;
            }
            else
            {
                bool black = set.property.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0;
                bool flatNormal = set.linearData && APSession.IsNormalProperty(set.property);
                APUtil.Clear(baseLayer.content, flatNormal ? new Color(0.5f, 0.5f, 1f, 1f) : black ? new Color(0, 0, 0, 1) : Color.white);
                if (flatNormal) baseLayer.name = "Base (flat normal)";
            }
            if (fileTex != null) Object.DestroyImmediate(fileTex);

            set.layers.Clear();
            set.layers.Add(baseLayer);
            set.activeLayer = 0;
            set.initialized = true;
            set.islandDirty = true;
            EnsureBuffers(set);
            NotifyChanged(set);
        }

        static bool IsPackedNormalMap(Texture t)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) as TextureImporter;
            return importer != null && importer.textureType == TextureImporterType.NormalMap;
        }

        public RenderTexture NewContentRT(TextureSet set, Color? clear = null)
        {
            var rt = APUtil.CreateRT(set.width, set.height, ContentFormat, "Layer");
            APUtil.Clear(rt, clear ?? new Color(0, 0, 0, 0));
            return rt;
        }

        public RenderTexture NewMaskRT(TextureSet set, float value)
        {
            var rt = APUtil.CreateRT(set.width, set.height, MaskFormat, "Mask");
            APUtil.Clear(rt, new Color(value, value, value, 1));
            return rt;
        }

        public RenderTexture CloneRT(RenderTexture src, string name)
        {
            if (src == null) return null;
            var rt = APUtil.CreateRT(src.width, src.height, src.graphicsFormat, name);
            APUtil.Copy(src, rt);
            return rt;
        }

        public void EnsureBuffers(TextureSet set)
        {
            if (set.display == null || set.display.width != set.width || set.display.height != set.height || set.display.graphicsFormat != set.DisplayFormat)
            {
                set.ReleaseBuffers();
                set.display = APUtil.CreateRT(set.width, set.height, set.DisplayFormat, "Display " + set.displayName, true,
                    FilterMode.Trilinear, TextureWrapMode.Repeat);
                set.compA = APUtil.CreateRT(set.width, set.height, ContentFormat, "CompA");
                set.compB = APUtil.CreateRT(set.width, set.height, ContentFormat, "CompB");
                set.island = APUtil.CreateRT(set.width, set.height, MaskFormat, "Island", false, FilterMode.Point);
                set.islandDirty = true;
                set.dirty = true;
            }
        }

        /// <summary>Rasterises every mesh slot using this texture in UV space, then builds the seam padding map.</summary>
        public void BuildIsland(TextureSet set)
        {
            EnsureBuffers(set);
            m_Meshes.EnsurePosed();
            var prev = RenderTexture.active;
            var m = APResources.Paint;
            Graphics.SetRenderTarget(set.island);
            GL.Clear(false, true, Color.black);
            m.SetFloat(APIds.Value, 1f);
            m.SetInt(APIds.UVChannel, set.uvChannel);
            m.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));
            foreach (var u in set.users)
            {
                if (u.renderer < 0 || u.renderer >= m_Meshes.entries.Count) continue;
                var e = m_Meshes.entries[u.renderer];
                if (u.submesh >= e.SubmeshCount) continue;
                m.SetPass(PaintPass.CoveragePass);
                Graphics.DrawMeshNow(e.paintMesh, e.matrix, u.submesh);
            }
            RenderTexture.active = prev;
            BuildSeed(set);
            set.islandDirty = false;
            set.dirty = true;
        }

        void BuildSeed(TextureSet set)
        {
            if (set.seed == null || set.seed.width != set.width || set.seed.height != set.height)
            {
                APUtil.Release(ref set.seed);
                set.seed = APUtil.CreateRT(set.width, set.height, APResources.SeedFormat, "Seed", false, FilterMode.Point);
            }
            var comp = APResources.Composite;
            var tmp = RenderTexture.GetTemporary(set.seed.descriptor);
            tmp.filterMode = FilterMode.Point;
            comp.SetTexture(APIds.ValidTex, set.island);
            Graphics.Blit(set.island, set.seed, comp, CompPass.JfaInit);
            int step = Mathf.NextPowerOfTwo(Mathf.Max(set.width, set.height)) / 2;
            var a = set.seed;
            var b = tmp;
            while (step >= 1)
            {
                comp.SetFloat(APIds.JumpStep, step);
                Graphics.Blit(a, b, comp, CompPass.JfaStep);
                var t = a; a = b; b = t;
                step /= 2;
            }
            comp.SetFloat(APIds.JumpStep, 1);
            Graphics.Blit(a, b, comp, CompPass.JfaStep);
            { var t = a; a = b; b = t; }
            if (a != set.seed) APUtil.Copy(a, set.seed);
            RenderTexture.ReleaseTemporary(tmp);
        }

        public void UpdateBounds(TextureSet set)
        {
            m_Meshes.EnsurePosed();
            bool any = false;
            var bounds = new Bounds();
            foreach (var u in set.users)
            {
                if (u.renderer < 0 || u.renderer >= m_Meshes.entries.Count) continue;
                var e = m_Meshes.entries[u.renderer];
                if (!any) { bounds = e.worldBounds; any = true; }
                else bounds.Encapsulate(e.worldBounds);
            }
            set.worldBounds = bounds;
        }

        // ================================================================== compositing

        public void Composite(TextureSet set, bool force = false)
        {
            if (!set.initialized || (!set.dirty && !force)) return;
            EnsureBuffers(set);
            if (set.islandDirty) BuildIsland(set);
            var prev = RenderTexture.active;
            var result = CompositeStack(set, displayPadding);
            set.lastComposite = result;

            var comp = APResources.Composite;
            var active = set.ActiveLayer;
            bool showMask = set.viewMode == 1 && active != null && active.HasMask;
            comp.SetFloat(APIds.ViewMode, showMask ? 1f : 0f);
            comp.SetTexture(APIds.MaskTex, showMask ? (Texture)active.mask : APResources.White);
            comp.SetFloat(APIds.ToLinear, GraphicsFormatUtility.IsSRGBFormat(set.display.graphicsFormat) ? 1f : 0f);
            bool sel = highlightSelection && set.HasSelection;
            if (sel && set.selectionDirty) BuildSelection(set);
            comp.SetFloat(APIds.SelOverlay, sel && set.selectionRT != null ? 1f : 0f);
            comp.SetTexture(APIds.SelTex, sel && set.selectionRT != null ? (Texture)set.selectionRT : APResources.White);
            Graphics.Blit(result, set.display, comp, CompPass.Output);
            comp.SetFloat(APIds.SelOverlay, 0f);
            RenderTexture.active = prev;
            set.dirty = false;
        }

        /// <summary>
        /// The layer a clipped adjustment layer works on: the nearest layer below that is not itself a clipped
        /// adjustment. -1 when there is none (it then adjusts everything below, like an unclipped one).
        /// </summary>
        public static int ClipBase(IList<PaintLayer> layers, int index)
        {
            if (index < 0 || index >= layers.Count) return -1;
            var layer = layers[index];
            if (layer.kind != LayerKind.Adjustment || !layer.clipBelow) return -1;
            for (int i = index - 1; i >= 0; i--)
            {
                var l = layers[i];
                if (l.kind != LayerKind.Adjustment) return i;
                if (!l.clipBelow) return -1;
            }
            return -1;
        }

        void SetupAdjust(Material comp, PaintLayer layer)
        {
            comp.SetFloat(APIds.HasMask, layer.MaskActive ? 1f : 0f);
            comp.SetTexture(APIds.MaskTex, layer.MaskActive ? (Texture)layer.mask : APResources.White);
            comp.SetFloat(APIds.Opacity, layer.opacity);
            comp.SetFloat(APIds.AdjMode, (float)layer.adjMode);
            comp.SetVector(APIds.AdjColor, APUtil.V(layer.adjColor));
            comp.SetVector(APIds.AdjHSL, new Vector4(layer.hue / 360f, layer.saturation, layer.lightness, 0f));
            comp.SetVector(APIds.SrcMean, layer.srcMean);
            comp.SetVector(APIds.SrcStd, layer.srcStd);
            comp.SetVector(APIds.RefMean, layer.refMean);
            comp.SetVector(APIds.RefStd, layer.refStd);
            comp.SetFloat(APIds.RefValid, layer.refValid ? 1f : 0f);
            var lut = layer.adjMode == AdjustMode.Match ? MatchLut(layer) : null;
            comp.SetFloat(APIds.UseLut, lut != null ? 1f : 0f);
            comp.SetTexture(APIds.LutTex, lut != null ? lut : (Texture)APResources.White);
        }

        /// <summary>Composites the stack into compA/compB and returns the one holding the result.</summary>
        RenderTexture CompositeStack(TextureSet set, float padding)
        {
            var comp = APResources.Composite;
            var a = set.compA;
            var b = set.compB;
            APUtil.Clear(a, new Color(0, 0, 0, 0));
            var layers = set.layers;
            var clipped = new List<PaintLayer>();
            for (int li = 0; li < layers.Count; li++)
            {
                var layer = layers[li];
                if (layer.kind == LayerKind.Adjustment)
                {
                    // Clipped adjustments are applied together with their layer below (even when that one is hidden).
                    if (ClipBase(layers, li) >= 0) continue;
                    if (!layer.visible || layer.opacity <= 0f) continue;
                    SetupAdjust(comp, layer);
                    Graphics.Blit(a, b, comp, CompPass.Adjust);
                    var ta = a; a = b; b = ta;
                    continue;
                }
                if (!layer.visible || layer.opacity <= 0f) continue;
                bool isFill = layer.kind == LayerKind.Fill;
                if (!isFill && layer.content == null) continue;

                clipped.Clear();
                for (int j = li + 1; j < layers.Count && layers[j].kind == LayerKind.Adjustment && layers[j].clipBelow; j++)
                    if (layers[j].visible && layers[j].opacity > 0f) clipped.Add(layers[j]);

                Texture source = isFill ? (Texture)APResources.White : layer.content;
                RenderTexture tmpA = null, tmpB = null;
                if (clipped.Count > 0)
                {
                    // Adjust a copy of just this layer, then blend the result as the layer itself.
                    tmpA = RenderTexture.GetTemporary(a.descriptor);
                    tmpB = RenderTexture.GetTemporary(a.descriptor);
                    if (isFill) APUtil.Clear(tmpA, layer.fillColor);
                    else
                    {
                        comp.SetFloat(APIds.CopyMode, 0f);
                        comp.SetFloat(APIds.SrcToGamma, 0f);
                        Graphics.Blit(layer.content, tmpA, comp, CompPass.Copy);
                    }
                    foreach (var adj in clipped)
                    {
                        SetupAdjust(comp, adj);
                        Graphics.Blit(tmpA, tmpB, comp, CompPass.Adjust);
                        var tt = tmpA; tmpA = tmpB; tmpB = tt;
                    }
                    source = tmpA;
                    isFill = false;
                }
                comp.SetFloat(APIds.IsFill, isFill ? 1f : 0f);
                comp.SetVector(APIds.FillColor, APUtil.V(layer.fillColor));
                comp.SetTexture(APIds.LayerTex, source);
                comp.SetFloat(APIds.HasMask, layer.MaskActive ? 1f : 0f);
                comp.SetTexture(APIds.MaskTex, layer.MaskActive ? (Texture)layer.mask : APResources.White);
                comp.SetFloat(APIds.Opacity, layer.opacity);
                comp.SetInt(APIds.BlendMode, (int)layer.blend);
                Graphics.Blit(a, b, comp, CompPass.Layer);
                var t = a; a = b; b = t;
                if (tmpA != null) RenderTexture.ReleaseTemporary(tmpA);
                if (tmpB != null) RenderTexture.ReleaseTemporary(tmpB);
            }
            if (padding > 0f && set.seed != null)
            {
                comp.SetTexture(APIds.ValidTex, set.island);
                comp.SetTexture(APIds.SeedTex, set.seed);
                comp.SetFloat(APIds.MaxPad, padding);
                Graphics.Blit(a, b, comp, CompPass.Remap);
                var t = a; a = b; b = t;
            }
            return a;
        }

        /// <summary>Composites only the given layers (bottom to top) into dst, without padding.</summary>
        public void CompositeSubset(TextureSet set, IList<PaintLayer> layers, RenderTexture dst)
        {
            EnsureBuffers(set);
            var saved = new List<PaintLayer>(set.layers);
            set.layers.Clear();
            set.layers.AddRange(layers);
            var prev = RenderTexture.active;
            var result = CompositeStack(set, 0f);
            APUtil.Copy(result, dst);
            RenderTexture.active = prev;
            set.layers.Clear();
            set.layers.AddRange(saved);
            set.dirty = true;
        }

        /// <summary>
        /// Average color and spread (per channel, gamma space) of the layers below `layerIndex`, inside a region mask.
        /// Returns false when the region is empty.
        /// </summary>
        public bool MeasureRegion(TextureSet set, int layerIndex, Texture region, out Vector4 mean, out Vector4 std) =>
            MeasureRegion(set, layerIndex, region, out mean, out std, out _);

        /// <summary>
        /// Average, spread and luma/chroma histograms (Y, Cb, Cr; 256 bins each, normalised) of the colors under
        /// the region, as composited by the layers below layerIndex.
        /// </summary>
        public bool MeasureRegion(TextureSet set, int layerIndex, Texture region, out Vector4 mean, out Vector4 std, out float[] hist,
            bool whatItAdjusts = false)
        {
            mean = std = Vector4.zero;
            hist = null;
            if (region == null) return false;
            // whatItAdjusts: for a clipped adjustment layer, only its layer below (with the clipped adjustments under it).
            int from = whatItAdjusts ? Mathf.Max(0, ClipBase(set.layers, layerIndex)) : 0;
            var below = new List<PaintLayer>();
            for (int i = from; i < layerIndex && i < set.layers.Count; i++) below.Add(set.layers[i]);
            var colorRT = NewContentRT(set);
            CompositeSubset(set, below, colorRT);
            var maskRT = APUtil.CreateRT(set.width, set.height, GraphicsFormat.R8G8B8A8_UNorm, "region");
            var comp = APResources.Composite;
            comp.SetFloat(APIds.CopyMode, 1f);
            comp.SetFloat(APIds.SrcToGamma, 0f);
            var prev = RenderTexture.active;
            Graphics.Blit(region, maskRT, comp, CompPass.Copy);
            RenderTexture.active = prev;
            var ct = APUtil.ReadToTexture(colorRT);
            var mt = APUtil.ReadToTexture(maskRT);
            var cp = ct.GetPixels32();
            var mp = mt.GetPixels32();
            Object.DestroyImmediate(ct);
            Object.DestroyImmediate(mt);
            APUtil.Release(ref colorRT);
            APUtil.Release(ref maskRT);
            double w = 0, r = 0, g = 0, b = 0, rr = 0, gg = 0, bb = 0;
            var h = new double[768];
            for (int i = 0; i < cp.Length && i < mp.Length; i++)
            {
                float k = mp[i].r / 255f;
                if (k < 0.5f || cp[i].a < 128) continue;
                double cr = cp[i].r / 255.0, cg = cp[i].g / 255.0, cb = cp[i].b / 255.0;
                ToYCC(cr, cg, cb, out double y, out double u, out double v);
                h[Bin(y)] += 1;
                h[256 + Bin(u)] += 1;
                h[512 + Bin(v)] += 1;
                w += 1;
                r += cr; g += cg; b += cb;
                rr += cr * cr; gg += cg * cg; bb += cb * cb;
            }
            if (w < 1) return false;
            hist = new float[768];
            for (int i = 0; i < 768; i++) hist[i] = (float)(h[i] / w);
            r /= w; g /= w; b /= w;
            mean = new Vector4((float)r, (float)g, (float)b, 1f);
            std = new Vector4(
                (float)Math.Sqrt(Math.Max(0, rr / w - r * r)),
                (float)Math.Sqrt(Math.Max(0, gg / w - g * g)),
                (float)Math.Sqrt(Math.Max(0, bb / w - b * b)), 1f);
            return true;
        }

        /// <summary>Final texture for export, padded by the given number of texels (negative = fill everything).</summary>
        static int Bin(double x) => Math.Min(255, Math.Max(0, (int)(x * 255.0 + 0.5)));

        /// <summary>BT.601 luma/chroma of gamma RGB, with chroma moved to 0..1. Must match AP_ToYCC in AP_Composite.</summary>
        static void ToYCC(double r, double g, double b, out double y, out double u, out double v)
        {
            y = 0.299 * r + 0.587 * g + 0.114 * b;
            u = (b - y) * 0.564 + 0.5;
            v = (r - y) * 0.713 + 0.5;
        }

        /// <summary>
        /// Histogram matching table: for every luma and chroma value of the source area, the value at the same
        /// percentile in the reference area. Applying it makes the area's whole color distribution (dark rim,
        /// mid tones, highlights) match the reference, not just its average.
        /// </summary>
        public static Texture2D BuildMatchLut(float[] src, float[] reference)
        {
            if (src == null || reference == null || src.Length != 768 || reference.Length != 768) return null;
            var lut = new Color[256];
            var outCh = new float[3][];
            for (int ch = 0; ch < 3; ch++)
            {
                var s = Smooth(src, ch * 256);
                var r = Smooth(reference, ch * 256);
                var sc = Cdf(s);
                var rc = Cdf(r);
                var o = new float[256];
                for (int i = 0; i < 256; i++)
                {
                    // Percentile of this source value (centre of its bin), then the reference value there.
                    float p = (i == 0 ? sc[0] : sc[i - 1] + sc[i]) * 0.5f;
                    if (i == 0) p = sc[0] * 0.5f;
                    o[i] = InverseCdf(rc, p);
                }
                // Keep the mapping increasing so detail never inverts.
                for (int i = 1; i < 256; i++) o[i] = Mathf.Max(o[i], o[i - 1]);
                outCh[ch] = o;
            }
            for (int i = 0; i < 256; i++) lut[i] = new Color(outCh[0][i], outCh[1][i], outCh[2][i], 1f);
            var tex = new Texture2D(256, 1, TextureFormat.RGBAHalf, false, true)
            {
                name = "AP Match LUT",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels(lut);
            tex.Apply(false);
            return tex;
        }

        static float[] Smooth(float[] h, int offset)
        {
            var o = new float[256];
            for (int i = 0; i < 256; i++)
            {
                float a = 0, wsum = 0;
                for (int d = -2; d <= 2; d++)
                {
                    int j = i + d;
                    if (j < 0 || j > 255) continue;
                    float wt = 3 - Mathf.Abs(d);
                    a += h[offset + j] * wt;
                    wsum += wt;
                }
                o[i] = a / wsum;
            }
            return o;
        }

        static float[] Cdf(float[] h)
        {
            var c = new float[256];
            float sum = 0;
            for (int i = 0; i < 256; i++) sum += h[i];
            float acc = 0;
            for (int i = 0; i < 256; i++)
            {
                acc += h[i];
                c[i] = sum > 0 ? acc / sum : (i + 1) / 256f;
            }
            return c;
        }

        /// <summary>Value (0..1) at which the cumulative distribution reaches p, interpolated within the bin.</summary>
        static float InverseCdf(float[] c, float p)
        {
            for (int i = 0; i < 256; i++)
            {
                if (c[i] < p) continue;
                float lo = i == 0 ? 0f : c[i - 1];
                float t = c[i] - lo > 1e-7f ? (p - lo) / (c[i] - lo) : 0.5f;
                return Mathf.Clamp01((i - 0.5f + t) / 255f);
            }
            return 1f;
        }

        /// <summary>The match table for a layer, rebuilt when its histograms change.</summary>
        public static Texture MatchLut(PaintLayer layer)
        {
            if (layer.matchLut == null && layer.refValid && layer.srcHist != null && layer.refHist != null)
                layer.matchLut = BuildMatchLut(layer.srcHist, layer.refHist);
            return layer.matchLut;
        }

        public RenderTexture CompositeForExport(TextureSet set, int padding)
        {
            EnsureBuffers(set);
            if (set.islandDirty) BuildIsland(set);
            var prev = RenderTexture.active;
            var result = CompositeStack(set, padding < 0 ? 1e7f : padding);
            var outRT = APUtil.CreateRT(set.width, set.height, ContentFormat, "Export");
            APUtil.Copy(result, outRT);
            RenderTexture.active = prev;
            set.dirty = true;
            return outRT;
        }

        public Color SampleComposite(TextureSet set, Vector2 uv)
        {
            if (set == null || !set.initialized) return Color.white;
            Composite(set);
            if (set.lastComposite == null) return Color.white;
            uv.x -= Mathf.Floor(uv.x);
            uv.y -= Mathf.Floor(uv.y);
            return APUtil.ReadPixel(set.lastComposite, uv);
        }

        // ================================================================== depth for occlusion

        public void RenderDepth(Matrix4x4 view, Matrix4x4 proj, int width, int height)
        {
            width = Mathf.Max(16, width);
            height = Mathf.Max(16, height);
            if (m_Depth == null || m_Depth.width != width || m_Depth.height != height)
            {
                APUtil.Release(ref m_Depth);
                m_Depth = APUtil.CreateRT(width, height, GraphicsFormat.R32_SFloat, "Depth", false, FilterMode.Point,
                    TextureWrapMode.Clamp, 24);
            }
            m_Meshes.EnsurePosed();
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(m_Depth);
            GL.Clear(true, true, new Color(1e6f, 1e6f, 1e6f, 1f));
            var m = APResources.Paint;
            m.SetMatrix(APIds.DepthVP, GL.GetGPUProjectionMatrix(proj, true) * view);
            m.SetMatrix(APIds.DepthV, view);
            foreach (var e in m_Meshes.entries)
            {
                if (!e.Visible) continue;
                for (int s = 0; s < e.SubmeshCount; s++)
                {
                    m.SetPass(PaintPass.Depth);
                    Graphics.DrawMeshNow(e.paintMesh, e.matrix, s);
                }
            }
            RenderTexture.active = prev;
        }

        // ================================================================== strokes

        public StrokeContext BeginStroke(IEnumerable<TextureSet> sets, StrokeTool tool, Color color, float opacity,
            string name, Matrix4x4 viewVP, bool hasDepth)
        {
            var ctx = new StrokeContext
            {
                tool = tool,
                color = color,
                opacity = opacity,
                name = name,
                viewVP = viewVP,
                hasDepth = hasDepth,
                maskValue = tool == StrokeTool.Erase ? 0f : APUtil.Luminance(color),
                seed = UnityEngine.Random.value * 997f,
            };
            foreach (var set in sets) AddTarget(ctx, set);
            return ctx;
        }

        public StrokeTarget AddTarget(StrokeContext ctx, TextureSet set)
        {
            var existing = ctx.Find(set);
            if (existing != null) return existing;
            if (!set.initialized) InitializeSet(set, true);
            var layer = set.ActiveLayer;
            if (layer == null) return null;
            bool mask = set.editingMask && layer.HasMask;

            if ((layer.kind == LayerKind.Fill || layer.kind == LayerKind.Adjustment) && !mask)
            {
                // Painting on a fill layer paints its mask: start hidden, reveal where painted.
                if (!layer.HasMask)
                {
                    var newMask = NewMaskRT(set, ctx.tool == StrokeTool.Erase ? 1f : 0f);
                    layer.mask = newMask;
                    layer.maskEnabled = true;
                    var capturedSet = set;
                    ctx.prefix.Add(new ActionEntry("Add mask",
                        () => { layer.mask = null; capturedSet.editingMask = false; NotifyChanged(capturedSet); },
                        () => { layer.mask = newMask; capturedSet.editingMask = true; NotifyChanged(capturedSet); },
                        applied => { if (!applied && layer.mask != newMask) { var r = newMask; APUtil.Release(ref r); } }));
                }
                set.editingMask = true;
                mask = true;
                if (ctx.tool == StrokeTool.Paint) ctx.maskValue = 1f;
            }
            else if (layer.kind == LayerKind.Image && !mask)
            {
                var placement = layer.placement;
                layer.kind = LayerKind.Paint;
                layer.placement = null;
                ctx.prefix.Add(new ActionEntry("Rasterize image",
                    () => { layer.kind = LayerKind.Image; layer.placement = placement; NotifyChanged(set); },
                    () => { layer.kind = LayerKind.Paint; layer.placement = null; NotifyChanged(set); },
                    applied =>
                    {
                        if (applied && placement != null && placement.image != null) Object.DestroyImmediate(placement.image);
                    }));
            }

            var target = mask ? layer.mask : layer.content;
            if (target == null)
            {
                layer.content = NewContentRT(set);
                target = layer.content;
            }

            var t = new StrokeTarget
            {
                set = set,
                layer = layer,
                mask = mask,
                target = target,
                snapshot = CloneRT(target, "Undo"),
            };
            var strokeDesc = new RenderTextureDescriptor(set.width, set.height, APResources.MaskStrokeFormat, 0);
            t.stroke = RenderTexture.GetTemporary(strokeDesc);
            t.stamp = RenderTexture.GetTemporary(strokeDesc);
            t.stroke.filterMode = FilterMode.Bilinear;
            t.stamp.filterMode = FilterMode.Bilinear;
            APUtil.Clear(t.stroke, Color.black);
            ctx.targets.Add(t);
            return t;
        }

        static bool SphereTouches(Bounds b, Vector3 p, float r)
        {
            b.Expand(r * 2f);
            return b.Contains(p);
        }

        void SetupTip(Material m, BrushTip tip, BrushSettings bs)
        {
            bool procedural = tip == null || tip.procedural;
            m.SetFloat(APIds.UseTip, procedural ? 0f : 1f);
            m.SetTexture(APIds.TipTex, procedural || tip.texture == null ? (Texture)APResources.White : tip.texture);
            m.SetFloat(APIds.Hardness, bs.hardness);
        }

        /// <summary>Bristle streaks and canvas grain, shared by the 3D and 2D dabs.</summary>
        void SetupPaintTexture(Material m, StrokeContext ctx, BrushSettings bs)
        {
            m.SetFloat(APIds.Bristles, Mathf.Clamp01(bs.bristles));
            m.SetFloat(APIds.BristleScale, Mathf.Lerp(3f, 40f, Mathf.Clamp01(bs.bristleDensity)));
            m.SetFloat(APIds.BristleSeed, ctx.seed);
            m.SetFloat(APIds.Grain, Mathf.Clamp01(bs.grain));
        }

        /// <summary>Paint mode applies to painting (not erasing) on layer content, not on masks.</summary>
        static bool UsesPaint(StrokeContext ctx, StrokeTarget t) => ctx.paintMode && ctx.tool == StrokeTool.Paint && !t.mask;

        /// <summary>
        /// Paint mode coverage per dab, scaled by spacing so a stroke at full flow covers about the same however
        /// tightly the dabs are packed (about 1 / spacing dabs overlap each texel along the stroke).
        /// </summary>
        static float PaintGain(BrushSettings bs) => Mathf.Clamp(bs.spacing, 0.02f, 1f) * 3f;

        void EnsurePaintBuffers(StrokeTarget t, bool stamp)
        {
            if (t.paint == null)
            {
                var desc = new RenderTextureDescriptor(t.set.width, t.set.height, APResources.PaintStrokeFormat, 0);
                t.paint = RenderTexture.GetTemporary(desc);
                t.paint.filterMode = FilterMode.Bilinear;
                APUtil.Clear(t.paint, new Color(0, 0, 0, 0));
            }
            if (stamp && t.paintStamp == null)
            {
                var desc = new RenderTextureDescriptor(t.set.width, t.set.height, APResources.PaintStampFormat, 0);
                t.paintStamp = RenderTexture.GetTemporary(desc);
                t.paintStamp.filterMode = FilterMode.Bilinear;
            }
        }

        public void Dabs3D(StrokeContext ctx, List<Dab3D> dabs, BrushTip tip, BrushSettings bs)
        {
            if (dabs.Count == 0) return;
            m_Meshes.EnsurePosed();
            var prev = RenderTexture.active;
            var m = APResources.Paint;
            var comp = APResources.Composite;
            SetupTip(m, tip, bs);
            SetupPaintTexture(m, ctx, bs);
            m.SetFloat(APIds.GrainScale, Mathf.Max(1e-4f, bs.radius * 0.12f));
            m.SetFloat(APIds.PaintGain, PaintGain(bs));
            m.SetFloat(APIds.ProjMode, bs.projection == BrushProjection.Projected ? 1f : 0f);
            m.SetFloat(APIds.DepthRange, Mathf.Max(0.05f, bs.depthRange));
            m.SetFloat(APIds.BackfaceCos, Mathf.Cos(bs.backfaceAngle * Mathf.Deg2Rad));
            m.SetMatrix(APIds.ViewVP, ctx.viewVP);
            m.SetTexture(APIds.DepthTex, m_Depth != null ? (Texture)m_Depth : APResources.White);
            m.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));

            foreach (var t in ctx.targets)
            {
                bool paint = UsesPaint(ctx, t);
                if (paint) EnsurePaintBuffers(t, true);
                m.SetFloat(APIds.PaintAccum, paint ? 1f : 0f);
                bool any = false;
                int count = 0;
                foreach (var dab in dabs)
                {
                    if (!SphereTouches(t.set.worldBounds, dab.position, dab.radius * Mathf.Max(1f, bs.depthRange))) continue;
                    if (!any)
                    {
                        Graphics.SetRenderTarget(paint ? t.paintStamp : t.stamp);
                        GL.Clear(false, true, Color.black);
                        any = true;
                    }
                    count++;
                    m.SetVector(APIds.BrushPos, dab.position);
                    m.SetFloat(APIds.BrushRadius, dab.radius);
                    m.SetVector(APIds.BrushRight, dab.right);
                    m.SetVector(APIds.BrushUp, dab.up);
                    m.SetVector(APIds.BrushFwd, dab.forward);
                    m.SetVector(APIds.BrushAcross, dab.across);
                    m.SetFloat(APIds.Strength, dab.strength);
                    m.SetVector(APIds.CamPos, dab.camPos);
                    m.SetFloat(APIds.UseOcclusion, dab.occlusion && ctx.hasDepth && m_Depth != null ? 1f : 0f);
                    m.SetFloat(APIds.OcclusionBias, occlusionBias + dab.radius * 0.15f);
                    m.SetFloat(APIds.UseBackface, dab.backface ? 1f : 0f);
                    m.SetInt(APIds.UVChannel, t.set.uvChannel);
                    // Paint mode sums dabs, so it rasterises exactly (each texel once per triangle pair) and then only
                    // tops up the texels a conservative pass would add, without counting shared edges twice.
                    int passes = paint ? (PaintPass.UseConservative ? 2 : 1) : 1;
                    for (int pi = 0; pi < passes; pi++)
                    {
                        m.SetPass(paint ? (pi == 0 ? PaintPass.Stamp3DPaint : PaintPass.Stamp3DPaintConservative) : PaintPass.Stamp3DPass);
                        foreach (var u in t.set.users)
                        {
                            if (u.renderer < 0 || u.renderer >= m_Meshes.entries.Count) continue;
                            var e = m_Meshes.entries[u.renderer];
                            if (!e.Visible || u.submesh >= e.SubmeshCount) continue;
                            Graphics.DrawMeshNow(e.paintMesh, e.matrix, u.submesh);
                        }
                    }
                }
                if (any)
                {
                    if (paint) FinishPaintBatch(ctx, t, bs, count);
                    else Graphics.Blit(t.stamp, t.stroke, comp, CompPass.Accumulate);
                    t.pending = true;
                    t.touched = true;
                }
            }
            m.SetFloat(APIds.PaintAccum, 0f);
            RenderTexture.active = prev;
        }

        /// <summary>
        /// Lays a batch of paint-mode dabs over the stroke. With color mixing, the brush first picks up some of the
        /// color under the dabs (averaged on the GPU through a mip chain, so nothing is read back).
        /// </summary>
        void FinishPaintBatch(StrokeContext ctx, StrokeTarget t, BrushSettings bs, int dabCount)
        {
            var comp = APResources.Composite;
            float mix = Mathf.Clamp01(bs.colorMixing);
            bool mixing = mix > 0.001f && t.set.lastComposite != null;
            if (mixing)
            {
                if (t.mixSample == null)
                {
                    int w = Mathf.Max(1, Mathf.Min(512, t.set.width));
                    int h = Mathf.Max(1, Mathf.Min(512, t.set.height));
                    var desc = new RenderTextureDescriptor(w, h, APResources.MixFormat, 0)
                    {
                        useMipMap = true,
                        autoGenerateMips = true,
                    };
                    t.mixSample = RenderTexture.GetTemporary(desc);
                    t.mixSample.filterMode = FilterMode.Trilinear;
                    var one = new RenderTextureDescriptor(1, 1, APResources.MixFormat, 0);
                    t.mixColor = RenderTexture.GetTemporary(one);
                    t.mixColorNext = RenderTexture.GetTemporary(one);
                    t.mixColor.filterMode = t.mixColorNext.filterMode = FilterMode.Point;
                    APUtil.Clear(t.mixColor, new Color(ctx.color.r, ctx.color.g, ctx.color.b, 1f));
                }
                comp.SetTexture(APIds.LayerTex, t.set.lastComposite);
                Graphics.Blit(t.paintStamp, t.mixSample, comp, CompPass.MixSample);
                // mixSample's mips are regenerated as soon as it stops being the render target.
                int lastMip = Mathf.FloorToInt(Mathf.Log(Mathf.Max(t.mixSample.width, t.mixSample.height), 2f));
                comp.SetTexture(APIds.LayerTex, t.mixSample);
                comp.SetFloat(APIds.MixLod, lastMip);
                comp.SetFloat(APIds.MixPick, 1f - Mathf.Pow(1f - mix * 0.1f, dabCount));
                comp.SetFloat(APIds.MixReload, 1f - Mathf.Pow(1f - (1f - mix) * 0.1f, dabCount));
                comp.SetVector(APIds.BrushColor, APUtil.V(ctx.color));
                Graphics.Blit(t.mixColor, t.mixColorNext, comp, CompPass.MixUpdate);
                var tmp = t.mixColor;
                t.mixColor = t.mixColorNext;
                t.mixColorNext = tmp;
            }
            comp.SetFloat(APIds.UseRes, mixing ? 1f : 0f);
            comp.SetTexture(APIds.ResTex, mixing ? (Texture)t.mixColor : APResources.White);
            comp.SetVector(APIds.BrushColor, APUtil.V(ctx.color));
            comp.SetFloat(APIds.ColorVar, Mathf.Clamp01(bs.colorVariation));
            Graphics.Blit(t.paintStamp, t.paint, comp, CompPass.PaintAccum);
            comp.SetFloat(APIds.UseRes, 0f);
        }

        public void Dabs2D(StrokeContext ctx, TextureSet set, List<Dab2D> dabs, BrushTip tip, BrushSettings bs)
        {
            var t = ctx.Find(set);
            if (t == null || dabs.Count == 0) return;
            var verts = new List<Vector3>();
            var locals = new List<Vector2>();
            var extras = new List<Vector2>();
            var tris = new List<int>();
            foreach (var d in dabs)
            {
                AddDabQuad(verts, locals, extras, tris, d.uv, d.radiusPx, d.angle, d.across, set.width, set.height, false, d.strength);
                if (bs.mirrorU2D)
                    AddDabQuad(verts, locals, extras, tris, new Vector2(1f - d.uv.x, d.uv.y), d.radiusPx, -d.angle,
                        new Vector2(-d.across.x, d.across.y), set.width, set.height, true, d.strength);
            }
            var mesh = TempMesh();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, locals);
            mesh.SetUVs(1, extras);
            mesh.SetTriangles(tris, 0, false);

            bool paint = UsesPaint(ctx, t);
            if (paint) EnsurePaintBuffers(t, true);
            var prev = RenderTexture.active;
            var m = APResources.Paint;
            SetupTip(m, tip, bs);
            SetupPaintTexture(m, ctx, bs);
            m.SetFloat(APIds.GrainScale, Mathf.Max(0.75f, bs.radius2D * set.width * 0.12f));
            m.SetVector(APIds.GrainTexSize, new Vector4(set.width, set.height, 0, 0));
            m.SetFloat(APIds.PaintAccum, paint ? 1f : 0f);
            m.SetFloat(APIds.PaintGain, PaintGain(bs));
            m.SetFloat(APIds.Strength, 1f);
            m.SetFloat(APIds.ClipIslands, bs.clipToIslands2D ? 1f : 0f);
            m.SetTexture(APIds.IslandTex, set.island != null ? (Texture)set.island : APResources.White);
            m.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));
            Graphics.SetRenderTarget(paint ? t.paintStamp : t.stamp);
            GL.Clear(false, true, Color.black);
            m.SetPass(paint ? PaintPass.Stamp2DPaint : PaintPass.Stamp2D);
            Graphics.DrawMeshNow(mesh, Matrix4x4.identity, 0);
            if (paint) FinishPaintBatch(ctx, t, bs, dabs.Count);
            else Graphics.Blit(t.stamp, t.stroke, APResources.Composite, CompPass.Accumulate);
            m.SetFloat(APIds.PaintAccum, 0f);
            t.pending = true;
            t.touched = true;
            RenderTexture.active = prev;
        }

        /// <summary>A dab quad whose second UV holds each corner's position across the stroke, in brush radii.</summary>
        static void AddDabQuad(List<Vector3> verts, List<Vector2> locals, List<Vector2> extras, List<int> tris, Vector2 uv,
            float r, float angle, Vector2 across, int width, int height, bool mirror, float strength)
        {
            int start = verts.Count;
            AddQuad(verts, locals, tris, uv, r, r, angle, width, height, mirror, strength);
            var cpx = new Vector2(uv.x * width, uv.y * height);
            float inv = 1f / Mathf.Max(r, 1e-4f);
            for (int i = start; i < verts.Count; i++)
            {
                var offset = new Vector2(verts[i].x * width, verts[i].y * height) - cpx;
                extras.Add(new Vector2(Vector2.Dot(offset, across) * inv, 0f));
            }
        }

        static void AddQuad(List<Vector3> verts, List<Vector2> locals, List<int> tris, Vector2 uv, float rx, float ry,
            float angle, int width, int height, bool mirror = false, float strength = 1f)
        {
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            var cpx = new Vector2(uv.x * width, uv.y * height);
            int baseIndex = verts.Count;
            var corners = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            foreach (var l in corners)
            {
                var o = new Vector2(l.x * rx, l.y * ry);
                var r = new Vector2(o.x * c - o.y * s, o.x * s + o.y * c);
                var p = cpx + r;
                verts.Add(new Vector3(p.x / width, p.y / height, strength));
                locals.Add(mirror ? new Vector2(-l.x, l.y) : l);
            }
            tris.Add(baseIndex); tris.Add(baseIndex + 1); tris.Add(baseIndex + 2);
            tris.Add(baseIndex); tris.Add(baseIndex + 2); tris.Add(baseIndex + 3);
        }

        /// <summary>The image quad as a fine grid bent by the placement's warp; each vertex keeps its unwarped image coordinate.</summary>
        static void AddWarpedQuad(List<Vector3> verts, List<Vector2> locals, List<int> tris, ImagePlacement p, float rx, float ry, int width, int height)
        {
            const int n = 24;
            float angle = p.rotation * Mathf.Deg2Rad;
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            var cpx = new Vector2(p.uvCenter.x * width, p.uvCenter.y * height);
            int baseIndex = verts.Count;
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                var l = new Vector2(i / (float)n * 2f - 1f, j / (float)n * 2f - 1f);
                var w = p.Warp(l);
                var o = new Vector2(w.x * rx, w.y * ry);
                var pos = cpx + new Vector2(o.x * c - o.y * s, o.x * s + o.y * c);
                verts.Add(new Vector3(pos.x / width, pos.y / height, 1f));
                locals.Add(l);
            }
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = baseIndex + j * (n + 1) + i;
                int b = a + 1, d = a + n + 1, e = d + 1;
                tris.Add(a); tris.Add(b); tris.Add(e);
                tris.Add(a); tris.Add(e); tris.Add(d);
            }
        }

        Mesh TempMesh()
        {
            if (m_TempMesh == null)
            {
                m_TempMesh = new Mesh { name = "AP Temp", hideFlags = HideFlags.HideAndDontSave };
                m_TempMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                m_TempMesh.MarkDynamic();
            }
            m_TempMesh.Clear();
            return m_TempMesh;
        }

        /// <summary>Writes pending dabs into the layers. Cheap enough to call every frame while painting.</summary>
        public void Apply(StrokeContext ctx)
        {
            var prev = RenderTexture.active;
            var comp = APResources.Composite;
            foreach (var t in ctx.targets)
            {
                if (!t.pending) continue;
                comp.SetTexture(APIds.StrokeTex, t.paint != null ? t.paint : t.stroke);
                comp.SetVector(APIds.BrushColor, APUtil.V(ctx.color));
                comp.SetFloat(APIds.Opacity, ctx.opacity);
                comp.SetFloat(APIds.LockAlpha, !t.mask && t.layer.lockAlpha ? 1f : 0f);
                comp.SetFloat(APIds.MaskValue, ctx.maskValue);
                float mode = t.paint != null ? (t.mask ? 4f : 3f) : t.mask ? 2f : (ctx.tool == StrokeTool.Erase ? 1f : 0f);
                comp.SetFloat(APIds.ApplyMode, mode);
                bool useSel = t.set.HasSelection;
                if (useSel && (t.set.selectionDirty || t.set.selectionRT == null)) BuildSelection(t.set);
                comp.SetFloat(APIds.UseSel, useSel && t.set.selectionRT != null ? 1f : 0f);
                comp.SetTexture(APIds.SelTex, useSel && t.set.selectionRT != null ? (Texture)t.set.selectionRT : APResources.White);
                Graphics.Blit(t.snapshot, t.target, comp, CompPass.Apply);
                comp.SetFloat(APIds.UseSel, 0f);
                t.pending = false;
                NotifyChanged(t.set);
            }
            RenderTexture.active = prev;
        }

        public IHistoryEntry EndStroke(StrokeContext ctx)
        {
            Apply(ctx);
            var entries = new List<IHistoryEntry>(ctx.prefix);
            foreach (var t in ctx.targets)
            {
                ReleaseStrokeBuffers(t);
                if (t.touched)
                {
                    var set = t.set;
                    entries.Add(new PixelEntry(ctx.name, t.target, t.snapshot, () => NotifyChanged(set)));
                }
                else
                {
                    APUtil.Release(ref t.snapshot);
                }
            }
            if (entries.Count == 0) return null;
            if (entries.Count == 1) return entries[0];
            return new CompoundEntry(ctx.name, entries);
        }

        static void ReleaseStrokeBuffers(StrokeTarget t)
        {
            void Rel(ref RenderTexture rt)
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                rt = null;
            }
            Rel(ref t.stroke);
            Rel(ref t.stamp);
            Rel(ref t.paint);
            Rel(ref t.paintStamp);
            Rel(ref t.mixSample);
            Rel(ref t.mixColor);
            Rel(ref t.mixColorNext);
        }

        public void CancelStroke(StrokeContext ctx)
        {
            foreach (var t in ctx.targets)
            {
                if (t.snapshot != null) APUtil.Copy(t.snapshot, t.target);
                ReleaseStrokeBuffers(t);
                APUtil.Release(ref t.snapshot);
                NotifyChanged(t.set);
            }
            for (int i = ctx.prefix.Count - 1; i >= 0; i--)
            {
                ctx.prefix[i].Undo();
                ctx.prefix[i].Dispose(false);
            }
            ctx.targets.Clear();
            ctx.prefix.Clear();
        }

        // ================================================================== fills

        /// <summary>Rasterises whole triangles (given by their first index) into a single-channel target.</summary>
        void DrawTriangles(RenderTexture target, int uvChannel, IEnumerable<KeyValuePair<RendererEntry, KeyValuePair<int, IEnumerable<int>>>> groups, bool clear)
        {
            var verts = new List<Vector3>();
            var uv0 = new List<Vector2>();
            var tris = new List<int>();
            foreach (var g in groups)
            {
                var e = g.Key;
                int submesh = g.Value.Key;
                if (submesh >= e.SubmeshCount) continue;
                var uvs = e.GetUVs(uvChannel);
                var idx = e.submeshIndices[submesh];
                foreach (int first in g.Value.Value)
                {
                    if (first < 0 || first + 2 >= idx.Length) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        tris.Add(verts.Count);
                        verts.Add(Vector3.zero);
                        uv0.Add(uvs[idx[first + k]]);
                    }
                }
            }
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(target);
            if (clear) GL.Clear(false, true, Color.black);
            if (tris.Count > 0)
            {
                var mesh = TempMesh();
                mesh.SetVertices(verts);
                mesh.SetUVs(0, uv0);
                var normals = new List<Vector3>(verts.Count);
                for (int i = 0; i < verts.Count; i++) normals.Add(Vector3.up);
                mesh.SetNormals(normals);
                mesh.SetTriangles(tris, 0, false);
                var m = APResources.Paint;
                m.SetFloat(APIds.Value, 1f);
                m.SetInt(APIds.UVChannel, 0);
                m.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));
                m.SetPass(PaintPass.CoveragePass);
                Graphics.DrawMeshNow(mesh, Matrix4x4.identity, 0);
            }
            RenderTexture.active = prev;
        }

        static IEnumerable<KeyValuePair<RendererEntry, KeyValuePair<int, IEnumerable<int>>>> One(RendererEntry e, int submesh, IEnumerable<int> tris)
        {
            yield return new KeyValuePair<RendererEntry, KeyValuePair<int, IEnumerable<int>>>(e, new KeyValuePair<int, IEnumerable<int>>(submesh, tris));
        }

        /// <summary>Marks whole triangles (by first index) of one renderer slot as fully covered.</summary>
        public void CoverTriangles(StrokeContext ctx, TextureSet set, RendererEntry e, int submesh, List<int> triStarts)
        {
            var t = ctx.Find(set);
            if (t == null || triStarts.Count == 0) return;
            DrawTriangles(t.stamp, set.uvChannel, One(e, submesh, triStarts), true);
            var prev = RenderTexture.active;
            Graphics.Blit(t.stamp, t.stroke, APResources.Composite, CompPass.Accumulate);
            RenderTexture.active = prev;
            t.pending = true;
            t.touched = true;
        }

        /// <summary>Rasterises the set's selected UV polygons into its selection mask.</summary>
        public void BuildSelection(TextureSet set)
        {
            set.selectionDirty = false;
            if (!set.HasSelection)
            {
                APUtil.Release(ref set.selectionRT);
                return;
            }
            if (set.selectionRT == null || set.selectionRT.width != set.width || set.selectionRT.height != set.height)
            {
                APUtil.Release(ref set.selectionRT);
                set.selectionRT = APUtil.CreateRT(set.width, set.height, MaskFormat, "Selection");
            }
            var groups = new List<KeyValuePair<RendererEntry, KeyValuePair<int, IEnumerable<int>>>>();
            foreach (var kv in set.selection)
            {
                int entry = kv.Key >> 8, submesh = kv.Key & 0xFF;
                if (entry < 0 || entry >= m_Meshes.entries.Count || kv.Value.Count == 0) continue;
                groups.Add(new KeyValuePair<RendererEntry, KeyValuePair<int, IEnumerable<int>>>(
                    m_Meshes.entries[entry], new KeyValuePair<int, IEnumerable<int>>(submesh, kv.Value)));
            }
            DrawTriangles(set.selectionRT, set.uvChannel, groups, true);
        }

        /// <summary>Covers every mesh slot of the set (its UV islands).</summary>
        public void CoverIslands(StrokeContext ctx, TextureSet set)
        {
            var t = ctx.Find(set);
            if (t == null) return;
            if (set.islandDirty || set.island == null) BuildIsland(set);
            var comp = APResources.Composite;
            comp.SetFloat(APIds.CopyMode, 1f);
            comp.SetFloat(APIds.SrcToGamma, 0f);
            var prev = RenderTexture.active;
            Graphics.Blit(set.island, t.stroke, comp, CompPass.Copy);
            RenderTexture.active = prev;
            t.pending = true;
            t.touched = true;
        }

        /// <summary>Covers the entire texture, including empty space between islands.</summary>
        public void CoverAll(StrokeContext ctx, TextureSet set)
        {
            var t = ctx.Find(set);
            if (t == null) return;
            APUtil.Clear(t.stroke, Color.white);
            t.pending = true;
            t.touched = true;
        }

        public void CoverFromTexture(StrokeContext ctx, TextureSet set, Texture maskTex)
        {
            var t = ctx.Find(set);
            if (t == null || maskTex == null) return;
            var comp = APResources.Composite;
            comp.SetFloat(APIds.CopyMode, 1f);
            comp.SetFloat(APIds.SrcToGamma, 0f);
            var prev = RenderTexture.active;
            Graphics.Blit(maskTex, t.stroke, comp, CompPass.Copy);
            RenderTexture.active = prev;
            t.pending = true;
            t.touched = true;
        }

        // ================================================================== 3D gradient

        /// <summary>
        /// Draws a gradient laid over the model in 3D (by world position, not by UV), replacing whatever the gradient
        /// drew before in this stroke, so it can be redrawn live while the end point is dragged.
        /// </summary>
        public void Gradient(StrokeContext ctx, GradientParams g)
        {
            m_Meshes.EnsurePosed();
            var prev = RenderTexture.active;
            var m = APResources.Paint;
            var dir = g.end - g.start;
            float len2 = Mathf.Max(dir.sqrMagnitude, 1e-12f);
            m.SetVector(APIds.GradStart, g.start);
            m.SetVector(APIds.GradDir, dir / len2);
            m.SetFloat(APIds.GradLength, Mathf.Sqrt(len2));
            m.SetFloat(APIds.GradShape, (float)g.shape);
            m.SetFloat(APIds.GradMirror, g.mirror ? 1f : 0f);
            m.SetFloat(APIds.GradMirrorAxis, Mathf.Clamp(g.mirrorAxis, 0, 2));
            m.SetFloat(APIds.GradMirrorSide, g.mirrorSide >= 0f ? 1f : -1f);
            m.SetMatrix(APIds.GradMirrorMatrix, g.mirrorMatrix);
            m.SetMatrix(APIds.GradWorldToLocal, g.worldToLocal);
            m.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));
            int[] passes = PaintPass.UseConservative ? new[] { PaintPass.GradientConservative, PaintPass.Gradient } : new[] { PaintPass.Gradient };
            foreach (var t in ctx.targets)
            {
                EnsurePaintBuffers(t, false);
                var a = g.colorA;
                var b = g.colorB;
                if (t.mask)
                {
                    // Masks get gray: the brush's mask value (white on fill layers) and the second color's brightness.
                    float va = ctx.maskValue;
                    float vb = APUtil.Luminance(b);
                    a = new Color(va, va, va, a.a);
                    b = new Color(vb, vb, vb, b.a);
                }
                m.SetVector(APIds.GradColorA, APUtil.V(a));
                m.SetVector(APIds.GradColorB, APUtil.V(b));
                m.SetInt(APIds.UVChannel, t.set.uvChannel);
                Graphics.SetRenderTarget(t.paint);
                GL.Clear(false, true, new Color(0, 0, 0, 0));
                foreach (int pass in passes)
                {
                    m.SetPass(pass);
                    foreach (var u in t.set.users)
                    {
                        if (u.renderer < 0 || u.renderer >= m_Meshes.entries.Count) continue;
                        var e = m_Meshes.entries[u.renderer];
                        if (!e.Visible || u.submesh >= e.SubmeshCount) continue;
                        Graphics.DrawMeshNow(e.paintMesh, e.matrix, u.submesh);
                    }
                }
                t.pending = true;
                t.touched = true;
            }
            RenderTexture.active = prev;
        }

        // ================================================================== stars

        static void SetupStars(Material m, StarSettings st)
        {
            m.SetFloat(APIds.StarAmount, Mathf.Clamp01(st.amount));
            m.SetFloat(APIds.StarSize, Mathf.Clamp(st.size, 0.005f, 0.4f));
            m.SetFloat(APIds.StarSizeVar, Mathf.Clamp01(st.sizeVariation));
            m.SetFloat(APIds.StarGlow, Mathf.Max(1f, st.glowSize));
            m.SetFloat(APIds.StarGlowStrength, Mathf.Max(0f, st.glow));
            m.SetFloat(APIds.StarSparkle, Mathf.Clamp01(st.sparkles));
            m.SetFloat(APIds.StarFlare, Mathf.Max(1f, st.sparkleLength));
            m.SetFloat(APIds.StarWhite, Mathf.Clamp01(st.whiteCores));
            m.SetFloat(APIds.StarSeed, st.seed);
            m.SetVector(APIds.StarColorA, APUtil.V(st.colorA));
            m.SetVector(APIds.StarColorB, APUtil.V(st.colorB));
            m.SetFloat(APIds.Nebula, Mathf.Clamp01(st.nebula));
            m.SetFloat(APIds.NebulaScale, Mathf.Max(0.5f, st.nebulaSize / Mathf.Max(1e-4f, st.spacing)));
        }

        /// <summary>
        /// Scatters stars over the rest-pose model in 3D and writes them into the texture's UV layout, so they are
        /// spread evenly across seams and stay stuck to the model (like a Local Pos projection, but baked).
        /// </summary>
        public void RenderStars(TextureSet set, RenderTexture target, StarSettings st)
        {
            m_Meshes.EnsureRestMeshes();
            var prev = RenderTexture.active;
            var m = APResources.Paint;
            SetupStars(m, st);
            m.SetFloat(APIds.StarCell, Mathf.Max(1e-4f, st.spacing));
            m.SetFloat(APIds.StarPeriod, 0f);
            m.SetFloat(APIds.StarMatcap, 0f);
            m.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));
            m.SetInt(APIds.UVChannel, set.uvChannel);
            Graphics.SetRenderTarget(target);
            GL.Clear(false, true, new Color(0, 0, 0, 0));
            int[] passes = PaintPass.UseConservative ? new[] { PaintPass.StarsConservative, PaintPass.Stars } : new[] { PaintPass.Stars };
            foreach (int pass in passes)
            {
                foreach (var u in set.users)
                {
                    if (u.renderer < 0 || u.renderer >= m_Meshes.entries.Count) continue;
                    var e = m_Meshes.entries[u.renderer];
                    if (e.restMesh == null || u.submesh >= e.SubmeshCount) continue;
                    m.SetPass(pass);
                    Graphics.DrawMeshNow(e.restMesh, Matrix4x4.identity, u.submesh);
                }
            }
            RenderTexture.active = prev;
            NotifyChanged(set);
        }

        /// <summary>
        /// A flat star image for shaders that move the stars themselves: a seamless tiling texture (parallax,
        /// Local/World Pos slots) or a matcap with a glowing rim. Returns PNG bytes.
        /// </summary>
        public static byte[] RenderStarsImage(StarSettings st, int size, bool matcap)
        {
            var m = APResources.Paint;
            var tile = new StarSettings();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(st), tile);
            // A tile holds about as many stars across as 10 cm of the model would.
            float cells = Mathf.Clamp(Mathf.Round(0.1f / Mathf.Max(1e-3f, st.spacing)), 2f, 64f);
            if (!matcap) tile.nebula = 0f;   // the cloud noise does not repeat, so it would show a seam
            var prev = RenderTexture.active;
            var rt = APUtil.CreateRT(size, size, GraphicsFormat.R8G8B8A8_UNorm, "Stars");
            SetupStars(m, tile);
            m.SetFloat(APIds.StarCell, 1f / cells);
            m.SetFloat(APIds.StarPeriod, matcap ? 0f : cells);
            m.SetFloat(APIds.StarMatcap, matcap ? 1f : 0f);
            m.SetFloat(APIds.NebulaScale, Mathf.Max(0.5f, st.nebulaSize / Mathf.Max(1e-4f, st.spacing)));
            Graphics.Blit(APResources.White, rt, m, PaintPass.StarsFlat);
            RenderTexture.active = prev;
            var png = APUtil.EncodePNG(rt);
            APUtil.Release(ref rt);
            return png;
        }

        /// <summary>Flood-fill selection by color similarity on the composited texture (CPU).</summary>
        public Texture2D FloodSelect(TextureSet set, Vector2 uv, float tolerance, bool contiguous, bool sampleActiveLayer)
        {
            Composite(set);
            RenderTexture src = sampleActiveLayer && set.ActiveLayer != null && set.ActiveLayer.content != null
                ? set.ActiveLayer.content
                : set.lastComposite;
            if (src == null) return null;
            var tex = APUtil.ReadToTexture(src);
            var px = tex.GetPixels32();
            Object.DestroyImmediate(tex);
            int w = set.width, h = set.height;
            uv.x -= Mathf.Floor(uv.x);
            uv.y -= Mathf.Floor(uv.y);
            int sx = Mathf.Clamp((int)(uv.x * w), 0, w - 1);
            int sy = Mathf.Clamp((int)(uv.y * h), 0, h - 1);
            var seed = px[sy * w + sx];
            int tol = Mathf.RoundToInt(Mathf.Clamp01(tolerance) * 255f);
            var sel = new byte[w * h];

            bool Match(Color32 c)
            {
                return Mathf.Abs(c.r - seed.r) <= tol && Mathf.Abs(c.g - seed.g) <= tol &&
                       Mathf.Abs(c.b - seed.b) <= tol && Mathf.Abs(c.a - seed.a) <= tol;
            }

            if (!contiguous)
            {
                for (int i = 0; i < px.Length; i++)
                    if (Match(px[i])) sel[i] = 255;
            }
            else
            {
                var stack = new Stack<int>();
                stack.Push(sy * w + sx);
                sel[sy * w + sx] = 255;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    int x = i % w, y = i / w;
                    if (x > 0) Visit(i - 1);
                    if (x < w - 1) Visit(i + 1);
                    if (y > 0) Visit(i - w);
                    if (y < h - 1) Visit(i + w);
                }

                void Visit(int j)
                {
                    if (sel[j] != 0 || !Match(px[j])) return;
                    sel[j] = 255;
                    stack.Push(j);
                }
            }

            var mask = new Texture2D(w, h, TextureFormat.R8, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            mask.LoadRawTextureData(sel);
            mask.Apply(false);
            return mask;
        }

        // ================================================================== image layers

        public void RenderImageLayer(TextureSet set, PaintLayer layer)
        {
            if (layer == null || layer.kind != LayerKind.Image || layer.placement == null) return;
            if (layer.content == null) layer.content = NewContentRT(set);
            var p = layer.placement;
            var prev = RenderTexture.active;
            APUtil.Clear(layer.content, new Color(0, 0, 0, 0));
            if (p.image == null)
            {
                RenderTexture.active = prev;
                NotifyChanged(set);
                return;
            }
            var m = APResources.Paint;
            m.SetFloat(APIds.FlipX, p.flipX ? 1f : 0f);
            m.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));
            if (p.wrap)
            {
                RenderWrapped(set, layer.content, p);
            }
            else if (!p.projected)
            {
                p.image.wrapMode = TextureWrapMode.Clamp;
                if (set.islandDirty) BuildIsland(set);
                var verts = new List<Vector3>();
                var locals = new List<Vector2>();
                var tris = new List<int>();
                float rx = p.uvSize.x * 0.5f * set.width;
                float ry = p.uvSize.y * 0.5f * set.height;
                if (p.HasWarp) AddWarpedQuad(verts, locals, tris, p, rx, ry, set.width, set.height);
                else AddQuad(verts, locals, tris, p.uvCenter, rx, ry, p.rotation * Mathf.Deg2Rad, set.width, set.height);
                var mesh = TempMesh();
                mesh.SetVertices(verts);
                mesh.SetUVs(0, locals);
                mesh.SetTriangles(tris, 0, false);
                m.SetTexture(APIds.MainTex, p.image);
                m.SetFloat(APIds.ClipIslands, p.clipToIslands ? 1f : 0f);
                m.SetTexture(APIds.IslandTex, set.island != null ? (Texture)set.island : APResources.White);
                Graphics.SetRenderTarget(layer.content);
                m.SetPass(PaintPass.QuadImage);
                Graphics.DrawMeshNow(mesh, Matrix4x4.identity, 0);
            }
            else
            {
                m_Meshes.EnsurePosed();
                p.image.wrapMode = TextureWrapMode.Clamp;
                var n = p.normal.sqrMagnitude > 1e-8f ? p.normal.normalized : Vector3.forward;
                var up = Vector3.ProjectOnPlane(p.up, n);
                if (up.sqrMagnitude < 1e-8f) up = Vector3.ProjectOnPlane(Vector3.up, n);
                if (up.sqrMagnitude < 1e-8f) up = Vector3.ProjectOnPlane(Vector3.forward, n);
                up.Normalize();
                var rot = Quaternion.AngleAxis(-p.rotation, n);
                up = rot * up;
                var right = Vector3.Cross(n, up).normalized;
                float hw = Mathf.Max(1e-5f, p.worldWidth * 0.5f);
                float hh = hw * p.Aspect;
                var tmp = RenderTexture.GetTemporary(set.width, set.height, 0, GraphicsFormat.R8G8B8A8_UNorm);
                Graphics.SetRenderTarget(tmp);
                GL.Clear(false, true, new Color(0, 0, 0, 0));
                m.SetVector(APIds.DecalPos, p.worldPos);
                m.SetVector(APIds.DecalRight, right);
                m.SetVector(APIds.DecalUp, up);
                m.SetVector(APIds.DecalNormal, n);
                m.SetVector(APIds.DecalHalf, new Vector3(hw, hh, Mathf.Max(1e-5f, p.depth)));
                m.SetFloat(APIds.DecalCos, Mathf.Cos(Mathf.Clamp(p.angleLimit, 1f, 179f) * Mathf.Deg2Rad));
                m.SetTexture(APIds.DecalTex, p.image);
                m.SetInt(APIds.UVChannel, set.uvChannel);
                m.SetPass(PaintPass.DecalPass);
                foreach (var u in set.users)
                {
                    if (u.renderer < 0 || u.renderer >= m_Meshes.entries.Count) continue;
                    var e = m_Meshes.entries[u.renderer];
                    if (!e.Visible || u.submesh >= e.SubmeshCount) continue;
                    Graphics.DrawMeshNow(e.paintMesh, e.matrix, u.submesh);
                }
                Graphics.Blit(tmp, layer.content, APResources.Composite, CompPass.Unpremultiply);
                RenderTexture.ReleaseTemporary(tmp);
            }
            RenderTexture.active = prev;
            NotifyChanged(set);
        }

        /// <summary>
        /// Projects a tiling image onto the rest-pose model from three sides (or one) and writes the result into the
        /// texture's UV layout. Normal maps are converted into the mesh's own tangent space, so the baked map
        /// lights exactly like the live world/local-position projection did.
        /// </summary>
        void RenderWrapped(TextureSet set, RenderTexture target, ImagePlacement p)
        {
            m_Meshes.EnsureRestMeshes();
            var m = APResources.Paint;
            var img = p.image;
            img.wrapMode = TextureWrapMode.Repeat;
            img.filterMode = FilterMode.Trilinear;
            float rad = p.rotation * Mathf.Deg2Rad;
            m.SetTexture(APIds.MainTex, img);
            m.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));
            m.SetInt(APIds.UVChannel, set.uvChannel);
            m.SetFloat(APIds.WrapSize, Mathf.Max(1e-4f, p.tileSize));
            m.SetFloat(APIds.WrapSharp, Mathf.Clamp(p.sharpness, 1f, 64f));
            m.SetFloat(APIds.WrapAxis, p.axis);
            m.SetVector(APIds.WrapOffset, p.wrapOffset);
            m.SetVector(APIds.WrapRot, new Vector4(Mathf.Cos(rad), Mathf.Sin(rad), 0, 0));
            m.SetFloat(APIds.WrapNormal, p.normalMap ? 1f : 0f);
            m.SetFloat(APIds.WrapStrength, p.normalStrength);
            m.SetFloat(APIds.WrapGreenDown, p.greenDown ? 1f : 0f);
            m.SetFloat(APIds.FlipX, p.flipX ? 1f : 0f);
            m.SetFloat(APIds.WrapImageAspect, img.width / (float)Mathf.Max(1, img.height));
            m.SetFloat(APIds.WrapPlanar, p.shaderSpace > 0 ? 1f : 0f);
            Graphics.SetRenderTarget(target);
            GL.Clear(false, true, new Color(0, 0, 0, 0));
            // Conservative first so thin triangles get covered, then the exact pass on top for clean interiors.
            int[] passes = PaintPass.UseConservative ? new[] { PaintPass.WrapConservative, PaintPass.Wrap } : new[] { PaintPass.Wrap };
            foreach (int pass in passes)
            {
                foreach (var u in set.users)
                {
                    if (u.renderer < 0 || u.renderer >= m_Meshes.entries.Count) continue;
                    var e = m_Meshes.entries[u.renderer];
                    if (e.restMesh == null || u.submesh >= e.SubmeshCount) continue;
                    if (p.shaderSpace > 0)
                    {
                        // Rest-pose avatar-root position -> the shader's local (or world) position -> texture uv.
                        var basis = p.shaderSpace == 1 ? e.restRootToLocal : e.restRootToWorld;
                        var st = p.shaderST;
                        m.SetVector(APIds.WrapRowU, basis.GetRow(Mathf.Clamp(p.axisU, 0, 2)) * st.x + new Vector4(0, 0, 0, st.z));
                        m.SetVector(APIds.WrapRowV, basis.GetRow(Mathf.Clamp(p.axisV, 0, 2)) * st.y + new Vector4(0, 0, 0, st.w));
                    }
                    m.SetPass(pass);
                    Graphics.DrawMeshNow(e.restMesh, Matrix4x4.identity, u.submesh);
                }
            }
        }

        // ================================================================== 2D view

        public void RenderWire(TextureSet set, RenderTexture rt, Vector4 uvTransform, Color color)
        {
            if (set == null || rt == null) return;
            var prev = RenderTexture.active;
            var m = APResources.Paint;
            m.SetVector(APIds.UVTransform, uvTransform);
            m.SetInt(APIds.UVChannel, set.uvChannel);
            m.SetColor(APIds.WireColor, color);
            Graphics.SetRenderTarget(rt);
            GL.wireframe = true;
            try
            {
                foreach (var u in set.users)
                {
                    if (u.renderer < 0 || u.renderer >= m_Meshes.entries.Count) continue;
                    var e = m_Meshes.entries[u.renderer];
                    if (u.submesh >= e.SubmeshCount) continue;
                    m.SetPass(PaintPass.Wire);
                    Graphics.DrawMeshNow(e.paintMesh, e.matrix, u.submesh);
                }
            }
            finally
            {
                GL.wireframe = false;
                m.SetVector(APIds.UVTransform, new Vector4(1, 1, 0, 0));
                RenderTexture.active = prev;
            }
        }

        public void RenderView2D(TextureSet set, RenderTexture rt, View2DParams p)
        {
            if (rt == null) return;
            var prev = RenderTexture.active;
            var comp = APResources.Composite;
            Texture src = set != null && set.initialized && set.display != null ? (Texture)set.display :
                set != null && set.originalTexture != null ? set.originalTexture : APResources.White;
            int tw = src.width, th = src.height;
            comp.SetVector(APIds.UVMap, p.uvMap);
            comp.SetVector(APIds.ViewSize, new Vector4(rt.width, rt.height, 0, 0));
            comp.SetVector(APIds.TexSize, new Vector4(tw, th, 0, 0));
            comp.SetFloat(APIds.ShowAlpha, p.showAlpha ? 1f : 0f);
            comp.SetFloat(APIds.PointSample, p.pointSample ? 1f : 0f);
            comp.SetFloat(APIds.Tile, p.tile ? 1f : 0f);
            comp.SetFloat(APIds.CheckerSize, 8f * EditorGUIUtility.pixelsPerPoint);
            comp.SetColor(APIds.CheckerA, p.checkerA);
            comp.SetColor(APIds.CheckerB, p.checkerB);
            comp.SetColor(APIds.OutsideColor, p.outside);
            var active = set?.ActiveLayer;
            bool overlay = p.maskOverlay && active != null && active.HasMask;
            comp.SetFloat(APIds.OverlayMaskView, overlay ? 1f : 0f);
            comp.SetTexture(APIds.MaskTex, overlay ? (Texture)active.mask : APResources.White);
            comp.SetColor(APIds.OverlayColor, new Color(0.9f, 0.15f, 0.25f, 0.45f));
            bool sel = set != null && set.initialized && set.HasSelection && !highlightSelection;
            if (sel && (set.selectionDirty || set.selectionRT == null)) BuildSelection(set);
            comp.SetFloat(APIds.SelOverlay, sel && set.selectionRT != null ? 1f : 0f);
            comp.SetTexture(APIds.SelTex, sel && set.selectionRT != null ? (Texture)set.selectionRT : APResources.White);
            Graphics.Blit(src, rt, comp, CompPass.View2D);
            comp.SetFloat(APIds.SelOverlay, 0f);
            RenderTexture.active = prev;
        }

        public void RenderGradient(RenderTexture rt, Color top, Color bottom)
        {
            var prev = RenderTexture.active;
            var comp = APResources.Composite;
            comp.SetColor(APIds.TopColor, top);
            comp.SetColor(APIds.BottomColor, bottom);
            Graphics.Blit(APResources.White, rt, comp, CompPass.Gradient);
            RenderTexture.active = prev;
        }

        // ================================================================== resizing

        public void Resize(TextureSet set, int width, int height)
        {
            if (!set.initialized || (width == set.width && height == set.height)) return;
            var comp = APResources.Composite;
            comp.SetFloat(APIds.CopyMode, 0f);
            comp.SetFloat(APIds.SrcToGamma, 0f);
            foreach (var l in set.layers)
            {
                if (l.content != null)
                {
                    var n = APUtil.CreateRT(width, height, ContentFormat, "Layer");
                    Graphics.Blit(l.content, n, comp, CompPass.Copy);
                    APUtil.Release(ref l.content);
                    l.content = n;
                }
                if (l.mask != null)
                {
                    var n = APUtil.CreateRT(width, height, MaskFormat, "Mask");
                    comp.SetFloat(APIds.CopyMode, 1f);
                    Graphics.Blit(l.mask, n, comp, CompPass.Copy);
                    comp.SetFloat(APIds.CopyMode, 0f);
                    APUtil.Release(ref l.mask);
                    l.mask = n;
                }
            }
            set.width = width;
            set.height = height;
            set.ReleaseBuffers();
            EnsureBuffers(set);
            foreach (var l in set.layers)
                if (l.kind == LayerKind.Image) RenderImageLayer(set, l);
            NotifyChanged(set);
        }

        public void Dispose()
        {
            APUtil.Release(ref m_Depth);
            if (m_TempMesh != null) Object.DestroyImmediate(m_TempMesh);
            m_TempMesh = null;
        }
    }
}
