using UnityEngine;
using Hemianopia.Gaze;
using Hemianopia.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Room
{
    /// <summary>
    /// Shows where the system thinks you are looking, as a laser.
    ///
    /// Two parts:
    ///   - The laser: a thin line that ends on the exact spot the gaze lands. A ray that starts between your
    ///     eyes would look like a single dot to you (you would be staring straight down it), so the line
    ///     starts a little below and in front of your face instead, like a laser pointer held at your chin.
    ///   - The beam ring: a circle around the landing spot, the size of the "flashlight beam" the LookTracker
    ///     uses. Anything whose centre is inside the ring (plus the object's own size) counts as looked at.
    ///
    /// It draws the same gaze the hemianopia mask and the LookTracker use (GazeProvider.FilterGazeRay).
    ///
    /// Press G (or push the RIGHT thumbstick left) to switch it on and off. It starts off.
    /// Leave it off for timed tests: a moving line in view is a distraction.
    /// </summary>
    public class GazeLaser : MonoBehaviour
    {
        [Tooltip("Show the laser. Toggle with G in Play mode.")]
        public bool visible = false;
        public Color color = new Color(1f, 0.25f, 0.35f);
        [Tooltip("Draw a circle the size of the LookTracker's beam around the spot the gaze lands on.")]
        public bool showBeamRing = true;
        [Tooltip("Beam half-width in degrees. Taken from the LookTracker when there is one.")]
        public float beamDegrees = 3f;
        public bool useControllerInput = true;

        const int RingSegments = 48;

        GameObject _root;
        LineRenderer _line;
        LineRenderer _ring;
        Material _material;
        LookTracker _tracker;
        HemianopiaController _filter;
        bool _searched;

        void OnEnable()
        {
            GazeProvider.GazeUpdated += OnGaze;
        }

        void OnDisable()
        {
            GazeProvider.GazeUpdated -= OnGaze;
            if (_root != null) Destroy(_root);
            if (_material != null) Destroy(_material);
            _root = null;
            _material = null;
            _line = null;
            _ring = null;
        }

        void Update()
        {
            if (TogglePressed())
            {
                visible = !visible;
                Debug.Log($"[Laser] Gaze laser {(visible ? "ON" : "OFF")}");
            }
        }

        // Runs right before rendering, so the laser matches the gaze used for this exact frame.
        void OnGaze(GazeProvider g)
        {
            if (!visible || !g.HasGaze)
            {
                if (_root != null && _root.activeSelf) _root.SetActive(false);
                return;
            }

            Camera cam = g.viewCamera != null ? g.viewCamera : Camera.main;
            if (cam == null) return;

            if (!_searched)
            {
                _tracker = FindAnyObjectByType<LookTracker>();
                _filter = FindAnyObjectByType<HemianopiaController>();
                _searched = true;
            }

            EnsureVisuals();
            if (!_root.activeSelf) _root.SetActive(true);

            Transform ct = cam.transform;
            Ray ray = g.FilterGazeRay;

            // Where the gaze lands: the same 3D point the mask uses.
            Vector3 hit = g.FilterFixationPoint;
            float distance = Vector3.Distance(ray.origin, hit);
            if (distance < 0.05f)
            {
                hit = ray.origin + ray.direction * 2f;
                distance = 2f;
            }

            // Start below and in front of the face, nudged toward the seeing side so the mask does not hide it.
            bool blindLeft = _filter == null || _filter.blindSide == BlindSide.Left;
            float seeingSign = blindLeft ? 1f : -1f;
            Vector3 start = ray.origin - ct.up * 0.12f + ct.right * (seeingSign * 0.05f) + ct.forward * 0.30f;

            _line.SetPosition(0, start);
            _line.SetPosition(1, hit);
            _line.startWidth = 0.004f;
            _line.endWidth = Mathf.Max(0.002f, distance * 0.0012f);

            _ring.enabled = showBeamRing;
            if (showBeamRing)
            {
                float degrees = _tracker != null ? _tracker.coneDegrees : beamDegrees;
                float radius = Mathf.Tan(degrees * Mathf.Deg2Rad) * distance;

                // Two directions at right angles to the gaze, to draw the circle facing the person.
                Vector3 dir = ray.direction;
                Vector3 u = Vector3.Cross(ct.up, dir);
                if (u.sqrMagnitude < 1e-6f) u = ct.right;
                u.Normalize();
                Vector3 v = Vector3.Cross(dir, u);
                Vector3 centre = hit - dir * 0.01f;

                for (int i = 0; i < RingSegments; i++)
                {
                    float a = i / (float)RingSegments * Mathf.PI * 2f;
                    _ring.SetPosition(i, centre + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius);
                }
                _ring.widthMultiplier = Mathf.Max(0.0015f, distance * 0.0012f);
            }
        }

        void EnsureVisuals()
        {
            if (_root != null) return;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            _material = new Material(shader);

            _root = new GameObject("GazeLaser");

            _line = MakeLine("laser");
            _line.positionCount = 2;
            _line.numCapVertices = 4;

            _ring = MakeLine("beam_ring");
            _ring.positionCount = RingSegments;
            _ring.loop = true;

            Color lineColor = color;
            lineColor.a = 0.9f;
            _line.startColor = lineColor;
            _line.endColor = lineColor;

            Color ringColor = color;
            ringColor.a = 0.6f;
            _ring.startColor = ringColor;
            _ring.endColor = ringColor;
        }

        LineRenderer MakeLine(string lineName)
        {
            GameObject go = new GameObject(lineName);
            go.transform.SetParent(_root.transform, false);
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.alignment = LineAlignment.View;
            lr.sharedMaterial = _material;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.gKey.wasPressedThisFrame) return true;
#endif
            return useControllerInput
                   && OVRInput.GetDown(OVRInput.RawButton.RThumbstickLeft, OVRInput.Controller.Touch);
        }
    }
}
