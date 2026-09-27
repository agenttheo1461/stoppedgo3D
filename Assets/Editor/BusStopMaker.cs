#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// ⚠ REQUIRES PLACEMENT IN A FOLDER NAMED "Editor" ANYWHERE UNDER Assets/ ⚠
// This is the standard Unity convention for excluding editor-only scripts
// from player builds — EditorWindow/UnityEditor types don't exist outside
// the editor, so this file (and BusRouteMaker.cs / CityLineMaker.cs) will
// fail to compile in a built player if left in a normal Assets folder.

// ═══════════════════════════════════════════════════════════════════════════════
//  BusStopMaker  —  runtime bus stop placement tool
//  See original header comments for the full tool description (naming
//  convention, stop codes, etc.) — unchanged from the prior version.
//
//  [ADD] TWIN/BATCH RANDOM SPACING
//  ─────────────────────────────────
//  Twin/Batch placement previously always spaced stops perfectly evenly
//  along the road ((i+1)/(n+1) of arc length) — which reads as artificial;
//  real-world bus stops are never uniformly spaced, they follow block
//  lengths and intersections. A new "RANDOM SPACING" toggle jitters each
//  stop within its own even "slot" of the road by up to _twinRandomAmount
//  (a fraction of that slot's width), then clamps the result so it can
//  never cross into a neighboring stop's slot. This keeps stops in the
//  correct order and avoids both giant empty gaps and clustered stops —
//  pure independent-random placement (roll N random points, sort them)
//  was tried and rejected for that reason. At _twinRandomAmount = 0 this
//  behaves identically to the old always-even placement; at 1, a stop can
//  jitter almost to the edge of its slot, right up against its neighbor.
//  Stop count remains capped at 1-100, unchanged from before.
// ═══════════════════════════════════════════════════════════════════════════════

public class BusStopMaker : EditorWindow
{
    [MenuItem("Tools/City Building/Bus Stop Maker")]
    public static void ShowWindow()
    {
        var window = GetWindow<BusStopMaker>("Bus Stop Maker");
        window.minSize = new Vector2(720, 660);
        window.Show();
    }

    public void SetOpen(bool open)
    {
        if (open)
        {
            RefreshAutoCodeIndex();
            Focus();
        }
    }
    public bool IsOpen => true;

    public int panelWidth  = 720;
    public int panelHeight = 660;

    [Header("Naming")]
    public bool rawBlockNumberDefault = false;

    private struct PendingStop
    {
        public string stopCode;
        public string stopName;
        public string parentRoadCode;
        public float  tValue;
        public bool   isTerminal;
        public bool   hasShelter;
        public bool   isLayover;
        public bool   nameIsManual;
    }

    private List<PendingStop> _pending = new List<PendingStop>();
    private int _selectedRoadIdx = -1;
    private float _previewT = 0.5f;
    private bool _rawBlockNumber;
    private int _autoCodeIndex = 1;
    private Vector2 _scrollRoads;
    private Vector2 _scrollPending;
    private GameObject _previewMarker;

    private bool  _twinOpen        = false;
    private int   _twinRoadAIdx    = -1;
    private int   _twinRoadBIdx    = -1;
    private int   _twinStopCount   = 3;
    private float _twinAvoidRadius = 15f;
    private float _twinNudgeDist   = 8f;

    // [ADD] Random/irregular spacing state — see class header comment.
    private bool  _twinRandomSpacing = false;
    private float _twinRandomAmount  = 0.6f; // 0 = no jitter (old even behavior), 1 = can touch slot edges

    [Serializable]
    private class ExportedStop
    {
        public string stopCode;
        public string stopName;
        public string parentRoadCode;
        public float  tValue;
        public bool   isTerminal;
        public bool   hasShelter;
        public bool   isLayover;
    }

    [Serializable]
    private class ExportWrapper
    {
        public List<ExportedStop> stops = new List<ExportedStop>();
    }

    private static string ExportFilePath => Path.Combine(Application.persistentDataPath, "BusStopMaker_PendingExport.json");

    private static CityManager ResolveCityManager()
    {
        var cm = CityManager.Instance;
        if (cm == null) cm = UnityEngine.Object.FindObjectOfType<CityManager>();
        return cm;
    }

    private bool  _stylesReady;

    private GUIStyle _lblTitle, _lblSection, _lblBody, _lblDim, _lblCode, _lblPreviewName;
    private GUIStyle _btnClose, _btnPrimary, _btnDanger, _btnSecond;
    private GUIStyle _chipOn, _chipOff;
    private GUIStyle _textField, _sliderValLabel;

    private const float HeaderH   = 34f;
    private const float Pad       = 12f;
    private const float RoadRowH  = 24f;
    private const float ChipH     = 22f;

    private void OnEnable()
    {
        _rawBlockNumber = rawBlockNumberDefault;
        RefreshAutoCodeIndex();
        EditorApplication.update += EditorUpdateTick;
    }

    private void OnDisable()
    {
        EditorApplication.update -= EditorUpdateTick;
        if (_previewMarker != null) _previewMarker.SetActive(false);
        ExportPendingToDisk();
    }

    private void EditorUpdateTick()
    {
        UpdatePreviewMarker();
    }

