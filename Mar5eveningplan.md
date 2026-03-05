# Mar 5 Evening Plan — Fix All Fundamental Flaws

## Problem Summary

Three interconnected design flaws prevent the app from functioning:
1. Passthrough camera feed is disabled — user sees blank/black screen
2. Manifest deadlock kills hand tracking — no hand data is ever captured
3. Single input toggle — controller and hand pinch fight each other, causing accidental recording start/stop

---

## Fix 1: Enable Passthrough (Blank Screen)

### Root Cause
- `BuildAndroid.cs:191` sets `passthrough.enableOnStart = false`
- `PassthroughEnabler` only activates when `enableOnStart == true`
- Nothing at runtime ever flips it to true
- Result: camera background is solid black (transparent with nothing composited)

### Changes Required

**File: `UnityApp/Assets/Editor/BuildAndroid.cs`**
- Line 191: Change `passthrough.enableOnStart = false` to `passthrough.enableOnStart = true`

**File: `UnityApp/Assets/Scripts/PassthroughEnabler.cs`**
- Add a retry mechanism — if the first `EnableVideoSeeThrough` call fails silently (PICO sometimes needs a second attempt after XR fully stabilizes), retry after 1-2 seconds
- Add a public `ForceEnable()` method that `SimpleRecordingController` can call during startup as a safety net
- Log success/failure more visibly so we can confirm passthrough is actually active from logcat

**File: `UnityApp/Assets/Scripts/SimpleRecordingController.cs`**
- In `Start()`, find the `PassthroughEnabler` on the main camera and call `ForceEnable()` as a belt-and-suspenders backup after a 2-second delay

### Verification
- After build+deploy, logcat should show `[Passthrough] Enabled via ...`
- User should see real-world camera feed through the headset immediately on app launch

---

## Fix 2: Fix the Manifest Deadlock (Hand Tracking Never Starts)

### Root Cause Chain
1. `EnsurePXROpenXRProjectSettings()` is commented out in `BuildCI()` (line 27) because `isHandTracking=true` causes startup hang on Pico 4 Ultra firmware
2. `HandTrackingManifestPostProcessor` (line 818) strips ALL input metadata (`handtracking`, `controller`, `Hand_Tracking_HighFrequency`)
3. With no metadata, PICO runtime defaults to controller-only mode
4. XRHandSubsystem never starts, PICO native hand API returns nothing
5. `blockStartWhenHandTrackingUnavailable=true` blocks recording from ever starting

### Strategy: Runtime Hand Tracking Activation

Instead of fighting the manifest, we bypass it entirely:
- Keep the manifest clean (no handtracking/controller metadata — avoids the startup hang)
- Activate hand tracking at RUNTIME using PICO's native API after the XR session is fully initialized
- This is the officially supported approach for apps that need both controllers and hands

### Changes Required

