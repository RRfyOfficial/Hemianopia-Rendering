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
    /// How fast does the tracker follow a real eye jump (saccade)?
    ///
    /// Head still, a dot that moves WITH the head jumps 20° at random moments. You follow it as fast as you can.
    /// Real eyes finish a 20° jump in well under a tenth of a second, at speeds of several hundred degrees
    /// per second. So if the tracker's reading takes much longer, or moves much slower, the headset is
    /// smoothing gaze. That matters a lot for the simulation: the hemianopia boundary would lag right after
    /// every eye jump, which is exactly when someone could peek into the blind half.
    ///
    /// It also helps explain the ~2° lean seen during head turns: a fast tracker points to the eyes slipping,
    /// a slow one points to the headset smoothing.
    ///
    /// Timing uses the eye samples' own timestamps. Their clock has an unknown starting point over Link,
    /// but the gaps between samples are real durations.
    ///
    /// Controls in Play mode: J or the right grip starts it. Results go to the status panel, the console,
    /// and a CSV in CalibrationLogs/. Write-up: Docs/SACCADE_TEST.md
    /// </summary>
    [DisallowMultipleComponent]
    public class SaccadeTest : MonoBehaviour
    {
        /// <summary>True while a saccade test is running (so other tests don't start on top of it).</summary>
        public static bool AnyRunning { get; private set; }

        [Header("Jumps")]
        [Tooltip("Size of each jump, in degrees.")]
        public float jumpDegrees = 20f;

        [Tooltip("How many jumps. Half go out to a side, half come back to the center.")]
        public int jumps = 16;

        [Tooltip("Shortest wait between jumps, in seconds. The wait is random so you can't jump early.")]
        public float minHoldSeconds = 1.0f;

        [Tooltip("Longest wait between jumps, in seconds.")]
        public float maxHoldSeconds = 1.6f;

        public float countdownSeconds = 3f;
        public int randomSeed = 7;

        [Header("Dot")]
        public float dotDistance = 2f;
        public float dotSizeDegrees = 0.6f;

        [Header("Hooks (found automatically if left empty)")]
        public GazeProvider gaze;
        public HemianopiaController hemianopia;
        public GazeDot gazeDot;
        public CalibrationRunner calibration;

        public bool IsRunning { get; private set; }
        public SaccadeResult LastResult { get; private set; }

        struct Sample
        {
            public int jump;        // which jump this belongs to (-1 = before the first jump)
            public double eyeTime;  // the sample's own timestamp (eye clock), seconds
            public double arrival;  // when it arrived here (Unity clock), seconds
            public float az;        // eye-in-head azimuth, degrees (calibration-corrected)
            public float el;
            public float targetAz;  // where the dot was
        }

        readonly List<Sample> _samples = new List<Sample>();
        readonly List<double> _jumpArrival = new List<double>(); // Unity clock when each jump happened
        readonly List<float> _jumpTarget = new List<float>();
        int _jumpIndex = -1;
        float _targetAz;
        double _lastStamp = double.NaN;

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
            if (HeadMotionTest.AnyRunning) return;
            if (StartPressed()) StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            Camera cam = gaze.viewCamera != null ? gaze.viewCamera : Camera.main;
            if (cam == null) yield break;

            IsRunning = true;
            AnyRunning = true;
            _samples.Clear();
            _jumpArrival.Clear();
            _jumpTarget.Clear();
            _jumpIndex = -1;
            _targetAz = 0f;
            _lastStamp = double.NaN;

            bool filterWasOn = hemianopia != null && hemianopia.filterEnabled;
            if (hemianopia != null) hemianopia.filterEnabled = false;
            bool dotWasOn = gazeDot != null && gazeDot.enabled;
            if (gazeDot != null) gazeDot.enabled = false;

            // A dot that moves WITH the head, so only the eyes matter.
            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "Saccade test dot";
            Destroy(dot.GetComponent<Collider>());
            dot.GetComponent<Renderer>().material.color = Color.white;
            dot.transform.SetParent(cam.transform, false);
            dot.transform.localPosition = GazeCorrection.FromAngles(Vector2.zero) * dotDistance;
            dot.transform.localScale = Vector3.one * (2f * dotDistance * Mathf.Tan(0.5f * dotSizeDegrees * Mathf.Deg2Rad));

            for (float c = countdownSeconds; c > 0f; c -= Time.deltaTime)
            {
                StatusHud.SetBanner($"Saccade test: keep your head still and follow the dot as FAST as you can. Starting in {Mathf.CeilToInt(c)}");
                yield return null;
            }
            StatusHud.SetBanner("Head still. Follow the dot as fast as you can.");

            GazeProvider.GazeUpdated += Record;
            var random = new System.Random(randomSeed);

            // A short hold at the center first, so the first jump has a starting point.
            yield return new WaitForSeconds(minHoldSeconds);

            for (int i = 0; i < jumps; i++)
            {
                // Out to a random side from the center, then back to the center.
                float next = Mathf.Approximately(_targetAz, 0f)
                    ? (random.Next(2) == 0 ? -jumpDegrees : jumpDegrees)
                    : 0f;

                _targetAz = next;
                dot.transform.localPosition = GazeCorrection.FromAngles(new Vector2(next, 0f)) * dotDistance;
                _jumpIndex = i;
                _jumpArrival.Add(Time.realtimeSinceStartupAsDouble);
                _jumpTarget.Add(next);

                float hold = minHoldSeconds + (float)random.NextDouble() * (maxHoldSeconds - minHoldSeconds);
                StatusHud.SetBanner($"Head still. Follow the dot as fast as you can. ({jumps - i} jumps left)");
                yield return new WaitForSeconds(hold);
            }

            GazeProvider.GazeUpdated -= Record;
            StatusHud.SetBanner(null);
            Destroy(dot);

            SaccadeResult r = Analyze();
            LastResult = r;
            string path = Save(r);
            Debug.Log(r.Summary() + "\n" + r.Reading() + (path != null ? $"\nSaved to {path}" : ""));
            StatusHud.SetLine("saccade", r.ShortSummary());

            if (hemianopia != null) hemianopia.filterEnabled = filterWasOn;
            if (gazeDot != null) gazeDot.enabled = dotWasOn;
            IsRunning = false;
            AnyRunning = false;
        }

        // Keeps only FRESH tracker samples (a new timestamp), each with its own capture time.
        void Record(GazeProvider g)
        {
            if (!g.HasGaze || !g.IsEyeTracking) return;
            if (g.Timestamp == _lastStamp) return;
            _lastStamp = g.Timestamp;

            Vector2 angles = GazeCorrection.ToAngles(g.CorrectedDirectionHead);
            _samples.Add(new Sample
            {
                jump = _jumpIndex,
                eyeTime = g.Timestamp,
                arrival = Time.realtimeSinceStartupAsDouble,
                az = angles.x,
                el = angles.y,
                targetAz = _targetAz,
            });
        }

        SaccadeResult Analyze()
        {
            var r = new SaccadeResult
            {
                participant = calibration != null ? calibration.participantCode : "P00",
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                eyes = gaze.IsEyeTracking,
                jumpDegrees = jumpDegrees,
            };

            // Typical gap between fresh samples (sets the timing resolution).
            var gaps = new List<double>();
            for (int i = 1; i < _samples.Count; i++)
            {
                double d = _samples[i].eyeTime - _samples[i - 1].eyeTime;
                if (d > 0 && d < 0.1) gaps.Add(d);
            }
            r.sampleGapMs = gaps.Count > 0 ? (float)(Median(gaps) * 1000.0) : 0f;

            for (int j = 0; j < _jumpTarget.Count; j++)
            {
                var jr = new SaccadeResult.Jump { index = j, toAz = _jumpTarget[j] };
                float fromAz = j == 0 ? 0f : _jumpTarget[j - 1];
                jr.fromAz = fromAz;
                float sign = Mathf.Sign(jr.toAz - fromAz);

                // Baseline: where the eye was during the last 150 ms before the jump.
                var before = new List<float>();
                var after = new List<Sample>();
                foreach (Sample s in _samples)
                {
                    if (s.jump == j - 1) before.Add(s.az);
                    if (s.jump == j) after.Add(s);
                }
                if (before.Count < 3 || after.Count < 10) { r.jumps.Add(jr); continue; }
                float baseAz = Median(before.GetRange(Mathf.Max(0, before.Count - 8), Mathf.Min(8, before.Count)));

                // Final: where the eye settled, from 0.5 s after the jump to the end of the hold.
                double t0 = after[0].eyeTime;
                var settled = new List<float>();
                foreach (Sample s in after) if (s.eyeTime - t0 >= 0.5) settled.Add(s.az);
                if (settled.Count < 3) { r.jumps.Add(jr); continue; }
                float finalAz = Median(settled);

                float amplitude = (finalAz - baseAz) * sign;
                jr.amplitudeDeg = amplitude;
                if (amplitude < 0.5f * jumpDegrees) { r.jumps.Add(jr); continue; } // didn't really follow

                // Progress toward the target, in degrees, for each sample.
                double t10 = double.NaN, t90 = double.NaN;
                int i10 = -1, i90 = -1;
                for (int i = 0; i < after.Count; i++)
                {
                    float p = (after[i].az - baseAz) * sign;
                    float pPrev = i > 0 ? (after[i - 1].az - baseAz) * sign : 0f;
                    if (double.IsNaN(t10) && p >= 0.1f * amplitude)
                    {
                        t10 = Interpolate(after, i, pPrev, p, 0.1f * amplitude);
                        i10 = i;
                    }
                    if (!double.IsNaN(t10) && double.IsNaN(t90) && p >= 0.9f * amplitude)
                    {
                        t90 = Interpolate(after, i, pPrev, p, 0.9f * amplitude);
                        i90 = i;
                        break;
                    }
                }
                if (double.IsNaN(t10) || double.IsNaN(t90)) { r.jumps.Add(jr); continue; }

                jr.valid = true;
                jr.riseMs = (float)((t90 - t10) * 1000.0);

                // Biggest single-sample step as a share of the whole jump. Real eyes need 60 to 80 ms
                // for 20°, which spreads over 3 or 4 samples. A share near 100% means the reading SNAPPED.
                float biggest = 0f;
                for (int i = 1; i < after.Count; i++)
                    biggest = Mathf.Max(biggest, (after[i].az - after[i - 1].az) * sign);
                jr.biggestStepShare = amplitude > 0f ? biggest / amplitude : 0f;

                // Peak speed between consecutive samples around the jump.
                float peak = 0f;
                for (int i = Mathf.Max(1, i10 - 1); i <= Mathf.Min(after.Count - 1, i90 + 1); i++)
                {
                    double dt = after[i].eyeTime - after[i - 1].eyeTime;
                    if (dt <= 0) continue;
                    float v = (float)((after[i].az - after[i - 1].az) * sign / dt);
                    if (v > peak) peak = v;
                }
                jr.peakSpeedDps = peak;

                // Overshoot: farthest past the final position within 200 ms of reaching 90%.
                float over = 0f;
                for (int i = i90; i < after.Count && after[i].eyeTime - t90 <= 0.2; i++)
                    over = Mathf.Max(over, (after[i].az - baseAz) * sign - amplitude);
                jr.overshootDeg = over;

                // Reaction: from the dot jumping to the tracker starting to move, on the Unity clock.
                // Includes the person's reaction time AND the pipeline delay.
                jr.reactionMs = (float)((after[i10].arrival - _jumpArrival[j]) * 1000.0);

                r.jumps.Add(jr);
            }

            var rise = new List<double>();
            var peakSpeed = new List<double>();
            var overshoot = new List<double>();
            var reaction = new List<double>();
            var stepShare = new List<double>();
            foreach (SaccadeResult.Jump jr in r.jumps)
            {
                if (!jr.valid) continue;
                rise.Add(jr.riseMs); peakSpeed.Add(jr.peakSpeedDps); overshoot.Add(jr.overshootDeg); reaction.Add(jr.reactionMs);
                stepShare.Add(jr.biggestStepShare);
            }
            r.validJumps = rise.Count;
            if (r.validJumps > 0)
            {
                r.medianRiseMs = (float)Median(rise);
                r.medianPeakSpeedDps = (float)Median(peakSpeed);
                r.medianOvershootDeg = (float)Median(overshoot);
                r.medianReactionMs = (float)Median(reaction);
                r.medianBiggestStepShare = (float)Median(stepShare);
            }
            return r;
        }

        // Linear interpolation of the moment the progress crossed a level, between samples i-1 and i.
        static double Interpolate(List<Sample> s, int i, float pPrev, float p, float level)
        {
            if (i == 0 || p <= pPrev) return s[i].eyeTime;
            double a = (level - pPrev) / (p - pPrev);
            return s[i - 1].eyeTime + a * (s[i].eyeTime - s[i - 1].eyeTime);
        }

        string Save(SaccadeResult r)
        {
            try
            {
                string folder = CalibrationRunner.LogFolder();
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(folder, $"saccade_{r.participant}_{stamp}.csv");
                var c = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine($"# participant={r.participant}, time={r.timestamp}");
                sb.AppendLine("# " + r.Summary());
                foreach (SaccadeResult.Jump j in r.jumps)
                    sb.AppendLine(string.Format(c, "# jump {0}: {1:0} to {2:0} deg, valid={3}, amplitude {4:0.0}, rise {5:0} ms, peak {6:0} deg/s, overshoot {7:0.0}, reaction {8:0} ms",
                        j.index, j.fromAz, j.toAz, j.valid, j.amplitudeDeg, j.riseMs, j.peakSpeedDps, j.overshootDeg, j.reactionMs));
                sb.AppendLine("jump,eye_time_s,arrival_s,az_deg,el_deg,target_az_deg");
                double e0 = _samples.Count > 0 ? _samples[0].eyeTime : 0, a0 = _samples.Count > 0 ? _samples[0].arrival : 0;
                foreach (Sample s in _samples)
                    sb.AppendLine(string.Format(c, "{0},{1:0.00000},{2:0.00000},{3:0.000},{4:0.000},{5:0.0}",
                        s.jump, s.eyeTime - e0, s.arrival - a0, s.az, s.el, s.targetAz));
                File.WriteAllText(path, sb.ToString());
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Saccade] Couldn't save the results: {e.Message}");
                return null;
            }
        }

        static double Median(List<double> v)
        {
            var s = new List<double>(v);
            s.Sort();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : 0.5 * (s[m - 1] + s[m]);
        }

        static float Median(List<float> v)
        {
            var s = new List<float>(v);
            s.Sort();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : 0.5f * (s[m - 1] + s[m]);
        }

        static bool StartPressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.jKey.wasPressedThisFrame) return true;
