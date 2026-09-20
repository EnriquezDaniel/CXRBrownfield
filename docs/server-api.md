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
layouts/<image-stem>.json                      layout JSON as handed to Unity, one per source image (repo root)
layouts/<image-stem>.brief.json                designer brief parsed from the notes on the last run (absent without notes)
```

## Endpoints

| Endpoint | Notes |
|---|---|
| `GET/POST /api/environments`, `GET/POST /api/buildings` | POST accepts a `"kind"` body field (defaults `user` / `static`) and makes names unique: environments `name`, `name2`, …; buildings `name`, `name 2`, … (a trailing number counts on, `Coffee Shop 2` → `Coffee Shop 3`, compared without case; same rule as Unity's `BuildingCopies`). `"dedupe": false` skips the identical-content reuse (Save As, pasted building copies). List endpoints accept `?kind=` and return a `kind` per row; building rows also carry `hiddenCopy` (an auto-named paste copy the Unity lists leave out until it is renamed). |
| `GET/PUT /api/environments/<id>`, `GET/PUT /api/buildings/<id>`, `POST .../<id>/archive`, `POST .../<id>/favorite` | Resolve an id across subfolders. Archive = soft delete into `_archive/`. |
| `GET /api/list`, `POST /api/search`, `GET /api/download/<filename>`, `POST /api/maintenance/dedup` | Generic list / search / download, and a dedup maintenance pass. |
| `POST /api/inputs`, `GET /api/inputs`, `GET /api/inputs/<name>` | Upload / list / serve sketch images. |
| `GET/PUT /api/inputs/<name>/notes` | Designer notes saved beside a sketch as `input/<stem>.notes.txt` (so `plan.png` and `plan.jpg` share one). GET returns `{ "name", "notes" }` with `""` when none; PUT takes `{ "notes": "..." }` (string or 400, capped at 2000 characters), and an empty string deletes the file. The sidecar never appears in the image listing. `POST /api/layout/generate` writes the same file whenever its body carries `notes`. |
| `GET /api/sites` | Site presets (`site_presets.py`): `auto`, `site_a_bronx`, `roosevelt_island`, `westchester_avenue` (a 459 x 66 ft strip, full-canvas rectangle, long side down the sketch). |
| `POST /api/layout/generate` | Body `{ "image": "<uploaded input name>" }` — **`image` is required**, a missing name returns 400 (the old server-side file dialog hung headless servers and was removed; `layout_prompt.py`'s CLI still has its dialog). Optional site fields: `"site"` (preset name) or explicit `lot_boundary` / `site_width_ft` / `site_height_ft` — Unity sends the explicit trio when a drawn site is the Generate target (`SiteFit.BoundaryToCanvas`: boundary normalized to its own bbox on the 0–1000 canvas, dims = bbox extents in feet); e.g. `{"image":"plan.png","lot_boundary":[[0,0],[0,1000],[1000,1000],[1000,0]],"site_width_ft":200,"site_height_ft":150}` — the response's `site_scale.lot_boundary` echoes it. Axis convention: canvas index `[0]` is the sketch's vertical axis and spans `site_width_ft` (Unity X); index `[1]` is the horizontal axis and spans `site_height_ft` (Unity Z). Unity turns the plan half a turn on conversion, so the top of the sketch (north) lands at +X and its left edge (west) at +Z, and `SiteFit.BoundaryToCanvas` applies the inverse (a drawn site's bbox max corner is canvas 0). Optional `"sketch_rotation"`: `"auto"` (default) rotates the sketch a quarter turn when its pixel aspect disagrees with the site's (both must be clearly non-square, ratio > 1.15), or `0` / `90` / `180` / `270` degrees counter-clockwise to force it; anything else is a 400. When both site dims are known the server also resamples the sketch to the parcel's true proportions (long edge 1568 px, uniform feet per pixel) before the model sees it, and the runtime context states the feet per canvas unit per axis (`sketch_prep.py`). The response's `"sketch_prep"` reports `rotation_deg`, `auto_rotated`, `original_px`, `prepared_px`, `resampled`, `ft_per_unit_y`, `ft_per_unit_x` (null when nothing ran). Optional `"notes"`: free text from the designer (program, floor counts, names, sizes) appended to the model's runtime context after the site JSON, capped at 2000 characters and told to win over visual massing cues; a non-string is a 400, and the response echoes it as `"notes"`. A body that carries the key saves it beside the sketch (`input/<stem>.notes.txt`, empty string included, which is how Unity clears notes) before generation runs; a body without the key reuses the saved text, so curl callers get what Unity last typed. **Building styles:** notes may assign a facade style letter to a named building (`Ice cream shop: style A. Movie theater: style B.`); the model copies that letter into an optional `"style"` field (`"A"` to `"F"`) on that `generated_buildings` entry and omits it everywhere else, never picking a letter on its own. Unity maps the letter to a wall material through `BuildingStylePalette` (see [unity-scene-wiring.md](unity-scene-wiring.md)). **Building signs:** a building the sketch labels or the notes name gets an optional `"sign"` field, one uppercase word of 1 to 16 letters or digits (`"ICECREAM"`, `"THEATER"`); unnamed buildings omit it. Unity stores it on the placed instance (`buildingInstances[].signs[0].text`, `signCompass` = `east`) and hangs it on the wall that faces east; the Sign section in the editor can rename, re-aim and pin it afterwards, and add more signs to the list (editor only, generation yields one). Records from before the list carry `signText` / `signPinned` / `signHostX/Z/Floor` instead, which Unity still reads. Response includes a `"warnings"` list: the server normalizes (drops null-valued fields, checks the 7 top-level keys, patches a usable `lot_boundary` back from the request's boundary if the model drops it; fatal 502 only in auto mode), reconciles against the designer brief (next row), then checks center-point-inside-lot containment, a `style` outside A to F (warn only; Unity drops it), a `sign` that is not a string of 1 to 16 characters (warn only), and the layout against `layout_schema.LAYOUT_SCHEMA` with `jsonschema` (warn only). Structured output on the Claude layout call is available behind `layout_prompt.LAYOUT_STRUCTURED_OUTPUT` but **off by default**: the full schema exceeds the API's grammar size limit, and the accepted subset (`LAYOUT_OUTPUT_SCHEMA`, site_scale and buildings typed, the rest free objects) made the model return a near-empty layout when tried. The layout call itself now sends the prompt file in `system` behind a cache breakpoint (the second run within five minutes reads about 9,700 tokens from cache) with an explicit `output_config.effort`. `layouts/<stem>.json` is rewritten whenever normalize or reconcile changed anything, so it always equals the `layout` Unity received. |
| Designer brief (`POST /api/layout/generate` with `notes`) | When `notes` is non-empty the server first runs a **brief pass** (`brief_prompt.parse_brief`, model `BRIEF_MODEL_ID` = `claude-sonnet-5`, text only, structured output, about 10 s) that turns the prose into a JSON brief: `buildings[]` (`ref` b1…, `name`, `use`, `style` A–F or null, `floors`, `size_hint`, `where`, `group`), `splits[]` (`group`, `where`, `count`: one drawn block that becomes N buildings), `program_totals[]` (`use`, `quantity`, `unit`, `across`), `paths[]` / `fences[]` (`ref` p1… / f1…, `where`, `material` / `type`, `width_ft` / `height_ft`, `exclude`), `props[]` (`ref` x1…, `type`, `where`, `arrangement`, `count`, `exclude`), `ignored[]` (ground and water sentences, out of scope), `unparsed[]`. The brief is saved as `layouts/<stem>.brief.json`, appended to the layout prompt after the raw notes as the authoritative reading, and the layout model tags each entry it satisfies with `brief_ref`. After the layout comes back, `layout_reconcile.reconcile_brief` enforces the brief without another model call: links each brief building (by `brief_ref`, then exact `area_name`, then a unique use word), overwrites `area_name`, `style`, `floors` from the brief, drops style letters on buildings the brief did not name, and **splits** a drawn block the model left whole into `count` touching pieces along its longer side in feet (`T = max(1, round(units × ft_per_unit × 0.3048 / 4))` tiles, pieces differ by at most one tile, never past the block; a block rotated off the quarter turns is left alone and reported; `corner_angles` are dropped from pieces). Paths and fences get their material / type / width / height enforced; excluded paths, fences and props are removed. Nothing here is fatal. Response adds `"brief"` (or null), `"brief_report"` (`satisfied`, `missing` [{ref, name, reason}], `extra_unmatched`, `splits` [{group, outcome: satisfied / split / split_reduced / skipped_rotated / missing}], `paths`, `fences`, `props`, `ignored`, `unparsed`, `changed`; or `{error}` when the pass failed), `"brief_model"` and `"layout_model"`; missing items also appear in `warnings`. A brief failure (no key, API error) is logged and generation continues on the raw notes. Tests: `.venv/Scripts/python.exe -m unittest tests.test_reconcile`. |
| `GET/POST /api/active` | Live-share pointer for VR / multi-client viewers — contract in [vr-live-sync.md](vr-live-sync.md). |
| `GET /health` | Liveness. |
