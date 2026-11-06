# Dev log

Newest first. Each entry says what changed and why.

## 2026-11-05

### Check-in with Prof. Zwicker

- Showed him the filter. He traced the jitter to the tracker's own estimation errors plus the delay between the eye tracker and Unity, and suggested temporal smoothing in the filter.
- **Our framing:** two kinds of error. Bias (a consistent offset) is fixed by calibration. Noise (random wobble) is fixed by smoothing.
- **Plan:** calibration first, because its noise numbers tell the smoothing how strong to be. Then smart smoothing on the filter: calm while the eyes hold still, instant when they jump, so it never lets anyone peek.
- Also planned: compare a native headset build with Link, to see how much of the jitter is Link's delay.

### Calibration check v1 (Step 1)

- **Added `CalibrationRunner`:** 9 head-locked dots, records fresh gaze samples per dot, drops glances away, measures bias (accuracy) and noise (precision), shows a results picture, saves a CSV to `CalibrationLogs/`. C or left thumbstick click runs it.
- **Added `GazeCorrection`:** an affine correction in (azimuth, elevation) degrees, fitted by least squares, with sanity limits. V or right thumbstick click toggles it.
- **`GazeProvider`** now keeps `TrackerDirectionHead` (before correction) and `GazeDirectionHead` (after). Everything downstream uses the corrected one.
- **`GazeDot`** now hides itself when disabled, so it can switch off during calibration.
- **`.gitignore`:** `CalibrationLogs/` and `Replays/` are never committed.
- Details in [CALIBRATION.md](CALIBRATION.md). Not yet tested on the headset.

### First calibration results, status panel, ghost dot

- **First runs (P01, 4 runs, the first one discarded as a warm-up with 8 of 9 dots):**
    - Error before correction about 1.2°. After correction 0.3 to 0.6°, measured on the same data, so optimistic.
    - Noise about 0.3 to 0.4°, a bit higher at the outer dots than the center.
    - Consistent bias across runs: the tracker reads gaze slightly low (0.5 to 0.8° on average, up to 2° at the bottom dots), stretches vertical angles by about 5%, and squeezes horizontal ones by about 2 to 3%.
- **What that suggests (not confirmed):** with the head still, noise is small, about a quarter of the bias and far under the fixation filter's 2° snap threshold. So the shimmer seen during head movement likely comes mostly from latency, as Prof. Zwicker suggested, since these dots moved with the head and never tested head motion.
- **Problem:** no way to see in the headset whether the correction was on, and the console showed nothing. The console's normal-message filter was most likely off. The results were all saved to CSV.
- **Added `StatusHud`:** a head-locked panel under the view with gaze source, correction ON/OFF, last calibration numbers and filter state. Changes flash in big text. I or the left menu button toggles it.
- **Added the ghost dot to `GazeDot`:** while the correction is on, a small red dot shows where the tracker alone would put your gaze.

### Head-motion test (latency vs noise)

- **Why:** the calibration dots move with the head, so they never tested head motion. With the head still, noise was only about 0.3°, so the shimmer during head movement needs its own test.
- **Added `HeadMotionTest`:** a dot fixed in the room, then still, slow and fast head turning phases with instructions on the status panel. T or the left grip starts it.
- **The trick:** with eyes on a fixed dot, a perfect system shows zero error at any head speed. If eye data lags the head data by a delay, error ≈ head speed × delay. A straight-line fit of error against head speed gives the delay as its slope, and the scatter around it is the noise. Details in [HEAD_MOTION_TEST.md](HEAD_MOTION_TEST.md).
- **`StatusHud`** can now show instructions (`SetBanner`) and lasting result lines (`SetLine`) from other scripts.
- Calibration won't start while the head-motion test runs.

### First head-motion results

- **8 runs (P01), the first 3 were warm-ups** (still error around 20°, eyes not on the dot yet). The last 5 agree with each other.
- **Still error about 1.4°**, matching the uncorrected bias from calibration (about 1.2°). Two different tests agree.
- **Delay about 80 ms** (72 to 87 ms across the 5 good runs). The error leans in the direction of the head turn at every speed, which is the latency fingerprint.

| Head turning speed | Typical error | Lean toward the turn |
|---|---|---|
| Still | 1.4° | +0.6° |
| 5 to 15 °/s | 2.6° | +1.9° |
| 15 to 30 °/s | 3.0° | +2.6° |
| 30 to 60 °/s | 2.2° | +1.8° |

