# Changelog

## 0.20.1

- Fixed the direction of Alt+click polygon loops: the loop now runs along the polygon edge nearest the click (click near a top or bottom edge for a horizontal loop, near a left or right edge for a vertical one), as in Blender. It was running across that edge, the opposite way.

## 0.20.0

- New Blender-style **polygon loop select**: with the Select tool, **Alt+click** a polygon in the 3D view or the texture view to select the loop of polygons through it. The loop runs across the polygon edge nearest your click and continues through opposite edges until it reaches a boundary, a triangle or a fork, or closes into a ring. Every Alt+click adds to the current selection, and **Ctrl+Alt+click** removes a loop. Alt+drag still orbits (3D) or pans (texture view). Loops always use whole polygons (not UV islands) and follow quads, so they work best on quad-based meshes.

## 0.19.5

- Fixed an avatar (seen on a Quest copy) being left in an idle or scrambled pose after leaving Play mode, with its clothing and hair stretched across the scene, and Reset not bringing it back. Two causes in the Pose tool and the clothing sync:
  - A bone with no prefab to read its rest pose from used whatever pose the avatar was in at that moment as its "rest", so Reset restored the bad pose. It now falls back to the T-pose stored in the avatar's model file.
  - The clothing sync captured offsets with some bones at rest and others still posed, which later threw the clothing bones far away. It now only uses rest values when every bone has one, and leaves an outfit alone if its captured offsets look wrong (a clothing bone more than 30 cm from its body bone).
- All remembered pose and clothing data is now thrown away whenever Play mode starts or stops and rebuilt a moment after the scene is restored, and nothing is worked out in Play mode.

## 0.19.4

- Fixed outline generation crawling or hanging on big avatars (usually on the last, biggest mesh, the body). The slow parts were an Undo snapshot of the whole mesh with all its blend shapes on every regenerate and resize, the mesh being built twice, and every blend shape's deltas for every vertex being held in memory and copied several times. Meshes are now built once, straight into the asset, no Undo snapshot is taken of them, and blend shape deltas are read from the original mesh only when the outline is written. Welding and simplifying also use exact lookups now instead of string keys.
- The progress bar now shows each stage (building, saving, linking, saving assets) and has a **Cancel** button.
- Rewrote the VRCFury BlendShape Link step. It now runs after all outlines are made, checks the type of VRCFury's fields instead of assuming them, reads the link back, and removes the half-made VRCFury component if anything doesn't match (an empty VRCFury component only causes errors). A problem with the link can no longer stop outlines from being made.
- Fixed two meshes with the same name (for example two "Body" objects) overwriting each other's outline mesh; they now get separate files.
- Fixed outline bounds on skinned meshes being taken from the mesh instead of the renderer, which could make an outline vanish when the camera moved. Fixed the outline material being deleted and recreated on every run (it is now updated in place).
- Generate and Remove now refuse to run in Play mode (the outlines would vanish when you stop, and the Play mode pose would be baked in), and put the avatar's position back if anything moved it during the run.
- Dragging a width slider no longer rebuilds blend shapes for the preview (the real save when you let go still includes them). New **Match outline blend shapes to the originals now** button on the Widths tab.

## 0.19.3

- Fixed regenerating outlines failing with "The object of type 'Mesh' has been destroyed": when the outline mesh already existed, saving rebuilt it in place and discarded the new mesh, but Generate then still read the discarded mesh to decide whether to add a blend-shape link. It now reads the saved mesh, so regenerating works.

## 0.19.2

- New *Copy Pose Between Avatars* tool (Tools › Avi Creator Essentials › Copy Pose Between Avatars…): pick a source avatar in the correct pose and a target avatar stuck in the wrong one, and it copies every bone's local position and rotation across, matched by bone path, in one undo step.

## 0.19.1

- Fixed a compile error in the outline generator (0.19.0 would not compile at all, so Unity kept running 0.18.0 and the even-width toggle was missing). The fix makes the even-width toggle and the 0.19.0 blend-shape link work as intended.
- Fixed regenerated outlines not rendering: saving a regenerated outline could leave stale blend-shape frames sized for the old vertex count, so the SkinnedMeshRenderer refused to draw the mesh. Saving now rebuilds the mesh in place, so the saved data always matches the new mesh.
- New *Expand flat silhouettes* toggle (on by default): the outline now grows the silhouette outward as well as pushing along the normals, so it shows on flat surfaces viewed straight on instead of disappearing. Turn it off to go back to a pure normal push.

