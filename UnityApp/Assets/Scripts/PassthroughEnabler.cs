using System;
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

    private static string _logPath;

    private static void NativeLog(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Debug.Log($"[Passthrough] {msg}");
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var logClass = new AndroidJavaClass("android.util.Log"))
                logClass.CallStatic<int>("i", "PassthroughDbg", msg);
        }
        catch { }
#endif
        // File-based log as fallback since logcat may not show Unity output
        try
        {
            if (_logPath == null)
                _logPath = System.IO.Path.Combine(Application.persistentDataPath, "passthrough_debug.log");
            System.IO.File.AppendAllText(_logPath, line + "\n");
        }
        catch { }
    }

    void Start()
    {
        var cam = GetComponent<Camera>();
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            NativeLog($"Camera configured: clearFlags=SolidColor, bg=transparent");
        }

        // Enable premultiplied alpha so the compositor uses the eye buffer's alpha channel.
        // Without this, the eye buffer is composited as fully opaque and hides passthrough.
        EnablePremultipliedAlphaBlending();

        if (!enableOnStart)
        {
            NativeLog("Passthrough disabled on start.");
            return;
        }

        QueueEnableWhenReady();
    }

    void OnApplicationPause(bool pause)
    {
        NativeLog($"OnApplicationPause({pause})");
        if (pause)
            _enabled = false;
        else
            QueueEnableWhenReady();
    }

    void OnApplicationFocus(bool hasFocus)
    {
        NativeLog($"OnApplicationFocus({hasFocus})");
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
        NativeLog("Waiting for XR device to become active...");

        float waited = 0f;
        while (!XRSettings.isDeviceActive)
        {
            if (waited > 30f)
            {
                NativeLog("XR inactive for 30s; skipping enable attempt.");
                _pendingEnable = null;
                yield break;
            }
            waited += 0.25f;
            yield return new WaitForSeconds(0.25f);
        }

        NativeLog($"XR active after {waited:F1}s. loadedDeviceName={XRSettings.loadedDeviceName}");

        // Give the OpenXR session extra time to reach READY state
        yield return new WaitForSeconds(1.0f);

        TryEnableAllPaths();

        // Retry up to 5 times with increasing delay (PICO sometimes needs several tries)
        for (int retry = 0; retry < 5 && !_enabled; retry++)
        {
            float delay = 1f + retry;
            NativeLog($"Retry {retry + 1}/5 after {delay}s...");
            yield return new WaitForSeconds(delay);
            TryEnableAllPaths();
        }

        if (!_enabled)
            NativeLog("ERROR: All passthrough enable attempts failed. User sees black.");

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

    private void EnablePremultipliedAlphaBlending()
    {
#if PICO_XR
        try
        {
            PXR_Plugin.Render.UPxr_EnablePremultipliedAlpha(true);
            NativeLog("Premultiplied alpha enabled via PXR_Plugin");
        }
        catch (Exception e)
        {
            NativeLog($"PXR_Plugin premultiplied alpha failed: {e.Message}");
        }
#endif
        // Reflection fallback for premultiplied alpha
        try
        {
            var pluginType = Type.GetType("Unity.XR.PXR.PXR_Plugin+Render, Unity.XR.PICO")
                          ?? Type.GetType("Unity.XR.PXR.PXR_Plugin+Render, Unity.XR.PICO.OpenXR");
            if (pluginType != null)
            {
                var method = pluginType.GetMethod("UPxr_EnablePremultipliedAlpha",
                    BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    method.Invoke(null, new object[] { true });
                    NativeLog("Premultiplied alpha enabled via reflection");
                }
            }
        }
        catch (Exception e)
        {
            NativeLog($"Premultiplied alpha reflection failed: {e.Message}");
        }

        // Also set layer blend as belt-and-suspenders
#if PICO_XR
        try
        {
            var layerBlend = new PxrLayerBlend();
            layerBlend.srcColor = PxrBlendFactor.PxrBlendFactorSrcAlpha;
            layerBlend.dstColor = PxrBlendFactor.PxrBlendFactorOneMinusSrcAlpha;
            layerBlend.srcAlpha = PxrBlendFactor.PxrBlendFactorOne;
            layerBlend.dstAlpha = PxrBlendFactor.PxrBlendFactorOneMinusSrcAlpha;
            PXR_Plugin.Render.UPxr_SetLayerBlend(true, layerBlend);
            NativeLog("Layer blend set: srcAlpha/oneMinusSrcAlpha");
        }
        catch (Exception e)
        {
            NativeLog($"Layer blend setup failed: {e.Message}");
        }
#endif
    }

    private void TryEnableAllPaths()
    {
        if (_enabled)
            return;

        bool enabledAny = false;

        // Path 0: Set OpenXR environment blend mode to AlphaBlend.
        // This is the standard OpenXR way to composite app content over passthrough.
        // Without this, even if passthrough is started, the eye buffer is opaque.
        TrySetEnvironmentBlendMode();

        // Path 1: PICO OpenXR SDK direct API
        enabledAny |= TryOpenXRDirect();

        // Path 2: PXR_Manager + Boundary API
        enabledAny |= TryPXRManager();

        // Path 3: Reflection fallback (handles different SDK assembly names)
        if (!enabledAny)
            enabledAny |= TryReflection();

        // Path 4: Android system property fallback via PXR_Plugin System API
        if (!enabledAny)
            enabledAny |= TrySystemApi();

        _enabled = enabledAny;
        NativeLog($"TryEnableAllPaths result: enabled={_enabled}");
    }

    private void TrySetEnvironmentBlendMode()
    {
        // OpenXRFeature.SetEnvironmentBlendMode is protected static, so we call via reflection.
        // XrEnvironmentBlendMode: Opaque=1, Additive=2, AlphaBlend=3
        try
        {
            var featureType = Type.GetType(
                "UnityEngine.XR.OpenXR.Features.OpenXRFeature, Unity.XR.OpenXR");
            if (featureType == null)
            {
                // Try scanning loaded assemblies
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    featureType = asm.GetType("UnityEngine.XR.OpenXR.Features.OpenXRFeature");
                    if (featureType != null)
                        break;
                }
            }

            if (featureType == null)
            {
                NativeLog("SetEnvironmentBlendMode: OpenXRFeature type not found");
                return;
            }

            var method = featureType.GetMethod("SetEnvironmentBlendMode",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                NativeLog("SetEnvironmentBlendMode: method not found on OpenXRFeature");
                return;
            }

            // Resolve the enum type for the parameter
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum)
            {
                var enumVal = Enum.ToObject(parameters[0].ParameterType, 3); // AlphaBlend=3
                method.Invoke(null, new object[] { enumVal });
                NativeLog($"SetEnvironmentBlendMode(AlphaBlend=3) success via enum");
            }
            else
            {
                // Fallback: try passing int directly
                method.Invoke(null, new object[] { 3 });
                NativeLog($"SetEnvironmentBlendMode(3) success via int");
            }
        }
        catch (Exception e)
        {
            NativeLog($"SetEnvironmentBlendMode failed: {e.Message}");
            if (e.InnerException != null)
                NativeLog($"  Inner: {e.InnerException.Message}");
        }
    }

    private bool TryOpenXRDirect()
    {
#if PICO_OPENXR_SDK
        try
        {
            bool extensionReady = PassthroughFeature.isExtensionEnable;
            bool supported = false;

            if (extensionReady)
            {
                supported = PassthroughFeature.IsPassthroughSupported();
            }

            NativeLog($"OpenXR: ext={extensionReady}, supported={supported}");

            if (extensionReady && supported)
            {
                PassthroughFeature.EnableVideoSeeThrough = true;
                PassthroughFeature.PassthroughStart();
                NativeLog("OpenXR: PassthroughStart() called, EnableVideoSeeThrough=true");
                return true;
            }

            if (extensionReady && !supported)
            {
                // Try enabling anyway - some firmware reports unsupported but works
                PassthroughFeature.EnableVideoSeeThrough = true;
                PassthroughFeature.PassthroughStart();
                NativeLog("OpenXR: Forced enable despite !supported");
                return true;
            }
        }
        catch (Exception e)
        {
            NativeLog($"OpenXR direct failed: {e.Message}");
        }
#endif
        return false;
    }

    private bool TryPXRManager()
    {
        bool any = false;
#if PICO_XR
        try
        {
            PXR_Manager.EnableVideoSeeThrough = true;
            NativeLog($"PXR_Manager.EnableVideoSeeThrough={PXR_Manager.EnableVideoSeeThrough}");
            any = true;
        }
        catch (Exception e)
        {
            NativeLog($"PXR_Manager enable failed: {e.Message}");
        }

        try
        {
            int rcBg = PXR_Plugin.Boundary.UPxr_SetSeeThroughBackground(true);
            PXR_Plugin.Boundary.UPxr_SetSeeThroughState(true);
            bool boundaryOk = rcBg == (int)PxrResult.SUCCESS;
            NativeLog($"Boundary: SetSeeThroughBackground rc={rcBg}, ok={boundaryOk}");
            any = any || boundaryOk;
        }
        catch (Exception e)
        {
            NativeLog($"Boundary fallback failed: {e.Message}");
        }

        // Also try the MR seethrough API if available
        try
        {
            var mrType = Type.GetType("Unity.XR.PXR.PXR_MixedReality, Unity.XR.PICO");
            if (mrType != null)
            {
                var enableMethod = mrType.GetMethod("EnableVideoSeeThrough",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(bool) }, null);
                if (enableMethod != null)
                {
                    var result = enableMethod.Invoke(null, new object[] { true });
                    NativeLog($"PXR_MixedReality.EnableVideoSeeThrough(true) = {result}");
                    any = true;
                }

                var startMethod = mrType.GetMethod("StartVideoSeeThrough",
                    BindingFlags.Public | BindingFlags.Static);
                if (startMethod != null)
                {
                    var result = startMethod.Invoke(null, null);
                    NativeLog($"PXR_MixedReality.StartVideoSeeThrough() = {result}");
                }
            }
        }
        catch (Exception e)
        {
            NativeLog($"PXR_MixedReality API failed: {e.Message}");
        }
