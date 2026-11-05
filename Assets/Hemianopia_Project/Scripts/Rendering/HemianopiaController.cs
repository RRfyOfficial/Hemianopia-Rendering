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
    /// Controls in Play mode:
    ///   Keyboard:   H toggles the filter, B flips the blind side
    ///   Controllers: A, B, X or Y toggles the filter, either index trigger flips the blind side
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
        static readonly int FixationId   = Shader.PropertyToID("_HemiFixationWS");
        static readonly int HeadUpId     = Shader.PropertyToID("_HemiHeadUpWS");
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
            if (TogglePressed()) ToggleFilter();
            if (FlipPressed()) FlipBlindSide();
        }

        /// <summary>Turns the simulated hemianopia on or off.</summary>
        public void ToggleFilter()
        {
            filterEnabled = !filterEnabled;
            Debug.Log($"[Hemianopia] Filter {(filterEnabled ? "ON" : "OFF")}");
        }

        /// <summary>Switches which half of the visual field is blind.</summary>
        public void FlipBlindSide()
        {
            blindSide = blindSide == BlindSide.Left ? BlindSide.Right : BlindSide.Left;
            Debug.Log($"[Hemianopia] Blind side: {blindSide}");
        }

        // Keyboard H, or any face button on either Touch controller (A, B on the right, X, Y on the left).
        static bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame) return true;
#endif
            const OVRInput.Controller touch = OVRInput.Controller.Touch; // both controllers
            return OVRInput.GetDown(OVRInput.RawButton.A, touch)
                || OVRInput.GetDown(OVRInput.RawButton.B, touch)
                || OVRInput.GetDown(OVRInput.RawButton.X, touch)
                || OVRInput.GetDown(OVRInput.RawButton.Y, touch);
        }

        // Keyboard B, or the index trigger (the front trigger under your pointer finger) on either controller.
        static bool FlipPressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.bKey.wasPressedThisFrame) return true;
#endif
            const OVRInput.Controller touch = OVRInput.Controller.Touch;
            return OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger, touch)
                || OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger, touch);
        }

        void Apply(GazeProvider gaze)
        {
            if (!filterEnabled || !gaze.HasGaze || gaze.viewCamera == null)
            {
                Shader.SetGlobalFloat(EnabledId, 0f);
                return;
            }

            // RAW fixation point, never the stable one. No smoothing, no added lag.
            // This is the 3D spot both eyes are looking at. The shader draws each eye's boundary
            // through it, so the two boundaries cross where you look instead of running parallel.
            Vector3 fixation = gaze.RawFixationPoint;

            // "Vertical" follows the head, so tilting your head tilts the boundary with it.
            Vector3 headUp = gaze.viewCamera.transform.up;

            Shader.SetGlobalVector(FixationId, fixation);
            Shader.SetGlobalVector(HeadUpId, headUp);
            Shader.SetGlobalFloat(BlindSignId, blindSide == BlindSide.Left ? -1f : 1f);
            Shader.SetGlobalColor(BlindColorId, blindColor);
            Shader.SetGlobalFloat(EnabledId, 1f);
        }
    }
}
