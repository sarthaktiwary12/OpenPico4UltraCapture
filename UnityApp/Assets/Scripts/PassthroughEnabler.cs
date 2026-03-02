using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

public class PassthroughEnabler : MonoBehaviour
{
    private bool _enabled;

    void Start()
    {
        var cam = GetComponent<Camera>();
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
        }

        StartCoroutine(EnablePassthroughOnceWhenXRReady());
    }

    private IEnumerator EnablePassthroughOnceWhenXRReady()
    {
        // Avoid repeated toggles that can trigger runtime tracking restarts.
        const float timeoutSeconds = 8f;
        float waited = 0f;
        while (!XRSettings.isDeviceActive && waited < timeoutSeconds)
        {
            waited += 0.25f;
            yield return new WaitForSeconds(0.25f);
        }

        yield return new WaitForSeconds(0.25f);
        TryEnableOpenXRPassthrough();
    }

    private void TryEnableOpenXRPassthrough()
    {
        if (_enabled)
            return;

        string[] typeNames =
        {
            "Unity.XR.OpenXR.Features.PICOSupport.PassthroughFeature, Unity.XR.PICO",
            "Unity.XR.OpenXR.Features.PICOSupport.PassthroughFeature, Unity.XR.PICO.OpenXR"
        };

        foreach (var name in typeNames)
        {
            var passthroughType = System.Type.GetType(name);
            if (passthroughType == null)
                continue;

            var prop = passthroughType.GetProperty("EnableVideoSeeThrough", BindingFlags.Public | BindingFlags.Static);
            if (prop == null)
                continue;

            prop.SetValue(null, true, null);
            _enabled = true;
            Debug.Log($"[Passthrough] Enabled via {name}");
            return;
        }

        Debug.LogWarning("[Passthrough] OpenXR passthrough feature not found.");
    }
}
