using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using System.Diagnostics;

public class ServerWorldManager : MonoBehaviour
{
    // Global static reference for Client.cs configuration access
    public static ServerWorldManager Instance { get; private set; }

    // =================================================================
    // 🌍 WORLD GENERATION NETWORKING PROPERTIES (Type Definitions Fixed)
    // =================================================================
    public NetworkVariable<float> NetworkHeight = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<float> NetworkWaterHeight = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    
    // 🛠️ FIX: Clean matching <int> type structure assignment
    public NetworkVariable<int> NetworkSeed = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    
    // 🛠️ FIX: Changed to <int> to match ClientWorldLoader's loops/arrays setup
    public NetworkVariable<int> NetworkChunkSize = new NetworkVariable<int>(8, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> NetworkSectionSize = new NetworkVariable<int>(16, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    
    public NetworkVariable<float> NetworkWaveFrequency = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // =================================================================
    // ⚙️ SYSTEM SETTINGS
    // =================================================================
    [Header("Network Port Management")]
    [SerializeField] private ushort preferredPort = 7777;
    
    private int maxRetries = 1;
    private int currentRetryCount = 0;

    private void Awake()
    {
        // Enforce safe Singleton runtime constraints
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        // Hook into the transport architecture failure event loop
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnTransportFailure += HandleTransportFailure;
        }

        SmartAutoLaunch();
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnTransportFailure -= HandleTransportFailure;
        }
    }

    private void SmartAutoLaunch()
    {
        if (NetworkManager.Singleton == null) return;

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport == null) return;

        bool isClientInstance = false;

        // Path validation mechanics to identify dynamic editor clones
        if (Application.dataPath.ToLower().Contains("clone") || 
            Application.dataPath.ToLower().Contains("client"))
        {
            isClientInstance = true;
        }

        if (isClientInstance)
        {
            UnityEngine.Debug.Log($"🔌 CLIENT: Connecting to port {preferredPort}...");
            transport.ConnectionData.Port = preferredPort;
            NetworkManager.Singleton.StartClient();
        }
        else
        {
            UnityEngine.Debug.Log($"👑 HOST: Attempting to bind port {preferredPort}...");
            transport.ConnectionData.Port = preferredPort;
            NetworkManager.Singleton.StartHost();
        }
    }

    private void HandleTransportFailure()
    {
        bool isServerInstance = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        // Run port purge routine if the hosting instance fails to bind the socket
        if (isServerInstance && currentRetryCount < maxRetries)
        {
            currentRetryCount++;
            UnityEngine.Debug.LogWarning($"💥 Transport failed on port {preferredPort}. Clearing macOS sockets...");

            KillPortOnMac(preferredPort);

            // Brief delay allowing the OS kernel network stack to drop the interface
            System.Threading.Thread.Sleep(200);

            UnityEngine.Debug.Log($"🔄 Retrying Host startup on cleaned port {preferredPort}...");
            NetworkManager.Singleton.StartHost();
        }
        else if (currentRetryCount >= maxRetries)
        {
            UnityEngine.Debug.LogError("❌ Port purge executed, but socket remains occupied by an external application process.");
        }
    }

    private void KillPortOnMac(ushort port)
    {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        try
        {
            // Execute background bash process to find and force-kill the ghost port PID
            ProcessStartInfo procInfo = new ProcessStartInfo();
            procInfo.FileName = "/bin/bash";
            procInfo.Arguments = $"-c \"kill -9 $(lsof -t -i:{port})\"";
            procInfo.RedirectStandardOutput = true;
            procInfo.RedirectStandardError = true;
            procInfo.UseShellExecute = false;
            procInfo.CreateNoWindow = true;

            using (Process process = Process.Start(procInfo))
            {
                process.WaitForExit();
                UnityEngine.Debug.Log($"🧼 Code-level network socket purge complete for port {port}.");
            }
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogError($"⚠️ Failed to execute system port-kill process sequence: {e.Message}");
        }
#endif
    }

    private void OnDisable() => CleanupSockets();
    private void OnApplicationQuit() => CleanupSockets();

    private void CleanupSockets()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            UnityEngine.Debug.Log("🧼 Force flushing active local host ports...");
            NetworkManager.Singleton.Shutdown();
        }
    }
}