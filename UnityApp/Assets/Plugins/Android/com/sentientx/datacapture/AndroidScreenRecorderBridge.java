package com.sentientx.datacapture;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.MediaRecorder;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.util.DisplayMetrics;

import com.unity3d.player.UnityPlayer;

public class AndroidScreenRecorderBridge {
    private static final int REQUEST_MEDIA_PROJECTION = 9917;
    private static final AndroidScreenRecorderBridge INSTANCE = new AndroidScreenRecorderBridge();

    private String unityCallbackObject = "AndroidScreenRecorder";
    private MediaProjectionManager projectionManager;
    private MediaProjection mediaProjection;
    private VirtualDisplay virtualDisplay;
    private MediaRecorder mediaRecorder;

    private boolean recording = false;
    private String outputPath = null;
    private int width = 1920;
    private int height = 1080;
    private int bitrate = 8000000;
    private int fps = 30;

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
            return false;
        }
        if (recording) return true;

        outputPath = outPath;
        width = (w & 1) == 0 ? w : w - 1;
        height = (h & 1) == 0 ? h : h - 1;
        bitrate = Math.max(4000000, bps);
        fps = Math.max(24, Math.min(60, frameRate));

        if (projectionManager == null) {
            projectionManager = (MediaProjectionManager) activity.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        }
        if (projectionManager == null) {
            sendEvent("error:no_projection_manager");
            return false;
        }

        activity.runOnUiThread(() -> {
            try {
                if (mediaProjection == null) {
                    Intent intent = projectionManager.createScreenCaptureIntent();
                    activity.startActivityForResult(intent, REQUEST_MEDIA_PROJECTION);
                } else {
                    startInternal(activity);
                }
            } catch (Throwable t) {
                sendEvent("error:start_request:" + t.getClass().getSimpleName());
            }
        });

        return true;
    }

    public boolean stopRecording() {
        Activity activity = UnityPlayer.currentActivity;
        if (activity == null) return false;
        activity.runOnUiThread(this::stopInternal);
        return true;
    }

    public void onActivityResult(Activity activity, int requestCode, int resultCode, Intent data) {
        if (requestCode != REQUEST_MEDIA_PROJECTION) return;
        if (resultCode != Activity.RESULT_OK || data == null) {
            sendEvent("error:permission_denied");
            return;
        }
        try {
            if (projectionManager == null) {
                projectionManager = (MediaProjectionManager) activity.getSystemService(Context.MEDIA_PROJECTION_SERVICE);
            }
            mediaProjection = projectionManager.getMediaProjection(resultCode, data);
            if (mediaProjection == null) {
                sendEvent("error:projection_null");
                return;
            }
            startInternal(activity);
        } catch (Throwable t) {
            sendEvent("error:on_activity_result:" + t.getClass().getSimpleName());
        }
    }

    private void startInternal(Activity activity) {
        try {
            stopInternal();

            mediaRecorder = new MediaRecorder();
            mediaRecorder.setVideoSource(MediaRecorder.VideoSource.SURFACE);
            mediaRecorder.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4);
            mediaRecorder.setVideoEncoder(MediaRecorder.VideoEncoder.H264);
            mediaRecorder.setVideoSize(width, height);
            mediaRecorder.setVideoFrameRate(fps);
            mediaRecorder.setVideoEncodingBitRate(bitrate);
            mediaRecorder.setOutputFile(outputPath);
            mediaRecorder.prepare();

            DisplayMetrics dm = activity.getResources().getDisplayMetrics();
            int densityDpi = dm != null ? dm.densityDpi : 320;

            virtualDisplay = mediaProjection.createVirtualDisplay(
                    "OpenPicoCapture",
                    width,
                    height,
                    densityDpi,
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                    mediaRecorder.getSurface(),
                    null,
                    null
            );

            mediaRecorder.start();
            recording = true;
            sendEvent("started:" + outputPath);
        } catch (Throwable t) {
            sendEvent("error:start_internal:" + t.getClass().getSimpleName());
            stopInternal();
        }
    }

    private void stopInternal() {
        if (!recording && mediaRecorder == null && virtualDisplay == null) return;
        try {
            if (mediaRecorder != null) {
                try {
                    mediaRecorder.stop();
                } catch (Throwable ignored) {
                }
                try {
                    mediaRecorder.reset();
                } catch (Throwable ignored) {
                }
                try {
                    mediaRecorder.release();
                } catch (Throwable ignored) {
                }
            }
        } finally {
            mediaRecorder = null;
        }

        try {
            if (virtualDisplay != null) {
                virtualDisplay.release();
            }
        } catch (Throwable ignored) {
        } finally {
            virtualDisplay = null;
        }

        recording = false;
        if (outputPath != null) sendEvent("stopped:" + outputPath);
    }

    private void sendEvent(String event) {
        try {
            UnityPlayer.UnitySendMessage(unityCallbackObject, "OnAndroidScreenRecorderEvent", event);
        } catch (Throwable ignored) {
        }
    }
}
