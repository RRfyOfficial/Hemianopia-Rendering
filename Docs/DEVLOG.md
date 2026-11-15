# Dev log

Newest first. Each entry says what changed and why.

## 2026-11-14

### First nudge test: the nudge made things slower, and why

- **Test (Russell, mask on, lab room):** 4 surprises with the nudge off, 7 with it on.
- **Result:** typical time to find about 1.4 s with the nudge off and about 2.7 s with it on. The nudge hurt.
- **Why 1, the cue pulls the eyes the wrong way.** A new bright thing draws the eyes to it. The chevrons popped up 5 degrees to the seeing side, so the eyes went toward them, away from the surprise. They also moved with the gaze, so looking at them pushed them further away (a carrot on a stick). In 3 of the 7 nudge tries, the eyes and head swung 25 to 40 degrees the wrong way at the moment the cue appeared. Russell confirmed he felt pulled to look at it.
- **Why 2, the tester hunts.** With the nudge off, he started sweeping toward the blind side almost as soon as the surprise appeared, because he knew it was coming and which side. He found it in about 1.4 s, and the nudge only appears after 1 s, so it cannot win that race. The test needs a way to stop people hunting (still open, Tamanna).
- **Takeaway:** where the nudge sits matters as much as what it looks like. A nudge that steals the eyes is worse than no nudge.

### Nudge v1: appears at the edge, stays in the room

- **`NudgeCue` placement:** the default is now `StayInRoom`. The row appears touching the edge of the blind half, right beside the spot being looked at, then stays fixed in the room, so glancing at it does not move it. If the eyes settle more than 12 degrees away and the target is still unseen, it re-appears beside the new spot.
- The first version is kept as `FollowGaze`. Key 9 switches between the two, so they can be compared.
- **Rule fix:** the nudge now sticks with one target until it has been looked at. On one try it flickered between the surprise and an unseen shape.
- `room_summary.csv` has a new `cue_mode` column (`stay` or `follow`). All nudge tries before this change are `follow`.
- Only the placement changed. The 1 s wait is the same, so a retest shows the effect of placement alone.

### Nudge v1 retest, and a shorter wait

- **Retest (Russell, mask on, lab room, 6 tries, `stay`):** typical time to find about 2.0 s, slowest 2.5 s. The old `follow` cue was about 2.7 s, slowest 4.4 s. No nudge was about 1.4 s.
- **The wrong-way swings are gone.** After the new cue appeared, the eyes drifted away by 4 degrees at most (25 to 40 before). When he was still far from the surprise as the cue appeared, he started moving toward it about half a second later.
- **Why it still trails no nudge:** the 1 s wait plus about half a second to react, and he hunts when the nudge is off but tends to wait for it when it is on.
- **Change:** `nudgeDelaySeconds` is now 0.3 s (was 1 s). Not retested yet.
- **To do, agreed but not built:** make the test fair with a small job that keeps the eyes busy (for example, look at whichever shape is pulsing), so nobody can camp on the blind side waiting for the surprise. This is what will show what the nudge is really worth.

### Gaze laser, and a nudge fix

- **Added `GazeLaser`:** key G shows a laser ending on the spot the gaze lands, plus a ring the size of the `LookTracker` beam (3 degrees). It draws the same gaze the mask and the look check use. Off by default. Leave it off for timed tests.
- **Fix:** the nudge used to wait on its chosen object forever. If that object slid onto the seeing side without being looked at, nothing else of the same age could be nudged. It now lets go after 0.75 s (`stickGraceSeconds`).
- **How the nudge picks when several things qualify (as built):** the newest object wins. If they are the same age, the one nearest the gaze wins. Then it sticks with that one until it is looked at. Importance, movement, and how long something has gone unseen are not weighed yet. That ordering is what a trained model would replace with a score per object.

### Step 1 toward the model: a fair, varied test (built, not yet run)

- **`BusyJob`:** one shape pulses and the tester has to look at it, then another, and so on. It can be on the blind side, so sometimes it has to be searched for. Stops the tester hunting for the surprise. Key P runs it alone.
- **Varied surprises:** each one now appears 12 to 40 degrees into the blind side (random) and up to 15 degrees above or below the gaze. Was fixed at 25.
- **Blocks (key N):** 10 surprises back to back with the busy job, 3 to 9 s apart, saved as one recording. The shapes flash green at the end.
- **Free looking (key F):** 5 minutes of the busy job with no surprises, recorded. This is the recording that imaginary objects get placed on.
- `room_summary.csv` has a new `job` column. Shape positions and busy-job events are written into the recordings.
- How to run it all, and every key, is in [ROOM.md](ROOM.md).
- **Plan:** about 40 surprises with the nudge off plus one free-looking recording, then a Python script builds the table and trains a linear model.

