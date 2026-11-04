using UnityEngine;

namespace Hemianopia.Gaze
{
    /// <summary>
    /// Splits gaze into "holding still" (fixation) and "jumping" (saccade), and produces a calm,
    /// stable gaze direction without adding lag to real eye jumps.
    ///
    /// Rule, applied once per frame:
    ///   angle = angle between the current stable direction and the new raw gaze direction
    ///   angle >  threshold  -> saccade: snap the stable direction to the raw gaze instantly
    ///   angle <= threshold  -> fixation: ease the stable direction toward the raw gaze,
    ///                          which irons out tracker wobble
    ///
    /// Works in WORLD space on purpose. When you stare at a cube and turn your head, your eyes
    /// counter-rotate to stay on it, so the world gaze direction barely changes and the whole
    /// thing counts as one steady fixation.
    ///
    /// This is a simple displacement-threshold classifier. Research-grade fixation detection
    /// (e.g. velocity thresholds, minimum fixation durations) can replace it later without
    /// changing anything that uses it. See Docs/GAZE.md.
    /// </summary>
    public class FixationFilter
    {
        /// <summary>Stabilized gaze direction in world space (unit vector).</summary>
        public Vector3 StableDirection { get; private set; } = Vector3.forward;

        /// <summary>True while the eyes are holding roughly still.</summary>
        public bool IsFixating { get; private set; }

        /// <summary>Seconds since the current fixation started. Resets on every saccade.</summary>
        public float FixationDuration { get; private set; }

        /// <summary>Angle in degrees between the stable and raw gaze on the last step. Handy for tuning.</summary>
        public float LastAngleDegrees { get; private set; }

        bool _initialized;

        /// <summary>Forget the current state. Call when the gaze source changes.</summary>
        public void Reset()
        {
            _initialized = false;
            IsFixating = false;
            FixationDuration = 0f;
            LastAngleDegrees = 0f;
        }

        /// <param name="rawDirection">This frame's raw gaze direction in world space.</param>
        /// <param name="deltaTime">Seconds since the last step.</param>
        /// <param name="snapThresholdDegrees">Jumps bigger than this snap instantly.</param>
        /// <param name="easeTimeConstant">Seconds. How quickly small movements are followed.</param>
        public void Step(Vector3 rawDirection, float deltaTime, float snapThresholdDegrees, float easeTimeConstant)
        {
            if (rawDirection.sqrMagnitude < 1e-8f) return;
            rawDirection.Normalize();

            if (!_initialized)
            {
                StableDirection = rawDirection;
                _initialized = true;
                IsFixating = false;
                FixationDuration = 0f;
                LastAngleDegrees = 0f;
                return;
            }

            float angle = Vector3.Angle(StableDirection, rawDirection);
            LastAngleDegrees = angle;

            if (angle > snapThresholdDegrees)
            {
                // Saccade: jump with the eyes, no lag.
                StableDirection = rawDirection;
                IsFixating = false;
                FixationDuration = 0f;
            }
            else
            {
                // Fixation: exponential smoothing. alpha = 1 - e^(-dt / tau) keeps the feel the same
                // at any frame rate (72 Hz, 90 Hz, editor, whatever).
                float alpha = easeTimeConstant <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / easeTimeConstant);
                StableDirection = Vector3.Slerp(StableDirection, rawDirection, alpha).normalized;
                IsFixating = true;
                FixationDuration += deltaTime;
            }
        }
    }
}
