using UnityEngine;

/// <summary>
/// Places a world-space Canvas either once in room space, or continuously in front of the user.
/// Default behavior is room-anchored so operators can walk to a fixed panel and press RECORD/STOP.
/// </summary>
public class CanvasFollower : MonoBehaviour
{
    public enum PlacementMode
    {
        AnchorInRoom = 0,
        FollowHead = 1,
    }

    [Tooltip("AnchorInRoom = place once and keep fixed. FollowHead = keep retargeting with head motion.")]
    public PlacementMode placementMode = PlacementMode.AnchorInRoom;

    [Tooltip("Camera whose forward direction the canvas follows (usually the XR camera).")]
    public Camera targetCamera;

    [Tooltip("Distance in meters from camera when initial anchor is computed (or while following).")]
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
    private Vector3 _anchoredPosition;
    private Quaternion _anchoredRotation;
    private bool _initialized;

    public void ReanchorNow()
    {
        _initialized = false;
    }

    void LateUpdate()
    {
        if (targetCamera == null)
            targetCamera = Camera.main;
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
            _anchoredPosition = _targetPosition;
            _anchoredRotation = _targetRotation;
            _initialized = true;
            return;
        }

        if (placementMode == PlacementMode.AnchorInRoom)
        {
            // Hard-lock transform so no other script/system drift can make UI follow the head.
            transform.position = _anchoredPosition;
            transform.rotation = _anchoredRotation;
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
