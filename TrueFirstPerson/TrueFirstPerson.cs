using BepInEx;
using BepInEx.Logging;
using Multiplayer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DouBai.PerspectiveSwitcher
{
    internal static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.doubai.hff.perspectiveswitcher";
        public const string PLUGIN_NAME = "True First Person";
        public const string PLUGIN_VERSION = "1.0.1";
    }

    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency("HSRTimer", BepInEx.BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("TwilightTimer", BepInEx.BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} v{PluginInfo.PLUGIN_VERSION} loaded.");
            PerspectiveSettings.Load();
            var coreGo = new GameObject("PerspectiveSwitcher.Core");
            UnityEngine.Object.DontDestroyOnLoad(coreGo);
            coreGo.AddComponent<PerspectiveController>();
            var uiGo = new GameObject("PerspectiveSwitcher.UI");
            UnityEngine.Object.DontDestroyOnLoad(uiGo);
            uiGo.AddComponent<SettingsUi>();
            TimerIntegration.TryInit();
        }
    }

    public static class PerspectiveSettings
    {
        public static bool DefaultFirstPerson = false;
        public static KeyCode ToggleKey = KeyCode.B;
        public static float FirstPersonFov = 90f;
        public static float ViewSmoothness = 0.9f;
        public static KeyCode UiToggleKey = KeyCode.Home;
        private const string Section = "settings";
        private static string FilePath =>
            Path.Combine(Paths.ConfigPath, PluginInfo.PLUGIN_GUID, "settings.ini");

        public static void Load()
        {
            if (!File.Exists(FilePath)) return;
            string section = "";
            try
            {
                foreach (string rawLine in File.ReadAllLines(FilePath, new UTF8Encoding(false)))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || section != Section) continue;
                    Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning($"PerspectiveSwitcher: failed to load settings: {ex.Message}");
            }
        }

        private static void Apply(string key, string value)
        {
            try
            {
                switch (key)
                {
                    case "default_first_person": DefaultFirstPerson = ParseBool(value, DefaultFirstPerson); break;
                    case "toggle_key": ToggleKey = ParseKeyCode(value, ToggleKey); break;
                    case "first_person_fov": FirstPersonFov = ParseFloat(value, FirstPersonFov); break;
                    case "view_smoothness": ViewSmoothness = ParseFloat(value, ViewSmoothness); break;
                    case "ui_toggle_key": UiToggleKey = ParseKeyCode(value, UiToggleKey); break;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder();
                sb.Append("# PerspectiveSwitcher settings\n\n");
                sb.Append('[').Append(Section).Append("]\n");
                sb.Append("default_first_person = ").Append(DefaultFirstPerson ? "true" : "false").Append('\n');
                sb.Append("toggle_key = ").Append(ToggleKey).Append('\n');
                sb.Append("first_person_fov = ").Append(FirstPersonFov.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("view_smoothness = ").Append(ViewSmoothness.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("ui_toggle_key = ").Append(UiToggleKey).Append('\n');
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning($"PerspectiveSwitcher: failed to save settings: {ex.Message}");
            }
        }

        private static bool ParseBool(string s, bool fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
            if (s == "false" || s == "0" || s == "no" || s == "off") return false;
            return fallback;
        }

        private static KeyCode ParseKeyCode(string s, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return System.Enum.TryParse(s, true, out KeyCode kc) ? kc : fallback;
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }
    }

    [DefaultExecutionOrder(1000)]
    public class PerspectiveController : MonoBehaviour
    {
        public static PerspectiveController Instance { get; private set; }

        private bool _firstPerson;
        private bool _headHidden;
        private float _blend;
        private float _camPitch;
        private float _climbDelay;
        private Texture2D _crosshairTex;
        private int _crosshairTexSize;
        private const float TransitionDuration = 0.2f;
        private const float FirstPersonNearClip = 0.02f;
        private const float TimerFpsRuleTimeout = 2f;
        private static int CrosshairRadius = 2;
        private static float CrosshairOpacity = 0.8f;
        private static Type _freeRoamType;
        private static FieldInfo _freeRoamCamField;
        private static bool _freeRoamResolved;
        private bool _freeRoamActive;
        private static Type _mapSwitcherType;
        private bool _mapHasOwnSwitcher;

        // Timer "FPS" tag-rule state. While the rule is enabled and ticking, the
        // plugin stays in first person and the toggle key is ignored. The lock
        // releases when the timer stops ticking (tag disabled), the level ends,
        // or the game leaves the level.
        private bool _fpsRuleLocked;
        private bool _firstPersonBeforeLock;
        private bool _timerFpsRuleActive;
        private float _lastTimerFpsRuleTick = float.MinValue;

        public bool IsFirstPerson => _firstPerson;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void TogglePerspective()
        {
            if (_fpsRuleLocked) return;
            _firstPerson = !_firstPerson;
        }

        /// <summary>Called by the timer tag rule (via FpsRuleBridge) on level enter/tick/exit.</summary>
        public static void SetFpsRuleActive(bool active)
        {
            if (Instance == null) return;
            Instance._timerFpsRuleActive = active;
            if (active)
            {
                Instance._lastTimerFpsRuleTick = Time.unscaledTime;
                Instance.SetFirstPersonLocked(true);
            }
            else
            {
                Instance.SetFirstPersonLocked(false);
            }
        }

        private void SetFirstPersonLocked(bool locked)
        {
            if (_fpsRuleLocked == locked) return;
            _fpsRuleLocked = locked;
            if (locked)
            {
                _firstPersonBeforeLock = _firstPerson;
                _firstPerson = true;
            }
            else
            {
                _firstPerson = _firstPersonBeforeLock;
            }
        }

        private void UpdateTimerFpsRule()
        {
            if (!_timerFpsRuleActive) return;
            if (!ShouldKeepTimerFpsLock())
                SetFpsRuleActive(false);
        }

        private bool ShouldKeepTimerFpsLock()
        {
            Game game = Game.instance;
            if (game == null) return false;
            GameState state = game.state;
            if (state == GameState.PlayingLevel)
                return Time.unscaledTime - _lastTimerFpsRuleTick <= TimerFpsRuleTimeout;
            // Keep the camera locked while a level is loading. The timer only
            // ticks during PlayingLevel, so during the LoadingLevel gap between
            // campaign levels OnTick goes quiet even though the FPS tag is still
            // enabled. Treat LoadingLevel (and Paused) as still active; release
            // only on Inactive (quit/menu/retry) or when ticks stop while playing.
            if (state == GameState.Paused || state == GameState.LoadingLevel)
                return true;
            return false;
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void Start()
        {
            _firstPerson = PerspectiveSettings.DefaultFirstPerson;
            ResetForNewScene();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ResetForNewScene();
        }

        private void ResetForNewScene()
        {
            _blend = _firstPerson ? 1f : 0f;
            _headHidden = false;
            _hierarchyLogged = false;
            _nameTagHidden = false;
            _mapHasOwnSwitcher = DetectMapOwnSwitcher();
            if (_mapHasOwnSwitcher)
                Plugin.Logger.LogInfo("PerspectiveSwitcher: map provides its own PerspectiveSwitcher; plugin controls disabled on this map.");
        }

        private static bool DetectMapOwnSwitcher()
        {
            if (_mapSwitcherType == null)
                _mapSwitcherType = FindTypeInLoaded("DouBai.ETB_Level0.PerspectiveSwitcher");
            if (_mapSwitcherType == null) return false;
            UnityEngine.Object[] objs = UnityEngine.Object.FindObjectsOfType(_mapSwitcherType);
            return objs != null && objs.Length > 0;
        }

        private static Type FindTypeInLoaded(string fullName)
        {
            try
            {
                Type t = Type.GetType(fullName);
                if (t != null) return t;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        t = asm.GetType(fullName);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private void Update()
        {
            UpdateTimerFpsRule();

            if (_mapHasOwnSwitcher) return;
            if (UiShared.IsRebinding) return;
            if (_fpsRuleLocked) return;
            if (Game.GetKeyDown(PerspectiveSettings.ToggleKey))
                TogglePerspective();
        }

        private void LateUpdate()
        {
            if (_mapHasOwnSwitcher) return;
            _freeRoamActive = IsFreeRoamActive();
            bool freeRoam = _freeRoamActive;
            float target = _firstPerson ? 1f : 0f;
            float duration = target < _blend ? TransitionDuration * 2f : TransitionDuration;
            _blend = Mathf.MoveTowards(_blend, target, Time.deltaTime / Mathf.Max(duration, 0.001f));
            List<NetPlayer> players = GetLocalPlayers();
            if (players == null) return;
            HideLocalNameTags(players);
            for (int i = 0; i < players.Count; i++)
            {
                NetPlayer player = players[i];
                if (player == null || player.cameraController == null) continue;
                if (_blend > 0f && !freeRoam)
                    ApplyBlend(player.cameraController);
            }
            UpdateHeadHiding(players, freeRoam);
        }

        private void UpdateHeadHiding(List<NetPlayer> players, bool freeRoam)
        {
            bool wantHide = !freeRoam && _blend > 0.9f;
            if (wantHide == _headHidden) return;
            _headHidden = wantHide;
            if (players == null) return;
            for (int i = 0; i < players.Count; i++)
            {
                NetPlayer player = players[i];
                if (player == null || player.human == null) continue;
                if (wantHide) DumpHierarchyOnce(player);
                SetHatHidden(player, wantHide);
            }
        }

        private void HideLocalNameTags(List<NetPlayer> players)
        {
            if (_nameTagHidden) return;
            _nameTagHidden = true;
            if (IsMultiplayer()) return;
            if (players == null) return;
            for (int i = 0; i < players.Count; i++)
            {
                NetPlayer player = players[i];
                if (player == null) continue;
                SetNameTagsHidden(player, true);
            }
        }

        private static bool IsMultiplayer()
        {
            return NetGame.instance != null && NetGame.instance.isNetActive;
        }

        private bool _hierarchyLogged;
        private bool _nameTagHidden;

        private void DumpHierarchyOnce(NetPlayer player)
        {
            if (_hierarchyLogged) return;
            _hierarchyLogged = true;
            var sb = new StringBuilder();
            sb.AppendLine("=== PerspectiveSwitcher hierarchy dump ===");
            DumpTree(player.transform, "", sb, 0);
            if (player.customization != null)
            {
                sb.AppendLine("customization: head=" + SafeName(player.customization.head)
                    + " main=" + SafeName(player.customization.main)
                    + " upper=" + SafeName(player.customization.upper)
                    + " lower=" + SafeName(player.customization.lower));
            }
            if (player.overHeadNameTag != null)
                sb.AppendLine("overHeadNameTag: GO=" + player.overHeadNameTag.gameObject.name
                    + " active=" + player.overHeadNameTag.gameObject.activeSelf);
            try
            {
                File.WriteAllText(
                    Path.Combine(Paths.ConfigPath, PluginInfo.PLUGIN_GUID, "hierarchy.txt"),
                    sb.ToString(), new UTF8Encoding(false));
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning("PerspectiveSwitcher: hierarchy dump failed: " + ex.Message);
            }
            Plugin.Logger.LogInfo("PerspectiveSwitcher: hierarchy dumped to hierarchy.txt");
        }

        private static string SafeName(UnityEngine.Object o)
        {
            return o != null ? o.name : "null";
        }

        private static void DumpTree(Transform t, string prefix, StringBuilder sb, int depth)
        {
            if (t == null || depth > 40) return;
            Renderer r = t.GetComponent<Renderer>();
            TextMesh tm = t.GetComponent<TextMesh>();
            if (r != null || tm != null)
            {
                string kind = r is SkinnedMeshRenderer ? " [SKINNED]" : r != null ? " [MESH]" : "";
                if (tm != null) kind += " [TEXT]";
                sb.AppendLine(prefix + t.name + kind + (r != null && !r.enabled ? " (disabled)" : ""));
            }
            for (int i = 0; i < t.childCount; i++)
                DumpTree(t.GetChild(i), prefix + t.name + "/", sb, depth + 1);
        }

        private static Type _nameTagType;

        private static void SetNameTagsHidden(NetPlayer player, bool hidden)
        {
            var oh = player.overHeadNameTag;
            if (oh != null && oh.gameObject != null)
                oh.gameObject.SetActive(!hidden);
            if (_nameTagType == null)
                _nameTagType = Type.GetType("NameTag, Assembly-CSharp");
            if (_nameTagType != null)
            {
                UnityEngine.Component c = player.GetComponentInChildren(_nameTagType, true);
                if (c != null && c.gameObject != null)
                    c.gameObject.SetActive(!hidden);
            }
        }

        private static void SetHatHidden(NetPlayer player, bool hidden)
        {
            var cus = player.customization;
            if (cus != null && cus.head != null && cus.head.gameObject != null)
                cus.head.gameObject.SetActive(!hidden);
        }

        private void OnGUI()
        {
            if (_mapHasOwnSwitcher) return;
            if (_blend <= 0.5f) return;
            if (Event.current.type != EventType.Repaint) return;
            if (_freeRoamActive) return;
            if (Time.timeScale <= 0f) return;
            if (CrosshairOpacity <= 0f)
            {
                if (_crosshairTex != null) Destroy(_crosshairTex);
                _crosshairTex = null;
                _crosshairTexSize = 0;
                return;
            }
            int size = Mathf.Max(2, Mathf.RoundToInt(CrosshairRadius * 2 * (Screen.height / 1080f)));
            if (_crosshairTex == null || _crosshairTexSize != size)
            {
                if (_crosshairTex != null) Destroy(_crosshairTex);
                _crosshairTex = CreateCircleTexture(size);
                _crosshairTexSize = size;
            }
            Rect rect = new Rect(Screen.width * 0.5f - size * 0.5f, Screen.height * 0.5f - size * 0.5f, size, size);
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, CrosshairOpacity);
            GUI.DrawTexture(rect, _crosshairTex, ScaleMode.ScaleToFit, true);
            GUI.color = prev;
        }

        private static bool IsFreeRoamActive()
        {
            if (!_freeRoamResolved)
            {
                _freeRoamResolved = true;
                _freeRoamType = Type.GetType("FreeRoamCam, Assembly-CSharp");
                if (_freeRoamType != null)
                    _freeRoamCamField = _freeRoamType.GetField("cam", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            if (_freeRoamCamField == null) return false;
            UnityEngine.Object[] cams = UnityEngine.Object.FindObjectsOfType(_freeRoamType);
            for (int i = 0; i < cams.Length; i++)
            {
                Camera cam = _freeRoamCamField.GetValue(cams[i]) as Camera;
                if (cam != null && cam.enabled) return true;
            }
            return false;
        }

        private static Texture2D CreateCircleTexture(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Point;
            float radius = size * 0.5f;
            float r2 = radius * radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - radius;
                    float dy = y + 0.5f - radius;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, dx * dx + dy * dy <= r2 ? 1f : 0f));
                }
            }
            tex.Apply();
            return tex;
        }

        private void ApplyBlend(CameraController3 cam)
        {
            if (cam.human == null) return;
            Vector3 headPos;
            Quaternion headRot;
            if (!TryGetHeadPose(cam.human, out headPos, out headRot))
                return;
            float eased = _blend * _blend * (3f - 2f * _blend);
            Transform t = cam.transform;
            t.position = Vector3.Lerp(t.position, headPos, eased);
            t.rotation = Quaternion.Slerp(t.rotation, headRot, eased);
            Camera gameCam = cam.gameCam;
            if (gameCam != null)
            {
                gameCam.fieldOfView = Mathf.Lerp(gameCam.fieldOfView, PerspectiveSettings.FirstPersonFov, eased);
                gameCam.nearClipPlane = Mathf.Lerp(gameCam.nearClipPlane, FirstPersonNearClip, eased);
            }
        }

        private bool TryGetHeadPose(Human human, out Vector3 pos, out Quaternion rot)
        {
            if (human.ragdoll == null || human.ragdoll.partHead == null)
            {
                pos = Vector3.zero;
                rot = Quaternion.identity;
                return false;
            }
            Transform head = human.ragdoll.partHead.transform;
            Collider headCollider = human.ragdoll.partHead.collider;
            if (human.controls != null)
            {
                float yaw = human.controls.cameraYawAngle;
                float pitch = human.controls.cameraPitchAngle;
                if (Time.timeScale <= 0f)
                    yaw = Mathf.Clamp(yaw, -80f, 80f);
                float headPitch = head.eulerAngles.x;
                if (headPitch > 180f) headPitch -= 360f;
                bool climbingNow = human.isClimbing || (human.hasGrabbed && !human.onGround);
                if (climbingNow)
                    _climbDelay = 0.4f;
                else if (_climbDelay > 0f)
                    _climbDelay -= Time.deltaTime;
                float target;
                if (climbingNow || _climbDelay > 0f)
                    target = Mathf.Clamp(pitch, -20f, 20f);
                else
                    target = Mathf.Clamp(Mathf.LerpAngle(pitch, headPitch, 1f - PerspectiveSettings.ViewSmoothness), -80f, 80f);
                _camPitch = Mathf.LerpAngle(_camPitch, target, 1f - Mathf.Exp(-Time.deltaTime * 12f));
                pitch = _camPitch;
                rot = Quaternion.Euler(pitch, yaw, 0f);
            }
            else
            {
                rot = head.rotation;
            }
            pos = (headCollider != null ? headCollider.bounds.center : head.position);
            return true;
        }

        private static List<NetPlayer> GetLocalPlayers()
        {
            NetGame net = NetGame.instance;
            if (net == null || net.local == null) return null;
            return net.local.players;
        }
    }

    public static class UiShared
    {
        public static GUIStyle Label, Section, Small, Toggle, Button, TextField;
        private static Font _font;
        private static bool _stylesReady;
        private static bool _rebinding;
        private static string _fovInput;
        private static string _smoothInput;
        private static bool _fovEditing;
        private static bool _smoothEditing;
        public static bool IsRebinding => _rebinding;

        public static void EnsureStyles()
        {
            if (_stylesReady) return;
            try
            {
                _font = Font.CreateDynamicFontFromOSFont(new[]
                {
                    "PingFang SC", "Microsoft YaHei", "Noto Sans CJK SC",
                    "Noto Sans CJK", "Heiti SC", "Arial Unicode MS", "Arial",
                }, 14);
            }
            catch { _font = null; }
            Label = new GUIStyle(GUI.skin.label) { font = _font, fontSize = 13, wordWrap = false };
            Section = new GUIStyle(GUI.skin.label) { font = _font, fontSize = 14, fontStyle = FontStyle.Bold };
            Small = new GUIStyle(GUI.skin.label) { font = _font, fontSize = 11, wordWrap = true };
            Toggle = new GUIStyle(GUI.skin.toggle) { font = _font, fontSize = 13, wordWrap = false };
            Button = new GUIStyle(GUI.skin.button) { font = _font, fontSize = 13, wordWrap = false };
            TextField = new GUIStyle(GUI.skin.textField) { font = _font, fontSize = 13 };
            _stylesReady = true;
        }

        public static void RebindCapture()
        {
            if (_rebinding && Event.current.type == EventType.KeyDown)
            {
                KeyCode pressed = Event.current.keyCode;
                if (!IsModifier(pressed))
                {
                    PerspectiveSettings.ToggleKey = pressed;
                    _rebinding = false;
                    Event.current.Use();
                }
            }
        }

        public static void DrawControls()
        {
            SectionHeader(L10n.T("PS_SECTION_GENERAL"));
            PerspectiveSettings.DefaultFirstPerson = GUILayout.Toggle(
                PerspectiveSettings.DefaultFirstPerson, L10n.T("PS_DEFAULT_FIRST_PERSON"), Toggle);
            KeybindRow();
            SectionHeader(L10n.T("PS_SECTION_CAMERA"));
            PerspectiveSettings.FirstPersonFov = InputRow(
                L10n.T("PS_FIRST_PERSON_FOV"), PerspectiveSettings.FirstPersonFov, 60f, 120f, true, "0", ref _fovInput, ref _fovEditing);
            PerspectiveSettings.ViewSmoothness = InputRow(
                L10n.T("PS_VIEW_SMOOTHNESS"), PerspectiveSettings.ViewSmoothness, 0f, 1f, false, "0.0", ref _smoothInput, ref _smoothEditing);
        }

        private static void SectionHeader(string title)
        {
            GUILayout.Space(6);
            GUILayout.Label(title, Section);
        }

        private static float InputRow(string label, float value, float min, float max, bool floor, string format, ref string input, ref bool editing)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Label);
            GUILayout.FlexibleSpace();
            if (input == null)
                input = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            string control = "ps_input_" + label;
            GUI.SetNextControlName(control);
            string edited = GUILayout.TextField(input, TextField, GUILayout.Width(80), GUILayout.Height(25));
            GUILayout.EndHorizontal();
            float slid = GUILayout.HorizontalSlider(value, min, max);
            bool nowFocused = GUI.GetNameOfFocusedControl() == control;
            bool enter = nowFocused && Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            bool lostFocus = editing && !nowFocused;
            editing = nowFocused;
            if (edited != input) input = edited;
            if (enter || lostFocus)
            {
                float parsed;
                if (float.TryParse(input.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    parsed = Mathf.Clamp(parsed, min, max);
                    if (floor) parsed = Mathf.Floor(parsed);
                    input = parsed.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
                    return parsed;
                }
                input = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (Mathf.Abs(slid - value) > 0.001f)
            {
                if (floor) slid = Mathf.Floor(slid);
                input = slid.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            }
            return slid;
        }

        private static void KeybindRow()
        {
            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label(L10n.T("PS_TOGGLE_KEY"), Label);
            GUILayout.FlexibleSpace();
            string btn = _rebinding ? L10n.T("PS_PRESS_KEY") : PerspectiveSettings.ToggleKey.ToString();
            bool clicked = TimerIntegration.Enabled
                ? GUILayout.Button(btn, Button, GUILayout.Width(120))
                : GUILayout.Button(btn, Button, GUILayout.Width(120), GUILayout.Height(25));
            if (clicked && Event.current.isMouse)
                _rebinding = !_rebinding;
            GUILayout.Space(16);
            GUILayout.EndHorizontal();
        }

        private static bool IsModifier(KeyCode key)
        {
            return key == KeyCode.LeftShift || key == KeyCode.RightShift
                || key == KeyCode.LeftControl || key == KeyCode.RightControl
                || key == KeyCode.LeftAlt || key == KeyCode.RightAlt
                || key == KeyCode.LeftCommand || key == KeyCode.RightCommand;
        }
    }

    public class SettingsUi : MonoBehaviour
    {
        private bool _visible;
        private Rect _rect;

        private void Update()
        {
            if (TimerIntegration.Enabled) return;
            if (Game.GetKeyDown(PerspectiveSettings.UiToggleKey))
            {
                _visible = !_visible;
                if (_visible)
                    PositionRect();
            }
        }

        private void PositionRect()
        {
            const float w = 300f, h = 580f;
            if (TimerIntegration.Enabled)
                _rect = new Rect(Screen.width * 0.5f - w * 0.5f, Screen.height * 0.5f - h * 0.5f, w, h);
            else
                _rect = new Rect(Screen.width - w - 20f, Screen.height - h - 20f, w, h);
        }

        private void OnDestroy()
        {
            PerspectiveSettings.Save();
        }

        private void OnApplicationQuit()
        {
            PerspectiveSettings.Save();
        }

        private void OnGUI()
        {
            if (TimerIntegration.Enabled) return;
            if (!_visible) return;
            UiShared.EnsureStyles();
            string title = TimerIntegration.Enabled ? L10n.T("PS_TITLE") :
                L10n.T("PS_TITLE") + " v" + PluginInfo.PLUGIN_VERSION;
            _rect = GUI.Window(GetInstanceID(), _rect, Draw, title);
            _rect.width = 300f;
        }

        private void Draw(int id)
        {
            UiShared.RebindCapture();
            UiShared.DrawControls();
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Space(12);
            if (GUILayout.Button(L10n.T("PS_SAVE"), UiShared.Button, GUILayout.Width(120), GUILayout.Height(30)))
                PerspectiveSettings.Save();
            GUILayout.Space(10);
            if (GUILayout.Button(L10n.T("PS_CLOSE"), UiShared.Button, GUILayout.Width(120), GUILayout.Height(30)))
            {
                PerspectiveSettings.Save();
                _visible = false;
            }
            GUILayout.Space(18);
            GUILayout.EndHorizontal();
            GUILayout.Label(L10n.T("PS_FOOTER"), UiShared.Small);
            if (Event.current.type == EventType.Repaint)
            {
                float bottom = GUILayoutUtility.GetLastRect().yMax;
                float target = bottom + 34f;
                if (Mathf.Abs(_rect.height - target) > 0.5f)
                    _rect.height = target;
            }
            GUI.DragWindow(new Rect(0, 0, _rect.width, 20));
        }
    }

    public static class FpsRuleBridge
    {
        public const string Id = "FPS";
        public const string DisplayNameKey = "";

        public static void OnLevelEnter(object ctx)
        {
            PerspectiveController.SetFpsRuleActive(true);
        }

        public static void OnTick(object ctx)
        {
            PerspectiveController.SetFpsRuleActive(true);
        }

        public static void OnLevelExit(object ctx)
        {
            // HSRTimer fires this both when a run segment is completed (before
            // the next level loads) and when it ends for good, so releasing the
            // FPS lock here would briefly drop back to third person during the
            // LoadingLevel gap between campaign levels. Instead, leave the lock
            // on and let PerspectiveController release it when the game actually
            // goes inactive (quit/menu) or when timer ticks stop while playing.
        }
    }

    public static class TimerIntegration
    {
        private const string HSRTimerAssembly = "HSRTimer";
        private const string TwilightTimerAssembly = "TwilightTimer";
        private const string FpsTagId = "FPS";

        public static bool Enabled { get; private set; }

        public static void TryInit()
        {
            RegisterFpsRuleWithAnyTimer();
            TryRegisterSettingsTab();
        }

        // ── FPS tag rule (HSRTimer / TwilightTimer, reflection-based) ───────

        private static void RegisterFpsRuleWithAnyTimer()
        {
            TryRegisterFpsRule(HSRTimerAssembly);
            TryRegisterFpsRule(TwilightTimerAssembly);
        }

        private static void TryRegisterFpsRule(string assemblyName)
        {
            try
            {
                Type ruleType = Type.GetType(assemblyName + ".ITagRule, " + assemblyName);
                Type registryType = Type.GetType(assemblyName + ".TagRuleRegistry, " + assemblyName);
                if (ruleType == null || registryType == null) return;

                var instanceProp = registryType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProp == null) return;
                object registry = instanceProp.GetValue(null, null);
                if (registry == null) return;

                var register = registryType.GetMethod("Register", new[] { ruleType });
                if (register == null) return;

                Type proxyType = BuildFpsRuleProxy(ruleType);
                object proxy = Activator.CreateInstance(proxyType);
                object result = register.Invoke(registry, new[] { proxy });
                if (result is bool ok && ok)
                    Plugin.Logger.LogInfo($"PerspectiveSwitcher: registered '{FpsTagId}' tag rule with {assemblyName}.");
                else
                    Plugin.Logger.LogWarning($"PerspectiveSwitcher: {assemblyName} rejected '{FpsTagId}' tag rule (duplicate id?).");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning($"PerspectiveSwitcher: failed to register '{FpsTagId}' tag rule with {assemblyName}: {ex.Message}");
            }
        }

        private static Type BuildFpsRuleProxy(Type interfaceType)
        {
            var asmName = new AssemblyName("PerspectiveSwitcher.FpsRuleProxy." + interfaceType.Assembly.GetName().Name);
            AssemblyBuilder ab = AppDomain.CurrentDomain.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
            ModuleBuilder mb = ab.DefineDynamicModule("ProxyModule");
            TypeBuilder tb = mb.DefineType("FpsTagRuleProxy",
                TypeAttributes.Public | TypeAttributes.Sealed,
                typeof(object), new[] { interfaceType });

            MethodInfo[] methods = interfaceType.GetMethods();
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo mi = methods[i];
                ParameterInfo[] pars = mi.GetParameters();
                Type[] paramTypes = new Type[pars.Length];
                for (int p = 0; p < pars.Length; p++)
                    paramTypes[p] = pars[p].ParameterType;

                MethodBuilder method = tb.DefineMethod(
                    mi.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                    mi.ReturnType, paramTypes);

                ILGenerator il = method.GetILGenerator();
                if (mi.Name == "get_Id")
                {
                    il.Emit(OpCodes.Ldstr, FpsTagId);
                    il.Emit(OpCodes.Ret);
                }
                else if (mi.Name == "get_DisplayNameKey")
                {
                    il.Emit(OpCodes.Ldstr, "");
                    il.Emit(OpCodes.Ret);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_1);
                    if (paramTypes.Length > 0 && paramTypes[0].IsValueType)
                        il.Emit(OpCodes.Box, paramTypes[0]);
                    MethodInfo bridgeMethod = typeof(FpsRuleBridge).GetMethod(
                        mi.Name,
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(object) }, null);
                    if (bridgeMethod == null)
                        throw new System.MissingMethodException("FpsRuleBridge." + mi.Name);
                    il.Emit(OpCodes.Call, bridgeMethod);
                    il.Emit(OpCodes.Ret);
                }
                tb.DefineMethodOverride(method, mi);
            }

            return tb.CreateType();
        }

        // ── Settings panel tab (HSRTimer ISettingsPanelTab, reflection-based) ─

        private static void TryRegisterSettingsTab()
        {
            if (Enabled) return;
            try
            {
                Type interfaceType = Type.GetType("HSRTimer.ISettingsPanelTab, HSRTimer");
                Type registryType = Type.GetType("HSRTimer.SettingsPanelTabRegistry, HSRTimer");
                if (interfaceType == null || registryType == null)
                {
                    Plugin.Logger.LogInfo("PerspectiveSwitcher: HSRTimer settings-panel tab API not found; standalone settings UI stays available.");
                    return;
                }

                var instanceProp = registryType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProp == null) return;
                object registry = instanceProp.GetValue(null, null);
                if (registry == null) return;

                var register = registryType.GetMethod("Register", new[] { typeof(string), interfaceType });
                if (register == null)
                {
                    Plugin.Logger.LogWarning("PerspectiveSwitcher: HSRTimer.SettingsPanelTabRegistry.Register(string, ISettingsPanelTab) not found.");
                    return;
                }

                Type proxyType = BuildSettingsTabProxy(interfaceType);
                object proxy = Activator.CreateInstance(proxyType);
                object result = register.Invoke(registry, new[] { PluginInfo.PLUGIN_GUID, proxy });
                if (result is bool ok && ok)
                {
                    Enabled = true;
                    Plugin.Logger.LogInfo("PerspectiveSwitcher: settings page registered in HSRTimer settings panel via ISettingsPanelTab.");
                }
                else
                {
                    Plugin.Logger.LogWarning("PerspectiveSwitcher: HSRTimer rejected settings panel tab registration (duplicate or invalid plugin GUID?).");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning($"PerspectiveSwitcher: HSRTimer settings panel tab registration failed: {ex.Message}");
            }
        }

        private static Type BuildSettingsTabProxy(Type interfaceType)
        {
            var asmName = new AssemblyName("PerspectiveSwitcher.SettingsTabProxy." + interfaceType.Assembly.GetName().Name);
            AssemblyBuilder ab = AppDomain.CurrentDomain.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
            ModuleBuilder mb = ab.DefineDynamicModule("SettingsTabProxyModule");
            TypeBuilder tb = mb.DefineType("SettingsTabProxy",
                TypeAttributes.Public | TypeAttributes.Sealed,
                typeof(object), new[] { interfaceType });

            MethodInfo[] methods = interfaceType.GetMethods();
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo mi = methods[i];
                ParameterInfo[] pars = mi.GetParameters();
                Type[] paramTypes = new Type[pars.Length];
                for (int p = 0; p < pars.Length; p++)
                    paramTypes[p] = pars[p].ParameterType;

                MethodBuilder method = tb.DefineMethod(
                    mi.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                    mi.ReturnType, paramTypes);

                ILGenerator il = method.GetILGenerator();
                if (mi.Name == "get_Title")
                {
                    MethodInfo bridge = typeof(SettingsTabBridge).GetMethod(
                        "Title", BindingFlags.Public | BindingFlags.Static);
                    il.Emit(OpCodes.Call, bridge);
                    il.Emit(OpCodes.Ret);
                }
                else
                {
                    MethodInfo bridge = typeof(SettingsTabBridge).GetMethod(
                        "Draw", BindingFlags.Public | BindingFlags.Static);
                    il.Emit(OpCodes.Call, bridge);
                    il.Emit(OpCodes.Ret);
                }
                tb.DefineMethodOverride(method, mi);
            }

            return tb.CreateType();
        }
    }

    /// <summary>
    /// Static bridge the runtime <c>ISettingsPanelTab</c> proxy delegates to.
    /// Kept separate so the emitted proxy only has to call two static methods.
    /// </summary>
    public static class SettingsTabBridge
    {
        public static string Title()
        {
            return L10n.T("PS_TITLE");
        }

        public static void Draw()
        {
            UiShared.EnsureStyles();
            UiShared.RebindCapture();
            UiShared.DrawControls();
        }
    }

    public static class L10n
    {
        private static readonly Dictionary<string, string> _en = new Dictionary<string, string>
        {
            ["PS_TITLE"] = "True First Person",
            ["PS_SECTION_GENERAL"] = "General",
            ["PS_SECTION_CAMERA"] = "First Person Camera",
            ["PS_DEFAULT_FIRST_PERSON"] = "Default First Person",
            ["PS_TOGGLE_KEY"] = "Toggle Perspective Key",
            ["PS_FIRST_PERSON_FOV"] = "Field of View",
            ["PS_VIEW_SMOOTHNESS"] = "Smoothness",
            ["PS_PRESS_KEY"] = "Press a key...",
            ["PS_SAVE"] = "Save",
            ["PS_CLOSE"] = "Close",
            ["PS_FOOTER"] = "Changes apply live; saved on close or exit.",
        };

        private static readonly Dictionary<string, string> _zh = new Dictionary<string, string>
        {
            ["PS_TITLE"] = "真实第一人称",
            ["PS_SECTION_GENERAL"] = "常规",
            ["PS_SECTION_CAMERA"] = "第一人称相机",
            ["PS_DEFAULT_FIRST_PERSON"] = "默认第一人称",
            ["PS_TOGGLE_KEY"] = "切换视角键",
            ["PS_FIRST_PERSON_FOV"] = "视野",
            ["PS_VIEW_SMOOTHNESS"] = "平滑",
            ["PS_PRESS_KEY"] = "请按键...",
            ["PS_SAVE"] = "保存",
            ["PS_CLOSE"] = "关闭",
            ["PS_FOOTER"] = "改动实时生效，关闭面板或退出时保存。",
        };

        public static string T(string key)
        {
            var map = IsChinese() ? _zh : _en;
            string val;
            return map.TryGetValue(key, out val) && !string.IsNullOrEmpty(val) ? val : key;
        }

        private static bool IsChinese()
        {
            bool result = false;
            try
            {
                string name = I2.Loc.LocalizationManager.CurrentLanguage;
                if (!string.IsNullOrEmpty(name))
                {
                    name = name.ToLowerInvariant();
                    result = name.Contains("中") || name.Contains("chinese") || name.StartsWith("zh");
                }
            }
            catch { }
            return result;
        }
    }
}