### Three-way nudge test: none vs everything vs smart

All with the mask on and the busy job (Russell).

| Rule | Surprises | Never found | Typical time when found | Slowest find | Nudges shown |
|---|---|---|---|---|---|
| No nudge | 40 | 12 (30%) | 2.1 s | 11.2 s | 0 |
| Nudge on everything | 21 | 0 | 2.2 s | 18.9 s | 17 (81%) |
| Smart nudge (model decides) | 20 | 0 | 1.9 s | 5.5 s | 5 (25%) |

- **Both nudge rules removed the never-found cases.** Smart did it with 5 nudges where "everything" used 17.
- **When smart stayed quiet (15 times), he found the surprise himself every time,** typically in 1.8 s, slowest 5.5 s. So the model's "they will find it" calls held up.
- **When a nudge fired, the find came a typical 1.5 to 1.9 s later.**
- **The nudge does not always land.** Under "everything", two surprises took 9 and 19 s even though the nudge appeared within a third of a second. Worth looking at why (not seen, or not understood).
- **Unity's model matches the Python one.** For the 40 surprises with a logged guess, the live guess and the offline guess differ by 0.26 s on average (correlation 0.99).
- **Caveats:** one person, about 20 surprises per nudge rule, one evening, an easy room. The difference in nudge counts is large. The differences in find times are not big enough to read anything into yet.
- **Hops counted as finds.** Russell noticed that a quick hop of the gaze across the purple ball counted as finding it. In the data, 8 of 69 finds had the gaze on the ball for under 0.35 s (none 3 of 28, everything 1 of 21, smart 4 of 20). That flatters the smart rule a little. **Fixed going forward:** an object is now ticked off as seen only after the gaze rests on it for 0.35 s (`LookTracker.seenSeconds`). It still lights up after 0.1 s. `room_summary.csv` has a `seen_hold_s` column so old (0.10) and new (0.35) runs can be told apart.
- **Stutter, likely cause found.** Every block since the first one tonight has about a fifth of its frames late (block 1 was a steady 72 frames a second). Russell had the status panel on for all of them, including the smooth one, so the panel is probably not it. He was switching to other windows during the later blocks, and Unity slows down when it is not the front window. `RoomTest` now notes focus changes in the recording (`app_unfocused`, `app_focused`), warns on the panel, and prints how many frames were late at the end of each block. To confirm: one block without leaving Unity.

### The model, plugged into Unity (built, not yet run)

- **`MissModel`:** keeps the last 10 s of head and gaze, and for any spot works out the same eight facts the training script uses, then applies the weights. `MissModelWeights.cs` is written by `Analysis/train_model.py`, so retraining updates Unity with no hand editing.
- **Smart nudge:** key 7 now cycles off, everything, smart. In smart mode the nudge steps in only when the model guesses a miss of 8 s or more (`smartThresholdSeconds`). Safety net: if the object is still unseen after 5 s (`smartPatienceSeconds`), it nudges anyway.
- **Every surprise now records the model's guess** (`model_guess_s` in `room_summary.csv`, and a `guess:` event in the recording), whatever the nudge mode. So every run also tests the model live, and the live facts can be checked against the offline ones.
- `room_summary.csv` gains `nudge_rule` (`all` or `smart`) and `model_guess_s`.
- **Next test:** busy job, mask on, 2 blocks with the nudge on everything and 2 blocks smart, against the 40 no-nudge surprises already recorded. Compare how many were missed, how long they took, and how many nudges were shown.

### First real data and the first model

- **Recorded (Russell, mask on, nudge off, busy job on):** 4 blocks, 40 surprises, plus 5 minutes of free looking. The recorder held for every block.
- **The fair test changed the picture.** 12 of the 40 surprises (30%) were never looked at within 20 s. The 28 that were found took a typical 2.1 s. Back when he was hunting for the surprise, nothing was ever missed.
- **How things get found (from the 40 real runs):** every one of the 12 never-found surprises was never uncovered by his own looking around (it stayed on the blind side the whole 20 s). Every one of the 28 found ones was uncovered first, then looked at a typical 0.46 s later. So: you find it about half a second after your own looking uncovers it, and if nothing uncovers it you do not find it.
- **Imaginary objects check out.** 3000 imaginary objects placed on the free-looking recording, using that rule: 32% never found (real: 30%), typical time when found 1.7 s (real: 2.1 s).
- **First model (`Analysis/train_model.py`):** linear regression, 8 facts in, seconds unnoticed out (log scale), trained on the 3000 imaginary objects, tested on the 40 real surprises it had never seen.
  - Guesses for the found ones: typical 2.1 s. Guesses for the never-found ones: typical 15.8 s.
  - Given one missed and one found surprise, it gives the missed one the longer guess 99% of the time.
  - Typical error 1.4 s. Always guessing the middle value: 1.8 s. Using only "how far into the blind side": no better than chance.
