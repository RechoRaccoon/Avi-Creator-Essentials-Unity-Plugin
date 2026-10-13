using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    /// <summary>A camera pose for rendering the 3D view.</summary>
    public struct ViewCamera
    {
        public Vector3 position;
        public Quaternion rotation;
        public float fov;
        public float near;
        public float far;

        public Matrix4x4 View => Matrix4x4.Inverse(Matrix4x4.TRS(position, rotation, new Vector3(1, 1, -1)));
        public Matrix4x4 Projection(float aspect) => Matrix4x4.Perspective(fov, aspect, near, far);
        public Vector3 Forward => rotation * Vector3.forward;
    }

    /// <summary>
    /// One painting session: an isolated preview copy of the avatar, its texture sets and layer stacks,
    /// the armature, the undo history and the rendering of the 3D view.
    /// </summary>
    public partial class APSession : IDisposable
    {
        public GameObject sourceAvatar;
        public string avatarName;
        public string projectFolder;
        public bool dirty;

        public Scene scene;
        public Camera camera;
        Light m_Key, m_Fill, m_Rim;
        GameObject m_Container;
        public GameObject clone;

        public MeshCache meshes;
        public PaintEngine engine;
        public History history;
        public PoseSystem pose;
        public ArmatureLinker linker;
        public readonly List<TextureSet> sets = new List<TextureSet>();
        TextureSet m_ActiveSet;

        public ViewSettings view;
        public BrushSettings brush;

        readonly Dictionary<Material, Material> m_CloneMaterials = new Dictionary<Material, Material>();
        readonly Dictionary<Renderer, Material[]> m_RendererMaterials = new Dictionary<Renderer, Material[]>();
        readonly List<Material> m_EmissionMaterials = new List<Material>();
        bool m_EmissionApplied;
        readonly Dictionary<string, HumanBodyBones> m_HumanPaths = new Dictionary<string, HumanBodyBones>();

        /// <summary>Layers, sets or other structure changed (UI should rebuild lists).</summary>
        public event Action StructureChanged;
        /// <summary>Pixels of a texture set changed (thumbnails, 3D view).</summary>
        public event Action<TextureSet> PixelsChanged;
        public event Action ActiveSetChanged;
        public event Action PoseEdited;
        int m_BoundsVersion = -1;

        /// <summary>World bounds of every set, refreshed after the pose changes.</summary>
        public void UpdateBounds()
        {
            meshes.EnsurePosed();
            if (m_BoundsVersion == meshes.Version) return;
            m_BoundsVersion = meshes.Version;
            foreach (var s in sets) engine.UpdateBounds(s);
        }

        public TextureSet ActiveSet
        {
            get => m_ActiveSet;
            set
            {
                if (m_ActiveSet == value) return;
                m_ActiveSet = value;
                if (value != null && !value.initialized) InitializeSet(value);
                ActiveSetChanged?.Invoke();
                StructureChanged?.Invoke();
            }
        }

        public IReadOnlyDictionary<string, HumanBodyBones> HumanPaths => m_HumanPaths;

        // ================================================================== creation

        public static APSession Create(GameObject avatar, ViewSettings view, BrushSettings brush)
        {
            var s = new APSession { sourceAvatar = avatar, avatarName = avatar.name, view = view, brush = brush };
            try
            {
                s.Setup();
            }
            catch
            {
                s.Dispose();
                throw;
            }
            return s;
        }

        void Setup()
        {
            CollectHumanoid(sourceAvatar);

            scene = EditorSceneManager.NewPreviewScene();
            scene.name = "Avi Creator Essentials";

            var camGo = EditorUtility.CreateGameObjectWithHideFlags("AP Camera", HideFlags.HideAndDontSave, typeof(Camera));
            SceneManager.MoveGameObjectToScene(camGo, scene);
            camera = camGo.GetComponent<Camera>();
            camera.cameraType = CameraType.Preview;
            camera.enabled = false;
            camera.scene = scene;
            camera.renderingPath = RenderingPath.Forward;
            camera.useOcclusionCulling = false;
            camera.allowHDR = false;
            camera.allowMSAA = true;
            camera.clearFlags = CameraClearFlags.Depth;

            m_Key = CreateLight("AP Key Light");
            m_Fill = CreateLight("AP Fill Light");
            m_Rim = CreateLight("AP Rim Light");

            m_Container = EditorUtility.CreateGameObjectWithHideFlags("AP Avatar Root", HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene(m_Container, scene);
            // Instantiate under an inactive parent so no edit-mode scripts on the avatar wake up on the copy.
            m_Container.SetActive(false);

            clone = Object.Instantiate(sourceAvatar, m_Container.transform);
            clone.name = sourceAvatar.name;
            clone.SetActive(true);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = sourceAvatar.transform.lossyScale;
            SetHideFlagsRecursive(clone, HideFlags.HideAndDontSave);
            StripComponents(clone);
            foreach (var smr in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.updateWhenOffscreen = true;
                // Re-skin on every render, so the mesh follows bone edits immediately instead of a frame or more later.
                smr.forceMatrixRecalculationPerRender = true;
            }
            m_Container.SetActive(true);

            meshes = new MeshCache();
            meshes.Build(clone);
            engine = new PaintEngine(meshes) { displayPadding = view.displayPadding, highlightSelection = view.highlightSelection };
            engine.SetChanged = OnSetPixelsChanged;
            history = new History { MaxSteps = Mathf.Max(5, view.undoSteps) };
            history.Changed += () => StructureChanged?.Invoke();

            pose = new PoseSystem();
            pose.Build(clone.transform, m_HumanPaths);
            BuildLinker();
            meshes.projectionRoot = clone.transform;
            meshes.runAtRest = work =>
            {
                var saved = pose.Capture();
                pose.Restore(pose.RestSnapshot(), false);
                if (view.linkClothing && linker != null) linker.Apply();
                try { work(); }
                finally
                {
                    pose.Restore(saved, false);
                    if (view.linkClothing && linker != null) linker.Apply();
                }
            };
            pose.PoseChanged += () =>
            {
                if (view.linkClothing && linker != null) linker.Apply();
                meshes.MarkPoseDirty();
                PoseEdited?.Invoke();
            };

            BuildTextureSets();
            if (sets.Count > 0) ActiveSet = sets[0];
        }

        void BuildLinker()
        {
            Transform hips = null;
            foreach (var kv in m_HumanPaths)
                if (kv.Value == HumanBodyBones.Hips) hips = APUtil.FindPath(clone.transform, kv.Key);
            linker = ArmatureLinker.Detect(clone.transform, hips);
            // Offsets come from the model's rest pose (prefab/FBX values of the scene avatar), so it works even if posed.
            var src = sourceAvatar.transform;
            linker.CaptureOffsets(t =>
            {
                var st = APUtil.FindPath(src, APUtil.GetPath(t, clone.transform));
                if (st == null) return null;
                var o = PrefabUtility.GetCorrespondingObjectFromOriginalSource(st);
                if (o == null || o == st) return null;
                return (o.localRotation, o.localPosition);
            });
            ApplyLinkSetting();
        }

        /// <summary>Turns clothing armature linking on or off for the painter's copy.</summary>
        public void ApplyLinkSetting()
        {
            bool on = view.linkClothing && linker != null && linker.HasClothing;
            pose.SetLinkHidden(on ? (Func<Transform, bool>)linker.IsLinkedClothBone : null);
            if (on)
            {
                linker.Apply();
                meshes.MarkPoseDirty();
            }
            if (pose.selected != null && pose.selected.linkHidden) pose.Select(null);
            PoseEdited?.Invoke();
        }

        Light CreateLight(string name)
        {
            var go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave, typeof(Light));
            SceneManager.MoveGameObjectToScene(go, scene);
            var l = go.GetComponent<Light>();
            l.type = LightType.Directional;
            l.shadows = LightShadows.None;
            l.enabled = true;
            return l;
        }

        static void SetHideFlagsRecursive(GameObject go, HideFlags flags)
        {
            go.hideFlags = flags;
            foreach (Transform c in go.transform) SetHideFlagsRecursive(c.gameObject, flags);
        }

        void CollectHumanoid(GameObject avatar)
        {
            m_HumanPaths.Clear();
            var animator = avatar.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman) return;
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                Transform t = null;
                try { t = animator.GetBoneTransform((HumanBodyBones)i); }
                catch (Exception) { /* ignore */ }
                if (t == null || !t.IsChildOf(avatar.transform)) continue;
                m_HumanPaths[APUtil.GetPath(t, avatar.transform)] = (HumanBodyBones)i;
            }
        }

        /// <summary>
        /// Removes everything except transforms and mesh renderers from the preview copy, so no avatar scripts,
        /// constraints, physics or animators run against the posing and painting.
        /// </summary>
        static void StripComponents(GameObject root)
        {
            for (int pass = 0; pass < 8; pass++)
            {
                bool removed = false;
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    if (c is Transform || c is SkinnedMeshRenderer || c is MeshRenderer || c is MeshFilter) continue;
                    if (IsRequiredByOthers(c)) continue;
                    try
                    {
                        Object.DestroyImmediate(c);
                        removed = true;
                    }
                    catch (Exception)
                    {
                        // Some components refuse removal; they are harmless in a preview scene.
                    }
                }
                if (!removed) break;
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
        }

        static bool IsRequiredByOthers(Component c)
        {
            var type = c.GetType();
            foreach (var other in c.gameObject.GetComponents<Component>())
            {
                if (other == null || other == c) continue;
                foreach (RequireComponent rc in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                {
                    if ((rc.m_Type0 != null && rc.m_Type0.IsAssignableFrom(type)) ||
                        (rc.m_Type1 != null && rc.m_Type1.IsAssignableFrom(type)) ||
                        (rc.m_Type2 != null && rc.m_Type2.IsAssignableFrom(type)))
                        return true;
                }
            }
            return false;
        }

        // ================================================================== texture sets

        public static List<string> TextureProperties(Material m)
        {
            var list = new List<string>();
            if (m == null || m.shader == null) return list;
            var sh = m.shader;
            int n = sh.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (sh.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (sh.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                if ((sh.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0) continue;
                list.Add(sh.GetPropertyName(i));
            }
            return list;
        }

        public static string PropertyDescription(Material m, string property)
        {
            if (m == null || m.shader == null) return property;
            int i = m.shader.FindPropertyIndex(property);
            if (i < 0) return property;
            string d = m.shader.GetPropertyDescription(i);
            if (string.IsNullOrEmpty(d)) return property;
            // Some shaders (Poiyomi, Thry) pack editor metadata after "--" in the display name.
            int meta = d.IndexOf("--", StringComparison.Ordinal);
            if (meta > 0) d = d.Substring(0, meta);
            d = d.Trim();
            return d.Length == 0 ? property : d;
        }

        static string DefaultProperty(Material m)
        {
            var props = TextureProperties(m);
            foreach (var candidate in new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_Diffuse" })
                if (props.Contains(candidate)) return candidate;
            if (m.HasProperty("_MainTex")) return "_MainTex";
            return props.Count > 0 ? props[0] : null;
        }

        void BuildTextureSets()
        {
            foreach (var e in meshes.entries)
            {
                var r = e.renderer;
                var mats = r.sharedMaterials;
                var newMats = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    var orig = mats[i];
                    if (orig == null) continue;
                    if (!m_CloneMaterials.TryGetValue(orig, out var cm))
                    {
                        cm = new Material(orig) { name = orig.name, hideFlags = HideFlags.HideAndDontSave };
                        m_CloneMaterials[orig] = cm;
                    }
                    newMats[i] = cm;
                }
                r.sharedMaterials = newMats;
                m_RendererMaterials[r] = newMats;
            }

            foreach (var e in meshes.entries)
            {
                var mats = e.renderer.sharedMaterials;
                // Map clone materials back to originals.
                for (int slot = 0; slot < mats.Length; slot++)
                {
                    var cm = mats[slot];
                    if (cm == null) continue;
                    Material orig = null;
                    foreach (var kv in m_CloneMaterials)
                        if (kv.Value == cm) { orig = kv.Key; break; }
                    if (orig == null) continue;
                    string prop = DefaultProperty(cm);
                    if (prop == null) continue;
                    var set = FindOrCreateSet(orig, cm, prop);
                    int submesh = Mathf.Min(slot, Mathf.Max(0, e.SubmeshCount - 1));
                    AddUser(set, e.index, submesh, slot);
                }
            }

            // Every material that has an emission slot gets its own paintable emission set, listed under it.
            var bases = new List<TextureSet>(sets);
            foreach (var b in bases)
            {
                string emission = EmissionProperty(b.cloneMaterial);
                if (emission == null || emission == b.property) continue;
                CreateSiblingSet(b, emission);
            }
        }

        /// <summary>The material's emission texture property, if its shader has one.</summary>
        public static string EmissionProperty(Material m)
        {
            var props = TextureProperties(m);
            foreach (var candidate in new[] { "_EmissionMap", "_EmissionTex", "_EmissionMap1" })
                if (props.Contains(candidate)) return candidate;
            foreach (var p in props)
                if (p.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    p.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) < 0 &&
                    p.IndexOf("Scroll", StringComparison.OrdinalIgnoreCase) < 0)
                    return p;
            return null;
        }

        /// <summary>The material's normal map property, if its shader has one.</summary>
        public static string NormalProperty(Material m)
        {
            var props = TextureProperties(m);
            foreach (var candidate in new[] { "_BumpMap", "_NormalMap", "_MainNormal", "_BumpTex" })
                if (props.Contains(candidate)) return candidate;
            foreach (var p in props)
            {
                if (p.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) < 0 && p.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (p.IndexOf("Detail", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return p;
            }
            return null;
        }

        public static bool IsNormalProperty(string property)
        {
            string p = property.ToLowerInvariant();
            return (p.Contains("bump") || p.Contains("normal")) && !p.Contains("mask");
        }

        /// <summary>True for a tangent-space normal map set.</summary>
        public static bool IsNormalMap(TextureSet set)
        {
            if (set == null || !set.linearData) return false;
            if (set.originalTexture != null)
            {
                var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(set.originalTexture)) as TextureImporter;
                if (importer != null) return importer.textureType == TextureImporterType.NormalMap;
            }
            return IsNormalProperty(set.property);
        }

        /// <summary>Makes sure the shader actually uses the normal map (Standard needs a keyword; Poiyomi and lilToon have toggles).</summary>
        public static void EnableNormalMap(Material m)
        {
            if (m == null) return;
            m.EnableKeyword("_NORMALMAP");
            foreach (var toggle in new[] { "_UseBumpMap", "_UseNormalMap" })
                if (m.HasProperty(toggle) && m.shader.FindPropertyIndex(toggle) >= 0 &&
                    m.shader.GetPropertyType(m.shader.FindPropertyIndex(toggle)) != ShaderPropertyType.Texture)
                    m.SetFloat(toggle, 1f);
        }

        public static bool IsEmission(TextureSet set) =>
            set != null && set.property.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The base-color set and emission set of the same material.</summary>
        public TextureSet Sibling(TextureSet set, bool emission)
        {
            if (set == null) return null;
            if (IsEmission(set) == emission) return set;
            foreach (var s in sets)
                if (s.originalMaterial == set.originalMaterial && s != set && IsEmission(s) == emission)
                    return s;
            return null;
        }

        TextureSet CreateSiblingSet(TextureSet from, string property)
        {
            var set = FindOrCreateSet(from.originalMaterial, from.cloneMaterial, property);
            foreach (var u in from.users) AddUser(set, u.renderer, u.submesh, u.materialSlot);
            if (set.originalTexture == null)
            {
                set.createdProperty = true;
                set.width = from.width;
                set.height = from.height;
            }
            if (IsEmission(set)) set.displayName = from.originalMaterial.name + "  ·  Emission";
            sets.Remove(set);
            int idx = sets.IndexOf(from);
            int insert = idx + 1;
            while (insert < sets.Count && sets[insert].originalMaterial == from.originalMaterial) insert++;
            sets.Insert(insert, set);
            return set;
        }

        static void AddUser(TextureSet set, int renderer, int submesh, int slot)
        {
            foreach (var u in set.users)
                if (u.renderer == renderer && u.submesh == submesh) return;
            set.users.Add(new SetUser { renderer = renderer, submesh = submesh, materialSlot = slot });
        }

        TextureSet FindOrCreateSet(Material orig, Material cloneMat, string property)
        {
            foreach (var s in sets)
                if (s.originalMaterial == orig && s.property == property) return s;
            var set = new TextureSet
            {
                originalMaterial = orig,
                cloneMaterial = cloneMat,
                property = property,
                originalTexture = cloneMat.HasProperty(property) ? cloneMat.GetTexture(property) : null,
            };
            set.displayName = orig.name + (property == DefaultProperty(cloneMat) ? "" : "  ·  " + PropertyDescription(cloneMat, property));
            set.linearData = IsLinearData(set.originalTexture, property);
            var main = set.originalTexture;
            if (main != null)
            {
                set.width = Mathf.Clamp(main.width, 16, 8192);
                set.height = Mathf.Clamp(main.height, 16, 8192);
            }
            sets.Add(set);
            return set;
        }

        static bool IsLinearData(Texture tex, string property)
        {
            if (tex != null)
            {
                var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
                if (importer != null)
                    return !importer.sRGBTexture || importer.textureType == TextureImporterType.NormalMap;
                return !APUtil.IsSRGB(tex) && APUtil.LinearProject;
            }
            string p = property.ToLowerInvariant();
            return p.Contains("bump") || p.Contains("normal") || p.Contains("mask") && !p.Contains("color");
        }

        /// <summary>Adds another paintable texture (e.g. the emission map) for a material that already has a set.</summary>
        public TextureSet AddPropertySet(TextureSet from, string property)
        {
            foreach (var s in sets)
                if (s.originalMaterial == from.originalMaterial && s.property == property)
                {
                    ActiveSet = s;
                    return s;
                }
            var set = CreateSiblingSet(from, property);
            ActiveSet = set;
            StructureChanged?.Invoke();
            return set;
        }

        /// <summary>Turns emission on for common VRChat shaders (Standard, Poiyomi, lilToon...).</summary>
        public static void EnableEmission(Material m)
        {
            if (m == null) return;
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            foreach (var toggle in new[] { "_EnableEmission", "_UseEmission", "_EmissionEnabled", "_Emission" })
                if (m.HasProperty(toggle) && m.shader.FindPropertyIndex(toggle) >= 0 &&
                    m.shader.GetPropertyType(m.shader.FindPropertyIndex(toggle)) != ShaderPropertyType.Texture)
                    m.SetFloat(toggle, 1f);
            if (m.HasProperty("_EmissionColor"))
            {
                var c = m.GetColor("_EmissionColor");
                if (c.maxColorComponent < 0.001f) m.SetColor("_EmissionColor", Color.white);
            }
            if (m.HasProperty("_EmissionStrength") && m.GetFloat("_EmissionStrength") <= 0f) m.SetFloat("_EmissionStrength", 1f);
        }

        public void InitializeSet(TextureSet set)
        {
            if (set.initialized) return;
            engine.InitializeSet(set, view.preferSourceFiles);
            // A new emission map only glows once it exists, so emission is switched on now rather than at startup.
            if (set.createdProperty && IsEmission(set)) EnableEmission(set.cloneMaterial);
            if (IsNormalMap(set)) EnableNormalMap(set.cloneMaterial);
            engine.UpdateBounds(set);
            AssignDisplay(set);
            StructureChanged?.Invoke();
        }

        /// <summary>Re-links a (re)loaded set to the preview material.</summary>
        public void ForceAssign(TextureSet set)
        {
            engine.EnsureBuffers(set);
            engine.UpdateBounds(set);
            AssignDisplay(set);
            StructureChanged?.Invoke();
        }

        void AssignDisplay(TextureSet set)
        {
            if (set.cloneMaterial == null || set.display == null) return;
            set.cloneMaterial.SetTexture(set.property, set.display);
            if (m_EmissionApplied) RefreshEmissionMaterials();
        }

        public TextureSet SetForUser(int renderer, int submesh, string preferProperty = null)
        {
            TextureSet fallback = null;
            foreach (var s in sets)
            {
                foreach (var u in s.users)
                {
                    if (u.renderer != renderer || u.submesh != submesh) continue;
                    if (preferProperty == null || s.property == preferProperty) return s;
                    if (fallback == null) fallback = s;
                }
            }
            return fallback;
        }

        void OnSetPixelsChanged(TextureSet set)
        {
            dirty = true;
            PixelsChanged?.Invoke(set);
        }

        public void NotifyStructureChanged()
        {
            dirty = true;
            StructureChanged?.Invoke();
        }

        /// <summary>Composites every changed texture set. Called before drawing.</summary>
        public void UpdateComposites()
        {
            engine.displayPadding = view.displayPadding;
            foreach (var s in sets)
            {
                if (!s.initialized) continue;
                var before = s.display;
                engine.Composite(s);
                if (s.display != before) AssignDisplay(s);
            }
        }

        // ================================================================== visibility

        public void SetRendererVisible(RendererEntry e, bool visible)
        {
            if (e.renderer == null) return;
            if (visible)
            {
                e.renderer.enabled = true;
                var t = e.renderer.transform;
                while (t != null && t != clone.transform.parent)
                {
                    if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                    t = t.parent;
                }
            }
            else
            {
                e.renderer.enabled = false;
            }
            meshes.MarkVisibilityDirty();
            StructureChanged?.Invoke();
        }

        // ================================================================== emission view

        public void SetEmissionView(bool on)
        {
            view.emissionOnly = on;
            var sibling = Sibling(ActiveSet, on);
            if (sibling != null) ActiveSet = sibling;
            if (on) ApplyEmissionMaterials();
            else RestoreMaterials();
        }

        void ApplyEmissionMaterials()
        {
            RestoreMaterials();
            var shader = APResources.EmissionShader;
            if (shader == null) return;
            var made = new Dictionary<Material, Material>();
            foreach (var kv in m_RendererMaterials)
            {
                var r = kv.Key;
                if (r == null) continue;
                var src = kv.Value;
                var dst = new Material[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    var m = src[i];
                    if (m == null) continue;
                    if (!made.TryGetValue(m, out var em))
                    {
                        em = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = m.name + " (Emission)" };
                        made[m] = em;
                        m_EmissionMaterials.Add(em);
                    }
                    dst[i] = em;
                }
                r.sharedMaterials = dst;
            }
            m_EmissionApplied = true;
            RefreshEmissionMaterials();
        }

        void RefreshEmissionMaterials()
        {
            if (!m_EmissionApplied) return;
            foreach (var kv in m_RendererMaterials)
            {
                var r = kv.Key;
                if (r == null) continue;
                var src = kv.Value;
                var dst = r.sharedMaterials;
                for (int i = 0; i < src.Length && i < dst.Length; i++)
                {
                    var m = src[i];
                    var em = dst[i];
                    if (m == null || em == null) continue;
                    Texture map = null;
                    foreach (var name in new[] { "_EmissionMap", "_EmissionTex", "_Emission" })
                    {
                        if (m.HasProperty(name) && m.shader.FindPropertyIndex(name) >= 0 &&
                            m.shader.GetPropertyType(m.shader.FindPropertyIndex(name)) == ShaderPropertyType.Texture)
                        {
                            map = m.GetTexture(name);
                            if (map != null)
                            {
                                em.SetTextureScale(APIds.EmissionMap, m.GetTextureScale(name));
                                em.SetTextureOffset(APIds.EmissionMap, m.GetTextureOffset(name));
                                break;
                            }
                        }
                    }
                    Color color = m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black;
                    bool enabled = EmissionEnabled(m);
                    if (map == null && color.maxColorComponent > 0f && enabled) map = Texture2D.whiteTexture;
                    em.SetTexture(APIds.EmissionMap, map != null ? map : Texture2D.blackTexture);
                    em.SetColor(APIds.EmissionColor, enabled ? color : Color.black);
                    float strength = m.HasProperty("_EmissionStrength") ? m.GetFloat("_EmissionStrength") : 1f;
                    em.SetFloat(APIds.EmissionStrength, strength);
                    var mainTex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                    em.SetTexture(APIds.MainTex, mainTex != null ? mainTex : Texture2D.blackTexture);
                    em.SetFloat(APIds.BaseDim, view.emissionBaseDim);
                }
            }
        }

        static bool EmissionEnabled(Material m)
        {
            foreach (var toggle in new[] { "_EnableEmission", "_UseEmission" })
            {
                if (m.HasProperty(toggle))
                {
                    int idx = m.shader.FindPropertyIndex(toggle);
                    if (idx >= 0 && m.shader.GetPropertyType(idx) != ShaderPropertyType.Texture && m.GetFloat(toggle) < 0.5f)
                        return false;
                }
            }
            if (m.shader != null && m.shader.name == "Standard" && !m.IsKeywordEnabled("_EMISSION")) return false;
            return true;
        }

        void RestoreMaterials()
        {
            if (m_EmissionApplied)
            {
                foreach (var kv in m_RendererMaterials)
                    if (kv.Key != null) kv.Key.sharedMaterials = kv.Value;
            }
            foreach (var m in m_EmissionMaterials)
                if (m != null) Object.DestroyImmediate(m);
            m_EmissionMaterials.Clear();
            m_EmissionApplied = false;
        }

        public void RefreshEmissionView()
        {
            if (m_EmissionApplied) RefreshEmissionMaterials();
        }

        // ================================================================== rendering

        public void Render(RenderTexture rt, ViewCamera cam)
        {
            if (rt == null || camera == null) return;
            UpdateComposites();
            if (m_EmissionApplied) RefreshEmissionMaterials();

            camera.transform.SetPositionAndRotation(cam.position, cam.rotation);
            camera.fieldOfView = cam.fov;
            camera.nearClipPlane = cam.near;
            camera.farClipPlane = cam.far;
            camera.aspect = rt.width / (float)Mathf.Max(1, rt.height);

            bool emission = view.emissionOnly;
            Color top = view.backgroundTop, bottom = view.backgroundBottom;
            if (view.LightTheme)
            {
                top = new Color(0.66f, 0.67f, 0.70f);
                bottom = new Color(0.46f, 0.47f, 0.50f);
            }
            else
            {
                top = new Color(0.27f, 0.27f, 0.28f);
                bottom = new Color(0.14f, 0.14f, 0.15f);
            }
            engine.RenderGradient(rt, emission ? new Color(0.05f, 0.05f, 0.07f) : top,
                emission ? new Color(0.01f, 0.01f, 0.015f) : bottom);
            camera.clearFlags = CameraClearFlags.Depth;
            camera.targetTexture = rt;

            SetupLights(cam, emission);

            bool overridden = !EditorApplication.isUpdating && Unsupported.SetOverrideLightingSettings(scene);
            if (overridden)
            {
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = emission ? Color.black : AmbientColor();
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                RenderSettings.customReflectionTexture = ReflectionProbe.defaultTexture;
                RenderSettings.reflectionIntensity = emission ? 0f : 0.8f;
                RenderSettings.fog = false;
            }
            try
            {
                camera.Render();
            }
            finally
            {
                if (overridden) Unsupported.RestoreOverrideLightingSettings();
                camera.targetTexture = null;
            }
        }

        Color AmbientColor()
        {
            float a = view.ambient;
            switch (view.lighting)
            {
                case LightingPreset.Flat: return new Color(1f, 1f, 1f) * Mathf.Max(a, 0.9f);
                case LightingPreset.Soft: return new Color(0.72f, 0.74f, 0.8f) * (a + 0.25f);
                case LightingPreset.Dramatic: return new Color(0.3f, 0.32f, 0.4f) * a;
                case LightingPreset.Night: return new Color(0.18f, 0.2f, 0.34f) * a;
                default: return new Color(0.82f, 0.84f, 0.9f) * a;
            }
        }

        void SetupLights(ViewCamera cam, bool emission)
        {
            float yaw = view.lightYaw + (view.lightFollowsCamera ? cam.rotation.eulerAngles.y : 0f);
            var baseRot = Quaternion.Euler(0f, yaw, 0f);
            float k = emission ? 0f : view.lightIntensity;
            switch (view.lighting)
            {
                case LightingPreset.Flat:
                    SetLight(m_Key, baseRot * Quaternion.Euler(40, -30, 0), new Color(1, 1, 1), 0.15f * k);
                    SetLight(m_Fill, baseRot * Quaternion.Euler(20, 150, 0), new Color(1, 1, 1), 0f);
                    SetLight(m_Rim, baseRot * Quaternion.Euler(10, 180, 0), new Color(1, 1, 1), 0f);
                    break;
                case LightingPreset.Soft:
                    SetLight(m_Key, baseRot * Quaternion.Euler(35, -25, 0), new Color(1f, 0.98f, 0.95f), 0.65f * k);
                    SetLight(m_Fill, baseRot * Quaternion.Euler(15, 140, 0), new Color(0.75f, 0.8f, 1f), 0.3f * k);
                    SetLight(m_Rim, baseRot * Quaternion.Euler(10, 185, 0), new Color(1f, 1f, 1f), 0.25f * k);
                    break;
                case LightingPreset.Dramatic:
                    SetLight(m_Key, baseRot * Quaternion.Euler(25, -60, 0), new Color(1f, 0.92f, 0.82f), 1.25f * k);
                    SetLight(m_Fill, baseRot * Quaternion.Euler(10, 120, 0), new Color(0.4f, 0.5f, 0.9f), 0.2f * k);
                    SetLight(m_Rim, baseRot * Quaternion.Euler(15, 170, 0), new Color(1f, 0.85f, 0.9f), 0.9f * k);
                    break;
                case LightingPreset.Night:
                    SetLight(m_Key, baseRot * Quaternion.Euler(50, -20, 0), new Color(0.55f, 0.62f, 1f), 0.35f * k);
                    SetLight(m_Fill, baseRot * Quaternion.Euler(10, 140, 0), new Color(0.3f, 0.25f, 0.5f), 0.15f * k);
                    SetLight(m_Rim, baseRot * Quaternion.Euler(10, 180, 0), new Color(0.6f, 0.7f, 1f), 0.4f * k);
                    break;
                default:
                    SetLight(m_Key, baseRot * Quaternion.Euler(35, -35, 0), new Color(1f, 0.97f, 0.92f), 1.0f * k);
                    SetLight(m_Fill, baseRot * Quaternion.Euler(15, 135, 0), new Color(0.7f, 0.78f, 1f), 0.35f * k);
                    SetLight(m_Rim, baseRot * Quaternion.Euler(10, 185, 0), new Color(1f, 1f, 1f), 0.5f * k);
                    break;
            }
        }

        static void SetLight(Light l, Quaternion rot, Color color, float intensity)
        {
            if (l == null) return;
            l.transform.rotation = rot;
            l.color = color;
            l.intensity = intensity;
            l.enabled = intensity > 0f;
        }

        public void Tick()
        {
            if (pose != null && pose.Animating) pose.Tick();
        }

        // ================================================================== cleanup

        public long MemoryBytes
        {
            get
            {
                long b = history != null ? history.Bytes : 0;
                foreach (var s in sets) b += s.Bytes;
                return b;
            }
        }

        public void Dispose()
        {
            RestoreMaterials();
            history?.Dispose();
            history = null;
            foreach (var s in sets) s.ReleaseAll();
            sets.Clear();
            engine?.Dispose();
            engine = null;
            meshes?.Dispose();
            meshes = null;
            foreach (var kv in m_CloneMaterials)
                if (kv.Value != null) Object.DestroyImmediate(kv.Value);
            m_CloneMaterials.Clear();
            m_RendererMaterials.Clear();
            if (m_Container != null) Object.DestroyImmediate(m_Container);
            if (camera != null) Object.DestroyImmediate(camera.gameObject);
            if (m_Key != null) Object.DestroyImmediate(m_Key.gameObject);
            if (m_Fill != null) Object.DestroyImmediate(m_Fill.gameObject);
            if (m_Rim != null) Object.DestroyImmediate(m_Rim.gameObject);
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            clone = null;
        }
    }
}
