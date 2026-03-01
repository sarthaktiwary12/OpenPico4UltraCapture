using System.Collections;
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
        StartCoroutine(EnablePassthrough());
#else
        Debug.Log("[Passthrough] Camera configured (editor mode).");
#endif
    }

#if PICO_XR
    IEnumerator EnablePassthrough()
    {
        // Wait a frame for XR to fully initialize
        yield return null;

        // Disable guardian system to prevent seethrough settings dialog from popping up
        try
        {
            PXR_Boundary.SetGuardianSystemDisable(true);
            Debug.Log("[Passthrough] Guardian system disabled.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Passthrough] SetGuardianSystemDisable failed: {e.Message}");
        }

        yield return null;

        // Enable passthrough
        PXR_Manager.EnableVideoSeeThrough = true;
        Debug.Log("[Passthrough] PICO passthrough enabled.");
    }
#endif

    void OnDestroy()
    {
#if PICO_XR
        PXR_Manager.EnableVideoSeeThrough = false;
#endif
    }
}
