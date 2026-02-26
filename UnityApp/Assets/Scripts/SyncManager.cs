using System;
using UnityEngine;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class SyncManager : MonoBehaviour
{
    public SensorRecorder sensorRecorder;
    public AudioSource audioSource;
    public float clapThresholdM = 0.08f, clapResetM = 0.20f, cooldownS = 2f;
    public bool SyncAchieved { get; private set; }
    public int SyncCount { get; private set; }
    public double SyncTimestamp { get; private set; }
    private bool _armed = true; private float _lastClap = -99f;
    AudioClip _beep;

    void Start()
    {
        _beep = MakeBeep(1000f, 0.2f);
        if (!audioSource) { audioSource = gameObject.AddComponent<AudioSource>(); audioSource.playOnAwake = false; audioSource.spatialBlend = 0; }
        audioSource.clip = _beep;
    }

    void Update()
    {
        if (sensorRecorder == null || !sensorRecorder.IsRecording) return;
        if (!GetPalm("left", out var lp) || !GetPalm("right", out var rp)) return;
        float d = Vector3.Distance(lp, rp);
        if (_armed && d < clapThresholdM && Time.time - _lastClap > cooldownS) { OnClap(d, lp, rp); _armed = false; _lastClap = Time.time; }
        else if (!_armed && d > clapResetM) _armed = true;
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

    bool GetPalm(string side, out Vector3 pos)
    {
        pos = Vector3.zero;
#if PICO_XR
        try
        {
            var ht = side == "left" ? HandType.HandLeft : HandType.HandRight;
            var jl = new HandJointsLocations();
            if (!PXR_HandTracking.GetJointLocations(ht, ref jl) || jl.jointLocations == null || jl.jointLocations.Length < 1) return false;
            if ((jl.jointLocations[0].locationStatus & (ulong)HandLocationStatus.PositionValid) == 0) return false;
            pos = jl.jointLocations[0].pose.Position.ToVector3();
            return true;
        }
        catch
        {
            return false;
        }
#else
        return false;
#endif
    }

    static AudioClip MakeBeep(float hz, float dur)
    {
        int sr=44100; int n=(int)(sr*dur); var d=new float[n];
        for(int i=0;i<n;i++){float t=(float)i/sr; float env=Mathf.Min(t/0.01f,1)*Mathf.Min((dur-t)/0.01f,1); d[i]=Mathf.Sin(2*Mathf.PI*hz*t)*env*0.8f;}
        var c=AudioClip.Create("beep",n,1,sr,false); c.SetData(d,0); return c;
    }
}
