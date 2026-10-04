using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ALERTS CENTER WINDOW
//
//  Draggable, toggleable (KeyBindings.alertsCenter, default F3) window listing
//  every currently active alert from AlertsCenter.GetAll() -- route alerts
//  (breakdowns/road events) and live events side by side, each tagged with
//  its source and color-coded. Same drag/style pattern as RouteRosterWindow.
//
//  "Hella known": auto-opens itself the moment the active-alert count goes
//  from 0 (or from a lower count) to a new alert appearing, so a fresh
//  breakdown/live-event doesn't require the player to already have this
//  open to notice it. Closing it manually suppresses that for the alerts
//  already showing -- it'll auto-open again next time a genuinely NEW one
//  shows up (count increases again).
//
//  Untested in Unity.
// ═══════════════════════════════════════════════════════════════════════════════
public class AlertsCenterWindow : MonoBehaviour
{
    public static AlertsCenterWindow Instance { get; private set; }

    private bool _open;
    public bool IsOpen => _open;

    private Rect _windowRect = new Rect(220, 140, 420, 360);
    private bool _dragging;
    private Vector2 _dragOffset;
    private Vector2 _scroll;

    private int _lastKnownCount = 0;

    private bool _stylesReady;
    private GUIStyle _lblTitle, _lblDim, _lblSource, _lblBody, _btnSecond;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("AlertsCenterWindow").AddComponent<AlertsCenterWindow>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    public void Toggle() => _open = !_open;
    public void Open()  => _open = true;
    public void Close() => _open = false;

    private void Update()
    {
        if (KeyBindings.Current.alertsCenter != KeyCode.None && Input.GetKeyDown(KeyBindings.Current.alertsCenter))
            Toggle();

        int count = AlertsCenter.GetAll().Count;
        if (count > _lastKnownCount && !_open) _open = true; // a new alert just showed up -- "hella known"
        _lastKnownCount = count;
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle  = MDT_UITheme.MakeLabel(16, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblDim    = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblSource = MDT_UITheme.MakeLabel(9,  FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblBody   = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.UpperLeft,  Color.black);
        _btnSecond = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 12);
    }

    private void OnGUI()
    {
        if (!_open) return;
        EnsureStyles();

        MDT_UITheme.DrawSoftShadow(_windowRect, 18f);
        MDT_UITheme.DrawRoundedRectBordered(_windowRect, 14f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        var titleBar = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, 34);
        HandleDrag(titleBar);
        GUI.Label(new Rect(titleBar.x + 14, titleBar.y, titleBar.width - 60, titleBar.height), "ALERTS CENTER", _lblTitle);
        if (GUI.Button(new Rect(titleBar.xMax - 34, titleBar.y + 5, 24, 24), "✕", _btnSecond))
            _open = false;

        float x = _windowRect.x + 14, y = _windowRect.y + 44, w = _windowRect.width - 28;

        var alerts = AlertsCenter.GetAll();

        GUI.Label(new Rect(x, y, w, 18),
            alerts.Count == 0 ? "No active alerts." : $"{alerts.Count} active alert{(alerts.Count > 1 ? "s" : "")}.",
            _lblDim);
        y += 24;

        var listArea = new Rect(x, y, w, _windowRect.height - (y - _windowRect.y) - 14);
        float rowPad = 6f;
        float rowH = 56f;
        _scroll = GUI.BeginScrollView(listArea, _scroll, new Rect(0, 0, w - 20, Mathf.Max(listArea.height, alerts.Count * (rowH + rowPad))));

        // [FIX] Same reasoning as ClientBusPickerWindow's try/finally -- if anything in this loop
        // throws (a malformed alert entry from a source this window doesn't own), skipping
        // GUI.EndScrollView() would corrupt Unity's IMGUI state for the rest of the frame and
        // break OTHER windows' GUI too, not just this one.
        try
        {
            float ry = 0f;
            foreach (var a in alerts)
            {
                var r = new Rect(0, ry, w - 20, rowH);
                GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, a.bgColor, 0, 6);

                GUI.Label(new Rect(r.x + 6, r.y + 2, r.width - 12, 12), $"{a.sourceLabel} · Route {a.routeNumber}", _lblSource);
                GUI.Label(new Rect(r.x + 6, r.y + 15, r.width - 12, rowH - 18), a.message, _lblBody);

                ry += rowH + rowPad;
            }
        }
        finally
        {
            GUI.EndScrollView();
        }
    }

    private void HandleDrag(Rect titleBar)
    {
        var e = Event.current;
        if (e.type == EventType.MouseDown && titleBar.Contains(e.mousePosition))
        {
            _dragging = true;
            _dragOffset = e.mousePosition - new Vector2(_windowRect.x, _windowRect.y);
        }
        else if (e.type == EventType.MouseUp)
        {
            _dragging = false;
        }
        else if (e.type == EventType.MouseDrag && _dragging)
        {
            _windowRect.x = e.mousePosition.x - _dragOffset.x;
            _windowRect.y = e.mousePosition.y - _dragOffset.y;
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0, Screen.width - _windowRect.width);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0, Screen.height - _windowRect.height);
        }
    }
}
