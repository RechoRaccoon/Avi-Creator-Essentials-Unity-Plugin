# Changelog

## 0.14.11

- Color recolor (C) now works in HSV instead of scaling RGB: bright pixels keep the target color's hue and saturation at full brightness instead of clipping to white, so pale colors shift evenly across the whole area. Same fix in the Lasso recolor.

## 0.14.10

- Fixed the Color recolor tool (C) washing pale colors out to white in the highlights: the recolor now scales the chosen color by each pixel's brightness instead of adding the same brightness to every channel, so bright spots stay pink instead of going white. Dark colors already looked right and are unchanged. Same fix in the Lasso recolor.

## 0.14.9

- Color recolor tool (C) now makes a Recolor layer by default: click a color and every pixel of that color becomes the color wheel's color, shading kept, and turning the wheel follows live. Clicking again picks a different color for the same layer instead of making a new one; Shift-click adds more colors to the selection and Ctrl-click (Cmd on Mac) takes colors away. The color match is now measured by hue, so the lit and shaded parts of the same color are all picked up instead of scattered patches. Fixed the red selection tint showing on the unselected area instead of the selected one.

## 0.14.8

- New Color recolor tool (C): click a color on the texture or the model and every pixel of that color on the selected layer gets its own Hue/Saturation layer, masked to just those pixels — the two green sleeves change together even though they are separate islands. Shift Hue in the Adjust section and the hue changes while shading and detail stay. The Tolerance slider catches the shade variants, and Merge down bakes it into the layer below.

## 0.14.7

- Avatar templates: copy one avatar's full layout onto another avatar instead of redrawing it from scratch. With the finished avatar open, right-click the Save button and pick "Save as template…" — it saves every layer stack (layer order, names, opacity, blend, placement, hidden state and pixels) as a template named after that avatar. Then, on the start screen, pick the other avatar in the Avatar field and press "Apply to avatar" on the template in the new Templates list. Layers land on the matching materials by material name (your avatars share the same names), so eye layers land on the new avatar's eyes, and you just edit from there. Materials with no match on the new avatar are skipped and listed, never created; the pose isn't copied, and the new avatar's own project file is untouched — applying only changes its open session, so save normally afterwards. The whole apply is one Ctrl+Z step, and texture sizes stay the target avatar's own (the template's pixels are resampled into them).

## 0.14.6

- New Lasso recolor tool (L): draw a freehand loop around part of a layer in the texture (2D) view and everything inside the loop is recoloured to the primary colour, while everything outside is untouched. Transparent pixels are left alone and alpha is preserved, shading is kept, and — unlike the normal Recolor adjustment, which is a multiply tint and can never change black (black x any colour = black) — black turns into the new colour too: black ink gets the new colour at full strength, since black text's shape lives in its alpha, not its luminance. So lassoing the black "Pancake" text and picking pink turns the letters pink while the pink heart outside the loop stays pink. Image layers are rasterized first (the same as painting on one), as part of the same undo step, so one Ctrl+Z undoes the whole thing. The loop draws in amber so it reads as a recolor rather than the blue UV selection.

## 0.14.5

- Fixed text layers previewing the wrong letters: the preview showed mirrored / jumbled letters instead of what was typed. The glyph UV code only flipped the V axis for atlas-flipped glyphs, but Unity flips flipped glyphs in BOTH U and V (they are stored rotated 180 degrees in the atlas), so every flipped glyph came out mirrored horizontally. With Arial's private, freshly-packed atlas almost no glyph was flipped, so it looked right; LegacyRuntime shares its atlas with the whole editor UI, so many glyphs come back flipped and the preview turned to nonsense. Flipped glyphs now sample backwards on both axes. The redundant second font-texture request between capturing the glyph UVs and drawing them is also gone, since a texture regeneration there would have invalidated the captured UVs.
- The text box still can't draw a colour emoji itself (IMGUI uses the editor font, which has no colour emoji), so the window now shows the emoji that are actually in your text as their Twemoji pictures right under the box — a pasted emoji is visible there and in the live preview, even though the box shows a blank gap for it.

