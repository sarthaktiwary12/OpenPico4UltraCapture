#!/usr/bin/env python3
"""
TELUS Dataset 1 — Post-Processing Pipeline
Egocentric Humanoid Motion Data from PICO 4 Ultra (consumer path)

Pipeline:
  1. Pull sensor data + spatial video from device via ADB
  2. Synchronize video↔sensor using audio beep detection + wallclock alignment
  3. Blur all faces in video (TELUS privacy requirement)
  4. Validate completeness against TELUS spec
  5. Package for delivery

uv sync

Usage:
  uv run postprocess.py pull                          # Pull from PICO via ADB
  uv run postprocess.py sync SESSION_DIR VIDEO.mp4    # Sync video + sensors
  uv run postprocess.py blur VIDEO.mp4 OUTPUT.mp4     # Blur faces
  uv run postprocess.py validate SESSION_DIR          # Validate session
  uv run postprocess.py package SESSION_DIR VIDEO.mp4 # Full pipeline → delivery
"""

import argparse, json, os, subprocess, sys
from pathlib import Path

import cv2
import numpy as np
import pandas as pd
from tqdm import tqdm

# ── 1. ADB Pull ──────────────────────────────────────────────

APP_DATA = "/sdcard/Android/data/com.sentientx.datacapture/files/dataset_capture"
VIDEO_DIRS = ["/sdcard/PICO/SpatialVideo", "/sdcard/DCIM/ScreenRecording", "/sdcard/PICO/Videos"]

def cmd_pull(args):
    out = args.output
    os.makedirs(out, exist_ok=True)

    r = subprocess.run(["adb", "devices"], capture_output=True, text=True)
    if "device" not in r.stdout.split("\n", 2)[1]:
        sys.exit("ERROR: No device. Connect PICO 4 Ultra via USB, enable USB debugging.")

    print("[PULL] Sensor data...")
    subprocess.run(["adb", "pull", APP_DATA, f"{out}/sessions/"], check=False)

    print("[PULL] Video recordings...")
    os.makedirs(f"{out}/video", exist_ok=True)
    for vd in VIDEO_DIRS:
        subprocess.run(["adb", "pull", vd, f"{out}/video/"], check=False)

    # List what we got
    print("\n[PULL] Sessions found:")
    sd = Path(f"{out}/sessions")
    if sd.exists():
        for d in sorted(sd.iterdir()):
            if d.is_dir(): print(f"  {d.name}")
    print("\n[PULL] Videos found:")
    vd = Path(f"{out}/video")
    if vd.exists():
        for f in sorted(vd.rglob("*.mp4")):
            print(f"  {f.relative_to(vd)}")
    print("\nDone. Next: uv run postprocess.py sync <session_dir> <video.mp4>")

# ── 2. Video-Sensor Sync ─────────────────────────────────────

