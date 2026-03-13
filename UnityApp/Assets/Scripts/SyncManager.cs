using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class SyncManager : MonoBehaviour
{
    public SensorRecorder sensorRecorder;
    public AudioSource audioSource;
    [Tooltip("Palms must be within this distance for a sync clap.")]
    public float clapThresholdM = 0.08f;
    public float cooldownS = 2f;
    public bool SyncAchieved { get; private set; }
    public int SyncCount { get; private set; }
    public double SyncTimestamp { get; private set; }
    private float _lastClap = -99f;
    AudioClip _beep;
    private XRHandSubsystem _xrHandSub;
    private bool _xrHandSubSearched;
    private int _frameCount;

    void Start()
    {
        _beep = MakeBeep(1000f, 0.2f);
        if (!audioSource) { audioSource = gameObject.AddComponent<AudioSource>(); audioSource.playOnAwake = false; audioSource.spatialBlend = 0; }
        audioSource.clip = _beep;
    }

    void Update()
    {
        if (sensorRecorder == null || !sensorRecorder.IsRecording) return;
        _frameCount++;
        if (_frameCount % 2 != 0) return;

        if (!GetPalmPoses(out Pose lp, out Pose rp)) return;
        float d = Vector3.Distance(lp.position, rp.position);

        // Reject garbage: XRInput fallback reports identical positions (d≈0)
        if (d < 0.01f) return;

        if (d < clapThresholdM && Time.time - _lastClap > cooldownS)
        {
            OnClap(d, lp.position, rp.position);
            _lastClap = Time.time;
        }
    }

    void OnClap(float dist, Vector3 lp, Vector3 rp)
    {
        SyncCount++; SyncTimestamp = sensorRecorder.SessionElapsed;
        double wc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        sensorRecorder.LogAction("sync_clap", $"clap_{SyncCount}", $"palm_dist={dist:F4}|wall={wc:F3}");
        audioSource.Play(); SyncAchieved = true;
        Debug.Log($"[Sync] CLAP #{SyncCount} t={SyncTimestamp:F3}s");
    }

    public void ManualSync()
    {
        if (sensorRecorder == null || !sensorRecorder.IsRecording) return;
        SyncCount++; SyncTimestamp = sensorRecorder.SessionElapsed;
        double wc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        sensorRecorder.LogAction("sync_manual", $"manual_{SyncCount}", $"wall={wc:F3}");
        audioSource.Play(); SyncAchieved = true;
    }

    bool GetPalmPoses(out Pose leftPose, out Pose rightPose)
    {
        leftPose = Pose.identity;
        rightPose = Pose.identity;

        // Primary: XRHands subsystem (full pose with orientation)
        if (!_xrHandSubSearched || (_xrHandSub == null && _frameCount % 90 == 0))
        {
            _xrHandSubSearched = true;
            var subs = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            _xrHandSub = subs.Count > 0 ? subs[0] : null;
        }

        if (_xrHandSub != null && _xrHandSub.running &&
            _xrHandSub.leftHand.isTracked && _xrHandSub.rightHand.isTracked)
        {
            var lJoint = _xrHandSub.leftHand.GetJoint(XRHandJointID.Palm);
            var rJoint = _xrHandSub.rightHand.GetJoint(XRHandJointID.Palm);
            if (lJoint.TryGetPose(out leftPose) && rJoint.TryGetPose(out rightPose))
                return true;
        }

#if PICO_XR
        // Secondary: PICO native (has orientation)
        if (TryGetPicoPalmPose(HandType.HandLeft, out leftPose) &&
            TryGetPicoPalmPose(HandType.HandRight, out rightPose))
            return true;
#endif

        return false;
    }

#if PICO_XR
    static bool TryGetPicoPalmPose(HandType handType, out Pose pose)
    {
        pose = Pose.identity;
        try
        {
            var jl = new HandJointLocations();
            if (PXR_Plugin.HandTracking.UPxr_GetHandTrackerJointLocations(handType, ref jl)
                && jl.jointLocations != null && jl.jointLocations.Length > 0)
            {
                var j = jl.jointLocations[0];
                if (((ulong)j.locationStatus & (ulong)HandLocationStatus.PositionValid) != 0)
                {
                    pose = new Pose(j.pose.Position.ToVector3(), j.pose.Orientation.ToQuat());
                    return true;
                }
            }
        }
        catch { }
        return false;
    }
#endif

    static AudioClip MakeBeep(float hz, float dur)
    {
        int sr=44100; int n=(int)(sr*dur); var d=new float[n];
        for(int i=0;i<n;i++){float t=(float)i/sr; float env=Mathf.Min(t/0.01f,1)*Mathf.Min((dur-t)/0.01f,1); d[i]=Mathf.Sin(2*Mathf.PI*hz*t)*env*0.8f;}
        var c=AudioClip.Create("beep",n,1,sr,false); c.SetData(d,0); return c;
    }
}
