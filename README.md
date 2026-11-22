# Hemianopia Rendering

An XR hemianopia simulator for the Meta Quest Pro, built in Unity.

Hemianopia is the loss of the same half of the visual field in both eyes, usually after a stroke. The eyes themselves are fine. The brain's visual processing for one side is not. This project simulates that condition in VR, locked to where a person is looking, so we can compare different ways of cueing people about things on their blind side.

**Team:** Russell Mehta (rendering, eye tracking, code) and Tamanna (neuroscience)

## Where it stands and where it's going

**Working today:** the gaze-locked mask, a test room where a ball appears on the blind side, arrows that nudge the wearer toward it, and a small trained model that decides when the arrows are needed. In a first test on one person, the smart nudge removed the never-found cases with far fewer nudges than nudging every time. The numbers are in [`Docs/DEVLOG.md`](Docs/DEVLOG.md). It is pilot data from one person.

**Next (chosen 21 Nov 2026):** objects that approach from the blind side, and a nudge that fires when the wearer would not notice in time, meaning the model's guess is longer than the time until the object reaches them. Not built yet.

**Where it should end up:** passthrough on the Meta VR Glasses, with real objects found by computer vision. The Unity project is the simulator where we work it out first.

The plan, what has to change, and what is still undecided are in [`Docs/DIRECTION.md`](Docs/DIRECTION.md).

## How it's built

Six layers, each with one job, so any one of them can be rebuilt without breaking the others.

| Layer | Job | Status |
|---|---|---|
| 1. Gaze source | Where is the person looking? Quest Pro eye tracking, or the mouse as a stand-in | Done (v1), with a calibration check and fixation anchoring |
| 2. Scene render | Standard URP, untouched | Done |
| 3. Hemianopia filter | Image-space pass that hides one half of the visual field, locked to gaze | Working (v0.1): hard edge, no macular sparing |
| 4. Blind-field tracker | What's on the blind side, and when it was last seen | Working (v0): `TrackedObject`, `LookTracker` |
| 5. Cue strategy | None, always-on, or smart (the model decides) | Working for still objects: `NudgeCue`, `MissModel`. Approaching objects are next |
| 6. Logger | Gaze, events, trial results | Working: `TrialLogger`, plus `room_summary.csv` |

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
| Run the calibration check | C | Left thumbstick click |
| Calibration correction on or off | V | Right thumbstick click |
| Status panel in the headset on or off | I | Left menu button |
| Run the head-motion test | T | Left grip |
| Fixation anchoring on or off | K | |
| Time-matching (experimental, off by default) on or off | M | |
| Run the saccade test | J | Right grip |

## Analysis setup (Python only, no Unity or headset)

The analysis scripts live in `Analysis/` and read recordings from `CalibrationLogs/`. The recordings are not on GitHub. They sit in a private shared Google Drive folder, and `CalibrationLogs` in your copy of the project is a link to that folder. Ask Russell for access first.

**On a Mac**

1. Install Google Drive for desktop and sign in with the account that was given access.
2. At drive.google.com, open "Shared with me", right-click `Hemianopia-CalibrationLogs`, and choose Organize > Add shortcut > My Drive. Shared folders do not appear on your disk until you do this.
3. In Finder, open Google Drive > My Drive, right-click the folder and choose "Available offline", so the scripts do not wait on downloads.
4. In Terminal, go to the project folder and update it:

```
cd path/to/Hemianopia-Rendering
git pull
```

5. Check for an old recordings folder. If this prints `CalibrationLogs`, move it aside with the second line. If you skip that, the link ends up inside the old folder.

```
ls -d CalibrationLogs
mv CalibrationLogs CalibrationLogs_old
```

6. Make the link. Type `ln -s ` with a space after it, drag the Drive folder from Finder into the Terminal window to paste its path, then type ` CalibrationLogs` and press Enter. It ends up looking like this:

```
ln -s "/Users/you/Library/CloudStorage/GoogleDrive-.../My Drive/Hemianopia-CalibrationLogs" CalibrationLogs
```

7. Check it. The first line should list recording files. The second must not mention `CalibrationLogs`. If it does, stop and ask before committing anything.

```
ls CalibrationLogs | head
git status
```

