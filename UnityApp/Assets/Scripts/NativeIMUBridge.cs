using System;
using UnityEngine;

public class NativeIMUBridge : MonoBehaviour
{
    public static NativeIMUBridge Instance { get; private set; }
    public Vector3 Acceleration { get; private set; }
    public Vector3 AngularVelocity { get; private set; }
    public bool IsActive { get; private set; }
    public bool HasFreshData => Time.realtimeSinceStartup - _lastSensorUpdateS < 0.5f;

    private float _lastSensorUpdateS;
#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject _sensorManager;
    private AndroidJavaObject _accSensor;
    private AndroidJavaObject _gyroSensor;
    private SL _accListener;
    private SL _gyroListener;
#endif

    void Awake() { if (Instance != null) { Destroy(gameObject); return; } Instance = this; DontDestroyOnLoad(gameObject); }

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var act = up.GetStatic<AndroidJavaObject>("currentActivity");
            _sensorManager = act.Call<AndroidJavaObject>("getSystemService", "sensor");
            _accSensor = _sensorManager?.Call<AndroidJavaObject>("getDefaultSensor", 1);
            _gyroSensor = _sensorManager?.Call<AndroidJavaObject>("getDefaultSensor", 4);
            if (_sensorManager == null || _accSensor == null || _gyroSensor == null)
            {
                IsActive = false;
                Input.gyro.enabled = true;
                Debug.LogWarning("[IMU] Android sensors unavailable, using Unity fallback.");
                return;
            }

            _accListener = new SL(v => { Acceleration = new Vector3(v[0], v[1], v[2]); _lastSensorUpdateS = Time.realtimeSinceStartup; });
            _gyroListener = new SL(v => { AngularVelocity = new Vector3(v[0], v[1], v[2]); _lastSensorUpdateS = Time.realtimeSinceStartup; });

            bool accOk = _sensorManager.Call<bool>("registerListener", _accListener, _accSensor, 0);
            bool gyroOk = _sensorManager.Call<bool>("registerListener", _gyroListener, _gyroSensor, 0);
            IsActive = accOk && gyroOk;
            if (IsActive) Debug.Log("[IMU] Android sensors OK");
            else Debug.LogWarning($"[IMU] registerListener failed: acc={accOk}, gyro={gyroOk}");
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

    void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _sensorManager?.Call("unregisterListener", _accListener);
            _sensorManager?.Call("unregisterListener", _gyroListener);
        }
        catch { }
        _accSensor?.Dispose();
        _gyroSensor?.Dispose();
        _sensorManager?.Dispose();
        _accSensor = null;
        _gyroSensor = null;
        _sensorManager = null;
        _accListener = null;
        _gyroListener = null;
#endif
        if (Instance == this) Instance = null;
    }
}
