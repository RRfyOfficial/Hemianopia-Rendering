# Gaze system (layer 1)

Everything in the project that needs to know where the person is looking asks one component: `GazeProvider`. Nothing downstream cares whether the answer came from the Quest Pro's eye tracker or the mouse.

Files in `Assets/Hemianopia_Project/Scripts/Gaze/`:

| File | Job |
|---|---|
| `GazeProvider.cs` | Reads gaze, applies the calibration correction and time-matching, stores it relative to the head, exposes raw and stable versions, and finds the 3D fixation point |
| `FixationFilter.cs` | Splits gaze into fixations (holding still) and saccades (jumps) |
| `GazeDot.cs` | Debug marker at the gaze point |

## 1. Where the data comes from

**Quest Pro.** `OVRPlugin.GetEyeGazeInteractionState`, which gives one combined gaze pose for both eyes plus a validity flag and a timestamp. This API is public starting with Meta XR Core SDK v207, which is the version we use. Every read is gated by two checks:

- `OVRPlugin.eyeGazeInteractionsSupported`: the hardware and runtime support it
- `OVRPlugin.eyeGazeInteractionsEnabled`: the user granted eye tracking permission

**Mouse fallback.** A ray from the camera through the mouse cursor. Used automatically when eye tracking isn't available (no headset, a Mac, permission denied). The `mode` field on `GazeProvider` can force `Auto`, `EyeTracking`, or `Mouse`.

**Invalid samples** (blinks, dropped frames) keep the last good gaze instead of jumping somewhere random.

## 2. Three coordinate spaces

- **Tracking space:** the headset's own coordinates, centered on the play area origin. The eye tracker reports here.
- **Head space:** coordinates relative to the head (the camera). +Z is straight ahead. This is where we store gaze.
- **World space:** Unity's scene coordinates. Raycasts and objects live here.

Write a pose as a rotation $R$ and a position $\mathbf{p}$. With the head's pose in tracking space $(R_h, \mathbf{p}_h)$, and the tracker's gaze direction $\hat{\mathbf{g}}_{\text{track}}$ and origin $\mathbf{o}_{\text{track}}$, step one moves gaze into head space:

$$\hat{\mathbf{g}}_{\text{head}} = R_h^{-1}\,\hat{\mathbf{g}}_{\text{track}}$$

$$\mathbf{o}_{\text{head}} = R_h^{-1}\left(\mathbf{o}_{\text{track}} - \mathbf{p}_h\right)$$

Step two uses the camera's world pose $(R_c, \mathbf{p}_c)$ to move it into world space:

$$\hat{\mathbf{g}}_{\text{world}} = R_c\,\hat{\mathbf{g}}_{\text{head}}$$

$$\mathbf{o}_{\text{world}} = R_c\,\mathbf{o}_{\text{head}} + \mathbf{p}_c$$

In code, `ToOVRPose().ToHeadSpacePose()` does step one and `Transform.TransformPoint` / `TransformDirection` do step two. It's the same idea as a view matrix: re-describe a point from someone else's point of view by undoing their translation and rotation.

## 3. Why gaze is stored in head space (the jitter fix)

**Symptom (v0):** the dot jittered while turning the head, even though the world itself rendered smoothly.

**Cause:** the Meta camera rig moves the camera twice per frame. Once in `Update`, then once more right before rendering, in `Application.onBeforeRender` (`OVRCameraRig.OnBeforeRenderCallback`), using the freshest head prediction. v0 placed the dot in `LateUpdate`, between those two moves. So the dot was positioned with a head pose that was already outdated by the time the frame was drawn, and it slipped by one frame of head motion.

**Frame timeline (v1):**

```
Update            OVRCameraRig moves the camera (first time)
LateUpdate        GazeProvider samples gaze (so gameplay scripts get this frame's data)
onBeforeRender    OVRCameraRig moves the camera again (final pose for this frame)
                  GazeProvider re-samples, steps the fixation filter
                  GazeUpdated event fires, GazeDot places itself
Render
```

**Fix:**
1. Store gaze relative to the head. World-space rays are built from the camera's pose at the moment they're read, so they can't fall behind the head.
2. Do render-critical work in `onBeforeRender`, after the rig. `GazeProvider` subscribes on its first `LateUpdate`. By then `OVRCameraRig` has already subscribed in its `Start`, so our callback always runs second.

**Why it matters beyond the dot:** hemianopia is defined relative to where the eyes point, not the room. The hemianopia filter will compare each pixel's viewing direction with the gaze direction in head space, so it gets this fix for free.

## 4. Raw vs stable gaze

