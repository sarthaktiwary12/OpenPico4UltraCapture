package com.sentientx.datacapture;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.util.Log;

import com.unity3d.player.UnityPlayer;

public class AndroidScreenRecorderBridge {
    private static final String TAG = "AndroidScreenRecorder";
    private static final int REQUEST_MEDIA_PROJECTION = 9917;
    private static final AndroidScreenRecorderBridge INSTANCE = new AndroidScreenRecorderBridge();

    private String unityCallbackObject = "AndroidScreenRecorder";
    private MediaProjectionManager projectionManager;
    private boolean recording = false;

    private String pendingOutputPath;
    private int pendingWidth;
    private int pendingHeight;
    private int pendingBitrate;
    private int pendingFps;

    public static AndroidScreenRecorderBridge getInstance() {
        return INSTANCE;
    }

    public void setUnityCallbackObject(String gameObjectName) {
        if (gameObjectName != null && !gameObjectName.isEmpty()) {
            unityCallbackObject = gameObjectName;
        }
    }

    public boolean requestStart(String outPath, int w, int h, int bps, int frameRate) {
        Activity activity = UnityPlayer.currentActivity;
        if (activity == null) {
            sendEvent("error:no_activity");
            Log.e(TAG, "requestStart failed: no current activity");
            return false;
        }
        if (recording) {
            // Already recording — stop current recording and restart with new output path.
            // The existing MediaProjection consent is consumed, so we stop+re-request.
            Log.i(TAG, "Already recording, stopping before restarting with new path: " + outPath);
            Intent stopIntent = new Intent(activity, ScreenRecorderService.class);
            stopIntent.setAction(ScreenRecorderService.ACTION_STOP);
            activity.startService(stopIntent);
            recording = false;
            // Small delay to let the service finalize, then fall through to start new recording
        }
        if (outPath == null || outPath.isEmpty()) {
            sendEvent("error:empty_output_path");
            Log.e(TAG, "requestStart failed: empty output path");
            return false;
        }

        pendingOutputPath = outPath;
        pendingWidth = (w & 1) == 0 ? w : w - 1;
        pendingHeight = (h & 1) == 0 ? h : h - 1;
        pendingBitrate = Math.max(4_000_000, bps);
        pendingFps = Math.max(24, Math.min(60, frameRate));

        if (projectionManager == null) {
            projectionManager = (MediaProjectionManager) activity.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        }
        if (projectionManager == null) {
            sendEvent("error:no_projection_manager");
            Log.e(TAG, "requestStart failed: projection manager unavailable");
            return false;
        }

        Log.i(TAG, "requestStart path=" + pendingOutputPath + " size=" + pendingWidth + "x" + pendingHeight + " fps=" + pendingFps + " bitrate=" + pendingBitrate);

        activity.runOnUiThread(() -> {
            try {
                Intent intent = projectionManager.createScreenCaptureIntent();
                activity.startActivityForResult(intent, REQUEST_MEDIA_PROJECTION);
            } catch (Throwable t) {
                String msg = sanitize(t.getMessage());
                sendEvent("error:start_request:" + t.getClass().getSimpleName() + ":" + msg);
                Log.e(TAG, "requestStart exception", t);
            }
        });

        return true;
    }

    public boolean stopRecording() {
        Activity activity = UnityPlayer.currentActivity;
        if (activity == null) return false;
        if (!recording) return false;

        Intent stopIntent = new Intent(activity, ScreenRecorderService.class);
        stopIntent.setAction(ScreenRecorderService.ACTION_STOP);
        activity.startService(stopIntent);
        return true;
    }

    public void onActivityResult(Activity activity, int requestCode, int resultCode, Intent data) {
        if (requestCode != REQUEST_MEDIA_PROJECTION) return;

        if (resultCode != Activity.RESULT_OK || data == null) {
            sendEvent("error:permission_denied");
            Log.e(TAG, "onActivityResult denied resultCode=" + resultCode + " dataNull=" + (data == null));
            return;
        }

        try {
            Intent serviceIntent = new Intent(activity, ScreenRecorderService.class);
            serviceIntent.setAction(ScreenRecorderService.ACTION_START);
            serviceIntent.putExtra(ScreenRecorderService.EXTRA_RESULT_CODE, resultCode);
            serviceIntent.putExtra(ScreenRecorderService.EXTRA_DATA_INTENT, data);
            serviceIntent.putExtra(ScreenRecorderService.EXTRA_OUTPUT_PATH, pendingOutputPath);
            serviceIntent.putExtra(ScreenRecorderService.EXTRA_WIDTH, pendingWidth);
            serviceIntent.putExtra(ScreenRecorderService.EXTRA_HEIGHT, pendingHeight);
            serviceIntent.putExtra(ScreenRecorderService.EXTRA_BITRATE, pendingBitrate);
            serviceIntent.putExtra(ScreenRecorderService.EXTRA_FPS, pendingFps);

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                activity.startForegroundService(serviceIntent);
            } else {
                activity.startService(serviceIntent);
            }
        } catch (Throwable t) {
            String msg = sanitize(t.getMessage());
            sendEvent("error:on_activity_result:" + t.getClass().getSimpleName() + ":" + msg);
            Log.e(TAG, "onActivityResult exception", t);
        }
    }

    public void onServiceEvent(String evt) {
        if (evt == null || evt.isEmpty()) return;

        if (evt.startsWith("started:")) {
            recording = true;
        } else if (evt.startsWith("stopped:")) {
            recording = false;
        } else if (evt.startsWith("error:")) {
            recording = false;
        }

        sendEvent(evt);
    }

    private void sendEvent(String event) {
        try {
            UnityPlayer.UnitySendMessage(unityCallbackObject, "OnAndroidScreenRecorderEvent", event);
        } catch (Throwable ignored) {
        }
    }

    private static String sanitize(String input) {
        if (input == null) return "";
        return input.replace(",", "_").replace("\n", " ").replace("\r", " ");
    }
}
