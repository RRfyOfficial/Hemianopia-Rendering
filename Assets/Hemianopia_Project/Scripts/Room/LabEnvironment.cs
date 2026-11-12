using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hemianopia.Room
{
    /// <summary>
    /// Gives the scene a dark "grid lab" look while the app is playing: a near-black room with glowing grid
    /// lines on the floor and walls, light pillars in the corners, a ring on the floor where you stand, and
    /// a little fog so the grid fades into the distance.
    ///
    /// Everything is built by code when Play starts and removed when it stops, so the scene file itself is
    /// not changed. Press 8 (or push the LEFT thumbstick up) to switch the look on and off.
    ///
    /// It is cosmetic only. The floor and walls keep colliders, so the gaze still lands on real surfaces.
    /// Keep it calm on purpose: a busy background would make it harder to find things, and that would
    /// change the test results.
    /// </summary>
    public class LabEnvironment : MonoBehaviour
    {
        [Header("Look")]
        public Color lineColor = new Color(0.10f, 1.00f, 0.50f);
        public Color backgroundColor = new Color(0.00f, 0.015f, 0.01f);
        [Range(0f, 1f)] public float floorBrightness = 0.85f;
        [Range(0f, 1f)] public float wallBrightness = 0.30f;

        [Header("Room size (metres)")]
        public float roomSize = 14f;
        public float wallHeight = 5f;
        [Tooltip("Size of one grid square.")]
        public float cellSize = 1f;

        [Header("Atmosphere")]
        public bool fog = true;
        public float fogDensity = 0.045f;

        [Header("Behaviour")]
        public bool showOnStart = true;
        [Tooltip("Hide the scene's original floor while the lab look is on (its collider stays).")]
        public bool hideOldFloor = true;
        public string oldFloorName = "Plane";
        public bool useControllerInput = true;

        public bool IsShown => _root != null;

        struct CameraState
        {
            public Camera cam;
            public CameraClearFlags flags;
            public Color background;
        }

        GameObject _root;
        readonly List<Object> _owned = new List<Object>();
        readonly List<CameraState> _cameras = new List<CameraState>();
        Renderer _oldFloor;
        bool _oldFloorWasEnabled;
        bool _wantShow;

        bool _savedRenderSettings;
        bool _prevFog;
        Color _prevFogColor;
        FogMode _prevFogMode;
        float _prevFogDensity;
        AmbientMode _prevAmbientMode;
        Color _prevAmbientLight;

        // ---------- Unity ----------

        void Start()
        {
            _wantShow = showOnStart;
        }

        void Update()
        {
            if (TogglePressed())
            {
                if (IsShown) { Hide(); _wantShow = false; }
                else _wantShow = true;
            }
            // Waits for the headset camera to exist before building.
            if (_wantShow && !IsShown) Show();
        }

        void OnDisable()
        {
            Hide();
        }

        // ---------- Build ----------

        public void Show()
        {
            if (IsShown) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            _wantShow = false;

            // Floor height: reuse the original floor's height if it exists.
            float floorY = 0f;
            _oldFloor = null;
            GameObject oldFloorObject = string.IsNullOrEmpty(oldFloorName) ? null : GameObject.Find(oldFloorName);
            if (oldFloorObject != null)
            {
                floorY = oldFloorObject.transform.position.y;
                _oldFloor = oldFloorObject.GetComponent<Renderer>();
            }

            Vector3 centre = cam.transform.position;
            centre.y = floorY;

            _root = new GameObject("LabEnvironment");
            _root.transform.position = centre;

            Texture2D grid = MakeGridTexture(256, lineColor, backgroundColor);
            float half = roomSize * 0.5f;
            float cells = roomSize / Mathf.Max(0.1f, cellSize);
            float cellsUp = wallHeight / Mathf.Max(0.1f, cellSize);

            // Floor.
            Color floorTint = Color.white * floorBrightness;
            floorTint.a = 1f;
            MakeQuad("Lab_Floor", centre + Vector3.up * 0.003f, Quaternion.Euler(90f, 0f, 0f),
                new Vector3(roomSize, roomSize, 1f), MakeUnlit(grid, floorTint, new Vector2(cells, cells)));

            // Four walls, dimmer than the floor so they sit in the background.
            Color wallTint = Color.white * wallBrightness;
            wallTint.a = 1f;
            for (int i = 0; i < 4; i++)
            {
                Quaternion rot = Quaternion.Euler(0f, 90f * i, 0f);
                Vector3 pos = centre + rot * new Vector3(0f, wallHeight * 0.5f, half);
                MakeQuad("Lab_Wall" + i, pos, rot, new Vector3(roomSize, wallHeight, 1f),
                    MakeUnlit(grid, wallTint, new Vector2(cells, cellsUp)));
            }

            // Light pillars in the corners.
            Color pillarColor = lineColor * 0.8f;
            pillarColor.a = 1f;
            Material pillarMaterial = MakeUnlit(null, pillarColor, Vector2.one);
            for (int i = 0; i < 4; i++)
            {
                Quaternion rot = Quaternion.Euler(0f, 45f + 90f * i, 0f);
                Vector3 corner = centre + rot * new Vector3(0f, 0f, half * 1.4142f - 0.15f);
                GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pillar.name = "Lab_Pillar" + i;
                Destroy(pillar.GetComponent<Collider>());
                pillar.transform.SetParent(_root.transform, true);
                pillar.transform.position = corner + Vector3.up * (wallHeight * 0.5f);
                pillar.transform.localScale = new Vector3(0.06f, wallHeight, 0.06f);
                Renderer r = pillar.GetComponent<Renderer>();
                r.sharedMaterial = pillarMaterial;
                r.shadowCastingMode = ShadowCastingMode.Off;
            }

            // A ring on the floor where you stand.
            MakeRing(centre + Vector3.up * 0.01f, 0.75f, 0.02f);

            // Dark background instead of the sky.
            _cameras.Clear();
            foreach (Camera c in Camera.allCameras)
            {
                _cameras.Add(new CameraState { cam = c, flags = c.clearFlags, background = c.backgroundColor });
                c.clearFlags = CameraClearFlags.SolidColor;
                c.backgroundColor = backgroundColor;
            }

            // Fog and dim surrounding light.
            _prevFog = RenderSettings.fog;
            _prevFogColor = RenderSettings.fogColor;
            _prevFogMode = RenderSettings.fogMode;
            _prevFogDensity = RenderSettings.fogDensity;
            _prevAmbientMode = RenderSettings.ambientMode;
            _prevAmbientLight = RenderSettings.ambientLight;
            _savedRenderSettings = true;

            RenderSettings.fog = fog;
            RenderSettings.fogColor = backgroundColor;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.20f, 0.26f, 0.23f);

            if (_oldFloor != null && hideOldFloor)
            {
                _oldFloorWasEnabled = _oldFloor.enabled;
                _oldFloor.enabled = false;
            }

            Debug.Log("[Lab] Lab look ON. Press 8 to switch it off.");
        }

        public void Hide()
        {
            if (!IsShown) return;

            foreach (CameraState s in _cameras)
            {
                if (s.cam == null) continue;
                s.cam.clearFlags = s.flags;
                s.cam.backgroundColor = s.background;
            }
            _cameras.Clear();

            if (_savedRenderSettings)
            {
                RenderSettings.fog = _prevFog;
                RenderSettings.fogColor = _prevFogColor;
                RenderSettings.fogMode = _prevFogMode;
                RenderSettings.fogDensity = _prevFogDensity;
                RenderSettings.ambientMode = _prevAmbientMode;
                RenderSettings.ambientLight = _prevAmbientLight;
                _savedRenderSettings = false;
            }

            if (_oldFloor != null && hideOldFloor) _oldFloor.enabled = _oldFloorWasEnabled;
            _oldFloor = null;

            Destroy(_root);
            _root = null;
            foreach (Object o in _owned)
                if (o != null) Destroy(o);
            _owned.Clear();

            Debug.Log("[Lab] Lab look OFF.");
        }

        // ---------- Pieces ----------

        void MakeQuad(string quadName, Vector3 position, Quaternion rotation, Vector3 scale, Material material)
        {
            // A Unity quad faces backwards along its Z axis, so these rotations make each one face into the room.
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = quadName;
            go.transform.SetParent(_root.transform, true);
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = scale;
            Renderer r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        void MakeRing(Vector3 centre, float radius, float width)
        {
            GameObject go = new GameObject("Lab_Ring");
            go.transform.SetParent(_root.transform, true);
            go.transform.SetPositionAndRotation(centre, Quaternion.Euler(90f, 0f, 0f)); // lie flat on the floor
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.alignment = LineAlignment.TransformZ;
            lr.widthMultiplier = width;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;

            const int segments = 64;
            lr.positionCount = segments;
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f));
            }

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            Material m = new Material(shader);
            _owned.Add(m);
            lr.sharedMaterial = m;
            lr.startColor = lineColor;
            lr.endColor = lineColor;
        }

        /// <summary>A material that ignores lights, so the grid glows evenly.</summary>
        Material MakeUnlit(Texture texture, Color tint, Vector2 tiling)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            Material m = new Material(shader);
            _owned.Add(m);

            if (texture != null)
            {
                if (m.HasProperty("_BaseMap"))
                {
                    m.SetTexture("_BaseMap", texture);
                    m.SetTextureScale("_BaseMap", tiling);
                }
                if (m.HasProperty("_MainTex"))
                {
                    m.SetTexture("_MainTex", texture);
                    m.SetTextureScale("_MainTex", tiling);
                }
            }
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
            if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
            return m;
        }

        /// <summary>One grid square: a thin bright line around the edge with a soft glow, dark in the middle.</summary>
        Texture2D MakeGridTexture(int size, Color line, Color background)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.name = "LabGrid";
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;

            const float lineHalf = 1.5f; // pixels
            const float glow = 9f;       // pixels
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Min(x, size - 1 - x);
                    float dy = Mathf.Min(y, size - 1 - y);
                    float d = Mathf.Min(dx, dy);
                    float core = d <= lineHalf ? 1f : 0f;
                    float soft = Mathf.Clamp01(1f - (d - lineHalf) / glow);
                    soft = soft * soft * 0.30f;
                    Color c = Color.Lerp(background, line, Mathf.Max(core, soft));
                    c.a = 1f;
                    pixels[y * size + x] = c;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(true);
            _owned.Add(tex);
            return tex;
        }

        bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.digit8Key.wasPressedThisFrame) return true;
#endif
            return useControllerInput
                   && OVRInput.GetDown(OVRInput.RawButton.LThumbstickUp, OVRInput.Controller.Touch);
        }
    }
}
