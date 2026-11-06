# Saccade test

How fast does the tracker follow a real eye jump (a saccade)? With the head still, a dot jumps 20° at random moments and you follow it as fast as you can.

File: `Assets/Hemianopia_Project/Scripts/Diagnostics/SaccadeTest.cs`

## 1. Why it matters

- **For the simulation:** right after an eye jump is the moment someone could peek into the blind half. If the tracker's reading lands late, the hemianopia boundary lands late too.
- **For the 2° lean during head turns:** a tracker that follows jumps about as fast as real eyes isn't smoothing much, so the lean is more likely the eyes slipping. A slow tracker points to the headset smoothing its own output, which would also explain the lean (see [DEVLOG.md](DEVLOG.md), strap test and sawtooth check).

Real 20° saccades take roughly 60 to 80 ms from start to finish, which is about 40 to 50 ms for the middle 10 to 90% of the movement, at peak speeds of several hundred degrees per second. These are textbook ballpark figures, good enough to tell "about as fast as eyes" from "much slower."

## 2. What happens

1. Press **J** or the **right grip**. The hemianopia filter and gaze dot switch off.
2. A white dot appears straight ahead. It moves **with your head**, so only your eyes matter. Keep your head still anyway.
3. After a 3 s countdown and a short hold, the dot jumps 16 times: out 20° to a random side, back to the center, and so on. The wait between jumps is random (1 to 1.6 s), so you can't jump early.
4. Follow each jump as fast as you can.
5. Results go to the status panel, the console (with a plain-language reading) and a CSV in `CalibrationLogs/` (never committed).

## 3. What it measures, per jump

Only fresh tracker samples are used, each timed by its own timestamp. Over Link that clock has an unknown starting point, but the gaps between samples are real durations.

| Number | How |
|---|---|
| Baseline | Median eye position over the last ~8 samples before the jump |
| Final | Median eye position from 0.5 s after the jump to the end of the hold |
| Amplitude $A$ | Final minus baseline, toward the jump. Jumps where $A$ is under half the jump size are skipped (the eyes didn't really follow). |
| Rise time | Time between reaching $0.1A$ and $0.9A$, each moment interpolated between the two samples around it |
| Peak speed | Fastest movement between two consecutive samples around the jump, °/s |
| Overshoot | Farthest past the final position within 200 ms of reaching $0.9A$ |
| Reaction | Dot jump to the reading reaching $0.1A$, on the Unity clock. Includes the person's reaction time and the pipeline delay, so it's not a tracker number by itself. |

The summary reports medians across the usable jumps, and the typical gap between samples (about 19 ms at ~52 Hz), which is the timing resolution.

## 4. Reading the result

| Result | Meaning |
|---|---|
| Biggest single step ≥ 80% of the jump | The reading **snaps**: it jumps between two samples, faster than real eyes can move. The headset runs its own hold-or-snap filter (see below). |
| Rise ≤ 70 ms and peak ≥ 300 °/s | About as fast as real eyes. Little smoothing. The head-turn lean is more likely the eyes. |
| Rise ≥ 100 ms or peak < 200 °/s | Much slower than real eyes. The headset smooths gaze, and the boundary lags after every eye jump. |
| In between | Some smoothing. Run it again. |

## 5. First result (Nov 5, P01, 4 runs, 60 usable jumps)

- **Rise 14 to 28 ms, peak speed 720 to 1,240 °/s, no overshoot.** Faster than real eyes physically move (a real 20° jump peaks at roughly 400 to 500 °/s).
- **The traces show steps, not movements.** The biggest single-sample step was a median **97%** of the whole jump. In 37 of 60 jumps, not one sample landed mid-movement. Example: the reading sat at exactly −2.8° for four samples, then moved 16° in one 16.7 ms step.
- **Between jumps the reading is nearly frozen:** a median change of 0.01° from one sample to the next, with 7% of consecutive samples exactly identical (stretches up to 217 ms).
- **Conclusion:** the headset doesn't pass raw eye data. It holds gaze steady during fixations and snaps on saccades, its own version of our hold-or-snap fixation filter.
- **This explains the head-motion results too.** While you stare at a dot and turn your head, your eyes counter-rotate smoothly, but the headset holds the eye reading until the change is big enough, then snaps. So the reading lags the real eyes by a bounded amount, the gaze leans toward the turn by about the same amount at any speed (~2°), the error creeps then snaps back (the weak sawtooth), and it takes ~100 ms to flip at a reversal.
- The test's first automatic reading said "more likely your eyes slipping." That was wrong: it assumed a fast tracker means no filtering. It now checks the step size first.

## 6. Known limitations

- **Timing resolution is one sample gap** (about 19 ms). A real saccade spans only 3 or 4 samples, so single jumps are rough. The medians over 16 jumps are what count.
- **Peak speed between samples underestimates** the true peak, because each pair of samples averages over ~19 ms.
- **Horizontal jumps only.**
- **A blink or a lazy jump** gets skipped by the amplitude check, but a half-hearted follow still slows the numbers. Follow every jump as fast as you can.
