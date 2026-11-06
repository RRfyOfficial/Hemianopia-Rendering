using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Hemianopia.Calibration;
using Hemianopia.Gaze;
using Hemianopia.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.UI
{
    /// <summary>
    /// A small status panel that floats just below your view inside the headset, so you can see what's
    /// going on without taking the headset off or reading the console:
    ///   - gaze source (eyes or mouse)
    ///   - calibration correction: ON (green), OFF (orange), or none yet
    ///   - the last calibration's numbers (error before and after, noise at center and outer dots)
    ///   - the hemianopia filter's state
    /// Whenever something gets switched, a big line flashes for a moment.
    ///
    /// It's a normal object in the scene, so the hemianopia filter hides it too when it falls on the
    /// blind side. Look at its left edge to read it, or press H to turn the filter off.
    ///
    /// Toggle the panel with I (keyboard) or the left controller's menu button.
    /// </summary>
    [DisallowMultipleComponent]
    public class StatusHud : MonoBehaviour
    {
        [Tooltip("Show the panel. Toggle with I or the left menu button in Play mode.")]
        public bool visible = true;

        [Tooltip("Where the panel sits, in degrees right (+) or left (-) of straight ahead.")]
        public float azimuthDegrees = 4f;

        [Tooltip("Where the panel sits, in degrees above (+) or below (-) straight ahead.")]
        public float elevationDegrees = -20f;

        [Tooltip("How far away the panel floats, in meters.")]
        public float distance = 1.2f;

        [Tooltip("Height of one line of text, in degrees of visual angle.")]
        public float lineHeightDegrees = 1.3f;

        [Tooltip("How long the big 'something changed' line stays up, in seconds.")]
        public float flashSeconds = 1.5f;

        [Header("Hooks (found automatically if left empty)")]
        public GazeProvider gaze;
        public HemianopiaController hemianopia;
        public CalibrationRunner calibration;

        const float MetersPerCanvasUnit = 0.001f; // 1 canvas unit = 1 mm
        const float Padding = 12f;                // canvas units (mm)

        GameObject _panel;
        RectTransform _panelRect;
        Text _text;
        int _fontSize;

        string _flash;
        float _flashUntil;
        bool _primed;
        bool _lastApply, _lastFilter;
        BlindSide _lastSide;
        CalibrationResult _lastResult;
        readonly StringBuilder _sb = new StringBuilder();

        void Awake()
        {
            if (gaze == null) gaze = FindAnyObjectByType<GazeProvider>();
            if (hemianopia == null) hemianopia = FindAnyObjectByType<HemianopiaController>();
            if (calibration == null) calibration = FindAnyObjectByType<CalibrationRunner>();
        }

        void Start()
        {
            Build();
        }

        void OnDestroy()
        {
            if (_panel != null) Destroy(_panel);
        }

        void Update()
        {
            if (TogglePressed()) visible = !visible;
            if (_panel == null) Build();
            if (_panel == null || gaze == null) return;

            if (_panel.activeSelf != visible) _panel.SetActive(visible);
            if (!visible) return;

            DetectChanges();
            _text.text = Compose();

            // Size the dark background to the text.
            _panelRect.sizeDelta = new Vector2(_text.preferredWidth + 2f * Padding, _text.preferredHeight + 2f * Padding);
        }

        // A head-locked world-space canvas: a dark panel with white text, parented to the head camera.
        void Build()
        {
            Camera cam = gaze != null && gaze.viewCamera != null ? gaze.viewCamera : Camera.main;
            if (cam == null) return;

            _panel = new GameObject("Status HUD", typeof(RectTransform), typeof(Canvas), typeof(Image));
            _panel.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            _panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            _panelRect = _panel.GetComponent<RectTransform>();
            _panelRect.SetParent(cam.transform, false);
            _panelRect.localScale = Vector3.one * MetersPerCanvasUnit;
            _panelRect.pivot = new Vector2(0f, 1f); // anchored at its top-left corner
            Vector3 pos = GazeCorrection.FromAngles(new Vector2(azimuthDegrees, elevationDegrees)) * distance;
            _panelRect.localPosition = pos;
            _panelRect.localRotation = Quaternion.LookRotation(pos.normalized, Vector3.up); // face the eyes

            var textObject = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(_panelRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(Padding, Padding);
            textRect.offsetMax = new Vector2(-Padding, -Padding);

            _text = textObject.GetComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            float lineMeters = 2f * distance * Mathf.Tan(0.5f * lineHeightDegrees * Mathf.Deg2Rad);
            _fontSize = Mathf.Max(8, Mathf.RoundToInt(lineMeters / MetersPerCanvasUnit / 1.15f));
            _text.fontSize = _fontSize;
            _text.alignment = TextAnchor.UpperLeft;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
            _text.supportRichText = true;
            _text.color = Color.white;
            _text.raycastTarget = false;

            _panel.SetActive(visible);
        }

        void DetectChanges()
        {
            bool apply = gaze.applyCorrection;
            bool valid = gaze.Correction.IsValid;
            bool filter = hemianopia != null && hemianopia.filterEnabled;
            BlindSide side = hemianopia != null ? hemianopia.blindSide : BlindSide.Left;
            CalibrationResult result = calibration != null ? calibration.LastResult : null;
            bool calibrating = calibration != null && calibration.IsRunning;

            if (_primed)
            {
                if (result != _lastResult && result != null)
                    Flash(result.fitOk
                        ? string.Format(CultureInfo.InvariantCulture, "Calibration done: error {0:0.0}° to {1:0.0}°", result.meanAccuracyBeforeDeg, result.meanAccuracyAfterDeg)
                        : "Calibration failed, press C to retry");
                else if (apply != _lastApply && valid)
                    Flash(apply ? "Correction ON" : "Correction OFF");
                else if (filter != _lastFilter && !calibrating)
                    Flash(filter ? "Hemianopia filter ON" : "Hemianopia filter OFF");
                else if (side != _lastSide)
                    Flash($"Blind side: {side}");
            }

            _primed = true;
            _lastApply = apply;
            _lastFilter = filter;
            _lastSide = side;
            _lastResult = result;
        }

        void Flash(string message)
        {
            _flash = message;
            _flashUntil = Time.time + flashSeconds;
        }

        string Compose()
        {
            var c = CultureInfo.InvariantCulture;
            _sb.Clear();

            if (Time.time < _flashUntil && !string.IsNullOrEmpty(_flash))
                _sb.Append("<size=").Append(Mathf.RoundToInt(_fontSize * 1.4f)).Append("><b>").Append(_flash).Append("</b></size>\n");

            if (calibration != null && calibration.IsRunning)
                _sb.Append("<color=#ffd75a>Calibrating: look at each white dot</color>\n");

            _sb.Append("Gaze: ").Append(gaze.IsEyeTracking ? "eyes" : "<color=#ff8a5d>mouse</color>");
            _sb.Append("     Correction: ");
            if (!gaze.Correction.IsValid) _sb.Append("<color=#aaaaaa>none yet (press C)</color>");
            else if (gaze.IsCorrectionActive) _sb.Append("<color=#5dff8a>ON</color>");
            else _sb.Append("<color=#ff8a5d>OFF</color>");
            _sb.Append('\n');

            CalibrationResult r = calibration != null ? calibration.LastResult : null;
            if (r != null)
            {
                if (r.fitOk)
                    _sb.AppendFormat(c, "Last check {0}: error {1:0.0}° to {2:0.0}°, noise {3:0.00}° center, {4:0.00}° outer\n",
                        r.participant, r.meanAccuracyBeforeDeg, r.meanAccuracyAfterDeg, r.centerPrecisionDeg, r.outerPrecisionDeg);
                else
                    _sb.Append("Last check: no correction (").Append(r.fitNote).Append(")\n");
            }

            if (hemianopia != null)
                _sb.Append("Filter: ").Append(hemianopia.filterEnabled ? $"ON, {hemianopia.blindSide.ToString().ToLowerInvariant()} side blind" : "OFF");

            return _sb.ToString();
        }

        static bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.iKey.wasPressedThisFrame) return true;
#endif
            return OVRInput.GetDown(OVRInput.RawButton.Start, OVRInput.Controller.Touch);
        }
    }
}
