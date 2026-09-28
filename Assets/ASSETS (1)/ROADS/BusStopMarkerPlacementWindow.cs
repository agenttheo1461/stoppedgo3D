using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS STOP MARKER PLACEMENT  (Tools > City Building)
//
//  Auto-places the visual busstop.prefab curb marker for every stop on one or
//  two chosen routes that doesn't already have one nearby -- 44 stops (Route
//  1 + Route 201) were placed by hand; the other ~550 across every other
//  route never got one.
//
//  Position comes straight from the same math BusStopData.GetWorldPosition()
//  already uses (CityManager.cs) -- centerline position + Cross(up, tangent)
//  * lateral offset. That formula scales the offset with each road's own
//  roadWidth for free, so one-way vs two-way and narrow vs wide roads never
//  need special-casing. What's DIFFERENT from GetWorldPosition() itself, per
//  direct instruction: the lateral offset is a tunable margin computed FROM
//  roadWidth (not the fixed +0.5m pedestrian-line constant that method
//  uses), and stops that land within clusterDistance of each other on the
//  same road get spread apart along the tangent instead of stacking on the
//  same point -- some real stop locations serve 2 close-together
//  stopCodes, some serve 1, and this keeps either case looking right
//  without needing to know which case it is ahead of time.
//
//  Every stop is DOUBLE-SIDED by default (one marker per curb), matching how
//  the existing 44 were placed: reverse-engineering their recorded position/
//  rotation pairs showed the "near" curb (centerline + Cross(up,tangent)*
//  offset -- the same point GetWorldPosition() itself resolves to) always
//  faces -tangent, and the "far" curb (centerline - that offset) always
//  faces +tangent -- i.e. the far marker is just the near one's rotation
//  flipped 180. Uncheck Double-Sided to place only the near curb.
//
//  Works in Edit Mode without BuildCity()/Play mode having ever run, same
//  as CityManagerEditor's own stop/intersection gizmos -- builds a
//  throwaway RoadSegment per road code from RoadSegmentDefinition.
//  BuildControlPoints()+roadWidth rather than relying on CityManager's
//  runtime _roadByCode (only populated by BuildCity()).
// ═══════════════════════════════════════════════════════════════════════════════
public class BusStopMarkerPlacementWindow : EditorWindow
{
    [MenuItem("Tools/City Building/Bus Stop Marker Placement")]
    public static void ShowWindow()
    {
        var win = GetWindow<BusStopMarkerPlacementWindow>("Bus Stop Markers");
        win.Show();
    }

    private CityManager _city;
    private GameObject  _markerPrefab;
    private Transform   _parentContainer;
    private readonly List<BusRouteData> _targetRoutes = new();

    private float _extraOffsetBeyondHalfWidth = 2.0f; // added to roadWidth/2 -- NOT reusing GetWorldPosition()'s fixed +0.5m, per instruction
    private float _clusterDistanceMeters       = 12f;  // stops within this world distance of each other (same road) get staggered instead of stacked
    private float _staggerSpacingMeters        = 6f;   // spacing applied along the road tangent within a cluster
    private float _skipIfExistingWithinMeters  = 4f;   // don't duplicate a marker that's already sitting basically on top of the target spot
    private bool  _doubleSided                 = true; // every stop gets a marker on BOTH curbs by default (matches the existing 44); uncheck for one side only

    private Vector2 _scroll;
    private List<(BusStopData stop, Vector3 pos, Quaternion rot, bool alreadyMarked)> _preview = new();

    private void OnEnable()
    {
        if (_city == null) _city = FindFirstObjectByType<CityManager>();
        if (_markerPrefab == null)
        {
            var guids = AssetDatabase.FindAssets("busstop t:prefab");
            if (guids.Length > 0)
                _markerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }
    }

