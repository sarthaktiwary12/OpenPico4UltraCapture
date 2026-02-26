using System.Collections;
using UnityEngine;

public class AutoSessionStarter : MonoBehaviour
{
    public SensorRecorder sensor;
    public SpatialMeshCapture mesh;

    public string defaultTaskType = "pick_place_general";
    public string defaultScenario = "indoor_household";
    public float startDelaySeconds = 1.0f;
    public float stopAfterSeconds = 120.0f;

    private bool _started;

    private IEnumerator Start()
    {
        yield return new WaitForSeconds(startDelaySeconds);

        if (sensor == null)
        {
            Debug.LogError("[AutoStart] SensorRecorder reference is missing.");
            yield break;
        }

        sensor.StartSession(defaultTaskType, defaultScenario);
        mesh?.StartCapture(sensor.GetSessionDir());
        sensor.LogAction("task_start", defaultTaskType, "autostart");
        _started = true;
        Debug.Log("[AutoStart] Recording started.");

        if (stopAfterSeconds <= 0f) yield break;

        yield return new WaitForSeconds(stopAfterSeconds);

        if (!_started || !sensor.IsRecording) yield break;

        sensor.LogAction("task_end", defaultTaskType, "autostop");
        mesh?.StopCapture();
        sensor.StopSession();
        Debug.Log("[AutoStart] Recording stopped.");
    }
}
