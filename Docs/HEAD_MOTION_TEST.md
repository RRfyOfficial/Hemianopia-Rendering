# Head-motion test

The calibration check measures the tracker with your head still. This test measures it while your head moves, and splits the error into two parts: **latency** (the eye data arriving late) and **noise** (random wobble).

File: `Assets/Hemianopia_Project/Scripts/Diagnostics/HeadMotionTest.cs`

## 1. The idea

Where your gaze points in the world = where your head points + where your eyes point inside your head.

Stare at a dot fixed in the room and turn your head: your eyes counter-rotate to stay on it. So a perfect system shows **zero error at any head speed**.

If the eye data arrives Δ seconds later than the head data, the system adds *now's* head to *slightly old* eyes. The gaze overshoots in the direction you're turning, by head speed times the delay:

$$e(t) \approx \omega(t)\,\Delta + b + \varepsilon(t)$$

| Symbol | Meaning |
|---|---|
| $e$ | Horizontal gaze error, degrees (+ = gaze right of the dot) |
| $\omega$ | Head turning speed, degrees per second (+ = turning right) |
| $\Delta$ | The delay we want, seconds |
| $b$ | Leftover constant offset |
| $\varepsilon$ | Noise |

But a delay isn't the only thing that pushes gaze toward the turn. A **fixed lean** does too: the same offset at any speed, for example the headset or the eyes slipping a little while the head moves. The two look identical when the turns are slow, and only separate at speed:

| | What it looks like across speeds |
|---|---|
| Delay $\Delta$ | Offset grows with speed: $\Delta \cdot \omega$ |
| Fixed lean $L$ | Same offset at any speed: $L \cdot \operatorname{sign}(\omega)$ |

So the test fits both at once:

$$e = c + L\,\operatorname{sign}(\omega) + \Delta\,\omega + \varepsilon$$

by least squares over the slow and fast phases, leaving out frames slower than 5 °/s. That's three unknowns $(c, L, \Delta)$, solved from the 3 × 3 normal equations $(X^\top X)\,\beta = X^\top e$ with rows $x_i = (1,\ \operatorname{sign}\omega_i,\ \omega_i)$.

- **$L$** is the fixed lean toward the turn, in degrees.
- **$\Delta$** is the delay (degrees per degree-per-second = seconds).
- **$R^2$** says how much of the error the two explain together. What's left is the noise while moving.

**Lesson learned.** The first version fitted only a delay. With slow turns, a fixed 2° lean came out as an "80 ms delay." Fast turns (100 to 200 °/s) showed the lean stays put while the delay is small. Always make the fast phase properly fast.

## 2. What happens

1. Press **T** (or the **left grip**). The hemianopia filter and gaze dot switch off.
2. A white dot appears 2 m straight ahead at eye height. It stays fixed in the room.
3. The status panel walks you through it:
    - 3 s countdown
    - **Still** (4 s): hold your head still, eyes on the dot
    - **Slow** (8 s): turn your head slowly left and right, eyes on the dot
    - **Fast** (8 s): turn faster, eyes on the dot
4. The result goes to the status panel, the console (with a plain-language reading), and a CSV.

## 3. What it reports

| Number | Meaning |
|---|---|
| Error still / slow / fast | RMS angle between gaze and dot in each phase, degrees |
| Head speed (slow, fast) | Average turning speed in that phase, °/s |
| Lean | Fixed offset toward the turn, degrees |
| Delay | Offset that grows with speed, in ms |
| Explains | $R^2$ of lean + delay together, as a percentage |
| Noise | RMS of what the fit doesn't explain, degrees |
| Eye data age | Median age of the eye sample when it was used, ms (a direct, second look at latency) |
| Time-matching | On or off, and the share of frames that actually got time-matched |
| Tracker rate | Fresh eye samples per second |
| Frame rate | Rendered frames per second |

The CSV (`CalibrationLogs/headmotion_<code>_<time>.csv`, never committed) has one row per frame: time, phase, yaw and pitch speed, horizontal and vertical error, total error, whether a fresh tracker sample arrived, whether the correction was on, whether the frame was time-matched, and the eye sample's age.

## 4. How to use it

- **Run it once over Link and once as a native build on the headset.** If the delay drops a lot in the native build, Link is a big part of the jitter.
- **Run it with fixation anchoring on and off** (K). The test measures the gaze the hemianopia filter actually uses, so this is the A/B for the anchoring fix in [GAZE.md](GAZE.md), section 6. With it on, the fixed lean should drop toward 0.
- **Run it with time-matching on and off** (M). This is the A/B test for the latency fix in [GAZE.md](GAZE.md), section 5. Off should show the ~80 ms delay. On should pull it toward 0. Negative means it over-corrects.
- **Run it with the correction on and off** (V) to check the correction doesn't hurt during motion.
- **Turn properly fast in the fast phase**, like a quick "no" headshake. The first runs never went past about 60 °/s.
- **Compare against the calibration noise** (about 0.3° for P01). If the error while moving is much bigger and follows the line, latency is the thing the smart smoothing has to deal with.

## 5. Known limitations

- **Horizontal only.** The delay is fitted on left-right turning. Vertical speed and error are logged in the CSV but not fitted yet.
- **It assumes your eyes really stay on the dot.** A glance away during the test adds error that isn't the tracker's fault.
- **Head speed comes from frame-to-frame camera rotation**, so it's as noisy as the frame timing.
- **One combined delay.** It can't tell tracker latency from rendering or Link latency, only the total mismatch between eye data and head data.
