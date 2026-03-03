#!/usr/bin/env python3
import argparse
import csv
import json
import math
import subprocess
from collections import Counter
from dataclasses import dataclass, asdict
from pathlib import Path


@dataclass
class Check:
    name: str
    status: str  # pass | warn | fail
    detail: str


def fval(v, d=float("nan")):
    try:
        return float(v)
    except Exception:
        return d


def ival(v, d=0):
    try:
        return int(v)
    except Exception:
        return d


def read_csv(path: Path):
    with path.open("r", newline="") as f:
        return list(csv.DictReader(f))


def monotonic_violations(vals):
    bad = 0
    for a, b in zip(vals, vals[1:]):
        if b < a:
            bad += 1
    return bad


def frame_gaps(frames):
    if not frames:
        return 0
    fs = sorted(set(frames))
    missing = 0
    for a, b in zip(fs, fs[1:]):
        if b > a + 1:
            missing += b - a - 1
    return missing


def ffprobe_video(path: Path):
    p = subprocess.run(
        [
            "ffprobe",
            "-v",
            "error",
            "-show_entries",
            "format=duration,size",
            "-show_entries",
            "stream=width,height,avg_frame_rate,bit_rate,codec_name",
            "-of",
            "default=noprint_wrappers=1",
            str(path),
        ],
        capture_output=True,
        text=True,
    )
    if p.returncode != 0:
        return {}
    out = {}
    for ln in p.stdout.splitlines():
        if "=" in ln:
            k, v = ln.split("=", 1)
            out[k.strip()] = v.strip()
    return out


def pick_session(root: Path, session_name: str):
    if session_name:
        s = root / session_name
        if not s.exists():
            raise RuntimeError(f"Session not found: {s}")
        return s
    sessions = [p for p in root.iterdir() if p.is_dir() and p.name.endswith("_capture") and p.name[:8].isdigit()]
    if not sessions:
        raise RuntimeError(f"No capture sessions in {root}")
    sessions.sort(key=lambda p: p.name)
    return sessions[-1]


