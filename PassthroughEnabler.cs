using UnityEngine;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class PassthroughEnabler : MonoBehaviour
{
    void Start()
    {
        var cam = GetComponent<Camera>();
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
        }

#if PICO_XR
        PXR_Manager.EnableVideoSeeThrough = true;
        Debug.Log("[Passthrough] PICO passthrough enabled.");
#else
        Debug.Log("[Passthrough] Camera configured (editor mode).");
#endif
    }

    void OnDestroy()
    {
#if PICO_XR
        PXR_Manager.EnableVideoSeeThrough = false;
#endif
    }
}
