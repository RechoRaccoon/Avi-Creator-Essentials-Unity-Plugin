using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    [Serializable]
    class ProjectData
    {
        public int version = 1;
        public string avatarName;
        public string savedAt;
        public string activeSet;
        // Templates: a project saved as a reusable layout. The same file format,
        // just flagged and stored under _Templates instead of the avatar's folder.
        public bool isTemplate;
        public string templateName;
        public List<SetData> sets = new List<SetData>();
        public List<PoseBone> pose = new List<PoseBone>();
    }

    [Serializable]
    class SetData
    {
        public string materialGuid;
        public long materialLocalId;
        public string materialName;
        public string rendererPath;
        public int materialSlot;
        public string property;
        public int width;
        public int height;
        public int uvChannel;
        public int activeLayer;
        public string mirrorSource; // SetLabel of the source set, or null/empty: live mirror link
        // Live mirror: flip the mirrored image horizontally. Defaults to true
        // (JsonUtility keeps this initializer value when an older save has no
        // such field, so existing links keep flipping after the update).
        public bool mirrorFlip = true;
        // Hidden from the painter (see TextureSet.hidden). Defaults to false,
        // so older saves load with every material visible.
        public bool hidden;
        public List<LayerData> layers = new List<LayerData>();
    }

    [Serializable]
    class LayerData
    {
        public string id;
        public string name;
        public int kind;
        public bool visible = true;
        public float opacity = 1f;
        public int blend;
        public bool lockAlpha;
        public Color fillColor = Color.white;
        public string contentFile;
        public string maskFile;
        public bool maskEnabled = true;
        public bool hasPlacement;
        public ImagePlacement placement;
        public int adjMode;
        public bool clipBelow = true;
        public Color adjColor = Color.white;
        public float hue, saturation, lightness;
        public Vector4 srcMean, srcStd, refMean, refStd;
        public bool refValid;
        public float[] srcHist, refHist;
    }

    public class ProjectInfo
    {
        public string folder;
        public string avatarName;
        public DateTime savedAt;
        public int setCount;
        public bool recovery;
        public bool isTemplate;
        public string templateName;
    }

    /// <summary>Saving and loading layered projects. Projects live outside Assets so Unity never imports them.</summary>
    public static class ProjectIO
    {
        public const string FileName = "project.json";

        public static string DefaultFolder(string avatarName) =>
            Path.Combine(APUtil.ProjectsRoot, APUtil.SafeFileName(avatarName));

        public static string RecoveryFolder(string avatarName) =>
            Path.Combine(APUtil.ProjectsRoot, "_Recovery", APUtil.SafeFileName(avatarName));

        public static List<ProjectInfo> List()
        {
            var list = new List<ProjectInfo>();
            void Scan(string root, bool recovery)
            {
                if (!Directory.Exists(root)) return;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string file = Path.Combine(dir, FileName);
                    if (!File.Exists(file)) continue;
                    try
                    {
                        var data = JsonUtility.FromJson<ProjectData>(File.ReadAllText(file));
                        if (data.isTemplate) continue;
                        DateTime.TryParse(data.savedAt, out var when);
                        list.Add(new ProjectInfo
                        {
                            folder = dir,
                            avatarName = data.avatarName,
                            savedAt = when,
                            setCount = data.sets.Count,
                            recovery = recovery,
                        });
                    }
                    catch (Exception)
                    {
                        // ignore unreadable projects
                    }
                }
            }
            Scan(APUtil.ProjectsRoot, false);
            Scan(Path.Combine(APUtil.ProjectsRoot, "_Recovery"), true);
            list.Sort((a, b) => b.savedAt.CompareTo(a.savedAt));
            return list;
        }

        // ------------------------------------------------------------------ templates
        //
        // A template is a saved project kept as a reusable layout: the same file
        // format, stored under _Templates so it never shows up in "Saved projects".
        // Applying one copies its layer stacks onto another avatar's matching
        // materials (same material names) — the target avatar's own project file
        // is never touched, only its open session.

        public static string TemplatesRoot => Path.Combine(APUtil.ProjectsRoot, "_Templates");

        public static string TemplateFolder(string name) =>
            Path.Combine(TemplatesRoot, APUtil.SafeFileName(name));

        public static List<ProjectInfo> ListTemplates()
        {
            var list = new List<ProjectInfo>();
            string root = TemplatesRoot;
            if (!Directory.Exists(root)) return list;
            foreach (var dir in Directory.GetDirectories(root))
            {
                string file = Path.Combine(dir, FileName);
                if (!File.Exists(file)) continue;
                try
                {
                    var data = JsonUtility.FromJson<ProjectData>(File.ReadAllText(file));
                    DateTime.TryParse(data.savedAt, out var when);
                    list.Add(new ProjectInfo
                    {
                        folder = dir,
                        avatarName = data.avatarName,
                        savedAt = when,
                        setCount = data.sets.Count,
                        isTemplate = true,
                        templateName = string.IsNullOrEmpty(data.templateName) ? data.avatarName : data.templateName,
                    });
                }
                catch (Exception)
                {
                    // ignore unreadable templates
                }
            }
            list.Sort((a, b) => b.savedAt.CompareTo(a.savedAt));
            return list;
        }

        /// <summary>Saves the open session's full layer stacks as a named template. Returns its folder.</summary>
        public static string SaveAsTemplate(APSession s, string name)
        {
            string folder = TemplateFolder(name);
            Save(s, folder, true, true, name);
            return folder;
        }

        public static string ReadTemplateName(string folder)
        {
            try
            {
                var data = JsonUtility.FromJson<ProjectData>(File.ReadAllText(Path.Combine(folder, FileName)));
                return string.IsNullOrEmpty(data.templateName) ? data.avatarName : data.templateName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ save

        public static void Save(APSession s, string folder, bool showProgress = true, bool asTemplate = false, string templateName = null)
        {
            Directory.CreateDirectory(folder);
            var data = new ProjectData
            {
                avatarName = s.avatarName,
                savedAt = DateTime.Now.ToString("o"),
                activeSet = s.ActiveSet != null ? SetLabel(s.ActiveSet) : null,
                isTemplate = asTemplate,
                templateName = templateName,
                pose = s.pose.ToPoseBones(),
            };
            var written = new HashSet<string> { FileName };
            int si = 0;
            try
            {
                foreach (var set in s.sets)
                {
                    if (!set.initialized) { si++; continue; }
                    if (showProgress)
                        EditorUtility.DisplayProgressBar("Avi Creator Essentials", "Saving " + set.displayName, si / (float)Mathf.Max(1, s.sets.Count));
                    var sd = new SetData
                    {
                        materialName = set.originalMaterial != null ? set.originalMaterial.name : "",
                        property = set.property,
                        width = set.width,
                        height = set.height,
                        uvChannel = set.uvChannel,
                        activeLayer = set.activeLayer,
                        mirrorSource = set.mirrorSource != null ? SetLabel(set.mirrorSource) : null,
                        mirrorFlip = set.mirrorFlip,
                        hidden = set.hidden,
                    };
                    if (set.originalMaterial != null &&
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(set.originalMaterial, out string guid, out long localId))
                    {
                        sd.materialGuid = guid;
                        sd.materialLocalId = localId;
                    }
                    if (set.users.Count > 0)
                    {
                        var u = set.users[0];
                        sd.rendererPath = s.meshes.entries[u.renderer].path;
                        sd.materialSlot = u.materialSlot;
                    }
                    int li = 0;
                    foreach (var layer in set.layers)
                    {
                        var ld = new LayerData
                        {
                            id = layer.id,
                            name = layer.name,
                            kind = (int)layer.kind,
                            visible = layer.visible,
                            opacity = layer.opacity,
                            blend = (int)layer.blend,
                            lockAlpha = layer.lockAlpha,
                            fillColor = layer.fillColor,
                            maskEnabled = layer.maskEnabled,
                            adjMode = (int)layer.adjMode,
                            clipBelow = layer.clipBelow,
                            adjColor = layer.adjColor,
                            hue = layer.hue,
                            saturation = layer.saturation,
                            lightness = layer.lightness,
                            srcMean = layer.srcMean,
                            srcStd = layer.srcStd,
                            refMean = layer.refMean,
                            refStd = layer.refStd,
                            refValid = layer.refValid,
                            srcHist = layer.srcHist,
                            refHist = layer.refHist,
                        };
                        string prefix = "s" + si + "_l" + li + "_";
                        if (layer.content != null && layer.kind != LayerKind.Image)
                        {
                            ld.contentFile = prefix + "content.png";
                            File.WriteAllBytes(Path.Combine(folder, ld.contentFile), APUtil.EncodePNG(layer.content));
                            written.Add(ld.contentFile);
                        }
                        if (layer.mask != null)
                        {
                            ld.maskFile = prefix + "mask.png";
                            File.WriteAllBytes(Path.Combine(folder, ld.maskFile), EncodeMask(layer.mask));
                            written.Add(ld.maskFile);
                        }
                        if (layer.placement != null)
                        {
                            ld.hasPlacement = true;
                            ld.placement = layer.placement.Clone();
                            if (layer.placement.image != null)
                            {
                                ld.placement.imageFile = prefix + "image.png";
                                File.WriteAllBytes(Path.Combine(folder, ld.placement.imageFile), EncodeTexture(layer.placement.image));
                                written.Add(ld.placement.imageFile);
                            }
                        }
                        sd.layers.Add(ld);
                        li++;
                    }
                    data.sets.Add(sd);
                    si++;
                }
                File.WriteAllText(Path.Combine(folder, FileName), JsonUtility.ToJson(data, true));
                // Remove files of layers that no longer exist.
                foreach (var f in Directory.GetFiles(folder, "*.png"))
                    if (!written.Contains(Path.GetFileName(f))) File.Delete(f);
            }
            finally
            {
                if (showProgress) EditorUtility.ClearProgressBar();
            }
        }

        static string SetLabel(TextureSet set) =>
            (set.originalMaterial != null ? set.originalMaterial.name : "") + "|" + set.property;

        static byte[] EncodeMask(RenderTexture mask)
        {
            var tmp = APUtil.CreateRT(mask.width, mask.height, GraphicsFormat.R8G8B8A8_UNorm, "maskexport");
            var comp = APResources.Composite;
            comp.SetFloat(APIds.CopyMode, 1f);
            comp.SetFloat(APIds.SrcToGamma, 0f);
            var prev = RenderTexture.active;
            Graphics.Blit(mask, tmp, comp, CompPass.Copy);
            RenderTexture.active = prev;
            var bytes = APUtil.EncodePNG(tmp);
            APUtil.Release(ref tmp);
            return bytes;
        }

        static byte[] EncodeTexture(Texture2D tex)
        {
            if (tex.isReadable && tex.format == TextureFormat.RGBA32) return tex.EncodeToPNG();
            var rt = APUtil.CreateRT(tex.width, tex.height, GraphicsFormat.R8G8B8A8_UNorm, "imgexport");
            APUtil.Copy(tex, rt);
            var bytes = APUtil.EncodePNG(rt);
            APUtil.Release(ref rt);
            return bytes;
        }

        // ------------------------------------------------------------------ load

        public static string ReadAvatarName(string folder)
        {
            try
            {
                var data = JsonUtility.FromJson<ProjectData>(File.ReadAllText(Path.Combine(folder, FileName)));
                return data.avatarName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Loads a project into an open session. Returns a list of warnings.</summary>
        public static List<string> Load(APSession s, string folder)
        {
            var warnings = new List<string>();
            var data = JsonUtility.FromJson<ProjectData>(File.ReadAllText(Path.Combine(folder, FileName)));
            s.history.Clear();
            try
            {
                int si = 0;
                foreach (var sd in data.sets)
                {
                    EditorUtility.DisplayProgressBar("Avi Creator Essentials", "Loading " + sd.materialName, si++ / (float)Mathf.Max(1, data.sets.Count));
                    var set = FindSet(s, sd);
                    if (set == null)
                    {
                        warnings.Add("Material '" + sd.materialName + "' (" + sd.property + ") was not found on this avatar.");
                        continue;
                    }
                    LoadSetLayers(s, sd, folder, set, false);
                }
                // Live mirror links, resolved after every set exists (a source
                // set may be saved after its target). Links to a missing set are
                // dropped with a warning, never left dangling.
                foreach (var sd in data.sets)
                {
                    if (string.IsNullOrEmpty(sd.mirrorSource)) continue;
                    var target = FindSet(s, sd);
                    TextureSet source = null;
                    foreach (var cand in s.sets)
                        if (SetLabel(cand) == sd.mirrorSource) { source = cand; break; }
                    if (target == null || source == null)
                    {
                        if (target != null) warnings.Add("Live mirror link for '" + sd.materialName + "' was dropped: its source material was not found.");
                        continue;
                    }
                    if (!s.SetMirror(target, source))
                        warnings.Add("Live mirror link for '" + sd.materialName + "' was dropped: it would close a cycle.");
                    else
                        target.mirrorFlip = sd.mirrorFlip;
                }
                // Hidden materials, restored after every set exists: the flag
                // was read per set above, this applies it to the clone and BVH.
                s.RefreshMaterialVisibility();
                if (data.pose != null && data.pose.Count > 0)
                    s.pose.Restore(s.pose.SnapshotFrom(data.pose));
                if (!string.IsNullOrEmpty(data.activeSet))
                {
                    foreach (var set in s.sets)
                        if (SetLabel(set) == data.activeSet) { s.ActiveSet = set; break; }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            s.projectFolder = folder.Contains(Path.DirectorySeparatorChar + "_Recovery" + Path.DirectorySeparatorChar) ? null : folder;
            s.dirty = false;
            s.NotifyStructureChanged();
            s.dirty = false;
            return warnings;
        }

        /// <summary>Rebuilds one set's layer stack from its saved data, exactly as a project load does.
        /// When keepTargetSize is true (applying a template to another avatar) the target's own texture
        /// size is kept and the template's pixels are resampled into it; otherwise the saved size is
        /// restored. The set's previous layers are released, so snapshot anything to keep first.</summary>
        static void LoadSetLayers(APSession s, SetData sd, string folder, TextureSet set, bool keepTargetSize)
        {
            set.ReleaseAll();
            if (!keepTargetSize)
            {
                set.width = Mathf.Clamp(sd.width, 16, 8192);
                set.height = Mathf.Clamp(sd.height, 16, 8192);
                set.uvChannel = sd.uvChannel;
            }
            set.islandDirty = true;
            foreach (var ld in sd.layers)
            {
                var layer = new PaintLayer
                {
                    id = string.IsNullOrEmpty(ld.id) ? Guid.NewGuid().ToString("N") : ld.id,
                    name = ld.name,
                    kind = (LayerKind)ld.kind,
                    visible = ld.visible,
                    opacity = ld.opacity,
                    blend = (LayerBlendMode)ld.blend,
                    lockAlpha = ld.lockAlpha,
                    fillColor = ld.fillColor,
                    maskEnabled = ld.maskEnabled,
                    adjMode = (AdjustMode)ld.adjMode,
                    clipBelow = ld.clipBelow,
                    adjColor = ld.adjColor,
                    hue = ld.hue,
                    saturation = ld.saturation,
                    lightness = ld.lightness,
                    srcMean = ld.srcMean,
                    srcStd = ld.srcStd,
                    refMean = ld.refMean,
                    refStd = ld.refStd,
                    refValid = ld.refValid,
                    srcHist = ld.srcHist != null && ld.srcHist.Length == 768 ? ld.srcHist : null,
                    refHist = ld.refHist != null && ld.refHist.Length == 768 ? ld.refHist : null,
                };
                if (!string.IsNullOrEmpty(ld.contentFile))
                    layer.content = LoadRT(s, set, Path.Combine(folder, ld.contentFile), false);
                if (!string.IsNullOrEmpty(ld.maskFile))
                    layer.mask = LoadRT(s, set, Path.Combine(folder, ld.maskFile), true);
                if (ld.hasPlacement && ld.placement != null)
                {
                    layer.placement = ld.placement.Clone();
                    if (!string.IsNullOrEmpty(ld.placement.imageFile))
                        layer.placement.image = APUtil.LoadImageFile(Path.Combine(folder, ld.placement.imageFile));
                }
                if (layer.kind != LayerKind.Fill && layer.kind != LayerKind.Adjustment && layer.content == null) layer.content = s.engine.NewContentRT(set);
                set.layers.Add(layer);
            }
            if (set.layers.Count == 0)
            {
                s.engine.InitializeSet(set, s.view.preferSourceFiles);
            }
            set.activeLayer = Mathf.Clamp(sd.activeLayer, 0, set.layers.Count - 1);
            set.hidden = sd.hidden;
            set.initialized = true;
            s.engine.EnsureBuffers(set);
            foreach (var l in set.layers)
                if (l.kind == LayerKind.Image) s.engine.RenderImageLayer(set, l);
            set.dirty = true;
            s.ForceAssign(set);
        }

        public class ApplyTemplateResult
        {
            public int setsApplied;
            public int layersApplied;
            public List<string> skipped = new List<string>();
            public List<string> warnings = new List<string>();
        }

        class AppliedSet
        {
            public TextureSet set;
            public List<PaintLayer> before;
            public List<PaintLayer> after;
            public int beforeActive;
            public int afterActive;
            public bool beforeHidden;
            public bool afterHidden;
            public TextureSet beforeMirror;
            public TextureSet afterMirror;
            public bool beforeFlip;
            public bool afterFlip;
        }

        /// <summary>Copies every layer stack from a saved template onto the matching materials of the
        /// open session, matched by material name and texture property — so an avatar's full layout
        /// (layer order, names, opacity, blend, placement, hidden state and pixels) lands on another
        /// avatar to edit from, instead of redrawing it. Materials with no match on this avatar are
        /// skipped and reported, never created. The pose is not copied (it belongs to the other
        /// avatar's bones), the session's own project file is untouched, and the whole apply is one
        /// undo step.</summary>
        public static ApplyTemplateResult ApplyTemplate(APSession s, string folder)
        {
            var result = new ApplyTemplateResult();
            var data = JsonUtility.FromJson<ProjectData>(File.ReadAllText(Path.Combine(folder, FileName)));
            var applied = new Dictionary<TextureSet, AppliedSet>();
            try
            {
                int si = 0;
                foreach (var sd in data.sets)
                {
                    EditorUtility.DisplayProgressBar("Avi Creator Essentials", "Applying template to " + sd.materialName, si++ / (float)Mathf.Max(1, data.sets.Count));
                    var set = FindSetByName(s, sd) ?? FindSet(s, sd);
                    if (set == null)
                    {
                        result.skipped.Add(sd.materialName + " (" + sd.property + ")");
                        continue;
                    }
                    // Snapshot the target's own stack first: LoadSetLayers releases it,
                    // and these clones are what one Ctrl+Z restores.
                    var snap = new AppliedSet
                    {
                        set = set,
                        before = CloneStack(s, set),
                        beforeActive = set.activeLayer,
                        beforeHidden = set.hidden,
                        beforeMirror = set.mirrorSource,
                        beforeFlip = set.mirrorFlip,
                    };
                    LoadSetLayers(s, sd, folder, set, true);
                    snap.after = new List<PaintLayer>(set.layers);
                    snap.afterActive = set.activeLayer;
                    snap.afterHidden = set.hidden;
                    snap.afterMirror = null;
                    snap.afterFlip = true;
                    applied[set] = snap;
                    result.setsApplied++;
                    result.layersApplied += sd.layers.Count;
                }
                // Live mirror links, resolved after every set exists (a source set
                // may be saved after its target). Links to a missing set are dropped
                // with a warning, never left dangling — same as a project load.
                foreach (var sd in data.sets)
                {
                    if (string.IsNullOrEmpty(sd.mirrorSource)) continue;
                    var target = FindSetByName(s, sd) ?? FindSet(s, sd);
                    if (target == null || !applied.TryGetValue(target, out var snap)) continue;
                    TextureSet source = null;
                    foreach (var cand in s.sets)
                        if (SetLabel(cand) == sd.mirrorSource) { source = cand; break; }
                    if (source == null)
                    {
                        result.warnings.Add("Live mirror link for '" + sd.materialName + "' was dropped: its source material was not found.");
                        continue;
                    }
                    if (!s.SetMirror(target, source))
                    {
                        result.warnings.Add("Live mirror link for '" + sd.materialName + "' was dropped: it would close a cycle.");
                        continue;
                    }
                    target.mirrorFlip = sd.mirrorFlip;
                    snap.afterMirror = source;
                    snap.afterFlip = sd.mirrorFlip;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (applied.Count > 0)
            {
                var entries = new List<IHistoryEntry>();
                foreach (var snap in applied.Values)
                {
                    var a = snap;
                    long bytes = 0;
                    foreach (var l in a.before) bytes += l.Bytes;
                    entries.Add(new ActionEntry("Apply template to '" + (a.set.originalMaterial != null ? a.set.originalMaterial.name : a.set.displayName) + "'",
                        () => RestoreStack(s, a, a.before, a.beforeActive, a.beforeHidden, a.beforeMirror, a.beforeFlip),
                        () => RestoreStack(s, a, a.after, a.afterActive, a.afterHidden, a.afterMirror, a.afterFlip),
                        appliedNow =>
                        {
                            // Dropped while the template is in effect: the old stack is
                            // unreachable, so release it. Dropped while undone: release
                            // the template stack instead.
                            foreach (var l in appliedNow ? a.before : a.after) l.Release();
                        },
                        bytes));
                }
                s.history.Push(new CompoundEntry("Apply template", entries));
            }
            s.RefreshMaterialVisibility();
            s.NotifyStructureChanged();
            s.dirty = true;
            return result;
        }

        static List<PaintLayer> CloneStack(APSession s, TextureSet set)
        {
            var list = new List<PaintLayer>(set.layers.Count);
            foreach (var l in set.layers) list.Add(s.CloneLayer(l));
            return list;
        }

        static void RestoreStack(APSession s, AppliedSet a, List<PaintLayer> layers, int active, bool hidden, TextureSet mirror, bool flip)
        {
            // Swap the stack without releasing anything: the other stack stays
            // referenced by the undo entry, so redo can bring it back.
            a.set.layers.Clear();
            a.set.layers.AddRange(layers);
            a.set.activeLayer = Mathf.Clamp(active, 0, Mathf.Max(0, a.set.layers.Count - 1));
            a.set.hidden = hidden;
            a.set.mirrorSource = mirror;
            a.set.mirrorFlip = flip;
            a.set.initialized = true;
            a.set.islandDirty = true;
            s.engine.EnsureBuffers(a.set);
            foreach (var l in a.set.layers)
                if (l.kind == LayerKind.Image) s.engine.RenderImageLayer(a.set, l);
            a.set.dirty = true;
            s.ForceAssign(a.set);
            s.engine.SetChanged?.Invoke(a.set);
        }

        static TextureSet FindSetByName(APSession s, SetData sd)
        {
            foreach (var set in s.sets)
                if (set.originalMaterial != null && set.originalMaterial.name == sd.materialName && set.property == sd.property)
                    return set;
            return null;
        }

        static RenderTexture LoadRT(APSession s, TextureSet set, string file, bool mask)
        {
            var tex = APUtil.LoadImageFile(file, false);
            if (tex == null) return null;
            var rt = mask ? s.engine.NewMaskRT(set, 1f) : s.engine.NewContentRT(set);
            var comp = APResources.Composite;
            comp.SetFloat(APIds.CopyMode, mask ? 1f : 0f);
            comp.SetFloat(APIds.SrcToGamma, 0f);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt, comp, CompPass.Copy);
            RenderTexture.active = prev;
            Object.DestroyImmediate(tex);
            return rt;
        }

        static TextureSet FindSet(APSession s, SetData sd)
        {
            // 1. Same material asset and property.
            if (!string.IsNullOrEmpty(sd.materialGuid))
            {
                foreach (var set in s.sets)
                {
                    if (set.originalMaterial == null) continue;
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(set.originalMaterial, out string g, out long id) &&
                        g == sd.materialGuid && id == sd.materialLocalId)
                    {
                        if (set.property == sd.property) return set;
                        return s.AddPropertySet(set, sd.property);
                    }
                }
            }
            // 2. Same renderer slot (material may have been swapped since).
            foreach (var e in s.meshes.entries)
            {
                if (e.path != sd.rendererPath) continue;
                foreach (var set in s.sets)
                {
                    foreach (var u in set.users)
                    {
                        if (u.renderer != e.index || u.materialSlot != sd.materialSlot) continue;
                        if (set.property == sd.property) return set;
                        return s.AddPropertySet(set, sd.property);
                    }
                }
            }
            // 3. Same material name.
            foreach (var set in s.sets)
                if (set.originalMaterial != null && set.originalMaterial.name == sd.materialName && set.property == sd.property)
                    return set;
            return null;
        }
    }

    /// <summary>Writes final textures to PNG assets and optionally assigns them to the avatar.</summary>
    public static class Baker
    {
        public class Options
        {
            public int padding = 32;
            public bool fillAll = true;
            public bool assignToAvatar = true;
            public string folder;
        }

        public static List<string> Bake(APSession s, IList<TextureSet> sets, Options o)
        {
            var created = new List<string>();
            string folder = string.IsNullOrEmpty(o.folder)
                ? APUtil.OutputFolder + "/" + APUtil.SafeFileName(s.avatarName)
                : o.folder;
            APUtil.EnsureAssetFolder(folder);
            var bakedBySet = new Dictionary<TextureSet, Texture2D>();
            try
            {
                int i = 0;
                foreach (var set in sets)
                {
                    if (!set.initialized) continue;
                    EditorUtility.DisplayProgressBar("Avi Creator Essentials", "Baking " + set.displayName, i++ / (float)Mathf.Max(1, sets.Count));
                    var rt = s.engine.CompositeForExport(set, o.fillAll ? -1 : o.padding);
                    var png = APUtil.EncodePNG(rt);
                    APUtil.Release(ref rt);
                    string name = APUtil.SafeFileName(set.originalMaterial != null ? set.originalMaterial.name : "Texture") +
                                  (set.property == "_MainTex" ? "" : set.property);
                    string assetPath = folder + "/" + name + ".png";
                    File.WriteAllBytes(APUtil.AssetPathToFull(assetPath), png);
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                    ConfigureImporter(assetPath, set);
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (tex != null) bakedBySet[set] = tex;
                    created.Add(assetPath);
                }

                if (o.assignToAvatar && s.sourceAvatar != null)
                    AssignToAvatar(s, bakedBySet, folder);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            AssetDatabase.SaveAssets();
            return created;
        }

        static void ConfigureImporter(string assetPath, TextureSet set)
        {
            var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp == null) return;
            var orig = set.originalTexture != null
                ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(set.originalTexture)) as TextureImporter
                : null;
            int size = Mathf.Max(set.width, set.height);
            int max = 32;
            while (max < size && max < 8192) max *= 2;
            imp.maxTextureSize = max;
            imp.sRGBTexture = !set.linearData;
            imp.mipmapEnabled = true;
            imp.streamingMipmaps = true;
            imp.alphaIsTransparency = !set.linearData;
            if (orig != null)
            {
                if (orig.textureType == TextureImporterType.NormalMap) imp.textureType = TextureImporterType.NormalMap;
                imp.textureCompression = orig.textureCompression;
                imp.crunchedCompression = orig.crunchedCompression;
                imp.compressionQuality = orig.compressionQuality;
                imp.filterMode = orig.filterMode;
                imp.anisoLevel = orig.anisoLevel;
                imp.wrapMode = orig.wrapMode;
                imp.mipmapEnabled = orig.mipmapEnabled;
                imp.alphaIsTransparency = orig.alphaIsTransparency;
            }
            else
            {
                imp.textureCompression = TextureImporterCompression.CompressedHQ;
                if (APSession.IsNormalMap(set)) imp.textureType = TextureImporterType.NormalMap;
            }
            imp.SaveAndReimport();
        }

        static void AssignToAvatar(APSession s, Dictionary<TextureSet, Texture2D> baked, string folder)
        {
            // One painted material per original material, holding every baked property.
            var painted = new Dictionary<Material, Material>();
            foreach (var kv in baked)
            {
                var set = kv.Key;
                var orig = set.originalMaterial;
                if (orig == null) continue;
                if (!painted.TryGetValue(orig, out var pm))
                {
                    pm = GetPaintedMaterial(orig, folder);
                    painted[orig] = pm;
                }
                pm.SetTexture(set.property, kv.Value);
                if (set.createdProperty && set.property.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0)
                    APSession.EnableEmission(pm);
                if (APSession.IsNormalMap(set)) APSession.EnableNormalMap(pm);
                EditorUtility.SetDirty(pm);
            }

            var root = s.sourceAvatar.transform;
            foreach (var kv in painted)
            {
                var orig = kv.Key;
                foreach (var set in s.sets)
                {
                    if (set.originalMaterial != orig) continue;
                    foreach (var u in set.users)
                    {
                        var entry = s.meshes.entries[u.renderer];
                        var t = APUtil.FindPath(root, entry.path);
                        if (t == null) continue;
                        var r = t.GetComponent<Renderer>();
                        if (r == null) continue;
                        var mats = r.sharedMaterials;
                        if (u.materialSlot >= mats.Length || mats[u.materialSlot] != orig) continue;
                        Undo.RecordObject(r, "Assign painted material");
                        mats[u.materialSlot] = kv.Value;
                        r.sharedMaterials = mats;
                        EditorUtility.SetDirty(r);
                    }
                }
                // The session keeps working against the painted material from now on.
                foreach (var set in s.sets)
                    if (set.originalMaterial == orig) set.originalMaterial = kv.Value;
            }
        }

        static Material GetPaintedMaterial(Material orig, string folder)
        {
            string origPath = AssetDatabase.GetAssetPath(orig);
            // Already one of ours: update it in place.
            if (!string.IsNullOrEmpty(origPath) && origPath.StartsWith(APUtil.OutputFolder + "/", StringComparison.Ordinal))
                return orig;
            string path = folder + "/" + APUtil.SafeFileName(orig.name) + "_Painted.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            var copy = new Material(orig) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }
    }
}
