using System.Collections;
using UnityEngine;
#if PICO_XR
using Unity.XR.PXR;
#endif

public class RuntimePermissions : MonoBehaviour
{
    public static readonly string[] RequiredPermissions =
    {
        "android.permission.CAMERA",
        "android.permission.RECORD_AUDIO",
        "android.permission.BODY_SENSORS",
        "android.permission.READ_EXTERNAL_STORAGE",
        "android.permission.WRITE_EXTERNAL_STORAGE",
        "android.permission.READ_MEDIA_VIDEO",
        "com.picovr.permission.HAND_TRACKING",
        "com.picovr.permission.BODY_TRACKING",
        "com.picovr.permission.SPATIAL_DATA",
        "com.picoxr.permission.HAND_TRACKING",
        "com.picoxr.permission.BODY_TRACKING",
        "com.picoxr.permission.SPATIAL_DATA"
    };

    public static bool IsPermissionGranted(string permission)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission);
#else
        return true;
#endif
    }

    /// <summary>
    /// Returns true if hand tracking is available. Checks Android permission first,
    /// then falls back to PICO SDK setting state since PICO custom permissions
    /// are not registered in Android's runtime permission framework.
    /// </summary>
    public static bool HasAnyHandTrackingPermission()
    {
        if (IsPermissionGranted("com.picovr.permission.HAND_TRACKING") ||
            IsPermissionGranted("com.picoxr.permission.HAND_TRACKING"))
            return true;

        // PICO custom permissions aren't grantable via Android's permission system.
        // Check the PICO SDK directly — if the system-level hand tracking setting
        // is enabled, the app has access.
#if PICO_XR
        try
        {
            if (PXR_Plugin.HandTracking.UPxr_GetHandTrackerSettingState())
                return true;
        }
        catch { }

        // Also check the system setting via Android Settings API
        try
        {
            if (GetGlobalSettingInt("sys_tracking_hand_enable") == 1)
                return true;
        }
        catch { }
#endif
        return false;
    }

    /// <summary>
    /// Returns true if body tracking is available. Same fallback logic as hand tracking.
    /// </summary>
    public static bool HasAnyBodyTrackingPermission()
    {
        if (IsPermissionGranted("com.picovr.permission.BODY_TRACKING") ||
            IsPermissionGranted("com.picoxr.permission.BODY_TRACKING"))
            return true;

#if PICO_XR
        // Body tracking doesn't have a simple setting check like hand tracking.
        // Check if the device supports it via the SDK.
        try
        {
            bool supported = false;
            int ret = PXR_MotionTracking.GetBodyTrackingSupported(ref supported);
            if (ret == 0 && supported) return true;
        }
        catch { }
#endif
        return false;
    }

    private static int GetGlobalSettingInt(string key)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using var resolver = new AndroidJavaClass("android.provider.Settings$Global");
        using var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        using var act = up.GetStatic<AndroidJavaObject>("currentActivity");
        using var cr = act.Call<AndroidJavaObject>("getContentResolver");
        return resolver.CallStatic<int>("getInt", cr, key, 0);
#else
        return 0;
#endif
    }

    private IEnumerator Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Delay one frame to avoid requesting before activity is ready.
        yield return null;

        for (int i = 0; i < RequiredPermissions.Length; i++)
        {
            var permission = RequiredPermissions[i];
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission))
                continue;

            try
            {
                Debug.Log($"[Perms] Requesting {permission}");
                UnityEngine.Android.Permission.RequestUserPermission(permission);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Perms] Request failed for {permission}: {e.Message}");
            }

            // Give Android permission dialog time before requesting next one.
            yield return new WaitForSeconds(0.25f);
        }

        yield return new WaitForSeconds(0.5f);

        for (int i = 0; i < RequiredPermissions.Length; i++)
        {
            var permission = RequiredPermissions[i];
            bool granted = UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission);
            Debug.Log($"[Perms] {permission} granted={granted}");
        }

        // Log effective permission state using SDK fallback checks
        Debug.Log($"[Perms] Effective hand_tracking={HasAnyHandTrackingPermission()}, body_tracking={HasAnyBodyTrackingPermission()}");
#endif
        yield break;
    }
}
