---
paths:
  - "server/**"
---

# Server and data store rules

- `server/data/` is the database. **Never edit or create records by hand**; use the CRUD endpoints
  (`POST /api/environments`, `POST /api/buildings`, PUT by id, `.../archive`). Layout: records split
  by *kind* — `environments/{user,generated}`, `buildings/{static,cached}`, `_archive/` — file still
  `<uuid>.json`, kind also stored as a tag. POST accepts `"kind"` (defaults `user` / `static`) and
  makes names unique (`name`, `name (2)`, …); GET/PUT/archive resolve an id across subfolders; list
  endpoints accept `?kind=` and return `kind` per row. Keep all of that intact.
- `server/data/active.json` is the live-share pointer (`{ envId, loadedIds }`) behind
  `GET/POST /api/active`; contract in `docs/vr-live-sync.md`. Archived/deleted ids must keep being
  dropped silently.
- `POST /api/layout/generate`: `image` is **required** (400 when missing — no server-side file
  dialog, it hung headless servers). Keep the response's `warnings` list warn-first; the only fatal
  (502) check is an unusable `lot_boundary` in auto mode.
- `layout_prompt.py`, `site_presets.py`, `prompts/site_parsing.md`, `input/`, and `layouts/` live at
  the **repo root**, not in `server/`; `server.py` adds the project root to `sys.path`.
- Server binds `0.0.0.0:5002` on purpose (LAN viewers); debug is opt-in via `M2U_DEBUG=1`.

Full endpoint table: `docs/server-api.md`.
