using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    [Header("Systems")]
    public SensorRecorder sensorRecorder;
    public SyncManager syncManager;
    public SpatialMeshCapture spatialMeshCapture;
    public BodyTrackingRecorder bodyTrackingRecorder;
    public CameraPermissionManager cameraPermissionManager;

    [Header("Camera2 Settings")]
    public int videoFps = 30;
    public int videoBitrateMbps = 8;
    public int videoIFrameInterval = 1;

    [Header("UI")]
    public GameObject hudRoot;
    public bool showHudInHeadset = true;
    public Button btnToggle;
    public Text txtStatus;
    public Text txtButtonLabel;
    public Image btnImage;

    [Header("Hand Visualization")]
    public HandVisualizer handVisualizer;

    [Header("Capture Guards")]
    public bool blockStartWhenHandTrackingUnavailable = false;

    [Header("Clap Control")]
    public bool enableClapToggle = true;
    public float clapWarmupSeconds = 5f;
    public float clapTriggerDistanceM = 0.10f;
    public float clapResetDistanceM = 0.25f;
    public float clapMinClosingSpeedMps = 0.20f;
    public float clapCooldownSeconds = 1.5f;
    public bool requireBothHandsTrackedForClap = true;

    [Header("Remote Control")]
    public bool enableAdbRemoteControl = false;
    public float remoteCommandPollInterval = 0.25f;

    private bool _recording;
    private float _startTime;
    private bool _handTrackingReady;
    private float _handTrackingReadyTime;
    private string _sessionDir;
    private long _videoStartTimeMs;
    private string _videoBackend = "none";
    private string _directVideoPath;
    private long _videoStopTimeMs;
    private bool _stallDetectedLogged;
    private string _pendingPreflightResult;
    private readonly List<string> _videoSearchDiagnostics = new List<string>(64);
    private float _nextRemotePollTime;
    private string _remoteCmdPath;
    private string _remoteStatusPath;
