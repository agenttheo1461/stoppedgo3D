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
    float ts = Time.time;
    for (int i = _all.Count - 1; i >= 0; i--)
    {
        if (_all[i] == null) { _all.RemoveAt(i); continue; }
        if (!_all[i].enabled) continue;
        if (_all[i].State == NPCBusController.BusState.Idle) continue;
        _all[i].ManagedTick(Time.deltaTime, ts);
    }
}
private void FixedUpdate()
{
    float fdt = Time.fixedDeltaTime;
    for (int i = 0; i < _all.Count; i++)
    {
        if (_all[i] == null) continue;
        if (!_all[i].enabled) continue;
        if (_all[i].State == NPCBusController.BusState.Idle) continue;
        _all[i].ManagedFixedTick(fdt);
    }
}
}