    private void ExportPendingToDisk()
    {
        if (_pending.Count == 0) return;

        var wrapper = new ExportWrapper();
        foreach (var p in _pending)
        {
            wrapper.stops.Add(new ExportedStop
            {
                stopCode       = p.stopCode,
                stopName       = p.stopName,
                parentRoadCode = p.parentRoadCode,
                tValue         = p.tValue,
                isTerminal     = p.isTerminal,
                hasShelter     = p.hasShelter,
                isLayover      = p.isLayover
            });
        }

        try
        {
            File.WriteAllText(ExportFilePath, JsonUtility.ToJson(wrapper, true));
            Debug.Log($"[BusStopMaker] Exported {wrapper.stops.Count} pending stop(s) to:\n{ExportFilePath}\n" +
                      "After exiting Play Mode, use Tools ▸ Bus Stop Maker ▸ Import Pending Stops to write them into the scene for real.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[BusStopMaker] Failed to export pending stops: {e.Message}");
        }
    }

    private void OnGUI()
    {
        EnsureStyles();

        panelWidth  = (int)position.width;
        panelHeight = (int)position.height;

        var panelRect = new Rect(0, 0, panelWidth, panelHeight);
        MDT_UITheme.DrawPanel(panelRect);

        GUI.BeginGroup(panelRect);

        DrawHeader(new Rect(0, 0, panelWidth, HeaderH));

        float y = HeaderH + Pad;
        y = DrawPlayModeBanner(y);
        y = DrawRoadPicker(y);
        y += Pad;
        y = DrawTSliderAndPreview(y);
        y += Pad;
        y = DrawAddButton(y);
        y += Pad;
        y = DrawTwinPanel(y);
        y += Pad;

        float footerH = 40f;
        float pendingBottom = panelHeight - footerH - Pad;
        DrawPendingList(y, pendingBottom - y);

        DrawFooterButtons(new Rect(Pad, panelHeight - footerH - Pad * 0.5f, panelWidth - Pad * 2f, footerH));

        GUI.EndGroup();
    }

    private void DrawHeader(Rect r)
    {
        MDT_UITheme.DrawHeader(r);
        GUI.Label(new Rect(Pad, 0, r.width - 200, r.height), "BUS STOP MAKER", _lblTitle);
        GUI.Label(new Rect(r.width - 180, 0, 170, r.height), $"Next code: {FormatStopCode(_autoCodeIndex)}", _lblDim);
    }

    private float DrawPlayModeBanner(float y)
    {
        if (!Application.isPlaying || _pending.Count == 0) return y;

        var r = new Rect(Pad, y, panelWidth - Pad * 2f, 26f);
        MDT_UITheme.DrawRoundedRect(r, MDT_UITheme.RadiusRow, new Color(0.30f, 0.20f, 0.04f, 1f));
        GUI.Label(r, "  ⚠ PLAY MODE — click EXPORT before stopping, then Tools ▸ Bus Stop Maker ▸ Import Pending Stops",
                  MDT_UITheme.MakeLabel(10, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextAmber));
        return y + r.height + 6f;
    }

    private float DrawRoadPicker(float y)
    {
        GUI.Label(new Rect(Pad, y, 200, 18), "ROAD", _lblSection);
        y += 20f;

        var cm = ResolveCityManager();
        var listRect = new Rect(Pad, y, panelWidth - Pad * 2f, 120f);
        MDT_UITheme.DrawInset(listRect, MDT_UITheme.BGMid);

        if (cm == null || cm.roadDefinitions == null || cm.roadDefinitions.Count == 0)
        {
            GUI.Label(new Rect(listRect.x + 8, listRect.y + 8, listRect.width - 16, 20),
                      "No CityManager / roads found.", _lblDim);
            return y + listRect.height;
        }

        float viewH = RoadRowH * cm.roadDefinitions.Count;
        var viewRect = new Rect(0, 0, listRect.width - 16, Mathf.Max(viewH, listRect.height));
        _scrollRoads = GUI.BeginScrollView(listRect, _scrollRoads, viewRect);

        for (int i = 0; i < cm.roadDefinitions.Count; i++)
        {
            var def = cm.roadDefinitions[i];
            if (def == null) continue;

            var rowRect = new Rect(0, i * RoadRowH, viewRect.width, RoadRowH);
            bool selected = (i == _selectedRoadIdx);
            MDT_UITheme.DrawRect(rowRect, selected ? MDT_UITheme.BGPillSel
                                                    : (i % 2 == 0 ? MDT_UITheme.BGRowEven : MDT_UITheme.BGRowOdd));

            if (GUI.Button(rowRect, "", GUIStyle.none))
            {
                _selectedRoadIdx = i;
                _previewT = 0.5f;
            }
            GUI.Label(new Rect(rowRect.x + 8, rowRect.y, rowRect.width - 16, rowRect.height),
                      $"{def.roadName}  [{def.roadCode}]  w={def.roadWidth}",
                      selected ? _lblBody : _lblDim);
        }

        GUI.EndScrollView();
        return y + listRect.height;
    }

