using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class XRDescriptorDump : MonoBehaviour
{
    private void Start()
    {
        DumpDescriptors();
    }

    private static void DumpDescriptors()
    {
        var displays = new List<XRDisplaySubsystemDescriptor>();
        var inputs = new List<XRInputSubsystemDescriptor>();
        var meshes = new List<XRMeshSubsystemDescriptor>();

        SubsystemManager.GetSubsystemDescriptors(displays);
        SubsystemManager.GetSubsystemDescriptors(inputs);
        SubsystemManager.GetSubsystemDescriptors(meshes);

        Debug.Log($"[XRDump] Display descriptors: {displays.Count}");
        for (int i = 0; i < displays.Count; i++)
            Debug.Log($"[XRDump]   Display[{i}] id={displays[i].id}");

        Debug.Log($"[XRDump] Input descriptors: {inputs.Count}");
        for (int i = 0; i < inputs.Count; i++)
            Debug.Log($"[XRDump]   Input[{i}] id={inputs[i].id}");

        Debug.Log($"[XRDump] Mesh descriptors: {meshes.Count}");
        for (int i = 0; i < meshes.Count; i++)
            Debug.Log($"[XRDump]   Mesh[{i}] id={meshes[i].id}");
    }
}