## 0.19.0

- Separate outlines now follow blend-shape toggles: Generate adds a VRCFury BlendShape Link to each outline, so toggles and sliders that animate the source's blend shapes drive the outline's too. It runs at upload, after VRCFury's own toggles are baked, so it covers those as well. Turn it off with *Link blend shapes with VRCFury* in the Outline window; without VRCFury installed the outline is made anyway, with a warning. Outlines made before 0.19.0 need one Generate run to get the link.
- New *Even width on scaled objects* toggle (on by default): the outline is pushed along the world-space normal by the exact width, so it stays the same thickness everywhere even when an object or its bones are scaled unevenly. The old width divided by the average scale, which made outlines thicker in some directions than others. Turn the toggle off to go back to the older width.

## 0.18.0

- New **Widths** tab in the Outline & Optimize window: every separate outline on the avatar in one place, each with its own width slider. Drag a slider and the outline changes live in the scene; letting go saves it. Outlines merged into the mesh have no separate object, so they are not listed.
- The right-click **Resize Outline…** menu still works for changing one outline at a time.

## 0.17.0

- Separate outlines are now children of the object they outline, so hiding or disabling a source (for example clothing) hides its outline too, and accessories attached with VRCFury Armature Link carry their outlines with them. This replaces the single "Outlines" parent from 0.16.1, which broke both. Re-running replaces outlines as before, and Remove outlines still cleans up a stale empty "Outlines" parent.
- New **Resize Outline** menu (right-click an outline or its source in the Hierarchy) to change one object's outline width after adding it, without regenerating the whole avatar. The width info is saved next to each outline mesh, so outlines made before 0.17.0 need one Generate run first.
- Separate placement still makes one outline object per mesh, and merged placement is unchanged.

## 0.16.1

- Outlines made as separate objects now all sit under one empty `Outlines` object on the avatar, instead of one object per mesh scattered through the hierarchy. Each mesh still gets its own `…_Outline` object, in the same place it renders; only the parenting changed, so uploads and blend shapes are unaffected. Re-running replaces them as before, and **Remove outlines** takes the `Outlines` parent away too once it is empty.

## 0.16.0

- New **Bake Shaders for Quest** window (Tools › Avi Creator Essentials › Bake Shaders for Quest…, or right-click the avatar in the Hierarchy). It bakes how each PC shader (Poiyomi, lilToon, Standard) makes a material look into a plain texture, builds a Quest material from it (VRChat/Mobile/Toon Standard by default, or Toon Lit / Standard Lite), and can make a `<Avatar>_Quest` copy of the avatar with the baked materials swapped in. Emission bakes into its own texture so the Quest shader can still glow it, and the source normal map carries through. Your PC avatar is never changed. View-dependent effects (matcap, rim light, specular) and animated effects (hue shift, scrolling emission, AudioLink) bake frozen at their current values and will not move afterwards, and the baked lighting is fixed, so the result is closer to the PC look, not identical. The Quest copy still needs a manual pass: remove or replace Quest-rejected components (PhysBones, constraints, PC-only scripts) and switch the build target to Android before uploading.
- New **Mute Toggle** window (Tools › Avi Creator Essentials › Mute Toggle…, or right-click the avatar in the Hierarchy). Pick objects on the avatar and choose whether each one shows while you are muted or unmuted in VRChat. It adds a layer called "ACE Mute Toggle" to the avatar's FX controller, driven by VRChat's built-in MuteSelf parameter, so nothing is added to your Expression Parameters and it costs no sync budget, and other players see the objects change too. Works with an existing custom FX controller, or makes one from the SDK sample FX controller when the avatar still uses the default FX layer. Applying again replaces the previous setup, and **Remove mute setup** takes it all back out.

## 0.15.0

