# Capture Audit Issues And Fix Plan (2026-03-11)

## Scope
- Audit date: 2026-03-11
- Device/app path: `/sdcard/Android/data/com.sentientx.datacapture/files/dataset_capture`
- Sessions reviewed:
  - `20260311_164118_capture`
  - `20260311_165414_capture`
  - `20260311_165659_capture` (primary 15-second clap-trigger run)

## Verified Recording Window (Primary Session)
- Session: `20260311_165659_capture`
- `task_start`: `2.241326s`
- `task_end`: `17.982120s`
- Effective capture window: `15.740794s`

## Findings Summary
| ID | Severity | Issue | Evidence | Impact |
|---|---|---|---|---|
| VIDEO-001 | Critical | `pov_video.mp4` missing from session folder even though start/stop reported `ok` | `action_log.csv` has `video_start` + `video_stop`, no `video_saved`; no MP4 in session | Session not usable for TELUS delivery |
| IMU-001 | High | IMU recorded entirely in fallback mode | `session_summary.json`: `imu_fallback_frames=826`; `action_log.csv`: `imu_fallback` | Sensor source quality unclear; native IMU path not validated |
| BODY-001 | High | Body stream present but confidence is always `0.000` | `body_pose.csv` confidence min/mean/max all `0` | Body data likely synthetic fallback, not native tracked body |
| METRIC-001 | Medium | `session_summary.duration_s` includes post-stop finalize time | Summary `36.588s` vs task window `15.740794s` | QA metrics are misleading for real capture duration |
| QA-001 | Medium | Reality check coverage rule compares body span vs full head span | `body.coverage_vs_head` fails in audit despite full task-window coverage | False negatives in automated validation |

## Detailed Issues And Fix Plan

### VIDEO-001: Missing `pov_video.mp4`
**Observed**
- `video_start,shell_broadcast,ok`
- `video_stop,shell_broadcast,ok`
- no `video_saved` event
- no file at `.../20260311_165659_capture/pov_video.mp4`

**Likely cause**
- `TryStartVideoShellBroadcast`/`TryStopVideoShellBroadcast` treat broadcast dispatch as success without verifying recorder completion.
- `FindNewestVideo`/`ScanDir` path discovery is not finding the recorder output path under current OS/runtime behavior.

**Implementation plan**
1. In `UnityApp/Assets/Scripts/SimpleRecordingController.cs`:
   - Add explicit recorder state verification before logging `video_start`/`video_stop` success.
   - Expand file discovery using both Java file scan and MediaStore query filtered by `_videoStartTimeMs`.
   - Increase finalization polling with size-stabilization check.
   - Log richer events: `video_search_attempt`, `video_saved`, `video_save_failed` with reason/path.
2. On stop, if video copy fails:
   - Persist diagnostic JSON in session directory (`video_diagnostics.json`) with searched paths and timestamps.
3. Only mark success when destination `pov_video.mp4` exists and size > 0.

**Acceptance criteria**
- 3 consecutive clap sessions produce `video_saved` event and non-zero `pov_video.mp4` in each session directory.
- No `video_not_found` in those sessions.

---

### IMU-001: Full-session IMU fallback
**Observed**
- `session_summary.json` reports `imu_fallback_frames=826/826`.
- `action_log.csv` records `imu_fallback`.
- IMU CSV contains data, but source provenance per frame is not visible.

**Likely cause**
- Native SensorManager path does not reliably expose both accelerometer + gyroscope, forcing `SensorRecorder` to blend XR head kinematics/Unity fallback.

**Implementation plan**
1. In `UnityApp/Assets/Scripts/SensorRecorder.cs`:
   - Add source columns to `imu.csv` (for example: `accel_source`, `gyro_source`, `grav_source`, `fallback_used`).
   - Track fallback reason counts (missing native gyro, missing accel, stale native data).
   - Emit summary fields separating partial fallback from full fallback.