- **Latency is about a third of the story.** The delay line explains about 34% of the error while moving. Roughly 2° of other wobble appears when the head moves, and the error stops growing above about 30 °/s, which pure latency can't explain. Suspects: the headset shifting, the tracker getting worse during motion, small catch-up eye movements.
- **The tracker is slower than the display:** about 50 eye samples per second vs 72 frames, so about 3 in 10 frames reuse the previous eye sample.
- **Caveats:** the correction was off in every run (it didn't survive restarting Play), and the "fast" phase never went past about 60 °/s.

### Calibration persists, time-matching (latency fix)

- **The correction now survives restarting Play:** saved per participant to `CalibrationLogs/correction_<code>.json` and loaded on start. The status panel says when it was saved.
- **Time-matching (experiment, on by default):** each eye sample is re-paired with the head pose from its own timestamp (`GetNodePoseStateAtTime`), instead of the current head. That should cancel the overshoot while fixating during head motion, and the steps from the slower tracker. Details in [GAZE.md](GAZE.md), section 5.
- **It's falsifiable:** with it on, the head-motion test's delay should drop toward 0. Negative means it over-corrects. M or the right grip toggles it for the A/B.
- **Eye data age** (sample timestamp vs the runtime clock) now shows on the status panel and in the head-motion CSV, a second, direct look at latency.
- The ghost dot now shows the calibration correction only, and calibration reads the raw tracker origin and direction, both before time-matching.
- **First try in the headset:** the panel said time-matching found no head history. Two possible causes, so the panel now names which one (clocks disagree vs no past head pose), and the console logs the numbers once.
- **Added a fallback:** our own ring buffer of the last ~1.5 s of head poses, stamped with the headset's clock and blended between the two poses around each eye sample's time. Used whenever the headset can't answer for a past time.
- **Second try:** the panel said the eye clock doesn't match, with an eye data "age" of about 188,000 s (52 hours). Over Link, the eye timestamps are on a different clock than the runtime, with a different starting point.
- **Fix:** time-matching now detects which clock the eye time matches (head-pose clock, runtime clock, or neither). With neither, it estimates each sample's moment as its arrival time minus `assumedLatencyMs` (80 ms to start, from the head-motion test). The head-motion test suggests a tuned value from whatever delay is left over.
- **Third try (head-motion test, correction on):** still error **0.35°**, down from 1.4° without the correction. That's fresh data the calibration never saw, so it's an honest check that the correction works.
- **Bug found in that run:** time-matching was applied to 0% of frames. The estimate mode was labeled but not counted as "matched", so the turn was never applied, and the leftover delay (63 ms) was just the original latency. Fixed. The test now refuses to suggest a latency when time-matching ran on less than half the frames.

### Head-motion verdict: a fixed lean, not latency

- **Time-matching ON at 80 ms, then 50 ms:** it helped at slow and medium speeds (error 3.0° down to 1.5° at about 28 °/s) but over-corrected at fast turns (leaning up to 6° *against* the turn at about 150 °/s).
- **Five fast runs with time-matching OFF** (100 to 300 °/s peaks, about 4,600 frames) settled it:

| Head speed | Lean toward the turn |
|---|---|
| ~14 °/s | +2.1° |
| ~27 °/s | +2.6° |
| ~52 °/s | +0.8° |
| ~98 °/s | +2.0° |
| ~157 °/s | +2.9° |
| ~217 °/s | +3.9° |

- **Fit across those:** a fixed lean of about **+2.0°** plus only about **5 ms** of delay. The real latency is small (roughly 5 to 20 ms depending on how it's fitted). The earlier "80 ms" was a fixed lean that looked like a delay because the first runs only had slow turns.
- **Changes:** time-matching is now off by default (kept for experiments). The head-motion test now fits a fixed lean and a delay together, and its reading says which one it sees, and warns when the fast phase is too slow.
- **Open question:** is the 2° lean the headset slipping on the face, or the eyes really slipping with the head? Cheap test: tighten the strap and rerun. If the lean shrinks, it's the headset. A good question for Tamanna too: do healthy eyes slip a little during head turns while fixating?

### Strap test and sawtooth check

- **Strap test (glances over 10° removed):** fixed lean +1.5° with the normal strap, +1.9° with a tight strap (per-run ranges overlap: 0.8 to 1.9° vs 1.6 to 2.6°). Tightening didn't shrink it, so it's probably **not the headset sliding**. Delay again small, 4 to 12 ms.
- **Sawtooth check** on 12 fast runs, looking at the frame-by-frame error for the fingerprint of eyes slipping (slow creep toward the turn, quick snaps back):
    - **Slow turns (20 to 60 °/s):** a weak sawtooth. Snaps back were about 2× as common as jumps forward (4.1 vs 2.2 per second), and the error crept toward the turn between them. Consistent with eyes slipping.
    - **Fast turns (60 °/s and up):** unreadable. The head moves 3 to 8° between eye samples (about 50 per second), so timing jitter swamps the signal.
    - **Head reversals (85 clean ones):** the error takes about 100 ms to flip to the new direction (crosses zero at about +75 to +100 ms, settled by +125 ms). Not instant, so it's **not a flat offset that flips the moment the head reverses**.
- **Where that leaves it:** two explanations still fit. (1) The eyes slip with the head and catch up with small jumps. (2) The headset smooths its own gaze output, more when the eyes move slowly, which would also give a roughly constant lean and a ~100 ms flip. 50 Hz eye data can't separate them during head motion.
- **Next test to separate them (and it matters for the simulation itself):** a saccade test with the head still. A dot jumps, and we measure how long the tracker takes to land on it. Real eyes finish a 20° jump in roughly 60 to 70 ms. If the tracker takes much longer, it's smoothing, and the hemianopia boundary would lag right after every eye jump, which is exactly when people could peek.

### Saccade test

- **Added `SaccadeTest`:** head still, a head-locked dot jumps 20° at random moments (16 jumps), you follow it. Per jump: rise time (10 to 90%), peak speed, overshoot, reaction. Timed with the eye samples' own timestamps, whose gaps are real even though their clock's starting point is unknown over Link. J or the right grip starts it. Details in [SACCADE_TEST.md](SACCADE_TEST.md).
- **Controls:** the right grip moved from time-matching (now keyboard M only, it's off by default) to the saccade test. Calibration, the head-motion test and the saccade test refuse to start on top of each other.

### Saccade results: the headset snaps

- **4 runs, 60 usable jumps:** rise 14 to 28 ms, peak 720 to 1,240 °/s, no overshoot. Faster than real eyes can move.
- **Traces are steps:** the biggest single-sample step was a median 97% of the jump, and 37 of 60 jumps had no sample mid-movement. Between jumps the reading barely moves (median 0.01° per sample, sometimes frozen for up to ~200 ms).
- **Conclusion:** the headset runs its own hold-or-snap filter on gaze. It holds steady during fixations and jumps on saccades.
- **That explains the head-motion lean** better than the eyes slipping: during a head turn the eyes counter-rotate smoothly, but the headset holds the eye reading until the change passes its threshold, then snaps. Bounded lean (~2°) at any speed, creep-then-snap, ~100 ms flip at reversals. It's probably also the "hops" in the boundary felt since day one.
- **The test's automatic reading was wrong** ("more likely your eyes"). It assumed fast = unfiltered. Fixed: it now checks how much of each jump happens in a single step.
- **What it means for us:**
    - Good news for saccades: the boundary lands within about one sample (17 ms) after an eye jump, so no peek window from tracker smoothing.
    - Don't stack heavy smoothing on top. The headset already smooths.
    - The head-motion lean can likely be cancelled by holding gaze in WORLD space during fixations (our existing world-space fixation filter), snapping only on real jumps. During a fixation the eyes stay on the same world point, so world-space holding costs no lag there.

### Fixation anchoring

- **Idea:** use the headset's own snaps as the signal. After a catch-up snap the gaze lands back on the same world point (keep the anchor). After a real eye jump it lands somewhere new (move the anchor, same frame). Between snaps, the filter uses the anchored world point, so the creep from the headset's hold never reaches it.
- **Added to `GazeProvider`:** `FilterFixationPoint` and `FilterGazeRay`, with snap detection (0.5°), re-anchor distance (1.5°) and a safety net (5° for 0.15 s). On by default for eye tracking. K toggles it. Details in [GAZE.md](GAZE.md), section 6.
- **`HemianopiaController`** now uses `FilterFixationPoint`. **The head-motion test** now measures that same gaze (`FilterGazeRay`), so it can A/B the fix. The status panel shows anchoring ON/OFF and how much creep it's holding back.
- **First A/B (2 runs each, glances removed):**

| | Anchoring OFF | Anchoring ON |
|---|---|---|
| Fixed lean toward the turn | +2.1° | +1.2° |
| Error, 5 to 40 °/s | 2.5° | 2.2° |
| Error, 40 to 120 °/s | 3.9° | 2.8° |
| Error, 120 °/s and up | 5.4° | 3.3° |
| Error, head still | 0.65° | 1.64° |

- **Works where it was meant to** (about 40% less error in fast turns, lean down about 40%), but **costs about 1° with the head still**: the anchor ignored corrective eye movements under 1.5°.
- **Leftover lean:** at fast speeds the creep passes the 5° safety net within 150 ms, so the safety net re-anchored mid-creep and locked part of the lean in.
- **Tweaks (v2):** anchoring only holds while the head turns (over 8 °/s, plus 0.1 s after), and follows the raw gaze when the head is still. The safety net only applies while the head turns slower than 30 °/s. The status panel shows holding vs following and the head speed.

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

- **Run the calibration check on Tamanna** too, and compare with P01.
- **A/B fixation anchoring** with the head-motion test (K on vs off). The fixed lean should drop toward 0 with it on.
- **Native build vs Link:** less urgent now that latency looks small, but still worth one comparison.
- **Add a validation run** (a second pass with the correction on) for an honest after-correction error.
- **Smart smoothing on the filter**, tuned from the noise numbers.
- **Filter v1 with Tamanna:** what the blind half should look like, edge softness, macular sparing.
