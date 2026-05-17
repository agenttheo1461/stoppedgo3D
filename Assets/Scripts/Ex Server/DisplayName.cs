using UnityEngine;
using Unity.Netcode;
using Unity.Collections;

public class PlayerNetworkIdentity : NetworkBehaviour
{
    // NetworkVariables automatically synchronize values from server down to all clients
    public NetworkVariable<FixedString32Bytes> NetworkDisplayName = new NetworkVariable<FixedString32Bytes>("", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> NetworkPlayerLevel = new NetworkVariable<int>(1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    [Header("Overhead Nameplate Settings")]
    [SerializeField] private Vector3 offset = new Vector3(0f, 2.3f, 0f); // Position above character model
    
    private Transform mainCamTransform;
    private GUIStyle nameplateStyle;

    public override void OnNetworkSpawn()
    {
        if (Camera.main != null) mainCamTransform = Camera.main.transform;

        // If this instance owns this specific avatar character, tell the server who we are!
        if (IsOwner && AccountSystem.Instance != null && AccountSystem.Instance.IsLoggedIn)
        {
            string profileName = AccountSystem.Instance.CurrentPlayer.DisplayName;
            int profileLevel = AccountSystem.Instance.CurrentPlayer.PlayerLevel;

            SyncProfileDataServerRpc(profileName, profileLevel);
        }
    }

    [ServerRpc]
    private void SyncProfileDataServerRpc(string displayName, int playerLevel)
    {
        NetworkDisplayName.Value = displayName;
        NetworkPlayerLevel.Value = playerLevel;
    }

    private void Update()
    {
        // Fallback camera finder if main camera changes layers or spawns late
        if (mainCamTransform == null && Camera.main != null)
        {
            mainCamTransform = Camera.main.transform;
        }
    }

    // Draws a genuine world-space nameplate layout floating straight over the asset bounds
    private void OnGUI()
    {
        if (mainCamTransform == null || string.IsNullOrEmpty(NetworkDisplayName.Value.ToString())) return;

        // 👁️ Distance Check: Don't render text if the target is completely behind the camera viewport
        Vector3 targetWorldPos = transform.position + offset;
        Vector3 screenPos = Camera.main.WorldToScreenPoint(targetWorldPos);

        if (screenPos.z > 0) 
        {
            InitializeStyle();

            // Format layout print: "[Dev] Ted (LVL 1)"
            string nameplateText = $"{NetworkDisplayName.Value} (LVL {NetworkPlayerLevel.Value})";
            
            // Native UI coordinates start from top-left, Screen points start from bottom-left; flip Y axis
            float uiX = screenPos.x;
            float uiY = Screen.height - screenPos.y;

            // Draw text shadow for extreme high visibility tracking on any skybox template
            Rect labelRect = new Rect(uiX - 150, uiY, 300, 25);
            
            nameplateStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(labelRect.x + 1, labelRect.y + 1, labelRect.width, labelRect.height), nameplateText, nameplateStyle);
            
            // Draw core crisp color plate
            nameplateStyle.normal.textColor = IsDeveloper(NetworkDisplayName.Value.ToString()) ? Color.cyan : Color.white;
            GUI.Label(labelRect, nameplateText, nameplateStyle);
        }
    }

    private void InitializeStyle()
    {
        if (nameplateStyle != null) return;
        nameplateStyle = new GUIStyle();
        nameplateStyle.fontSize = 14;
        nameplateStyle.fontStyle = FontStyle.Bold;
        nameplateStyle.alignment = TextAnchor.MiddleCenter;
    }

    private bool IsDeveloper(string name)
    {
        return name.Contains("[Dev]");
    }
}