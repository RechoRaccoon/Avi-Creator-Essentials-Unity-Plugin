# Changelog

## 0.5.1

- Match colors now uses histogram matching in luma/chroma: every shade (dark rim, mid tones, highlights) is mapped to the reference area's shade at the same percentile, so the match is complete instead of only lining up the average and spread.

## 0.5.0

- Wrap a tiling texture around the model: a triplanar (or one-sided) projection on the rest pose, baked into each material's UVs, like Poiyomi's World Pos / Local Pos mapping but stuck to the model.
- Wrapped normal maps go into the normal map slot, converted to each mesh's tangent space; a new "Normal map combine" blend mode layers them over the existing normal map.
- Empty normal map slots start flat instead of white, and imported (compressed) normal maps are unpacked correctly when painting them.
- Baked normal maps are imported as normal maps and switched on in the material.

## 0.4.0

- Adjustment layers: Recolor (new overall color, detail kept), Hue / Saturation / Lightness, and Match colors (copy one area's colors to another, e.g. one eye to the other). Each uses the UV selection it was made with.
- The color wheel edits the selected Recolor layer live.
- Shader property names show without Poiyomi's extra "--" labels.
- Update checker with an Update button, and a VRChat Creator Companion listing for one-click updates.

## 0.3.0

- Clothing armatures are matched to the body armature by name (like VRCFury's Armature Link), including prefixed/suffixed names and outfit-only extra bones.
- Scene view posing: live "Keep clothing in sync", plus "Merge into body armature" and "Unmerge clothing".
- Painter posing: clothing follows the body, and only one skeleton is drawn.
- "Exit posing" button in the Bone Posing panel.

## 0.2.0

- Renamed to Avi Creator Essentials and packaged for the Unity Package Manager.
- Unity-style interface with Dark, Light and Follow Unity themes.
- Emission maps are listed under every material that has one, with a Base color / Emission switch above the layers.
- New Select tool: pick UV islands or polygons (click, box or lasso) in the 2D or 3D view; painting and fills stay inside the selection.
- Painter posing updates the model immediately while dragging.
- New Scene view bone posing tool for animating the real avatar with the Animation window, sharing saved poses with the painter.

## 0.1.0

- First version of the avatar texture painter.
