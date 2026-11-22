# Notes for Claude Code (and for people)

Read this first. It tells you what this project is, where to work, and what never to do.

## What this is

A hemianopia simulator and helper, built in Unity for the Meta Quest Pro.

Hemianopia is the loss of the same half of the field of view in both eyes, usually after a stroke.
We black out half the view and keep that half locked to the eyes. Then we test a helper that
nudges the wearer toward things they have missed on the blind side.

The current centrepiece is the **miss-time model**: a small formula that guesses how many seconds
the wearer will take to notice an object without help. A long guess means "nudge". A short guess
means "stay quiet".

## Where it is going (chosen 21 Nov 2026)

Read `Docs/DIRECTION.md` before planning any new work. The short version:

- **Goal:** warn the wearer about things coming at them from the blind side, only when they
  would not notice in time on their own.
- **Rule:** nudge when the model's guess ("seconds until they notice") is longer than the time
  left ("seconds until it reaches them"). Time left is distance divided by closing speed.
- **First version:** the wearer stands still and objects approach from the blind side.
- **Where it should end up:** passthrough on the Meta VR Glasses, with real objects found by
  computer vision. The Unity project is the simulator where we work it out first. Prefer choices
  that survive that move: inputs the glasses can also measure, and a field of view of 70 by 66
  degrees.

What exists today is the still-object version: a ball appears on the blind side and stays put,
and the smart nudge compares the model's guess with a fixed 8 s. **Moving objects and the
time-left rule are not built yet.** `Docs/DIRECTION.md` lists what has to change and what is
still undecided. Ask before picking any of the undecided things.

## How to explain things to us

The machine learning side is new to us, and long explanations have lost us before.

- One idea per message. Stop and check it landed before the next one.
- Plain words. Define a term the first time you use it, and keep using the same word after.
- Show it from inside the headset (what the wearer sees) before showing a graph of it.
- Say what a thing is for in this project before saying how it works.
- Short answers first. Offer the detail, do not lead with it.

## Who works on what

| Person | Background | Works in |
| --- | --- | --- |
| Russell | CS, Unity, C# | `Assets/Hemianopia_Project/Scripts/`, the headset, the scene |
| Tamanna | Neuroscience, Python | `Analysis/`, the literature, the study plan |

If you are helping Tamanna: stay in `Analysis/` unless she asks otherwise. Explain things in plain
words and define any term you introduce. She does not need Unity or a headset for her work.

## Hard rules

1. **Never commit eye recordings.** This repository is public and its history is permanent.
   `CalibrationLogs/` and `Replays/` are ignored by git. Keep it that way. Before any commit, run
   `git status` and confirm nothing from those folders is listed. Do not paste recording rows into
   commit messages, issues or docs either.
2. **Participants are codes, never names.** P01, P02 and so on.
3. **Do not hand-edit `Assets/Hemianopia_Project/Scripts/Room/MissModelWeights.cs`.**
   `Analysis/train_model.py` writes it. Running that script changes the model the headset uses,
   so commit a changed weights file only on purpose, and say so in the commit message.
4. **Do not change the C# scripts or the Unity scene from an analysis session.** If the analysis
   shows the headset code needs a change, write down what and why for Russell.
5. **Work on your own branch** and open a pull request. Do not push to `main` directly.
6. **Report results as pilot data from one person** until more people have been tested.

## Getting the recordings

They are not on GitHub. Russell shares them through a private synced folder. Place or link that
folder at `CalibrationLogs/` in the project's top folder. The scripts look there.

## Running the analysis

Needs Python 3 with `numpy`, `pandas` and `matplotlib`. Nothing else. The model is fitted with
plain numpy on purpose, so do not add scikit-learn without asking.

Run from the project's top folder:

```
python Analysis/build_table.py
python Analysis/train_model.py
```

`build_table.py` reads the recordings and writes two tables into `CalibrationLogs/analysis/`:

- `real_table.csv`: one row per real surprise, from `trial_*_roomblock_*.csv` (mask on, nudge off)
- `imaginary_table.csv`: pretend objects placed into `trial_*_freelook_*.csv`

`train_model.py` trains on the imaginary table, tests on the real one, prints the scores, and writes
`miss_model.json`, `real_table_with_guesses.csv`, `miss_model_check.png` and the Unity weights file.

`slow_nudges.py` (Tamanna) looks at surprises that took long to find even with the arrows
showing. It prints plain facts about each one and saves a two-panel picture per case into
`CalibrationLogs/analysis/slow_nudges/`. It only reads recordings. It changes no model or table.

