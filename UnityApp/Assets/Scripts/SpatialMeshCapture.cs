using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR;
#if PICO_XR
using Unity.XR.PXR;
#if PICO_OPENXR_SDK
using Unity.XR.OpenXR.Features.PICOSupport;
#endif
#endif

public class SpatialMeshCapture : MonoBehaviour
{
    private static SpatialMeshCapture _activeWriter;
    public float intervalS = 1f;
    public SensorRecorder sensorRecorder;
    private string _dir; private StreamWriter _idx; private float _last; private int _n; private bool _on;
    private bool _ownsWriter;
    private bool _queryInFlight;
    private bool _loggedNoMesh;
    private bool _providerReady;
    private bool _providerStartInFlight;
    private int _emptyQueryCount;
    private int _lastLoggedQueryCode = int.MinValue;
#if PICO_XR
    private readonly List<PxrSpatialMeshInfo> _eventMeshes = new List<PxrSpatialMeshInfo>();
    private bool _hasEventMeshes;
#endif
    private XRMeshSubsystem _xrMeshSubsystem;
    private bool _xrMeshInitTried;
    private readonly List<MeshInfo> _xrMeshInfos = new List<MeshInfo>();
    private readonly Dictionary<MeshId, Mesh> _xrMeshCache = new Dictionary<MeshId, Mesh>();

    public void StartCapture(string sessionDir)
    {
        if (_activeWriter != null && _activeWriter != this)
        {
            Debug.LogWarning("[Mesh] Another SpatialMeshCapture instance is already active. This instance will stay idle.");
            _ownsWriter = false;
            _on = false;
            return;
        }
        _activeWriter = this;
        _ownsWriter = true;

        _dir = Path.Combine(sessionDir, "depth_mesh"); Directory.CreateDirectory(_dir);
        _idx = new StreamWriter(Path.Combine(_dir, "depth_mesh_index.csv"), false, new UTF8Encoding(false));
        _idx.WriteLine("ts_s,frame,snapshot,filename,verts,tris");
        _n = 0;
        _last = Time.realtimeSinceStartup;
        _on = true;
        _loggedNoMesh = false;
        _providerReady = false;
        _providerStartInFlight = false;
        _emptyQueryCount = 0;
        _lastLoggedQueryCode = int.MinValue;
#if PICO_XR
        _eventMeshes.Clear();
        _hasEventMeshes = false;
#endif
        _xrMeshInitTried = false;
        _xrMeshInfos.Clear();
        _xrMeshCache.Clear();

#if PICO_XR && PICO_OPENXR_SDK
        OpenXRExtensions.SpatialMeshDataUpdated += OnSpatialMeshDataUpdated;
#endif
        EnsureXRMeshSubsystem();
        StartCoroutine(EnsureProviderReady());
    }

    public void StopCapture()
    {
        _on = false;
        if (_activeWriter == this) _activeWriter = null;
#if PICO_XR && PICO_OPENXR_SDK
        OpenXRExtensions.SpatialMeshDataUpdated -= OnSpatialMeshDataUpdated;
#endif
        foreach (var kv in _xrMeshCache) Destroy(kv.Value);
        _xrMeshCache.Clear();
        _idx?.Flush(); _idx?.Close(); _idx = null; Debug.Log($"[Mesh] {_n} snapshots");
    }

    void Update()
    {
        if (!_on || !_ownsWriter || sensorRecorder == null || !sensorRecorder.IsRecording) return;
        if (Time.realtimeSinceStartup - _last < intervalS) return; _last = Time.realtimeSinceStartup;
        if (!_queryInFlight) StartCoroutine(SnapRoutine());
    }

#if PICO_XR && PICO_OPENXR_SDK
    void OnSpatialMeshDataUpdated(List<PxrSpatialMeshInfo> meshInfos)
    {
        if (!_on || meshInfos == null) return;
        _eventMeshes.Clear();
        _eventMeshes.AddRange(meshInfos);
        _hasEventMeshes = _eventMeshes.Count > 0;
    }
#endif

    System.Collections.IEnumerator EnsureProviderReady()
    {
#if PICO_XR
        if (_providerStartInFlight) yield break;
        _providerStartInFlight = true;

        // Spatial mesh queries may validate against both anchor and scene providers.
        var anchorStartTask = PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SpatialAnchor);
        while (!anchorStartTask.IsCompleted) yield return null;
        if (anchorStartTask.IsCompletedSuccessfully)
        {
            sensorRecorder?.LogAction("mesh_provider_start", "spatial_anchor", anchorStartTask.Result == PxrResult.SUCCESS ? "ok" : anchorStartTask.Result.ToString());
        }

