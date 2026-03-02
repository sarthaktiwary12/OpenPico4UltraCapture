using System.Collections;
using UnityEngine;

public class RuntimePermissions : MonoBehaviour
{
    private static readonly string[] RequiredPermissions =
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
#endif
        yield break;
    }
}
