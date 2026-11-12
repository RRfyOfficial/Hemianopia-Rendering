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
            _renderer.material.color = LookedAtNow ? Color.Lerp(baseColor, Color.white, 0.65f) : baseColor;
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
