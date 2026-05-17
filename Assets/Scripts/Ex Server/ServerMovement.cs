using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

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

    private Vector3 velocity;
    private bool isGrounded;

    public override void OnNetworkSpawn()
    {
        // If this avatar belongs to the human sitting at this monitor
        if (IsOwner)
        {
            if (Camera.main != null) cam = Camera.main.transform;
            
            // Warp player above ground to prevent spawning trapped inside a hill
            transform.position = new Vector3(transform.position.x, 25f, transform.position.z);
        }
    }

    void Update()
    {
        // SEPARATION: Immediately drop updates if this isn't our local controlled player
        if (!IsOwner) return;

        isGrounded = controller.isGrounded;
        if (IsOwner && isGrounded && velocity.y < 0)
        {
            velocity.y = -2f; 
        }

        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // Rotation Input
        float rotationInput = 0f;
        if (IsOwner && keyboard.dKey.isPressed) rotationInput += 1f;
        if (IsOwner && keyboard.aKey.isPressed) rotationInput -= 1f;
        transform.Rotate(0, rotationInput * rotationSpeed * Time.deltaTime, 0);

        // Forward/Backward Input
        float moveInput = 0f;
        if (IsOwner && keyboard.wKey.isPressed) moveInput += 1f;
        if (IsOwner && keyboard.sKey.isPressed) moveInput -= 1f;

        Vector3 move = transform.forward * moveInput;
        controller.Move(move * speed * Time.deltaTime);

        // Jumping
        if (IsOwner && keyboard.spaceKey.wasPressedThisFrame && isGrounded)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

        HandleCamera();
    }

    void HandleCamera()
    {
        if (cam == null) return;

        if (IsOwner && cameraMode == CameraMode.FirstPerson)
        {
            cam.position = transform.position + transform.TransformDirection(new Vector3(0, 1.5f, 0));
            cam.rotation = transform.rotation;
        }
        else if (IsOwner && cameraMode == CameraMode.ThirdPerson)
        {
            Vector3 desiredPosition = transform.position + transform.TransformDirection(camOffset);
            cam.position = Vector3.Lerp(cam.position, desiredPosition, camSmooth * Time.deltaTime);
            cam.LookAt(transform.position + Vector3.up * 1.5f);
        }
    }
}