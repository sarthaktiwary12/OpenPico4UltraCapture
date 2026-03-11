using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;
#if PICO_XR
using Unity.XR.PXR;
#endif
#if PICO_OPENXR_SDK
using Unity.XR.OpenXR.Features.PICOSupport;
#endif

public class PassthroughEnabler : MonoBehaviour
{
    [Tooltip("Enable on app start so the POV recording captures real camera passthrough.")]
    public bool enableOnStart = false;

    private bool _enabled;
    private Coroutine _pendingEnable;

    void Start()
    {
        var cam = GetComponent<Camera>();
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
        }

        if (!enableOnStart)
        {
            Debug.Log("[Passthrough] Disabled on start.");
            return;
        }

        QueueEnableWhenReady();
    }

    void OnApplicationPause(bool pause)
    {
        if (pause)
            _enabled = false;
        else
            QueueEnableWhenReady();
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
            _enabled = false;
        else
            QueueEnableWhenReady();
    }

    private void QueueEnableWhenReady()
    {
        if (_enabled || !enableOnStart || _pendingEnable != null)
            return;
        _pendingEnable = StartCoroutine(EnablePassthroughWhenXRReady());
    }

    private IEnumerator EnablePassthroughWhenXRReady()
    {
        // Wait for XR runtime instead of forcing passthrough during inactive/sleeping states.
        float waited = 0f;
        while (!XRSettings.isDeviceActive)
        {
            if (waited > 30f)
            {
                Debug.LogWarning("[Passthrough] XR inactive for 30s; skipping enable attempt.");
                _pendingEnable = null;
                yield break;
            }
            waited += 0.25f;
            yield return new WaitForSeconds(0.25f);
        }

        yield return new WaitForSeconds(0.25f);
        TryEnableOpenXRPassthrough();

        // Retry up to 3 times if first attempt failed (PICO sometimes needs a second try)
        for (int retry = 0; retry < 3 && !_enabled; retry++)
        {
            Debug.Log($"[Passthrough] Retry {retry + 1}/3 after {1 + retry}s...");
            yield return new WaitForSeconds(1f + retry);
            TryEnableOpenXRPassthrough();
        }

        if (!_enabled)
            Debug.LogError("[Passthrough] All enable attempts failed — user will see black screen.");

        _pendingEnable = null;
    }

    /// <summary>Call from other scripts as a belt-and-suspenders backup.</summary>
    public void ForceEnable()
    {
        if (_enabled)
            return;

        enableOnStart = true;
        QueueEnableWhenReady();
    }

    private void TryEnableOpenXRPassthrough()
    {
        if (_enabled)
            return;

        bool enabledAny = false;

#if PICO_OPENXR_SDK
        try
        {
            bool extensionReady = PassthroughFeature.isExtensionEnable;
            bool supported = false;

            if (extensionReady)
            {
                supported = PassthroughFeature.IsPassthroughSupported();
            }

            if (extensionReady && supported)
            {
                PassthroughFeature.EnableVideoSeeThrough = true;
                PassthroughFeature.PassthroughStart();
                enabledAny = true;
            }

            Debug.Log($"[Passthrough] OpenXR ext={extensionReady}, supported={supported}, enabled={enabledAny}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Passthrough] OpenXR direct enable failed: {e.Message}");
        }
#endif

#if PICO_XR
        try
        {
            PXR_Manager.EnableVideoSeeThrough = true;
            Debug.Log($"[Passthrough] PXR_Manager.EnableVideoSeeThrough={PXR_Manager.EnableVideoSeeThrough}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Passthrough] PXR_Manager enable failed: {e.Message}");
        }

        try
        {
            int rcBg = PXR_Plugin.Boundary.UPxr_SetSeeThroughBackground(true);
            PXR_Plugin.Boundary.UPxr_SetSeeThroughState(true);
            bool boundaryOk = rcBg == (int)PxrResult.SUCCESS;
            enabledAny = enabledAny || boundaryOk;
            Debug.Log($"[Passthrough] Boundary enable rc={rcBg}, ok={boundaryOk}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Passthrough] Boundary fallback failed: {e.Message}");
        }
#endif

        string[] typeNames =
        {
            "Unity.XR.OpenXR.Features.PICOSupport.PassthroughFeature, Unity.XR.PICO",
            "Unity.XR.OpenXR.Features.PICOSupport.PassthroughFeature, Unity.XR.PICO.OpenXR",
            "Unity.XR.OpenXR.Features.PICOSupport.PassthroughFeature, Unity.XR.PICO.OpenXR.Features"
        };

        foreach (var name in typeNames)
        {
            var passthroughType = System.Type.GetType(name);
            if (passthroughType == null)
                continue;

            var prop = passthroughType.GetProperty("EnableVideoSeeThrough", BindingFlags.Public | BindingFlags.Static);
            if (prop == null)
                continue;

            var extProp = passthroughType.GetProperty("isExtensionEnable", BindingFlags.Public | BindingFlags.Static);
            bool extensionReady = extProp == null || (bool)extProp.GetValue(null, null);
            if (!extensionReady)
                continue;

            prop.SetValue(null, true, null);

            var startMethod = passthroughType.GetMethod("PassthroughStart", BindingFlags.Public | BindingFlags.Static);
            startMethod?.Invoke(null, null);
            enabledAny = true;
            Debug.Log($"[Passthrough] Enabled via reflection: {name}, ext={extensionReady}");
            break;
        }

        _enabled = enabledAny;
        if (!_enabled)
            Debug.LogWarning("[Passthrough] OpenXR passthrough feature not found.");
    }
}
