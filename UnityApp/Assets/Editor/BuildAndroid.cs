using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildAndroid
{
    private const string ScenePath = "Assets/Scenes/OpenPicoMain.unity";

    public static void BuildCI()
    {
        EnsureScene();

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenePath, true)
        };

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        PlayerSettings.companyName = "SentientX";
        PlayerSettings.productName = "OpenPico4UltraCapture";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.sentientx.datacapture");
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
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
            throw new Exception($"Android build failed: {report.summary.result}");
        }

        Debug.Log($"Android build succeeded: {apkPath}");
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

        var autoGo = new GameObject("AutoSessionStarter");
        var auto = autoGo.AddComponent<AutoSessionStarter>();
        auto.sensor = sensor;
        auto.mesh = mesh;

        EditorSceneManager.SaveScene(scene, ScenePath);
    }
}
