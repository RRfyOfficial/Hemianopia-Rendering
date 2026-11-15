using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Hemianopia.Calibration;
using Hemianopia.Gaze;
using Hemianopia.Logging;
using Hemianopia.Rendering;
using Hemianopia.UI;
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
    ///                  P = busy job on or off (see BusyJob)
    ///                  N = run a block of surprises back to back, with the busy job (press again to stop early)
    ///                  F = record free looking: the busy job only, no surprises (press again to stop early)
    ///                  N and F refuse to start while the mask is off (the shapes flash red), because in the dark
    ///                  room it is easy not to notice. Hold Shift to run them with the mask off on purpose.
    ///
    /// The status panel (key I) shows what the room test is doing.
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

        [Header("Variety")]
        [Tooltip("When on, each surprise appears at a random distance into the blind side and a random height, " +
                 "so the recordings cover many different situations.")]
        public bool varySurprises = true;
        [Tooltip("Random range for how far into the blind side the surprise appears, in degrees.")]
        public Vector2 offsetRangeDegrees = new Vector2(12f, 40f);
        [Tooltip("The surprise appears up to this many degrees above or below the gaze.")]
        public float elevationRangeDegrees = 15f;

        [Header("Blocks and free looking (for collecting data)")]
        [Tooltip("How many surprises in one block (key N).")]
        public int blockTrials = 10;
        [Tooltip("In a block, each surprise appears after a random wait in this range, in seconds.")]
        public Vector2 blockWaitRangeSeconds = new Vector2(3f, 9f);
        [Tooltip("How long a free-looking recording lasts (key F), in seconds.")]
        public float freeLookSeconds = 300f;

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

        readonly List<TrackedObject> _shapes = new List<TrackedObject>(); // the room's five shapes
        BusyJob _job;
        float _trialOffsetDeg;       // how far into the blind side this surprise aims for
        float _trialElevationRange;  // how far above or below the gaze it may appear
        bool _inBlock;
        bool _blockAbort;
        int _blockRemaining;
        int _blockDone;
        bool _freeLook;
        double _freeLookEndAt;
        bool _sessionLogger;          // the recorder was started by a block or a free-look recording
        bool _sessionWanted;          // a block or free-look recording is supposed to be running right now
        string _sessionLabel = "";
        double _nextRestartTry;
        bool _loggerToggleWas;        // the recorder's own controller shortcut, switched off during a session
        static readonly Color WarnColor = new Color(0.95f, 0.20f, 0.15f);
        int _stopLoggerInFrames = -1; // counts down so the last event is written before the recorder stops

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
            _job = GetComponent<BusyJob>();
            if (_job == null) _job = gameObject.AddComponent<BusyJob>();
            _job.Marked += Queue;
        }

        void OnDisable()
        {
            if (_tracker != null) _tracker.FirstLook -= OnFirstLook;
            if (_cue != null) _cue.Hide();
            if (_job != null)
            {
                _job.Marked -= Queue;
                _job.End();
            }
            if ((_startedLogger || _sessionLogger) && logger != null)
            {
                logger.ClearTarget();
                logger.StopSession();
            }
            _startedLogger = false;
            _sessionLogger = false;
            if (_sessionWanted && logger != null) logger.useControllerToggle = _loggerToggleWas;
            _sessionWanted = false;
            _stopLoggerInFrames = -1;
            _inBlock = false;
            _freeLook = false;
            _phase = Phase.Idle;
            StatusHud.SetBanner(null);
            StatusHud.SetLine("room", null);
            ClearRoom();
        }

        void Update()
        {
            // A block or free-look recording ended last frame: its final event has been written, so stop now.
            if (_stopLoggerInFrames > 0 && --_stopLoggerInFrames == 0 && logger != null)
            {
                logger.ClearTarget();
                logger.StopSession();
            }

            // Keep the session's recording alive. On Nov 14 the recorder was switched off about a second into
            // a block by something outside this script, and one surprise went unrecorded.
            if (_sessionWanted && recordWithLogger && logger != null && !logger.IsRecording
                && Time.realtimeSinceStartupAsDouble >= _nextRestartTry)
            {
                _nextRestartTry = Time.realtimeSinceStartupAsDouble + 1.0; // at most one try per second
                Debug.LogWarning("[Room] The recorder stopped in the middle of a session (something else switched it off). " +
                                 "Restarted it in a new file.");
                logger.sessionLabel = _sessionLabel;
                _sessionLogger = logger.StartSession();
                QueueLayout();
            }

            HandleInput();

            double now = Time.realtimeSinceStartupAsDouble;
            if (_freeLook && now >= _freeLookEndAt) EndFreeLook();

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
            if (_phase != Phase.Idle || _inBlock || _freeLook) return;
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
                _shapes.Add(CreateShape(ShapeKinds[i], ShapeLabels[i], ShapeColors[i], pos, shapeSize));
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
            if (_job != null) _job.End();
            _shapes.Clear();
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
            UpdateHud();
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
            if (_phase != Phase.Idle || _freeLook) return;
            if (!FindReferences()) return;
            if (_root == null) BuildRoom();
            if (_root == null) return;

            bool maskOn = filter != null && filter.filterEnabled;
            _trialIndex++;

            double now = Time.realtimeSinceStartupAsDouble;
            Vector2 wait = _inBlock ? blockWaitRangeSeconds : waitRangeSeconds;
            _spawnAt = now + Random.Range(wait.x, wait.y);
            _forceSpawnAt = _spawnAt + 5.0;

            _trialOffsetDeg = varySurprises ? Random.Range(offsetRangeDegrees.x, offsetRangeDegrees.y) : surpriseOffsetDegrees;
            _trialElevationRange = varySurprises ? elevationRangeDegrees : 8f;

            _trialNudge = nudgeEnabled;
            _trialCueMode = _trialNudge && _cue != null
                ? (_cue.placement == CuePlacement.StayInRoom ? "stay" : "follow")
                : "";
            _nudgeShown = false;
            _nudgeAtSeconds = 0.0;

            _startedLogger = false;
            if (logger != null)
            {
                logger.SetTrial(_trialIndex, (maskOn ? "surprise_mask_on" : "surprise_mask_off")
                                             + (_trialNudge ? "_nudge" : "")
                                             + (_job != null && _job.IsRunning ? "_job" : ""));
                if (recordWithLogger && !_sessionWanted && !logger.IsRecording)
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
            float az = Mathf.Clamp(gazeAz + sign * _trialOffsetDeg, -maxHeadAzimuthDegrees, maxHeadAzimuthDegrees);
            float offset = sign * (az - gazeAz);
            if (offset < minOffsetDegrees)
            {
                if (!force) return; // looking too far toward the blind side: wait for a better moment
                az = gazeAz + sign * minOffsetDegrees;
                offset = minOffsetDegrees;
            }

            // Pick a height that doesn't sit on top of one of the room's shapes.
            float baseEl = Random.Range(-_trialElevationRange, _trialElevationRange);
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

            if (_inBlock)
            {
                _blockDone++;
                _blockRemaining--;
                if (_blockRemaining > 0 && !_blockAbort)
                {
                    StartSurprise();
                    UpdateHud();
                }
                else
                {
                    EndBlock();
                }
            }
        }

        // ---------- Collecting data: the busy job, blocks, and free looking ----------

        /// <summary>Busy job on or off, with nothing else going on. Good for getting used to it.</summary>
        public void ToggleJob()
        {
            if (_job == null || _inBlock || _freeLook) return;
            if (_job.IsRunning)
            {
                _job.End();
                StatusHud.SetBanner(null);
                UpdateHud();
                Debug.Log("[Room] Busy job OFF.");
                return;
            }
            if (!FindReferences()) return;
            if (_root == null) BuildRoom();
            if (_root == null) return;
            _job.Begin(_shapes);
            StatusHud.SetBanner(JobBanner);
            UpdateHud();
            Debug.Log("[Room] Busy job ON. Look at whichever shape has the cyan ring. It may be on your blind side.");
        }

        const string JobBanner = "Your job: look at the shape with the cyan ring";

        bool MaskOn()
        {
            return filter != null && filter.filterEnabled;
        }

        // Blocks and free looking are for collecting blind-side data, so they need the mask. In the dark room
        // it is easy not to notice that it is off (a whole block was recorded that way on Nov 14).
        bool RefuseBecauseMaskOff(bool allowMaskOff, string key)
        {
            if (MaskOn() || allowMaskOff) return false;
            for (int i = 0; i < _shapes.Count; i++)
                if (_shapes[i] != null) _shapes[i].Flash(WarnColor, 1.2f);
            StatusHud.SetLine("room", $"<color=#ff8a5d>Mask is OFF. Press H, then {key}. (Shift+{key} runs it with the mask off.)</color>");
            Debug.LogWarning($"[Room] Not started: the mask is OFF. Press H, then {key}. Hold Shift with {key} to run it with the mask off on purpose.");
            return true;
        }

        // One line on the status panel (key I) saying what the room test is doing.
        void UpdateHud()
        {
            string nudge = nudgeEnabled ? "<color=#ffd75a>ON</color>" : "OFF";
            string mask = MaskOn() ? "<color=#5dff8a>ON</color>" : "<color=#ff8a5d>OFF</color>";
            string text;
            if (_inBlock)
                text = $"Room: block, surprise {_blockDone + 1} of {_blockDone + _blockRemaining}   mask {mask}   nudge {nudge}";
            else if (_freeLook)
                text = $"Room: free looking, recording   mask {mask}   (F stops it)";
            else
                text = $"Room: idle   nudge {nudge}   (N = block, F = free looking)";
            StatusHud.SetLine("room", text);
        }

        /// <summary>
        /// A block: several surprises back to back, with the busy job running the whole time, saved as ONE
        /// recording. Press N again to stop after the current surprise.
        /// </summary>
        public void StartBlock(bool allowMaskOff = false)
        {
            if (_inBlock)
            {
                _blockAbort = true;
                Debug.Log("[Room] Block will stop after this surprise.");
                return;
            }
            if (_phase != Phase.Idle || _freeLook) return;
            if (!FindReferences()) return;
            if (_root == null) BuildRoom();
            if (_root == null) return;
            if (RefuseBecauseMaskOff(allowMaskOff, "N")) return;

            BeginSessionLogging("roomblock");
            _job.Begin(_shapes);
            _inBlock = true;
            _blockAbort = false;
            _blockRemaining = Mathf.Max(1, blockTrials);
            _blockDone = 0;
            Queue("block_start");
            Debug.Log($"[Room] Block of {_blockRemaining} surprises started (mask {(MaskOn() ? "ON" : "OFF")}, nudge {(nudgeEnabled ? "ON" : "OFF")}). " +
                      "Your job: look at whichever shape has the cyan ring. The shapes flash green when the block is done.");
            StartSurprise();
            StatusHud.SetBanner(JobBanner);
            UpdateHud();
        }

        void EndBlock()
        {
            _inBlock = false;
            _blockAbort = false;
            int hits = _job != null ? _job.Hits : 0;
            int targets = _job != null ? _job.Targets : 0;
            if (_job != null) _job.End();
            Queue("block_end");
            EndSessionLogging();
            FlashShapes();
            StatusHud.SetBanner(null);
            UpdateHud();
            Debug.Log($"[Room] Block finished: {_blockDone} surprises. Busy job: looked at {hits} of {targets} pulsing shapes.");
        }

        /// <summary>
        /// Free looking: the busy job only, no surprises, recorded for freeLookSeconds. This is the recording
        /// that imaginary objects get placed on afterwards (see Docs/DEVLOG.md, Nov 14).
        /// </summary>
        public void ToggleFreeLook(bool allowMaskOff = false)
        {
            if (_freeLook)
            {
                EndFreeLook();
                return;
            }
            if (_phase != Phase.Idle || _inBlock) return;
            if (!FindReferences()) return;
            if (_root == null) BuildRoom();
            if (_root == null) return;
            if (RefuseBecauseMaskOff(allowMaskOff, "F")) return;

            bool maskOn = filter != null && filter.filterEnabled;
            if (logger != null) logger.SetTrial(0, maskOn ? "freelook_mask_on_job" : "freelook_mask_off_job");
            BeginSessionLogging("freelook");
            _job.Begin(_shapes);
            _freeLook = true;
            _freeLookEndAt = Time.realtimeSinceStartupAsDouble + Mathf.Max(5f, freeLookSeconds);
            Queue("freelook_start");
            StatusHud.SetBanner(JobBanner);
            UpdateHud();
            Debug.Log($"[Room] Free looking started for {freeLookSeconds.ToString("0", C)} seconds (mask {(maskOn ? "ON" : "OFF")}). " +
                      "Your job: look at whichever shape has the cyan ring. No surprises. Press F to stop early.");
        }

        void EndFreeLook()
        {
            _freeLook = false;
            int hits = _job != null ? _job.Hits : 0;
            int targets = _job != null ? _job.Targets : 0;
            if (_job != null) _job.End();
            Queue("freelook_end");
            EndSessionLogging();
            FlashShapes();
            StatusHud.SetBanner(null);
            UpdateHud();
            Debug.Log($"[Room] Free looking finished. Busy job: looked at {hits} of {targets} pulsing shapes.");
        }

        // One recording for the whole block or free-look session. Also notes where each shape is, so the
        // analysis knows what was in the room.
        void BeginSessionLogging(string label)
        {
            _sessionLogger = false;
            _stopLoggerInFrames = -1;
            _sessionLabel = label;
            _sessionWanted = true;
            if (logger != null)
            {
                // The recorder has its own controller shortcut (right thumbstick up). Switch it off for the
                // session so a bumped thumbstick cannot stop the recording.
                _loggerToggleWas = logger.useControllerToggle;
                logger.useControllerToggle = false;
            }
            if (logger != null && recordWithLogger && !logger.IsRecording)
            {
                logger.sessionLabel = label;
                _sessionLogger = logger.StartSession();
            }
            QueueLayout();
        }

        // Notes where each shape is, so the analysis knows what was in the room.
        void QueueLayout()
        {
            for (int i = 0; i < _shapes.Count; i++)
            {
                TrackedObject s = _shapes[i];
                if (s == null) continue;
                Vector3 p = s.transform.position;
                Queue($"shape:{s.label}@{p.x.ToString("F3", C)}|{p.y.ToString("F3", C)}|{p.z.ToString("F3", C)}");
            }
        }

        void EndSessionLogging()
        {
            _sessionWanted = false;
            if (logger != null) logger.useControllerToggle = _loggerToggleWas;
            if (_sessionLogger) _stopLoggerInFrames = 1; // stop next frame, after the last event is written
            _sessionLogger = false;
        }

        void FlashShapes()
        {
            for (int i = 0; i < _shapes.Count; i++)
                if (_shapes[i] != null) _shapes[i].Flash(FoundColor, 1.2f);
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
                    if (isNew) w.WriteLine("time,participant,trial,mask_on,blind_side,offset_deg,result,miss_s,nudge,nudge_at_s,cue_mode,job");
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
                        _trialCueMode,
                        _job != null && _job.IsRunning ? "1" : "0"));
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
                else if (kb.pKey.wasPressedThisFrame) ToggleJob();
                else if (kb.nKey.wasPressedThisFrame) StartBlock(kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
                else if (kb.fKey.wasPressedThisFrame) ToggleFreeLook(kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
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
