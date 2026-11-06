using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Hemianopia.Calibration;
using Hemianopia.Gaze;
using Hemianopia.Rendering;
using Hemianopia.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Diagnostics
{
    /// <summary>
    /// Separates tracker NOISE from LATENCY.
    ///
    /// A white dot is fixed in the room. You stare at it while (1) holding still, (2) turning your head
    /// slowly, (3) turning it fast. Your eyes counter-rotate to stay on the dot, so a perfect system
    /// would show zero error at any head speed.
    ///
    /// Two things can push the gaze toward the direction you're turning, and they behave differently:
    ///   a fixed LEAN: the same offset at any speed (the headset or the eyes slipping a little)
    ///   a DELAY: eye data arriving late, so the offset grows with speed (speed × delay)
    /// So we fit both at once:
    ///     horizontal error = c + lean × direction of turn + delay × head speed + noise
    /// Fitting only a delay mistakes a fixed lean for latency when the turns are slow. That happened in
    /// the first runs, so the fast phase has to be properly fast.
    ///
    /// Controls in Play mode: T or the left grip starts it. Results go to the status panel, the console,
    /// and a CSV in CalibrationLogs/. Full write-up: Docs/HEAD_MOTION_TEST.md
    /// </summary>
    [DisallowMultipleComponent]
    public class HeadMotionTest : MonoBehaviour
    {
        /// <summary>True while any head-motion test is running (so calibration doesn't start on top of it).</summary>
        public static bool AnyRunning { get; private set; }

        [Header("Who")]
        [Tooltip("Used only if there's no CalibrationRunner in the scene. Otherwise its participant code is used.")]
        public string participantCode = "P00";

        [Header("Phases (seconds)")]
        public float countdownSeconds = 3f;
        public float stillSeconds = 4f;
        public float slowSeconds = 8f;
        public float fastSeconds = 8f;

        [Header("Dot")]
        [Tooltip("How far in front of you the dot is placed, in meters. It stays fixed in the room.")]
        public float dotDistance = 2f;

        [Tooltip("Dot size in degrees of visual angle.")]
        public float dotSizeDegrees = 0.6f;

        [Header("Analysis")]
        [Tooltip("Frames with slower head turning than this (degrees per second) are left out of the delay fit.")]
        public float minHeadSpeedForFit = 5f;

        [Header("Hooks (found automatically if left empty)")]
        public GazeProvider gaze;
        public HemianopiaController hemianopia;
        public GazeDot gazeDot;
        public CalibrationRunner calibration;

        public bool IsRunning { get; private set; }
        public HeadMotionResult LastResult { get; private set; }

        enum Phase { None, Still, Slow, Fast }

        struct Sample
        {
            public float time;
            public Phase phase;
            public float yawRate;    // head turning speed, degrees per second, + = turning right
            public float pitchRate;  // + = tilting up
            public float errAz;      // gaze minus dot, degrees, + = gaze to the right of the dot
            public float errEl;      // + = gaze above the dot
            public float errTotal;   // angle between gaze and dot, degrees
            public bool fresh;       // a new tracker sample arrived this frame
            public bool corrected;   // calibration correction was on
            public bool timeMatched; // this frame's gaze was time-matched to the head
            public TimeMatchStatus timeMatchState;
            public float ageMs;      // how old the eye sample was, ms
        }

        readonly List<Sample> _samples = new List<Sample>();
        Phase _phase = Phase.None;
        Transform _dot;
        bool _hasLast;
        double _lastTime, _lastStamp, _startTime;
        float _lastYaw, _lastPitch;

        void Awake()
        {
            if (gaze == null) gaze = FindAnyObjectByType<GazeProvider>();
            if (hemianopia == null) hemianopia = FindAnyObjectByType<HemianopiaController>();
            if (gazeDot == null) gazeDot = FindAnyObjectByType<GazeDot>();
            if (calibration == null) calibration = FindAnyObjectByType<CalibrationRunner>();
        }

        void OnDisable()
        {
            GazeProvider.GazeUpdated -= Record;
            if (IsRunning) AnyRunning = false;
        }

        void Update()
        {
            if (gaze == null || IsRunning) return;
            if (calibration != null && calibration.IsRunning) return;
            if (StartPressed()) StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            Camera cam = gaze.viewCamera != null ? gaze.viewCamera : Camera.main;
            if (cam == null) yield break;

            IsRunning = true;
            AnyRunning = true;
            _samples.Clear();

            // The filter and the gaze dot are hidden so nothing blocks or pulls the eyes. Restored at the end.
            bool filterWasOn = hemianopia != null && hemianopia.filterEnabled;
            if (hemianopia != null) hemianopia.filterEnabled = false;
            bool dotWasOn = gazeDot != null && gazeDot.enabled;
            if (gazeDot != null) gazeDot.enabled = false;

            // A dot fixed in the ROOM, straight ahead at eye height.
            Vector3 flat = cam.transform.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "Head-motion test dot";
            Destroy(dot.GetComponent<Collider>());
            dot.GetComponent<Renderer>().material.color = Color.white;
            dot.transform.position = cam.transform.position + flat * dotDistance;
            dot.transform.localScale = Vector3.one * (2f * dotDistance * Mathf.Tan(0.5f * dotSizeDegrees * Mathf.Deg2Rad));
            _dot = dot.transform;

            for (float c = countdownSeconds; c > 0f; c -= Time.deltaTime)
            {
                StatusHud.SetBanner($"Head-motion test: look at the white dot. Starting in {Mathf.CeilToInt(c)}");
                yield return null;
            }

            _hasLast = false;
            _startTime = Time.realtimeSinceStartupAsDouble;
            GazeProvider.GazeUpdated += Record;

            yield return StartCoroutine(RunPhase(Phase.Still, stillSeconds, "Hold your head still. Eyes on the dot."));
            yield return StartCoroutine(RunPhase(Phase.Slow, slowSeconds, "Turn your head SLOWLY left and right. Eyes on the dot."));
            yield return StartCoroutine(RunPhase(Phase.Fast, fastSeconds, "Now turn FASTER, left and right. Eyes on the dot."));

            GazeProvider.GazeUpdated -= Record;
            _phase = Phase.None;
            StatusHud.SetBanner(null);
            Destroy(dot);
            _dot = null;

            HeadMotionResult r = Analyze();
            LastResult = r;
            string path = Save(r);
            Debug.Log(r.Summary() + "\n" + r.Reading() + (path != null ? $"\nSaved to {path}" : ""));
            StatusHud.SetLine("headmotion", r.ShortSummary());

            if (hemianopia != null) hemianopia.filterEnabled = filterWasOn;
            if (gazeDot != null) gazeDot.enabled = dotWasOn;
            IsRunning = false;
            AnyRunning = false;
        }

        IEnumerator RunPhase(Phase phase, float seconds, string instruction)
        {
            _phase = phase;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                StatusHud.SetBanner($"{instruction} ({Mathf.CeilToInt(seconds - t)} s)");
                yield return null;
            }
        }

        // Called once per frame right before rendering, after the headset's final head update.
        void Record(GazeProvider g)
        {
            if (_phase == Phase.None || _dot == null || !g.HasGaze || g.viewCamera == null) return;

            Transform head = g.viewCamera.transform;
            double now = Time.realtimeSinceStartupAsDouble;
            Vector3 forward = head.forward;
            float yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;

            if (!_hasLast)
            {
                _lastYaw = yaw; _lastPitch = pitch; _lastTime = now; _lastStamp = g.Timestamp;
                _hasLast = true;
                return;
            }

            float dt = (float)(now - _lastTime);
            if (dt <= 1e-4f) return;
            float yawRate = Mathf.DeltaAngle(_lastYaw, yaw) / dt;
            float pitchRate = Mathf.DeltaAngle(_lastPitch, pitch) / dt;
            _lastYaw = yaw; _lastPitch = pitch; _lastTime = now;

            // Error measured in the head's frame: where the gaze points vs where the dot is.
            Ray gazeRay = g.RawGazeRay; // unsmoothed (bias-corrected if a calibration is on)
            Vector3 toDot = _dot.position - gazeRay.origin;
            Vector2 gazeAngles = GazeCorrection.ToAngles(head.InverseTransformDirection(gazeRay.direction));
            Vector2 dotAngles = GazeCorrection.ToAngles(head.InverseTransformDirection(toDot));
            Vector2 err = gazeAngles - dotAngles;

            bool fresh = g.Timestamp != _lastStamp;
            _lastStamp = g.Timestamp;

            _samples.Add(new Sample
            {
                time = (float)(now - _startTime),
                phase = _phase,
                yawRate = yawRate,
                pitchRate = pitchRate,
                errAz = err.x,
                errEl = err.y,
                errTotal = Vector3.Angle(gazeRay.direction, toDot),
                fresh = fresh,
                corrected = g.IsCorrectionActive,
                timeMatched = g.IsTimeMatched,
                timeMatchState = g.TimeMatchState,
                ageMs = g.SampleAgeMs,
            });
        }

        HeadMotionResult Analyze()
        {
            var r = new HeadMotionResult
            {
                participant = calibration != null ? calibration.participantCode : participantCode,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                eyes = gaze.IsEyeTracking,
                corrected = gaze.IsCorrectionActive,
                timeMatchSetting = gaze.timeMatchGaze,
                estimated = gaze.TimeMatchState == TimeMatchStatus.EstimatedLatency,
                assumedLatencyMs = gaze.assumedLatencyMs,
                still = PhaseStats(Phase.Still),
                slow = PhaseStats(Phase.Slow),
                fast = PhaseStats(Phase.Fast),
            };

            // Fresh tracker samples per second, across the whole test.
            int fresh = 0;
            foreach (Sample s in _samples) if (s.fresh) fresh++;
            float duration = _samples.Count > 1 ? _samples[_samples.Count - 1].time - _samples[0].time : 0f;
            r.trackerRateHz = duration > 0f ? fresh / duration : 0f;
            r.frameRateHz = duration > 0f ? _samples.Count / duration : 0f;

            // Eye data age (median) and the share of frames that actually got time-matched.
            var ages = new List<float>(_samples.Count);
            int matched = 0;
            foreach (Sample s in _samples)
            {
                ages.Add(s.ageMs);
                if (s.timeMatched) matched++;
            }
            if (ages.Count > 0)
            {
                ages.Sort();
                r.medianSampleAgeMs = ages[ages.Count / 2];
                r.timeMatchedShare = (float)matched / ages.Count;
            }

            // Fit: horizontal error = c + lean * sign(yaw rate) + delay * yaw rate, over the moving phases.
            // Least squares with three unknowns: normal equations (X^T X) beta = X^T y, solved directly.
            double[,] m = new double[3, 3];
            double[] v = new double[3];
            double sy = 0, syy = 0;
            int n = 0;
            foreach (Sample s in _samples)
            {
                if (s.phase == Phase.Still || Mathf.Abs(s.yawRate) < minHeadSpeedForFit) continue;
                double[] x = { 1.0, Math.Sign(s.yawRate), s.yawRate };
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++) m[i, j] += x[i] * x[j];
                    v[i] += x[i] * s.errAz;
                }
                sy += s.errAz; syy += s.errAz * s.errAz;
                n++;
            }
            r.fitSamples = n;
            if (n >= 30 && Solve3(m, v, out double c0, out double lean, out double delay))
            {
                double ssTot = syy - sy * sy / n;
                double ssRes = 0;
                foreach (Sample s in _samples)
                {
                    if (s.phase == Phase.Still || Mathf.Abs(s.yawRate) < minHeadSpeedForFit) continue;
                    double resid = s.errAz - (c0 + lean * Math.Sign(s.yawRate) + delay * s.yawRate);
                    ssRes += resid * resid;
                }
                r.fitOk = true;
                r.interceptDeg = (float)c0;
                r.leanDeg = (float)lean;
                r.delayMs = (float)(delay * 1000.0); // degrees per (degrees per second) = seconds
                r.rSquared = ssTot > 1e-9 ? (float)(1.0 - ssRes / ssTot) : 0f;
                r.residualRmsDeg = (float)Math.Sqrt(ssRes / n);
            }
            return r;
        }

        static bool Solve3(double[,] m, double[] v, out double x0, out double x1, out double x2)
        {
            double det = Det3(m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2], m[2, 0], m[2, 1], m[2, 2]);
            x0 = x1 = x2 = 0;
            if (Math.Abs(det) < 1e-9) return false;
            x0 = Det3(v[0], m[0, 1], m[0, 2], v[1], m[1, 1], m[1, 2], v[2], m[2, 1], m[2, 2]) / det;
            x1 = Det3(m[0, 0], v[0], m[0, 2], m[1, 0], v[1], m[1, 2], m[2, 0], v[2], m[2, 2]) / det;
            x2 = Det3(m[0, 0], m[0, 1], v[0], m[1, 0], m[1, 1], v[1], m[2, 0], m[2, 1], v[2]) / det;
            return true;
        }

        static double Det3(double a, double b, double c, double d, double e, double f, double g, double h, double i)
        {
            return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        }

        HeadMotionResult.Stats PhaseStats(Phase phase)
        {
            var st = new HeadMotionResult.Stats();
            double speed = 0, err = 0, err2 = 0;
            foreach (Sample s in _samples)
            {
                if (s.phase != phase) continue;
                st.samples++;
                speed += Mathf.Abs(s.yawRate);
                err += s.errTotal;
                err2 += s.errTotal * s.errTotal;
            }
            if (st.samples > 0)
            {
                st.meanHeadSpeed = (float)(speed / st.samples);
                st.meanErrorDeg = (float)(err / st.samples);
                st.rmsErrorDeg = (float)Math.Sqrt(err2 / st.samples);
            }
            return st;
        }

        string Save(HeadMotionResult r)
        {
            try
            {
                string folder = CalibrationRunner.LogFolder();
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(folder, $"headmotion_{r.participant}_{stamp}.csv");

                var c = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine($"# participant={r.participant}, time={r.timestamp}, source={(r.eyes ? "eyes" : "mouse")}, correction={(r.corrected ? "on" : "off")}, time_matching={(r.timeMatchSetting ? "on" : "off")}");
                sb.AppendLine("# " + r.Summary());
                sb.AppendLine("time_s,phase,yaw_rate_dps,pitch_rate_dps,err_az_deg,err_el_deg,err_total_deg,fresh,corrected,time_matched,time_match_state,sample_age_ms");
                foreach (Sample s in _samples)
                {
                    sb.AppendLine(string.Format(c, "{0:0.0000},{1},{2:0.00},{3:0.00},{4:0.0000},{5:0.0000},{6:0.0000},{7},{8},{9},{10},{11:0.0}",
                        s.time, s.phase, s.yawRate, s.pitchRate, s.errAz, s.errEl, s.errTotal, s.fresh ? 1 : 0, s.corrected ? 1 : 0,
                        s.timeMatched ? 1 : 0, s.timeMatchState, s.ageMs));
                }
                File.WriteAllText(path, sb.ToString());
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HeadMotion] Couldn't save the results: {e.Message}");
                return null;
            }
        }

        static bool StartPressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.tKey.wasPressedThisFrame) return true;
