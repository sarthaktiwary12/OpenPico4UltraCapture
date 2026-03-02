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
    private long _lastFrameWritten = -1;

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
        if (sensorRecorder.FrameIndex == _lastFrameWritten) return;
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
        _lastFrameWritten = sensorRecorder.FrameIndex;
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
                bool posOk = p.sqrMagnitude > 1e-8f;
                bool rotOk = Mathf.Abs(q.w) + Mathf.Abs(q.x) + Mathf.Abs(q.y) + Mathf.Abs(q.z) > 1e-6f;
                float conf = (posOk && rotOk) ? 0.85f : (posOk || rotOk ? 0.65f : 0.25f);
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

        Vector3 headPos;
        Quaternion headRot;
        if (sensorRecorder.HasHeadPose)
        {
            headPos = sensorRecorder.LastHeadPosition;
            headRot = SanitizeQuaternion(sensorRecorder.LastHeadRotation);
        }
        else
        {
            InputTrackingState headState = 0;
            head.TryGetFeatureValue(CommonUsages.trackingState, out headState);
            headPos = Vector3.zero;
            headRot = Quaternion.identity;
            bool headPosValid = ((headState & InputTrackingState.Position) != 0) &&
                                head.TryGetFeatureValue(CommonUsages.devicePosition, out headPos);
            bool headRotValid = ((headState & InputTrackingState.Rotation) != 0) &&
                                head.TryGetFeatureValue(CommonUsages.deviceRotation, out headRot);
            if (!headPosValid) return;
            if (!headRotValid) headRot = Quaternion.identity;
            headRot = SanitizeQuaternion(headRot);
        }

        Vector3 leftWristPos = headPos + headRot * new Vector3(-0.20f, -0.35f, 0.25f);
        Vector3 rightWristPos = headPos + headRot * new Vector3(0.20f, -0.35f, 0.25f);
        Quaternion leftWristRot = headRot;
        Quaternion rightWristRot = headRot;

        var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        bool leftTracked = false;
        if (left.isValid)
        {
            InputTrackingState ls = 0;
            left.TryGetFeatureValue(CommonUsages.trackingState, out ls);
            if ((ls & InputTrackingState.Position) != 0) left.TryGetFeatureValue(CommonUsages.devicePosition, out leftWristPos);
            if ((ls & InputTrackingState.Rotation) != 0 && left.TryGetFeatureValue(CommonUsages.deviceRotation, out var lq)) leftWristRot = SanitizeQuaternion(lq);
            leftTracked = (ls & InputTrackingState.Position) != 0 && (ls & InputTrackingState.Rotation) != 0;
        }

        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        bool rightTracked = false;
        if (right.isValid)
        {
            InputTrackingState rs = 0;
            right.TryGetFeatureValue(CommonUsages.trackingState, out rs);
            if ((rs & InputTrackingState.Position) != 0) right.TryGetFeatureValue(CommonUsages.devicePosition, out rightWristPos);
            if ((rs & InputTrackingState.Rotation) != 0 && right.TryGetFeatureValue(CommonUsages.deviceRotation, out var rq)) rightWristRot = SanitizeQuaternion(rq);
            rightTracked = (rs & InputTrackingState.Position) != 0 && (rs & InputTrackingState.Rotation) != 0;
        }

        Vector3 pelvisPos = headPos + headRot * new Vector3(0f, -0.55f, 0f);
        Vector3 leftShoulderPos = headPos + headRot * new Vector3(-0.17f, -0.16f, 0.02f);
        Vector3 rightShoulderPos = headPos + headRot * new Vector3(0.17f, -0.16f, 0.02f);
        Vector3 leftElbowPos = Vector3.Lerp(leftShoulderPos, leftWristPos, 0.5f);
        Vector3 rightElbowPos = Vector3.Lerp(rightShoulderPos, rightWristPos, 0.5f);

        double ts = sensorRecorder.SessionElapsed;
        long frame = sensorRecorder.FrameIndex;

        float headConf = 0.95f;
        float shoulderConf = 0.62f;
        float elbowConf = 0.58f;
        float wristConfL = leftTracked ? 0.82f : 0.52f;
        float wristConfR = rightTracked ? 0.82f : 0.52f;
        float handConfL = leftTracked ? 0.78f : 0.48f;
        float handConfR = rightTracked ? 0.78f : 0.48f;
        float pelvisConf = 0.50f;

        WriteJoint(ts, frame, 0, "Pelvis", pelvisPos, headRot, pelvisConf);
        WriteJoint(ts, frame, 15, "Head", headPos, headRot, headConf);
        WriteJoint(ts, frame, 16, "LeftShoulder", leftShoulderPos, headRot, shoulderConf);
        WriteJoint(ts, frame, 17, "RightShoulder", rightShoulderPos, headRot, shoulderConf);
        WriteJoint(ts, frame, 18, "LeftElbow", leftElbowPos, leftWristRot, elbowConf);
        WriteJoint(ts, frame, 19, "RightElbow", rightElbowPos, rightWristRot, elbowConf);
        WriteJoint(ts, frame, 20, "LeftWrist", leftWristPos, leftWristRot, wristConfL);
        WriteJoint(ts, frame, 21, "RightWrist", rightWristPos, rightWristRot, wristConfR);
        WriteJoint(ts, frame, 22, "LeftHand", leftWristPos, leftWristRot, handConfL);
        WriteJoint(ts, frame, 23, "RightHand", rightWristPos, rightWristRot, handConfR);
    }

    void WriteJoint(double ts, long frame, int jointId, string jointName, Vector3 p, Quaternion q, float confidence)
    {
        _w.WriteLine($"{ts:F6},{frame},{jointId},{jointName},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{confidence:F3}");
    }

    bool LooksLikeValidNativeBodyData(BodyTrackingData data)
    {
        int n = Mathf.Min(data.roleDatas.Length, 24);
        int validJointCount = 0;
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < n; i++)
        {
            var j = data.roleDatas[i];
            var p = new Vector3((float)j.localPose.PosX, (float)j.localPose.PosY, (float)j.localPose.PosZ);
            var q = new Quaternion((float)j.localPose.RotQx, (float)j.localPose.RotQy, (float)j.localPose.RotQz, (float)j.localPose.RotQw);
            bool posOk = p.sqrMagnitude > 1e-8f;
            bool quatOk = (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 1e-8f;
            if (posOk || quatOk) validJointCount++;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        Vector3 span = max - min;
        bool hasSpatialSpread = span.x > 0.04f || span.y > 0.04f || span.z > 0.04f;
        return validJointCount >= 6 && hasSpatialSpread;
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
