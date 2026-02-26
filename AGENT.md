# AGENT.md

## Repository Overview
- Unity/C# scripts collect capture-time data on-device (head pose, hand joints, IMU, action logs, depth mesh snapshots).
- `postprocess.py` is the Python post-processing pipeline for ADB pull, sync, face blur, validation, and delivery packaging.
- `README.md` is the source of truth for operator and post-processing workflow.

## Python Tooling (uv)
- Dependency management uses `uv` with `pyproject.toml` + `uv.lock`.
- Install/update the environment with `uv sync`.
- Run commands via `uv run ...` (for example, `uv run postprocess.py validate SESSION_DIR`).
- Do not reintroduce `requirements.txt` as the primary dependency source.

## Code Quality Standards
- Keep code human-readable: clear names, short functions, and explicit control flow.
- Prefer modern language idioms and standard tooling for each language in this repo.
- Avoid tightly coupled “god functions”; split by responsibility.

## Serialization and Data Modeling
- Avoid manual JSON string construction.
- Python:
  - Prefer typed models with `pydantic` or `dataclasses` for structured payloads.
  - Centralize schema definitions and serialization/deserialization logic.
- C#:
  - Prefer typed DTOs/records and `System.Text.Json` serializers over handcrafted JSON text.
  - Keep schema types close to producer/consumer boundaries.
- Preserve existing file schemas unless a schema change is intentional and documented.

## Language-Specific Best Practices
- Python:
  - Use type hints for public functions and data boundaries.
  - Keep CLI entrypoints thin; move business logic into reusable functions.
- C# / Unity:
  - Favor small, testable methods.
  - Keep runtime allocations low in frame-critical paths.
  - Treat capture formats (`*.csv`, `*.json`) as stable interfaces.

## Documentation
- Any behavior or interface change must update `README.md`.
- Keep command examples executable as written from repository root.