## 0.14.4

- Fixed text layers never previewing (and the console spamming "Arial.ttf is no longer a valid built in font" every second): TextImage asked Unity for the built-in Arial font first, but in this Unity version that lookup throws an ArgumentException instead of returning null, so rendering died before any word was drawn — even plain "Pancake" showed "Nothing to preview yet". It now tries LegacyRuntime.ttf first, and every font lookup is guarded so the search can never throw and always falls through to the next font. The text window also has a Paste button that appends whatever is on the clipboard (emoji included) even if the text field itself drops a pasted emoji.

## 0.14.3

- Fixed hidden materials showing white instead of hiding: 0.14.2 gave hidden slots a fully-transparent material (Unlit/Transparent, alpha 0), but it still rendered the polygons white in the painter's 3D view — hiding was depending on alpha blending that the view doesn't honour that way. Hidden slots now use a dedicated invisible shader (Hidden/AviCreatorEssentials/Invisible) that writes no colour and no depth at all (ColorMask 0, ZWrite Off), so the polygons cannot show up regardless of blending or alpha, and they still don't occlude what's behind them. The brush still passes through to what's underneath, and unhiding still restores the real material exactly, including the emission material while the emission view is on. The emission refresh also skips hidden slots now, so it never writes textures onto the shared invisible material.

## 0.14.2

- Fixed hidden materials turning pink instead of hiding: 0.14.0 set hidden material slots to null, but Unity renders a null material slot with the magenta error material, so the polygons stayed visible in pink. Hidden slots now get one shared fully-transparent material (Unlit/Transparent, alpha 0, ZWrite Off), so the polygons are truly invisible and don't occlude what's behind them. The brush still passes through to what's underneath (the picking BVH skip from 0.14.0 is unchanged), and unhiding still restores the real material exactly, including the emission material while the emission view is on.

## 0.14.1

- Fixed blendshapes not moving the painting hit mesh: opening a mouth (or any blendshape) with the Pose tab's sliders showed the mouth open in the 3D view, but the brush still hit the closed mouth — it painted over the lips instead of inside. The hit test uses a baked copy of the mesh (MeshCache), and the Pose tab's blendshape path set the weights on the clone without telling the cache the pose had changed, so the baked copy kept the old vertices. It now marks the pose dirty, so the hit mesh is re-baked from the current blendshape weights on the next brush stroke. The Scene view overlay's blendshape sliders already did this, and bone poses already mark the cache dirty too, so both are covered now.

## 0.14.0

- Hide materials: every row in the Materials list now has an eye toggle, like hiding a layer. Hiding a material hides its polygons in the 3D view, so the brush can reach — and paint — the things underneath it that it was blocking: hidden polygons can't be hit by the brush, and they don't occlude what's behind them either. The material stays in the list (dimmed, with a "Show all materials" button while anything is hidden), its layers and texture are untouched, and unhiding restores it exactly. Hiding applies to every texture set of the same material (base, emission...), since they share the same polygons. The 2D view is untouched: each material has its own texture there, so nothing blocks anything. Like hiding a mesh, it is a view toggle, not undoable — and it is saved with the project, so hidden materials stay hidden when the project is reopened. It only affects the painter, never the avatar.

## 0.13.3

- Fixed the new compile error in 0.13.2 (`TextImage.cs(90,30)`, error CS0136): the `DrawTexture` fix introduced a `foreach (var g in glyphs)` loop, but `Render` already has a pre-existing local named `g` (the green tint channel, `byte g`), and C# forbids a nested-scope local sharing a name with an enclosing-scope local — even though the `byte g` is declared later in the method. The loop variable is now `glyph`, the pre-existing `g` is untouched. This also explains the still-missing Blendshapes section: the assembly was still broken, so nothing in the plugin could run. The Pose tab's Blendshapes section is already in the code, right under the Reset/Mirror pose buttons, with a "No blendshapes on this avatar." message when the avatar has none.

## 0.13.2

