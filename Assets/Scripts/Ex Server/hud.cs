using UnityEngine;
using Unity.Netcode;

public class NetcodeHUD : MonoBehaviour
{
    private bool hasBooted = false;

    void Update()
    {
        // Wait until we are past frame one, and ensure the NetworkManager singleton is active
        if (!hasBooted && NetworkManager.Singleton != null)
        {
            hasBooted = true;
            Debug.Log("🔌 Singleton found! Auto-Booting Network Manager as HOST...");
            NetworkManager.Singleton.StartHost();
        }
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(20, 20, 250, 100));
        if (NetworkManager.Singleton != null)
        {
            string status = NetworkManager.Singleton.IsListening ? "ONLINE (Host Active)" : "OFFLINE";
            GUILayout.Label($"Network Status: {status}");
        }
        else
        {
            GUILayout.Label("Network Status: INITIALIZING...");
        }
        GUILayout.EndArea();
    }
}