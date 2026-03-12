using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class SensorRecorder : MonoBehaviour
{
    [Header("Recording")]
    public float sampleRateHz = 30f;
    public string taskType = "";
    public string scenarioCategory = "";

    public bool IsRecording { get; private set; }
    public string SessionId { get; private set; }
    public long FrameIndex { get; private set; }
    public double SessionElapsed => _elapsed;
    public float LastSampleRealtimeS { get; private set; }
    public bool HasHeadPose { get; private set; }
    public Vector3 LastHeadPosition { get; private set; }
    public Quaternion LastHeadRotation { get; private set; } = Quaternion.identity;
    public bool LastHandTrackingReal { get; private set; }
    public bool LastHandTrackingFallback => !LastHandTrackingReal;
    public int LastRealHandJointCount { get; private set; }
    public float LastHandRotationVariance { get; private set; }
    public float LastImuAccelMagnitude { get; private set; }
    public bool LastImuHasGravity { get; private set; }
    public bool LastImuFallbackUsed { get; private set; }
    public Vector3 LastImuGravity { get; private set; }
    public float RealHandFrameRatio => _sampleCount > 0 ? (float)_realHandFrameCount / _sampleCount : 0f;
    public float ImuGravityFrameRatio => _sampleCount > 0 ? (float)_imuGravityFrameCount / _sampleCount : 0f;
    public float BodyNativeFrameRatio => _bodyFramesSampled > 0 ? _bodyNativeFrameRatio : 0f;
    public float BodyFallbackFrameRatio => _bodyFramesSampled > 0 ? _bodyFallbackFrameRatio : 0f;
    public int BodyFramesSampled => _bodyFramesSampled;
    public double SessionElapsedNow => GetLiveSessionElapsed();

    private string _sessionDir;
    private double _startRealtime, _startWallclock, _elapsed;
    private float _sampleInterval, _lastSample;
    private StreamWriter _headW, _handW, _imuW, _actionW;
    private readonly StringBuilder _sb = new StringBuilder(512);
    private bool _nativeImu;
    private bool _loggedImuFallback;
    private bool _loggedHandFallback;
    private bool _hasPrevHeadVel;
    private Vector3 _prevHeadVel;
    private double _prevHeadVelTs;
    private bool _hasPrevHeadPos;
    private Vector3 _prevHeadPos;
    private bool _hasPrevDerivedHeadVel;
    private Vector3 _prevDerivedHeadVel;
    private double _prevHeadPosTs;
    // Rotation-based angular velocity derivation
    private bool _hasPrevHeadRot;
    private Quaternion _prevHeadRot;
    private double _prevHeadRotTs;
    // 3-frame sliding window for smoother position-derived acceleration
    private Vector3 _posHistoryN0, _posHistoryN1, _posHistoryN2;
    private double _posHistoryTs0, _posHistoryTs1, _posHistoryTs2;
    private int _posHistoryCount;
    // Low-pass filter state for derived acceleration
    private Vector3 _lpfAccel;
    private bool _hasLpfAccel;
    private const float LPF_ALPHA = 0.35f;
    // Derived IMU quality counters
    private long _imuDerivedAccelFrameCount;
    private long _imuDerivedGyroFrameCount;
    private bool _loggedImuNoGravity;
    private bool _loggedImuStale;
    private long _sampleCount;
    private long _realHandFrameCount;
    private long _imuGravityFrameCount;
    private long _handFallbackFrameCount;
    private long _imuFallbackFrameCount;
    private long _imuNativeAccelFrameCount;
    private long _imuNativeGyroFrameCount;
    private long _imuNativeGravityFrameCount;
    private long _imuFullNativeFrameCount;
    private long _imuHeadKinematicsFallbackFrameCount;
    private long _imuUnityFallbackFrameCount;
    private long _imuNativeStaleFrameCount;
    private bool _hasTaskStart;
    private bool _hasTaskEnd;
    private bool _hasFinalizeStart;
    private double _taskStartElapsed;
    private double _taskEndElapsed;
    private double _finalizeStartElapsed;
    private int _bodyFramesSampled;
    private float _bodyNativeFrameRatio;
    private float _bodyFallbackFrameRatio;
    private readonly List<InputDevice> _tmpDevices = new List<InputDevice>(4);
    private readonly List<XRHandSubsystem> _tmpHandSubsystems = new List<XRHandSubsystem>(4);
    private XRHandSubsystem _xrHandSubsystem;
    private long _xrHandSubsystemLastLookupFrame = -1;

    static readonly string[] JN = {
        "Palm","Wrist","ThumbMeta","ThumbProx","ThumbDist","ThumbTip",
        "IndexMeta","IndexProx","IndexInter","IndexDist","IndexTip",
        "MiddleMeta","MiddleProx","MiddleInter","MiddleDist","MiddleTip",
        "RingMeta","RingProx","RingInter","RingDist","RingTip",
        "LittleMeta","LittleProx","LittleInter","LittleDist","LittleTip"
    };

    static readonly XRHandJointID[] XRJointIds = {
        XRHandJointID.Palm, XRHandJointID.Wrist,
        XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip,
        XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip,
        XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
        XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip,
        XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip
    };

    static readonly float[] DefaultJointRadius = {
        0.0240f, 0.0200f,
        0.0160f, 0.0140f, 0.0120f, 0.0100f,
        0.0160f, 0.0140f, 0.0120f, 0.0100f, 0.0080f,
        0.0160f, 0.0140f, 0.0120f, 0.0100f, 0.0080f,
        0.0160f, 0.0140f, 0.0120f, 0.0100f, 0.0080f,
        0.0160f, 0.0140f, 0.0120f, 0.0100f, 0.0080f
    };

    struct HandSampleStats
    {
        public bool wroteAny;
        public bool usedRealTracking;
        public int realJointCount;
        public int rotCount;
        public float sumX;
        public float sumY;
        public float sumZ;
        public float sumW;
        public float sumSqX;
        public float sumSqY;
        public float sumSqZ;
        public float sumSqW;
    }

    void Awake() => _sampleInterval = 1f / sampleRateHz;

    void Update()
    {
        if (!IsRecording) return;
        float now = Time.realtimeSinceStartup;
        if (now - _lastSample < _sampleInterval) return;
        // Advance by fixed interval to prevent timing drift (was: _lastSample = now)
        _lastSample += _sampleInterval;
        // Prevent spiral if we fell behind by more than 2 intervals
        if (now - _lastSample > _sampleInterval * 2)
            _lastSample = now - _sampleInterval;
        LastSampleRealtimeS = now;
        _elapsed = GetLiveSessionElapsed();
        SampleHeadPose();
        SampleHands();
        SampleIMU();
        _sampleCount++;
        FrameIndex++;
        // Flush every 150 frames (~5s) to reduce I/O load while preventing data loss
        if (FrameIndex % 150 == 0)
        {
            _headW?.Flush();
            _handW?.Flush();
            _imuW?.Flush();
            WriteSummary();
        }
    }

    void OnDestroy() { if (IsRecording) StopSession(); }
    void OnApplicationPause(bool pause) { if (pause && IsRecording) FlushCheckpoint(); }

    public string StartSession(string task, string scenario)
    {
        if (IsRecording) return SessionId;
        taskType = task; scenarioCategory = scenario;
        SessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Safe(task);
        _sessionDir = Path.Combine(Application.persistentDataPath, "dataset_capture", SessionId);
        Directory.CreateDirectory(_sessionDir);

        _headW = W("head_pose.csv", "ts_s,frame,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,tracking_state");
        _handW = W("hand_joints.csv", "ts_s,frame,hand,joint_id,joint_name,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,radius");
        _imuW = W("imu.csv", "ts_s,frame,accel_x,accel_y,accel_z,gyro_x,gyro_y,gyro_z,grav_x,grav_y,grav_z,accel_source,gyro_source,grav_source,fallback_used,fallback_reason");
        _actionW = W("action_log.csv", "ts_s,frame,event_type,action_type,metadata");

        _startRealtime = Time.realtimeSinceStartupAsDouble;
        _startWallclock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _elapsed = 0.0;
        _nativeImu = NativeIMUBridge.Instance != null && NativeIMUBridge.Instance.IsActive;
        if (SystemInfo.supportsGyroscope) Input.gyro.enabled = true;
        _loggedHandFallback = false;
        _loggedImuFallback = false;
        _loggedImuNoGravity = false;
        _loggedImuStale = false;
        _hasPrevHeadVel = false;
        _hasPrevHeadPos = false;
        _hasPrevDerivedHeadVel = false;
        _hasPrevHeadRot = false;
        _prevHeadRot = Quaternion.identity;
        _prevHeadRotTs = 0;
        _posHistoryCount = 0;
        _hasLpfAccel = false;
        _lpfAccel = Vector3.zero;
        _imuDerivedAccelFrameCount = 0;
        _imuDerivedGyroFrameCount = 0;
        LastHandTrackingReal = false;
        LastRealHandJointCount = 0;
        LastHandRotationVariance = 0f;
        LastImuAccelMagnitude = 0f;
        LastImuHasGravity = false;
        LastImuFallbackUsed = false;
        LastImuGravity = Vector3.zero;
        _sampleCount = 0;
        _realHandFrameCount = 0;
        _imuGravityFrameCount = 0;
        _handFallbackFrameCount = 0;
        _imuFallbackFrameCount = 0;
        _imuNativeAccelFrameCount = 0;
        _imuNativeGyroFrameCount = 0;
        _imuNativeGravityFrameCount = 0;
        _imuFullNativeFrameCount = 0;
        _imuHeadKinematicsFallbackFrameCount = 0;
        _imuUnityFallbackFrameCount = 0;
        _imuNativeStaleFrameCount = 0;
        _hasTaskStart = false;
        _hasTaskEnd = false;
        _hasFinalizeStart = false;
        _taskStartElapsed = 0d;
        _taskEndElapsed = 0d;
        _finalizeStartElapsed = 0d;
        _bodyFramesSampled = 0;
        _bodyNativeFrameRatio = 0f;
        _bodyFallbackFrameRatio = 0f;
        FrameIndex = 0; _lastSample = Time.realtimeSinceStartup;
        IsRecording = true;
        LogPermissionSnapshot();
        LogNativeImuRuntimeStatus();
        LogHandTrackingAvailability();
        WriteCalibration(); LogAction("session_start", task, $"scenario={scenario}");
        WriteSummary();
        Debug.Log($"[Rec] Started: {SessionId} → {_sessionDir}");
        return SessionId;
    }

    public void StopSession(string endEventType = "session_end", string endMetadata = "")
    {
        if (!IsRecording) return;
        _elapsed = GetLiveSessionElapsed();
        IsRecording = false;
        if (string.IsNullOrEmpty(endMetadata))
            endMetadata = $"frames={FrameIndex}";
        WriteActionRow(_elapsed, endEventType, taskType, endMetadata);
        Close(ref _headW); Close(ref _handW); Close(ref _imuW); Close(ref _actionW);
        WriteSummary();
        Debug.Log($"[Rec] Stopped: {FrameIndex} frames, {_elapsed:F1}s");
    }

    public void LogAction(string evt, string action = "", string meta = "")
    {
        if (_actionW == null) return;
        if (IsRecording) _elapsed = GetLiveSessionElapsed();
        WriteActionRow(_elapsed, evt, action, meta);
    }

    public string GetSessionDir() => _sessionDir;
    public double GetLiveSessionElapsed()
    {
        if (_startRealtime <= 0.0) return _elapsed;
        var now = Time.realtimeSinceStartupAsDouble - _startRealtime;
        if (double.IsNaN(now) || double.IsInfinity(now)) return _elapsed;
        if (now < _elapsed) return _elapsed;
        return now;
    }

    public void FlushCheckpoint()
    {
        if (!IsRecording) return;
        _elapsed = GetLiveSessionElapsed();
        _headW?.Flush();
        _handW?.Flush();
        _imuW?.Flush();
        _actionW?.Flush();
        WriteSummary();
    }

    public void MarkFinalizePhaseStart()
    {
        if (!IsRecording) return;
        _elapsed = GetLiveSessionElapsed();
        _hasFinalizeStart = true;
        _finalizeStartElapsed = _elapsed;
        WriteSummary();
    }

    public void UpdateBodyMetrics(float nativeFrameRatio, float fallbackFrameRatio, int sampledFrames)
    {
        _bodyFramesSampled = Mathf.Max(0, sampledFrames);
        _bodyNativeFrameRatio = Mathf.Clamp01(nativeFrameRatio);
        _bodyFallbackFrameRatio = Mathf.Clamp01(fallbackFrameRatio);
        WriteSummary();
    }

    // ── Sampling ──

    void SampleHeadPose()
    {
        var hmd = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!hmd.isValid) return;
        if (!hmd.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p)) return;
        if (!hmd.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion r)) return;
        string ts = "0";
        if (hmd.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState s)) ts = ((int)s).ToString();
        HasHeadPose = true;
        LastHeadPosition = p;
        LastHeadRotation = r;
        _headW.WriteLine($"{_elapsed:F6},{FrameIndex},{p.x:F6},{p.y:F6},{p.z:F6},{r.x:F6},{r.y:F6},{r.z:F6},{r.w:F6},{ts}");
    }

    void SampleHands()
    {
        var left = SampleHand("left");
        var right = SampleHand("right");

        int realJointCount = left.realJointCount + right.realJointCount;
        int trackedHands = (left.usedRealTracking ? 1 : 0) + (right.usedRealTracking ? 1 : 0);

        float rotVar = 0f;
        int rotVarCount = 0;
        if (left.rotCount > 0) { rotVar += ComputeRotationVariance(left); rotVarCount++; }
        if (right.rotCount > 0) { rotVar += ComputeRotationVariance(right); rotVarCount++; }
        LastHandRotationVariance = rotVarCount > 0 ? rotVar / rotVarCount : 0f;
        LastRealHandJointCount = realJointCount;
        // A single tracked hand still provides real egocentric supervision data.
        LastHandTrackingReal = realJointCount >= 20 && trackedHands >= 1;

        if (LastHandTrackingReal)
        {
            _realHandFrameCount++;
            if (_loggedHandFallback)
            {
                _loggedHandFallback = false;
                LogAction("hand_tracking_recovered", "xr_hands", $"real_joint_count={realJointCount};tracked_hands={trackedHands}");
            }
        }
        else
        {
            _handFallbackFrameCount++;
            if (!_loggedHandFallback)
            {
                _loggedHandFallback = true;
                LogAction("hand_fallback", "controller_skeleton", $"real_joint_count={realJointCount};rot_var={LastHandRotationVariance:F6}");
            }
        }
    }

    HandSampleStats SampleHand(string label)
    {
        // Prefer XR Hands subsystem. It maps to true tracked joints, unlike generic
        // InputDevice.handData which can mirror controller-style pseudo hands.
        var stats = TrySampleXRHandSubsystem(label);
        if (stats.wroteAny) return stats;

#if PICO_XR
        try
        {
            var ht = label == "left" ? HandType.HandLeft : HandType.HandRight;
            var jl = new HandJointLocations();
            if (PXR_Plugin.HandTracking.UPxr_GetHandTrackerJointLocations(ht, ref jl) && jl.jointLocations != null)
            {
                int n = Mathf.Min(jl.jointLocations.Length, 26);
                for (int i = 0; i < n; i++)
                {
                    var j = jl.jointLocations[i];
                    if ((((ulong)j.locationStatus & (ulong)HandLocationStatus.PositionValid) == 0) &&
                        (((ulong)j.locationStatus & (ulong)HandLocationStatus.OrientationValid) == 0)) continue;
                    var p = j.pose.Position.ToVector3();
                    var q = j.pose.Orientation.ToQuat();
                    string jn = i < JN.Length ? JN[i] : $"J{i}";
                    WriteHandJoint(label, i, jn, p, q, j.radius, true, ref stats);
                }
            }
        }
        catch
        {
        }
#endif
        if (stats.wroteAny) return stats;

        // Fallback: synthesize a full hand skeleton from controller pose so the stream keeps schema consistency.
        Vector3 cp = Vector3.zero;
        Quaternion cq = Quaternion.identity;
        var node = label == "left" ? XRNode.LeftHand : XRNode.RightHand;
        var dev = InputDevices.GetDeviceAtXRNode(node);
        bool hasControllerPose = dev.isValid &&
                                 dev.TryGetFeatureValue(CommonUsages.devicePosition, out cp) &&
                                 dev.TryGetFeatureValue(CommonUsages.deviceRotation, out cq);
        if (!hasControllerPose)
        {
            if (!HasHeadPose) return stats;
            cp = LastHeadPosition + LastHeadRotation * (label == "left"
                ? new Vector3(-0.20f, -0.35f, 0.25f)
                : new Vector3(0.20f, -0.35f, 0.25f));
            cq = LastHeadRotation;
        }
        WriteControllerHandSkeletonFallback(label, cp, cq);
        stats.wroteAny = true;
        stats.usedRealTracking = false;
        return stats;
    }

    bool TryGetRunningXRHandSubsystem(out XRHandSubsystem subsystem)
    {
        // Refresh lookup every ~90 capture frames (~3s at 30Hz) in case subsystem state changes.
        bool needsLookup = _xrHandSubsystem == null ||
                           !_xrHandSubsystem.running ||
                           (FrameIndex - _xrHandSubsystemLastLookupFrame) >= 90;

        if (needsLookup)
        {
            _xrHandSubsystemLastLookupFrame = FrameIndex;
            _tmpHandSubsystems.Clear();
            SubsystemManager.GetSubsystems(_tmpHandSubsystems);
            _xrHandSubsystem = null;
            for (int i = 0; i < _tmpHandSubsystems.Count; i++)
            {
                var sub = _tmpHandSubsystems[i];
                if (sub != null && sub.running)
                {
                    _xrHandSubsystem = sub;
                    break;
                }
            }
        }

        subsystem = _xrHandSubsystem;
        return subsystem != null && subsystem.running;
    }

    HandSampleStats TrySampleXRHandSubsystem(string label)
    {
        var stats = new HandSampleStats();
        if (!TryGetRunningXRHandSubsystem(out var subsystem))
            return stats;

        XRHand hand = label == "left" ? subsystem.leftHand : subsystem.rightHand;
        if (!hand.isTracked)
            return stats;

        for (int i = 0; i < XRJointIds.Length; i++)
        {
            var joint = hand.GetJoint(XRJointIds[i]);
            if (!joint.TryGetPose(out Pose pose))
                continue;

            float radius = DefaultJointRadius[i];
            if (joint.TryGetRadius(out float trackedRadius) && trackedRadius > 0f)
                radius = trackedRadius;

            WriteHandJoint(label, i, JN[i], pose.position, pose.rotation, radius, true, ref stats);
        }

        return stats;
    }

    void SampleIMU()
    {
        Vector3 a = Vector3.zero;
        Vector3 g = Vector3.zero;
        Vector3 grav = Vector3.zero;
        string accelSource = "none";
        string gyroSource = "none";
        string gravSource = "none";
        string fallbackReason = "none";
        bool usedNative = false;
        bool usedHeadFallback = false;
        bool usedUnityFallback = false;

        var native = NativeIMUBridge.Instance;
        bool nativeActive = native != null && native.IsActive;
        bool nativeFresh = nativeActive && native.HasFreshData;
        if (nativeActive && !nativeFresh)
        {
            _imuNativeStaleFrameCount++;
            fallbackReason = AppendReason(fallbackReason, "native_stale");
            if (!_loggedImuStale)
            {
                _loggedImuStale = true;
                LogAction("imu_native_stale", "native_bridge", native.BuildRegistrationStatus());
            }
        }
        else if (_loggedImuStale && nativeFresh)
        {
            _loggedImuStale = false;
            LogAction("imu_native_recovered", "native_bridge", native.BuildRegistrationStatus());
        }

        if (nativeFresh)
        {
            usedNative = true;
            _nativeImu = true;

            if (native.Acceleration.sqrMagnitude > 1e-8f)
            {
                a = native.Acceleration;
                accelSource = "native_accel";
                _imuNativeAccelFrameCount++;
            }
            else
            {
                fallbackReason = AppendReason(fallbackReason, "native_accel_zero");
            }

            if (native.AngularVelocity.sqrMagnitude > 1e-8f)
            {
                g = native.AngularVelocity;
                gyroSource = "native_gyro";
                _imuNativeGyroFrameCount++;
            }
            else
            {
                fallbackReason = AppendReason(fallbackReason, "native_gyro_zero");
            }

            if (native.Gravity.sqrMagnitude > 1e-8f)
            {
                grav = native.Gravity;
                gravSource = "native_gravity";
                _imuNativeGravityFrameCount++;
            }
        }
        else
        {
            fallbackReason = AppendReason(fallbackReason, nativeActive ? "native_not_fresh" : "native_inactive");
        }

        if ((a.sqrMagnitude < 1e-8f || g.sqrMagnitude < 1e-8f) && TryGetHeadKinematics(out Vector3 xrA, out Vector3 xrG))
        {
            if (a.sqrMagnitude < 1e-8f && xrA.sqrMagnitude > 1e-8f)
            {
                a = xrA;
                accelSource = "xr_head_kinematics";
                usedHeadFallback = true;
            }
            if (g.sqrMagnitude < 1e-8f && xrG.sqrMagnitude > 1e-8f)
            {
                g = xrG;
                gyroSource = "xr_head_kinematics";
                usedHeadFallback = true;
            }
        }

        if (a.sqrMagnitude < 1e-8f)
        {
            Vector3 ua = Input.gyro.enabled ? Input.gyro.userAcceleration : Vector3.zero;
            if (ua.sqrMagnitude > 1e-8f)
            {
                a = ua;
                accelSource = "unity_user_accel";
                usedUnityFallback = true;
            }
        }

        if (a.sqrMagnitude < 1e-8f)
        {
            Vector3 ra = Input.acceleration;
            if (ra.sqrMagnitude > 1e-8f)
            {
                a = ra;
                accelSource = "unity_acceleration";
                usedUnityFallback = true;
            }
            else
            {
                accelSource = "zero";
                fallbackReason = AppendReason(fallbackReason, "accel_unavailable");
            }
        }

        if (g.sqrMagnitude < 1e-8f && Input.gyro.enabled)
        {
            Vector3 ug = Input.gyro.rotationRateUnbiased;
            if (ug.sqrMagnitude > 1e-8f)
            {
                g = ug;
                gyroSource = "unity_gyro";
                usedUnityFallback = true;
            }
        }

        if (g.sqrMagnitude < 1e-8f)
        {
            gyroSource = "zero";
            fallbackReason = AppendReason(fallbackReason, "gyro_unavailable");
        }

        if (grav.sqrMagnitude < 1e-8f && Input.gyro.enabled)
        {
            Vector3 inferredGravity = Input.gyro.gravity;
            if (inferredGravity.sqrMagnitude > 1e-8f)
            {
                grav = inferredGravity;
                gravSource = "unity_gyro_gravity";
                usedUnityFallback = true;
            }
        }

        if (grav.sqrMagnitude < 1e-8f && Input.gyro.enabled)
        {
            Vector3 inferredGravity = Input.acceleration - Input.gyro.userAcceleration;
            if (inferredGravity.sqrMagnitude > 1e-8f)
            {
                grav = inferredGravity;
                gravSource = "inferred_gravity";
                usedUnityFallback = true;
            }
        }

        // Last resort: if accel magnitude is near ~9.8 and no user accel data,
        // the raw acceleration likely IS gravity (device stationary or slow movement).
        if (grav.sqrMagnitude < 1e-8f && a.magnitude >= 8f && a.magnitude <= 12f)
        {
            grav = a;
            gravSource = "accel_as_gravity";
            usedUnityFallback = true;
        }

        if (grav.sqrMagnitude < 1e-8f)
        {
            gravSource = "none";
            fallbackReason = AppendReason(fallbackReason, "gravity_missing");
        }

        if (usedHeadFallback) _imuHeadKinematicsFallbackFrameCount++;
        if (usedUnityFallback) _imuUnityFallbackFrameCount++;

        bool fullNative = accelSource.StartsWith("native_") && gyroSource.StartsWith("native_");
        if (fullNative) _imuFullNativeFrameCount++;
        bool loggedFallback = !fullNative;

        LastImuAccelMagnitude = a.magnitude;
        LastImuHasGravity = grav.sqrMagnitude > 1f || LastImuAccelMagnitude >= 8f;
        LastImuFallbackUsed = loggedFallback;
        LastImuGravity = grav;
        if (LastImuHasGravity) _imuGravityFrameCount++;
        if (loggedFallback) _imuFallbackFrameCount++;

        if (loggedFallback && !_loggedImuFallback)
        {
            LogAction("imu_fallback", "multi_source", fallbackReason);
            _loggedImuFallback = true;
        }
        else if (!loggedFallback && _loggedImuFallback)
        {
            _loggedImuFallback = false;
            LogAction("imu_fallback_recovered", "native_bridge", "full_native");
        }

        if (usedNative && !LastImuHasGravity && !_loggedImuNoGravity)
        {
            _loggedImuNoGravity = true;
            LogAction("imu_no_gravity_detected", "native", $"accel_mag={LastImuAccelMagnitude:F3}");
        }

        _imuW.WriteLine(
            $"{_elapsed:F6},{FrameIndex},{a.x:F6},{a.y:F6},{a.z:F6},{g.x:F6},{g.y:F6},{g.z:F6},{grav.x:F6},{grav.y:F6},{grav.z:F6}," +
            $"{Esc(accelSource)},{Esc(gyroSource)},{Esc(gravSource)},{(loggedFallback ? 1 : 0)},{Esc(fallbackReason)}");
    }

    bool TryGetHeadKinematics(out Vector3 accel, out Vector3 gyro)
    {
        accel = Vector3.zero;
        gyro = Vector3.zero;
        var hmd = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        bool hasA = false;
        bool hasG = false;

        // 1. Try XR-reported angular velocity first
        if (hmd.isValid && hmd.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out var rawGyro))
        {
            if (rawGyro.sqrMagnitude > 1e-8f)
            {
                gyro = rawGyro;
                hasG = true;
            }
        }

        // 2. Derive angular velocity from head rotation deltas if XR didn't provide it
        if (!hasG && HasHeadPose)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_hasPrevHeadRot)
            {
                double dt = now - _prevHeadRotTs;
                if (dt > 1e-4 && dt < 0.2)
                {
                    // deltaRot = Inverse(prev) * cur  =>  rotation that happened during dt
                    Quaternion deltaRot = Quaternion.Inverse(_prevHeadRot) * LastHeadRotation;
                    // Normalize to avoid NaN from accumulated drift
                    float mag = Mathf.Sqrt(deltaRot.x * deltaRot.x + deltaRot.y * deltaRot.y +
                                           deltaRot.z * deltaRot.z + deltaRot.w * deltaRot.w);
                    if (mag > 1e-6f)
                    {
                        deltaRot.x /= mag; deltaRot.y /= mag;
                        deltaRot.z /= mag; deltaRot.w /= mag;
                    }
                    // Convert to angle-axis
                    float halfAngle = Mathf.Acos(Mathf.Clamp(deltaRot.w, -1f, 1f));
                    float sinHalf = Mathf.Sin(halfAngle);
                    if (sinHalf > 1e-6f)
                    {
                        Vector3 axis = new Vector3(deltaRot.x, deltaRot.y, deltaRot.z) / sinHalf;
                        float angle = 2f * halfAngle;
                        gyro = axis * (angle / (float)dt);
                        hasG = gyro.sqrMagnitude > 1e-8f;
                        if (hasG) _imuDerivedGyroFrameCount++;
                    }
                }
            }
            _prevHeadRot = LastHeadRotation;
            _prevHeadRotTs = Time.realtimeSinceStartupAsDouble;
            _hasPrevHeadRot = true;
        }

        // 3. Try XR-reported acceleration
        if (hmd.isValid && hmd.TryGetFeatureValue(CommonUsages.deviceAcceleration, out var rawAccel))
        {
            if (rawAccel.sqrMagnitude > 1e-8f)
            {
                accel = rawAccel;
                hasA = true;
            }
        }

        // 4. Derive from XR velocity differentiation
        if (!hasA && hmd.isValid && hmd.TryGetFeatureValue(CommonUsages.deviceVelocity, out var vel))
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_hasPrevHeadVel)
            {
                double dt = now - _prevHeadVelTs;
                if (dt > 1e-4 && dt < 0.2)
                {
                    var derived = (vel - _prevHeadVel) / (float)dt;
                    if (derived.sqrMagnitude > 1e-8f)
                    {
                        accel = ApplyLowPassFilter(derived);
                        hasA = true;
                        _imuDerivedAccelFrameCount++;
                    }
                }
            }
            _prevHeadVel = vel;
            _prevHeadVelTs = now;
            _hasPrevHeadVel = true;
        }

        // 5. 3-frame sliding window position double-differentiation
        if (!hasA && HasHeadPose)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            // Shift history window
            _posHistoryN2 = _posHistoryN1; _posHistoryTs2 = _posHistoryTs1;
            _posHistoryN1 = _posHistoryN0; _posHistoryTs1 = _posHistoryTs0;
            _posHistoryN0 = LastHeadPosition; _posHistoryTs0 = now;
            _posHistoryCount = Mathf.Min(_posHistoryCount + 1, 3);

            if (_posHistoryCount >= 3)
            {
                double dt01 = _posHistoryTs0 - _posHistoryTs1;
                double dt12 = _posHistoryTs1 - _posHistoryTs2;
                double dtAvg = (dt01 + dt12) * 0.5;
                if (dtAvg > 1e-4 && dt01 < 0.2 && dt12 < 0.2)
                {
                    Vector3 v01 = (_posHistoryN0 - _posHistoryN1) / (float)dt01;
                    Vector3 v12 = (_posHistoryN1 - _posHistoryN2) / (float)dt12;
                    Vector3 derived = (v01 - v12) / (float)dtAvg;
                    if (derived.sqrMagnitude > 1e-8f)
                    {
                        accel = ApplyLowPassFilter(derived);
                        hasA = true;
                        _imuDerivedAccelFrameCount++;
                    }
                }
            }
        }

        if (!hasA) accel = Vector3.zero;
        if (!hasG) gyro = Vector3.zero;
        return hasA || hasG;
    }

    Vector3 ApplyLowPassFilter(Vector3 raw)
    {
        if (!_hasLpfAccel)
        {
            _lpfAccel = raw;
            _hasLpfAccel = true;
            return raw;
        }
        _lpfAccel = Vector3.Lerp(_lpfAccel, raw, LPF_ALPHA);
        return _lpfAccel;
    }

    void WriteControllerHandSkeletonFallback(string label, Vector3 wristPos, Quaternion wristRot)
    {
        void WriteJoint(int id, string name, Vector3 localPos, float radius)
        {
            var p = wristPos + wristRot * localPos;
            var q = wristRot;
            _handW.WriteLine($"{_elapsed:F6},{FrameIndex},{label},{id},{name},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{radius:F4}");
        }

        WriteJoint(1, "Wrist", Vector3.zero, 0.0200f);
        WriteJoint(0, "Palm", new Vector3(0f, 0f, 0.035f), 0.0240f);

        void WriteFinger(int startId, int count, Vector3 baseOffset, Vector3 dir, float[] segLens)
        {
            dir.Normalize();
            float acc = 0f;
            for (int k = 0; k < count; k++)
            {
                acc += segLens[Mathf.Min(k, segLens.Length - 1)];
                int id = startId + k;
                string nm = id < JN.Length ? JN[id] : $"J{id}";
                float r = Mathf.Max(0.0065f, 0.016f - k * 0.0020f);
                WriteJoint(id, nm, baseOffset + dir * acc, r);
            }
        }

        WriteFinger(2, 4, new Vector3(-0.030f, -0.008f, 0.020f), new Vector3(-0.35f, 0.0f, 1.0f), new[] { 0.018f, 0.017f, 0.015f, 0.013f });   // Thumb
        WriteFinger(6, 5, new Vector3(-0.016f, 0.000f, 0.032f), new Vector3(-0.06f, 0.0f, 1.0f), new[] { 0.020f, 0.018f, 0.016f, 0.014f, 0.012f }); // Index
        WriteFinger(11, 5, new Vector3(0.000f, 0.000f, 0.035f), new Vector3(0.00f, 0.0f, 1.0f), new[] { 0.022f, 0.019f, 0.017f, 0.015f, 0.013f });   // Middle
        WriteFinger(16, 5, new Vector3(0.015f, 0.000f, 0.032f), new Vector3(0.05f, 0.0f, 1.0f), new[] { 0.021f, 0.018f, 0.016f, 0.014f, 0.012f });   // Ring
        WriteFinger(21, 5, new Vector3(0.030f, -0.001f, 0.028f), new Vector3(0.12f, 0.0f, 1.0f), new[] { 0.019f, 0.017f, 0.015f, 0.013f, 0.011f });  // Little
    }

    void WriteHandJoint(string label, int jointId, string jointName, Vector3 pos, Quaternion rot, float radius, bool realTracking, ref HandSampleStats stats)
    {
        _handW.WriteLine($"{_elapsed:F6},{FrameIndex},{label},{jointId},{jointName},{pos.x:F6},{pos.y:F6},{pos.z:F6},{rot.x:F6},{rot.y:F6},{rot.z:F6},{rot.w:F6},{radius:F4}");
        stats.wroteAny = true;
        if (!realTracking) return;
        stats.usedRealTracking = true;
        stats.realJointCount++;
        stats.rotCount++;
        stats.sumX += rot.x; stats.sumY += rot.y; stats.sumZ += rot.z; stats.sumW += rot.w;
        stats.sumSqX += rot.x * rot.x; stats.sumSqY += rot.y * rot.y; stats.sumSqZ += rot.z * rot.z; stats.sumSqW += rot.w * rot.w;
    }

    static float ComputeRotationVariance(HandSampleStats stats)
    {
        if (stats.rotCount <= 1) return 0f;
        float inv = 1f / stats.rotCount;
        float mx = stats.sumX * inv;
        float my = stats.sumY * inv;
        float mz = stats.sumZ * inv;
        float mw = stats.sumW * inv;
        float vx = Mathf.Max(0f, stats.sumSqX * inv - mx * mx);
        float vy = Mathf.Max(0f, stats.sumSqY * inv - my * my);
        float vz = Mathf.Max(0f, stats.sumSqZ * inv - mz * mz);
        float vw = Mathf.Max(0f, stats.sumSqW * inv - mw * mw);
        return (vx + vy + vz + vw) * 0.25f;
    }

    public bool RunPreflightHealthCheck(out string message)
    {
        bool handOk = IsHandTrackingReadyForCapture(out string handDetail);
        bool imuOk = IsImuReadyForCapture(out string imuDetail, out float accelMag);

        if (handOk && imuOk)
        {
            message = $"hand=ok;imu=ok;accel_mag={accelMag:F2}";
            return true;
        }

        var sb = new StringBuilder();
        if (!handOk) sb.Append($"hand={handDetail}");
        if (!imuOk)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append($"imu={imuDetail};accel_mag={accelMag:F2}");
        }
        message = sb.ToString();
        return false;
    }

    /// <summary>Grace period: during the first 10s after app launch, return true
    /// with a warning instead of blocking, so hand tracking has time to initialize
    /// via runtime activation.</summary>
    private static float _appStartTime = -1f;

    public bool IsHandTrackingReadyForCapture(out string detail)
    {
        if (_appStartTime < 0f) _appStartTime = Time.realtimeSinceStartup;
        float elapsed = Time.realtimeSinceStartup - _appStartTime;

        _tmpHandSubsystems.Clear();
        SubsystemManager.GetSubsystems(_tmpHandSubsystems);
        int runningCount = 0;
        bool xrTrackedHands = false;
        for (int i = 0; i < _tmpHandSubsystems.Count; i++)
        {
            var sub = _tmpHandSubsystems[i];
            if (sub == null || !sub.running) continue;
            runningCount++;
            xrTrackedHands |= sub.leftHand.isTracked || sub.rightHand.isTracked;
        }
        bool subsystemReady = runningCount > 0;

        bool picoSettingReady = false;
        int nativeTrackedHands = 0;
#if PICO_XR
        ActiveInputDevice picoInput = ActiveInputDevice.HeadActive;
#else
        string picoInput = "n/a";
#endif
#if PICO_XR
        try
        {
            picoSettingReady = PXR_Plugin.HandTracking.UPxr_GetHandTrackerSettingState();
            picoInput = PXR_Plugin.HandTracking.UPxr_GetHandTrackerActiveInputType();
            var left = new HandJointLocations();
            if (PXR_Plugin.HandTracking.UPxr_GetHandTrackerJointLocations(HandType.HandLeft, ref left) &&
                left.jointLocations != null && left.jointLocations.Length > 0)
                nativeTrackedHands++;

            var right = new HandJointLocations();
            if (PXR_Plugin.HandTracking.UPxr_GetHandTrackerJointLocations(HandType.HandRight, ref right) &&
                right.jointLocations != null && right.jointLocations.Length > 0)
                nativeTrackedHands++;
        }
        catch
        {
            picoSettingReady = false;
        }
#endif

        _tmpDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.HandTracking | InputDeviceCharacteristics.TrackedDevice,
            _tmpDevices);
        bool trackedDevicesPresent = _tmpDevices.Count > 0;

        // HasAnyHandTrackingPermission now includes PICO SDK fallback checks,
        // so this works even when Android permissions aren't formally granted.
        bool granted = RuntimePermissions.HasAnyHandTrackingPermission();

        // Require evidence of real tracked hands, not just connected devices/permissions.
        bool nativeHandsTracked = nativeTrackedHands > 0;
        bool ok = xrTrackedHands || nativeHandsTracked;
        detail = $"perm={granted};subsystems_running={runningCount};tracked_hands={xrTrackedHands};tracked_devices={_tmpDevices.Count};pico_setting={picoSettingReady};pico_input={picoInput};native_hands={nativeTrackedHands}";

        // Grace period: allow start during first 10s while hand tracking initializes at runtime
        if (!ok && elapsed < 10f)
        {
            Debug.LogWarning($"[SensorRecorder] Hand tracking not confirmed yet (grace period {elapsed:F1}s/10s) — allowing start.");
            detail += ";grace_period=true";
            return true;
        }

        return ok;
    }

    public bool IsImuReadyForCapture(out string detail, out float accelMag)
    {
        var native = NativeIMUBridge.Instance;
        Vector3 candidate = Vector3.zero;
        bool nativeActive = native != null && native.IsActive;
        bool nativeReady = nativeActive && native.HasFreshData;
        if (nativeReady)
            candidate = native.Acceleration;
        if (candidate.sqrMagnitude < 1e-8f)
            candidate = Input.acceleration;

        accelMag = candidate.magnitude;
        bool gravityPresent = accelMag >= 5f;
        string mode = nativeReady ? "native" : "fallback_only";
        detail = $"native_active={nativeActive};native_fresh={nativeReady};accel_mag={accelMag:F3};gravity={gravityPresent};mode={mode}";
        if (native != null)
            detail += ";" + native.BuildRegistrationStatus();
        // Warn when native IMU is unavailable; start remains non-blocking in controller logic.
        return gravityPresent && nativeReady;
    }

    void LogPermissionSnapshot()
    {
        bool handGranted = RuntimePermissions.HasAnyHandTrackingPermission();
        bool bodyGranted = RuntimePermissions.HasAnyBodyTrackingPermission();
        LogAction("permissions_state", "runtime", $"hand={handGranted};body={bodyGranted}");
    }

    void LogHandTrackingAvailability()
    {
        bool handOk = IsHandTrackingReadyForCapture(out string detail);
        LogAction("hand_tracking_availability", handOk ? "ready" : "not_ready", detail);
        Debug.Log($"[Hands] Availability: {detail}");
    }

    void LogNativeImuRuntimeStatus()
    {
        var native = NativeIMUBridge.Instance;
        if (native == null)
        {
            LogAction("imu_runtime_status", "native_bridge", "bridge_missing");
            return;
        }
        LogAction("imu_runtime_status", "native_bridge", native.BuildRegistrationStatus());
    }

    // ── Calibration ──

    void WriteCalibration()
    {
        var j = new StringBuilder();
        j.AppendLine("{");
        j.AppendLine($"  \"session_id\": \"{SessionId}\",");
        j.AppendLine($"  \"task_type\": \"{Esc(taskType)}\", \"scenario_category\": \"{Esc(scenarioCategory)}\",");
        j.AppendLine($"  \"capture_start_utc\": \"{DateTime.UtcNow:o}\",");
        j.AppendLine($"  \"capture_start_unix_s\": {_startWallclock:F3}, \"sample_rate_hz\": {sampleRateHz},");
        j.AppendLine($"  \"device\": {{ \"model\": \"{SystemInfo.deviceModel}\", \"os\": \"{SystemInfo.operatingSystem}\" }},");
        j.AppendLine("  \"coordinate_system\": {");
        j.AppendLine("    \"convention\": \"Unity left-handed Y-up\", \"units\": \"meters\",");
        j.AppendLine("    \"rotation_format\": \"quaternion (x,y,z,w)\", \"origin\": \"XR tracking origin (floor)\",");
        j.AppendLine("    \"axes\": { \"x\": \"right\", \"y\": \"up\", \"z\": \"forward\" }");
        j.AppendLine("  },");
        j.AppendLine("  \"camera_intrinsics\": {");
        j.AppendLine("    \"note\": \"PICO 4 Ultra: 2x32MP stereo RGB. Spatial video output 2048x1536@60fps.\",");
        j.AppendLine("    \"sensor_resolution\": [3264, 2448], \"output_resolution\": [2048, 1536],");
        j.AppendLine("    \"stereo_baseline_mm\": 64, \"hfov_deg_approx\": 100,");
        j.AppendLine("    \"approx_fx\": 860, \"approx_fy\": 860, \"approx_cx\": 1024, \"approx_cy\": 768,");
        j.AppendLine("    \"calibration_note\": \"Run checkerboard calibration for precise fx/fy/cx/cy per device.\"");
        j.AppendLine("  },");
        j.AppendLine("  \"extrinsics\": {");
        j.AppendLine("    \"note\": \"Cameras rigidly mounted on HMD. Extrinsics = fixed offset from HMD tracking center.\",");
        j.AppendLine("    \"reference\": \"HMD tracking origin\"");
        j.AppendLine("  },");
        j.AppendLine("  \"depth_sensor\": { \"type\": \"iToF\", \"fov_deg\": 60, \"range_m\": 3.0, \"access\": \"spatial_mesh\" },");
        j.AppendLine("  \"hand_tracking\": { \"joints_per_hand\": 26, \"model\": \"OpenXR XR_EXT_hand_tracking\",");
        j.AppendLine("    \"per_joint\": [\"position_xyz_m\", \"orientation_quat\", \"radius_m\"],");
        j.Append("    \"joint_names\": ["); for (int i=0;i<JN.Length;i++) { j.Append($"\"{JN[i]}\""); if(i<JN.Length-1) j.Append(","); } j.AppendLine("]");
        j.AppendLine("  },");
        j.AppendLine("  \"body_tracking\": {");
        j.AppendLine("    \"joints\": 24, \"model\": \"PICO BodyTrackerResult\",");
        j.AppendLine("    \"per_joint\": [\"position_xyz_m\", \"orientation_quat\", \"confidence\"],");
        j.AppendLine("    \"joint_names\": [\"Pelvis\",\"LeftHip\",\"RightHip\",\"Spine1\",");
        j.AppendLine("      \"LeftKnee\",\"RightKnee\",\"Spine2\",");
        j.AppendLine("      \"LeftAnkle\",\"RightAnkle\",\"Spine3\",");
        j.AppendLine("      \"LeftFoot\",\"RightFoot\",\"Neck\",");
        j.AppendLine("      \"LeftCollar\",\"RightCollar\",\"Head\",");
        j.AppendLine("      \"LeftShoulder\",\"RightShoulder\",\"LeftElbow\",\"RightElbow\",");
        j.AppendLine("      \"LeftWrist\",\"RightWrist\",\"LeftHand\",\"RightHand\"],");
        j.AppendLine("    \"output_file\": \"body_pose.csv\"");
        j.AppendLine("  },");
        j.AppendLine("  \"imu\": { \"location\": \"HMD\", \"accel_unit\": \"m/s^2\", \"gyro_unit\": \"rad/s\", \"gravity_unit\": \"m/s^2\", \"source\": \"auto_multi_source\", \"columns\": [\"accel_xyz\",\"gyro_xyz\",\"grav_xyz\",\"accel_source\",\"gyro_source\",\"grav_source\",\"fallback_used\",\"fallback_reason\"] },");
        j.AppendLine("  \"sync\": {");
        j.AppendLine($"    \"method\": \"clap_gesture + audio_beep + wallclock\",");
        j.AppendLine($"    \"sensor_epoch_realtime\": {_startRealtime:F6}, \"sensor_epoch_unix_s\": {_startWallclock:F3},");
        j.AppendLine("    \"note\": \"Find sync_clap events in action_log.csv. Audio beep in video for alignment.\"");
        j.AppendLine("  }");
        j.AppendLine("}");
        File.WriteAllText(Path.Combine(_sessionDir, "calibration.json"), j.ToString());
    }

    void WriteSummary()
    {
        if (string.IsNullOrEmpty(_sessionDir)) return;
        try
        {
            double endWall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            double taskDuration = (_hasTaskStart && _hasTaskEnd && _taskEndElapsed >= _taskStartElapsed)
                ? (_taskEndElapsed - _taskStartElapsed)
                : 0.0;
            double recordingDuration = _hasFinalizeStart ? _finalizeStartElapsed : _elapsed;
            double finalizeDuration = Math.Max(0.0, _elapsed - recordingDuration);
            double fullNativeRatio = _sampleCount > 0 ? (double)_imuFullNativeFrameCount / _sampleCount : 0.0;
            var j = new StringBuilder();
            j.AppendLine("{");
            j.AppendLine($"  \"session_id\": \"{SessionId}\", \"task_type\": \"{Esc(taskType)}\",");
            j.AppendLine($"  \"scenario_category\": \"{Esc(scenarioCategory)}\",");
            j.AppendLine($"  \"total_frames\": {FrameIndex}, \"duration_s\": {_elapsed:F3},");
            j.AppendLine($"  \"has_task_window\": {(_hasTaskStart && _hasTaskEnd).ToString().ToLowerInvariant()},");
            j.AppendLine($"  \"task_start_s\": {_taskStartElapsed:F3}, \"task_end_s\": {_taskEndElapsed:F3},");
            j.AppendLine($"  \"task_duration_s\": {taskDuration:F3}, \"recording_duration_s\": {recordingDuration:F3}, \"finalize_duration_s\": {finalizeDuration:F3},");
            j.AppendLine($"  \"actual_fps\": {(FrameIndex / Math.Max(_elapsed, 0.001)):F2}, \"target_fps\": {sampleRateHz},");
            j.AppendLine($"  \"start_unix_s\": {_startWallclock:F3}, \"end_unix_s\": {endWall:F3},");
            j.AppendLine("  \"quality\": {");
            j.AppendLine($"    \"real_hand_frame_ratio\": {RealHandFrameRatio:F4},");
            j.AppendLine($"    \"imu_gravity_frame_ratio\": {ImuGravityFrameRatio:F4},");
            j.AppendLine($"    \"hand_fallback_frames\": {_handFallbackFrameCount},");
            j.AppendLine($"    \"imu_fallback_frames\": {_imuFallbackFrameCount},");
            j.AppendLine($"    \"imu_full_native_frame_ratio\": {fullNativeRatio:F4},");
            j.AppendLine($"    \"imu_native_accel_frames\": {_imuNativeAccelFrameCount},");
            j.AppendLine($"    \"imu_native_gyro_frames\": {_imuNativeGyroFrameCount},");
            j.AppendLine($"    \"imu_native_gravity_frames\": {_imuNativeGravityFrameCount},");
            j.AppendLine($"    \"imu_full_native_frames\": {_imuFullNativeFrameCount},");
            j.AppendLine($"    \"imu_head_kinematics_fallback_frames\": {_imuHeadKinematicsFallbackFrameCount},");
            j.AppendLine($"    \"imu_unity_fallback_frames\": {_imuUnityFallbackFrameCount},");
            j.AppendLine($"    \"imu_native_stale_frames\": {_imuNativeStaleFrameCount},");
            j.AppendLine($"    \"imu_derived_accel_frames\": {_imuDerivedAccelFrameCount},");
            j.AppendLine($"    \"imu_derived_gyro_frames\": {_imuDerivedGyroFrameCount},");
            j.AppendLine($"    \"body_frames_sampled\": {_bodyFramesSampled},");
            j.AppendLine($"    \"body_native_frame_ratio\": {_bodyNativeFrameRatio:F4},");
            j.AppendLine($"    \"body_fallback_frame_ratio\": {_bodyFallbackFrameRatio:F4}");
            j.AppendLine("  }");
            j.AppendLine("}");
            File.WriteAllText(Path.Combine(_sessionDir, "session_summary.json"), j.ToString());
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Rec] Failed to write session_summary.json: {e.Message}");
        }
    }

    void WriteActionRow(double ts, string evt, string action, string meta)
    {
        if (evt == "task_start")
        {
            _hasTaskStart = true;
            _taskStartElapsed = ts;
        }
        else if (evt == "task_end")
        {
            _hasTaskEnd = true;
            _taskEndElapsed = ts;
        }
        _actionW?.WriteLine($"{ts:F6},{FrameIndex},{evt},{Esc(action)},{Esc(meta)}");
        _actionW?.Flush();
    }

    StreamWriter W(string fn, string hdr) { var w = new StreamWriter(Path.Combine(_sessionDir, fn), false, new UTF8Encoding(false), 131072); w.WriteLine(hdr); return w; }
    void Close(ref StreamWriter w) { w?.Flush(); w?.Close(); w?.Dispose(); w = null; }
    static string AppendReason(string current, string reason)
    {
        if (string.IsNullOrEmpty(reason)) return current;
        if (string.IsNullOrEmpty(current) || current == "none") return reason;
        if (current.Contains(reason)) return current;
        return current + "|" + reason;
    }
    static string Esc(string s) => s?.Replace(",", ";").Replace("\n", " ") ?? "";
    static string Safe(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "x", @"[^a-zA-Z0-9_\-]", "_");
}
