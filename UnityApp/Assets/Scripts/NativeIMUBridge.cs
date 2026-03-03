using System;
using UnityEngine;

public class NativeIMUBridge : MonoBehaviour
{
    public static NativeIMUBridge Instance { get; private set; }
    public Vector3 Acceleration { get; private set; }
    public Vector3 LinearAcceleration { get; private set; }
    public Vector3 Gravity { get; private set; }
    public Vector3 AngularVelocity { get; private set; }
    public bool IsActive { get; private set; }
    public bool HasGravityData { get; private set; }
    public bool IsUsingReconstructedAcceleration { get; private set; }
    public bool HasFreshData => Time.realtimeSinceStartup - _lastSensorUpdateS < 0.5f;

    private float _lastSensorUpdateS;
    private bool _loggedFirstAccelSample;
    private bool _loggedFirstGravitySample;
#if UNITY_ANDROID && !UNITY_EDITOR
    private const int TYPE_ACCELEROMETER = 1;
    private const int TYPE_GYROSCOPE = 4;
    private const int TYPE_GRAVITY = 9;
    private const int TYPE_LINEAR_ACCELERATION = 10;

    private AndroidJavaObject _sensorManager;
    private AndroidJavaObject _accSensor;
    private AndroidJavaObject _gravSensor;
    private AndroidJavaObject _linSensor;
    private AndroidJavaObject _gyroSensor;
    private SL _accListener;
    private SL _gravListener;
    private SL _linListener;
    private SL _gyroListener;
    private Vector3 _accSensorValue;
    private bool _hasAccSample;
    private bool _hasLinSample;
    private bool _hasGravSample;
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
            _accSensor = _sensorManager?.Call<AndroidJavaObject>("getDefaultSensor", TYPE_ACCELEROMETER);
            _gravSensor = _sensorManager?.Call<AndroidJavaObject>("getDefaultSensor", TYPE_GRAVITY);
            _linSensor = _sensorManager?.Call<AndroidJavaObject>("getDefaultSensor", TYPE_LINEAR_ACCELERATION);
            _gyroSensor = _sensorManager?.Call<AndroidJavaObject>("getDefaultSensor", TYPE_GYROSCOPE);

            if (_sensorManager == null)
            {
                IsActive = false;
                Input.gyro.enabled = true;
                Debug.LogWarning("[IMU] Android SensorManager unavailable, using Unity fallback.");
                return;
            }

            _accListener = new SL(OnSensorChanged);
            _gravListener = new SL(OnSensorChanged);
            _linListener = new SL(OnSensorChanged);
            _gyroListener = new SL(OnSensorChanged);

            bool accOk = _accSensor != null && _sensorManager.Call<bool>("registerListener", _accListener, _accSensor, 0);
            bool gravOk = _gravSensor != null && _sensorManager.Call<bool>("registerListener", _gravListener, _gravSensor, 0);
            bool linOk = _linSensor != null && _sensorManager.Call<bool>("registerListener", _linListener, _linSensor, 0);
            bool gyroOk = _gyroSensor != null && _sensorManager.Call<bool>("registerListener", _gyroListener, _gyroSensor, 0);

            // Activate if we have ANY useful sensor data path. On PICO, the XR runtime
            // may consume the gyroscope exclusively, but gravity and accelerometer can
            // still provide valuable data. Gyro fallback comes from XR head kinematics.
            bool hasAnySensor = accOk || gravOk || linOk || gyroOk;
            IsActive = hasAnySensor;

            // Always enable Unity gyro as supplemental source
            Input.gyro.enabled = true;

            Debug.Log($"[IMU] Sensor(acc): {DescribeSensor(_accSensor)} register={accOk}");
            Debug.Log($"[IMU] Sensor(gravity): {DescribeSensor(_gravSensor)} register={gravOk}");
            Debug.Log($"[IMU] Sensor(linear): {DescribeSensor(_linSensor)} register={linOk}");
            Debug.Log($"[IMU] Sensor(gyro): {DescribeSensor(_gyroSensor)} register={gyroOk}");

            if (IsActive) Debug.Log($"[IMU] Android sensors OK (acc={accOk}, grav={gravOk}, lin={linOk}, gyro={gyroOk})");
            else Debug.LogWarning("[IMU] No Android sensors registered, using Unity fallback only.");
        }
        catch (Exception e) { Debug.LogWarning($"[IMU] Native fail: {e.Message}"); IsActive = false; Input.gyro.enabled = true; }
