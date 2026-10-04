#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  NETWORK PREFAB SETUP  (Editor-only)
//
//  [CHANGE 2026-09-29] Multiplayer, rebuilt architecture: this used to ADD
//  NetworkObject/NetworkTransform/NetworkRigidbody/BusNetworkSync to every bus
//  prefab (Phase 2/3's per-bus-NetworkObject model). That model was the source
//  of nearly every multiplayer bug hit this project -- GlobalObjectIdHash
//  collisions, prefab-registration mismatches, and (most recently) the
//  "SYSTEMS" scene GameObject accidentally getting these same components,
//  which poisoned the entire connection sync for every client. Per the user's
//  own explicit direction, buses no longer carry a NetworkObject at all --
//  every process spawns its own full local fleet (same as single-player
//  always did) and NetworkGameBridge (see that file's header comment) keys
//  position/state sync off a stable busID instead. This tool now does the
//  OPPOSITE of what it used to: menu bar Headway ▸ Multiplayer ▸ Remove
//  Network Components From All Bus Prefabs strips those four components back
//  off every prefab FleetRoster references, undoing the original setup.
//
//  BusNetworkSync.cs itself has been deleted (replaced by NetworkGameBridge),
//  so any prefab that still has it shows up as a "Missing Script" slot --
//  GameObjectUtility.RemoveMonoBehavioursWithMissingScript (a real, documented
//  Editor API) is what strips those, alongside the three typed component
//  removals below for NetworkObject/NetworkTransform/NetworkRigidbody. Note:
//  this removes ANY missing-script component on these prefabs, not just the
//  old BusNetworkSync ones -- fine here since bus prefabs shouldn't have any
//  other dangling missing-script references, but worth knowing if that
//  assumption is ever wrong.
//
//  Wrapped in #if UNITY_EDITOR so it's automatically excluded from any real
//  player build -- no separate Editor/ folder needed for that.
// ═══════════════════════════════════════════════════════════════════════════════
public static class NetworkPrefabSetupEditor
{
    [MenuItem("Headway/Multiplayer/Remove Network Components From All Bus Prefabs")]
    public static void RemoveNetworkComponentsFromAllBusPrefabs()
    {
        var fleetRoster = FindFleetRoster();

        if (fleetRoster == null || fleetRoster.series == null)
        {
            EditorUtility.DisplayDialog("Network Prefab Cleanup", "Couldn't find a FleetRosterData asset -- nothing to do.", "OK");
            return;
        }

        var seen = new HashSet<GameObject>();
        int updated = 0, alreadyClean = 0, missingScriptsRemoved = 0;

        foreach (var s in fleetRoster.series)
        {
            if (s == null || s.prefab == null) continue;
            if (!seen.Add(s.prefab)) continue; // multiple series can share one physical prefab -- touch it once

            string path = AssetDatabase.GetAssetPath(s.prefab);
            if (string.IsNullOrEmpty(path)) continue;

            var root = PrefabUtility.LoadPrefabContents(path);
            bool changed = false;

            // BusNetworkSync.cs no longer exists -- any prefab that still had it now shows a
            // "Missing Script" slot instead. Check every object in the hierarchy, not just root,
            // since it (like NetworkRigidbody) could have ended up on a child.
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                if (removed > 0) { missingScriptsRemoved += removed; changed = true; }
            }

            // [FIX] Order matters -- NetworkRigidbody has [RequireComponent(typeof(NetworkTransform))]
            // (confirmed in the installed package source), so destroying NetworkTransform first
            // throws "Can't remove NetworkTransform because NetworkRigidbody depends on it."
            // Dependents come out before their dependencies: NetworkRigidbody, then
            // NetworkTransform, then NetworkObject last (nothing depends on it via
            // RequireComponent).
            //
            // [FIX 2] root.GetComponent<NetworkTransform>() (root only) missed a SECOND
            // NetworkTransform on the ~7 prefabs whose Rigidbody -- and therefore NetworkRigidbody
            // -- lives on a child (e.g. "front"): Unity's RequireComponent auto-adds its
            // dependency onto the SAME GameObject as whatever's being added, not onto the
            // hierarchy root, so AddComponent<NetworkRigidbody>() on that child implicitly gave
            // IT its own NetworkTransform too, alongside the one explicitly added on root. Found
            // this by reading a cleaned-up prefab back after running the tool -- root's
            // NetworkObject/NetworkRigidbody were gone as expected, but a NetworkTransform was
            // still sitting on "front". GetComponentsInChildren catches both instead of just root.
            var allNetRbs = root.GetComponentsInChildren<NetworkRigidbody>(true);
            foreach (var netRb in allNetRbs) { Object.DestroyImmediate(netRb, true); changed = true; }

            var allNetTransforms = root.GetComponentsInChildren<NetworkTransform>(true);
            foreach (var netTransform in allNetTransforms) { Object.DestroyImmediate(netTransform, true); changed = true; }

            var netObj = root.GetComponent<NetworkObject>();
            if (netObj != null) { Object.DestroyImmediate(netObj, true); changed = true; }

            if (changed) { PrefabUtility.SaveAsPrefabAsset(root, path); updated++; }
            else alreadyClean++;

            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log($"[NetworkPrefabSetupEditor] {updated} bus prefab(s) cleaned up, {alreadyClean} already had nothing to remove, " +
                   $"{missingScriptsRemoved} missing-script slot(s) removed.");
        EditorUtility.DisplayDialog("Network Prefab Cleanup",
            $"{updated} bus prefab(s) had Network Object/Network Transform/Network Rigidbody/the old " +
            $"(now-deleted) Bus Network Sync removed.\n{alreadyClean} already had nothing to remove.",
            "OK");
    }

    private static FleetRosterData FindFleetRoster()
    {
        var depotManager = Object.FindFirstObjectByType<DepotManager>();
        var fleetRoster = depotManager != null ? depotManager.fleetRoster : null;
        if (fleetRoster == null)
        {
            var guids = AssetDatabase.FindAssets("t:FleetRosterData");
            if (guids.Length > 0)
                fleetRoster = AssetDatabase.LoadAssetAtPath<FleetRosterData>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }
        return fleetRoster;
    }
}
#endif
