using UnityEngine;

/// <summary>
/// Keeps a world-space Canvas in front of the user's head.
/// When the user turns more than <see cref="reTargetAngle"/> degrees away
/// from the canvas, the canvas smoothly lerps to the new forward direction.
/// </summary>
public class CanvasFollower : MonoBehaviour
{
    [Tooltip("Camera whose forward direction the canvas follows (usually the XR camera).")]
    public Camera targetCamera;

    [Tooltip("Distance in meters the canvas floats in front of the camera.")]
    public float followDistance = 2f;

    [Tooltip("Vertical offset from the camera (meters). Positive = above eye level.")]
    public float heightOffset = 0f;

    [Tooltip("Horizontal offset from the camera in meters. Positive = right side.")]
    public float rightOffset = 0f;

    [Tooltip("Angle (degrees) the user must turn before the canvas re-targets.")]
    public float reTargetAngle = 30f;

    [Tooltip("Lerp speed for smooth follow (higher = snappier).")]
    public float followSpeed = 3f;

    private Vector3 _targetPosition;
    private Quaternion _targetRotation;
    private bool _initialized;

    void LateUpdate()
    {
        if (targetCamera == null) return;

        Vector3 camPos = targetCamera.transform.position;
        Vector3 camFwd = targetCamera.transform.forward;
        // Project forward onto the horizontal plane so the canvas stays level
        Vector3 flatFwd = new Vector3(camFwd.x, 0f, camFwd.z).normalized;
        if (flatFwd.sqrMagnitude < 0.001f)
            flatFwd = new Vector3(targetCamera.transform.right.x, 0f, targetCamera.transform.right.z).normalized;
        Vector3 camRight = targetCamera.transform.right;
        Vector3 flatRight = new Vector3(camRight.x, 0f, camRight.z).normalized;
        if (flatRight.sqrMagnitude < 0.001f)
            flatRight = Vector3.Cross(Vector3.up, flatFwd).normalized;

        Vector3 desiredPos = camPos + flatFwd * followDistance + flatRight * rightOffset + Vector3.up * heightOffset;
        Quaternion desiredRot = Quaternion.LookRotation(flatFwd, Vector3.up);

        if (!_initialized)
        {
            _targetPosition = desiredPos;
            _targetRotation = desiredRot;
            transform.position = _targetPosition;
            transform.rotation = _targetRotation;
            _initialized = true;
            return;
        }

        // Re-target when user turns past the threshold
        float angle = Quaternion.Angle(_targetRotation, desiredRot);
        if (angle > reTargetAngle)
        {
            _targetPosition = desiredPos;
            _targetRotation = desiredRot;
        }

        // Smooth follow
        float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, _targetPosition, t);
        transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, t);
    }
}
