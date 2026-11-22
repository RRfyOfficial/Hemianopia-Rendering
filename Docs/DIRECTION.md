# Where this project is going

Chosen on 21 Nov 2026 (Russell). Read this before planning any new work.

Everything under "The plan" is agreed. Everything under "Not decided yet" is open, so ask before
picking one. Nothing in this file is built yet unless it says so.

## The goal in one line

Warn someone with hemianopia about things coming at them from the blind side, and only when they
would not notice in time on their own.

The Unity project on the Quest Pro is the simulator where we work this out. The place we want it
to end up is passthrough on the Meta VR Glasses, with real objects found by computer vision.

## The plan

### Three questions

A nudge is worth showing when something matters and the wearer will not notice it in time. That
splits into three questions.

| Question | In Unity today | Later, on the glasses | Status |
| --- | --- | --- | --- |
| 1. What is out there, and where? | Free. Unity knows every object | Cameras plus computer vision | Nothing to build yet |
| 2. How much time is left before it matters? | Missing. Every surprise counts the same | Depth, and how fast the thing is closing in | **Not built. This is the gap** |
| 3. Will the wearer notice it in time on their own? | The miss-time model | The same model, fed by the glasses' eye tracking | Built for still objects |

### The rule

Nudge when the answer to question 3 is longer than the answer to question 2.

```
time left   = distance to the object / how fast it is closing in
model guess = seconds until the wearer would notice it without help

nudge when model guess > time left
```

Example: something will reach the wearer in 3 s and the model guesses 10 s to notice it. Nudge.
A chair that has been standing there all along is not closing in on anyone. Stay quiet.

Today the smart nudge compares the model's guess with a fixed 8 s
(`smartThresholdSeconds` in `RoomTest.cs`). The fixed 8 s is what the time left replaces.

### The first version

- The wearer stands still. The Quest Pro is tethered over Link, so the objects do the moving.
- Objects approach from the blind side while the busy job keeps the eyes occupied.
- Score: how many objects were looked at before they arrived, against how many nudges were shown.
- Compare the same three rules as before: no nudge, nudge on everything, smart.

### Why approaching objects and not things on a desk

A still object has no deadline, so "does it matter?" would mean guessing what the wearer wants.
An approaching object gives the deadline for free (distance divided by closing speed). It also
matches the problem people with hemianopia describe: collisions with people and things coming
from the blind side. See "Background reading".

## What stays and what changes

**Stays as it is:** the mask, the gaze code, the recorder (`TrialLogger`), the busy job, the
nudge arrows, the 8 inputs, and the imaginary-object way of making training data.

**Changes (none of this is built as of 21 Nov 2026):**

1. **The surprise moves.** `RoomTest.TrySpawn` places the ball once, 2 m away (`distance`), and it
   never moves. It needs a start distance, a speed, and an "arrived" event in the recording.
   The ball's position is already written to the recording every frame, so the recorder needs no
   change for this.
2. **The nudge rule.** `RoomTest.ModelSaysNudge` compares the guess with `smartThresholdSeconds`.
   It should compare the guess with the time left. It already asks the model again every 0.2 s
   with the object's current position, so a moving object works with the call as it is. Whether
   the guesses are any good for a moving object is untested.
3. **The analysis.** Three places assume a still object:
   - `build_table.py` reads only the first logged position of each real surprise.
   - `build_table.py` places each imaginary object at one fixed point, `OBJECT_DISTANCE_M` (2 m) away.
   - `gaze_tools.view_of_point` takes one fixed point.

   All three need a position per frame. Imaginary objects must move the same way the real ones
   do. The trick stays fair for a moving object: while it is hidden on the blind side it still
   cannot change where the eyes go.
4. **The results.** `room_summary.csv` needs the time left when the object appeared, and whether
   it was looked at before it arrived.
5. **`MissModel.cs` and `gaze_tools.py` must still match** after any of this. See `CLAUDE.md`.

## Not decided yet

Ask before choosing any of these.

- How fast the objects move, and how far away they start.
- What counts as "arrived" (how close is too close).
- Whether objects come straight at the wearer, or also cross in front.
- A safety margin on the rule. Nudging exactly when the guess passes the time left leaves no
  time to react.
- Whether the model needs new inputs for a moving object (for example its speed), or whether
  asking about its current position is enough.
