using System.IO;
using System.Reflection;
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

    public int LastWrittenJointCount { get; private set; }
    public int LastNativeJointCount { get; private set; }
    public bool LastFrameUsedFallback { get; private set; }
    public float FullJointFrameRatio => _framesSampled > 0 ? (float)_framesWith24Written / _framesSampled : 0f;

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
    private int _frameCount;
    private int _nativeDiagnosticCounter;
    private int _framesSampled;
    private int _framesWith24Written;

    // Matches BodyTrackerRole enum order (0–23)
    private static readonly string[] BodyJointNames = {
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

    private readonly Vector3[] _fallbackPos = new Vector3[24];
    private readonly Quaternion[] _fallbackRot = new Quaternion[24];
    private readonly Vector3[] _framePos = new Vector3[24];
    private readonly Quaternion[] _frameRot = new Quaternion[24];
    private readonly float[] _frameConf = new float[24];
    private readonly bool[] _frameHasNative = new bool[24];

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

        SetWantBodyTracking(true, true);
        TryStartBodyTracking(forceLog: true);
        _available = CheckAvailability();
        _nextProbeTime = Time.realtimeSinceStartup + 2f;
        _nextStartAttemptTime = Time.realtimeSinceStartup + 2f;
        _loggedFallbackActive = false;
        _loggedBadNativeOnce = false;
        _frameCount = 0;
        _nativeDiagnosticCounter = 0;
        _framesSampled = 0;
        _framesWith24Written = 0;
        LastWrittenJointCount = 0;
        LastNativeJointCount = 0;
        LastFrameUsedFallback = false;
        _on = true;

        if (!_available)
            Debug.LogWarning("[Body] Body tracking unavailable at start; using synthetic 24-joint fallback.");
    }

    public void StopCapture()
    {
        _on = false;
        if (_activeWriter == this) _activeWriter = null;

#if PICO_XR
        try { SetWantBodyTracking(false, false); } catch { }
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

    void Update()
    {
        if (!_on || !_ownsWriter || sensorRecorder == null || !sensorRecorder.IsRecording) return;
        if (sensorRecorder.FrameIndex == _lastFrameWritten) return;

        if (!_bodyTrackingStarted && Time.realtimeSinceStartup >= _nextStartAttemptTime)
        {
            TryStartBodyTracking(forceLog: false);
            _nextStartAttemptTime = Time.realtimeSinceStartup + 2f;
        }

        if (Time.realtimeSinceStartup >= _nextProbeTime)
        {
            _available = CheckAvailability();
            _nextProbeTime = Time.realtimeSinceStartup + 2f;
            if (_available) Debug.Log("[Body] Body tracking became available during recording.");
        }

        SampleBody();
        _lastFrameWritten = sensorRecorder.FrameIndex;
        _frameCount++;
        if (_frameCount % 60 == 0) _w?.Flush();
    }

    private void SampleBody()
    {
        bool wrote = false;

#if PICO_XR
        if (_available)
        {
            try
            {
                var getInfo = new BodyTrackingGetDataInfo { displayTime = 0 };
                var data = new BodyTrackingData();
                int ret = PXR_MotionTracking.GetBodyTrackingData(ref getInfo, ref data);
                _nativeDiagnosticCounter++;
                if (_nativeDiagnosticCounter >= 90)
                {
                    _nativeDiagnosticCounter = 0;
                    LogNativeSnapshot(ret, data);
                }

                if (ret == 0 && data.roleDatas != null && data.roleDatas.Length > 0 &&
                    LooksLikeValidNativeBodyData(data, out int validJointCount, out Vector3 span))
                {
                    wrote = WriteNativeWithFallbackFill(data);
                    LastNativeJointCount = validJointCount;
                    if (_loggedFallbackActive && LastNativeJointCount >= 20)
                    {
                        _loggedFallbackActive = false;
                        Debug.Log("[Body] Native body data recovered.");
                    }
                }
                else
                {
                    if (!_loggedBadNativeOnce)
                    {
                        _loggedBadNativeOnce = true;
                        int len = data.roleDatas == null ? 0 : data.roleDatas.Length;
                        Debug.LogWarning($"[Body] Native body data invalid. ret={ret}, joints={len}. Using synthetic fallback.");
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Body] Sample error: {e.Message}");
            }
        }
#endif

        if (!wrote)
        {
            WriteFallbackBodyPose();
        }
    }

#if PICO_XR
    private bool WriteNativeWithFallbackFill(BodyTrackingData data)
    {
        if (!BuildFallbackPose())
        {
            // If head/controller data is missing, still emit a stable 24-joint frame at origin.
            for (int i = 0; i < 24; i++)
            {
                _fallbackPos[i] = Vector3.zero;
                _fallbackRot[i] = Quaternion.identity;
            }
        }

        for (int i = 0; i < 24; i++)
        {
            _framePos[i] = _fallbackPos[i];
            _frameRot[i] = _fallbackRot[i];
            _frameConf[i] = 0f;
            _frameHasNative[i] = false;
        }

        int n = Mathf.Min(data.roleDatas.Length, 24);
        for (int i = 0; i < n; i++)
        {
            var j = data.roleDatas[i];
            var p = new Vector3((float)j.localPose.PosX, (float)j.localPose.PosY, (float)j.localPose.PosZ);
            var q = SanitizeQuaternion(new Quaternion((float)j.localPose.RotQx, (float)j.localPose.RotQy, (float)j.localPose.RotQz, (float)j.localPose.RotQw));
            bool posOk = p.sqrMagnitude > 1e-8f;
            bool rotOk = Mathf.Abs(q.w) + Mathf.Abs(q.x) + Mathf.Abs(q.y) + Mathf.Abs(q.z) > 1e-6f;
            if (!posOk && !rotOk) continue;

            _framePos[i] = p;
            _frameRot[i] = q;
            _frameConf[i] = (posOk && rotOk) ? 0.85f : 0.65f;
            _frameHasNative[i] = true;
        }

        int nativeCount = 0;
        for (int i = 0; i < 24; i++)
        {
            if (_frameHasNative[i]) nativeCount++;
        }

        double ts = sensorRecorder.GetLiveSessionElapsed();
        long frame = sensorRecorder.FrameIndex;
        for (int i = 0; i < 24; i++)
        {
            WriteJoint(ts, frame, i, BodyJointNames[i], _framePos[i], _frameRot[i], _frameConf[i]);
        }

        LastNativeJointCount = nativeCount;
        LastWrittenJointCount = 24;
        LastFrameUsedFallback = nativeCount < 24;
        _framesSampled++;
        _framesWith24Written++;

        return true;
    }
#endif

    private void WriteFallbackBodyPose()
    {
        if (!_loggedFallbackActive)
        {
            _loggedFallbackActive = true;
            Debug.LogWarning("[Body] Writing synthetic 24-joint fallback body pose.");
        }

        if (!BuildFallbackPose())
        {
            for (int i = 0; i < 24; i++)
            {
                _fallbackPos[i] = Vector3.zero;
                _fallbackRot[i] = Quaternion.identity;
            }
        }

        double ts = sensorRecorder.GetLiveSessionElapsed();
        long frame = sensorRecorder.FrameIndex;
        for (int i = 0; i < 24; i++)
        {
            WriteJoint(ts, frame, i, BodyJointNames[i], _fallbackPos[i], _fallbackRot[i], 0.0f);
        }

        LastNativeJointCount = 0;
        LastWrittenJointCount = 24;
        LastFrameUsedFallback = true;
        _framesSampled++;
        _framesWith24Written++;
    }

    private bool BuildFallbackPose()
    {
        var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!head.isValid && !sensorRecorder.HasHeadPose) return false;

        Vector3 headPos;
        Quaternion headRot;
        if (sensorRecorder.HasHeadPose)
        {
            headPos = sensorRecorder.LastHeadPosition;
            headRot = SanitizeQuaternion(sensorRecorder.LastHeadRotation);
        }
        else
        {
            headPos = Vector3.zero;
            headRot = Quaternion.identity;
            if (!head.TryGetFeatureValue(CommonUsages.devicePosition, out headPos)) return false;
            if (!head.TryGetFeatureValue(CommonUsages.deviceRotation, out headRot)) headRot = Quaternion.identity;
            headRot = SanitizeQuaternion(headRot);
        }

        float headHeight = Mathf.Clamp(headPos.y, 1.1f, 2.1f);
        float pelvisY = Mathf.Max(0.75f, headHeight - 0.88f);

        Vector3 pelvis = new Vector3(headPos.x, pelvisY, headPos.z - 0.03f);
        Vector3 neck = headPos + headRot * new Vector3(0f, -0.12f, 0.01f);
        Vector3 spine3 = Vector3.Lerp(pelvis, neck, 0.82f);
        Vector3 spine2 = Vector3.Lerp(pelvis, neck, 0.62f);
        Vector3 spine1 = Vector3.Lerp(pelvis, neck, 0.38f);

        Vector3 leftCollar = neck + headRot * new Vector3(-0.07f, 0.00f, 0.01f);
        Vector3 rightCollar = neck + headRot * new Vector3(0.07f, 0.00f, 0.01f);
        Vector3 leftShoulder = neck + headRot * new Vector3(-0.17f, -0.04f, 0.02f);
        Vector3 rightShoulder = neck + headRot * new Vector3(0.17f, -0.04f, 0.02f);

        Vector3 leftWristPos = headPos + headRot * new Vector3(-0.20f, -0.35f, 0.25f);
        Vector3 rightWristPos = headPos + headRot * new Vector3(0.20f, -0.35f, 0.25f);
        Quaternion leftWristRot = headRot;
        Quaternion rightWristRot = headRot;

        var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        if (left.isValid)
        {
            if (left.TryGetFeatureValue(CommonUsages.devicePosition, out var lp)) leftWristPos = lp;
            if (left.TryGetFeatureValue(CommonUsages.deviceRotation, out var lq)) leftWristRot = SanitizeQuaternion(lq);
        }

        var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (right.isValid)
        {
            if (right.TryGetFeatureValue(CommonUsages.devicePosition, out var rp)) rightWristPos = rp;
            if (right.TryGetFeatureValue(CommonUsages.deviceRotation, out var rq)) rightWristRot = SanitizeQuaternion(rq);
        }

        Vector3 leftElbow = Vector3.Lerp(leftShoulder, leftWristPos, 0.52f) + headRot * new Vector3(-0.02f, -0.01f, 0.02f);
        Vector3 rightElbow = Vector3.Lerp(rightShoulder, rightWristPos, 0.52f) + headRot * new Vector3(0.02f, -0.01f, 0.02f);

        Vector3 leftHand = leftWristPos + leftWristRot * new Vector3(0f, 0f, 0.05f);
        Vector3 rightHand = rightWristPos + rightWristRot * new Vector3(0f, 0f, 0.05f);

        Vector3 leftHip = pelvis + headRot * new Vector3(-0.10f, -0.02f, 0.00f);
        Vector3 rightHip = pelvis + headRot * new Vector3(0.10f, -0.02f, 0.00f);
        Vector3 leftKnee = leftHip + new Vector3(0f, -0.42f, 0.04f);
        Vector3 rightKnee = rightHip + new Vector3(0f, -0.42f, 0.04f);
        Vector3 leftAnkle = leftKnee + new Vector3(0f, -0.42f, -0.02f);
        Vector3 rightAnkle = rightKnee + new Vector3(0f, -0.42f, -0.02f);
        Vector3 leftFoot = leftAnkle + headRot * new Vector3(0f, -0.05f, 0.12f);
        Vector3 rightFoot = rightAnkle + headRot * new Vector3(0f, -0.05f, 0.12f);

        Quaternion torsoRot = headRot;

        _fallbackPos[0] = pelvis;
        _fallbackPos[1] = leftHip;
        _fallbackPos[2] = rightHip;
        _fallbackPos[3] = spine1;
        _fallbackPos[4] = leftKnee;
        _fallbackPos[5] = rightKnee;
        _fallbackPos[6] = spine2;
        _fallbackPos[7] = leftAnkle;
        _fallbackPos[8] = rightAnkle;
        _fallbackPos[9] = spine3;
        _fallbackPos[10] = leftFoot;
        _fallbackPos[11] = rightFoot;
        _fallbackPos[12] = neck;
        _fallbackPos[13] = leftCollar;
        _fallbackPos[14] = rightCollar;
        _fallbackPos[15] = headPos;
        _fallbackPos[16] = leftShoulder;
        _fallbackPos[17] = rightShoulder;
        _fallbackPos[18] = leftElbow;
        _fallbackPos[19] = rightElbow;
        _fallbackPos[20] = leftWristPos;
        _fallbackPos[21] = rightWristPos;
        _fallbackPos[22] = leftHand;
        _fallbackPos[23] = rightHand;

        _fallbackRot[0] = torsoRot;
        _fallbackRot[1] = torsoRot;
        _fallbackRot[2] = torsoRot;
        _fallbackRot[3] = torsoRot;
        _fallbackRot[4] = torsoRot;
        _fallbackRot[5] = torsoRot;
        _fallbackRot[6] = torsoRot;
        _fallbackRot[7] = torsoRot;
        _fallbackRot[8] = torsoRot;
        _fallbackRot[9] = torsoRot;
        _fallbackRot[10] = torsoRot;
        _fallbackRot[11] = torsoRot;
        _fallbackRot[12] = torsoRot;
        _fallbackRot[13] = torsoRot;
        _fallbackRot[14] = torsoRot;
        _fallbackRot[15] = headRot;
        _fallbackRot[16] = torsoRot;
        _fallbackRot[17] = torsoRot;
        _fallbackRot[18] = leftWristRot;
        _fallbackRot[19] = rightWristRot;
        _fallbackRot[20] = leftWristRot;
        _fallbackRot[21] = rightWristRot;
        _fallbackRot[22] = leftWristRot;
        _fallbackRot[23] = rightWristRot;

        return true;
    }

    private void WriteJoint(double ts, long frame, int jointId, string jointName, Vector3 p, Quaternion q, float confidence)
    {
        _w.WriteLine($"{ts:F6},{frame},{jointId},{jointName},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{confidence:F3}");
    }

#if PICO_XR
    private bool LooksLikeValidNativeBodyData(BodyTrackingData data, out int validJointCount, out Vector3 span)
    {
        int n = Mathf.Min(data.roleDatas.Length, 24);
        validJointCount = 0;
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

        span = max - min;
        bool hasSpatialSpread = span.x > 0.02f || span.y > 0.02f || span.z > 0.02f;
        return validJointCount >= 6 && hasSpatialSpread;
    }
#endif

    private static Quaternion SanitizeQuaternion(Quaternion q)
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

    private void TryStartBodyTracking(bool forceLog)
    {
#if PICO_XR
        if (_bodyTrackingStarted) return;
        try
        {
            SetWantBodyTracking(true, false);

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

    private void SetWantBodyTracking(bool enable, bool forceLog)
    {
#if PICO_XR
        try
        {
            var mtType = typeof(PXR_MotionTracking);
            var method = mtType.GetMethod("WantBodyTracking", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
            if (method == null)
            {
                if (forceLog) Debug.Log("[Body] WantBodyTracking(bool) not available in this SDK.");
                return;
            }

            object ret = method.Invoke(null, new object[] { enable });
            if (forceLog) Debug.Log($"[Body] WantBodyTracking({enable}) -> {ret}");
        }
        catch (System.Exception e)
        {
            if (forceLog) Debug.LogWarning($"[Body] WantBodyTracking({enable}) failed: {e.Message}");
        }
#endif
    }

    private bool CheckAvailability()
    {
#if PICO_XR
        try
        {
            bool isTracking = false;
            var state = new BodyTrackingStatus();
            int stateRet = PXR_MotionTracking.GetBodyTrackingState(ref isTracking, ref state);
            var getInfo = new BodyTrackingGetDataInfo { displayTime = 0 };
            var data = new BodyTrackingData();
            int ret = PXR_MotionTracking.GetBodyTrackingData(ref getInfo, ref data);
            int len = data.roleDatas == null ? 0 : data.roleDatas.Length;
            bool available = stateRet == 0 && isTracking && ret == 0 && len > 0;
            return available;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

#if PICO_XR
    private void LogNativeSnapshot(int ret, BodyTrackingData data)
    {
        int len = data.roleDatas == null ? 0 : data.roleDatas.Length;
        int validCount = 0;
        Vector3 span = Vector3.zero;

        if (ret == 0 && data.roleDatas != null && data.roleDatas.Length > 0)
        {
            LooksLikeValidNativeBodyData(data, out validCount, out span);
        }

        Debug.Log($"[Body] Native snapshot ret={ret}, joints={len}, valid={validCount}, span=({span.x:F3},{span.y:F3},{span.z:F3})");
    }
#endif

    void OnDestroy()
    {
        if (_on) StopCapture();
        if (_activeWriter == this) _activeWriter = null;
    }
}
