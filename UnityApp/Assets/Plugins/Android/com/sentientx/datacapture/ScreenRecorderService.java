package com.sentientx.datacapture;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.MediaRecorder;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.IBinder;
import android.os.ParcelFileDescriptor;
import android.os.Handler;
import android.os.Looper;
import android.util.DisplayMetrics;
import android.util.Log;

import java.io.File;
import java.io.IOException;

public class ScreenRecorderService extends Service {
    public static final String ACTION_START = "com.sentientx.datacapture.action.START_PROJECTION_RECORD";
    public static final String ACTION_STOP = "com.sentientx.datacapture.action.STOP_PROJECTION_RECORD";

    public static final String EXTRA_RESULT_CODE = "result_code";
    public static final String EXTRA_DATA_INTENT = "data_intent";
    public static final String EXTRA_OUTPUT_PATH = "output_path";
    public static final String EXTRA_WIDTH = "width";
    public static final String EXTRA_HEIGHT = "height";
    public static final String EXTRA_BITRATE = "bitrate";
    public static final String EXTRA_FPS = "fps";

    private static final String TAG = "AndroidScreenRecorder";
    private static final String CHANNEL_ID = "openpico_capture_channel";
    private static final int NOTIFICATION_ID = 4107;

    private MediaProjection mediaProjection;
    private MediaProjection.Callback mediaProjectionCallback;
    private VirtualDisplay virtualDisplay;
    private MediaRecorder mediaRecorder;
    private ParcelFileDescriptor outputFd;

    private boolean recording = false;
    private String outputPath = null;

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) return START_NOT_STICKY;

        String action = intent.getAction();
        if (ACTION_STOP.equals(action)) {
            stopInternal();
            stopSelf();
            return START_NOT_STICKY;
        }

        if (ACTION_START.equals(action)) {
            handleStart(intent);
        }

        return START_NOT_STICKY;
    }

    private void handleStart(Intent intent) {
        int resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0);
        Intent dataIntent = intent.getParcelableExtra(EXTRA_DATA_INTENT);
        outputPath = intent.getStringExtra(EXTRA_OUTPUT_PATH);

        int width = intent.getIntExtra(EXTRA_WIDTH, 1280);
        int height = intent.getIntExtra(EXTRA_HEIGHT, 720);
        int bitrate = intent.getIntExtra(EXTRA_BITRATE, 8_000_000);
        int fps = intent.getIntExtra(EXTRA_FPS, 30);

        if (outputPath == null || outputPath.isEmpty()) {
            emitError("error:service:empty_output_path");
            stopSelf();
            return;
        }
        if (dataIntent == null || resultCode == 0) {
            emitError("error:service:missing_projection_result");
            stopSelf();
            return;
        }

        if ((width & 1) == 1) width -= 1;
        if ((height & 1) == 1) height -= 1;
        width = Math.max(640, width);
        height = Math.max(640, height);
        bitrate = Math.max(4_000_000, bitrate);
        fps = Math.max(24, Math.min(60, fps));

        try {
            startForegroundNotification();

            MediaProjectionManager mgr = (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
            if (mgr == null) {
                throw new IllegalStateException("projection_manager_null");
            }

            mediaProjection = mgr.getMediaProjection(resultCode, dataIntent);
            if (mediaProjection == null) {
                throw new IllegalStateException("media_projection_null");
            }
            mediaProjectionCallback = new MediaProjection.Callback() {
                @Override
                public void onStop() {
                    Log.w(TAG, "MediaProjection onStop callback");
                    stopInternal();
                    stopSelf();
                }
            };
            mediaProjection.registerCallback(mediaProjectionCallback, new Handler(Looper.getMainLooper()));

            prepareOutputFile(outputPath);

            mediaRecorder = new MediaRecorder();
            mediaRecorder.setVideoSource(MediaRecorder.VideoSource.SURFACE);
            mediaRecorder.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4);
            mediaRecorder.setVideoEncoder(MediaRecorder.VideoEncoder.H264);
            mediaRecorder.setVideoSize(width, height);
            mediaRecorder.setVideoFrameRate(fps);
            mediaRecorder.setVideoEncodingBitRate(bitrate);

            outputFd = ParcelFileDescriptor.open(
                    new File(outputPath),
                    ParcelFileDescriptor.MODE_CREATE | ParcelFileDescriptor.MODE_TRUNCATE | ParcelFileDescriptor.MODE_READ_WRITE
            );
            mediaRecorder.setOutputFile(outputFd.getFileDescriptor());
            mediaRecorder.prepare();

            DisplayMetrics dm = getResources().getDisplayMetrics();
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
            if (virtualDisplay == null) {
                throw new IllegalStateException("virtual_display_null");
            }

            mediaRecorder.start();
            recording = true;
            Log.i(TAG, "Recorder started via foreground service -> " + outputPath);
            AndroidScreenRecorderBridge.getInstance().onServiceEvent("started:" + outputPath);
        } catch (Throwable t) {
            String msg = sanitize(t.getMessage());
            Log.e(TAG, "handleStart failed", t);
            AndroidScreenRecorderBridge.getInstance().onServiceEvent(
                    "error:service_start:" + t.getClass().getSimpleName() + ":" + msg
            );
            stopInternal();
            stopSelf();
        }
    }

    private void stopInternal() {
        boolean wasRecording = recording;
        String stoppedPath = outputPath;

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

        try {
            if (mediaProjection != null) {
                if (mediaProjectionCallback != null) {
                    try {
                        mediaProjection.unregisterCallback(mediaProjectionCallback);
                    } catch (Throwable ignored) {
                    }
                }
                mediaProjection.stop();
            }
        } catch (Throwable ignored) {
        } finally {
            mediaProjection = null;
            mediaProjectionCallback = null;
        }

        try {
            if (outputFd != null) {
                outputFd.close();
            }
        } catch (IOException ignored) {
        } finally {
            outputFd = null;
        }

        recording = false;

        if (wasRecording && stoppedPath != null) {
            Log.i(TAG, "Recorder stopped via foreground service -> " + stoppedPath);
            AndroidScreenRecorderBridge.getInstance().onServiceEvent("stopped:" + stoppedPath);
        }
    }

    private void prepareOutputFile(String path) throws IOException {
        File outFile = new File(path);
        File parent = outFile.getParentFile();
        if (parent != null && !parent.exists() && !parent.mkdirs()) {
            throw new IOException("failed_mkdirs");
        }
        if (outFile.exists() && !outFile.delete()) {
            throw new IOException("failed_delete_existing");
        }
    }

    private void startForegroundNotification() {
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (nm != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel ch = new NotificationChannel(
                    CHANNEL_ID,
                    "OpenPico Capture",
                    NotificationManager.IMPORTANCE_LOW
            );
            nm.createNotificationChannel(ch);
        }

        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);

        builder
                .setContentTitle("OpenPico Capture")
                .setContentText("Recording POV video")
                .setSmallIcon(android.R.drawable.presence_video_online)
                .setOngoing(true);

        Notification notification = builder.build();

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        } else {
            startForeground(NOTIFICATION_ID, notification);
        }
    }

    private void emitError(String evt) {
        Log.e(TAG, evt);
        AndroidScreenRecorderBridge.getInstance().onServiceEvent(evt);
    }

    private static String sanitize(String input) {
        if (input == null) return "";
        return input.replace(",", "_").replace("\n", " ").replace("\r", " ");
    }

    @Override
    public void onDestroy() {
        stopInternal();
        super.onDestroy();
    }
}