    private void OnGUI()
    {
        _city = (CityManager)EditorGUILayout.ObjectField("City Manager", _city, typeof(CityManager), true);
        _markerPrefab = (GameObject)EditorGUILayout.ObjectField("Marker Prefab (busstop)", _markerPrefab, typeof(GameObject), false);
        _parentContainer = (Transform)EditorGUILayout.ObjectField("Parent Container", _parentContainer, typeof(Transform), true);
        EditorGUILayout.HelpBox("Drag in the same parent the existing 44 hand-placed markers sit under, so new ones land in the same hierarchy spot.", MessageType.Info);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Target Routes (one or two at a time)", EditorStyles.boldLabel);
        for (int i = 0; i < _targetRoutes.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            _targetRoutes[i] = (BusRouteData)EditorGUILayout.ObjectField(_targetRoutes[i], typeof(BusRouteData), false);
            if (GUILayout.Button("✕", GUILayout.Width(24))) { _targetRoutes.RemoveAt(i); i--; }
            EditorGUILayout.EndHorizontal();
        }
        if (GUILayout.Button("Add Route")) _targetRoutes.Add(null);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Placement Tuning", EditorStyles.boldLabel);
        _extraOffsetBeyondHalfWidth = EditorGUILayout.FloatField(new GUIContent("Extra Offset Past Half-Width (m)", "Lateral distance PAST roadWidth/2 -- total offset from centerline is roadWidth/2 + this."), _extraOffsetBeyondHalfWidth);
        _clusterDistanceMeters      = EditorGUILayout.FloatField(new GUIContent("Cluster Distance (m)", "Stops on the same road within this many meters of each other get staggered instead of stacked."), _clusterDistanceMeters);
        _staggerSpacingMeters       = EditorGUILayout.FloatField(new GUIContent("Stagger Spacing (m)", "How far apart clustered stops get spread along the road."), _staggerSpacingMeters);
        _skipIfExistingWithinMeters = EditorGUILayout.FloatField(new GUIContent("Skip If Marker Within (m)", "Don't place a new marker if one already sits this close (avoids duplicating Route 1/201's existing 44)."), _skipIfExistingWithinMeters);
        _doubleSided = EditorGUILayout.Toggle(new GUIContent("Double-Sided", "Place a marker on both curbs (one per direction of travel), like the existing 44. Uncheck to place only the resolved single side."), _doubleSided);

        EditorGUILayout.Space(10);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Preview", GUILayout.Height(28))) BuildPreview();
        GUI.enabled = _preview.Count > 0;
        if (GUILayout.Button("Place Markers", GUILayout.Height(28))) PlaceMarkers();
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        if (_preview.Count > 0)
        {
            int toPlace = _preview.Count(p => !p.alreadyMarked);
            int skipped = _preview.Count - toPlace;
            EditorGUILayout.HelpBox($"{_preview.Count} marker(s){(_doubleSided ? " (double-sided)" : "")} for the selected route(s). {toPlace} will get a new marker, {skipped} already have one nearby and will be skipped.", MessageType.None);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(240));
            foreach (var p in _preview)
            {
                string tag = p.alreadyMarked ? "[skip -- already marked]" : "[will place]";
                EditorGUILayout.LabelField($"{p.stop.stopCode}  {p.stop.stopName}  {tag}");
            }
            EditorGUILayout.EndScrollView();
        }

