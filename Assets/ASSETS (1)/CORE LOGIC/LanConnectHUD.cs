using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  LAN CONNECT HUD  —  Multiplayer Phase 1
//
//  The whole "connect two devices" surface for same-WiFi testing: no Unity
//  Gaming Services account, no Relay, no login -- direct IP over LAN via
//  Unity Transport. Host taps HOST, reads its own LAN IP (or the short join
//  code below it) off this HUD, client types either one and taps JOIN.
//
//  Join code: a 6-character alphanumeric code encoding just the LAST TWO
//  octets of the host's LAN IP (e.g. the ".1.42" in "192.168.1.42"). The
//  first two octets are assumed to match on the joining device too, since
//  that's the network prefix -- true for any two devices on the same home
//  WiFi, which is the whole point of LAN testing. No server/relay involved;
//  it's purely a shorter way to type the same IP. The raw-IP field still
//  works exactly as before as a fallback for anyone on an unusual subnet
//  where that assumption doesn't hold.
//
//  Lives on the "NetworkManager" scene object (see the phase-1 scene setup
//  instructions) alongside NetworkManager + Unity Transport. Needs both:
//  reads `_transport` to set connection data, reads NetworkManager.Singleton
//  to start host/client and report connection state.
//
//  Deliberately OnGUI, matching every other window in this project
//  (MDT_UITheme etc.) instead of a Canvas/UI Toolkit screen -- no prefab or
//  Canvas setup needed, same convention as TrackerUI/AlertsCenterWindow.
//
//  Compile-verified against the real installed NGO 2.2.0 package.
//
//  [FIX 2026-09-29] NetworkConfig.EnableSceneManagement defaults to true, and
//  NGO's scene-sync machinery (real methods confirmed by reading
//  NetworkSceneManager.cs: OnClientUnloadScene, SynchronizeNetworkObjects,
//  UnloadSceneAsync) assumes clients start in an empty/lobby-style scene and
//  get told what to load by the server. This project's client is instead
//  ALREADY sitting in a fully-loaded world (roads, city, the whole fleet)
//  before it ever tries to connect -- confirmed root cause of "the map and
//  every other GameObject is gone" after a connection attempt. Disabled
//  entirely: this project only ever wants specific NetworkObjects (the
//  buses) synced, never scene loading/unloading -- each side keeps its own
//  independently-loaded world completely untouched by networking.
// ═══════════════════════════════════════════════════════════════════════════════
public class LanConnectHUD : MonoBehaviour
{
    [Tooltip("Auto-found via GetComponent if left blank.")]
    public UnityTransport transport;

    [Tooltip("Port both devices connect on. Must match on host and client -- it does automatically here since both read this same field, but if you type an IP manually elsewhere, use this port.")]
    public ushort port = 7777;

    [Tooltip("The NetworkGameBridge PREFAB asset (not a scene instance -- see this field's own comment below for why).")]
    public GameObject networkGameBridgePrefab;

    // [FIX 2026-09-29] A scene-PLACED NetworkGameBridge hit the exact same "[Netcode] Failed to
    // create object locally -- NetworkPrefab could not be found" error the SYSTEMS GameObject did
    // earlier. Root cause, now understood properly: with EnableSceneManagement disabled (see this
    // class's own header comment for why that's required), NGO has no client-side scene-load event
    // to hang its normal "match an incoming spawn to an already-existing LOCAL scene object by
    // hash" reconciliation on -- so EVERY object in the initial ConnectionApprovedMessage sync,
    // scene-placed or not, gets routed through the DYNAMIC prefab-spawn path instead, which needs a
    // registered NetworkPrefab (a real project asset) to instantiate from. A scene-only GameObject
    // has no such asset, so it can never resolve. Fix: NetworkGameBridge is instantiated at runtime
    // from a real prefab and spawned exactly once, by the host, after StartHost succeeds -- the
    // SAME pattern buses used before today's rebuild, just for this one singleton object instead of
    // the whole fleet (so none of the old hash-collision/scene-sweep risk that came with sharing
    // one prefab across ~400 instances applies here).

