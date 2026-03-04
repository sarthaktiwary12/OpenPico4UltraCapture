using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
#if PICO_XR
using Unity.XR.PXR;
#endif

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

    [Header("Hand Visualization")]
    public HandVisualizer handVisualizer;
    public bool enableHandPinchToggle = false;
    public bool requireHandNearButtonForPinch = false;
    public float pinchProximityRadius = 0.15f;

    [Header("Capture Guards")]
    public bool blockStartWhenHandTrackingUnavailable = true;

    [Header("Remote Control")]
    public bool enableAdbRemoteControl = true;
    public float remoteCommandPollInterval = 0.25f;

    private bool _recording;
    private bool _handNearButton;
    private float _startTime;
    private bool _triggerWasDown;
    private bool _handTrackingReady;
    private float _handTrackingReadyTime;
    private string _sessionDir;
    private long _videoStartTimeMs;
    private string _videoBackend = "none";
    private bool _enterpriseRecordingToggled;
    private string _directVideoPath;
    private bool _projectionStartConfirmed;
    private bool _projectionRequested;
    private Coroutine _projectionStartWatchdog;
    private bool _stallDetectedLogged;
    private string _pendingPreflightResult;
    private float _nextRemotePollTime;
    private string _remoteCmdPath;
    private string _remoteStatusPath;
