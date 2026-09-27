using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS ROSTER WINDOW
//
//  Browse every currently-spawned bus and star the ones you'd like to
//  potentially drive. Starring does NOT guarantee you'll get offered that
//  specific bus -- StarredBusAvailabilityNotifier periodically picks ONE
//  random idle starred bus to highlight, so starring several just improves
//  your odds of a relevant nudge rather than reserving any one of them.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusRosterWindow : MonoBehaviour
{
    public static BusRosterWindow Instance { get; private set; }

    private bool _open;
    public bool IsOpen => _open;

    private Rect _windowRect = new Rect(160, 90, 400, 480);
    private bool _dragging;
    private Vector2 _dragOffset;
    private Vector2 _scroll;

    private bool _stylesReady;
    private GUIStyle _lblTitle, _lblDim, _lblBody, _btnSecond, _starOn, _starOff;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("BusRosterWindow").AddComponent<BusRosterWindow>();
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

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle  = MDT_UITheme.MakeLabel(16, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblDim    = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblBody   = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _btnSecond = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 12);
        _starOn    = MDT_UITheme.MakeButton(new Color(0.35f, 0.28f, 0.03f, 1f), MDT_UITheme.TextAmber, 16, FontStyle.Bold);
        _starOff   = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextDim, 16);
    }

    private void OnGUI()
    {
        if (!_open) return;
        EnsureStyles();

        MDT_UITheme.DrawSoftShadow(_windowRect, 18f);
        MDT_UITheme.DrawRoundedRectBordered(_windowRect, 14f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        var titleBar = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, 34);
        HandleDrag(titleBar);
        GUI.Label(new Rect(titleBar.x + 14, titleBar.y, titleBar.width - 60, titleBar.height), "MY BUSES", _lblTitle);
        if (GUI.Button(new Rect(titleBar.xMax - 34, titleBar.y + 5, 24, 24), "✕", _btnSecond))
            _open = false;

        float x = _windowRect.x + 14, y = _windowRect.y + 44, w = _windowRect.width - 28;

        GUI.Label(new Rect(x, y, w, 32), "Star a bus to be a candidate for the occasional \"available\" nudge -- not guaranteed, so star a few for better odds.", _lblDim);
        y += 40;

        var records = BusManager.Instance != null ? BusManager.Instance.GetAllRecords() : null;
        var list = records?.Where(r => r != null && r.controller != null).OrderBy(r => r.controller.fleetNumber).ToList();
        int count = list?.Count ?? 0;

        var listArea = new Rect(x, y, w, _windowRect.height - (y - _windowRect.y) - 14);
        float rowH = 46f;
        _scroll = GUI.BeginScrollView(listArea, _scroll, new Rect(0, 0, w - 20, Mathf.Max(listArea.height, count * rowH)));
        for (int i = 0; i < count; i++)
        {
            var rec = list[i];
            int fleetNumber = rec.controller.fleetNumber;
            var meta = FleetMetadata.Get(fleetNumber);
            var row = new Rect(0, i * rowH, w - 24, rowH - 6);
            MDT_UITheme.DrawRoundedRect(row, 8f, MDT_UITheme.BGRowEven);

            bool starred = StarredBusData.Instance != null && StarredBusData.Instance.IsStarred(fleetNumber);
            if (GUI.Button(new Rect(row.x + 6, row.y + 4, 34, row.height - 8), starred ? "★" : "☆", starred ? _starOn : _starOff))
                StarredBusData.Instance?.Toggle(fleetNumber);

            string status = rec.isIdle ? "Idle" : "In service";
            GUI.Label(new Rect(row.x + 48, row.y + 4, row.width - 60, 18), $"Fleet #{fleetNumber}  ·  {meta?.busType ?? "Unknown"}", _lblBody);
            GUI.Label(new Rect(row.x + 48, row.y + 22, row.width - 60, 16),
                $"{(meta?.homeDepot != null ? meta.homeDepot.depotName : "Unknown depot")}  ·  {status}", _lblDim);
        }
        GUI.EndScrollView();
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
