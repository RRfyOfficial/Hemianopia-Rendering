# The room test: what it is and how to run it

Scripts live in `Assets/Hemianopia_Project/Scripts/Room/`. They are attached to the `Trials` object in `Sim.unity`. `RoomTest` adds the helper scripts it needs when Play starts.

## The pieces

| Script | Job |
|---|---|
| `TrackedObject` | A name tag plus a small memory on each object: ever looked at, when, on the blind side right now |
| `LookTracker` | Works out which object the gaze is resting on (3 degree beam, seeing side only, 0.1 s dwell) |
| `RoomTest` | The five shapes, the surprise, the nudge rule, blocks, free looking, and the results file |
| `NudgeCue` | Draws the chevrons that point at a missed object |
| `BusyJob` | "Look at whichever shape is pulsing", so the tester is not hunting for the surprise |
| `GazeLaser` | Shows the gaze as a laser plus the beam ring |
| `LabEnvironment` | The dark grid room (cosmetic) |
| `MissModel` | The trained model, live: guesses how long an object at a spot would go unnoticed |
| `MissModelWeights` | The model's numbers. Written by `Analysis/train_model.py`, never edited by hand |

## Keys in Play mode

| Key | What it does |
|---|---|
| H | Mask (simulated hemianopia) on or off |
| B | Flip the blind side |
| 5 | Show or hide the five shapes |
| 6 | Run one surprise |
| 7 | Nudge: off, then on for everything, then smart (the model decides), then off again |
| 9 | Nudge placement: stays in the room, or follows the gaze |
| 8 | Lab look on or off |
| G | Gaze laser on or off |
| D | Gaze dots on or off (they start hidden) |
| I | Status panel on or off. It shows the mask, the nudge, and what the room test is doing |
| P | Busy job on or off, with nothing else running |
| N | Run a block of 10 surprises with the busy job. Press again to stop after the current one |
| F | Record 5 minutes of free looking with the busy job and no surprises. Press again to stop early |

N and F will not start while the mask is off. The shapes flash red instead. In the dark room it is easy not to notice the mask is off. Hold Shift with N or F to run them with the mask off on purpose.

What the colours mean: a **cyan ring** around a shape means "look at this one" (the busy job). A shape turning **white** means "you are looking at it right now". **Green** flash means a block or recording finished. **Red** flash means it did not start.

Keys 1 to 4, 0 and L belong to `MovingBlock` and `TrialLogger` (see `Hemianopia_TAMANNA/`).

## Collecting data for the model

1. Press Play. Turn the mask on (H). Show the shapes (5) and look at all five once.
2. Make sure the nudge is OFF. The training data is how long things go unnoticed with no help.
3. Press N for a block of 10 surprises. Do the job: look at whichever shape has the cyan ring. The shapes flash green when the block is done.
4. Repeat until you have about 40 surprises.
5. Press F for 5 minutes of free looking. Same job, no surprises.

## What gets saved (in `CalibrationLogs/`, never committed)

- `trial_<person>_roomblock_<time>.csv`: one recording per block, every frame, with events
  (`block_start`, `shape:<name>@x|y|z`, `job_target`, `job_hit`, `job_skip`, `trial_start`, `appear:surprise`,
  `first_look:surprise` or `timeout:surprise`, `nudge_on:<name>`, `trial_end`, `block_end`).
- `trial_<person>_freelook_<time>.csv`: the free-looking recording.
- `room_summary.csv`: one line per surprise: mask, blind side, how far into the blind side it appeared,
  found or timeout, seconds unnoticed, nudge on or off, when the nudge appeared, cue placement, busy job on or off.

## From recordings to a model (`Analysis/`)

Run these from the project folder, with Python, numpy, pandas (and matplotlib for the picture):

```
python Analysis/build_table.py
python Analysis/train_model.py
```

| Script | Job |
|---|---|
| `gaze_tools.py` | Shared helpers: read a recording, work out where a spot is compared with the gaze, and when it would be noticed |
| `build_table.py` | Builds two tables: one row per real surprise, and thousands of imaginary objects placed on the free-looking recording |
| `train_model.py` | Trains a linear model on the imaginary objects, tests it on the real surprises, prints how it did, saves the model |

Everything they write goes to `CalibrationLogs/analysis/`, which is never committed.

## Comparing the three nudge rules

Same setup each time: mask on, busy job, blocks with N. Only key 7 changes.

| Rule | Key 7 state | What the nudge does |
|---|---|---|
| No nudge | OFF | Nothing. This is the baseline |
| Everything | ON | Points at anything on the blind side that has not been looked at |
| Smart | SMART | Asks the model first. Steps in only if it guesses a miss of 8 s or more, or if the object is still unseen after 5 s |

For each surprise, `room_summary.csv` records the rule (`nudge_rule`), whether and when the nudge appeared (`nudge_at_s`), how long the surprise went unnoticed (`miss_s`), and the model's guess (`model_guess_s`, written in every mode).
