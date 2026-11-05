using System.Collections.Generic;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Renders text into a transparent Texture2D that becomes an image layer's picture, so a text layer
    /// places, scales, rotates and rasterizes exactly like an image layer — in the 3D viewport and the 2D
    /// texture view, with the Image tool. Uses only UnityEngine font APIs (no TextMeshPro dependency).
    /// Glyphs come from the font's atlas; the tint is applied in gamma space, like other image layers.
    /// Emoji: Unity's built-in font has no colour emoji, so most emoji render as blank/missing glyphs.
    /// Plain symbols that the font does have (♥ ★ ☺ → etc.) render fine.
    /// </summary>
    public static class TextImage
    {
        const int Pad = 8;
        const int MaxSize = 4096;

        static Font s_Font;

        static Font Font
        {
            get
            {
                if (s_Font != null) return s_Font;
                s_Font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (s_Font == null) s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (s_Font == null) s_Font = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Segoe UI", "Helvetica", "sans-serif" }, 64);
                return s_Font;
            }
        }

        struct Glyph
        {
            public Rect dest;   // bottom-up texture pixels
            public Rect src;    // font atlas UVs
        }

        /// <summary>Layer name for a text layer, e.g. Text "hello".</summary>
        public static string LayerName(string text)
        {
            if (string.IsNullOrEmpty(text)) return "Text";
            string first = text.Split('\n')[0].Trim();
            if (first.Length > 24) first = first.Substring(0, 24).TrimEnd() + "…";
            return "Text \"" + first + "\"";
        }

        /// <summary>Renders the text, or null when there is nothing to render.</summary>
        public static Texture2D Render(string text, int fontSize, Color color, bool bold)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var font = Font;
            if (font == null) return null;
            fontSize = Mathf.Clamp(fontSize, 8, 512);
            var style = bold ? FontStyle.Bold : FontStyle.Normal;

            // Lay out, shrinking the font until the text fits in one texture.
            List<Glyph> glyphs = null;
            int w = 0, h = 0;
            int size = fontSize;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                glyphs = Layout(font, text, size, style, out w, out h);
                if (glyphs == null) return null;
                if (w <= MaxSize && h <= MaxSize) break;
                size = Mathf.Max(8, size / 2);
            }
            if (w > MaxSize || h > MaxSize) return null;

            font.RequestCharactersInTexture(text, size, style);
            var rt = APUtil.CreateRT(w, h, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, "text");
            APUtil.Clear(rt, Color.clear);
            var atlas = font.material != null ? font.material.mainTexture : null;
            if (atlas != null)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, w, 0, h);
                foreach (var g in glyphs)
                    Graphics.DrawTexture(g.dest, atlas, g.src);
                GL.PopMatrix();
                RenderTexture.active = prev;
            }
            var tex = APUtil.ReadToTexture(rt, TextureFormat.RGBA32, true);
            APUtil.Release(ref rt);

            // Tint the white glyphs. Pixels are white * alpha, so scaling RGB by the colour keeps the alpha.
            var pixels = tex.GetPixels32();
            byte r = (byte)Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f);
            byte g = (byte)Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f);
            byte b = (byte)Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f);
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.a == 0) continue;
                pixels[i] = new Color32((byte)(p.r * r / 255), (byte)(p.g * g / 255), (byte)(p.b * b / 255), p.a);
            }
            tex.SetPixels32(pixels);
            tex.Apply(false);
            tex.name = LayerName(text);
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        /// <summary>Works out where every glyph goes. Returns null when the text has no visible characters.</summary>
        static List<Glyph> Layout(Font font, string text, int fontSize, FontStyle style, out int width, out int height)
        {
            width = 0;
            height = 0;
            font.RequestCharactersInTexture(text, fontSize, style);
            var lines = text.Split('\n');
            CharacterInfo space;
            font.GetCharacterInfo(' ', out space, fontSize, style);
            float spaceAdvance = space.advance > 0 ? space.advance : fontSize * 0.5f;

            var glyphs = new List<Glyph>();
            var lineHeights = new List<float>();
            float maxWidth = 0f;
            float totalHeight = Pad * 2f;

            // First pass: measure each line (ascent/descent come from the glyphs themselves, so they are
            // right for whatever size is being laid out).
            var perLine = new List<List<Glyph>>();
            foreach (var line in lines)
            {
                var lg = new List<Glyph>();
                float cursorX = Pad;
                float ascent = 0f, descent = 0f;
                foreach (char c in line)
                {
                    if (c == '\r') continue;
                    if (c == '\t')
                    {
                        cursorX += spaceAdvance * 4f;
                        continue;
                    }
                    CharacterInfo info;
                    font.GetCharacterInfo(c, out info, fontSize, style);
                    if (info.advance <= 0 && info.glyphWidth <= 0)
                    {
                        // Missing glyph (an emoji, usually): leave a space-width gap and draw nothing.
                        cursorX += spaceAdvance;
                        continue;
                    }
                    ascent = Mathf.Max(ascent, info.maxY);
                    descent = Mathf.Max(descent, -info.minY);
                    if (info.glyphWidth > 0 && info.glyphHeight > 0)
                    {
                        float u0 = Mathf.Min(info.uvBottomLeft.x, info.uvBottomRight.x);
                        float u1 = Mathf.Max(info.uvBottomLeft.x, info.uvBottomRight.x);
                        float v0 = Mathf.Min(info.uvBottomLeft.y, info.uvTopLeft.y);
                        float v1 = Mathf.Max(info.uvBottomLeft.y, info.uvTopLeft.y);
                        // Flipped glyphs are stored upside down in the atlas; flip the v mapping back.
                        var src = info.flipped
                            ? new Rect(u0, v1, u1 - u0, v0 - v1)
                            : new Rect(u0, v0, u1 - u0, v1 - v0);
                        lg.Add(new Glyph
                        {
                            // dest y is filled in on the second pass, once the line tops are known.
                            dest = new Rect(cursorX + info.bearing, 0f, info.glyphWidth, info.glyphHeight),
                            src = src,
                        });
                        // Stash the glyph's top (relative to the line's baseline) in dest.y for now.
                        var last = lg[lg.Count - 1];
                        last.dest = new Rect(last.dest.x, info.maxY, last.dest.width, last.dest.height);
                        lg[lg.Count - 1] = last;
                    }
                    cursorX += info.advance;
                }
                float lineHeight = ascent + descent + fontSize * 0.1f;
                lineHeights.Add(lineHeight);
                maxWidth = Mathf.Max(maxWidth, cursorX - Pad);
                totalHeight += lineHeight;
                perLine.Add(lg);
            }

            width = Mathf.Max(1, Mathf.CeilToInt(maxWidth + Pad * 2f));
            height = Mathf.Max(1, Mathf.CeilToInt(totalHeight));

            // Second pass: place each line's glyphs. Line 0 is at the top; texture y runs bottom-up.
            float yTop = Pad;
            for (int li = 0; li < perLine.Count; li++)
            {
                float lineHeight = lineHeights[li];
                // Recompute this line's ascent (the maxY values were stashed in dest.y).
                float ascent = 0f;
                foreach (var g in perLine[li]) ascent = Mathf.Max(ascent, g.dest.y);
                float baselineFromTop = yTop + ascent;
                foreach (var g in perLine[li])
                {
                    float glyphTop = baselineFromTop - g.dest.y;
                    float destY = height - (glyphTop + g.dest.height);
                    glyphs.Add(new Glyph { dest = new Rect(g.dest.x, destY, g.dest.width, g.dest.height), src = g.src });
                }
                yTop += lineHeight;
            }
            return glyphs.Count > 0 ? glyphs : null;
        }
    }
}
