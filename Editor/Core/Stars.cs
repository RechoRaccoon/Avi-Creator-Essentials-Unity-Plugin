using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>Settings of the star generator, saved in EditorPrefs between sessions.</summary>
    [Serializable]
    public class StarSettings
    {
        const string PrefKey = "AviCreatorEssentials.Stars.v1";

        public float spacing = 0.02f;          // average distance between stars on the model, metres
        public float amount = 0.6f;            // 0..1 share of places that get a star
        public float size = 0.07f;             // star core radius, fraction of the spacing
        public float sizeVariation = 0.85f;    // 0: all the same size, 1: mostly tiny with a few big ones
        public float glow = 0.35f;             // strength of the soft halo
        public float glowSize = 4f;            // halo radius, multiple of the core
        public float sparkles = 0.3f;          // 0..1 share of stars with four-point flares
        public float sparkleLength = 7f;       // flare length, multiple of the core
        public float whiteCores = 0.7f;        // 0..1 how white the centres are
        public float seed = 1f;
        public Color colorA = new Color(0.55f, 1f, 0.6f, 1f);
        public Color colorB = new Color(0.45f, 0.9f, 1f, 1f);
        public float nebula;                   // 0..1 soft glowing clouds under the stars
        public float nebulaSize = 0.08f;       // metres
        public bool addToEmission = true;      // also put the stars on the material's emission texture so they glow

        public static StarSettings Load()
        {
            var s = new StarSettings();
            string json = EditorPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, s); }
                catch (Exception) { /* keep defaults */ }
            }
            return s;
        }

        public void Save() => EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(this));
    }

    public partial class APSession
    {
        /// <summary>The emission texture of the same material, if it has one.</summary>
        public TextureSet EmissionSibling(TextureSet set)
        {
            if (set == null || set.cloneMaterial == null) return null;
            string prop = EmissionProperty(set.cloneMaterial);
            if (prop == null || prop == set.property) return null;
            foreach (var s in sets)
                if (s.originalMaterial == set.originalMaterial && s.property == prop) return s;
            return null;
        }

        /// <summary>
        /// Adds a layer of stars to each texture, as one undo step. Returns that step, so the Stars panel can keep
        /// redrawing the new layers while the settings change, as long as nothing else has been done since.
        /// </summary>
        public IHistoryEntry AddStarLayers(IList<TextureSet> targets, StarSettings st, List<(TextureSet set, PaintLayer layer)> added)
        {
            var entries = new List<IHistoryEntry>();
            foreach (var target in targets)
            {
                var set = target;
                if (!set.initialized) InitializeSet(set);
                var layer = new PaintLayer { name = UniqueName(set, "Stars"), kind = LayerKind.Paint };
                layer.content = engine.NewContentRT(set);
                engine.RenderStars(set, layer.content, st);
                int index = InsertIndex(set);
                int prevActive = set.activeLayer;
                set.layers.Insert(index, layer);
                set.activeLayer = index;
                set.editingMask = false;
                entries.Add(new ActionEntry("Add stars",
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
                added.Add((set, layer));
                Changed(set);
            }
            if (entries.Count == 0) return null;
            var entry = entries.Count == 1 ? entries[0] : new CompoundEntry("Add stars", entries);
            Push(entry);
            return entry;
        }
    }
}
