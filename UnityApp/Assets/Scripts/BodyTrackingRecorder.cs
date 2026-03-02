using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class BodyTrackingRecorder : MonoBehaviour
{
    private static BodyTrackingRecorder _activeWriter;
    public SensorRecorder sensorRecorder;
    private StreamWriter _w;
    private bool _on;
    private bool _ownsWriter;
    private bool _available;
    private float _nextProbeTime;
    private bool _bodyTrackingStarted;
    private float _nextStartAttemptTime;
    private bool _loggedFallbackActive;
    private bool _loggedBadNativeOnce;

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
        if (_activeWriter != null && _activeWriter != this)
        {
            Debug.LogWarning("[Body] Another BodyTrackingRecorder instance is already active. This instance will stay idle.");
            _ownsWriter = false;
            _on = false;
            return;
        }
        _activeWriter = this;
        _ownsWriter = true;

        var path = Path.Combine(sessionDir, "body_pose.csv");
        _w = new StreamWriter(path, false, new UTF8Encoding(false), 65536);
        _w.WriteLine("ts_s,frame,joint_id,joint_name,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,confidence");
        TryStartBodyTracking(forceLog: true);
        _available = CheckAvailability();
        _nextProbeTime = Time.realtimeSinceStartup + 2f;
        _nextStartAttemptTime = Time.realtimeSinceStartup + 2f;
        _loggedFallbackActive = false;
        _loggedBadNativeOnce = false;
        _on = true;
        if (!_available) Debug.LogWarning("[Body] Body tracking unavailable at start; using fallback pose estimation until it becomes available.");
    }

    public void StopCapture()
    {
        _on = false;
        if (_activeWriter == this) _activeWriter = null;
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
        if (!_on || !_ownsWriter || sensorRecorder == null || !sensorRecorder.IsRecording) return;
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
            if (ret != 0 || data.roleDatas == null || data.roleDatas.Length == 0 || !LooksLikeValidNativeBodyData(data))
            {
                if (!_loggedBadNativeOnce)
                {
                    _loggedBadNativeOnce = true;
                    Debug.LogWarning("[Body] Native body data invalid/empty; switching to fallback body estimation.");
                }
                WriteFallbackBodyPose();
                return;
            }

            double ts = sensorRecorder.SessionElapsed;
            long frame = sensorRecorder.FrameIndex;
            int n = Mathf.Min(data.roleDatas.Length, 24);
            int wrote = 0;

            for (int i = 0; i < n; i++)
            {
                var j = data.roleDatas[i];
                string jn = i < BodyJointNames.Length ? BodyJointNames[i] : $"BJ{i}";
                var p = new Vector3((float)j.localPose.PosX, (float)j.localPose.PosY, (float)j.localPose.PosZ);
                var q = SanitizeQuaternion(new Quaternion((float)j.localPose.RotQx, (float)j.localPose.RotQy, (float)j.localPose.RotQz, (float)j.localPose.RotQw));
                float conf = j.bodyAction == 0 ? 0.0f : 1.0f;
                _w.WriteLine($"{ts:F6},{frame},{i},{jn},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{conf:F3}");
                wrote++;
            }

            if (wrote == 0)
            {
                WriteFallbackBodyPose();
            }
            else if (_loggedFallbackActive)
            {
                _loggedFallbackActive = false;
                Debug.Log("[Body] Native body data recovered.");
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
        if (!_loggedFallbackActive)
        {
            _loggedFallbackActive = true;
            Debug.LogWarning("[Body] Writing fallback body pose from HMD/controllers.");
        }

        var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!head.isValid) return;

        InputTrackingState headState = 0;
        head.TryGetFeatureValue(CommonUsages.trackingState, out headState);
        Vector3 headPos = Vector3.zero;
        Quaternion headRot = Quaternion.identity;
        bool headPosValid = ((headState & InputTrackingState.Position) != 0) &&
                            head.TryGetFeatureValue(CommonUsages.devicePosition, out headPos);
        bool headRotValid = ((headState & InputTrackingState.Rotation) != 0) &&
                            head.TryGetFeatureValue(CommonUsages.deviceRotation, out headRot);
        if (!headPosValid) return;
        if (!headRotValid) headRot = Quaternion.identity;
        headRot = SanitizeQuaternion(headRot);

        Vector3 leftWristPos = headPos + headRot * new Vector3(-0.20f, -0.35f, 0.25f);
        Vector3 rightWristPos = headPos + headRot * new Vector3(0.20f, -0.35f, 0.25f);
        Quaternion leftWristRot = headRot;
        Quaternion rightWristRot = headRot;

        var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        if (left.isValid)
        {
            InputTrackingState ls = 0;
            left.TryGetFeatureValue(CommonUsages.trackingState, out ls);
            if ((ls & InputTrackingState.Position) != 0) left.TryGetFeatureValue(CommonUsages.devicePosition, out leftWristPos);
            if ((ls & InputTrackingState.Rotation) != 0 && left.TryGetFeatureValue(CommonUsages.deviceRotation, out var lq)) leftWristRot = SanitizeQuaternion(lq);
        }

        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (right.isValid)
        {
            InputTrackingState rs = 0;
            right.TryGetFeatureValue(CommonUsages.trackingState, out rs);
            if ((rs & InputTrackingState.Position) != 0) right.TryGetFeatureValue(CommonUsages.devicePosition, out rightWristPos);
            if ((rs & InputTrackingState.Rotation) != 0 && right.TryGetFeatureValue(CommonUsages.deviceRotation, out var rq)) rightWristRot = SanitizeQuaternion(rq);
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

    bool LooksLikeValidNativeBodyData(BodyTrackingData data)
    {
        int n = Mathf.Min(data.roleDatas.Length, 24);
        int validJointCount = 0;
        for (int i = 0; i < n; i++)
        {
            var j = data.roleDatas[i];
            var p = new Vector3((float)j.localPose.PosX, (float)j.localPose.PosY, (float)j.localPose.PosZ);
            var q = new Quaternion((float)j.localPose.RotQx, (float)j.localPose.RotQy, (float)j.localPose.RotQz, (float)j.localPose.RotQw);
            bool posOk = p.sqrMagnitude > 1e-8f;
            bool quatOk = (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 1e-8f;
            if (posOk || quatOk) validJointCount++;
        }
        return validJointCount >= 4;
    }

    Quaternion SanitizeQuaternion(Quaternion q)
    {
        if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w)) return Quaternion.identity;
        float mag = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (mag < 1e-8f) return Quaternion.identity;
        if (Mathf.Abs(1f - mag) > 0.001f)
        {
            float inv = 1f / Mathf.Sqrt(mag);
            q.x *= inv; q.y *= inv; q.z *= inv; q.w *= inv;
        }
        return q;
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

    void OnDestroy() { if (_on) StopCapture(); if (_activeWriter == this) _activeWriter = null; }
}
