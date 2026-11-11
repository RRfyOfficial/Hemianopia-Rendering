#!/usr/bin/env python3
"""
analyze_recording.py: turn TrialLogger recordings into numbers and a figure.

Usage (from the project folder, with Python 3, numpy, pandas and matplotlib installed):

    python analyze_recording.py CalibrationLogs/trial_P01_square_20261109_101500.csv
    python analyze_recording.py "CalibrationLogs/trial_P01_*.csv" --outdir results

What it does for each recording:
  1. Data quality: frame rate, how often the tracker gave a fresh reading, blinks/dropouts.
  2. Reads the condition from the log and runs the matching analysis:
       Square_* / XPath_*     smooth-pursuit LAG and tracking error
       SaccadeSteps_*         landing accuracy at each point and time-to-land
       MidlineCrossing_*      fixation stability and the moments the block crosses the midline
  3. Saves a figure (<recording>_analysis.png) and appends a row to analysis_summary.csv.

What the numbers mean (and don't):
  * Angles are degrees from straight ahead in the head frame (azimuth = left/right, elevation = up/down).
    Gaze is the calibration-corrected gaze (cor_az, cor_el). The target's angles are rebuilt from its
    logged world position and the logged head pose, so they should match the commanded path.
  * Pursuit lag = how far gaze trails the target when the eyes are following it. It is measured at frame
    times, so it includes up to one display frame (about 14 ms at 72 Hz) of rounding, about 7 ms on average.
    Part of it is the person's own pursuit delay, which is small for a predictable path but not zero.
    Average over several runs and people, and treat the result as an estimate.
  * Saccade time-to-land mixes in the person's reaction time (150 to 250 ms), so it says little about the
    tracker. Use the landing accuracy from those runs, and use pursuit for lag.
  * Whether the mask hid the block, as the PERSON experienced it, is not logged yet. tgt_visible is where the
    filter SHOULD hide it. Compare it with what the participant reports.
"""
import argparse
import glob
import os
import sys

import numpy as np
import pandas as pd
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

REQUIRED = [
    "t_s", "dt_ms", "new_sample", "since_new_ms", "raw_valid", "is_eyes",
    "head_x", "head_y", "head_z", "head_qx", "head_qy", "head_qz", "head_qw",
    "cor_az", "cor_el", "trial", "condition", "tgt_x", "tgt_y", "tgt_z", "tgt_visible", "event",
]

DROPOUT_MS = 100.0   # no fresh eye reading for longer than this counts as a dropout
MOVING_DPS = 3.0     # the target counts as "moving" (pursuit) above this speed


# ---------------------------------------------------------------- loading

def read_header_lines(path):
    lines = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            if line.startswith("#"):
                lines.append(line[1:].strip())
            else:
                break
    return lines


def load(path):
    df = pd.read_csv(path, comment="#")
    missing = [c for c in REQUIRED if c not in df.columns]
    if missing:
        raise ValueError(f"Missing columns {missing}. Was this written by TrialLogger?")
    df["event"] = df["event"].fillna("").astype(str)
    df["condition"] = df["condition"].fillna("").astype(str)
    return df


def rotate_by_inverse(q, v):
    """Rotate vectors v (N,3) by the inverse of unit quaternions q (N,4: x,y,z,w)."""
    u = -q[:, :3]
    w = q[:, 3:4]
    t = 2.0 * np.cross(u, v)
    return v + w * t + np.cross(u, t)


def add_target_angles(df):
    """Target azimuth/elevation in the head frame, from its world position and the head pose."""
    has = df[["tgt_x", "tgt_y", "tgt_z"]].notna().all(axis=1).to_numpy()
    az = np.full(len(df), np.nan)
    el = np.full(len(df), np.nan)
    if has.any():
        sub = df[has]
        q = sub[["head_qx", "head_qy", "head_qz", "head_qw"]].to_numpy(float)
        q = q / np.linalg.norm(q, axis=1, keepdims=True)
        rel = sub[["tgt_x", "tgt_y", "tgt_z"]].to_numpy(float) - sub[["head_x", "head_y", "head_z"]].to_numpy(float)
        v = rotate_by_inverse(q, rel)
        az[has] = np.degrees(np.arctan2(v[:, 0], v[:, 2]))
        el[has] = np.degrees(np.arctan2(v[:, 1], np.hypot(v[:, 0], v[:, 2])))
    df["tgt_az"] = az
    df["tgt_el"] = el
    return df