#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _wakeLock;
#endif

    // Hand tracking live status (shown on screen in idle state)
    private string _handStatus = "waiting...";

    // Camera2 video capture
    private Camera2SessionManager _cam2Session;
    private Camera2VideoRecorder _cam2Recorder;

    // Known PICO video save directories
    static readonly string[] VideoDirs = {
        "/sdcard/PICO/SpatialVideo",
        "/sdcard/Movies/SpatialMP4",
        "/sdcard/Movies/ScreenRecording",
        "/sdcard/DCIM/ScreenRecording",
        "/sdcard/PICO/Videos",
        "/sdcard/DCIM/Camera",
        "/sdcard/Movies",
        "/sdcard/DCIM"
    };

    void Awake()
    {
        // Keep update loop alive even if focus/user-presence changes.
        Application.runInBackground = true;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        TryAcquireWakeLock();
    }

    void Start()
    {
        if (cameraPermissionManager == null)
            cameraPermissionManager = FindObjectOfType<CameraPermissionManager>();
        if (cameraPermissionManager == null)
        {
            var go = new GameObject("CameraPermissionManager");
            cameraPermissionManager = go.AddComponent<CameraPermissionManager>();
            Debug.Log("[Controller] Auto-created CameraPermissionManager");
        }

        if (handVisualizer == null)
            handVisualizer = new GameObject("HandVisualizer").AddComponent<HandVisualizer>();

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
        Debug.Log("[Controller] Ready. Clap hands to start/stop recording (hands-only mode).");
        StartCoroutine(LogDiagnostics());
        StartCoroutine(InitHandTrackingRuntime());
    }

    IEnumerator InitHandTrackingRuntime()
    {
        // Wait for XR session to be fully ready before initializing hand tracking
        yield return new WaitForSeconds(3f);

#if PICO_XR
        // Step 1: Request hand tracking permission at runtime
        try
        {
            Debug.Log("[Controller] Requesting hand tracking permission at runtime...");
            using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var act = up.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                act.Call("requestPermissions", new string[] { "com.picovr.permission.HAND_TRACKING" }, 9999);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Controller] Hand tracking permission request failed: {e.Message}");
        }

        yield return new WaitForSeconds(1f);

        // Step 2: Try to ensure OpenXR HandTracking subsystem is initialized
        try
        {
            var handTrackingType = System.Type.GetType(
                "UnityEngine.XR.Hands.OpenXR.HandTracking, Unity.XR.Hands");
            if (handTrackingType != null)
            {
                var ensureMethod = handTrackingType.GetMethod("EnsureSubsystemInitialized",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (ensureMethod != null)
                {
                    ensureMethod.Invoke(null, null);
                    Debug.Log("[Controller] HandTracking subsystem initialized via runtime call");
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Controller] HandTracking subsystem init failed: {e.Message}");
        }

        // Step 3: Activate dual input mode (controller + hand tracking simultaneously)
        yield return ActivateDualInputMode();

        // Step 4: Force-enable passthrough as a safety net
        var ptEnabler = FindObjectOfType<PassthroughEnabler>();
        if (ptEnabler != null)
        {
            yield return new WaitForSeconds(1f);
            ptEnabler.ForceEnable();
            Debug.Log("[Controller] Passthrough ForceEnable called as safety net.");
        }

        // Now that hand tracking is activated, enable the recording guard
        blockStartWhenHandTrackingUnavailable = true;
        Debug.Log("[Controller] Hand tracking init complete. Recording guard enabled.");
#endif
    }

#if PICO_XR
    IEnumerator ActivateDualInputMode()
    {
        // SetActiveInputDevice tells PICO runtime to enable BOTH controller and hand tracking
        // without requiring manifest metadata (which causes startup hang on current firmware).
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool setInputOk = TrySetControllerAndHandActiveInputDevice();
            if (setInputOk)
                Debug.Log($"[Controller] SetActiveInputDevice(ControllerAndHandActive) called (attempt {attempt + 1})");
            else
                Debug.LogWarning($"[Controller] SetActiveInputDevice API not available in this SDK (attempt {attempt + 1}).");

            yield return new WaitForSeconds(1.5f);

            // Verify it took effect
            try
            {
                bool settingOn = PXR_Plugin.HandTracking.UPxr_GetHandTrackerSettingState();
                var activeInput = PXR_Plugin.HandTracking.UPxr_GetHandTrackerActiveInputType();
                Debug.Log($"[Controller] Hand tracking verify: setting={settingOn}, activeInput={activeInput}");

                if (settingOn)
                {
                    Debug.Log("[Controller] Hand tracking runtime activation succeeded.");
                    yield break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Controller] Hand tracking verify failed: {e.Message}");
            }

            if (attempt < 2)
            {
                Debug.Log($"[Controller] Hand tracking not ready yet, retrying in 2s...");
                yield return new WaitForSeconds(2f);
            }
        }

        Debug.LogWarning("[Controller] Hand tracking runtime activation did not confirm after 3 attempts. Clap input may not work.");
    }

    static bool TrySetControllerAndHandActiveInputDevice()
    {
        try
        {
            var handTrackingType = typeof(PXR_Plugin.HandTracking);
            var setMethod = handTrackingType.GetMethod(
                "UPxr_SetActiveInputDevice",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (setMethod == null)
                return false;

            var enumType = handTrackingType.Assembly.GetType("Unity.XR.PXR.ActiveInputDevice");
            if (enumType == null)
                return false;

            string enumName = null;
            if (System.Enum.IsDefined(enumType, "ControllerAndHandActive"))
                enumName = "ControllerAndHandActive";
            else if (System.Enum.IsDefined(enumType, "ControllerAndHand"))
                enumName = "ControllerAndHand";

            if (string.IsNullOrEmpty(enumName))
                return false;

            object enumValue = System.Enum.Parse(enumType, enumName);
            setMethod.Invoke(null, new[] { enumValue });
            return true;
        }
        catch
        {
            return false;
        }
    }
#endif

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
            CheckClapInput();

            if (_recording)
            {
                RenderLiveStatus();
                RunRecordingWatchdog();
            }
            else
            {
                // Refresh idle status to show live hand tracking state
                if (_handTrackingReady && _handFrameCount % 36 == 0)
                    SetIdle();
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

    // ----- Hand tracking state -----
    private int _handFrameCount;
    private int _handDiagLogCount;
    private XRHandSubsystem _xrHandSub;
    private bool _xrHandSubSearched;
    private bool _clapArmed = true;
    private float _lastClapTime = -99f;
    private float _lastPalmSampleTime = -1f;
    private float _lastPalmDistance = -1f;
    private float _lastClapClosingSpeed;
    private string _lastClapSource = "none";

    void CheckClapInput()
    {
        if (!enableClapToggle)
            return;

        // Delay to let XR subsystems fully initialize after app launch.
        if (!_handTrackingReady)
        {
            if (_handTrackingReadyTime == 0f)
                _handTrackingReadyTime = Time.time + Mathf.Max(0f, clapWarmupSeconds);

            float remaining = _handTrackingReadyTime - Time.time;
            if (remaining > 0f)
            {
                _handStatus = $"warming_up {remaining:F1}s";
                return;
            }

            _handTrackingReady = true;
            Debug.Log("[Hand] Starting clap polling.");
        }

        _handFrameCount++;
        // Poll every other frame to keep runtime overhead low.
        if (_handFrameCount % 2 != 0)
            return;

        if (!TryGetPalmPositions(out Vector3 leftPalm, out Vector3 rightPalm, out string source))
        {
            _lastPalmSampleTime = -1f;
            _lastPalmDistance = -1f;
            _handStatus = "tracking_lost";
            return;
        }

        float now = Time.time;
        float palmDistance = Vector3.Distance(leftPalm, rightPalm);
        float closingSpeed = 0f;
        if (_lastPalmSampleTime > 0f)
        {
            float dt = Mathf.Max(0.0001f, now - _lastPalmSampleTime);
            // Positive speed means palms are moving closer together.
            closingSpeed = (_lastPalmDistance - palmDistance) / dt;
        }

        _lastPalmSampleTime = now;
        _lastPalmDistance = palmDistance;

        if (!_clapArmed && palmDistance >= clapResetDistanceM)
            _clapArmed = true;

        if (_handFrameCount % 24 == 0)
            _handStatus = $"src={source} d={palmDistance:F2}m v={closingSpeed:F2}m/s armed={_clapArmed}";

        bool cooldownActive = now - _lastClapTime < clapCooldownSeconds;
        bool clapDetected = _clapArmed
            && !cooldownActive
            && palmDistance <= clapTriggerDistanceM
            && closingSpeed >= clapMinClosingSpeedMps;

        if (!clapDetected)
            return;

        _clapArmed = false;
        _lastClapTime = now;
        _lastClapClosingSpeed = closingSpeed;
        _lastClapSource = source;

        if (_handDiagLogCount < 12)
        {
            _handDiagLogCount++;
            Debug.Log($"[Hand] CLAP via {source}, dist={palmDistance:F3}m speed={closingSpeed:F2}m/s");
        }

        if (!_recording)
        {
            Debug.Log("[Hand] CLAP -> Start recording");
            if (PreflightAllowsStart())
                StartRecording();
        }
        else
        {
            Debug.Log("[Hand] CLAP -> Stop recording");
            StopRecording();
        }
    }

    bool TryGetPalmPositions(out Vector3 leftPalm, out Vector3 rightPalm, out string source)
    {
        leftPalm = Vector3.zero;
        rightPalm = Vector3.zero;
        source = "none";

        if (TryGetPalmsFromXRHands(out leftPalm, out rightPalm))
        {
            source = "XRHands";
            return true;
        }

#if PICO_XR
        if (TryGetPalmFromPico(HandType.HandLeft, out leftPalm) &&
            TryGetPalmFromPico(HandType.HandRight, out rightPalm))
        {
            source = "PICO-native";
            return true;
        }
#endif

        if (TryGetPalmFromXRNode(XRNode.LeftHand, out leftPalm) &&
            TryGetPalmFromXRNode(XRNode.RightHand, out rightPalm))
        {
            source = "XRInput";
            return true;
        }

        return false;
    }

    bool TryGetPalmsFromXRHands(out Vector3 leftPalm, out Vector3 rightPalm)
    {
        leftPalm = Vector3.zero;
        rightPalm = Vector3.zero;

        if (!_xrHandSubSearched || (_xrHandSub == null && _handFrameCount % 90 == 0))
        {
            _xrHandSubSearched = true;
            var subs = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            _xrHandSub = subs.Count > 0 ? subs[0] : null;
            if (_xrHandSub != null && _handDiagLogCount < 4)
            {
                _handDiagLogCount++;
                Debug.Log($"[Hand] XRHandSubsystem found: {_xrHandSub.subsystemDescriptor.id}, running={_xrHandSub.running}");
            }
        }

        if (_xrHandSub == null || !_xrHandSub.running)
            return false;

        bool leftTracked = _xrHandSub.leftHand.isTracked;
        bool rightTracked = _xrHandSub.rightHand.isTracked;
        if (requireBothHandsTrackedForClap && (!leftTracked || !rightTracked))
            return false;

        if (!TryGetPalmFromXRHand(_xrHandSub.leftHand, out leftPalm))
            return false;
        if (!TryGetPalmFromXRHand(_xrHandSub.rightHand, out rightPalm))
            return false;

        return true;
    }

    static bool TryGetPalmFromXRHand(XRHand hand, out Vector3 palm)
    {
        palm = Vector3.zero;
        if (!hand.isTracked)
            return false;

        var palmJoint = hand.GetJoint(XRHandJointID.Palm);
        if (palmJoint.TryGetPose(out Pose palmPose))
        {
            palm = palmPose.position;
            return true;
        }

        var wristJoint = hand.GetJoint(XRHandJointID.Wrist);
        if (wristJoint.TryGetPose(out Pose wristPose))
        {
            palm = wristPose.position;
            return true;
        }

        return false;
    }

#if PICO_XR
    static bool TryGetPalmFromPico(HandType handType, out Vector3 palm)
    {
        palm = Vector3.zero;
        try
        {
            var jointLocations = new HandJointLocations();
            if (!PXR_Plugin.HandTracking.UPxr_GetHandTrackerJointLocations(handType, ref jointLocations))
                return false;
            if (jointLocations.jointLocations == null || jointLocations.jointLocations.Length == 0)
                return false;

            var rootJoint = jointLocations.jointLocations[0];
            if (((ulong)rootJoint.locationStatus & (ulong)HandLocationStatus.PositionValid) == 0)
                return false;

            palm = rootJoint.pose.Position.ToVector3();
            return true;
        }
        catch
        {
            return false;
        }
    }
#endif

    static bool TryGetPalmFromXRNode(XRNode handNode, out Vector3 palm)
    {
        palm = Vector3.zero;
        var device = InputDevices.GetDeviceAtXRNode(handNode);
        if (!device.isValid)
            return false;

        if (!device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position))
            return false;

        palm = position;
        return true;
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
            string text = $"recording={_recording}\nframes={frames}\nsession={session}\n";
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
            _videoStopTimeMs = 0;
            _directVideoPath = Path.Combine(_sessionDir, "pov_video.mp4");
            _videoSearchDiagnostics.Clear();
            AddVideoDiag($"start_ms={_videoStartTimeMs}");
            AddVideoDiag($"backend_pre={_videoBackend}");
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
            sensorRecorder.MarkFinalizePhaseStart();
            _videoStopTimeMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Stop POV video recording
            StopVideoRecording();

            bodyTrackingRecorder?.StopCapture();
            spatialMeshCapture?.StopCapture();

            if (bodyTrackingRecorder != null)
            {
                sensorRecorder.UpdateBodyMetrics(
                    bodyTrackingRecorder.NativeFrameRatio,
                    bodyTrackingRecorder.FallbackOnlyFrameRatio,
                    bodyTrackingRecorder.FramesSampled);
            }

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
                $"hand_real_ratio={sensorRecorder.RealHandFrameRatio:F3};imu_gravity_ratio={sensorRecorder.ImuGravityFrameRatio:F3};body_joints={bodyTrackingRecorder?.LastWrittenJointCount ?? 0};body_native_ratio={bodyTrackingRecorder?.NativeFrameRatio ?? 0f:F3};body_fallback_ratio={bodyTrackingRecorder?.FallbackOnlyFrameRatio ?? 0f:F3}");

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

        // Primary: Camera2 API via questcameralib.aar
        if (TryStartCamera2Video())
        {
            _videoBackend = "camera2";
            sensorRecorder.LogAction("video_start", _videoBackend, "pending");
            AddVideoDiag("camera2_start=pending");
            return;
        }

        // Fallback: shell broadcast to system recorder
        if (TryStartVideoShellBroadcast())
        {
            _videoBackend = "shell_broadcast";
            sensorRecorder.LogAction("video_start", _videoBackend, "pending_dispatch_ok");
            AddVideoDiag("shell_start=dispatch_ok");
            return;
        }

        sensorRecorder.LogAction("video_start", "none", "failed");
        Debug.LogWarning("[Video] Failed to start recording with all backends.");
    }

    bool TryStartCamera2Video()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (cameraPermissionManager == null || !cameraPermissionManager.IsReady)
            {
                Debug.LogWarning("[Video] CameraPermissionManager not ready");
                return false;
            }

            var cameraManager = cameraPermissionManager.CameraManager;
            string cameraId = cameraPermissionManager.SelectedCameraId;
            int w = cameraPermissionManager.SelectedWidth;
            int h = cameraPermissionManager.SelectedHeight;

            if (cameraManager == null || string.IsNullOrEmpty(cameraId))
            {
                Debug.LogWarning("[Video] No camera available");
                return false;
            }

            // Create recorder
            _cam2Recorder = new Camera2VideoRecorder();
            if (!_cam2Recorder.Initialize(w, h, _directVideoPath, videoFps, videoBitrateMbps, videoIFrameInterval))
            {
                Debug.LogError("[Video] Camera2 recorder init failed");
                _cam2Recorder = null;
                return false;
            }

            // Open camera session
            _cam2Session = new Camera2SessionManager();
            if (!_cam2Session.Open(cameraManager, cameraId, _cam2Recorder.JavaInstance))
            {
                Debug.LogError("[Video] Camera2 session open failed");
                _cam2Recorder.Close();
                _cam2Recorder = null;
                _cam2Session = null;
                return false;
            }

            // Wait briefly for session to be ready, then start recording
            StartCoroutine(StartCamera2RecordingDelayed());
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[Video] Camera2 start error: {e.Message}\n{e.StackTrace}");
            CleanupCamera2();
            return false;
        }
