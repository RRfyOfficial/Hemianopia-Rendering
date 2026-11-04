# Hemianopia Rendering

An XR hemianopia simulator for the Meta Quest Pro, built in Unity.

Hemianopia is the loss of the same half of the visual field in both eyes, usually after a stroke. The eyes themselves are fine. The brain's visual processing for one side is not. This project simulates that condition in VR, locked to where a person is looking, so we can compare different ways of cueing people about things on their blind side.

**Team:** Russell Mehta (rendering, eye tracking, code) and Tamanna (neuroscience)

## How it's built

Six layers, each with one job, so any one of them can be rebuilt without breaking the others.

| Layer | Job | Status |
|---|---|---|
| 1. Gaze source | Where is the person looking? Quest Pro eye tracking, or the mouse as a stand-in | Done (v1) |
| 2. Scene render | Standard URP, untouched | Done |
| 3. Hemianopia filter | Image-space pass that hides one half of the visual field, locked to gaze | v0 in progress |
| 4. Blind-field tracker | What's on the blind side, and when it was last seen | Planned |
| 5. Cue strategy | None, always-on, or smart (staleness-based) | Planned |
| 6. Logger | Gaze, events, trial results | Planned |

Layers 1 to 3 are the simulator. Layers 4 to 6 are the experiment, where cue strategies get swapped and compared.

## Requirements

- **Unity 6000.3.27f1** (Unity 6.3 LTS). Use this exact version, or Unity will try to upgrade the project.
- **Android Build Support** module (with OpenJDK and Android SDK & NDK), installed through Unity Hub
- **Git with Git LFS**
- For live eye tracking: **Meta Quest Pro** and the **Meta Horizon Link** app (Windows only)

Mac works for everything except live eye tracking. The mouse stands in for your eyes.

## Getting started

```
git lfs install
git clone https://github.com/RRfyOfficial/Hemianopia-Rendering.git
```

1. In Unity Hub: Add > Add project from disk, and pick the cloned folder
2. Open `Assets/Hemianopia_Project/Scenes/Sim`
3. Press Play. A red dot follows your mouse. That's the gaze system running in mouse mode.

## Eye tracking over Link (Quest Pro, Windows)

**On the headset:** Settings > Movement Tracking. Turn on Eye Tracking and run the calibration. Recalibrate whenever a different person wears it.

**In the Meta Horizon Link app on the PC:**
1. Settings > General: set Link as the active OpenXR runtime
2. Settings > Developer: turn on Developer Runtime Features, then turn on Eye tracking over Meta Horizon Link (it only appears after the first toggle)

Connect the headset over Link and press Play in Unity. The dot turns green and follows your eyes.

**Controls in Play mode:**

| Action | Keyboard | Controllers |
|---|---|---|
| Toggle the hemianopia filter | H | A, B, X or Y |
| Flip the blind side | B | Either index trigger |
| Gaze dot: stable vs raw | R | |

## Project layout

```
Assets/
  Hemianopia_Project/    everything we write lives here
    Scenes/              Sim.unity is the main scene
    Scripts/Gaze/        layer 1: GazeProvider, FixationFilter, GazeDot
    Scripts/Rendering/   layer 3: HemianopiaController
    Shaders/             layer 3: HemianopiaFilter.shader
    Materials/
  URPDefaultResources/   URP pipeline assets ("Medium" is the one in use everywhere)
  XR/, Oculus/           XR and Meta settings, managed by Unity and the Meta SDK
Docs/                    design notes and the dev log (Unity ignores this folder)
```

## Ground rules

- **Don't press Fix All in the Meta Project Setup Tool.** It turns Application SpaceWarp and Subsampled Layout back on. Fix items one at a time and skip anything about foveation, SpaceWarp, or Subsampled Layout. The reasons are in `Docs/DEVLOG.md`.
- **One person per scene at a time.** Unity scene files merge badly. Say so before editing `Sim`, or work in your own scene.
- **All our files go in `Assets/Hemianopia_Project/`.**
- **Commit small and often,** with messages that say what changed and why.

## Docs

- [`Docs/GAZE.md`](Docs/GAZE.md): how the gaze system works, with the math
- [`Docs/FILTER.md`](Docs/FILTER.md): how the hemianopia filter works, with the math and the Unity setup
- [`Docs/DEVLOG.md`](Docs/DEVLOG.md): what we did, when, and why
