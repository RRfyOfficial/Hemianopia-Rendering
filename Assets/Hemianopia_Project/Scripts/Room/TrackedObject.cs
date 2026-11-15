using System.Collections.Generic;
using UnityEngine;

namespace Hemianopia.Room
{
    /// <summary>
    /// A name tag plus a small memory for one object in the room.
    ///
    /// Put this on anything the system should keep track of. The LookTracker fills in the memory every frame:
    /// has the person looked at this yet, when did they last look, and is it on their blind side right now.
    ///
    /// This is the start of the "notebook" (see the project plan): one entry per object.
    /// </summary>
    public class TrackedObject : MonoBehaviour
    {
        /// <summary>Every tracked object that is currently active in the scene.</summary>
        public static readonly List<TrackedObject> All = new List<TrackedObject>();

        [Tooltip("Human-readable name, used in logs. Example: \"red cube\".")]
        public string label = "";

        [Tooltip("The object's normal colour. It brightens while the person is looking at it.")]
        public Color baseColor = Color.white;

        // ---------- Memory, filled in by LookTracker ----------

        /// <summary>True once the person has looked at this object at least once.</summary>
        public bool EverSeen { get; set; }

        /// <summary>When it was first looked at (Time.realtimeSinceStartupAsDouble). Only valid if EverSeen.</summary>
        public double FirstSeenTime { get; set; }

        /// <summary>When it was last looked at. Only valid if EverSeen.</summary>
        public double LastSeenTime { get; set; }

        /// <summary>When it appeared in the scene.</summary>
        public double AppearedTime { get; set; }

        /// <summary>True while the person is looking at it right now.</summary>
        public bool LookedAtNow { get; private set; }

        /// <summary>Angle between the gaze and this object's centre, in degrees.</summary>
        public float AngleFromGaze { get; set; }

        /// <summary>True when the object's centre is on the blind side of the gaze.</summary>
        public bool OnBlindSide { get; set; }

        /// <summary>True when the mask is on and the whole object sits in the blind half (the person cannot see any of it).</summary>
        public bool HiddenByMask { get; set; }

        /// <summary>How long the gaze has rested on it without a break, in seconds.</summary>
        public float Dwell { get; set; }

        Renderer _renderer;
        bool _pulsing;
        float _flashUntil = -1f;
        Color _flashColor;

        /// <summary>
        /// While true, a glowing cyan ring pulses around the object: "look at me" (used by the BusyJob).
        /// It is a ring on purpose, so it cannot be mixed up with the object turning white, which means
        /// "you are looking at this right now".
        /// </summary>
        public bool Pulsing
        {
            get { return _pulsing; }
            set
            {
                if (_pulsing == value) return;
                _pulsing = value;
                if (!_pulsing && _ring != null) _ring.gameObject.SetActive(false);
            }
        }

        const int RingSegments = 40;
        static readonly Color RingColor = new Color(0.20f, 0.95f, 1.00f);
        static Material _ringMaterial;
        LineRenderer _ring;

        void EnsureRing()
        {
            if (_ring != null) return;
            if (_ringMaterial == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                _ringMaterial = new Material(shader);
            }
            GameObject go = new GameObject("LookAtMe_Ring");
            go.transform.SetParent(transform.parent, false); // beside the shape, so the shape's scale does not affect it
            _ring = go.AddComponent<LineRenderer>();
            _ring.useWorldSpace = true;
            _ring.loop = true;
            _ring.positionCount = RingSegments;
            _ring.alignment = LineAlignment.View;
            _ring.sharedMaterial = _ringMaterial;
            _ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _ring.receiveShadows = false;
        }

        void OnDestroy()
        {
            if (_ring != null) Destroy(_ring.gameObject);
        }

        /// <summary>Show one colour for a moment (for example, green when a block of runs has finished).</summary>
        public void Flash(Color color, float seconds)
        {
            _flashColor = color;
            _flashUntil = Time.unscaledTime + seconds;
        }

        /// <summary>Rough radius in metres, used to turn the object's size into an angle.</summary>
        public float WorldRadius
        {
            get
            {
                if (_renderer == null) return 0.05f;
                Vector3 e = _renderer.bounds.extents;
                return Mathf.Max(e.x, Mathf.Max(e.y, e.z));
            }
        }

        public void Init(string objectLabel, Color color)
        {
            label = objectLabel;
            baseColor = color;
            AppearedTime = Time.realtimeSinceStartupAsDouble;
            RefreshColor();
        }

        /// <summary>Called by LookTracker. Brightens the object while it is being looked at.</summary>
        public void SetLookedAt(bool looked)
        {
            if (looked == LookedAtNow) return;
            LookedAtNow = looked;
            RefreshColor();
        }

        public void RefreshColor()
        {
            if (_renderer == null) _renderer = GetComponent<Renderer>();
            if (_renderer == null) return;
            if (_flashUntil > 0f) return; // Update is driving the colour right now
            _renderer.material.color = LookedAtNow ? Color.Lerp(baseColor, Color.white, 0.65f) : baseColor;
        }

        void Update()
        {
            if (_renderer == null) return;

            if (_flashUntil > 0f)
            {
                if (Time.unscaledTime < _flashUntil)
                {
                    _renderer.material.color = _flashColor;
                    return;
                }
                _flashUntil = -1f;
                RefreshColor();
            }

            if (_pulsing)
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                EnsureRing();
                if (!_ring.gameObject.activeSelf) _ring.gameObject.SetActive(true);

                // A bit more than two pulses per second: the ring breathes in size, thickness and brightness.
                float k = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f * 2.2f);
                float radius = WorldRadius * (1.55f + 0.15f * k);

                // Draw the circle facing the person.
                Vector3 centre = transform.position;
                Vector3 toCam = cam.transform.position - centre;
                toCam = toCam.sqrMagnitude > 1e-6f ? toCam.normalized : -cam.transform.forward;
                Vector3 u = Vector3.Cross(cam.transform.up, toCam);
                u = u.sqrMagnitude > 1e-6f ? u.normalized : cam.transform.right;
                Vector3 v = Vector3.Cross(toCam, u);
                for (int i = 0; i < RingSegments; i++)
                {
                    float a = i / (float)RingSegments * Mathf.PI * 2f;
                    _ring.SetPosition(i, centre + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius);
                }

                _ring.widthMultiplier = 0.012f + 0.006f * k;
                Color c = RingColor;
                c.a = 0.55f + 0.45f * k;
                _ring.startColor = c;
                _ring.endColor = c;
            }
        }

        void Awake()
        {
            _renderer = GetComponent<Renderer>();
        }

        void OnEnable()
        {
            if (!All.Contains(this)) All.Add(this);
        }

        void OnDisable()
        {
            All.Remove(this);
        }
    }
}