#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _wakeLock;
#endif

    // Hand tracking live status (shown on screen in idle state)
    private string _handStatus = "waiting...";

    // Known PICO video save directories
    static readonly string[] VideoDirs = {
        "/sdcard/PICO/SpatialVideo",
        "/sdcard/DCIM/ScreenRecording",
        "/sdcard/PICO/Videos",
        "/sdcard/DCIM/Camera",
        "/sdcard/Movies",
        "/sdcard/DCIM"
    };

    void Awake()
    {
        // Keep update loop alive for ADB remote control even if focus/user-presence changes.
        Application.runInBackground = true;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        TryAcquireWakeLock();
    }

    void Start()
    {
        if (handVisualizer == null)
            handVisualizer = new GameObject("HandVisualizer").AddComponent<HandVisualizer>();

        // Headset-only operation requires pinch as the primary input.
        if (!enableHandPinchToggle)
        {
            enableHandPinchToggle = true;
            Debug.Log("[Controller] Hand pinch toggle forced ON.");
        }

        string recordDir = Path.Combine(Application.persistentDataPath, "record");
        Directory.CreateDirectory(recordDir);
        _remoteCmdPath = Path.Combine(recordDir, "remote_cmd.txt");
        _remoteStatusPath = Path.Combine(recordDir, "remote_status.txt");

        // Keep HUD anchored off to the side so it does not sit in the central view.
        var follower = hudRoot != null ? hudRoot.GetComponent<CanvasFollower>() : null;
        if (follower != null)
        {
            follower.placementMode = CanvasFollower.PlacementMode.AnchorInRoom;
            if (follower.followDistance < 1.8f) follower.followDistance = 1.8f;
            if (Mathf.Abs(follower.rightOffset) < 0.01f) follower.rightOffset = 0.85f;
            if (Mathf.Abs(follower.heightOffset) < 0.01f) follower.heightOffset = -0.10f;
            follower.ReanchorNow();
        }

        if (btnToggle != null) btnToggle.onClick.AddListener(OnToggle);
        SetHudVisible(true);
        SetIdle();
        Debug.Log("[Controller] Ready. Point at button and pinch, or press A/Trigger to toggle recording.");
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
            else
            {
                // Refresh idle status to show live hand tracking state
                if (_handTrackingReady && _handFrameCount % 36 == 0)
                {
                    _handNearButton = IsHandNearButton();
                    SetIdle();
                }
            }

            PollRemoteCommand();
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

        // --- Controller input ---
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

        // --- Hand tracking pinch input ---
        if (!down && enableHandPinchToggle)
            down = CheckHandTrackingPinch();

        if (down && !_triggerWasDown)
        {
            Debug.Log("[Controller] Input -> Toggle (hand or controller)");
            OnToggle();
        }
        _triggerWasDown = down;
    }

    // ----- Hand tracking state -----
    private int _handFrameCount;
    private int _handDiagLogCount;
    private XRHandSubsystem _xrHandSub;
    private bool _xrHandSubSearched;
    private string _handMethod = "";

    bool CheckHandTrackingPinch()
    {
        // Delay to let XR subsystem fully initialize.
        if (!_handTrackingReady)
        {
            if (_handTrackingReadyTime == 0f)
                _handTrackingReadyTime = Time.time + 5f;
            if (Time.time < _handTrackingReadyTime)
                return false;
            _handTrackingReady = true;
            Debug.Log("[Hand] Starting hand tracking polling (5s delay elapsed).");
        }

        _handFrameCount++;
        // Only poll every 3rd frame.
        if (_handFrameCount % 3 != 0)
            return false;

        bool pinchDetected = false;

        // ====================================================================
        // METHOD 1: XR Input devices with HandTracking characteristic
        //   (OpenXR HandInteractionProfile creates these automatically)
        // ====================================================================
        var handDevices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.HandTracking, handDevices);

        foreach (var dev in handDevices)
        {
            // Try trigger (maps to pinch in HandInteractionProfile)
            if (dev.TryGetFeatureValue(CommonUsages.trigger, out float trigVal) && trigVal > 0.5f)
            {
                pinchDetected = true;
                _handMethod = "XRInput-trigger";
            }
            if (dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigBtn) && trigBtn)
            {
                pinchDetected = true;
                _handMethod = "XRInput-triggerBtn";
            }
            // Try grip (maps to grasp in HandInteractionProfile)
            if (dev.TryGetFeatureValue(CommonUsages.grip, out float gripVal) && gripVal > 0.5f)
            {
                pinchDetected = true;
                _handMethod = "XRInput-grip";
            }
            if (dev.TryGetFeatureValue(CommonUsages.primaryButton, out bool primBtn) && primBtn)
            {
                pinchDetected = true;
                _handMethod = "XRInput-primaryBtn";
            }
        }

        // ====================================================================
        // METHOD 2: XRHandSubsystem (com.unity.xr.hands via OpenXR)
        //   Provides raw joint positions - detect pinch by finger distance
        // ====================================================================
        if (!pinchDetected)
        {
            if (!_xrHandSubSearched || (_xrHandSub == null && _handFrameCount % 90 == 0))
            {
                _xrHandSubSearched = true;
                var subs = new List<XRHandSubsystem>();
                SubsystemManager.GetSubsystems(subs);
                _xrHandSub = subs.Count > 0 ? subs[0] : null;
                if (_xrHandSub != null)
                    Debug.Log($"[Hand] XRHandSubsystem found: {_xrHandSub.subsystemDescriptor.id}, running={_xrHandSub.running}");
            }

            if (_xrHandSub != null && _xrHandSub.running)
            {
                if (CheckXRHandPinch(_xrHandSub.rightHand, "R") ||
                    CheckXRHandPinch(_xrHandSub.leftHand, "L"))
                {
                    pinchDetected = true;
                    _handMethod = "XRHands-joint";
                }
            }
        }

        // ====================================================================
        // METHOD 3: PICO native API (may work in OpenXR mode via native lib)
        // ====================================================================
#if PICO_XR
        if (!pinchDetected)
        {
            try
            {
                if (CheckPicoNativePinch(HandType.HandRight, "R") ||
                    CheckPicoNativePinch(HandType.HandLeft, "L"))
                {
                    pinchDetected = true;
                    _handMethod = "PICO-native";
                }
            }
            catch (System.Exception e)
            {
                if (_handDiagLogCount < 3)
                    Debug.LogWarning($"[Hand] PICO native error: {e.Message}");
            }
        }
