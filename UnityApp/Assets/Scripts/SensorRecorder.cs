using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
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
    public bool HasHeadPose { get; private set; }
    public Vector3 LastHeadPosition { get; private set; }
    public Quaternion LastHeadRotation { get; private set; } = Quaternion.identity;

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
    private readonly List<InputDevice> _tmpDevices = new List<InputDevice>(4);
    private readonly List<Bone> _tmpBones = new List<Bone>(8);

    static readonly string[] JN = {
        "Palm","Wrist","ThumbMeta","ThumbProx","ThumbDist","ThumbTip",
        "IndexMeta","IndexProx","IndexInter","IndexDist","IndexTip",
        "MiddleMeta","MiddleProx","MiddleInter","MiddleDist","MiddleTip",
        "RingMeta","RingProx","RingInter","RingDist","RingTip",
        "LittleMeta","LittleProx","LittleInter","LittleDist","LittleTip"
    };

    void Awake() => _sampleInterval = 1f / sampleRateHz;

    void Update()
    {
        if (!IsRecording) return;
        float now = Time.realtimeSinceStartup;
        if (now - _lastSample < _sampleInterval) return;
        _lastSample = now;
        _elapsed = Time.realtimeSinceStartupAsDouble - _startRealtime;
        SampleHeadPose(); SampleHands(); SampleIMU();
        FrameIndex++;
        // Flush every 60 frames to prevent data loss on crash/kill
        if (FrameIndex % 60 == 0) { _headW?.Flush(); _handW?.Flush(); _imuW?.Flush(); }
    }

    void OnDestroy() { if (IsRecording) StopSession(); }

    public string StartSession(string task, string scenario)
    {
        if (IsRecording) return SessionId;
        taskType = task; scenarioCategory = scenario;
        SessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Safe(task);
        _sessionDir = Path.Combine(Application.persistentDataPath, "dataset_capture", SessionId);
        Directory.CreateDirectory(_sessionDir);

        _headW = W("head_pose.csv", "ts_s,frame,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,tracking_state");
        _handW = W("hand_joints.csv", "ts_s,frame,hand,joint_id,joint_name,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,radius");
        _imuW = W("imu.csv", "ts_s,frame,accel_x,accel_y,accel_z,gyro_x,gyro_y,gyro_z");
        _actionW = W("action_log.csv", "ts_s,frame,event_type,action_type,metadata");

        _startRealtime = Time.realtimeSinceStartupAsDouble;
        _startWallclock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _elapsed = 0.0;
        _nativeImu = NativeIMUBridge.Instance != null && NativeIMUBridge.Instance.IsActive;
        if (SystemInfo.supportsGyroscope) Input.gyro.enabled = true;
        _loggedHandFallback = false;
        _hasPrevHeadVel = false;
        _hasPrevHeadPos = false;
        _hasPrevDerivedHeadVel = false;
        FrameIndex = 0; _lastSample = Time.realtimeSinceStartup;
        IsRecording = true;
        WriteCalibration(); LogAction("session_start", task, $"scenario={scenario}");
        Debug.Log($"[Rec] Started: {SessionId} → {_sessionDir}");
        return SessionId;
    }

    public void StopSession()
    {
        if (!IsRecording) return; IsRecording = false;
        LogAction("session_end", taskType, $"frames={FrameIndex}");
        Close(ref _headW); Close(ref _handW); Close(ref _imuW); Close(ref _actionW);
        WriteSummary();
        Debug.Log($"[Rec] Stopped: {FrameIndex} frames, {_elapsed:F1}s");
    }

    public void LogAction(string evt, string action = "", string meta = "")
    {
        _actionW?.WriteLine($"{_elapsed:F6},{FrameIndex},{evt},{Esc(action)},{Esc(meta)}");
        _actionW?.Flush();
    }

    public string GetSessionDir() => _sessionDir;

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
        SampleHand("left");
        SampleHand("right");
    }

    void SampleHand(string label)
    {
        bool wroteAny = false;
#if PICO_XR && !PICO_OPENXR_SDK
        try
        {
            var ht = label == "left" ? HandType.HandLeft : HandType.HandRight;
            var jl = new HandJointLocations();
            if (PXR_HandTracking.GetJointLocations(ht, ref jl) && jl.jointLocations != null)
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
                    _handW.WriteLine($"{_elapsed:F6},{FrameIndex},{label},{i},{jn},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{j.radius:F4}");
                    wroteAny = true;
                }
            }
        }
        catch
        {
        }
