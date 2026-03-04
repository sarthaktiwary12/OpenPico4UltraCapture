package com.sentientx.datacapture;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.Locale;

public class RemoteCommandReceiver extends BroadcastReceiver {
    private static final String TAG = "RemoteCommandReceiver";
    private static final String ACTION_CMD = "com.sentientx.datacapture.CMD";

    @Override
    public void onReceive(Context context, Intent intent) {
        if (context == null || intent == null) return;
        if (!ACTION_CMD.equals(intent.getAction())) return;

        String cmd = intent.getStringExtra("cmd");
        if (cmd == null || cmd.trim().isEmpty()) {
            cmd = intent.getStringExtra("command");
        }
        if (cmd == null || cmd.trim().isEmpty()) {
            Log.w(TAG, "Missing cmd extra.");
            return;
        }

        cmd = cmd.trim().toLowerCase(Locale.US);
        Log.i(TAG, "Received cmd=" + cmd);

        // Persist command in app-owned storage so Unity can consume it even if message delivery is delayed.
        writeCommandFile(context, cmd);
    }

    private static void writeCommandFile(Context context, String cmd) {
        try {
            File externalBase = context.getExternalFilesDir(null);
            File recordDir = externalBase != null
                    ? new File(externalBase, "record")
                    : new File(context.getFilesDir(), "record");
            if (!recordDir.exists() && !recordDir.mkdirs()) {
                Log.w(TAG, "Failed to create record dir: " + recordDir.getAbsolutePath());
            }

            File cmdFile = new File(recordDir, "remote_cmd.txt");
            try (FileOutputStream fos = new FileOutputStream(cmdFile, false)) {
                fos.write(cmd.getBytes(StandardCharsets.UTF_8));
            }
        } catch (Throwable t) {
            Log.w(TAG, "Failed to write remote_cmd.txt: " + t.getMessage());
        }
    }

}
