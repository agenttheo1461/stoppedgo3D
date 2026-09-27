using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE SNAPSHOT — a full, self-contained JSON copy of every route's data
//  (stops, nodes, timing, variants, short turns) at a point in time. This is
//  the "pt1" baseline you compare a later snapshot against for service
//  updates -- once saved, it has zero dependency on the live BusRouteData
//  assets, so it keeps representing "what the network looked like on this
//  date" even after you go change the actual routes.
//
//  Everything here uses plain serializable classes/lists (no Dictionary, no
//  interfaces) because JsonUtility -- Unity's built-in, no-package-required
//  JSON serializer -- can't handle those. If NodeSpline/BezierSegment ever
//  need capturing, they'd need converting to a flat sampled polyline first
//  (see RouteNodeSnapshot below, which already stores raw nodes rather than
//  IRouteSegment objects for exactly this reason).
// ═══════════════════════════════════════════════════════════════════════════════

[System.Serializable]
public class RouteNodeSnapshot
{
    public float x, y, z;
    public bool isCurve;

    public static RouteNodeSnapshot From(RouteNode n) => new RouteNodeSnapshot
    {
        x = n.position.x, y = n.position.y, z = n.position.z,
        isCurve = n.isCurve
    };

    public Vector3 ToVector3() => new Vector3(x, y, z);
}

[System.Serializable]
public class RouteStopSnapshot
{
    public string stopCode;
    public float  minutesFromStart;
    public bool   isTimepoint;

    // Resolved at capture time (if CityManager was available) so the
    // snapshot is readable on its own without re-resolving codes later.
    public string stopName;
    public float  worldX, worldY, worldZ;

    public static RouteStopSnapshot From(RouteStopBinding b, BusStopData resolved)
    {
        var s = new RouteStopSnapshot
        {
            stopCode         = b.stopCode,
            minutesFromStart = b.minutesFromStart,
            isTimepoint      = b.isTimepoint,
        };
        if (resolved != null)
        {
            s.stopName = resolved.stopName;
            // BusStopData doesn't carry a baked world position field in
            // every version of this project -- if yours does (e.g. a
            // resolved Vector3 on BusStopData), wire it in here. Left at
            // zero otherwise; the map viewer falls back to node-based
            // fitting when stop world positions aren't available.
        }
        return s;
    }
}

[System.Serializable]
public class ScheduleWindowSnapshot
{
    public string label;
    public float  windowStartMinutes, windowEndMinutes;
    public float  headwayFromAMinutes, headwayFromZMinutes;

    public static ScheduleWindowSnapshot From(ScheduleWindow w) => new ScheduleWindowSnapshot
    {
        label = w.label,
        windowStartMinutes = w.windowStartMinutes, windowEndMinutes = w.windowEndMinutes,
        headwayFromAMinutes = w.headwayFromAMinutes, headwayFromZMinutes = w.headwayFromZMinutes
    };
}

[System.Serializable]
public class RouteVariantSnapshot
{
    public string variantLetter;
    public string destinationNameOutboundOverride;
    public string destinationNameInboundOverride;
    public string terminalACodeOverride;
    public string terminalZCodeOverride;

    // [FIX] These didn't exist on this class at all -- RouteVariantData's
    // overrideRoute flag and its stop/node override lists were silently
    // dropped on every export. A variant that diverges from the mainline
    // path/stop sequence (not just destination text / terminal) came back
    // out of a snapshot indistinguishable from one that doesn't.
    public bool overrideRoute;
    // Short turn = a variant with isShortTurn (letter '~').
    public bool   isShortTurn;
    public string turnbackStopCode;
    public string turnbackInboundStopCode;
    public List<RouteStopSnapshot> outboundStopsOverride = new();
    public List<RouteStopSnapshot> inboundStopsOverride  = new();
    public List<RouteNodeSnapshot> outboundNodesOverride = new();
    public List<RouteNodeSnapshot> inboundNodesOverride  = new();

    // [FIX] A variant can carry its own schedule entirely independent of
    // the mainline route (RouteVariantData.overrideSchedule + everything
    // gated behind it). None of this was captured before, so a variant
    // running a different headway/operating window/trip time than its
    // parent route snapshotted as if it just inherited the parent's.
    public bool  overrideSchedule;
    public float operatingStartMinutes;
    public float operatingEndMinutes;
    public float headwayFromAMinutes;
    public float headwayFromZMinutes;
    public float oneWayTripMinutes;
    public List<ScheduleWindowSnapshot> scheduleWindows = new();

    // [FIX] Vehicle-restriction overrides (RouteVariantData.overrideVehicleRestrictions
    // + articulatedPolicyOverride/allowedFleetSeriesOverride) were also
    // dropped -- a variant restricted to specific fleet series or an
    // articulated-bus policy different from the mainline came back
    // indistinguishable from an unrestricted one.
    public bool      overrideVehicleRestrictions;
    public string    articulatedPolicyOverride; // ArticulatedRequirement, stored as string -- see note on RouteDataSnapshot.articulatedPolicy
    public List<int> allowedFleetSeriesOverride = new();
}