- What the object looks like. It is a purple ball today.

## Field of view

Russell raised this on 21 Nov and it is already in our data: in Tamanna's slow-nudge pictures,
the slowest finds were ones where the ball had left the headset's view (see `DEVLOG.md`).

| | Side to side | Up and down | Source |
| --- | --- | --- | --- |
| Human vision, eyes still | About 140 to 210 degrees, depending on the source and how it is measured | About 135 to 150 | Textbook and lecture figures |
| Quest Pro (what we test on) | 106 | 96 | Meta's published spec |
| Meta VR Glasses (the target) | 70 | 66 | Meta developer docs, Sept 2026 |

What that means for us:

- **Our code assumes the Quest Pro.** The view is taken as 48 degrees to each side and 42 up and
  down: `FOV_AZ_DEG` and `FOV_EL_DEG` in `gaze_tools.py`, `ViewHalfWidthDegrees` and
  `ViewHalfHeightDegrees` in `MissModel.cs`. Surprises spawn at most 35 degrees to the side of
  the head (`maxHeadAzimuthDegrees` in `RoomTest.cs`). On the glasses the view is 35 degrees to
  each side, so these numbers and the model would have to follow.
- **Things can approach from outside the view.** Experiments should include that case and not
  hide it. One study in "Background reading" puts most of the collision risk more than 50
  degrees to the side, which is outside what the glasses show.
- **A cue for the head, not only the eyes.** When the object is outside the view, eye movements
  alone cannot uncover it. An idea, not built: have the arrows ask for a head turn in that case.
- **Meta ships a field-of-view simulator.** It shows the Meta VR Glasses' view on a Quest
  headset, and Meta's page lists the Quest Pro. In Unity it is a "Simulate Device Field of View"
  setting on `OVRManager` (under experimental features), or the command
  `metavr device fov-sim enable`. **Not tried by us.** The project is on Meta XR Core SDK
  207.0.1 and we have not checked that this version has the setting.
  - https://developers.meta.com/horizon/documentation/unity/in-headset-fov-simulation
  - https://developers.meta.com/horizon/essentials/field-of-view

## What carries over to passthrough, and what does not

**Carries over**

- The method behind question 3, and the 8 inputs. They are only angles between the gaze, the
  head and a spot. Nothing in them is tied to Unity.
- The imaginary-object way of making training data. It needs a recording of eyes and head and
  nothing else, so it works on recordings from real life with no computer vision.
- The nudge rule.

**Does not carry over**

- Object positions. Unity hands them over. On the glasses that takes detection, depth and
  tracking. Depth can come from two cameras: the same object lands in a slightly different place
  in the left and right image, and the bigger the shift, the closer it is.
- The model's weights. They come from one person in one dark, tidy room.
- "Noticed about 0.46 s after it is uncovered." Measured on one person in an empty room. In a
  cluttered scene people sweep past things without registering them.
- The mask. A patient's own measured field of vision replaces it.

**Unknown**

- Whether apps on the Meta VR Glasses can read the camera feed. Meta's Passthrough Camera API
  is documented for the Quest 3 and 3S. Check this before planning the computer vision part.
- The glasses were announced in Sept 2026 with eye tracking, and are expected in spring 2027.
  Details may change before then.

## Background reading

For Tamanna to read properly and correct us on. These notes are from the abstracts only.

- Doyon, Kim, Hwang, Jung (2025). Pedestrian collision avoidance in hemianopia during natural
  walking in immersive virtual reality. https://arxiv.org/abs/2510.04218
  People with hemianopia report trouble avoiding other walkers. Tested in a headset with
  virtual pedestrians.
- Alberti, Peli, Bowers (2014). Driving with hemianopia: III. Detection of stationary and
  approaching pedestrians in a simulator. https://pmc.ncbi.nlm.nih.gov/articles/PMC3900270
  Blind-side pedestrians were missed more often, and some that were detected were detected too
  late. "Noticed, but not in time" is the case our rule is built around.
- Kurukuti and others (2024), Peli lab. Collision risk between a walker with hemianopia and
  other pedestrians. https://pelilab.partners.org/papers/Kurukuti_OVS_2024.pdf
  Risk was highest from people more than 50 degrees to the side and closer than 2 m.