    private float DrawTSliderAndPreview(float y)
    {
        GUI.Label(new Rect(Pad, y, 200, 18), "PLACEMENT", _lblSection);
        y += 20f;

        var cm = ResolveCityManager();
        var boxRect = new Rect(Pad, y, panelWidth - Pad * 2f, 96f);
        MDT_UITheme.DrawInset(boxRect, MDT_UITheme.BGMid);

        if (cm == null || _selectedRoadIdx < 0 || _selectedRoadIdx >= cm.roadDefinitions.Count)
        {
            GUI.Label(new Rect(boxRect.x + 8, boxRect.y + 8, boxRect.width - 16, 20), "Select a road above.", _lblDim);
            return y + boxRect.height;
        }

        var def  = cm.roadDefinitions[_selectedRoadIdx];
        var road = cm.GetRoad(def.roadCode);
        if (road == null)
        {
            GUI.Label(new Rect(boxRect.x + 8, boxRect.y + 8, boxRect.width - 16, 20), "Road not built yet.", _lblDim);
            if (GUI.Button(new Rect(boxRect.x + 8, boxRect.y + 32, 200, 26), "Build City Now", _btnPrimary))
                cm.BuildCity();
            return y + boxRect.height;
        }

        float ix = boxRect.x + 10f;
        float iy = boxRect.y + 8f;
        float iw = boxRect.width - 20f;

        GUI.Label(new Rect(ix, iy, 16, 20), "t:", _lblBody);
        _previewT = GUI.HorizontalSlider(new Rect(ix + 20, iy + 3, iw - 90, 16), _previewT, 0f, 1f);
        GUI.Label(new Rect(ix + iw - 60, iy, 60, 20), _previewT.ToString("0.000"), _sliderValLabel);
        iy += 24f;

        Vector3 pos = road.EvaluatePosition(_previewT);
        GUI.Label(new Rect(ix, iy, iw, 18), $"Position: ({pos.x:0.0}, {pos.z:0.0})", _lblDim);
        iy += 20f;

        if (Chip(new Rect(ix, iy, 220f, ChipH), "RAW BLOCK NUMBER", _rawBlockNumber))
            _rawBlockNumber = !_rawBlockNumber;
        iy += ChipH + 6f;

        string previewName = GenerateStopName(def, road, _previewT, _rawBlockNumber);
        var nameRect = new Rect(ix, iy, iw, 20f);
        MDT_UITheme.DrawRoundedRect(nameRect, MDT_UITheme.RadiusRow, MDT_UITheme.BGPill);
        GUI.Label(nameRect, $"  {previewName}", _lblPreviewName);

        return y + boxRect.height;
    }

    private void UpdatePreviewMarker()
    {
        var cm = ResolveCityManager();
        if (cm == null || _selectedRoadIdx < 0 || _selectedRoadIdx >= cm.roadDefinitions.Count) return;

        var def  = cm.roadDefinitions[_selectedRoadIdx];
        var road = cm.GetRoad(def.roadCode);
        if (road == null) return;

        Vector3 pos = road.EvaluatePosition(_previewT);
        pos.y += 12f;

        if (_previewMarker == null)
        {
            _previewMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _previewMarker.name = "StopMaker_PreviewMarker";
            _previewMarker.transform.localScale = Vector3.one * 3f;
            UnityEngine.Object.DestroyImmediate(_previewMarker.GetComponent<Collider>());
            var rend = _previewMarker.GetComponent<MeshRenderer>();
            rend.sharedMaterial = new Material(Shader.Find("Standard")) { color = Color.magenta };
        }
        _previewMarker.transform.position = pos;
        _previewMarker.SetActive(true);
    } 

