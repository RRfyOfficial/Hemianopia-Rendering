using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Hemianopia.Gaze;
using Hemianopia.Logging;
using Hemianopia.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Tasks
{
    public enum BlockMode
    {
        Square,          // glides around a square: horizontal and vertical pursuit
        XPath,           // glides along both diagonals through the centre: diagonal pursuit
        SaccadeSteps,    // jumps between fixed points on a regular rhythm: saccades the person can anticipate
        MidlineCrossing  // person fixates a centre dot while the block slides across the vertical midline
    }

    /// <summary>
    /// A small target that moves along set paths. It gives the eyes something to follow and gives the
    /// TrialLogger a target position to record. It contains no hemianopia logic.
    ///
    /// Used for:
    ///   - Smooth pursuit (Square, XPath): compare the gaze trace with the block's path to estimate tracker lag.
    ///   - Saccade steps: landing accuracy at known locations (timing here mixes in the person's own reaction
    ///     time, so use pursuit for lag).
    ///   - Mask check (MidlineCrossing): with the filter on and the person fixating the centre dot, the block
    ///     should disappear as it crosses to the blind side of the gaze point. The logger's tgt_visible column
    ///     says where it SHOULD be hidden, so you can compare that with what the person actually reports.
    ///
    /// The block is HEAD-LOCKED: it sits a fixed number of degrees from straight ahead in the head's frame,
    /// the same way the calibration dots do. That removes head motion from these tests (keep the head still).
    ///
    /// Everything is positioned inside GazeProvider.GazeUpdated, which fires right before rendering and after
    /// the headset's final head move, so the block never lags the head by a frame. This script subscribes before
    /// the TrialLogger does (the logger subscribes when recording starts), so each logged row sees the block's
    /// current position.
    ///
    /// Controls:
    ///     Keyboard:    1 Square, 2 XPath, 3 SaccadeSteps, 4 MidlineCrossing   (starts a run)
    ///                  0 or Esc: abort the run
    ///     Controllers: right thumbstick RIGHT cycles the selected mode, right thumbstick DOWN starts or aborts it
    ///
    /// A run logs through the TrialLogger (started and stopped automatically unless it was already recording).
    /// Events marked in the log: run_start, run_end, run_aborted, lapN_start, jump_*, cross_rightward,
    /// cross_leftward, midline_cross (the frame the block's azimuth changes sign).
    /// </summary>
    public class MovingBlock : MonoBehaviour
    {
        [Header("References (found automatically if left empty)")]
        public GazeProvider gaze;
        public HemianopiaController filter;
        public TrialLogger logger;

        [Header("Path")]
        public BlockMode mode = BlockMode.Square;
        [Tooltip("How far from straight ahead the path reaches, in degrees. Keep inside the area you calibrated.")]
        public float amplitudeDegrees = 10f;
        [Tooltip("Pursuit speed for Square and XPath, in degrees per second.")]
        public float glideSpeedDps = 15f;
        [Tooltip("Slide speed for MidlineCrossing, in degrees per second. Slow, so the edge can be watched.")]
        public float crossingSpeedDps = 5f;
        [Tooltip("How many times to repeat the Square, XPath or MidlineCrossing path.")]
        public int laps = 2;
        [Tooltip("Hold at the start so the person can find the block and settle.")]
        public float startHoldSeconds = 2f;
        [Tooltip("SaccadeSteps: how long the block stays at each point.")]
        public float stepDwellSeconds = 1.5f;
        [Tooltip("SaccadeSteps: how many back-and-forth repeats per pair of points.")]
        public int stepRepeats = 3;

        [Header("Look")]
        [Tooltip("Block diameter in degrees of visual angle.")]
        public float sizeDegrees = 1.5f;
        [Tooltip("Distance from the eyes in metres.")]
        public float distance = 2f;
        public Color blockColor = new Color(1f, 0.85f, 0f);
        public Color fixationColor = Color.cyan;

        [Header("Gaze depth")]
        [Tooltip("While a run is active, add an invisible head-locked wall at the task distance, so the filter's fixation " +
                 "raycast always lands at the block's depth instead of on whatever is behind it. Turn off to test the scene as-is.")]
        public bool addDepthPlane = true;

        [Header("Logging")]
        [Tooltip("Start the TrialLogger for each run (and stop it afterwards) if it isn't already recording.")]
        public bool recordWithLogger = true;
        [Tooltip("Half-width of the central area the filter leaves visible, in degrees. Must match the filter (it has no sparing yet, so 0).")]
        public float sparingDegrees = 0f;

        [Header("Input")]
        public bool useControllerInput = true;

        public bool IsRunning => _running;
        /// <summary>Block position in degrees (azimuth, elevation) from straight ahead in the head frame.</summary>
        public Vector2 CurrentAngles => _pos;

        struct Seg
        {
            public Vector2 from, to;
            public float duration;
            public string mark;
        }

        readonly List<Seg> _segs = new List<Seg>();
        int _si;
        float _st;
        bool _newSeg;
        bool _running;
        bool _ending;
        bool _abortRequested;
        bool _startedLogger;
        int _runIndex;
        Vector2 _pos;
        float _prevAz;
        bool _hasPrevAz;

        GameObject _block, _dot, _depth;

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        // ---------- Public API ----------

        public void StartRun(BlockMode m)
        {
            if (_running || _ending) return;
            mode = m;

            if (gaze == null) gaze = FindAnyObjectByType<GazeProvider>();
            if (filter == null) filter = FindAnyObjectByType<HemianopiaController>();
            if (logger == null) logger = FindAnyObjectByType<TrialLogger>();
            if (gaze == null)
            {
                Debug.LogWarning("[MovingBlock] No GazeProvider in the scene.");
                return;
            }

            BuildPath(m);
            if (_segs.Count == 0) return;

            EnsureVisuals();
            _block.SetActive(true);
            _dot.SetActive(m == BlockMode.MidlineCrossing);
            _depth.SetActive(addDepthPlane);

            _runIndex++;
            _si = 0;
            _st = 0f;
            _newSeg = true;
            _hasPrevAz = false;
            _abortRequested = false;

            _startedLogger = false;
            if (logger != null)
            {
                logger.SetTrial(_runIndex, ConditionLabel(m));
                if (recordWithLogger && !logger.IsRecording)
                {
                    logger.sessionLabel = m.ToString().ToLowerInvariant();
                    _startedLogger = logger.StartSession();
                }
            }

            _running = true;
            Debug.Log($"[MovingBlock] Run {_runIndex}: {ConditionLabel(m)}. Keep your head still and follow the block.");
        }

        public void AbortRun()
        {
            if (_running) _abortRequested = true;
        }

        // ---------- Unity ----------

        void OnEnable()
        {
            GazeProvider.GazeUpdated += OnGaze;
        }

        void OnDisable()
        {
            GazeProvider.GazeUpdated -= OnGaze;
            _running = false;
            _ending = false;
            if (_startedLogger && logger != null) logger.StopSession();
            _startedLogger = false;
            DestroyVisuals();
        }

        void Update()
        {
            HandleInput();
        }

        // ---------- Per frame ----------

        void OnGaze(GazeProvider g)
        {
            if (_ending)
            {
                Finish();
                return;
            }
            if (!_running) return;

            Camera cam = g.viewCamera != null ? g.viewCamera : Camera.main;
            if (cam == null) return;

            // 1. Where is the block right now?
            Seg seg = _segs[_si];
            float t = seg.duration > 0f ? Mathf.Clamp01(_st / seg.duration) : 1f;
            _pos = Vector2.Lerp(seg.from, seg.to, t);

            string ev = "";
            if (_newSeg)
            {
                ev = Join(ev, seg.mark);
                _newSeg = false;
            }
            if (_hasPrevAz && _prevAz * _pos.x < 0f) // strictly opposite sides of the midline (leaving or arriving at exactly 0 does not count)
                ev = Join(ev, "midline_cross");
            _prevAz = _pos.x;
            _hasPrevAz = true;

            // 2. Place everything (head-locked).
            Transform ct = cam.transform;
            _block.transform.position = ct.TransformPoint(GazeCorrection.FromAngles(_pos) * distance);
            if (_dot.activeSelf) _dot.transform.position = ct.TransformPoint(Vector3.forward * distance);
            if (_depth.activeSelf)
            {
                _depth.transform.position = ct.TransformPoint(Vector3.forward * (distance + 0.011f));
                _depth.transform.rotation = ct.rotation;
            }

            // 3. Should this block be hidden by the filter right now? Approximate: compares the block's azimuth
            //    with the gaze azimuth in the head frame (the real filter works per eye in 3D).
            bool visible = true;
            if (filter != null && filter.filterEnabled)
            {
                float gazeAz = GazeCorrection.ToAngles(g.CorrectedDirectionHead).x;
                float rel = _pos.x - gazeAz;
                visible = filter.blindSide == BlindSide.Left ? rel >= -sparingDegrees : rel <= sparingDegrees;
            }

            // 4. Advance the path.
            bool finished = false;
            if (_abortRequested)
            {
                ev = Join(ev, "run_aborted");
                finished = true;
            }
            else
            {
                _st += Time.deltaTime;
                while (_si < _segs.Count && _st >= _segs[_si].duration)
                {
                    _st -= _segs[_si].duration;
                    _si++;
                    _newSeg = true;
                }
                if (_si >= _segs.Count)
                {
                    ev = Join(ev, "run_end");
                    finished = true;
                }
            }

            // 5. Tell the logger. It writes this frame's row after us.
            if (logger != null)
            {
                logger.SetTarget(_block.transform.position, visible);
                // Only mark while recording, or the label would be saved on the first row of some later session.
                if (ev.Length > 0 && logger.IsRecording) logger.Mark(ev);
            }

            if (finished)
            {
                _running = false;
                _ending = true; // cleaned up next frame, so the last row (with its event) is written first
            }
        }

        void Finish()
        {
            _ending = false;
            _abortRequested = false;
            if (logger != null)
            {
                logger.ClearTarget();
                if (_startedLogger) logger.StopSession();
            }
            _startedLogger = false;
            if (_block != null) _block.SetActive(false);
            if (_dot != null) _dot.SetActive(false);
            if (_depth != null) _depth.SetActive(false);
            Debug.Log("[MovingBlock] Run finished.");
        }

        // ---------- Paths ----------

        void BuildPath(BlockMode m)
        {
            _segs.Clear();
            float A = Mathf.Max(1f, amplitudeDegrees);
            Vector2 c = Vector2.zero;
            int n = Mathf.Max(1, laps);

            switch (m)
            {
                case BlockMode.Square:
                {
                    Vector2[] k = { new Vector2(-A, -A), new Vector2(A, -A), new Vector2(A, A), new Vector2(-A, A) };
                    Hold(c, startHoldSeconds, "run_start");
                    Glide(c, k[0], glideSpeedDps);
                    for (int lap = 0; lap < n; lap++)
                        for (int i = 0; i < 4; i++)
                            Glide(k[i], k[(i + 1) % 4], glideSpeedDps, i == 0 ? $"lap{lap + 1}_start" : "");
                    Glide(k[0], c, glideSpeedDps);
                    Hold(c, 1f);
                    break;
                }
                case BlockMode.XPath:
                {
                    Vector2 tl = new Vector2(-A, A), tr = new Vector2(A, A), bl = new Vector2(-A, -A), br = new Vector2(A, -A);
                    Hold(c, startHoldSeconds, "run_start");
                    for (int lap = 0; lap < n; lap++)
                    {
                        Glide(c, tl, glideSpeedDps, $"lap{lap + 1}_start");
                        Glide(tl, br, glideSpeedDps);
                        Glide(br, c, glideSpeedDps);
                        Glide(c, tr, glideSpeedDps);
                        Glide(tr, bl, glideSpeedDps);
                        Glide(bl, c, glideSpeedDps);
                    }
                    Hold(c, 1f);
                    break;
                }
                case BlockMode.SaccadeSteps:
                {
                    var pairs = new (string name, Vector2 a, Vector2 b)[]
                    {
                        ("horizontal", new Vector2(-A, 0), new Vector2(A, 0)),
                        ("vertical", new Vector2(0, -A), new Vector2(0, A)),
                        ("diagonal1", new Vector2(-A, -A), new Vector2(A, A)),
                        ("diagonal2", new Vector2(-A, A), new Vector2(A, -A)),
                    };
                    Hold(c, startHoldSeconds, "run_start");
                    foreach (var p in pairs)
                    {
                        for (int r = 0; r < Mathf.Max(1, stepRepeats); r++)
                        {
                            Hold(p.a, stepDwellSeconds, "jump_" + p.name);
                            Hold(p.b, stepDwellSeconds, "jump_" + p.name);
                        }
                        Hold(c, stepDwellSeconds, "jump_center");
                    }
                    break;
                }
                case BlockMode.MidlineCrossing:
                {
                    Vector2 left = new Vector2(-A, 0), right = new Vector2(A, 0);
                    Hold(left, Mathf.Max(startHoldSeconds, 3f), "run_start");
                    for (int lap = 0; lap < n; lap++)
                    {
                        Glide(left, right, crossingSpeedDps, "cross_rightward");
                        Hold(right, 1f);
                        Glide(right, left, crossingSpeedDps, "cross_leftward");
                        Hold(left, 1f);
                    }
                    break;
                }
            }
        }

        void Glide(Vector2 a, Vector2 b, float speed, string mark = "")
        {
            float d = Vector2.Distance(a, b);
            _segs.Add(new Seg { from = a, to = b, duration = Mathf.Max(0.01f, d / Mathf.Max(0.1f, speed)), mark = mark });
        }

        void Hold(Vector2 p, float seconds, string mark = "")
        {
            _segs.Add(new Seg { from = p, to = p, duration = Mathf.Max(0.01f, seconds), mark = mark });
        }

        string ConditionLabel(BlockMode m)
        {
            switch (m)
            {
                case BlockMode.Square:
                case BlockMode.XPath:
                    return $"{m}_{glideSpeedDps.ToString("0.#", C)}dps";
                case BlockMode.MidlineCrossing:
                    return $"{m}_{crossingSpeedDps.ToString("0.#", C)}dps";
                default:
                    return $"{m}_{stepDwellSeconds.ToString("0.0", C)}s";
            }
        }

        static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(b)) return a;
            return string.IsNullOrEmpty(a) ? b : a + ";" + b;
        }

        // ---------- Visuals ----------

        void EnsureVisuals()
        {
            if (_block == null)
            {
                _block = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _block.name = "MovingBlock_Target";
                Destroy(_block.GetComponent<Collider>()); // the depth plane handles gaze depth
                _block.GetComponent<Renderer>().material.color = blockColor;
            }
            if (_dot == null)
            {
                _dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _dot.name = "MovingBlock_FixationDot";
                Destroy(_dot.GetComponent<Collider>());
                _dot.GetComponent<Renderer>().material.color = fixationColor;
            }
            if (_depth == null)
            {
                // Invisible: a collider and nothing else.
                _depth = new GameObject("MovingBlock_DepthPlane");
                var box = _depth.AddComponent<BoxCollider>();
                box.size = new Vector3(8f, 8f, 0.02f);
            }

            float blockSize = 2f * distance * Mathf.Tan(sizeDegrees * 0.5f * Mathf.Deg2Rad);
            _block.transform.localScale = Vector3.one * blockSize;
            float dotSize = 2f * distance * Mathf.Tan(0.6f * 0.5f * Mathf.Deg2Rad);
            _dot.transform.localScale = Vector3.one * dotSize;

            _block.GetComponent<Renderer>().material.color = blockColor;
            _dot.GetComponent<Renderer>().material.color = fixationColor;
        }

        void DestroyVisuals()
        {
            if (_block != null) Destroy(_block);
            if (_dot != null) Destroy(_dot);
            if (_depth != null) Destroy(_depth);
            _block = _dot = _depth = null;
        }

        // ---------- Input ----------

        void HandleInput()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.digit1Key.wasPressedThisFrame) StartRun(BlockMode.Square);
                else if (kb.digit2Key.wasPressedThisFrame) StartRun(BlockMode.XPath);
                else if (kb.digit3Key.wasPressedThisFrame) StartRun(BlockMode.SaccadeSteps);
                else if (kb.digit4Key.wasPressedThisFrame) StartRun(BlockMode.MidlineCrossing);
                else if (kb.digit0Key.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame) AbortRun();
            }
#endif
            if (!useControllerInput) return;

            const OVRInput.Controller touch = OVRInput.Controller.Touch;
            if (!_running && !_ending && OVRInput.GetDown(OVRInput.RawButton.RThumbstickRight, touch))
            {
                mode = (BlockMode)(((int)mode + 1) % System.Enum.GetValues(typeof(BlockMode)).Length);
                Debug.Log($"[MovingBlock] Selected mode: {mode}");
            }
            if (OVRInput.GetDown(OVRInput.RawButton.RThumbstickDown, touch))
            {
                if (_running) AbortRun(); else StartRun(mode);
            }
        }
    }
}
