using System.IO;
using UnityEditor.Android;

public class FixOpenXRNativeMergePost : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => int.MaxValue - 10;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        var root = path;
        if (!Directory.Exists(Path.Combine(root, "launcher")))
        {
            var parent = Directory.GetParent(path);
            if (parent != null && Directory.Exists(Path.Combine(parent.FullName, "launcher")))
                root = parent.FullName;
        }

        PatchGradleFile(Path.Combine(root, "launcher", "build.gradle"));
        PatchGradleFile(Path.Combine(root, "unityLibrary", "build.gradle"));
    }

    private static void PatchGradleFile(string gradlePath)
    {
        if (!File.Exists(gradlePath))
            return;

        var text = File.ReadAllText(gradlePath);
        if (text.Contains("libopenxr_loader.so"))
            return;

        const string marker = "android {";
        var idx = text.IndexOf(marker);
        if (idx < 0)
            return;

        var insert = @"
    packagingOptions {
        jniLibs {
            pickFirsts += ['**/libopenxr_loader.so']
        }
    }
";

        var bracePos = text.IndexOf('\n', idx);
        if (bracePos < 0) return;
        text = text.Insert(bracePos + 1, insert);
        File.WriteAllText(gradlePath, text);
    }
}
