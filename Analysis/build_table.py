#!/usr/bin/env python3
"""
build_table.py: turn room-test recordings into two tables for training the "miss time" model.

    python Analysis/build_table.py                 (run from the project folder)
    python Analysis/build_table.py --samples 5000

It writes into CalibrationLogs/analysis/ (never committed):

  real_table.csv        one row per real surprise from the block recordings (trial_*_roomblock_*.csv),
                        mask on, nudge off. The answer column is how long it really went unnoticed.

  imaginary_table.csv   thousands of rows made from the free-looking recordings (trial_*_freelook_*.csv).
                        Each row is an IMAGINARY object dropped at a random moment and place, exactly the way
                        the real surprise is placed. The answer is when the recorded eyes would have uncovered
                        it, plus the person's usual reaction time.

Why imaginary objects are fair: while an object is hidden on the blind side it cannot change where the eyes
go, so the eyes would have moved the same way whether it existed or not. The real surprises back this up:
the script checks that the same rule explains the real runs before it uses it (it prints that check).

Each row has the same facts, all known at the moment the object appears (see gaze_tools.spot_features):
  into_blind_deg         how far into the blind side of the gaze it is (left/right)
  up_down_deg            how far above or below the gaze it is
  from_head_deg          how far toward the blind side of straight-ahead-of-the-face it is
  since_uncovered_s      how long since the person's looking around last uncovered that spot (max 10)
  uncovered_share        what share of the last 10 s that spot was uncovered
  beyond_scan_deg        how far it sits beyond the furthest the gaze reached in the last 10 s (+ = out of reach)
  head_speed_dps         how fast the head is turning
  eyes_toward_blind_deg  how far the eyes are turned toward the blind side inside the head
Answer columns: miss_s (seconds unnoticed, capped at 20) and found (1 or 0).
"""
import argparse
import glob
import os

import numpy as np
import pandas as pd

import gaze_tools as G

NOTICE_UNCOVERED_S = 0.10   # the spot must stay uncovered this long to be noticed
NOTICE_MAX_ECC_DEG = 60.0   # and be within this many degrees of the gaze

# How RoomTest places a surprise (keep in step with RoomTest.cs).
OFFSET_RANGE = (12.0, 40.0)
ELEVATION_RANGE = 15.0
MAX_HEAD_AZ = 35.0
MIN_OFFSET = 12.0
MIN_REST_S = 0.25


def real_rows(folder):
    rows = []
    for path in sorted(glob.glob(os.path.join(folder, "trial_*_roomblock_*.csv"))):
        df = G.load(path)
        track = G.Track(df)
        ev = G.events(df)
        appear = [t for t, e in ev if e == "appear:surprise"]
        ends = [(t, e) for t, e in ev if e in ("first_look:surprise", "timeout:surprise")]
        for ta, (te, label) in zip(appear, ends):
            ia = track.index_at(ta)
            cond = str(df["condition"].iloc[ia])
            if "mask_on" not in cond or "nudge" in cond:
                continue  # only mask on, nudge off: how long things go unnoticed with no help
            seg = df[(df.t_s >= ta) & (df.t_s <= te)][["tgt_x", "tgt_y", "tgt_z"]].dropna()
            if len(seg) == 0:
                continue
            point = seg.iloc[0].to_numpy(float)
            found = label.startswith("first_look")
            row = G.spot_features(track, point, ia)
            ie = track.index_at(ta + G.TIMEOUT_S + 0.2)
            view = G.view_of_point(track, point, ia, ie)
            tn = G.noticed_time(view, NOTICE_UNCOVERED_S, NOTICE_MAX_ECC_DEG)
            row.update(source="real", file=os.path.basename(path), t0=ta,
                       miss_s=min(te - ta, G.TIMEOUT_S), found=int(found),
                       uncovered_at_s=(tn - ta) if tn is not None else np.nan)
            rows.append(row)
    return pd.DataFrame(rows)