- New **Outline & Optimize** window (Tools › Avi Creator Essentials › Outline & Optimize…, or right-click the avatar in the Hierarchy › Avi Creator Essentials › Add Outline To This Avatar…). Drop in your avatar and press **Generate outline**: it makes a slightly bigger copy of each mesh, pushed out along smoothed normals with the faces flipped (an inverted hull), with its own outline material and color. The flip is baked into the mesh, so it works no matter what shader is on the avatar (Poiyomi, lilToon, Standard, VRChat Mobile…) and on Quest.
- Outline look: width in real centimetres (independent of model scale), color, and an outline shader of your choice (VRChat Mobile Toon Lit or Standard Lite for Quest + PC, Unlit color, any custom shader, or your own outline material). A flat-look option makes lit shaders show a solid color.
- Control over where the outline goes: every mesh and every material slot has its own tick box, with a per-mesh width multiplier and an optional own color. Eyes, lashes, brows, mouth, cutout and transparent materials are skipped by default (you can tick them back on). You can also leave out whole areas by bone (for example the head), fading out smoothly, or paint the outline width in Blender with vertex colors. Running it again replaces the earlier outlines so you can tweak and retry, and **Remove outlines** takes them away (Ctrl+Z works too).
- Placement: as a separate object per mesh (easy to remove), or merged into the original mesh as one more submesh (fewer renderers, best for Quest). The outline follows blend shapes so it stays on the face during visemes and expressions; shapes that barely move it are skipped to save size.
- Outline optimization: weld duplicate vertices (safe, usually saves 30–50%), a Simplify slider, and a triangle budget that simplifies automatically until the outline fits. A live estimate shows the outline's triangles and the avatar's Quest rank with it.
- New **Textures** tab: lists every texture the avatar's materials use with its size and memory, lets you cap sizes (4096 / 2048 / 1024 / 512, or per texture), turn on Crunch compression, mipmap streaming, turn off Read/Write, and add a Quest (Android ASTC 6x6) override, with a before/after memory estimate. Only import settings change; your image files are never edited.

## 0.14.34

