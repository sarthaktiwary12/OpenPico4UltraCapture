using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class HandVisualizer : MonoBehaviour
{
    public Vector3 RightIndexTipPosition { get; private set; }
    public Vector3 LeftIndexTipPosition { get; private set; }
    public bool RightHandTracked { get; private set; }
    public bool LeftHandTracked { get; private set; }

    const float JointRadius = 0.012f;
    const float BoneWidth = 0.007f;

    static readonly Color LeftColor = new Color(0.15f, 0.75f, 1.0f, 0.95f);
    static readonly Color RightColor = new Color(0.2f, 1.0f, 0.45f, 0.95f);

    static readonly int JointCount = (int)XRHandJointID.LittleTip - (int)XRHandJointID.Wrist + 1;

    // PICO native joint order (0..25) mapped to XR hand joint IDs.
    static readonly XRHandJointID[] PxrJointToXrJoint =
    {
        XRHandJointID.Palm, XRHandJointID.Wrist,
        XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip,
        XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip,
        XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
        XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip,
        XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip
    };

    struct BonePair
    {
        public XRHandJointID from, to;
        public BonePair(XRHandJointID f, XRHandJointID t) { from = f; to = t; }
    }

    static readonly BonePair[] BoneConnections =
    {
        // Wrist to metacarpals
        new BonePair(XRHandJointID.Wrist, XRHandJointID.ThumbMetacarpal),
        new BonePair(XRHandJointID.Wrist, XRHandJointID.IndexMetacarpal),
        new BonePair(XRHandJointID.Wrist, XRHandJointID.MiddleMetacarpal),
        new BonePair(XRHandJointID.Wrist, XRHandJointID.RingMetacarpal),
        new BonePair(XRHandJointID.Wrist, XRHandJointID.LittleMetacarpal),
        // Palm to Wrist
        new BonePair(XRHandJointID.Palm, XRHandJointID.Wrist),
        // Thumb
        new BonePair(XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal),
        new BonePair(XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal),
        new BonePair(XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip),
        // Index
        new BonePair(XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal),
        new BonePair(XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate),
        new BonePair(XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal),
        new BonePair(XRHandJointID.IndexDistal, XRHandJointID.IndexTip),
        // Middle
        new BonePair(XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal),
        new BonePair(XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate),
        new BonePair(XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal),
        new BonePair(XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip),
        // Ring
        new BonePair(XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal),
        new BonePair(XRHandJointID.RingProximal, XRHandJointID.RingIntermediate),
        new BonePair(XRHandJointID.RingIntermediate, XRHandJointID.RingDistal),
        new BonePair(XRHandJointID.RingDistal, XRHandJointID.RingTip),
        // Little
        new BonePair(XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal),
        new BonePair(XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate),
        new BonePair(XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal),
        new BonePair(XRHandJointID.LittleDistal, XRHandJointID.LittleTip),
    };

    private XRHandSubsystem _xrHandSub;
    private bool _xrHandSubSearched;
    private int _frameCount;

    private GameObject _leftRoot, _rightRoot;
    private Transform[] _leftJoints, _rightJoints;
    private LineRenderer[] _leftBones, _rightBones;
    private Material _leftMat, _rightMat;

    void Start()
    {
        _leftMat = CreateMaterial(LeftColor);
        _rightMat = CreateMaterial(RightColor);
        _leftRoot = CreateHandRoot("LeftHand", _leftMat, out _leftJoints, out _leftBones);
        _rightRoot = CreateHandRoot("RightHand", _rightMat, out _rightJoints, out _rightBones);
        _leftRoot.SetActive(false);
        _rightRoot.SetActive(false);
    }

    Material CreateMaterial(Color color)
    {
        Shader shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("UI/Default");
        var mat = new Material(shader);
        mat.color = color;
        return mat;
    }

    GameObject CreateHandRoot(string name, Material mat, out Transform[] joints, out LineRenderer[] bones)
    {
        var root = new GameObject(name);
        root.transform.SetParent(transform);

        joints = new Transform[JointCount];
        for (int i = 0; i < JointCount; i++)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = $"Joint_{(XRHandJointID)(i + (int)XRHandJointID.Wrist)}";
            sphere.transform.SetParent(root.transform);
            sphere.transform.localScale = Vector3.one * JointRadius * 2f;
            sphere.GetComponent<Renderer>().material = mat;
            var col = sphere.GetComponent<Collider>();
            if (col != null) Destroy(col);
            joints[i] = sphere.transform;
        }

        bones = new LineRenderer[BoneConnections.Length];
        for (int i = 0; i < BoneConnections.Length; i++)
        {
            var boneGO = new GameObject($"Bone_{i}");
            boneGO.transform.SetParent(root.transform);
            var lr = boneGO.AddComponent<LineRenderer>();
            lr.material = mat;
            lr.startWidth = BoneWidth;
            lr.endWidth = BoneWidth;
            lr.positionCount = 2;
            lr.useWorldSpace = true;
            lr.numCapVertices = 4;
            lr.numCornerVertices = 4;
            bones[i] = lr;
        }

        return root;
    }

    void Update()
    {
        _frameCount++;

        if (!_xrHandSubSearched || (_xrHandSub == null && _frameCount % 90 == 0))
        {
            _xrHandSubSearched = true;
            var subs = new List<XRHandSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            _xrHandSub = subs.Count > 0 ? subs[0] : null;
        }

        bool leftTracked = false;
        bool rightTracked = false;

        if (_xrHandSub != null && _xrHandSub.running)
        {
            leftTracked = UpdateXRHand(_xrHandSub.leftHand, _leftRoot, _leftJoints, _leftBones, true);
            rightTracked = UpdateXRHand(_xrHandSub.rightHand, _rightRoot, _rightJoints, _rightBones, false);
        }

        // Fallback: if XR Hands is not active/tracked, visualize from PICO native joints.
        if (!leftTracked)
            leftTracked = UpdatePicoNativeHand(true, _leftRoot, _leftJoints, _leftBones);
        if (!rightTracked)
            rightTracked = UpdatePicoNativeHand(false, _rightRoot, _rightJoints, _rightBones);

        LeftHandTracked = leftTracked;
        RightHandTracked = rightTracked;
    }

    static int JointToIndex(XRHandJointID id) => (int)id - (int)XRHandJointID.Wrist;

    bool UpdateXRHand(XRHand hand, GameObject root, Transform[] joints, LineRenderer[] bones, bool isLeft)
    {
        if (!hand.isTracked)
        {
            root.SetActive(false);
            return false;
        }

        int validJointCount = 0;

        for (int i = 0; i < joints.Length; i++)
        {
            var jointID = (XRHandJointID)(i + (int)XRHandJointID.Wrist);
            var joint = hand.GetJoint(jointID);
            if (joint.TryGetPose(out Pose pose))
            {
                joints[i].position = pose.position;
                joints[i].rotation = pose.rotation;
                validJointCount++;
                if (jointID == XRHandJointID.IndexTip)
                {
                    if (isLeft)
                        LeftIndexTipPosition = pose.position;
                    else
                        RightIndexTipPosition = pose.position;
                }
            }
        }

        bool tracked = validJointCount >= 6;
        root.SetActive(tracked);
        if (!tracked) return false;

        UpdateBones(joints, bones);
        return true;
    }

    void UpdateBones(Transform[] joints, LineRenderer[] bones)
    {
        for (int i = 0; i < BoneConnections.Length; i++)
        {
            int fromIdx = JointToIndex(BoneConnections[i].from);
            int toIdx = JointToIndex(BoneConnections[i].to);
            if (fromIdx >= 0 && fromIdx < joints.Length && toIdx >= 0 && toIdx < joints.Length)
            {
                bones[i].SetPosition(0, joints[fromIdx].position);
                bones[i].SetPosition(1, joints[toIdx].position);
            }
        }
    }

    bool UpdatePicoNativeHand(bool isLeft, GameObject root, Transform[] joints, LineRenderer[] bones)
    {
#if PICO_XR
        try
        {
            var ht = isLeft ? HandType.HandLeft : HandType.HandRight;
            var jl = new HandJointLocations();
            if (!PXR_Plugin.HandTracking.UPxr_GetHandTrackerJointLocations(ht, ref jl) || jl.jointLocations == null)
            {
                root.SetActive(false);
                return false;
            }

            int validJointCount = 0;
            int n = Mathf.Min(jl.jointLocations.Length, PxrJointToXrJoint.Length);
            for (int i = 0; i < n; i++)
            {
                var j = jl.jointLocations[i];
                ulong status = (ulong)j.locationStatus;
                bool posValid = (status & (ulong)HandLocationStatus.PositionValid) != 0;
                bool rotValid = (status & (ulong)HandLocationStatus.OrientationValid) != 0;
                if (!posValid && !rotValid) continue;

                int idx = JointToIndex(PxrJointToXrJoint[i]);
                if (idx < 0 || idx >= joints.Length) continue;

                var pos = j.pose.Position.ToVector3();
                var rot = j.pose.Orientation.ToQuat();
                joints[idx].position = pos;
                joints[idx].rotation = rot;
                validJointCount++;

                if (PxrJointToXrJoint[i] == XRHandJointID.IndexTip)
                {
                    if (isLeft)
                        LeftIndexTipPosition = pos;
                    else
                        RightIndexTipPosition = pos;
                }
            }

            bool tracked = validJointCount >= 6;
            root.SetActive(tracked);
            if (!tracked) return false;

            UpdateBones(joints, bones);
            return true;
        }
        catch
        {
            root.SetActive(false);
            return false;
        }
#else
        root.SetActive(false);
        return false;
#endif
    }

    void OnDestroy()
    {
        if (_leftMat != null) Destroy(_leftMat);
        if (_rightMat != null) Destroy(_rightMat);
    }
}
