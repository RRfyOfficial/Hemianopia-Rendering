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

## The 8 inputs to the model

All use only the last 10 seconds of eye and head movement. They are computed in
`Analysis/gaze_tools.py` (`spot_features`) and again in C# in `MissModel.cs`. The two must stay
identical. If you change one, tell Russell so the other can follow.

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

- `Docs/ROOM.md`: how to run the test room, every key, what gets saved
- `Docs/DEVLOG.md`: dated notes on every result, finding and fix
- `Analysis/gaze_tools.py`: start here to understand the analysis. Read it before changing anything.
- `README.md`: project overview and Unity setup

## Open questions (good places to start)

1. Why do some nudges not land? A few finds took 9 to 19 s with the arrows already showing.
2. Does the model hold up when trained on some blocks and tested on others?
3. Is there any surprise where the model beats the one-line rule `beyond_scan_deg > 0`?
4. How do misses and nudge counts trade off as the 8 s nudge line moves?
