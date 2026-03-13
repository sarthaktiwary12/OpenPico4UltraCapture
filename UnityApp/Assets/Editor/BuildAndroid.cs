using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

public static class BuildAndroid
{
    private const string ScenePath = "Assets/Scenes/OpenPicoMain.unity";
    private const string PicoAppIdEnvVar = "PICO_APP_ID";

    public static void BuildCI()
    {
        EnsurePXRProjectSettings();
        // NOTE: EnsurePXROpenXRProjectSettings causes startup hang on Pico 4 Ultra
        // (handtracking=1 manifest metadata blocks app launch).
        // Hand tracking is enabled at runtime instead via PXR_Plugin.HandTracking.
        // HandTrackingManifestPostProcessor removes the problematic metadata at build time.
        // EnsurePXROpenXRProjectSettings();
        EnsureScene();

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenePath, true)
        };

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
        EnableDefineSymbol("PICO_XR");
        EnableDefineSymbol("PICO_OPENXR_SDK");
        EnsurePicoLoaderConfigured();
        EnsureOpenXRSettingsLoaded();
        EnsurePicoOpenXRFeatureSetEnabled();
        EnsureSinglePicoTouchInteractionProfile();
        EnsurePicoPlatformAppIdConfigured();

        PlayerSettings.companyName = "SentientX";
        PlayerSettings.productName = "OpenPico4UltraCapture";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.sentientx.datacapture");
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.colorSpace = ColorSpace.Linear;
#if UNITY_2023_1_OR_NEWER
        PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
