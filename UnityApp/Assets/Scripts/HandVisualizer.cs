using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

public class HandVisualizer : MonoBehaviour
{
    public Vector3 RightIndexTipPosition { get; private set; }
    public Vector3 LeftIndexTipPosition { get; private set; }
    public bool RightHandTracked { get; private set; }
    public bool LeftHandTracked { get; private set; }

    const float JointRadius = 0.006f;
    const float BoneWidth = 0.003f;

    static readonly Color LeftColor = new Color(0.2f, 0.7f, 1.0f, 0.5f);
    static readonly Color RightColor = new Color(0.2f, 1.0f, 0.5f, 0.5f);

    static readonly int JointCount = (int)XRHandJointID.LittleTip - (int)XRHandJointID.Wrist + 1;

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
        Shader shader = Shader.Find("Sprites/Default");
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

        if (_xrHandSub == null || !_xrHandSub.running)
        {
            _leftRoot.SetActive(false);
            _rightRoot.SetActive(false);
            LeftHandTracked = false;
            RightHandTracked = false;
            return;
        }

        UpdateHand(_xrHandSub.leftHand, _leftRoot, _leftJoints, _leftBones, true);
        UpdateHand(_xrHandSub.rightHand, _rightRoot, _rightJoints, _rightBones, false);
    }

    static int JointToIndex(XRHandJointID id) => (int)id - (int)XRHandJointID.Wrist;

    void UpdateHand(XRHand hand, GameObject root, Transform[] joints, LineRenderer[] bones, bool isLeft)
    {
        bool tracked = hand.isTracked;
        root.SetActive(tracked);

        if (isLeft)
            LeftHandTracked = tracked;
        else
            RightHandTracked = tracked;

        if (!tracked) return;

        for (int i = 0; i < joints.Length; i++)
        {
            var jointID = (XRHandJointID)(i + (int)XRHandJointID.Wrist);
            var joint = hand.GetJoint(jointID);
            if (joint.TryGetPose(out Pose pose))
            {
                joints[i].position = pose.position;
                if (jointID == XRHandJointID.IndexTip)
                {
                    if (isLeft)
                        LeftIndexTipPosition = pose.position;
                    else
                        RightIndexTipPosition = pose.position;
                }
            }
        }

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

    void OnDestroy()
    {
        if (_leftMat != null) Destroy(_leftMat);
        if (_rightMat != null) Destroy(_rightMat);
    }
}