| | `RawGazeRay` | `StableGazeRay` |
|---|---|---|
| Filtered | No | Yes, through `FixationFilter` |
| Lag | None added | None on saccades, slight easing during fixations |
| Use for | The hemianopia filter, and anything where lag would break the simulation | Visuals like the gaze dot, and later deciding what the person actually looked at |

**Rule:** the hemianopia filter must use raw gaze. Smoothing adds lag. If the blind half lags behind a saccade, the person gets a split second to see into the area that's supposed to be gone, and the simulation is broken.

### Calibration correction

`GazeProvider` keeps the tracker's own reading as `TrackerDirectionHead`, and the bias-corrected one as `GazeDirectionHead`. Everything downstream uses the corrected one, including `RawGazeRay`, where "raw" means unsmoothed. The calibration check reads the tracker's own reading so it always measures the tracker's real error. Details in [CALIBRATION.md](CALIBRATION.md).

## 5. Latency and time-matching

> **Verdict (Nov 5, P01, over Link): off by default.** Fast head turns showed the real latency is small, about 5 to 20 ms. The motion error is mostly a **fixed lean of about 2° toward the turn**, the same at any speed. Time-matching assumes a delay that grows with speed, so it over-corrected at fast turns. It stays in the code for experiments (M). The story of how we got there is below and in [DEVLOG.md](DEVLOG.md).

**The problem.** World gaze = head pose + eye-in-head direction. Head data is close to instant. Eye data arrives tens of milliseconds late. The head-motion test measured about 80 ms of effective delay ([HEAD_MOTION_TEST.md](HEAD_MOTION_TEST.md)). When a stale eye sample is paired with the current head, the gaze overshoots in the direction the head is turning, by about head speed × delay.

**The fix (an experiment, now off by default).** Re-pair each eye sample with the head pose from the sample's own moment. While you look at something fixed in the room, where you looked when the sample was taken is still where you're looking now, so the overshoot disappears during fixations.

In head space, that means turning the eye-in-head gaze by how much the head has moved since the sample. With $\mathbf{H}(t)$ the head pose in tracking space and $t_s$ the sample's timestamp:

$$\mathbf{M} = \mathbf{H}_{\text{now}}^{-1}\,\mathbf{H}(t_s)$$

$$\hat{\mathbf{g}}'_{\text{head}} = \mathbf{R}_M\,\hat{\mathbf{g}}_{\text{head}}, \qquad \mathbf{o}'_{\text{head}} = \mathbf{R}_M\,\mathbf{o}_{\text{head}} + \mathbf{t}_M$$

where $\mathbf{R}_M$ and $\mathbf{t}_M$ are the rotation and translation of $\mathbf{M}$. Everything downstream then converts to world space with the current camera, as before, which gives $\mathbf{H}(t_s)$ applied to the eye sample.

**Where the pieces come from:**

| Piece | Source |
|---|---|
| Sample time $t_s$ | `EyeGazeInteractionState.Time` |
| Head pose at $t_s$ | `OVRPlugin.GetNodePoseStateAtTime(t_s, Node.Head)` first. If the headset gives no answer, our own head-pose history (below). |
| Head pose now | `OVRPlugin.GetNodePose(Node.Head, Step.Render)` |
| Sample age | `OVRPlugin.GetTimeInSeconds()` minus $t_s$, shown on the status panel |

**Our own head-pose history (fallback).** Over Link, the headset may not answer "where was the head at time $t$?". So `GazeProvider` also keeps a ring buffer of the last 128 head poses (about 1.5 s), each stamped with the headset's clock (`GetNodePoseStateRaw(Node.Head, Step.Render)`). To look up $t_s$ it finds the two recorded poses on either side and blends them: slerp for rotation, lerp for position. Same idea as a TAA history buffer, for head poses instead of pixels.

**Which clock is the eye timestamp on?** Over Link, the eye timestamps turned out to be on a different clock, about 52 hours away from the runtime clock (two clocks that started counting at different moments). So for each sample, `GazeProvider` checks which clock the eye time matches:

| Eye time matches | What it does |
|---|---|
| The head-pose clock | Uses the real timestamp: asks the headset, then our history |
| The runtime clock (`GetTimeInSeconds`) | Same, on that clock |
| Neither | Estimates: the sample's moment = when it arrived here, minus `assumedLatencyMs` (default 80 ms, from the head-motion test). A sample that sits around for extra frames correctly counts as older. |

The estimate gets tuned with the head-motion test: with time-matching on, whatever delay is left over gets added to `assumedLatencyMs`. The test prints the suggested value.

**What the status panel says:**

