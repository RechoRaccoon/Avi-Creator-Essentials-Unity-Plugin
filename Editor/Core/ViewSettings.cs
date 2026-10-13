using System;
using UnityEditor;
using UnityEngine;

namespace AviCreatorEssentials
{
    public enum LightingPreset
    {
        Studio,
        Soft,
        Flat,
        Dramatic,
        Night,
    }

    public enum ViewLayout
    {
        View3D,
        Split,
        View2D,
    }

    /// <summary>Viewport and workflow preferences, saved in EditorPrefs.</summary>
    [Serializable]
    public class ViewSettings
    {
        const string PrefKey = "AviCreatorEssentials.View.v1";

        public LightingPreset lighting = LightingPreset.Studio;
        public float lightYaw;
        public bool lightFollowsCamera = true;
        public float lightIntensity = 1f;
        public float ambient = 0.4f;
        public Color backgroundTop = new Color(0.27f, 0.28f, 0.32f);
        public Color backgroundBottom = new Color(0.10f, 0.10f, 0.12f);
        public bool emissionOnly;
        public float emissionBaseDim = 0.06f;
        public bool showArmature;
        public float boneSize = 1f;
        public bool humanoidOnly;
        public bool gizmoLocal = true;
        public int displayPadding = 16;
        public bool preferSourceFiles = true;
        public int undoSteps = 40;
        public bool showWire2D = true;
        public bool showAlpha2D = true;
        public bool tile2D;
        public float fov = 30f;
        public ViewLayout layout = ViewLayout.Split;
        public float splitRatio = 0.62f;
        public bool orbitAroundCursor = true;
        public bool showMaskOverlay = true;
        public bool highlightSelection = true;
        public int bakePadding = 32;
        public bool bakeFillAll = true;
        public bool showCursorOnModel = true;
        public float rightPanelWidth = 330f;
        public int theme;                      // 0 follow Unity, 1 dark, 2 light
        public bool linkClothing = true;
        public bool pickerEditsLayer = true;   // the color wheel edits the selected fill/recolor layer       // clothing armatures follow the body like VRCFury's Armature Link

        /// <summary>Resolved at runtime from the theme setting and Unity's own skin.</summary>
        public bool LightTheme => theme == 2 || (theme == 0 && !EditorGUIUtility.isProSkin);

        public static ViewSettings Load()
        {
            var s = new ViewSettings();
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
}
