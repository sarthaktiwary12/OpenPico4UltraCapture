using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

public class SimpleRecordingController : MonoBehaviour
{
    [Header("Systems")]
    public SensorRecorder sensorRecorder;
    public SyncManager syncManager;
    public SpatialMeshCapture spatialMeshCapture;
    public BodyTrackingRecorder bodyTrackingRecorder;

    [Header("UI")]
    public Button btnToggle;
    public Text txtStatus;
    public Text txtButtonLabel;
    public Image btnImage;

    private bool _recording;
    private float _startTime;
    private bool _triggerWasDown;
    private string _sessionDir;
    private long _videoStartTimeMs;
    private string _videoBackend = "none";
    private bool _enterpriseRecordingToggled;

    // Known PICO video save directories
    static readonly string[] VideoDirs = {
        "/sdcard/PICO/SpatialVideo",
        "/sdcard/DCIM/ScreenRecording",
        "/sdcard/PICO/Videos",
        "/sdcard/DCIM/Camera",
        "/sdcard/Movies",
        "/sdcard/DCIM"
    };

    void Start()
    {
        if (btnToggle != null) btnToggle.onClick.AddListener(OnToggle);
        SetIdle();
        Debug.Log("[Controller] Ready. Press A or Trigger to toggle recording.");
        StartCoroutine(LogDiagnostics());
    }

    System.Collections.IEnumerator LogDiagnostics()
    {
        yield return new WaitForSeconds(2f);
        var xrDisplay = UnityEngine.XR.XRSettings.isDeviceActive;
        var xrModel = UnityEngine.XR.XRSettings.loadedDeviceName;
        Debug.Log($"[Diag] XR active={xrDisplay}, device={xrModel}, Screen={Screen.width}x{Screen.height}");
        Debug.Log($"[Diag] sensorRecorder={sensorRecorder != null}, spatialMesh={spatialMeshCapture != null}, bodyTracker={bodyTrackingRecorder != null}");

        var devices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
        UnityEngine.XR.InputDevices.GetDevices(devices);
        Debug.Log($"[Diag] XR devices found: {devices.Count}");
        foreach (var d in devices)
            Debug.Log($"[Diag]   device: {d.name} ({d.characteristics})");

        var hmd = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
        Debug.Log($"[Diag] HMD valid={hmd.isValid}");
        if (hmd.isValid)
        {
            hmd.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 p);
            hmd.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out Quaternion r);
            Debug.Log($"[Diag] HMD pos=({p.x:F2},{p.y:F2},{p.z:F2}) rot=({r.x:F2},{r.y:F2},{r.z:F2},{r.w:F2})");
        }

        var nativeImu = NativeIMUBridge.Instance;
        Debug.Log($"[Diag] NativeIMU active={nativeImu?.IsActive}, accel={nativeImu?.Acceleration}");
    }

    void Update()
    {
        CheckControllerInput();

        if (_recording)
        {
            float elapsed = Time.time - _startTime;
            int min = (int)(elapsed / 60f);
            int sec = (int)(elapsed % 60f);
            SetStatus($"REC  {min:D2}:{sec:D2}\n\nFrames: {sensorRecorder.FrameIndex}");
        }
    }

    void CheckControllerInput()
    {
        bool down = false;

        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (right.isValid)
        {
            if (right.TryGetFeatureValue(CommonUsages.triggerButton, out bool tb) && tb)
                down = true;
            if (right.TryGetFeatureValue(CommonUsages.primaryButton, out bool ab) && ab)
                down = true;
        }

        var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        if (left.isValid)
        {
            if (left.TryGetFeatureValue(CommonUsages.triggerButton, out bool tb) && tb)
                down = true;
            if (left.TryGetFeatureValue(CommonUsages.primaryButton, out bool ab) && ab)
                down = true;
        }

        if (!right.isValid && !left.isValid)
        {
            var devices = new List<InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, devices);
            foreach (var dev in devices)
            {
                if (dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool tb) && tb)
                    down = true;
                if (dev.TryGetFeatureValue(CommonUsages.primaryButton, out bool ab) && ab)
                    down = true;
            }
        }

        if (down && !_triggerWasDown)
        {
            Debug.Log("[Controller] Controller input -> Toggle");
            OnToggle();
        }
        _triggerWasDown = down;
    }

    void OnToggle()
    {
        Debug.Log("[Controller] Toggle pressed!");
        if (!_recording)
            StartRecording();
        else
            StopRecording();
    }

    void StartRecording()
    {
        try
        {
            // Start sensor recording
            sensorRecorder.StartSession("capture", "general");
            _sessionDir = sensorRecorder.GetSessionDir();

            spatialMeshCapture?.StartCapture(_sessionDir);
            bodyTrackingRecorder?.StartCapture(_sessionDir);

            sensorRecorder.LogAction("task_start", "capture", "user_initiated");

            // Start POV video recording
            _videoStartTimeMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            StartVideoRecording();

            _recording = true;
            _startTime = Time.time;

            if (txtButtonLabel != null) txtButtonLabel.text = "STOP";
            if (btnImage != null) btnImage.color = new Color(0.8f, 0.2f, 0.2f, 1f);
            SetStatus("REC  00:00\n\nFrames: 0");

            Debug.Log("[Controller] RECORDING STARTED -> " + _sessionDir);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[Controller] Failed to start recording: " + e.Message);
            SetStatus("ERROR\n" + e.Message);
        }
    }

    void StopRecording()
    {
        try
        {
            sensorRecorder.LogAction("task_end", "capture", "user_initiated");

            // Stop POV video recording
            StopVideoRecording();

            bodyTrackingRecorder?.StopCapture();
            spatialMeshCapture?.StopCapture();

            // NOTE: StopSession is deferred to end of FindAndCopyVideo so that
            // LogAction("video_saved") can still write to the action log.

            float dur = Time.time - _startTime;
            long frames = sensorRecorder.FrameIndex;

            _recording = false;

            if (txtButtonLabel != null) txtButtonLabel.text = "SAVING...";
            if (btnImage != null) btnImage.color = new Color(0.6f, 0.6f, 0.1f, 1f);
            SetStatus($"Saving video...\n{frames} frames, {dur:F1}s");

            // Wait a moment for video file to finalize, then find and copy it
            StartCoroutine(FindAndCopyVideo(dur, frames));

            Debug.Log($"[Controller] RECORDING STOPPED. {frames} frames, {dur:F1}s.");
        }
        catch (System.Exception e)
        {
            Debug.LogError("[Controller] Failed to stop recording: " + e.Message);
            _recording = false;
            sensorRecorder.StopSession();
            SetIdle();
        }
    }

    void StartVideoRecording()
    {
        _videoBackend = "none";

        if (TryStartVideoEnterprise())
        {
            _videoBackend = "enterprise_record";
            sensorRecorder.LogAction("video_start", _videoBackend, "ok");
            return;
        }

        if (TryStartVideoShellBroadcast())
        {
            _videoBackend = "shell_broadcast";
            sensorRecorder.LogAction("video_start", _videoBackend, "ok");
            return;
        }

        sensorRecorder.LogAction("video_start", "none", "failed");
        Debug.LogWarning("[Video] Failed to start system recording with all backends.");
    }

    void StopVideoRecording()
    {
        bool stopped = false;

        if (_videoBackend == "enterprise_record")
        {
            stopped = TryStopVideoEnterprise();
        }

        if (!stopped && _videoBackend == "shell_broadcast")
        {
            stopped = TryStopVideoShellBroadcast();
        }

        if (!stopped)
        {
            // Last-chance stop attempts when start backend is unknown.
            stopped = TryStopVideoEnterprise() || TryStopVideoShellBroadcast();
        }

        sensorRecorder.LogAction("video_stop", _videoBackend, stopped ? "ok" : "failed");
    }

    bool TryStartVideoEnterprise()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (!InvokeEnterpriseRecordToggle()) return false;
            _enterpriseRecordingToggled = true;
            Debug.Log("[Video] Started recording with PXR_Enterprise.Record().");
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Video] Enterprise start failed: {e.Message}");
        }
