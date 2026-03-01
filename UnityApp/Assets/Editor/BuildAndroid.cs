using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class BuildAndroid
{
    private const string ScenePath = "Assets/Scenes/OpenPicoMain.unity";
    private const string PicoAppIdEnvVar = "PICO_APP_ID";

    public static void BuildCI()
    {
        EnsurePXRProjectSettings();
        EnsureScene();

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenePath, true)
        };

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        EnableDefineSymbol("PICO_XR");
        EnsurePicoLoaderConfigured();
        EnsureOpenXRSettingsLoaded();
        EnsurePicoPlatformAppIdConfigured();

        PlayerSettings.companyName = "SentientX";
        PlayerSettings.productName = "OpenPico4UltraCapture";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.sentientx.datacapture");
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
#if UNITY_2023_1_OR_NEWER
        PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
#endif
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string buildDir = Path.Combine(projectRoot, "Builds");
        Directory.CreateDirectory(buildDir);
        string apkPath = Path.Combine(buildDir, "openpico4ultra.apk");

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            target = BuildTarget.Android,
            locationPathName = apkPath,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            // OpenXR can require one pass to register settings assets in batchmode.
            report = BuildPipeline.BuildPlayer(options);
        }
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception($"Android build failed: {report.summary.result}");
        }

        Debug.Log($"Android build succeeded: {apkPath}");
    }

    public static void ConfigurePicoPlatformAppId()
    {
        EnsurePicoPlatformAppIdConfigured();
        AssetDatabase.SaveAssets();
        Debug.Log("PICO Platform appID configuration step finished.");
    }

    private static void EnsurePXRProjectSettings()
    {
        var type = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == "Unity.XR.PXR.PXR_ProjectSetting");
        if (type == null) return;

        var getInstance = type.GetMethod("GetProjectConfig",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var instance = getInstance?.Invoke(null, null);
        if (instance == null) return;

        void SetBool(string name, bool val) {
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) { if (f.FieldType == typeof(bool)) f.SetValue(instance, val); else if (f.FieldType == typeof(int)) f.SetValue(instance, val ? 1 : 0); }
        }
        void SetInt(string name, int val) {
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) f.SetValue(instance, val);
        }

        SetBool("handTracking", true);
        SetBool("bodyTracking", true);
        SetBool("spatialMesh", true);
        SetBool("videoSeeThrough", false);
        SetBool("openMRC", false);

        if (instance is UnityEngine.Object obj) EditorUtility.SetDirty(obj);
        AssetDatabase.SaveAssets();
        Debug.Log("[Build] PXR_ProjectSetting configured: hand+body+mesh.");
    }

    private static void EnsureScene()
    {
        Directory.CreateDirectory("Assets/Scenes");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ── XR Origin hierarchy ──
        var xrOriginGo = new GameObject("XR Origin");
        AddXROriginComponent(xrOriginGo);

        var cameraOffsetGo = new GameObject("Camera Offset");
        cameraOffsetGo.transform.SetParent(xrOriginGo.transform, false);

        var cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        cameraGo.transform.SetParent(cameraOffsetGo.transform, false);
        var cam = cameraGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.1f, 0.1f, 0.3f, 1f);
        cameraGo.AddComponent<AudioListener>();
        cameraGo.AddComponent<PassthroughEnabler>();
        AddTrackedPoseDriver(cameraGo);

        // Point XROrigin.CameraFloorOffsetObject and Camera at the right objects
        ConfigureXROrigin(xrOriginGo, cameraOffsetGo, cameraGo);

        // ── Directional Light ──
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
        lightGo.transform.position = new Vector3(0, 3, 0);

        // ── PICO SDK objects ──
        var pxrManagerComp = AddPXRManager();
        AddComponentByReflection("PXR_SpatialMeshManager", "PXR_SpatialMeshManager");

        // ── System objects ──
        var imuGo = new GameObject("NativeIMUBridge");
        imuGo.AddComponent<NativeIMUBridge>();

        var sensorGo = new GameObject("SensorRecorder");
        var sensor = sensorGo.AddComponent<SensorRecorder>();

        var syncGo = new GameObject("SyncManager");
        var sync = syncGo.AddComponent<SyncManager>();
        sync.sensorRecorder = sensor;
        sync.audioSource = syncGo.AddComponent<AudioSource>();

        var meshGo = new GameObject("SpatialMeshCapture");
        var mesh = meshGo.AddComponent<SpatialMeshCapture>();
        mesh.sensorRecorder = sensor;

        var bodyGo = new GameObject("BodyTrackingRecorder");
        var body = bodyGo.AddComponent<BodyTrackingRecorder>();
        body.sensorRecorder = sensor;

        // ── Screen Space Overlay Canvas (2D panel mode) ──
        var canvasGo = new GameObject("RecordingCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(506, 900);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        // Full-screen toggle button — pull trigger anywhere on the panel to toggle
        var btnGo = new GameObject("BtnFullScreen");
        btnGo.transform.SetParent(canvasGo.transform, false);
        var btnImage = btnGo.AddComponent<Image>();
        btnImage.color = new Color(0.1f, 0.3f, 0.1f, 1f);
        var btnToggle = btnGo.AddComponent<Button>();
        btnToggle.targetGraphic = btnImage;
        var btnRt = btnGo.GetComponent<RectTransform>();
        btnRt.anchorMin = Vector2.zero;
        btnRt.anchorMax = Vector2.one;
        btnRt.offsetMin = Vector2.zero;
        btnRt.offsetMax = Vector2.zero;

        // Big label text (RECORD / STOP)
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(btnGo.transform, false);
        var btnLabel = labelGo.AddComponent<Text>();
        btnLabel.text = "RECORD";
        btnLabel.fontSize = 64;
        btnLabel.fontStyle = FontStyle.Bold;
        btnLabel.alignment = TextAnchor.MiddleCenter;
        btnLabel.color = Color.white;
        btnLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var labelRt = labelGo.GetComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0, 0.3f);
        labelRt.anchorMax = new Vector2(1, 0.7f);
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;

        // Status text below the label
        var txtStatusGo = CreateUIText(btnGo.transform, "TxtStatus", "Pull trigger to\nstart recording",
            Vector2.zero, Vector2.zero, 32);
        var txtStatus = txtStatusGo.GetComponent<Text>();
        var statusRt = txtStatusGo.GetComponent<RectTransform>();
        statusRt.anchorMin = new Vector2(0, 0.05f);
        statusRt.anchorMax = new Vector2(1, 0.35f);
        statusRt.offsetMin = new Vector2(20, 0);
        statusRt.offsetMax = new Vector2(-20, 0);
        statusRt.anchoredPosition = Vector2.zero;

        // ── SimpleRecordingController ──
        var controllerGo = new GameObject("SimpleRecordingController");
        var controller = controllerGo.AddComponent<SimpleRecordingController>();
        controller.sensorRecorder = sensor;
        controller.syncManager = sync;
        controller.spatialMeshCapture = mesh;
        controller.bodyTrackingRecorder = body;
        controller.btnToggle = btnToggle;
        controller.txtStatus = txtStatus;
        controller.txtButtonLabel = btnLabel;
        controller.btnImage = btnImage;

        EditorSceneManager.SaveScene(scene, ScenePath);
    }

    private static void EnableDefineSymbol(string symbol)
    {
        var target = BuildTargetGroup.Android;
        var existing = PlayerSettings.GetScriptingDefineSymbolsForGroup(target);
        var symbols = existing.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!symbols.Contains(symbol))
        {
            symbols.Add(symbol);
            PlayerSettings.SetScriptingDefineSymbolsForGroup(target, string.Join(";", symbols));
        }
    }

    private static void EnsureOpenXRSettingsLoaded()
    {
        var openXrSettingsType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == "UnityEditor.XR.OpenXR.OpenXRPackageSettings");
        if (openXrSettingsType == null) return;

        var getOrCreate = openXrSettingsType.GetMethod(
            "GetOrCreateInstance",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var instance = getOrCreate?.Invoke(null, null);
        if (instance == null) return;

        var getSettingsForBuildTargetGroup = openXrSettingsType.GetMethod(
            "GetSettingsForBuildTargetGroup",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        getSettingsForBuildTargetGroup?.Invoke(instance, new object[] { BuildTargetGroup.Android });
        AssetDatabase.SaveAssets();
    }

    private static void EnsurePicoLoaderConfigured()
    {
        var perBuildTargetType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == "UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget");
        if (perBuildTargetType == null) return;

        var getOrCreate = perBuildTargetType.GetMethod(
            "GetOrCreate",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var perBuildTarget = getOrCreate?.Invoke(null, null);
        if (perBuildTarget == null) return;

        var hasSettingsForBuildTarget = perBuildTargetType.GetMethod(
            "HasSettingsForBuildTarget",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var createDefaultSettingsForBuildTarget = perBuildTargetType.GetMethod(
            "CreateDefaultSettingsForBuildTarget",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var hasManagerSettingsForBuildTarget = perBuildTargetType.GetMethod(
            "HasManagerSettingsForBuildTarget",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var createDefaultManagerSettingsForBuildTarget = perBuildTargetType.GetMethod(
            "CreateDefaultManagerSettingsForBuildTarget",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var managerSettingsForBuildTarget = perBuildTargetType.GetMethod(
            "ManagerSettingsForBuildTarget",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var settingsForBuildTarget = perBuildTargetType.GetMethod(
            "SettingsForBuildTarget",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        var androidTarget = new object[] { BuildTargetGroup.Android };
        if (!(bool)(hasSettingsForBuildTarget?.Invoke(perBuildTarget, androidTarget) ?? false))
        {
            createDefaultSettingsForBuildTarget?.Invoke(perBuildTarget, androidTarget);
        }
        if (!(bool)(hasManagerSettingsForBuildTarget?.Invoke(perBuildTarget, androidTarget) ?? false))
        {
            createDefaultManagerSettingsForBuildTarget?.Invoke(perBuildTarget, androidTarget);
        }

        var managerSettings = managerSettingsForBuildTarget?.Invoke(perBuildTarget, androidTarget);
        if (managerSettings == null) return;

        var metadataStoreType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == "UnityEditor.XR.Management.Metadata.XRPackageMetadataStore");
        if (metadataStoreType == null) return;

        var assignLoader = metadataStoreType.GetMethod(
            "AssignLoader",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        assignLoader?.Invoke(null, new[] { managerSettings, "Unity.XR.PXR.PXR_Loader", (object)BuildTargetGroup.Android });

        var generalSettings = settingsForBuildTarget?.Invoke(perBuildTarget, androidTarget);
        var initManagerOnStartProp = generalSettings?.GetType().GetProperty(
            "InitManagerOnStart",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        initManagerOnStartProp?.SetValue(generalSettings, false);

        // Keep AutomaticLoading/Running as-is from asset file (0 = 2D panel mode)

        if (perBuildTarget is UnityEngine.Object perBuildObject)
        {
            EditorUtility.SetDirty(perBuildObject);
        }
        if (managerSettings is UnityEngine.Object managerObject)
        {
            EditorUtility.SetDirty(managerObject);
        }
        if (generalSettings is UnityEngine.Object generalObject)
        {
            EditorUtility.SetDirty(generalObject);
        }
        AssetDatabase.SaveAssets();
    }

    private static void EnsurePicoPlatformAppIdConfigured()
    {
        var envAppId = Environment.GetEnvironmentVariable(PicoAppIdEnvVar)?.Trim();
        if (string.IsNullOrEmpty(envAppId))
        {
            return;
        }

        var platformSettingType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == "Unity.XR.PXR.PXR_PlatformSetting");
        if (platformSettingType == null) return;

        var instanceProp = platformSettingType.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var instance = instanceProp?.GetValue(null);
        if (instance == null) return;

        var appIdField = platformSettingType.GetField(
            "appID",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (appIdField == null) return;

        var current = (appIdField.GetValue(instance) as string)?.Trim();
        if (current == envAppId) return;

        appIdField.SetValue(instance, envAppId);
        if (instance is UnityEngine.Object unityObject)
        {
            EditorUtility.SetDirty(unityObject);
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"Configured PICO Platform appID from {PicoAppIdEnvVar}.");
    }

    private static void AddTrackedPoseDriver(GameObject go)
    {
        var type = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == "UnityEngine.InputSystem.XR.TrackedPoseDriver" && typeof(Component).IsAssignableFrom(t));

        if (type != null)
        {
            go.AddComponent(type);
        }
        else
        {
            // Fallback to legacy TrackedPoseDriver
            var legacyType = AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(t => t.FullName == "UnityEngine.SpatialTracking.TrackedPoseDriver" && typeof(Component).IsAssignableFrom(t));
            if (legacyType != null)
                go.AddComponent(legacyType);
            else
                Debug.LogWarning("[Build] No TrackedPoseDriver type found.");
        }
    }

    private static void AddXROriginComponent(GameObject go)
    {
        var xrOriginType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == "Unity.XR.CoreUtils.XROrigin" && typeof(Component).IsAssignableFrom(t));

        if (xrOriginType != null)
        {
            go.AddComponent(xrOriginType);
        }
        else
        {
            Debug.LogWarning("[Build] Unity.XR.CoreUtils.XROrigin not found — XR Origin component skipped.");
        }
    }

    private static void ConfigureXROrigin(GameObject xrOriginGo, GameObject cameraOffsetGo, GameObject cameraGo)
    {
        var xrOrigin = xrOriginGo.GetComponents<Component>()
            .FirstOrDefault(c => c.GetType().FullName == "Unity.XR.CoreUtils.XROrigin");
        if (xrOrigin == null) return;

        var originType = xrOrigin.GetType();

        var offsetProp = originType.GetProperty("CameraFloorOffsetObject",
            BindingFlags.Public | BindingFlags.Instance);
        offsetProp?.SetValue(xrOrigin, cameraOffsetGo);

        var cameraProp = originType.GetProperty("Camera",
            BindingFlags.Public | BindingFlags.Instance);
        cameraProp?.SetValue(xrOrigin, cameraGo.GetComponent<Camera>());
    }

    private static Component AddPXRManager()
    {
        var type = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.Name == "PXR_Manager" && typeof(Component).IsAssignableFrom(t));

        if (type == null) return null;

        var go = new GameObject("PXR_Manager");
        var component = go.AddComponent(type);

        // Disable MRC by default
        var openMrcField = type.GetField("openMRC",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        openMrcField?.SetValue(component, false);

        // Enable body tracking
        var bodyTrackingField = type.GetField("bodyTracking",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (bodyTrackingField != null)
        {
            if (bodyTrackingField.FieldType == typeof(bool))
                bodyTrackingField.SetValue(component, true);
            else if (bodyTrackingField.FieldType == typeof(int))
                bodyTrackingField.SetValue(component, 1);
        }

        return component;
    }

    private static void AddComponentByReflection(string gameObjectName, string typeName)
    {
        var type = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.Name == typeName && typeof(Component).IsAssignableFrom(t));

        if (type == null) return;

        var go = new GameObject(gameObjectName);
        go.AddComponent(type);
    }

    private static GameObject CreateButton(Transform parent, string name, string label, Color color, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = color;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        var txtGo = new GameObject("Text");
        txtGo.transform.SetParent(go.transform, false);
        var txt = txtGo.AddComponent<Text>();
        txt.text = label;
        txt.fontSize = 40;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.fontStyle = FontStyle.Bold;
        txt.color = Color.white;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var txtRt = txtGo.GetComponent<RectTransform>();
        txtRt.anchorMin = Vector2.zero;
        txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = Vector2.zero;
        txtRt.offsetMax = Vector2.zero;

        return go;
    }

    private static GameObject CreateUIText(Transform parent, string name, string text, Vector2 pos, Vector2 size, int fontSize = 24)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var txt = go.AddComponent<Text>();
        txt.text = text;
        txt.fontSize = fontSize;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.color = Color.white;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return go;
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).ToArray();
        }
    }
}
