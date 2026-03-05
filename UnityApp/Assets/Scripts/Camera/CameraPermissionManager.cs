using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages Camera2 API permissions and camera discovery via questcameralib.aar.
/// On Pico, the AAR's Meta-specific camera discovery (getLeftCameraMetaDataJson) may not
/// find passthrough cameras. In that case, we enumerate all cameras and pick the best one.
/// </summary>
public class CameraPermissionManager : MonoBehaviour
{
    private const string LOG_TAG = "CamPermMgr";
    private const float CHECK_INTERVAL = 0.1f;

    private const string PERMISSION_MANAGER_CLASS = "com.samusynth.questcamera.core.CameraPermissionManager";
    private const string UNITY_PLAYER_CLASS = "com.unity3d.player.UnityPlayer";

    private AndroidJavaObject _javaInstance;
    private AndroidJavaObject _cameraManager;

    public AndroidJavaObject CameraManager => _cameraManager;
    public bool HasCameraManager => _javaInstance?.Call<bool>("hasCameraManager") ?? false;

    /// <summary>
    /// Best camera ID discovered for video recording. May be set by AAR discovery or Pico enumeration.
    /// </summary>
    public string SelectedCameraId { get; private set; }

    /// <summary>
    /// Resolution of the selected camera (from metadata or enumeration).
    /// </summary>
    public int SelectedWidth { get; private set; } = 1920;
    public int SelectedHeight { get; private set; } = 1080;

    public event Action OnCameraReady;

    private bool _ready;
    public bool IsReady => _ready;

#if UNITY_ANDROID && !UNITY_EDITOR
    void Start()
    {
        Initialize();
    }

    void OnDestroy()
    {
        Cleanup();
    }

