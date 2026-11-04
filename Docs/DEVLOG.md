# Dev log

Newest first. Each entry says what changed and why.

## 2026-11-03

### Hemianopia filter v0 (layer 3)

- **Added `HemianopiaFilter.shader`:** an image-space pass after the scene renders. Per pixel it recovers the world direction the pixel shows (inverse view-projection), then checks which side of the gaze line it's on with one dot product. Blind side goes black. Details in [FILTER.md](FILTER.md).
- **Added `HemianopiaController`:** sends the gaze "right" vector, blind side, and color to the shader every frame, inside `GazeUpdated` (after the final head update). Uses raw gaze only, so the boundary never lags behind a saccade.
- **Why per pixel and not "left half of the screen":** the condition is locked to the eyes, not the head. The boundary has to move whenever the eyes move.
- **Both eyes for free:** each eye renders with its own camera, so both lose the same half of the world, matching real hemianopia.
- **v0 choices:** left side blind by default, pure black, hard edge, no macular sparing. Keys: H toggles the filter, B flips the side.

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

- **Test filter v0 on the Quest Pro** and check the boundary stays locked to the eyes during fast saccades.
- **Filter v1 with Tamanna:** what the blind half should look like, edge softness, macular sparing.
