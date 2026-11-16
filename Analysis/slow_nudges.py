"""
slow_nudges.py: why did some finds take so long even though the arrows were already showing?

Open question #1 in CLAUDE.md. This script looks only at surprises where a nudge appeared, picks out
the slow ones, and for each slow one:
  - prints plain facts about what the eyes did after the arrows appeared
  - saves a two-panel picture of the eyes against the object

It does NOT change any model, table or Unity file. It only reads recordings and writes pictures.

Run from the project's top folder:

    python Analysis/slow_nudges.py                 # slow = more than 4 s from nudge to first look
    python Analysis/slow_nudges.py --min-wait 2.5  # catch more cases
    python Analysis/slow_nudges.py --all           # also draw the quick ones, to compare

Needs numpy, pandas, matplotlib. Pictures go to CalibrationLogs/analysis/slow_nudges/ (never committed).

Words (same as CLAUDE.md):
  uncovered   the eyes have moved far enough that the object is out from behind the mask
  looked at   gaze within about 3 degrees of the object, held for a moment
  busy job    the "look at the shape with the cyan ring" task
"""
import argparse
import glob
import os
import sys

import numpy as np
import matplotlib

matplotlib.use("Agg")  # we only save pictures, so no window is needed
import matplotlib.pyplot as plt

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gaze_tools as G  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_FOLDER = os.path.join(HERE, "..", "CalibrationLogs")

LEAD_IN_S = 2.0     # how much of the time before the surprise to draw
LEAD_OUT_S = 1.5    # and after the first look
BEFORE_NUDGE_S = 5.0  # "before the arrows" means this many seconds before they appeared
FINAL_MOVE_S = 0.5    # the last moments before the first look are the successful look itself, so the
                      # "search" facts below leave them out


# ---------------------------------------------------------------- reading the recording

def shapes_from(ev):
    """The five shapes' positions, from events like 'shape:red cube@-1.9|1.2|-5.2'."""
    out = {}
    for _, e in ev:
        if e.startswith("shape:") and "@" in e:
            name, pos = e[len("shape:"):].split("@", 1)
            out[name] = np.array([float(v) for v in pos.split("|")])
    return out


def job_targets_from(ev):
    """(time, shape name) each time the busy job moved its cyan ring to a new shape."""
    return [(t, e[len("job_target:"):]) for t, e in ev if e.startswith("job_target:")]


def seconds_true(flags, t):
    """How many seconds a True/False-per-frame array was True, using the frame times."""
    dt = np.diff(t, prepend=t[0])
    return float((dt * np.asarray(flags)).sum())


def on_job_target(track, shapes, jobs, i0, i1):
    """For frames i0..i1: was the gaze resting on whichever shape the busy job was asking for?"""
    tt = track.t[i0:i1 + 1]
    on = np.zeros(len(tt), dtype=bool)
    for k, (tj, name) in enumerate(jobs):
        end = jobs[k + 1][0] if k + 1 < len(jobs) else np.inf
        if name not in shapes:
            continue
        idx = np.where((tt >= tj) & (tt < end))[0]
        if len(idx) == 0:
            continue
        v = G.view_of_point(track, shapes[name], i0 + idx[0], i0 + idx[-1])
        on[idx] = v["ecc"] <= G.LOOK_BEAM_DEG + v["radius"]
    return on


# ---------------------------------------------------------------- finding the cases

