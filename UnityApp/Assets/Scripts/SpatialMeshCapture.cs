using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class SpatialMeshCapture : MonoBehaviour
{
    public float intervalS = 1f;
    public SensorRecorder sensorRecorder;
    private string _dir; private StreamWriter _idx; private float _last; private int _n; private bool _on;

    public void StartCapture(string sessionDir)
    {
        _dir = Path.Combine(sessionDir, "depth_mesh"); Directory.CreateDirectory(_dir);
        _idx = new StreamWriter(Path.Combine(sessionDir, "depth_mesh_index.csv"), false, new UTF8Encoding(false));
        _idx.WriteLine("ts_s,frame,snapshot,filename,verts,tris"); _n = 0; _last = Time.realtimeSinceStartup; _on = true;
    }

    public void StopCapture() { _on = false; _idx?.Flush(); _idx?.Close(); _idx = null; Debug.Log($"[Mesh] {_n} snapshots"); }

    void Update()
    {
        if (!_on || sensorRecorder == null || !sensorRecorder.IsRecording) return;
        if (Time.realtimeSinceStartup - _last < intervalS) return; _last = Time.realtimeSinceStartup;
        Snap();
    }

    void Snap()
    {
        var verts = new List<Vector3>(); var tris = new List<int>();
        foreach (var mf in FindObjectsOfType<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            string nm = mf.gameObject.name.ToLower();
            bool isSpatial = nm.Contains("spatial") || nm.Contains("mesh");
#if PICO_XR
            isSpatial |= mf.GetComponent<PXR_MeshRendering>() != null;
#endif
            if (mf.transform.parent != null) { string pn = mf.transform.parent.name.ToLower(); isSpatial |= pn.Contains("spatial") || pn.Contains("mesh"); }
            if (!isSpatial) continue;
            int off = verts.Count; var m = mf.sharedMesh; var t = mf.transform;
            foreach (var v in m.vertices) verts.Add(t.TransformPoint(v));
            foreach (var i in m.triangles) tris.Add(i + off);
        }
        if (verts.Count == 0) return;
        string fn = $"mesh_{_n:D4}.ply";
        using (var w = new StreamWriter(Path.Combine(_dir, fn), false, new UTF8Encoding(false)))
        {
            w.WriteLine("ply"); w.WriteLine("format ascii 1.0");
            w.WriteLine($"element vertex {verts.Count}"); w.WriteLine("property float x"); w.WriteLine("property float y"); w.WriteLine("property float z");
            w.WriteLine($"element face {tris.Count/3}"); w.WriteLine("property list uchar int vertex_indices"); w.WriteLine("end_header");
            foreach (var v in verts) w.WriteLine($"{v.x:F4} {v.y:F4} {v.z:F4}");
            for (int i = 0; i < tris.Count; i += 3) w.WriteLine($"3 {tris[i]} {tris[i+1]} {tris[i+2]}");
        }
        _idx.WriteLine($"{sensorRecorder.SessionElapsed:F6},{sensorRecorder.FrameIndex},{_n},{fn},{verts.Count},{tris.Count/3}");
        _n++;
    }

    void OnDestroy() { if (_on) StopCapture(); }
}
