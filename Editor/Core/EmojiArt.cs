using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Colour emoji as pictures. Unity's built-in font has no colour emoji, so emoji in a text layer
    /// are drawn from Twemoji PNGs (Twemoji by Twitter, CC-BY 4.0 — see Attribution) instead of the
    /// font atlas: one 72x72 picture per emoji, downloaded once from the Twemoji CDN and cached in
    /// the project's Library folder, so the text window's live preview and the final layer on the
    /// model both show real colour emoji. No emoji are bundled with the plugin, so it stays small;
    /// offline, an emoji that has never been downloaded yet just leaves a blank gap (as before).
    /// </summary>
    public static class EmojiArt
    {
        public const string Attribution = "Emoji art: Twemoji by Twitter, CC-BY 4.0.";
        const string Cdn = "https://cdn.jsdelivr.net/gh/twitter/twemoji@14.0.2/assets/72x72/";
        const string Fallback = "https://raw.githubusercontent.com/twitter/twemoji/master/assets/72x72/";

        /// <summary>One emoji grapheme in a string: its UTF-16 span and its Twemoji file key.</summary>
        public struct Run
        {
            public int start;       // UTF-16 index in the string
            public int length;      // UTF-16 length of the whole grapheme (ZWJ sequences included)
            public string codepoints; // Twemoji file name without ".png", e.g. "1f600" or "1f468-200d-1f469-200d-1f466"
        }

        static readonly Dictionary<string, Texture2D> s_Memory = new Dictionary<string, Texture2D>();
        static readonly HashSet<string> s_Failed = new HashSet<string>();
        static readonly HashSet<string> s_Pending = new HashSet<string>();
        static readonly List<Action> s_Waiting = new List<Action>();

        static string CacheDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "AviCreatorEssentialsEmoji"));
        static string DiskPath(string key) => Path.Combine(CacheDir, key + ".png");

        /// <summary>The cached picture for an emoji key, or null when it has not been downloaded yet.</summary>
        public static Texture2D Get(string key)
        {
            if (key == null) return null;
            Texture2D t;
            return s_Memory.TryGetValue(key, out t) ? t : null;
        }

        /// <summary>How many emoji in the text are still downloading (failed ones don't count —
        /// they stay blank, but must not block adding the layer).</summary>
        public static int NotReadyCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 0;
            foreach (var r in FindRuns(text))
                if (!s_Memory.ContainsKey(r.codepoints) && !s_Failed.Contains(r.codepoints)) n++;
            return n;
        }

        /// <summary>True when at least one emoji in the text failed to download (offline, usually).</summary>
        public static bool HasFailures(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var r in FindRuns(text))
                if (s_Failed.Contains(r.codepoints)) return true;
            return false;
        }

        /// <summary>Let failed downloads be tried again (used by the text window's retry button).</summary>
        public static void RetryFailed() => s_Failed.Clear();

        /// <summary>Start downloading every emoji in the text that is not cached yet, then call
        /// onReady once they are all in (or have failed). Calls onReady at once when there is
        /// nothing new to fetch.</summary>
        public static void Prepare(string text, Action onReady)
        {
            bool anyNew = false;
            foreach (var r in FindRuns(text))
            {
                string key = r.codepoints;
                if (s_Memory.ContainsKey(key) || s_Failed.Contains(key) || s_Pending.Contains(key)) continue;
                string disk = DiskPath(key);
                if (File.Exists(disk))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(disk);
                        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (t.LoadImage(bytes))
                        {
                            t.wrapMode = TextureWrapMode.Clamp;
                            s_Memory[key] = t;
                            continue;
                        }
                        UnityEngine.Object.DestroyImmediate(t);
                    }
                    catch { /* corrupt cache file: fall through to downloading it again */ }
                }
                anyNew = true;
                s_Pending.Add(key);
                StartDownload(key);
            }
            if (!anyNew)
            {
                onReady?.Invoke();
                return;
            }
            if (onReady != null) s_Waiting.Add(onReady);
        }

        static void CheckDone()
        {
            if (s_Pending.Count > 0) return;
            var waiting = s_Waiting.ToArray();
            s_Waiting.Clear();
            foreach (var cb in waiting) cb?.Invoke();
        }

        static void StartDownload(string key)
        {
            Download(key, Cdn + key + ".png", () => Download(key, Fallback + key + ".png", null));
        }

        static void Download(string key, string url, Action onFallback)
        {
            var req = UnityWebRequestTexture.GetTexture(url);
            var op = req.SendWebRequest();
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!op.isDone) return;
                EditorApplication.update -= poll;
                var tex = req.result == UnityWebRequest.Result.Success ? DownloadHandlerTexture.GetContent(req) : null;
                if (tex == null && onFallback != null)
                {
                    req.Dispose();
                    onFallback();
                    return;
                }
                if (tex != null)
                {
                    tex.wrapMode = TextureWrapMode.Clamp;
                    s_Memory[key] = tex;
                    try
                    {
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllBytes(DiskPath(key), req.downloadHandler.data);
                    }
                    catch { /* cache is a nice-to-have; the picture is already in memory */ }
                }
                else s_Failed.Add(key);
                req.Dispose();
                s_Pending.Remove(key);
                CheckDone();
            };
            EditorApplication.update += poll;
        }

        /// <summary>Every emoji grapheme in the text, in order.</summary>
        public static List<Run> FindRuns(string text)
        {
            var runs = new List<Run>();
            if (string.IsNullOrEmpty(text)) return runs;
            int i = 0;
            while (i < text.Length)
            {
                if (TryReadRun(text, i, out int len, out string key))
                {
                    runs.Add(new Run { start = i, length = len, codepoints = key });
                    i += len;
                }
                else i += NextCodePoint(text, i, out int l);
            }
            return runs;
        }

        /// <summary>True when an emoji grapheme starts at index: its UTF-16 length and Twemoji key.</summary>
        public static bool TryReadRun(string s, int index, out int length, out string codepoints)
        {
            length = 0;
            codepoints = null;
            if (index < 0 || index >= s.Length) return false;
            int cp = NextCodePoint(s, index, out int len);
            int pos = index + len;

            // Flags: two regional indicators.
            if (cp >= 0x1F1E6 && cp <= 0x1F1FF && pos < s.Length)
            {
                int cp2 = NextCodePoint(s, pos, out int len2);
                if (cp2 >= 0x1F1E6 && cp2 <= 0x1F1FF)
                {
                    length = len + len2;
                    codepoints = Hex(cp) + "-" + Hex(cp2);
                    return true;
                }
            }
            // Keycaps: digit / # / * followed by variation selector + combining enclosing keycap.
            if ((cp >= '0' && cp <= '9') || cp == '#' || cp == '*')
            {
                int p = pos;
                if (p < s.Length && NextCodePoint(s, p, out int l0) == 0xFE0F) p += l0;
                if (p < s.Length && NextCodePoint(s, p, out int l1) == 0x20E3)
                {
                    length = p + l1 - index;
                    codepoints = Hex(cp) + "-20e3";
                    return true;
                }
            }
            if (!IsEmojiBase(cp)) return false;

            // Build the grapheme: optional variation selector / skin tone, then ZWJ-joined parts.
            var parts = new List<int> { cp };
            pos = ConsumeModifiers(s, pos, parts);
            while (pos < s.Length && NextCodePoint(s, pos, out int zwLen) == 0x200D)
            {
                int afterZwj = pos + zwLen;
                if (afterZwj >= s.Length) break;
                int next = NextCodePoint(s, afterZwj, out int nextLen);
                if (!IsEmojiBase(next)) break;
                parts.Add(next);
                pos = ConsumeModifiers(s, afterZwj + nextLen, parts);
            }
            length = pos - index;
            codepoints = string.Join("-", parts.ConvertAll(Hex).ToArray());
            return true;
        }

        // Consumes a variation selector (FE0F) and/or a skin-tone modifier after a base emoji.
        // The variation selector is not part of the Twemoji file name; skin tones are.
        static int ConsumeModifiers(string s, int pos, List<int> parts)
        {
            if (pos < s.Length && NextCodePoint(s, pos, out int l0) == 0xFE0F) pos += l0;
            if (pos < s.Length)
            {
                int cp = NextCodePoint(s, pos, out int l1);
                if (cp >= 0x1F3FB && cp <= 0x1F3FF) { parts.Add(cp); pos += l1; }
            }
            return pos;
        }

        static bool IsEmojiBase(int cp)
        {
            if (cp >= 0x1F000 && cp <= 0x1FAFF) return true;   // emoticons, people, animals, food, activities…
            if (cp >= 0x2600 && cp <= 0x27BF) return true;     // misc symbols, dingbats (☀ ♥ ★ ☺ …)
            if (cp >= 0x2190 && cp <= 0x21FF) return true;     // arrows
            if (cp >= 0x2300 && cp <= 0x23FF) return true;     // technical (⏰ ⏱ …)
            if (cp >= 0x2B00 && cp <= 0x2BFF) return true;     // stars, arrows (⭐ ⬛ …)
            return cp == 0x203C || cp == 0x2049 || cp == 0x00A9 || cp == 0x00AE ||
                   cp == 0x3030 || cp == 0x303D || cp == 0x3297 || cp == 0x3299;
        }

        static int NextCodePoint(string s, int i, out int len)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                len = 2;
                return char.ConvertToUtf32(c, s[i + 1]);
            }
            len = 1;
            return c;
        }

        static string Hex(int cp) => cp.ToString("x");
    }
}
