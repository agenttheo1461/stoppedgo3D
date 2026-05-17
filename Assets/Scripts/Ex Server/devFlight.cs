using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem; 

public class SandboxController : MonoBehaviour
{
    [Header("Flight Settings")]
    [SerializeField] private float normalSpeed = 20f;
    [SerializeField] private float fastSpeed = 60f;

    private bool isFlying = false;
    
    // UI input fields for coordinates
    private string inputX = "0";
    private string inputY = "0";
    private string inputZ = "0";

    private Rigidbody rb;
    private CharacterController cc;
    private Collider col;

    private void Start()
    {
        var networkObj = GetComponent<NetworkObject>();
        if (networkObj != null && !networkObj.IsOwner)
        {
            enabled = false;
            return;
        }

        rb = GetComponent<Rigidbody>();
        cc = GetComponent<CharacterController>();
        col = GetComponent<Collider>();
    }

    private void Update()
    {
        if (Keyboard.current == null) return;

        // Press 'F' to toggle flight mode
        if (Keyboard.current.fKey.wasPressedThisFrame)
        {
            isFlying = !isFlying;
            
            // Toggle physics engines so gravity/ground bounds don't freeze you
            TogglePlayerPhysics(!isFlying);

            Debug.Log(isFlying ? "🚀 Flight Mode Active (Mouse Unlocked)" : "🚶 Physics Mode Active");
        }

        if (isFlying)
        {
            HandleFlightMovement();
        }
    }

    private void HandleFlightMovement()
    {
        if (Keyboard.current == null) return;

        // Boost check
        bool isShiftHeld = Keyboard.current.leftShiftKey.isPressed;
        float currentSpeed = isShiftHeld ? fastSpeed : normalSpeed;

        Vector3 moveDir = Vector3.zero;

        // Absolute horizontal directional axes (Global World Grid)
        if (Keyboard.current.wKey.isPressed) moveDir.z += 1f;
        if (Keyboard.current.sKey.isPressed) moveDir.z -= 1f;
        if (Keyboard.current.dKey.isPressed) moveDir.x += 1f;
        if (Keyboard.current.aKey.isPressed) moveDir.x -= 1f;

        // Vertical axes
        if (Keyboard.current.spaceKey.isPressed) moveDir.y += 1f;
        if (Keyboard.current.leftCtrlKey.isPressed) moveDir.y -= 1f;

        // Apply clean position shifts directly to transform
        transform.position += moveDir.normalized * currentSpeed * Time.deltaTime;
    }

    private void TogglePlayerPhysics(bool enablePhysics)
    {
        if (rb != null)
        {
            rb.isKinematic = !enablePhysics;
            if (!enablePhysics) rb.linearVelocity = Vector3.zero; 
        }
        
        if (cc != null) cc.enabled = enablePhysics;
        if (col != null) col.enabled = enablePhysics;
    }

    private void OnGUI()
    {
        // Ubuntu Dark Aubergine Custom Skin
        GUIStyle panelStyle = new GUIStyle(GUI.skin.box);
        Texture2D backgroundTexture = new Texture2D(1, 1);
        backgroundTexture.SetPixel(0, 0, new Color(0.18f, 0.04f, 0.14f, 0.85f)); 
        backgroundTexture.Apply();
        panelStyle.normal.background = backgroundTexture;

        GUIStyle textStyle = new GUIStyle(GUI.skin.label);
        textStyle.normal.textColor = Color.white; 

        GUIStyle titleStyle = new GUIStyle(textStyle);
        titleStyle.fontStyle = FontStyle.Bold;

        // Terminal position box configuration
        GUILayout.BeginArea(new Rect(Screen.width - 240, 20, 220, 140), panelStyle);
        
        GUILayout.Label("💻 terminal@sandbox:~", titleStyle);
        GUILayout.Space(5);

        GUILayout.Label($"📍 Position: X:{Mathf.Round(transform.position.x)} Y:{Mathf.Round(transform.position.y)} Z:{Mathf.Round(transform.position.z)}", textStyle);
        GUILayout.Space(8);

        GUILayout.BeginHorizontal();
        GUILayout.Label("X:", textStyle, GUILayout.Width(15));
        inputX = GUILayout.TextField(inputX, GUILayout.Width(45));
        
        GUILayout.Label("Y:", textStyle, GUILayout.Width(15));
        inputY = GUILayout.TextField(inputY, GUILayout.Width(45));
        
        GUILayout.Label("Z:", textStyle, GUILayout.Width(15));
        inputZ = GUILayout.TextField(inputZ, GUILayout.Width(45));
        GUILayout.EndHorizontal();

        GUILayout.Space(8);

        if (GUILayout.Button("Warp Player", GUILayout.Height(24)))
        {
            if (float.TryParse(inputX, out float targetX) &&
                float.TryParse(inputY, out float targetY) &&
                float.TryParse(inputZ, out float targetZ))
            {
                Vector3 destination = new Vector3(targetX, targetY, targetZ);
                
                bool wasCcEnabled = (cc != null && cc.enabled);
                if (cc != null) cc.enabled = false;

                if (transform.root != null) transform.root.position = destination;
                else transform.position = destination;

                if (cc != null && wasCcEnabled) cc.enabled = true;
                
                Debug.Log($"🔮 Teleported cleanly to: {destination}");
            }
        }

        GUILayout.EndArea();
    }
}