    private float DrawAddButton(float y)
    {
        var cm = ResolveCityManager();
        bool canAdd = cm != null && _selectedRoadIdx >= 0 && _selectedRoadIdx < cm.roadDefinitions.Count;

        var btnRect = new Rect(Pad, y, panelWidth - Pad * 2f, 30f);
        bool prevEnabled = GUI.enabled;
        GUI.enabled = canAdd;
        if (GUI.Button(btnRect, $"+ ADD STOP HERE  (will be {FormatStopCode(_autoCodeIndex)})", _btnPrimary) && canAdd)
        {
            var def  = cm.roadDefinitions[_selectedRoadIdx];
            var road = cm.GetRoad(def.roadCode);
            if (road != null)
            {
                string code = FormatStopCode(_autoCodeIndex);
                _autoCodeIndex++;

                _pending.Add(new PendingStop
                {
                    stopCode       = code,
                    stopName       = GenerateStopName(def, road, _previewT, _rawBlockNumber),
                    parentRoadCode = def.roadCode,
                    tValue         = _previewT,
                    isTerminal     = false,
                    hasShelter     = false,
                    isLayover      = false,
                    nameIsManual   = false
                });
            }
        }
        GUI.enabled = prevEnabled;

        return y + btnRect.height;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  TWIN / BATCH PLACEMENT
    //  [ADD] Now includes a RANDOM SPACING toggle + amount slider — see the
    //  class-level header comment for how the jitter is computed.
    // ═════════════════════════════════════════════════════════════════════════
    private float DrawTwinPanel(float y)
    {
        var cm = ResolveCityManager();

        var headerRect = new Rect(Pad, y, panelWidth - Pad * 2f, 26f);
        if (GUI.Button(headerRect, (_twinOpen ? "▾ " : "▸ ") + "TWIN / BATCH PLACEMENT", _btnSecond))
            _twinOpen = !_twinOpen;
        y += 26f + 6f;

        if (!_twinOpen) return y;

        if (cm == null)
        {
            GUI.Label(new Rect(Pad, y, panelWidth - Pad * 2f, 20f), "No CityManager instance.", _lblDim);
            return y + 24f;
        }

        // [FIX] Box height bumped from 168f to 194f to fit the new
        // random-spacing row without overlapping the Avoid/Nudge sliders.
        var boxRect = new Rect(Pad, y, panelWidth - Pad * 2f, 194f);
        MDT_UITheme.DrawRoundedRect(boxRect, MDT_UITheme.RadiusRow, MDT_UITheme.BGPill);
        float ix = boxRect.x + 10f;
        float iy = boxRect.y + 8f;
        float iw = boxRect.width - 20f;

        GUI.Label(new Rect(ix, iy, iw, 18f), "Road A:", _lblDim);
        string roadALabel = RoadLabelOrNone(cm, _twinRoadAIdx);
        if (GUI.Button(new Rect(ix + 60f, iy - 2f, iw - 60f, 22f), roadALabel, _btnSecond))
            _twinRoadAIdx = _selectedRoadIdx;
        iy += 26f;

        GUI.Label(new Rect(ix, iy, iw, 18f), "Road B:", _lblDim);
        string roadBLabel = RoadLabelOrNone(cm, _twinRoadBIdx);
        if (GUI.Button(new Rect(ix + 60f, iy - 2f, iw - 130f, 22f), roadBLabel, _btnSecond))
            _twinRoadBIdx = _selectedRoadIdx;
        if (_twinRoadBIdx >= 0 && GUI.Button(new Rect(ix + iw - 64f, iy - 2f, 64f, 22f), "clear", _btnDanger))
            _twinRoadBIdx = -1;
        iy += 26f;

        GUI.Label(new Rect(ix, iy, 90f, 18f), $"Stops: {_twinStopCount}", _lblDim);
        _twinStopCount = Mathf.RoundToInt(GUI.HorizontalSlider(new Rect(ix + 90f, iy + 2f, iw - 90f, 14f), _twinStopCount, 1, 100));
        iy += 22f;

        // [ADD] Random spacing toggle + amount slider (only shown when on).
        if (Chip(new Rect(ix, iy, 160f, ChipH), "RANDOM SPACING", _twinRandomSpacing))
            _twinRandomSpacing = !_twinRandomSpacing;
        if (_twinRandomSpacing)
        {
            GUI.Label(new Rect(ix + 170f, iy, 60f, ChipH), $"{_twinRandomAmount:0.00}", _lblDim);
            _twinRandomAmount = GUI.HorizontalSlider(new Rect(ix + 230f, iy + 4f, iw - 230f, 14f), _twinRandomAmount, 0f, 1f);
        }
        iy += ChipH + 6f;

        GUI.Label(new Rect(ix, iy, iw, 18f), $"Avoid radius: {_twinAvoidRadius:0}m   Nudge: {_twinNudgeDist:0}m", _lblDim);
        iy += 20f;
        _twinAvoidRadius = GUI.HorizontalSlider(new Rect(ix, iy + 2f, iw * 0.48f, 14f), _twinAvoidRadius, 5f, 40f);
        _twinNudgeDist   = GUI.HorizontalSlider(new Rect(ix + iw * 0.52f, iy + 2f, iw * 0.48f, 14f), _twinNudgeDist, 2f, 25f);
        iy += 26f;

        bool canGenerate = _twinRoadAIdx >= 0 && _twinRoadAIdx < cm.roadDefinitions.Count;
        bool prevEnabled = GUI.enabled;
        GUI.enabled = canGenerate;
        string genLabel = _twinRoadBIdx >= 0
            ? $"GENERATE {_twinStopCount * 2} STOPS (TWIN)"
            : $"GENERATE {_twinStopCount} STOP(S)";
        if (GUI.Button(new Rect(ix, iy, iw, 28f), genLabel, _btnPrimary) && canGenerate)
            GenerateTwinStops(cm);
        GUI.enabled = prevEnabled;

        return y + boxRect.height + 4f;
    }

    private string RoadLabelOrNone(CityManager cm, int idx)
    {
        if (idx < 0 || idx >= cm.roadDefinitions.Count) return "— tap ROAD list above, then here —";
        var def = cm.roadDefinitions[idx];
        return $"{def.roadName} [{def.roadCode}]";
    }

    private void GenerateTwinStops(CityManager cm)
    {
        GenerateStopsAlongRoad(cm, _twinRoadAIdx);
        if (_twinRoadBIdx >= 0) GenerateStopsAlongRoad(cm, _twinRoadBIdx);
    }

    private void GenerateStopsAlongRoad(CityManager cm, int roadIdx)
    {
        if (roadIdx < 0 || roadIdx >= cm.roadDefinitions.Count) return;
        var def  = cm.roadDefinitions[roadIdx];
        var road = cm.GetRoad(def.roadCode);
        if (road == null)
        {
            Debug.LogWarning($"[BusStopMaker] Twin placement: road '{def.roadCode}' not built yet — skipped.");
            return;
        }

        var graph = cm.Graph;
        float totalLen = road.ApproximateLength();
        if (totalLen < 1f) return;

        int n = Mathf.Max(1, _twinStopCount);

        // [ADD] Each stop's "slot" is an equal share of the road's length —
        // (i+1) of (n+1) equal divisions, same base positions as before.
        // Random spacing jitters WITHIN a slot rather than picking fully
        // independent random points, so stops stay in order and can't
        // cluster or leave huge gaps — see class header comment.
        float slotWidth = totalLen / (n + 1);

        for (int i = 0; i < n; i++)
        {
            float targetArcLen = slotWidth * (i + 1);

            if (_twinRandomSpacing)
            {
                float maxJitter = slotWidth * 0.5f * _twinRandomAmount;
                targetArcLen += UnityEngine.Random.Range(-maxJitter, maxJitter);
                // Clamp to this slot's bounds so order along the road is
                // always preserved and stops can't collide with neighbors.
                targetArcLen = Mathf.Clamp(targetArcLen, slotWidth * i + 1f, slotWidth * (i + 2) - 1f);
            }

            float arcLen = NudgeAwayFromIntersections(road, targetArcLen, totalLen, graph);
            float t = TAtArcLength(road, arcLen, totalLen);

            string code = FormatStopCode(_autoCodeIndex);
            _autoCodeIndex++;

            _pending.Add(new PendingStop
            {
                stopCode       = code,
                stopName       = GenerateStopName(def, road, t, _rawBlockNumber),
                parentRoadCode = def.roadCode,
                tValue         = t,
                isTerminal     = false,
                hasShelter     = false,
                isLayover      = false,
                nameIsManual   = false
            });
        }

        string spacingNote = _twinRandomSpacing ? $" (random spacing, amount={_twinRandomAmount:0.00})" : " (even spacing)";
        Debug.Log($"[BusStopMaker] Twin placement: added {n} stop(s) along '{def.roadCode}'{spacingNote}.");
    }

    private float NudgeAwayFromIntersections(RoadSegment road, float arcLen, float totalLen, RoadGraph graph)
    {
        if (graph == null) return arcLen;

        const int maxAttempts = 6;
        float candidate = arcLen;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            float t = TAtArcLength(road, candidate, totalLen);
            Vector3 pos = road.EvaluatePosition(t);

            bool onIntersection = false;
            foreach (var node in graph.nodes)
            {
                if (Vector3.Distance(node.position, pos) <= _twinAvoidRadius) { onIntersection = true; break; }
            }
            if (!onIntersection) return candidate;

            float forward = candidate + _twinNudgeDist;
            candidate = forward <= totalLen - 1f ? forward : Mathf.Max(1f, arcLen - _twinNudgeDist);
        }

        Debug.LogWarning("[BusStopMaker] Twin placement: couldn't clear an intersection after several nudges — " +
                          "placing at closest attempt anyway (this stretch may have back-to-back crossings).");
        return candidate;
    }

