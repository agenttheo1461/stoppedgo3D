using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  PLAYER REGISTRY
//
//  Every live human-driven bus in this session, local or remote, as ONE list --
//  the thing BusTrackerService's live-arrivals loop and MDT_LiveMap's chip loop
//  both used to reimplement independently (local player + a manual
//  NetworkGameBridge.GetPossessedBusIDs() scan, each with its own copy of the
//  "skip my own physical busID" / sentinel-resolution logic). Two separate,
//  slightly-differently-written copies of the same lookup is exactly how the
//  "-2 ambiguity" bug and the fleet-label bugs kept recurring in different
//  places this session -- one canonical source instead.
//
//  In single-player this list has at most ONE entry (the local player, if
//  they're currently driving a bus) -- deliberately not special-cased away.
//  Every caller gets the same shape either way instead of branching on "am I
//  in multiplayer right now."
//
//  Pure static utility, not a MonoBehaviour -- there's no per-frame state to
//  own here, just a fresh, cheap snapshot (at most a handful of players) built
//  from PlayerHandoff.Instance + NetworkGameBridge.Instance on demand. Returns
//  a brand-new List each call (never a shared/reused buffer) so two callers
//  mid-iteration can never stomp on each other.
// ═══════════════════════════════════════════════════════════════════════════════
public static class PlayerRegistry
{
    public struct Entry
    {
        /// <summary>BusScheduler.PLAYER_BUS_ID-equivalent identity for this player --
        /// -2 for the local player, or the -1000-based per-client encoding for a remote one
        /// (NetworkGameBridge.ClientIdToSentinel). This is what TryGetAssignedSlot/
        /// ResolveFleetLabel/IsPlayer all key on.</summary>
        public int Sentinel;
        /// <summary>The real physical busID this player is driving (BusManager's identity space,
        /// same on every machine). -1 should never actually occur in a returned Entry -- GetAll
        /// only adds an entry once it has a real bus.</summary>
        public int PhysicalBusID;
        public ulong ClientId;
        public bool IsLocal;
        public Transform Transform;
        public bool IsOutbound;
        public string RouteNumber;
        public string VariantLetter;
        public int FleetNumber;
        public int NextStopIndex;
    }

    /// <summary>Every currently-driving player this machine knows about. Local player first (if
    /// any), then every other possessed bus NetworkGameBridge knows about (empty list outside a
    /// network session). Safe to call every frame -- cheap, no caching, always reflects current
    /// possession state.</summary>
    public static List<Entry> GetAll()
    {
        var result = new List<Entry>(4);

        var player = PlayerHandoff.Instance;
        int myPhysicalBusID = player != null ? player.PossessedPhysicalBusID : -1;

        if (player != null && player.playerBus != null && myPhysicalBusID >= 0)
        {
            result.Add(new Entry
            {
                Sentinel      = player.PlayerBusID,
                PhysicalBusID = myPhysicalBusID,
                ClientId      = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : NetworkManager.ServerClientId,
                IsLocal       = true,
                Transform     = player.playerBus.transform,
                IsOutbound    = player.IsOutbound,
                RouteNumber   = player.ActiveRoute,
                VariantLetter = player.ActiveVariant,
                FleetNumber   = player.FleetNumber,
                NextStopIndex = player.CurrentStopIndex,
            });
        }

        if (NetworkGameBridge.Instance != null && BusManager.Instance != null)
        {
            foreach (var busID in NetworkGameBridge.Instance.GetPossessedBusIDs())
            {
                if (busID == myPhysicalBusID) continue; // already added above -- don't double-list
                var clientId = NetworkGameBridge.Instance.GetPossessingClientId(busID);
                if (!clientId.HasValue) continue;
                var rec = BusManager.Instance.GetRecord(busID);
                if (rec?.controller == null) continue;

                result.Add(new Entry
                {
                    Sentinel      = NetworkGameBridge.ClientIdToSentinel(clientId.Value),
                    PhysicalBusID = busID,
                    ClientId      = clientId.Value,
                    IsLocal       = false,
                    Transform     = rec.controller.transform,
                    IsOutbound    = rec.controller.IsOutbound,
                    RouteNumber   = rec.controller.CurrentRoute?.routeNumber,
                    VariantLetter = rec.controller.variantLetter,
                    FleetNumber   = rec.controller.fleetNumber,
                    NextStopIndex = Mathf.Max(0, rec.controller.NextStopIndex),
                });
            }
        }

        return result;
    }

    /// <summary>Just this machine's own player, if they're currently driving -- null otherwise.
    /// Convenience for a caller that only ever cared about the local player before (e.g. a HUD
    /// element) and doesn't need the full multi-player list.</summary>
    public static Entry? GetLocal()
    {
        var all = GetAll();
        foreach (var e in all)
            if (e.IsLocal) return e;
        return null;
    }
}
