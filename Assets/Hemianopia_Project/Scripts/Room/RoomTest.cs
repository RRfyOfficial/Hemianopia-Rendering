using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Hemianopia.Calibration;
using Hemianopia.Gaze;
using Hemianopia.Logging;
using Hemianopia.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Room
{
    /// <summary>
    /// The first, smallest version of "a world where stuff happens".
    ///
    /// Part 1, the room: a few simple shapes placed around the person. Each one has a name tag (TrackedObject),
    /// and brightens while it is being looked at (LookTracker).
    ///
    /// Part 2, the surprise: after a short random wait, one new shape appears on the person's BLIND side.
    /// The test measures how long it takes until they first look at it. That gap is the number the whole
    /// project is about: how long something on the blind side goes unnoticed.
    ///
    /// Part 3, the nudge (optional): if anything has sat on the blind side for a moment without ever being
    /// looked at, the NudgeCue lights up beside the gaze and points toward it. That covers the room's shapes
    /// when they first appear, and the surprise later, with one rule. Running the same surprise with the
    /// nudge off and on shows whether the nudge helps.
    ///
    /// Controls:
    ///     Keyboard:    5 = show or hide the room,  6 = run one surprise,  7 = nudge on or off,
    ///                  9 = switch the nudge between "stays in the room" and "follows the gaze",
    ///                  G = show or hide the gaze laser (see GazeLaser)
    ///     Controllers: LEFT thumbstick right = show or hide the room,  LEFT thumbstick down = run one surprise,
    ///                  LEFT thumbstick left = nudge on or off
    ///
    /// Each surprise is recorded through the TrialLogger (events: trial_start, appear:surprise,
    /// nudge_on:<object>, first_look:surprise or timeout:surprise, trial_end), and one line per surprise is
    /// added to CalibrationLogs/room_summary.csv.
    ///
    /// The shapes stay fixed in the world, so the person can turn their head and look around freely.
    /// </summary>
    [RequireComponent(typeof(LookTracker))]
    public class RoomTest : MonoBehaviour
    {
        [Header("References (found automatically if left empty)")]
        public GazeProvider gaze;
        public HemianopiaController filter;
        public TrialLogger logger;

        [Header("Room")]
        [Tooltip("Distance from the person to every shape, in metres.")]
        public float distance = 2f;
        [Tooltip("Size of each shape in metres.")]
        public float shapeSize = 0.2f;

        [Header("Surprise")]
        [Tooltip("The surprise appears after a random wait between these two values, in seconds.")]
        public Vector2 waitRangeSeconds = new Vector2(2f, 4f);
        [Tooltip("How far into the blind side the surprise appears, in degrees from where the person is looking.")]
        public float surpriseOffsetDegrees = 25f;
        [Tooltip("The surprise is never placed closer to the gaze than this, in degrees.")]
        public float minOffsetDegrees = 12f;
        [Tooltip("Keeps the surprise inside the headset's view: at most this many degrees left or right of straight ahead.")]
        public float maxHeadAzimuthDegrees = 35f;
        [Tooltip("The surprise only appears while the eyes have been resting on one spot for at least this long, in seconds. " +
                 "Stops lucky finds where the eyes were already mid-jump toward the spot.")]
        public float minRestSeconds = 0.25f;
        [Tooltip("Give up if the surprise has not been looked at after this long, in seconds.")]
        public float timeoutSeconds = 20f;

        [Header("Nudge")]
        [Tooltip("When on, the nudge points at anything on the blind side that has never been looked at. Toggle with 7 in Play mode.")]
        public bool nudgeEnabled = false;
        [Tooltip("How long something must sit unnoticed before the nudge points at it, in seconds. " +
                 "Was 1 s at first. Shortened on Nov 14: with a 1 s wait plus about half a second to react, " +
                 "the nudge could not beat someone who was already searching.")]
        public float nudgeDelaySeconds = 0.3f;
        [Tooltip("The nudge sticks with one object until it is looked at. If that object sits on the seeing side " +
                 "for this long without being looked at, the nudge lets go and can point at something else.")]
        public float stickGraceSeconds = 0.75f;

        [Header("Logging")]
        [Tooltip("Start the TrialLogger for each surprise (and stop it afterwards) if it isn't already recording.")]
        public bool recordWithLogger = true;

        [Header("Input")]
        public bool useControllerInput = true;

        enum Phase { Idle, Waiting, Shown, Found, Closing }

        Phase _phase = Phase.Idle;
        LookTracker _tracker;
        GameObject _root;
        TrackedObject _surprise;
        int _trialIndex;
        bool _startedLogger;
        double _spawnAt;
        double _forceSpawnAt;
        double _endAt;
        float _spawnOffsetDeg;
        string _events = "";
        NudgeCue _cue;
        TrackedObject _nudgeTarget; // what the nudge is pointing at right now
        double _targetOffBlindSince = -1.0; // when the current target left the blind side (-1 = it is on it)
        bool _trialNudge;      // was the nudge switched on for this surprise?
        bool _nudgeShown;      // did the nudge actually appear?
        double _nudgeAtSeconds; // how long after the surprise appeared
        string _trialCueMode = ""; // "stay" or "follow", for the summary file

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        static readonly float[] ShapeAzimuths = { -40f, -20f, 0f, 20f, 40f };
        static readonly float[] ShapeHeights = { 0.10f, -0.15f, 0f, 0.20f, -0.10f };
        static readonly PrimitiveType[] ShapeKinds =
        {
            PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Cylinder, PrimitiveType.Cube, PrimitiveType.Sphere
        };
        static readonly string[] ShapeLabels = { "red cube", "blue ball", "yellow can", "green cube", "orange ball" };
        static readonly Color[] ShapeColors =
        {
            new Color(0.80f, 0.15f, 0.15f),
            new Color(0.15f, 0.35f, 0.85f),
            new Color(0.85f, 0.70f, 0.10f),
            new Color(0.15f, 0.60f, 0.25f),
            new Color(0.90f, 0.45f, 0.10f),
        };
        static readonly Color SurpriseColor = new Color(0.80f, 0.10f, 0.80f);
        static readonly Color FoundColor = new Color(0.20f, 0.90f, 0.30f);

        // ---------- Unity ----------

        void OnEnable()
        {
            _tracker = GetComponent<LookTracker>();
            if (_tracker != null) _tracker.FirstLook += OnFirstLook;
            _cue = GetComponent<NudgeCue>();
            if (_cue == null) _cue = gameObject.AddComponent<NudgeCue>();
            if (GetComponent<GazeLaser>() == null) gameObject.AddComponent<GazeLaser>();
        }

        void OnDisable()
        {
            if (_tracker != null) _tracker.FirstLook -= OnFirstLook;
            if (_cue != null) _cue.Hide();
            if (_startedLogger && logger != null)
            {
                logger.ClearTarget();
                logger.StopSession();
            }
            _startedLogger = false;
            _phase = Phase.Idle;
            ClearRoom();
        }

        void Update()
        {
            HandleInput();

            double now = Time.realtimeSinceStartupAsDouble;
            switch (_phase)
            {
                case Phase.Waiting:
                    if (now >= _spawnAt) TrySpawn(now >= _forceSpawnAt);
                    break;

                case Phase.Shown:
                    if (_surprise != null && logger != null)
                        logger.SetTarget(_surprise.transform.position, !_surprise.HiddenByMask);


                    if (_surprise != null && now - _surprise.AppearedTime > timeoutSeconds)
                        Finish(false);
                    break;

                case Phase.Found:
                    if (now >= _endAt)
                    {
                        Queue("trial_end");
                        _phase = Phase.Closing; // closed next frame, so the trial_end row is written first
                    }
                    break;

                case Phase.Closing:
                    Close();
                    break;
            }

            UpdateNudge(now);

            // Hand this frame's events to the logger in one go. (TrialLogger.Mark keeps only the latest label.)
            if (_events.Length > 0)
            {
                if (logger != null && logger.IsRecording) logger.Mark(_events);
                _events = "";
            }
        }

        // ---------- Part 1: the room ----------

        public void ToggleRoom()
        {
            if (_phase != Phase.Idle) return;
            if (_root != null)
            {
                ClearRoom();
                Debug.Log("[Room] Room hidden.");
            }
            else
            {
                BuildRoom();
            }
        }

        void BuildRoom()
        {
            if (!FindReferences()) return;
            Camera cam = Cam();
            if (cam == null) return;

            // Straight ahead of the person right now, kept level so the shapes sit at eye height.
            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            Vector3 eye = cam.transform.position;

            _root = new GameObject("Room_Shapes");

            // Invisible wall just behind the shapes. Without it, looking at the empty space between shapes makes
            // the gaze raycast land far away, which shifts the mask's edge by about a degree (same reason
            // MovingBlock adds its depth plane).
            GameObject wall = new GameObject("Room_DepthWall");
            wall.transform.SetParent(_root.transform, true);
            wall.transform.position = eye + forward * (distance + 0.3f);
            wall.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            BoxCollider box = wall.AddComponent<BoxCollider>();
            box.size = new Vector3(12f, 8f, 0.02f);

            for (int i = 0; i < ShapeAzimuths.Length; i++)
            {
                Vector3 dir = Quaternion.AngleAxis(ShapeAzimuths[i], Vector3.up) * forward;
                Vector3 pos = eye + dir * distance + Vector3.up * ShapeHeights[i];
                CreateShape(ShapeKinds[i], ShapeLabels[i], ShapeColors[i], pos, shapeSize);
            }
            Debug.Log("[Room] Room shown: 5 shapes around you. Look at each one and it should brighten.");
        }

        TrackedObject CreateShape(PrimitiveType kind, string label, Color color, Vector3 position, float size)
        {
            GameObject go = GameObject.CreatePrimitive(kind);
            go.name = "Room_" + label.Replace(' ', '_');
            go.transform.SetParent(_root.transform, true);
            go.transform.position = position;
            // Unity's cylinder is 2 units tall, so halve its height to match the other shapes.
            go.transform.localScale = kind == PrimitiveType.Cylinder
                ? new Vector3(size, size * 0.5f, size)
                : Vector3.one * size;
            // The collider stays on, so the gaze raycast finds the shape's real depth.
            TrackedObject t = go.AddComponent<TrackedObject>();
            t.Init(label, color);
            return t;
        }

        void ClearRoom()
        {
            if (_root != null) Destroy(_root);
            _root = null;
            _surprise = null;
        }

        // ---------- Part 3: the nudge ----------

        public void ToggleNudge()
        {
            nudgeEnabled = !nudgeEnabled;
            Debug.Log($"[Room] Nudge {(nudgeEnabled ? "ON" : "OFF")}");
            // Flash it once when switching on, so you can see what it looks like and know it is on.
            if (_cue != null)
            {
                if (nudgeEnabled) _cue.Preview(1.5f);
                else _cue.Hide();
            }
            _nudgeTarget = null;
        }

        public void ToggleCuePlacement()
        {
            if (_cue == null) return;
            _cue.placement = _cue.placement == CuePlacement.StayInRoom ? CuePlacement.FollowGaze : CuePlacement.StayInRoom;
            Debug.Log($"[Room] Nudge placement: {(_cue.placement == CuePlacement.StayInRoom ? "stays in the room" : "follows the gaze")}");
            if (nudgeEnabled) _cue.Preview(1.5f);
        }

        /// <summary>
        /// The nudge rule. Point at anything that:
        ///   1. is on the blind side of where the person is looking,
        ///   2. has never been looked at, and
        ///   3. has been there for at least nudgeDelaySeconds.
        /// If several things qualify, the newest one wins (something that just appeared matters most).
        /// Once it has picked something, it sticks with it until that thing has been looked at. It only
        /// switches early for something clearly newer. (Without this, the cue flickered between the surprise
        /// and an old unseen shape whenever the surprise slipped onto the seeing side for a moment.)
        /// Once everything has been looked at, the nudge goes quiet.
        /// </summary>
        void UpdateNudge(double now)
        {
            if (_cue == null) return;

            // Stick with the current target until it has been looked at. But if it has sat on the seeing side
            // for a while without being looked at, let go, so the nudge is free to point at something else.
            // (It becomes a candidate again as soon as it is back on the blind side.)
            bool letGo = false;
            if (_nudgeTarget != null && !_nudgeTarget.EverSeen)
            {
                if (_nudgeTarget.OnBlindSide) _targetOffBlindSince = -1.0;
                else if (_targetOffBlindSince < 0.0) _targetOffBlindSince = now;
                letGo = _targetOffBlindSince >= 0.0 && now - _targetOffBlindSince > stickGraceSeconds;
            }
            TrackedObject current = nudgeEnabled && _nudgeTarget != null && !_nudgeTarget.EverSeen && !letGo ? _nudgeTarget : null;

            TrackedObject best = current;
            if (nudgeEnabled)
            {
                List<TrackedObject> all = TrackedObject.All;
                for (int i = 0; i < all.Count; i++)
                {
                    TrackedObject o = all[i];
                    if (o == null || o.EverSeen || !o.OnBlindSide) continue;
                    if (now - o.AppearedTime < nudgeDelaySeconds) continue;

                    if (best == null)
                    {
                        best = o;
                        continue;
                    }

                    double newer = o.AppearedTime - best.AppearedTime;
                    if (newer > 0.05)
                    {
                        best = o; // clearly newer: it wins
                    }
                    else if (newer > -0.05 && current == null && o.AngleFromGaze < best.AngleFromGaze)
                    {
                        best = o; // about the same age and nothing picked yet: take the one nearest the gaze
                    }
                }
            }

            if (best == _nudgeTarget) return; // nothing changed

            bool hadTarget = _nudgeTarget != null;
            _nudgeTarget = best;
            _targetOffBlindSince = -1.0;
            if (best == null)
            {
                if (hadTarget) _cue.Hide();
                return;
            }

            _cue.Show(best);
            Queue("nudge_on:" + best.label);
            if (best == _surprise && _phase == Phase.Shown)
            {
                _nudgeShown = true;
                _nudgeAtSeconds = now - best.AppearedTime;
            }
        }

        // ---------- Part 2: the surprise ----------

        public void StartSurprise()
        {
            if (_phase != Phase.Idle) return;
            if (!FindReferences()) return;
            if (_root == null) BuildRoom();
            if (_root == null) return;

            bool maskOn = filter != null && filter.filterEnabled;
            _trialIndex++;

            double now = Time.realtimeSinceStartupAsDouble;
            _spawnAt = now + Random.Range(waitRangeSeconds.x, waitRangeSeconds.y);
            _forceSpawnAt = _spawnAt + 5.0;

            _trialNudge = nudgeEnabled;
            _trialCueMode = _trialNudge && _cue != null
                ? (_cue.placement == CuePlacement.StayInRoom ? "stay" : "follow")
                : "";
            _nudgeShown = false;
            _nudgeAtSeconds = 0.0;

            _startedLogger = false;
            if (logger != null)
            {
                logger.SetTrial(_trialIndex, (maskOn ? "surprise_mask_on" : "surprise_mask_off") + (_trialNudge ? "_nudge" : ""));
                if (recordWithLogger && !logger.IsRecording)
                {
                    logger.sessionLabel = "room";
                    _startedLogger = logger.StartSession();
                }
            }

            Queue("trial_start");
            _phase = Phase.Waiting;
            Debug.Log($"[Room] Surprise {_trialIndex} armed (mask {(maskOn ? "ON" : "OFF")}, nudge {(_trialNudge ? "ON" : "OFF")}). Keep looking around at the shapes. Something new will appear.");
        }

        void TrySpawn(bool force)
        {
            Camera cam = Cam();
            if (cam == null || gaze == null || !gaze.HasGaze) return;
            Transform ct = cam.transform;

            // Wait until the eyes are resting on something. If they are mid-jump, they might land on the
            // surprise by luck, which gives an impossibly fast "find" (seen on Nov 11: 0.12 s).
            if (!force && (!gaze.IsFixating || gaze.FixationDuration < minRestSeconds)) return;

            // Where is the person looking, left/right, compared with straight ahead of their head?
            float gazeAz = GazeCorrection.ToAngles(ct.InverseTransformDirection(gaze.FilterGazeRay.direction)).x;
            float sign = BlindLeft() ? -1f : 1f;

            // Aim for a spot on the blind side of the gaze, but keep it inside the headset's view.
            float az = Mathf.Clamp(gazeAz + sign * surpriseOffsetDegrees, -maxHeadAzimuthDegrees, maxHeadAzimuthDegrees);
            float offset = sign * (az - gazeAz);
            if (offset < minOffsetDegrees)
            {
                if (!force) return; // looking too far toward the blind side: wait for a better moment
                az = gazeAz + sign * minOffsetDegrees;
                offset = minOffsetDegrees;
            }

            // Pick a height that doesn't sit on top of one of the room's shapes.
            float baseEl = Random.Range(-8f, 8f);
            float[] tries = { baseEl, baseEl + 12f, baseEl - 12f };
            Vector3 pos = Vector3.zero;
            for (int i = 0; i < tries.Length; i++)
            {
                pos = ct.position + ct.TransformDirection(GazeCorrection.FromAngles(new Vector2(az, tries[i]))) * distance;
                if (IsClearOfShapes(ct.position, pos, 8f)) break;
            }

            _surprise = CreateShape(PrimitiveType.Sphere, "surprise", SurpriseColor, pos, shapeSize);
            _spawnOffsetDeg = offset;
            if (logger != null) logger.SetTarget(pos, false);
            Queue("appear:surprise");
            _phase = Phase.Shown;
            Debug.Log($"[Room] Surprise appeared {offset.ToString("0", C)} degrees into the blind side.");
        }

        static bool IsClearOfShapes(Vector3 eye, Vector3 candidate, float minDegrees)
        {
            Vector3 toCandidate = candidate - eye;
            for (int i = 0; i < TrackedObject.All.Count; i++)
            {
                TrackedObject o = TrackedObject.All[i];
                if (o == null) continue;
                if (Vector3.Angle(toCandidate, o.transform.position - eye) < minDegrees) return false;
            }
            return true;
        }

        void OnFirstLook(TrackedObject o)
        {
            if (_phase == Phase.Shown && o == _surprise) Finish(true);
        }

        void Finish(bool found)
        {
            if (_surprise == null)
            {
                _phase = Phase.Closing;
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            double missSeconds = (found ? _surprise.FirstSeenTime : now) - _surprise.AppearedTime;

            Queue(found ? "first_look:surprise" : "timeout:surprise");
            if (found)
            {
                _surprise.baseColor = FoundColor;
                _surprise.RefreshColor();
                Debug.Log($"[Room] FOUND. The surprise went unnoticed for {missSeconds.ToString("0.00", C)} seconds.");
            }
            else
            {
                Debug.Log($"[Room] TIMEOUT. The surprise was not looked at within {timeoutSeconds.ToString("0", C)} seconds.");
            }

            WriteSummary(found, missSeconds);
            _endAt = now + 1.5;
            _phase = Phase.Found;
        }

        void Close()
        {
            if (logger != null)
            {
                logger.ClearTarget();
                if (_startedLogger) logger.StopSession();
            }
            _startedLogger = false;
            if (_surprise != null) Destroy(_surprise.gameObject);
            _surprise = null;
            _phase = Phase.Idle;
        }

        void WriteSummary(bool found, double missSeconds)
        {
            try
            {
                string folder = CalibrationRunner.LogFolder();
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "room_summary.csv");
                bool isNew = !File.Exists(path);

                CalibrationRunner cal = FindAnyObjectByType<CalibrationRunner>();
                string participant = cal != null ? cal.participantCode : "P00";
                bool maskOn = filter != null && filter.filterEnabled;

                using (var w = new StreamWriter(path, true))
                {
                    if (isNew) w.WriteLine("time,participant,trial,mask_on,blind_side,offset_deg,result,miss_s,nudge,nudge_at_s,cue_mode");
                    w.WriteLine(string.Join(",",
                        System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", C),
                        participant,
                        _trialIndex.ToString(C),
                        maskOn ? "1" : "0",
                        BlindLeft() ? "Left" : "Right",
                        _spawnOffsetDeg.ToString("0.0", C),
                        found ? "found" : "timeout",
                        missSeconds.ToString("0.000", C),
                        _trialNudge ? "1" : "0",
                        _nudgeShown ? _nudgeAtSeconds.ToString("0.000", C) : "",
                        _trialCueMode));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Room] Could not write room_summary.csv: " + e.Message);
            }
        }

        // ---------- Helpers ----------

        bool FindReferences()
        {
            if (gaze == null) gaze = FindAnyObjectByType<GazeProvider>();
            if (filter == null) filter = FindAnyObjectByType<HemianopiaController>();
            if (logger == null) logger = FindAnyObjectByType<TrialLogger>();
            if (gaze == null)
            {
                Debug.LogWarning("[Room] No GazeProvider in the scene.");
                return false;
            }
            return true;
        }

        Camera Cam()
        {
            return gaze != null && gaze.viewCamera != null ? gaze.viewCamera : Camera.main;
        }

        bool BlindLeft()
        {
            return filter == null || filter.blindSide == BlindSide.Left;
        }

        void Queue(string label)
        {
            _events = _events.Length == 0 ? label : _events + ";" + label;
        }

        void HandleInput()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.digit5Key.wasPressedThisFrame) ToggleRoom();
                else if (kb.digit6Key.wasPressedThisFrame) StartSurprise();
                else if (kb.digit7Key.wasPressedThisFrame) ToggleNudge();
                else if (kb.digit9Key.wasPressedThisFrame) ToggleCuePlacement();
            }
#endif
            if (!useControllerInput) return;

            const OVRInput.Controller touch = OVRInput.Controller.Touch;
            if (OVRInput.GetDown(OVRInput.RawButton.LThumbstickRight, touch)) ToggleRoom();
            if (OVRInput.GetDown(OVRInput.RawButton.LThumbstickDown, touch)) StartSurprise();
            if (OVRInput.GetDown(OVRInput.RawButton.LThumbstickLeft, touch)) ToggleNudge();
        }
    }
}