- Fixed the 0.13.1 compile error that broke the whole plugin: `TextImage.cs(91)` called `Graphics.DrawTexture` with a source `Rect` as the third argument, but the only 3-argument overload takes a `Material` there (`error CS1503: cannot convert from 'UnityEngine.Rect' to 'UnityEngine.Material'`), so the entire editor assembly failed to compile. It now uses the overload that takes the source rect plus border sizes (`0, 0, 0, 0`), so text layers render again. This also explains the missing Blendshapes section: the Pose tab's Blendshapes section was already in the code, but nothing in the plugin could run while the assembly failed to compile. The section is always shown, right under the Reset/Mirror pose buttons — with a "No blendshapes on this avatar." message when the avatar has none, so missing and none are easy to tell apart.

## 0.13.1

- Fixed the Blendshapes section not showing: 0.13.0 only put it in the Scene view's Bone Posing overlay, but the painter window's Pose tab has its own panel — and that's the one used while texturing. The Pose tab now has its own "Blendshapes" section right under the Reset/Mirror pose buttons, with one slider (0–100) per blendshape on the avatar. It drives the painter's preview clone and the real Scene view avatar follows, one drag is one Ctrl+Z step on the painter's undo stack, and "Reset blendshapes" restores the weights from when the session opened.
- Emoji in text layers now render as real colour pictures instead of blank boxes: emoji are drawn from Twemoji PNGs (Twemoji by Twitter, CC-BY 4.0), downloaded once per emoji from the Twemoji CDN and cached in the project's Library folder, so they show correctly in the text window's live preview and in the final layer on the model. Plain symbols the font does have (♥ ★ ☺) still render from the font. Offline, an emoji that has never been downloaded yet stays blank (as before) and the window offers a retry; the Add button waits while emoji are still downloading so a layer can't be added with its emoji missing.
- Fixed the compiler warning in TextImage.cs: `CharacterInfo.flipped` is obsolete, so flipped glyphs are now detected from the glyph's UVs instead.

## 0.13.0

- Blendshapes in Posing mode: the Bone Posing panel has a new "Blendshapes" section with one slider (0–100) per blendshape on the avatar, grouped by mesh. Drag a slider and the mouth (or anything else) opens live on the avatar in the Scene view while you texture — and the painter's 3D view follows too, since the weights are mirrored onto its preview clone. One drag is one Ctrl+Z step, prefab overrides are recorded like bone edits, and "Reset blendshapes" puts the weights back to how they were when the avatar was first picked. Note: an Animator or animation preview can fight the sliders (it drives the same weights), and blendshapes on meshes the avatar doesn't have in the scene can't be driven this way.
- Text layers: the Layers image button has a new "Text layer (words on the model)…" option. Type words, pick a size, colour and bold, and they become an image layer — drag it on the model in 3D or in the 2D texture view, scale and rotate it with the Image tool, and use the same placement options (On texture / Projected / Wrapped) and Rasterize as any image layer. The "Edit text…" button in the image panel re-opens the words for a text layer. Text saves with the project like any image layer. Note: most emoji show as blank boxes — Unity's built-in font has no colour emoji; plain symbols the font does have (♥ ★ ☺) work.

## 0.12.1

- Fixed the live mirror showing the wrong layers: a mirrored material's Layers panel listed its own ignored layer stack (in the reported case, 3 layers with different names, no Recolor 1) instead of the source's. It now lists the source material's exact layer stack, read-only, so the list matches what the material displays. Layer actions and properties are disabled while linked; edit the source material to change both.
- Live mirror has a "Flip horizontally" toggle (Materials section, under "Live mirror from", on by default): the mirrored image is flipped in the texture, but some eye meshes already mirror the texture through their UVs, so the flip cancelled out on the model and the eye looked unflipped. Turn the toggle off for those and the mirror copies straight across. The bake follows the same setting, and the choice is saved with the project (older saves keep flipping, as before).

## 0.12.0

