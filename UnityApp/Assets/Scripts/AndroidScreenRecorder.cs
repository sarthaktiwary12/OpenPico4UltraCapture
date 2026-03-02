using System.IO;
using UnityEngine;

public class AndroidScreenRecorder : MonoBehaviour
{
    [Header("Quality")]
    public int captureWidth = 1920;
    public int captureHeight = 1080;
    public int captureBitrate = 20000000;
    public int captureFps = 30;

    private AndroidJavaObject _bridge;
    private bool _supported;
    private bool _isRecording;
    private bool _startedSignalPending;
    private string _lastEvent = "";
    private string _lastError = "";

    public bool IsSupported => _supported;
    public bool IsRecording => _isRecording;
    public string LastError => _lastError;
    public string LastEvent => _lastEvent;

    void Awake()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var cls = new AndroidJavaClass("com.sentientx.datacapture.AndroidScreenRecorderBridge");
            _bridge = cls.CallStatic<AndroidJavaObject>("getInstance");
            if (_bridge != null)
            {
                _bridge.Call("setUnityCallbackObject", gameObject.name);
                _supported = true;
                Debug.Log("[AndroidScreenRecorder] Bridge initialized.");
            }
        }
        catch (System.Exception e)
        {
            _supported = false;
            _lastError = e.Message;
            Debug.LogWarning($"[AndroidScreenRecorder] Init failed: {e.Message}");
        }
#endif
    }

    public bool StartRecording(string outputPath)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!_supported || _bridge == null) return false;
        try
        {
            _isRecording = false;
            _startedSignalPending = false;
            _lastEvent = "";
            _lastError = "";

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(outputPath)) File.Delete(outputPath);

            int w = Mathf.Max(640, captureWidth);
            int h = Mathf.Max(640, captureHeight);
            if ((w & 1) == 1) w -= 1;
            if ((h & 1) == 1) h -= 1;
            int bitrate = Mathf.Max(8_000_000, captureBitrate);
            int fps = Mathf.Clamp(captureFps, 24, 60);

            bool ok = _bridge.Call<bool>("requestStart", outputPath, w, h, bitrate, fps);
            if (!ok) _lastError = "requestStart returned false";
            return ok;
        }
        catch (System.Exception e)
        {
            _lastError = e.Message;
            Debug.LogWarning($"[AndroidScreenRecorder] Start failed: {e.Message}");
            return false;
        }
#else
        return false;
#endif
    }

    public bool StopRecording()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!_supported || _bridge == null) return false;
        try
        {
            return _bridge.Call<bool>("stopRecording");
        }
        catch (System.Exception e)
        {
            _lastError = e.Message;
            Debug.LogWarning($"[AndroidScreenRecorder] Stop failed: {e.Message}");
            return false;
        }
#else
        return false;
#endif
    }

    public bool ConsumeStartedSignal()
    {
        if (!_startedSignalPending) return false;
        _startedSignalPending = false;
        return true;
    }

    public string ConsumeLastEvent()
    {
        var evt = _lastEvent;
        _lastEvent = "";
        return evt;
    }

    // Called from Java bridge via UnityPlayer.UnitySendMessage.
    public void OnAndroidScreenRecorderEvent(string evt)
    {
        if (string.IsNullOrEmpty(evt)) return;
        _lastEvent = evt;
        if (evt.StartsWith("started:"))
        {
            _isRecording = true;
            _startedSignalPending = true;
            _lastError = "";
            Debug.Log("[AndroidScreenRecorder] Started.");
            return;
        }

        if (evt.StartsWith("stopped:"))
        {
            _isRecording = false;
            Debug.Log("[AndroidScreenRecorder] Stopped.");
            return;
        }

        if (evt.StartsWith("error:"))
        {
            _isRecording = false;
            _lastError = evt;
            Debug.LogWarning($"[AndroidScreenRecorder] {evt}");
        }
    }
}
