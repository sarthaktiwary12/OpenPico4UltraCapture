# TELUS Dataset 1 — Capture Pipeline for PICO 4 Ultra

## Requirement Coverage Matrix

Every TELUS requirement mapped to how this pipeline delivers it:

| TELUS Requirement | How We Deliver | File/Location |
|---|---|---|
| **Egocentric RGB video (Mono/Stereo)** | PICO spatial video recording (stereo, 2048×1536 @ 60fps) | `video_blurred.mp4` |
| **Depth maps (if available)** | iToF-fed spatial mesh snapshots as PLY files | `depth_mesh/*.ply` |
| **Head pose** | XR InputDevice tracking, 60Hz | `head_pose.csv` |
| **Hand joint positions** | PXR_HandTracking: 26 joints/hand, position+rotation+radius | `hand_joints.csv` |
| **Finger articulations** | Quaternion rotation per finger joint | `hand_joints.csv` (rot columns) |
| **Accelerometer** | Android SensorManager native (or Unity fallback) | `imu.csv` (accel + gravity columns) |
| **Gyroscope** | Android SensorManager native (or Unity fallback) | `imu.csv` (gyro columns) |
| **Task start / Task end** | Operator-triggered via in-VR UI | `action_log.csv` |
| **Action type** | Selected from TELUS task list before recording | `action_log.csv` |
| **Camera intrinsics** | Approximate from device specs; note for per-device calibration | `calibration.json` |
| **Extrinsics (if available)** | HMD-relative camera mount described | `calibration.json` |
| **Coordinate system definition** | Unity left-handed Y-up, meters, quaternions | `calibration.json` |
| **Face blurring** | MediaPipe Face Detection in post-processing | Applied to `video_blurred.mp4` |
| **Device: PICO 4 Ultra** | ✓ Consumer hardware | — |
| **Slow, robot-like movements** | Enforced by operator protocol + UI reminders | — |
| **~2 min segments** | Timer + warnings in recording UI | — |
| **Hands visible in camera** | Real-time hand-visibility % shown during recording | — |

---

## Architecture (Path 2 — Consumer PICO 4 Ultra)

```
┌─────────────────────────────────────────────────────────────┐
│                    PICO 4 Ultra                             │
│                                                             │
│  SYSTEM LEVEL                    UNITY APP                  │
│  ┌──────────────┐               ┌───────────────────────┐  │
│  │ Spatial Video │               │ SensorRecorder        │  │
│  │ Recorder     │   SYNC via    │  ├ head_pose.csv      │  │
│  │              │◄─ audio beep ─│  ├ hand_joints.csv    │  │
│  │ 2K stereo    │   + clap      │  ├ imu.csv            │  │
│  │ @ 60fps      │   gesture     │  ├ action_log.csv     │  │
│  │              │               │  └ calibration.json   │  │
│  └──────┬───────┘               │                       │  │
│         │                       │ SyncManager           │  │
│         │                       │  ├ Clap detection     │  │
│         │                       │  └ Audio beep emit    │  │
│         │                       │                       │  │
│         │                       │ SpatialMeshCapture    │  │
│         │                       │  └ depth_mesh/*.ply   │  │
│         │                       │                       │  │
│         │                       │ NativeIMUBridge       │  │
│         │                       │  └ Android sensors    │  │
│         │                       │                       │  │
│         │                       │ RecordingController   │  │
│         │                       │  └ In-VR UI + QA      │  │
│  ───────┼───────────────────────┼───────────────────────┤  │
│         │            ADB Pull   │                       │  │
└─────────┼───────────────────────┼───────────────────────┘  │
          ▼                       ▼                          │
┌─────────────────────────────────────────────────────────┐  │
│                    postprocess.py                        │  │
│  sync → blur faces → validate → MANIFEST.json → deliver │  │
└─────────────────────────────────────────────────────────┘  │
```

**Key insight**: Video (`pov_video.mp4`) and sensor streams are started/stopped by the same Unity capture flow, then aligned with clap/beep sync markers.

---

## Setup

### 1. PICO 4 Ultra

