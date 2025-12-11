using UnityEngine;

[RequireComponent(typeof(Camera))]
public class MouseLook : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] float sensitivity = 200f;
    [SerializeField] float pitchLimit = 85f;
    [SerializeField] bool invertY = false;
    [SerializeField] bool startLocked = true;

    float yaw = 0f;   // horizontal rotation
    float pitch = 0f; // vertical rotation

    void Start()
    {
        pitch = transform.eulerAngles.x;
        yaw = transform.eulerAngles.y;
        SetCursorLocked(startLocked);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
            SetCursorLocked(false);
        if (Input.GetMouseButtonDown(0))
            SetCursorLocked(true);

        if (Cursor.lockState != CursorLockMode.Locked) return;

        float mx = Input.GetAxis("Mouse X");
        float my = Input.GetAxis("Mouse Y");

        float mul = sensitivity * Time.deltaTime;

        yaw += mx * mul;
        pitch += (invertY ? my : -my) * mul;
        pitch = Mathf.Clamp(pitch, -pitchLimit, pitchLimit);

        // Apply: use world yaw + local pitch (no roll)
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    // Optional: expose setters to change sensitivity/invert at runtime
    public void SetSensitivity(float s) => sensitivity = s;
    public void SetInvertY(bool inv) => invertY = inv;
}
