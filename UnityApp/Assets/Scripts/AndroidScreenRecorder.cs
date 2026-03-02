using System.IO;
using UnityEngine;

public class AndroidScreenRecorder : MonoBehaviour
{
    private AndroidJavaObject _bridge;
    private bool _supported;
    private bool _isRecording;
    private string _lastError = "";

    public bool IsSupported => _supported;
    public bool IsRecording => _isRecording;
    public string LastError => _lastError;

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
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(outputPath)) File.Delete(outputPath);

            int w = Mathf.Max(640, Screen.width);
            int h = Mathf.Max(640, Screen.height);
            if ((w & 1) == 1) w -= 1;
            if ((h & 1) == 1) h -= 1;
            int bitrate = Mathf.Max(6_000_000, w * h * 4);
            int fps = 30;

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

    // Called from Java bridge via UnityPlayer.UnitySendMessage.
    public void OnAndroidScreenRecorderEvent(string evt)
    {
        if (string.IsNullOrEmpty(evt)) return;
        if (evt.StartsWith("started:"))
        {
            _isRecording = true;
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