[System.Serializable]
public class RouteDataSnapshot
{
    // Stable identity, independent of routeNumber/routeName — this is what
    // survives a rename (e.g. 36 -> 136) or a "discontinued and replaced"
    // swap. Pulled directly from BusRouteData.routeId (a real, Inspector-
    // settable field you control) — NOT auto-generated from the asset's
    // Unity GUID, since that produces an opaque hash with no relation to
    // the human-readable IDs already in use (36, 225, etc.). Set it once
    // per route and never change it, even across a rename. Routes/snapshots
    // that predate this field (or where it's left blank) fall back to
    // routeNumber matching in RouteSnapshotViewerUI, same as before.
    public string routeId;

    public string routeNumber;
    public string routeName;

    public string terminalACode;
    public string terminalZCode;
    public string destinationNameOutbound;
    public string destinationNameInbound;
    public string routeQualifierOutbound;
    public string routeQualifierInbound;

    public float headwayFromAMinutes;
    public float headwayFromZMinutes;
    public float oneWayTripMinutes;
    public float operatingStartMinutes;
    public float operatingEndMinutes;

    public List<ScheduleWindowSnapshot> scheduleWindows = new();
    public List<RouteVariantSnapshot>   variants        = new();

    public List<RouteStopSnapshot> outboundStops = new();
    public List<RouteStopSnapshot> inboundStops  = new();

    public List<RouteNodeSnapshot> outboundNodes = new();
    public List<RouteNodeSnapshot> inboundNodes  = new();

    // [FIX] deadRunNodes wasn't captured at all -- a route's pull-in/pull-out
    // path to/from the depot (distinct from its revenue-service outbound/
    // inbound nodes) was completely invisible to every past and future
    // snapshot comparison.
    public List<RouteNodeSnapshot> deadRunNodes = new();

    // [FIX] Fleet/vehicle-restriction and capacity fields were entirely
    // absent -- a route's max-buses cap, its articulated/mini-bus policy,
    // which fleet series it allows, its night-fleet-only series, and its
    // old-bus depot range could all change with zero record of it in a
    // snapshot diff.
    public int maxBusesAllowed;

    // Stored as strings rather than the ArticulatedRequirement enum so this
    // class has no compile-time dependency on that enum's definition --
    // matches this file's existing "flat, serializer-agnostic" approach
    // (see the file-level comment) and means a future rename/reorder of
    // the enum's values can't silently corrupt old snapshots the way an
    // int-backed enum field would.
    public string articulatedPolicy;
    public string miniBusPolicy;

    public List<int> allowedFleetSeries = new();
    public List<int> nightFleetSeries   = new();
    public int oldBusRangeStart;
    public int oldBusRangeEnd;

    // Stored as separate floats rather than UnityEngine.Color so this class
    // keeps zero UnityEngine-type dependencies beyond what's already here.
    public float routeColorR, routeColorG, routeColorB, routeColorA;
}

[System.Serializable]
public class NetworkSnapshot
{
    public string label = "Snapshot";
    public string capturedAtUtc;
    public List<RouteDataSnapshot> routes = new();
}

#if UNITY_EDITOR
// ═══════════════════════════════════════════════════════════════════════════════
//  EXPORTER — editor-only. Scans every BusRouteData asset in the project and
//  writes one NetworkSnapshot JSON file. This is "pt1": run it now, and
//  whenever you want to diff against later, run it again with a different
//  label and load both into RouteSnapshotViewerUI.
// ═══════════════════════════════════════════════════════════════════════════════
public static class RouteSnapshotExporter
{
    [MenuItem("Headway/Snapshot/Export Route Network Snapshot")]
    public static void ExportSnapshot()
    {
        string label = "Snapshot_" + System.DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string path = EditorUtility.SaveFilePanel("Save Route Snapshot", "Assets", label, "json");
        if (string.IsNullOrEmpty(path)) return;

        var snapshot = BuildSnapshot(Path.GetFileNameWithoutExtension(path));
        string json = JsonUtility.ToJson(snapshot, prettyPrint: true);
        File.WriteAllText(path, json);

        Debug.Log($"[RouteSnapshotExporter] Wrote {snapshot.routes.Count} routes to {path}");
        AssetDatabase.Refresh();
    }

    public static NetworkSnapshot BuildSnapshot(string label)
    {
        var snapshot = new NetworkSnapshot
        {
            label = label,
            capturedAtUtc = System.DateTime.UtcNow.ToString("o"),
        };

        string[] guids = AssetDatabase.FindAssets("t:BusRouteData");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var route = AssetDatabase.LoadAssetAtPath<BusRouteData>(path);
            if (route == null) continue;

            snapshot.routes.Add(CaptureRoute(route));
        }

