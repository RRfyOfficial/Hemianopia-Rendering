using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Hemianopia.Diagnostics;
using Hemianopia.Gaze;
using Hemianopia.Rendering;
using Hemianopia.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Calibration
{
    /// <summary>
    /// Step 1 of the plan: our own calibration check, on top of Meta's built-in calibration.
    ///
    /// Shows 9 dots, one at a time, that move with your head (so only your eyes matter, not where your
    /// head points). For each dot it compares where the tracker THINKS you looked with where the dot
    /// really is, and measures two things:
    ///   bias  = the consistent offset (fixable: we fit a correction and apply it live)
    ///   noise = the random wobble around that offset (not fixable by calibration, it sets the smoothing)
    ///
    /// Then it shows a results picture (white = dot, red = tracker, green = after correction)
    /// and saves a CSV to CalibrationLogs/ (never committed).
    ///
    /// The fitted correction is also saved per participant (CalibrationLogs/correction_<code>.json)
    /// and loaded automatically next time you press Play, so it survives restarts.
    ///
    /// Controls in Play mode:
    ///   C or left thumbstick click   start the check (press again during the results to close them)
    ///   V or right thumbstick click  turn the correction on or off, to compare
    ///
    /// Full write-up with the math: Docs/CALIBRATION.md
    /// </summary>
    [DisallowMultipleComponent]
    public class CalibrationRunner : MonoBehaviour
    {
        [Header("Who")]
        [Tooltip("Participant code saved with the results. Never a real name.")]
        public string participantCode = "P00";

        [Tooltip("Load this participant's saved correction when Play starts.")]
        public bool loadSavedCorrection = true;

        [Header("Dot grid (degrees from straight ahead, relative to the head)")]
        [Tooltip("The outer dots sit this far left and right.")]
        public float horizontalExtentDegrees = 20f;

        [Tooltip("The outer dots sit this far up and down.")]
        public float verticalExtentDegrees = 12f;

        [Tooltip("How far away the dots float, in meters.")]
        public float dotDistance = 2f;

        [Header("Timing (seconds per dot)")]
        [Tooltip("Time for the eyes to land on a new dot before recording starts.")]
        public float settleSeconds = 0.6f;

        [Tooltip("Time spent recording each dot.")]
        public float recordSeconds = 1.0f;

        [Header("Data quality")]
        [Tooltip("A dot with fewer good samples than this is skipped.")]
        public int minSamplesPerDot = 15;

        [Tooltip("Samples farther than this from the dot's median reading are treated as glances away and dropped, in degrees.")]
        public float glanceAwayDegrees = 5f;

        [Tooltip("The correction is only fitted if at least this many dots are usable.")]
        public int minDotsForFit = 6;

        [Header("Looks")]
        [Tooltip("Size of a dot in degrees of visual angle.")]
        public float dotSizeDegrees = 0.6f;

        [Tooltip("How long the results picture stays up, in seconds.")]
        public float resultsSeconds = 12f;

        [Header("Hooks (found automatically if left empty)")]
        public GazeProvider gaze;
        public HemianopiaController hemianopia;
        public GazeDot gazeDot;

        public bool IsRunning { get; private set; }
        public CalibrationResult LastResult { get; private set; }

        readonly List<GameObject> _visuals = new List<GameObject>();
        bool _closeResults;

        void Awake()
        {
            if (gaze == null) gaze = FindAnyObjectByType<GazeProvider>();
            if (hemianopia == null) hemianopia = FindAnyObjectByType<HemianopiaController>();
            if (gazeDot == null) gazeDot = FindAnyObjectByType<GazeDot>();
        }

        void Start()
        {
            if (loadSavedCorrection) LoadSavedCorrection();
        }

        void Update()
        {
            if (gaze == null) return;

            if (CalibratePressed())
            {
                if (IsRunning) _closeResults = true;
                else if (!HeadMotionTest.AnyRunning) StartCoroutine(Run());
            }

            if (ToggleCorrectionPressed())
            {
                gaze.applyCorrection = !gaze.applyCorrection;
                Debug.Log(gaze.applyCorrection
                    ? (gaze.Correction.IsValid ? "[Calibration] Correction ON" : "[Calibration] Correction ON, but no calibration yet (press C)")
                    : "[Calibration] Correction OFF (raw tracker)");
            }
        }

        IEnumerator Run()
        {
            IsRunning = true;
            _closeResults = false;
            ClearVisuals();

            Camera cam = gaze.viewCamera != null ? gaze.viewCamera : Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[Calibration] No camera found.");
                IsRunning = false;
                yield break;
            }

            // Hide things that would block or pull the eyes: the blind half and the gaze dot. Restored at the end.
            bool filterWasOn = hemianopia != null && hemianopia.filterEnabled;
            if (hemianopia != null) hemianopia.filterEnabled = false;
            bool dotWasOn = gazeDot != null && gazeDot.enabled;
            if (gazeDot != null) gazeDot.enabled = false;

            bool eyes = gaze.IsEyeTracking;
            Debug.Log($"[Calibration] Starting ({(eyes ? "eye tracking" : "MOUSE stand-in")}). Look at each white dot until it moves.");

            var stats = new List<DotStats>();
            GameObject dot = MakeSphere("Calibration dot", Color.white, cam.transform);

            foreach (Vector2 nominal in BuildGrid())
            {
                // Dot position in head space. It's a child of the head camera, so it moves with the head.
                Vector3 dotHead = GazeCorrection.FromAngles(nominal) * dotDistance;
                dot.transform.localPosition = dotHead;

                // Settle: the dot shrinks from 3x to 1x, which pulls the eyes to its center.
                float t = 0f;
                while (t < settleSeconds)
                {
                    t += Time.deltaTime;
                    SetAngularSize(dot.transform, dotSizeDegrees * Mathf.Lerp(3f, 1f, t / settleSeconds));
                    yield return null;
                }
                SetAngularSize(dot.transform, dotSizeDegrees);

                // Record: only FRESH samples (a new timestamp), so a blink can't add copies of an old reading.
                var measured = new List<Vector2>();
                var truth = new List<Vector2>();
                double lastStamp = gaze.Timestamp;
                t = 0f;
                while (t < recordSeconds)
                {
                    t += Time.deltaTime;
                    yield return null;
                    if (!gaze.HasGaze || gaze.Timestamp == lastStamp) continue;
                    lastStamp = gaze.Timestamp;

                    measured.Add(GazeCorrection.ToAngles(gaze.TrackerDirectionHead));
                    // True direction from the eyes' actual origin to the dot (not from the camera center).
                    truth.Add(GazeCorrection.ToAngles(dotHead - gaze.TrackerOriginHead));
                }

                stats.Add(Summarize(nominal, measured, truth));
            }
            Destroy(dot);

            if (gaze.IsEyeTracking != eyes)
            {
                Debug.LogWarning("[Calibration] The gaze source changed mid-check. Results thrown away, press C to retry.");
                Restore(filterWasOn, dotWasOn);
                IsRunning = false;
                yield break;
            }

            CalibrationResult result = Analyze(stats, eyes);
            LastResult = result;

            if (result.fitOk)
            {
                result.correction.participant = participantCode;
                result.correction.savedAt = result.timestamp;
                gaze.Correction.CopyFrom(result.correction);
                gaze.applyCorrection = true;
                SaveCorrection(result.correction);
                StatusHud.SetLine("calibration", null); // the fresh result shows on the panel instead
            }

            string path = Save(result);
            Debug.Log(result.Summary() + (path != null ? $"\nSaved to {path}" : ""));

            // Results picture: white = where the dot was, red = what the tracker said, green = after correction.
            ShowResults(result, cam.transform);
            _closeResults = false;
            float shown = 0f;
            while (shown < resultsSeconds && !_closeResults)
            {
                shown += Time.deltaTime;
                yield return null;
            }
            ClearVisuals();

            Restore(filterWasOn, dotWasOn);
            IsRunning = false;
        }

        // 3 x 3 grid. Center first, so the first reading is the easiest one.
        List<Vector2> BuildGrid()
        {
            float h = horizontalExtentDegrees, v = verticalExtentDegrees;
            return new List<Vector2>
            {
                new Vector2(0f, 0f),
                new Vector2(-h, v), new Vector2(0f, v), new Vector2(h, v),
                new Vector2(-h, 0f),                    new Vector2(h, 0f),
                new Vector2(-h, -v), new Vector2(0f, -v), new Vector2(h, -v),
            };
        }

        DotStats Summarize(Vector2 nominal, List<Vector2> measured, List<Vector2> truth)
        {
            var s = new DotStats { nominal = nominal, rawSamples = measured.Count };
            if (measured.Count == 0) return s;

            // Drop glances away: samples far from the median reading for this dot.
            Vector2 median = Median(measured);
            var keptM = new List<Vector2>();
            var keptT = new List<Vector2>();
            for (int i = 0; i < measured.Count; i++)
            {
                if ((measured[i] - median).magnitude <= glanceAwayDegrees)
                {
                    keptM.Add(measured[i]);
                    keptT.Add(truth[i]);
                }
            }
            s.samples = keptM.Count;
            if (s.samples < minSamplesPerDot) return s;

            s.used = true;
            s.measuredMean = Mean(keptM);
            s.trueAngles = Mean(keptT);
            s.bias = s.measuredMean - s.trueAngles;
            s.accuracyDeg = s.bias.magnitude;

            // Noise (precision): root-mean-square distance of the samples from their own average.
            float sum = 0f;
            foreach (Vector2 m in keptM) sum += (m - s.measuredMean).sqrMagnitude;
            s.precisionDeg = Mathf.Sqrt(sum / keptM.Count);
            return s;
        }

        CalibrationResult Analyze(List<DotStats> stats, bool eyes)
        {
            var r = new CalibrationResult
            {
                participant = participantCode,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                eyes = eyes,
                dots = stats,
                correction = new GazeCorrection(),
            };

            var usedMeasured = new List<Vector2>();
            var usedTruth = new List<Vector2>();
            foreach (DotStats s in stats)
            {
                if (!s.used) continue;
                usedMeasured.Add(s.measuredMean);
                usedTruth.Add(s.trueAngles);
            }
            r.usedDots = usedMeasured.Count;

            if (r.usedDots < minDotsForFit)
            {
                r.fitNote = $"only {r.usedDots} of {stats.Count} dots had enough good samples (need {minDotsForFit})";
            }
            else if (r.correction.TryFit(usedMeasured, usedTruth, eyes, out string reason))
            {
                r.fitOk = true;
                r.fitNote = "ok";
            }
            else
            {
                r.fitNote = reason;
            }

            // Averages, before and after correction. "After" is measured on the same data the fit learned
            // from, so it's optimistic. A second run with the correction on is the honest check.
            float accBefore = 0f, accAfter = 0f, precAll = 0f, precEdges = 0f;
            int edges = 0;
            foreach (DotStats s in stats)
            {
                if (!s.used) continue;
                s.correctedMean = r.fitOk ? r.correction.ApplyAngles(s.measuredMean) : s.measuredMean;
                s.correctedAccuracyDeg = (s.correctedMean - s.trueAngles).magnitude;
                accBefore += s.accuracyDeg;
                accAfter += s.correctedAccuracyDeg;
                precAll += s.precisionDeg;
                if (s.nominal == Vector2.zero) r.centerPrecisionDeg = s.precisionDeg;
                else { precEdges += s.precisionDeg; edges++; }
            }
            if (r.usedDots > 0)
            {
                r.meanAccuracyBeforeDeg = accBefore / r.usedDots;
                r.meanAccuracyAfterDeg = accAfter / r.usedDots;
                r.meanPrecisionDeg = precAll / r.usedDots;
            }
            if (edges > 0) r.outerPrecisionDeg = precEdges / edges;
            return r;
        }

        void ShowResults(CalibrationResult r, Transform head)
        {
            foreach (DotStats s in r.dots)
            {
                if (!s.used) continue;
                AddResultDot("Result: dot", Color.white, head, s.trueAngles);
                AddResultDot("Result: tracker", new Color(1f, 0.3f, 0.2f), head, s.measuredMean);
                if (r.fitOk) AddResultDot("Result: corrected", new Color(0.1f, 0.9f, 0.4f), head, s.correctedMean);
            }
        }

        void AddResultDot(string name, Color color, Transform head, Vector2 angles)
        {
            GameObject g = MakeSphere(name, color, head);
            g.transform.localPosition = GazeCorrection.FromAngles(angles) * dotDistance;
            SetAngularSize(g.transform, dotSizeDegrees * 0.8f);
            _visuals.Add(g);
        }

        void Restore(bool filterWasOn, bool dotWasOn)
        {
            if (hemianopia != null) hemianopia.filterEnabled = filterWasOn;
            if (gazeDot != null) gazeDot.enabled = dotWasOn;
        }

        void ClearVisuals()
        {
            foreach (GameObject g in _visuals) if (g != null) Destroy(g);
            _visuals.Clear();
        }

        void OnDisable()
        {
            ClearVisuals();
        }

        // ---------- Saving ----------

        // In the editor: <project>/CalibrationLogs (next to Assets, ignored by git).
        // On the headset: the app's private storage.
        public static string LogFolder()
        {
#if UNITY_EDITOR
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "CalibrationLogs"));
#else
            return Path.Combine(Application.persistentDataPath, "CalibrationLogs");