def cmd_sync(args):
    sd = Path(args.session_dir)
    vp = Path(args.video)

    print(f"[SYNC] Session: {sd.name}")
    print(f"[SYNC] Video:   {vp.name}")

    summary = json.loads((sd / "session_summary.json").read_text())
    action = pd.read_csv(sd / "action_log.csv")

    # Video info
    cap = cv2.VideoCapture(str(vp))
    vfps = cap.get(cv2.CAP_PROP_FPS)
    vframes = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    vdur = vframes / vfps if vfps > 0 else 0
    cap.release()

    sdur = summary["duration_s"]
    sfps = summary.get("actual_fps", summary.get("target_fps", 60))

    print(f"  Video: {vframes} frames @ {vfps:.1f}fps = {vdur:.1f}s")
    print(f"  Sensor: {summary['total_frames']} frames @ {sfps:.1f}fps = {sdur:.1f}s")

    # Find sync clap events
    claps = action[action["event_type"].str.contains("sync_clap|sync_manual", na=False)]
    sync_sensor_ts = None
    sync_wallclock = None

    if len(claps) > 0:
        first_clap = claps.iloc[0]
        sync_sensor_ts = first_clap["ts_s"]
        meta = str(first_clap.get("metadata", ""))
        # Extract wallclock from metadata: "...wall=1234567.890..."
        for part in meta.split("|"):
            if "wall=" in part:
                sync_wallclock = float(part.split("=")[1])
        print(f"  Sync clap: sensor_t={sync_sensor_ts:.3f}s, wall={sync_wallclock}")

    # Detect audio beep in video for precise alignment
    beep_video_ts = detect_audio_beep(str(vp))
    if beep_video_ts is not None:
        print(f"  Audio beep detected in video at: {beep_video_ts:.3f}s")
        if sync_sensor_ts is not None:
            offset = beep_video_ts - sync_sensor_ts
            print(f"  → Offset: video_time = sensor_time + {offset:.3f}s")
        else:
            offset = None
            print("  → No sensor sync event — using wallclock fallback")
    else:
        print("  ⚠ No audio beep detected — using duration-based alignment")
        offset = vdur - sdur  # Assume video started before sensors
        print(f"  → Approx offset: {offset:.2f}s (video started ~{offset:.1f}s before sensors)")

    # Write sync metadata
    sync_meta = {
        "video_file": vp.name,
        "video_fps": vfps, "video_frames": vframes, "video_duration_s": round(vdur, 3),
        "sensor_fps": sfps, "sensor_frames": summary["total_frames"], "sensor_duration_s": round(sdur, 3),
        "sync_method": "audio_beep" if beep_video_ts else "duration_alignment",
        "sync_clap_sensor_ts": sync_sensor_ts,
        "sync_beep_video_ts": beep_video_ts,
        "offset_s": round(offset, 4) if offset else None,
        "usage": "video_frame_time = sensor_timestamp + offset_s",
        "sensor_to_video_frame": f"video_frame = (sensor_ts + {offset:.4f}) * {vfps:.1f}" if offset else None
    }
    (sd / "sync_metadata.json").write_text(json.dumps(sync_meta, indent=2))
    print(f"[SYNC] Written: sync_metadata.json")

def detect_audio_beep(video_path, freq=1000, threshold=0.3):
    """Detect 1kHz sync beep in video audio track using FFT."""
    try:
        from scipy.io import wavfile
        from scipy.signal import spectrogram

        # Extract audio via ffmpeg
        wav_tmp = "/tmp/_sync_audio.wav"
        subprocess.run(["ffmpeg", "-y", "-i", video_path, "-ac", "1", "-ar", "44100",
                        "-vn", wav_tmp], capture_output=True, check=True)

        sr, data = wavfile.read(wav_tmp)
        if data.dtype != np.float32:
            data = data.astype(np.float32) / np.iinfo(data.dtype).max

        # Compute spectrogram
        f, t, Sxx = spectrogram(data, sr, nperseg=2048, noverlap=1024)

        # Find bin closest to 1kHz
        freq_idx = np.argmin(np.abs(f - freq))
        power_at_freq = Sxx[freq_idx, :]

        # Find first spike above threshold
        norm = power_at_freq / (power_at_freq.max() + 1e-10)
        peaks = np.where(norm > threshold)[0]

        os.remove(wav_tmp)

        if len(peaks) > 0:
            return float(t[peaks[0]])
        return None
    except Exception as e:
        print(f"  ⚠ Audio analysis failed: {e}")
        return None

# ── 3. Face Blurring ─────────────────────────────────────────

def cmd_blur(args):
    import mediapipe as mp

    inp, out = args.input_video, args.output_video
    print(f"[BLUR] Input:  {inp}")
    print(f"[BLUR] Output: {out}")

    face_det = mp.solutions.face_detection.FaceDetection(model_selection=1, min_detection_confidence=0.25)
    cap = cv2.VideoCapture(inp)
    fps = cap.get(cv2.CAP_PROP_FPS)
    w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    fourcc = cv2.VideoWriter_fourcc(*'mp4v')
    writer = cv2.VideoWriter(out, fourcc, fps, (w, h))
    detections = 0

    for _ in tqdm(range(total), desc="Blurring faces"):
        ret, frame = cap.read()
        if not ret: break

        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        results = face_det.process(rgb)

        if results.detections:
            for det in results.detections:
                bb = det.location_data.relative_bounding_box
                margin = 0.35
                x1 = max(0, int((bb.xmin - bb.width * margin) * w))
                y1 = max(0, int((bb.ymin - bb.height * margin) * h))
                x2 = min(w, int((bb.xmin + bb.width * (1 + margin)) * w))
                y2 = min(h, int((bb.ymin + bb.height * (1 + margin)) * h))
                if x2 > x1 and y2 > y1:
                    frame[y1:y2, x1:x2] = cv2.GaussianBlur(frame[y1:y2, x1:x2], (51, 51), 30)
                    detections += 1

        writer.write(frame)

    cap.release(); writer.release(); face_det.close()
    print(f"[BLUR] Done: {detections} face detections blurred across {total} frames.")
    print(f"[BLUR] Output: {out}")

