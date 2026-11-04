using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Gaze
{
    /// <summary>
    /// Debug marker that sits wherever the person is looking.
    /// Green = real eyes, red = mouse stand-in.
    ///
    /// It's placed inside GazeProvider.GazeUpdated, which fires right before rendering and after the
    /// headset's final head update, so the dot can't slip behind the head when you turn.
    /// It keeps the same apparent size at any distance, like a laser pointer dot.
    ///
    /// Press R (keyboard) during Play to switch between stable gaze and raw gaze, to compare them.
    /// </summary>
    public class GazeDot : MonoBehaviour
    {
        [Tooltip("On: follow the stabilized gaze (calm). Off: follow the raw gaze (every wobble). Toggle with R in Play mode.")]
        public bool useStableGaze = true;

        [Tooltip("Size of the dot in degrees of visual angle.")]
        public float angularSizeDegrees = 0.8f;

        [Tooltip("How far to look for a surface to land on, in meters.")]
        public float maxDistance = 30f;

        [Tooltip("Where to float the dot when you're looking at empty space, in meters.")]
        public float fallbackDistance = 2f;

        public Color eyeTrackingColor = new Color(0.1f, 0.9f, 0.4f); // green = real eyes
        public Color mouseColor = new Color(1f, 0.3f, 0.2f);         // red = mouse stand-in

        Transform _dot;
        Material _material;

        void OnEnable()  { GazeProvider.GazeUpdated += Place; }
        void OnDisable() { GazeProvider.GazeUpdated -= Place; }

        void Start()
        {
            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = "GazeDot (visual)";
            Destroy(dot.GetComponent<Collider>()); // so the gaze ray never hits the dot itself
            _material = dot.GetComponent<Renderer>().material;
            _dot = dot.transform;
            _dot.SetParent(transform, false);
            _dot.gameObject.SetActive(false);
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            {
                useStableGaze = !useStableGaze;
                Debug.Log($"[GazeDot] Showing {(useStableGaze ? "STABLE" : "RAW")} gaze");
            }
#endif
        }

        void Place(GazeProvider gaze)
        {
            if (_dot == null) return;
            if (!gaze.HasGaze)
            {
                _dot.gameObject.SetActive(false);
                return;
            }

            _dot.gameObject.SetActive(true);
            Ray ray = useStableGaze ? gaze.StableGazeRay : gaze.RawGazeRay;

            float distance = Physics.Raycast(ray, out RaycastHit hit, maxDistance)
                ? hit.distance * 0.98f // pull slightly toward the eye so it doesn't sink into the surface
                : fallbackDistance;

            _dot.position = ray.GetPoint(distance);
            float size = 2f * distance * Mathf.Tan(0.5f * angularSizeDegrees * Mathf.Deg2Rad);
            _dot.localScale = Vector3.one * size;
            _material.color = gaze.IsEyeTracking ? eyeTrackingColor : mouseColor;
        }
    }
}
