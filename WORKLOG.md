# OpenPico4UltraCapture Worklog

Use this file to track what we are actively building before we commit.

## Current Goal
- Fix all issues from 2026-03-11 capture audit.
- Reference: `docs/2026-03-11_CAPTURE_AUDIT_ISSUES_AND_FIX_PLAN.md`

## Active Tasks
- [ ] VIDEO-001: ensure `pov_video.mp4` is always found/copied and `video_saved` is logged.
- [ ] IMU-001: add IMU source provenance fields and fallback reason accounting.
- [ ] BODY-001: add explicit body source/native coverage metrics and events.
- [ ] METRIC-001: separate task duration vs finalize duration in `session_summary.json`.
- [ ] QA-001: update `reality_check_all_streams.py` to evaluate coverage within task window.

## Notes
- Latest audit sessions: `20260311_164118_capture`, `20260311_165414_capture`, `20260311_165659_capture`.
- Primary affected run (`20260311_165659_capture`) had valid sensor CSVs but missing `pov_video.mp4`.
- Body stream present but confidence values were all `0.000` (likely fallback-only).
- IMU stream existed but was flagged fallback for all frames.

## Ready To Commit Checklist
- [ ] Code builds locally
- [ ] Core flow tested
- [ ] Relevant docs updated
- [ ] Commit message drafted

## Commit Plan
- Branch:
- Commit message:
- Files expected:
