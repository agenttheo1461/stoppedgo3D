using System.Collections;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  LOADING SCREEN — one full-screen overlay for the whole game.
//
//    LoadingScreen.Show("Opening Route Manager", "Getting ready");
//    LoadingScreen.Set(0.4f, "Loading the map");
//    LoadingScreen.Hide();
//
//    LoadingScreen.Run("Title", MyCoroutine());   // shows it, runs the steps, hides it
//
//  It also covers game start-up: it appears as soon as the game launches and goes
//  away once the city, timetables and buses are ready (or after a safety timeout).
//  Inside a step coroutine, call LoadingScreen.Set(...) and `yield return null`
//  between steps so the bar gets drawn.
// ═══════════════════════════════════════════════════════════════════════════════
public class LoadingScreen : MonoBehaviour
{
    public static LoadingScreen Instance { get; private set; }

    public static bool IsShowing => Instance != null && Instance._visible;

    private bool   _visible;
    private string _title  = "";
    private string _status = "";
    private float  _target;
    private float  _shown;
    private float  _shownAt;
    private float  _hideAt = -1f;

    private GUIStyle _titleStyle, _statusStyle, _hintStyle;
    private Texture2D _bar;

    // ── Public API ───────────────────────────────────────────────────────────
    public static void Show(string title, string status = "")
    {
        var s = Ensure();
        s._visible = true; s._title = title; s._status = status;
        s._target = 0f; s._shown = 0f; s._shownAt = Time.realtimeSinceStartup; s._hideAt = -1f;
    }

    public static void Set(float progress01, string status = null)
    {
        var s = Ensure();
        if (!s._visible) return;
        s._target = Mathf.Clamp01(progress01);
        if (status != null) s._status = status;
    }

    public static void Hide()
    {
        if (Instance == null || !Instance._visible) return;
        Instance._target = 1f;
        Instance._hideAt = Time.realtimeSinceStartup + 0.25f; // let the bar finish
    }

    /// <summary>Show the screen, run `work` (a coroutine that calls Set between steps), then hide it.</summary>
    public static Coroutine Run(string title, IEnumerator work)
    {
        var s = Ensure();
        Show(title);
        return s.StartCoroutine(s.RunRoutine(work));
    }

    private IEnumerator RunRoutine(IEnumerator work)
    {
        yield return null; // draw the first frame before any heavy step
        yield return StartCoroutine(work);
        Hide();
    }

    private static LoadingScreen Ensure()
    {
        if (Instance == null)
        {
            var go = new GameObject("LoadingScreen");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<LoadingScreen>();
        }
        return Instance;
    }

    // ── Game start-up ────────────────────────────────────────────────────────
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void OnGameStart()
    {
        if (!Application.isPlaying) return;
        var s = Ensure();
        Show("Headway!", "Starting up");
        s.StartCoroutine(s.StartupRoutine());
    }

    private IEnumerator StartupRoutine()
    {
        float t0 = Time.realtimeSinceStartup;
        const float minShow = 1.6f, maxShow = 35f;

        while (Time.realtimeSinceStartup - t0 < maxShow)
        {
            bool city   = CityManager.Instance != null;
            bool clock  = SimClock.Instance != null;
            bool sched  = BusScheduler.Instance != null && BusScheduler.Instance.AllSlots != null && BusScheduler.Instance.AllSlots.Count > 0;
            bool fleet  = BusManager.Instance != null && BusManager.Instance.GetAllRecords().Count > 0;
            bool menu   = MainMenu.Instance != null;

            float p = 0.08f;
            string status = "Starting up";
            if (city)  { p = 0.30f; status = "Loading the city"; }
            if (clock && sched) { p = 0.60f; status = "Building today's timetables"; }
            if (fleet) { p = 0.85f; status = "Putting buses on the road"; }
            if (menu && fleet && sched) { p = 1f; status = "Ready"; }
            Set(p, status);

            if (p >= 1f && Time.realtimeSinceStartup - t0 >= minShow) break;
            yield return null;
        }

        // The Route Manager puts its saved schedule back on the timetable a little later (see ManagerMode),
        // after the scheduler has finished its own start-up work.
        ManagerMode.Ensure();
        Hide();
    }

    // ── Drawing ──────────────────────────────────────────────────────────────
    private void Update()
    {
        if (!_visible) return;
        _shown = Mathf.MoveTowards(_shown, _target, Time.unscaledDeltaTime * 1.4f);
        if (_hideAt > 0f && Time.realtimeSinceStartup >= _hideAt && _shown >= 0.999f) { _visible = false; _hideAt = -1f; }
    }

    private void OnGUI()
    {
        if (!_visible) return;
        GUI.depth = -10000;
        if (_titleStyle == null) BuildStyles();

        // Scaled like the manager screen so the text is readable on a phone.
        float ui = ManagerLayout.Scale;
        var oldMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(ui, ui, 1f));
        float W = Screen.width / ui, H = Screen.height / ui;
        Color prev = GUI.color;
        GUI.color = new Color(0.07f, 0.20f, 0.21f, 1f);
        GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
        GUI.color = prev;

        // Swallow clicks so nothing underneath can be used while loading.
        GUI.Box(new Rect(0, 0, W, H), GUIContent.none, GUIStyle.none);

        float cx = W * 0.5f, cy = H * 0.5f;
        GUI.Label(new Rect(cx - 300, cy - 70, 600, 44), _title, _titleStyle);

        float barW = Mathf.Min(420f, W - 80f), barH = 8f;
        var track = new Rect(cx - barW * 0.5f, cy - 6, barW, barH);
        GUI.color = new Color(1f, 1f, 1f, 0.16f);
        GUI.DrawTexture(track, Texture2D.whiteTexture);
        GUI.color = new Color(0.97f, 0.99f, 0.99f, 1f);
        GUI.DrawTexture(new Rect(track.x, track.y, track.width * Mathf.Clamp01(_shown), track.height), Texture2D.whiteTexture);
        GUI.color = prev;

        GUI.Label(new Rect(cx - 300, cy + 12, 600, 26), _status, _statusStyle);

        // Little moving dots so a long step still looks alive.
        int dots = 1 + (int)(Time.realtimeSinceStartup * 2.5f) % 3;
        GUI.Label(new Rect(cx - 300, cy + 40, 600, 22), new string('•', dots), _hintStyle);
        GUI.matrix = oldMatrix;
    }

    private void BuildStyles()
    {
        _titleStyle  = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _titleStyle.normal.textColor = Color.white;
        _statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
        _statusStyle.normal.textColor = new Color(0.85f, 0.95f, 0.95f, 1f);
        _hintStyle   = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
        _hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.55f);
    }
}
