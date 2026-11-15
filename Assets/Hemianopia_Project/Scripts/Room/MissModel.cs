using UnityEngine;
using Hemianopia.Gaze;
using Hemianopia.Rendering;

namespace Hemianopia.Room
{
    /// <summary>
    /// The learned "miss time" model, running live.
    ///
    /// Ask it about any spot in the room and it answers: "left alone, this person would take about this many
    /// seconds to notice something there". The nudge uses that answer to decide whether to step in.
    ///
    /// It has two halves:
    ///   1. A short memory of the last 10 seconds of head and gaze (one entry per frame).
    ///   2. The model itself: eight facts about the spot, each multiplied by a learned weight and added up.
    ///      The weights live in MissModelWeights.cs, which Analysis/train_model.py writes. Nothing here is
    ///      tuned by hand. To change the model, record more data and retrain.
    ///
    /// The eight facts are worked out exactly the way Analysis/gaze_tools.py works them out from a
    /// recording (spot_features), so the model sees live what it saw in training. Keep the two in step.
    /// </summary>
    public class MissModel : MonoBehaviour
    {
        [Header("References (found automatically if left empty)")]
        public HemianopiaController filter;

        [Tooltip("How far back the model looks, in seconds. Must match the training script (10).")]
        public float historySeconds = 10f;

        // Same numbers as Analysis/gaze_tools.py.
        const float ObjectRadiusMeters = 0.10f;
        const float ViewHalfWidthDegrees = 48f;
        const float ViewHalfHeightDegrees = 42f;
        const float MaxSeconds = 20f;

        const int Capacity = 2048; // 10 s at up to about 200 frames a second

        struct Sample
        {
            public double time;
            public Vector3 headPosition;
            public Quaternion headRotationInverse;
            public float gazeAz;     // gaze direction in the head's frame, degrees
            public float gazeEl;
            public bool maskOn;
            public float blindSign;  // -1 when the left is blind, +1 when the right is
        }

        readonly Sample[] _samples = new Sample[Capacity];
        int _next;
        int _count;
        float _headSpeed;
        bool _searchedForFilter;
        readonly float[] _facts = new float[8];

        /// <summary>True once there is enough history and the weights match the eight facts.</summary>
        public bool IsReady => _count >= 30 && MissModelWeights.Weights.Length == _facts.Length;

        /// <summary>The eight facts from the latest PredictSeconds call, in the order of MissModelWeights.Features.</summary>
        public float[] LastFacts => _facts;

        void OnEnable()
        {
            GazeProvider.GazeUpdated += OnGaze;
        }

        void OnDisable()
        {
            GazeProvider.GazeUpdated -= OnGaze;
            _count = 0;
            _next = 0;
        }

        // Once per frame, right before rendering: the same moment the recorder writes its row.
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
            Vector2 gazeAngles = GazeCorrection.ToAngles(ct.InverseTransformDirection(g.FilterGazeRay.direction));

            Sample s;
            s.time = Time.realtimeSinceStartupAsDouble;
            s.headPosition = ct.position;
            s.headRotationInverse = Quaternion.Inverse(ct.rotation);
            s.gazeAz = gazeAngles.x;
            s.gazeEl = gazeAngles.y;
            s.maskOn = filter != null && filter.filterEnabled;
            s.blindSign = filter == null || filter.blindSide == BlindSide.Left ? -1f : 1f;

            _samples[_next] = s;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
            _headSpeed = g.HeadSpeedDps;
        }

        /// <summary>
        /// How long would an object at this spot go unnoticed if nobody stepped in? In seconds, 0 to 20.
        /// Returns -1 if the model is not ready yet.
        /// </summary>
        public float PredictSeconds(Vector3 point)
        {
            if (!IsReady) return -1f;

            int newest = (_next - 1 + Capacity) % Capacity;
            double now = _samples[newest].time;
            double earliest = now - historySeconds;

            float intoBlindNow = 0f, azNow = 0f, elNow = 0f;
            float beyondScan = float.MaxValue;
            double uncoveredTime = 0.0;
            double lastUncoveredAt = double.NegativeInfinity;
            double firstTime = now;
            double previousTime = now;
            bool first = true;

            // Walk the history from oldest to newest.
            int start = (_next - _count + Capacity) % Capacity;
            for (int k = 0; k < _count; k++)
            {
                Sample s = _samples[(start + k) % Capacity];
                if (s.time < earliest) continue;

                Vector3 to = point - s.headPosition;
                float distance = to.magnitude;
                if (distance < 1e-4f) continue;
                Vector3 h = s.headRotationInverse * (to / distance); // direction to the spot, in the head's frame
                float az = Mathf.Atan2(h.x, h.z) * Mathf.Rad2Deg;
                float el = Mathf.Atan2(h.y, Mathf.Sqrt(h.x * h.x + h.z * h.z)) * Mathf.Rad2Deg;

                // How far the spot is on the blind side of the gaze, left/right only.
                float intoBlind = s.blindSign * Mathf.DeltaAngle(s.gazeAz, az);
                float radius = Mathf.Atan2(ObjectRadiusMeters, distance) * Mathf.Rad2Deg;

                bool hidden = s.maskOn && intoBlind > radius;
                bool inView = Mathf.Abs(az) <= ViewHalfWidthDegrees && Mathf.Abs(el) <= ViewHalfHeightDegrees;
                bool uncovered = !hidden && inView;

                if (first)
                {
                    firstTime = s.time;
                    first = false;
                }
                else if (uncovered)
                {
                    uncoveredTime += s.time - previousTime;
                }
                if (uncovered) lastUncoveredAt = s.time;
                previousTime = s.time;

                if (intoBlind < beyondScan) beyondScan = intoBlind;
                intoBlindNow = intoBlind;
                azNow = az;
                elNow = el;
            }
            if (first) return -1f;

            Sample latest = _samples[newest];
            double span = now - firstTime;

            _facts[0] = intoBlindNow;                                        // into_blind_deg
            _facts[1] = Mathf.Abs(elNow - latest.gazeEl);                    // up_down_deg
            _facts[2] = latest.blindSign * azNow;                            // from_head_deg
            _facts[3] = double.IsNegativeInfinity(lastUncoveredAt)           // since_uncovered_s
                ? historySeconds
                : Mathf.Min((float)(now - lastUncoveredAt), historySeconds);
            _facts[4] = span > 1e-6 ? (float)(uncoveredTime / span) : 0f;    // uncovered_share
            _facts[5] = beyondScan;                                          // beyond_scan_deg
            _facts[6] = _headSpeed;                                          // head_speed_dps
            _facts[7] = latest.blindSign * latest.gazeAz;                    // eyes_toward_blind_deg

            // guess = expm1( sum of weight * (fact - mean) / spread, plus the bias )
            float z = MissModelWeights.Bias;
            for (int i = 0; i < _facts.Length; i++)
                z += MissModelWeights.Weights[i] * (_facts[i] - MissModelWeights.Mean[i]) / MissModelWeights.Std[i];

            return Mathf.Clamp(Mathf.Exp(z) - 1f, 0f, MaxSeconds);
        }
    }
}