#endif
            return OVRInput.GetDown(OVRInput.RawButton.RHandTrigger, OVRInput.Controller.Touch);
        }
    }

    /// <summary>The result of one saccade test.</summary>
    public class SaccadeResult
    {
        public class Jump
        {
            public int index;
            public float fromAz, toAz;
            public bool valid;
            public float amplitudeDeg;   // how far the tracker reading actually moved
            public float riseMs;         // 10% to 90% of the way, on the eye clock
            public float peakSpeedDps;   // fastest speed between two samples
            public float overshootDeg;   // farthest past the final position
            public float reactionMs;     // dot jump to the reading starting to move (person + pipeline)
            public float biggestStepShare; // biggest single-sample step / whole jump (near 1 = snapped)
        }

        public string participant;
        public string timestamp;
        public bool eyes;
        public float jumpDegrees;
        public float sampleGapMs;
        public List<Jump> jumps = new List<Jump>();
        public int validJumps;
        public float medianRiseMs;
        public float medianPeakSpeedDps;
        public float medianOvershootDeg;
        public float medianReactionMs;
        public float medianBiggestStepShare;

        public string Summary()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Format(c,
                "[Saccade] {0}: {1}/{2} jumps usable. Rise (10 to 90%) {3:0} ms, peak speed {4:0} °/s, biggest single step {8:0}% of the jump, overshoot {5:0.0}°, reaction {6:0} ms. Samples every {7:0} ms.",
                participant, validJumps, jumps.Count, medianRiseMs, medianPeakSpeedDps, medianOvershootDeg, medianReactionMs, sampleGapMs, medianBiggestStepShare * 100f);
        }

        public string ShortSummary()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Format(c, "Saccades: rise {0:0} ms, peak {1:0} °/s ({2} jumps)", medianRiseMs, medianPeakSpeedDps, validJumps);
        }

        /// <summary>A plain-language reading. Real 20° saccades: roughly 40 to 50 ms for the 10 to 90% part, several hundred °/s.</summary>
        public string Reading()
        {
            var c = CultureInfo.InvariantCulture;
            if (validJumps < 5) return "Reading: too few usable jumps. Keep your head still, follow every jump, and try again.";
            if (medianBiggestStepShare >= 0.8f)
                return string.Format(c, "Reading: the reading SNAPS. About {0:0}% of each jump happens between two samples, faster than real eyes can move. The headset holds gaze steady during fixations and jumps on saccades (its own hold-or-snap filter). Great for saccades, but during head turns it holds the eye reading while the eyes really counter-rotate, which explains the ~2° lean toward the turn.", medianBiggestStepShare * 100f);
            if (medianRiseMs <= 70f && medianPeakSpeedDps >= 300f)
                return string.Format(c, "Reading: the tracker follows jumps about as fast as real eyes. It isn't smoothing much (timing resolution is about {0:0} ms), so the 2° lean during head turns is more likely your eyes slipping.", sampleGapMs);
            if (medianRiseMs >= 100f || medianPeakSpeedDps < 200f)
                return string.Format(c, "Reading: the tracker is much slower than real eyes, so the headset smooths gaze. The hemianopia boundary lags after every eye jump by roughly {0:0} ms more than it should.", Mathf.Max(0f, medianRiseMs - 45f));
            return "Reading: in between. Some smoothing, but not a lot. Run it again to firm up the numbers.";
        }
    }
}