#else
        return false;
#endif
    }

    IEnumerator StartCamera2RecordingDelayed()
    {
        // Give Camera2 session time to establish
        yield return new WaitForSeconds(0.5f);

        float deadline = Time.realtimeSinceStartup + 3f;
        while (_cam2Session != null && !_cam2Session.IsOpen && Time.realtimeSinceStartup < deadline)
            yield return null;

        if (_cam2Recorder != null)
        {
            if (_cam2Recorder.StartRecording())
            {
                Debug.Log("[Video] Camera2 recording started successfully");
                sensorRecorder?.LogAction("video_start", "camera2", "ok");
                AddVideoDiag("camera2_start=ok");
            }
            else
            {
                Debug.LogError("[Video] Camera2 StartRecording call failed");
                sensorRecorder?.LogAction("video_start", "camera2", "failed");
                AddVideoDiag("camera2_start=failed");
                if (TryStartVideoShellBroadcast())
                {
                    _videoBackend = "shell_broadcast";
                    sensorRecorder?.LogAction("video_start", "shell_broadcast", "pending_dispatch_ok");
                    AddVideoDiag("camera2_start_failed_fallback_shell=dispatch_ok");
                }
            }
        }
    }

    void CleanupCamera2()
    {
        _cam2Recorder?.Close();
        _cam2Recorder = null;
        _cam2Session?.Close();
        _cam2Session = null;
    }

    void StopVideoRecording()
    {
        bool stopped = false;

        if (_videoBackend == "camera2")
        {
            stopped = _cam2Recorder?.StopRecording() ?? false;
            // Close session after stopping recording
            _cam2Session?.Close();
            _cam2Session = null;
            // Don't close recorder yet — let the file finalize. Close in FindAndCopyVideo.
            sensorRecorder.LogAction("video_stop", _videoBackend, stopped ? "ok" : "failed");
            AddVideoDiag($"camera2_stop={(stopped ? "ok" : "failed")}");
            return;
        }

        if (!stopped && _videoBackend == "shell_broadcast")
        {
            stopped = TryStopVideoShellBroadcast();
        }

        if (!stopped && _videoBackend != "camera2")
        {
            stopped = TryStopVideoShellBroadcast();
        }

        // Broadcast dispatch success is not proof that the recorder finalized correctly.
        sensorRecorder.LogAction("video_stop", _videoBackend, stopped ? "pending_dispatch_ok" : "failed");
        AddVideoDiag($"shell_stop={(stopped ? "dispatch_ok" : "failed")}");
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
        bool videoSaved = false;
        string failureReason = "unknown";
        string resolvedPath = null;
        long resolvedSize = 0;

        // Camera2 writes directly to _directVideoPath.
        if (_videoBackend == "camera2" && !string.IsNullOrEmpty(_directVideoPath))
        {
            _cam2Recorder?.Close();
            _cam2Recorder = null;

            long lastSize = -1;
            int stableHits = 0;
            for (int i = 0; i < 24; i++)
            {
                if (TryGetFileSize(_directVideoPath, out long size))
                {
                    if (size == lastSize && size > 0) stableHits++;
                    else stableHits = 0;
                    lastSize = size;
                    if (stableHits >= 2)
                    {
                        videoSaved = true;
                        resolvedPath = _directVideoPath;
                        resolvedSize = size;
                        break;
                    }
                }
                yield return new WaitForSeconds(0.5f);
            }

            if (!videoSaved)
            {
                failureReason = "camera2_output_missing_or_unstable";
                sensorRecorder?.LogAction("video_save_failed", _videoBackend, failureReason);
                sensorRecorder?.LogAction("video_not_found", _videoBackend, failureReason);
            }
        }

        if (!videoSaved)
        {
            string candidate = null;
            long candidateModified = 0;
            for (int attempt = 0; attempt < 8 && !videoSaved; attempt++)
            {
                yield return new WaitForSeconds(attempt == 0 ? 2.5f : 3.0f);
                candidate = FindNewestVideo(out candidateModified);
                string searchMeta = $"attempt={attempt + 1};path={(candidate ?? "none")};modified_ms={candidateModified}";
                sensorRecorder?.LogAction("video_search_attempt", _videoBackend, searchMeta);
                AddVideoDiag(searchMeta);

                if (candidate == null)
                    continue;

                // Wait briefly for source file size to stabilize before copy.
                long lastSize = -1;
                int stableHits = 0;
                long sourceSize = 0;
                bool sourceStable = false;
                for (int k = 0; k < 10; k++)
                {
                    if (TryGetFileSize(candidate, out sourceSize))
                    {
                        if (sourceSize == lastSize && sourceSize > 0) stableHits++;
                        else stableHits = 0;
                        lastSize = sourceSize;
                        if (stableHits >= 2)
                        {
                            sourceStable = true;
                            break;
                        }
                    }
                    yield return new WaitForSeconds(0.4f);
                }

                if (!sourceStable)
                {
                    failureReason = "source_not_stable";
                    sensorRecorder?.LogAction("video_save_failed", _videoBackend, $"reason={failureReason};src={candidate}");
                    continue;
                }

                if (TryCopyVideoToSession(candidate, out string destPath, out long destSize, out string copyFail))
                {
                    videoSaved = true;
                    resolvedPath = destPath;
                    resolvedSize = destSize;
                    break;
                }

                failureReason = $"copy_failed:{copyFail}";
                sensorRecorder?.LogAction("video_save_failed", _videoBackend, $"reason={failureReason};src={candidate}");
            }
        }

        string statusMsg;
        if (videoSaved)
        {
            sensorRecorder?.LogAction("video_saved", "pov_video.mp4", $"src={resolvedPath};size={resolvedSize}");
            statusMsg = $"Saved!\n{frames} frames, {dur:F1}s\nVideo: OK ({resolvedSize / 1024}KB)";
        }
        else
        {
            if (string.IsNullOrEmpty(failureReason))
                failureReason = "search_timeout";
            sensorRecorder?.LogAction("video_not_found", _videoBackend, failureReason);
            WriteVideoDiagnostics(failureReason, resolvedPath, resolvedSize);
            statusMsg = $"Saved!\n{frames} frames, {dur:F1}s\nVideo: not found";
        }

        statusMsg += "\n" + BuildQualityFooter();
        sensorRecorder.StopSession();

        if (txtButtonLabel != null) txtButtonLabel.text = "RECORD";
        if (btnImage != null) btnImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);
        SetStatus(statusMsg);
    }

    string FindNewestVideo(out long bestTime)
    {
        string best = null;
        bestTime = _videoStartTimeMs - 5000; // Allow 5s tolerance before start

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            // Prefer MediaStore because recorder outputs are not always under a stable folder path.
            TryFindVideoViaMediaStore(ref best, ref bestTime);

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

            // Final fallback: recursive scan under /sdcard.
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

    void TryFindVideoViaMediaStore(ref string best, ref long bestTime)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var act = up.GetStatic<AndroidJavaObject>("currentActivity");
            if (act == null) return;
            using var resolver = act.Call<AndroidJavaObject>("getContentResolver");
            if (resolver == null) return;
            using var media = new AndroidJavaClass("android.provider.MediaStore$Video$Media");
            using var uri = media.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");

            long cutoffSec = System.Math.Max(0L, (_videoStartTimeMs / 1000L) - 20L);
            string[] projection = { "_data", "date_modified" };
            string[] args = { cutoffSec.ToString() };
            using var cursor = resolver.Call<AndroidJavaObject>("query", uri, projection, "date_modified >= ?", args, "date_modified DESC");
            if (cursor == null) return;

            int pathIndex = cursor.Call<int>("getColumnIndex", "_data");
            int modifiedIndex = cursor.Call<int>("getColumnIndex", "date_modified");
            if (pathIndex < 0 || modifiedIndex < 0) return;

            while (cursor.Call<bool>("moveToNext"))
            {
                string path = cursor.Call<string>("getString", pathIndex);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".mp4")) continue;
                if (path.ToLower().Contains("tmp")) continue;
                long modifiedMs = cursor.Call<long>("getLong", modifiedIndex) * 1000L;
                if (modifiedMs > bestTime)
                {
                    bestTime = modifiedMs;
                    best = path;
                }
            }
        }
        catch (System.Exception e)
        {
            AddVideoDiag($"media_store_error={e.GetType().Name}");
            Debug.LogWarning($"[Video] MediaStore query failed: {e.Message}");
        }
