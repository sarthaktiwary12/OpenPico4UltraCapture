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
    private bool _loggedImuNoGravity;
    private long _sampleCount;
    private long _realHandFrameCount;
    private long _imuGravityFrameCount;
    private long _handFallbackFrameCount;
    private long _imuFallbackFrameCount;
    private readonly List<InputDevice> _tmpDevices = new List<InputDevice>(4);
    private readonly List<Bone> _tmpBones = new List<Bone>(8);
    private readonly List<XRHandSubsystem> _tmpHandSubsystems = new List<XRHandSubsystem>(4);

    static readonly string[] JN = {
        "Palm","Wrist","ThumbMeta","ThumbProx","ThumbDist","ThumbTip",
        "IndexMeta","IndexProx","IndexInter","IndexDist","IndexTip",
        "MiddleMeta","MiddleProx","MiddleInter","MiddleDist","MiddleTip",
        "RingMeta","RingProx","RingInter","RingDist","RingTip",
        "LittleMeta","LittleProx","LittleInter","LittleDist","LittleTip"
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
        _lastSample = now;
        LastSampleRealtimeS = now;
        _elapsed = GetLiveSessionElapsed();
        SampleHeadPose();
        SampleHands();
        SampleIMU();
        _sampleCount++;
        FrameIndex++;
        // Flush every 60 frames to prevent data loss on crash/kill
        if (FrameIndex % 60 == 0)
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
        _imuW = W("imu.csv", "ts_s,frame,accel_x,accel_y,accel_z,gyro_x,gyro_y,gyro_z,grav_x,grav_y,grav_z");
        _actionW = W("action_log.csv", "ts_s,frame,event_type,action_type,metadata");

        _startRealtime = Time.realtimeSinceStartupAsDouble;
        _startWallclock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _elapsed = 0.0;
        _nativeImu = NativeIMUBridge.Instance != null && NativeIMUBridge.Instance.IsActive;
        if (SystemInfo.supportsGyroscope) Input.gyro.enabled = true;
        _loggedHandFallback = false;
        _loggedImuFallback = false;
        _loggedImuNoGravity = false;
        _hasPrevHeadVel = false;
        _hasPrevHeadPos = false;
        _hasPrevDerivedHeadVel = false;
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
        FrameIndex = 0; _lastSample = Time.realtimeSinceStartup;
        IsRecording = true;
        LogPermissionSnapshot();
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
        LastHandTrackingReal = trackedHands == 2 && realJointCount >= 40 && LastHandRotationVariance > 0.0005f;

        if (LastHandTrackingReal)
        {
            _realHandFrameCount++;
            if (_loggedHandFallback)
            {
                _loggedHandFallback = false;
                LogAction("hand_tracking_recovered", "both_hands", $"real_joint_count={realJointCount}");
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
        var stats = new HandSampleStats();
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
        if (!stats.wroteAny)
        {
            stats = TrySampleXRHand(label);
        }

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

    void SampleIMU()
    {
        Vector3 a;
        Vector3 g;
        Vector3 grav = Vector3.zero;
        bool loggedFallback = false;
        bool usedNative = false;
        var native = NativeIMUBridge.Instance;
        if (native != null && native.IsActive && native.HasFreshData)
        {
            a = native.Acceleration;
            g = native.AngularVelocity;
            grav = native.Gravity;
            usedNative = true;
            _nativeImu = true;

            bool nativeAccelValid = a.sqrMagnitude > 1e-8f;
            bool nativeGyroValid = g.sqrMagnitude > 1e-8f;

            if (!nativeAccelValid || !nativeGyroValid)
            {
                if (TryGetHeadKinematics(out Vector3 xrA, out Vector3 xrG))
                {
                    if (!nativeAccelValid && xrA.sqrMagnitude > 1e-8f) a = xrA;
                    if (!nativeGyroValid && xrG.sqrMagnitude > 1e-8f) g = xrG;
                    loggedFallback = true;
                }
            }

            if (a.sqrMagnitude < 1e-8f)
            {
                Vector3 ua = Input.gyro.enabled ? Input.gyro.userAcceleration : Vector3.zero;
                if (ua.sqrMagnitude > 1e-8f) a = ua;
                else
                {
                    Vector3 ra = Input.acceleration;
                    if (ra.sqrMagnitude > 1e-8f) a = ra;
                }
                loggedFallback = true;
            }

            if (g.sqrMagnitude < 1e-8f && Input.gyro.enabled)
            {
                Vector3 ug = Input.gyro.rotationRateUnbiased;
                if (ug.sqrMagnitude > 1e-8f) g = ug;
                loggedFallback = true;
            }

            if (grav.sqrMagnitude < 1e-8f && native.HasGravityData)
                grav = native.Gravity;
        }
        else if (TryGetHeadKinematics(out a, out g))
        {
            loggedFallback = true;
        }
        else
        {
            a = Input.gyro.enabled ? Input.gyro.userAcceleration : Input.acceleration;
            g = Input.gyro.enabled ? Input.gyro.rotationRateUnbiased : Vector3.zero;
            if (a.sqrMagnitude < 1e-8f)
                a = Input.acceleration;
            loggedFallback = true;
        }

        if (grav.sqrMagnitude < 1e-8f && Input.gyro.enabled)
        {
            Vector3 inferredGravity = Input.gyro.gravity;
            if (inferredGravity.sqrMagnitude > 1e-8f)
                grav = inferredGravity;
        }

        if (grav.sqrMagnitude < 1e-8f && Input.gyro.enabled)
        {
            Vector3 inferredGravity = Input.acceleration - Input.gyro.userAcceleration;
            if (inferredGravity.sqrMagnitude > 1e-8f)
                grav = inferredGravity;
        }

        // Last resort: if accel magnitude is near ~9.8 and no user accel data,
        // the raw acceleration likely IS gravity (device stationary or slow movement).
        if (grav.sqrMagnitude < 1e-8f && a.magnitude >= 8f && a.magnitude <= 12f)
        {
            grav = a;
        }

        LastImuAccelMagnitude = a.magnitude;
        LastImuHasGravity = grav.sqrMagnitude > 1f || LastImuAccelMagnitude >= 8f;
        LastImuFallbackUsed = loggedFallback;
        LastImuGravity = grav;
        if (LastImuHasGravity) _imuGravityFrameCount++;
        if (loggedFallback) _imuFallbackFrameCount++;

        if (loggedFallback && !_loggedImuFallback)
        {
            LogAction("imu_fallback", "xr_head_kinematics", "native_or_gyro_unavailable");
            _loggedImuFallback = true;
        }

        if (usedNative && !LastImuHasGravity && !_loggedImuNoGravity)
        {
            _loggedImuNoGravity = true;
            LogAction("imu_no_gravity_detected", "native", $"accel_mag={LastImuAccelMagnitude:F3}");
        }

        _imuW.WriteLine($"{_elapsed:F6},{FrameIndex},{a.x:F6},{a.y:F6},{a.z:F6},{g.x:F6},{g.y:F6},{g.z:F6},{grav.x:F6},{grav.y:F6},{grav.z:F6}");
    }

    bool TryGetHeadKinematics(out Vector3 accel, out Vector3 gyro)
    {
        accel = Vector3.zero;
        gyro = Vector3.zero;
        var hmd = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        bool hasA = false;
        bool hasG = false;

        if (hmd.isValid && hmd.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out var rawGyro))
        {
            gyro = rawGyro;
            hasG = true;
        }

        if (hmd.isValid && hmd.TryGetFeatureValue(CommonUsages.deviceAcceleration, out var rawAccel))
        {
            if (rawAccel.sqrMagnitude > 1e-8f)
            {
                accel = rawAccel;
                hasA = true;
            }
        }

        if (!hasA && hmd.isValid && hmd.TryGetFeatureValue(CommonUsages.deviceVelocity, out var vel))
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_hasPrevHeadVel)
            {
                double dt = now - _prevHeadVelTs;
                if (dt > 1e-4)
                {
                    var derived = (vel - _prevHeadVel) / (float)dt;
                    if (derived.sqrMagnitude > 1e-8f)
                    {
                        accel = derived;
                        hasA = true;
                    }
                }
            }
            _prevHeadVel = vel;
            _prevHeadVelTs = now;
            _hasPrevHeadVel = true;
        }

        if (!hasA && HasHeadPose)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_hasPrevHeadPos)
            {
                double dt = now - _prevHeadPosTs;
                if (dt > 1e-4)
                {
                    Vector3 velFromPose = (LastHeadPosition - _prevHeadPos) / (float)dt;
                    if (_hasPrevDerivedHeadVel)
                    {
                        Vector3 derived = (velFromPose - _prevDerivedHeadVel) / (float)dt;
                        if (derived.sqrMagnitude > 1e-8f)
                        {
                            accel = derived;
                            hasA = true;
                        }
                    }
                    _prevDerivedHeadVel = velFromPose;
                    _hasPrevDerivedHeadVel = true;
                }
            }
            _prevHeadPos = LastHeadPosition;
            _prevHeadPosTs = now;
            _hasPrevHeadPos = true;
        }

        if (!hasA)
        {
            Vector3 ua = Input.gyro.enabled ? Input.gyro.userAcceleration : Vector3.zero;
            if (ua.sqrMagnitude > 1e-8f)
            {
                accel = ua;
                hasA = true;
            }
        }

        if (!hasA)
        {
            Vector3 ra = Input.acceleration;
            if (ra.sqrMagnitude > 1e-8f)
            {
                accel = ra;
                hasA = true;
            }
        }

        if (!hasA) accel = Vector3.zero;
        if (!hasG) gyro = Vector3.zero;
        return hasA || hasG;
    }

    HandSampleStats TrySampleXRHand(string label)
    {
        var stats = new HandSampleStats();
        var handChar = label == "left"
            ? InputDeviceCharacteristics.HandTracking | InputDeviceCharacteristics.Left | InputDeviceCharacteristics.TrackedDevice
            : InputDeviceCharacteristics.HandTracking | InputDeviceCharacteristics.Right | InputDeviceCharacteristics.TrackedDevice;

        _tmpDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(handChar, _tmpDevices);
        if (_tmpDevices.Count == 0) return stats;

        var dev = _tmpDevices[0];
        if (!dev.isValid) return stats;
        if (!dev.TryGetFeatureValue(CommonUsages.handData, out Hand handData)) return stats;

        if (handData.TryGetRootBone(out Bone root))
        {
            if (root.TryGetPosition(out Vector3 rp) && root.TryGetRotation(out Quaternion rq))
            {
                WriteHandJoint(label, 1, "Wrist", rp, rq, 0.0200f, true, ref stats);
                var palm = rp + rq * new Vector3(0f, 0f, 0.035f);
                WriteHandJoint(label, 0, "Palm", palm, rq, 0.0250f, true, ref stats);
            }
        }

        WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Thumb, 2, 4, ref stats);
        WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Index, 6, 5, ref stats);
        WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Middle, 11, 5, ref stats);
        WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Ring, 16, 5, ref stats);
        WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Pinky, 21, 5, ref stats);

        return stats;
    }

    void WriteFingerBones(Hand handData, string label, UnityEngine.XR.HandFinger finger, int jointStartId, int targetCount, ref HandSampleStats stats)
    {
        _tmpBones.Clear();
        if (!handData.TryGetFingerBones(finger, _tmpBones) || _tmpBones.Count == 0) return;

        int n = Mathf.Min(_tmpBones.Count, targetCount);
        for (int bi = 0; bi < n; bi++)
        {
            var b = _tmpBones[bi];
            if (!b.TryGetPosition(out Vector3 p) || !b.TryGetRotation(out Quaternion q)) continue;
            int jid = jointStartId + bi;
            string jn = jid < JN.Length ? JN[jid] : $"J{jid}";
            float r = Mathf.Max(0.0075f, 0.018f - bi * 0.0022f);
            WriteHandJoint(label, jid, jn, p, q, r, true, ref stats);
        }
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

    public bool IsHandTrackingReadyForCapture(out string detail)
    {
        _tmpHandSubsystems.Clear();
        SubsystemManager.GetSubsystems(_tmpHandSubsystems);
        int runningCount = 0;
        for (int i = 0; i < _tmpHandSubsystems.Count; i++)
        {
            if (_tmpHandSubsystems[i] != null && _tmpHandSubsystems[i].running) runningCount++;
        }
        bool subsystemReady = runningCount > 0;

        bool picoSettingReady = false;
#if PICO_XR
        try
        {
            picoSettingReady = PXR_Plugin.HandTracking.UPxr_GetHandTrackerSettingState();
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

        // Any positive signal means hand tracking should be accessible.
        bool ok = granted || subsystemReady || picoSettingReady || trackedDevicesPresent;
        detail = $"perm={granted};subsystems_running={runningCount};tracked_devices={_tmpDevices.Count};pico_setting={picoSettingReady}";
        return ok;
    }

    public bool IsImuReadyForCapture(out string detail, out float accelMag)
    {
        var native = NativeIMUBridge.Instance;
        Vector3 candidate = Vector3.zero;
        bool nativeReady = native != null && native.IsActive && native.HasFreshData;
        if (nativeReady)
            candidate = native.Acceleration;
        if (candidate.sqrMagnitude < 1e-8f)
            candidate = Input.acceleration;

        accelMag = candidate.magnitude;
        bool gravityPresent = accelMag >= 5f;
        detail = $"native_active={nativeReady};accel_mag={accelMag:F3};gravity={gravityPresent}";
        return gravityPresent;
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
        j.AppendLine("  \"imu\": { \"location\": \"HMD\", \"accel_unit\": \"m/s^2\", \"gyro_unit\": \"rad/s\", \"gravity_unit\": \"m/s^2\", \"source\": \"auto_multi_source\", \"columns\": [\"accel_xyz\",\"gyro_xyz\",\"grav_xyz\"] },");
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
            var j = new StringBuilder();
            j.AppendLine("{");
            j.AppendLine($"  \"session_id\": \"{SessionId}\", \"task_type\": \"{Esc(taskType)}\",");
            j.AppendLine($"  \"scenario_category\": \"{Esc(scenarioCategory)}\",");
            j.AppendLine($"  \"total_frames\": {FrameIndex}, \"duration_s\": {_elapsed:F3},");
            j.AppendLine($"  \"actual_fps\": {(FrameIndex / Math.Max(_elapsed, 0.001)):F2}, \"target_fps\": {sampleRateHz},");
            j.AppendLine($"  \"start_unix_s\": {_startWallclock:F3}, \"end_unix_s\": {endWall:F3},");
            j.AppendLine("  \"quality\": {");
            j.AppendLine($"    \"real_hand_frame_ratio\": {RealHandFrameRatio:F4},");
            j.AppendLine($"    \"imu_gravity_frame_ratio\": {ImuGravityFrameRatio:F4},");
            j.AppendLine($"    \"hand_fallback_frames\": {_handFallbackFrameCount},");
            j.AppendLine($"    \"imu_fallback_frames\": {_imuFallbackFrameCount}");
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
        _actionW?.WriteLine($"{ts:F6},{FrameIndex},{evt},{Esc(action)},{Esc(meta)}");
        _actionW?.Flush();
    }

    StreamWriter W(string fn, string hdr) { var w = new StreamWriter(Path.Combine(_sessionDir, fn), false, new UTF8Encoding(false), 65536); w.WriteLine(hdr); return w; }
    void Close(ref StreamWriter w) { w?.Flush(); w?.Close(); w?.Dispose(); w = null; }
    static string Esc(string s) => s?.Replace(",", ";").Replace("\n", " ") ?? "";
    static string Safe(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "x", @"[^a-zA-Z0-9_\-]", "_");
}