#endif
        }

        static string Save(CalibrationResult r)
        {
            try
            {
                string folder = LogFolder();
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(folder, $"calibration_{r.participant}_{stamp}.csv");

                var c = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine($"# participant={r.participant}, time={r.timestamp}, source={(r.eyes ? "eyes" : "mouse")}");
                sb.AppendLine($"# fit_ok={r.fitOk}, note={r.fitNote}");
                var k = r.correction;
                sb.AppendLine(string.Format(c, "# a0={0:0.0000}, a1={1:0.0000}, a2={2:0.000}, b0={3:0.0000}, b1={4:0.0000}, b2={5:0.000}", k.a0, k.a1, k.a2, k.b0, k.b1, k.b2));
                sb.AppendLine("dot_az,dot_el,used,samples,raw_samples,true_az,true_el,tracker_az,tracker_el,bias_az,bias_el,accuracy_deg,precision_deg,corrected_accuracy_deg");
                foreach (DotStats s in r.dots)
                {
                    sb.AppendLine(string.Format(c,
                        "{0:0.0},{1:0.0},{2},{3},{4},{5:0.000},{6:0.000},{7:0.000},{8:0.000},{9:0.000},{10:0.000},{11:0.000},{12:0.000},{13:0.000}",
                        s.nominal.x, s.nominal.y, s.used ? 1 : 0, s.samples, s.rawSamples,
                        s.trueAngles.x, s.trueAngles.y, s.measuredMean.x, s.measuredMean.y,
                        s.bias.x, s.bias.y, s.accuracyDeg, s.precisionDeg, s.correctedAccuracyDeg));
                }
                File.WriteAllText(path, sb.ToString());
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Calibration] Couldn't save the results: {e.Message}");
                return null;
            }
        }

        // ---------- Saved correction ----------

        string CorrectionPath()
        {
            return Path.Combine(LogFolder(), $"correction_{participantCode}.json");
        }

        void SaveCorrection(GazeCorrection correction)
        {
            try
            {
                Directory.CreateDirectory(LogFolder());
                File.WriteAllText(CorrectionPath(), JsonUtility.ToJson(correction, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Calibration] Couldn't save the correction: {e.Message}");
            }
        }

        void LoadSavedCorrection()
        {
            if (gaze == null) return;
            string path = CorrectionPath();
            if (!File.Exists(path)) return;
            try
            {
                var loaded = JsonUtility.FromJson<GazeCorrection>(File.ReadAllText(path));
                if (loaded == null || !loaded.IsValid) return;
                gaze.Correction.CopyFrom(loaded);
                gaze.applyCorrection = true;
                string message = $"Using saved calibration for {loaded.participant} from {loaded.savedAt}. Re-run (C) if the headset moved.";
                Debug.Log($"[Calibration] {message}");
                StatusHud.SetLine("calibration", message);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Calibration] Couldn't load the saved correction: {e.Message}");
            }
        }

        // ---------- Small helpers ----------

        static GameObject MakeSphere(string name, Color color, Transform parent)
        {
            GameObject g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            g.name = name;
            Destroy(g.GetComponent<Collider>()); // never block the gaze raycast
            g.GetComponent<Renderer>().material.color = color;
            g.transform.SetParent(parent, false);
            return g;
        }

        void SetAngularSize(Transform t, float degrees)
        {
            float d = t.localPosition.magnitude > 0.01f ? t.localPosition.magnitude : dotDistance;
            t.localScale = Vector3.one * (2f * d * Mathf.Tan(0.5f * degrees * Mathf.Deg2Rad));
        }

        static Vector2 Mean(List<Vector2> values)
        {
            Vector2 sum = Vector2.zero;
            foreach (Vector2 v in values) sum += v;
            return sum / values.Count;
        }

        static Vector2 Median(List<Vector2> values)
        {
            var xs = new List<float>(values.Count);
            var ys = new List<float>(values.Count);
            foreach (Vector2 v in values) { xs.Add(v.x); ys.Add(v.y); }
            xs.Sort();
            ys.Sort();
            int mid = values.Count / 2;
            return values.Count % 2 == 1
                ? new Vector2(xs[mid], ys[mid])
                : new Vector2(0.5f * (xs[mid - 1] + xs[mid]), 0.5f * (ys[mid - 1] + ys[mid]));
        }

        static bool CalibratePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame) return true;
