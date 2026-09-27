using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ═══════════════════════════════════════════════════════════════════════════════
//  TERMINAL BAY
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class TerminalBay
{
    [Header("Ingress — route end ▶ bay slot")]
    public List<Vector3> ingressPath = new();

    [Header("Egress — bay slot ▶ route start")]
    public List<Vector3> egressPath = new();

    [Header("Parking Rotation")]
    [Tooltip("World-space Y rotation the bus snaps to when it finishes pulling into the bay. " +
             "0 = north, 90 = east, etc.  Use the scene gizmo arrow to confirm facing.")]
    public float parkingRotationY = 0f;

    // ── Runtime ───────────────────────────────────────────────────────────────
    [System.NonSerialized] public bool occupied        = false;
    [System.NonSerialized] public int  occupiedByBusID = -1;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  TERMINAL IDLE ZONE
// ═══════════════════════════════════════════════════════════════════════════════
public class TerminalIdleZone : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Identity")]
    [Tooltip("Must match BusStopData.stopCode of the terminal this zone serves.")]
    public string stopCode;

    [Header("Bays")]
    [Tooltip("Each entry is one parking slot.  Add as many as you need.")]
    public List<TerminalBay> bays = new();

    // ── Staggered row generator (Editor only) ─────────────────────────────────
    // [ADD] The common bay layout is a shared aisle entry point, then each bay
    // travels straight down the aisle past however many bays come before it,
    // then diagonals into its own slot -- typing that out by hand per bay is
    // the same repeated arithmetic every time. This fills the Vector3s in FOR
    // you (still typed/edited as plain numbers, same as every other field
    // here -- no Scene View clicking involved), verified against a real
    // 3-bay layout before shipping: entry (-378.5,0,-1040), step (0,0,15),
    // diagonal (3.5,0,10), egress offset (-3.5,0,5) reproduces that layout's
    // ingress/egress paths exactly.
    [Header("Staggered Row Generator  (Editor only — values not used at runtime)")]
    [Tooltip("Shared point every generated bay's ingress path starts from -- where the route's final segment feeds into this terminal's bay aisle.")]
    public Vector3 aisleEntryPoint;
    [Tooltip("Added to the entry point, once per bay index (bay 2 gets it twice, etc.), to travel further down the aisle before diagonalling in. Bay 0 never uses this -- it diagonals straight from the entry point.")]
    public Vector3 aisleStepPerBay = new Vector3(0f, 0f, 15f);
    [Tooltip("Added once, to the aisle point above, to reach this bay's actual parking slot. Also used as the START of the egress path.")]
    public Vector3 diagonalIntoSlot = new Vector3(3.5f, 0f, 10f);
    [Tooltip("Added to the parking slot to get where this bay's egress path rejoins the aisle -- keep this pointed further ALONG the aisle (not back toward the entry), so egressing buses flow forward instead of crossing back through the row.")]
    public Vector3 egressRejoinOffset = new Vector3(-3.5f, 0f, 5f);
    [Tooltip("Parking rotation applied to every generated bay -- still a single manual value, same as the per-bay field below.")]
    public float generateParkingRotationY = -180f;
    [Tooltip("How many bays to generate. Overwrites the whole Bays list above -- use this for the common staggered-row case, then hand-edit/add fully manual bays afterward for any that don't fit the pattern (e.g. ones far from the terminal).")]
    [Range(1, 20)] public int generateBayCount = 3;

    [ContextMenu("Generate Staggered Bay Row")]
    private void GenerateStaggeredBayRow()
    {
        var generated = new List<TerminalBay>(generateBayCount);
        for (int i = 0; i < generateBayCount; i++)
        {
            Vector3 aislePoint = aisleEntryPoint + aisleStepPerBay * i;
            Vector3 slot        = aislePoint + diagonalIntoSlot;

            var bay = new TerminalBay { parkingRotationY = generateParkingRotationY };
            bay.ingressPath = new List<Vector3> { aisleEntryPoint };
            if (i > 0) bay.ingressPath.Add(aislePoint); // bay 0 diagonals straight in, no aisle travel needed
            bay.ingressPath.Add(slot);

            bay.egressPath = new List<Vector3> { slot, slot + egressRejoinOffset };

            generated.Add(bay);
        }
        bays = generated;
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log($"[TerminalIdleZone '{stopCode}'] Generated {generateBayCount} staggered bays from aisle entry {aisleEntryPoint}.");
#endif
    }

    // ── Copy-with-offset helper (Editor only) ─────────────────────────────────
    [Header("Bay Copy Helper  (Editor only — values not used at runtime)")]
    [Tooltip("Index of the bay to duplicate.")]
    public int    copySourceBayIndex = 0;
    [Tooltip("Only the parking spot (last ingress waypoint / first egress waypoint) " +
             "is shifted by this amount.  All lead-in/lead-out waypoints stay identical.")]
    public Vector3 copyParkingOffset = new Vector3(4f, 0f, 0f);

    // ── Item 27(c): unified egress merge point ────────────────────────────────
    [Header("Egress Merge Point")]
    [Tooltip("Optional. When set, GenerateStaggeredRow() routes every generated " +
             "bay's egress path through this single point before continuing to the " +
             "real route-start -- kept upstream of route-start on purpose so every " +
             "egressing bus doesn't funnel straight into the same spot buses are " +
             "also trying to START routes from. Manually-authored bays are never " +
             "forced through this -- it's only consumed by the generator below.")]
    public bool    useEgressMergePoint = false;
    public Vector3 egressMergePoint    = Vector3.zero;

    // ── Item 27(b): staggered-row generator (Editor only) ─────────────────────
    [Header("Staggered Row Generator  (Editor only — values not used at runtime)")]
    [Tooltip("Shared aisle entry point every generated bay's ingress starts from -- " +
             "placed manually, not inferred.")]
    public Vector3 rowStartPoint = Vector3.zero;
    [Tooltip("Direction down the lane, one bay further per rowSpacing. Placed/picked " +
             "manually -- not inferred from anything.")]
    public Vector3 rowDirection = Vector3.forward;
    [Tooltip("Distance along rowDirection between successive bays' lane waypoints.")]
    public float   rowSpacing = 4f;
    [Tooltip("How many bays to generate, appended after whatever bays already exist " +
             "(never overwrites/removes existing bays).")]
    public int     rowBayCount = 4;
    [Tooltip("Offset from a bay's lane position into its actual parking slot -- the " +
             "diagonal leg. Same offset applied to every generated bay in the row.")]
    public Vector3 rowDiagonalOffset = new Vector3(4f, 0f, 0f);
    [Tooltip("Parking rotation applied to every bay this generates.")]
    public float   rowParkingRotationY = 0f;

    // ── Static registry ───────────────────────────────────────────────────────
    private static readonly Dictionary<string, TerminalIdleZone> _registry = new();

    private void OnEnable()
    {
        if (!string.IsNullOrEmpty(stopCode))
        {
            _registry[stopCode] = this;
            Debug.Log($"[TerminalIdleZone] Registered '{stopCode}' with {bays.Count} bay(s).");
        }
    }

    private void OnDisable()
    {
        if (!string.IsNullOrEmpty(stopCode))
            _registry.Remove(stopCode);
    }

    public static TerminalIdleZone GetForStop(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        _registry.TryGetValue(code, out var zone);
        return zone;
    }

    /// <summary>Same as GetForStop but ignores letter case (console input like "s0093").</summary>
    public static TerminalIdleZone GetForStopIgnoreCase(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        foreach (var kv in _registry)
            if (string.Equals(kv.Key, code, System.StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    // ── Bay management ────────────────────────────────────────────────────────
    // ── Physical occupancy ─────────────────────────────────────────────────
    // A bay can be BLOCKED without anyone having claimed it: e.g. a player parked in it (or any bus that
    // stopped there without registering). TryClaim skips such bays so an NPC never drives into a bus.
    private static readonly Collider[] _occBuf = new Collider[32];

    /// <summary>True if a bus other than <paramref name="ignoreBusID"/> is physically sitting in this bay's parking slot.</summary>
    public bool IsBayPhysicallyOccupied(int bayIdx, int ignoreBusID)
    {
        if (bayIdx < 0 || bayIdx >= bays.Count) return false;
        Vector3 c = GetParkingPosition(bayIdx) + Vector3.up * 1.6f;
        Quaternion rot = GetParkingRotation(bayIdx);
        int n = Physics.OverlapBoxNonAlloc(c, new Vector3(1.8f, 1.5f, 7f), _occBuf, rot, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var col = _occBuf[i];
            if (col == null) continue;
            var npc = col.GetComponentInParent<NPCBusController>();
            if (npc != null && npc.busID != ignoreBusID) return true;
            var sim = col.GetComponentInParent<BusSimulationController>();
            if (sim != null && PlayerHandoff.Instance != null && PlayerHandoff.Instance.playerBus == sim
                && ignoreBusID != PlayerHandoff.Instance.PlayerBusID) return true;
        }
        return false;
    }

    /// <summary>Owner bus id of a bay (-1 = free).</summary>
    public int GetBayOwner(int bayIdx) => bayIdx >= 0 && bayIdx < bays.Count && bays[bayIdx].occupied ? bays[bayIdx].occupiedByBusID : -1;

    /// <summary>Claims one SPECIFIC bay (player uses this). Fails if it's already claimed by someone else, or another bus is physically in it.</summary>
    public bool TryClaimSpecific(int busID, int bayIdx)
    {
        if (bayIdx < 0 || bayIdx >= bays.Count) return false;
        var b = bays[bayIdx];
        if (b.occupied && b.occupiedByBusID != busID) return false;
        if (IsBayPhysicallyOccupied(bayIdx, busID)) return false;
        b.occupied = true; b.occupiedByBusID = busID;
        Debug.Log($"[TerminalIdleZone '{stopCode}'] Bus#{busID} claimed bay {bayIdx} (specific).");
        return true;
    }

    public int TryClaim(int busID)
    {
        for (int i = 0; i < bays.Count; i++)
        {
            if (!bays[i].occupied && !IsBayPhysicallyOccupied(i, busID))
            {
                bays[i].occupied        = true;
                bays[i].occupiedByBusID = busID;
                Debug.Log($"[TerminalIdleZone '{stopCode}'] Bus#{busID} claimed bay {i}.");
                return i;
            }
        }
        Debug.Log($"[TerminalIdleZone '{stopCode}'] Bus#{busID} — all bays full.");
        return -1;
    }

    public void Release(int busID)
    {
        for (int i = 0; i < bays.Count; i++)
        {
            if (bays[i].occupiedByBusID != busID) continue;
            bays[i].occupied        = false;
            bays[i].occupiedByBusID = -1;
            Debug.Log($"[TerminalIdleZone '{stopCode}'] Bus#{busID} released bay {i}.");
            return;
        }
    }

    // ── Parking rotation ──────────────────────────────────────────────────────
    /// <summary>Returns the world-space parking rotation for the given bay.</summary>
    public Quaternion GetParkingRotation(int bayIdx)
    {
        if (bayIdx < 0 || bayIdx >= bays.Count) return Quaternion.identity;
        return Quaternion.Euler(0f, bays[bayIdx].parkingRotationY, 0f);
    }

    /// <summary>Returns the world-space parking spot for the given bay — the
    /// last ingress waypoint (== first egress waypoint). Used by anything that
    /// needs to PLACE a bus directly into a bay without driving the ingress
    /// path first (e.g. NPCBusController.SpawnParkedAtTerminal, which spawns a
    /// bus already-parked for a near-future departure instead of animating it
    /// in). Falls back to this transform's position if the bay has no path
    /// data yet, so callers always get something sane instead of a zero vector.</summary>
    public Vector3 GetParkingPosition(int bayIdx)
    {
        if (bayIdx < 0 || bayIdx >= bays.Count) return transform.position;
        var bay = bays[bayIdx];
        if (bay.ingressPath != null && bay.ingressPath.Count > 0)
            return bay.ingressPath[bay.ingressPath.Count - 1];
        if (bay.egressPath != null && bay.egressPath.Count > 0)
            return bay.egressPath[0];
        return transform.position;
    }

    // ── Path accessors ────────────────────────────────────────────────────────
    public List<Vector3> GetIngressPath(int bayIdx, float busY)
    {
        if (bayIdx < 0 || bayIdx >= bays.Count) return null;
        return FlattenY(bays[bayIdx].ingressPath, busY);
    }

    public List<Vector3> GetEgressPath(int bayIdx, float busY)
    {
        if (bayIdx < 0 || bayIdx >= bays.Count) return null;
        return FlattenY(bays[bayIdx].egressPath, busY);
    }

    private static List<Vector3> FlattenY(List<Vector3> src, float y)
    {
        if (src == null || src.Count == 0) return null;
        var result = new List<Vector3>(src.Count);
        foreach (var p in src) result.Add(new Vector3(p.x, y, p.z));
        return result;
    }

    // ── Copy bay with offset ──────────────────────────────────────────────────
    /// <summary>
    /// Duplicates bay at srcIdx and shifts ONLY the parking spot
    /// (last ingress waypoint + first egress waypoint) by posOffset.
    /// All lead-in and lead-out waypoints are shared/copied unchanged.
    /// Call from the Inspector context menu, or call at runtime to
    /// procedurally generate stacked bays.
    /// </summary>
    [ContextMenu("Copy Bay With Parking Offset")]
    public void CopyBayWithOffset()
    {
        CopyBayWithOffset(copySourceBayIndex, copyParkingOffset);
    }

    public void CopyBayWithOffset(int srcIdx, Vector3 posOffset)
    {
        if (srcIdx < 0 || srcIdx >= bays.Count)
        {
            Debug.LogWarning($"[TerminalIdleZone] CopyBayWithOffset: srcIdx {srcIdx} out of range.");
            return;
        }

        var src    = bays[srcIdx];
        var newBay = new TerminalBay
        {
            parkingRotationY = src.parkingRotationY,
            ingressPath      = new List<Vector3>(),
            egressPath       = new List<Vector3>(),
        };

        // Copy ingress — shift only the LAST waypoint (= parking spot)
        for (int i = 0; i < src.ingressPath.Count; i++)
        {
            bool isParking = (i == src.ingressPath.Count - 1);
            newBay.ingressPath.Add(isParking
                ? src.ingressPath[i] + posOffset
                : src.ingressPath[i]);
        }

        // Copy egress — shift only the FIRST waypoint (= same parking spot, departing)
        for (int i = 0; i < src.egressPath.Count; i++)
        {
            bool isParking = (i == 0);
            newBay.egressPath.Add(isParking
                ? src.egressPath[i] + posOffset
                : src.egressPath[i]);
        }

        bays.Add(newBay);
        Debug.Log($"[TerminalIdleZone '{stopCode}'] Bay {srcIdx} copied → Bay {bays.Count - 1} " +
                  $"with parking offset {posOffset}.");
    }

    // ── Staggered row generator ───────────────────────────────────────────────
    /// <summary>Generates rowBayCount new bays (appended after any existing
    /// ones) following the common pattern: bay 0's ingress is just the
    /// diagonal off rowStartPoint; each bay after that reuses the PREVIOUS
    /// bay's full lane waypoint list and inserts exactly one more lane
    /// waypoint before its own diagonal -- same "copy the whole path, shift
    /// only the parking end" idea CopyBayWithOffset already uses, just
    /// driven by the row parameters instead of a single manual offset.
    /// Egress mirrors the ingress lane waypoints in reverse and, if
    /// useEgressMergePoint is on, ends at egressMergePoint instead of
    /// rowStartPoint -- see that field's own comment for why.</summary>
    [ContextMenu("Generate Staggered Row")]
    public void GenerateStaggeredRow()
    {
        if (rowBayCount <= 0) { Debug.LogWarning("[TerminalIdleZone] GenerateStaggeredRow: rowBayCount must be > 0."); return; }
        Vector3 dir = rowDirection.sqrMagnitude > 0.0001f ? rowDirection.normalized : Vector3.forward;

        // Lane waypoints accumulate one at a time, bay over bay -- lanePts[i]
        // (0-based) is the Nth lane waypoint shared by every bay from that
        // point on, matching the "one extra waypoint per bay further down
        // the row" pattern.
        var lanePts = new List<Vector3>();

        for (int i = 0; i < rowBayCount; i++)
        {
            var newBay = new TerminalBay { parkingRotationY = rowParkingRotationY };

            newBay.ingressPath.Add(rowStartPoint);
            if (i > 0)
            {
                lanePts.Add(rowStartPoint + dir * (rowSpacing * i));
                newBay.ingressPath.AddRange(lanePts);
            }
            Vector3 parkSpot = (i > 0 ? lanePts[lanePts.Count - 1] : rowStartPoint) + rowDiagonalOffset;
            newBay.ingressPath.Add(parkSpot);

            // Egress: parking spot back out through the same lane waypoints
            // in reverse, ending at the merge point (if set) instead of
            // looping back to rowStartPoint.
            newBay.egressPath.Add(parkSpot);
            for (int p = lanePts.Count - 1; p >= 0; p--)
                newBay.egressPath.Add(lanePts[p]);
            newBay.egressPath.Add(useEgressMergePoint ? egressMergePoint : rowStartPoint);

            bays.Add(newBay);
        }

        Debug.Log($"[TerminalIdleZone '{stopCode}'] Generated {rowBayCount} staggered bay(s) — now {bays.Count} total.");
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────
    private void OnDrawGizmos()
    {
        for (int i = 0; i < bays.Count; i++)
        {
            var bay = bays[i];

            DrawPathGizmo(bay.ingressPath, new Color(0f, 0.9f, 1f, 0.75f));
            DrawPathGizmo(bay.egressPath,  new Color(1f, 0.9f, 0f, 0.75f));

            if (bay.ingressPath != null && bay.ingressPath.Count > 0)
            {
                Vector3 bayPos = bay.ingressPath[bay.ingressPath.Count - 1];
                Gizmos.color = bay.occupied ? Color.red : Color.green;
                Gizmos.DrawWireSphere(bayPos, 1.4f);

                // Parking rotation arrow — shows which way the bus will face
                Vector3 facingDir = Quaternion.Euler(0f, bay.parkingRotationY, 0f) * Vector3.forward;
                Gizmos.color = new Color(1f, 0.4f, 0f, 0.9f);
                Gizmos.DrawRay(bayPos + Vector3.up * 0.2f, facingDir * 3.5f);

#if UNITY_EDITOR
                string label = bay.occupied
                    ? $"Bay {i}  [BUS #{bay.occupiedByBusID}]"
                    : $"Bay {i}  FREE  | rot {bay.parkingRotationY:0}°";
                UnityEditor.Handles.Label(bayPos + Vector3.up * 2.4f, label);
#endif
            }

            // Connector: last ingress → first egress
            if (bay.ingressPath != null && bay.ingressPath.Count > 0 &&
                bay.egressPath  != null && bay.egressPath.Count  > 0)
            {
                Gizmos.color = new Color(1f, 1f, 1f, 0.2f);
                Gizmos.DrawLine(
                    bay.ingressPath[bay.ingressPath.Count - 1],
                    bay.egressPath[0]);
            }
        }

        if (useEgressMergePoint)
        {
            Gizmos.color = new Color(1f, 0.2f, 0.8f, 0.85f);
            Gizmos.DrawWireCube(egressMergePoint, Vector3.one * 1.8f);
            Gizmos.DrawWireSphere(egressMergePoint, 1f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(egressMergePoint + Vector3.up * 2f, "Egress Merge Point");
#endif
        }

#if UNITY_EDITOR
        UnityEditor.Handles.Label(
            transform.position + Vector3.up * 3.5f,
            $"[IdleZone] {stopCode}  ({bays.Count} bay{(bays.Count != 1 ? "s" : "")})");
#endif
    }

    private static void DrawPathGizmo(List<Vector3> path, Color col)
    {
        if (path == null || path.Count == 0) return;
        Gizmos.color = col;
        for (int i = 0; i < path.Count - 1; i++)
            Gizmos.DrawLine(path[i], path[i + 1]);
        foreach (var p in path)
            Gizmos.DrawWireSphere(p, 0.35f);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  TERMINAL IDLE ZONE EDITOR  (item 27)
//
//  Scene-view waypoint authoring: (a) every existing waypoint on every bay
//  gets a drag handle, always -- no selection step needed to move a point
//  that's already there. (b) the bay/path selector below only controls
//  where a Scene-view CLICK appends a NEW waypoint, since a click needs to
//  know which list to append to. (c) the row generator + egress merge point
//  both get their own draggable handles too, so their positions can be set
//  visually instead of typing numbers into the Inspector.
//
//  FleetRosterImporterEditor (DEPOTS/FleetRosterImporter.cs) is the only
//  other [CustomEditor] in the project -- Inspector-button-only, no
//  OnSceneGUI. This is the first Scene-view interactive editor in the repo.
// ═══════════════════════════════════════════════════════════════════════════════
#if UNITY_EDITOR
[CustomEditor(typeof(TerminalIdleZone))]
public class TerminalIdleZoneEditor : Editor
{
    private int  _editBayIndex   = 0;
    private bool _editIngress    = true;
    private bool _addWaypointMode = false;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var zone = (TerminalIdleZone)target;

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Scene-View Waypoint Editing (item 27a)", EditorStyles.boldLabel);

        if (zone.bays.Count == 0)
        {
            EditorGUILayout.HelpBox("No bays yet — add one manually above, or use Generate Staggered Row below.", MessageType.Info);
        }
        else
        {
            _editBayIndex = EditorGUILayout.IntSlider("Bay to append to", _editBayIndex, 0, zone.bays.Count - 1);
            _editIngress  = GUILayout.Toolbar(_editIngress ? 0 : 1, new[] { "Ingress", "Egress" }) == 0;

            EditorGUILayout.BeginHorizontal();
            _addWaypointMode = GUILayout.Toggle(_addWaypointMode, "Click Scene View to Append Waypoint", "Button");
            if (GUILayout.Button("Remove Last Waypoint", GUILayout.Width(150)))
            {
                var path = _editIngress ? zone.bays[_editBayIndex].ingressPath : zone.bays[_editBayIndex].egressPath;
                if (path.Count > 0)
                {
                    Undo.RecordObject(zone, "Remove Waypoint");
                    path.RemoveAt(path.Count - 1);
                    EditorUtility.SetDirty(zone);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_addWaypointMode)
                EditorGUILayout.HelpBox($"Clicking in the Scene view appends a waypoint (on this zone's own ground plane) to Bay {_editBayIndex}'s {(_editIngress ? "ingress" : "egress")} path. Every bay's existing waypoints stay drag-editable via their handles regardless of this toggle.", MessageType.None);
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Staggered Row Generator (item 27b)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Uses rowStartPoint/rowDirection/rowSpacing/rowBayCount/rowDiagonalOffset above (draggable in Scene view too). Appends new bays -- never removes or overwrites existing ones, so far-away hand-authored bays stay untouched.", MessageType.None);
        if (GUILayout.Button($"Generate {zone.rowBayCount} Staggered Bay(s)", GUILayout.Height(28)))
        {
            Undo.RecordObject(zone, "Generate Staggered Row");
            zone.GenerateStaggeredRow();
            EditorUtility.SetDirty(zone);
        }

        SceneView.RepaintAll();
    }

    private void OnSceneGUI()
    {
        var zone = (TerminalIdleZone)target;

        for (int b = 0; b < zone.bays.Count; b++)
        {
            EditWaypointList(zone, zone.bays[b].ingressPath, new Color(0f, 0.9f, 1f, 1f));
            EditWaypointList(zone, zone.bays[b].egressPath,  new Color(1f, 0.9f, 0f, 1f));
        }

        if (zone.useEgressMergePoint)
        {
            Handles.color = new Color(1f, 0.2f, 0.8f, 1f);
            EditorGUI.BeginChangeCheck();
            Vector3 newMerge = Handles.PositionHandle(zone.egressMergePoint, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(zone, "Move Egress Merge Point");
                zone.egressMergePoint = newMerge;
                EditorUtility.SetDirty(zone);
            }
        }

        Handles.color = new Color(0.6f, 1f, 0.3f, 1f);
        Handles.Label(zone.rowStartPoint + Vector3.up * 1.5f, "Row Start");
        EditorGUI.BeginChangeCheck();
        Vector3 newStart = Handles.PositionHandle(zone.rowStartPoint, Quaternion.identity);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(zone, "Move Row Start Point");
            zone.rowStartPoint = newStart;
            EditorUtility.SetDirty(zone);
        }

        // Click-to-append: only while the toggle is on and there's a bay to
        // append to. Raycasts against a horizontal plane through this zone's
        // own transform (matches how every other waypoint here is authored
        // -- flat, ground-relative), not against scene colliders, so it
        // works identically whether or not the terminal's road geometry has
        // colliders on it.
        if (_addWaypointMode && zone.bays.Count > 0
            && Event.current.type == EventType.MouseDown && Event.current.button == 0 && !Event.current.alt)
        {
            Plane ground = new Plane(Vector3.up, zone.transform.position);
            Ray   ray    = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            if (ground.Raycast(ray, out float dist))
            {
                Vector3 hit  = ray.GetPoint(dist);
                var     path = _editIngress ? zone.bays[_editBayIndex].ingressPath : zone.bays[_editBayIndex].egressPath;
                Undo.RecordObject(zone, "Add Waypoint");
                path.Add(hit);
                EditorUtility.SetDirty(zone);
                Event.current.Use();
            }
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
        }
    }

    private static void EditWaypointList(TerminalIdleZone zone, List<Vector3> path, Color col)
    {
        if (path == null) return;
        Handles.color = col;
        for (int i = 0; i < path.Count; i++)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 newPos = Handles.PositionHandle(path[i], Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(zone, "Move Waypoint");
                path[i] = newPos;
                EditorUtility.SetDirty(zone);
            }
        }
    }
}
#endif