- **What it learned:** two facts carry almost everything. `beyond_scan_deg` (the spot is further out than the gaze has reached in the last 10 s) and `since_uncovered_s` (how long since that spot was last uncovered). How far into the blind side the object is hardly matters. What matters is whether it sits somewhere the eyes have been visiting.
- **What this would mean for the nudge:** nudging only when the guess is over 8 s would nudge 14 of the 40, and catch all 12 that were never found plus the two slowest finds (9 and 11 s). The current rule would nudge all 40.
- **Caveats:** one person, one evening, one room, one busy job. The room makes it easy: surprises can land further out than the leftmost shape, where the job never sends the eyes. A one-line rule (`beyond_scan_deg > 0`) flags the same 14, so the model mostly rediscovered one fact. Needs more people and a less tidy scene before it means much.
- **To check:** in blocks 2 to 4 and the free look, about a quarter of frames took 50 ms (average about 45 frames a second, block 1 was a steady 72). Possibly Unity losing focus to another window. A mask that lags lets the wearer peek.

### First run of the busy job and blocks: a practice run, plus fixes

- **What was recorded (Russell):** one block of 10 surprises with the busy job. The mask was OFF for all of it, so it is a practice run, not blind-side data. No free-looking recording yet.
- **Mask off went unnoticed** because the dark room hides it. Blocks (N) and free looking (F) now refuse to start with the mask off and flash the shapes red. Shift+N or Shift+F runs them with the mask off on purpose.
- **The recorder stopped about a second into the block** and the first surprise went unrecorded. The later ones were saved one file each. Cause not found: the eyes and head look normal at that moment, so something outside `RoomTest` switched it off. Now a session restarts the recorder if it stops, prints a warning, and switches off the recorder's controller shortcut while a session runs.
- **Busy job feedback from Russell, fixed:**
  - "Look at me" was a white pulse, the same look as "you are looking at this". It is now a pulsing cyan ring around the shape, and the shape itself only turns white when looked at.
  - Hopping across a shape counted as looking at it. The gaze must now stay on it for 0.35 s (`BusyJob.holdSeconds`).
- **Gaze dots:** now hidden by default, key D shows them. Display only, so calibration, the mask and the gaze data are unaffected.
- **Status panel:** the room test now writes a line to it (key I): block progress, mask, nudge.
- **Open question:** the look check that marks an object as seen still uses a 0.1 s dwell. Russell's "I hopped over it but did not really look" applies there too. The recordings keep the raw gaze, so the threshold can be changed later in analysis without re-recording.

### Direction: from the hand-written rule to a trained score (discussed, not built)