def find_cases(folder):
    """Every surprise (mask on) where a nudge appeared, with the facts worked out for it."""
    cases = []
    for path in sorted(glob.glob(os.path.join(folder, "trial_*_roomblock_*.csv"))):
        df = G.load(path)
        track = G.Track(df)
        ev = G.events(df)
        shapes = shapes_from(ev)
        jobs = job_targets_from(ev)
        appear = [t for t, e in ev if e == "appear:surprise"]
        ends = [(t, e) for t, e in ev if e in ("first_look:surprise", "timeout:surprise")]
        nudges = [t for t, e in ev if e == "nudge_on:surprise"]

        for number, (ta, (te, label)) in enumerate(zip(appear, ends), start=1):
            ia = track.index_at(ta)
            cond = str(df["condition"].iloc[ia])
            if "mask_on" not in cond:
                continue
            tn_list = [t for t in nudges if ta <= t <= te]
            if not tn_list:
                continue
            tn = tn_list[0]
            seg = df[(df.t_s >= ta) & (df.t_s <= te)][["tgt_x", "tgt_y", "tgt_z"]].dropna()
            if len(seg) == 0:
                continue
            point = seg.iloc[0].to_numpy(float)

            i_start = track.index_at(ta - LEAD_IN_S)
            i_end = track.index_at(te + LEAD_OUT_S)
            view = G.view_of_point(track, point, i_start, i_end)
            t = view["t"]
            after = (t >= tn) & (t <= te)            # from the arrows appearing to the first look
            search = (t >= tn) & (t < te - FINAL_MOVE_S)  # the same, minus the final successful move
            before = (t >= tn - BEFORE_NUDGE_S) & (t < tn)
            toward = track.blind_sign[i_start:i_end + 1] * track.gaze_az[i_start:i_end + 1]
            on_job = on_job_target(track, shapes, jobs, i_start, i_end)
            i_nudge = int(np.argmin(np.abs(t - tn)))

            wait = te - tn
            case = dict(
                file=os.path.basename(path), trial=number, rule="smart" if "smart" in cond else "everything",
                found=label.startswith("first_look"), t_appear=ta, t_nudge=tn, t_end=te,
                nudge_delay_s=tn - ta, wait_after_nudge_s=wait,
                # facts about the time between the arrows appearing and the first look
                hidden_s=seconds_true(after & ~view["uncovered"], t),
                uncovered_s=seconds_true(after & view["uncovered"], t),
                search_s=seconds_true(search, t),
                uncovered_search_s=seconds_true(search & view["uncovered"], t),
                out_of_view_s=seconds_true(
                    search & ((np.abs(view["head_az"]) > G.FOV_AZ_DEG) | (np.abs(view["head_el"]) > G.FOV_EL_DEG)), t),
                ball_deg=float(view["head_az"][i_nudge]),      # how far toward the blind side the ball sits
                reach_before_deg=float(toward[before].max()) if before.any() else np.nan,
                reach_search_deg=float(toward[search].max()) if search.any() else np.nan,
                best_gap_deg=float(view["into_blind"][search].min()) if search.any() else np.nan,
                closest_ecc_deg=float(view["ecc"][search].min()) if search.any() else np.nan,
                radius_deg=float(view["radius"][i_nudge]),
                job_share=float(on_job[search].mean()) if search.any() else np.nan,
                job_hits=sum(1 for t_, e in ev if e.startswith("job_hit:") and tn < t_ <= te),
                # what the picture needs
                view=view, toward=toward, on_job=on_job,
                job_switches=[tj for tj, _ in jobs if t[0] <= tj <= t[-1]],
            )
            cases.append(case)
    return cases


# ---------------------------------------------------------------- printing

def print_table(cases, min_wait):
    print(f"{len(cases)} surprises had a nudge. Time from nudge to first look:\n")
    print(f"  {'file':38s} {'#':>2s} {'rule':10s} {'nudge came':>10s} {'wait after':>10s}  slow?")
    for c in cases:
        slow = "SLOW" if c["wait_after_nudge_s"] >= min_wait else ""
        print(f"  {c['file']:38s} {c['trial']:2d} {c['rule']:10s} {c['nudge_delay_s']:9.2f}s "
              f"{c['wait_after_nudge_s']:9.2f}s  {slow}")
    print()