def imaginary_rows(folder, samples, reaction_s, seed):
    rng = np.random.default_rng(seed)
    rows = []
    paths = sorted(glob.glob(os.path.join(folder, "trial_*_freelook_*.csv")))
    for path in paths:
        df = G.load(path)
        track = G.Track(df)
        t = track.t
        # Moments the real spawner would accept: mask on, eyes resting, enough recording before and after.
        ok = (track.mask_on == 1) & (track.is_fixating == 1) & (track.fix_dur >= MIN_REST_S) \
             & (t >= t[0] + 10.0) & (t <= t[-1] - (G.TIMEOUT_S + 0.5))
        candidates = np.where(ok)[0]
        if len(candidates) == 0:
            continue
        made = 0
        tries = 0
        per_file = samples // max(1, len(paths))
        while made < per_file and tries < per_file * 20:
            tries += 1
            i0 = int(rng.choice(candidates))
            sign = track.blind_sign[i0]
            gaze_az = track.gaze_az[i0]
            az = float(np.clip(gaze_az + sign * rng.uniform(*OFFSET_RANGE), -MAX_HEAD_AZ, MAX_HEAD_AZ))
            if sign * (az - gaze_az) < MIN_OFFSET:
                continue  # the real spawner would wait for a better moment
            el = float(rng.uniform(-ELEVATION_RANGE, ELEVATION_RANGE))
            direction_head = G.from_angles(az, el)[None, :]
            direction_world = G.rotate(track.head_q[i0:i0 + 1], direction_head)[0]
            point = track.head_pos[i0] + direction_world * G.OBJECT_DISTANCE_M

            ie = track.index_at(t[i0] + G.TIMEOUT_S)
            view = G.view_of_point(track, point, i0, ie)
            tn = G.noticed_time(view, NOTICE_UNCOVERED_S, NOTICE_MAX_ECC_DEG)
            miss = (tn - t[i0]) + reaction_s if tn is not None else G.TIMEOUT_S
            found = tn is not None and miss < G.TIMEOUT_S

            row = G.spot_features(track, point, i0)
            row.update(source="imaginary", file=os.path.basename(path), t0=float(t[i0]),
                       miss_s=float(min(miss, G.TIMEOUT_S)), found=int(found),
                       uncovered_at_s=(tn - t[i0]) if tn is not None else np.nan)
            rows.append(row)
            made += 1
    return pd.DataFrame(rows)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser()
    ap.add_argument("--folder", default=os.path.join(here, "..", "CalibrationLogs"))
    ap.add_argument("--samples", type=int, default=3000, help="how many imaginary objects to place")
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()

    out = os.path.join(args.folder, "analysis")
    os.makedirs(out, exist_ok=True)

    real = real_rows(args.folder)
    if len(real) == 0:
        print("No block recordings with the mask on and the nudge off were found.")
        return
    found = real[real.found == 1]
    missed = real[real.found == 0]

    # Check the rule on the real runs: "you notice it shortly after your own looking around uncovers it".
    reaction = (found.miss_s - found.uncovered_at_s)
    reaction_s = float(reaction.median())
    never_uncovered = int(missed.uncovered_at_s.isna().sum())
    print(f"Real surprises: {len(real)}  (found {len(found)}, never found in 20 s: {len(missed)})")
    print(f"  found ones: unnoticed for a typical {found.miss_s.median():.2f} s")
    print("  Check of the rule on the real runs:")
    print(f"    - of the {len(missed)} never found, {never_uncovered} were never uncovered by the eyes")
    print(f"    - of the {len(found)} found, {int(found.uncovered_at_s.notna().sum())} were uncovered first, "
          f"then looked at a typical {reaction_s:.2f} s later (middle half: "
          f"{reaction.quantile(.25):.2f} to {reaction.quantile(.75):.2f} s)")

    imag = imaginary_rows(args.folder, args.samples, reaction_s, args.seed)
    real.to_csv(os.path.join(out, "real_table.csv"), index=False)
    print(f"  wrote {os.path.join(out, 'real_table.csv')}")
    if len(imag) == 0:
        print("No free-looking recording found, so no imaginary objects were made.")
        return
    imag.to_csv(os.path.join(out, "imaginary_table.csv"), index=False)
    fi = imag[imag.found == 1]
    print(f"Imaginary objects: {len(imag)}  (found {len(fi)}, never found: {len(imag) - len(fi)})")
    print("  Do the imaginary runs look like the real ones?")
    print(f"    never found:        real {100 * len(missed) / len(real):.0f}%   imaginary {100 * (1 - len(fi) / len(imag)):.0f}%")
    print(f"    typical time found: real {found.miss_s.median():.2f} s   imaginary {fi.miss_s.median():.2f} s")
    print(f"  wrote {os.path.join(out, 'imaginary_table.csv')}")


if __name__ == "__main__":
    main()
