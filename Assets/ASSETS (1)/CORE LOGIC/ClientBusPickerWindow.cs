using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  CLIENT BUS PICKER  —  the client's "main menu"
//
//  [CHANGE 2026-09-29] Rebuilt for the shared-world architecture: a network
//  CLIENT now keeps its own FULL local fleet, spawned identically to the host
//  (same FleetRosterData, same deterministic busID assignment -- see
//  NetworkGameBridge's header comment). There's no more per-bus NetworkObject/
//  ownership to check -- "is this bus free" is now NetworkGameBridge.IsPossessed
//  (backed by a real NetworkList, no round trip needed), not
//  NetworkObject.IsOwnedByServer.
//
//  Deliberately grabs via Free Drive (PlayerHandoff.ResetForFreshPossession)
//  rather than the normal scheduled-route-join flow (BusSelectMenu's route
//  picker, which goes through BusScheduler.PLAYER_BUS_ID) -- confirmed by
//  reading ResetForFreshPossession directly that Free Drive never touches
//  _slotByBus/PLAYER_BUS_ID at all, which matters because that system is a
//  single shared dictionary key (-2) with no per-connection concept yet, so
//  it can't currently tell two different remote players apart. Free Drive
//  sidesteps that problem entirely rather than needing it solved first.
//
//  Auto-opens itself whenever this instance is a real network client with no
//  bus possessed yet; closes itself the moment a bus is taken. Same OnGUI/
//  MDT_UITheme convention as every other window this session (LanConnectHUD,
//  AlertsCenterWindow, etc.) -- no prefab or Canvas needed.
//
//  Untested in Unity, same as everything else in this batch.
// ═══════════════════════════════════════════════════════════════════════════════
public class ClientBusPickerWindow : MonoBehaviour
{
    public static ClientBusPickerWindow Instance { get; private set; }

    private bool _open;
    private Rect _windowRect = new Rect(300, 80, 360, 440);
    private Vector2 _scroll;

