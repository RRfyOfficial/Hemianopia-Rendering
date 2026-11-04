# Hemianopia filter (layer 3)

The filter is what makes the simulation. The scene renders completely normally (layer 2), and then this image-space pass hides one half of the visual field, locked to where the eyes point. This follows the advice to keep the standard rendering pipeline untouched and build the condition as a filter on top.

Files:

| File | Job |
|---|---|
| `Assets/Hemianopia_Project/Shaders/HemianopiaFilter.shader` | Per-pixel test: is this pixel on the blind side of the gaze? |
| `Assets/Hemianopia_Project/Scripts/Rendering/HemianopiaController.cs` | Sends the gaze to the shader every frame. Inspector settings and keyboard toggles. |

## 1. What it simulates

Hemianopia removes the same half of the visual field in both eyes. The boundary is the vertical line through the point you're fixating, and it moves with your eyes. Look 20 degrees to the right and the boundary moves 20 degrees to the right.

That's why this is not "black out the left half of the screen." That would be locked to the head. The real condition is locked to the eyes, which is why the filter needs eye tracking.

## 2. How it works, per pixel

**Step 1: pixel to ray.** Each pixel shows the world along one direction from the eye. The shader recovers that direction by undoing the camera's projection and view with the inverse view-projection matrix:

$$\mathbf{P}_{\text{world}} = (VP)^{-1}\,\mathbf{p}_{\text{clip}}, \qquad \hat{\mathbf{d}} = \frac{\mathbf{P}_{\text{world}} - \mathbf{c}}{\lVert \mathbf{P}_{\text{world}} - \mathbf{c} \rVert}$$

where $\mathbf{p}_{\text{clip}}$ is the pixel on the near plane and $\mathbf{c}$ is the eye's position. In code: `ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP)`.

**Step 2: which side of the gaze?** The controller builds a "right" vector that is perpendicular to both the gaze direction $\hat{\mathbf{g}}$ and the head's up vector $\hat{\mathbf{u}}$:

$$\hat{\mathbf{r}} = \frac{\hat{\mathbf{u}} \times \hat{\mathbf{g}}}{\lVert \hat{\mathbf{u}} \times \hat{\mathbf{g}} \rVert}$$

(Unity is left-handed, so up cross forward points to the viewer's right.) Then for every pixel:

$$x = \hat{\mathbf{d}} \cdot \hat{\mathbf{r}}$$

$x > 0$ means the pixel is right of the gaze line, $x < 0$ means left.

**Step 3: blind or not.** With $s = -1$ for a left-side loss and $s = +1$ for a right-side loss:

$$\text{blind} = \begin{cases} 1 & \text{if } s \cdot x \ge 0 \\ 0 & \text{otherwise} \end{cases}$$

and the output is $\text{lerp}(\text{scene}, \text{blindColor}, \text{blind})$.

Geometrically, the boundary is the plane containing the gaze direction and the head's up vector. Seen from the eye, that plane is the vertical line through the fixation point.

**Both eyes, for free.** In VR each eye renders its own image with its own camera, so the shader runs separately per eye with that eye's matrices. Both eyes get the same "right" vector, so both lose the same half of the world. That is exactly the "same half in both eyes" property of real hemianopia.

## 3. Timing

The controller runs inside `GazeProvider.GazeUpdated`, which fires in `Application.onBeforeRender` after the headset's final head update (see [GAZE.md](GAZE.md), section 3). So the boundary is computed from the same head pose the frame is drawn with.

It uses `RawGazeRay`, never `StableGazeRay`. Smoothing adds lag, and if the boundary trails behind a saccade, the person gets a split second to see into the area that should be gone.

## 4. Setup in Unity (one time)

1. Right-click `HemianopiaFilter.shader` > Create > Material. Name it `HemianopiaFilter`.
2. Select `Assets/URPDefaultResources/Default_Forward_Renderer`. All quality levels share this one renderer.
3. Add Renderer Feature > Full Screen Pass Renderer Feature.
   - Pass Material: `HemianopiaFilter`
   - Injection Point: After Rendering Post Processing
   - Requirements: None
   - Fetch Color Buffer: **on**. It's hidden by default. Open the feature's More (⋮) menu and enable Advanced Properties to see it.
4. Add `HemianopiaController` to the `Gaze` object in the `Sim` scene.

## 5. Settings and keys

| Setting | Default | Keyboard | Controllers |
|---|---|---|---|
| `filterEnabled` | on | **H** toggles | **A, B, X or Y** toggles |
| `blindSide` | Left | **B** flips | **Either index trigger** flips |
| `blindColor` | black | | See limitations |

`ToggleFilter()` and `FlipBlindSide()` are public, so a menu or an experiment script can call them later.

## 6. Known limitations (v0)

- **Black is not "nothing."** Real patients don't see a black wall. They perceive an absence, often without noticing it, and the brain can even fill across the boundary. A screen can't show "nothing," because a black pixel is still a pixel. What the blind half should look like is a v1 research question.
- **Hard edge.** Real boundaries can be graded.
- **No macular sparing.** Many patients keep a small central region around fixation. Planned for v1, from Tamanna's spec.
- **The boundary follows head roll exactly.** Real eyes partly counter-rotate when the head tilts. Small effect, ignored for now.
- **Scene view.** In Play mode the Scene view also gets filtered using the headset's gaze, so it can look odd there. The Game view and headset are what matter.
- **Edit mode.** The filter stays off outside Play mode, because nothing sends it gaze.

## 7. How to test

1. No headset: press Play. Everything left of the red mouse dot goes black, and the boundary follows the mouse.
2. Quest Pro over Link: press Play. The left half of the world disappears, and the boundary follows your eyes.
3. Press **H** (or A, B, X, Y) to compare with and without the filter. Press **B** (or either index trigger) to flip sides.
4. Look at a cube, then move your eyes to the left. The cube should vanish once your gaze passes it.