**On Windows**

Use Command Prompt, not PowerShell, with Unity closed. Move everything in `CalibrationLogs` into the Drive folder, delete the now-empty `CalibrationLogs`, then run this once with your own paths:

```
mklink /J "C:\path\to\Hemianopia-Rendering\CalibrationLogs" "G:\My Drive\Hemianopia-CalibrationLogs"
dir CalibrationLogs
```

**Run the analysis**

```
python3 -m pip install numpy pandas matplotlib
python3 Analysis/build_table.py
python3 Analysis/train_model.py
```

On the 14 Nov 2026 recordings this prints about 1.41 s typical error, 0.81 for order and 0.99 for missed against found. If yours match, your setup works.

Two things to know after a run:

- Results are written to `CalibrationLogs/analysis`, which is the shared folder, so your run replaces those files for everyone.
- `train_model.py` rewrites the weights file Unity uses. If `git status` shows `MissModelWeights.cs` as changed and you did not mean to change the model, undo it with `git checkout -- Assets/Hemianopia_Project/Scripts/Room/MissModelWeights.cs`.

Using Claude Code? Open it in the project folder. It reads `CLAUDE.md`, which has the rules and a word list.

## Project layout

```
Assets/
  Hemianopia_Project/    everything we write lives here
    Scenes/              Sim.unity is the main scene
    Scripts/Gaze/        layer 1: GazeProvider, FixationFilter, GazeDot
    Scripts/Rendering/   layer 3: HemianopiaController
    Scripts/Calibration/ Step 1: CalibrationRunner
    Scripts/UI/          StatusHud (the in-headset status panel)
    Scripts/Diagnostics/ HeadMotionTest (lean vs latency), SaccadeTest (how fast the tracker follows eye jumps)
    Scripts/Room/        layers 4 and 5: the test room, the busy job, the nudge, the miss-time model
    Hemianopia_TAMANNA/  layer 6: TrialLogger, plus MovingBlock and its analysis script
    Shaders/             layer 3: HemianopiaFilter.shader
    Materials/
  URPDefaultResources/   URP pipeline assets ("Medium" is the one in use everywhere)
  XR/, Oculus/           XR and Meta settings, managed by Unity and the Meta SDK
Analysis/                Python scripts that turn recordings into the miss-time model, and look into slow finds
Docs/                    design notes and the dev log (Unity ignores this folder)
CalibrationLogs/         recordings: a link to the private shared folder (never committed)
```

## Ground rules

- **Don't press Fix All in the Meta Project Setup Tool.** It turns Application SpaceWarp and Subsampled Layout back on. Fix items one at a time and skip anything about foveation, SpaceWarp, or Subsampled Layout. The reasons are in `Docs/DEVLOG.md`.
- **One person per scene at a time.** Unity scene files merge badly. Say so before editing `Sim`, or work in your own scene.
- **All our files go in `Assets/Hemianopia_Project/`.**
- **Commit small and often,** with messages that say what changed and why.
- **Never commit recordings.** `CalibrationLogs/` and `Replays/` are participant data. They live in a private shared folder, not on GitHub, and are already in `.gitignore`.

## Docs

- [`Docs/DIRECTION.md`](Docs/DIRECTION.md): where the project is going, what has to change, what is undecided
- [`Docs/GAZE.md`](Docs/GAZE.md): how the gaze system works, with the math
- [`Docs/FILTER.md`](Docs/FILTER.md): how the hemianopia filter works, with the math and the Unity setup
- [`Docs/CALIBRATION.md`](Docs/CALIBRATION.md): the calibration check, bias vs noise, with the math
- [`Docs/HEAD_MOTION_TEST.md`](Docs/HEAD_MOTION_TEST.md): measuring a fixed lean vs latency while the head moves
- [`Docs/SACCADE_TEST.md`](Docs/SACCADE_TEST.md): how fast the tracker follows a real eye jump
- [`Docs/ROOM.md`](Docs/ROOM.md): the test room, every key, data collection and the analysis
- [`Docs/DEVLOG.md`](Docs/DEVLOG.md): what we did, when, and why
- [`CLAUDE.md`](CLAUDE.md): starter notes for Claude Code, with the rules and a word list
