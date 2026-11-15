#!/usr/bin/env python3
"""
train_model.py: train the first "miss time" model and check it.

    python Analysis/build_table.py     (first: makes the two tables)
    python Analysis/train_model.py

The model is a linear regression, the simplest thing that can learn from data:

    guess = w1 * fact1 + w2 * fact2 + ... + bias

It guesses how long an object will go unnoticed (seconds, on a log scale so one 20 s miss does not swamp
everything). Training finds the weights w that make the guesses closest to the true answers
(the smallest mean squared error).

It TRAINS on the imaginary objects (thousands of rows from the free-looking recording) and is TESTED on
the real surprises, which it has never seen. If it does well on those, it learned something true.

It prints:
  - how much each fact matters (the weights)
  - how good the guesses are on the real surprises, next to two simple yardsticks
and writes CalibrationLogs/analysis/miss_model.json (the numbers Unity needs) and miss_model_check.png.
Needs numpy and pandas. matplotlib is optional (for the picture).
"""
import json
import os

import numpy as np
import pandas as pd

import gaze_tools as G

RIDGE = 1.0  # a small pull toward zero on the weights, so no single fact gets a wild weight


def fit(X, y):
    mean = X.mean(axis=0)
    std = X.std(axis=0)
    std[std < 1e-9] = 1.0
    Z = (X - mean) / std
    A = np.hstack([Z, np.ones((len(Z), 1))])
    reg = RIDGE * np.eye(A.shape[1])
    reg[-1, -1] = 0.0  # do not shrink the bias
    w = np.linalg.solve(A.T @ A + reg, A.T @ y)
    return dict(mean=mean, std=std, weights=w[:-1], bias=float(w[-1]))


def predict_seconds(model, X):
    Z = (X - model["mean"]) / model["std"]
    return np.clip(np.expm1(Z @ model["weights"] + model["bias"]), 0.0, G.TIMEOUT_S)


def rank_match(a, b):
    """Spearman rank correlation: 1 = same order, 0 = unrelated."""
    return float(pd.Series(a).rank().corr(pd.Series(b).rank()))


def pick_out_missed(score, missed):
    """
    If you pick one object that was missed and one that was found at random, how often did the model give
    the missed one the longer guess? 0.5 = coin flip, 1.0 = always right. (Also called AUC.)
    """
    missed = np.asarray(missed).astype(bool)
    if missed.all() or (~missed).all():
        return float("nan")
    ranks = pd.Series(score).rank().to_numpy()
    n1, n0 = missed.sum(), (~missed).sum()
    return float((ranks[missed].sum() - n1 * (n1 + 1) / 2) / (n1 * n0))


def report(name, guess, table):
    true = table["miss_s"].to_numpy(float)
    missed = table["found"].to_numpy(int) == 0
    err = np.abs(guess - true)
    print(f"  {name:34s} typical error {np.median(err):5.2f} s | average error {err.mean():5.2f} s | "
          f"same order {rank_match(guess, true):5.2f} | picks out the missed ones {pick_out_missed(guess, missed):4.2f}")
    return dict(median_error=float(np.median(err)), mean_error=float(err.mean()),
                rank=rank_match(guess, true), auc=pick_out_missed(guess, missed))


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "..", "CalibrationLogs", "analysis")
    real = pd.read_csv(os.path.join(out, "real_table.csv"))
    imag = pd.read_csv(os.path.join(out, "imaginary_table.csv"))

    feats = G.FEATURES
    y = np.log1p(imag["miss_s"].to_numpy(float))

    # 1. Does it hold up on a part of the free-looking recording it did not train on?
    #    Train on the first 70% of the recording (by time), test on the last 30%.
    cut = imag["t0"].quantile(0.7)
    early, late = imag[imag.t0 <= cut], imag[imag.t0 > cut]
    m_early = fit(early[feats].to_numpy(float), np.log1p(early["miss_s"].to_numpy(float)))
    print(f"Imaginary objects: {len(imag)} rows. Trained on the first 70% of the recording, tested on the last 30%:")
    report("model", predict_seconds(m_early, late[feats].to_numpy(float)), late)
    report("yardstick: always guess the middle", np.full(len(late), early["miss_s"].median()), late)

    # 2. The real test: train on ALL imaginary rows, test on the real surprises.
    model = fit(imag[feats].to_numpy(float), y)
    guess = predict_seconds(model, real[feats].to_numpy(float))
    print(f"\nReal surprises: {len(real)} rows the model has never seen:")
    r_model = report("model (8 facts)", guess, real)
    r_mid = report("yardstick: always guess the middle", np.full(len(real), imag["miss_s"].median()), real)
    one = fit(imag[["into_blind_deg"]].to_numpy(float), y)
    r_one = report("yardstick: distance into blind side", predict_seconds(one, real[["into_blind_deg"]].to_numpy(float)), real)

    # 3. What did it learn? Bigger weight = that fact moves the guess more. + means "longer miss".
    print("\nHow much each fact matters (weights, after putting every fact on the same scale):")
    order = np.argsort(-np.abs(model["weights"]))
    for i in order:
        w = model["weights"][i]
        print(f"  {feats[i]:24s} {w:+.2f}   {'longer miss when this is bigger' if w > 0 else 'shorter miss when this is bigger'}")

    # 4. Save the numbers Unity needs: guess = expm1( sum_i w_i * (x_i - mean_i) / std_i + bias ).
    saved = dict(features=feats, mean=model["mean"].tolist(), std=model["std"].tolist(),
                 weights=model["weights"].tolist(), bias=model["bias"],
                 output="seconds unnoticed = expm1(sum(w * (x - mean) / std) + bias), capped at 20",
                 trained_on=f"{len(imag)} imaginary objects", tested_on=f"{len(real)} real surprises",
                 test=dict(model=r_model, guess_middle=r_mid, distance_only=r_one))
    with open(os.path.join(out, "miss_model.json"), "w") as f:
        json.dump(saved, f, indent=2)
    real_out = real.copy()
    real_out["guess_s"] = guess
    real_out.to_csv(os.path.join(out, "real_table_with_guesses.csv"), index=False)
    print(f"\nSaved the model to {os.path.join(out, 'miss_model.json')}")

    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        fig, ax = plt.subplots(figsize=(5.2, 4.6))
        found = real["found"].to_numpy(int) == 1
        ax.scatter(real["miss_s"][found], guess[found], label="found", s=36)
        ax.scatter(real["miss_s"][~found], guess[~found], label="never found (20 s)", s=36, marker="x")
        ax.plot([0, 20], [0, 20], lw=0.8, color="gray")
        ax.set_xlabel("Real time unnoticed (s)")
        ax.set_ylabel("Model's guess (s)")
        ax.set_title("Model trained on imaginary objects, tested on real surprises")
        ax.legend()
        fig.tight_layout()
        fig.savefig(os.path.join(out, "miss_model_check.png"), dpi=140)
    except Exception as e:  # the picture is a nice-to-have
        print("(no picture: " + str(e) + ")")


if __name__ == "__main__":
    main()
