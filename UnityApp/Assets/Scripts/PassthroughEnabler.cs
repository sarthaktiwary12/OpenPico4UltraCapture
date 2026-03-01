using UnityEngine;

public class PassthroughEnabler : MonoBehaviour
{
    void Start()
    {
        // Set visible background for 2D panel mode
        var cam = GetComponent<Camera>();
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.15f, 1f);
        }
        Debug.Log("[Passthrough] Camera configured (2D panel mode).");
    }
}