def trim_to_run(df):
    """Keep the rows from run_start to run_end (or run_aborted) if those events exist."""
    ev = df["event"]
    start = df.index[ev.str.contains("run_start")]
    end = df.index[ev.str.contains("run_end|run_aborted")]
    i0 = start[0] if len(start) else df.index[0]
    i1 = end[-1] if len(end) else df.index[-1]
    return df.loc[i0:i1].copy()


# ---------------------------------------------------------------- data quality

def data_quality(df):
    out = {}
    dur = float(df["t_s"].iloc[-1] - df["t_s"].iloc[0])
    out["duration_s"] = dur
    out["frames"] = len(df)
    out["frame_rate_hz"] = 1000.0 / float(df["dt_ms"].iloc[1:].median()) if len(df) > 2 else np.nan
    eyes = bool(df["is_eyes"].iloc[len(df) // 2] == 1)
    out["source"] = "eyes" if eyes else "mouse"
    out["fresh_sample_fraction"] = float(df["new_sample"].mean())
    rv = df["raw_valid"]
    out["invalid_frame_fraction"] = float((rv == 0).mean()) if (rv >= 0).any() else np.nan
    s = df["since_new_ms"]
    out["since_new_median_ms"] = float(s.median())
    out["since_new_p95_ms"] = float(s.quantile(0.95))
    out["since_new_max_ms"] = float(s.max())
    # Dropout episodes: stretches where the gap stays above the threshold.
    over = (s > DROPOUT_MS).to_numpy()
    episodes = 0
    longest = 0.0
    run_start = None
    t = df["t_s"].to_numpy()
    for i, flag in enumerate(over):
        if flag and run_start is None:
            run_start = i
        if (not flag or i == len(over) - 1) and run_start is not None:
            end = i if not flag else i
            episodes += 1
            longest = max(longest, float(t[end] - t[run_start]))
            run_start = None
    out["dropout_episodes"] = episodes
    out["longest_dropout_s"] = longest
    return out


def fresh_samples(df):
    """Rows where the tracker gave a fresh, valid reading. Returns time, gaze (N,2), target (N,2)."""
    m = (df["new_sample"] == 1) & (df["raw_valid"] != 0)
    s = df[m]
    return (
        s["t_s"].to_numpy(float),
        s[["cor_az", "cor_el"]].to_numpy(float),
        s[["tgt_az", "tgt_el"]].to_numpy(float),
        s,
    )


# ---------------------------------------------------------------- pursuit

def pursuit_analysis(df, lags_ms=None):
    if lags_ms is None:
        lags_ms = np.arange(-100, 401, 2)
    t, gaze, tgt, _ = fresh_samples(df)
    ok = np.isfinite(tgt).all(axis=1) & np.isfinite(gaze).all(axis=1)
    t, gaze, tgt = t[ok], gaze[ok], tgt[ok]
    if len(t) < 50:
        return {"note": "Too few valid samples for a lag estimate."}, None

    # Uniform 5 ms grid. Target comes from every frame (it is smooth); gaze from fresh readings only.
    allt = df["t_s"].to_numpy(float)
    grid = np.arange(allt[0], allt[-1], 0.005)
    tg = np.column_stack([
        np.interp(grid, allt, df["tgt_az"].to_numpy(float)),
        np.interp(grid, allt, df["tgt_el"].to_numpy(float)),
    ])
    # Target speed (deg/s) over about 100 ms, to keep only the stretches where it is gliding.
    k = 20
    spd = np.zeros(len(grid))
    d = np.hypot(tg[k:, 0] - tg[:-k, 0], tg[k:, 1] - tg[:-k, 1]) / (k * 0.005)
    spd[k // 2: k // 2 + len(d)] = d
    moving = spd > MOVING_DPS

    best = None
    rms_by_lag = []
    for lag in lags_ms:
        shifted = grid + lag / 1000.0
        g = np.column_stack([
            np.interp(shifted, t, gaze[:, 0], left=np.nan, right=np.nan),
            np.interp(shifted, t, gaze[:, 1], left=np.nan, right=np.nan),
        ])
        err = np.sum((tg - g) ** 2, axis=1)
        use = moving & np.isfinite(err)
        if use.sum() < 100:
            rms_by_lag.append(np.nan)
            continue
        rms = float(np.sqrt(np.mean(err[use])))
        rms_by_lag.append(rms)
        if best is None or rms < best[1]:
            best = (lag, rms)
    rms_by_lag = np.array(rms_by_lag)
    if best is None:
        return {"note": "The target never moved fast enough for a pursuit estimate."}, None

    zero_idx = int(np.argmin(np.abs(lags_ms)))
    res = {
        "pursuit_lag_ms": float(best[0]),
        "rms_error_at_best_lag_deg": best[1],
        "rms_error_at_zero_lag_deg": float(rms_by_lag[zero_idx]),
        "moving_time_s": float(moving.sum() * 0.005),
    }
    extra = {"lags_ms": lags_ms, "rms_by_lag": rms_by_lag}
    return res, extra


# ---------------------------------------------------------------- saccade steps

def saccade_analysis(df):
    t, gaze, tgt, s = fresh_samples(df)
    ev_rows = df[df["event"].str.contains("jump")]
    if len(ev_rows) < 2:
        return {"note": "No jump events found."}
    starts = ev_rows["t_s"].to_numpy(float)
    ends = np.append(starts[1:], df["t_s"].iloc[-1])
    prev_t = df["tgt_az"].to_numpy(float), df["tgt_el"].to_numpy(float)
    tt = df["t_s"].to_numpy(float)

    landing_err, times_to_land, biases = [], [], []
    for a, b in zip(starts, ends):
        sel = (t >= a) & (t < b)
        if sel.sum() < 5:
            continue
        gz, tg = gaze[sel], tgt[sel]
        target = np.nanmedian(tg, axis=0)
        dist = np.hypot(gz[:, 0] - target[0], gz[:, 1] - target[1])
        # time-to-land: first fresh sample within 2 degrees of the target
        near = np.where(dist < 2.0)[0]
        if len(near):
            times_to_land.append(float(t[sel][near[0]] - a))
        # landing accuracy: the settled gaze, taken from the last half of the dwell
        late = t[sel] >= (a + 0.5 * (b - a))
        if late.sum() >= 3:
            settled = gz[late]
            landing_err.append(float(np.hypot(*(np.median(settled, axis=0) - target))))
            biases.append(np.median(settled, axis=0) - target)
    if not landing_err:
        return {"note": "No usable dwell periods."}
    biases = np.array(biases)
    return {
        "jumps_analyzed": len(landing_err),
        "landing_error_median_deg": float(np.median(landing_err)),
        "landing_error_p90_deg": float(np.percentile(landing_err, 90)),
        "mean_bias_az_deg": float(biases[:, 0].mean()),
        "mean_bias_el_deg": float(biases[:, 1].mean()),
        "time_to_land_median_ms": float(np.median(times_to_land) * 1000) if times_to_land else np.nan,
    }


# ---------------------------------------------------------------- midline crossing

def midline_analysis(df):
    t, gaze, tgt, s = fresh_samples(df)
    first_cross = df[df["event"].str.contains("cross_")]
    res = {}
    if len(first_cross):
        hold_end = float(first_cross["t_s"].iloc[0])
        hold = (t >= t[0] + 1.0) & (t < hold_end)  # skip the first second while the eyes settle
        if hold.sum() >= 10:
            g = gaze[hold]
            res["fixation_mean_az_deg"] = float(g[:, 0].mean())
            res["fixation_mean_el_deg"] = float(g[:, 1].mean())
            res["fixation_sd_az_deg"] = float(g[:, 0].std())
            res["fixation_sd_el_deg"] = float(g[:, 1].std())
    crosses = df[df["event"].str.contains("midline_cross")]
    res["midline_crossings"] = int(len(crosses))
    vis = df["tgt_visible"]
    res["fraction_time_flagged_hidden"] = float((vis == 0).mean()) if vis.notna().any() else np.nan
    return res


# ---------------------------------------------------------------- figure

def make_figure(df, path, title, lag_ms=None, extra=None, kind="generic"):
    fig, axes = plt.subplots(2, 2, figsize=(13, 8))
    t0 = df["t_s"].iloc[0]
    t = df["t_s"].to_numpy(float) - t0
    ax = axes[0, 0]
    ax.plot(t, df["tgt_az"], label="target", lw=1.5)
    ax.plot(t, df["cor_az"], label="gaze", lw=0.9, alpha=0.8)
    if lag_ms is not None:
        ax.plot(t - lag_ms / 1000.0, df["cor_az"], label=f"gaze shifted back {lag_ms:.0f} ms", lw=0.9, ls="--")
    if kind == "midline" and df["tgt_visible"].notna().any():
        hidden = (df["tgt_visible"] == 0).to_numpy()
        ax.fill_between(t, -20, 20, where=hidden, color="k", alpha=0.08, step="mid", label="filter should hide block")
    ax.set_title("Azimuth (left/right)")
    ax.set_xlabel("time (s)")
    ax.set_ylabel("degrees")
    ax.legend(fontsize=8)

    ax = axes[0, 1]
    ax.plot(t, df["tgt_el"], label="target", lw=1.5)
    ax.plot(t, df["cor_el"], label="gaze", lw=0.9, alpha=0.8)
    if lag_ms is not None:
        ax.plot(t - lag_ms / 1000.0, df["cor_el"], label=f"gaze shifted back {lag_ms:.0f} ms", lw=0.9, ls="--")
    ax.set_title("Elevation (up/down)")
    ax.set_xlabel("time (s)")
    ax.set_ylabel("degrees")
    ax.legend(fontsize=8)

    ax = axes[1, 0]
    ax.plot(df["tgt_az"], df["tgt_el"], lw=1.5, label="target path")
    ax.plot(df["cor_az"], df["cor_el"], lw=0.6, alpha=0.7, label="gaze path")
    ax.set_title("Path in the visual field")
    ax.set_xlabel("azimuth (deg)")
    ax.set_ylabel("elevation (deg)")
    ax.set_aspect("equal", adjustable="datalim")
    ax.legend(fontsize=8)

    ax = axes[1, 1]
    if extra is not None:
        ax.plot(extra["lags_ms"], extra["rms_by_lag"])
        ax.axvline(lag_ms, color="r", ls="--", lw=1)
        ax.set_title("Tracking error vs. time shift (minimum = lag)")
        ax.set_xlabel("gaze shifted back by (ms)")
        ax.set_ylabel("RMS error (deg)")
    else:
        ax.semilogy(t, df["since_new_ms"].clip(lower=1), lw=0.8)
        bad = df["raw_valid"] == 0
        ax.scatter(t[bad.to_numpy()], df["since_new_ms"].clip(lower=1)[bad], s=6, color="r", label="invalid frame")
        ax.axhline(DROPOUT_MS, color="gray", ls=":", lw=1)
        ax.set_title("Time since last fresh eye reading")
        ax.set_xlabel("time (s)")
        ax.set_ylabel("ms (log)")
        if bad.any():
            ax.legend(fontsize=8)

    fig.suptitle(title)
    fig.tight_layout()
    fig.savefig(path, dpi=130)
    plt.close(fig)


# ---------------------------------------------------------------- main

def analyze(path, outdir):
    header = read_header_lines(path)
    df_all = load(path)
    df_all = add_target_angles(df_all)
    df = trim_to_run(df_all)
    base = os.path.splitext(os.path.basename(path))[0]
    cond = df["condition"].replace("", np.nan).dropna()
    condition = cond.iloc[0] if len(cond) else "(none)"
    participant = ""
    for h in header:
        if h.startswith("participant="):
            participant = h.split(",")[0].split("=", 1)[1]

    print("=" * 72)
    print(f"{os.path.basename(path)}")
    print(f"  participant {participant or '?'}   condition {condition}")
    for h in header[:3]:
        print(f"  # {h}")

    summary = {"file": os.path.basename(path), "participant": participant, "condition": condition}
    q = data_quality(df)
    summary.update(q)
    print("\nData quality")
    print(f"  {q['frames']} frames over {q['duration_s']:.1f} s ({q['frame_rate_hz']:.0f} Hz), source: {q['source']}")
    print(f"  fresh eye reading on {q['fresh_sample_fraction']*100:.0f}% of frames")
    if not np.isnan(q["invalid_frame_fraction"]):
        print(f"  invalid frames: {q['invalid_frame_fraction']*100:.1f}%")
    print(f"  gap since last fresh reading: median {q['since_new_median_ms']:.0f} ms, "
          f"95th pct {q['since_new_p95_ms']:.0f} ms, max {q['since_new_max_ms']:.0f} ms")
    print(f"  dropouts over {DROPOUT_MS:.0f} ms: {q['dropout_episodes']} (longest {q['longest_dropout_s']:.2f} s)")
    if q["source"] == "mouse":
        print("  NOTE: this recording used the mouse, not eyes, so tracker numbers are not meaningful.")

    kind = "generic"
    lag = None
    extra = None
    lc = condition.lower()
    if df["tgt_az"].notna().sum() < 10:
        print("\n(No target in this recording, so only data quality is reported.)")
    elif lc.startswith("square") or lc.startswith("xpath"):
        kind = "pursuit"
        res, extra = pursuit_analysis(df)
        summary.update(res)
        print("\nSmooth pursuit")
        if "note" in res:
            print("  " + res["note"])
        else:
            lag = res["pursuit_lag_ms"]
            print(f"  gaze trails the target by about {lag:.0f} ms (rounded to the 2 ms search step)")
            print(f"  RMS tracking error: {res['rms_error_at_best_lag_deg']:.2f} deg after removing the lag, "
                  f"{res['rms_error_at_zero_lag_deg']:.2f} deg with no shift")
            print(f"  based on {res['moving_time_s']:.1f} s of target motion above {MOVING_DPS:.0f} deg/s")
            print("  (includes up to one display frame of rounding and some human pursuit delay; average several runs)")
    elif lc.startswith("saccadesteps"):
        kind = "saccade"
        res = saccade_analysis(df)
        summary.update(res)
        print("\nSaccade steps")
        if "note" in res:
            print("  " + res["note"])
        else:
            print(f"  {res['jumps_analyzed']} jumps analyzed")
            print(f"  landing error (settled gaze vs. target): median {res['landing_error_median_deg']:.2f} deg, "
                  f"90th pct {res['landing_error_p90_deg']:.2f} deg")
            print(f"  average offset: {res['mean_bias_az_deg']:+.2f} deg azimuth, {res['mean_bias_el_deg']:+.2f} deg elevation")
            print(f"  time to land within 2 deg: median {res['time_to_land_median_ms']:.0f} ms "
                  f"(includes the person's reaction time; not a tracker lag)")
    elif lc.startswith("midlinecrossing"):
        kind = "midline"
        res = midline_analysis(df)
        summary.update(res)
        print("\nMidline crossing")
        if "fixation_sd_az_deg" in res:
            print(f"  fixation on the centre dot: mean offset ({res['fixation_mean_az_deg']:+.2f}, "
                  f"{res['fixation_mean_el_deg']:+.2f}) deg, spread SD ({res['fixation_sd_az_deg']:.2f}, "
                  f"{res['fixation_sd_el_deg']:.2f}) deg")
            print("  (the filter's sparing and edge softness need to be wider than this offset plus spread to avoid wobble)")
        print(f"  midline crossings logged: {res['midline_crossings']}")
        if not np.isnan(res["fraction_time_flagged_hidden"]):
            print(f"  block flagged as hidden for {res['fraction_time_flagged_hidden']*100:.0f}% of the run")
        print("  Compare with what the participant reported: whether the block really vanished is not logged yet.")
    else:
        print("\n(Condition not recognised, so only data quality and the figure are produced.)")

    os.makedirs(outdir, exist_ok=True)
    fig_path = os.path.join(outdir, base + "_analysis.png")
    make_figure(df, fig_path, f"{base}   [{condition}]", lag_ms=lag, extra=extra, kind=kind)
    print(f"\n  figure saved: {fig_path}")
    return summary


def main():
    ap = argparse.ArgumentParser(description="Analyze TrialLogger recordings.")
    ap.add_argument("files", nargs="+", help="CSV files or glob patterns")
    ap.add_argument("--outdir", default=None, help="where to put figures and the summary (default: next to the first file)")
    args = ap.parse_args()

    paths = []
    for pattern in args.files:
        hits = sorted(glob.glob(pattern))
        paths.extend(hits if hits else [pattern])
    paths = [p for p in paths if os.path.isfile(p)]
    if not paths:
        print("No files found.")
        sys.exit(1)

    outdir = args.outdir or os.path.dirname(os.path.abspath(paths[0]))
    summaries = []
    for p in paths:
        try:
            summaries.append(analyze(p, outdir))
        except Exception as e:
            print(f"\n{os.path.basename(p)}: could not analyze ({e})")

    if summaries:
        out = pd.DataFrame(summaries)
        csv_path = os.path.join(outdir, "analysis_summary.csv")
        if os.path.exists(csv_path):
            out = pd.concat([pd.read_csv(csv_path), out], ignore_index=True)
            out = out.drop_duplicates(subset=["file"], keep="last")
        out.to_csv(csv_path, index=False)
        print("=" * 72)
        print(f"Summary table: {csv_path}")


if __name__ == "__main__":
    main()