    private float TAtArcLength(RoadSegment road, float targetArcLen, float totalLen, int samples = 200)
    {
        targetArcLen = Mathf.Clamp(targetArcLen, 0f, totalLen);

        Vector3 prev = road.EvaluatePosition(0f);
        float   accum = 0f;

        for (int i = 1; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector3 next = road.EvaluatePosition(t);
            float   segLen = Vector3.Distance(prev, next);

            if (accum + segLen >= targetArcLen)
            {
                float localFrac = segLen > 0.0001f ? (targetArcLen - accum) / segLen : 0f;
                return Mathf.Lerp((i - 1) / (float)samples, t, localFrac);
            }

            accum += segLen;
            prev = next;
        }

        return 1f;
    }

    private void DrawPendingList(float y, float height)
    {
        GUI.Label(new Rect(Pad, y, 300, 18), $"SESSION STOPS  ({_pending.Count})", _lblSection);
        y += 20f;
        height -= 20f;

        var listRect = new Rect(Pad, y, panelWidth - Pad * 2f, Mathf.Max(height, 40f));
        MDT_UITheme.DrawInset(listRect, MDT_UITheme.BGMid);

        var cm = ResolveCityManager();
        int removeIdx = -1;

        const float rowH = 92f;
        float viewH = rowH * _pending.Count;
        var viewRect = new Rect(0, 0, listRect.width - 16, Mathf.Max(viewH, listRect.height));
        _scrollPending = GUI.BeginScrollView(listRect, _scrollPending, viewRect);

        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            var rowOuter = new Rect(4, i * rowH + 4, viewRect.width - 8, rowH - 8);
            MDT_UITheme.DrawRoundedRect(rowOuter, MDT_UITheme.RadiusRow,
                                        i % 2 == 0 ? MDT_UITheme.BGRowEven : MDT_UITheme.BGRowOdd);

            float rx = rowOuter.x + 8f;
            float ry = rowOuter.y + 6f;
            float rw = rowOuter.width - 16f;

            GUI.Label(new Rect(rx, ry, 66, 20), p.stopCode, _lblCode);
            string newName = GUI.TextField(new Rect(rx + 70, ry, rw - 70 - 30, 20), p.stopName, _textField);
            if (newName != p.stopName) { p.stopName = newName; p.nameIsManual = true; }
            if (GUI.Button(new Rect(rx + rw - 24, ry, 24, 20), "✕", _btnDanger)) removeIdx = i;
            ry += 24f;

            GUI.Label(new Rect(rx, ry, 16, 20), "t:", _lblDim);
            float newT = GUI.HorizontalSlider(new Rect(rx + 20, ry + 3, rw - 90, 14), p.tValue, 0f, 1f);
            if (!Mathf.Approximately(newT, p.tValue))
            {
                p.tValue = newT;
                if (!p.nameIsManual && cm != null)
                {
                    var def  = cm.roadDefinitions.Find(d => d.roadCode == p.parentRoadCode);
                    var road = def != null ? cm.GetRoad(def.roadCode) : null;
                    if (def != null && road != null)
                        p.stopName = GenerateStopName(def, road, p.tValue, _rawBlockNumber);
                }
            }
            GUI.Label(new Rect(rx + rw - 60, ry, 60, 20), p.tValue.ToString("0.000"), _lblDim);
            ry += 22f;

            float chipW = (rw - 12f) / 3f;
            if (Chip(new Rect(rx, ry, chipW, ChipH), "TERMINAL", p.isTerminal)) p.isTerminal = !p.isTerminal;
            if (Chip(new Rect(rx + chipW + 6f, ry, chipW, ChipH), "SHELTER", p.hasShelter)) p.hasShelter = !p.hasShelter;
            if (Chip(new Rect(rx + (chipW + 6f) * 2f, ry, chipW, ChipH), "LAYOVER", p.isLayover)) p.isLayover = !p.isLayover;

            _pending[i] = p;
        }

