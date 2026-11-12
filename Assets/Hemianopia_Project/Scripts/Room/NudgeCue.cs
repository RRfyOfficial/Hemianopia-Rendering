using UnityEngine;
using Hemianopia.Gaze;
using Hemianopia.Rendering;

namespace Hemianopia.Room
{
    /// <summary>
    /// The nudge: a short row of glowing chevrons that tells the person "look over there".
    ///
    /// It sits just beside where the person is looking, on their SEEING side (anything drawn on the blind
    /// side would be hidden by the mask), and points toward the object they have missed. The chevrons light
    /// up one after another toward the object, like runway lights, so the direction reads at a glance.
    ///
    /// It follows the same gaze the hemianopia filter uses, and is placed at about the depth the person is
    /// looking at, so it is comfortable to see in the headset.
    ///
    /// This script only DRAWS the nudge. Deciding WHEN to nudge is someone else's job (RoomTest for now).
    ///     cue.Show(target);   // start pointing at a tracked object
    ///     cue.Hide();         // fade out
    ///     cue.Preview(1.2f);  // flash it briefly toward the blind side, to show what it looks like
    /// </summary>
    public class NudgeCue : MonoBehaviour
    {
        [Header("References (found automatically if left empty)")]
        public HemianopiaController filter;

        [Header("Look")]
        public Color color = new Color(1f, 0.72f, 0.15f);
        [Tooltip("How many chevrons in the row.")]
        [Range(1, 5)] public int chevrons = 3;
        [Tooltip("How far beside the gaze the row sits, toward the seeing side, in degrees.")]
        public float offsetDegrees = 5f;
        [Tooltip("Half the height of one chevron, in degrees.")]
        public float chevronHeightDegrees = 1.3f;
        [Tooltip("How pointy one chevron is, in degrees.")]
        public float chevronDepthDegrees = 0.9f;
        [Tooltip("Gap between chevrons, in degrees.")]
        public float spacingDegrees = 1.4f;
        [Tooltip("Line thickness, in degrees.")]
        public float lineWidthDegrees = 0.22f;

        [Header("Motion")]
        [Tooltip("How many times per second the light runs along the row.")]
        public float pulsesPerSecond = 1.6f;
        [Tooltip("Fade in and out time, in seconds.")]
        public float fadeSeconds = 0.2f;

        /// <summary>True while the cue is pointing at something (or previewing).</summary>
        public bool IsShowing => _target != null || Time.unscaledTime < _previewUntil;

        TrackedObject _target;
        Vector3 _lastTargetPos;
        float _previewUntil = -1f;
        float _alpha;

        GameObject _root;
        LineRenderer[] _core;
        LineRenderer[] _glow;
        Material _material;
        bool _searchedForFilter;

        // ---------- Public API ----------

        public void Show(TrackedObject target)
        {
            _target = target;
            if (target != null) _lastTargetPos = target.transform.position;
        }

        public void Hide()
        {
            _target = null;
            _previewUntil = -1f;
        }

        /// <summary>Flash the cue toward the blind side for a moment, with no target.</summary>
        public void Preview(float seconds)
        {
            _previewUntil = Time.unscaledTime + Mathf.Max(0.1f, seconds);
        }

        // ---------- Unity ----------

        void OnEnable()
        {
            GazeProvider.GazeUpdated += OnGaze;
        }

        void OnDisable()
        {
            GazeProvider.GazeUpdated -= OnGaze;
            _target = null;
            _alpha = 0f;
            if (_root != null) Destroy(_root);
            if (_material != null) Destroy(_material);
            _root = null;
            _material = null;
            _core = null;
            _glow = null;
        }

        // Runs right before rendering, after the headset's final head move, so the cue never lags the gaze.
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

            bool previewing = Time.unscaledTime < _previewUntil;
            if (_target != null) _lastTargetPos = _target.transform.position;

            // Only point while the target is still on the blind side. Once the person has turned far enough
            // that it is on their seeing side, they can see it themselves.
            bool want = previewing || (_target != null && _target.OnBlindSide);

            float step = fadeSeconds > 0.001f ? Time.unscaledDeltaTime / fadeSeconds : 1f;
            _alpha = Mathf.MoveTowards(_alpha, want ? 1f : 0f, step);

            if (_alpha <= 0f)
            {
                if (_root != null && _root.activeSelf) _root.SetActive(false);
                return;
            }