- Firmware ≥ 5.14.0 (Settings → General → About → System Update)
- Developer Mode: Settings → General → About → tap "Software Version" ×7
- Hand Tracking: Settings → Developer → Hand Tracking → ON
- USB Debugging: Settings → Developer → USB Debugging → ON
- Recording quality: Settings → General → Screencasting & Recording → max
- One-time MediaProjection allow (required for in-app `pov_video.mp4` recording):
  ```bash
  adb shell cmd appops set com.sentientx.datacapture PROJECT_MEDIA allow
  ```
- Verify system hand tracking switch (must be `1`):
  ```bash
  adb shell settings get global sys_tracking_hand_enable
  # if needed:
  adb shell settings put global sys_tracking_hand_enable 1
  ```

### 2. Unity Project

**Unity 2022.3 LTS** with these packages:
- PICO Unity Integration SDK ≥ 3.1.0
- PICO OpenXR Plugin ≥ 1.3.3
- Unity OpenXR Plugin ≥ 1.12
- XR Hands ≥ 1.4
- TextMeshPro

**Project Settings:**
- XR Plug-in Management → OpenXR → Add PICO Interaction Profile
- OpenXR Features: Hand Tracking Subsystem ✓, PICO OpenXR ✓
- PXR_Manager: Hand Tracking ✓, Spatial Mesh ✓
- Platform: Android, Min API 29, IL2CPP, ARM64
- Package Name: `com.sentientx.datacapture`

**Scene hierarchy:**
```
XR Origin (with PXR_Manager)
├── Camera Offset / Main Camera
├── [GameObject] NativeIMUBridge
├── [GameObject] SensorRecorder
├── [GameObject] SyncManager (ref → SensorRecorder)
├── [GameObject] SpatialMeshCapture (ref → SensorRecorder)
├── [GameObject] PXR_SpatialMeshManager (PICO Building Block)
└── Canvas (World Space, anchored in room at app start)
    └── RecordingController (refs → all above + UI elements)
        ├── Panel: Task Select (dropdowns + Start button)
        ├── Panel: Recording (status, timer, sync, hands %, buttons)
        └── Panel: Results (validation report + New Session button)
```

### 3. Post-Processing (PC)

```bash
uv sync
# Also need ffmpeg for audio analysis:
sudo apt install ffmpeg  # or brew install ffmpeg
```

---

## Capture Protocol

**Execute this sequence for every recording:**

```
1. PUT ON HEADSET in passthrough mode
   Ensure good, even lighting. Clear personal items.

2. OPEN the DataCapture app (our Unity app)
   The app starts/stops `pov_video.mp4` together with sensor recording.

3. SELECT scenario + task type from dropdowns

4. TAP "Start Session"
   → Sensor recording begins
   → Status shows "RECORDING"
   → Preflight health check runs (hand tracking + IMU gravity)
   → Start is blocked when real hand tracking is unavailable (prevents unusable sessions)

5. CLAP HANDS TOGETHER (sync gesture)
   → App detects clap, plays audio beep
   → "✓ Sync OK" appears on screen
   → This syncs video↔sensor timeline

6. TAP "Start Task"
   → Logs task_start timestamp

7. PERFORM THE TASK
   ⚠ SLOW, DELIBERATE, ROBOT-LIKE MOVEMENTS
   ⚠ KEEP BOTH HANDS VISIBLE IN CAMERA VIEW
   ⚠ AVOID FAST HEAD MOVEMENTS
   Target: ~2 minutes

8. TAP "End Task"
   → Logs task_end timestamp

9. TAP "Stop Session"
    → On-device validation runs
    → Review the pass/fail checklist

10. REVIEW VALIDATION REPORT
    Fix any issues, re-record if needed
```

### ADB Remote Start/Stop (Headset-on-neck testing)

The app can be controlled over ADB without pressing the in-headset button:

```bash
# Start
adb shell am broadcast -n com.sentientx.datacapture/.RemoteCommandReceiver \
  -a com.sentientx.datacapture.CMD --es cmd start

# Stop
adb shell am broadcast -n com.sentientx.datacapture/.RemoteCommandReceiver \
  -a com.sentientx.datacapture.CMD --es cmd stop

# Status
adb shell am broadcast -n com.sentientx.datacapture/.RemoteCommandReceiver \
  -a com.sentientx.datacapture.CMD --es cmd status
adb shell "cat /sdcard/Android/data/com.sentientx.datacapture/files/record/remote_status.txt"
```