        SceneView.RepaintAll();
    }

    // ── Road resolution (Edit Mode safe -- mirrors CityManagerEditor's own TempRoad gizmo helper) ──
    private Dictionary<string, RoadSegment> _tempRoads;
    private Dictionary<string, RoadSegmentDefinition> _tempRoadDefs;

    private void BuildTempRoadLookup()
    {
        _tempRoads = new Dictionary<string, RoadSegment>();
        _tempRoadDefs = new Dictionary<string, RoadSegmentDefinition>();
        if (_city?.roadDefinitions == null) return;

        foreach (var d in _city.roadDefinitions)
        {
            if (d == null || string.IsNullOrEmpty(d.roadCode)) continue;
            var pts = d.BuildControlPoints();
            if (pts != null && pts.Length >= 2)
            {
                _tempRoads[d.roadCode] = new RoadSegment { controlPoints = pts, roadWidth = d.roadWidth };
                _tempRoadDefs[d.roadCode] = d;
            }
        }
    }

    private void BuildPreview()
    {
        _preview.Clear();
        if (_city == null) { Debug.LogWarning("[BusStopMarkerPlacement] No CityManager assigned."); return; }

        BuildTempRoadLookup();

        // Collect target stop codes from the selected route(s), both directions,
        // de-duplicated (a stop shared by both directions of the same route --
        // or by two selected routes -- only needs one marker).
        var stopCodesByFirstSeenOrder = new List<string>();
        var seenCodes = new HashSet<string>();
        foreach (var route in _targetRoutes)
        {
            if (route == null) continue;
            foreach (var binding in route.outboundStops)
                if (!string.IsNullOrEmpty(binding.stopCode) && seenCodes.Add(binding.stopCode))
                    stopCodesByFirstSeenOrder.Add(binding.stopCode);
            foreach (var binding in route.inboundStops)
                if (!string.IsNullOrEmpty(binding.stopCode) && seenCodes.Add(binding.stopCode))
                    stopCodesByFirstSeenOrder.Add(binding.stopCode);
        }

        if (stopCodesByFirstSeenOrder.Count == 0)
        {
            Debug.LogWarning("[BusStopMarkerPlacement] No stops found -- add at least one route with stop bindings.");
            return;
        }

        var stopByCode = _city.stopDefinitions.Where(s => s != null).ToDictionary(s => s.stopCode, s => s);

        // Resolve base (unstaggered) position/tangent/rotation for each target stop.
        // pos is the "primary" curb -- same side CityManager's own GetWorldPosition()
        // resolves to (centerline + Cross(up, tangent) * offset) -- so the gameplay
        // stop point and this marker always agree on which curb.
        var resolved = new List<(BusStopData stop, string roadCode, float t, Vector3 pos, Vector3 tangent, float offset)>();
        foreach (var code in stopCodesByFirstSeenOrder)
        {
            if (!stopByCode.TryGetValue(code, out var stop)) { Debug.LogWarning($"[BusStopMarkerPlacement] stopCode '{code}' not found in CityManager.stopDefinitions."); continue; }
            if (!_tempRoads.TryGetValue(stop.parentRoadCode, out var seg) || !_tempRoadDefs.TryGetValue(stop.parentRoadCode, out var def))
            { Debug.LogWarning($"[BusStopMarkerPlacement] Road '{stop.parentRoadCode}' (stop {code}) not found in CityManager.roadDefinitions."); continue; }

            Vector3 pos     = seg.EvaluateSurfacePosition(stop.tValue);
            Vector3 tangent = seg.EvaluateTangent(stop.tValue).normalized;
            Vector3 right   = Vector3.Cross(Vector3.up, tangent).normalized;
            float   offset  = (def.roadWidth * 0.5f) + _extraOffsetBeyondHalfWidth;

            resolved.Add((stop, stop.parentRoadCode, stop.tValue, pos + right * offset, tangent, offset));
        }

        // Cluster stops on the same road within clusterDistanceMeters of each
        // other and stagger them along the tangent so they don't stack --
        // some physical stop locations carry 2 close stopCodes, some just 1.
        var used = new bool[resolved.Count];
        for (int i = 0; i < resolved.Count; i++)
        {
            if (used[i]) continue;
            var cluster = new List<int> { i };
            used[i] = true;
            for (int j = i + 1; j < resolved.Count; j++)
            {
                if (used[j]) continue;
                if (resolved[j].roadCode != resolved[i].roadCode) continue;
                if (Vector3.Distance(resolved[j].pos, resolved[i].pos) <= _clusterDistanceMeters)
                {
                    cluster.Add(j);
                    used[j] = true;
                }
            }

            if (cluster.Count == 1)
            {
                var (stop, _, _, pos, tangent, offset) = resolved[cluster[0]];
                AddPreviewEntry(stop, pos, tangent, offset);
            }
            else
            {
                // Spread the cluster evenly along the shared tangent, centered
                // on the cluster's average point, spacing meters apart.
                Vector3 avgPos = Vector3.zero;
                foreach (var idx in cluster) avgPos += resolved[idx].pos;
                avgPos /= cluster.Count;
                Vector3 tangent = resolved[cluster[0]].tangent;
                float   offset  = resolved[cluster[0]].offset;

                float totalSpan = _staggerSpacingMeters * (cluster.Count - 1);
                for (int k = 0; k < cluster.Count; k++)
                {
                    float along = -totalSpan * 0.5f + k * _staggerSpacingMeters;
                    Vector3 pos = avgPos + tangent * along;
                    AddPreviewEntry(resolved[cluster[k]].stop, pos, tangent, offset);
                }
            }
        }
    }

    // pos is the primary curb (centerline + Cross(up, tangent) * offset, same
    // side CityManager.GetWorldPosition() resolves to). Its correct facing is
    // -tangent, not tangent -- the existing 44 markers' own position/rotation
    // pairing confirms this (the +side always carries the 180-flipped
    // rotation). When double-sided, the mirrored curb (centerline - offset)
    // gets the "un-flipped" tangent rotation, i.e. the primary's rotation
    // rotated 180 -- one marker always faces the other's back.
    private void AddPreviewEntry(BusStopData stop, Vector3 pos, Vector3 tangent, float offset)
    {
        Quaternion rot = Quaternion.LookRotation(-tangent, Vector3.up);
        AddSingleMarker(stop, pos, rot);

        if (_doubleSided)
        {
            Vector3    right     = Vector3.Cross(Vector3.up, tangent).normalized;
            Vector3    mirrorPos = pos - right * (offset * 2f);
            Quaternion mirrorRot = Quaternion.LookRotation(tangent, Vector3.up);
            AddSingleMarker(stop, mirrorPos, mirrorRot);
        }
    }

    private void AddSingleMarker(BusStopData stop, Vector3 pos, Quaternion rot)
    {
        pos.y = 0f; // EvaluateSurfacePosition can return a road's snapped/elevated height -- the
                    // marker prefab's own child meshes already carry their height offsets from a
                    // y=0 root, same as the existing 44, so the root itself always sits at 0.
        bool alreadyMarked = _parentContainer != null && HasNearbyMarker(pos);
        _preview.Add((stop, pos, rot, alreadyMarked));
    }

    private bool HasNearbyMarker(Vector3 pos)
    {
        foreach (Transform child in _parentContainer)
            if (Vector3.Distance(child.position, pos) <= _skipIfExistingWithinMeters)
                return true;
        return false;
    }

    private void PlaceMarkers()
    {
        if (_markerPrefab == null) { Debug.LogWarning("[BusStopMarkerPlacement] No marker prefab assigned."); return; }

        int placed = 0;
        foreach (var p in _preview)
        {
            if (p.alreadyMarked) continue;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(_markerPrefab, _parentContainer);
            Undo.RegisterCreatedObjectUndo(instance, "Place Bus Stop Marker");
            instance.transform.position = p.pos;
            instance.transform.rotation = p.rot;
            instance.name = $"busstop ({p.stop.stopCode})";
            instance.tag  = "BusStop";
            placed++;
        }

        Debug.Log($"[BusStopMarkerPlacement] Placed {placed} new marker(s), skipped {_preview.Count - placed} already-covered stop(s).");
        BuildPreview(); // refresh so placed ones now show as already-marked
    }

    private void OnDisable()
    {
        // nothing persistent to unhook -- no SceneView gizmo hook (kept
        // deliberately simple; the list view above is enough to sanity-check
        // before committing, and Undo covers the rest if something's off).
    }
}
#endif