        GUI.EndScrollView();

        if (removeIdx >= 0) _pending.RemoveAt(removeIdx);
    }

    private void DrawFooterButtons(Rect r)
    {
        float gap = 10f;
        float clearW = 120f;
        float exportW = 120f;
        var clearRect  = new Rect(r.x, r.y, clearW, r.height);
        var exportRect = new Rect(r.x + clearW + gap, r.y, exportW, r.height);
        var saveRect   = new Rect(r.x + clearW + gap + exportW + gap, r.y,
                                   r.width - clearW - exportW - gap * 2f, r.height);

        if (GUI.Button(clearRect, "CLEAR SESSION", _btnSecond))
            _pending.Clear();

        bool prevEnabled = GUI.enabled;
        GUI.enabled = _pending.Count > 0;

        if (GUI.Button(exportRect, "EXPORT", _btnSecond))
            ExportPendingToDisk();

        if (GUI.Button(saveRect, $"SAVE {_pending.Count} STOP(S) TO CITYMANAGER", _btnPrimary))
            SaveAllStops();
        GUI.enabled = prevEnabled;
    }

    private bool Chip(Rect r, string label, bool active)
    {
        bool clicked = GUI.Button(r, "", GUIStyle.none);
        MDT_UITheme.DrawRoundedRect(r, MDT_UITheme.RadiusPill, active ? MDT_UITheme.BGDirSel : MDT_UITheme.BGButton);
        GUI.Label(r, label, active ? _chipOn : _chipOff);
        return clicked;
    }

    private void SaveAllStops()
    {
        var cm = ResolveCityManager();
        if (cm == null) { Debug.LogWarning("[BusStopMaker] No CityManager instance — can't save."); return; }

        ExportPendingToDisk();

        int added = 0, updated = 0;
        foreach (var p in _pending)
        {
            var existing = cm.stopDefinitions.Find(s => s != null && s.stopCode == p.stopCode);
            if (existing != null)
            {
                existing.stopName       = p.stopName;
                existing.parentRoadCode = p.parentRoadCode;
                existing.tValue         = p.tValue;
                existing.isTerminal     = p.isTerminal;
                existing.hasShelter     = p.hasShelter;
                existing.isLayover      = p.isLayover;
                updated++;
                continue;
            }

            var stop = new BusStopData
            {
                stopCode       = p.stopCode,
                stopName       = p.stopName,
                parentRoadCode = p.parentRoadCode,
                tValue         = p.tValue,
                isTerminal     = p.isTerminal,
                hasShelter     = p.hasShelter,
                isLayover      = p.isLayover,
                isAccessible   = true
            };
            cm.stopDefinitions.Add(stop);
            added++;
        }

        Debug.Log($"[BusStopMaker] Saved {added} new / {updated} updated stop(s) to CityManager.stopDefinitions " +
                  $"({_pending.Count} total in session). " +
                  "Call CityManager's rebuild/resolve step (or re-enter play mode) to pick them up, " +
                  "and remember to persist the CityManager asset/scene if this should survive a restart.");

        _pending.Clear();
        RefreshAutoCodeIndex();
    }

    private void RefreshAutoCodeIndex()
    {
        int maxIdx = 0;
        var cm = ResolveCityManager();

        if (cm != null && cm.stopDefinitions != null)
        {
            foreach (var s in cm.stopDefinitions)
            {
                if (s == null) continue;
                if (TryParseStopCodeIndex(s.stopCode, out int idx) && idx > maxIdx) maxIdx = idx;
            }
        }

        foreach (var p in _pending)
        {
            if (TryParseStopCodeIndex(p.stopCode, out int idx) && idx > maxIdx) maxIdx = idx;
        }

        _autoCodeIndex = maxIdx + 1;
    }

    private static bool TryParseStopCodeIndex(string code, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(code) || code.Length < 2) return false;
        if (code[0] != 's' && code[0] != 'S') return false;
        return int.TryParse(code.Substring(1), out index);
    }

    private static string FormatStopCode(int index) => $"s{index:0000}";

    private enum RoadGridType { Avenue, Street, Directional }

    private static RoadGridType ClassifyRoad(string roadName)
    {
        string upper = (roadName ?? "").ToUpperInvariant();
        if (EndsWithWord(upper, "AVENUE") || EndsWithWord(upper, "AVE") || EndsWithWord(upper, "AV"))
            return RoadGridType.Avenue;
        if (EndsWithWord(upper, "STREET") || EndsWithWord(upper, "ST"))
            return RoadGridType.Street;
        return RoadGridType.Directional;
    }

    private static bool EndsWithWord(string upper, string word)
    {
        if (!upper.EndsWith(word)) return false;
        int cut = upper.Length - word.Length;
        return cut == 0 || upper[cut - 1] == ' ';
    }

    private static readonly System.Text.RegularExpressions.Regex OrdinalSuffixRe =
        new System.Text.RegularExpressions.Regex(@"(?<=\d)(st|nd|rd|th)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string StripOrdinal(string s) => OrdinalSuffixRe.Replace(s ?? "", "");

    private static string StripSuffix(string roadName, RoadGridType type)
    {
        string cleaned = StripOrdinal((roadName ?? "").Trim()).ToUpperInvariant();
        if (type == RoadGridType.Directional) return cleaned;

        string digits = new string(cleaned.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return cleaned;

        string suffix = type == RoadGridType.Avenue ? "AV" : "ST";
        return $"{digits} {suffix}";
    }

    // Stop naming convention:  "<dir> <n> AV|ST & <dir> <n> ST|AV"  (n = |coord| / 10, unsigned).
    //   AV roads run north-south and take E/W from which side of x=0 they sit on.
    //   ST roads run east-west and take N/S from which side of z=0 they sit on.
    //   A road ON the zero line (n == 0) has no side of its own, so it takes the letter of the
    //   axis it runs along, read off the stop's position: "N 0 AV & N 62 ST", "E 0 ST & E 27 AV".
    //   Custom-named roads (Leaf Blvd...) keep their name: "LEAF BLVD & W 185 AV". Curved / diagonal
    //   roads keep the quadrant form: "NE SUMMIT BLVD & NE 340 ST".
    private string GenerateStopName(RoadSegmentDefinition def, RoadSegment road, float t, bool rawBlock)
    {
        if (def == null || road == null) return "UNNAMED STOP";

        Vector3 startPos = road.EvaluatePosition(0f);
        Vector3 midPos   = road.EvaluatePosition(0.5f);
        Vector3 endPos   = road.EvaluatePosition(1f);
        Vector3 pointPos = road.EvaluatePosition(t);

        RoadGridType type = ClassifyRoad(def.roadName);

        bool straightNS = Mathf.Abs(endPos.x - startPos.x) < 1f && Mathf.Abs(midPos.x - startPos.x) < 1f;
        bool straightEW = Mathf.Abs(endPos.z - startPos.z) < 1f && Mathf.Abs(midPos.z - startPos.z) < 1f;

        // Custom-named roads (Leaf Blvd, Berrelingway...) keep their name in ALL CAPS; only the cross street gets the grid form.
        // Rename a road to "<n>th Ave/St" if it should be named by the grid instead.
        if (type == RoadGridType.Directional && (straightNS || straightEW))
        {
            float along = straightNS ? pointPos.z : pointPos.x;
            string crossUnit = straightNS ? "ST" : "AV";
            string cross = FormatCrossNumber(along, rawBlock, crossUnit);
            bool zero = !rawBlock && Mathf.RoundToInt(Mathf.Abs(along) / 10f) == 0;
            string pre = straightNS ? (zero ? (pointPos.x < 0f ? "W" : "E") : (along < 0f ? "S" : "N"))
                                    : (zero ? (pointPos.z < 0f ? "S" : "N") : (along < 0f ? "W" : "E"));
            return $"{def.roadName.Trim().ToUpperInvariant()} & {pre} {cross}";
        }

        if (type == RoadGridType.Directional)
        {
            float dx = Mathf.Abs(endPos.x - startPos.x);
            float dz = Mathf.Abs(endPos.z - startPos.z);
            bool runsNS = dz >= dx;

            string ns   = pointPos.z < 0f ? "S" : "N";
            string ew   = pointPos.x < 0f ? "W" : "E";
            string quad = $"{ns}{ew}";

            string quadCross = runsNS
                ? FormatCrossNumber(pointPos.z, rawBlock, "ST")
                : FormatCrossNumber(pointPos.x, rawBlock, "AV");

            return $"{quad} {StripSuffix(def.roadName, type)} & {quad} {quadCross}";
        }

        bool isAvenue = type == RoadGridType.Avenue;
        float roadCoord = isAvenue ? (startPos.x + endPos.x) * 0.5f
                                   : (startPos.z + endPos.z) * 0.5f;

        // Number comes from the road's own name when it has one ("-333rd Ave" -> 333), else from its position.
        string digits = new string(StripOrdinal(def.roadName ?? "").Where(char.IsDigit).ToArray());
        int roadNumber = digits.Length > 0 ? int.Parse(digits) : Mathf.RoundToInt(Mathf.Abs(roadCoord) / 10f);
        string baseName = $"{roadNumber} {(isAvenue ? "AV" : "ST")}";

        // Cross street lies along the road.
        float crossCoord = isAvenue ? pointPos.z : pointPos.x;
        string crossLabel = FormatCrossNumber(crossCoord, rawBlock, isAvenue ? "ST" : "AV");
        bool crossIsZero = !rawBlock && Mathf.RoundToInt(Mathf.Abs(crossCoord) / 10f) == 0;

        string roadPrefix, crossPrefix;
        if (isAvenue)
        {
            roadPrefix  = roadNumber == 0 ? (pointPos.z < 0f ? "S" : "N") : (roadCoord < 0f ? "W" : "E");
            crossPrefix = crossIsZero     ? (pointPos.x < 0f ? "W" : "E") : (crossCoord < 0f ? "S" : "N");
        }
        else
        {
            roadPrefix  = roadNumber == 0 ? (pointPos.x < 0f ? "W" : "E") : (roadCoord < 0f ? "S" : "N");
            crossPrefix = crossIsZero     ? (pointPos.z < 0f ? "S" : "N") : (crossCoord < 0f ? "W" : "E");
        }

        return $"{roadPrefix} {baseName} & {crossPrefix} {crossLabel}";
    }

    private static string FormatCrossNumber(float coord, bool rawBlock, string unitSuffix)
    {
        if (rawBlock)
            return $"{Mathf.RoundToInt(Mathf.Abs(coord))} BLOCK";

        int number = Mathf.RoundToInt(Mathf.Abs(coord) / 10f);
        return $"{number} {unitSuffix}";
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return;
        _stylesReady = true;

        _lblTitle       = MDT_UITheme.MakeLabel(15, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblSection     = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextAmber);
        _lblBody        = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _lblDim         = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblCode        = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblPreviewName = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextGreen);
        _sliderValLabel = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleRight, MDT_UITheme.TextSecond);

        _btnClose   = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 11, FontStyle.Bold);
        _btnPrimary = MDT_UITheme.MakeButton(new Color(0.05f, 0.30f, 0.14f, 1f), MDT_UITheme.TextGreen, 11, FontStyle.Bold);
        _btnDanger  = MDT_UITheme.MakeButton(new Color(0.30f, 0.08f, 0.08f, 1f), MDT_UITheme.TextRed,   10, FontStyle.Bold);
        _btnSecond  = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 11, FontStyle.Bold);

        _chipOn  = MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextGreen);
        _chipOff = MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextDim);

        _textField = new GUIStyle(GUI.skin.textField)
        {
            fontSize  = 11,
            alignment = TextAnchor.MiddleLeft,
        };
        _textField.normal.textColor = MDT_UITheme.TextPrimary;
        MDT_UITheme.SetBg(_textField, MDT_UITheme.BGPill, MDT_UITheme.BGPill, MDT_UITheme.BGPill);
    }
}