def print_case(c):
    print("-" * 78)
    print(f"{c['file']}  surprise #{c['trial']}  (nudge rule: {c['rule']})")
    print(f"  Ball appeared at {c['t_appear']:.1f} s. Arrows came {c['nudge_delay_s']:.2f} s later. "
          f"First look {c['wait_after_nudge_s']:.1f} s after the arrows.")
    print(f"  The ball sat {c['ball_deg']:.0f} deg toward the blind side of the head's straight-ahead.")
    print(f"  Facts below cover the search: from the arrows until {FINAL_MOVE_S:.1f} s before the first look "
          f"({c['search_s']:.1f} s), so the final successful move is left out.")
    if c["search_s"] < 0.3:
        print("  The search was too short to say anything. This was not a slow case.")
        return
    print(f"  Ball was visible (uncovered) for {c['uncovered_search_s']:.1f} s of the search, "
          f"hidden for {c['search_s'] - c['uncovered_search_s']:.1f} s.")
    print(f"  Ball was outside the headset's view altogether (more than about {G.FOV_AZ_DEG:.0f} deg to the side "
          f"of the head) for {c['out_of_view_s']:.1f} s of the search. Head turns can take it out of view.")
    print(f"  Furthest the eyes turned toward the blind side: {c['reach_before_deg']:.0f} deg in the "
          f"{BEFORE_NUDGE_S:.0f} s before the arrows, {c['reach_search_deg']:.0f} deg during the search.")
    short = c["best_gap_deg"] - c["radius_deg"]
    if short > 0:
        print(f"  Nearest the eyes got to freeing the ball: {short:.1f} deg short of uncovering it.")
    else:
        print(f"  The eyes did get far enough to uncover the ball at some point in the search.")
    print(f"  Closest the gaze came to the ball itself: {c['closest_ecc_deg']:.1f} deg.")
    print(f"  Gaze was resting on the busy-job shape for {100 * c['job_share']:.0f}% of the search "
          f"({c['job_hits']} job hits).")

    # A rough reading. These are prompts for you to check against the picture, not conclusions.
    hints = []
    if c["out_of_view_s"] > 0.4 * c["search_s"]:
        hints.append("For much of the search the ball was outside the headset's view because the head had turned "
                     "away from it. Eyes alone could not have uncovered it then.")
    if c["uncovered_search_s"] > 0.5:
        hints.append("The ball was visible for a while before the first look. "
                     "He may have seen it but not moved his eyes to it. Check the picture.")
    if c["job_share"] > 0.5:
        hints.append("The gaze spent most of the search on the busy-job shape. "
                     "The job may have held his eyes while the arrows were showing.")
    if short > 0 and c["reach_search_deg"] <= c["reach_before_deg"] + 2.0:
        hints.append("The eyes did not turn further toward the blind side than they already had. "
                     "The arrows did not seem to draw the eyes that way.")
    elif short > 0:
        hints.append("The eyes turned further toward the blind side than before but stopped short. "
                     "Or they went the wrong height.")
    for h in hints:
        print(f"  Rough hint: {h}")
    if not hints:
        print("  No rough hint fired. Read the picture.")


# ---------------------------------------------------------------- the picture

