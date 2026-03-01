using UnityEngine;
using UnityEngine.UI;

public class SimpleRecordingController : MonoBehaviour
{
    [Header("Systems")]
    public SensorRecorder sensorRecorder;
    public SyncManager syncManager;
    public SpatialMeshCapture spatialMeshCapture;
    public BodyTrackingRecorder bodyTrackingRecorder;

    [Header("UI")]
    public Button btnToggle;
    public Text txtStatus;
    public Text txtButtonLabel;
    public Image btnImage;

    private bool _recording;
    private float _startTime;

    void Start()
    {
        if (btnToggle != null) btnToggle.onClick.AddListener(OnToggle);
        SetIdle();
        Debug.Log("[Controller] Ready. Pull trigger anywhere on panel to toggle recording.");
    }

    void Update()
    {
        if (_recording)
        {
            float elapsed = Time.time - _startTime;
            int min = (int)(elapsed / 60f);
            int sec = (int)(elapsed % 60f);
            SetStatus($"RECORDING\n\n{min:D2}:{sec:D2}\n\nFrames: {sensorRecorder.FrameIndex}");
        }
    }

    void OnToggle()
    {
        Debug.Log("[Controller] Toggle pressed!");
        if (!_recording)
            StartRecording();
        else
            StopRecording();
    }

    void StartRecording()
    {
        try
        {
            sensorRecorder.StartSession("capture", "general");
            string dir = sensorRecorder.GetSessionDir();

            spatialMeshCapture?.StartCapture(dir);
            bodyTrackingRecorder?.StartCapture(dir);

            sensorRecorder.LogAction("task_start", "capture", "user_initiated");

            _recording = true;
            _startTime = Time.time;

            if (txtButtonLabel != null) txtButtonLabel.text = "STOP";
            if (btnImage != null) btnImage.color = new Color(0.8f, 0.2f, 0.2f, 1f);
            SetStatus("RECORDING\n\n00:00\n\nFrames: 0");

            Debug.Log("[Controller] RECORDING STARTED -> " + dir);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[Controller] Failed to start recording: " + e.Message);
            SetStatus("ERROR\n" + e.Message);
        }
    }

    void StopRecording()
    {
        try
        {
            sensorRecorder.LogAction("task_end", "capture", "user_initiated");

            bodyTrackingRecorder?.StopCapture();
            spatialMeshCapture?.StopCapture();
            sensorRecorder.StopSession();

            float dur = Time.time - _startTime;
            long frames = sensorRecorder.FrameIndex;

            _recording = false;

            if (txtButtonLabel != null) txtButtonLabel.text = "RECORD";
            if (btnImage != null) btnImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);

            SetStatus($"Saved!\n{frames} frames\n{dur:F1}s");
            Debug.Log($"[Controller] RECORDING STOPPED. {frames} frames, {dur:F1}s.");
        }
        catch (System.Exception e)
        {
            Debug.LogError("[Controller] Failed to stop recording: " + e.Message);
            _recording = false;
        }
    }

    void SetIdle()
    {
        SetStatus("Pull trigger to\nstart recording");
        if (txtButtonLabel != null) txtButtonLabel.text = "RECORD";
        if (btnImage != null) btnImage.color = new Color(0.2f, 0.7f, 0.2f, 1f);
    }

    void SetStatus(string msg)
    {
        if (txtStatus != null) txtStatus.text = msg;
    }
}