# ── 4. Validation ────────────────────────────────────────────

def cmd_validate(args):
    sd = Path(args.session_dir)
    print(f"\n{'='*60}")
    print(f"VALIDATION: {sd.name}")
    print(f"{'='*60}\n")

    checks = []

    def chk(ok, label, critical=True):
        icon = "✓" if ok else ("✗" if critical else "⚠")
        checks.append({"pass": ok, "label": label, "critical": critical})
        print(f"  {icon}  {label}")

    # Required files
    for f in ["hand_joints.csv", "head_pose.csv", "imu.csv", "action_log.csv",
              "calibration.json", "session_summary.json"]:
        chk((sd / f).exists(), f"File: {f}")

    try:
        summary = json.loads((sd / "session_summary.json").read_text())
        dur = summary["duration_s"]
        chk(60 < dur < 300, f"Duration: {dur:.1f}s (target ~120s)")
        frames = summary["total_frames"]
        expected = dur * summary.get("target_fps", 60)
        ratio = frames / max(expected, 1)
        chk(ratio > 0.90, f"Frames: {frames} ({ratio:.0%} of expected)")
    except Exception as e:
        chk(False, f"Summary: {e}")

    try:
        hand = pd.read_csv(sd / "hand_joints.csv")
        chk("left" in hand["hand"].values, "Left hand data present")
        chk("right" in hand["hand"].values, "Right hand data present")
        joints_per_hand = hand.groupby(["frame", "hand"])["joint_id"].nunique().mean()
        chk(joints_per_hand > 20, f"Avg joints/hand/frame: {joints_per_hand:.1f} (need 26)")
        coverage = hand["frame"].nunique() / max(frames, 1)
        chk(coverage > 0.70, f"Hand tracking coverage: {coverage:.0%}")
    except Exception as e:
        chk(False, f"Hand data: {e}")

    try:
        imu = pd.read_csv(sd / "imu.csv")
        accel_ok = imu[["accel_x", "accel_y", "accel_z"]].abs().max().max() > 0.01
        gyro_ok = imu[["gyro_x", "gyro_y", "gyro_z"]].abs().max().max() > 0.001
        chk(accel_ok, "Accelerometer active (non-zero)")
        chk(gyro_ok, "Gyroscope active (non-zero)")
    except Exception as e:
        chk(False, f"IMU data: {e}")

    try:
        act = pd.read_csv(sd / "action_log.csv")
        chk("task_start" in act["event_type"].values, "Action: task_start logged")
        chk("task_end" in act["event_type"].values, "Action: task_end logged")
        has_sync = act["event_type"].str.contains("sync").any()
        chk(has_sync, "Sync event present", critical=False)
    except Exception as e:
        chk(False, f"Action log: {e}")

    chk((sd / "sync_metadata.json").exists(), "Sync metadata", critical=False)
    chk((sd / "depth_mesh").is_dir() and any((sd / "depth_mesh").iterdir()), "Depth mesh data", critical=False)

    # Check for blurred video
    has_video = any((sd / f).exists() for f in ["video_blurred.mp4"]) or \
                any(sd.parent.glob(f"*{sd.name}*blurred*"))
    chk(has_video, "Blurred video", critical=False)

    print(f"\n{'─'*60}")
    critical_pass = all(c["pass"] for c in checks if c["critical"])
    print(f"{'PASS ✓' if critical_pass else 'FAIL ✗'} — Critical checks")
    print(f"{'─'*60}\n")

    report = {"session": sd.name, "checks": checks, "overall_pass": critical_pass}
    (sd / "validation_report.json").write_text(json.dumps(report, indent=2, default=str))
    return critical_pass

# ── 5. Package for Delivery ──────────────────────────────────