def draw_case(c, out_path):
    v = c["view"]
    t = v["t"] - c["t_appear"]  # zero = when the ball appeared
    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(11, 7), sharex=True,
                                   gridspec_kw={"height_ratios": [3, 2]})

    # Top: where the eyes point and where the ball is, both as "degrees toward the blind side".
    ax1.plot(t, c["toward"], color="#2b6cb0", lw=1.6, label="where the eyes point")
    ax1.plot(t, v["head_az"], color="#805ad5", lw=1.6, label="where the ball is")
    ax1.fill_between(t, -90, 90, where=v["uncovered"],
                     color="#68d391", alpha=0.22, label="ball uncovered (visible)")
    ax1.set_ylabel("degrees toward the blind side\n(in the head's frame)")
    ax1.set_ylim(min(-15, float(np.nanmin(c["toward"])) - 5), max(60, float(np.nanmax(v["head_az"])) + 30))
    ax1.axhline(G.FOV_AZ_DEG, color="gray", ls=":", lw=1)
    ax1.text(t[-1], G.FOV_AZ_DEG + 1, "edge of the headset's view", fontsize=7, color="gray", ha="right")
    ax1.legend(loc="upper left", fontsize=8, ncol=3)
    ax1.set_title(f"{c['file']}  surprise #{c['trial']}  ({c['rule']} rule): "
                  f"{c['wait_after_nudge_s']:.1f} s from arrows to first look")

    # Ticks where the busy job moved the cyan ring.
    for tj in c["job_switches"]:
        ax1.plot([tj - c["t_appear"]] * 2, [ax1.get_ylim()[0], ax1.get_ylim()[0] + 3], color="#00a3c4", lw=1.2)

    # Bottom: how far the gaze is from the ball, in any direction.
    ax2.plot(t, v["ecc"], color="#444444", lw=1.4)
    ax2.axhline(G.LOOK_BEAM_DEG, color="#38a169", ls="--", lw=1)
    ax2.text(t[0], G.LOOK_BEAM_DEG + 1, "3 deg: gaze counts as on the ball", fontsize=7, color="#38a169")
    ax2.fill_between(t, 0, 100, where=c["on_job"], color="#00a3c4", alpha=0.12,
                     label="gaze on the busy-job shape")
    ax2.set_ylim(0, min(100, float(np.nanmax(v["ecc"])) + 5))
    ax2.set_ylabel("angle between gaze\nand the ball (deg)")
    ax2.set_xlabel("seconds since the ball appeared")
    ax2.legend(loc="upper right", fontsize=8)

    for ax in (ax1, ax2):
        ax.axvline(0, color="gray", lw=1.2)
        ax.axvline(c["t_nudge"] - c["t_appear"], color="#dd8800", lw=1.8)
        ax.axvline(c["t_end"] - c["t_appear"], color="#2f855a", lw=1.8)
        ax.grid(alpha=0.2)
    ytop = ax1.get_ylim()[1]
    ax1.text(0, ytop - 9, " ball appears", color="gray", fontsize=8, va="top")
    ax1.text(c["t_nudge"] - c["t_appear"], ytop - 15, " arrows appear", color="#dd8800", fontsize=8, va="top")
    ax1.text(c["t_end"] - c["t_appear"], ytop - 9, " first look", color="#2f855a", fontsize=8, va="top")
    fig.text(0.5, 0.005, "The ball is hidden when it sits further toward the blind side than the blue line "
             "(by more than its own size). The green band is when it is out from behind the mask.",
             ha="center", fontsize=7, color="gray")

    fig.tight_layout(rect=(0, 0.02, 1, 1))
    fig.savefig(out_path, dpi=130)
    plt.close(fig)


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="Look at surprises that took long to find even with the arrows showing.")
    ap.add_argument("--folder", default=DEFAULT_FOLDER, help="the CalibrationLogs folder")
    ap.add_argument("--min-wait", type=float, default=4.0,
                    help="seconds from arrows to first look that count as slow (default 4)")
    ap.add_argument("--all", action="store_true", help="draw every nudged surprise, not only the slow ones")
    args = ap.parse_args()

    folder = os.path.abspath(args.folder)
    cases = find_cases(folder)
    if not cases:
        print(f"No surprises with a nudge found in {folder}. Is the recordings folder in place?")
        return
    print_table(cases, args.min_wait)

    picked = [c for c in cases if args.all or c["wait_after_nudge_s"] >= args.min_wait]
    out_dir = os.path.join(folder, "analysis", "slow_nudges")
    os.makedirs(out_dir, exist_ok=True)
    for c in picked:
        print_case(c)
        name = f"{os.path.splitext(c['file'])[0]}_surprise{c['trial']}.png"
        draw_case(c, os.path.join(out_dir, name))
    print("-" * 78)
    print(f"Drew {len(picked)} picture(s) in {out_dir}")
    print("Pilot data from one person (P01). Do not quote these as general findings.")


if __name__ == "__main__":
    main()
