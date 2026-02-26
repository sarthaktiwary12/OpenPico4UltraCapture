using System;
using UnityEngine;

public class NativeIMUBridge : MonoBehaviour
{
    public static NativeIMUBridge Instance { get; private set; }
    public Vector3 Acceleration { get; private set; }
    public Vector3 AngularVelocity { get; private set; }
    public bool IsActive { get; private set; }

    void Awake() { if (Instance != null) { Destroy(gameObject); return; } Instance = this; DontDestroyOnLoad(gameObject); }

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var act = up.GetStatic<AndroidJavaObject>("currentActivity");
            var sm = act.Call<AndroidJavaObject>("getSystemService", "sensor");
            var acc = sm.Call<AndroidJavaObject>("getDefaultSensor", 1);
            var gyr = sm.Call<AndroidJavaObject>("getDefaultSensor", 4);
            if (acc == null || gyr == null) { IsActive = false; Input.gyro.enabled = true; return; }
            sm.Call<bool>("registerListener", new SL(v => Acceleration = new Vector3(v[0],v[1],v[2])), acc, 1);
            sm.Call<bool>("registerListener", new SL(v => AngularVelocity = new Vector3(v[0],v[1],v[2])), gyr, 1);
            IsActive = true; Debug.Log("[IMU] Android sensors OK");
        }
        catch (Exception e) { Debug.LogWarning($"[IMU] Native fail: {e.Message}"); IsActive = false; Input.gyro.enabled = true; }
#else
        IsActive = false; Input.gyro.enabled = true;
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    class SL : AndroidJavaProxy
    {
        Action<float[]> _cb;
        public SL(Action<float[]> cb) : base("android.hardware.SensorEventListener") { _cb = cb; }
        void onSensorChanged(AndroidJavaObject e) { var v = e.Get<float[]>("values"); if (v?.Length >= 3) _cb(v); }
        void onAccuracyChanged(AndroidJavaObject s, int a) {}
    }
#else
    void Update() { Acceleration = Input.gyro.userAcceleration; AngularVelocity = Input.gyro.rotationRateUnbiased; }
#endif
}
