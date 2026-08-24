# Server API and data store

Flask server: `server/server.py` (the only Python file in `server/`). Binds `0.0.0.0:5002` so LAN
viewers (VR headset, second PC) can reach it; `M2U_DEBUG=1` enables Flask debug/auto-reload.
Layout generation imports `layout_prompt.py` from the **repo root** (server.py puts the project
root on `sys.path`), so `python server/server.py` and `cd server && python server.py` both work.

## Run

```bash
pip install -r requirements.txt      # repo root; large (torch, open3d, ...)
# .env at repo root: ANTHROPIC_API_KEY=sk-ant-...   (Claude is the default provider)
#   Gemini instead: GOOGLE_API_KEY=... and CURRENT_PROVIDER = "gemini" in layout_prompt.py
python server/server.py
```

Smoke test without any API key:

```bash
curl http://localhost:5002/health
curl -X POST http://localhost:5002/api/environments \
  -H "Content-Type: application/json" \
  -d '{"name":"Test","version":1,"tags":[],"site":{"terrainSize":[100,100],"terrainZones":[],"paths":[],"scaleNote":""},"buildingInstances":[],"objectInstances":[]}'
curl http://localhost:5002/api/environments
```

## Data layout

Everything is JSON files. **Never edit them by hand; use the endpoints.** Records are split by
*kind* into subfolders (file is still `<uuid>.json`; the kind is also stored as a tag):

```
server/data/environments/user/<id>.json        user-authored
server/data/environments/generated/<id>.json   from layout generation
server/data/buildings/static/<id>.json         user-authored
server/data/buildings/cached/<id>.json         from layout generation
server/data/_archive/...                       soft-deleted records
server/data/active.json                        live-share pointer { envId, loadedIds } (see vr-live-sync.md)
input/<image>                                  uploaded sketches (repo root)
layouts/<image-stem>.json                      raw LLM layout JSON, one per source image (repo root)
```

## Endpoints

| Endpoint | Notes |
|---|---|
| `GET/POST /api/environments`, `GET/POST /api/buildings` | POST accepts a `"kind"` body field (defaults `user` / `static`) and makes names unique (`name`, `name (2)`, …). List endpoints accept `?kind=` and return a `kind` per row. |
| `GET/PUT /api/environments/<id>`, `GET/PUT /api/buildings/<id>`, `POST .../<id>/archive`, `POST .../<id>/favorite` | Resolve an id across subfolders. Archive = soft delete into `_archive/`. |
| `GET /api/list`, `POST /api/search`, `GET /api/download/<filename>`, `POST /api/maintenance/dedup` | Generic list / search / download, and a dedup maintenance pass. |
| `POST /api/inputs`, `GET /api/inputs`, `GET /api/inputs/<name>` | Upload / list / serve sketch images. |
| `GET /api/sites` | Site presets (`site_presets.py`). |
| `POST /api/layout/generate` | Body `{ "image": "<uploaded input name>" }` — **`image` is required**, a missing name returns 400 (the old server-side file dialog hung headless servers and was removed; `layout_prompt.py`'s CLI still has its dialog). Optional site fields: `"site"` (preset name) or explicit `lot_boundary` / `site_width_ft` / `site_height_ft` — Unity sends the explicit trio when a drawn site is the Generate target (`SiteFit.BoundaryToCanvas`: boundary normalized to its own bbox on the 0–1000 canvas, dims = bbox extents in feet); e.g. `{"image":"plan.png","lot_boundary":[[0,0],[0,1000],[1000,1000],[1000,0]],"site_width_ft":200,"site_height_ft":150}` — the response's `site_scale.lot_boundary` echoes it. Response includes a `"warnings"` list: the server checks for the 7 top-level keys, a usable `lot_boundary` (patched back from the request's boundary if the model drops it; fatal 502 only in auto mode), and center-point-inside-lot containment, but stays warn-first. |
| `GET/POST /api/active` | Live-share pointer for VR / multi-client viewers — contract in [vr-live-sync.md](vr-live-sync.md). |
| `GET /health` | Liveness. |
