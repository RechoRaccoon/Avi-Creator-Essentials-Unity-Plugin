using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    public enum LayerKind
    {
        Paint,
        Fill,
        Image,
    }

    /// <summary>Order must match AP_Blend in AP_Composite.shader.</summary>
    public enum LayerBlendMode
    {
        Normal,
        Multiply,
        Screen,
        Overlay,
        SoftLight,
        HardLight,
        Darken,
        Lighten,
        ColorDodge,
        ColorBurn,
        Add,
        Subtract,
        Difference,
        Hue,
        Saturation,
        Color,
        Luminosity,
    }

    /// <summary>Where and how an image layer's picture sits on the texture or model.</summary>
    [Serializable]
    public class ImagePlacement
    {
        public bool projected;          // false: placed in texture (UV) space, true: projected onto the model
        public bool flipX;
        public bool clipToIslands = true;

        // UV-space placement
        public Vector2 uvCenter = new Vector2(0.5f, 0.5f);
        public Vector2 uvSize = new Vector2(0.5f, 0.5f);
        public float rotation;          // degrees, both modes

        // projected placement
        public Vector3 worldPos;
        public Vector3 normal = Vector3.forward;
        public Vector3 up = Vector3.up;
        public float worldWidth = 0.1f;
        public float depth = 0.05f;     // half thickness of the projection box
        public float angleLimit = 80f;  // faces turned further than this from the projection do not receive the image

        [NonSerialized] public Texture2D image;
        public string imageFile;        // file name inside the project folder when saved

        public float Aspect => image != null && image.width > 0 ? image.height / (float)image.width : 1f;

        public ImagePlacement Clone()
        {
            var c = (ImagePlacement)MemberwiseClone();
            return c;
        }
    }

    public class PaintLayer
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "Layer";
        public LayerKind kind = LayerKind.Paint;
        public bool visible = true;
        public float opacity = 1f;
        public LayerBlendMode blend = LayerBlendMode.Normal;
        public bool lockAlpha;
        public Color fillColor = Color.white;    // gamma
        public RenderTexture content;            // paint and image layers
        public RenderTexture mask;               // optional
        public bool maskEnabled = true;
        public ImagePlacement placement;         // image layers

        public bool HasMask => mask != null;
        public bool MaskActive => mask != null && maskEnabled;

        public long Bytes => APUtil.EstimateBytes(content) + APUtil.EstimateBytes(mask);

        public void Release()
        {
            APUtil.Release(ref content);
            APUtil.Release(ref mask);
            if (placement != null && placement.image != null)
            {
                Object.DestroyImmediate(placement.image);
                placement.image = null;
            }
        }
    }

    /// <summary>A renderer slot (renderer + submesh) that samples a texture set.</summary>
    public struct SetUser
    {
        public int renderer;
        public int submesh;
        public int materialSlot;
    }

    /// <summary>
    /// One paintable texture: a (material, texture property) pair, shared by every renderer slot using that material.
    /// Holds its own layer stack.
    /// </summary>
    public class TextureSet
    {
        public string displayName;
        public Material originalMaterial;          // asset on the scene avatar
        public Material cloneMaterial;             // copy used by the preview clone
        public string property = "_MainTex";
        public Texture originalTexture;            // value of the property at session start
        public bool createdProperty;               // texture slot was empty (e.g. new emission map)
        public readonly List<SetUser> users = new List<SetUser>();

        public int width = 2048;
        public int height = 2048;
        public int uvChannel;
        public bool linearData;                    // normal maps, masks... stored and displayed without color conversion

        public bool initialized;
        public readonly List<PaintLayer> layers = new List<PaintLayer>();
        public int activeLayer;
        public bool editingMask;

        public RenderTexture display;              // what the clone material samples
        public RenderTexture compA;
        public RenderTexture compB;
        public RenderTexture island;               // UV coverage of all users
        public RenderTexture seed;                 // nearest covered texel, for seam padding
        public RenderTexture lastComposite;        // compA or compB: the padded composite (gamma data)
        public bool dirty = true;
        public bool islandDirty = true;
        public int viewMode;                       // 0 normal, 1 show active mask

        // UV selection: painting is limited to these triangles while any are selected.
        // Key = renderer entry index << 8 | submesh; value = first index of each selected triangle.
        public readonly Dictionary<int, HashSet<int>> selection = new Dictionary<int, HashSet<int>>();
        public RenderTexture selectionRT;
        public bool selectionDirty;

        public static int SelKey(int entry, int submesh) => (entry << 8) | (submesh & 0xFF);

        public int SelectedCount
        {
            get
            {
                int n = 0;
                foreach (var kv in selection) n += kv.Value.Count;
                return n;
            }
        }

        public bool HasSelection
        {
            get
            {
                foreach (var kv in selection)
                    if (kv.Value.Count > 0) return true;
                return false;
            }
        }

        public Bounds worldBounds;

        public string Key => MakeKey(originalMaterial, property);

        public static string MakeKey(Material m, string property)
        {
            return (m != null ? m.GetInstanceID().ToString() : "null") + "|" + property;
        }

        public PaintLayer ActiveLayer =>
            activeLayer >= 0 && activeLayer < layers.Count ? layers[activeLayer] : null;

        public GraphicsFormat DisplayFormat =>
            linearData || !APUtil.LinearProject ? GraphicsFormat.R8G8B8A8_UNorm : GraphicsFormat.R8G8B8A8_SRGB;

        public long Bytes
        {
            get
            {
                long b = APUtil.EstimateBytes(display) + APUtil.EstimateBytes(compA) + APUtil.EstimateBytes(compB) +
                         APUtil.EstimateBytes(island) + APUtil.EstimateBytes(seed);
                foreach (var l in layers) b += l.Bytes;
                return b;
            }
        }

        public int IndexOf(PaintLayer layer) => layers.IndexOf(layer);

        public void ReleaseBuffers()
        {
            APUtil.Release(ref display);
            APUtil.Release(ref compA);
            APUtil.Release(ref compB);
            APUtil.Release(ref island);
            APUtil.Release(ref seed);
            APUtil.Release(ref selectionRT);
            selectionDirty = true;
            lastComposite = null;
        }

        public void ReleaseAll()
        {
            foreach (var l in layers) l.Release();
            layers.Clear();
            ReleaseBuffers();
            initialized = false;
        }
    }
}