#endif

        // ====================================================================
        // Update on-screen status every ~0.5s
        // ====================================================================
        if (_handFrameCount % 36 == 0)
            UpdateHandStatus(handDevices);

        if (pinchDetected)
        {
            _handNearButton = IsHandNearButton();
            if (requireHandNearButtonForPinch && !_handNearButton)
            {
                if (_handDiagLogCount < 10)
                {
                    _handDiagLogCount++;
                    Debug.Log($"[Hand] PINCH via {_handMethod} suppressed (hand not near button)");
                }
                return false;
            }
            if (_handDiagLogCount < 10)
            {
                _handDiagLogCount++;
                Debug.Log($"[Hand] PINCH via {_handMethod}");
            }
            return true;
        }

        return false;
    }

    bool CheckXRHandPinch(XRHand hand, string label)
    {
        if (!hand.isTracked) return false;
        var thumbTip = hand.GetJoint(XRHandJointID.ThumbTip);
        var indexTip = hand.GetJoint(XRHandJointID.IndexTip);
        if (!thumbTip.TryGetPose(out Pose tp) || !indexTip.TryGetPose(out Pose ip))
            return false;
        float dist = Vector3.Distance(tp.position, ip.position);
        return dist < 0.03f; // 3cm pinch threshold
    }

#if PICO_XR
    bool CheckPicoNativePinch(HandType hand, string label)
    {
        var aim = new HandAimState();
        if (!PXR_Plugin.HandTracking.UPxr_GetHandTrackerAimState(hand, ref aim))
            return false;
        if ((aim.aimStatus & HandAimStatus.AimComputed) == 0)
            return false;
        return (aim.aimStatus & HandAimStatus.AimIndexPinching) != 0
            || (aim.aimStatus & HandAimStatus.AimRayTouched) != 0;
    }
