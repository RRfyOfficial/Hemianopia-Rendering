# Dev log

Newest first. Each entry says what changed and why.

## 2026-11-04

### Filter v0.1: each eye's boundary goes through the fixation point

- **Problem:** with a cube close to the face, the gaze dot sat away from the black edge, as if the cube pushed it off.
- **Test:** right eye only, the dot disappeared into the black. Left eye only, it sat to the right of the edge with a gap. Far away, it sat on the edge.
- **Cause:** both eyes shared one gaze direction, so their boundaries ran parallel, half the eye spacing apart, instead of crossing at the fixation point. Error about 3.7° at 0.5 m and 0.9° at 2 m.
- **Fix:** `GazeProvider` now finds the 3D fixation point (raycast along raw gaze). The shader builds each eye's own line of sight to that point, so each eye's boundary runs through it. Details in [FILTER.md](FILTER.md), section 2.

### Observed, not fixed yet

- **Shimmer while holding still.** Likely tracker noise plus the small eye movements that happen even during a fixation. Slightly worse when looking toward the edges of the view.
- **Extra shimmer during head movement.** Two candidates: the headset shifting slightly on the face, or eye data and head data coming from slightly different moments. Needs measurement, not guessing.
- **Slight tilt of the boundary in diagonal gaze.** By the math the boundary is always vertical relative to the head, so it shouldn't tilt. Suspects: slight head roll, lens distortion near the edge of the lens. Side note: in real hemianopia the boundary is fixed to the retina, and the eye twists slightly in diagonal gaze (Listing's law), so a small tilt would actually be realistic.
- **Eye tracking keeps working with one eye closed** (checked during the one-eye test).

## 2026-11-03

### Hemianopia filter v0 (layer 3)

- **Added `HemianopiaFilter.shader`:** an image-space pass after the scene renders. Per pixel it recovers the world direction the pixel shows (inverse view-projection), then checks which side of the gaze line it's on with one dot product. Blind side goes black. Details in [FILTER.md](FILTER.md).
- **Added `HemianopiaController`:** sends the gaze "right" vector, blind side, and color to the shader every frame, inside `GazeUpdated` (after the final head update). Uses raw gaze only, so the boundary never lags behind a saccade.
- **Why per pixel and not "left half of the screen":** the condition is locked to the eyes, not the head. The boundary has to move whenever the eyes move.
- **Both eyes for free:** each eye renders with its own camera, so both lose the same half of the world, matching real hemianopia.
- **v0 choices:** left side blind by default, pure black, hard edge, no macular sparing. Keys: H toggles the filter, B flips the side.
- **Controller bindings:** A, B, X or Y toggles the filter. Either index trigger flips the side. So the person wearing the headset can switch without the keyboard.
- **Result on the Quest Pro:** works and follows the eyes. The boundary still feels jittery. Investigating next, starting from observation before picking a fix.

### Gaze v1: head-relative gaze and fixation filter

- **Problem:** the gaze dot jittered while turning the head. The world rendered smoothly, so it wasn't frame rate.
- **Cause:** `OVRCameraRig` moves the camera a second time right before rendering. The dot was placed before that move, using an outdated head pose.
- **Fix:** `GazeProvider` now stores gaze relative to the head and does render-critical work in `Application.onBeforeRender`, after the rig. Details in [GAZE.md](GAZE.md), section 3.
- **Added `FixationFilter`:** holds small wobble, snaps on saccades. Exposed as `StableGazeRay`. `RawGazeRay` stays unfiltered for the hemianopia filter.
- `GazeDot` follows stable gaze by default. Press R to compare with raw.

### Gaze v0: Quest Pro eye tracking working over Link

- `GazeProvider` reads the Quest Pro's combined gaze through `OVRPlugin.GetEyeGazeInteractionState` (Meta XR Core SDK v207). Falls back to the mouse when eye tracking isn't available.
- `GazeDot`: green on real eyes, red on the mouse.
- Scene `Sim.unity`: `OVRCameraRig`, with Eye Tracking Support set to Supported and the eye tracking permission requested on startup.
- **Result:** the green dot follows the eyes over Link. Jitter observed, fixed in v1.

### XR features turned off on purpose

Turned off in the OpenXR settings (Android and Windows):

- **Meta XR Foveation and Eye Tracked Foveation.** They render the periphery at lower resolution. The simulator should render the full scene normally and let our filter be the only thing that changes what the person sees.
- **Meta XR Subsampled Layout.** Stores the frame in a squished format for foveation, which can break full-screen filters.
- **Application SpaceWarp** (both the Unity and Meta versions). Renders every other frame and synthesizes the ones in between. A gaze-locked mask needs every frame to be real, or the blind half can lag behind the eyes.

The Project Setup Tool's Fix All turns some of these back on. Fix items one at a time instead.

### Foundation

- **Render pipeline:** switched from Built-in to URP 17.3. Built-in is deprecated starting in Unity 6.5, and the hemianopia filter is built as a URP full-screen pass. The Medium URP asset is used on both Quest and PC, so editor tests and headset builds render the same way.
- **Packages:** removed Unity Version Control (we use git), Visual Scripting, and Multiplayer Center. Added URP 17.3.0, OpenXR 1.19.0, and Meta XR Core SDK 207.0.1 (Core only, not All-in-One). Kept `com.unity.ai.inference` (Unity's ONNX runtime) for possible ML later.
- **XR:** OpenXR on Android and Windows, with the Meta XR feature group on. The Quest build uses Vulkan, ARM64, IL2CPP, and a minimum Android API level of 32.
- **Repo:** fixed the Unity `.gitignore` (it had been saved as `gitignore.txt`, so nothing was ignored). Added `.gitattributes` for consistent line endings and Git LFS on large binaries.

## Next

- **Test filter v0.1 on the Quest Pro:** with a cube at arm's length, the edge should run through the gaze dot in each eye.
- **Measure the shimmer:** log gaze and head motion so we can look at real numbers before choosing a fix.
- **Filter v1 with Tamanna:** what the blind half should look like, edge softness, macular sparing.