#endif
        return any;
    }

    private bool TryReflection()
    {
        string[] typeNames =
        {
            "Unity.XR.OpenXR.Features.PICOSupport.PassthroughFeature, Unity.XR.PICO",
            "Unity.XR.OpenXR.Features.PICOSupport.PassthroughFeature, Unity.XR.PICO.OpenXR",
            "Unity.XR.OpenXR.Features.PICOSupport.PassthroughFeature, Unity.XR.PICO.OpenXR.Features"
        };

        foreach (var name in typeNames)
        {
            try
            {
                var passthroughType = Type.GetType(name);
                if (passthroughType == null)
                    continue;

                var prop = passthroughType.GetProperty("EnableVideoSeeThrough",
                    BindingFlags.Public | BindingFlags.Static);
                if (prop == null)
                    continue;

                var extProp = passthroughType.GetProperty("isExtensionEnable",
                    BindingFlags.Public | BindingFlags.Static);
                bool extensionReady = extProp == null || (bool)extProp.GetValue(null, null);

                NativeLog($"Reflection [{name}]: ext={extensionReady}");

                // Set EnableVideoSeeThrough regardless of extension state
                prop.SetValue(null, true, null);

                var startMethod = passthroughType.GetMethod("PassthroughStart",
                    BindingFlags.Public | BindingFlags.Static);
                startMethod?.Invoke(null, null);

                NativeLog($"Reflection: enabled via {name}");
                return true;
            }
            catch (Exception e)
            {
                NativeLog($"Reflection [{name}] failed: {e.Message}");
            }
        }

        NativeLog("Reflection: no PassthroughFeature type found in any assembly");
        return false;
    }

    private bool TrySystemApi()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            // Try calling the PICO system service to enable seethrough directly
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var contentResolver = activity.Call<AndroidJavaObject>("getContentResolver"))
            {
                using (var settings = new AndroidJavaClass("android.provider.Settings$System"))
                {
                    // Check if seethrough is enabled in system settings
                    int vstEnabled = settings.CallStatic<int>("getInt", contentResolver,
                        "enable_vst", 0);
                    NativeLog($"System settings enable_vst={vstEnabled}");
                }
            }
        }
        catch (Exception e)
        {
            NativeLog($"System API check failed: {e.Message}");
        }
#endif
        return false;
    }
}
