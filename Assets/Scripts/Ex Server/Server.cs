using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using System.Diagnostics;

public class ServerWorldManager : MonoBehaviour
{
    public static ServerWorldManager Instance { get; private set; }
    public NetworkVariable<float> NetworkHeight = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<float> NetworkWaterHeight = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> NetworkSeed = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> NetworkChunkSize = new NetworkVariable<int>(8, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> NetworkSectionSize = new NetworkVariable<int>(16, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    
    public NetworkVariable<float> NetworkWaveFrequency = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    [Header("Network Port Management")]
    [SerializeField] private ushort preferredPort = 7777;
    
    private int maxRetries = 3;
    private int currentRetryCount = 0;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
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
        if (Application.dataPath.ToLower().Contains("clone") || 
            Application.dataPath.ToLower().Contains("client"))
        {
            isClientInstance = true;
        }

        if (isClientInstance)
        {
            UnityEngine.Debug.Log($"client; port {preferredPort}...");
            transport.ConnectionData.Port = preferredPort;
            NetworkManager.Singleton.StartClient();
        }
        else
        {
            UnityEngine.Debug.Log($"host; port {preferredPort}...");
            transport.ConnectionData.Port = preferredPort;
            NetworkManager.Singleton.StartHost();
        }
    }

    private void HandleTransportFailure()
    {
        bool isServerInstance = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        if (isServerInstance && currentRetryCount < maxRetries)
        {
            currentRetryCount++;
            UnityEngine.Debug.LogWarning($"failed port {preferredPort}.");

            KillPortOnMac(preferredPort);
            System.Threading.Thread.Sleep(200);

            UnityEngine.Debug.Log($"trying port {preferredPort}...");
            NetworkManager.Singleton.StartHost();
        }
        else if (currentRetryCount >= maxRetries)
        {
            UnityEngine.Debug.LogError("port taken.");
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
                UnityEngine.Debug.Log($"port {port}.");
            }
        }
        catch (System.Exception e)
        {
            UnityEngine.Debug.LogError($"mac port-kill{e.Message}");
        }
#endif
    }

    private void OnDisable() => CleanupSockets();
    private void OnApplicationQuit() => CleanupSockets();

    private void CleanupSockets()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();
        }
    }
}