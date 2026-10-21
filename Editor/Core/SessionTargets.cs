using System.Collections.Generic;

namespace AviCreatorEssentials
{
    public partial class APSession
    {
        // Textures painted together with the active one (Ctrl/Shift-click in the Materials list).
        readonly HashSet<TextureSet> m_Selected = new HashSet<TextureSet>();
        readonly Dictionary<TextureSet, string> m_KindCache = new Dictionary<TextureSet, string>();

        /// <summary>The selected textures, active one first.</summary>
        public List<TextureSet> SelectedSets
        {
            get
            {
                var list = new List<TextureSet>();
                if (ActiveSet != null) list.Add(ActiveSet);
                foreach (var s in sets)
                    if (s != ActiveSet && m_Selected.Contains(s)) list.Add(s);
                return list;
            }
        }

        public bool IsSelected(TextureSet set) => set != null && (set == ActiveSet || m_Selected.Contains(set));

        public int SelectedCount
        {
            get
            {
                int n = ActiveSet != null ? 1 : 0;
                foreach (var s in sets)
                    if (s != ActiveSet && m_Selected.Contains(s)) n++;
                return n;
            }
        }

        /// <summary>Plain click: this texture alone.</summary>
        public void SelectOnly(TextureSet set)
        {
            m_Selected.Clear();
            ActiveSet = set;
            StructureChanged?.Invoke();
        }

        /// <summary>Ctrl-click: adds or removes a texture, keeping the one whose layers are shown.</summary>
        public void ToggleSelected(TextureSet set)
        {
            if (set == null) return;
            if (set == ActiveSet)
            {
                // Removing the active texture hands the layer list to another selected one.
                TextureSet next = null;
                foreach (var s in sets)
                    if (s != set && m_Selected.Contains(s)) { next = s; break; }
                if (next == null) return;
                m_Selected.Remove(next);
                m_Selected.Remove(set);
                ActiveSet = next;
            }
            else if (!m_Selected.Remove(set))
            {
                if (!set.initialized) InitializeSet(set);
                m_Selected.Add(set);
            }
            StructureChanged?.Invoke();
        }

        /// <summary>Shift-click: everything between the active texture and this one.</summary>
        public void SelectRange(TextureSet to)
        {
            int a = sets.IndexOf(ActiveSet), b = sets.IndexOf(to);
            if (a < 0 || b < 0)
            {
                SelectOnly(to);
                return;
            }
            for (int i = System.Math.Min(a, b); i <= System.Math.Max(a, b); i++)
            {
                if (sets[i] == ActiveSet) continue;
                if (!sets[i].initialized) InitializeSet(sets[i]);
                m_Selected.Add(sets[i]);
            }
            StructureChanged?.Invoke();
        }

        /// <summary>What a texture is for (base color, emission, normal map...), so painting can carry on into the same kind on other materials.</summary>
        public string PaintKind(TextureSet set)
        {
            if (m_KindCache.TryGetValue(set, out var kind)) return kind;
            if (IsEmission(set)) kind = "#emission";
            else if (set.linearData && IsNormalProperty(set.property)) kind = "#normal";
            else if (set.cloneMaterial != null && set.property == DefaultProperty(set.cloneMaterial)) kind = "#main";
            else kind = set.property;
            m_KindCache[set] = kind;
            return kind;
        }

        /// <summary>
        /// The textures a stroke, gradient or stars paint: the selected ones and, with "whole model", the same kind of
        /// texture on every other material, so painting carries on across body parts.
        /// </summary>
        public List<TextureSet> PaintTargets(bool wholeModel)
        {
            var result = SelectedSets;
            if (!wholeModel) return result;
            var kinds = new HashSet<string>();
            foreach (var s in result) kinds.Add(PaintKind(s));
            foreach (var s in sets)
                if (!result.Contains(s) && kinds.Contains(PaintKind(s))) result.Add(s);
            return result;
        }
    }
}
