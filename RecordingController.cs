using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class RecordingController : MonoBehaviour
{
    [Header("Systems")]
    public SensorRecorder sensor;
    public SyncManager sync;
    public SpatialMeshCapture mesh;

    [Header("UI — Task Select")]
    public GameObject panelSelect, panelRec, panelDone;
    public TMP_Dropdown ddScenario, ddTask;
    public Button btnStart;

    [Header("UI — Recording")]
    public Button btnTaskStart, btnTaskEnd, btnStop, btnManualSync;
    public TextMeshProUGUI txtStatus, txtTimer, txtFrames, txtSync, txtHands;
    public Image dotRec;

    [Header("UI — Done")]
    public TextMeshProUGUI txtReport;
    public Button btnNew;

    static readonly Dictionary<string, List<string>> Scenarios = new()
    {
        { "Indoor Household", new() { "Pick-Place: Utensils", "Pick-Place: Containers", "Pick-Place: Cloth", "Pick-Place: General" } },
        { "Office / Retail", new() { "Shelf Sorting", "Object Placement", "Appliance: Espresso", "Appliance: General" } }
    };
    static readonly Dictionary<string, string> TaskCode = new()
    {
        {"Pick-Place: Utensils","pick_place_utensils"}, {"Pick-Place: Containers","pick_place_containers"},
        {"Pick-Place: Cloth","pick_place_cloth"}, {"Pick-Place: General","pick_place_general"},
        {"Shelf Sorting","shelf_sorting"}, {"Object Placement","object_placement"},
        {"Appliance: Espresso","appliance_espresso"}, {"Appliance: General","appliance_general"}
    };
    static readonly Dictionary<string, string> ScenCode = new()
    {
        {"Indoor Household","indoor_household"}, {"Office / Retail","office_retail"}
    };

    enum St { Idle, Rec, Task, Done }
    St _st = St.Idle;
    float _t0;
    int _handOk, _handTotal;
    bool _taskLogged;
    string _scDisp, _tkDisp;

    void Start()
    {
        ddScenario.ClearOptions(); ddScenario.AddOptions(new List<string>(Scenarios.Keys));
        ddScenario.onValueChanged.AddListener(i => { ddTask.ClearOptions(); ddTask.AddOptions(Scenarios[new List<string>(Scenarios.Keys)[i]]); });
        ddScenario.onValueChanged.Invoke(0);
        btnStart.onClick.AddListener(DoStart); btnTaskStart.onClick.AddListener(DoTaskStart);
        btnTaskEnd.onClick.AddListener(DoTaskEnd); btnStop.onClick.AddListener(DoStop);
        btnManualSync.onClick.AddListener(() => sync?.ManualSync());
        btnNew.onClick.AddListener(() => { _st = St.Idle; Show(panelSelect); });
        Show(panelSelect);
    }

    void DoStart()
    {
        _scDisp = new List<string>(Scenarios.Keys)[ddScenario.value];
        _tkDisp = Scenarios[_scDisp][ddTask.value];
        string tc = TaskCode.GetValueOrDefault(_tkDisp, "unknown");
        string sc = ScenCode.GetValueOrDefault(_scDisp, "unknown");
        sensor.StartSession(tc, sc);
        mesh?.StartCapture(sensor.GetSessionDir());
        _t0 = Time.time; _handOk = _handTotal = 0; _taskLogged = false;
        _st = St.Rec; Show(panelRec); Btns();
        Status($"<b>RECORDING</b>  {_scDisp} / {_tkDisp}\n\n" +
            "<color=#FFAA00>1)</color> CLAP HANDS to sync with spatial video\n" +
            "<color=#FFAA00>2)</color> Press <b>Start Task</b>\n" +
            "<color=#FFAA00>3)</color> SLOW, ROBOT-LIKE movements\n" +
            "<color=#FFAA00>4)</color> Keep BOTH HANDS visible always");
    }

    void DoTaskStart()
    {
        sensor.LogAction("task_start", TaskCode.GetValueOrDefault(_tkDisp, ""), "operator");
        _taskLogged = true; _st = St.Task; Btns();
        Status("<color=#FF4444><b>● TASK ACTIVE</b></color>\n\nSlow. Deliberate. Hands visible.");
    }

    void DoTaskEnd()
    {
        sensor.LogAction("task_end", TaskCode.GetValueOrDefault(_tkDisp, ""), "operator");
        _st = St.Rec; Btns();
        Status("Segment ended.\n<b>Start Task</b> again or <b>Stop Session</b>.");
    }

    void DoStop()
    {
        if (_st == St.Task) sensor.LogAction("task_end", "", "auto_stop");
        mesh?.StopCapture(); sensor.StopSession();
        _st = St.Done; Show(panelDone); txtReport.text = Validate();
    }

    void Update()
    {
        if (_st != St.Rec && _st != St.Task) return;
        float el = Time.time - _t0;
        string tc = el > 110 ? "#FF4444" : el > 90 ? "#FFAA00" : "#FFFFFF";
        if (txtTimer) txtTimer.text = $"<color={tc}>{el:F0}s / ~120s</color>";
        if (txtFrames) txtFrames.text = $"Frames: {sensor.FrameIndex}";
        if (txtSync) txtSync.text = sync != null && sync.SyncAchieved
            ? $"<color=#44FF44>✓ Sync (x{sync.SyncCount})</color>"
            : "<color=#FFAA00>⚠ CLAP to sync!</color>";
        _handTotal++; if (HandsVis()) _handOk++;
        float hpct = _handTotal > 0 ? (float)_handOk / _handTotal * 100 : 0;
        if (txtHands) txtHands.text = $"Hands: <color={(hpct > 80 ? "#44FF44" : hpct > 60 ? "#FFAA00" : "#FF4444")}>{hpct:F0}%</color>";
        if (dotRec) dotRec.color = new Color(1, _st == St.Task ? 0 : 0.5f, 0, 0.5f + 0.5f * Mathf.Sin(Time.time * 4));
    }

    bool HandsVis()
    {
#if PICO_XR
        try
        {
            var l = new HandJointsLocations();
            var r = new HandJointsLocations();
            bool lk = PXR_HandTracking.GetJointLocations(HandType.HandLeft, ref l) && l.jointLocations?.Length > 0;
            bool rk = PXR_HandTracking.GetJointLocations(HandType.HandRight, ref r) && r.jointLocations?.Length > 0;
            return lk || rk;
        }
        catch
        {
            return true;
        }
#else
        return true;
#endif
    }

    string Validate()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<b>SESSION REPORT</b>\n");
        string d = sensor.GetSessionDir();
        float dur = Time.time - _t0;
        sb.AppendLine(Chk(dur > 60 && dur < 300, $"Duration: {dur:F0}s"));
        foreach (var f in new[] { "hand_joints.csv","head_pose.csv","imu.csv","action_log.csv","calibration.json","session_summary.json" })
            sb.AppendLine(Chk(File.Exists(Path.Combine(d, f)), f));
        sb.AppendLine(Chk(sensor.FrameIndex > 100, $"Frames: {sensor.FrameIndex}"));
        float hp = _handTotal > 0 ? (float)_handOk / _handTotal * 100 : 0;
        sb.AppendLine(Chk(hp > 70, $"Hand visibility: {hp:F0}%"));
        sb.AppendLine(Chk(sync != null && sync.SyncAchieved, $"Sync: {(sync?.SyncAchieved == true ? $"OK ({sync.SyncCount})" : "MISSING")}"));
        sb.AppendLine(Chk(_taskLogged, "Task start/end logged"));
        sb.AppendLine(Chk(Directory.Exists(Path.Combine(d, "depth_mesh")), "Depth mesh", false));
        sb.AppendLine($"\nSaved to:\n<size=80%>{d}</size>");
        sb.AppendLine("\n<i>Transfer via ADB, blur faces, deliver.</i>");
        return sb.ToString();
    }

    string Chk(bool ok, string l, bool crit = true) => $"  {(ok ? "<color=#44FF44>✓</color>" : crit ? "<color=#FF4444>✗</color>" : "<color=#FFAA00>⚠</color>")}  {l}";
    void Show(GameObject p) { panelSelect?.SetActive(p == panelSelect); panelRec?.SetActive(p == panelRec); panelDone?.SetActive(p == panelDone); }
    void Btns() { btnTaskStart.interactable = _st == St.Rec; btnTaskEnd.interactable = _st == St.Task; btnStop.interactable = _st != St.Idle; }
    void Status(string s) { if (txtStatus) txtStatus.text = s; }
}
