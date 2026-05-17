using UnityEngine;
using UnityEngine.InputSystem;

public class Movement : MonoBehaviour
{
    public enum CameraMode { FirstPerson, ThirdPerson }

    [Header("References")]
    public CharacterController controller;
    public Transform cam;

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

    void Update()
    {
        isGrounded = controller.isGrounded;
        if (isGrounded && velocity.y < 0)
        {
            velocity.y = -2f; 
        }
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        float rotationInput = 0f;
        if (keyboard.dKey.isPressed) rotationInput += 1f;
        if (keyboard.aKey.isPressed) rotationInput -= 1f;
        
        transform.Rotate(0, rotationInput * rotationSpeed * Time.deltaTime, 0);
        float moveInput = 0f;
        if (keyboard.wKey.isPressed) moveInput += 1f;
        if (keyboard.sKey.isPressed) moveInput -= 1f;

        Vector3 move = transform.forward * moveInput;
        controller.Move(move * speed * Time.deltaTime);

        if (keyboard.spaceKey.wasPressedThisFrame && isGrounded)
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