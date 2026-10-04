using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  PAX TRANSFER SYSTEM
//
//  Every boarding passenger gets a chance to be a TRANSFER rider -- someone
//  whose trip is actually 2 or 3 legs across different routes, connecting at a
//  stop the routes physically share (or a nearby stop across the street). This
//  is flavor/UI data, not a full simulation: a chain is rolled once, in full, at
//  the moment someone boards -- nobody actually rides a second bus in the
//  physics sense. What IS real: when a transfer rider's current leg ends, they
//  go into a `_pending` queue keyed to the stop + route + direction they're
//  waiting for, and PullArrivingTransfers lets whichever bus is dwelling there
//  pick them up -- including mid-dwell, if the door gets reopened, matching
//  "if a transfer gets to the stop while you're waiting there, you suddenly get
//  those pax added."
//
//  Called from PlayerHandoff.cs (player boarding/alighting + a dwell-tick poll
//  in Update()) and NPCBusController.cs's ProcessStopArrival (NPC boarding/
//  alighting). MDT_LiveMap's bus-chip popup reads GetOnboard(fleetNumber) to
//  show each rider's route chain instead of the old "next stop" ETA list.
//
//  Untested in Unity.
// ═══════════════════════════════════════════════════════════════════════════════

public struct TransferLeg
{
    public string route;
    public bool   outbound;
    public string atStopCode; // stop where this leg is boarded (shared or across-the-street from the previous leg's alighting stop)
}

public class OnboardPaxRecord
{
    public string           boardRoute;
    public bool             boardOutbound;
    public List<TransferLeg> remainingLegs = new List<TransferLeg>(); // legs AFTER the one they're riding now, in order

    public string FormatChain()
    {
        string DirTag(bool outbound) => outbound ? "OUB" : "IB";
        string s = $"{boardRoute} [{DirTag(boardOutbound)}]";
        foreach (var leg in remainingLegs)
            s += $" → {leg.route} [{DirTag(leg.outbound)}]";
        return s;
    }
}

public class PendingTransferArrival
{
    public string            stopCode;
    public string            forRoute;
    public bool              forOutbound;
    public float             arrivesAtAbsMin;
    public List<TransferLeg> remainingLegsAfter;
}

public static class PaxTransferSystem
{
    public const int   MaxLegs        = 3;      // 1 initial ride + up to 2 transfers
    public const float TransferRadius = 60f;     // "physically shares or is across the street" -- see custom51 spec

    private static readonly Dictionary<int, List<OnboardPaxRecord>> _onboard = new Dictionary<int, List<OnboardPaxRecord>>();
    private static readonly List<PendingTransferArrival>            _pending = new List<PendingTransferArrival>();

    // stopCode -> every (route, outbound) that calls there
    private static Dictionary<string, List<(string route, bool outbound)>> _servedBy;
    // stopCode -> other stopCodes within TransferRadius (a physical/across-the-street connection)
    private static Dictionary<string, List<string>> _nearby;
    private static bool _built = false;

    private static void EnsureGraph()
    {
        if (_built) return;
        _built = true;
        _servedBy = new Dictionary<string, List<(string, bool)>>();
        if (BusScheduler.Instance == null || BusScheduler.Instance.managedRoutes == null) return;

        foreach (var route in BusScheduler.Instance.managedRoutes)
        {
            if (route == null) continue;
            AddStops(route.GetStops(true),  route.routeNumber, true);
            AddStops(route.GetStops(false), route.routeNumber, false);
        }
        BuildNearbyIndex();
    }

    private static void AddStops(List<BusStopData> stops, string routeNumber, bool outbound)
    {
        if (stops == null) return;
        foreach (var s in stops)
        {
            if (s == null || string.IsNullOrEmpty(s.stopCode)) continue;
            if (!_servedBy.TryGetValue(s.stopCode, out var list))
                _servedBy[s.stopCode] = list = new List<(string, bool)>();
            var entry = (routeNumber, outbound);
            if (!list.Contains(entry)) list.Add(entry);
        }
    }

    private static void BuildNearbyIndex()
    {
        _nearby = new Dictionary<string, List<string>>();
        if (CityManager.Instance == null) return;

        var codes = new List<string>(_servedBy.Keys);
        var positions = new Dictionary<string, Vector3>();
        foreach (var code in codes)
        {
            var st = CityManager.Instance.GetStop(code);
            if (st != null) positions[code] = st.GetWorldPosition();
        }

        float radiusSq = TransferRadius * TransferRadius;
        foreach (var a in codes)
        {
            if (!positions.TryGetValue(a, out var pa)) continue;
            var list = new List<string>();
            foreach (var b in codes)
            {
                if (b == a || !positions.TryGetValue(b, out var pb)) continue;
                if ((pa - pb).sqrMagnitude <= radiusSq) list.Add(b);
            }
            _nearby[a] = list;
        }
    }

    // ── Boarding / alighting ─────────────────────────────────────────────────
    /// <summary>Call once per newly-boarded passenger. alightingStopCode is where THIS leg ends
    /// (the same "stops ahead" roll everything else already uses, converted to a real stop code) --
    /// pass null/empty if unknown, which just means no transfer chain gets rolled for them.</summary>
    public static void RegisterBoarding(int fleetNumber, string boardRoute, bool boardOutbound, string alightingStopCode)
    {
        if (!_onboard.TryGetValue(fleetNumber, out var list))
            _onboard[fleetNumber] = list = new List<OnboardPaxRecord>();

        var rec = new OnboardPaxRecord { boardRoute = boardRoute, boardOutbound = boardOutbound };
        if (!string.IsNullOrEmpty(alightingStopCode))
        {
            EnsureGraph();
            rec.remainingLegs = RollTransferChain(alightingStopCode, boardRoute);
        }
        list.Add(rec);
    }