    private string _joinIp = "";
    private string _statusLine = "";

    // [ADD] A join attempt that fails (timeout, refused, wrong IP, host not listening) used to be
    // totally silent -- StartClient() returning true only means the transport is ATTEMPTING to
    // connect, not that it succeeded; the actual result arrives later via
    // OnClientDisconnectCallback/OnTransportFailure (confirmed against the real installed NGO
    // 2.2.0 source: NetworkManager.cs's own doc comments state both fire on the local client
    // itself, not just server-side). Neither callback was ever subscribed to before, so a failed
    // join just looked like nothing happened.
    private bool _callbacksHooked;
    private bool _joinInFlight;

    private Rect _windowRect = new Rect(20, 20, 300, 0);
    private GUIStyle _lblTitle, _lblBody, _lblDim, _btn, _field;
    private bool _stylesReady;

    private void Awake()
    {
        if (transport == null) transport = GetComponent<UnityTransport>();
    }

    /// <summary>Lazily subscribes instead of doing it in Awake() -- NetworkManager.Singleton is
    /// set in NetworkManager's OWN Awake(), which lives on this same GameObject with no guaranteed
    /// execution order relative to this component's Awake(). Called from OnGUI (cheap, guarded by
    /// the bool) and at the start of both Host/Join coroutines so it's hooked well before it could
    /// ever matter.</summary>
    private void EnsureCallbacksHooked()
    {
        if (_callbacksHooked) return;
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        nm.OnClientDisconnectCallback += HandleClientDisconnect;
        nm.OnTransportFailure += HandleTransportFailure;
        _callbacksHooked = true;
    }

