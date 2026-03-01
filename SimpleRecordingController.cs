using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using TMPro;
using System.Collections.Generic;

public class SimpleRecordingController : MonoBehaviour
{
    [Header("Systems")]
    public SensorRecorder sensorRecorder;
    public SyncManager syncManager;
    public SpatialMeshCapture spatialMeshCapture;
    public BodyTrackingRecorder bodyTrackingRecorder;

    [Header("UI")]
    public Button btnRecord;
    public Button btnStop;
    public TextMeshProUGUI txtStatus;

    private bool _recording;
    private float _startTime;
    private bool _rightWasDown;
    private bool _leftWasDown;

    void Start()
    {
        if (btnRecord != null) btnRecord.onClick.AddListener(OnRecord);
        if (btnStop != null) btnStop.onClick.AddListener(OnStop);
        if (btnStop != null) btnStop.interactable = false;
        SetStatus("Ready.\nRight trigger = Record\nLeft trigger = Stop");
        Debug.Log("[Controller] Ready. Use right trigger to record, left trigger to stop.");
    }

    void OnRecord()
    {
        if (_recording) return;

        sensorRecorder.StartSession("capture", "general");
        string dir = sensorRecorder.GetSessionDir();

        spatialMeshCapture?.StartCapture(dir);
        bodyTrackingRecorder?.StartCapture(dir);

        sensorRecorder.LogAction("task_start", "capture", "user_initiated");

        _recording = true;
        _startTime = Time.time;
        if (btnRecord != null) btnRecord.interactable = false;
        if (btnStop != null) btnStop.interactable = true;
        Debug.Log("[Controller] RECORDING STARTED");
    }

    void OnStop()
    {
        if (!_recording) return;

        sensorRecorder.LogAction("task_end", "capture", "user_initiated");

        bodyTrackingRecorder?.StopCapture();
        spatialMeshCapture?.StopCapture();
        sensorRecorder.StopSession();

        _recording = false;
        if (btnRecord != null) btnRecord.interactable = true;
        if (btnStop != null) btnStop.interactable = false;

        float dur = Time.time - _startTime;
        string dir = sensorRecorder.GetSessionDir();
        SetStatus($"Saved!\n{sensorRecorder.FrameIndex} frames, {dur:F1}s\n\n{dir}");
        Debug.Log($"[Controller] RECORDING STOPPED. {sensorRecorder.FrameIndex} frames.");
    }

    void Update()
    {
        CheckControllerInput();

        if (!_recording) return;

        float elapsed = Time.time - _startTime;
        string syncStr = syncManager != null && syncManager.SyncAchieved
            ? $"Sync: OK (x{syncManager.SyncCount})"
            : "Sync: CLAP to sync";

        SetStatus($"RECORDING\nTime: {elapsed:F0}s | Frames: {sensorRecorder.FrameIndex}\n{syncStr}");
    }

    void CheckControllerInput()
    {
        bool aDown = false;

        // Check A button on right controller
        var rightHand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (rightHand.isValid)
        {
            if (rightHand.TryGetFeatureValue(CommonUsages.primaryButton, out bool a) && a)
                aDown = true;
        }

        // Fallback: scan all controllers for A button
        if (!rightHand.isValid)
        {
            var devices = new List<InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, devices);
            foreach (var dev in devices)
            {
                if (dev.TryGetFeatureValue(CommonUsages.primaryButton, out bool btn) && btn)
                    aDown = true;
            }
        }

        // A button toggles recording on/off
        if (aDown && !_rightWasDown)
        {
            if (!_recording)
            {
                Debug.Log("[Controller] A pressed -> Record");
                OnRecord();
            }
            else
            {
                Debug.Log("[Controller] A pressed -> Stop");
                OnStop();
            }
        }
        _rightWasDown = aDown;
    }

    void SetStatus(string msg)
    {
        try { if (txtStatus != null) txtStatus.text = msg; }
        catch { /* TMPro font missing - ignore */ }
    }
}
