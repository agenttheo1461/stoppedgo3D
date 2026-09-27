using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusBoardDebugChannel
//
//  Plain static channel (not a MonoBehaviour) that lets the driver console push
//  content onto every interior board without either side needing a reference
//  to the other. Three independent overrides, all optional and all clearable:
//
//    "boardmsg <text>" / "ms <text>"  -- Push(): jumps text to the front of
//        the gray-zone rotation for a few seconds, same mechanism STOP
//        REQUESTED already uses. "ms" is just a shorthand alias for the same
//        call (see Driver.cs SubmitInput) -- the brief asked for a quick way
//        to type free text without spelling out "boardmsg" every time.
//
//    "boardrt <number>"  -- SetRouteOverride(): replaces the raw route/variant
//        number shown top-left of the black zone. Sticky until "boardclear"
//        or a new "boardrt" call -- no timer, since this is meant as a manual
//        override you leave in place, not a transient toast.
//
//    "boardestination des1 <route>" / "des2 <route>"  -- SetDestinationOverride():
//        looks up the given route in BusScheduler and pushes its OUTBOUND
//        (des1) or INBOUND (des2) terminal name as the destination headsign.
//        Also sticky. Actual lookup happens in Driver.cs (it already has the
//        BusScheduler reference) -- this class just stores the resulting string.
//
//  "boardclear" wipes all three at once.
// ═══════════════════════════════════════════════════════════════════════════════
public static class BusBoardDebugChannel
{
    // ── Transient message (front of gray-zone rotation) ─────────────────────
    private static string _message;
    private static float  _messageExpiresAt;

    public static void Push(string message, float seconds = 8f)
    {
        _message          = message;
        _messageExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.5f, seconds);
    }

    public static bool TryGetActive(out string message)
    {
        if (!string.IsNullOrEmpty(_message) && Time.realtimeSinceStartup < _messageExpiresAt)
        {
            message = _message;
            return true;
        }
        message = null;
        return false;
    }

    // ── Sticky route number override ─────────────────────────────────────────
    private static string _routeOverride;

    public static void SetRouteOverride(string routeNumber) => _routeOverride = routeNumber;

    public static bool TryGetRouteOverride(out string routeNumber)
    {
        routeNumber = _routeOverride;
        return !string.IsNullOrEmpty(_routeOverride);
    }

    // ── Sticky destination override ──────────────────────────────────────────
    private static string _destinationOverride;

    public static void SetDestinationOverride(string destination) => _destinationOverride = destination;

    public static bool TryGetDestinationOverride(out string destination)
    {
        destination = _destinationOverride;
        return !string.IsNullOrEmpty(_destinationOverride);
    }

    public static void Clear()
    {
        _message              = null;
        _messageExpiresAt     = 0f;
        _routeOverride        = null;
        _destinationOverride  = null;
    }
}