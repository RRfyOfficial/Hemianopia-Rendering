"""
gaze_tools.py: shared helpers for reading TrialLogger recordings and asking
"when would this person have noticed an object at this spot?"

Used by build_table.py and train_model.py. Needs numpy and pandas.

Conventions (same as the Unity code):
  - Angles are in degrees. Azimuth = left/right, elevation = up/down, measured in the HEAD's frame
    (0 = straight ahead of the face, positive azimuth = to the right).
  - "Blind side" comes from the recording (blind_side column: Left or Right).
  - An object is HIDDEN when the mask is on and the whole object sits on the blind side of the gaze.
    Otherwise it is UNCOVERED (at least part of it is on the seeing side).
"""
import numpy as np
import pandas as pd

OBJECT_RADIUS_M = 0.10      # the test objects are 0.2 m across
OBJECT_DISTANCE_M = 2.0     # and are placed 2 m from the head
LOOK_BEAM_DEG = 3.0         # LookTracker.coneDegrees
LOOK_DWELL_S = 0.10         # LookTracker.dwellSeconds
FOV_AZ_DEG = 48.0           # rough half-width of what the headset shows
FOV_EL_DEG = 42.0           # rough half-height
TIMEOUT_S = 20.0            # RoomTest.timeoutSeconds


def load(path):
    df = pd.read_csv(path, comment="#")
    df["event"] = df["event"].fillna("").astype(str)
    return df


def events(df):
    """One (time, label) pair per event. A row can hold several labels joined by ';'."""
    out = []
    for t, e in zip(df["t_s"].to_numpy(), df["event"].to_numpy()):
        if e:
            for part in e.split(";"):
                if part:
                    out.append((float(t), part))
    return out


class Track:
    """The head and gaze over time, pulled out of one recording as plain arrays."""

    def __init__(self, df):
        self.t = df["t_s"].to_numpy(float)
        self.head_pos = df[["head_x", "head_y", "head_z"]].to_numpy(float)
        q = df[["head_qx", "head_qy", "head_qz", "head_qw"]].to_numpy(float)
        self.head_q = q / np.linalg.norm(q, axis=1, keepdims=True)
        g = df[["gaze_dir_x", "gaze_dir_y", "gaze_dir_z"]].to_numpy(float)
        self.gaze_world = g / np.linalg.norm(g, axis=1, keepdims=True)
        self.is_fixating = df["is_fixating"].to_numpy(int)
        self.fix_dur = df["fix_dur_s"].to_numpy(float)
        self.head_speed = df["head_speed_dps"].to_numpy(float)
        self.mask_on = df["filter_on"].to_numpy(int)
        side = df["blind_side"].astype(str).to_numpy()
        self.blind_sign = np.where(side == "Left", -1.0, 1.0)  # which way is "into the blind side"
        # Gaze direction in the head's frame, as azimuth and elevation.
        gh = rotate_inverse(self.head_q, self.gaze_world)
        self.gaze_az, self.gaze_el = to_angles(gh)

    def index_at(self, time):
        return int(np.clip(np.searchsorted(self.t, time), 0, len(self.t) - 1))


def rotate(q, v):
    """Rotate vectors v (N,3) by unit quaternions q (N,4 as x,y,z,w)."""
    u = q[:, :3]
    w = q[:, 3:4]
    t = 2.0 * np.cross(u, v)
    return v + w * t + np.cross(u, t)


def rotate_inverse(q, v):
    qi = q.copy()
    qi[:, :3] *= -1.0
    return rotate(qi, v)


def to_angles(v):
    """Head-frame direction to (azimuth, elevation) in degrees. Same as GazeCorrection.ToAngles."""
    az = np.degrees(np.arctan2(v[:, 0], v[:, 2]))
    el = np.degrees(np.arctan2(v[:, 1], np.hypot(v[:, 0], v[:, 2])))
    return az, el


def from_angles(az, el):
    """(azimuth, elevation) in degrees to a head-frame unit direction. Same as GazeCorrection.FromAngles."""
    a = np.radians(az)
    e = np.radians(el)
    return np.array([np.sin(a) * np.cos(e), np.sin(e), np.cos(a) * np.cos(e)])


def wrap(a):
    return (a + 180.0) % 360.0 - 180.0