#endif

    void UpdateHandStatus(List<InputDevice> handDevices)
    {
        var sb = new StringBuilder();

        // Hand tracking XR input devices
        sb.Append($"HandDevs:{handDevices.Count}");

        // XRHandSubsystem status
        if (_xrHandSub != null && _xrHandSub.running)
        {
            bool rT = _xrHandSub.rightHand.isTracked;
            bool lT = _xrHandSub.leftHand.isTracked;
            sb.Append($" Sub:R={rT},L={lT}");
        }
        else
        {
            sb.Append(_xrHandSub != null ? " Sub:stopped" : " Sub:none");
        }

        // PICO native status
#if PICO_XR
        try
        {
            var activeInput = PXR_Plugin.HandTracking.UPxr_GetHandTrackerActiveInputType();
            sb.Append($" PICO:{activeInput}");
        }
        catch { sb.Append(" PICO:err"); }
#endif

        // Log details a few times
        if (_handDiagLogCount < 5)
        {
            _handDiagLogCount++;
            Debug.Log($"[Hand] {sb}");

            // Log all XR devices for diagnostics
            var allDevs = new List<InputDevice>();
            InputDevices.GetDevices(allDevs);
            foreach (var d in allDevs)
                Debug.Log($"[Hand]   dev: {d.name} chars={d.characteristics}");

#if PICO_XR
            try
            {
                bool settingOn = PXR_Plugin.HandTracking.UPxr_GetHandTrackerSettingState();
                var aimR = new HandAimState();
                bool gotR = PXR_Plugin.HandTracking.UPxr_GetHandTrackerAimState(HandType.HandRight, ref aimR);
                Debug.Log($"[Hand]   PICO setting={settingOn}, R.got={gotR}, R.status={aimR.aimStatus}");
            }
            catch (System.Exception e) { Debug.Log($"[Hand]   PICO err: {e.Message}"); }
#endif
        }

        _handStatus = sb.ToString();
    }

    bool IsHandNearButton()
    {
        if (btnToggle == null) return true;
        Vector3 btnPos = btnToggle.transform.position;

        if (handVisualizer != null)
        {
            if (handVisualizer.RightHandTracked &&
                Vector3.Distance(handVisualizer.RightIndexTipPosition, btnPos) < pinchProximityRadius)
                return true;
            if (handVisualizer.LeftHandTracked &&
                Vector3.Distance(handVisualizer.LeftIndexTipPosition, btnPos) < pinchProximityRadius)
                return true;
        }

        // Fallback: read XRHandSubsystem directly
        if (_xrHandSub != null && _xrHandSub.running)
        {
            if (CheckJointNearButton(_xrHandSub.rightHand, btnPos) ||
                CheckJointNearButton(_xrHandSub.leftHand, btnPos))
                return true;
        }

        return false;
    }

    bool CheckJointNearButton(XRHand hand, Vector3 btnPos)
    {
        if (!hand.isTracked) return false;
        var indexTip = hand.GetJoint(XRHandJointID.IndexTip);
        if (!indexTip.TryGetPose(out Pose pose)) return false;
        return Vector3.Distance(pose.position, btnPos) < pinchProximityRadius;
    }

    void PollRemoteCommand()
    {
        if (!enableAdbRemoteControl) return;
        if (Time.unscaledTime < _nextRemotePollTime) return;
        _nextRemotePollTime = Time.unscaledTime + Mathf.Max(0.05f, remoteCommandPollInterval);
        if (string.IsNullOrEmpty(_remoteCmdPath) || !File.Exists(_remoteCmdPath)) return;

        string cmd;
        try
        {
            cmd = File.ReadAllText(_remoteCmdPath).Trim().ToLowerInvariant();
            File.Delete(_remoteCmdPath);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Remote] Failed to read command: {e.Message}");
            return;
        }

        if (string.IsNullOrEmpty(cmd)) return;
        HandleRemoteCommand(cmd);
    }

    // Entry-point for Android BroadcastReceiver (UnitySendMessage target).
    public void OnRemoteCommandFromAndroid(string command)
    {
        if (!enableAdbRemoteControl) return;
        if (string.IsNullOrEmpty(command)) return;

        var cmd = command.Trim().ToLowerInvariant();
        HandleRemoteCommand(cmd);
    }

    void HandleRemoteCommand(string cmd)
    {
        if (string.IsNullOrEmpty(cmd)) return;
        Debug.Log($"[Remote] Command: {cmd}");

        switch (cmd)
        {
            case "start":
                if (!_recording && PreflightAllowsStart())
                    StartRecording();
                break;
            case "stop":
                if (_recording)
                    StopRecording();
                break;
            case "toggle":
                OnToggle();
                break;
            case "status":
                WriteRemoteStatus();
                break;
            case "reanchor":
                {
                    var follower = hudRoot != null ? hudRoot.GetComponent<CanvasFollower>() : null;
                    follower?.ReanchorNow();
                    WriteRemoteStatus();
                    break;
                }
            default:
                Debug.LogWarning($"[Remote] Unknown command: {cmd}");
                break;
        }
    }

    void WriteRemoteStatus()
    {
        if (string.IsNullOrEmpty(_remoteStatusPath)) return;
        try
        {
            long frames = sensorRecorder != null ? sensorRecorder.FrameIndex : -1;
            string session = string.IsNullOrEmpty(_sessionDir) ? "none" : _sessionDir;
            string text = $"recording={_recording}\nframes={frames}\nsession={session}\nhand_near_button={_handNearButton}\n";
            File.WriteAllText(_remoteStatusPath, text);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Remote] Failed to write status: {e.Message}");
        }
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

        bool handReady = sensorRecorder.IsHandTrackingReadyForCapture(out string handDetail);
        if (blockStartWhenHandTrackingUnavailable && !handReady)
        {
            _pendingPreflightResult = $"blocked;hand={handDetail}";
            Debug.LogWarning($"[Controller] Start blocked: hand tracking unavailable ({handDetail})");
            SetStatus("Hand tracking not ready.\nEnable hands in PICO system settings and keep hands in view.");
            return false;
        }

        bool healthy = sensorRecorder.RunPreflightHealthCheck(out string detail);
        if (healthy)
        {
            _pendingPreflightResult = $"ok;{detail}";
            return true;
        }

        // Non-blocking: warn but continue so start/stop stays single-press.
        _pendingPreflightResult = $"warn;{detail}";
        Debug.LogWarning($"[Controller] Preflight warning (continuing): {detail}");
        return true;
    }

    void StartRecording()
    {
        if (sensorRecorder == null)
        {
            Debug.LogError("[Controller] sensorRecorder is not assigned.");
            SetStatus("ERROR\nSensorRecorder missing");
            return;
        }

        try
        {
            // Start sensor recording
            sensorRecorder.StartSession("capture", "general");
            _sessionDir = sensorRecorder.GetSessionDir();
            if (!string.IsNullOrEmpty(_pendingPreflightResult))
            {
                if (_pendingPreflightResult.StartsWith("warn;"))
                    sensorRecorder.LogAction("preflight_warning", "health_check", _pendingPreflightResult.Substring("warn;".Length));
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

            if (txtButtonLabel != null) txtButtonLabel.text = "STOP";
            if (btnImage != null) btnImage.color = new Color(0.8f, 0.2f, 0.2f, 1f);
            SetStatus("REC  00:00\n\nFrames: 0");
            SetHudVisible(true);

            Debug.Log("[Controller] RECORDING STARTED -> " + _sessionDir);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[Controller] Failed to start recording: " + e.Message);
            if (sensorRecorder.IsRecording)
            {
                try
                {
                    StopVideoRecording();
                    bodyTrackingRecorder?.StopCapture();
                    spatialMeshCapture?.StopCapture();
                    sensorRecorder.StopSession("session_end_crash", "reason=start_exception");
                }
                catch (System.Exception cleanupErr)
                {
                    Debug.LogWarning("[Controller] Start failure cleanup error: " + cleanupErr.Message);
                }
            }
            _recording = false;
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
        _projectionRequested = false;

        if (androidScreenRecorder != null && androidScreenRecorder.IsSupported && androidScreenRecorder.StartRecording(_directVideoPath))
        {
            _videoBackend = "android_projection";
            _projectionRequested = true;
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
        // Keep android_projection backend after timeout. On some firmware builds the "started"
        // callback can be delayed/lost even though MediaProjection is active; stopping projection
        // explicitly at session end produces a finalized mp4.
        sensorRecorder.LogAction("video_start", _videoBackend, "pending_after_timeout");

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
            bool projectionFilePresent = !string.IsNullOrEmpty(_directVideoPath) && File.Exists(_directVideoPath);
            bool projectionWasRecording = _projectionRequested ||
                                          _projectionStartConfirmed ||
                                          (androidScreenRecorder != null && androidScreenRecorder.IsRecording) ||
                                          projectionFilePresent;
            stopped = projectionWasRecording && androidScreenRecorder != null && androidScreenRecorder.StopRecording();
            _projectionRequested = false;
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
        if (!pause)
            TryAcquireWakeLock();
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
        ReleaseWakeLock();
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

    void OnDestroy()
    {
        ReleaseWakeLock();
    }

    void TryAcquireWakeLock()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            if (activity == null) return;

            // Keep display active while the app is running in foreground.
            using (var window = activity.Call<AndroidJavaObject>("getWindow"))
            using (var lp = new AndroidJavaClass("android.view.WindowManager$LayoutParams"))
            {
                int flagKeepScreenOn = lp.GetStatic<int>("FLAG_KEEP_SCREEN_ON");
                window?.Call("addFlags", flagKeepScreenOn);
            }

            if (_wakeLock != null)
            {
                bool held = false;
                try { held = _wakeLock.Call<bool>("isHeld"); } catch { held = false; }
                if (held) return;
            }

            using var powerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
            if (powerManager == null) return;
            using var pmClass = new AndroidJavaClass("android.os.PowerManager");
            int partialWakeLock = pmClass.GetStatic<int>("PARTIAL_WAKE_LOCK");
            _wakeLock = powerManager.Call<AndroidJavaObject>("newWakeLock", partialWakeLock, "OpenPicoCapture:WakeLock");
            if (_wakeLock == null) return;
            _wakeLock.Call("setReferenceCounted", false);
            _wakeLock.Call("acquire");
            Debug.Log("[Controller] Wake lock acquired.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Controller] Wake lock acquire failed: {e.Message}");
        }
#endif
    }

    void ReleaseWakeLock()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_wakeLock == null) return;
        try
        {
            bool held = false;
            try { held = _wakeLock.Call<bool>("isHeld"); } catch { held = false; }
            if (held)
                _wakeLock.Call("release");
            _wakeLock.Dispose();
            _wakeLock = null;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Controller] Wake lock release failed: {e.Message}");
        }
#endif
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
        string hint;
        if (requireHandNearButtonForPinch)
            hint = _handNearButton ? "Pinch to record!" : "Move hand to button";
        else
            hint = "Pinch anywhere or press A";
        SetStatus($"{hint}\nHands: {_handStatus}");
        if (txtButtonLabel != null) txtButtonLabel.text = "RECORD";
        if (btnImage != null) btnImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);
    }

    void SetStatus(string msg)
    {
        if (txtStatus != null) txtStatus.text = msg;
    }
}