            EnsureVisuals();
            if (!_root.activeSelf) _root.SetActive(true);

            Transform ct = cam.transform;
            Ray ray = g.FilterGazeRay;
            Vector3 eye = ray.origin;

            bool blindLeft = filter == null || filter.blindSide == BlindSide.Left;
            float seeingSign = blindLeft ? 1f : -1f; // turning right (+) is toward the seeing side when the left is blind

            // Where the row sits: a few degrees beside the gaze, on the seeing side.
            Vector3 cueDir = Quaternion.AngleAxis(seeingSign * offsetDegrees, ct.up) * ray.direction;

            // What it points at. In a preview there is no target, so point 30 degrees into the blind side.
            Vector3 targetPos = previewing && _target == null
                ? eye + (Quaternion.AngleAxis(-seeingSign * 30f, ct.up) * ray.direction) * 2f
                : _lastTargetPos;

            // The pointing direction, flattened onto the "screen" facing the person.
            Vector3 toTarget = (targetPos - eye).normalized;
            Vector3 point = toTarget - Vector3.Dot(toTarget, cueDir) * cueDir;
            if (point.sqrMagnitude < 1e-6f) point = -seeingSign * ct.right;
            point.Normalize();
            Vector3 up = Vector3.Cross(cueDir, point);

            // Depth: about where the person is looking, a little in front, so it is easy on the eyes.
            float depth = Mathf.Clamp(Vector3.Distance(eye, g.FilterFixationPoint) * 0.9f, 0.5f, 2.5f);

            _root.transform.SetPositionAndRotation(eye + cueDir * depth, Quaternion.LookRotation(cueDir, up));

            // Sizes are set in degrees, so the cue looks the same size at any depth.
            float perDegree = Mathf.Tan(Mathf.Deg2Rad) * depth;
            float h = chevronHeightDegrees * perDegree;
            float d = chevronDepthDegrees * perDegree;
            float gap = spacingDegrees * perDegree;
            float width = lineWidthDegrees * perDegree;

            int n = _core.Length;
            float x0 = -0.5f * ((n - 1) * gap + d); // centre the row on its spot
            float phase = Time.unscaledTime * pulsesPerSecond;

            for (int i = 0; i < n; i++)
            {
                float x = x0 + i * gap;
                Vector3 a = new Vector3(x, h, 0f);
                Vector3 tip = new Vector3(x + d, 0f, 0f);
                Vector3 b = new Vector3(x, -h, 0f);

                // The light runs from the chevron furthest from the target to the one nearest it.
                float p = Mathf.Repeat(phase - i / (float)n, 1f);
                float brightness = 0.25f + 0.75f * Mathf.Clamp01(1f - p * 2f);

                Color core = color;
                core.a = _alpha * brightness;
                Color glow = color;
                glow.a = _alpha * brightness * 0.22f;

                Apply(_core[i], a, tip, b, width, core);
                Apply(_glow[i], a, tip, b, width * 3.2f, glow);
            }
        }

        static void Apply(LineRenderer lr, Vector3 a, Vector3 tip, Vector3 b, float width, Color c)
        {
            lr.SetPosition(0, a);
            lr.SetPosition(1, tip);
            lr.SetPosition(2, b);
            lr.widthMultiplier = width;
            lr.startColor = c;
            lr.endColor = c;
        }

        // ---------- Visuals ----------

        void EnsureVisuals()
        {
            int n = Mathf.Clamp(chevrons, 1, 5);
            if (_root != null && _core != null && _core.Length == n) return;

            if (_root != null) Destroy(_root);
            if (_material == null)
            {
                // Built into Unity, always available, see-through, and not affected by lights.
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                _material = new Material(shader);
            }

            _root = new GameObject("NudgeCue_Chevrons");
            _core = new LineRenderer[n];
            _glow = new LineRenderer[n];
            for (int i = 0; i < n; i++)
            {
                _glow[i] = MakeLine("glow" + i);
                _core[i] = MakeLine("core" + i);
            }
        }

        LineRenderer MakeLine(string lineName)
        {
            GameObject go = new GameObject(lineName);
            go.transform.SetParent(_root.transform, false);
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 3;
            lr.numCornerVertices = 4;
            lr.numCapVertices = 4;
            lr.alignment = LineAlignment.View;
            lr.sharedMaterial = _material;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }
    }
}
