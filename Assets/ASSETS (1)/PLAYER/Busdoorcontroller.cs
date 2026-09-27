using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS DOOR CONTROLLER — sits on the same bus root as BusSimulationController
//  (player) or NPCBusController (NPC). Owns the front/rear BusDoorSets for
//  THIS bus and drives their visual animation off the REAL doorsOpen /
//  rearDoorsOpen fields those controllers already use for movement-locking —
//  it doesn't own door-open state itself, it just watches and animates.
//
//  Only ONE of playerBus / npcBus should be assigned, matching whichever
//  controller is actually active on this bus.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusDoorController : MonoBehaviour
{
    public BusDoorSet frontDoors;
    public BusDoorSet rearDoors;

    [Tooltip("Assign if this bus is player-driven (BusSimulationController).")]
    public BusSimulationController playerBus;
    [Tooltip("Assign if this bus is NPC-driven (NPCBusController).")]
    public NPCBusController npcBus;

    private bool _prevFrontOpen;
    private bool _prevRearOpen;

    void Update()
    {
        bool frontOpen = playerBus != null ? playerBus.doorsOpen : (npcBus != null && npcBus.doorsOpen);
        bool rearOpen  = playerBus != null ? playerBus.rearDoorsOpen : (npcBus != null && npcBus.rearDoorsOpen);

        if (frontOpen != _prevFrontOpen)
        {
            if (frontDoors != null) { if (frontOpen) frontDoors.Open(); else frontDoors.Close(); }
            _prevFrontOpen = frontOpen;
        }

        if (rearOpen != _prevRearOpen)
        {
            if (rearDoors != null) { if (rearOpen) rearDoors.Open(); else rearDoors.Close(); }
            _prevRearOpen = rearOpen;
        }
    }

    // Convenience for spawn/pooling — avoids a visible open/close cycle the
    // instant a bus appears already carrying stale door state.
    public void SnapAllClosed()
    {
        if (frontDoors != null) frontDoors.SnapClosed();
        if (rearDoors != null) rearDoors.SnapClosed();
        _prevFrontOpen = false;
        _prevRearOpen = false;
    }
}