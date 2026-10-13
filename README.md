# Avi Creator Essentials

Unity editor tools for VRChat avatar creators:

- **Avatar Painter**: paint textures directly on your avatar in 3D or on the flat texture in 2D, with layers, masks, emission painting and no visible seams.
- **Bone Posing**: Blender-style bones drawn over your avatar in the Scene view. Click a bone, rotate it, and record it with Unity's Animation window.

Made for the VRChat Creator Companion's Unity version (2022.3) with the Built-in Render Pipeline. It works with Poiyomi, lilToon, Standard and most other avatar shaders.

## Install

Pick one:

- **Package Manager:** Window › Package Manager › **+** › *Add package from git URL…* and paste:
  ```
  https://github.com/RechoRaccoon/Avi-Creator-Essentials-Unity-Plugin.git
  ```
- **Download:** Code › *Download ZIP*, unzip it, and drag the folder into your project's `Assets` folder.

Everything is editor-only, so it never ends up in an avatar upload.

## Avatar Painter

Open it from **Tools › Avi Creator Essentials › Avatar Painter**, or right-click your avatar in the Hierarchy › **Avi Creator Essentials › Paint This Avatar**. Then press **Start painting**.

The painter works on a private copy of your avatar. Your scene, materials and textures are not touched until you press **Bake**.

**Basics**

| Do this | How |
| --- | --- |
| Paint / erase | Left-drag. **B** brush, **E** eraser |
| Orbit / pan / zoom | Right-drag or middle-drag / Shift+drag / scroll |
| Brush size | **[** **]**, Ctrl+scroll, or Ctrl+right-drag |
| Opacity | **1** … **0** |
| Fill an area | **G**, then click a UV island, mesh part, material or similar color |
| Pick a color | **I**, then click |
| Undo / redo | Ctrl+Z / Ctrl+Y |
| Save | Ctrl+S |

- **Brush size is in centimetres on the model**, so it doesn't change with zoom or UV layout.
- **No seams:** paint is worked out from the 3D surface, so both sides of a UV seam match exactly.
- **Only visible surfaces** stops paint from going through to hidden areas such as the inside of sleeves. Turn it off under Brush › Projection.
- **Symmetry** (top bar) mirrors your strokes left to right.

**Layers.** Paint layers, fill layers (solid color revealed where you paint) and image layers, with blend modes, opacity and masks. Right-click a layer for every option, and drag layers to reorder them.

**Emission.** Every material with an emission slot has a **Base color / Emission** switch above its layers. The sun button in the toolbar shows only what glows.

**Selecting UV areas.** Press **S** (Select tool). Click a UV island, or drag a box or lasso, in the texture view or on the model. Shift adds and Ctrl removes. While something is selected, brushes, erasers and fills only paint inside it. Ctrl+D clears the selection, Ctrl+A selects everything and Ctrl+I inverts it.

**Images.** Import a PNG or JPG from the Layers section. Place it on the texture, or project it onto the model like a sticker.

**Custom brushes.** Click **+** after the brush tips and pick a PNG or JPG. White (or opaque) areas paint.

**Saving and baking.** **Save** keeps your layers in an `AviCreatorEssentialsProjects` folder next to `Assets`. **Bake** writes finished PNGs to `Assets/Avi Creator Essentials Data/Baked` and can put them on your avatar using copies of your materials.

## Bone Posing (Scene view)

1. Select your avatar in the Hierarchy.
2. **Tools › Avi Creator Essentials › Bone Posing (Scene View)**. You can also right-click the avatar › *Avi Creator Essentials › Pose In Scene View*, or pick the tool from the Scene view's Tools overlay.
3. Click a bone, then rotate it with the handle. Switch to Move in the *Bone Posing* panel to move it instead.

To make an animation, open **Window › Animation › Animation**, select your avatar, create or pick a clip and press record. Every bone you rotate or move becomes a keyframe. Applying a saved pose while recording keys every bone it changes.

The *Bone Posing* panel also has reset bone or whole pose (back to the model's rest pose), mirror pose, humanoid-only display, bone size, and saved poses. Saved poses are shared with the painter.

Shortcuts: **Alt+R** resets the bone's rotation, **Alt+G** resets its position, and the handle follows Unity's Local/Global setting.

## Themes

The painter follows Unity's light or dark skin by default. Use the half-circle button in its toolbar, or Viewport › Theme, to force Dark or Light.

## Requirements

- Unity 2022.3 (VRChat's current version), Built-in Render Pipeline
- A DirectX 11 class GPU

## License

GPL-3.0. See [LICENSE.md](LICENSE.md).
