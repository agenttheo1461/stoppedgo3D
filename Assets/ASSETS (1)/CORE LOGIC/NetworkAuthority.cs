using Unity.Netcode;

// ═══════════════════════════════════════════════════════════════════════════════
//  NETWORK AUTHORITY
//
//  The one thing every system checks to answer "should I actually be
//  simulating anything right now, or is the server the authority for this?"
//
//  ShouldSimulate is true for single-player (no NetworkManager, or one that
//  exists but was never used to connect) AND for the host/server. It's false
//  ONLY for a genuine network CLIENT -- a client still spawns its OWN full
//  local fleet at scene Start (DepotManager, unconditionally, same as
//  single-player -- see NetworkGameBridge's header comment for why that's
//  now safe: every process gets the SAME buses, matched by a deterministic
//  busID, nothing is torn down or cloned over the network any more). What a
//  client does NOT do is run bus AI/scheduling decisions for buses it isn't
//  driving -- it just displays whatever NetworkGameBridge's periodic
//  broadcast says for those. Everything else (menus, HUD, input) stays
//  exactly as it always was on every client; this gate is specifically about
//  SIMULATION, not UI, and not fleet existence.
//
//  This is the "script that keeps track of everything" -- the single choke
//  point BusUpdateManager (bus AI ticking) and BusScheduler (dispatch/
//  scheduling decisions) check, instead of each system independently
//  guessing whether it's safe to run its own local simulation.
// ═══════════════════════════════════════════════════════════════════════════════
public static class NetworkAuthority
{
    public static bool ShouldSimulate
    {
        get
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return true; // no networking active at all -- normal single-player
            return nm.IsServer; // host counts as server-authoritative; a pure client does not
        }
    }

    /// <summary>True only for a connected client that is NOT also the host -- the one case where
    /// local simulation must be fully suppressed and the world instead comes entirely from the
    /// network.</summary>
    public static bool IsPureClient
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return nm != null && nm.IsListening && nm.IsClient && !nm.IsServer;
        }
    }
}
