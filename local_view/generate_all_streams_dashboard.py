#!/usr/bin/env python3
import argparse
import csv
import html
import json
import math
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np


def fval(value, default=np.nan):
    try:
        return float(value)
    except Exception:
        return default


def ival(value, default=0):
    try:
        return int(value)
    except Exception:
        return default


def read_csv_rows(path):
    with path.open("r", newline="") as f:
        return list(csv.DictReader(f))


def read_json(path):
    with path.open("r") as f:
        return json.load(f)


def save_fig(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    plt.tight_layout()
    plt.savefig(path, dpi=150)
    plt.close()


def latest_session(local_view_root: Path) -> Path:
    sessions = []
    for p in local_view_root.iterdir():
        if not p.is_dir():
            continue
        name = p.name
        if name.startswith("dashboard_"):
            continue
        if not name.endswith("_capture"):
            continue
        if len(name) < 16 or not name[:8].isdigit():
            continue
        sessions.append(p)
    if not sessions:
        raise RuntimeError(f"No *_capture folders found in {local_view_root}")
    sessions.sort(key=lambda p: p.name)
    return sessions[-1]


def plot_imu(rows, out_png: Path):
    t = np.array([fval(r["ts_s"]) for r in rows], dtype=float)
    ax = np.array([fval(r["accel_x"]) for r in rows], dtype=float)
    ay = np.array([fval(r["accel_y"]) for r in rows], dtype=float)
    az = np.array([fval(r["accel_z"]) for r in rows], dtype=float)
    gx = np.array([fval(r["gyro_x"]) for r in rows], dtype=float)
    gy = np.array([fval(r["gyro_y"]) for r in rows], dtype=float)
    gz = np.array([fval(r["gyro_z"]) for r in rows], dtype=float)

    plt.figure(figsize=(12, 7))
    plt.subplot(2, 1, 1)
    plt.plot(t, ax, label="accel_x")
    plt.plot(t, ay, label="accel_y")
    plt.plot(t, az, label="accel_z")
    plt.title("IMU Stream")
    plt.ylabel("Acceleration")
    plt.grid(True, alpha=0.3)
    plt.legend()

    plt.subplot(2, 1, 2)
    plt.plot(t, gx, label="gyro_x")
    plt.plot(t, gy, label="gyro_y")
    plt.plot(t, gz, label="gyro_z")
    plt.xlabel("Time (s)")
    plt.ylabel("Gyroscope")
    plt.grid(True, alpha=0.3)
    plt.legend()
    save_fig(out_png)


def plot_head(rows, out_pos_png: Path, out_rot_png: Path, out_3d_png: Path):
    t = np.array([fval(r["ts_s"]) for r in rows], dtype=float)
    px = np.array([fval(r["pos_x"]) for r in rows], dtype=float)
    py = np.array([fval(r["pos_y"]) for r in rows], dtype=float)
    pz = np.array([fval(r["pos_z"]) for r in rows], dtype=float)
    rx = np.array([fval(r["rot_x"]) for r in rows], dtype=float)
    ry = np.array([fval(r["rot_y"]) for r in rows], dtype=float)
    rz = np.array([fval(r["rot_z"]) for r in rows], dtype=float)
    rw = np.array([fval(r["rot_w"]) for r in rows], dtype=float)
    ts = np.array([ival(r["tracking_state"]) for r in rows], dtype=int)

    plt.figure(figsize=(12, 7))
    plt.subplot(3, 1, 1)
    plt.plot(t, px, label="pos_x")
    plt.plot(t, py, label="pos_y")
    plt.plot(t, pz, label="pos_z")
    plt.title("Head Pose Position")
    plt.ylabel("Position (m)")
    plt.grid(True, alpha=0.3)
    plt.legend()

    speed = np.zeros_like(px)
    if len(t) > 1:
        dt = np.maximum(np.diff(t), 1e-6)
        dist = np.sqrt(np.diff(px) ** 2 + np.diff(py) ** 2 + np.diff(pz) ** 2)
        speed[1:] = dist / dt
    plt.subplot(3, 1, 2)
    plt.plot(t, speed, color="tab:orange", label="head_speed_mps")
    plt.ylabel("Speed (m/s)")
    plt.grid(True, alpha=0.3)
    plt.legend()

    plt.subplot(3, 1, 3)
    plt.plot(t, ts, color="tab:green", label="tracking_state")
    plt.xlabel("Time (s)")
    plt.ylabel("Tracking state")
    plt.grid(True, alpha=0.3)
    plt.legend()
    save_fig(out_pos_png)

    plt.figure(figsize=(12, 6))
    plt.plot(t, rx, label="rot_x")
    plt.plot(t, ry, label="rot_y")
    plt.plot(t, rz, label="rot_z")
    plt.plot(t, rw, label="rot_w")
    plt.title("Head Pose Rotation Quaternion")
    plt.xlabel("Time (s)")
    plt.ylabel("Quaternion")
    plt.grid(True, alpha=0.3)
    plt.legend()
    save_fig(out_rot_png)

    fig = plt.figure(figsize=(8, 7))
    ax3 = fig.add_subplot(111, projection="3d")
    ax3.plot(px, py, pz, color="tab:blue", linewidth=1.0)
    ax3.scatter(px[0], py[0], pz[0], color="green", label="start", s=24)
    ax3.scatter(px[-1], py[-1], pz[-1], color="red", label="end", s=24)
    ax3.set_title("Head 3D Trajectory")
    ax3.set_xlabel("X")
    ax3.set_ylabel("Y")
    ax3.set_zlabel("Z")
    ax3.legend()
    save_fig(out_3d_png)


def split_hand_wrist(rows):
    out = {"left": {"t": [], "x": [], "y": [], "z": [], "radius": [], "frame": []},
           "right": {"t": [], "x": [], "y": [], "z": [], "radius": [], "frame": []}}
    for r in rows:
        hand = (r.get("hand") or "").strip().lower()
        if hand not in out:
            continue
        if r.get("joint_name", "") not in ("WristFallback", "Wrist"):
            continue
        out[hand]["t"].append(fval(r["ts_s"]))
        out[hand]["frame"].append(ival(r["frame"]))
        out[hand]["x"].append(fval(r["pos_x"]))
        out[hand]["y"].append(fval(r["pos_y"]))
        out[hand]["z"].append(fval(r["pos_z"]))
        out[hand]["radius"].append(fval(r["radius"]))
    return out


def plot_hand(rows, out_pos_png: Path, out_dist_png: Path):
    hw = split_hand_wrist(rows)

    plt.figure(figsize=(12, 8))
    for i, axis in enumerate(("x", "y", "z", "radius"), start=1):
        plt.subplot(4, 1, i)
        for hand, color in (("left", "tab:blue"), ("right", "tab:orange")):
            t = np.array(hw[hand]["t"], dtype=float)
            v = np.array(hw[hand][axis], dtype=float)
            if t.size > 0:
                plt.plot(t, v, label=f"{hand}_{axis}", color=color, alpha=0.9)
        plt.ylabel(axis)
        plt.grid(True, alpha=0.3)
        if i == 1:
            plt.title("Hand Joint Stream (Wrist Fallback)")
        if i == 4:
            plt.xlabel("Time (s)")
        plt.legend()
    save_fig(out_pos_png)

    left_idx = {f: i for i, f in enumerate(hw["left"]["frame"])}
    dist_t = []
    dist_v = []
    for i, f in enumerate(hw["right"]["frame"]):
        li = left_idx.get(f)
        if li is None:
            continue
        lx, ly, lz = hw["left"]["x"][li], hw["left"]["y"][li], hw["left"]["z"][li]
        rx, ry, rz = hw["right"]["x"][i], hw["right"]["y"][i], hw["right"]["z"][i]
        dist_t.append(hw["right"]["t"][i])
        dist_v.append(math.sqrt((lx - rx) ** 2 + (ly - ry) ** 2 + (lz - rz) ** 2))

    plt.figure(figsize=(12, 4))
    if dist_t:
        plt.plot(dist_t, dist_v, color="tab:purple")
    plt.title("Left-Right Wrist Distance")
    plt.xlabel("Time (s)")
    plt.ylabel("Distance (m)")
    plt.grid(True, alpha=0.3)
    save_fig(out_dist_png)


def plot_body(rows, out_pos_png: Path, out_conf_png: Path, out_3d_png: Path):
    selected = ["Pelvis", "Head", "LeftHand", "RightHand", "LeftWrist", "RightWrist"]
    by_joint = {}
    all_joints = sorted({r["joint_name"] for r in rows})
    for j in all_joints:
        by_joint[j] = {"t": [], "x": [], "y": [], "z": [], "conf": [], "frame": []}

    for r in rows:
        j = r["joint_name"]
        by_joint[j]["t"].append(fval(r["ts_s"]))
        by_joint[j]["frame"].append(ival(r["frame"]))
        by_joint[j]["x"].append(fval(r["pos_x"]))
        by_joint[j]["y"].append(fval(r["pos_y"]))
        by_joint[j]["z"].append(fval(r["pos_z"]))
        by_joint[j]["conf"].append(fval(r["confidence"]))

    plt.figure(figsize=(12, 8))
    for i, axis in enumerate(("x", "y", "z"), start=1):
        plt.subplot(3, 1, i)
        for j in selected:
            if j not in by_joint or not by_joint[j]["t"]:
                continue
            t = np.array(by_joint[j]["t"], dtype=float)
            v = np.array(by_joint[j][axis], dtype=float)
            plt.plot(t, v, label=j, linewidth=1.0)
        plt.grid(True, alpha=0.3)
        plt.ylabel(f"pos_{axis}")
        if i == 1:
            plt.title("Body Joint Positions")
        if i == 3:
            plt.xlabel("Time (s)")
            plt.legend(ncol=3, fontsize=8)
    save_fig(out_pos_png)

    frames = sorted({ival(r["frame"]) for r in rows})
    joints = all_joints
    frame_to_idx = {f: i for i, f in enumerate(frames)}
    joint_to_idx = {j: i for i, j in enumerate(joints)}
    heat = np.full((len(joints), len(frames)), np.nan, dtype=float)
    for r in rows:
        ji = joint_to_idx[r["joint_name"]]
        fi = frame_to_idx[ival(r["frame"])]
        heat[ji, fi] = fval(r["confidence"])

    plt.figure(figsize=(13, 4.8))
    plt.imshow(heat, aspect="auto", interpolation="nearest", cmap="viridis", vmin=0.0, vmax=1.0)
    plt.colorbar(label="Confidence")
    plt.yticks(np.arange(len(joints)), joints)
    x_ticks = np.linspace(0, len(frames) - 1, num=min(10, len(frames))).astype(int)
    x_labels = [str(frames[i]) for i in x_ticks]
    plt.xticks(x_ticks, x_labels)
    plt.xlabel("Frame")
    plt.ylabel("Joint")
    plt.title("Body Pose Confidence Heatmap")
    save_fig(out_conf_png)

    fig = plt.figure(figsize=(8, 7))
    ax3 = fig.add_subplot(111, projection="3d")
    for j, c in (("Pelvis", "tab:blue"), ("Head", "tab:orange"), ("LeftHand", "tab:green"), ("RightHand", "tab:red")):
        if j in by_joint and by_joint[j]["x"]:
            ax3.plot(by_joint[j]["x"], by_joint[j]["y"], by_joint[j]["z"], label=j, color=c, linewidth=1.0)
    ax3.set_title("Body 3D Trajectories")
    ax3.set_xlabel("X")
    ax3.set_ylabel("Y")
    ax3.set_zlabel("Z")
    ax3.legend()
    save_fig(out_3d_png)


def parse_ascii_ply_vertices(path: Path, max_points=3000):
    with path.open("r") as f:
        lines = f.readlines()
    vertex_count = 0
    header_end = None
    for i, ln in enumerate(lines):
        if ln.startswith("element vertex"):
            vertex_count = int(ln.strip().split()[-1])
        if ln.strip() == "end_header":
            header_end = i + 1
            break
    if header_end is None or vertex_count <= 0:
        return np.zeros((0, 3), dtype=float)

    verts = []
    for ln in lines[header_end: header_end + vertex_count]:
        parts = ln.strip().split()
        if len(parts) < 3:
            continue
        verts.append((fval(parts[0]), fval(parts[1]), fval(parts[2])))
    verts = np.array(verts, dtype=float)
    if len(verts) > max_points:
        idx = np.linspace(0, len(verts) - 1, max_points).astype(int)
        verts = verts[idx]
    return verts


def plot_depth(rows, mesh_dir: Path, out_stats_png: Path, out_cloud_png: Path):
    t = np.array([fval(r["ts_s"]) for r in rows], dtype=float)
    verts = np.array([ival(r["verts"]) for r in rows], dtype=float)
    tris = np.array([ival(r["tris"]) for r in rows], dtype=float)
    snap = np.array([ival(r["snapshot"]) for r in rows], dtype=int)

    plt.figure(figsize=(12, 6))
    plt.subplot(2, 1, 1)
    plt.plot(t, verts, label="verts")
    plt.plot(t, tris, label="tris")
    plt.title("Depth Mesh Stream")
    plt.ylabel("Count")
    plt.grid(True, alpha=0.3)
    plt.legend()

    plt.subplot(2, 1, 2)
    plt.plot(t, snap, label="snapshot", color="tab:purple")
    plt.xlabel("Time (s)")
    plt.ylabel("Snapshot index")
    plt.grid(True, alpha=0.3)
    plt.legend()
    save_fig(out_stats_png)

    mesh_files = sorted(mesh_dir.glob("mesh_*.ply"))
    verts3 = np.zeros((0, 3), dtype=float)
    if mesh_files:
        verts3 = parse_ascii_ply_vertices(mesh_files[0])

    fig = plt.figure(figsize=(8, 7))
    ax3 = fig.add_subplot(111, projection="3d")
    if len(verts3) > 0:
        ax3.scatter(verts3[:, 0], verts3[:, 1], verts3[:, 2], c=verts3[:, 2], s=1, cmap="viridis")
    ax3.set_title("Depth Mesh Point Cloud Preview (first snapshot)")
    ax3.set_xlabel("X")
    ax3.set_ylabel("Y")
    ax3.set_zlabel("Z")
    save_fig(out_cloud_png)


def plot_action(rows, out_timeline_png: Path, out_counts_png: Path):
    event_types = sorted({r["event_type"] for r in rows})
    idx = {e: i for i, e in enumerate(event_types)}
    t = np.array([fval(r["ts_s"]) for r in rows], dtype=float)
    y = np.array([idx[r["event_type"]] for r in rows], dtype=int)

    plt.figure(figsize=(12, 5))
    plt.scatter(t, y, c=y, cmap="tab10", s=55)
    for i, r in enumerate(rows):
        plt.text(t[i], y[i] + 0.06, r["action_type"], fontsize=7, alpha=0.75)
    plt.yticks(np.arange(len(event_types)), event_types)
    plt.xlabel("Time (s)")
    plt.ylabel("Event type")
    plt.title("Action/Event Timeline")
    plt.grid(True, alpha=0.3)
    save_fig(out_timeline_png)

    counts = {e: 0 for e in event_types}
    for r in rows:
        counts[r["event_type"]] += 1
    xs = list(counts.keys())
    ys = [counts[x] for x in xs]
    plt.figure(figsize=(12, 4))
    plt.bar(xs, ys)
    plt.xticks(rotation=25, ha="right")
    plt.ylabel("Count")
    plt.title("Action/Event Counts")
    plt.grid(True, axis="y", alpha=0.3)
    save_fig(out_counts_png)


def html_table_preview(csv_path: Path, max_rows=20):
    with csv_path.open("r", newline="") as f:
        rows = list(csv.reader(f))
    if not rows:
        return "<p><i>empty</i></p>"
    head = rows[0]
    body = rows[1:max_rows + 1]
    out = ["<table><tr>" + "".join(f"<th>{html.escape(c)}</th>" for c in head) + "</tr>"]
    for r in body:
        out.append("<tr>" + "".join(f"<td>{html.escape(c)}</td>" for c in r) + "</tr>")
    out.append("</table>")
    return "\n".join(out)


def generate_dashboard(session_dir: Path):
    root = session_dir.parent
    session_name = session_dir.name
    out_dir = root / f"dashboard_{session_name}"
    assets = out_dir / "assets"
    assets.mkdir(parents=True, exist_ok=True)

    imu_rows = read_csv_rows(session_dir / "imu.csv")
    head_rows = read_csv_rows(session_dir / "head_pose.csv")
    hand_rows = read_csv_rows(session_dir / "hand_joints.csv")
    body_rows = read_csv_rows(session_dir / "body_pose.csv")
    depth_rows = read_csv_rows(session_dir / "depth_mesh" / "depth_mesh_index.csv")
    action_rows = read_csv_rows(session_dir / "action_log.csv")
    summary = read_json(session_dir / "session_summary.json")
    calib = read_json(session_dir / "calibration.json")

    plot_imu(imu_rows, assets / "imu.png")
    plot_head(head_rows, assets / "head_position.png", assets / "head_rotation.png", assets / "head_3d.png")
    plot_hand(hand_rows, assets / "hand_wrist.png", assets / "hand_distance.png")
    plot_body(body_rows, assets / "body_position.png", assets / "body_confidence_heatmap.png", assets / "body_3d.png")
    plot_depth(depth_rows, session_dir / "depth_mesh", assets / "depth_stats.png", assets / "depth_cloud.png")
    plot_action(action_rows, assets / "action_timeline.png", assets / "action_counts.png")

    files = [
        "pov_video.mp4",
        "imu.csv",
        "head_pose.csv",
        "hand_joints.csv",
        "body_pose.csv",
        "depth_mesh/depth_mesh_index.csv",
        "action_log.csv",
        "session_summary.json",
        "calibration.json",
    ]
    file_links = "\n".join(
        f'<li><a href="../{session_name}/{f}" target="_blank">{f}</a></li>' for f in files
    )

    html_path = out_dir / "index.html"
    html_text = f"""<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>All Streams Dashboard - {session_name}</title>
  <style>
    body {{ font-family: Arial, sans-serif; margin: 24px; background: #0f1115; color: #e7ebf3; }}
    .card {{ background: #171b24; border: 1px solid #2a3243; border-radius: 10px; padding: 16px; margin-bottom: 16px; }}
    img {{ max-width: 100%; border: 1px solid #2a3243; border-radius: 8px; background: #101622; }}
    video {{ width: 100%; max-width: 980px; border-radius: 8px; border: 1px solid #2a3243; background: black; }}
    pre {{ background: #0d121b; border: 1px solid #2a3243; border-radius: 8px; padding: 10px; overflow: auto; }}
    table {{ border-collapse: collapse; width: 100%; overflow: auto; display: block; }}
    th, td {{ border: 1px solid #2a3243; padding: 6px; text-align: left; white-space: nowrap; }}
    th {{ background: #1f2735; }}
    a {{ color: #7dbbff; }}
  </style>
</head>
<body>
  <h1>All Data Streams Dashboard</h1>
  <div class="card">
    <p><b>Session:</b> {session_name}</p>
    <p><b>Duration:</b> {summary.get("duration_s")} s | <b>Total frames:</b> {summary.get("total_frames")} | <b>FPS:</b> {summary.get("actual_fps")}</p>
    <p><a href="../{session_name}/" target="_blank">Open raw session folder</a></p>
  </div>

  <div class="card">
    <h2>POV Video Stream</h2>
    <video controls>
      <source src="../{session_name}/pov_video.mp4" type="video/mp4" />
    </video>
  </div>

  <div class="card"><h2>IMU Stream</h2><img src="assets/imu.png" alt="imu" /></div>

  <div class="card">
    <h2>Head Pose Stream</h2>
    <img src="assets/head_position.png" alt="head_position" />
    <img src="assets/head_rotation.png" alt="head_rotation" />
    <img src="assets/head_3d.png" alt="head_3d" />
  </div>

  <div class="card">
    <h2>Hand Joint Stream</h2>
    <img src="assets/hand_wrist.png" alt="hand_wrist" />
    <img src="assets/hand_distance.png" alt="hand_distance" />
  </div>

  <div class="card">
    <h2>Body Pose Stream</h2>
    <img src="assets/body_position.png" alt="body_position" />
    <img src="assets/body_confidence_heatmap.png" alt="body_confidence_heatmap" />
    <img src="assets/body_3d.png" alt="body_3d" />
  </div>

  <div class="card">
    <h2>Depth Mesh Stream</h2>
    <img src="assets/depth_stats.png" alt="depth_stats" />
    <img src="assets/depth_cloud.png" alt="depth_cloud" />
  </div>

  <div class="card">
    <h2>Action Log Stream</h2>
    <img src="assets/action_timeline.png" alt="action_timeline" />
    <img src="assets/action_counts.png" alt="action_counts" />
  </div>

  <div class="card">
    <h2>File Links (All Streams)</h2>
    <ul>
      {file_links}
    </ul>
  </div>

  <div class="card">
    <h2>Session Summary JSON</h2>
    <pre>{html.escape(json.dumps(summary, indent=2))}</pre>
  </div>

  <div class="card">
    <h2>Calibration JSON</h2>
    <pre>{html.escape(json.dumps(calib, indent=2))}</pre>
  </div>

  <div class="card">
    <h2>CSV Previews</h2>
    <h3>imu.csv</h3>{html_table_preview(session_dir / "imu.csv")}
    <h3>head_pose.csv</h3>{html_table_preview(session_dir / "head_pose.csv")}
    <h3>hand_joints.csv</h3>{html_table_preview(session_dir / "hand_joints.csv")}
    <h3>body_pose.csv</h3>{html_table_preview(session_dir / "body_pose.csv")}
    <h3>depth_mesh_index.csv</h3>{html_table_preview(session_dir / "depth_mesh" / "depth_mesh_index.csv")}
    <h3>action_log.csv</h3>{html_table_preview(session_dir / "action_log.csv")}
  </div>
</body>
</html>
"""
    html_path.write_text(html_text)
    return html_path


def main():
    parser = argparse.ArgumentParser(description="Generate all-stream dashboard for a capture session.")
    parser.add_argument("--local-view-root", default=str(Path(__file__).resolve().parent))
    parser.add_argument("--session", default="", help="Session folder name (e.g., 20260302_131912_capture)")
    args = parser.parse_args()

    root = Path(args.local_view_root).resolve()
    if args.session:
        session_dir = root / args.session
    else:
        session_dir = latest_session(root)
    if not session_dir.exists():
        raise RuntimeError(f"Session directory not found: {session_dir}")

    html_path = generate_dashboard(session_dir)
    print(str(html_path))


if __name__ == "__main__":
    main()
