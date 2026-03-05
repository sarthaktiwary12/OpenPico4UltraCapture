using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Manages a Camera2 capture session via questcameralib.aar's CameraSessionManager Java class.
/// Simplified for Pico — no MonoBehaviour lifecycle (managed explicitly by SimpleRecordingController).
/// </summary>
public class Camera2SessionManager
{
    private const string LOG_TAG = "Cam2Session";
    private const string SESSION_MANAGER_CLASS = "com.samusynth.questcamera.core.CameraSessionManager";

    private AndroidJavaObject _sessionManager;

    public bool IsOpen
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return _sessionManager?.Call<bool>("isOpen") ?? false;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Opens a camera session with the given surface provider (e.g. VideoRecorderSurfaceProvider).
    /// </summary>
    public bool Open(AndroidJavaObject cameraManager, string cameraId, AndroidJavaObject surfaceProvider)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            Close();

            _sessionManager = new AndroidJavaObject(SESSION_MANAGER_CLASS);
            _sessionManager.Call("registerSurfaceProvider", surfaceProvider);
            _sessionManager.Call("setCaptureTemplateFromString", "RECORD");

            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                _sessionManager.Call("openCamera", activity, cameraManager, cameraId);
            }

            Debug.Log($"[{LOG_TAG}] Camera session opened for camera {cameraId}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LOG_TAG}] Failed to open camera session: {e.Message}\n{e.StackTrace}");
            Close();
            return false;
        }
#else
        return false;
#endif
    }

    public void Close()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _sessionManager?.Call("close");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LOG_TAG}] Close error: {e.Message}");
        }
        finally
        {
            _sessionManager?.Dispose();
            _sessionManager = null;
        }
#endif
    }
}