- New Audio Link layers: the switch above the layers is now Base color / Emission / **Audio Link**. Audio Link holds every texture a material uses for Audio Link, so you can paint, fill, mask and bake them like any other layer. They are found from the material itself: textures named for Audio Link (such as an Audio Link mask), plus the emission map and mask of every emission slot that has its Audio Link section switched on (Poiyomi's Emission 0-3). When a material has several, a Texture dropdown under the switch picks which one you are editing, and each also has its own row in the Materials list. The Unity console lists what was found for each material when the painter opens.
- Audio Link textures are no longer treated as emission textures, so they are not switched on as a glow map or painted as the emission kind by Whole model.

## 0.14.32

- Fixed the brush missing a mesh whose GameObject is scaled: the hit-test used a matrix that dropped the scale, so for a scaled mesh (including a scaled skinned accessory) the brush aimed at the unscaled position and clicked through it even though it renders in the right place. Skinned meshes now use their full local-to-world matrix (position, rotation, and scale) for hit-testing and painting, same as non-skinned meshes already do.

## 0.14.31

- Fixed the brush not painting objects parented to the avatar (accessories, props) that aren't skinned to its bones: their hit-test and paint data used to be refreshed only when the pose was marked dirty, so the brush kept aiming at where the object was at the last pose change and missed it even though it renders in its new place. Non-skinned meshes now refresh from their live transform every time, so the brush always hits where the accessory actually is. Only meshes whose transform actually moved rebuild the picking tree.

## 0.14.30

- Fixed the painter always opening in a T-pose: pose the avatar in Play mode with Gesture Manager, hit "Capture Play-mode pose" on the painter's start screen, then stop Play mode and open the painter — it opens in that pose, so stripes and paint land right with the arms down. The captured pose is used once, then forgotten.

## 0.14.29

- Fixed the Stripes color popup breaking the tool: clicking a stripe color swatch opened the wheel without its background, misaligned, and froze the editor. The popup was being attached to the panel's top-level tree, outside the window root that carries the stylesheet and theme colors, so it had no background or absolute positioning and its full-window backdrop took part in layout. It now opens through the window's own root (like Major colors) and always attaches inside the styled root.
- Removed the temporary diagnostic logging added in 0.14.28.

## 0.14.28

- Temporary diagnostic logging for the color wheel popup: clicking a stripe or Major color swatch now writes the real positions and sizes to the Unity console (swatch rect, popup anchor, placement, wheel and square sizes, and how many times the placement runs), so the next test shows exactly where the wheel goes wrong instead of another guess. This logging is temporary and comes out once the cause is found.

## 0.14.27

- Fixed the color wheel still not opening on the swatch you clicked: it now opens centered on the swatch itself instead of landing in the wrong place, so the editor doesn't freeze with a wheel you can't see.

## 0.14.26

- Fixed the 2D texture view going blank with a -2147483648% zoom readout: the view fitted itself before it was laid out, the zoom went bad, and it never re-fitted. It now waits for a real layout, and re-fits if the zoom ever goes bad, so the texture view comes back.
- Fixed stripes showing a third color: the pattern left half of every stripe as a gap showing the sweater underneath, which read as a third color. Stripes now cover fully by default, so a pattern only shows the colors in the Stripes menu — lower Stripe width if you want the gaps back. Stripes already painted keep their gaps until you repaint them.
- Fixed the color wheel opening in the wrong place when clicking a stripe or Major color swatch: it now opens on the swatch you clicked.

## 0.14.25

- Fixed the color popup opening in the wrong place and freezing the editor: clicking a stripe color or a Major color swatch used to open the wheel at the top-left of the window, around the material name, and the editor stopped responding to clicks. The swatches were passing the click position in their own space and the popup reads it as a screen position. It now opens with the wheel centered on the swatch you clicked, and closes when you click outside it or press Escape.
- Fixed stripes showing a third color: while a stripe color popup was open, changing the selected color anywhere else also rewrote that stripe's color, so a pattern made with just black and white could pick up the selected color as a third stripe. The popup no longer stays open unnoticed, so the stripe colors only change when you change them in the Stripes menu — a pattern only ever shows the colors listed there.

## 0.14.24

- Fixed the Stripes options not showing when the tool is picked: the tool boxes now follow the current tool on every panel rebuild too, instead of only on the tool-change event, so the Stripes options (and the other tools' boxes) always match the tool in hand.

## 0.14.23

- New stripes tool (P): drag across the model and it lays stripes around it in 3D, so they wrap right no matter how the UVs are laid out — same as gradients, and baked into the layer like any paint. Pick horizontal or vertical, add your own colors (up to 8, click a swatch to change it), and set how many stripes repeat across the drag, how wide each one is, and the offset.
- Stripes work with the rest of the paint setup too: opacity, whole model, and the symmetry mirror all carry over from gradients.

## 0.14.22

- The Major Colors popup now has a hex code box under the wheel: type or paste a hex code (with or without the #) and press Enter to set the row's color, just like moving the wheel. Bad input does nothing — the box just goes back to the current color.

## 0.14.21

- Fixed the 2D texture view sometimes staying blank until a stroke was drawn on the model: a first render that failed or had nothing to show yet used to eat the render request and never ask again, so the view only appeared once a stroke requested a render. It now retries the next few updates until it actually draws, and asks again when there is nothing to show yet instead of settling on white.
- Fixed the color wheel while editing a Major mask: switching to a second row's mask used to drag the wheel to that row's recolor color, and ending the edit left the previous layer stuck in mask-editing mode, so the earlier edit's state broke. The wheel now reads white for the Brush and black for the Eraser the whole time a mask is being edited (and follows the tool if it is switched mid-edit), and ending an edit puts the layer that was active before back, like it should.
- The Major Colors setup is now remembered across reloads: the detected rows, the color picked for each one, and which masks were painted by hand are saved in the project file, so after reopening the window or reloading scripts the panel shows the same rows and colors with the same masks — no Detect and no re-picking. Hand-painted masks survive the reload too, since the layers' masks were already saved and a later Detect now keeps them instead of rebuilding them.

## 0.14.20

- Fixed painting a Major mask with the wrong color: the selected color no longer touches the row's recolor at all while its mask is being edited, and the Brush now adds solid white to the mask while the Eraser takes it away, whatever color the wheel holds — before, a dark color painted a grey mask that barely recolored anything.
- Removed the Cleanup step that eroded every mask 3px away from dark pixels: on the hoodie it cut a pale halo around the paw-box frame when the beige was recolored, punched black squares into the paws and frame when the grey was recolored, and left a white fringe ringing the paws. The rest of Cleanup still runs, and the text stays readable, the box stays free of red and blue fringe, and the shoulder band stays one beige. Existing Major layers keep the mask they were built with — delete the layer or hit Reset and change the color again to rebuild it.

## 0.14.19

- Each Major colors row now has an Edit mask button: it opens that color's mask for painting by hand, so thin lines left behind, fringe bleeding onto a neighboring color, and speckles can be fixed directly. Brush adds to the mask, Eraser removes from it, Ctrl/Cmd erases and Shift paints, and each stroke is one undo. The mask shows tinted red in the texture view, and the recolor updates live on the model in the 3D view. A row's layer is made on the spot if it doesn't have one yet, so editing works before the first color change too.
- Hand-painted masks survive pressing Detect again: re-Detect now adopts the existing "Major: ..." layers by name, and a row whose mask was painted by hand keeps that mask exactly as left, while rows that were never hand-edited get a fresh mask rebuilt from the new detection. This also fixes old Major layers stacking under new masks after a re-Detect. Existing Major layers keep the mask they were built with — delete the layer or hit Reset and change the color again to rebuild it.

## 0.14.18

- Fixed the last Major colors edge problems on the hoodie. The shoulder patch no longer splits off from the beige: a gradient band now stays one color because detection looks at which color each shade actually touches, not just which one it is closest to. The dark Japanese text next to the paw box stays readable when the green is recolored bright green — the green blobs that used to stick out of the strokes are gone. New Cleanup toggle in the Major colors panel (on by default): it smooths each color's mask, so the thin pale weave lines and cyan speckles on a recolored body are gone, the paw-box frame edge no longer leaks blue, and there is no red fringe under the pads. Turning Cleanup off gives the old 0.14.17 masks. Existing Major layers keep the mask they were built with — delete the layer or hit Reset and change the color again to rebuild it with cleanup.

## 0.14.17

- Fixed the paw prints splitting across rows: the pads and their outlines are now detected as one color, so recoloring that row moves the whole print instead of leaving dark outlines or pink specks behind. The pale paw-box background is its own row again, and its name is fixed — it reads "Light Green" now instead of "Dark Green", since a pale color should never be named dark. Same fix for any other pale color that used to pick up a dark name when it collided with a bigger color.
- Fixed the color picker (I) reading white on areas covered by an image or text layer: it now samples the composited color actually shown on screen (base plus every layer on top), so clicking the pink Pancake text sets the current color to that pink instead of the white base underneath. Clicking plain base areas still works the same. The Color recolor tool (C) and its mask are untouched.

## 0.14.16

- Major colors is now its own section in the right panel, above Brush, instead of living inside the Brush section. Same Detect button and rows, same popup with history — just in its own menu where it belongs.
- Fixed the beige splitting in two: the light beige and its darker shading and folds are now one detected color, so changing it recolors the whole hoodie body with no greyish patches left around the pocket, folds, or hem. The paw-box background and the paw-print outlines stay their own separate rows, and the pads are untouched.

## 0.14.15

- Major colors is no longer a tool in the rail: the section is always visible in the right panel, with Detect major colors and one row per color. Clicking a row's new-color swatch opens the plugin's own color wheel popup, with the recent-colors history and saved swatches, instead of Unity's built-in picker; dragging the wheel recolors that color live, and the final color is recorded in history when the popup closes. Detection now lists every distinct color instead of only the biggest ones (down to 0.1%, up to 12 rows), and muted colors are separated properly: light beige, darker brownish-beige, the pale paw-box background and the dark paw pads each get their own row, while all the green shades still count as one green and the beige no longer splits across its hue boundary. The mask no longer claims the 1px blend fringe where two colors meet, so recoloring the beige leaves the sleeve/body seam clean.
- Removed the descriptive paragraphs from the tool option boxes: tool names and their controls speak for themselves, and the hover tooltips still explain each control.

## 0.14.14

- Fixed the Major colors tool leaving thin lines of the old color behind and bleeding onto neighboring colors: the mask is now rasterized straight from the detected color cluster at full resolution, so it covers exactly what detection found — no more seed sampling that missed the fine weave lines or over-reached onto the beige. The Tolerance slider is gone from the Major colors panel (it never affected this mask; it still works in the Color recolor tool).

## 0.14.13

- Fixed the Major colors tool not compiling: the color field lives in UnityEditor.UIElements, which Panels.cs was not importing.

## 0.14.12

- New Major colors tool (palette button in the tool rail): Detect major colors lists the big colors of the selected material — all the shades of the green count as one green, all the beige as one beige — each with its coverage, and changing one recolors only that color as its own Recolor layer. The Recolor mapping now shifts brightness instead of scaling it, with the spread compressed just enough to fit: the area's average becomes exactly the picked color, pale or deep, and the dark folds stay dark and the lit parts stay light instead of crushing flat. Same fix in the Lasso recolor.

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
