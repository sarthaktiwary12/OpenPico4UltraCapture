using System.IO;
using System.Text;
using UnityEngine;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class BodyTrackingRecorder : MonoBehaviour
{
    public SensorRecorder sensorRecorder;
    private StreamWriter _w;
    private bool _on;
    private bool _available;
    private long _lastFrameWritten = -1;
    private readonly Vector3[] _fallbackPos = new Vector3[24];
    private readonly Quaternion[] _fallbackRot = new Quaternion[24];

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
        _available = CheckAvailability();
        _lastFrameWritten = -1;
        _on = true;
        if (!_available) Debug.LogWarning("[Body] Body tracking not available — using fallback skeleton.");
    }

    public void StopCapture()
    {
        _on = false;
        _w?.Flush();
        _w?.Close();
        _w?.Dispose();
        _w = null;
        Debug.Log("[Body] Capture stopped.");
    }

    void Update()
    {
        if (!_on || sensorRecorder == null || !sensorRecorder.IsRecording) return;
        if (sensorRecorder.FrameIndex == _lastFrameWritten) return;
        SampleBody();
        _lastFrameWritten = sensorRecorder.FrameIndex;
    }

    void SampleBody()
    {
        BuildFallbackPose(_fallbackPos, _fallbackRot);
        float[] conf = new float[24];

        bool nativeWrote = false;
#if PICO_XR
        try
        {
            var getInfo = new BodyTrackingGetDataInfo { displayTime = 0 };
            var data = new BodyTrackingData();
            int ret = PXR_MotionTracking.GetBodyTrackingData(ref getInfo, ref data);
            if (ret == 0 && data.roleDatas != null && data.roleDatas.Length > 0)
            {
                int n = Mathf.Min(data.roleDatas.Length, 24);
                for (int i = 0; i < n; i++)
                {
                    var j = data.roleDatas[i];
                    var p = new Vector3((float)j.localPose.PosX, (float)j.localPose.PosY, (float)j.localPose.PosZ);
                    var q = new Quaternion((float)j.localPose.RotQx, (float)j.localPose.RotQy, (float)j.localPose.RotQz, (float)j.localPose.RotQw);
                    bool posOk = p.sqrMagnitude > 1e-8f;
                    bool rotOk = (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 1e-8f;
                    if (posOk) _fallbackPos[i] = p;
                    if (rotOk) _fallbackRot[i] = q.normalized;
                    conf[i] = (posOk && rotOk) ? 0.85f : 0.65f;
                }
                nativeWrote = true;
                _available = true;
            }
            else
            {
                _available = false;
            }
        }
        catch (System.Exception e)
        {
            _available = false;
            Debug.LogWarning($"[Body] Sample error: {e.Message}");
        }
#endif

        double ts = sensorRecorder.GetLiveSessionElapsed();
        long frame = sensorRecorder.FrameIndex;
        for (int i = 0; i < 24; i++)
        {
            string jn = i < BodyJointNames.Length ? BodyJointNames[i] : $"BJ{i}";
            float c = nativeWrote ? conf[i] : 0.0f;
            var p = _fallbackPos[i];
            var q = _fallbackRot[i];
            _w.WriteLine($"{ts:F6},{frame},{i},{jn},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{c:F3}");
        }
    }

    void BuildFallbackPose(Vector3[] outPos, Quaternion[] outRot)
    {
        for (int i = 0; i < 24; i++)
        {
            outPos[i] = Vector3.zero;
            outRot[i] = Quaternion.identity;
        }

        if (sensorRecorder == null || !sensorRecorder.HasHeadPose) return;

        var headPos = sensorRecorder.LastHeadPosition;
        var headRot = sensorRecorder.LastHeadRotation;
        if ((headRot.x * headRot.x + headRot.y * headRot.y + headRot.z * headRot.z + headRot.w * headRot.w) < 1e-8f)
            headRot = Quaternion.identity;
        headRot = headRot.normalized;

        float headHeight = Mathf.Clamp(headPos.y, 1.1f, 2.1f);
        float pelvisY = Mathf.Max(0.75f, headHeight - 0.88f);
        Vector3 pelvis = new Vector3(headPos.x, pelvisY, headPos.z - 0.03f);
        Vector3 neck = headPos + headRot * new Vector3(0f, -0.12f, 0.01f);
        Vector3 leftShoulder = neck + headRot * new Vector3(-0.17f, -0.04f, 0.02f);
        Vector3 rightShoulder = neck + headRot * new Vector3(0.17f, -0.04f, 0.02f);
        Vector3 leftElbow = leftShoulder + headRot * new Vector3(-0.15f, -0.12f, 0.04f);
        Vector3 rightElbow = rightShoulder + headRot * new Vector3(0.15f, -0.12f, 0.04f);
        Vector3 leftWrist = leftElbow + headRot * new Vector3(-0.12f, -0.10f, 0.05f);
        Vector3 rightWrist = rightElbow + headRot * new Vector3(0.12f, -0.10f, 0.05f);

        outPos[0] = pelvis;         outRot[0] = headRot;
        outPos[1] = pelvis + new Vector3(-0.09f, -0.03f, 0.00f); outRot[1] = Quaternion.identity;
        outPos[2] = pelvis + new Vector3(0.09f, -0.03f, 0.00f);  outRot[2] = Quaternion.identity;
        outPos[3] = Vector3.Lerp(pelvis, neck, 0.35f); outRot[3] = headRot;
        outPos[4] = outPos[1] + new Vector3(0f, -0.42f, 0.02f); outRot[4] = Quaternion.identity;
        outPos[5] = outPos[2] + new Vector3(0f, -0.42f, 0.02f); outRot[5] = Quaternion.identity;
        outPos[6] = Vector3.Lerp(pelvis, neck, 0.62f); outRot[6] = headRot;
        outPos[7] = outPos[4] + new Vector3(0f, -0.40f, 0.03f); outRot[7] = Quaternion.identity;
        outPos[8] = outPos[5] + new Vector3(0f, -0.40f, 0.03f); outRot[8] = Quaternion.identity;
        outPos[9] = Vector3.Lerp(pelvis, neck, 0.82f); outRot[9] = headRot;
        outPos[10] = outPos[7] + new Vector3(0.02f, -0.05f, 0.10f); outRot[10] = Quaternion.identity;
        outPos[11] = outPos[8] + new Vector3(-0.02f, -0.05f, 0.10f); outRot[11] = Quaternion.identity;
        outPos[12] = neck; outRot[12] = headRot;
        outPos[13] = neck + headRot * new Vector3(-0.07f, 0.00f, 0.01f); outRot[13] = headRot;
        outPos[14] = neck + headRot * new Vector3(0.07f, 0.00f, 0.01f);  outRot[14] = headRot;
        outPos[15] = headPos; outRot[15] = headRot;
        outPos[16] = leftShoulder; outRot[16] = headRot;
        outPos[17] = rightShoulder; outRot[17] = headRot;
        outPos[18] = leftElbow; outRot[18] = headRot;
        outPos[19] = rightElbow; outRot[19] = headRot;
        outPos[20] = leftWrist; outRot[20] = headRot;
        outPos[21] = rightWrist; outRot[21] = headRot;
        outPos[22] = leftWrist; outRot[22] = headRot;
        outPos[23] = rightWrist; outRot[23] = headRot;
    }

    bool CheckAvailability()
    {
#if PICO_XR
        try
        {
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
