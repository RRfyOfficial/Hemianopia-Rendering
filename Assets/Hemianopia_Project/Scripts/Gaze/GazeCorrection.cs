using System;
using System.Collections.Generic;
using UnityEngine;

namespace Hemianopia.Gaze
{
    /// <summary>
    /// A per-person correction for eye tracker BIAS (consistent offsets), learned by the calibration check.
    ///
    /// Directions are described as two angles relative to the head:
    ///   azimuth   = degrees left (-) or right (+) of straight ahead
    ///   elevation = degrees below (-) or above (+) straight ahead
    ///
    /// The correction is an affine map, fitted by least squares on the calibration dots:
    ///   corrected azimuth   = a0 * az + a1 * el + a2
    ///   corrected elevation = b0 * az + b1 * el + b2
    /// It can shift (offset), stretch (scale) and slightly shear the tracker's angles.
    /// It can NOT remove noise (random wobble). That's the smoothing's job. See Docs/CALIBRATION.md.
    /// </summary>
    [Serializable]
    public class GazeCorrection
    {
        public float a0 = 1f, a1 = 0f, a2 = 0f;
        public float b0 = 0f, b1 = 1f, b2 = 0f;

        /// <summary>False until a calibration succeeds.</summary>
        public bool IsValid;

        /// <summary>True if learned from real eye tracking. A mouse fit is never applied to eyes, or the reverse.</summary>
        public bool FitOnEyes;

        /// <summary>Participant code this correction belongs to (saved with it).</summary>
        public string participant = "";

        /// <summary>When the calibration that produced this correction ran (saved with it).</summary>
        public string savedAt = "";

        // Sanity limits. Real tracker bias is a few degrees, and its scale is close to 1.
        // A fit far outside these means the calibration went wrong (looked away, lots of blinks).
        public const float MaxOffsetDegrees = 10f;
        public const float MinScale = 0.6f;
        public const float MaxScale = 1.4f;
        public const float MaxShear = 0.4f;

        /// <summary>Head-space direction to (azimuth, elevation) in degrees.</summary>
        public static Vector2 ToAngles(Vector3 direction)
        {
            direction.Normalize();
            float az = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            float el = Mathf.Atan2(direction.y, Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z)) * Mathf.Rad2Deg;
            return new Vector2(az, el);
        }

        /// <summary>(azimuth, elevation) in degrees to a head-space unit direction. Exact inverse of ToAngles.</summary>
        public static Vector3 FromAngles(Vector2 angles)
        {
            // Pitch up by elevation (negative X rotation tilts +Z upward in Unity), then yaw by azimuth.
            return Quaternion.Euler(-angles.y, angles.x, 0f) * Vector3.forward;
        }

        public Vector2 ApplyAngles(Vector2 m)
        {
            return new Vector2(a0 * m.x + a1 * m.y + a2,
                               b0 * m.x + b1 * m.y + b2);
        }

        public Vector3 ApplyDirection(Vector3 directionHead)
        {
            return FromAngles(ApplyAngles(ToAngles(directionHead)));
        }

        public void Reset()
        {
            a0 = 1f; a1 = 0f; a2 = 0f;
            b0 = 0f; b1 = 1f; b2 = 0f;
            IsValid = false;
            FitOnEyes = false;
            participant = "";
            savedAt = "";
        }

        public void CopyFrom(GazeCorrection other)
        {
            a0 = other.a0; a1 = other.a1; a2 = other.a2;
            b0 = other.b0; b1 = other.b1; b2 = other.b2;
            IsValid = other.IsValid;
            FitOnEyes = other.FitOnEyes;
            participant = other.participant;
            savedAt = other.savedAt;
        }

        /// <summary>
        /// Least-squares fit from pairs of (measured, true) angles. Needs at least 4 pairs (3 unknowns per axis).
        /// On failure this object is left unchanged and <paramref name="reason"/> says why.
        /// </summary>
        public bool TryFit(IReadOnlyList<Vector2> measured, IReadOnlyList<Vector2> truth, bool fitOnEyes, out string reason)
        {
            reason = "";
            int n = measured.Count;
            if (n < 4 || truth.Count != n)
            {
                reason = $"need at least 4 good dots, got {n}";
                return false;
            }

            // Normal equations for y = c0 * x + c1 * e + c2, where (x, e) are the measured angles.
            double sxx = 0, sxe = 0, sx = 0, see = 0, se = 0, s1 = 0;
            double tAzX = 0, tAzE = 0, tAz = 0, tElX = 0, tElE = 0, tEl = 0;
            for (int i = 0; i < n; i++)
            {
                double x = measured[i].x, e = measured[i].y;
                double az = truth[i].x, el = truth[i].y;
                sxx += x * x; sxe += x * e; sx += x;
                see += e * e; se += e; s1 += 1;
                tAzX += az * x; tAzE += az * e; tAz += az;
                tElX += el * x; tElE += el * e; tEl += el;
            }

            if (!Solve3(sxx, sxe, sx, sxe, see, se, sx, se, s1, tAzX, tAzE, tAz, out double ra0, out double ra1, out double ra2) ||
                !Solve3(sxx, sxe, sx, sxe, see, se, sx, se, s1, tElX, tElE, tEl, out double rb0, out double rb1, out double rb2))
            {
                reason = "the dots didn't spread out enough to fit a correction";
                return false;
            }

            bool sane = Math.Abs(ra2) <= MaxOffsetDegrees && Math.Abs(rb2) <= MaxOffsetDegrees
                        && ra0 >= MinScale && ra0 <= MaxScale && rb1 >= MinScale && rb1 <= MaxScale
                        && Math.Abs(ra1) <= MaxShear && Math.Abs(rb0) <= MaxShear;
            if (!sane)
            {
                reason = $"the fit looked wrong (offsets {ra2:0.0}°, {rb2:0.0}°, scales {ra0:0.00}, {rb1:0.00}), probably a look-away or blinks";
                return false;
            }

            a0 = (float)ra0; a1 = (float)ra1; a2 = (float)ra2;
            b0 = (float)rb0; b1 = (float)rb1; b2 = (float)rb2;
            IsValid = true;
            FitOnEyes = fitOnEyes;
            return true;
        }

        // Solves the 3x3 system [m] * [x0 x1 x2] = [r] with Cramer's rule.
        static bool Solve3(double m00, double m01, double m02,
                           double m10, double m11, double m12,
                           double m20, double m21, double m22,
                           double r0, double r1, double r2,
                           out double x0, out double x1, out double x2)
        {
            double det = Det3(m00, m01, m02, m10, m11, m12, m20, m21, m22);
            if (Math.Abs(det) < 1e-9)
            {
                x0 = x1 = x2 = 0;
                return false;
            }
            x0 = Det3(r0, m01, m02, r1, m11, m12, r2, m21, m22) / det;
            x1 = Det3(m00, r0, m02, m10, r1, m12, m20, r2, m22) / det;
            x2 = Det3(m00, m01, r0, m10, m11, r1, m20, m21, r2) / det;
            return true;
        }

        static double Det3(double a, double b, double c,
                           double d, double e, double f,
                           double g, double h, double i)
        {
            return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        }
    }
}