        PxrSenseDataProviderState state;
        var st = PXR_MixedReality.GetSenseDataProviderState(PxrSenseDataProviderType.SceneCapture, out state);
        if (st == PxrResult.SUCCESS && state == PxrSenseDataProviderState.Running)
        {
            _providerReady = true;
            _providerStartInFlight = false;
            yield break;
        }

        var startTask = PXR_MixedReality.StartSenseDataProvider(PxrSenseDataProviderType.SceneCapture);
        while (!startTask.IsCompleted) yield return null;

        if (startTask.IsCompletedSuccessfully && startTask.Result == PxrResult.SUCCESS)
        {
            _providerReady = true;
            Debug.Log("[Mesh] Scene capture provider started.");
            sensorRecorder?.LogAction("mesh_provider_start", "scene_capture", "ok");
        }
        else
        {
            _providerReady = false;
            Debug.LogWarning($"[Mesh] Failed to start scene capture provider: {startTask.Status}");
            sensorRecorder?.LogAction("mesh_provider_start", "scene_capture", "failed");
        }
        _providerStartInFlight = false;
#else
        yield break;
#endif
    }

    System.Collections.IEnumerator SnapRoutine()
    {
        _queryInFlight = true;
        bool wrote = SnapFromXRMeshSubsystem();
#if PICO_XR
        if (!_providerReady)
        {
            StartCoroutine(EnsureProviderReady());
        }

        if (_hasEventMeshes && _eventMeshes.Count > 0)
        {
            wrote = SnapFromPicoMeshInfos(_eventMeshes);
        }

        Task<(PxrResult result, List<PxrSpatialMeshInfo> meshInfos)> task = null;
        if (!wrote)
        {
            try
            {
                task = PXR_MixedReality.QueryMeshAnchorAsync();
            }
            catch
            {
                task = null;
            }

            if (task != null)
            {
                float wait = 0f;
                while (!task.IsCompleted && wait < 1.5f)
                {
                    wait += Time.unscaledDeltaTime;
                    yield return null;
                }

                if (task.IsCompletedSuccessfully)
                {
                    var result = task.Result;
                    int queryCode = (int)result.result;
                    if (queryCode != _lastLoggedQueryCode)
                    {
                        _lastLoggedQueryCode = queryCode;
                        sensorRecorder?.LogAction("mesh_query_status", "query_mesh_anchor", $"{result.result}");
                    }
                    if (result.result == PxrResult.SUCCESS && result.meshInfos != null && result.meshInfos.Count > 0)
                    {
                        wrote = SnapFromPicoMeshInfos(result.meshInfos);
                        _emptyQueryCount = 0;
                    }
                    else
                    {
                        _emptyQueryCount++;
                    }
                }
                else
                {
                    _emptyQueryCount++;
                }
            }
        }
#endif

        if (!wrote) wrote = SnapFromSceneMeshFilters();
        if (!wrote && !_loggedNoMesh)
        {
            _loggedNoMesh = true;
            Debug.LogWarning("[Mesh] No spatial mesh data available yet.");
        }

        _queryInFlight = false;
        yield break;
    }

    void EnsureXRMeshSubsystem()
    {
        if (_xrMeshSubsystem != null) return;
        if (_xrMeshInitTried) return;
        _xrMeshInitTried = true;

        var subsystems = new List<XRMeshSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        for (int i = 0; i < subsystems.Count; i++)
        {
            if (subsystems[i] == null) continue;
            _xrMeshSubsystem = subsystems[i];
            if (!_xrMeshSubsystem.running) _xrMeshSubsystem.Start();
            break;
        }

        if (_xrMeshSubsystem != null)
            sensorRecorder?.LogAction("mesh_provider_start", "xr_mesh_subsystem", "ok");
        else
            sensorRecorder?.LogAction("mesh_provider_start", "xr_mesh_subsystem", "missing");
    }

    bool SnapFromXRMeshSubsystem()
    {
        EnsureXRMeshSubsystem();
        if (_xrMeshSubsystem == null || !_xrMeshSubsystem.running) return false;
        if (!_xrMeshSubsystem.TryGetMeshInfos(_xrMeshInfos) || _xrMeshInfos.Count == 0) return false;

        for (int i = 0; i < _xrMeshInfos.Count; i++)
        {
            var info = _xrMeshInfos[i];
            bool needsUpdate = info.ChangeState == UnityEngine.XR.MeshChangeState.Added ||
                               info.ChangeState == UnityEngine.XR.MeshChangeState.Updated ||
                               !_xrMeshCache.ContainsKey(info.MeshId);
            if (!needsUpdate) continue;

            var generated = new Mesh();
            _xrMeshSubsystem.GenerateMeshAsync(
                info.MeshId,
                generated,
                null,
                MeshVertexAttributes.Normals,
                result =>
                {
                    if (result.Status != MeshGenerationStatus.Success || result.Mesh == null) return;
                    if (_xrMeshCache.TryGetValue(result.MeshId, out var oldMesh) && oldMesh != null) Destroy(oldMesh);
                    _xrMeshCache[result.MeshId] = result.Mesh;
                });
        }

        var verts = new List<Vector3>();
        var tris = new List<int>();
        foreach (var kv in _xrMeshCache)
        {
            var mesh = kv.Value;
            if (mesh == null || mesh.vertexCount == 0) continue;

            int off = verts.Count;
            var mVerts = mesh.vertices;
            for (int i = 0; i < mVerts.Length; i++) verts.Add(mVerts[i]);
            var mTris = mesh.triangles;
            for (int i = 0; i < mTris.Length; i++) tris.Add(mTris[i] + off);
        }

        if (verts.Count == 0 || tris.Count < 3) return false;
        WriteSnapshot(verts, tris);
        return true;
    }

