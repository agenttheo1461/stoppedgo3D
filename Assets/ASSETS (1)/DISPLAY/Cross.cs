using System;

// Make sure there is NO "namespace MyGame { ... }" wrapping this — matches
// BusRegistry's pattern for global-namespace static utility classes.
public static class MDT_FocusBus
{
    public static event Action<int, bool> OnRequestFollowBus; // (fleetOrBusID, isPlayer)

    public static void RequestFollow(int fleetOrBusID, bool isPlayer)
        => OnRequestFollowBus?.Invoke(fleetOrBusID, isPlayer);
}