#if UNITY_EDITOR
// ═══════════════════════════════════════════════════════════════════════════════
//  EDITOR-ONLY IMPORTER (unchanged from prior version)
// ═══════════════════════════════════════════════════════════════════════════════
public static class BusStopMakerImporter
{
    [Serializable]
    private class ExportedStop
    {
        public string stopCode;
        public string stopName;
        public string parentRoadCode;
        public float  tValue;
        public bool   isTerminal;
        public bool   hasShelter;
        public bool   isLayover;
    }

    [Serializable]
    private class ExportWrapper
    {
        public List<ExportedStop> stops = new List<ExportedStop>();
    }

    private static string ExportFilePath => Path.Combine(Application.persistentDataPath, "BusStopMaker_PendingExport.json");

    [MenuItem("Tools/Bus Stop Maker/Import Pending Stops")]
    public static void ImportPendingStops()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogWarning("[BusStopMakerImporter] Exit Play Mode first — imports only persist when done in Edit Mode.");
            return;
        }

        if (!File.Exists(ExportFilePath))
        {
            Debug.LogWarning($"[BusStopMakerImporter] No export file found at:\n{ExportFilePath}\n" +
                              "Nothing to import — did you click EXPORT (or SAVE) in BusStopMaker before exiting Play Mode?");
            return;
        }

        var cm = UnityEngine.Object.FindFirstObjectByType<CityManager>();
        if (cm == null)
        {
            Debug.LogError("[BusStopMakerImporter] No CityManager found in the currently open scene — open the right scene first.");
            return;
        }

        ExportWrapper wrapper;
        try
        {
            wrapper = JsonUtility.FromJson<ExportWrapper>(File.ReadAllText(ExportFilePath));
        }
        catch (Exception e)
        {
            Debug.LogError($"[BusStopMakerImporter] Failed to read/parse export file: {e.Message}");
            return;
        }

        if (wrapper?.stops == null || wrapper.stops.Count == 0)
        {
            Debug.LogWarning("[BusStopMakerImporter] Export file was empty — nothing to import.");
            return;
        }

        Undo.RecordObject(cm, "Import Bus Stops");

        int added = 0, updated = 0;
        foreach (var s in wrapper.stops)
        {
            var existing = cm.stopDefinitions.Find(e => e != null && e.stopCode == s.stopCode);
            if (existing != null)
            {
                existing.stopName       = s.stopName;
                existing.parentRoadCode = s.parentRoadCode;
                existing.tValue         = s.tValue;
                existing.isTerminal     = s.isTerminal;
                existing.hasShelter     = s.hasShelter;
                existing.isLayover      = s.isLayover;
                updated++;
                continue;
            }

            cm.stopDefinitions.Add(new BusStopData
            {
                stopCode       = s.stopCode,
                stopName       = s.stopName,
                parentRoadCode = s.parentRoadCode,
                tValue         = s.tValue,
                isTerminal     = s.isTerminal,
                hasShelter     = s.hasShelter,
                isLayover      = s.isLayover,
                isAccessible   = true
            });
            added++;
        }

        EditorUtility.SetDirty(cm);
        EditorSceneManager.MarkSceneDirty(cm.gameObject.scene);

        Debug.Log($"[BusStopMakerImporter] Imported {added} new / updated {updated} existing stop(s) into " +
                  $"'{cm.gameObject.scene.name}'. Remember to save the scene (Ctrl+S) to keep them.");

        try
        {
            string archivePath = ExportFilePath + ".imported";
            if (File.Exists(archivePath)) File.Delete(archivePath);
            File.Move(ExportFilePath, archivePath);
        }
        catch { /* non-fatal — leftover file just gets overwritten next export */ }
    }
}
#endif
#endif