#if PICO_XR
    bool SnapFromPicoMeshInfos(List<PxrSpatialMeshInfo> meshInfos)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        for (int m = 0; m < meshInfos.Count; m++)
        {
            var info = meshInfos[m];
            if (info.vertices == null || info.indices == null) continue;
            if (info.vertices.Length == 0 || info.indices.Length < 3) continue;

            int off = verts.Count;
            for (int i = 0; i < info.vertices.Length; i++)
            {
                var world = info.position + info.rotation * info.vertices[i];
                verts.Add(world);
            }

            for (int i = 0; i + 2 < info.indices.Length; i += 3)
            {
                tris.Add(info.indices[i] + off);
                tris.Add(info.indices[i + 1] + off);
                tris.Add(info.indices[i + 2] + off);
            }
        }

        if (verts.Count == 0 || tris.Count < 3) return false;
        WriteSnapshot(verts, tris);
        return true;
    }
#endif

    bool SnapFromSceneMeshFilters()
    {
        var verts = new List<Vector3>(); var tris = new List<int>();
        foreach (var mf in FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
        {
            if (mf.sharedMesh == null) continue;
            if (mf.GetComponentInParent<Canvas>() != null) continue;
            string nm = mf.gameObject.name.ToLowerInvariant();
            bool isSpatial = nm.Contains("spatial") || nm.Contains("scene") || nm.Contains("mesh") || nm.Contains("mr");
#if PICO_XR
            isSpatial |= mf.GetComponentInParent<PXR_SpatialMeshManager>() != null;
#endif
            if (mf.transform.parent != null)
            {
                string pn = mf.transform.parent.name.ToLowerInvariant();
                isSpatial |= pn.Contains("spatial") || pn.Contains("scene") || pn.Contains("mesh") || pn.Contains("mr");
            }
            if (!isSpatial)
            {
                var comps = mf.GetComponents<Component>();
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    string tn = c.GetType().Name.ToLowerInvariant();
                    if (tn.Contains("spatial") || tn.Contains("scene") || tn.Contains("mesh") || tn.Contains("mr"))
                    {
                        isSpatial = true;
                        break;
                    }
                }
            }
            if (!isSpatial) continue;
            int off = verts.Count; var m = mf.sharedMesh; var t = mf.transform;
            foreach (var v in m.vertices) verts.Add(t.TransformPoint(v));
            foreach (var i in m.triangles) tris.Add(i + off);
        }
        if (verts.Count == 0 || tris.Count < 3) return false;
        WriteSnapshot(verts, tris);
        return true;
    }

    void WriteSnapshot(List<Vector3> verts, List<int> tris)
    {
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
        _idx.Flush();
        _n++;
    }

    void OnDestroy() { if (_on) StopCapture(); if (_activeWriter == this) _activeWriter = null; }
}
