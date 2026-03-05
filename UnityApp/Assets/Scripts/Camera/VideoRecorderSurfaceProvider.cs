using System;
using UnityEngine;

/// <summary>
/// Wraps questcameralib.aar's VideoRecorderSurfaceProvider Java class.
/// Manages MediaRecorder-based video capture from a Camera2 session.
/// </summary>
public class Camera2VideoRecorder
{
    private const string LOG_TAG = "Cam2Recorder";
    private const string RECORDER_CLASS = "com.samusynth.questcamera.io.VideoRecorderSurfaceProvider";

    private AndroidJavaObject _recorder;
    private bool _isRecording;

    public bool IsRecording => _isRecording;
    public AndroidJavaObject JavaInstance => _recorder;

    /// <summary>
    /// Creates the Java VideoRecorderSurfaceProvider instance.
    /// </summary>
    public bool Initialize(int width, int height, string outputPath, int fps = 30, int bitrateMbps = 8, int iFrameInterval = 1)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            Close();

            _recorder = new AndroidJavaObject(
                RECORDER_CLASS,
                width, height, outputPath,
                fps, bitrateMbps, iFrameInterval
            );

            Debug.Log($"[{LOG_TAG}] Initialized: {width}x{height}, {fps}fps, {bitrateMbps}Mbps, output={outputPath}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LOG_TAG}] Failed to initialize: {e.Message}\n{e.StackTrace}");
            _recorder = null;
            return false;
        }
#else
        return false;
#endif
    }

    public void UpdateOutputFile(string outputPath)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _recorder?.Call("updateOutputFile", outputPath);
            Debug.Log($"[{LOG_TAG}] Updated output: {outputPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LOG_TAG}] updateOutputFile failed: {e.Message}");
        }
#endif
    }

    public bool StartRecording()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_recorder == null) return false;

        try
        {
            _recorder.Call("startRecording");
            _isRecording = true;
            Debug.Log($"[{LOG_TAG}] Recording started");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LOG_TAG}] startRecording failed: {e.Message}\n{e.StackTrace}");
            return false;
        }
#else
        return false;
#endif
    }

    public bool StopRecording()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_recorder == null) return false;

        try
        {
            _recorder.Call("stopRecording");
            _isRecording = false;
            Debug.Log($"[{LOG_TAG}] Recording stopped");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[{LOG_TAG}] stopRecording failed: {e.Message}");
            _isRecording = false;
            return false;
        }
#else
        return false;
#endif
    }

    public void Close()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_recorder == null) return;

        try
        {
            if (_isRecording)
            {
                try { _recorder.Call("stopRecording"); }
                catch (Exception e) { Debug.LogWarning($"[{LOG_TAG}] Stop during close: {e.Message}"); }
                _isRecording = false;
            }
            _recorder.Call("close");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[{LOG_TAG}] Close error: {e.Message}");
        }
        finally
        {
            _recorder.Dispose();
            _recorder = null;
            _isRecording = false;
        }
#endif
    }
}