#endif
        return false;
    }

    bool TryStopVideoEnterprise()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!_enterpriseRecordingToggled) return false;
        try
        {
            if (!InvokeEnterpriseRecordToggle()) return false;
            _enterpriseRecordingToggled = false;
            Debug.Log("[Video] Stopped recording with PXR_Enterprise.Record().");
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Video] Enterprise stop failed: {e.Message}");
        }
#endif
        return false;
    }

    static bool InvokeEnterpriseRecordToggle()
    {
        try
        {
            var t = System.AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(a => a.GetType("Unity.XR.PXR.PXR_Enterprise"))
                .FirstOrDefault(x => x != null);
            if (t == null) return false;
            var m = t.GetMethod("Record", BindingFlags.Public | BindingFlags.Static);
            if (m == null) return false;
            m.Invoke(null, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    bool TryStartVideoShellBroadcast()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var runtimeClass = new AndroidJavaClass("java.lang.Runtime");
            using var runtime = runtimeClass.CallStatic<AndroidJavaObject>("getRuntime");
            using var proc = runtime?.Call<AndroidJavaObject>("exec", "am broadcast -a com.pico.recorder.action.RECORD_CONTROL --es command start");
            int code = proc != null ? proc.Call<int>("waitFor") : -1;
            if (code == 0)
            {
                Debug.Log("[Video] Shell broadcast start command accepted.");
                return true;
            }
            Debug.LogWarning($"[Video] Shell broadcast start exit code={code}.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Video] Shell broadcast start failed: {e.Message}");
        }
#endif
        return false;
    }

    bool TryStopVideoShellBroadcast()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var runtimeClass = new AndroidJavaClass("java.lang.Runtime");
            using var runtime = runtimeClass.CallStatic<AndroidJavaObject>("getRuntime");
            using var proc = runtime?.Call<AndroidJavaObject>("exec", "am broadcast -a com.pico.recorder.action.RECORD_CONTROL --es command stop");
            int code = proc != null ? proc.Call<int>("waitFor") : -1;
            if (code == 0)
            {
                Debug.Log("[Video] Shell broadcast stop command accepted.");
                return true;
            }
            Debug.LogWarning($"[Video] Shell broadcast stop exit code={code}.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Video] Shell broadcast stop failed: {e.Message}");
        }
