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
    public float sampleRateHz = 60f;
    public string taskType = "";
    public string scenarioCategory = "";

    public bool IsRecording { get; private set; }
    public string SessionId { get; private set; }
    public long FrameIndex { get; private set; }
    public double SessionElapsed => _elapsed;
    public double SessionElapsedNow => GetLiveSessionElapsed();
    public bool HasHeadPose { get; private set; }
    public Vector3 LastHeadPosition { get; private set; }
    public Quaternion LastHeadRotation { get; private set; } = Quaternion.identity;

    private string _sessionDir;
    private double _startRealtime, _startWallclock, _elapsed;
    private float _sampleInterval, _lastSample;
    private StreamWriter _headW, _handW, _imuW, _actionW;
    private readonly StringBuilder _sb = new StringBuilder(512);
    private bool _nativeImu;

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
        _elapsed = GetLiveSessionElapsed();
        SampleHeadPose(); SampleHands(); SampleIMU();
        FrameIndex++;
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
        _imuW = W("imu.csv", "ts_s,frame,accel_x,accel_y,accel_z,gyro_x,gyro_y,gyro_z");
        _actionW = W("action_log.csv", "ts_s,frame,event_type,action_type,metadata");

        _startRealtime = Time.realtimeSinceStartupAsDouble;
        _startWallclock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        _elapsed = 0.0;
        _nativeImu = NativeIMUBridge.Instance != null && NativeIMUBridge.Instance.IsActive;
        if (SystemInfo.supportsGyroscope) Input.gyro.enabled = true;
        HasHeadPose = false;
        LastHeadPosition = Vector3.zero;
        LastHeadRotation = Quaternion.identity;
        FrameIndex = 0; _lastSample = Time.realtimeSinceStartup;
        IsRecording = true;
        WriteCalibration(); LogAction("session_start", task, $"scenario={scenario}");
        WriteSummary();
        Debug.Log($"[Rec] Started: {SessionId} → {_sessionDir}");
        return SessionId;
    }

    public void StopSession()
    {
        if (!IsRecording) return;
        _elapsed = GetLiveSessionElapsed();
        IsRecording = false;
        WriteActionRow(_elapsed, "session_end", taskType, $"frames={FrameIndex}");
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
        SampleHand("left");
        SampleHand("right");
    }

    void SampleHand(string label)
    {
#if PICO_XR
        try
        {
            var ht = label == "left" ? HandType.HandLeft : HandType.HandRight;
            var jl = new HandJointLocations();
            if (!PXR_HandTracking.GetJointLocations(ht, ref jl) || jl.jointLocations == null) return;
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
            }
        }
        catch
        {
        }
#endif
    }

    void SampleIMU()
    {
        Vector3 a, g;
        if (_nativeImu && NativeIMUBridge.Instance.IsActive)
        { a = NativeIMUBridge.Instance.Acceleration; g = NativeIMUBridge.Instance.AngularVelocity; }
        else { a = Input.gyro.enabled ? Input.gyro.userAcceleration : Input.acceleration; g = Input.gyro.enabled ? Input.gyro.rotationRateUnbiased : Vector3.zero; }
        _imuW.WriteLine($"{_elapsed:F6},{FrameIndex},{a.x:F6},{a.y:F6},{a.z:F6},{g.x:F6},{g.y:F6},{g.z:F6}");
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
        j.AppendLine($"  \"imu\": {{ \"location\": \"HMD\", \"accel_unit\": \"m/s^2\", \"gyro_unit\": \"rad/s\", \"source\": \"{(_nativeImu?"native_android":"unity_fallback")}\" }},");
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
            j.AppendLine($"  \"start_unix_s\": {_startWallclock:F3}, \"end_unix_s\": {endWall:F3}");
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