        return snapshot;
    }

    private static RouteDataSnapshot CaptureRoute(BusRouteData route)
    {
        var cm = CityManager.Instance; // may be null if no scene is open/playing -- stop names just won't resolve

        var rd = new RouteDataSnapshot
        {
            routeId = route.routeId,
            routeNumber = route.routeNumber,
            routeName   = route.routeName,
            terminalACode = route.terminalACode,
            terminalZCode = route.terminalZCode,
            destinationNameOutbound = route.destinationNameOutbound,
            destinationNameInbound  = route.destinationNameInbound,
            routeQualifierOutbound  = route.routeQualifierOutbound,
            routeQualifierInbound   = route.routeQualifierInbound,
            headwayFromAMinutes = route.headwayFromAMinutes,
            headwayFromZMinutes = route.headwayFromZMinutes,
            oneWayTripMinutes   = route.oneWayTripMinutes,
            operatingStartMinutes = route.operatingStartMinutes,
            operatingEndMinutes   = route.operatingEndMinutes,

            // [FIX] previously unset -- see field comments on RouteDataSnapshot
            maxBusesAllowed   = route.maxBusesAllowed,
            articulatedPolicy = route.articulatedPolicy.ToString(),
            miniBusPolicy     = route.miniBusPolicy.ToString(),
            oldBusRangeStart  = route.oldBusRangeStart,
            oldBusRangeEnd    = route.oldBusRangeEnd,
            routeColorR = route.routeColor.r,
            routeColorG = route.routeColor.g,
            routeColorB = route.routeColor.b,
            routeColorA = route.routeColor.a,
        };

        if (route.allowedFleetSeries != null)
            rd.allowedFleetSeries.AddRange(route.allowedFleetSeries);
        if (route.nightFleetSeries != null)
            rd.nightFleetSeries.AddRange(route.nightFleetSeries);

        if (route.scheduleWindows != null)
            foreach (var w in route.scheduleWindows)
                rd.scheduleWindows.Add(ScheduleWindowSnapshot.From(w));

        if (route.variants != null)
            foreach (var v in route.variants)
            {
                var vs = new RouteVariantSnapshot
                {
                    variantLetter = v.variantLetter,
                    destinationNameOutboundOverride = v.destinationNameOutboundOverride,
                    destinationNameInboundOverride  = v.destinationNameInboundOverride,
                    terminalACodeOverride = v.terminalACodeOverride,
                    terminalZCodeOverride = v.terminalZCodeOverride,
                    overrideRoute = v.overrideRoute,
                    isShortTurn = v.isShortTurn,
                    turnbackStopCode = v.turnbackStopCode,
                    turnbackInboundStopCode = v.turnbackInboundStopCode,

                    // [FIX] previously unset -- see field comments on RouteVariantSnapshot
                    overrideSchedule      = v.overrideSchedule,
                    operatingStartMinutes = v.operatingStartMinutes,
                    operatingEndMinutes   = v.operatingEndMinutes,
                    headwayFromAMinutes   = v.headwayFromAMinutes,
                    headwayFromZMinutes   = v.headwayFromZMinutes,
                    oneWayTripMinutes     = v.oneWayTripMinutes,

                    overrideVehicleRestrictions = v.overrideVehicleRestrictions,
                    articulatedPolicyOverride   = v.articulatedPolicyOverride.ToString(),
                };

                if (v.scheduleWindows != null)
                    foreach (var w in v.scheduleWindows)
                        vs.scheduleWindows.Add(ScheduleWindowSnapshot.From(w));

                if (v.allowedFleetSeriesOverride != null)
                    vs.allowedFleetSeriesOverride.AddRange(v.allowedFleetSeriesOverride);

                if (v.outboundStopsOverride != null)
                    foreach (var b in v.outboundStopsOverride)
                        vs.outboundStopsOverride.Add(RouteStopSnapshot.From(b, cm?.GetStop(b.stopCode)));

                if (v.inboundStopsOverride != null)
                    foreach (var b in v.inboundStopsOverride)
                        vs.inboundStopsOverride.Add(RouteStopSnapshot.From(b, cm?.GetStop(b.stopCode)));

                if (v.outboundNodesOverride != null)
                    foreach (var n in v.outboundNodesOverride)
                        vs.outboundNodesOverride.Add(RouteNodeSnapshot.From(n));

                if (v.inboundNodesOverride != null)
                    foreach (var n in v.inboundNodesOverride)
                        vs.inboundNodesOverride.Add(RouteNodeSnapshot.From(n));

                rd.variants.Add(vs);
            }

        if (route.outboundStops != null)
            foreach (var b in route.outboundStops)
                rd.outboundStops.Add(RouteStopSnapshot.From(b, cm?.GetStop(b.stopCode)));

        if (route.inboundStops != null)
            foreach (var b in route.inboundStops)
                rd.inboundStops.Add(RouteStopSnapshot.From(b, cm?.GetStop(b.stopCode)));

        if (route.outboundNodes != null)
            foreach (var n in route.outboundNodes)
                rd.outboundNodes.Add(RouteNodeSnapshot.From(n));

        if (route.inboundNodes != null)
            foreach (var n in route.inboundNodes)
                rd.inboundNodes.Add(RouteNodeSnapshot.From(n));

        // [FIX] previously unset -- see field comment on RouteDataSnapshot.deadRunNodes
        if (route.deadRunNodes != null)
            foreach (var n in route.deadRunNodes)
                rd.deadRunNodes.Add(RouteNodeSnapshot.From(n));

        return rd;
    }
}
#endif