using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Gaze
{
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
        /// Gaze in world space straight from the tracker, with NO calibration correction.
        /// Used to show where the tracker alone would put your gaze, next to the corrected dot.
        /// </summary>
        public Ray TrackerGazeRay => HeadToWorld(GazeOriginHead, TrackerDirectionHead);

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

        void LateUpdate()
        {
            if (!EnsureCamera()) return;

            // Sample here so gameplay scripts reading gaze in Update/LateUpdate get this frame's data.
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
                    OVRPose headPose = _state.Pose.ToOVRPose().ToHeadSpacePose();
                    GazeOriginHead = headPose.position;
                    TrackerDirectionHead = headPose.orientation * Vector3.forward;
                    GazeDirectionHead = Corrected(TrackerDirectionHead);
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
                    GazeOriginHead = cam.InverseTransformPoint(worldRay.origin);
                    TrackerDirectionHead = cam.InverseTransformDirection(worldRay.direction).normalized;
                    GazeDirectionHead = Corrected(TrackerDirectionHead);
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
