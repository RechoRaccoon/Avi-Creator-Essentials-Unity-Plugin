using System.Collections.Generic;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Renders text into a transparent Texture2D that becomes an image layer's picture, so a text layer
    /// places, scales, rotates and rasterizes exactly like an image layer — in the 3D viewport and the 2D
    /// texture view, with the Image tool. Uses only UnityEngine font APIs (no TextMeshPro dependency).
    /// Glyphs come from the font's atlas; the tint is applied in gamma space, like other image layers.
    /// Emoji are drawn as colour pictures from EmojiArt (Twemoji PNGs, downloaded once and cached),
    /// composited on top untinted — Unity's built-in font has no colour emoji, so the font alone would
    /// leave them as blank boxes. Plain symbols the font does have (♥ ★ ☺ → etc.) render from the font.
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
                // LegacyRuntime.ttf first: in this Unity version Resources.GetBuiltinResource<Font>("Arial.ttf")
                // throws ArgumentException ("Arial.ttf is no longer a valid built in font") instead of
                // returning null, so an Arial-first lookup never falls through — the exception escapes
                // Render, spams the console on every repaint, and the preview stays blank. Every lookup
                // below is guarded so the font search can never throw.
                s_Font = TryBuiltinFont("LegacyRuntime.ttf");
                if (s_Font == null) s_Font = TryBuiltinFont("Arial.ttf");
                if (s_Font == null) s_Font = TryOSFont();
                return s_Font;
            }
        }

        static Font TryBuiltinFont(string name)
        {
            try { return Resources.GetBuiltinResource<Font>(name); }
            catch (System.Exception) { return null; }
        }

        static Font TryOSFont()
        {
            try { return Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Segoe UI", "Helvetica", "sans-serif" }, 64); }
            catch (System.Exception) { return null; }
        }

        struct Glyph
        {
            public Rect dest;   // bottom-up texture pixels
            public Rect src;    // font atlas UVs
            public bool isEmoji;
            public string codepoints; // Twemoji file key, when isEmoji
        }

        struct EmojiGlyph
        {
            public Rect dest;   // bottom-up texture pixels
            public string codepoints;
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
            List<EmojiGlyph> emojis = null;
            int w = 0, h = 0;
            int size = fontSize;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                glyphs = Layout(font, text, size, style, out w, out h, out emojis);
                if (glyphs == null) return null;
                if (w <= MaxSize && h <= MaxSize) break;
                size = Mathf.Max(8, size / 2);
            }
            if (w > MaxSize || h > MaxSize) return null;

            // No second RequestCharactersInTexture here on purpose: Layout already requested
            // these exact characters at this size/style before capturing the glyph UVs, and
            // requesting again between capture and draw could regenerate the font texture and
            // invalidate the UVs Layout just captured (see Font.RequestCharactersInTexture docs).
            var rt = APUtil.CreateRT(w, h, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, "text");
            APUtil.Clear(rt, Color.clear);
            var atlas = font.material != null ? font.material.mainTexture : null;
            if (atlas != null)
            {
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, w, 0, h);
                foreach (var glyph in glyphs)
                    Graphics.DrawTexture(glyph.dest, atlas, glyph.src, 0, 0, 0, 0);
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

            // Emoji pictures go on top, untinted — they are already coloured — alpha-blended over the glyphs.
            foreach (var e in emojis)
            {
                var art = EmojiArt.Get(e.codepoints);
                if (art == null) continue; // not downloaded yet; the text window re-renders once it lands
                var src = art.GetPixels32();
                int sw = art.width, sh = art.height;
                int dx = Mathf.RoundToInt(e.dest.x), dy = Mathf.RoundToInt(e.dest.y);
                int dw = Mathf.RoundToInt(e.dest.width), dh = Mathf.RoundToInt(e.dest.height);
                if (dw <= 0 || dh <= 0) continue;
                for (int y = 0; y < dh; y++)
                {
                    int py = dy + y;
                    if (py < 0 || py >= h) continue;
                    for (int x = 0; x < dw; x++)
                    {
                        int px = dx + x;
                        if (px < 0 || px >= w) continue;
                        float u = (x + 0.5f) / dw * sw - 0.5f;
                        float v = (y + 0.5f) / dh * sh - 0.5f;
                        var sp = SampleBilinear(src, sw, sh, u, v);
                        if (sp.a == 0) continue;
                        int idx = py * w + px;
                        var dp = pixels[idx];
                        float a = sp.a / 255f;
                        pixels[idx] = new Color32(
                            (byte)(sp.r * a + dp.r * (1f - a)),
                            (byte)(sp.g * a + dp.g * (1f - a)),
                            (byte)(sp.b * a + dp.b * (1f - a)),
                            (byte)Mathf.Max(dp.a, sp.a));
                    }
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false);
            tex.name = LayerName(text);
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        static Color32 SampleBilinear(Color32[] src, int w, int h, float u, float v)
        {
            u = Mathf.Clamp(u, 0f, w - 1f);
            v = Mathf.Clamp(v, 0f, h - 1f);
            int x0 = Mathf.FloorToInt(u), y0 = Mathf.FloorToInt(v);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float fx = u - x0, fy = v - y0;
            return LerpColor(LerpColor(src[y0 * w + x0], src[y0 * w + x1], fx),
                             LerpColor(src[y1 * w + x0], src[y1 * w + x1], fx), fy);
        }

        static Color32 LerpColor(Color32 a, Color32 b, float t) => new Color32(
            (byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t),
            (byte)(a.b + (b.b - a.b) * t), (byte)(a.a + (b.a - a.a) * t));

        /// <summary>Works out where every glyph goes. Returns null when the text has no visible characters.
        /// Emoji graphemes come back in <paramref name="emojis"/> as picture slots, not font glyphs.</summary>
        static List<Glyph> Layout(Font font, string text, int fontSize, FontStyle style, out int width, out int height, out List<EmojiGlyph> emojis)
        {
            width = 0;
            height = 0;
            emojis = new List<EmojiGlyph>();
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
                for (int ci = 0; ci < line.Length;)
                {
                    // Emoji first: they are drawn as pictures, not font glyphs.
                    if (EmojiArt.TryReadRun(line, ci, out int runLen, out string key))
                    {
                        float side = fontSize;
                        float top = fontSize * 0.8f; // picture sits on the baseline like a capital
                        ascent = Mathf.Max(ascent, top);
                        descent = Mathf.Max(descent, side - top);
                        lg.Add(new Glyph
                        {
                            // dest y is filled in on the second pass, once the line tops are known.
                            dest = new Rect(cursorX, top, side, side),
                            src = new Rect(),
                            isEmoji = true,
                            codepoints = key,
                        });
                        cursorX += side;
                        ci += runLen;
                        continue;
                    }
                    char c = line[ci];
                    ci++;
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
                        // Missing glyph: leave a space-width gap and draw nothing.
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
                        // Flipped glyphs are stored rotated 180 degrees in the atlas: Unity flips
                        // BOTH the U and V coordinates "to make more efficient use of texture space",
                        // so the source rect must run backwards on both axes, not just V. The old code
                        // only ran V backwards, which mirrors every flipped glyph horizontally.
                        // With Arial's private, freshly-packed atlas almost no glyph was flipped, so
                        // it looked right; LegacyRuntime shares its atlas with the whole editor UI, so
                        // many glyphs come back flipped and the preview showed wrong / mirrored
                        // letters ("random letters"). DrawTexture already honours a negative-height
                        // source rect for the V flip, so a negative width is the same mechanism.
                        bool flipped = info.uvBottomLeft.y > info.uvTopLeft.y;
                        var src = flipped
                            ? new Rect(u1, v1, u0 - u1, v0 - v1)
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
                    if (g.isEmoji)
                        emojis.Add(new EmojiGlyph { dest = new Rect(g.dest.x, destY, g.dest.width, g.dest.height), codepoints = g.codepoints });
                    else
                        glyphs.Add(new Glyph { dest = new Rect(g.dest.x, destY, g.dest.width, g.dest.height), src = g.src });
                }
                yTop += lineHeight;
            }
            return glyphs.Count > 0 || emojis.Count > 0 ? glyphs : null;
        }
    }
}