#endif
            return OVRInput.GetDown(OVRInput.RawButton.LHandTrigger, OVRInput.Controller.Touch);
        }
    }

    /// <summary>The result of one head-motion test.</summary>
    public class HeadMotionResult
    {
        public class Stats
        {
            public int samples;
            public float meanHeadSpeed;   // degrees per second
            public float meanErrorDeg;
            public float rmsErrorDeg;
        }

        public string participant;
        public string timestamp;
        public bool eyes;
        public bool corrected;
        public bool timeMatchSetting;     // time-matching switch at the start of the test
        public bool estimated;            // time-matching was estimating (eye clock unknown)
        public float assumedLatencyMs;    // the assumed latency it used
        public float timeMatchedShare;    // share of frames that actually got time-matched (0 to 1)
        public float medianSampleAgeMs;   // how old the eye data was, median
        public Stats still, slow, fast;
        public float trackerRateHz;
        public float frameRateHz;

        public bool fitOk;
        public int fitSamples;
        public float leanDeg;        // fixed offset toward the turn, the same at any speed
        public float delayMs;        // extra offset that grows with speed, as a delay
        public float interceptDeg;   // leftover horizontal offset, whichever way you turn
        public float rSquared;       // how much of the error lean + delay explain together (0 to 1)
        public float residualRmsDeg; // scatter the fit doesn't explain = noise while moving

        public string Summary()
        {
            var c = CultureInfo.InvariantCulture;
            string fit = fitOk
                ? string.Format(c, "Lean toward the turn {0:0.0}°, delay {1:0} ms (together they explain {2:0}% of the error), noise {3:0.00}°.", leanDeg, delayMs, rSquared * 100f, residualRmsDeg)
                : "Not enough head movement to fit.";
            string matching = timeMatchSetting
                ? string.Format(c, "time-matching ON ({0:0}% of frames)", timeMatchedShare * 100f)
                : "time-matching OFF";
            return string.Format(c,
                "[HeadMotion] {0} ({1}, correction {2}, {3}): error still {4:0.00}°, slow {5:0.00}° at {6:0} °/s, fast {7:0.00}° at {8:0} °/s. {9} Eye data {10:0} ms old. Tracker {11:0} Hz, frames {12:0} Hz.",
                participant, eyes ? "eyes" : "MOUSE", corrected ? "on" : "off", matching,
                still.rmsErrorDeg, slow.rmsErrorDeg, slow.meanHeadSpeed, fast.rmsErrorDeg, fast.meanHeadSpeed,
                fit, medianSampleAgeMs, trackerRateHz, frameRateHz);
        }

        public string ShortSummary()
        {
            var c = CultureInfo.InvariantCulture;
            string fit = fitOk ? string.Format(c, ", lean {0:0.0}°, delay ~{1:0} ms", leanDeg, delayMs) : "";
            return string.Format(c, "Head-motion ({0}): still {1:0.0}°, slow {2:0.0}°, fast {3:0.0}°{4}",
                timeMatchSetting ? "time-matched" : "not time-matched",
                still.rmsErrorDeg, slow.rmsErrorDeg, fast.rmsErrorDeg, fit);
        }

        /// <summary>A plain-language reading of the numbers. A hint, not a verdict.</summary>
        public string Reading()
        {
            if (!fitOk) return "Reading: turn your head more during the slow and fast phases, then retry.";
            if (timeMatchSetting && timeMatchedShare < 0.5f)
                return string.Format(CultureInfo.InvariantCulture,
                    "Reading: time-matching was switched on but only applied to {0:0}% of frames, so this run can't judge it. Check the status panel's time-matching line.",
                    timeMatchedShare * 100f);
            if (timeMatchSetting && estimated)
                return string.Format(CultureInfo.InvariantCulture,
                    "Reading: time-matching is estimating with {0:0} ms. {1:0} ms of delay is left over, so try assumedLatencyMs = {2:0} on GazeProvider and run again.",
                    assumedLatencyMs, delayMs, assumedLatencyMs + delayMs);
            var c = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            if (fast.meanHeadSpeed < 60f)
                parts.Add("The fast phase was slow, so a fixed lean and a delay are hard to tell apart. Turn faster.");
            parts.Add(Mathf.Abs(leanDeg) >= 1f
                ? string.Format(c, "There's a fixed lean of {0:0.0}° toward the turn, the same at any speed: the headset or the eyes slipping, not latency.", leanDeg)
                : "No real fixed lean.");
            parts.Add(delayMs >= 30f
                ? string.Format(c, "There's also a real delay of about {0:0} ms on top.", delayMs)
                : delayMs <= -30f
                    ? string.Format(c, "The error shrinks AGAINST the turn as speed grows ({0:0} ms). If time-matching is on, it's over-correcting.", delayMs)
                    : string.Format(c, "Latency is small ({0:0} ms).", delayMs));
            return "Reading: " + string.Join(" ", parts);
        }
    }
}