- **Goal:** replace the fixed tie-break with a small model. Input: one object's facts at a given moment (angles from the gaze and from the head, apparent size, speed, time since that area was last checked, recent scanning, head motion). Output: seconds until the person would notice it on their own. The highest score gets the nudge.
- **Loss:** mean squared error between the predicted and the real time to first look. The model minimises its prediction error. The project's goal (shorter misses without nagging) is measured separately, with the nudge test.
- **Data idea 1, real eyes with imaginary objects:** record a few minutes of looking around with the mask on and no surprises. Afterwards, place thousands of imaginary objects along that recording and ask when the real gaze first swept over each one. Fair because an object hidden on the blind side cannot change where the eyes go. Only valid while the object stays hidden.
- **Data idea 2, Russell's "eye blueprint" (later layer):** measure how a person's eyes move (how long they rest, how far and which way they jump, how often they check the blind side, what pulls them, when the head joins in), then generate realistic random eye paths from that profile and run them against randomised scenes. Like a measured BRDF, sampled many times. Must be checked against the person's real miss times before anything is trained on it. Could also become a per-person profile the nudge adapts to.
- **Scope note:** for this project the focus stays on the blind side. The general version is "anywhere the person's own vision cannot catch it", which later could include a patient's personal field map and things outside the view.
- **Passthrough note:** everything from the per-object table onward carries over. The part that fills the table (Unity's knowledge of every object) does not, and needs a camera plus object detection. A stepping stone is to treat Unity's rendered frame as a fake camera feed.

## 2026-11-11

### Tamanna's moving block, logger and analysis script: hooked up and run

- Added `TrialLogger` and `MovingBlock` (in `Hemianopia_TAMANNA/`) to the `Trials` object in `Sim.unity`. They use the existing gaze and filter code as is.
- **Square path, mask off:** gaze follows the block's path, typically 2 to 3 degrees off, against about 0.3 when staring at a still dot. The reading freezes, then hops about 5 degrees, roughly 4 times a second. Could be real catch-up eye jumps, the headset's hold-and-snap, or both. Noted as a known quirk, not a blocker.
- **Midline crossing, mask on:** with the eyes held on the centre dot (within 1 degree for 92% of the run), the block appeared and vanished 4 times, each within about half a degree of the dot. Russell saw it pop in and out cleanly, with no flicker or leak.
- Run the square with the mask OFF. With it on, the block vanishes whenever it slips to the blind side of the gaze.

### Room v0: shapes, the look check, and the first surprise (new `Scripts/Room/`)

- **`TrackedObject`:** a name tag plus a small memory for one object (ever seen, last seen, on the blind side, hidden by the mask).
- **`LookTracker`:** works out which object the gaze is resting on. A beam, not a single ray: within 3 degrees plus the object's size, at least partly on the seeing side, held for 0.1 s. Looked-at objects brighten.
- **`RoomTest`:** key 5 places five shapes around you. Key 6 runs one surprise: a purple ball appears on the blind side, and the test times how long until it is first looked at. Results go through `TrialLogger`, plus one line per surprise in `CalibrationLogs/room_summary.csv`.
- **First numbers (Russell, 10 tries, plain room):** typical time to find about 0.6 s with the mask off and about 2.3 s with it on. One person, tiny sample, and he knew a surprise was coming, so this shows the direction only.
- **Fix:** the surprise now only appears while the eyes are resting on something. One try was "found" in 0.12 s because the eyes were already mid-jump toward the spot.

### Nudge v0 and the lab look

- **`NudgeCue`:** three amber chevrons beside the gaze, on the seeing side, pointing toward the missed object and lighting up in sequence. Key 7 switches the nudge on or off (it flashes once as a preview).
- **Rule:** nudge for anything that is on the blind side, has never been looked at, and has been there for 1 s. If several qualify, the newest wins. Once everything has been looked at, it goes quiet. (The first version only covered the surprise.) "Looked at" means the gaze rested on it, so something only caught in the corner of the eye still counts as unseen.
- **`LabEnvironment`:** a dark grid room built by code at Play (key 8 toggles it). Cosmetic only. Walls are kept dim so the background does not make things harder to find.
- A dark room also makes the blind half read less like a black curtain and more like a gap, which is closer to what patients describe. Harder to see whether the mask is on, so a tint for filming may be needed later.
- Do not compare tries from the plain room with tries from the lab room. Redo the no-nudge tries in the lab look.

### Open

- The surprise always lands on the blind side and the tester is expecting it. Needs a way to stop people hunting for it (Tamanna).
- Recordings are not in git (on purpose). Share them directly.

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
- **v2 A/B (Nov 6, time-matching accidentally ON in all runs, glances removed):**

| | Anchoring OFF (2 runs) | Anchoring ON (3 runs) |
|---|---|---|
| Error, head still | 0.73° | 0.64° |
| Fixed lean toward the turn | +1.35° | **+0.23°** |
| Error, 5 to 40 °/s | 1.68° | 1.54° |
| Error, 40 to 120 °/s | 3.20° | 3.48° |
| Error, 120 °/s and up | 6.57° | 6.59° |

- **The still-head cost is gone** (1.64° in v1, 0.64° now) and **the fixed lean is nearly gone** (+0.23°).
- **Fast turns didn't improve, but that's time-matching, not anchoring.** It was ON (assumes 80 ms, real latency is about 5 to 10 ms), so it over-corrected by about 30 ms in both conditions (delay −24 to −34 ms). At 150 °/s that alone is about 5° of error, which swamps the fast phase. Needs a clean rerun with time-matching OFF.
- **Clean v2 A/B (Nov 6, time-matching OFF, 2 runs each, glances removed):**

| | Anchoring OFF | Anchoring ON |
|---|---|---|
| Error, head still | 0.23° | 0.25° |
| Error, 5 to 40 °/s | 2.37° | 1.86° |
| Error, 40 to 120 °/s | 2.72° | 1.83° |
| Error, 120 °/s and up | 4.47° | 3.86° |
| Fixed lean (fit) | +1.71° | +1.00° |
| Delay (fit) | +11 ms | +9 ms |

- **Verdict:** v2 is a safe, across-the-board improvement. No cost with the head still, 15 to 35% less error during turns, and the fixed lean down about 40%. It doesn't cancel the lean completely: about 1° is left (more at fast speeds).
- **Possible reasons for the leftover:** catch-up snaps during fast turns landing more than 1.5° from the anchor (which re-anchors at a leaning spot), or some real eye lag (healthy eyes don't counter-rotate perfectly at high speeds).
- **Decision for now:** keep v2 on by default. Further tuning has diminishing returns compared with the rest of Step 1 and the test kitchen.

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
