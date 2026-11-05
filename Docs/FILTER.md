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

$$\mathbf{P}_{\text{world}} = (VP)^{-1}\,\mathbf{p}_{\text{clip}}, \qquad \hat{\mathbf{d}} = \frac{\mathbf{P}_{\text{world}} - \mathbf{c}_e}{\lVert \mathbf{P}_{\text{world}} - \mathbf{c}_e \rVert}$$

where $\mathbf{p}_{\text{clip}}$ is the pixel on the near plane and $\mathbf{c}_e$ is the position of the eye being rendered. In code: `ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP)`.

**Step 2: where the eyes are looking, in 3D.** `GazeProvider` casts the raw gaze ray into the scene. The point it hits is the fixation point $\mathbf{F}$. In real life both eyes angle inward to point at it (vergence).

**Step 3: each eye's own line of sight.** For the eye being rendered:

$$\hat{\mathbf{g}}_e = \frac{\mathbf{F} - \mathbf{c}_e}{\lVert \mathbf{F} - \mathbf{c}_e \rVert}$$

Its "right" vector is perpendicular to that line of sight and to the head's up vector $\hat{\mathbf{u}}$:

$$\hat{\mathbf{r}}_e = \frac{\hat{\mathbf{u}} \times \hat{\mathbf{g}}_e}{\lVert \hat{\mathbf{u}} \times \hat{\mathbf{g}}_e \rVert}$$

(Unity is left-handed, so up cross forward points to the viewer's right.)

**Step 4: which side, and blind or not.** For every pixel:

$$x = \hat{\mathbf{d}} \cdot \hat{\mathbf{r}}_e$$

$x > 0$ means the pixel is right of this eye's line of sight, $x < 0$ means left. With $s = -1$ for a left-side loss and $s = +1$ for a right-side loss:

$$\text{blind} = \begin{cases} 1 & \text{if } s \cdot x \ge 0 \\ 0 & \text{otherwise} \end{cases}$$

and the output is $\text{lerp}(\text{scene}, \text{blindColor}, \text{blind})$.

Geometrically, each eye's boundary is the plane through that eye containing its line of sight and the head's up vector. Both planes contain $\mathbf{F}$, so through either eye the boundary is the vertical line through the point you're looking at, and both eyes lose the same half of the world. That's the "same half in both eyes" property of real hemianopia.

### Why per eye: the v0 bug

v0 gave both eyes one shared gaze direction from the point between the eyes. So each eye's boundary ran straight ahead from that eye, parallel to the other one and offset by half the distance between the eyes (about 3.2 cm). The boundaries missed the fixation point by

$$\theta_{\text{error}} \approx \arctan\left(\frac{\text{IPD}/2}{d}\right)$$

| Looking at something | Error per eye |
|---|---|
| 0.5 m away | about 3.7° |
| 2 m away | about 0.9° |
| 10 m away | about 0.2° |

How we found it: with a cube close to the face, the gaze dot sat away from the black edge, as if the cube pushed it off. With only the right eye open, the dot disappeared into the black. With only the left eye open, it sat to the right of the edge with a gap. Looking far away, it sat right on the edge.

## 3. Timing

The controller runs inside `GazeProvider.GazeUpdated`, which fires in `Application.onBeforeRender` after the headset's final head update (see [GAZE.md](GAZE.md), section 3). So the boundary is computed from the same head pose the frame is drawn with.

It uses the raw fixation point (from `RawGazeRay`), never the stable gaze. Smoothing adds lag, and if the boundary trails behind a saccade, the person gets a split second to see into the area that should be gone.

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
- **Vergence comes from a raycast, not from the eyes.** The fixation point is wherever the gaze ray hits a surface. Real eyes take a moment to change vergence, ours changes instantly, so when the gaze crosses from a near object to a far one, each eye's boundary jumps slightly. It also assumes you're looking at a surface, not through a gap or a window.
- **Looking at empty space** puts the fixation point 100 m away, which makes the two boundaries nearly parallel. That's correct for distant viewing.
- **No macular sparing.** Many patients keep a small central region around fixation. Planned for v1, from Tamanna's spec.
- **The boundary follows head roll exactly.** Real eyes partly counter-rotate when the head tilts. Small effect, ignored for now.
- **Scene view.** In Play mode the Scene view also gets filtered using the headset's gaze, so it can look odd there. The Game view and headset are what matter.
- **Edit mode.** The filter stays off outside Play mode, because nothing sends it gaze.

## 7. How to test

1. No headset: press Play. Everything left of the red mouse dot goes black, and the boundary follows the mouse.
2. Quest Pro over Link: press Play. The left half of the world disappears, and the boundary follows your eyes.
3. Press **H** (or A, B, X, Y) to compare with and without the filter. Press **B** (or either index trigger) to flip sides.
4. Look at a cube, then move your eyes to the left. The cube should vanish once your gaze passes it.
5. Bring a cube to arm's length and look at it. The black edge should run right through the gaze dot. Check with only the left eye open, then only the right.
