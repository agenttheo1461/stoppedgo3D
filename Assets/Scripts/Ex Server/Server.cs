using UnityEngine;
using Unity.Netcode; 

// FIX: Change NetworkBehaviour to MonoBehaviour so this runs BEFORE Netcode boots!
public class ServerWorldManager : MonoBehaviour
{
    public static ServerWorldManager Instance { get; private set; }

    // Keep your synced variables intact
    public NetworkVariable<int> NetworkSeed = new NetworkVariable<int>(1337);
    public NetworkVariable<int> NetworkSectionSize = new NetworkVariable<int>(8);
    public NetworkVariable<float> NetworkWaveFrequency = new NetworkVariable<float>(0.04f);
    public NetworkVariable<float> NetworkHeight = new NetworkVariable<float>(12f);
    public NetworkVariable<float> NetworkWaterHeight = new NetworkVariable<float>(-2.0f);

    private void Awake()
    {
        if (Instance != null && Instance != this) 
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        // Now this is guaranteed to run on both Main and Clone windows
        Invoke(nameof(SmartAutoLaunch), 0.5f);
    }

    private void SmartAutoLaunch()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("❌ Blocker: Could not find a NetworkManager component in the scene!");
            return;
        }

        bool isClone = Application.dataPath.ToLower().Contains("clone");

        if (isClone)
        {
            Debug.Log("🔌 Clone detected! Auto-joining as CLIENT...");
            NetworkManager.Singleton.StartClient();
        }
        else
        {
            Debug.Log("👑 Main Editor detected! Auto-starting as PLAYER 1 (HOST)...");
            NetworkManager.Singleton.StartHost();
        }
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 300, 100));
        if (NetworkManager.Singleton != null)
        {
            // Simple display to track state changes
            string role = "Offline/Idle";
            if (NetworkManager.Singleton.IsHost) role = "Host (Player 1)";
            else if (NetworkManager.Singleton.IsClient && NetworkManager.Singleton.IsConnectedClient) role = "Client (Joined)";
            else if (NetworkManager.Singleton.IsClient) role = "Client (Connecting...)";

            GUILayout.Label($"Network Role: {role}");
        }
        GUILayout.EndArea();
    }
}