    void Initialize()
    {
        Cleanup();

        try
        {
            using (var unityPlayer = new AndroidJavaClass(UNITY_PLAYER_CLASS))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                _javaInstance = new AndroidJavaObject(PERMISSION_MANAGER_CLASS, activity);
                _javaInstance.Call("requestCameraPermissionIfNeeded");
                StartCoroutine(WaitForCameraManager());
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LOG_TAG}] Failed to initialize: {e.Message}");
            // Fallback: try to enumerate cameras directly
            StartCoroutine(FallbackCameraDiscovery());
        }
    }

    void Cleanup()
    {
        _javaInstance?.Dispose();
        _javaInstance = null;
        _cameraManager?.Dispose();
        _cameraManager = null;
        _ready = false;
        StopAllCoroutines();
    }

    IEnumerator WaitForCameraManager()
    {
        float timeout = 10f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            if (HasCameraManager)
            {
                _cameraManager = _javaInstance.Call<AndroidJavaObject>("getCameraManager");

                if (_cameraManager != null)
                {
                    Debug.Log($"[{LOG_TAG}] CameraManager obtained from AAR");

                    // Try AAR's Meta-specific camera discovery first
                    if (TryAARCameraDiscovery())
                    {
                        _ready = true;
                        OnCameraReady?.Invoke();
                        yield break;
                    }

                    // Fallback: enumerate cameras on Pico
                    Debug.Log($"[{LOG_TAG}] AAR camera discovery returned no cameras, trying Pico enumeration...");
                    yield return EnumeratePicoCameras();

                    if (!string.IsNullOrEmpty(SelectedCameraId))
                    {
                        _ready = true;
                        OnCameraReady?.Invoke();
                    }
                    else
                    {
                        Debug.LogError($"[{LOG_TAG}] No suitable camera found");
                    }
                    yield break;
                }
            }

            yield return new WaitForSeconds(CHECK_INTERVAL);
            elapsed += CHECK_INTERVAL;
        }

        Debug.LogWarning($"[{LOG_TAG}] CameraManager timeout, trying fallback enumeration...");
        yield return FallbackCameraDiscovery();
    }

    bool TryAARCameraDiscovery()
    {
        try
        {
            string leftJson = _javaInstance?.Call<string>("getLeftCameraMetaDataJson") ?? "";
            if (!string.IsNullOrEmpty(leftJson))
            {
                var meta = JsonUtility.FromJson<CameraMetadata>(leftJson);
                if (meta != null && !string.IsNullOrEmpty(meta.cameraId))
                {
                    SelectedCameraId = meta.cameraId;
                    if (meta.sensor?.pixelArraySize != null)
                    {
                        SelectedWidth = meta.sensor.pixelArraySize.width;
                        SelectedHeight = meta.sensor.pixelArraySize.height;
                    }
                    Debug.Log($"[{LOG_TAG}] AAR found camera: id={SelectedCameraId}, {SelectedWidth}x{SelectedHeight}");
                    return true;
                }
            }

            string rightJson = _javaInstance?.Call<string>("getRightCameraMetaDataJson") ?? "";
            if (!string.IsNullOrEmpty(rightJson))
            {
                var meta = JsonUtility.FromJson<CameraMetadata>(rightJson);
                if (meta != null && !string.IsNullOrEmpty(meta.cameraId))
                {
                    SelectedCameraId = meta.cameraId;
                    if (meta.sensor?.pixelArraySize != null)
                    {
                        SelectedWidth = meta.sensor.pixelArraySize.width;
                        SelectedHeight = meta.sensor.pixelArraySize.height;
                    }
                    Debug.Log($"[{LOG_TAG}] AAR found camera (right): id={SelectedCameraId}, {SelectedWidth}x{SelectedHeight}");
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LOG_TAG}] AAR camera discovery error: {e.Message}");
        }

        return false;
    }

    IEnumerator EnumeratePicoCameras()
    {
        yield return null; // Let the frame settle

        try
        {
            if (_cameraManager == null)
            {
                Debug.LogError($"[{LOG_TAG}] No CameraManager for enumeration");
                yield break;
            }

            string[] cameraIds = _cameraManager.Call<string[]>("getCameraIdList");
            if (cameraIds == null || cameraIds.Length == 0)
            {
                Debug.LogError($"[{LOG_TAG}] No cameras found on device");
                yield break;
            }

            Debug.Log($"[{LOG_TAG}] Found {cameraIds.Length} cameras, enumerating...");

            string bestId = null;
            int bestWidth = 0, bestHeight = 0;
            bool foundFront = false;

            foreach (string id in cameraIds)
            {
                try
                {
                    using (var chars = _cameraManager.Call<AndroidJavaObject>("getCameraCharacteristics", id))
                    {
                        // Get LENS_FACING
                        using (var facingKey = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics")
                            .GetStatic<AndroidJavaObject>("LENS_FACING"))
                        {
                            var facingObj = chars.Call<AndroidJavaObject>("get", facingKey);
                            int facing = facingObj != null ? facingObj.Call<int>("intValue") : -1;
                            facingObj?.Dispose();

                            // Get sensor size
                            int w = 0, h = 0;
                            try
                            {
                                using (var sizeKey = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics")
                                    .GetStatic<AndroidJavaObject>("SENSOR_INFO_PIXEL_ARRAY_SIZE"))
                                {
                                    var size = chars.Call<AndroidJavaObject>("get", sizeKey);
                                    if (size != null)
                                    {
                                        w = size.Call<int>("getWidth");
                                        h = size.Call<int>("getHeight");
                                        size.Dispose();
                                    }
                                }
                            }
                            catch { }

                            // 0=BACK, 1=FRONT, 2=EXTERNAL
                            string facingStr = facing == 0 ? "BACK" : facing == 1 ? "FRONT" : facing == 2 ? "EXTERNAL" : $"UNKNOWN({facing})";
                            Debug.Log($"[{LOG_TAG}] Camera {id}: facing={facingStr}, size={w}x{h}");

                            // Prefer front-facing camera (egocentric on VR headsets)
                            // If no front camera, pick the highest-res one
                            if (facing == 1 && !foundFront)
                            {
                                bestId = id;
                                bestWidth = w > 0 ? w : 1920;
                                bestHeight = h > 0 ? h : 1080;
                                foundFront = true;
                            }
                            else if (!foundFront && w * h > bestWidth * bestHeight)
                            {
                                bestId = id;
                                bestWidth = w > 0 ? w : 1920;
                                bestHeight = h > 0 ? h : 1080;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{LOG_TAG}] Error querying camera {id}: {e.Message}");
                }
            }

            if (bestId != null)
            {
                SelectedCameraId = bestId;
                SelectedWidth = bestWidth;
                SelectedHeight = bestHeight;
                Debug.Log($"[{LOG_TAG}] Selected camera: id={SelectedCameraId}, {SelectedWidth}x{SelectedHeight}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LOG_TAG}] Camera enumeration failed: {e.Message}\n{e.StackTrace}");
        }
    }

    IEnumerator FallbackCameraDiscovery()
    {
        yield return new WaitForSeconds(1f);

        bool gotManager = false;
        try
        {
            using (var unityPlayer = new AndroidJavaClass(UNITY_PLAYER_CLASS))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                _cameraManager = activity.Call<AndroidJavaObject>("getSystemService", "camera");
                gotManager = _cameraManager != null;

                if (!gotManager)
                    Debug.LogError($"[{LOG_TAG}] Cannot get CameraManager from system service");
                else
                    Debug.Log($"[{LOG_TAG}] Got CameraManager from system service directly");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LOG_TAG}] Fallback camera discovery failed: {e.Message}");
        }

        if (gotManager)
        {
            yield return EnumeratePicoCameras();

            if (!string.IsNullOrEmpty(SelectedCameraId))
            {
                _ready = true;
                OnCameraReady?.Invoke();
            }
        }
    }
#endif
}
