using UnityEngine;
using System.Collections.Generic;

// Make sure there is NO "namespace MyGame { ... }" wrapping this
public static class BusRegistry
{
    public static Dictionary<int, NPCBusController> ActiveBuses = new Dictionary<int, NPCBusController>();

    public static void Register(int id, NPCBusController controller) 
    {

            if (!ActiveBuses.ContainsKey(id)) ActiveBuses.Add(id, controller);
        else ActiveBuses[id] = controller;
    }

    public static void Unregister(int id) 
    {
        if (ActiveBuses.ContainsKey(id)) ActiveBuses.Remove(id);
    }
    public static int GetBusIDFromFleetNumber(int fleetNumber)
    {
        foreach (var kv in ActiveBuses)
        {
            if (kv.Value != null && kv.Value.fleetNumber == fleetNumber)
            {
                return kv.Key; // Returns the active runtime ID (e.g., 1, 2, 3)
            }
        }
        return -1; // Bus is still pre-scheduled/unspawned
    }
}