The files in `CalibrationLogs/analysis/` are results to read. They are not inputs for
`analyze_recording.py` (in `Hemianopia_TAMANNA/`), which takes raw `trial_*.csv` recordings
from the moving-block tests.

A healthy run on the 14 Nov 2026 recordings prints about: typical error 1.41 s, same-order score
0.81, missed-against-found score 0.99. If your numbers match, your setup works.

## Words you need

| Word | Meaning |
| --- | --- |
| Mask | The blacked-out half of the view. It follows the eyes. |
| Blind side | The half behind the mask |
| Uncovered | The moment the eyes move far enough that an object comes out from behind the mask |
| Surprise | An object that appears on the blind side without warning |
| Busy job | The "look at the ringed shape" task that stops the wearer from hunting for surprises |
| Block | One session of 10 surprises (`roomblock` recordings) |
| Free look | Five minutes of the busy job with no surprises (`freelook` recordings) |
| Miss time | Seconds from an object appearing to the wearer looking at it. Capped at 20. |
| Looked at | Gaze within about 3 degrees of the object, held for 0.35 s |
| Imaginary object | A pretend object placed into a real recording after the fact |
| Input (or feature) | One number that describes the last 10 s of eye and head movement. The model has 8 |
| Weight | How much one input counts in the model's guess. Training picks the weights |
| Time left | Seconds until an approaching object reaches the wearer: distance divided by closing speed. Not built yet |
| Arrived | The approaching object has reached the wearer. Not built or defined yet |

## The 8 inputs to the model

All use only the last 10 seconds of eye and head movement. They are computed in
`Analysis/gaze_tools.py` (`spot_features`) and again in C# in `MissModel.cs`. The two must stay
identical. If you change one, tell Russell so the other can follow.

The 8 were chosen by hand as guesses at what might matter. Training then showed that two carry
almost all the weight: `beyond_scan_deg` and `since_uncovered_s`. In plain words, the model
learned one thing: if the eyes have not been going near a spot lately, an object there gets
missed. When explaining the model, start from that sentence, not from the table.

| Name | Meaning |
| --- | --- |
| `beyond_scan_deg` | How far the spot is beyond the furthest the eyes swept into the blind side lately |
| `since_uncovered_s` | Seconds since the spot was last uncovered |
| `uncovered_share` | Share of the last 10 s the spot was uncovered |
| `into_blind_deg` | How far into the blind side the spot is from the gaze now |
| `from_head_deg` | How far to the side of the head's direction the spot is |
| `up_down_deg` | How far above or below the gaze the spot is |
| `head_speed_dps` | How fast the head is turning |
| `eyes_toward_blind_deg` | How far the eyes are turned toward the blind side now |

## Where to read more

- `Docs/DIRECTION.md`: where the project is going, what has to change, what is undecided
- `Docs/ROOM.md`: how to run the test room, every key, what gets saved
- `Docs/DEVLOG.md`: dated notes on every result, finding and fix
- `Analysis/gaze_tools.py`: start here to understand the analysis. Read it before changing anything.
- `README.md`: project overview and Unity setup

## What to work on next

The build list is in `Docs/DIRECTION.md` under "What stays and what changes". In short:

1. Unity: make the surprise approach the wearer, and log when it arrives.
2. Unity: change the smart nudge to compare the model's guess with the time left.
3. Analysis: make `build_table.py` and `gaze_tools.py` handle an object that moves,
   for real and imaginary objects alike.
4. Retrain, then rerun the three-way test (no nudge, everything, smart) with approaching objects.

Settle the undecided things in `Docs/DIRECTION.md` first (speeds, distances, what "arrived"
means). They decide what gets built.

## Open questions

1. Why do some nudges not land? A few finds took 9 to 19 s with the arrows already showing.
   First read from `slow_nudges.py` on 21 Nov: in the slowest cases the ball had left the
   headset's view because the head had turned away. A hunch from four pictures, not a finding.
   See `Docs/DEVLOG.md`.
2. Does the model hold up when trained on some blocks and tested on others?
3. Is there any surprise where the model beats the one-line rule `beyond_scan_deg > 0`?
4. How do misses and nudge counts trade off as the 8 s nudge line moves? The time-left rule
   replaces the fixed 8 s, so this one matters less now.
5. Do the model's guesses hold up for an object that moves?
