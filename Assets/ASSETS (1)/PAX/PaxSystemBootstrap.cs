using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  PAX SYSTEM BOOTSTRAP
//
//  Put this on one empty GameObject in your main scene (alongside CityManager /
//  BusScheduler / BusManager). It just guarantees the three new managers exist
//  and initialise in the right order:
//
//      CityManager.Start()        — builds roads/stops (already happens first
//                                    because CityManager has no dependency on
//                                    the pax system)
//      SidewalkNetwork (Awake)    — just sets Instance, builds nothing yet
//      PaxSimManager.Start()      — calls SidewalkNetwork.BuildFromCity() then
//                                    starts spawning
//      PaxVisualPool (Awake)      — pool pre-warmed, ready for first Acquire()
//
//  Unity's Start() order across separate GameObjects isn't guaranteed, so
//  PaxSimManager.Start() explicitly pulls CityManager.Instance and calls
//  BuildFromCity() itself rather than relying on Start() ordering — this
//  bootstrap component isn't strictly required for that reason, but it's a
//  convenient single place to add all three components instead of hunting
//  through the scene.
// ═══════════════════════════════════════════════════════════════════════════════
[RequireComponent(typeof(SidewalkNetwork))]
[RequireComponent(typeof(PaxSimManager))]
[RequireComponent(typeof(PaxVisualPool))]
public class PaxSystemBootstrap : MonoBehaviour
{
    private void Awake()
    {
        // Components are required above, so just confirm wiring in the log.
        Debug.Log("[PaxSystemBootstrap] Sidewalk/PaxSim/VisualPool components present.");
    }
}