    private void OnDestroy()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !_callbacksHooked) return;
        nm.OnClientDisconnectCallback -= HandleClientDisconnect;
        nm.OnTransportFailure -= HandleTransportFailure;
    }

    /// <summary>Fires on the server (for a remote client disconnecting) AND on a client's own
    /// disconnect -- including a failed/timed-out connection attempt, per NGO's own doc comment on
    /// OnClientDisconnectCallback.</summary>
    private void HandleClientDisconnect(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (nm.IsServer)
        {
            // A remote client dropped (crash, network loss, explicit disconnect) without
            // necessarily calling NetworkGameBridge.RequestReleaseBus first -- release whatever
            // bus it was possessing so it goes back to normal NPC AI instead of being stuck
            // disabled forever (see NetworkGameBridge.RequestPossessBusServerRpc's own comment on
            // why a possessed bus has its AI disabled host-side).
            NetworkGameBridge.Instance?.ReleaseOnDisconnect(clientId);
            return;
        }

        _joinInFlight = false;
        string reason = string.IsNullOrEmpty(nm.DisconnectReason)
            ? "no reason given by the host -- could be a firewall, the host not actually listening, a wrong IP/port, or (on macOS) Local Network permission blocking one of the two apps"
            : nm.DisconnectReason;
        _statusLine = $"Join failed/disconnected: {reason}";
        Debug.LogWarning($"[LanConnectHUD] Client disconnected: {reason}");
    }

    private void HandleTransportFailure()
    {
        _joinInFlight = false;
        _statusLine = "Join failed: transport-level failure -- couldn't reach the host at all (check IP/port, firewall, and that both devices/apps are actually on the same network).";
        Debug.LogWarning("[LanConnectHUD] OnTransportFailure fired.");
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle = MDT_UITheme.MakeLabel(14, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblBody  = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _lblDim   = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _btn      = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 12);
        _field    = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
    }

    private void OnGUI()
    {
        EnsureStyles();
        EnsureCallbacksHooked();
        var nm = NetworkManager.Singleton;
        bool connected = nm != null && (nm.IsHost || nm.IsClient) && nm.IsConnectedClient;
        bool startingUp = nm != null && (nm.IsHost || nm.IsClient) && !nm.IsConnectedClient;

        bool showHostCode = connected && nm != null && nm.IsHost;
        float h = connected ? (showHostCode ? 158f : 120f) : startingUp ? 120f : 214f;
        _windowRect.height = h;

        MDT_UITheme.DrawSoftShadow(_windowRect, 14f);
        MDT_UITheme.DrawRoundedRectBordered(_windowRect, 12f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        float x = _windowRect.x + 12, y = _windowRect.y + 10, w = _windowRect.width - 24;
        GUI.Label(new Rect(x, y, w, 20), "LAN CONNECT", _lblTitle);
        y += 24;

        if (nm == null)
        {
            GUI.Label(new Rect(x, y, w, 40), "No NetworkManager found in the scene.", _lblDim);
            return;
        }

        if (connected)
        {
            string role = nm.IsHost ? "HOST" : "CLIENT";
            GUI.Label(new Rect(x, y, w, 18), $"Connected as {role} — {nm.ConnectedClientsIds.Count} player(s).", _lblBody);
            y += 24;

            // Kept visible the whole session, not just pre-connect -- a third player (or anyone
            // who missed it the first time) can still read it off without the host disconnecting.
            if (showHostCode)
            {
                string hostIp = GetLocalIPAddress();
                string hostCode = TryGetLastTwoOctets(hostIp, out byte hb3, out byte hb4) ? EncodeJoinCode(hb3, hb4) : "------";
                GUI.Label(new Rect(x, y, w, 16), $"Join code: {hostCode}  (IP: {hostIp})", _lblDim);
                y += 22;
            }

            if (GUI.Button(new Rect(x, y, w, 26), "Disconnect", _btn))
            {
                nm.Shutdown();
                _statusLine = "Disconnected.";
            }
            y += 32;
            GUI.Label(new Rect(x, y, w, 16), _statusLine, _lblDim);
            return;
        }

        if (startingUp)
        {
            GUI.Label(new Rect(x, y, w, 18), "Connecting...", _lblBody);
            y += 26;
            if (GUI.Button(new Rect(x, y, w, 26), "Cancel", _btn))
            {
                nm.Shutdown();
                _statusLine = "Cancelled.";
                _joinInFlight = false;
            }
            return;
        }

        // ── Not connected: host or join ─────────────────────────────────
        string localIp = GetLocalIPAddress();
        GUI.Label(new Rect(x, y, w, 16), $"This device's LAN IP: {localIp}", _lblDim);
        y += 18;
        string myCode = TryGetLastTwoOctets(localIp, out byte b3, out byte b4) ? EncodeJoinCode(b3, b4) : "------";
        GUI.Label(new Rect(x, y, w, 16), $"Join code: {myCode}", _lblBody);
        y += 24;

        if (GUI.Button(new Rect(x, y, w, 28), "HOST (start here on the Mac)", _btn))
            StartHost();
        y += 34;

        GUI.Label(new Rect(x, y, w, 16), "— or join a host —", _lblDim);
        y += 20;

        _joinIp = GUI.TextField(new Rect(x, y, w, 24), _joinIp, _field);
        if (string.IsNullOrEmpty(_joinIp)) GUI.Label(new Rect(x + 6, y + 4, w - 12, 16), "join code (e.g. K7X9QM) or full IP", _lblDim);
        y += 30;

        if (GUI.Button(new Rect(x, y, w, 28), "JOIN (code or IP above)", _btn))
            StartClient(_joinIp);
        y += 34;

        GUI.Label(new Rect(x, y, w, 32), _statusLine, _lblDim);
    }

    private void StartHost() => StartCoroutine(EnsureCleanShutdownThen(DoStartHostCoroutine()));

    private void StartClient(string input) => StartCoroutine(EnsureCleanShutdownThen(DoStartClientCoroutine(input)));

    /// <summary>[FIX] Repro confirmed: fail a Join, then try Host again in the SAME running
    /// session (no app relaunch) -- buses end up with destroyed Rigidbodies on BOTH sides. A
    /// connection attempt that fails can leave NetworkManager still considering itself
    /// "listening" even though nothing usable is happening, so the next Host/Join attempt was
    /// starting on top of leftover state instead of a clean one. This forces an explicit
    /// Shutdown() and waits for NGO's own teardown to fully finish (ShutdownInProgress) before
    /// ever starting a new attempt.</summary>
    private IEnumerator EnsureCleanShutdownThen(IEnumerator next)
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsListening || nm.ShutdownInProgress))
        {
            _statusLine = "Cleaning up previous session...";
            nm.Shutdown();
            while (nm.ShutdownInProgress) yield return null;
        }
        yield return StartCoroutine(next);
    }

    /// <summary>[FIX 2] ShutdownInProgress clearing means NGO's own C#-side teardown finished --
    /// it does NOT mean the underlying OS socket has actually been released back to the system
    /// yet. Confirmed by repro: retrying Host right after ShutdownInProgress cleared hit
    /// "Address already in use" identically on every attempt, including several manual re-clicks
    /// in a row. A single extra frame (the previous fix) isn't nearly enough real time for that;
    /// this retries with an actual paced delay instead of requiring you to keep re-clicking HOST
    /// yourself.</summary>
    private IEnumerator DoStartHostCoroutine()
    {
        if (transport == null) { _statusLine = "No Unity Transport assigned."; yield break; }
        DisableSceneManagement(); // see DisableSceneManagement's own comment -- this is what was wiping out the map/world
        transport.ConnectionData.Address = "0.0.0.0"; // listen on all local interfaces
        transport.ConnectionData.Port = port;

        const int maxAttempts = 6;
        bool started = false;
        for (int attempt = 1; attempt <= maxAttempts && !started; attempt++)
        {
            try
            {
                started = NetworkManager.Singleton.StartHost();
            }
            catch (System.Exception e)
            {
                _statusLine = $"EXCEPTION: {e.GetType().Name}: {e.Message}";
                Debug.LogException(e);
                yield break;
            }

            if (!started && attempt < maxAttempts)
            {
                _statusLine = $"Port busy, retrying... ({attempt}/{maxAttempts})";
                yield return new WaitForSeconds(0.5f);
            }
        }

        _statusLine = started
            ? "Host started."
            : $"Host failed to start after {maxAttempts} attempts -- port {port} may still be held by another process (or a previous run of this app). Try fully quitting and relaunching.";

        if (started) SpawnNetworkGameBridge();
    }

    /// <summary>Host-only: registers the NetworkGameBridge prefab (if not already) and spawns the
    /// one instance for this session. See the field's own comment above for why this can't just be
    /// a scene-placed object.</summary>
    private void SpawnNetworkGameBridge()
    {
        if (networkGameBridgePrefab == null)
        {
            Debug.LogError("[LanConnectHUD] networkGameBridgePrefab is not assigned -- multiplayer state sync (positions, possession, relief/etc RPCs) will not work at all. Assign the NetworkGameBridge prefab in the Inspector.");
            return;
        }
        var nm = NetworkManager.Singleton;
        if (!nm.NetworkConfig.Prefabs.Contains(networkGameBridgePrefab))
            nm.AddNetworkPrefab(networkGameBridgePrefab);

        var instance = Instantiate(networkGameBridgePrefab);
        instance.GetComponent<NetworkObject>().Spawn();

        RegisterAlreadyPossessedBus();
    }

    /// <summary>[FIX] A player who was ALREADY driving a bus before ever pressing Host/Join (the
    /// normal, common case -- single-player-style, then deciding to host/join) had that
    /// possession set up while NetworkGameBridge.Instance was still null, so the fire-and-forget
    /// RequestPossessBus call in PlayerHandoff.SetPlayerBus/ResetForFreshPossession silently
    /// no-op'd -- there was nothing to call yet. Nothing ever went back afterward and registered
    /// it once networking actually started, so that bus just kept running as an ordinary,
    /// un-suppressed NPC from the network's point of view forever -- explains "still marked as
    /// NPC" on whichever side possessed their bus before connecting. Call this right after
    /// NetworkGameBridge actually exists: host, right after spawning it; client, right after the
    /// connection succeeds (see DoStartClientCoroutine).</summary>
    private void RegisterAlreadyPossessedBus()
    {
        var ph = PlayerHandoff.Instance;
        if (ph == null || ph.playerBus == null) return;
        int busID = ph.PossessedPhysicalBusID;
        if (busID < 0) return;
        NetworkGameBridge.Instance?.RequestPossessBus(busID, null);
    }

    /// <summary>Client-only counterpart -- NetworkGameBridge takes a moment to actually replicate
    /// to a newly-connected client after IsConnectedClient flips true (it's spawned by the host,
    /// then synced over), so calling RegisterAlreadyPossessedBus() in that same instant would race
    /// a null NetworkGameBridge.Instance. Waits (briefly) for it to actually exist first.</summary>
    private IEnumerator RegisterAlreadyPossessedBusWhenReady()
    {
        float waited = 0f;
        while (NetworkGameBridge.Instance == null && waited < 5f)
        {
            waited += Time.deltaTime;
            yield return null;
        }
        RegisterAlreadyPossessedBus();
    }

    private IEnumerator DoStartClientCoroutine(string input)
    {
        if (transport == null) { _statusLine = "No Unity Transport assigned."; yield break; }
        if (string.IsNullOrWhiteSpace(input)) { _statusLine = "Type a join code or the host's LAN IP first."; yield break; }
        EnsureCallbacksHooked();
        DisableSceneManagement(); // see DisableSceneManagement's own comment -- this is what was wiping out the map/world

        // [CHANGE] A client now keeps its own full local fleet, spawned identically to the host at
        // scene Start -- see NetworkGameBridge's header comment for why that's safe (deterministic
        // busID assignment from the same FleetRosterData, nothing cloned/destroyed over the
        // network). Nothing to tear down here any more.

        // The client must have the SAME prefab registered before connecting -- this is what lets it
        // resolve the host's NetworkGameBridge spawn message by hash instead of hitting "NetworkPrefab
        // could not be found" (see SpawnNetworkGameBridge's own comment on the field above).
        if (networkGameBridgePrefab != null && !NetworkManager.Singleton.NetworkConfig.Prefabs.Contains(networkGameBridgePrefab))
            NetworkManager.Singleton.AddNetworkPrefab(networkGameBridgePrefab);

        string ip = ResolveJoinInput(input);
        transport.ConnectionData.Address = ip;
        transport.ConnectionData.Port = port;

        bool startedOk;
        try
        {
            startedOk = NetworkManager.Singleton.StartClient();
        }
        catch (System.Exception e)
        {
            _statusLine = $"EXCEPTION: {e.GetType().Name}: {e.Message}";
            Debug.LogException(e);
            yield break;
        }

        if (!startedOk)
        {
            _statusLine = "Client failed to start.";
            yield break;
        }

        _statusLine = $"Connecting to {ip}...";
        _joinInFlight = true;

        // [ADD] Watchdog. OnClientDisconnectCallback/OnTransportFailure SHOULD fire on a failed
        // connection (confirmed against NGO's real source), but this guarantees the attempt still
        // resolves even if neither does for some reason -- instead of "Connecting..." (and a
        // torn-down local fleet) hanging forever with zero feedback, which is exactly what was
        // reported: it just silently drops back to the join screen after a while with no error.
        const float timeoutSeconds = 15f;
        float elapsed = 0f;
        var nm = NetworkManager.Singleton;
        while (_joinInFlight && elapsed < timeoutSeconds)
        {
            if (nm.IsConnectedClient)
            {
                _joinInFlight = false;
                _statusLine = "Connected.";
                StartCoroutine(RegisterAlreadyPossessedBusWhenReady());
                yield break;
            }
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (_joinInFlight)
        {
            _joinInFlight = false;
            _statusLine = $"Join failed: timed out after {timeoutSeconds:0}s with no response from {ip}. " +
                          "Check the IP/port, that the host is actually listening, any firewall, and (on macOS) " +
                          "Local Network permission for BOTH the Editor and the built app in " +
                          "System Settings > Privacy & Security > Local Network.";
            Debug.LogWarning($"[LanConnectHUD] Join attempt to {ip} timed out client-side after {timeoutSeconds}s with no disconnect/failure callback either.");
            if (nm.IsListening || nm.ShutdownInProgress) nm.Shutdown();
        }
    }

    /// <summary>A raw IP (contains a '.') is used as-is. Anything else is treated as a 6-character
    /// join code and decoded into the last two octets, prefixed with THIS device's own detected LAN
    /// network prefix -- correct as long as both devices are on the same home WiFi, which is the
    /// premise LAN testing already depends on. Falls back to the raw input if it can't decode as a
    /// code either, so the error you get is "couldn't connect to X" rather than a silent no-op.</summary>
    private static string ResolveJoinInput(string input)
    {
        input = input.Trim();
        if (input.Contains(".")) return input;

        if (TryDecodeJoinCode(input, out byte b3, out byte b4))
        {
            string localIp = GetLocalIPAddress();
            var parts = localIp.Split('.');
            if (parts.Length == 4) return $"{parts[0]}.{parts[1]}.{b3}.{b4}";
        }
        return input;
    }

    private const string JoinCodeAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static string EncodeJoinCode(byte octet3, byte octet4)
    {
        int value = (octet3 << 8) | octet4;
        var chars = new char[6];
        for (int i = 5; i >= 0; i--)
        {
            chars[i] = JoinCodeAlphabet[value % 36];
            value /= 36;
        }
        return new string(chars);
    }

    /// <summary>Must be called before StartHost()/StartClient() -- NetworkConfig can't be changed
    /// once the NetworkManager is listening. See the class header comment for why this exists.</summary>
    private void DisableSceneManagement()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.NetworkConfig != null)
            nm.NetworkConfig.EnableSceneManagement = false;
    }

    private static bool TryDecodeJoinCode(string code, out byte octet3, out byte octet4)
    {
        octet3 = 0; octet4 = 0;
        code = code.Trim().ToUpperInvariant();
        if (code.Length == 0 || code.Length > 8) return false;

        long value = 0;
        foreach (char c in code)
        {
            int digit = JoinCodeAlphabet.IndexOf(c);
            if (digit < 0) return false; // not a valid join-code character at all -- treat as raw input instead
            value = value * 36 + digit;
        }
        if (value < 0 || value > 65535) return false;

        octet3 = (byte)((value >> 8) & 0xFF);
        octet4 = (byte)(value & 0xFF);
        return true;
    }

    private static bool TryGetLastTwoOctets(string ip, out byte octet3, out byte octet4)
    {
        octet3 = 0; octet4 = 0;
        var parts = ip.Split('.');
        if (parts.Length != 4) return false;
        return byte.TryParse(parts[2], out octet3) && byte.TryParse(parts[3], out octet4);
    }

    /// <summary>First non-loopback IPv4 address on this machine -- good enough for "read this off
    /// the Mac, type it into the phone" on a typical home WiFi. If a device has multiple adapters
    /// (WiFi + Ethernet, a VPN, etc.) and picks the wrong one, override manually by typing the
    /// correct IP into the join field on the other device regardless of what this displays.</summary>
    private static string GetLocalIPAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var addr = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return addr != null ? addr.ToString() : "unknown";
        }
        catch { return "unknown"; }
    }
}