**File: `UnityApp/Assets/Editor/BuildAndroid.cs`**
- Keep `EnsurePXROpenXRProjectSettings()` commented out (it causes the hang)
- Keep `HandTrackingManifestPostProcessor` as-is (removes problematic metadata)
- In `EnsurePXRProjectSettings()`, confirm `handTracking=true` is set (this is the PXR_ProjectSetting, not the OpenXR one — it's safe and enables the native API)

**File: `UnityApp/Assets/Scripts/SimpleRecordingController.cs`**
- Rewrite `InitHandTrackingRuntime()` coroutine to do proper runtime activation:
  ```
  1. Wait 3s for XR session to stabilize
  2. Request com.picovr.permission.HAND_TRACKING at runtime (already done)
  3. Wait 1s for permission grant
  4. Call PXR_HandTracking.SetActiveInputDevice(ActiveInputDevice.ControllerAndHandActive)
     — This is the KEY missing call. It tells the PICO runtime to enable BOTH
       controller and hand tracking simultaneously
  5. Wait 1s, then verify with UPxr_GetHandTrackerSettingState()
  6. If still not working, retry steps 4-5 up to 3 times with 2s delays
  7. Log detailed status at each step
  ```

**New utility method in `SimpleRecordingController.cs`:**
```csharp
IEnumerator ActivateDualInputMode()
{
    // PXR_HandTracking.SetActiveInputDevice(ActiveInputDevice.ControllerAndHandActive)
    // This enables simultaneous controller + hand tracking without manifest metadata
}
```

**File: `UnityApp/Assets/Scripts/SensorRecorder.cs`**
- In `IsHandTrackingReadyForCapture()`, add a grace period: if called within the first 10 seconds of app launch, return true with a warning instead of blocking. Hand tracking takes time to initialize at runtime.
- After 10s, revert to normal behavior (require real tracked hands)

**File: `UnityApp/Assets/Scripts/SimpleRecordingController.cs`**
- Change `blockStartWhenHandTrackingUnavailable` default to `false` initially
- After runtime hand tracking activation succeeds, set it to `true`
- This prevents the deadlock where recording is blocked before hand tracking has had time to start

### Verification
- Logcat should show: `[Controller] SetActiveInputDevice(ControllerAndHandActive) succeeded`
- Logcat should show: `[Hand] XRHandSubsystem found: ..., running=true`
- `IsHandTrackingReadyForCapture` should eventually report `tracked_hands=true`
- Both controller and hand joint data should appear in recordings

---

## Fix 3: Separate Controller Input from Hand Tracking (Accidental Toggle)

### Root Cause
- `CheckControllerInput()` combines controller buttons AND hand pinch into ONE `down` boolean
- `enableHandPinchToggle` is force-set to `true` in `Start()` — cannot be disabled
- Single `OnToggle()` flips recording state — any input toggles between start/stop
- Pinch detection is extremely sensitive (3cm threshold, 3 different detection methods, no debounce)

### Strategy: Controllers Record, Hands are Data-Only

**Principle:** Controller buttons = recording control. Hands = sensor data captured by SensorRecorder. Hand gestures should NEVER trigger recording state changes.

### Changes Required

**File: `UnityApp/Assets/Scripts/SimpleRecordingController.cs`**

#### A. Remove forced hand pinch toggle
- Delete lines 111-115 (the force-enable of `enableHandPinchToggle`)
- Set `enableHandPinchToggle = false` as the default in the field declaration (line 39)
- The field stays in case someone wants to re-enable it for a specific use case, but it's OFF by default

#### B. Rewrite `CheckControllerInput()` to separate start/stop
Replace the single toggle with explicit start/stop buttons:

```
Recording Start: Right Trigger OR A button (primaryButton on right controller)
Recording Stop:  B button (secondaryButton on right controller) OR Left Trigger

Why separate buttons:
- Eliminates accidental toggling
- Right trigger = natural "go" action
- B button = natural "stop/cancel" action
- Left trigger as backup stop (easy to reach in a hurry)
```

New logic:
```csharp
void CheckControllerInput()
{
    bool startPressed = false;
    bool stopPressed = false;

    var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
    if (right.isValid)
    {
        // Start: right trigger or A button
        if (right.TryGetFeatureValue(CommonUsages.triggerButton, out bool rt) && rt)
            startPressed = true;
        if (right.TryGetFeatureValue(CommonUsages.primaryButton, out bool ra) && ra)
            startPressed = true;

        // Stop: B button (secondaryButton)
        if (right.TryGetFeatureValue(CommonUsages.secondaryButton, out bool rb) && rb)
            stopPressed = true;
    }

    var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
    if (left.isValid)
    {
        // Stop: left trigger
        if (left.TryGetFeatureValue(CommonUsages.triggerButton, out bool lt) && lt)
            stopPressed = true;
    }

    // Fallback: enumerate all controllers if XRNode didn't find any
    if (!right.isValid && !left.isValid)
    {
        // ... similar enumeration with start/stop separation
    }

    // Hand pinch input — ONLY if explicitly enabled (default: OFF)
    if (enableHandPinchToggle)
    {
        if (CheckHandTrackingPinch())
        {
            if (!_recording) startPressed = true;
            else stopPressed = true;
        }
    }

    // Edge detection (only fire on press-down, not hold)
    if (startPressed && !_startWasDown && !_recording)
    {
        Debug.Log("[Controller] START pressed (controller)");
        if (PreflightAllowsStart())
            StartRecording();
    }
    if (stopPressed && !_stopWasDown && _recording)
    {
        Debug.Log("[Controller] STOP pressed (controller)");
        StopRecording();
    }

    _startWasDown = startPressed;
    _stopWasDown = stopPressed;
}
```

#### C. Add new state variables
```csharp
private bool _startWasDown;
private bool _stopWasDown;
```
Replace the single `_triggerWasDown` with these two.

#### D. Add 2-second cooldown after stop
After `StopRecording()` completes, block `startPressed` for 2 seconds to prevent accidental immediate re-start:
```csharp
private float _stopCooldownUntil;

// In stop logic:
_stopCooldownUntil = Time.time + 2f;

// In start logic:
if (Time.time < _stopCooldownUntil) startPressed = false;
```

#### E. Update HUD text
- Idle state: show "RIGHT TRIGGER: Start | B BUTTON: Stop"
- Recording state: show "B BUTTON or LEFT TRIGGER: Stop"

### Verification
- Right trigger starts recording, does NOT stop it
- B button stops recording, does NOT start it
- Hand gestures (pinch, grip, etc.) do NOT affect recording state
- SensorRecorder still captures hand joint data normally during recording
- 2s cooldown prevents accidental restart

---

## Fix 4: Camera2 Video Backend (Secondary)

### Root Cause
- `questcameralib.aar` is built for Meta Quest — `getLeftCameraMetaDataJson()` returns empty on PICO
- Falls back to generic Camera2 enumeration, which may not find the correct passthrough camera
- PICO 4 Ultra passthrough cameras may require PICO-specific access patterns

### Changes Required

**File: `UnityApp/Assets/Scripts/Camera/CameraPermissionManager.cs`**
- In `EnumeratePicoCameras()`, add PICO-specific camera ID hints:
  - PICO 4 Ultra typically exposes passthrough cameras at known IDs
  - Try camera IDs "0", "1" (stereo pair) with FRONT facing as primary candidates
  - Log ALL camera characteristics (facing, resolution, capabilities) for debugging
- Add a config option to hardcode camera ID if auto-discovery keeps failing

**File: `UnityApp/Assets/Scripts/SimpleRecordingController.cs`**
- In `TryStartCamera2Video()`, if camera2 fails, log detailed reason (not just "not ready")
- Add sensor recorder action log entry for camera discovery results

### Verification
- Logcat should show all available cameras with their properties
- If Camera2 backend works: `pov_video.mp4` should be non-empty with actual camera imagery
- If Camera2 still fails: shell_broadcast fallback should be tried

---

## Fix 5: Sensor Data Completeness Audit

### Current Streams
| Stream | File | Status | Issue |
|--------|------|--------|-------|
| Head Pose | head_pose.csv | Should work | Needs runtime verification |
| Hand Joints | hand_joints.csv | BROKEN | Falls back to synthetic skeleton because hand tracking never starts (Fix 2) |
| IMU | imu.csv | Partially working | Native bridge may work; multiple fallbacks mask real failures |
| Body Tracking | body_pose.csv | Unknown | Requires PICO body tracking to be supported and enabled |
| Spatial Mesh | depth_mesh/*.ply | Unknown | Multiple fallback methods; needs runtime verification |
| Video | pov_video.mp4 | BROKEN | Camera2 fails on PICO; shell broadcast may or may not work |

### Changes Required

**File: `UnityApp/Assets/Scripts/SensorRecorder.cs`**
- After Fix 2 is applied, hand tracking should provide real joint data
- Add a per-stream health indicator that logs every 10 seconds:
  ```
  [Health] head=OK(30fps) hands=REAL(26j) imu=NATIVE(9.8m/s2) body=SYNTH(24j) mesh=0_snapshots
  ```
- This makes it instantly visible from logcat which streams are real vs fallback

**File: `UnityApp/Assets/Scripts/BodyTrackingRecorder.cs`**
- Verify body tracking is properly requested at runtime (similar to hand tracking)
- If PICO body tracking API isn't available, log clearly instead of silently falling back

**File: `UnityApp/Assets/Scripts/NativeIMUBridge.cs`**
- Verify sensor registration succeeds on PICO 4 Ultra
- Log sensor type and sampling rate on successful registration

### Verification
- Health log every 10s shows all streams with real/fallback status
- After all fixes: head=OK, hands=REAL, imu=NATIVE or XR_HEAD, body=REAL or SYNTH (logged), mesh=captured

---

## Implementation Order

1. **Fix 1 (Passthrough)** — 1 line change + retry logic. Instant visual feedback.
2. **Fix 3 (Input Separation)** — Rewrite CheckControllerInput. Can test with controller immediately.
3. **Fix 2 (Hand Tracking Runtime)** — Rewrite InitHandTrackingRuntime. Needs device testing.
4. **Fix 5 (Health Logging)** — Add per-stream health log. Helps debug 2, 3, 4.
5. **Fix 4 (Camera2)** — Improve camera discovery. Needs device testing.

## Build & Test Cycle

```bash
# Build
~/Unity/Hub/Editor/6000.3.10f1/Editor/Unity \
  -batchmode -nographics -quit \
  -projectPath ~/OpenPico4UltraCapture/UnityApp \
  -executeMethod BuildAndroid.BuildCI \
  -logFile /tmp/build.log

# Install on PICO (serial from adb devices)
~/.local/android/platform-tools/adb -s <PICO_SERIAL> install -r UnityApp/Builds/openpico4ultra.apk

# Launch
~/.local/android/platform-tools/adb -s <PICO_SERIAL> shell am start \
  -n com.sentientx.datacapture/com.sentientx.datacapture.CaptureUnityPlayerActivity

# Monitor logs
~/.local/android/platform-tools/adb -s <PICO_SERIAL> logcat -s Unity:V

# Remote start/stop (for testing without controller)
~/.local/android/platform-tools/adb -s <PICO_SERIAL> shell am broadcast \
  -a com.sentientx.datacapture.CMD --es cmd start

# Pull recordings
~/.local/android/platform-tools/adb -s <PICO_SERIAL> pull \
  /sdcard/Android/data/com.sentientx.datacapture/files/dataset_capture/ ./local_view/
```

## Success Criteria

After all 5 fixes:
- [ ] App launches without hang, passthrough camera feed is visible (not blank)
- [ ] Controller right trigger starts recording
- [ ] Controller B button stops recording
- [ ] Hand pinch gestures do NOT start/stop recording
- [ ] hand_joints.csv contains REAL tracked joint data (not synthetic skeleton)
- [ ] All sensor streams show real data in health log
- [ ] pov_video.mp4 contains actual camera imagery
- [ ] No accidental recording toggle from hand movements