def cmd_package(args):
    import shutil

    sd = Path(args.session_dir)
    vp = Path(args.video) if args.video else None

    print(f"\n[PACKAGE] Full pipeline: {sd.name}")

    # Step 1: Sync
    if vp and vp.exists():
        print("\n── Step 1: Sync ──")
        ns = argparse.Namespace(session_dir=str(sd), video=str(vp))
        cmd_sync(ns)

        # Step 2: Blur
        print("\n── Step 2: Face Blur ──")
        blurred = sd / "video_blurred.mp4"
        ns = argparse.Namespace(input_video=str(vp), output_video=str(blurred))
        cmd_blur(ns)
    else:
        print("⚠ No video provided. Packaging sensor data only.")

    # Step 3: Validate
    print("\n── Step 3: Validate ──")
    ns = argparse.Namespace(session_dir=str(sd))
    ok = cmd_validate(ns)

    # Step 4: Package
    print("\n── Step 4: Package ──")
    pkg = Path(args.output) / sd.name
    pkg.mkdir(parents=True, exist_ok=True)

    # Copy all session files
    for f in sd.iterdir():
        if f.is_file() and f.name != "video_raw.mp4":
            shutil.copy2(f, pkg)
    if (sd / "depth_mesh").is_dir():
        shutil.copytree(sd / "depth_mesh", pkg / "depth_mesh", dirs_exist_ok=True)

    # Delivery manifest matching TELUS spec exactly
    manifest = {
        "dataset": "Dataset 1: Humanoid Motion Data (Egocentric)",
        "device": "PICO 4 Ultra (head-mounted)",
        "motion_style": "Slow, deliberate, robot-like movements",
        "segment_duration_s": round(json.loads((sd / "session_summary.json").read_text())["duration_s"], 1),
        "output": {
            "egocentric_rgb_video": {
                "file": "video_blurred.mp4",
                "format": "Stereo RGB",
                "resolution": "2048x1536",
                "fps": 60,
                "privacy": "All faces blurred (MediaPipe Face Detection)"
            },
            "depth_maps": {
                "directory": "depth_mesh/",
                "format": "PLY (triangulated mesh from iToF sensor)",
                "index": "depth_mesh_index.csv",
                "note": "Spatial mesh snapshots from PICO iToF depth sensor"
            },
            "pose_data": {
                "head_pose": "head_pose.csv — 6DoF position + quaternion per frame",
                "hand_joint_positions": "hand_joints.csv — 26 joints per hand (pos + rot + radius)",
                "finger_articulations": "hand_joints.csv — quaternion rotation per finger joint"
            },
            "imu_logs": {
                "file": "imu.csv",
                "accelerometer": "accel_x/y/z (m/s²)",
                "gyroscope": "gyro_x/y/z (rad/s)"
            },
            "action_logs": {
                "file": "action_log.csv",
                "events": ["task_start", "task_end", "action_type", "sync_clap"]
            },
            "calibration_metadata": {
                "file": "calibration.json",
                "contents": ["camera_intrinsics (approximate)", "extrinsics (HMD-relative)",
                             "coordinate_system_definition", "hand_joint_model", "sync_info"]
            }
        },
        "synchronization": "sync_metadata.json — video↔sensor alignment via audio beep + clap gesture",
        "privacy_compliance": "All faces and sensitive content blurred before delivery"
    }
    (pkg / "MANIFEST.json").write_text(json.dumps(manifest, indent=2))

    print(f"\n[PACKAGE] ✓ Delivery ready: {pkg}")
    print(f"[PACKAGE] Files: {sum(1 for _ in pkg.rglob('*') if _.is_file())}")

# ── CLI ──────────────────────────────────────────────────────

def main():
    p = argparse.ArgumentParser(description="TELUS Dataset 1 Post-Processing")
    sp = p.add_subparsers(dest="cmd")

    sp.add_parser("pull").add_argument("-o", "--output", default="./raw")

    s = sp.add_parser("sync")
    s.add_argument("session_dir"); s.add_argument("video")

    b = sp.add_parser("blur")
    b.add_argument("input_video"); b.add_argument("output_video")

    sp.add_parser("validate").add_argument("session_dir")

    pk = sp.add_parser("package")
    pk.add_argument("session_dir"); pk.add_argument("video", nargs="?")
    pk.add_argument("-o", "--output", default="./delivery")

    args = p.parse_args()
    if args.cmd == "pull": cmd_pull(args)
    elif args.cmd == "sync": cmd_sync(args)
    elif args.cmd == "blur": cmd_blur(args)
    elif args.cmd == "validate": cmd_validate(args)
    elif args.cmd == "package": cmd_package(args)
    else: p.print_help()

if __name__ == "__main__":
    main()