#endif
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.Vulkan });
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
        SetBool("spatialAnchor", true);
        SetBool("sceneCapture", true);
        SetBool("videoSeeThrough", true);
        SetBool("openMRC", false);

        if (instance is UnityEngine.Object obj) EditorUtility.SetDirty(obj);
        AssetDatabase.SaveAssets();
        Debug.Log("[Build] PXR_ProjectSetting configured: hand+body+mesh.");
    }

    private static void EnsurePXROpenXRProjectSettings()
    {
        // The PICO OpenXR build processor (PXR_BuildProcessor.PXR_Manifest) checks
        // PXR_OpenXRProjectSetting.isHandTracking to decide manifest meta-data.
        // When isHandTracking=false, it injects controller=1 and removes handtracking,
        // causing PICO to show "controller required" and block hand tracking input.
        var type = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == "Unity.XR.OpenXR.Features.PICOSupport.PXR_OpenXRProjectSetting");
        if (type == null) { Debug.LogWarning("[Build] PXR_OpenXRProjectSetting type not found."); return; }

        var getInstance = type.GetMethod("GetProjectConfig",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var instance = getInstance?.Invoke(null, null);
        if (instance == null) { Debug.LogWarning("[Build] PXR_OpenXRProjectSetting instance not found."); return; }

        void SetField(string name, object val) {
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) f.SetValue(instance, val);
        }

        SetField("isHandTracking", true);
        SetField("highFrequencyHand", true);
        // HandTrackingSupport enum: ControllersAndHands=0, HandsOnly=1, ControllersOnly=2
        // ControllersAndHands enables simultaneous hand+controller tracking.
        var htEnum = type.GetField("handTrackingSupportType", BindingFlags.Public | BindingFlags.Instance);
        if (htEnum != null)
        {
            var enumType = htEnum.FieldType;
            htEnum.SetValue(instance, System.Enum.ToObject(enumType, 0)); // 0 = ControllersAndHands
        }
        // NOTE: HandTrackingManifestPostProcessor removes handtracking=1/controller=1 metadata
        // that the PICO build processor injects, preventing the startup hang on this firmware.

        if (instance is UnityEngine.Object obj) EditorUtility.SetDirty(obj);
        AssetDatabase.SaveAssets();
        Debug.Log("[Build] PXR_OpenXRProjectSetting configured: isHandTracking=true.");
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
        cam.backgroundColor = new Color(0, 0, 0, 0); // Transparent for passthrough
        // Close-up hand joints can be within 10-20 cm when headset is neck-mounted.
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 100f;
        cameraGo.AddComponent<AudioListener>();
        var passthrough = cameraGo.AddComponent<PassthroughEnabler>();
        passthrough.enableOnStart = true;
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
        // NOTE: PXR_SpatialMeshManager removed — it throws NullRef in InitMeshColor()
        // when materials aren't assigned. SpatialMeshCapture.cs handles mesh data independently.

        // ── System objects ──
        var imuGo = new GameObject("NativeIMUBridge");
        imuGo.AddComponent<NativeIMUBridge>();

        var sensorGo = new GameObject("SensorRecorder");
        var sensor = sensorGo.AddComponent<SensorRecorder>();
        sensor.sampleRateHz = 30f;

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

        // ── World Space Canvas (VR passthrough mode) ──
        var canvasGo = new GameObject("RecordingCanvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = cam;
        canvasGo.AddComponent<GraphicRaycaster>();

        // Room-anchored panel so operator can walk to a fixed RECORD/STOP location.
        var follower = canvasGo.AddComponent<CanvasFollower>();
        follower.placementMode = CanvasFollower.PlacementMode.AnchorInRoom;
        follower.targetCamera = cam;
        follower.followDistance = 1.8f;
        follower.heightOffset = -0.1f;
        follower.rightOffset = 0.85f;
        follower.reTargetAngle = 45f;
        follower.followSpeed = 5f;

        // Place a compact HUD in the upper-right peripheral view.
        var canvasRt = canvasGo.GetComponent<RectTransform>();
        canvasRt.sizeDelta = new Vector2(420, 200);
        canvasGo.transform.position = new Vector3(0, 1.6f, 1.5f);
        canvasGo.transform.localScale = Vector3.one * 0.0015f; // ~0.63m wide in world

        // EventSystem — required for Unity UI to receive any input
        var eventSystemGo = new GameObject("EventSystem");
        eventSystemGo.AddComponent<EventSystem>();
        eventSystemGo.AddComponent<StandaloneInputModule>();

        // Runtime Android permission requests for camera/mic/sensors/media access.
        var runtimePermissionsGo = new GameObject("RuntimePermissions");
        runtimePermissionsGo.AddComponent<RuntimePermissions>();

        // Recovery path in case XR subsystem does not auto-start on device.
        var xrBootstrapFixGo = new GameObject("XRBootstrapFix");
        xrBootstrapFixGo.AddComponent<XRBootstrapFix>();
        var xrDescriptorDumpGo = new GameObject("XRDescriptorDump");
        xrDescriptorDumpGo.AddComponent<XRDescriptorDump>();

        // Compact recording button in the corner (non-blocking UI).
        var btnGo = new GameObject("BtnToggle");
        btnGo.transform.SetParent(canvasGo.transform, false);
        var btnImage = btnGo.AddComponent<Image>();
        btnImage.color = new Color(0.1f, 0.45f, 0.15f, 0.82f);
        var btnToggle = btnGo.AddComponent<Button>();
        btnToggle.targetGraphic = btnImage;
        var btnRt = btnGo.GetComponent<RectTransform>();
        btnRt.anchorMin = new Vector2(1f, 1f);
        btnRt.anchorMax = new Vector2(1f, 1f);
        btnRt.pivot = new Vector2(1f, 1f);
        btnRt.sizeDelta = new Vector2(220f, 86f);
        btnRt.anchoredPosition = new Vector2(-12f, -10f);

        // Big label text (RECORD / STOP)
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(btnGo.transform, false);
        var btnLabel = labelGo.AddComponent<Text>();
        btnLabel.text = "RECORD";
        btnLabel.fontSize = 38;
        btnLabel.fontStyle = FontStyle.Bold;
        btnLabel.alignment = TextAnchor.MiddleCenter;
        btnLabel.color = Color.white;
        btnLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var labelRt = labelGo.GetComponent<RectTransform>();
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;

        // Status text below the button.
        var txtStatusGo = CreateUIText(btnGo.transform, "TxtStatus",
            "Move hand to button\nHands: waiting...",
            Vector2.zero, Vector2.zero, 28);
        var txtStatus = txtStatusGo.GetComponent<Text>();
        txtStatus.alignment = TextAnchor.UpperRight;
        var statusRt = txtStatusGo.GetComponent<RectTransform>();
        statusRt.SetParent(canvasGo.transform, false);
        statusRt.anchorMin = new Vector2(1f, 1f);
        statusRt.anchorMax = new Vector2(1f, 1f);
        statusRt.pivot = new Vector2(1f, 1f);
        statusRt.sizeDelta = new Vector2(360f, 100f);
        statusRt.anchoredPosition = new Vector2(-12f, -102f);

        // ── SimpleRecordingController ──
        var controllerGo = new GameObject("SimpleRecordingController");
        var controller = controllerGo.AddComponent<SimpleRecordingController>();
        controller.sensorRecorder = sensor;
        controller.syncManager = sync;
        controller.spatialMeshCapture = mesh;
        controller.bodyTrackingRecorder = body;
        // CameraPermissionManager is auto-created at runtime by SimpleRecordingController
        controller.hudRoot = canvasGo;
        controller.showHudInHeadset = true;
        controller.btnToggle = btnToggle;
        controller.txtStatus = txtStatus;
        controller.txtButtonLabel = btnLabel;
        controller.btnImage = btnImage;
        controller.enableGestureToggle = true;
        controller.gestureWarmupSeconds = 5f;
        controller.gestureTriggerDistanceM = 0.10f;
        controller.gestureHoldDurationS = 1.5f;
        controller.gestureCooldownSeconds = 2.0f;
        controller.requireBothHandsTrackedForGesture = true;
        controller.blockStartWhenHandTrackingUnavailable = true;
        controller.enableAdbRemoteControl = false;
        controller.remoteCommandPollInterval = 0.25f;

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

    private static void DisableDefineSymbol(string symbol)
    {
        var target = BuildTargetGroup.Android;
        var existing = PlayerSettings.GetScriptingDefineSymbolsForGroup(target);
        var symbols = existing.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (symbols.RemoveAll(s => s == symbol) > 0)
        {
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

    private static void EnsurePicoOpenXRFeatureSetEnabled()
    {
        var featureSetMgrType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.FullName == "UnityEditor.XR.OpenXR.Features.OpenXRFeatureSetManager");
        if (featureSetMgrType == null) return;

        var initializeFeatureSets = featureSetMgrType.GetMethod(
            "InitializeFeatureSets",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            Type.EmptyTypes,
            null);
        initializeFeatureSets?.Invoke(null, null);

        var getFeatureSetWithId = featureSetMgrType.GetMethod(
            "GetFeatureSetWithId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var setFeaturesFromEnabledFeatureSets = featureSetMgrType.GetMethod(
            "SetFeaturesFromEnabledFeatureSets",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(BuildTargetGroup) },
            null);

        var picoSet = getFeatureSetWithId?.Invoke(null, new object[] { BuildTargetGroup.Android, "com.picoxr.openxr.features" });
        if (picoSet != null)
        {
            var enabledField = picoSet.GetType().GetField(
                "isEnabled",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            enabledField?.SetValue(picoSet, true);
            setFeaturesFromEnabledFeatureSets?.Invoke(null, new object[] { BuildTargetGroup.Android });
            AssetDatabase.SaveAssets();
            Debug.Log("[Build] Enabled OpenXR feature set: com.picoxr.openxr.features");
        }
        else
        {
            Debug.LogWarning("[Build] PICO OpenXR feature set not found. Passthrough may remain unavailable.");
        }
    }

    private static void EnsureSinglePicoTouchInteractionProfile()
    {
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (settings == null)
        {
            Debug.LogWarning("[Build] OpenXR Android settings not found; cannot enforce interaction profiles.");
            return;
        }

        var allowedInteractionTypes = new[]
        {
            "PICONeo3ControllerProfile",
            "PICO4UltraControllerProfile",
            "PICO4ControllerProfile",
            "PICOG3ControllerProfile",
            "EyeGazeInteraction",
            "HandInteractionProfile",
            "PalmPoseInteraction"
        };

        var interactionFeatures = settings.GetFeatures(typeof(OpenXRInteractionFeature));
        if (interactionFeatures == null || interactionFeatures.Length == 0)
        {
            Debug.LogWarning("[Build] No OpenXR interaction features found on Android settings.");
            return;
        }

        OpenXRFeature preferredController = null;
        bool hasEnabledAllowedInteraction = false;
        bool changed = false;
        foreach (var feature in interactionFeatures)
        {
            if (feature == null) continue;
            var typeName = feature.GetType().Name;
            var isAllowed = allowedInteractionTypes.Contains(typeName);
            if (typeName == "PICO4ControllerProfile")
                preferredController = feature;

            if (!isAllowed && feature.enabled)
            {
                feature.enabled = false;
                changed = true;
                Debug.Log($"[Build] OpenXR interaction disabled: {typeName}");
            }
            else if (isAllowed && feature.enabled)
            {
                hasEnabledAllowedInteraction = true;
            }
        }

        if (!hasEnabledAllowedInteraction && preferredController != null)
        {
            preferredController.enabled = true;
            changed = true;
            Debug.Log("[Build] Enabled OpenXR interaction: PICO4ControllerProfile");
        }

        if (changed)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Build] Enforced PICO-only OpenXR interaction profile set for validation.");
        }

        var enabledInteractions = settings.GetFeatures(typeof(OpenXRInteractionFeature))
            .Where(f => f != null && f.enabled)
            .Select(f => f.GetType().Name);
        Debug.Log("[Build] Enabled OpenXR interactions: " + string.Join(", ", enabledInteractions));
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
        var removeLoader = metadataStoreType.GetMethod(
            "RemoveLoader",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        removeLoader?.Invoke(null, new[] { managerSettings, "Unity.XR.PXR.PXR_Loader", (object)BuildTargetGroup.Android });
        assignLoader?.Invoke(null, new[] { managerSettings, "UnityEngine.XR.OpenXR.OpenXRLoader", (object)BuildTargetGroup.Android });

        var generalSettings = settingsForBuildTarget?.Invoke(perBuildTarget, androidTarget);
        var initManagerOnStartProp = generalSettings?.GetType().GetProperty(
            "InitManagerOnStart",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        initManagerOnStartProp?.SetValue(generalSettings, true);

        // Enable AutomaticLoading/Running for VR passthrough mode
        var autoLoadProp = managerSettings?.GetType().GetProperty(
            "automaticLoading",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        autoLoadProp?.SetValue(managerSettings, true);
        var autoRunProp = managerSettings?.GetType().GetProperty(
            "automaticRunning",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        autoRunProp?.SetValue(managerSettings, true);

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

        void SetBool(string name, bool val) {
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) {
                if (f.FieldType == typeof(bool)) f.SetValue(component, val);
                else if (f.FieldType == typeof(int)) f.SetValue(component, val ? 1 : 0);
            }
        }

        SetBool("openMRC", false);
        SetBool("handTracking", true);
        SetBool("bodyTracking", true);
        SetBool("spatialMesh", true);
        SetBool("videoSeeThrough", true);
        SetBool("usePremultipliedAlpha", true);

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

#if UNITY_ANDROID
/// <summary>
/// Post-build processor that re-injects hand tracking manifest metadata after the
/// PICO build processor strips it. Runs at callbackOrder=999 to execute after PICO's processor.
/// This avoids needing isHandTracking=true in PXR_OpenXRProjectSetting (which causes startup hangs).
/// </summary>
public class HandTrackingManifestPostProcessor : UnityEditor.Android.IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 999;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        var manifests = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string manifestPath)
        {
            if (File.Exists(manifestPath))
                manifests.Add(Path.GetFullPath(manifestPath));
        }

        // Unity passes either unityLibrary or launcher path depending on version/setup.
        AddIfExists(Path.Combine(path, "src", "main", "AndroidManifest.xml"));

        var parent = Directory.GetParent(path)?.FullName;
        if (!string.IsNullOrEmpty(parent))
        {
            AddIfExists(Path.Combine(parent, "unityLibrary", "src", "main", "AndroidManifest.xml"));
            AddIfExists(Path.Combine(parent, "launcher", "src", "main", "AndroidManifest.xml"));
        }

        if (manifests.Count == 0)
        {
            Debug.LogWarning("[HandTrackingManifest] No AndroidManifest.xml files found to patch.");
            return;
        }

        int patched = 0;
        foreach (var manifestPath in manifests)
        {
            if (PatchManifest(manifestPath))
                patched++;
        }

        Debug.Log($"[HandTrackingManifest] Patched {patched}/{manifests.Count} manifests.");
    }

    private static bool PatchManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return false;

        var doc = new System.Xml.XmlDocument();
        doc.Load(manifestPath);

        var nsManager = new System.Xml.XmlNamespaceManager(doc.NameTable);
        nsManager.AddNamespace("android", "http://schemas.android.com/apk/res/android");

        var appNode = doc.SelectSingleNode("//application");
        if (appNode == null)
        {
            Debug.LogWarning("[HandTrackingManifest] <application> node not found");
            return false;
        }

        // Remove ALL input mode metadata to let Pico use its default behavior.
        // Having controller=1 without handtracking=1 forces "controller required" mode.
        // Having handtracking=1 causes startup hangs on this firmware.
        // Removing both lets the system use whatever input is available.
        RemoveMetaData(appNode, nsManager, "handtracking");
        RemoveMetaData(appNode, nsManager, "Hand_Tracking_HighFrequency");
        RemoveMetaData(appNode, nsManager, "controller");
        EnsureMetaData(doc, appNode, nsManager, "enable_vst", "1");
        EnsureMetaData(doc, appNode, nsManager, "enable_mr", "1");

        // Ensure hand tracking permission exists (needed for runtime hand tracking)
        var manifestNode = doc.SelectSingleNode("//manifest");
        if (manifestNode != null)
        {
            string permName = "com.picovr.permission.HAND_TRACKING";
            var existingPerm = manifestNode.SelectSingleNode(
                $"uses-permission[@android:name='{permName}']", nsManager);
            if (existingPerm == null)
            {
                var permElem = doc.CreateElement("uses-permission");
                permElem.SetAttribute("name", "http://schemas.android.com/apk/res/android", permName);
                manifestNode.InsertBefore(permElem, appNode);
                Debug.Log($"[HandTrackingManifest] Added permission: {permName}");
            }
        }

        doc.Save(manifestPath);
        Debug.Log($"[HandTrackingManifest] Patched: {manifestPath}");
        return true;
    }

    private static void EnsureMetaData(System.Xml.XmlDocument doc, System.Xml.XmlNode appNode,
        System.Xml.XmlNamespaceManager nsManager, string name, string value)
    {
        var existing = appNode.SelectSingleNode(
            $"meta-data[@android:name='{name}']", nsManager);
        if (existing != null)
        {
            ((System.Xml.XmlElement)existing).SetAttribute("value",
                "http://schemas.android.com/apk/res/android", value);
        }
        else
        {
            var elem = doc.CreateElement("meta-data");
            elem.SetAttribute("name", "http://schemas.android.com/apk/res/android", name);
            elem.SetAttribute("value", "http://schemas.android.com/apk/res/android", value);
            appNode.AppendChild(elem);
        }
    }

    /// <summary>
    /// Removes a meta-data element with the given android:name from a parent node.
    /// </summary>
    private static void RemoveMetaData(System.Xml.XmlNode parentNode,
        System.Xml.XmlNamespaceManager nsManager, string name)
    {
        var existing = parentNode.SelectSingleNode(
            $"meta-data[@android:name='{name}']", nsManager);
        if (existing != null)
        {
            parentNode.RemoveChild(existing);
        }
    }
}
#endif
