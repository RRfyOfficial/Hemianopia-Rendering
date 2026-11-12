using System;
using System.Collections.Generic;
using UnityEngine;
using Hemianopia.Gaze;
using Hemianopia.Rendering;

namespace Hemianopia.Room
{
    /// <summary>
    /// The name tag check: works out WHICH object the person is looking at, every frame.
    ///
    /// The gaze is treated like a flashlight beam, not a laser. An object counts as looked at when:
    ///   1. its centre is within a few degrees of the gaze (coneDegrees, plus the object's own size), and
    ///   2. at least part of it is on the seeing side (when the hemianopia mask is on), and
    ///   3. the gaze has rested there for a short moment (dwellSeconds), so a glance sweeping past does not count.
    ///
    /// It is a beam and not a single ray because sharp vision covers an area, and the eye tracker is off by
    /// a degree or so (more while the eyes follow something that moves).
    ///
    /// It uses the same gaze the hemianopia filter uses (GazeProvider.FilterGazeRay), so "seen" here agrees
    /// with what the mask actually showed. The blind-side test is the same approximation MovingBlock uses:
    /// left/right angle of the object compared with the left/right angle of the gaze, in the head's frame.
    /// </summary>
    public class LookTracker : MonoBehaviour
    {
        [Header("References (found automatically if left empty)")]
        public HemianopiaController filter;

        [Header("Flashlight beam")]
        [Tooltip("Half-width of the beam in degrees. The object's own size is added on top.")]
        public float coneDegrees = 3f;
        [Tooltip("How long the gaze must rest on an object before it counts as looked at, in seconds.")]
        public float dwellSeconds = 0.1f;

        [Header("Feedback")]
        [Tooltip("Brighten an object while it is being looked at.")]
        public bool highlightLooked = true;
        [Tooltip("Print the object's name in the console each time the gaze lands on a new one.")]
        public bool logLooks = true;

        /// <summary>Fires the first time an object is looked at.</summary>
        public event Action<TrackedObject> FirstLook;

        /// <summary>The object being looked at right now (the closest one to the gaze), or null.</summary>
        public TrackedObject Current { get; private set; }

        readonly List<TrackedObject> _firstLooks = new List<TrackedObject>();
        bool _searchedForFilter;

        void OnEnable()
        {
            GazeProvider.GazeUpdated += OnGaze;
        }

        void OnDisable()
        {
            GazeProvider.GazeUpdated -= OnGaze;
        }

        void OnGaze(GazeProvider g)
        {
            if (!g.HasGaze) return;
            Camera cam = g.viewCamera != null ? g.viewCamera : Camera.main;
            if (cam == null) return;

            if (filter == null && !_searchedForFilter)
            {
                filter = FindAnyObjectByType<HemianopiaController>();
                _searchedForFilter = true;
            }

            Transform ct = cam.transform;
            Ray ray = g.FilterGazeRay;
            float gazeAz = GazeCorrection.ToAngles(ct.InverseTransformDirection(ray.direction)).x;

            bool maskOn = filter != null && filter.filterEnabled;
            bool blindLeft = filter == null || filter.blindSide == BlindSide.Left;

            float dt = Time.unscaledDeltaTime;
            double now = Time.realtimeSinceStartupAsDouble;

            TrackedObject best = null;
            float bestAngle = float.MaxValue;
            _firstLooks.Clear();

            List<TrackedObject> all = TrackedObject.All;
            for (int i = 0; i < all.Count; i++)
            {
                TrackedObject o = all[i];
                if (o == null) continue;

                Vector3 to = o.transform.position - ray.origin;
                float dist = to.magnitude;
                if (dist < 0.01f) continue;

                float angle = Vector3.Angle(ray.direction, to);
                float radiusDeg = Mathf.Atan2(o.WorldRadius, dist) * Mathf.Rad2Deg;

                // Positive = the object's centre is on the blind side of where the person is looking.
                float objAz = GazeCorrection.ToAngles(ct.InverseTransformDirection(to)).x;
                float rel = Mathf.DeltaAngle(gazeAz, objAz);
                float towardBlind = blindLeft ? -rel : rel;

                o.AngleFromGaze = angle;
                o.OnBlindSide = towardBlind > 0f;
                o.HiddenByMask = maskOn && towardBlind > radiusDeg;

                bool inBeam = !o.HiddenByMask && angle <= coneDegrees + radiusDeg;
                o.Dwell = inBeam ? o.Dwell + dt : 0f;
                bool looked = inBeam && o.Dwell >= dwellSeconds;

                if (looked)
                {
                    o.LastSeenTime = now;
                    if (!o.EverSeen)
                    {
                        o.EverSeen = true;
                        o.FirstSeenTime = now;
                        _firstLooks.Add(o);
                    }
                    if (angle < bestAngle)
                    {
                        bestAngle = angle;
                        best = o;
                    }
                }

                if (highlightLooked) o.SetLookedAt(looked);
            }

            if (best != Current)
            {
                Current = best;
                if (logLooks && best != null) Debug.Log($"[Look] Looking at: {best.label}");
            }

            // Fired after the loop, so a listener can add or remove objects safely.
            for (int i = 0; i < _firstLooks.Count; i++)
                FirstLook?.Invoke(_firstLooks[i]);
        }
    }
}
