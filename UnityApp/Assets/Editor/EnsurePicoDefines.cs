using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Auto-adds the PICO_XR scripting define symbol for Android when the PICO SDK
/// is present.  Runs on editor load and after every recompilation so that
/// local builds (not just CI) get the define.
/// </summary>
[InitializeOnLoad]
public static class EnsurePicoDefines
{
    private const string Symbol = "PICO_XR";

    static EnsurePicoDefines()
    {
        EnsureDefine();
    }

    private static void EnsureDefine()
    {
        // Only add the define if the PICO XR Loader type is actually present
        bool picoSdkPresent = AppDomain.CurrentDomain
            .GetAssemblies()
            .Any(a =>
            {
                try { return a.GetTypes().Any(t => t.FullName == "Unity.XR.PXR.PXR_Loader"); }
                catch { return false; }
            });

        if (!picoSdkPresent) return;

        var target = BuildTargetGroup.Android;
        var existing = PlayerSettings.GetScriptingDefineSymbolsForGroup(target);
        var symbols = existing.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!symbols.Contains(Symbol))
        {
            symbols.Add(Symbol);
            PlayerSettings.SetScriptingDefineSymbolsForGroup(target, string.Join(";", symbols));
            Debug.Log($"[EnsurePicoDefines] Added {Symbol} to Android scripting defines.");
        }
    }
}