2. In `UnityApp/Assets/Scripts/NativeIMUBridge.cs`:
   - Add explicit runtime status snapshot event after sensor registration (`acc_ok`, `gyro_ok`, `grav_ok`, `lin_ok`).
   - Add stale-sensor detection event if `HasFreshData` is false during active recording.
3. In preflight (`SimpleRecordingController`/`SensorRecorder`):
   - Gate or warn on sessions where native IMU is unavailable at start.

**Acceptance criteria**
- IMU CSV clearly indicates data source per frame.
- Session summary distinguishes native vs fallback contribution.
- At least one new capture confirms expected source labeling behavior.

---

### BODY-001: Body stream appears fallback-only
**Observed**
- Body file has correct schema and 24 joints, but confidence always `0.000`.
- This pattern matches synthetic fallback path.

**Likely cause**
- `BodyTrackingRecorder` is writing fallback poses (`WriteFallbackBodyPose`) instead of valid native body tracking output.

**Implementation plan**
1. In `UnityApp/Assets/Scripts/BodyTrackingRecorder.cs`:
   - Add explicit `source` and `native_joint_count` output columns to disambiguate native vs fallback rows.
   - Log state transitions in `action_log.csv` (`body_native_active`, `body_fallback_active`, `body_native_recovered`).
   - Keep fallback confidence low/zero, but make source explicit so downstream QA can filter correctly.
2. Improve startup/retry diagnostics around:
   - `WantBodyTracking(true)`
   - `StartBodyTracking(...)`
   - `GetBodyTrackingState(...)`
3. Add session summary metrics for body-native ratio.

**Acceptance criteria**
- QA can distinguish native and fallback body frames without inference.
- In supported conditions, native body frames appear with non-zero confidence and logged native source.

---

### METRIC-001: Duration semantics mismatch
**Observed**
- `session_summary.duration_s` reflects session close time (after video-finalization wait), not task action window.

**Likely cause**
- `SensorRecorder.StopSession()` is intentionally deferred until `FindAndCopyVideo` finishes.

**Implementation plan**
1. In `UnityApp/Assets/Scripts/SensorRecorder.cs` and `SimpleRecordingController.cs`:
   - Add separate metrics:
     - `task_duration_s` (`task_start` -> `task_end`)
     - `recording_duration_s` (sensor active window)
     - `finalize_duration_s` (post-stop file finalization)
2. Preserve current behavior for logging, but make duration semantics explicit in summary JSON.

**Acceptance criteria**
- Session summary exposes task vs finalize timings distinctly.
- No confusion between operator recording length and post-stop housekeeping.

---

### QA-001: Validation false negatives for body coverage
**Observed**
- `local_view/reality_check_all_streams.py` compares body span vs full head stream span, causing failure when head/IMU keep running during finalize period.

**Implementation plan**
1. Update `local_view/reality_check_all_streams.py`:
   - Derive task window from `task_start`/`task_end`.
   - Evaluate body coverage and hand completeness inside task window.
   - Keep separate checks for post-stop residual logging.
2. Mark missing task markers as warning/fail with explicit reason.

**Acceptance criteria**
- Primary session no longer fails body coverage check when task-window coverage is complete.
- Report clearly indicates which window each metric uses.

## Execution Order
1. VIDEO-001 (blocker)
2. IMU-001 + BODY-001 observability upgrades
3. METRIC-001 summary semantics
4. QA-001 validator alignment
5. Re-run capture + audit and update this file with final status

## Re-Test Plan
1. Deploy new build.
2. Run three clap sessions (10s, 20s, 30s) with visible hand/body movement.
3. Pull sessions from device.
4. Run `local_view/reality_check_all_streams.py` on each session.
5. Confirm:
   - `pov_video.mp4` present in session folder
   - `video_saved` event present
   - IMU source labels present and coherent
   - Body source labels present and coherent
   - Summary durations match task window semantics

## Status
- Logged on: 2026-03-11
- Owner: capture pipeline workstream
- Next action: implement VIDEO-001 first, then sensor/source observability fixes