    private GUIStyle _lblTitle, _lblBody, _lblDim, _btn, _btnReclaim;
    private bool _stylesReady;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("ClientBusPickerWindow").AddComponent<ClientBusPickerWindow>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    private void Update()
    {
        _open = NetworkAuthority.IsPureClient
            && (PlayerHandoff.Instance == null || PlayerHandoff.Instance.playerBus == null);
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle = MDT_UITheme.MakeLabel(16, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblBody  = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _lblDim   = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _btn      = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 12);
        _btnReclaim = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextCyan, 12);
    }

    private void OnGUI()
    {
        if (!_open) return;
        EnsureStyles();

        MDT_UITheme.DrawSoftShadow(_windowRect, 18f);
        MDT_UITheme.DrawRoundedRectBordered(_windowRect, 14f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        float x = _windowRect.x + 14, y = _windowRect.y + 12, w = _windowRect.width - 28;
        GUI.Label(new Rect(x, y, w, 22), "PICK A BUS", _lblTitle);
        y += 28;
        GUI.Label(new Rect(x, y, w, 32), "Connected as a client — pick any bus below to start driving it.", _lblDim);
        y += 40;

        var reclaimable = new List<NPCBusController>();
        var available = new List<NPCBusController>();
        if (BusManager.Instance != null)
        {
            var bridge = NetworkGameBridge.Instance;
            foreach (var rec in BusManager.Instance.GetAllRecords())
            {
                if (rec.controller == null) continue;
                if (bridge != null && bridge.IsInGrace(rec.busID)) { reclaimable.Add(rec.controller); continue; }
                if (bridge != null && bridge.IsPossessed(rec.busID)) continue; // someone else already has this one
                available.Add(rec.controller);
            }
        }

        // Reclaim section -- a disconnect (network drop, app restart) within its grace window
        // leaves a bus reserved instead of handing it straight back to NPC AI (see
        // NetworkGameBridge.disconnectGraceSeconds). Shown above the normal list, distinctly
        // colored, so reconnecting is obviously different from picking a fresh bus.
        if (reclaimable.Count > 0)
        {
            GUI.Label(new Rect(x, y, w, 18), "RECLAIM YOUR BUS", _lblTitle);
            y += 22;
            foreach (var ctrl in reclaimable)
            {
                string label = $"Fleet #{ctrl.fleetNumber}  ·  Route {(ctrl.CurrentRoute != null ? ctrl.CurrentRoute.routeNumber : "--")} (reclaim)";
                if (GUI.Button(new Rect(x, y, w, 32), label, _btnReclaim))
                    ReclaimBus(ctrl);
                y += 36;
            }
            y += 8;
        }

        var listArea = new Rect(x, y, w, _windowRect.height - (y - _windowRect.y) - 14);
        float rowH = 40f;
        _scroll = GUI.BeginScrollView(listArea, _scroll, new Rect(0, 0, w - 20, Mathf.Max(listArea.height, available.Count * rowH)));

        // [FIX] TakeBus() below can throw (an edge-case bus state a normal single-player pick
        // never hits). If it does mid-loop, without this try/finally the GUI.EndScrollView() call
        // after the loop never runs -- Unity's IMGUI state stays open/corrupted for the rest of
        // that frame, which manifests as OTHER, unrelated windows breaking too ("most UI things
        // aren't working"), not just this one. Confirmed as the likely cause earlier this session.
        try
        {
            float ry = 0f;
            foreach (var ctrl in available)
            {
                string label = $"Fleet #{ctrl.fleetNumber}  ·  Route {(ctrl.CurrentRoute != null ? ctrl.CurrentRoute.routeNumber : "--")}";

                if (GUI.Button(new Rect(0, ry, w - 20, rowH - 4), label, _btn))
                    TakeBus(ctrl);
                ry += rowH;
            }

            if (available.Count == 0 && reclaimable.Count == 0)
                GUI.Label(new Rect(0, 0, w - 20, 24), "No buses available right now.", _lblDim);
        }
        finally
        {
            GUI.EndScrollView();
        }
    }

    private void ReclaimBus(NPCBusController ctrl)
    {
        if (PlayerHandoff.Instance == null || ctrl == null || NetworkGameBridge.Instance == null) return;
        var busSim = ctrl.GetComponentInChildren<BusSimulationController>(true);
        if (busSim == null) { Debug.LogWarning($"[ClientBusPickerWindow] '{ctrl.name}' has no BusSimulationController -- can't reclaim it."); return; }

        int fleetNumber = ctrl.fleetNumber;
        NetworkGameBridge.Instance.RequestReclaimBus(ctrl.busID, success =>
        {
            if (!success)
            {
                // Grace period expired (or someone else beat us to it) between opening this
                // window and clicking -- it'll simply drop out of the reclaimable list on the
                // next NetworkList sync. Nothing else to do here.
                Debug.LogWarning($"[ClientBusPickerWindow] Fleet #{fleetNumber} could no longer be reclaimed -- its grace period likely expired.");
                return;
            }

            try
            {
                PlayerHandoff.Instance.ResetForFreshPossession(busSim, fleetNumber, ctrl);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ClientBusPickerWindow] Failed to reclaim bus '{ctrl.name}': {e.GetType().Name}: {e.Message}");
            }
        });
    }

    private void TakeBus(NPCBusController ctrl)
    {
        if (PlayerHandoff.Instance == null || ctrl == null || NetworkGameBridge.Instance == null) return;
        var busSim = ctrl.GetComponentInChildren<BusSimulationController>(true);
        if (busSim == null) { Debug.LogWarning($"[ClientBusPickerWindow] '{ctrl.name}' has no BusSimulationController -- can't take it."); return; }

        int fleetNumber = ctrl.fleetNumber;
        // Ask the host FIRST -- another client could be grabbing this same bus in the same
        // instant. Only actually possess it locally if the host confirms nobody beat us to it;
        // otherwise leave the picker open (it'll drop off the list on the next NetworkList sync
        // once the winner's possession registers).
        NetworkGameBridge.Instance.RequestPossessBus(ctrl.busID, success =>
        {
            if (!success)
            {
                Debug.LogWarning($"[ClientBusPickerWindow] Fleet #{fleetNumber} was already taken by someone else.");
                return;
            }

            try
            {
                PlayerHandoff.Instance.ResetForFreshPossession(busSim, fleetNumber, ctrl);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ClientBusPickerWindow] Failed to take bus '{ctrl.name}': {e.GetType().Name}: {e.Message}");
            }
        });
    }
}