def audit(session: Path):
    checks = []

    imu = read_csv(session / "imu.csv")
    head = read_csv(session / "head_pose.csv")
    hand = read_csv(session / "hand_joints.csv")
    body = read_csv(session / "body_pose.csv")
    depth = read_csv(session / "depth_mesh" / "depth_mesh_index.csv")
    actions = read_csv(session / "action_log.csv")
    summary = json.load((session / "session_summary.json").open("r"))

    # 1) timestamp monotonicity across streams
    for name, rows in (("imu", imu), ("head", head), ("hand", hand), ("body", body), ("depth", depth), ("actions", actions)):
        ts = [fval(r["ts_s"]) for r in rows]
        bad = monotonic_violations(ts)
        st = "pass" if bad == 0 else "fail"
        checks.append(Check(f"{name}.timestamp_monotonic", st, f"violations={bad}, rows={len(rows)}"))

    # 2) frame continuity
    for name, rows in (("imu", imu), ("head", head), ("body", body)):
        fr = [ival(r["frame"]) for r in rows]
        miss = frame_gaps(fr)
        st = "pass" if miss == 0 else "warn"
        checks.append(Check(f"{name}.frame_gaps", st, f"missing_frames={miss}, frame_min={min(fr)}, frame_max={max(fr)}"))

    # 3) imu quality
    acc = [(fval(r["accel_x"]), fval(r["accel_y"]), fval(r["accel_z"])) for r in imu]
    gyr = [(fval(r["gyro_x"]), fval(r["gyro_y"]), fval(r["gyro_z"])) for r in imu]
    zero_acc = sum(1 for x, y, z in acc if abs(x) < 1e-9 and abs(y) < 1e-9 and abs(z) < 1e-9)
    acc_ratio = zero_acc / max(len(acc), 1)
    if acc_ratio > 0.95:
        checks.append(Check("imu.acceleration_nonzero", "fail", f"all_or_most_accel_zero={zero_acc}/{len(acc)}"))
    else:
        checks.append(Check("imu.acceleration_nonzero", "pass", f"nonzero_accel_rows={len(acc)-zero_acc}/{len(acc)}"))
    gyr_mag = [math.sqrt(x * x + y * y + z * z) for x, y, z in gyr]
    checks.append(Check("imu.gyro_signal_present", "pass" if max(gyr_mag) > 0.05 else "warn", f"gyro_mag_min={min(gyr_mag):.4f}, max={max(gyr_mag):.4f}"))

    # 4) head pose sanity
    qn = []
    for r in head:
        q = (fval(r["rot_x"]), fval(r["rot_y"]), fval(r["rot_z"]), fval(r["rot_w"]))
        qn.append(math.sqrt(sum(v * v for v in q)))
    qdev = max(abs(v - 1.0) for v in qn)
    checks.append(Check("head.quaternion_norm", "pass" if qdev < 0.01 else "warn", f"max_abs_deviation={qdev:.6f}"))
    ts_counts = Counter(ival(r["tracking_state"]) for r in head)
    checks.append(Check("head.tracking_state", "pass" if ts_counts else "warn", f"counts={dict(ts_counts)}"))

    # 5) hand stream completeness
    hand_joints = sorted({r["joint_name"] for r in hand})
    hands = sorted({r["hand"] for r in hand})
    if hand_joints == ["WristFallback"]:
        checks.append(Check("hand.joint_completeness", "fail", "only WristFallback present (no finger joints)"))
    elif len(hand_joints) < 5:
        checks.append(Check("hand.joint_completeness", "warn", f"limited joints={hand_joints}"))
    else:
        checks.append(Check("hand.joint_completeness", "pass", f"joints={len(hand_joints)}"))
    checks.append(Check("hand.left_right_presence", "pass" if {"left", "right"}.issubset(set(hands)) else "warn", f"hands={hands}"))

    # 6) body stream quality
    body_joints = sorted({r["joint_name"] for r in body})
    conf = [fval(r["confidence"]) for r in body]
    conf_mean = sum(conf) / max(len(conf), 1)
    if conf_mean < 0.3:
        st = "warn"
    else:
        st = "pass"
    checks.append(Check("body.joint_count", "pass" if len(body_joints) >= 10 else "warn", f"joints={len(body_joints)} {body_joints}"))
    checks.append(Check("body.mean_confidence", st, f"mean_confidence={conf_mean:.3f}, min={min(conf):.3f}, max={max(conf):.3f}"))

    # 7) depth mesh stream
    verts = [ival(r["verts"]) for r in depth]
    tris = [ival(r["tris"]) for r in depth]
    files_ok = 0
    for r in depth:
        if (session / "depth_mesh" / r["filename"]).exists():
            files_ok += 1
    checks.append(Check("depth.files_exist", "pass" if files_ok == len(depth) else "fail", f"existing={files_ok}/{len(depth)}"))
    checks.append(Check("depth.geometry_nontrivial", "pass" if max(verts) > 500 and max(tris) > 500 else "warn", f"verts_range=[{min(verts)},{max(verts)}], tris_range=[{min(tris)},{max(tris)}]"))

    # 8) video validity and sync
    vid = session / "pov_video.mp4"
    if not vid.exists() or vid.stat().st_size == 0:
        checks.append(Check("video.file_nonzero", "fail", "missing or zero bytes"))
        probe = {}
    else:
        checks.append(Check("video.file_nonzero", "pass", f"size={vid.stat().st_size}"))
        probe = ffprobe_video(vid)

    if probe:
        w = int(probe.get("width", "0") or "0")
        h = int(probe.get("height", "0") or "0")
        br = int(probe.get("bit_rate", "0") or "0")
        vd = float(probe.get("duration", "nan") or "nan")
        checks.append(Check("video.resolution", "pass" if (w >= 1920 and h >= 1080) else "warn", f"{w}x{h}"))
        checks.append(Check("video.bitrate", "pass" if br >= 12_000_000 else "warn", f"bitrate={br}"))

        starts = [fval(r["ts_s"]) for r in actions if r["event_type"] == "video_start" and r.get("metadata") == "ok"]
        stops = [fval(r["ts_s"]) for r in actions if r["event_type"] == "video_stop"]
        if starts and stops and not math.isnan(vd):
            expected = stops[-1] - starts[0]
            delta = vd - expected
            checks.append(Check("video.duration_alignment", "pass" if abs(delta) < 0.2 else "warn", f"expected={expected:.3f}, actual={vd:.3f}, delta={delta:.3f}"))
    else:
        checks.append(Check("video.metadata_probe", "fail", "ffprobe_failed"))

    # 9) event-level reality checks
    has_video_ok = any(r["event_type"] == "video_start" and r.get("metadata") == "ok" for r in actions)
    has_video_saved = any(r["event_type"] == "video_saved" for r in actions)
    checks.append(Check("events.video_start_ok", "pass" if has_video_ok else "fail", f"present={has_video_ok}"))
    checks.append(Check("events.video_saved", "pass" if has_video_saved else "fail", f"present={has_video_saved}"))

    result = {
        "session": session.name,
        "summary": summary,
        "counts": {
            "imu": len(imu),
            "head": len(head),
            "hand": len(hand),
            "body": len(body),
            "depth_mesh_index": len(depth),
            "actions": len(actions),
        },
        "checks": [asdict(c) for c in checks],
    }
    return result


def main():
    ap = argparse.ArgumentParser(description="Reality-check all capture streams.")
    ap.add_argument("--root", default="/home/sarthak/OpenPico4UltraCapture/local_view")
    ap.add_argument("--session", default="", help="session folder name (optional)")
    ap.add_argument("--out", default="", help="output JSON report path (optional)")
    args = ap.parse_args()

    root = Path(args.root)
    session = pick_session(root, args.session)
    result = audit(session)

    if args.out:
        out = Path(args.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(result, indent=2))
        print(str(out))
    else:
        print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
