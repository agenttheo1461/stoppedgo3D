using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS UPDATE MANAGER  — simple active/inactive split
//
//  Buses in BusState.Idle are NEVER ticked. SetIdle(true) on NPCBusController
//  disables their Rigidbody, AudioSource, renderers, and colliders so Unity
//  itself does zero work on them between dispatches.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusUpdateManager : MonoBehaviour
{
    public static BusUpdateManager Instance { get; private set; }

    private readonly List<NPCBusController> _all = new(520);

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public void Register(NPCBusController bus)
    {
        if (!_all.Contains(bus)) _all.Add(bus);
    }

    public void Unregister(NPCBusController bus)
    {
        _all.Remove(bus);
    }

private void Update()
{
    // [FIX] Update() used to bail out entirely on a pure client (same blanket
    // NetworkAuthority.ShouldSimulate gate as FixedUpdate below). That was too
    // broad: it also skipped ManagedTick's per-camera visual/audio upkeep
    // (LOD tier, renderer cull, audio cull) for EVERY bus on that client,
    // including a remote player's possessed bus -- which is exactly why that
    // bus's IsNetworkPossessed cull-exemption never actually ran and the bus
    // stayed invisible/silent on the opposite end no matter what its distance
    // was. ManagedTick now always runs; the AI/movement-decision portions
    // that must stay host-authoritative (UpdateSimulationTick, obstacle
    // avoidance, unstuck recovery, breakdown FX) are gated INSIDE ManagedTick
    // instead, where they can special-case a network-possessed bus. Movement
    // PHYSICS (FixedUpdate below) is untouched -- a client's copy of a bus it
    // doesn't control gets its transform overwritten wholesale by
    // NetworkGameBridge's broadcast, so running local Rigidbody forces on top
    // of that would just fight the network state.
    float ts = Time.time;
    for (int i = _all.Count - 1; i >= 0; i--)
    {
        if (_all[i] == null) { _all.RemoveAt(i); continue; }
        if (!_all[i].enabled) continue;
        // [FIX] THE ACTUAL REMAINING CULLING BUG: this class's own header comment says it --
        // "SetIdle(true) disables their Rigidbody, AudioSource, renderers, and colliders." A bus
        // picked up for Free Drive / Fleet-tab adopt is overwhelmingly likely to have been sitting
        // IDLE at a depot at the moment of possession (that's the whole premise of "idle bus
        // available to take") -- and nothing in the possession flow ever transitions its State
        // away from Idle. So on every machine OTHER than the one actually driving it (where the
        // renderers/audio/colliders were already force-disabled by SetIdle), this unconditional
        // "skip Idle buses" check skipped ManagedTick ENTIRELY for a possessed bus -- meaning the
        // IsNetworkPossessed cull-exemption inside ManagedTick never even got a CHANCE to run, no
        // matter how correct that logic was. This is almost certainly the real reason the bus
        // stayed invisible/silent through every previous fix. A possessed bus now always ticks
        // regardless of its State, so ManagedTick's own renderer/audio-cull exemption can actually
        // override whatever SetIdle(true) forced off.
        if (_all[i].State == NPCBusController.BusState.Idle && !_all[i].IsNetworkPossessed) continue;
        _all[i].ManagedTick(Time.deltaTime, ts);
    }
}
private void FixedUpdate()
{
    // A network client never runs bus movement physics locally -- the server
    // is authoritative for every bus's position, and the client just applies
    // whatever NetworkGameBridge's periodic world-state broadcast says onto
    // the bus's transform. Single-player and the host both still tick
    // normally (NetworkAuthority.ShouldSimulate is true for both).
    if (!NetworkAuthority.ShouldSimulate) return;

    float fdt = Time.fixedDeltaTime;
    for (int i = 0; i < _all.Count; i++)
    {
        if (_all[i] == null) continue;
        if (!_all[i].enabled) continue;
        if (_all[i].State == NPCBusController.BusState.Idle) continue;
        // [FIX] NetworkGameBridge's possession bookkeeping used to fully disable a possessed
        // bus's NPCBusController on the HOST (see its own header comment) specifically so THIS
        // loop wouldn't run independent AI movement physics on top of a bus a human is actually
        // driving (locally or remotely) -- that fought the driver's authoritative position every
        // tick. Disabling the component also unregistered it from BusUpdateManager entirely,
        // which silently broke Update()'s visual/audio cull-exemption above forever (that fix's
        // whole point). Now that the component stays enabled, skip movement physics for a
        // possessed bus HERE instead -- narrower, and doesn't collateral-damage the visual tick.
        if (_all[i].IsNetworkPossessed) continue;
        _all[i].ManagedFixedTick(fdt);
    }
}
}