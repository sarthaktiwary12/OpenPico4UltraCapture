using System.IO;
using System.Text.RegularExpressions;
using UnityEditor.Android;

public class FixAndroidManifestPost : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => int.MaxValue;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        var root = path;
        var directUnityLibrary = Path.Combine(root, "unityLibrary");
        if (!Directory.Exists(directUnityLibrary))
        {
            var parent = Directory.GetParent(path);
            if (parent != null && Directory.Exists(Path.Combine(parent.FullName, "unityLibrary")))
                root = parent.FullName;
        }

        var manifestPath = Path.Combine(root, "unityLibrary/src/main/AndroidManifest.xml");
        if (!File.Exists(manifestPath)) return;

        var xml = File.ReadAllText(manifestPath);

        xml = Regex.Replace(
            xml,
            "android:hardwareAccelerated=\"false\"",
            "android:hardwareAccelerated=\"true\"");

        xml = Regex.Replace(
            xml,
            "android:screenOrientation=\"fullUser\"",
            "android:screenOrientation=\"landscape\"");

        File.WriteAllText(manifestPath, xml);
    }
}
