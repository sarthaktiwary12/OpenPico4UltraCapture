using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;

public class XRBootstrapFix : MonoBehaviour
{
    private bool _isEnsuring;
    private bool _bootstrapDone;

    private IEnumerator Start()
    {
        // Give runtime time to register XR subsystems before first init attempt.
        yield return new WaitForSeconds(2.0f);
        yield return BootstrapLoop("startup");
    }

    private void OnApplicationPause(bool pause)
    {
        if (!pause)
            StartCoroutine(BootstrapLoop("resume"));
    }

    private IEnumerator BootstrapLoop(string reason)
    {
        if (_bootstrapDone)
            yield break;

        // Retry for ~20s because some headset runtimes register descriptors late.
        for (int i = 0; i < 10; i++)
        {
            yield return EnsureXRRunning($"{reason}#{i + 1}");
            if (XRSettings.isDeviceActive)
            {
                _bootstrapDone = true;
                yield break;
            }
            yield return new WaitForSeconds(2.0f);
        }
    }

    private IEnumerator EnsureXRRunning(string reason)
    {
        if (_isEnsuring)
            yield break;

        _isEnsuring = true;

        var gs = XRGeneralSettings.Instance;
        if (gs == null || gs.Manager == null)
        {
            Debug.LogWarning($"[XRFix] XRGeneralSettings missing ({reason}).");
            _isEnsuring = false;
            yield break;
        }

        var manager = gs.Manager;

        if (manager.activeLoader == null)
        {
            Debug.Log($"[XRFix] No active XR loader, initializing ({reason})...");
            yield return manager.InitializeLoader();
        }

        if (manager.activeLoader == null)
        {
            Debug.LogError($"[XRFix] XR loader initialization failed ({reason}).");
            _isEnsuring = false;
            yield break;
        }

        // If runtime is not active yet, restart subsystems once.
        if (!XRSettings.isDeviceActive)
        {
            Debug.Log($"[XRFix] XR inactive, restarting subsystems ({reason})...");
            manager.StopSubsystems();
            manager.StartSubsystems();
            yield return null;
        }

        var devices = new List<InputDevice>();
        InputDevices.GetDevices(devices);
        Debug.Log($"[XRFix] XR active={XRSettings.isDeviceActive}, loader={manager.activeLoader.GetType().Name}, devices={devices.Count}, mode={XRSettings.loadedDeviceName}");
        _isEnsuring = false;
    }
}