### Task Checklist (per TELUS spec)

**Indoor Household:**
- [ ] Pick-and-place: utensils (spoons, forks → drawer/rack)
- [ ] Pick-and-place: containers (boxes, jars → shelf)
- [ ] Pick-and-place: cloth (towels, napkins → fold/place)

**Office / Retail:**
- [ ] Shelf sorting (arrange by category/size)
- [ ] Object placement (items to specific positions)
- [ ] Appliance interaction: espresso machine (full operating sequence)

---

## Post-Processing

### Pull from device
```bash
uv run postprocess.py pull -o ./raw
```

### Full pipeline (sync + blur + validate + package)
```bash
uv run postprocess.py package \
  ./raw/sessions/20260226_143000_pick_place_utensils \
  ./raw/video/spatial_recording_001.mp4 \
  -o ./delivery
```

### Individual steps
```bash
# Sync only
uv run postprocess.py sync SESSION_DIR VIDEO.mp4

# Blur only
uv run postprocess.py blur input.mp4 output_blurred.mp4

# Validate only
uv run postprocess.py validate SESSION_DIR
```

---

## Delivery Package Structure

Each session delivers:

```
20260226_143000_pick_place_utensils/
├── MANIFEST.json               ← Contents description (matches TELUS spec)
├── video_blurred.mp4           ← Egocentric stereo RGB, faces blurred
├── hand_joints.csv             ← 26 joints × 2 hands × N frames
├── head_pose.csv               ← 6DoF head pose per frame
├── imu.csv                     ← Accelerometer + gyroscope + gravity per frame
├── action_log.csv              ← task events, sync events, quality/fallback diagnostics
├── calibration.json            ← Camera intrinsics, extrinsics, coordinate system
├── session_summary.json        ← Duration, fps, frame count, quality ratios
├── sync_metadata.json          ← Video↔sensor alignment parameters
├── validation_report.json      ← QA checklist results
└── depth_mesh/                 ← Depth proxy from iToF spatial mesh
    ├── depth_mesh_index.csv    ← Timestamp index
    ├── mesh_0000.ply           ← Triangulated environment mesh
    ├── mesh_0001.ply
    └── ...
```

### CSV Schemas

**hand_joints.csv** — 26 joints per hand (OpenXR model):
```
ts_s, frame, hand, joint_id, joint_name,
pos_x, pos_y, pos_z,           ← Position (meters)
rot_x, rot_y, rot_z, rot_w,    ← Orientation (quaternion)
radius                          ← Joint radius (meters)
```

Joint map: 0=Palm, 1=Wrist, 2-5=Thumb, 6-10=Index, 11-15=Middle, 16-20=Ring, 21-25=Little

**head_pose.csv**:
```
ts_s, frame, pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w, tracking_state
```

**imu.csv**:
```
ts_s, frame,
accel_x, accel_y, accel_z (m/s²),
gyro_x, gyro_y, gyro_z (rad/s),
grav_x, grav_y, grav_z (m/s²)
```

**action_log.csv**:
```
ts_s, frame, event_type, action_type, metadata
```
Events: session_start, session_end, session_end_crash, task_start, task_end, sync_clap, sync_manual
Additional diagnostic events may appear during degraded capture conditions:
`hand_fallback`, `hand_tracking_recovered`, `imu_fallback`, `imu_no_gravity_detected`,
`video_start_timeout`, `stall_detected`, `recording_too_short`, lifecycle events.

---

## Known Limitations (Consumer Path)

1. **Video sync precision**: ~10-50ms alignment via audio beep detection. Enterprise path would give per-frame hardware sync.
2. **Camera intrinsics**: Approximate from specs. Per-device checkerboard calibration recommended for sub-pixel accuracy.
3. **Depth**: Spatial mesh (not per-pixel depth frames). Enterprise path provides raw iToF data.
4. **Camera extrinsics**: Described as HMD-relative; exact offset TBD via calibration.

All of these are explicitly noted in `calibration.json` so TELUS knows what they're getting.
