using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;
using Unity.Collections;
using System.Collections.Generic;

public class ServerMovement : NetworkBehaviour
{
    public enum CameraMode { FirstPerson, ThirdPerson }

    [Header("References")]
    [SerializeField] private CharacterController controller;
    private Transform cam; 

    [Header("Camera Settings")]
    public CameraMode cameraMode = CameraMode.ThirdPerson;
    public Vector3 camOffset = new Vector3(0, 1.5f, -3f);
    public float camSmooth = 10f;

    [Header("Movement Settings")]
    public float speed = 5f;
    public float rotationSpeed = 150f;
    public float jumpHeight = 1.5f;
    public float gravity = -9.81f;
    [SerializeField] private List<string> developerNames = new List<string> { "AgentTheo" };

    private Vector3 velocity;
    private bool isGrounded;

    private string cachedLocalName = "debug/127.7777...";
    private int cachedLocalLevel = 0;
    private bool hasSyncedProfile = false;

    public NetworkVariable<FixedString64Bytes> NetworkDisplayName = new NetworkVariable<FixedString64Bytes>("", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> NetworkPlayerLevel = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private GUIStyle nameplateStyle;

    public override void OnNetworkSpawn()
    {
        NetworkDisplayName.OnValueChanged += OnNetworkNameChanged;
        NetworkPlayerLevel.OnValueChanged += OnNetworkLevelChanged;

        if (IsOwner)
        {
            if (Camera.main != null) cam = Camera.main.transform;
            transform.position = new Vector3(transform.position.x, 25f, transform.position.z);
        }
    }

    public override void OnNetworkDespawn()
    {
        NetworkDisplayName.OnValueChanged -= OnNetworkNameChanged;
        NetworkPlayerLevel.OnValueChanged -= OnNetworkLevelChanged;
    }

    public void InitializeNameplate(string displayName, int playerLevel)
    {
        cachedLocalName = displayName;
        cachedLocalLevel = playerLevel;

        if (IsSpawned && IsOwner)
        {
            SyncProfileDataServerRpc(cachedLocalName, cachedLocalLevel);
        }

        hasSyncedProfile = true; 
    }

    void Update()
    {
        if (!hasSyncedProfile && AccountSystem.Instance != null && AccountSystem.Instance.IsLoggedIn)
        {
            cachedLocalName = AccountSystem.Instance.CurrentPlayer.DisplayName;
            cachedLocalLevel = AccountSystem.Instance.CurrentPlayer.PlayerLevel;
            
            if (IsSpawned && IsOwner)
            {
                SyncProfileDataServerRpc(cachedLocalName, cachedLocalLevel);
            }

            hasSyncedProfile = true; 
        }

        if (!IsOwner) return;

        isGrounded = controller.isGrounded;
        if (isGrounded && velocity.y < 0)
        {
            velocity.y = -2f; 
        }

        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Rotation Input
        float rotationInput = 0f;
        if (keyboard.dKey.isPressed) rotationInput += 1f;
        if (keyboard.aKey.isPressed) rotationInput -= 1f;
        transform.Rotate(0, rotationInput * rotationSpeed * Time.deltaTime, 0);

        // Forward/Backward Input
        float moveInput = 0f;
        if (keyboard.wKey.isPressed) moveInput += 1f;
        if (keyboard.sKey.isPressed) moveInput -= 1f;

        Vector3 move = transform.forward * moveInput;
        controller.Move(move * speed * Time.deltaTime);

        // Jumping
        if (keyboard.spaceKey.wasPressedThisFrame && isGrounded)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

        HandleCamera();
    }

    private void OnGUI()
    {
        string currentName = !string.IsNullOrEmpty(cachedLocalName) && cachedLocalName != "loading..."
            ? cachedLocalName 
            : (IsSpawned ? NetworkDisplayName.Value.ToString() : cachedLocalName);

        int currentLevel = cachedLocalLevel != 0 
            ? cachedLocalLevel 
            : (IsSpawned ? NetworkPlayerLevel.Value : cachedLocalLevel);

        if (string.IsNullOrEmpty(currentName)) return;

        Camera currentCam = cam != null ? cam.GetComponent<Camera>() : Camera.main;
        if (currentCam == null) return;

        Vector3 targetWorldPos = transform.position + new Vector3(0f, 1.7f, 0f); 
        Vector3 screenPos = currentCam.WorldToScreenPoint(targetWorldPos);

        if (screenPos.z > 0) 
        {
            if (nameplateStyle == null)
            {
                nameplateStyle = new GUIStyle();
                nameplateStyle.fontSize = 14;
                nameplateStyle.fontStyle = FontStyle.Bold;
                nameplateStyle.alignment = TextAnchor.MiddleCenter;
            }

            string cleanUsername = currentName.Replace("[Dev]", "").Trim();
            bool isDeveloper = developerNames.Exists(name => name.Equals(cleanUsername, System.StringComparison.OrdinalIgnoreCase));

            string finalDisplayString = isDeveloper ? $"[Dev] {cleanUsername}" : currentName;
            string nameplateText = $"{finalDisplayString} (LVL {currentLevel})";

            float uiX = screenPos.x;
            float uiY = Screen.height - screenPos.y; 

            Rect labelRect = new Rect(uiX - 150, uiY, 300, 25);
            
            nameplateStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(labelRect.x + 1, labelRect.y + 1, labelRect.width, labelRect.height), nameplateText, nameplateStyle);
            
            if (currentName == "Loading...")
            {
                nameplateStyle.normal.textColor = Color.gray;
            }
            else if (isDeveloper)
            {
                nameplateStyle.normal.textColor = Color.cyan;
            }
            else
            {
                nameplateStyle.normal.textColor = Color.white;
            }

            GUI.Label(labelRect, nameplateText, nameplateStyle);
        }
    }

    private void OnNetworkNameChanged(FixedString64Bytes oldVal, FixedString64Bytes newVal) => cachedLocalName = newVal.ToString();
    private void OnNetworkLevelChanged(int oldVal, int newVal) => cachedLocalLevel = newVal;

    [ServerRpc]
    private void SyncProfileDataServerRpc(string displayName, int playerLevel)
    {
        NetworkDisplayName.Value = displayName;
        NetworkPlayerLevel.Value = playerLevel;
    }

    void HandleCamera()
    {
        if (cam == null) return;

        if (cameraMode == CameraMode.FirstPerson)
        {
            cam.position = transform.position + transform.TransformDirection(new Vector3(0, 1.5f, 0));
            cam.rotation = transform.rotation;
        }
        else if (cameraMode == CameraMode.ThirdPerson)
        {
            Vector3 desiredPosition = transform.position + transform.TransformDirection(camOffset);
            cam.position = Vector3.Lerp(cam.position, desiredPosition, camSmooth * Time.deltaTime);
            cam.LookAt(transform.position + Vector3.up * 1.5f);
        }
    }
}