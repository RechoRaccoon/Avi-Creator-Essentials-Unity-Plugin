# Avi Creator Essentials

Unity editor tools for VRChat avatar creators:

- **Avatar Painter**: paint textures directly on your avatar in 3D or on the flat texture in 2D, with layers, masks, emission painting and no visible seams.
- **Bone Posing**: Blender-style bones drawn over your avatar in the Scene view. Click a bone, rotate it, and record it with Unity's Animation window.

Made for the VRChat Creator Companion's Unity version (2022.3) with the Built-in Render Pipeline. It works with Poiyomi, lilToon, Standard and most other avatar shaders.

## Install

Pick one:

- **VRChat Creator Companion (recommended, easiest to update):** open the [listing page](https://rechoraccoon.github.io/Avi-Creator-Essentials-Unity-Plugin/) and press **Add to VRChat Creator Companion**. Then, in the VCC, open your project's *Manage Project* page and add **Avi Creator Essentials**.
- **Package Manager:** Window › Package Manager › **+** › *Add package from git URL…* and paste:
  ```
  https://github.com/RechoRaccoon/Avi-Creator-Essentials-Unity-Plugin.git
  ```
- **Download:** grab the zip from [Releases](https://github.com/RechoRaccoon/Avi-Creator-Essentials-Unity-Plugin/releases), unzip it, and drag the folder into your project's `Assets` folder.

Everything is editor-only, so it never ends up in an avatar upload.

### Updating

The tool checks GitHub once a day. When a new version is out, an **Update** button appears in the painter's toolbar (and at the bottom of the Viewport section). What it does depends on how you installed:

- **VCC:** press the update arrow next to Avi Creator Essentials on your project's *Manage Project* page.
- **Git URL:** the button updates it for you.
- **Manual copy:** the button offers to switch you to the VCC listing, so future updates are one click.

## Avatar Painter

Open it from **Tools › Avi Creator Essentials › Avatar Painter**, or right-click your avatar in the Hierarchy › **Avi Creator Essentials › Paint This Avatar**. Then press **Start painting**.

The painter works on a private copy of your avatar. Your scene, materials and textures are not touched until you press **Bake**.

**Basics**

| Do this | How |
| --- | --- |
| Paint / erase | Left-drag. **B** brush, **E** eraser |
| Orbit / pan / zoom | Right-drag / middle-drag (or Shift+right-drag) / scroll |
| Brush size | **[** **]**, Ctrl+scroll (down grows), or Ctrl+right-drag |
| Quick color | Right-click (without dragging) for a color wheel with your recent colors |
| Move part of a layer | **V**, draw around it (or click a color), drag / scale / rotate / warp, **Enter** to apply |
| Steadier lines | Brush → Stabilizer: set the intensity, Smooth or Rope mode |
| Opacity | **1** … **0** |
| Fill an area | **G**, then click a UV island, mesh part, material or similar color |
| 3D gradient | **D**, then drag across the model |
| Pick a color | **I**, then click |
| Undo / redo | Ctrl+Z / Ctrl+Y |
| Save | Ctrl+S |

- **Brush size is in centimetres on the model**, so it doesn't change with zoom or UV layout.
- **No seams:** paint is worked out from the 3D surface, so both sides of a UV seam match exactly.
- **Only visible surfaces** stops paint from going through to hidden areas such as the inside of sleeves. Turn it off under Brush › Projection.
- **Symmetry** (top bar) mirrors your strokes left to right.
- **Whole model** (Brush section, on by default): brushes, gradients and stars carry on across every material that uses the same kind of texture, each on its own selected layer, so you can paint over the entire avatar. Turn it off to paint only the textures selected in Materials.
- **Several textures at once:** Ctrl-click textures in the Materials section (Shift-click for a range), for example a material's base color and its emission. Brushes, gradients and stars then paint all of them; in the texture view, the selected textures of the same material are painted together.

**Painting like paint.** Brush › **Paint** turns on **Wet paint**: overlapping dabs build up like real paint instead of laying down one flat color, and strokes can carry texture:

- **Bristles** streak the stroke with separate bristle lines running along it (**Bristle detail** goes from a few thick ones to many fine ones). This works with any tip.
- **Color mixing** makes the brush pick up the colors it passes over and drag them along, so dark fur blends into glowing green the way it does in a painting.
- **Color variation** gives each bristle a slightly lighter or darker shade.
- **Canvas grain** adds a rough texture the paint catches on. It is fixed to the model, so strokes on top of each other share it.
- **Taper start / Taper end** make strokes thin to a point without a pen, for fur and hair. The tapered end appears when you let go.
- The presets (**Fur strokes**, **Fur tufts**, **Oil paint**, **Soft blend**, **Plain**) are good starting points.

New tips made of thin lines: **Bristles**, **Dry Brush**, **Fur Strands** and **Fur Tuft** (a clump of pointed hairs, for fluffy edges). They always turn with the stroke.

**3D gradients.** Press **D** (Gradient tool) and drag across the model from where the gradient starts to where it ends. It is laid out in 3D on the model, not on the texture, so it flows smoothly across UV seams. Choose **Linear**, **Radial** (spreads out in a sphere from where you start) or **Reflected**, and fade the paint color out or blend it into the second color. It paints the selected layer or mask of every texture it reaches (see *Whole model* below) and stays inside a UV selection. With **Symmetry** on (top bar), the gradient is mirrored on the other side of the model too.

**Stars and space looks.** The **Stars** section scatters glowing stars over the model: tiny specks and bigger stars with soft halos, four-point sparkles, two star colors and optional nebula clouds. They are placed in 3D on the avatar's rest pose (like Local Pos), so they spread evenly across UV seams, and baked into a new layer, so they stay stuck to the model and work in any shader. With **Glow** on, they also go on the emission texture so they shine in the dark. The new layer updates live while you change the settings (until you do something else); add a mask to keep stars only where you want them.

Stars that shift as you look around (parallax, matcap, World Pos) are worked out live by the avatar's shader, so they can't be painted into a texture. For those, **Save tiling texture…** makes a seamless star texture for a parallax or Local / World Pos slot, and **Save matcap…** makes a star matcap with a glowing rim to add on top (Add or Screen blend).

**Layers.** Paint layers, fill layers (solid color revealed where you paint) and image layers, with blend modes, opacity and masks. Right-click a layer for every option, and drag layers to reorder them.

**Emission.** Every material with an emission slot has a **Base color / Emission** switch above its layers. The sun button in the toolbar shows only what glows.

**Selecting UV areas.** Press **S** (Select tool). Click a UV island, or drag a box or lasso, in the texture view or on the model. Shift adds and Ctrl removes. While something is selected, brushes, erasers and fills only paint inside it. Ctrl+D clears the selection, Ctrl+A selects everything and Ctrl+I inverts it.

**Recoloring and matching colors.** Adjustment layers change the color of an area without painting over it, so shading, highlights and patterns stay. An adjustment layer changes only the layer directly below it (shown as "↓ … only" in the layer list); turn off **Only the layer below** in its panel to change everything below instead. Select an area first (**S**, then click a UV island), then press the adjustment button in the Layers section:

- **Recolor** gives the area a new overall color. While the layer is selected, the color wheel edits it live, so you can just pick colors until it looks right.
- **Hue / Saturation / Lightness** shifts the existing colors.
- **Match colors** makes the area look like another one, shade for shade (dark rim, mid tones and highlights all line up). Example for two different-colored eyes: select the eye you want to change, add a *Match colors* layer, then select the other eye and press **Use selected area as reference**.

To make two sides identical instead, use **Mirror texture** in the Layers **⋯** menu: it copies the left half of the texture onto the right half (or any other direction) as a new layer. With an area selected, the **Mirror…** button next to the selection count copies just that area to its flipped spot on the texture. This works when the UV layout is symmetric, as on most avatars.

Each adjustment layer remembers its area, so you can stack one per eye, hide them, or change them later, like in Substance.

**Images.** Import a PNG or JPG from the Layers section. Place it on the texture, or project it onto the model like a sticker.

**Flattening a material.** Right-click a material in the Inspector (or open its ⋮ menu) → **Flatten to Main + Emission Textures (Avi Creator Essentials)…** combines the main texture and color, every normal map (baked in as shading) and the emission into one main texture and one emission texture on the avatar's UVs, and can make a flattened copy of the material that uses them. Effects the shader computes live (decals, color masking, hue shift, matcaps, rim light) aren't included.

**Filling a mask's shape with another texture.** Right-click a mask texture in the Project window › **Avi Creator Essentials › Fill Mask Shape With Texture…**, then pick or drop the texture to fill it with. It makes a new PNG next to the mask in the same shape, showing the new texture where the mask is white, opaque, or (for a colored image on black) anything that isn't black, and black everywhere else. You can pick whether the shape comes from the mask's alpha, brightness, everything that isn't black, or a single channel, invert it, tile the fill texture, and choose the color outside the shape (set its alpha to 0 for transparent).

**Baking a Local Pos / World Pos texture.** If your material already shows a texture or normal map with Poiyomi's *Local Pos* (or *World Pos*) UV mode, right-click the material in the Inspector (or open its ⋮ menu) and choose **Bake Local Pos to UV (Avi Creator Essentials)…**. It writes a normal UV texture that looks exactly like the shader shows it with the avatar standing still, but stays stuck to the model when it moves, and switches the material over to it.

**Wrapping a texture around the model (cardboard, fabric, scales...).** Shader options like Poiyomi's *World Pos* or *Local Pos* make a tiling texture flow over the model without UV seams, but it slides when the avatar moves. Layers › image button › **Wrap tiling texture around the model…** does the same projection once (pick the texture from the material's slots, the project or a file; a slot already set to Local Pos / World Pos is matched exactly), on the avatar's rest pose, and writes it into each material's own UV layout, so it looks the same but stays stuck to the model.

- Normal maps are detected automatically and go into each material's normal map slot (one is added if a material has none). They are converted to each mesh's tangent space, so the bumps light correctly everywhere, and the blend mode *Normal map combine* keeps the material's own normal detail underneath.
- Pick **Every material** so the pattern lines up across body and clothes. Edits to one copy change them all (turn off *Same settings on all materials* to change one alone).
- With the Image tool, drag on the model to slide the pattern, Shift-drag to scale it and Ctrl-drag to rotate it. The Image panel also has tile size, blend sharpness, one-side projection (Side / Top / Front, like a planar Local Pos), bump strength and a DirectX green-channel switch.
- Press **Bake** to save the finished textures and put them on the avatar. Then set the material's normal map UV mode back to UV0 in Poiyomi.

**Custom brushes.** Click **+** after the brush tips and pick a PNG or JPG. White (or opaque) areas paint.

**Saving and baking.** **Save** keeps your layers in an `AviCreatorEssentialsProjects` folder next to `Assets`. **Bake** writes finished PNGs to `Assets/Avi Creator Essentials Data/Baked` and can put them on your avatar using copies of your materials.

## Bone Posing (Scene view)

1. Select your avatar in the Hierarchy.
2. **Tools › Avi Creator Essentials › Bone Posing (Scene View)**. You can also right-click the avatar › *Avi Creator Essentials › Pose In Scene View*, or pick the tool from the Scene view's Tools overlay.
3. Click a bone, then rotate it with the handle. Switch to Move in the *Bone Posing* panel to move it instead.

To make an animation, open **Window › Animation › Animation**, select your avatar, create or pick a clip and press record. Every bone you rotate or move becomes a keyframe. Applying a saved pose while recording keys every bone it changes.

The *Bone Posing* panel also has reset bone or whole pose (back to the model's rest pose), mirror pose, humanoid-only display, bone size, and saved poses. Saved poses are shared with the painter.

Shortcuts: **Alt+R** resets the bone's rotation, **Alt+G** resets its position, and the handle follows Unity's Local/Global setting.

To stop posing, press **Exit posing** in the panel or switch to any other tool (**W**, **E**, **R**...).

### Clothing armatures

Outfits often come with their own copy of the armature (Hoodie › Armature › Hips › ...). Posing finds these automatically and pairs their bones with the body's by name, the same way VRCFury's Armature Link does. It handles names like `Hoodie_Spine` or `Spine.001`, and outfits that skip a bone such as UpperChest. Bones that only exist on the outfit, like hoodie strings or skirt bones, stay attached to their parent and move with it.

- **Keep clothing in sync** (on by default): clothing follows the body live while you pose or scrub an animation, without changing your hierarchy. Only the body bones need keys; at upload, VRCFury merges the outfit as usual.
- **Merge into body armature…** really parents the clothing bones under the body bones, for setups that don't use VRCFury. Prefabs are unpacked first because Unity requires it. Ctrl+Z undoes the whole merge, and **Unmerge clothing** puts everything back (until Unity is restarted).

The Avatar Painter's posing uses the same matching (*Clothing follows the body* in its Pose panel), so outfits stay on while you pose for painting.

## Themes

The painter follows Unity's light or dark skin by default. Use the half-circle button in its toolbar, or Viewport › Theme, to force Dark or Light.

## Requirements

- Unity 2022.3 (VRChat's current version), Built-in Render Pipeline
- A DirectX 11 class GPU

## License

GPL-3.0. See [LICENSE.md](LICENSE.md).
