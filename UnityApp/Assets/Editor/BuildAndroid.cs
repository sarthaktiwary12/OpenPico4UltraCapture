using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildAndroid
{
    private const string ScenePath = "Assets/Scenes/OpenPicoMain.unity";
    private const string PicoAppIdEnvVar = "PICO_APP_ID";

    public static void BuildCI()
    {
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

    private static void EnsureScene()
    {
        Directory.CreateDirectory("Assets/Scenes");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

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

        AddOptionalPicoRuntimeObjects();

        var autoGo = new GameObject("AutoSessionStarter");
        var auto = autoGo.AddComponent<AutoSessionStarter>();
        auto.sensor = sensor;
        auto.mesh = mesh;

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
        initManagerOnStartProp?.SetValue(generalSettings, true);

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

    private static void AddOptionalPicoRuntimeObjects()
    {
        AddComponentByReflection("PXR_Manager", "PXR_Manager");
        AddComponentByReflection("PXR_SpatialMeshManager", "PXR_SpatialMeshManager");
    }

    private static void AddComponentByReflection(string gameObjectName, string typeName)
    {
        var type = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.Name == typeName && typeof(Component).IsAssignableFrom(t));

        if (type == null) return;

        var go = new GameObject(gameObjectName);
        var component = go.AddComponent(type);

        // Disable MRC by default to avoid external camera/compositor paths in headless capture builds.
        if (typeName == "PXR_Manager")
        {
            var openMrcField = type.GetField(
                "openMRC",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            openMrcField?.SetValue(component, false);
        }
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