#endif
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

    bool TryGetFileSize(string path, out long size)
    {
        size = 0;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;
        try
        {
            size = new FileInfo(path).Length;
            return size > 0;
        }
        catch
        {
            return false;
        }
    }

    bool TryCopyVideoToSession(string sourcePath, out string destPath, out long destSize, out string reason)
    {
        destPath = string.Empty;
        destSize = 0;
        reason = "unknown";

        if (string.IsNullOrEmpty(_sessionDir))
        {
            reason = "missing_session_dir";
            return false;
        }
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
        {
            reason = "source_missing";
            return false;
        }

        destPath = Path.Combine(_sessionDir, "pov_video.mp4");
        try
        {
            string srcFull = Path.GetFullPath(sourcePath);
            string dstFull = Path.GetFullPath(destPath);
            if (!string.Equals(srcFull, dstFull, System.StringComparison.Ordinal))
                File.Copy(sourcePath, destPath, true);

            if (!TryGetFileSize(destPath, out destSize))
            {
                reason = "dest_zero";
                return false;
            }

            reason = "ok";
            return true;
        }
        catch (System.Exception e)
        {
            reason = e.GetType().Name;
            return false;
        }
    }

    void AddVideoDiag(string line)
    {
        if (_videoSearchDiagnostics.Count >= 120) return;
        _videoSearchDiagnostics.Add($"{System.DateTime.UtcNow:O} | {line}");
    }

    void WriteVideoDiagnostics(string reason, string candidatePath, long candidateSize)
    {
        if (string.IsNullOrEmpty(_sessionDir)) return;
        try
        {
            string path = Path.Combine(_sessionDir, "video_diagnostics.json");
            var sb = new StringBuilder(2048);
            sb.AppendLine("{");
            sb.AppendLine($"  \"backend\": \"{EscapeJson(_videoBackend)}\",");
            sb.AppendLine($"  \"start_ms\": {_videoStartTimeMs},");
            sb.AppendLine($"  \"stop_ms\": {_videoStopTimeMs},");
            sb.AppendLine($"  \"reason\": \"{EscapeJson(reason ?? "unknown")}\",");
            sb.AppendLine($"  \"candidate_path\": \"{EscapeJson(candidatePath ?? "")}\",");
            sb.AppendLine($"  \"candidate_size\": {candidateSize},");
            sb.AppendLine("  \"events\": [");
            for (int i = 0; i < _videoSearchDiagnostics.Count; i++)
            {
                string suffix = i + 1 < _videoSearchDiagnostics.Count ? "," : "";
                sb.AppendLine($"    \"{EscapeJson(_videoSearchDiagnostics[i])}\"{suffix}");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
            sensorRecorder?.LogAction("video_diagnostics_written", _videoBackend, $"path={path}");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Video] Failed writing diagnostics JSON: {e.Message}");
        }
    }

    static string EscapeJson(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
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
            $"REC  {min:D2}:{sec:D2}   CLAP to stop\n" +
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
            CleanupCamera2();
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
        float bodyNativePct = bodyTrackingRecorder != null ? bodyTrackingRecorder.NativeFrameRatio * 100f : 0f;
        return $"Quality H={handPct:F0}% IMU={imuPct:F0}% BodyNative={bodyNativePct:F0}%";
    }

    void SetIdle()
    {
        SetHudVisible(true);
        string hint = "CLAP to start recording";
        string lastClap = _lastClapTime > 0f ? $"Last clap: {_lastClapSource} {_lastClapClosingSpeed:F2}m/s" : "Last clap: -";
        SetStatus($"{hint}\nHands: {_handStatus}\n{lastClap}");
        if (txtButtonLabel != null) txtButtonLabel.text = "RECORD";
        if (btnImage != null) btnImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);
    }

    void SetStatus(string msg)
    {
        if (txtStatus != null) txtStatus.text = msg;
    }
}
