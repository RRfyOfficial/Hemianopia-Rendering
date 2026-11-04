using UnityEngine;
using Hemianopia.Gaze;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Rendering
{
    public enum BlindSide
    {
        Left,
        Right
    }

    /// <summary>
    /// Layer 3 controller: feeds the hemianopia filter shader (Shaders/HemianopiaFilter.shader)
    /// with fresh gaze data every frame.
    ///
    /// It runs inside GazeProvider.GazeUpdated, which fires right before rendering and after the
    /// headset's final head update, so the blind boundary lines up with the exact frame being drawn.
    ///
    /// It uses RAW gaze on purpose. Any smoothing would add lag, and a lagging boundary lets people
    /// peek into the blind half right after a saccade. See Docs/FILTER.md.
    ///
    /// Keys in Play mode: H toggles the filter, B flips the blind side.
    /// </summary>
    public class HemianopiaController : MonoBehaviour
    {
        [Tooltip("Turn the simulated hemianopia on or off. Toggle with H in Play mode.")]
        public bool filterEnabled = true;

        [Tooltip("Which half of the visual field is lost. Flip with B in Play mode.")]
        public BlindSide blindSide = BlindSide.Left;

        [Tooltip("What the blind half shows. Black for v0. Real patients perceive 'nothing', not black, which is a v1 research question.")]
        public Color blindColor = Color.black;

        static readonly int EnabledId    = Shader.PropertyToID("_HemiEnabled");
        static readonly int GazeRightId  = Shader.PropertyToID("_HemiGazeRightWS");
        static readonly int BlindSignId  = Shader.PropertyToID("_HemiBlindSign");
        static readonly int BlindColorId = Shader.PropertyToID("_HemiBlindColor");

        void OnEnable()
        {
            GazeProvider.GazeUpdated += Apply;
        }

        void OnDisable()
        {
            GazeProvider.GazeUpdated -= Apply;
            Shader.SetGlobalFloat(EnabledId, 0f); // never leave the filter stuck on
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            if (kb.hKey.wasPressedThisFrame)
            {
                filterEnabled = !filterEnabled;
                Debug.Log($"[Hemianopia] Filter {(filterEnabled ? "ON" : "OFF")}");
            }

            if (kb.bKey.wasPressedThisFrame)
            {
                blindSide = blindSide == BlindSide.Left ? BlindSide.Right : BlindSide.Left;
                Debug.Log($"[Hemianopia] Blind side: {blindSide}");
            }
#endif
        }

        void Apply(GazeProvider gaze)
        {
            if (!filterEnabled || !gaze.HasGaze || gaze.viewCamera == null)
            {
                Shader.SetGlobalFloat(EnabledId, 0f);
                return;
            }

            // RAW gaze, never the stable one. No smoothing, no added lag.
            Vector3 gazeForward = gaze.RawGazeRay.direction;

            // The blind boundary is the vertical line through wherever the eyes point.
            // "Vertical" follows the head, so tilting your head tilts the boundary with it.
            // right = up x forward (Unity is left-handed, so this points to the viewer's right).
            Vector3 headUp = gaze.viewCamera.transform.up;
            Vector3 gazeRight = Vector3.Cross(headUp, gazeForward);
            if (gazeRight.sqrMagnitude < 1e-6f)
                gazeRight = gaze.viewCamera.transform.right; // looking straight up or down, fall back
            gazeRight.Normalize();

            Shader.SetGlobalVector(GazeRightId, gazeRight);
            Shader.SetGlobalFloat(BlindSignId, blindSide == BlindSide.Left ? -1f : 1f);
            Shader.SetGlobalColor(BlindColorId, blindColor);
            Shader.SetGlobalFloat(EnabledId, 1f);
        }
    }
}
