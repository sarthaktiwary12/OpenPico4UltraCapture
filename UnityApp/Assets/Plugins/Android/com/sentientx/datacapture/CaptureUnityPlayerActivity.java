package com.sentientx.datacapture;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;

import com.unity3d.player.UnityPlayerActivity;

public class CaptureUnityPlayerActivity extends UnityPlayerActivity {
    private static Activity instance;

    public static Activity getInstance() {
        return instance;
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        instance = this;
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        AndroidScreenRecorderBridge.getInstance().onActivityResult(this, requestCode, resultCode, data);
    }
}
