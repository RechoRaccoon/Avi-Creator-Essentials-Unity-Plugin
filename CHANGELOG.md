# Changelog

## 0.5.5

- Stabilizer: a Stabilizer section in the Brush panel with an intensity slider and two modes. Smooth averages your recent movement to iron out shaky lines; Rope is the lazy-mouse string. "Finish line on release" draws the rest of the stroke to the cursor when you let go.
- Right-click (without dragging) in the 3D or 2D view opens a color wheel with your recent colors and swatches under the cursor.
- Recent colors now sit right under the color wheel for quick swapping.
- Middle-drag pans in the 3D view without holding a key (Alt+middle-drag orbits, Ctrl+middle-drag zooms).
- Ctrl+scroll brush resizing is inverted: scrolling down grows the brush.
- Selecting a fill or recolor layer (or switching an adjustment layer to Recolor) loads its color into the brush.
- Fixed the color wheel's hue ring disappearing (and the wheel no longer following color changes) after the window was docked or moved.

## 0.5.4

- Mirror texture runs on the GPU in a single pass: instant at any texture size, with any number of selected polygons.
- The 3D "other side of the model" mirror no longer slows to a crawl with large selections.

## 0.5.3

- Mirror the texture: copy the left half onto the right (or right onto left, top/bottom) as a new layer, from the Layers "⋯" menu. With a UV selection, the selected area is copied to its flipped spot on the texture instead.

## 0.5.2

- "Mirror to other side": select an area (e.g. the eye that looks right) and copy it onto its mirror image on the other side of the model as a new layer. Points are matched through 3D space on the rest pose, so it works however the two areas are laid out in the UVs.

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
