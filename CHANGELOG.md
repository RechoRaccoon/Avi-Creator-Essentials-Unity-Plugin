# Changelog

## 0.9.0

- Fill a mask's shape with a texture: right-click a mask texture in the Project window › Avi Creator Essentials › "Fill Mask Shape With Texture (Avi Creator Essentials)…", then pick or drop the texture to fill it with. Makes a new PNG next to the mask, the same size as the mask, showing the new texture where the mask is white (or opaque) and transparent elsewhere. Choose whether the shape comes from the mask's alpha, brightness or one channel (Auto picks for you), invert it, tile the fill texture, and set the color outside the shape. A live preview shows the result. Also in Tools › Avi Creator Essentials.

## 0.8.0

- Flatten a material: right-click a material in the Inspector (or its ⋮ menu) → "Flatten to Main + Emission Textures (Avi Creator Essentials)…". Bakes the main texture × main color with every normal map shaded in, and the emission map × its color and mask, into two textures laid out on the avatar's UVs. Textures mapped by Local Pos / World Pos are sampled the way the shader does. Each texture slot gets a role you can change, a "Bump shading" slider sets how strong the bumps look, and a flattened copy of the material can be made and put on the avatar (undoable). Also in Tools › Avi Creator Essentials › Flatten Material.

## 0.7.0

- Bake Local Pos to UV: right-click a material in the Inspector (or its ⋮ menu) → "Bake Local Pos to UV (Avi Creator Essentials)…". Textures the shader maps by Local Pos or World Pos (Poiyomi's UV dropdown) are baked into ordinary UV textures that look exactly like the shader shows them with the avatar standing still, but stay stuck to the model when it moves and work with any shader. Normal maps keep their look, with the normal strength baked in if you like; the material can be switched over to the baked texture automatically (undoable). Also in Tools › Avi Creator Essentials and the Project window's right-click menu.

## 0.6.2

- Wrap tiling texture now lets you pick the image from the material's own texture slots or from any texture in the project (Unity's texture picker), as well as from an image file.
- Texture slots that use Poiyomi's Local Pos or World Pos mapping are read straight from the material (mode, axes, tiling and offset) and baked with exactly that projection, so the result matches what the shader showed. "Adjust by hand instead" in the Image panel switches back to the adjustable wrap.

## 0.6.1

- Fixed the brush tips showing up twice in the Brush panel after Unity reloaded scripts.
- The Heart brush is a complete heart, with a sharp point and a clean dip between the lobes.

## 0.6.0

- Transform tool (V): pick up part of a paint layer and move it. In the texture view, draw a shape around what to move, or click a color to take every touching pixel of that color (click on the model in the 3D view). The piece floats above the layer: drag it, pull corners to scale (Shift stretches freely), edges to stretch, the top handle to rotate (Shift snaps to 15°). Enter puts it down, Esc puts everything back.
- Warp: switch the Transform tool to Warp for a 3 × 3 grid of points that bend and distort the piece.
- The Transform panel can also pick up the UV selection or the whole layer, keep the original (copy), flip, and reset the warp. Leaving the tool applies the piece.
- Symmetry now works in the texture view too: strokes drawn in 2D are repeated on the other side of the model, found through the 3D mesh so it works with any UV layout.

## 0.5.6

- Alt+middle-drag pans in the 3D view again (it orbited in 0.5.5), so middle-drag pans with or without Alt.

## 0.5.5

- Stabilizer: a Stabilizer section in the Brush panel with an intensity slider and two modes. Smooth averages your recent movement to iron out shaky lines; Rope is the lazy-mouse string. "Finish line on release" draws the rest of the stroke to the cursor when you let go.
- Right-click (without dragging) in the 3D or 2D view opens a color wheel with your recent colors and swatches under the cursor.
- Recent colors now sit right under the color wheel for quick swapping.
- Middle-drag pans in the 3D view without holding a key (Ctrl+middle-drag zooms).
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
