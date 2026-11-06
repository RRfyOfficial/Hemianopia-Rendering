using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Gaze
{
    /// <summary>What happened to time-matching on the latest sample, and why.</summary>
    public enum TimeMatchStatus
    {
        NotEyes,           // mouse stand-in, nothing to match
        Off,               // switched off
        HeadsetHistory,    // matched with the head pose the headset gave for that moment
        OwnHistory,        // matched with our own recorded head poses (headset gave none)
        EstimatedLatency,  // eye clock unusable: matched using arrival time minus the assumed latency
        ClockMismatch,     // the eye sample's age made no sense and estimating is switched off
        NoHistory,         // no head pose for that moment from either source
    }

    public enum GazeMode
    {
        Auto,        // Quest Pro eye tracking if available, otherwise the mouse
        EyeTracking, // eye tracking only
        Mouse        // mouse only (for working without the headset, e.g. on a Mac)
    }

    /// <summary>
    /// Layer 1 of the stack: the single source of truth for "where is the person looking?"
    /// Everything downstream (gaze dot, hemianopia filter, cue logic) reads from here and
    /// never cares whether the gaze came from real eyes or the mouse.
    ///
    /// Two design rules (full write-up with math in Docs/GAZE.md):
    ///
    /// 1. Gaze is stored relative to the HEAD (the camera's local space), not the room.
    ///    The Meta camera rig moves the camera one last time right before each frame is drawn
    ///    (OVRCameraRig late update in Application.onBeforeRender). Anything placed in the world
    ///    using an earlier head pose slips by one frame whenever the head moves. A head-relative
    ///    ray is converted to world space using the camera's pose at the moment it's read, so it
    ///    can never fall behind the head.
    ///
    /// 2. RAW and STABLE gaze are kept separate.
    ///    RawGazeRay is the unfiltered signal. The hemianopia filter must use this one, because
    ///    any smoothing adds lag and lag lets people peek into the blind half after a saccade.
    ///    StableGazeRay goes through the FixationFilter (hold small wobble, snap on real jumps).
    ///    Use it for visuals like the debug dot, and later for deciding what the person actually looked at.
    ///
    /// 3. Eye samples CAN be time-matched to the head (latency fix, OFF by default).
    ///    The head-motion test showed the real latency is small (about 5 to 20 ms) and the motion error is
    ///    mostly a fixed lean, so this over-corrects at speed. Kept for experiments.
    ///    Eye data arrives tens of milliseconds late, while head data is fresh. Pairing old eyes with the
    ///    current head makes the gaze overshoot in the direction the head is turning. So each eye sample is
    ///    re-paired with the head pose from the sample's own moment. Toggle with M or the right grip.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class GazeProvider : MonoBehaviour
    {
        public static GazeProvider Instance { get; private set; }

        /// <summary>
        /// Raised once per frame right before rendering, AFTER the headset's final head update.
        /// Subscribe to this to place anything that has to line up exactly with the rendered view.
        /// </summary>
        public static event Action<GazeProvider> GazeUpdated;

        [Header("Source")]
        [Tooltip("Auto uses Quest Pro eye tracking when it's available, otherwise the mouse.")]
        public GazeMode mode = GazeMode.Auto;

        [Tooltip("Camera driven by the headset. Leave empty to use Camera.main (the OVRCameraRig center eye).")]
        public Camera viewCamera;

        [Header("Fixation filter (affects StableGazeRay only, never RawGazeRay)")]
        [Tooltip("Movements bigger than this many degrees count as saccades and snap instantly.")]
        public float snapThresholdDegrees = 2f;

        [Tooltip("How fast the stable gaze eases toward small movements, in seconds. Smaller follows faster, bigger is calmer.")]
        public float easeTimeConstant = 0.08f;

        [Header("Fixation point (the 3D spot the eyes are looking at)")]
        [Tooltip("How far to search along the raw gaze for a surface, in meters.")]
        public float fixationMaxDistance = 50f;

        [Tooltip("Where to put the fixation point when the gaze hits nothing, in meters. Far away means both eyes look almost parallel.")]
        public float noHitDistance = 100f;

        [Tooltip("Which layers count as things you can look at.")]
        public LayerMask fixationLayers = ~0;

        [Header("Calibration (bias correction)")]
        [Tooltip("Apply the per-person correction learned by the calibration check. Toggle with V or the right thumbstick click in Play mode.")]
        public bool applyCorrection = true;

        [Header("Time-matching (latency fix)")]
        [Tooltip("Pair each eye sample with the head pose from the moment it was taken, instead of the current head. Toggle with M or the right grip in Play mode.")]
        public bool timeMatchGaze = false;

        [Tooltip("An eye timestamp more than this many milliseconds from a known clock means it's on a different clock.")]
        public float maxTimeMatchMs = 250f;

        [Tooltip("When the eye timestamps are on an unknown clock (e.g. over Link), estimate each sample's moment as its arrival time minus this latency.")]
        public bool estimateWhenClocksDiffer = true;

        [Tooltip("Assumed delay from the eye being captured to the sample arriving, in ms. Start from the head-motion test (P01 measured about 80 ms), then tune it with the test: add the delay it still reports with time-matching on.")]
        public float assumedLatencyMs = 80f;

        // ---------- Head-relative state (camera-local space) ----------

        /// <summary>Gaze origin in head (camera-local) space.</summary>
        public Vector3 GazeOriginHead { get; private set; }

        /// <summary>
        /// Gaze direction in head (camera-local) space, after the calibration correction when one is active.
        /// +Z is straight ahead. This is what everything downstream uses.
        /// </summary>
        public Vector3 GazeDirectionHead { get; private set; } = Vector3.forward;

        /// <summary>
        /// Gaze direction straight from the tracker (or the mouse), BEFORE the calibration correction.
        /// The calibration check reads this one, so it always measures the tracker's own error.
        /// </summary>
        public Vector3 TrackerDirectionHead { get; private set; } = Vector3.forward;

        /// <summary>Gaze origin in head space straight from the tracker, before time-matching.</summary>
        public Vector3 TrackerOriginHead { get; private set; }

        /// <summary>Gaze direction in head space after the calibration correction, before time-matching.</summary>
        public Vector3 CorrectedDirectionHead { get; private set; } = Vector3.forward;

        /// <summary>How old the latest eye sample was when it was used, in milliseconds. 0 for the mouse.</summary>
        public float SampleAgeMs { get; private set; }

        /// <summary>True if the latest eye sample was time-matched to the head pose from its own moment.</summary>
        public bool IsTimeMatched => TimeMatchState == TimeMatchStatus.HeadsetHistory
                                     || TimeMatchState == TimeMatchStatus.OwnHistory
                                     || TimeMatchState == TimeMatchStatus.EstimatedLatency;

        /// <summary>What happened to time-matching on the latest sample, and why.</summary>
        public TimeMatchStatus TimeMatchState { get; private set; } = TimeMatchStatus.NotEyes;

        /// <summary>Which clock the eye timestamps turned out to be on: "head pose", "runtime", or "unknown".</summary>
        public string EyeClock { get; private set; } = "unknown";

        /// <summary>How far time-matching turned the gaze on the latest sample, in degrees.</summary>
        public float TimeMatchShiftDeg { get; private set; }

        /// <summary>The per-person bias correction learned by the calibration check. See Docs/CALIBRATION.md.</summary>
        public GazeCorrection Correction { get; } = new GazeCorrection();

        /// <summary>True when the correction is actually being applied to the current gaze source.</summary>
        public bool IsCorrectionActive => applyCorrection && Correction.IsValid && Correction.FitOnEyes == IsEyeTracking;

        /// <summary>True once we've received at least one gaze sample.</summary>
        public bool HasGaze { get; private set; }

        /// <summary>True when the gaze comes from real eyes, false when it's the mouse.</summary>
        public bool IsEyeTracking { get; private set; }

        /// <summary>Timestamp of the latest sample (headset time for eyes, Unity time for mouse).</summary>
        public double Timestamp { get; private set; }

        // ---------- World-space views ----------

        /// <summary>
        /// Unfiltered gaze (no smoothing) in world space, built from the camera's pose at the moment you read it.
        /// Bias-corrected when a calibration is active. This is the one the hemianopia filter uses.
        /// </summary>
        public Ray RawGazeRay => HeadToWorld(GazeOriginHead, GazeDirectionHead);

        /// <summary>
        /// Gaze in world space straight from the tracker: no calibration correction, no time-matching.
        /// For comparisons and diagnostics only.
        /// </summary>
        public Ray TrackerGazeRay => HeadToWorld(TrackerOriginHead, TrackerDirectionHead);

        /// <summary>Stabilized gaze in world space. Updated once per frame right before rendering.</summary>
        public Ray StableGazeRay => new Ray(RawGazeRay.origin, _filter.StableDirection);

        /// <summary>True while the eyes are holding roughly still (no saccade this frame).</summary>
        public bool IsFixating => _filter.IsFixating;

        /// <summary>Seconds since the current fixation started.</summary>
        public float FixationDuration => _filter.FixationDuration;

        /// <summary>
        /// The 3D point the eyes are looking at: where the raw gaze ray hits the scene.
        /// Both eyes point at this spot, so each eye's hemianopia boundary passes through it.
        /// Updated once per frame (LateUpdate, then again right before rendering).
        /// </summary>
        public Vector3 RawFixationPoint { get; private set; }

        /// <summary>Distance from the gaze origin to RawFixationPoint, in meters.</summary>
        public float RawFixationDistance { get; private set; }

        /// <summary>True if the raw gaze actually hit a surface (false = looking at empty space).</summary>
        public bool RawFixationHit { get; private set; }

        /// <summary>Kept for older scripts. Same as RawGazeRay.</summary>
        public Ray GazeRay => RawGazeRay;

        readonly FixationFilter _filter = new FixationFilter();
        OVRPlugin.EyeGazeInteractionState _state;
        bool _subscribedToRender;
        int _lastRenderFrame = -1;
        bool _hasReportedSource;

        void Awake()
        {
            Instance = this;
            if (viewCamera == null) viewCamera = Camera.main;
        }

        void OnDisable()
        {
            if (_subscribedToRender)
            {
                Application.onBeforeRender -= OnBeforeRender;
                _subscribedToRender = false;
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (TimeMatchTogglePressed())
            {
                timeMatchGaze = !timeMatchGaze;
                Debug.Log($"[Gaze] Time-matching {(timeMatchGaze ? "ON" : "OFF")}");
            }
        }

        void LateUpdate()
        {
            if (!EnsureCamera()) return;

            // Sample here so gameplay scripts reading gaze in Update/LateUpdate get this frame's data.
            RecordHeadPose();
            Sample();
            if (HasGaze) UpdateFixationPoint();

            // Subscribe on the first LateUpdate instead of in Start. By now every Start() has run,
            // so OVRCameraRig has already subscribed its late head update. Subscribing after it
            // guarantees our callback runs after the head's final move each frame.
            if (!_subscribedToRender)
            {
                Application.onBeforeRender += OnBeforeRender;
                _subscribedToRender = true;
            }
        }

        void OnBeforeRender()
        {
            if (!EnsureCamera()) return;
            if (Time.frameCount == _lastRenderFrame) return; // only once per frame
            _lastRenderFrame = Time.frameCount;

            // Re-sample so the gaze matches the exact head pose this frame is drawn with.
            RecordHeadPose();
            Sample();

            if (HasGaze)
            {
                UpdateFixationPoint();
                _filter.Step(RawGazeRay.direction, Time.deltaTime, snapThresholdDegrees, easeTimeConstant);
            }

            GazeUpdated?.Invoke(this);
        }

        /// <summary>Reads the current gaze from eyes or mouse and stores it in head space.</summary>
        void Sample()
        {
            bool eyesAvailable = mode != GazeMode.Mouse
                                 && OVRPlugin.eyeGazeInteractionsSupported
                                 && OVRPlugin.eyeGazeInteractionsEnabled;

            if (eyesAvailable)
            {
                SetSource(true); // first, so the correction check below knows the current source
                if (OVRPlugin.GetEyeGazeInteractionState(OVRPlugin.Step.Render, -1, ref _state) && _state.IsValid)
                {
                    // The pose arrives in the headset's tracking space (relative to the play area origin).
                    // ToHeadSpacePose removes the head's pose, leaving "where the eyes point relative to the head".
                    OVRPose eyeInHead = _state.Pose.ToOVRPose().ToHeadSpacePose();
                    TrackerOriginHead = eyeInHead.position;
                    TrackerDirectionHead = eyeInHead.orientation * Vector3.forward;
                    CorrectedDirectionHead = Corrected(TrackerDirectionHead);

                    // Time-matching. The eye sample is older than the head pose it was just combined with,
                    // so re-pair it with the head pose from the sample's own moment. In head space that means
                    // turning it by how much the head has moved since then. See Docs/GAZE.md.
                    Vector3 origin = TrackerOriginHead;
                    Vector3 direction = CorrectedDirectionHead;
                    TimeMatchShiftDeg = 0f;
                    Quaternion motion = Quaternion.identity;
                    Vector3 shift = Vector3.zero;
                    TimeMatchState = timeMatchGaze ? TimeMatch(_state.Time, out motion, out shift) : TimeMatchStatus.Off;
                    if (IsTimeMatched)
                    {
                        direction = motion * direction;
                        origin = motion * origin + shift;
                        TimeMatchShiftDeg = Vector3.Angle(CorrectedDirectionHead, direction);
                    }
                    ReportTimeMatchOnce();
                    GazeOriginHead = origin;
                    GazeDirectionHead = direction.normalized;
                    Timestamp = _state.Time;
                    HasGaze = true;
                }
                // If this sample is invalid (a blink, a dropped frame) we keep the last gaze on purpose.
            }
            else if (mode != GazeMode.EyeTracking)
            {
                SetSource(false);
                if (TryGetMouseRay(out Ray worldRay))
                {
                    Transform cam = viewCamera.transform;
                    TrackerOriginHead = cam.InverseTransformPoint(worldRay.origin);
                    GazeOriginHead = TrackerOriginHead;
                    TrackerDirectionHead = cam.InverseTransformDirection(worldRay.direction).normalized;
                    CorrectedDirectionHead = Corrected(TrackerDirectionHead);
                    GazeDirectionHead = CorrectedDirectionHead;
                    TimeMatchState = TimeMatchStatus.NotEyes;
                    SampleAgeMs = 0f;
                    TimeMatchShiftDeg = 0f;
                    Timestamp = Time.timeAsDouble;
                    HasGaze = true;
                }
            }
        }

        /// <summary>
        /// Finds the 3D spot the eyes are looking at by casting the raw gaze ray into the scene.
        /// This stands in for vergence (how much the two eyes angle inward): in VR you're almost
        /// always looking at a surface, so the surface you hit is where both eyes converge.
        /// </summary>
        void UpdateFixationPoint()
        {
            Ray ray = RawGazeRay;
            if (Physics.Raycast(ray, out RaycastHit hit, fixationMaxDistance, fixationLayers, QueryTriggerInteraction.Ignore))
            {
                RawFixationPoint = hit.point;
                RawFixationDistance = hit.distance;
                RawFixationHit = true;
            }
            else
            {
                RawFixationPoint = ray.GetPoint(noHitDistance);
                RawFixationDistance = noHitDistance;
                RawFixationHit = false;
            }
        }

        /// <summary>
        /// How the head has moved from time t until now, expressed in the CURRENT head's frame.
        /// Applying it to a head-space gaze from time t re-describes that same gaze line from the current head.
        ///   motion = inverse(head now) * (head then)
        /// Returns false if the runtime can't give a head pose for that time.
        /// </summary>
        double _lastEyeTime = double.NaN;
        double _eyeArrivalClock;

        /// <summary>
        /// Works out which clock the eye timestamp is on, then finds how the head moved since the sample.
        ///   1. Same clock as the head poses: use the real timestamp.
        ///   2. Same clock as the runtime: use the real timestamp.
        ///   3. Neither (e.g. over Link): the sample's moment = when it ARRIVED here, minus assumedLatencyMs.
        ///      A sample that sits around for extra frames correctly counts as older.
        /// Also sets SampleAgeMs.
        /// </summary>
        TimeMatchStatus TimeMatch(double eyeTime, out Quaternion motion, out Vector3 shift)
        {
            motion = Quaternion.identity;
            shift = Vector3.zero;

            double clockNow = OVRPlugin.GetTimeInSeconds();
            if (eyeTime != _lastEyeTime) // a new eye sample just arrived
            {
                _lastEyeTime = eyeTime;
                _eyeArrivalClock = clockNow;
            }

            int newest = (_historyNext - 1 + HistorySize) % HistorySize;
            double poseNow = _historyCount > 0 ? _historyPoseTime[newest] : 0.0;
            double limit = maxTimeMatchMs / 1000.0;

            if (poseNow > 0.0 && System.Math.Abs(poseNow - eyeTime) <= limit)
            {
                EyeClock = "head pose";
                SampleAgeMs = (float)((poseNow - eyeTime) * 1000.0);
                return TryHeadMotionSince(eyeTime, true, true, out motion, out shift, out bool fromHeadset)
                    ? (fromHeadset ? TimeMatchStatus.HeadsetHistory : TimeMatchStatus.OwnHistory)
                    : TimeMatchStatus.NoHistory;
            }

            if (System.Math.Abs(clockNow - eyeTime) <= limit)
            {
                EyeClock = "runtime";
                SampleAgeMs = (float)((clockNow - eyeTime) * 1000.0);
                return TryHeadMotionSince(eyeTime, true, false, out motion, out shift, out bool fromHeadset)
                    ? (fromHeadset ? TimeMatchStatus.HeadsetHistory : TimeMatchStatus.OwnHistory)
                    : TimeMatchStatus.NoHistory;
            }

            EyeClock = "unknown";
            if (!estimateWhenClocksDiffer)
            {
                SampleAgeMs = (float)((clockNow - eyeTime) * 1000.0);
                return TimeMatchStatus.ClockMismatch;
            }

            double age = (clockNow - _eyeArrivalClock) + assumedLatencyMs / 1000.0;
            SampleAgeMs = (float)(age * 1000.0);
            return TryHeadMotionSince(clockNow - age, false, false, out motion, out shift, out _)
                ? TimeMatchStatus.EstimatedLatency
                : TimeMatchStatus.NoHistory;
        }

        bool TryHeadMotionSince(double t, bool askHeadset, bool usePoseClock,
                                out Quaternion rotation, out Vector3 translation, out bool fromHeadset)
        {
            rotation = Quaternion.identity;
            translation = Vector3.zero;
            fromHeadset = false;

            Quaternion thenRotation = Quaternion.identity;
            Vector3 thenPosition = Vector3.zero;
            bool found = false;

            // First choice: ask the headset where the head was at time t (only meaningful on its own clock).
            if (askHeadset)
            {
                OVRPlugin.PoseStatef past = OVRPlugin.GetNodePoseStateAtTime(t, OVRPlugin.Node.Head);
                if (!IsIdentity(past.Pose)) // the SDK returns identity when it has no answer
                {
                    OVRPose then = past.Pose.ToOVRPose();
                    thenRotation = then.orientation;
                    thenPosition = then.position;
                    fromHeadset = true;
                    found = true;
                }
            }

            // Fallback: our own recorded head poses, blended between the two around time t.
            if (!found && !TryHeadFromHistory(t, usePoseClock, out thenRotation, out thenPosition))
            {
                return false;
            }

            OVRPose now = OVRPlugin.GetNodePose(OVRPlugin.Node.Head, OVRPlugin.Step.Render).ToOVRPose();
            Quaternion inverseNow = Quaternion.Inverse(now.orientation);
            rotation = inverseNow * thenRotation;
            translation = inverseNow * (thenPosition - now.position);
            return true;
        }

        // ---------- Our own head-pose history (fallback for time-matching) ----------
        // A ring buffer of the last ~1.5 s of head poses, each stamped with the headset's own clock,
        // so an eye sample's timestamp can be looked up in it. Same idea as a TAA history buffer,
        // but for head poses instead of pixels.

        const int HistorySize = 128;
        readonly double[] _historyPoseTime = new double[HistorySize];  // the pose's own time (0 if the SDK leaves it empty)
        readonly double[] _historyClockTime = new double[HistorySize]; // GetTimeInSeconds() when it was recorded
        readonly Quaternion[] _historyRotation = new Quaternion[HistorySize];
        readonly Vector3[] _historyPosition = new Vector3[HistorySize];
        int _historyCount;
        int _historyNext;
        double _historyLastTime = double.NegativeInfinity;

        void RecordHeadPose()
        {
            OVRPlugin.PoseStatef state = OVRPlugin.GetNodePoseStateRaw(OVRPlugin.Node.Head, OVRPlugin.Step.Render);
            if (IsIdentity(state.Pose)) return;

            double clock = OVRPlugin.GetTimeInSeconds();
            if (clock <= _historyLastTime) return; // nothing new since the last call

            OVRPose pose = state.Pose.ToOVRPose();
            _historyPoseTime[_historyNext] = state.Time;
            _historyClockTime[_historyNext] = clock;
            _historyRotation[_historyNext] = pose.orientation;
            _historyPosition[_historyNext] = pose.position;
            _historyNext = (_historyNext + 1) % HistorySize;
            _historyCount = Mathf.Min(_historyCount + 1, HistorySize);
            _historyLastTime = clock;
        }

        bool TryHeadFromHistory(double t, bool usePoseClock, out Quaternion rotation, out Vector3 position)
        {
            rotation = Quaternion.identity;
            position = Vector3.zero;
            if (_historyCount < 2) return false;
            double[] times = usePoseClock ? _historyPoseTime : _historyClockTime;

            int newest = (_historyNext - 1 + HistorySize) % HistorySize;
            if (t >= times[newest])
            {
                // Newer than anything recorded: the latest pose is the best we have.
                rotation = _historyRotation[newest];
                position = _historyPosition[newest];
                return true;
            }

            // Walk back in time until we find the two poses on either side of t, then blend.
            for (int k = 1; k < _historyCount; k++)
            {
                int newer = (newest - k + 1 + HistorySize) % HistorySize;
                int older = (newest - k + HistorySize) % HistorySize;
                if (times[older] <= t)
                {
                    double span = times[newer] - times[older];
                    float a = span > 0.0 ? (float)((t - times[older]) / span) : 1f;
                    rotation = Quaternion.Slerp(_historyRotation[older], _historyRotation[newer], a);
                    position = Vector3.Lerp(_historyPosition[older], _historyPosition[newer], a);
                    return true;
                }
            }
            return false; // older than everything we remember
        }

        bool _reportedTimeMatchProblem;
        bool _reportedTimeMatchSource;

        // One console line the first time each kind of outcome happens, with the numbers needed to debug it.
        void ReportTimeMatchOnce()
        {
            switch (TimeMatchState)
            {
                case TimeMatchStatus.HeadsetHistory:
                case TimeMatchStatus.OwnHistory:
                case TimeMatchStatus.EstimatedLatency:
                    if (_reportedTimeMatchSource) return;
                    _reportedTimeMatchSource = true;
                    string how = TimeMatchState == TimeMatchStatus.HeadsetHistory ? "the headset's head history"
                               : TimeMatchState == TimeMatchStatus.OwnHistory ? "our own head history"
                               : $"arrival time minus an assumed {assumedLatencyMs:0} ms (eye clock unknown)";
                    Debug.Log($"[Gaze] Time-matching works, using {how}. Eye clock: {EyeClock}. Eye data about {SampleAgeMs:0} ms old.");
                    break;
                case TimeMatchStatus.ClockMismatch:
                case TimeMatchStatus.NoHistory:
                    if (_reportedTimeMatchProblem) return;
                    _reportedTimeMatchProblem = true;
                    int newest = (_historyNext - 1 + HistorySize) % HistorySize;
                    Debug.LogWarning($"[Gaze] Time-matching can't run: {TimeMatchState}. Eye sample time {_state.Time:0.000} s, clock now {OVRPlugin.GetTimeInSeconds():0.000} s, age {SampleAgeMs:0} ms, " +
                                     $"head history {_historyCount} poses, newest pose time {(_historyCount > 0 ? _historyPoseTime[newest] : 0.0):0.000} s.");
                    break;
            }
        }

        static bool IsIdentity(OVRPlugin.Posef p)
        {
            return p.Orientation.w == 1f && p.Orientation.x == 0f && p.Orientation.y == 0f && p.Orientation.z == 0f
                   && p.Position.x == 0f && p.Position.y == 0f && p.Position.z == 0f;
        }

        static bool TimeMatchTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame) return true;
#endif
            return OVRInput.GetDown(OVRInput.RawButton.RHandTrigger, OVRInput.Controller.Touch);
        }

        Vector3 Corrected(Vector3 trackerDirectionHead)
        {
            return IsCorrectionActive ? Correction.ApplyDirection(trackerDirectionHead) : trackerDirectionHead;
        }

        Ray HeadToWorld(Vector3 originHead, Vector3 directionHead)
        {
            if (viewCamera == null) return new Ray(Vector3.zero, Vector3.forward);
            Transform cam = viewCamera.transform;
            return new Ray(cam.TransformPoint(originHead), cam.TransformDirection(directionHead).normalized);
        }

        bool EnsureCamera()
        {
            if (viewCamera == null) viewCamera = Camera.main;
            return viewCamera != null;
        }

        bool TryGetMouseRay(out Ray ray)
        {
            ray = default;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current == null) return false;
            Vector2 screenPos = Mouse.current.position.ReadValue();
#else
            Vector2 screenPos = Input.mousePosition;
#endif
            ray = viewCamera.ScreenPointToRay(screenPos);
            return true;
        }

        void SetSource(bool eyes)
        {
            if (_hasReportedSource && eyes == IsEyeTracking) return;

            IsEyeTracking = eyes;
            _hasReportedSource = true;
            _filter.Reset(); // a different source means the old stable direction is meaningless
            Debug.Log(eyes
                ? "[Gaze] Using Quest Pro eye tracking"
                : "[Gaze] Eye tracking not available, using the mouse as your eyes");
        }
    }
}