| Panel | Meaning |
|---|---|
| ON (headset's head history) | Matched using the headset's answer |
| ON (our own head history) | The headset gave nothing, matched using our buffer |
| ON (estimated, assumes 80 ms) | Eye clock unknown, matched using arrival time minus the assumed latency |
| can't run: eye clock doesn't match | Eye clock unknown and estimating is switched off (`estimateWhenClocksDiffer`) |
| can't run: no head pose for that moment | Neither source had a pose for that time |
| OFF | Switched off (M) |

The first time it can't run, the console prints the eye sample time, the clock, the age, and the history state, which is everything needed to debug it.

**Order of operations:** tracker reading, then calibration correction, then time-matching. `TrackerDirectionHead` is the raw reading, `CorrectedDirectionHead` adds the correction, `GazeDirectionHead` adds time-matching and feeds everything else. The calibration check uses the raw reading. The ghost dot shows the correction only.

**Bonus:** a stale sample gets re-matched every frame against the newest head pose, so while you fixate, the gaze no longer steps when the tracker (about 50 Hz) is slower than the display (72 Hz).

**How we'll know if it's right.** It rests on a reading of the data: that the headset pairs old eye data with the current head. The head-motion test checks it:

| Head-motion result with time-matching on | Meaning |
|---|---|
| Delay drops toward 0 ms | The fix works |
| Delay turns negative (error leans against the turn) | Over-correcting: the headset was already doing part of it |
| Delay unchanged, panel says "no head history" | The runtime can't give past head poses, so nothing changed |

**What it can't fix:** the moment the eyes jump to something new. Nothing knows where they landed until the sample arrives.

Toggle with **M**. Setting: `timeMatchGaze` on `GazeProvider`. Samples older than `maxTimeMatchMs` (250 ms) are left alone, in case the clocks disagree.

## 6. Fixation anchoring (cancels the headset's head-turn lean)

**What the headset does.** The saccade test showed the headset doesn't pass raw eye data. It holds the reading nearly frozen during a fixation and snaps in one step when the eyes jump ([SACCADE_TEST.md](SACCADE_TEST.md)). During a head turn, the eyes counter-rotate smoothly, but the held reading doesn't, so the world gaze creeps toward the turn until the next snap. That's the ~2° lean from the head-motion test.

**The fix: use the headset's own snaps as the signal.** A snap means one of two things:

| Snap type | Where the gaze lands after it | What we do |
|---|---|---|
| Catch-up (head turned, eyes stayed on the same thing) | Back near the anchored world point | Keep the anchor |
| Real eye jump | Somewhere new | Move the anchor there, on that same frame |

Between snaps, the hemianopia filter uses the **anchored world point**, so the creep never reaches it. A real eye jump moves the anchor on the frame the headset reports it, so there's no added lag.

**The rules, per frame (eye tracking only):**

1. **Head still?** If the head turns slower than `anchorMinHeadSpeedDps` (8 °/s), and has for `anchorReleaseSeconds` (0.1 s), the filter just **follows the raw gaze**. The headset's hold only creeps while the head moves, so there's nothing to cancel, and small corrective eye movements land exactly.
2. **Head moving: snap?** The eye-in-head reading (after calibration, before time-matching) changed by at least `snapDetectDegrees` (0.5°) since the last frame.
3. **On a snap:** if the raw gaze is more than `reanchorDegrees` (1.5°) from the anchor, re-anchor to the current raw fixation point. Otherwise keep it.
4. **No snap:** keep the anchor. Safety net, only while the head turns slower than `failsafeMaxHeadSpeedDps` (30 °/s): if the raw gaze stays more than `failsafeDegrees` (5°) from the anchor for `failsafeSeconds` (0.15 s), re-anchor anyway. That's for slow eye drifts the headset never snaps on. During fast turns the creep is expected, and re-anchoring mid-creep would lock part of it in.

Head speed comes from the camera's rotation between frames, smoothed over about 50 ms.

The anchor is a **3D point**, not a direction, so it stays right when the head moves sideways too. The filter reads it through `FilterFixationPoint`, and the head-motion test measures the same gaze through `FilterGazeRay`.

**Toggle:** K in Play mode, or `anchorFixations` on `GazeProvider`. The status panel shows OFF, ON and following (head still), or ON and holding, with the head speed and how much creep it's holding back right now.

**First A/B (v1, before the head-still and safety-net tweaks):** fast-turn error 5.4° to 3.3°, lean +2.1° to +1.2°, but still-head error 0.65° to 1.64°, because the anchor ignored small corrections under 1.5°. The tweaks above target both.

**Clean A/B with the tweaks (v2, time-matching OFF):** still-head error unchanged (0.23° vs 0.25°), turn error 15 to 35% lower (for example 2.72° to 1.83° at 40 to 120 °/s, 4.47° to 3.86° above 120 °/s), and the fixed lean down from +1.71° to +1.00°. About 1° of lean is left. On by default.

**Trade-offs:**
- Real eye movements under 1.5° that the headset doesn't snap on are ignored. That's below the tracker's own accuracy anyway.
- Slow, smooth eye movements (following something moving) only update through the safety net, so the boundary can trail them by up to 5°. Worth revisiting once the test kitchen has moving objects.

## 7. The fixation point (where in 3D)

A direction says *which way* you're looking. The hemianopia filter also needs *where*: the 3D spot both eyes are pointed at, because each eye's boundary has to pass through it (see [FILTER.md](FILTER.md), section 2).

`GazeProvider` finds it by casting the raw gaze ray into the scene:

$$\mathbf{F} = \mathbf{o} + t^{*}\,\hat{\mathbf{g}}$$

where $t^{*}$ is the distance to the first surface hit. If nothing is hit, $t^{*}$ is `noHitDistance` (100 m by default).

This stands in for vergence, how much the two eyes angle inward. In VR you're almost always looking at a surface, so the surface you hit is where your eyes converge.

| Property | Meaning |
|---|---|
| `RawFixationPoint` | The 3D point, in world space |
| `RawFixationDistance` | Meters from the gaze origin |
| `RawFixationHit` | False when looking at empty space |

Updated in `LateUpdate` and again right before rendering, from **raw** gaze. Not to be confused with the fixation *filter* below, which is about holding still vs jumping.

Limitations: it changes depth instantly when the gaze crosses an object's edge, while real vergence takes a moment. And it assumes you look *at* surfaces, not through gaps.

## 8. The fixation filter

Real eyes don't glide. They jump (saccades, a few times per second) and hold still in between (fixations), with tracker noise on top. The filter keeps the jumps and irons out the wobble.

Each frame, with the current stable direction $\hat{\mathbf{s}}$ and the new raw direction $\hat{\mathbf{g}}$:

$$\theta = \arccos\left(\hat{\mathbf{s}} \cdot \hat{\mathbf{g}}\right)$$

**If $\theta > \theta_{\text{snap}}$, it's a saccade.** Snap instantly, $\hat{\mathbf{s}} \leftarrow \hat{\mathbf{g}}$, and reset the fixation timer.

**Otherwise it's a fixation.** Ease toward the raw gaze with exponential smoothing:

$$\alpha = 1 - e^{-\Delta t / \tau}$$

$$\hat{\mathbf{s}} \leftarrow \operatorname{slerp}\left(\hat{\mathbf{s}}, \hat{\mathbf{g}}, \alpha\right)$$

and add $\Delta t$ to the fixation timer. Writing $\alpha$ this way makes the smoothing feel the same at any frame rate.

The filter runs in **world space** on purpose. When you stare at a cube and turn your head, your eyes counter-rotate to stay on it (the vestibulo-ocular reflex). The world gaze direction barely changes, so it counts as one steady fixation.

### Tunables (on `GazeProvider`)

| Setting | Default | Effect |
|---|---|---|
| `snapThresholdDegrees` | 2 | Bigger is calmer, but small real saccades get eased instead of snapped. Smaller lets more jitter leak through as fake saccades. |
| `easeTimeConstant` | 0.08 s | Bigger is calmer and slower. Smaller follows faster with more wobble. |

The defaults are a starting guess. Tune them on the headset using the R toggle (raw vs stable).

### Known limitations

- **Displacement threshold, not velocity.** Standard research methods classify by angular velocity (for example I-VT) and require a minimum fixation duration. Fine for a debug dot. Worth upgrading before fixations feed the blind-field tracker.
- **Smooth pursuit** (eyes following a moving object) shows up as a chain of eased fixations and small snaps.
- **The 2 degree default is a guess.** It should come from the Quest Pro's measured accuracy and noise, ideally measured on our own setup.
- **One update per rendered frame.** The tracker may sample at a different rate than the display.

## 9. How to test

1. No headset: press Play. A red dot follows the mouse.
2. Quest Pro over Link: press Play. The dot turns green and follows your eyes.
3. Stare at one cube and turn your head slowly. The dot should stay on the cube.
4. Press R to compare stable and raw gaze.
5. The console prints which source is live (lines starting with `[Gaze]`).

## 10. Open questions

- How low does end-to-end gaze latency need to be before people stop noticing that a gaze-locked mask lags behind their saccades?
- What snap threshold matches the Quest Pro's real tracking noise?
- Should fixation detection run on raw samples at the tracker's native rate instead of once per rendered frame?
