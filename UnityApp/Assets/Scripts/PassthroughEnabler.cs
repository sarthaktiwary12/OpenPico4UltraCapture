using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

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
        if (!pause)
            QueueEnableWhenReady();
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus)
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
        if (_enabled) return;
        enableOnStart = true;
        TryEnableOpenXRPassthrough();
        if (!_enabled)
            StartCoroutine(ForceEnableRetry());
    }

    private IEnumerator ForceEnableRetry()
    {
        for (int i = 0; i < 3 && !_enabled; i++)
        {
            yield return new WaitForSeconds(1.5f);
            TryEnableOpenXRPassthrough();
        }
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
