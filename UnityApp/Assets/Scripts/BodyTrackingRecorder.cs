using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class BodyTrackingRecorder : MonoBehaviour
{
    public SensorRecorder sensorRecorder;
    private StreamWriter _w;
    private bool _on;
    private bool _available;
    private float _nextProbeTime;
    private bool _bodyTrackingStarted;
    private float _nextStartAttemptTime;

    // Matches BodyTrackerRole enum order (0–23)
    static readonly string[] BodyJointNames = {
        "Pelvis","LeftHip","RightHip","Spine1",
        "LeftKnee","RightKnee","Spine2",
        "LeftAnkle","RightAnkle","Spine3",
        "LeftFoot","RightFoot","Neck",
        "LeftCollar","RightCollar","Head",
        "LeftShoulder","RightShoulder",
        "LeftElbow","RightElbow",
        "LeftWrist","RightWrist",
        "LeftHand","RightHand"
    };

    public void StartCapture(string sessionDir)
    {
        var path = Path.Combine(sessionDir, "body_pose.csv");
        _w = new StreamWriter(path, false, new UTF8Encoding(false), 65536);
        _w.WriteLine("ts_s,frame,joint_id,joint_name,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,confidence");
        TryStartBodyTracking(forceLog: true);
        _available = CheckAvailability();
        _nextProbeTime = Time.realtimeSinceStartup + 2f;
        _nextStartAttemptTime = Time.realtimeSinceStartup + 2f;
        _on = true;
        if (!_available) Debug.LogWarning("[Body] Body tracking unavailable at start; using fallback pose estimation until it becomes available.");
    }

    public void StopCapture()
    {
        _on = false;
#if PICO_XR
        if (_bodyTrackingStarted)
        {
            try { PXR_MotionTracking.StopBodyTracking(); } catch { }
            _bodyTrackingStarted = false;
        }
#endif
        _w?.Flush();
        _w?.Close();
        _w?.Dispose();
        _w = null;
        Debug.Log("[Body] Capture stopped.");
    }

    private int _frameCount;

    void Update()
    {
        if (!_on || sensorRecorder == null || !sensorRecorder.IsRecording) return;
        if (!_bodyTrackingStarted && Time.realtimeSinceStartup >= _nextStartAttemptTime)
        {
            TryStartBodyTracking(forceLog: false);
            _nextStartAttemptTime = Time.realtimeSinceStartup + 2f;
        }
        if (!_available && Time.realtimeSinceStartup >= _nextProbeTime)
        {
            _available = CheckAvailability();
            _nextProbeTime = Time.realtimeSinceStartup + 2f;
            if (_available) Debug.Log("[Body] Body tracking became available during recording.");
        }
        if (!_available) return;
        SampleBody();
        _frameCount++;
        if (_frameCount % 60 == 0) _w?.Flush();
    }

    void SampleBody()
    {
#if PICO_XR
        try
        {
            var getInfo = new BodyTrackingGetDataInfo { displayTime = 0 };
            var data = new BodyTrackingData();
            int ret = PXR_MotionTracking.GetBodyTrackingData(ref getInfo, ref data);
            if (ret != 0 || data.roleDatas == null || data.roleDatas.Length == 0)
            {
                WriteFallbackBodyPose();
                return;
            }

            double ts = sensorRecorder.SessionElapsed;
            long frame = sensorRecorder.FrameIndex;
            int n = Mathf.Min(data.roleDatas.Length, 24);

            for (int i = 0; i < n; i++)
            {
                var j = data.roleDatas[i];
                string jn = i < BodyJointNames.Length ? BodyJointNames[i] : $"BJ{i}";
                var p = new Vector3((float)j.localPose.PosX, (float)j.localPose.PosY, (float)j.localPose.PosZ);
                var q = new Quaternion((float)j.localPose.RotQx, (float)j.localPose.RotQy, (float)j.localPose.RotQz, (float)j.localPose.RotQw);
                float conf = j.bodyAction == 0 ? 0.0f : 1.0f;
                _w.WriteLine($"{ts:F6},{frame},{i},{jn},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{conf:F3}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Body] Sample error: {e.Message}");
            WriteFallbackBodyPose();
        }
#endif
    }

    void WriteFallbackBodyPose()
    {
        var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!head.isValid) return;
        if (!head.TryGetFeatureValue(CommonUsages.devicePosition, out var headPos)) return;
        if (!head.TryGetFeatureValue(CommonUsages.deviceRotation, out var headRot)) headRot = Quaternion.identity;

        Vector3 leftWristPos = headPos + headRot * new Vector3(-0.20f, -0.35f, 0.25f);
        Vector3 rightWristPos = headPos + headRot * new Vector3(0.20f, -0.35f, 0.25f);
        Quaternion leftWristRot = headRot;
        Quaternion rightWristRot = headRot;

        var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        if (left.isValid)
        {
            left.TryGetFeatureValue(CommonUsages.devicePosition, out leftWristPos);
            left.TryGetFeatureValue(CommonUsages.deviceRotation, out leftWristRot);
        }

        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (right.isValid)
        {
            right.TryGetFeatureValue(CommonUsages.devicePosition, out rightWristPos);
            right.TryGetFeatureValue(CommonUsages.deviceRotation, out rightWristRot);
        }

        Vector3 pelvisPos = headPos + headRot * new Vector3(0f, -0.55f, 0f);
        Vector3 leftShoulderPos = headPos + headRot * new Vector3(-0.17f, -0.16f, 0.02f);
        Vector3 rightShoulderPos = headPos + headRot * new Vector3(0.17f, -0.16f, 0.02f);
        Vector3 leftElbowPos = Vector3.Lerp(leftShoulderPos, leftWristPos, 0.5f);
        Vector3 rightElbowPos = Vector3.Lerp(rightShoulderPos, rightWristPos, 0.5f);

        double ts = sensorRecorder.SessionElapsed;
        long frame = sensorRecorder.FrameIndex;

        WriteJoint(ts, frame, 0, "Pelvis", pelvisPos, headRot, 0.25f);
        WriteJoint(ts, frame, 15, "Head", headPos, headRot, 0.35f);
        WriteJoint(ts, frame, 16, "LeftShoulder", leftShoulderPos, headRot, 0.20f);
        WriteJoint(ts, frame, 17, "RightShoulder", rightShoulderPos, headRot, 0.20f);
        WriteJoint(ts, frame, 18, "LeftElbow", leftElbowPos, leftWristRot, 0.20f);
        WriteJoint(ts, frame, 19, "RightElbow", rightElbowPos, rightWristRot, 0.20f);
        WriteJoint(ts, frame, 20, "LeftWrist", leftWristPos, leftWristRot, 0.25f);
        WriteJoint(ts, frame, 21, "RightWrist", rightWristPos, rightWristRot, 0.25f);
        WriteJoint(ts, frame, 22, "LeftHand", leftWristPos, leftWristRot, 0.20f);
        WriteJoint(ts, frame, 23, "RightHand", rightWristPos, rightWristRot, 0.20f);
    }

    void WriteJoint(double ts, long frame, int jointId, string jointName, Vector3 p, Quaternion q, float confidence)
    {
        _w.WriteLine($"{ts:F6},{frame},{jointId},{jointName},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{confidence:F3}");
    }

    void TryStartBodyTracking(bool forceLog)
    {
#if PICO_XR
        if (_bodyTrackingStarted) return;
        try
        {
            bool supported = false;
            int supportRet = PXR_MotionTracking.GetBodyTrackingSupported(ref supported);
            if (supportRet != 0 || !supported)
            {
                if (forceLog) Debug.LogWarning($"[Body] Body tracking unsupported (ret={supportRet}, supported={supported}).");
                return;
            }

            var boneLength = new BodyTrackingBoneLength();
            int startRet = PXR_MotionTracking.StartBodyTracking(BodyJointSet.BODY_JOINT_SET_BODY_FULL_START, boneLength);
            if (startRet != 0)
                startRet = PXR_MotionTracking.StartBodyTracking(BodyJointSet.BODY_JOINT_SET_BODY_START_WITHOUT_ARM, boneLength);

            _bodyTrackingStarted = startRet == 0;
            if (_bodyTrackingStarted) Debug.Log("[Body] Body tracking started.");
            else if (forceLog) Debug.LogWarning($"[Body] Failed to start body tracking (ret={startRet}).");
        }
        catch (System.Exception e)
        {
            if (forceLog) Debug.LogWarning($"[Body] Start body tracking error: {e.Message}");
        }
#endif
    }

    bool CheckAvailability()
    {
#if PICO_XR
        try
        {
            bool isTracking = false;
            var state = new BodyTrackingStatus();
            int stateRet = PXR_MotionTracking.GetBodyTrackingState(ref isTracking, ref state);
            if (stateRet == 0 && !isTracking) return false;
            var getInfo = new BodyTrackingGetDataInfo { displayTime = 0 };
            var data = new BodyTrackingData();
            int ret = PXR_MotionTracking.GetBodyTrackingData(ref getInfo, ref data);
            return ret == 0 && data.roleDatas != null && data.roleDatas.Length > 0;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

    void OnDestroy() { if (_on) StopCapture(); }
}
