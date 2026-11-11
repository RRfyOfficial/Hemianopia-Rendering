using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Hemianopia.Calibration;
using Hemianopia.Gaze;
using Hemianopia.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Logging
{
    /// <summary>
    /// Layer 6 (minimal): one shared logger that every test and trial writes through.
    ///
    /// While recording, it writes ONE ROW PER FRAME to a CSV in CalibrationLogs/ (the same folder the
    /// calibration and diagnostic tests use, which is ignored by git, so participant data never gets committed).
    ///
    /// Each row holds: timing, head pose, gaze (tracker and corrected, in degrees relative to the head),
    /// the 3D fixation point, fixation/anchor state, head speed, filter state, and whatever the current
    /// trial wants to say (trial number, condition, target position, target visible, event marker).
    ///
    /// How a test uses it:
    ///     var log = FindAnyObjectByType&lt;TrialLogger&gt;();
    ///     log.StartSession();                         // or press L
    ///     log.SetTrial(12, "smart_cue");              // at the start of each trial
    ///     log.SetTarget(block.position, visible);     // every frame (or whenever it changes)
    ///     log.Mark("response_click");                 // one-off event, lands on the next row
    ///     log.StopSession();                          // or press L again
    ///
    /// Reading the files: the first lines start with "#" (metadata). Skip them when loading:
    ///     R:       read.csv(file, comment.char = "#")
    ///     Python:  pd.read_csv(file, comment="#")
    ///     MATLAB:  readtable(file, 'CommentStyle', '#')
    ///
    /// Notes:
    ///  - "new_sample" is 1 when the eye tracker delivered a fresh reading this frame. The tracker runs
    ///    slower than the display, so some frames reuse the previous reading. Use new_sample == 1 rows
    ///    when you need independent samples (jitter, saccade detection).
    ///  - Data loss. The eye API this project uses (OVRPlugin eye gaze interaction state) reports whether
    ///    each reading is valid, not a confidence score, so there is no confidence column. Instead:
    ///      raw_valid     1 = the tracker's reading was valid this frame, 0 = invalid (a blink or dropped
    ///                    frame), -1 = not using eyes (mouse mode). Read straight from OVRPlugin.
    ///      since_new_ms  milliseconds since the last fresh eye reading. Normal is roughly 14 to 30 ms;
    ///                    long stretches mean the tracker was not delivering.
    ///    has_gaze is NOT a data-loss flag: GazeProvider keeps the last gaze through blinks on purpose,
    ///    so has_gaze stays 1 after the first reading.
    ///  - Angles: azimuth is left/right, elevation is up/down, both in degrees from straight ahead
    ///    in the head's frame (same convention as GazeCorrection.ToAngles).
    /// </summary>
    public class TrialLogger : MonoBehaviour
    {
        [Header("References (left empty, they are found automatically)")]
        public GazeProvider gaze;
        public HemianopiaController filter;
        public CalibrationRunner calibration;

        [Header("Behaviour")]
        [Tooltip("Start recording as soon as Play starts. Normally off: start from a test or with the L key.")]
        public bool autoStartOnPlay = false;

        [Tooltip("Optional short label added to the file name, e.g. \"pursuit\" or \"leakcheck\".")]
        public string sessionLabel = "";

        [Tooltip("How often the file is flushed to disk, in seconds. Row writing itself is buffered.")]
        public float flushSeconds = 2f;

        [Tooltip("Also start/stop recording by pushing the RIGHT thumbstick up (the one controller input nothing else uses). The L key always works.")]
        public bool useControllerToggle = true;

        public bool IsRecording => _writer != null;
        public string CurrentPath { get; private set; }
        public int RowCount { get; private set; }

        // Trial state, set by whatever test is running.
        int _trial = -1;
        string _condition = "";
        Vector3 _target;
        bool _targetValid;
        bool _targetVisible;
        string _pendingEvent = "";

        // Writer state.
        StreamWriter _writer;
        readonly StringBuilder _sb = new StringBuilder(512);
        double _t0;
        double _lastFrameTime;
        double _lastEyeStamp = double.NaN;
        double _lastNewSampleTime;
        double _lastFlush;
        string _participant = "P00";
        Camera _cam;

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        const string Header =
            "frame,t_s,dt_ms,eye_time,new_sample,since_new_ms,raw_valid,has_gaze,is_eyes," +
            "head_x,head_y,head_z,head_qx,head_qy,head_qz,head_qw," +
            "trk_az,trk_el,cor_az,cor_el," +
            "gaze_dir_x,gaze_dir_y,gaze_dir_z," +
            "fix_x,fix_y,fix_z,fix_dist,fix_hit," +
            "is_fixating,fix_dur_s,anchor_active,anchor_holding,head_speed_dps,sample_age_ms," +
            "filter_on,blind_side,correction_on," +
            "trial,condition,tgt_x,tgt_y,tgt_z,tgt_visible,event";

        // ---------- Public API ----------

        /// <summary>Set the trial number and condition. Both appear on every row until changed.</summary>
        public void SetTrial(int trialNumber, string condition)
        {
            _trial = trialNumber;
            _condition = Clean(condition);
        }

        /// <summary>Set the target's world position and whether the participant could see it right now.</summary>
        public void SetTarget(Vector3 worldPosition, bool visible)
        {
            _target = worldPosition;
            _targetValid = true;
            _targetVisible = visible;
        }

        /// <summary>No target on screen (rows get blank target columns).</summary>
        public void ClearTarget()
        {
            _targetValid = false;
            _targetVisible = false;
        }

        /// <summary>Label a one-off moment (response click, trial start, timeout). It lands on the next row.</summary>
        public void Mark(string label)
        {
            _pendingEvent = Clean(label);
        }

        public bool StartSession()
        {
            if (IsRecording) return true;
            if (gaze == null) gaze = FindAnyObjectByType<GazeProvider>();
            if (filter == null) filter = FindAnyObjectByType<HemianopiaController>();
            if (calibration == null) calibration = FindAnyObjectByType<CalibrationRunner>();
            if (gaze == null)
            {
                Debug.LogWarning("[TrialLogger] No GazeProvider in the scene, so there is nothing to record.");
                return false;
            }

            _participant = calibration != null ? calibration.participantCode : "P00";

            try
            {
                string folder = CalibrationRunner.LogFolder();
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", C);
                string label = string.IsNullOrWhiteSpace(sessionLabel) ? "" : "_" + Clean(sessionLabel);
                CurrentPath = Path.Combine(folder, $"trial_{_participant}{label}_{stamp}.csv");

                _writer = new StreamWriter(CurrentPath, false, new UTF8Encoding(false), 1 << 16);
                _writer.WriteLine($"# participant={_participant}, start={DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", C)}, label={Clean(sessionLabel)}");
                _writer.WriteLine($"# source={(gaze.IsEyeTracking ? "eyes" : "mouse")}, unity={Application.unityVersion}, platform={Application.platform}");
                _writer.WriteLine($"# anchor_fixations={gaze.anchorFixations}, apply_correction={gaze.applyCorrection}, time_match={gaze.timeMatchGaze}");
                _writer.WriteLine("# angles in degrees from straight ahead in the head frame (azimuth = left/right, elevation = up/down); positions in metres");
                _writer.WriteLine(Header);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrialLogger] Could not open the log file: " + e.Message);
                _writer = null;
                return false;
            }

            RowCount = 0;
            _t0 = Time.realtimeSinceStartupAsDouble;
            _lastFrameTime = _t0;
            _lastFlush = _t0;
            _lastEyeStamp = double.NaN;
            _lastNewSampleTime = _t0;
            GazeProvider.GazeUpdated += OnGaze;
            Debug.Log("[TrialLogger] Recording to " + CurrentPath);
            return true;
        }

        public void StopSession()
        {
            if (!IsRecording) return;
            GazeProvider.GazeUpdated -= OnGaze;
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrialLogger] Problem closing the log file: " + e.Message);
            }
            _writer = null;
            Debug.Log($"[TrialLogger] Saved {RowCount} rows to {CurrentPath}");
        }

        public void ToggleSession()
        {
            if (IsRecording) StopSession(); else StartSession();
        }

        // ---------- Unity ----------

        void Start()
        {
            if (autoStartOnPlay) StartSession();
        }

        void Update()
        {
            if (TogglePressed()) ToggleSession();

            if (IsRecording && Time.realtimeSinceStartupAsDouble - _lastFlush > flushSeconds)
            {
                _lastFlush = Time.realtimeSinceStartupAsDouble;
                try { _writer.Flush(); } catch { /* keep recording */ }
            }
        }

        // The headset can pause the app without a clean quit, so close the file in all of these.
        void OnApplicationPause(bool paused) { if (paused) StopSession(); }
        void OnApplicationQuit() { StopSession(); }
        void OnDisable() { StopSession(); }

        // ---------- Row writing ----------

        void OnGaze(GazeProvider g)
        {
            if (_writer == null) return;

            if (_cam == null) _cam = g.viewCamera != null ? g.viewCamera : Camera.main;

            double now = Time.realtimeSinceStartupAsDouble;
            double dtMs = (now - _lastFrameTime) * 1000.0;
            _lastFrameTime = now;

            bool newSample = g.HasGaze && g.Timestamp != _lastEyeStamp;
            if (g.HasGaze) _lastEyeStamp = g.Timestamp;
            if (newSample) _lastNewSampleTime = now;
            double sinceNewMs = (now - _lastNewSampleTime) * 1000.0;

            // The tracker's own validity flag for this frame (same call GazeProvider uses).
            // -1 = not using eyes (mouse mode).
            int rawValid = -1;
            if (g.IsEyeTracking)
            {
                OVRPlugin.EyeGazeInteractionState st = default;
                rawValid = OVRPlugin.GetEyeGazeInteractionState(OVRPlugin.Step.Render, -1, ref st) && st.IsValid ? 1 : 0;
            }

            Vector2 trk = GazeCorrection.ToAngles(g.TrackerDirectionHead);
            Vector2 cor = GazeCorrection.ToAngles(g.CorrectedDirectionHead);
            Ray filterRay = g.FilterGazeRay;
            Vector3 fix = g.FilterFixationPoint;

            Vector3 headPos = Vector3.zero;
            Quaternion headRot = Quaternion.identity;
            if (_cam != null)
            {
                headPos = _cam.transform.position;
                headRot = _cam.transform.rotation;
            }

            var sb = _sb;
            sb.Length = 0;

            sb.Append(Time.frameCount).Append(',');
            sb.Append((now - _t0).ToString("F4", C)).Append(',');
            sb.Append(dtMs.ToString("F2", C)).Append(',');
            sb.Append(g.HasGaze ? g.Timestamp.ToString("F6", C) : "").Append(',');
            sb.Append(newSample ? 1 : 0).Append(',');
            sb.Append(sinceNewMs.ToString("F1", C)).Append(',');
            sb.Append(rawValid).Append(',');
            sb.Append(g.HasGaze ? 1 : 0).Append(',');
            sb.Append(g.IsEyeTracking ? 1 : 0).Append(',');

            Vec(sb, headPos); sb.Append(',');
            sb.Append(headRot.x.ToString("F5", C)).Append(',');
            sb.Append(headRot.y.ToString("F5", C)).Append(',');
            sb.Append(headRot.z.ToString("F5", C)).Append(',');
            sb.Append(headRot.w.ToString("F5", C)).Append(',');

            sb.Append(trk.x.ToString("F3", C)).Append(',');
            sb.Append(trk.y.ToString("F3", C)).Append(',');
            sb.Append(cor.x.ToString("F3", C)).Append(',');
            sb.Append(cor.y.ToString("F3", C)).Append(',');

            Vec(sb, filterRay.direction); sb.Append(',');
            Vec(sb, fix); sb.Append(',');
            sb.Append(g.RawFixationDistance.ToString("F3", C)).Append(',');
            sb.Append(g.RawFixationHit ? 1 : 0).Append(',');

            sb.Append(g.IsFixating ? 1 : 0).Append(',');
            sb.Append(g.FixationDuration.ToString("F3", C)).Append(',');
            sb.Append(g.IsAnchorActive ? 1 : 0).Append(',');
            sb.Append(g.IsAnchorHolding ? 1 : 0).Append(',');
            sb.Append(g.HeadSpeedDps.ToString("F2", C)).Append(',');
            sb.Append(g.SampleAgeMs.ToString("F2", C)).Append(',');

            sb.Append(filter != null && filter.filterEnabled ? 1 : 0).Append(',');
            sb.Append(filter != null ? filter.blindSide.ToString() : "").Append(',');
            sb.Append(g.IsCorrectionActive ? 1 : 0).Append(',');

            sb.Append(_trial).Append(',');
            sb.Append(_condition).Append(',');
            if (_targetValid) Vec(sb, _target); else sb.Append(",,");
            sb.Append(',');
            sb.Append(_targetValid ? (_targetVisible ? "1" : "0") : "").Append(',');
            sb.Append(_pendingEvent);
            _pendingEvent = "";

            try
            {
                _writer.WriteLine(sb.ToString());
                RowCount++;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TrialLogger] Write failed, stopping: " + e.Message);
                StopSession();
            }
        }

        static void Vec(StringBuilder sb, Vector3 v)
        {
            sb.Append(v.x.ToString("F4", C)).Append(',');
            sb.Append(v.y.ToString("F4", C)).Append(',');
            sb.Append(v.z.ToString("F4", C));
        }

        // Commas and line breaks would break the CSV columns.
        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ').Trim();
        }

        bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.lKey.wasPressedThisFrame) return true;
#endif
            return useControllerToggle
                   && OVRInput.GetDown(OVRInput.RawButton.RThumbstickUp, OVRInput.Controller.Touch);
        }
    }
}
