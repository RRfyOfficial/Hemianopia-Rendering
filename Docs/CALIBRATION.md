# Calibration check (Step 1)

Meta's built-in calibration gets the eye tracker close. Our calibration check measures how far off it still is for the person wearing the headset right now, fixes the part that can be fixed, and records the part that can't.

Files:

| File | Job |
|---|---|
| `Assets/Hemianopia_Project/Scripts/Calibration/CalibrationRunner.cs` | Runs the check: dots, recording, stats, results picture, CSV |
| `Assets/Hemianopia_Project/Scripts/Gaze/GazeCorrection.cs` | The correction math: angles, least-squares fit, applying it |
| `Assets/Hemianopia_Project/Scripts/Gaze/GazeProvider.cs` | Applies the correction to live gaze |

## 1. Why: two kinds of error

Think of a bathroom scale.

- **Bias:** the scale always reads 2 kg heavy. Fixable: subtract 2. For us, the boundary sits consistently a bit off from where you're really looking.
- **Noise:** the needle shakes. Subtracting doesn't help, only averaging over time does. For us, that's the jitter.

| | Bias | Noise |
|---|---|---|
| What it looks like | The boundary sits a bit off, the same way every time | The boundary shimmers |
| Fixed by | This calibration check | Smart smoothing (next step) |
| Measured here as | Accuracy: distance from the dot to the average reading | Precision: wobble of the readings around their own average |

This came out of the Nov 5 check-in with Prof. Zwicker. He traced the jitter to the tracker's own estimation errors plus the delay between the eye tracker and Unity, and suggested temporal smoothing. Calibration comes first because its noise numbers tell the smoothing how strong to be.

## 2. What happens

1. Press **C** (or click the **left thumbstick**). The hemianopia filter and the gaze dot switch off so they don't block or pull the eyes.
2. Nine white dots appear one at a time, in a 3 × 3 grid. The dots move **with your head**, so only your eyes matter, not where your head points.
3. Each dot shrinks from 3× to 1× over 0.6 s to pull the eyes to its center, then the system records gaze for 1 s.
4. After the last dot, it computes the stats, fits a correction, turns it on, prints a one-line summary in the console, and saves a CSV.
5. A results picture stays up for 12 s (press **C** again to close it early):
   - **white** = where each dot really was
   - **red** = where the tracker said you looked
   - **green** = after the correction
6. The filter and the dot come back on.

Press **V** (or click the **right thumbstick**) any time to turn the correction off and on, to feel the difference.

| Dot grid | Left | Center | Right |
|---|---|---|---|
| Up | (−20°, +12°) | (0°, +12°) | (+20°, +12°) |
| Middle | (−20°, 0°) | (0°, 0°), shown first | (+20°, 0°) |
| Down | (−20°, −12°) | (0°, −12°) | (+20°, −12°) |

Angles are (left/right, up/down) from straight ahead, relative to the head. All of them are settings.

### Seeing it in the headset

- **Status panel** (`StatusHud`): floats just below your view and shows the gaze source, the correction as **ON** (green), **OFF** (orange) or *none yet*, the last check's numbers, and the filter state. Anything you switch flashes in big text for a moment. Toggle the panel with **I** or the left controller's **menu button**.
- **Ghost dot:** while the correction is on, a smaller red dot appears next to the green one. It shows where the tracker alone would have put your gaze. Correction off = no ghost, and the green dot moves back to the tracker's reading.
- The panel is a normal object in the scene, so the hemianopia filter hides it too when it falls on your blind side. Look at its left edge to read it, or press H to turn the filter off.

## 3. The math

**Directions as two angles.** A head-space direction $\mathbf{d}$ becomes an azimuth (left/right) and an elevation (up/down), in degrees:

$$\theta = \operatorname{atan2}(d_x,\ d_z), \qquad \phi = \operatorname{atan2}\left(d_y,\ \sqrt{d_x^2 + d_z^2}\right)$$

**Per dot.** With the readings $\mathbf{m}_i = (\theta_i, \phi_i)$ for $i = 1 \dots N$, their average $\bar{\mathbf{m}}$, and the dot's true angles $\bar{\mathbf{t}}$ (measured from the eyes' actual origin, not the camera center):

$$\mathbf{b} = \bar{\mathbf{m}} - \bar{\mathbf{t}} \qquad \text{(bias)}$$

$$\text{accuracy} = \lVert \mathbf{b} \rVert, \qquad \text{precision} = \sqrt{\frac{1}{N}\sum_{i=1}^{N} \lVert \mathbf{m}_i - \bar{\mathbf{m}} \rVert^2}$$

Before averaging, readings more than 5° from the dot's median reading are dropped as glances away. A dot needs at least 15 kept readings. Only fresh tracker samples count (a new timestamp), so a blink can't add copies of an old reading.

