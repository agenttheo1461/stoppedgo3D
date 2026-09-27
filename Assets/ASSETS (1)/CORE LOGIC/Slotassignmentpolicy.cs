/// <summary>Decides whether a claim attempt is allowed — caps, vehicle
/// restrictions, random denial. Completely swappable: a "relaxed mode"
/// policy with no denial chance and no caps is a five-line class.</summary>
public interface ISlotAssignmentPolicy
{
    bool CanClaim(int busID, bool isPlayer, BusRouteData routeData, int liveCountOnRoute, out string denialReason);
}

public class DefaultSlotAssignmentPolicy : ISlotAssignmentPolicy
{
    public int candidateWindowSize = 3;
    public float slotDenialChance = 0.05f;

    private readonly System.Func<int, int> _resolveFleetNumber;

    public DefaultSlotAssignmentPolicy(System.Func<int, int> resolveFleetNumber) => _resolveFleetNumber = resolveFleetNumber ?? (_ => -1);

    public bool CanClaim(int busID, bool isPlayer, BusRouteData routeData, int liveCountOnRoute, out string denialReason)
    {
        denialReason = null;
        if (isPlayer) return true; // player bypasses vehicle policy/caps/random denial entirely

        if (routeData != null)
        {
            int fleetNum = _resolveFleetNumber(busID);
            if (fleetNum >= 0 && !routeData.IsBusAllowed(fleetNum)) { denialReason = "vehicle policy"; return false; }
            if (liveCountOnRoute >= routeData.maxBusesAllowed) { denialReason = "route at cap"; return false; }
        }

        if (UnityEngine.Random.value < slotDenialChance) { denialReason = "random denial"; return false; }

        return true;
    }
}