#endif
        if (!wroteAny)
        {
            wroteAny = TrySampleXRHand(label);
            if (wroteAny && _loggedHandFallback)
            {
                _loggedHandFallback = false;
                LogAction("hand_tracking_recovered", label, "xr_hand_data_available");
            }
        }

        if (wroteAny) return;

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
            if (!HasHeadPose) return;
            cp = LastHeadPosition + LastHeadRotation * (label == "left"
                ? new Vector3(-0.20f, -0.35f, 0.25f)
                : new Vector3(0.20f, -0.35f, 0.25f));
            cq = LastHeadRotation;
        }
        WriteControllerHandSkeletonFallback(label, cp, cq);
        if (!_loggedHandFallback)
        {
            _loggedHandFallback = true;
            LogAction("hand_fallback", "controller_skeleton", "xr_hand_data_unavailable");
        }
    }

    void SampleIMU()
    {
        Vector3 a, g;
        bool loggedFallback = false;
        var native = NativeIMUBridge.Instance;
        bool useNative = native != null && native.IsActive && native.HasFreshData;
        if (useNative)
        {
            a = native.Acceleration;
            g = native.AngularVelocity;
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

        if (loggedFallback && !_loggedImuFallback)
        {
            LogAction("imu_fallback", "xr_head_kinematics", "native_or_gyro_unavailable");
            _loggedImuFallback = true;
        }
        _imuW.WriteLine($"{_elapsed:F6},{FrameIndex},{a.x:F6},{a.y:F6},{a.z:F6},{g.x:F6},{g.y:F6},{g.z:F6}");
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

    bool TrySampleXRHand(string label)
    {
        var handChar = label == "left"
            ? InputDeviceCharacteristics.HandTracking | InputDeviceCharacteristics.Left | InputDeviceCharacteristics.TrackedDevice
            : InputDeviceCharacteristics.HandTracking | InputDeviceCharacteristics.Right | InputDeviceCharacteristics.TrackedDevice;

        _tmpDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(handChar, _tmpDevices);
        if (_tmpDevices.Count == 0) return false;

        var dev = _tmpDevices[0];
        if (!dev.isValid) return false;
        if (!dev.TryGetFeatureValue(CommonUsages.handData, out Hand handData)) return false;

        bool wroteAny = false;

        if (handData.TryGetRootBone(out Bone root))
        {
            if (root.TryGetPosition(out Vector3 rp) && root.TryGetRotation(out Quaternion rq))
            {
                _handW.WriteLine($"{_elapsed:F6},{FrameIndex},{label},1,Wrist,{rp.x:F6},{rp.y:F6},{rp.z:F6},{rq.x:F6},{rq.y:F6},{rq.z:F6},{rq.w:F6},0.0200");
                var palm = rp + rq * new Vector3(0f, 0f, 0.035f);
                _handW.WriteLine($"{_elapsed:F6},{FrameIndex},{label},0,Palm,{palm.x:F6},{palm.y:F6},{palm.z:F6},{rq.x:F6},{rq.y:F6},{rq.z:F6},{rq.w:F6},0.0250");
                wroteAny = true;
            }
        }

        wroteAny |= WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Thumb, 2, 4);
        wroteAny |= WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Index, 6, 5);
        wroteAny |= WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Middle, 11, 5);
        wroteAny |= WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Ring, 16, 5);
        wroteAny |= WriteFingerBones(handData, label, UnityEngine.XR.HandFinger.Pinky, 21, 5);

        return wroteAny;
    }

    bool WriteFingerBones(Hand handData, string label, UnityEngine.XR.HandFinger finger, int jointStartId, int targetCount)
    {
        _tmpBones.Clear();
        if (!handData.TryGetFingerBones(finger, _tmpBones) || _tmpBones.Count == 0) return false;

        bool wrote = false;
        int n = Mathf.Min(_tmpBones.Count, targetCount);
        for (int bi = 0; bi < n; bi++)
        {
            var b = _tmpBones[bi];
            if (!b.TryGetPosition(out Vector3 p) || !b.TryGetRotation(out Quaternion q)) continue;
            int jid = jointStartId + bi;
            string jn = jid < JN.Length ? JN[jid] : $"J{jid}";
            float r = Mathf.Max(0.0075f, 0.018f - bi * 0.0022f);
            _handW.WriteLine($"{_elapsed:F6},{FrameIndex},{label},{jid},{jn},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{r:F4}");
            wrote = true;
        }
        return wrote;
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
        j.AppendLine("  \"imu\": { \"location\": \"HMD\", \"accel_unit\": \"m/s^2\", \"gyro_unit\": \"rad/s\", \"source\": \"auto_multi_source\" },");
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
        double endWall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var j = new StringBuilder();
        j.AppendLine("{");
        j.AppendLine($"  \"session_id\": \"{SessionId}\", \"task_type\": \"{Esc(taskType)}\",");
        j.AppendLine($"  \"scenario_category\": \"{Esc(scenarioCategory)}\",");
        j.AppendLine($"  \"total_frames\": {FrameIndex}, \"duration_s\": {_elapsed:F3},");
        j.AppendLine($"  \"actual_fps\": {(FrameIndex / Math.Max(_elapsed, 0.001)):F2}, \"target_fps\": {sampleRateHz},");
        j.AppendLine($"  \"start_unix_s\": {_startWallclock:F3}, \"end_unix_s\": {endWall:F3}");
        j.AppendLine("}");
        File.WriteAllText(Path.Combine(_sessionDir, "session_summary.json"), j.ToString());
    }

    StreamWriter W(string fn, string hdr) { var w = new StreamWriter(Path.Combine(_sessionDir, fn), false, new UTF8Encoding(false), 65536); w.WriteLine(hdr); return w; }
    void Close(ref StreamWriter w) { w?.Flush(); w?.Close(); w?.Dispose(); w = null; }
    static string Esc(string s) => s?.Replace(",", ";").Replace("\n", " ") ?? "";
    static string Safe(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "x", @"[^a-zA-Z0-9_\-]", "_");
}