    /// <summary>FIFO alighting, same convention as _passengerDestinations.RemoveAt(0) elsewhere.
    /// Anyone alighting with legs still left in their chain goes into the pending-transfer queue
    /// instead of just vanishing.</summary>
    public static void RegisterAlighting(int fleetNumber, int count, float nowAbsMin)
    {
        if (count <= 0 || !_onboard.TryGetValue(fleetNumber, out var list)) return;

        for (int i = 0; i < count && list.Count > 0; i++)
        {
            var rec = list[0];
            list.RemoveAt(0);
            if (rec.remainingLegs == null || rec.remainingLegs.Count == 0) continue;

            var nextLeg = rec.remainingLegs[0];
            _pending.Add(new PendingTransferArrival
            {
                stopCode           = nextLeg.atStopCode,
                forRoute            = nextLeg.route,
                forOutbound         = nextLeg.outbound,
                // short walk across a shared/street-facing stop, not a real commute -- flavor timing only
                arrivesAtAbsMin     = nowAbsMin + Random.Range(0.5f, 2.5f),
                remainingLegsAfter  = rec.remainingLegs.GetRange(1, rec.remainingLegs.Count - 1),
            });
        }
    }

    /// <summary>Whatever's currently riding this fleet number, oldest-boarded first -- for the
    /// live-map bus-chip popup.</summary>
    public static List<OnboardPaxRecord> GetOnboard(int fleetNumber) =>
        _onboard.TryGetValue(fleetNumber, out var list) ? list : EmptyList;
    private static readonly List<OnboardPaxRecord> EmptyList = new List<OnboardPaxRecord>();

    /// <summary>Picks up every pending transfer rider waiting at this stop for this exact
    /// route+direction whose arrival time has passed, and adds them onto fleetNumberPickingUp's
    /// onboard list. Returns how many boarded -- call this on door-open AND poll it while dwelling,
    /// since a transfer can "arrive while you're waiting there."</summary>
    public static int PullArrivingTransfers(string stopCode, string route, bool outbound, float nowAbsMin, int fleetNumberPickingUp)
    {
        if (string.IsNullOrEmpty(stopCode) || string.IsNullOrEmpty(route)) return 0;
        int picked = 0;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (p.stopCode != stopCode || p.forRoute != route || p.forOutbound != outbound) continue;
            if (p.arrivesAtAbsMin > nowAbsMin) continue;

            _pending.RemoveAt(i);
            picked++;

            if (!_onboard.TryGetValue(fleetNumberPickingUp, out var list))
                _onboard[fleetNumberPickingUp] = list = new List<OnboardPaxRecord>();
            list.Add(new OnboardPaxRecord
            {
                boardRoute      = route,
                boardOutbound   = outbound,
                remainingLegs   = p.remainingLegsAfter,
            });
        }
        return picked;
    }

    /// <summary>Fleet numbers no longer in service (retired/despawned) should drop their roster --
    /// call from wherever a bus's onboard count gets reset to 0 on reassignment, to avoid a stale
    /// list quietly growing forever. Safe to skip; harmless if never called, just wastes memory.</summary>
    public static void ClearFleet(int fleetNumber) => _onboard.Remove(fleetNumber);

    // ── Chain rolling ─────────────────────────────────────────────────────────
    private static List<TransferLeg> RollTransferChain(string firstAlightStopCode, string excludeRoute)
    {
        var chain = new List<TransferLeg>();
        string anchorStop = firstAlightStopCode;
        string lastRoute  = excludeRoute;

        int extraLegs = RollExtraLegCount();
        for (int i = 0; i < extraLegs; i++)
        {
            var candidates = CandidateRoutesNear(anchorStop, lastRoute);
            if (candidates.Count == 0) break;

            var pick = candidates[Random.Range(0, candidates.Count)];
            chain.Add(new TransferLeg { route = pick.route, outbound = pick.outbound, atStopCode = pick.atStopCode });
            lastRoute  = pick.route;
            anchorStop = pick.atStopCode; // good-enough flavor anchor for the NEXT transfer, if any
        }
        return chain;
    }

    /// <summary>"Most will prob be transfers" -- 55% chance of at least one transfer,
    /// capped at 2 extra legs (3 total) per custom51's spec.</summary>
    private static int RollExtraLegCount()
    {
        float r = Random.value;
        if (r < 0.45f) return 0;
        if (r < 0.80f) return 1;
        return 2;
    }

    private static List<(string route, bool outbound, string atStopCode)> CandidateRoutesNear(string stopCode, string excludeRoute)
    {
        var result = new List<(string, bool, string)>();
        if (_servedBy == null) return result;

        void AddFrom(string code)
        {
            if (!_servedBy.TryGetValue(code, out var list)) return;
            foreach (var (route, outbound) in list)
            {
                if (route == excludeRoute) continue;
                var entry = (route, outbound, code);
                if (!result.Contains(entry)) result.Add(entry);
            }
        }

        AddFrom(stopCode);
        if (_nearby != null && _nearby.TryGetValue(stopCode, out var nearbyCodes))
            foreach (var nc in nearbyCodes) AddFrom(nc);

        return result;
    }
}
