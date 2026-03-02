#!/usr/bin/env python3
import argparse
import csv
import json
import math
from pathlib import Path


def to_f(v, default=0.0):
    try:
        return float(v)
    except Exception:
        return default


def read_csv(path: Path):
    with path.open("r", newline="") as f:
        return list(csv.DictReader(f))


def trim_to_main_segment(ts, *cols):
    if len(ts) < 3:
        return (ts, *cols)

    gaps = [ts[i + 1] - ts[i] for i in range(len(ts) - 1)]
    pos_gaps = [g for g in gaps if g > 0]
    if not pos_gaps:
        return (ts, *cols)

    med_gap = sorted(pos_gaps)[len(pos_gaps) // 2]
    big_gap = max(gaps)
    big_i = gaps.index(big_gap)

    # If a gap is far larger than the nominal sampling interval,
    # keep the larger contiguous segment and drop the outlier segment.
    if big_gap <= max(1.0, med_gap * 40.0):
        return (ts, *cols)

    left_len = big_i + 1
    right_len = len(ts) - left_len
    if left_len >= right_len:
        start, end = 0, left_len
    else:
        start, end = left_len, len(ts)

    out = [ts[start:end]]
    for c in cols:
        out.append(c[start:end])
    return tuple(out)


def nearest_index(ts, t):
    if not ts:
        return -1
    lo, hi = 0, len(ts) - 1
    while lo < hi:
        mid = (lo + hi) // 2
        if ts[mid] < t:
            lo = mid + 1
        else:
            hi = mid
    i = lo
    if i > 0 and abs(ts[i - 1] - t) <= abs(ts[i] - t):
        i -= 1
    return i


def build_data(session_dir: Path):
    imu_rows = read_csv(session_dir / "imu.csv")
    head_rows = read_csv(session_dir / "head_pose.csv")
    hand_rows = read_csv(session_dir / "hand_joints.csv")
    body_rows = read_csv(session_dir / "body_pose.csv")
    depth_rows = read_csv(session_dir / "depth_mesh" / "depth_mesh_index.csv")
    action_rows = read_csv(session_dir / "action_log.csv")

    summary_path = session_dir / "session_summary.json"
    summary = json.loads(summary_path.read_text()) if summary_path.exists() else {}

    video_path = session_dir / "pov_video.mp4"
    has_video = video_path.exists() and video_path.stat().st_size > 0

    video_start_ts = None
    for r in action_rows:
        evt = (r.get("event_type") or "").strip()
        meta = (r.get("metadata") or "").strip()
        if evt == "video_start" and ("ok" in meta):
            video_start_ts = to_f(r.get("ts_s"))
            break
    if video_start_ts is None:
        video_start_ts = to_f(imu_rows[0]["ts_s"]) if imu_rows else 0.0

    imu_ts, accel_mag, gyro_mag = [], [], []
    for r in imu_rows:
        t = to_f(r["ts_s"])
        ax, ay, az = to_f(r["accel_x"]), to_f(r["accel_y"]), to_f(r["accel_z"])
        gx, gy, gz = to_f(r["gyro_x"]), to_f(r["gyro_y"]), to_f(r["gyro_z"])
        imu_ts.append(t)
        accel_mag.append(math.sqrt(ax * ax + ay * ay + az * az))
        gyro_mag.append(math.sqrt(gx * gx + gy * gy + gz * gz))
    imu_ts, accel_mag, gyro_mag = trim_to_main_segment(imu_ts, accel_mag, gyro_mag)

    head_ts, px, py, pz = [], [], [], []
    for r in head_rows:
        head_ts.append(to_f(r["ts_s"]))
        px.append(to_f(r["pos_x"]))
        py.append(to_f(r["pos_y"]))
        pz.append(to_f(r["pos_z"]))
    head_ts, px, py, pz = trim_to_main_segment(head_ts, px, py, pz)

    wrist_by_ts = {}
    for r in hand_rows:
        if (r.get("joint_name") or "") != "Wrist":
            continue
        t = to_f(r["ts_s"])
        hand = (r.get("hand") or "").lower()
        wrist_by_ts.setdefault(t, {})[hand] = (
            to_f(r["pos_x"]),
            to_f(r["pos_y"]),
            to_f(r["pos_z"]),
        )
    hand_ts, wrist_dist = [], []
    for t in sorted(wrist_by_ts.keys()):
        d = None
        p = wrist_by_ts[t]
        if "left" in p and "right" in p:
            dx = p["left"][0] - p["right"][0]
            dy = p["left"][1] - p["right"][1]
            dz = p["left"][2] - p["right"][2]
            d = math.sqrt(dx * dx + dy * dy + dz * dz)
        hand_ts.append(t)
        wrist_dist.append(d)
    hand_ts, wrist_dist = trim_to_main_segment(hand_ts, wrist_dist)

    body_acc = {}
    for r in body_rows:
        t = to_f(r["ts_s"])
        c = to_f(r["confidence"])
        s, n = body_acc.get(t, (0.0, 0))
        body_acc[t] = (s + c, n + 1)
    body_ts = sorted(body_acc.keys())
    body_conf = [body_acc[t][0] / max(body_acc[t][1], 1) for t in body_ts]
    body_ts, body_conf = trim_to_main_segment(body_ts, body_conf)

    depth_ts, depth_verts, depth_tris = [], [], []
    for r in depth_rows:
        depth_ts.append(to_f(r["ts_s"]))
        depth_verts.append(to_f(r["verts"]))
        depth_tris.append(to_f(r["tris"]))
    depth_ts, depth_verts, depth_tris = trim_to_main_segment(depth_ts, depth_verts, depth_tris)

    ts_starts = [arr[0] for arr in [imu_ts, head_ts, hand_ts, body_ts, depth_ts] if arr]
    ts_ends = [arr[-1] for arr in [imu_ts, head_ts, hand_ts, body_ts, depth_ts] if arr]
    session_min_ts = min(ts_starts) if ts_starts else 0.0
    session_max_ts = max(ts_ends) if ts_ends else max(video_start_ts, 1.0)

    duration_s = to_f(summary.get("duration_s"), 0.0)
    if duration_s <= 0.0:
        duration_s = max(0.0, session_max_ts - session_min_ts)

    return {
        "session": session_dir.name,
        "video_rel": f"../{session_dir.name}/pov_video.mp4",
        "has_video": has_video,
        "video_start_ts": video_start_ts,
        "duration_s": duration_s,
        "session_min_ts": session_min_ts,
        "session_max_ts": session_max_ts,
        "target_fps": summary.get("target_fps"),
        "actual_fps": summary.get("actual_fps"),
        "streams": {
            "imu": {"ts": imu_ts, "accel_mag": accel_mag, "gyro_mag": gyro_mag},
            "head": {"ts": head_ts, "x": px, "y": py, "z": pz},
            "hand": {"ts": hand_ts, "wrist_dist": wrist_dist},
            "body": {"ts": body_ts, "mean_conf": body_conf},
            "depth": {"ts": depth_ts, "verts": depth_verts, "tris": depth_tris},
        },
    }


def write_html(out_path: Path, data):
    data_json = json.dumps(data, separators=(",", ":"))
    html = f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Synced Multi-Stream Viewer - {data["session"]}</title>
  <style>
    :root {{
      --bg:#0d1117; --panel:#161b22; --border:#30363d; --text:#e6edf3; --muted:#9da7b3;
      --accent:#58a6ff; --ok:#3fb950; --warn:#f2cc60; --danger:#f85149;
    }}
    body {{ margin:0; font-family: ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, sans-serif; background:var(--bg); color:var(--text); }}
    .wrap {{ padding:14px; display:grid; gap:12px; }}
    .top {{ display:grid; grid-template-columns: 1.2fr 1fr; gap:12px; }}
    .panel {{ background:var(--panel); border:1px solid var(--border); border-radius:10px; padding:10px; }}
    h1 {{ font-size:18px; margin:0 0 10px 0; }}
    h2 {{ font-size:14px; margin:0 0 8px 0; color:var(--muted); }}
    video {{ width:100%; border-radius:8px; background:#000; border:1px solid #000; }}
    .meta {{ display:grid; grid-template-columns: repeat(2, minmax(0,1fr)); gap:8px; font-size:13px; }}
    .k {{ color:var(--muted); }} .v {{ font-weight:600; }}
    .grid {{ display:grid; grid-template-columns: 1fr 1fr; gap:12px; }}
    canvas {{ width:100%; height:170px; border:1px solid var(--border); border-radius:8px; background:#0b0f15; }}
    .legend {{ font-size:12px; color:var(--muted); display:flex; gap:14px; flex-wrap:wrap; margin-bottom:6px; }}
    .dot {{ display:inline-block; width:10px; height:10px; border-radius:50%; margin-right:6px; }}
    .footer {{ font-size:12px; color:var(--muted); }}
    .controls {{ margin-top:8px; display:flex; align-items:center; gap:8px; flex-wrap:wrap; }}
    .controls button {{ background:#21262d; color:var(--text); border:1px solid var(--border); border-radius:6px; padding:4px 8px; cursor:pointer; }}
    .controls button:hover {{ border-color:#4f627b; }}
    .controls input[type=range] {{ width: min(560px, 95%); }}
    .mode {{ font-size:12px; color:var(--muted); }}
    @media (max-width: 1100px) {{
      .top {{ grid-template-columns: 1fr; }}
      .grid {{ grid-template-columns: 1fr; }}
    }}
  </style>
</head>
<body>
  <div class="wrap">
    <div class="top">
      <div class="panel">
        <h1>Synced Video + Sensor Streams</h1>
        <video id="video" controls preload="metadata">
          <source src="{data["video_rel"]}" type="video/mp4" />
        </video>
        <div class="controls">
          <button id="btn_play">Play Streams</button>
          <button id="btn_pause">Pause</button>
          <button id="btn_video">Use Video Clock</button>
          <input id="timeline" type="range" min="{data["session_min_ts"]:.3f}" max="{data["session_max_ts"]:.3f}" step="0.001" value="{data["video_start_ts"]:.3f}" />
          <span class="mode" id="clock_mode"></span>
        </div>
      </div>
      <div class="panel">
        <h2>Session Status</h2>
        <div class="meta">
          <div><span class="k">Session</span><br><span class="v">{data["session"]}</span></div>
          <div><span class="k">Session Time</span><br><span class="v" id="t_session">0.000s</span></div>
          <div><span class="k">Video Time</span><br><span class="v" id="t_video">0.000s</span></div>
          <div><span class="k">Video Offset</span><br><span class="v">{data["video_start_ts"]:.3f}s</span></div>
          <div><span class="k">Target FPS</span><br><span class="v">{data.get("target_fps", "n/a")}</span></div>
          <div><span class="k">Actual FPS</span><br><span class="v">{data.get("actual_fps", "n/a")}</span></div>
        </div>
        <hr style="border:0;border-top:1px solid var(--border);margin:10px 0" />
        <div class="meta">
          <div><span class="k">IMU accel mag</span><br><span class="v" id="v_accel">-</span></div>
          <div><span class="k">IMU gyro mag</span><br><span class="v" id="v_gyro">-</span></div>
          <div><span class="k">Head pos (x,y,z)</span><br><span class="v" id="v_head">-</span></div>
          <div><span class="k">L-R wrist dist</span><br><span class="v" id="v_wrist">-</span></div>
          <div><span class="k">Body mean conf</span><br><span class="v" id="v_body">-</span></div>
          <div><span class="k">Depth (verts,tris)</span><br><span class="v" id="v_depth">-</span></div>
        </div>
      </div>
    </div>

    <div class="grid">
      <div class="panel">
        <h2>IMU Magnitudes</h2>
        <div class="legend">
          <span><span class="dot" style="background:#58a6ff"></span>accel magnitude</span>
          <span><span class="dot" style="background:#f2cc60"></span>gyro magnitude</span>
        </div>
        <canvas id="c_imu" width="900" height="170"></canvas>
      </div>
      <div class="panel">
        <h2>Head Position</h2>
        <div class="legend">
          <span><span class="dot" style="background:#58a6ff"></span>x</span>
          <span><span class="dot" style="background:#3fb950"></span>y</span>
          <span><span class="dot" style="background:#f85149"></span>z</span>
        </div>
        <canvas id="c_head" width="900" height="170"></canvas>
      </div>
      <div class="panel">
        <h2>Hand Stream</h2>
        <div class="legend">
          <span><span class="dot" style="background:#a371f7"></span>left-right wrist distance</span>
        </div>
        <canvas id="c_hand" width="900" height="170"></canvas>
      </div>
      <div class="panel">
        <h2>Body Stream</h2>
        <div class="legend">
          <span><span class="dot" style="background:#3fb950"></span>mean confidence</span>
        </div>
        <canvas id="c_body" width="900" height="170"></canvas>
      </div>
      <div class="panel">
        <h2>Depth Mesh Stream</h2>
        <div class="legend">
          <span><span class="dot" style="background:#58a6ff"></span>vertices</span>
          <span><span class="dot" style="background:#f2cc60"></span>triangles</span>
        </div>
        <canvas id="c_depth" width="900" height="170"></canvas>
      </div>
      <div class="panel">
        <h2>Data Files</h2>
        <div class="footer">
          <div><a href="../{data["session"]}/pov_video.mp4" target="_blank">pov_video.mp4</a></div>
          <div><a href="../{data["session"]}/imu.csv" target="_blank">imu.csv</a></div>
          <div><a href="../{data["session"]}/head_pose.csv" target="_blank">head_pose.csv</a></div>
          <div><a href="../{data["session"]}/hand_joints.csv" target="_blank">hand_joints.csv</a></div>
          <div><a href="../{data["session"]}/body_pose.csv" target="_blank">body_pose.csv</a></div>
          <div><a href="../{data["session"]}/depth_mesh/depth_mesh_index.csv" target="_blank">depth_mesh_index.csv</a></div>
          <div style="margin-top:10px">If video is missing, use timeline controls to play streams.</div>
        </div>
      </div>
    </div>
  </div>

  <script id="data-json" type="application/json">{data_json}</script>
  <script>
    const DATA = JSON.parse(document.getElementById("data-json").textContent);

    function finite(v) {{ return Number.isFinite(v); }}
    function nearestIndex(ts, t) {{
      if (!ts || ts.length === 0) return -1;
      let lo = 0, hi = ts.length - 1;
      while (lo < hi) {{
        const mid = (lo + hi) >> 1;
        if (ts[mid] < t) lo = mid + 1; else hi = mid;
      }}
      let i = lo;
      if (i > 0 && Math.abs(ts[i - 1] - t) <= Math.abs(ts[i] - t)) i--;
      return i;
    }}

    function makeChart(canvasId, x, series) {{
      const canvas = document.getElementById(canvasId);
      const ctx = canvas.getContext("2d");
      const pad = {{l:50,r:10,t:12,b:20}};
      const w = canvas.width, h = canvas.height;
      const xMin = x.length ? x[0] : 0;
      const xMax = x.length ? x[x.length - 1] : 1;
      let yMin = Infinity, yMax = -Infinity;
      for (const s of series) {{
        for (const y of s.y) {{
          if (!finite(y)) continue;
          if (y < yMin) yMin = y;
          if (y > yMax) yMax = y;
        }}
      }}
      if (!finite(yMin) || !finite(yMax) || yMin === yMax) {{ yMin = 0; yMax = 1; }}
      const yPad = Math.max((yMax - yMin) * 0.12, 1e-6);
      yMin -= yPad; yMax += yPad;
      const sx = (t) => pad.l + ((t - xMin) / Math.max(1e-6, xMax - xMin)) * (w - pad.l - pad.r);
      const sy = (v) => h - pad.b - ((v - yMin) / Math.max(1e-6, yMax - yMin)) * (h - pad.t - pad.b);

      function draw(cursorTs) {{
        ctx.clearRect(0, 0, w, h);
        ctx.strokeStyle = "#2f3745";
        ctx.lineWidth = 1;
        ctx.strokeRect(pad.l, pad.t, w - pad.l - pad.r, h - pad.t - pad.b);

        for (const s of series) {{
          ctx.strokeStyle = s.color;
          ctx.lineWidth = 1.8;
          ctx.beginPath();
          let started = false;
          const n = Math.min(x.length, s.y.length);
          for (let i = 0; i < n; i++) {{
            const yv = s.y[i];
            if (!finite(yv)) {{
              started = false;
              continue;
            }}
            const X = sx(x[i]), Y = sy(yv);
            if (!started) {{ ctx.moveTo(X, Y); started = true; }} else {{ ctx.lineTo(X, Y); }}
          }}
          ctx.stroke();

          if (finite(cursorTs)) {{
            const idx = nearestIndex(x, cursorTs);
            if (idx >= 0 && idx < s.y.length && finite(s.y[idx])) {{
              ctx.fillStyle = s.color;
              ctx.beginPath();
              ctx.arc(sx(x[idx]), sy(s.y[idx]), 3.2, 0, Math.PI * 2);
              ctx.fill();
            }}
          }}
        }}

        if (finite(cursorTs)) {{
          const X = sx(Math.max(xMin, Math.min(xMax, cursorTs)));
          ctx.strokeStyle = "#ffffff";
          ctx.globalAlpha = 0.75;
          ctx.beginPath();
          ctx.moveTo(X, pad.t);
          ctx.lineTo(X, h - pad.b);
          ctx.stroke();
          ctx.globalAlpha = 1;
        }}
      }}
      return {{draw}};
    }}

    const charts = [
      makeChart("c_imu", DATA.streams.imu.ts, [
        {{y: DATA.streams.imu.accel_mag, color:"#58a6ff"}},
        {{y: DATA.streams.imu.gyro_mag, color:"#f2cc60"}}
      ]),
      makeChart("c_head", DATA.streams.head.ts, [
        {{y: DATA.streams.head.x, color:"#58a6ff"}},
        {{y: DATA.streams.head.y, color:"#3fb950"}},
        {{y: DATA.streams.head.z, color:"#f85149"}}
      ]),
      makeChart("c_hand", DATA.streams.hand.ts, [
        {{y: DATA.streams.hand.wrist_dist, color:"#a371f7"}}
      ]),
      makeChart("c_body", DATA.streams.body.ts, [
        {{y: DATA.streams.body.mean_conf, color:"#3fb950"}}
      ]),
      makeChart("c_depth", DATA.streams.depth.ts, [
        {{y: DATA.streams.depth.verts, color:"#58a6ff"}},
        {{y: DATA.streams.depth.tris, color:"#f2cc60"}}
      ])
    ];

    const video = document.getElementById("video");
    const timeline = document.getElementById("timeline");
    const btnPlay = document.getElementById("btn_play");
    const btnPause = document.getElementById("btn_pause");
    const btnVideo = document.getElementById("btn_video");
    const clockModeEl = document.getElementById("clock_mode");

    const tSession = document.getElementById("t_session");
    const tVideo = document.getElementById("t_video");
    const vAccel = document.getElementById("v_accel");
    const vGyro = document.getElementById("v_gyro");
    const vHead = document.getElementById("v_head");
    const vWrist = document.getElementById("v_wrist");
    const vBody = document.getElementById("v_body");
    const vDepth = document.getElementById("v_depth");

    const minTs = Number(DATA.session_min_ts || 0);
    const maxTs = Number(DATA.session_max_ts || 1);
    const videoOffset = Number(DATA.video_start_ts || 0);
    let currentTs = Math.max(minTs, Math.min(maxTs, videoOffset));
    let mode = DATA.has_video ? "video" : "streams";
    let streamPlaying = false;
    let lastMs = performance.now();

    function clampTs(ts) {{
      return Math.max(minTs, Math.min(maxTs, ts));
    }}

    function setModeLabel() {{
      clockModeEl.textContent = DATA.has_video
        ? ("clock mode: " + mode)
        : "clock mode: streams (no video file)";
    }}

    function drawAt(st) {{
      const stClamped = clampTs(st);
      currentTs = stClamped;
      timeline.value = String(stClamped);

      const vt = Math.max(0, stClamped - videoOffset);
      tVideo.textContent = vt.toFixed(3) + "s";
      tSession.textContent = stClamped.toFixed(3) + "s";

      const iImu = nearestIndex(DATA.streams.imu.ts, stClamped);
      if (iImu >= 0) {{
        vAccel.textContent = DATA.streams.imu.accel_mag[iImu].toFixed(4);
        vGyro.textContent = DATA.streams.imu.gyro_mag[iImu].toFixed(4);
      }}
      const iHead = nearestIndex(DATA.streams.head.ts, stClamped);
      if (iHead >= 0) {{
        vHead.textContent = DATA.streams.head.x[iHead].toFixed(3) + ", " + DATA.streams.head.y[iHead].toFixed(3) + ", " + DATA.streams.head.z[iHead].toFixed(3);
      }}
      const iHand = nearestIndex(DATA.streams.hand.ts, stClamped);
      if (iHand >= 0) {{
        const d = DATA.streams.hand.wrist_dist[iHand];
        vWrist.textContent = Number.isFinite(d) ? d.toFixed(3) + " m" : "n/a";
      }}
      const iBody = nearestIndex(DATA.streams.body.ts, stClamped);
      if (iBody >= 0) {{
        vBody.textContent = DATA.streams.body.mean_conf[iBody].toFixed(3);
      }}
      const iDepth = nearestIndex(DATA.streams.depth.ts, stClamped);
      if (iDepth >= 0) {{
        vDepth.textContent = Math.round(DATA.streams.depth.verts[iDepth]) + ", " + Math.round(DATA.streams.depth.tris[iDepth]);
      }}

      for (const ch of charts) ch.draw(stClamped);
    }}

    function raf(nowMs) {{
      const dt = (nowMs - lastMs) / 1000.0;
      lastMs = nowMs;

      if (mode === "video" && DATA.has_video && !video.paused && !video.ended) {{
        drawAt(videoOffset + (video.currentTime || 0));
      }} else if (mode === "streams" && streamPlaying) {{
        let next = currentTs + dt;
        if (next > maxTs) {{
          next = maxTs;
          streamPlaying = false;
        }}
        drawAt(next);
        if (DATA.has_video) {{
          video.currentTime = Math.max(0, next - videoOffset);
        }}
      }}
      requestAnimationFrame(raf);
    }}

    btnPlay.addEventListener("click", () => {{
      mode = "streams";
      setModeLabel();
      streamPlaying = true;
      if (DATA.has_video) video.pause();
    }});

    btnPause.addEventListener("click", () => {{
      streamPlaying = false;
      if (DATA.has_video) video.pause();
    }});

    btnVideo.addEventListener("click", () => {{
      if (!DATA.has_video) return;
      mode = "video";
      streamPlaying = false;
      setModeLabel();
      video.currentTime = Math.max(0, currentTs - videoOffset);
    }});

    timeline.addEventListener("input", () => {{
      const ts = Number(timeline.value);
      mode = "streams";
      streamPlaying = false;
      setModeLabel();
      drawAt(ts);
      if (DATA.has_video) {{
        video.currentTime = Math.max(0, ts - videoOffset);
      }}
    }});

    if (!DATA.has_video) {{
      btnVideo.disabled = true;
      video.style.opacity = 0.55;
    }} else {{
      video.addEventListener("timeupdate", () => {{
        if (mode === "video") drawAt(videoOffset + (video.currentTime || 0));
      }});
      video.addEventListener("seeking", () => {{
        if (mode === "video") drawAt(videoOffset + (video.currentTime || 0));
      }});
      video.addEventListener("play", () => {{
        mode = "video";
        streamPlaying = false;
        setModeLabel();
      }});
    }}

    setModeLabel();
    drawAt(currentTs);
    requestAnimationFrame(raf);
  </script>
</body>
</html>
"""
    out_path.write_text(html, encoding="utf-8")


def main():
    ap = argparse.ArgumentParser(description="Generate synced side-by-side multi-stream dashboard.")
    ap.add_argument("--local-view-root", default="local_view")
    ap.add_argument("--session", required=True, help="Session folder name (e.g., 20260302_151416_capture)")
    args = ap.parse_args()

    root = Path(args.local_view_root).resolve()
    session_dir = root / args.session
    if not session_dir.exists():
        raise FileNotFoundError(f"Session not found: {session_dir}")

    data = build_data(session_dir)
    out_dir = root / f"sync_dashboard_{args.session}"
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / "index.html"
    write_html(out_path, data)
    print(str(out_path))


if __name__ == "__main__":
    main()