- Live mirror between two materials: in the Materials section, set "Live mirror from" on one material (e.g. the right eye) to another (e.g. the left eye) and its display becomes the source's full composite flipped horizontally — every paint layer, mask, and hue/saturation/lightness adjustment included — and stays in sync as you edit the source. Painting on the mirrored material is blocked with a hint to paint the source instead, so no stroke can vanish into an ignored layer stack. The link is saved with the project, survives reloads, and bakes the mirrored result. The mirrored material's own layers are kept but ignored while linked, so unlinking restores them. Cycle links are refused.
- Note: this is a horizontal flip only, not a color invert — inverting colors would make the second eye's colors wrong.

## 0.11.4

- Fixed the painter opening with no brushes, settings or layers on a fresh window: 0.11.3's panel-width clamp registered on the right panel before it was created, throwing a NullReferenceException that stopped the whole workspace build (empty tool rail, no right panel, no status bar). The panel is now created before its resize handling is set up.
- Fixed the update button offering "Update to 0.11.3" when already on 0.11.3: the version it compared against was hardcoded to 0.8.0. It now reads the installed package version.

## 0.11.3

- Round brush is now solid by default (Hardness 1), instead of fading at the edge.
- Fixed right-panel sliders overflowing past the panel's right edge; the panel also can't be dragged wider than the window anymore.

## 0.11.2

- Right-click a layer and choose "Save layer as image…" to save just that layer's pixels, with its mask applied, as a PNG in the project.
- Right-click a layer and choose "Add to emission" to copy it onto the same material's emission texture as a new layer, so what it draws glows. (Not offered when the material has no emission slot.)
- Select tool: the new "To emission" button (in the Select tool's options, or next to the selection count in the Layers section) copies the selected area's colors onto the emission texture as a new layer, so just the selected parts glow — for example, select the eyes and make only the eyes glow.

## 0.11.1

- Fixed the gradient tool ignoring the mirror (symmetry) toggle: with symmetry on, a gradient dragged on one side of the model is now mirrored on the other side too (across the chosen mirror axis), and the drag preview shows the mirrored guide. The side you drag on is unchanged.

## 0.11.0

- Whole model (Brush section, on by default): brushes, gradients and stars carry on across every material that uses the same kind of texture, each on its selected layer, so they cover the entire avatar. Replaces "Paint every material".
- Paint several textures at once: Ctrl-click (or Shift-click) textures in the Materials section, such as a material's base color and emission. Brushes, gradients and stars paint all selected textures; the texture view paints the selected textures of the same material together.
- Stars go on every selected texture (and the whole model), each with its emission when Glow is on.
- Adjustment layers now change only the layer directly below them. "Only the layer below" in the Adjustment panel switches one back to changing everything below. Recolor and Match measure just that layer.

## 0.10.0

- Gradient tool (D): drag across the model for a gradient laid out in 3D, not on the texture, so it runs smoothly across UV seams. Linear, Radial and Reflected; the paint color fades out or blends into the second color. Paints the selected layer or mask, stays inside a UV selection, and can run across every material.
- Wet paint (Brush › Paint): dabs build up like real paint instead of laying down one flat color. Bristle streaks along the stroke, color mixing that picks up and drags the colors underneath, a different shade per bristle, and canvas grain. Presets for fur strokes, fur tufts, oil paint and soft blending.
- Taper start / Taper end: strokes thin to a point without a pen, for fur and hair.
- New brush tips made of thin lines: Bristles, Dry Brush, Fur Strands and Fur Tuft. They turn with the stroke.
- Stars: a new section that scatters glowing stars over the model in 3D (specks, halos, four-point sparkles, two colors, nebula clouds) onto a new layer, and onto the emission texture so they glow. Also saves a tiling star texture and a star matcap for parallax / matcap effects in your avatar shader.

## 0.9.2

- Fill Mask Shape: the area outside the shape is now black by default instead of transparent, like a normal mask. Pick another color under "Outside the shape", or turn its alpha down to 0 to keep it transparent.

## 0.9.1

- Fill Mask Shape: masks that are a colored image on a black background (like a painted texture with everything else black) now give a solid shape. Before, the shape was taken from the mask's brightness, so dark or colored parts came out see-through. Auto now picks the new "Anything not black (solid)" mode for these; black-and-white masks still use brightness and transparent masks still use alpha.

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