def view_of_point(track, point, i0, i1):
    """
    For frames i0..i1, where is a fixed world point compared with the gaze and the head?
    Returns a dict of arrays:
      ecc        angle between the gaze and the point (degrees)
      into_blind how far the point is on the blind side of the gaze, left/right only (degrees, + = blind side)
      head_az    the point's azimuth in the head frame, signed so + = toward the blind side
      head_el    the point's elevation in the head frame
      radius     how big the object looks (degrees, half-width)
      uncovered  True when the object is not fully hidden by the mask AND is inside the headset's view
    """
    sl = slice(i0, i1 + 1)
    to = point[None, :] - track.head_pos[sl]
    dist = np.linalg.norm(to, axis=1)
    to_n = to / dist[:, None]
    ecc = np.degrees(np.arccos(np.clip((to_n * track.gaze_world[sl]).sum(1), -1.0, 1.0)))
    az, el = to_angles(rotate_inverse(track.head_q[sl], to_n))
    sign = track.blind_sign[sl]
    into_blind = sign * wrap(az - track.gaze_az[sl])
    radius = np.degrees(np.arctan2(OBJECT_RADIUS_M, dist))
    hidden = (track.mask_on[sl] == 1) & (into_blind > radius)
    in_view = (np.abs(az) <= FOV_AZ_DEG) & (np.abs(el) <= FOV_EL_DEG)
    return dict(ecc=ecc, into_blind=into_blind, head_az=sign * az, head_el=el, radius=radius,
                uncovered=(~hidden) & in_view, t=track.t[sl])


def first_run(flags, t, min_seconds):
    """Time at which `flags` has been True without a break for min_seconds. None if never."""
    start = None
    for i in range(len(flags)):
        if flags[i]:
            if start is None:
                start = t[i]
            if t[i] - start >= min_seconds:
                return t[i]
        else:
            start = None
    return None


def looked_at_time(view):
    """When the gaze first RESTS on the object (the same test LookTracker runs live). None if never."""
    in_beam = view["uncovered"] & (view["ecc"] <= LOOK_BEAM_DEG + view["radius"])
    return first_run(in_beam, view["t"], LOOK_DWELL_S)


def noticed_time(view, min_uncovered_s, max_ecc_deg):
    """
    When would the person NOTICE the object without being told? The idea: you cannot notice it while it is
    hidden on the blind side. Once your looking around uncovers it (it ends up on your seeing side, inside
    the headset's view, not too far out in the corner of your eye) for a short moment, you spot it.
    Returns the time it is spotted, or None.
    """
    seen = view["uncovered"] & (view["ecc"] <= max_ecc_deg)
    return first_run(seen, view["t"], min_uncovered_s)


def spot_features(track, point, i0, history_s=10.0):
    """
    The facts about one spot at one moment (frame i0) that a live system could also work out:
    everything here uses only what happened up to that moment.
    """
    j0 = track.index_at(track.t[i0] - history_s)
    hist = view_of_point(track, point, j0, i0)
    now_ecc = hist["ecc"][-1]
    into_blind = hist["into_blind"][-1]
    unc = hist["uncovered"]
    span = max(1e-6, hist["t"][-1] - hist["t"][0])

    # How long since this spot was last uncovered (capped at the history length).
    if unc.any():
        since_uncovered = float(hist["t"][-1] - hist["t"][np.where(unc)[0][-1]])
    else:
        since_uncovered = float(history_s)
    # What share of the recent past was it uncovered?
    dt = np.diff(hist["t"], prepend=hist["t"][0])
    uncovered_share = float((dt * unc).sum() / span)
    # How far beyond the edge of the recent scanning does it sit? Positive = further into the blind side
    # than the gaze ever went in the last few seconds (so normal looking around never reaches it).
    beyond_scan = float(hist["into_blind"].min())

    return dict(
        into_blind_deg=float(into_blind),
        up_down_deg=float(abs(hist["head_el"][-1] - track.gaze_el[i0])),
        from_head_deg=float(hist["head_az"][-1]),
        ecc_deg=float(now_ecc),
        since_uncovered_s=min(since_uncovered, history_s),
        uncovered_share=uncovered_share,
        beyond_scan_deg=beyond_scan,
        head_speed_dps=float(track.head_speed[i0]),
        eyes_toward_blind_deg=float(track.blind_sign[i0] * track.gaze_az[i0]),
    )


FEATURES = ["into_blind_deg", "up_down_deg", "from_head_deg", "since_uncovered_s", "uncovered_share",
            "beyond_scan_deg", "head_speed_dps", "eyes_toward_blind_deg"]
