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
    if not path.exists():
        return []
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
    monotonic_ok = {}
    summary = {}

    imu_path = session / "imu.csv"
    head_path = session / "head_pose.csv"
    hand_path = session / "hand_joints.csv"
    body_path = session / "body_pose.csv"
    depth_path = session / "depth_mesh" / "depth_mesh_index.csv"
    actions_path = session / "action_log.csv"
    summary_path = session / "session_summary.json"

    required_files = (
        ("imu", imu_path),
        ("head_pose", head_path),
        ("hand_joints", hand_path),
        ("body_pose", body_path),
        ("depth_mesh_index", depth_path),
        ("action_log", actions_path),
        ("session_summary", summary_path),
    )
    for name, path in required_files:
        checks.append(Check(f"files.{name}", "pass" if path.exists() else "fail", f"exists={path.exists()}"))

    imu = read_csv(imu_path)
    head = read_csv(head_path)
    hand = read_csv(hand_path)
    body = read_csv(body_path)
    depth = read_csv(depth_path)
    actions = read_csv(actions_path)

    if summary_path.exists():
        try:
            summary = json.load(summary_path.open("r"))
        except Exception as e:
            checks.append(Check("session.summary_parse", "fail", f"error={e}"))
            summary = {}
    else:
        checks.append(Check("session.summary_parse", "fail", "missing session_summary.json"))

    def ts_values(rows):
        out = []
        for r in rows:
            v = fval(r.get("ts_s"))
            if math.isfinite(v):
                out.append(v)
        return out

    def ts_span(rows):
        vals = ts_values(rows)
        if not vals:
            return None
        return min(vals), max(vals)

    # 1) timestamp monotonicity across streams
    for name, rows in (("imu", imu), ("head", head), ("hand", hand), ("body", body), ("depth", depth), ("actions", actions)):
        ts = ts_values(rows)
        if not ts:
            monotonic_ok[name] = False
            checks.append(Check(f"{name}.timestamp_monotonic", "fail", f"rows={len(rows)} valid_ts=0"))
            continue
        bad = monotonic_violations(ts)
        st = "pass" if bad == 0 else "fail"
        monotonic_ok[name] = bad == 0
        checks.append(Check(f"{name}.timestamp_monotonic", st, f"violations={bad}, rows={len(rows)}"))
        if len(ts) >= 2:
            jump = ts[0] - ts[1]
            checks.append(Check(f"{name}.first_timestamp_jump", "fail" if jump > 5.0 else "pass", f"first_minus_second={jump:.3f}s"))

    # 2) frame continuity
    for name, rows in (("imu", imu), ("head", head), ("body", body)):
        fr = [ival(r.get("frame")) for r in rows if "frame" in r]
        if not fr:
            checks.append(Check(f"{name}.frame_gaps", "fail", "no_frames"))
            continue
        miss = frame_gaps(fr)
        st = "pass" if miss == 0 else "warn"
        checks.append(Check(f"{name}.frame_gaps", st, f"missing_frames={miss}, frame_min={min(fr)}, frame_max={max(fr)}"))

    # 3) imu quality
    if imu:
        acc = [(fval(r.get("accel_x")), fval(r.get("accel_y")), fval(r.get("accel_z"))) for r in imu]
        gyr = [(fval(r.get("gyro_x")), fval(r.get("gyro_y")), fval(r.get("gyro_z"))) for r in imu]
        zero_acc = sum(1 for x, y, z in acc if abs(x) < 1e-9 and abs(y) < 1e-9 and abs(z) < 1e-9)
        acc_ratio = zero_acc / max(len(acc), 1)
        if acc_ratio > 0.95:
            checks.append(Check("imu.acceleration_nonzero", "fail", f"all_or_most_accel_zero={zero_acc}/{len(acc)}"))
        else:
            checks.append(Check("imu.acceleration_nonzero", "pass", f"nonzero_accel_rows={len(acc)-zero_acc}/{len(acc)}"))
        gyr_mag = [math.sqrt(x * x + y * y + z * z) for x, y, z in gyr]
        checks.append(Check("imu.gyro_signal_present", "pass" if max(gyr_mag) > 0.05 else "warn", f"gyro_mag_min={min(gyr_mag):.4f}, max={max(gyr_mag):.4f}"))
    else:
        checks.append(Check("imu.acceleration_nonzero", "fail", "missing imu rows"))
        checks.append(Check("imu.gyro_signal_present", "fail", "missing imu rows"))

    # 4) head pose sanity
    if head:
        qn = []
        for r in head:
            q = (fval(r.get("rot_x")), fval(r.get("rot_y")), fval(r.get("rot_z")), fval(r.get("rot_w")))
            qn.append(math.sqrt(sum(v * v for v in q)))
        qdev = max(abs(v - 1.0) for v in qn)
        checks.append(Check("head.quaternion_norm", "pass" if qdev < 0.01 else "warn", f"max_abs_deviation={qdev:.6f}"))
        ts_counts = Counter(ival(r.get("tracking_state")) for r in head)
        checks.append(Check("head.tracking_state", "pass" if ts_counts else "warn", f"counts={dict(ts_counts)}"))
    else:
        checks.append(Check("head.quaternion_norm", "fail", "missing head rows"))
        checks.append(Check("head.tracking_state", "fail", "missing head rows"))

    # 5) hand stream completeness
    if hand:
        hand_joints = sorted({r.get("joint_name", "") for r in hand})
        hands = sorted({r.get("hand", "") for r in hand})
        if hand_joints == ["WristFallback"]:
            checks.append(Check("hand.joint_completeness", "fail", "only WristFallback present (no finger joints)"))
        elif len(hand_joints) < 5:
            checks.append(Check("hand.joint_completeness", "warn", f"limited joints={hand_joints}"))
        else:
            checks.append(Check("hand.joint_completeness", "pass", f"joints={len(hand_joints)}"))
        checks.append(Check("hand.left_right_presence", "pass" if {"left", "right"}.issubset(set(hands)) else "warn", f"hands={hands}"))
    else:
        checks.append(Check("hand.joint_completeness", "fail", "missing hand rows"))
        checks.append(Check("hand.left_right_presence", "fail", "missing hand rows"))

    # 6) body stream quality
    if body:
        body_joints = sorted({r.get("joint_name", "") for r in body})
        conf = [fval(r.get("confidence")) for r in body]
        conf_mean = sum(conf) / max(len(conf), 1)
        checks.append(Check("body.joint_count", "pass" if len(body_joints) >= 10 else "warn", f"joints={len(body_joints)} {body_joints}"))
        checks.append(Check("body.mean_confidence", "pass" if conf_mean >= 0.3 else "warn", f"mean_confidence={conf_mean:.3f}, min={min(conf):.3f}, max={max(conf):.3f}"))
    else:
        checks.append(Check("body.joint_count", "fail", "missing body rows"))
        checks.append(Check("body.mean_confidence", "fail", "missing body rows"))

    head_span = ts_span(head)
    body_span = ts_span(body)
    if not monotonic_ok.get("body", False):
        checks.append(Check("body.coverage_vs_head", "fail", "body timestamps non-monotonic"))
    elif head_span and body_span:
        head_dur = max(0.0, head_span[1] - head_span[0])
        body_dur = max(0.0, body_span[1] - body_span[0])
        ratio = body_dur / max(head_dur, 1e-6)
        if ratio >= 0.80:
            st = "pass"
        elif ratio >= 0.50:
            st = "warn"
        else:
            st = "fail"
        checks.append(Check("body.coverage_vs_head", st, f"body_span={body_dur:.3f}s, head_span={head_dur:.3f}s, ratio={ratio:.3f}"))
    else:
        checks.append(Check("body.coverage_vs_head", "fail", "insufficient head/body timestamps"))

    # 7) depth mesh stream
    if depth:
        verts = [ival(r.get("verts")) for r in depth]
        tris = [ival(r.get("tris")) for r in depth]
        files_ok = 0
        for r in depth:
            if (session / "depth_mesh" / r.get("filename", "")).exists():
                files_ok += 1
        checks.append(Check("depth.files_exist", "pass" if files_ok == len(depth) else "fail", f"existing={files_ok}/{len(depth)}"))
        checks.append(Check("depth.geometry_nontrivial", "pass" if max(verts) > 500 and max(tris) > 500 else "warn", f"verts_range=[{min(verts)},{max(verts)}], tris_range=[{min(tris)},{max(tris)}]"))
    else:
        checks.append(Check("depth.files_exist", "fail", "missing depth index rows"))
        checks.append(Check("depth.geometry_nontrivial", "fail", "missing depth index rows"))

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

        starts = [fval(r.get("ts_s")) for r in actions if r.get("event_type") == "video_start" and "ok" in (r.get("metadata") or "")]
        stops = [fval(r.get("ts_s")) for r in actions if r.get("event_type") == "video_stop" and "ok" in (r.get("metadata") or "")]
        if starts and stops and not math.isnan(vd):
            expected = stops[-1] - starts[0]
            delta = vd - expected
            checks.append(Check("video.duration_alignment", "pass" if abs(delta) < 0.2 else "warn", f"expected={expected:.3f}, actual={vd:.3f}, delta={delta:.3f}"))
        else:
            checks.append(Check("video.duration_alignment", "warn", "missing ok video_start/video_stop events for alignment"))
    else:
        checks.append(Check("video.metadata_probe", "fail", "ffprobe_failed_or_video_missing"))

    # 9) event-level reality checks
    video_start_meta = [(r.get("metadata") or "") for r in actions if r.get("event_type") == "video_start"]
    has_video_ok = any("ok" in m for m in video_start_meta)
    has_video_pending = any("pending" in m for m in video_start_meta)
    has_video_saved = any(r.get("event_type") == "video_saved" for r in actions)
    if has_video_ok:
        start_status = "pass"
    elif has_video_pending:
        start_status = "warn"
    else:
        start_status = "fail"
    checks.append(Check("events.video_start_ok", start_status, f"metadata={video_start_meta if video_start_meta else []}"))
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
