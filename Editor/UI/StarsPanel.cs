using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace AviCreatorEssentials
{
    /// <summary>
    /// The space look: glowing stars scattered over the model in 3D (baked onto a layer), and flat star textures
    /// for shader effects that move with the view (matcap, parallax, Local/World Pos slots).
    /// </summary>
    public class StarsPanel : VisualElement
    {
        readonly PainterContext m_Ctx;
        readonly List<VisualElement> m_Rows = new List<VisualElement>();
        readonly List<(TextureSet set, PaintLayer layer)> m_Live = new List<(TextureSet set, PaintLayer layer)>();
        readonly ColorField m_ColorA;
        readonly ColorField m_ColorB;
        IHistoryEntry m_LiveEntry;
        APSession m_LiveSession;

        StarSettings S => m_Ctx.stars;

        public StarsPanel(PainterContext ctx)
        {
            m_Ctx = ctx;
            AddToClassList("ap-stars-panel");
            Add(W.Text("Scatter glowing stars over the model. They're placed in 3D on the avatar's rest pose (like Local Pos), " +
                       "so they spread evenly across UV seams, then baked into a new layer that stays stuck to the model in any " +
                       "shader. Settings update the new layer live until you do something else.", "ap-hint"));

            Add(Row(new W.SliderRow("Spacing (cm)", 0.002f, 0.15f, () => S.spacing, v => { S.spacing = v; Changed(); }, 100f,
                "Average distance between stars on the model", true)));
            Add(Row(new W.SliderRow("Amount", 0f, 1f, () => S.amount, v => { S.amount = v; Changed(); }, 100f,
                "How many of the possible places get a star")));
            Add(Row(new W.SliderRow("Star size", 0.01f, 0.35f, () => S.size, v => { S.size = v; Changed(); }, 100f,
                "Size of the brightest part, as a percentage of the spacing")));
            Add(Row(new W.SliderRow("Size variety", 0f, 1f, () => S.sizeVariation, v => { S.sizeVariation = v; Changed(); }, 100f,
                "0: all stars the same size. 1: mostly tiny specks with a few big stars.")));
            Add(Row(new W.SliderRow("Glow", 0f, 1.5f, () => S.glow, v => { S.glow = v; Changed(); }, 100f,
                "Strength of the soft halo around each star")));
            Add(Row(new W.SliderRow("Glow size", 1f, 10f, () => S.glowSize, v => { S.glowSize = v; Changed(); }, 1f,
                "Halo radius, as a multiple of the star size")));
            Add(Row(new W.SliderRow("Sparkles", 0f, 1f, () => S.sparkles, v => { S.sparkles = v; Changed(); }, 100f,
                "Share of stars that get four-point flares ✦")));
            Add(Row(new W.SliderRow("Sparkle length", 1f, 15f, () => S.sparkleLength, v => { S.sparkleLength = v; Changed(); }, 1f,
                "Flare length, as a multiple of the star size")));
            Add(Row(new W.SliderRow("White centres", 0f, 1f, () => S.whiteCores, v => { S.whiteCores = v; Changed(); }, 100f,
                "How white the hot centre of each star is; the halo keeps the star's color")));
            Add(Row(new W.SliderRow("Nebula", 0f, 1f, () => S.nebula, v => { S.nebula = v; Changed(); }, 100f,
                "Soft glowing clouds in the star colors underneath the stars")));
            Add(Row(new W.SliderRow("Nebula size (cm)", 0.01f, 0.5f, () => S.nebulaSize, v => { S.nebulaSize = v; Changed(); }, 100f,
                "Size of the nebula clouds on the model", true)));

            var seedRow = W.Row("ap-row-gap");
            var seed = Row(new W.SliderRow("Pattern", 0f, 100f, () => S.seed, v => { S.seed = Mathf.Round(v); Changed(); }, 1f,
                "Which random arrangement of stars"));
            seed.style.flexGrow = 1;
            seedRow.Add(seed);
            seedRow.Add(W.TextButton("Shuffle", () =>
            {
                S.seed = Random.Range(0, 101);
                Refresh();
                Changed();
            }, "ap-small"));
            Add(seedRow);

            var colors = W.Row("ap-row-gap", "ap-wrap");
            m_ColorA = new ColorField { value = S.colorA, showAlpha = false, hdr = false, tooltip = "Star color" };
            m_ColorA.RegisterValueChangedCallback(e => { S.colorA = e.newValue; Changed(); });
            m_ColorB = new ColorField { value = S.colorB, showAlpha = false, hdr = false, tooltip = "Second star color: each star is somewhere between the two" };
            m_ColorB.RegisterValueChangedCallback(e => { S.colorB = e.newValue; Changed(); });
            m_ColorA.style.flexGrow = 1;
            m_ColorB.style.flexGrow = 1;
            colors.Add(m_ColorA);
            colors.Add(m_ColorB);
            colors.Add(W.TextButton("Use paint colors", () =>
            {
                S.colorA = m_Ctx.primary;
                S.colorB = m_Ctx.secondary;
                Refresh();
                Changed();
            }, "ap-small"));
            Add(colors);

            Add(Row(new W.ToggleRow("Glow (also on emission)", () => S.addToEmission, v => { S.addToEmission = v; S.Save(); },
                "Also put the stars on the material's emission texture, so they shine in the dark")));

            var buttons = W.Row("ap-row-gap", "ap-wrap");
            buttons.Add(W.TextButton("Add star layer", AddStars, "ap-small", "ap-accent"));
            Add(buttons);

            Add(W.Text("For stars that shift with the view, make a texture for your avatar shader: a tiling one for parallax or " +
                       "a Local/World Pos slot, or a matcap (add it with an additive blend).", "ap-hint"));
            var export = W.Row("ap-row-gap", "ap-wrap");
            export.Add(W.TextButton("Save tiling texture…", () => SaveImage(false), "ap-small"));
            export.Add(W.TextButton("Save matcap…", () => SaveImage(true), "ap-small"));
            Add(export);
        }

        VisualElement Row(VisualElement e)
        {
            m_Rows.Add(e);
            return e;
        }

        void Refresh()
        {
            foreach (var e in m_Rows)
            {
                if (e is W.SliderRow s) s.Refresh();
                else if (e is W.ToggleRow t) t.Refresh();
            }
            m_ColorA.SetValueWithoutNotify(S.colorA);
            m_ColorB.SetValueWithoutNotify(S.colorB);
        }

        void Changed()
        {
            S.Save();
            UpdateLive();
        }

        /// <summary>Redraws the layers added last, as long as nothing else has been done since (so no painting is lost).</summary>
        void UpdateLive()
        {
            var s = m_Ctx.session;
            if (s == null || m_LiveEntry == null || s != m_LiveSession || s.history.Top != m_LiveEntry)
            {
                m_LiveEntry = null;
                m_Live.Clear();
                return;
            }
            foreach (var (set, layer) in m_Live)
                if (layer.content != null && set.layers.Contains(layer)) s.engine.RenderStars(set, layer.content, S);
            s.dirty = true;
            m_Ctx.Render();
        }

        void AddStars()
        {
            var s = m_Ctx.session;
            var set = s?.ActiveSet;
            if (set == null)
            {
                m_Ctx.ShowHint("Pick a material to add stars to first.");
                return;
            }
            var targets = new List<TextureSet> { set };
            var emission = S.addToEmission ? s.EmissionSibling(set) : null;
            if (emission != null) targets.Add(emission);
            m_Live.Clear();
            m_LiveEntry = s.AddStarLayers(targets, S, m_Live);
            m_LiveSession = s;
            if (S.addToEmission && emission == null && set.property != APSession.EmissionProperty(set.cloneMaterial))
                m_Ctx.ShowHint("Stars added. This material's shader has no emission slot, so they are only on the main texture.");
            else
                m_Ctx.ShowHint("Stars added on a new layer" + (emission != null ? " and on the emission texture, so they glow" : "") +
                               ". Change the settings to adjust them; add a mask to keep them only where you want.");
            m_Ctx.Render();
        }

        void SaveImage(bool matcap)
        {
            string path = EditorUtility.SaveFilePanelInProject(matcap ? "Save star matcap" : "Save tiling star texture",
                matcap ? "Star Matcap" : "Stars Tiling", "png",
                matcap ? "A matcap of stars with a glowing rim." : "A seamless tiling texture of stars.");
            if (string.IsNullOrEmpty(path)) return;
            var png = PaintEngine.RenderStarsImage(S, 1024, matcap);
            File.WriteAllBytes(APUtil.AssetPathToFull(path), png);
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.wrapMode = matcap ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                importer.SaveAndReimport();
            }
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            m_Ctx.ShowHint("Saved " + path + ".");
        }
    }
}
