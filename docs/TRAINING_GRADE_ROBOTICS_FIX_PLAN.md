# Training-Grade Robotics Data Plan

Date logged: 2026-03-02

## Objective
Upgrade OpenPico4UltraCapture from "best effort" XR capture to deterministic, quality-gated robotics training data capture.

## Workstreams (parallel implementation)

1. Hand tracking correctness (critical)
- Enable Android OpenXR hand features (`XR_EXT_hand_tracking`, `XR_EXT_hand_interaction`).
- Remove dead compile path (`#if PICO_XR && !PICO_OPENXR_SDK`) so native hand path is reachable.
- Add startup diagnostics: XR hand subsystem running state, PICO hand setting state, permission snapshot.
- Success metric: no persistent `hand_fallback,controller_skeleton`; real per-joint articulation variance.

2. IMU raw + gravity integrity
- Register accelerometer + gyroscope + gravity (+ linear acceleration fallback) in native bridge.
- Log sensor registration details (name/vendor/type) and first-sample magnitudes.
- Reconstruct raw acceleration from `linear + gravity` when runtime accelerometer is gravity-compensated.
- Extend `imu.csv` schema with `grav_x,grav_y,grav_z`.
- Success metric: stationary accel magnitude near gravity; explicit gravity vector always available or flagged.

3. Body tracking coverage and fallback quality
- Add detailed native diagnostic logs (`ret`, joint count, spatial spread).
- Attempt `WantBodyTracking(true/false)` when supported; preserve start/stop resource lifecycle.
- Relax over-strict native validation threshold.
- Guarantee 24 joints per frame by filling missing native joints with synthetic fallback joints (`confidence=0.0`).
- Success metric: `body_pose.csv` always has 24 joints/frame.

4. Session stability and crash resilience
- Guard frame loop with try/catch and log update exceptions.
- Add lifecycle event logging: pause/resume/focus/unfocus.
- Add crash-safe quit path with `session_end_crash` closeout.
- Add stall watchdog (`stall_detected` when no frame write >2s).
- Add minimum-duration warning (<10s) for unusable sessions.
- Success metric: each session has deterministic end event and explicit failure reasons.

5. Live quality validation and operator gating
- Add preflight health check before start (hand tracking + IMU gravity).
- Add explicit override flow when health check fails.
- Add live HUD quality indicators for hand, IMU, body, and effective FPS.
- Add quality snapshot events and quality footer on save status.
- Success metric: operator sees data quality before and during capture.

## Execution status
- [x] Workstream 1 implemented in project settings + SensorRecorder.
- [x] Workstream 2 implemented in NativeIMUBridge + SensorRecorder CSV/schema.
- [x] Workstream 3 implemented in BodyTrackingRecorder with 24-joint fallback fill.
- [x] Workstream 4 implemented in SimpleRecordingController lifecycle + watchdog handling.
- [x] Workstream 5 implemented in preflight checks + live status + quality snapshot.

## Validation checklist
- [ ] Build Unity Android player and confirm no compile errors.
- [ ] Device run: verify `hand_tracking_availability` is `ready`.
- [ ] Device run: verify `imu.csv` has gravity columns and sane magnitudes.
- [ ] Device run: verify `body_pose.csv` has 24 rows/frame.
- [ ] Device run: verify app pause/focus/quit events in `action_log.csv`.
- [ ] Device run: verify preflight warning + override behavior.
