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
    // Enterprise recorder toggle can crash/steal focus on some firmware variants.
    // Keep disabled for stable in-app capture flow.
    const bool EnableEnterpriseRecorder = false;

    [Header("Systems")]
    public SensorRecorder sensorRecorder;
    public SyncManager syncManager;
    public SpatialMeshCapture spatialMeshCapture;
    public BodyTrackingRecorder bodyTrackingRecorder;
    public AndroidScreenRecorder androidScreenRecorder;

    [Header("UI")]
    public GameObject hudRoot;
    public bool showHudInHeadset = true;
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
    private string _directVideoPath;
    private bool _projectionStartConfirmed;
    private Coroutine _projectionStartWatchdog;
    private bool _preflightOverrideArmed;
    private float _preflightOverrideUntilS;
    private bool _stallDetectedLogged;
    private string _pendingPreflightResult;

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
        SetHudVisible(true);
        SetIdle();
        Debug.Log("[Controller] Ready. Press A or Trigger to toggle recording.");
        StartCoroutine(LogDiagnostics());
    }

    void SetHudVisible(bool visible)
    {
        if (hudRoot != null)
        {
            hudRoot.SetActive(visible);
        }
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
        try
        {
            CheckControllerInput();

            if (_recording)
            {
                RenderLiveStatus();
                RunRecordingWatchdog();
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Controller] Update error: {e.Message}");
            if (_recording && sensorRecorder != null)
                sensorRecorder.LogAction("update_exception", "recording_loop", e.GetType().Name);
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
        {
            if (!PreflightAllowsStart())
                return;
            StartRecording();
        }
        else
            StopRecording();
    }

    bool PreflightAllowsStart()
    {
        if (sensorRecorder == null) return true;

        bool healthy = sensorRecorder.RunPreflightHealthCheck(out string detail);
        if (healthy)
        {
            _preflightOverrideArmed = false;
            _pendingPreflightResult = $"ok;{detail}";
            return true;
        }

        if (_preflightOverrideArmed && Time.time <= _preflightOverrideUntilS)
        {
            _pendingPreflightResult = $"override;{detail}";
            _preflightOverrideArmed = false;
            return true;
        }

        _preflightOverrideArmed = true;
        _preflightOverrideUntilS = Time.time + 15f;
        Debug.LogWarning($"[Controller] Preflight blocked: {detail}");
        SetStatus("Preflight Warning\nHand tracking or IMU quality is degraded.\nPress RECORD again within 15s to continue anyway.");
        return false;
    }

    void StartRecording()
    {
        try
        {
            // Start sensor recording
            sensorRecorder.StartSession("capture", "general");
            _sessionDir = sensorRecorder.GetSessionDir();
            if (!string.IsNullOrEmpty(_pendingPreflightResult))
            {
                if (_pendingPreflightResult.StartsWith("override;"))
                    sensorRecorder.LogAction("preflight_override", "continue_anyway", _pendingPreflightResult.Substring("override;".Length));
                else
                    sensorRecorder.LogAction("preflight_ok", "health_check", _pendingPreflightResult);
                _pendingPreflightResult = string.Empty;
            }

            spatialMeshCapture?.StartCapture(_sessionDir);
            bodyTrackingRecorder?.StartCapture(_sessionDir);

            sensorRecorder.LogAction("task_start", "capture", "user_initiated");

            // Start POV video recording
            _videoStartTimeMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _directVideoPath = Path.Combine(_sessionDir, "pov_video.mp4");
            StartVideoRecording();

            _recording = true;
            _startTime = Time.time;
            _stallDetectedLogged = false;
            _preflightOverrideArmed = false;

            if (txtButtonLabel != null) txtButtonLabel.text = "STOP";
            if (btnImage != null) btnImage.color = new Color(0.8f, 0.2f, 0.2f, 1f);
            SetStatus("REC  00:00\n\nFrames: 0");
            SetHudVisible(true);

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

            if (dur < 10f)
            {
                sensorRecorder.LogAction("recording_too_short", "duration_warning", $"duration_s={dur:F2}");
                Debug.LogWarning($"[Controller] Recording duration {dur:F2}s is below the 10s training-quality threshold.");
            }

            sensorRecorder.LogAction("quality_snapshot", "live",
                $"hand_real_ratio={sensorRecorder.RealHandFrameRatio:F3};imu_gravity_ratio={sensorRecorder.ImuGravityFrameRatio:F3};body_joints={bodyTrackingRecorder?.LastWrittenJointCount ?? 0}");

            _recording = false;

            SetHudVisible(true);
            if (txtButtonLabel != null) txtButtonLabel.text = "SAVING...";
            if (btnImage != null) btnImage.color = new Color(0.6f, 0.6f, 0.1f, 1f);
            string shortWarn = dur < 10f ? "\nWARNING: recording too short for training quality" : "";
            SetStatus($"Saving video...\n{frames} frames, {dur:F1}s{shortWarn}");

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
        _projectionStartConfirmed = false;

        if (androidScreenRecorder != null && androidScreenRecorder.IsSupported && androidScreenRecorder.StartRecording(_directVideoPath))
        {
            _videoBackend = "android_projection";
            sensorRecorder.LogAction("video_start", _videoBackend, "pending");
            if (_projectionStartWatchdog != null) StopCoroutine(_projectionStartWatchdog);
            _projectionStartWatchdog = StartCoroutine(WaitForProjectionStartOrFallback());
            return;
        }

        if (EnableEnterpriseRecorder && TryStartVideoEnterprise())
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

    IEnumerator WaitForProjectionStartOrFallback()
    {
        const float timeoutS = 6f;
        float startedAt = Time.realtimeSinceStartup;

        while (Time.realtimeSinceStartup - startedAt < timeoutS)
        {
            if (androidScreenRecorder != null)
            {
                string evt = androidScreenRecorder.ConsumeLastEvent();
                if (!string.IsNullOrEmpty(evt))
                {
                    if (evt.StartsWith("started:"))
                    {
                        _projectionStartConfirmed = true;
                        sensorRecorder.LogAction("video_start", _videoBackend, "ok");
                        _projectionStartWatchdog = null;
                        yield break;
                    }

                    if (evt.StartsWith("error:"))
                    {
                        sensorRecorder.LogAction("video_start", _videoBackend, evt);
                        break;
                    }
                }
                else if (androidScreenRecorder.ConsumeStartedSignal())
                {
                    _projectionStartConfirmed = true;
                    sensorRecorder.LogAction("video_start", _videoBackend, "ok");
                    _projectionStartWatchdog = null;
                    yield break;
                }
            }

            yield return null;
        }

        sensorRecorder.LogAction("video_start_timeout", _videoBackend, "projection_not_started_within_6s");
        if (TryStartVideoShellBroadcast())
        {
            _videoBackend = "shell_broadcast";
            sensorRecorder.LogAction("video_start", _videoBackend, "ok_after_projection_fail");
        }
        else
        {
            _videoBackend = "none";
            sensorRecorder.LogAction("video_start", _videoBackend, "failed_after_projection_fail");
        }

        _projectionStartWatchdog = null;
    }

    void StopVideoRecording()
    {
        bool stopped = false;

        if (_projectionStartWatchdog != null)
        {
            StopCoroutine(_projectionStartWatchdog);
            _projectionStartWatchdog = null;
        }

        if (_videoBackend == "android_projection")
        {
            bool projectionWasRecording = _projectionStartConfirmed || (androidScreenRecorder != null && androidScreenRecorder.IsRecording);
            stopped = projectionWasRecording && androidScreenRecorder != null && androidScreenRecorder.StopRecording();
        }

        if (EnableEnterpriseRecorder && _videoBackend == "enterprise_record")
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
            if (EnableEnterpriseRecorder)
                stopped = TryStopVideoEnterprise() || TryStopVideoShellBroadcast();
            else
                stopped = TryStopVideoShellBroadcast();
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
            // Preferred path: send broadcast directly from app context.
            using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var act = up.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = new AndroidJavaObject("android.content.Intent", "com.pico.recorder.action.RECORD_CONTROL"))
            {
                if (act != null && intent != null)
                {
                    intent.Call<AndroidJavaObject>("putExtra", "command", "start");
                    act.Call("sendBroadcast", intent);
                    Debug.Log("[Video] App broadcast start sent.");
                    return true;
                }
            }

            using var runtimeClass = new AndroidJavaClass("java.lang.Runtime");
            using var runtime = runtimeClass.CallStatic<AndroidJavaObject>("getRuntime");
            if (runtime == null) return false;

            // Use full binary path first; fallback to PATH lookup.
            var cmd1 = new string[] { "/system/bin/am", "broadcast", "-a", "com.pico.recorder.action.RECORD_CONTROL", "--es", "command", "start" };
            var cmd2 = new string[] { "am", "broadcast", "-a", "com.pico.recorder.action.RECORD_CONTROL", "--es", "command", "start" };

            AndroidJavaObject proc = null;
            try { proc = runtime.Call<AndroidJavaObject>("exec", cmd1); } catch { proc = runtime.Call<AndroidJavaObject>("exec", cmd2); }
            if (proc != null)
            {
                Debug.Log("[Video] Shell broadcast start command sent.");
                return true;
            }
            Debug.LogWarning("[Video] Shell broadcast start command not launched.");
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
            using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var act = up.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = new AndroidJavaObject("android.content.Intent", "com.pico.recorder.action.RECORD_CONTROL"))
            {
                if (act != null && intent != null)
                {
                    intent.Call<AndroidJavaObject>("putExtra", "command", "stop");
                    act.Call("sendBroadcast", intent);
                    Debug.Log("[Video] App broadcast stop sent.");
                    return true;
                }
            }

            using var runtimeClass = new AndroidJavaClass("java.lang.Runtime");
            using var runtime = runtimeClass.CallStatic<AndroidJavaObject>("getRuntime");
            if (runtime == null) return false;

            var cmd1 = new string[] { "/system/bin/am", "broadcast", "-a", "com.pico.recorder.action.RECORD_CONTROL", "--es", "command", "stop" };
            var cmd2 = new string[] { "am", "broadcast", "-a", "com.pico.recorder.action.RECORD_CONTROL", "--es", "command", "stop" };

            AndroidJavaObject proc = null;
            try { proc = runtime.Call<AndroidJavaObject>("exec", cmd1); } catch { proc = runtime.Call<AndroidJavaObject>("exec", cmd2); }
            if (proc != null)
            {
                Debug.Log("[Video] Shell broadcast stop command sent.");
                return true;
            }
            Debug.LogWarning("[Video] Shell broadcast stop command not launched.");
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
        if (_videoBackend == "android_projection" && !string.IsNullOrEmpty(_directVideoPath))
        {
            for (int i = 0; i < 10; i++)
            {
                if (File.Exists(_directVideoPath) && new FileInfo(_directVideoPath).Length > 0)
                {
                    sensorRecorder?.LogAction("video_saved", "pov_video.mp4", $"src={_directVideoPath}");
                    sensorRecorder.StopSession();
                    if (txtButtonLabel != null) txtButtonLabel.text = "RECORD";
                    if (btnImage != null) btnImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);
                    SetStatus($"Saved!\n{frames} frames, {dur:F1}s\nVideo: OK\n{BuildQualityFooter()}");
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
            sensorRecorder?.LogAction("video_not_found", _videoBackend, "projection_output_missing");
        }

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

        statusMsg += "\n" + BuildQualityFooter();

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

    void RenderLiveStatus()
    {
        float elapsed = Time.time - _startTime;
        int min = (int)(elapsed / 60f);
        int sec = (int)(elapsed % 60f);
        float fps = elapsed > 0.01f ? sensorRecorder.FrameIndex / elapsed : 0f;

        string handStatus = sensorRecorder.LastHandTrackingReal
            ? "<color=#53c653>REAL</color>"
            : "<color=#ff5555>FALLBACK</color>";

        string imuStatus;
        if (sensorRecorder.LastImuAccelMagnitude < 0.05f)
            imuStatus = "<color=#ff5555>DEAD</color>";
        else if (sensorRecorder.LastImuHasGravity)
            imuStatus = "<color=#53c653>RAW+GRAVITY</color>";
        else
            imuStatus = "<color=#f6c343>COMPENSATED</color>";

        string bodyStatus = "<color=#ff5555>OFF</color>";
        if (bodyTrackingRecorder != null)
        {
            if (bodyTrackingRecorder.LastWrittenJointCount >= 24 && !bodyTrackingRecorder.LastFrameUsedFallback)
                bodyStatus = "<color=#53c653>24 joints</color>";
            else if (bodyTrackingRecorder.LastNativeJointCount >= 10)
                bodyStatus = "<color=#f6c343>10 joints</color>";
            else if (bodyTrackingRecorder.LastWrittenJointCount > 0)
                bodyStatus = "<color=#f6c343>SYNTHETIC</color>";
        }

        SetStatus(
            $"REC  {min:D2}:{sec:D2}\n" +
            $"Frames: {sensorRecorder.FrameIndex}  FPS: {fps:F1}/{sensorRecorder.sampleRateHz:F0}\n" +
            $"Hands: {handStatus}  IMU: {imuStatus}  Body: {bodyStatus}");
    }

    void RunRecordingWatchdog()
    {
        if (sensorRecorder == null) return;
        if (Time.realtimeSinceStartup - sensorRecorder.LastSampleRealtimeS > 2f)
        {
            if (!_stallDetectedLogged)
            {
                _stallDetectedLogged = true;
                sensorRecorder.LogAction("stall_detected", "sampling", "no_frame_written_for_gt_2s");
            }
        }
        else
        {
            _stallDetectedLogged = false;
        }
    }

    void OnApplicationPause(bool pause)
    {
        if (_recording && sensorRecorder != null)
            sensorRecorder.LogAction(pause ? "app_pause" : "app_resume", "lifecycle", pause ? "true" : "false");
    }

    void OnApplicationFocus(bool focus)
    {
        if (_recording && sensorRecorder != null)
            sensorRecorder.LogAction(focus ? "app_focus" : "app_unfocus", "lifecycle", focus ? "true" : "false");
    }

    void OnApplicationQuit()
    {
        if (!_recording || sensorRecorder == null) return;

        try
        {
            StopVideoRecording();
            bodyTrackingRecorder?.StopCapture();
            spatialMeshCapture?.StopCapture();
            sensorRecorder.StopSession("session_end_crash", "reason=application_quit");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Controller] Quit cleanup failed: {e.Message}");
        }
    }

    string BuildQualityFooter()
    {
        if (sensorRecorder == null) return "Quality: unavailable";

        float handPct = sensorRecorder.RealHandFrameRatio * 100f;
        float imuPct = sensorRecorder.ImuGravityFrameRatio * 100f;
        string body = bodyTrackingRecorder != null ? bodyTrackingRecorder.LastWrittenJointCount.ToString() : "0";
        return $"Quality H={handPct:F0}% IMU={imuPct:F0}% Body={body}";
    }

    void SetIdle()
    {
        SetHudVisible(true);
        SetStatus("Press A or Trigger\nto start recording");
        if (txtButtonLabel != null) txtButtonLabel.text = "RECORD";
        if (btnImage != null) btnImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);
    }

    void SetStatus(string msg)
    {
        if (txtStatus != null) txtStatus.text = msg;
    }
}