#else
        IsActive = false; Input.gyro.enabled = true;
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void OnSensorChanged(int sensorType, float[] values)
    {
        if (values == null || values.Length < 3) return;

        var v = new Vector3(values[0], values[1], values[2]);
        _lastSensorUpdateS = Time.realtimeSinceStartup;

        if (sensorType == TYPE_ACCELEROMETER)
        {
            _accSensorValue = v;
            _hasAccSample = true;
            if (!_loggedFirstAccelSample)
            {
                _loggedFirstAccelSample = true;
                Debug.Log($"[IMU] First accelerometer sample mag={v.magnitude:F3} m/s^2");
            }
        }
        else if (sensorType == TYPE_GRAVITY)
        {
            Gravity = v;
            _hasGravSample = true;
            HasGravityData = true;
            if (!_loggedFirstGravitySample)
            {
                _loggedFirstGravitySample = true;
                Debug.Log($"[IMU] First gravity sample mag={v.magnitude:F3} m/s^2");
            }
        }
        else if (sensorType == TYPE_LINEAR_ACCELERATION)
        {
            LinearAcceleration = v;
            _hasLinSample = true;
        }
        else if (sensorType == TYPE_GYROSCOPE)
        {
            AngularVelocity = v;
        }

        RefreshAccelerationEstimate();
    }

    private void RefreshAccelerationEstimate()
    {
        Vector3 raw = Vector3.zero;
        bool reconstructed = false;
        string source = "none";

        if (_hasAccSample)
        {
            raw = _accSensorValue;
            source = "accelerometer";

            // Some runtimes expose linear acceleration on TYPE_ACCELEROMETER.
            if (_hasGravSample && raw.magnitude < 3f)
            {
                if (_hasLinSample)
                {
                    raw = LinearAcceleration + Gravity;
                    source = "linear_plus_gravity";
                    reconstructed = true;
                }
                else
                {
                    raw = raw + Gravity;
                    source = "acc_plus_gravity";
                    reconstructed = true;
                }
            }
        }
        else if (_hasLinSample && _hasGravSample)
        {
            raw = LinearAcceleration + Gravity;
            source = "linear_plus_gravity_only";
            reconstructed = true;
        }
        else if (_hasLinSample)
        {
            raw = LinearAcceleration;
            source = "linear_only";
        }

        IsUsingReconstructedAcceleration = reconstructed;
        Acceleration = raw;

        if (_loggedFirstAccelSample && _lastSensorUpdateS > 0f && source != "none")
        {
            // Keep one explicit source trace in logs for diagnostics.
            if (_loggedFirstAccelSample)
            {
                Debug.Log($"[IMU] Active acceleration source={source}, mag={Acceleration.magnitude:F3}, reconstructed={reconstructed}");
                // Reuse the existing guard to emit source once.
                _loggedFirstAccelSample = false;
            }
        }
    }

    private static string DescribeSensor(AndroidJavaObject sensor)
    {
        if (sensor == null) return "none";
        try
        {
            string name = sensor.Call<string>("getName");
            string vendor = sensor.Call<string>("getVendor");
            int type = sensor.Call<int>("getType");
            return $"{name} ({vendor}) type={type}";
        }
        catch
        {
            return "unknown";
        }
    }

    class SL : AndroidJavaProxy
    {
        private readonly Action<int, float[]> _cb;

        public SL(Action<int, float[]> cb) : base("android.hardware.SensorEventListener")
        {
            _cb = cb;
        }

        void onSensorChanged(AndroidJavaObject e)
        {
            var v = e.Get<float[]>("values");
            if (v?.Length < 3) return;

            int sensorType = 0;
            try
            {
                using var sensor = e.Get<AndroidJavaObject>("sensor");
                if (sensor != null) sensorType = sensor.Call<int>("getType");
            }
            catch
            {
            }

            _cb(sensorType, v);
        }

        void onAccuracyChanged(AndroidJavaObject s, int a) {}
    }
#else
    void Update()
    {
        Acceleration = Input.acceleration;
        AngularVelocity = Input.gyro.rotationRateUnbiased;
        Gravity = Input.acceleration - Input.gyro.userAcceleration;
        HasGravityData = Gravity.sqrMagnitude > 1e-8f;
        LinearAcceleration = Input.gyro.userAcceleration;
        IsUsingReconstructedAcceleration = false;
    }
#endif

    void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _sensorManager?.Call("unregisterListener", _accListener);
            _sensorManager?.Call("unregisterListener", _gravListener);
            _sensorManager?.Call("unregisterListener", _linListener);
            _sensorManager?.Call("unregisterListener", _gyroListener);
        }
        catch { }
        _accSensor?.Dispose();
        _gravSensor?.Dispose();
        _linSensor?.Dispose();
        _gyroSensor?.Dispose();
        _sensorManager?.Dispose();
        _accSensor = null;
        _gravSensor = null;
        _linSensor = null;
        _gyroSensor = null;
        _sensorManager = null;
        _accListener = null;
        _gravListener = null;
        _linListener = null;
        _gyroListener = null;
#endif
        if (Instance == this) Instance = null;
    }
}
