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
        _on = true;
        if (!_available) Debug.LogWarning("[Body] Body tracking not available — body_pose.csv will be empty.");
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
        if (!_on || !_available || sensorRecorder == null || !sensorRecorder.IsRecording) return;
        SampleBody();
    }

    void SampleBody()
    {
#if PICO_XR
        try
        {
            var getInfo = new BodyTrackingGetDataInfo { displayTime = 0 };
            var data = new BodyTrackingData();
            int ret = PXR_MotionTracking.GetBodyTrackingData(ref getInfo, ref data);
            if (ret != 0 || data.roleDatas == null) return;

            double ts = sensorRecorder.SessionElapsed;
            long frame = sensorRecorder.FrameIndex;
            int n = Mathf.Min(data.roleDatas.Length, 24);

            for (int i = 0; i < n; i++)
            {
                var j = data.roleDatas[i];
                string jn = i < BodyJointNames.Length ? BodyJointNames[i] : $"BJ{i}";
                var p = new Vector3((float)j.localPose.PosX, (float)j.localPose.PosY, (float)j.localPose.PosZ);
                var q = new Quaternion((float)j.localPose.RotQx, (float)j.localPose.RotQy, (float)j.localPose.RotQz, (float)j.localPose.RotQw);
                ulong conf = (ulong)j.bodyAction;
                _w.WriteLine($"{ts:F6},{frame},{i},{jn},{p.x:F6},{p.y:F6},{p.z:F6},{q.x:F6},{q.y:F6},{q.z:F6},{q.w:F6},{conf}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Body] Sample error: {e.Message}");
        }
#endif
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