#endif
        return false;
    }

    IEnumerator FindAndCopyVideo(float dur, long frames)
    {
        // Give the system recorder time to finalize and retry for up to ~20s.
        string videoPath = null;
        for (int attempt = 0; attempt < 5 && videoPath == null; attempt++)
        {
            yield return new WaitForSeconds(attempt == 0 ? 3f : 4f);
            videoPath = FindNewestVideo();
            Debug.Log($"[Video] Search attempt {attempt + 1}: {(videoPath ?? "none")}");
        }
        string statusMsg;

        if (videoPath != null && _sessionDir != null)
        {
            try
            {
                string destPath = Path.Combine(_sessionDir, "pov_video.mp4");
                File.Copy(videoPath, destPath, true);
                sensorRecorder?.LogAction("video_saved", "pov_video.mp4", $"src={videoPath}");
                Debug.Log($"[Video] Copied to session: {destPath}");
                statusMsg = $"Saved!\n{frames} frames, {dur:F1}s\nVideo: OK";
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Video] Failed to copy video: {e.Message}");
                statusMsg = $"Saved!\n{frames} frames, {dur:F1}s\nVideo at: {videoPath}";
            }
        }
        else
        {
            Debug.LogWarning("[Video] No video file found after recording.");
            sensorRecorder?.LogAction("video_not_found", _videoBackend, "search_timeout");
            statusMsg = $"Saved!\n{frames} frames, {dur:F1}s\nVideo: not found";
        }

        // Now that all logging is done, close the session
        sensorRecorder.StopSession();

        // Reset UI
        if (txtButtonLabel != null) txtButtonLabel.text = "RECORD";
        if (btnImage != null) btnImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);
        SetStatus(statusMsg);
    }

    string FindNewestVideo()
    {
        string best = null;
        long bestTime = _videoStartTimeMs - 5000; // Allow 5s tolerance before start

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var act = up.GetStatic<AndroidJavaObject>("currentActivity");

            foreach (string dir in VideoDirs)
            {
                try
                {
                    using var file = new AndroidJavaObject("java.io.File", dir);
                    if (!file.Call<bool>("exists") || !file.Call<bool>("isDirectory")) continue;

                    var files = file.Call<AndroidJavaObject[]>("listFiles");
                    if (files == null) continue;

                    foreach (var f in files)
                    {
                        if (f == null) continue;
                        string name = f.Call<string>("getName");
                        if (name == null || !name.EndsWith(".mp4")) continue;
                        if (name.ToLower().Contains("tmp")) continue;

                        long modified = f.Call<long>("lastModified");
                        if (modified > bestTime)
                        {
                            bestTime = modified;
                            best = f.Call<string>("getAbsolutePath");
                        }
                        f.Dispose();
                    }
                }
                catch { }
            }

            // Also scan /sdcard recursively for any recent mp4
            if (best == null)
            {
                Debug.Log("[Video] Scanning /sdcard for recent mp4 files...");
                ScanDir("/sdcard", ref best, ref bestTime, 0);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Video] FindNewestVideo error: {e.Message}");
        }
#endif

        if (best != null)
            Debug.Log($"[Video] Found newest video: {best} (modified: {bestTime})");

        return best;
    }

    void ScanDir(string path, ref string best, ref long bestTime, int depth)
    {
        if (depth > 6) return;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var dir = new AndroidJavaObject("java.io.File", path);
            if (!dir.Call<bool>("exists") || !dir.Call<bool>("isDirectory")) return;

            var files = dir.Call<AndroidJavaObject[]>("listFiles");
            if (files == null) return;

            foreach (var f in files)
            {
                if (f == null) continue;
                string name = f.Call<string>("getName");
                if (f.Call<bool>("isDirectory") && !name.StartsWith("."))
                {
                    ScanDir(f.Call<string>("getAbsolutePath"), ref best, ref bestTime, depth + 1);
                }
                else if (name != null && name.EndsWith(".mp4"))
                {
                    long modified = f.Call<long>("lastModified");
                    if (modified > bestTime)
                    {
                        bestTime = modified;
                        best = f.Call<string>("getAbsolutePath");
                    }
                }
                f.Dispose();
            }
        }
        catch { }
#endif
    }

    void SetIdle()
    {
        SetStatus("Press A or Trigger\nto start recording");
        if (txtButtonLabel != null) txtButtonLabel.text = "RECORD";
        if (btnImage != null) btnImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);
    }

    void SetStatus(string msg)
    {
        if (txtStatus != null) txtStatus.text = msg;
    }
}