#endif
            return OVRInput.GetDown(OVRInput.RawButton.LThumbstick, OVRInput.Controller.Touch);
        }

        static bool ToggleCorrectionPressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame) return true;
#endif
            return OVRInput.GetDown(OVRInput.RawButton.RThumbstick, OVRInput.Controller.Touch);
        }
    }

    /// <summary>What we learned from one dot. Angles are (azimuth, elevation) in degrees, relative to the head.</summary>
    public class DotStats
    {
        public Vector2 nominal;              // where the dot was meant to be (grid position)
        public bool used;                    // false if too few good samples
        public int samples;                  // samples kept after dropping glances away
        public int rawSamples;               // samples recorded
        public Vector2 trueAngles;           // where the dot really was, seen from the eyes
        public Vector2 measuredMean;         // where the tracker said you looked, on average
        public Vector2 bias;                 // measuredMean - trueAngles
        public float accuracyDeg;            // size of the bias
        public float precisionDeg;           // noise: RMS wobble around measuredMean
        public Vector2 correctedMean;        // measuredMean after the correction
        public float correctedAccuracyDeg;   // remaining error after the correction
    }

    /// <summary>One whole calibration check.</summary>
    public class CalibrationResult
    {
        public string participant;
        public string timestamp;
        public bool eyes;
        public List<DotStats> dots;
        public int usedDots;
        public bool fitOk;
        public string fitNote;
        public GazeCorrection correction;
        public float meanAccuracyBeforeDeg;
        public float meanAccuracyAfterDeg;
        public float meanPrecisionDeg;
        public float centerPrecisionDeg;
        public float outerPrecisionDeg;

        public string Summary()
        {
            var c = CultureInfo.InvariantCulture;
            string src = eyes ? "eyes" : "MOUSE";
            string fit = fitOk
                ? string.Format(c, "Error {0:0.00}° before, {1:0.00}° after correction (same data, so optimistic).", meanAccuracyBeforeDeg, meanAccuracyAfterDeg)
                : $"No correction fitted: {fitNote}.";
            string noise = string.Format(c, "Noise {0:0.00}° at center, {1:0.00}° at the outer dots.", centerPrecisionDeg, outerPrecisionDeg);
            return $"[Calibration] {participant} ({src}): {usedDots}/{dots.Count} dots usable. {fit} {noise}";
        }
    }
}