**The correction** is an affine map from tracker angles to true angles:

$$\begin{bmatrix} \theta' \\ \phi' \end{bmatrix} = \begin{bmatrix} a_0 & a_1 \\ b_0 & b_1 \end{bmatrix} \begin{bmatrix} \theta \\ \phi \end{bmatrix} + \begin{bmatrix} a_2 \\ b_2 \end{bmatrix}$$

It can shift, stretch and slightly shear the tracker's angles. It's fitted by least squares over the usable dots $k$:

$$\min_{a,\,b}\ \sum_k \left\lVert \begin{bmatrix} \theta'_k \\ \phi'_k \end{bmatrix} - \bar{\mathbf{t}}_k \right\rVert^2$$

which splits into two 3 × 3 systems (one per angle), solved directly. It needs at least 6 usable dots.

**Sanity limits.** Real bias is a few degrees and the scale stays close to 1. A fit is rejected if an offset exceeds 10°, a scale leaves 0.6 to 1.4, or a cross term exceeds 0.4. That usually means someone looked away or blinked a lot.

## 4. How it plugs in

`GazeProvider` now keeps two directions:

| Property | Meaning | Who uses it |
|---|---|---|
| `TrackerDirectionHead` | Straight from the tracker, before correction | The calibration check only, so it always measures the tracker's own error |
| `GazeDirectionHead` | After correction, when one is active | Everything else: `RawGazeRay`, the fixation point, the filter, the dot |

"Raw" in `RawGazeRay` still means *unsmoothed*. It is bias-corrected when a calibration is active. A correction learned from the mouse is never applied to real eyes, or the reverse.

## 5. How to run it

**One-time setup:** add `CalibrationRunner` and `StatusHud` to the `Gaze` object in `Sim`. They find `GazeProvider`, `HemianopiaController`, `GazeDot` and each other on their own.

**Each session:**

1. Run Meta's built-in eye calibration on the headset first.
2. Set `participantCode` on `CalibrationRunner` (a code like P01, never a name).
3. Press Play, put the headset on, press **C**, follow the dots.
4. Read the result on the status panel in the headset, or the console line, for example: `P01 (eyes): 9/9 dots usable. Error 1.42° before, 0.51° after correction (same data, so optimistic). Noise 0.38° at center, 0.66° at the outer dots.`

**Nothing in the console?** Check the three toggle icons at the top right of the Console window. The white speech-bubble one (normal messages) has to be on. Also clear the search box, and make sure the dropdown next to Error Pause says Editor.

**Where the CSV goes:** `CalibrationLogs/` next to `Assets/` in the editor, the app's private storage on the headset. That folder is in `.gitignore`, because it's participant data and the repo is public.

**CSV columns:** dot position, whether it was used, sample counts, true angles, tracker angles, bias, accuracy, precision, corrected accuracy. The first lines (starting with `#`) hold the participant, the time, and the fitted correction.

## 6. Settings (on `CalibrationRunner`)

| Setting | Default | Notes |
|---|---|---|
| `horizontalExtentDegrees` | 20 | Outer dots, left and right |
| `verticalExtentDegrees` | 12 | Outer dots, up and down |
| `dotDistance` | 2 m | How far away the dots float |
| `settleSeconds` | 0.6 s | Time for the eyes to land before recording |
| `recordSeconds` | 1.0 s | Recording time per dot |
| `minSamplesPerDot` | 15 | Fewer kept samples and the dot is skipped |
| `glanceAwayDegrees` | 5° | Readings farther than this from the median are dropped |
| `minDotsForFit` | 6 | Fewer usable dots and no correction is fitted |
| `dotSizeDegrees` | 0.6° | Dot size, in visual angle |
| `resultsSeconds` | 12 s | How long the results picture stays up |

All defaults are a first guess. Tune them after the first runs.

## 7. Known limitations

- **"After" is optimistic.** The after-correction error is measured on the same readings the fit learned from. The honest check is a second run with the correction on. A proper validation mode is the next small addition.
- **The headset shifts on the face**, so bias drifts over a session. Re-run the check after taking the headset off or after a lot of movement.
- **It only covers ±20° by ±12°.** Outside that the correction is extrapolated, which gets less trustworthy toward the edges.
- **Angle distances are approximate.** Accuracy and precision use distances in (azimuth, elevation) degrees, which is close to the true angle near the center and slightly off far out.
- **The mouse mode is only a pipeline test.** A mouse has no bias, so the fit comes out close to "do nothing."

## 8. What the numbers feed next

- **Smart smoothing (next step):** the noise numbers, center vs outer dots, set how strong the smoothing is and whether it should be stronger toward the edges.
- **The "seen" cone (Step 4):** accuracy plus noise set how wide the cone has to be so a looked-at object reliably counts as seen.
