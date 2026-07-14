#!/usr/bin/env python3
"""
Local Model to Unity Bridge Server

Handles communication between the Flask backend and Unity for:
- Model search / download (existing)
- Sketch-to-layout generation via Claude/Gemini (layout_prompt.py)
- Environment and Building CRUD library (new, JSON file store)
"""

import os
import re
import glob
import json
import threading
import sys
import tempfile
import uuid
import shutil
from datetime import datetime, timezone
from pathlib import Path
from flask import Flask, jsonify, request, send_file, abort
from flask_cors import CORS

# Force UTF-8 console output. layout_prompt logs progress with emoji (🔄, 📡, …);
# on Windows the default cp1252 stdout raises UnicodeEncodeError on those, which
# would otherwise surface as a 500 from /api/layout/generate.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

try:
    from layout_prompt import process_sketch
    _LAYOUT_AVAILABLE = True
except Exception as _layout_import_err:
    _LAYOUT_AVAILABLE = False
    print(f"[server] layout_prompt not loaded ({_layout_import_err}). "
          "POST /api/layout/generate will return 503.")

from site_presets import get_site_preset, list_site_presets, AUTO as AUTO_SITE

app = Flask(__name__)
CORS(app)

SUPPORTED_EXTENSIONS       = ['.obj', '.glb']
SUPPORTED_REGEX_EXTENSIONS = ['*.obj', '*.glb']
# Default to "auto" (no hard-coded boundary): the model traces the parcel outline
# from the sketch. Named presets / explicit bounds override this per request.
DEFAULT_SITE              = AUTO_SITE

IMAGE_EXTENSIONS = {'.png', '.jpg', '.jpeg', '.webp', '.bmp'}

# Uploaded source images and the raw per-image layout JSON live at the repo root.
INPUT_DIR   = PROJECT_ROOT / "input"
LAYOUTS_DIR = PROJECT_ROOT / "layouts"

layout_generation_lock = threading.Lock()

# One coarse lock around every record read-modify-write (create dedup, PUT version
# bump, favorite toggle, archive, active-pointer publish). Flask serves requests on
# multiple threads; without this, concurrent writes lose updates. Traffic is a
# handful of clients, so a single global lock is simpler than per-record locking.
_STORE_LOCK = threading.Lock()

# ---------------------------------------------------------------------------
# Data directory layout
#   server/data/environments/user/<id>.json       (user-authored)
#   server/data/environments/generated/<id>.json  (layout generation)
#   server/data/buildings/static/<id>.json        (user-authored)
#   server/data/buildings/cached/<id>.json         (layout generation)
#   server/data/_archive/{environments,buildings}/<id>.json
# Files are named by UUID so buildingInstances[].buildingId references stay valid;
# the "kind" is carried by the subfolder plus a tag.
# ---------------------------------------------------------------------------
DATA_DIR         = Path(__file__).resolve().parent / "data"
ENVIRONMENTS_DIR = DATA_DIR / "environments"
BUILDINGS_DIR    = DATA_DIR / "buildings"
ARCHIVE_DIR      = DATA_DIR / "_archive"

# Single shared "active environment" pointer for live multi-client sync (VR / 2nd PC).
# The desktop host (admin) publishes its full loaded set — the active env plus any backdrop
# envs — and viewers poll GET /api/active to mirror all of them.
ACTIVE_POINTER_FILE = DATA_DIR / "active.json"

# kind -> subfolder
ENV_KINDS  = {"user": ENVIRONMENTS_DIR / "user", "generated": ENVIRONMENTS_DIR / "generated"}
BLDG_KINDS = {"static": BUILDINGS_DIR / "static", "cached": BUILDINGS_DIR / "cached"}
DEFAULT_ENV_KIND  = "user"
DEFAULT_BLDG_KIND = "static"


def _ensure_data_dirs():
    for d in [
        *ENV_KINDS.values(),
        *BLDG_KINDS.values(),
        ARCHIVE_DIR / "environments",
        ARCHIVE_DIR / "buildings",
        INPUT_DIR,
        LAYOUTS_DIR,
    ]:
        d.mkdir(parents=True, exist_ok=True)


def _migrate_flat_files():
    """Move legacy flat <id>.json files into kind subfolders based on their tags."""
    for flat_dir, kinds, gen_kind, default_kind in [
        (ENVIRONMENTS_DIR, ENV_KINDS, "generated", DEFAULT_ENV_KIND),
        (BUILDINGS_DIR, BLDG_KINDS, "cached", DEFAULT_BLDG_KIND),
    ]:
        for path in flat_dir.glob("*.json"):
            try:
                data = _load_json(path)
            except Exception as exc:
                print(f"[WARN] Skipping migration of {path.name}: {exc}")
                continue
            tags = data.get("tags", []) or []
            kind = gen_kind if "generated" in tags else default_kind
            dst = kinds[kind] / path.name
            shutil.move(str(path), str(dst))
            print(f"[migrate] {path.name} -> {kind}/")


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_UUID_RE = re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')

def _client_or_new_id(data: dict) -> str:
    """Use the client-provided 'id' if it is a valid UUID, otherwise generate one."""
    client_id = str(data.get("id", "")).lower()
    return client_id if _UUID_RE.match(client_id) else str(uuid.uuid4())


def _atomic_write(path: Path, data: dict):
    """Write JSON atomically: unique temp file in the same dir, then rename over.

    mkstemp (not a fixed '<id>.tmp') so concurrent writes to the same record can't
    share a temp file; the '.tmp' suffix keeps the '*.json' globs blind to temps.
    """
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(json.dumps(data, indent=2))
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def _load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _iso_mtime(path: Path) -> str:
    ts = path.stat().st_mtime
    return datetime.fromtimestamp(ts, tz=timezone.utc).isoformat()


def _summarize(data: dict, path: Path) -> dict:
    return {
        "id":       data.get("id"),
        "name":     data.get("name"),
        "version":  data.get("version"),
        "tags":     data.get("tags", []),
        "kind":     path.parent.name,
        "updated":  _iso_mtime(path),
        "favorite": data.get("favorite", False),
        "locked":   data.get("locked", False),
    }


def _validate_required(data: dict, fields: list[str]) -> str | None:
    """Return an error message if any required field is missing, else None."""
    missing = [f for f in fields if f not in data]
    return f"Missing required fields: {missing}" if missing else None


# ---------------------------------------------------------------------------
# Kind-aware record helpers
# ---------------------------------------------------------------------------

def _kind_dir(kinds: dict, kind: str, default_kind: str) -> tuple[str, Path]:
    """Resolve a (kind, directory) pair, falling back to the default kind."""
    kind = (kind or "").lower()
    if kind not in kinds:
        kind = default_kind
    return kind, kinds[kind]


def _find_record(base_dir: Path, kinds: dict, rec_id: str) -> Path | None:
    """Locate <id>.json across all kind subfolders (and the legacy flat dir)."""
    for d in [*kinds.values(), base_dir]:
        candidate = d / f"{rec_id}.json"
        if candidate.is_file():
            return candidate
    return None


def _set_kind_tag(data: dict, kind: str, all_kinds: dict):
    """Ensure tags reflect the record's kind (drop sibling kind tags, add this one)."""
    tags = [t for t in (data.get("tags") or []) if t not in all_kinds]
    tags.append(kind)
    data["tags"] = tags


def _unique_name(kinds: dict, base: str) -> str:
    """Return base, or 'base2', 'base3', ... so names stay unique across kinds.

    The first record keeps its bare name; later same-name records get a bare-number
    suffix (e.g. 'TestEnvironment', 'TestEnvironment2', 'TestEnvironment3').
    """
    existing = set()
    for d in kinds.values():
        for path in d.glob("*.json"):
            try:
                existing.add(_load_json(path).get("name"))
            except Exception:
                pass
    if base not in existing:
        return base
    n = 2
    while f"{base}{n}" in existing:
        n += 1
    return f"{base}{n}"


# ---------------------------------------------------------------------------
# Content dedup helpers
# ---------------------------------------------------------------------------

# Fields that vary between otherwise-identical records — ignored when matching for dedup.
# Stripping 'instanceId' everywhere makes buildingInstances / objectInstances / embeddedObjects
# compare on content, not their random per-run ids. 'buildingId' is a different key and is kept,
# so environment equality correctly depends on which (canonical) buildings are referenced.
# 'name' is deliberately KEPT: it is part of a record's identity, so Save As (same content,
# new name) creates a real new record instead of deduping back onto the original id.
# 'favorite' and 'locked' are server/user-managed flags, not content.
_VOLATILE_KEYS = {"id", "version", "tags", "kind", "updated", "instanceId", "favorite", "locked"}


def _canonical_signature(data: dict) -> str:
    """Stable serialization of a record's content, ignoring volatile fields."""
    def clean(obj):
        if isinstance(obj, dict):
            return {k: clean(v) for k, v in obj.items() if k not in _VOLATILE_KEYS}
        if isinstance(obj, list):
            return [clean(v) for v in obj]
        return obj
    return json.dumps(clean(data), sort_keys=True, separators=(",", ":"))


def _find_duplicate(scan_dirs: list[Path], data: dict) -> dict | None:
    """Return an existing record in scan_dirs whose content matches data, else None."""
    target = _canonical_signature(data)
    for d in scan_dirs:
        for path in d.glob("*.json"):
            try:
                existing = _load_json(path)
            except Exception:
                continue
            if _canonical_signature(existing) == target:
                return existing
    return None


def _group_by_signature(kinds: dict) -> dict:
    """Group all record files under `kinds` by canonical content signature.

    Returns {signature: [(path, data), ...]} with each group sorted canonically so
    the first entry is the one we keep: a `static` building beats a `cached` one,
    then oldest mtime, then id. (For environments, kind order is irrelevant.)"""
    # Lower sort rank = preferred as canonical.
    kind_rank = {"static": 0, "cached": 1, "user": 0, "generated": 1}
    groups: dict = {}
    for d in kinds.values():
        for path in d.glob("*.json"):
            try:
                data = _load_json(path)
            except Exception:
                continue
            groups.setdefault(_canonical_signature(data), []).append((path, data))
    for sig, items in groups.items():
        items.sort(key=lambda pd: (kind_rank.get(pd[0].parent.name, 9),
                                   pd[0].stat().st_mtime,
                                   str(pd[1].get("id", ""))))
    return groups


def _archive_record(path: Path, subfolder: str):
    """Soft-delete a record file by moving it under _archive/<subfolder>/."""
    dst = ARCHIVE_DIR / subfolder / path.name
    shutil.move(str(path), str(dst))


def _dedup_existing_records() -> dict:
    """Collapse duplicate building/environment files already on disk (idempotent).

    Buildings are deduped first; every environment's buildingInstances[].buildingId is
    retargeted from an archived duplicate to its canonical id BEFORE the duplicate file is
    removed (envs reference buildings by id). Environments are deduped afterwards, so two
    otherwise-identical envs that referenced duplicate buildings now share canonical ids and
    match. Duplicates are archived, never hard-deleted."""
    report = {"archivedBuildings": [], "retargetedEnvironments": 0, "archivedEnvironments": []}

    # --- Step A: buildings ---
    dup_to_canonical: dict = {}
    for items in _group_by_signature(BLDG_KINDS).values():
        if len(items) < 2:
            continue
        canonical_id = items[0][1].get("id")
        for path, data in items[1:]:
            dup_to_canonical[data.get("id")] = canonical_id

    if dup_to_canonical:
        # Retarget references in every environment, then archive the duplicate buildings.
        for d in ENV_KINDS.values():
            for path in d.glob("*.json"):
                try:
                    env = _load_json(path)
                except Exception:
                    continue
                changed = False
                for bi in env.get("buildingInstances") or []:
                    new_id = dup_to_canonical.get(bi.get("buildingId"))
                    if new_id:
                        bi["buildingId"] = new_id
                        changed = True
                if changed:
                    _atomic_write(path, env)
                    report["retargetedEnvironments"] += 1

        for items in _group_by_signature(BLDG_KINDS).values():
            for path, data in items[1:] if len(items) >= 2 else []:
                _archive_record(path, "buildings")
                report["archivedBuildings"].append(data.get("id"))

    # --- Step B: environments (after building ids are canonicalized) ---
    for items in _group_by_signature(ENV_KINDS).values():
        for path, data in items[1:] if len(items) >= 2 else []:
            _archive_record(path, "environments")
            report["archivedEnvironments"].append(data.get("id"))

    n_b, n_e = len(report["archivedBuildings"]), len(report["archivedEnvironments"])
    if n_b or n_e or report["retargetedEnvironments"]:
        print(f"[dedup] archived {n_b} building(s), {n_e} environment(s); "
              f"retargeted {report['retargetedEnvironments']} environment(s)")
    return report


_ensure_data_dirs()
_migrate_flat_files()
_dedup_existing_records()


# ---------------------------------------------------------------------------
# Existing endpoints (unchanged)
# ---------------------------------------------------------------------------

def get_models_dir() -> Path:
    return Path(__file__).resolve().parent.parent / "models"


@app.route('/health', methods=['GET'])
def health_check():
    return jsonify({
        "status":  "ok",
        "message": "Local Model to Unity Bridge Server is running",
        "version": "1.0.0"
    })


@app.route('/api/list', methods=['GET'])
def list_models():
    try:
        models_dir  = get_models_dir()
        found_models = []

        if models_dir.exists():
            for extension in SUPPORTED_REGEX_EXTENSIONS:
                pattern = str(models_dir / extension)
                files   = glob.glob(pattern)
                for file_path in files:
                    found_models.append({
                        "name":      os.path.basename(file_path),
                        "path":      file_path,
                        "size":      os.path.getsize(file_path),
                        "extension": os.path.splitext(file_path)[1].lower(),
                        "modified":  os.path.getmtime(file_path)
                    })

        found_models.sort(key=lambda x: x["name"])
        return jsonify({
            "status":           "success",
            "models_directory": str(models_dir),
            "count":            len(found_models),
            "models":           found_models
        })
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/search', methods=['POST'])
def search_models():
    try:
        data = request.get_json()
        if not data or 'query' not in data:
            return jsonify({"status": "error", "message": "Missing 'query'"}), 400

        query      = data['query'].lower().strip()
        models_dir = get_models_dir()
        found_models = []

        if models_dir.exists():
            for extension in SUPPORTED_REGEX_EXTENSIONS:
                pattern = str(models_dir / extension)
                files   = glob.glob(pattern)
                for file_path in files:
                    filename = os.path.basename(file_path)
                    if query in filename.lower():
                        found_models.append({
                            "name":      filename,
                            "path":      file_path,
                            "size":      os.path.getsize(file_path),
                            "extension": os.path.splitext(file_path)[1].lower(),
                            "modified":  os.path.getmtime(file_path)
                        })

        found_models.sort(key=lambda x: x["name"])
        return jsonify({
            "status":           "success",
            "query":            query,
            "models_directory": str(models_dir),
            "count":            len(found_models),
            "models":           found_models
        })
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/download/<filename>', methods=['GET'])
def download_model(filename):
    try:
        models_dir = get_models_dir()
        file_path  = models_dir / filename

        if not file_path.is_file() or not str(file_path).startswith(str(models_dir)):
            return jsonify({"status": "error", "message": f"File '{filename}' not found"}), 404

        if file_path.suffix.lower() not in SUPPORTED_EXTENSIONS:
            return jsonify({"status": "error", "message": f"Unsupported type: {file_path.suffix}"}), 400

        return send_file(str(file_path), as_attachment=True, download_name=filename)
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/sites', methods=['GET'])
def list_sites():
    """List named site presets (plus "auto") for the layout-generation UI."""
    try:
        return jsonify({"status": "success", "sites": list_site_presets()})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


# The seven top-level keys the prompt requires (prompts/site_parsing.md).
_LAYOUT_TOP_KEYS = ("site_scale", "terrain_zones", "paths", "fences",
                    "generated_buildings", "generated_objects", "prefab_instances")


def _point_in_polygon(pt, polygon) -> bool:
    """Ray-cast containment test on [y, x] pairs (the prompt's 0-1000 canvas)."""
    y, x = float(pt[0]), float(pt[1])
    inside = False
    n = len(polygon)
    for i in range(n):
        y1, x1 = float(polygon[i][0]), float(polygon[i][1])
        y2, x2 = float(polygon[(i + 1) % n][0]), float(polygon[(i + 1) % n][1])
        if (y1 > y) != (y2 > y):
            if x < x1 + (y - y1) / (y2 - y1) * (x2 - x1):
                inside = not inside
    return inside


def _validate_layout(site_data: dict, requested_boundary) -> tuple[str | None, list[str], bool]:
    """Sanity-check the model's layout JSON. Returns (fatal_error, warnings, patched).

    Warn-first: imperfect layouts still flow to Unity (whose converter null-guards
    each category). Two exceptions:
      - If the request supplied an authoritative lot_boundary and the model dropped
        or mangled it, the boundary is patched back in (patched=True).
      - A missing boundary with nothing to patch from (auto mode) is fatal, because
        Unity sizes the terrain from it.
    Containment is checked on center points only — bounding-box corners would flag
    every building that correctly hugs the parcel edge.
    """
    warnings, patched = [], False

    for key in _LAYOUT_TOP_KEYS:
        if key not in site_data:
            warnings.append(f"Missing top-level key '{key}'.")

    site_scale = site_data.get("site_scale")
    if not isinstance(site_scale, dict):
        site_scale = {}
        site_data["site_scale"] = site_scale

    boundary = site_scale.get("lot_boundary")
    if not (isinstance(boundary, list) and len(boundary) >= 3):
        if requested_boundary and len(requested_boundary) >= 3:
            site_scale["lot_boundary"] = requested_boundary
            boundary = requested_boundary
            patched = True
            warnings.append("Model omitted/mangled lot_boundary; patched back from the requested site boundary.")
        else:
            return ("Layout has no usable lot_boundary and none was supplied in the "
                    "request (auto mode); Unity terrain sizing depends on it."), warnings, patched

    def _center(item):
        pt = item.get("center_point")
        if isinstance(pt, (list, tuple)) and len(pt) >= 2:
            return pt
        bb = item.get("bounding_box")
        if isinstance(bb, (list, tuple)) and len(bb) == 4:
            return [(bb[0] + bb[2]) / 2.0, (bb[1] + bb[3]) / 2.0]
        return None

    for category in ("generated_buildings", "generated_objects", "prefab_instances"):
        items = site_data.get(category)
        if not isinstance(items, list):
            continue
        for idx, item in enumerate(items):
            if not isinstance(item, dict):
                continue
            pt = _center(item)
            if pt is None:
                continue
            try:
                outside = not _point_in_polygon(pt, boundary)
            except (TypeError, ValueError, IndexError, ZeroDivisionError):
                warnings.append(f"{category}[{idx}]: unreadable center/boundary geometry.")
                continue
            if outside:
                label = (item.get("building_type") or item.get("object_type")
                         or item.get("prefab_type") or "?")
                warnings.append(f"{category}[{idx}] ({label}) center {list(pt[:2])} "
                                "is outside the lot boundary.")

    return None, warnings, patched


@app.route('/api/layout/generate', methods=['POST'])
def generate_layout_from_sketch():
    if not _LAYOUT_AVAILABLE:
        return jsonify({
            "status": "error",
            "message": "Layout generation is not available (layout_prompt.py could not be loaded). "
                       "Check that layout_prompt.py exists and your model API key "
                       "(ANTHROPIC_API_KEY / GOOGLE_API_KEY) is configured."
        }), 503

    if not layout_generation_lock.acquire(blocking=False):
        return jsonify({"status": "error", "message": "Layout generation already in progress."}), 409

    try:
        body = request.get_json(silent=True) or {}
        image_name = body.get("image")

        # Resolve the sketch: must be a named upload under input/. (No server-side
        # file dialog here — this endpoint may be called headless/remotely, and a
        # dialog would hang the request thread forever. The dialog still exists for
        # layout_prompt.py's CLI entry point.)
        if not image_name:
            return jsonify({"status": "error",
                            "message": "Missing 'image'. Upload the sketch via POST /api/inputs "
                                       "and pass its stored name."}), 400
        selected_sketch_path = (INPUT_DIR / image_name).resolve()
        if INPUT_DIR.resolve() not in selected_sketch_path.parents or not selected_sketch_path.is_file():
            return jsonify({"status": "error", "message": f"Input image '{image_name}' not found."}), 404

        # Resolve the site: a named preset, explicit bounds in the body, or "auto"
        # (no boundary → the model traces the parcel from the sketch). Explicit
        # body fields override the preset; the preset overrides the default.
        preset = get_site_preset(body.get("site", DEFAULT_SITE)) or {}
        lot_boundary   = body.get("lot_boundary",   preset.get("lot_boundary"))
        site_width_ft  = body.get("site_width_ft",  preset.get("site_width_ft"))
        site_height_ft = body.get("site_height_ft", preset.get("site_height_ft"))

        # One raw layout JSON per source image, archived under layouts/.
        layout_output_path = LAYOUTS_DIR / f"{Path(selected_sketch_path).stem}.json"

        output = process_sketch(
            output_path    = layout_output_path,
            sketch_path    = selected_sketch_path,
            lot_boundary   = lot_boundary,
            site_width_ft  = site_width_ft,
            site_height_ft = site_height_ft,
        )

        if output is None:
            return jsonify({"status": "error", "message": "Layout generation failed."}), 502

        if not layout_output_path.exists():
            return jsonify({"status": "error", "message": f"Output file not created: {layout_output_path}"}), 500

        try:
            site_data = json.loads(layout_output_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            return jsonify({"status": "error", "message": f"Invalid JSON output: {exc.msg}"}), 500

        fatal, warnings, patched = _validate_layout(site_data, lot_boundary)
        if fatal:
            return jsonify({"status": "error", "message": fatal, "warnings": warnings}), 502
        if patched:
            # Keep the archived raw layout consistent with what we hand to Unity.
            layout_output_path.write_text(json.dumps(site_data, indent=2), encoding="utf-8")
        for w in warnings:
            print(f"[layout warning] {w}")

        response = {
            "status":          "success",
            "message":         "Layout generated successfully.",
            "layout":          site_data,
            "output_path":     str(layout_output_path),
            "selected_sketch": Path(selected_sketch_path).name,
            "warnings":        warnings,
        }
        print(json.dumps(response, indent=4))
        return jsonify(response)

    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500
    finally:
        layout_generation_lock.release()


# ---------------------------------------------------------------------------
# Input image upload / listing  (/api/inputs)
# ---------------------------------------------------------------------------

_SAFE_NAME_RE = re.compile(r'[^A-Za-z0-9._-]+')

def _sanitize_filename(name: str) -> str:
    name = os.path.basename(name or "")
    name = _SAFE_NAME_RE.sub("_", name).strip("._")
    return name or "image"


@app.route('/api/inputs', methods=['GET'])
def list_inputs():
    """List uploaded source images available under input/."""
    try:
        names = sorted(
            p.name for p in INPUT_DIR.glob("*")
            if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS
        )
        return jsonify({"status": "success", "count": len(names), "inputs": names})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/inputs', methods=['POST'])
def upload_input():
    """Upload a source image (multipart 'file') into input/. Returns the stored name."""
    try:
        if "file" not in request.files:
            return jsonify({"status": "error", "message": "Missing 'file' in multipart form."}), 400

        f = request.files["file"]
        name = _sanitize_filename(f.filename)
        if Path(name).suffix.lower() not in IMAGE_EXTENSIONS:
            return jsonify({"status": "error", "message": f"Unsupported image type: {Path(name).suffix}"}), 400

        # Avoid clobbering an existing upload of the same name.
        dst = INPUT_DIR / name
        if dst.exists():
            stem, suffix = Path(name).stem, Path(name).suffix
            n = 2
            while (INPUT_DIR / f"{stem} ({n}){suffix}").exists():
                n += 1
            dst = INPUT_DIR / f"{stem} ({n}){suffix}"

        f.save(str(dst))
        return jsonify({"status": "success", "name": dst.name}), 201
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/inputs/<name>', methods=['GET'])
def get_input(name):
    """Serve a stored source image (for UI thumbnails)."""
    safe = _sanitize_filename(name)
    path = (INPUT_DIR / safe).resolve()
    if INPUT_DIR.resolve() not in path.parents or not path.is_file():
        return jsonify({"status": "error", "message": f"Input '{name}' not found"}), 404
    return send_file(str(path))


# ---------------------------------------------------------------------------
# Environment CRUD  (/api/environments)
# ---------------------------------------------------------------------------

@app.route('/api/environments', methods=['GET'])
def list_environments():
    """Return summary list of all environments (not archived). Optional ?kind= filter."""
    try:
        kind_filter = request.args.get("kind")
        dirs = [ENV_KINDS[kind_filter]] if kind_filter in ENV_KINDS else ENV_KINDS.values()

        summaries = []
        for d in dirs:
            for path in sorted(d.glob("*.json")):
                try:
                    summaries.append(_summarize(_load_json(path), path))
                except Exception as exc:
                    print(f"[WARN] Could not read environment {path.name}: {exc}")

        return jsonify({"status": "success", "count": len(summaries), "environments": summaries})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/environments', methods=['POST'])
def create_environment():
    """Create a new environment. Server assigns the ID and initial version.

    Body may include "kind": "user" (default) or "generated". Generated names are
    made unique (name, "name (2)", ...) so each layout is a distinct environment.
    """
    try:
        data = request.get_json(silent=True)
        if not data:
            return jsonify({"status": "error", "message": "Request body must be JSON"}), 400

        err = _validate_required(data, ["name"])
        if err:
            return jsonify({"status": "error", "message": err}), 400

        kind, target_dir = _kind_dir(ENV_KINDS, data.get("kind"), DEFAULT_ENV_KIND)

        with _STORE_LOCK:
            # Reuse an existing environment of the SAME kind with identical content
            # (same name too — Save As with a new name creates a real new record).
            dup = _find_duplicate([target_dir], data)
            if dup is not None:
                return jsonify({"status": "success", "id": dup["id"],
                                "name": dup["name"], "deduped": True}), 200

            env_id = _client_or_new_id(data)
            data["id"]      = env_id
            data["version"] = data.get("version") or 1
            data["name"]    = _unique_name(ENV_KINDS, data["name"])
            data.pop("kind", None)
            _set_kind_tag(data, kind, ENV_KINDS)

            _atomic_write(target_dir / f"{env_id}.json", data)
        return jsonify({"status": "success", "id": env_id, "name": data["name"]}), 201
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/environments/<env_id>', methods=['GET'])
def get_environment(env_id):
    """Return the full EnvironmentDef for a given ID."""
    path = _find_record(ENVIRONMENTS_DIR, ENV_KINDS, env_id)
    if path is None:
        return jsonify({"status": "error", "message": f"Environment '{env_id}' not found"}), 404
    try:
        return jsonify(_load_json(path))
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/environments/<env_id>', methods=['PUT'])
def update_environment(env_id):
    """Overwrite an existing environment in place, bumping its version."""
    path = _find_record(ENVIRONMENTS_DIR, ENV_KINDS, env_id)
    if path is None:
        return jsonify({"status": "error", "message": f"Environment '{env_id}' not found"}), 404

    try:
        data = request.get_json(silent=True)
        if not data:
            return jsonify({"status": "error", "message": "Request body must be JSON"}), 400

        with _STORE_LOCK:
            existing = _load_json(path)
            data["id"]      = env_id
            data["version"] = existing.get("version", 0) + 1
            if "favorite" not in data:
                data["favorite"] = existing.get("favorite", False)
            if "locked" not in data:
                data["locked"] = existing.get("locked", False)
            data.pop("kind", None)
            _set_kind_tag(data, path.parent.name, ENV_KINDS)

            _atomic_write(path, data)
        return jsonify({"status": "success", "id": env_id, "version": data["version"]})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/environments/<env_id>/archive', methods=['POST'])
def archive_environment(env_id):
    """Soft-delete: move environment to _archive/environments/. Never hard-deletes."""
    src = _find_record(ENVIRONMENTS_DIR, ENV_KINDS, env_id)
    if src is None:
        return jsonify({"status": "error", "message": f"Environment '{env_id}' not found"}), 404

    try:
        with _STORE_LOCK:
            dst = ARCHIVE_DIR / "environments" / f"{env_id}.json"
            shutil.move(str(src), str(dst))
        return jsonify({"status": "success", "message": f"Environment '{env_id}' archived."})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/environments/<env_id>/favorite', methods=['POST'])
def toggle_environment_favorite(env_id):
    """Flip the favorite flag on an environment (does not bump version)."""
    path = _find_record(ENVIRONMENTS_DIR, ENV_KINDS, env_id)
    if path is None:
        return jsonify({"status": "error", "message": f"Environment '{env_id}' not found"}), 404

    try:
        with _STORE_LOCK:
            data = _load_json(path)
            data["favorite"] = not data.get("favorite", False)
            _atomic_write(path, data)
        return jsonify({"status": "success", "id": env_id, "favorite": data["favorite"]})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


# ---------------------------------------------------------------------------
# Active-environment pointer  (/api/active)  — live multi-client sync
# The host (admin) publishes its loaded set: every loaded env id plus which one is
# active (editable / paints the terrain). Viewers poll this to mirror the whole set.
# Each version is read live from its env record so pollers detect loads/closes
# (the id list changes), an active switch (envId changes), and edits (a version
# bumps after the host's PUT).
# ---------------------------------------------------------------------------

def _env_pointer_entry(env_id: str):
    """Resolve one env id to {envId, version, name, updatedAt}, or None if the env
    was archived/deleted (self-healing: stale ids are silently dropped)."""
    path = _find_record(ENVIRONMENTS_DIR, ENV_KINDS, env_id) if env_id else None
    if path is None:
        return None
    env = _load_json(path)
    return {
        "envId":     env_id,
        "version":   env.get("version", 0),
        "name":      env.get("name"),
        "updatedAt": _iso_mtime(path),
    }


def _active_payload() -> dict:
    """Resolve the shared pointer to a poll-friendly payload: the active env's fields
    at the top level (back-compat) plus the full `loaded` list."""
    empty = {"status": "success", "envId": None, "version": 0, "name": None,
             "updatedAt": None, "loaded": []}
    if not ACTIVE_POINTER_FILE.is_file():
        return empty
    try:
        ptr = _load_json(ACTIVE_POINTER_FILE)
    except Exception:
        return empty

    active_id  = ptr.get("envId")
    loaded_ids = ptr.get("loadedIds") or ([active_id] if active_id else [])
    if active_id and active_id not in loaded_ids:
        loaded_ids.append(active_id)

    loaded = []
    seen = set()
    for env_id in loaded_ids:
        if not env_id or env_id in seen:
            continue
        seen.add(env_id)
        entry = _env_pointer_entry(env_id)
        if entry is not None:
            loaded.append(entry)

    active = next((e for e in loaded if e["envId"] == active_id), None)
    return {
        "status":    "success",
        "envId":     active["envId"] if active else None,
        "version":   active["version"] if active else 0,
        "name":      active["name"] if active else None,
        "updatedAt": active["updatedAt"] if active else None,
        "loaded":    loaded,
    }


@app.route('/api/active', methods=['GET'])
def get_active_environment():
    """Return the shared pointer: active env + every loaded env with live versions."""
    try:
        return jsonify(_active_payload())
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/active', methods=['POST'])
def set_active_environment():
    """Publish the host's loaded set. Body: {"envId": "<active id>", "loadedIds": [...]}.
    loadedIds is optional (defaults to just the active env, preserving the old
    single-env contract). Pass envId null with no loadedIds to clear the pointer."""
    try:
        data = request.get_json(silent=True) or {}
        env_id     = data.get("envId")
        loaded_ids = data.get("loadedIds") or []

        with _STORE_LOCK:
            if not env_id and not loaded_ids:
                if ACTIVE_POINTER_FILE.is_file():
                    ACTIVE_POINTER_FILE.unlink()
                return jsonify(_active_payload())

            if env_id and _find_record(ENVIRONMENTS_DIR, ENV_KINDS, env_id) is None:
                return jsonify({"status": "error", "message": f"Environment '{env_id}' not found"}), 404

            # Keep only ids that exist on the server; unknown ids (e.g. never-saved envs) are dropped.
            valid_ids = [i for i in loaded_ids
                         if i and _find_record(ENVIRONMENTS_DIR, ENV_KINDS, i) is not None]
            if env_id and env_id not in valid_ids:
                valid_ids.append(env_id)

            _atomic_write(ACTIVE_POINTER_FILE, {"envId": env_id, "loadedIds": valid_ids})
        return jsonify(_active_payload())
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


# ---------------------------------------------------------------------------
# Building CRUD  (/api/buildings)
# ---------------------------------------------------------------------------

@app.route('/api/buildings', methods=['GET'])
def list_buildings():
    """Return summary list of all buildings (not archived). Optional ?kind= filter."""
    try:
        kind_filter = request.args.get("kind")
        dirs = [BLDG_KINDS[kind_filter]] if kind_filter in BLDG_KINDS else BLDG_KINDS.values()

        summaries = []
        for d in dirs:
            for path in sorted(d.glob("*.json")):
                try:
                    summaries.append(_summarize(_load_json(path), path))
                except Exception as exc:
                    print(f"[WARN] Could not read building {path.name}: {exc}")

        return jsonify({"status": "success", "count": len(summaries), "buildings": summaries})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/buildings', methods=['POST'])
def create_building():
    """Create a new building. Server assigns the ID and initial version.

    Body may include "kind": "static" (default, user-authored) or "cached"
    (layout generation). Names are made unique across both kinds.
    """
    try:
        data = request.get_json(silent=True)
        if not data:
            return jsonify({"status": "error", "message": "Request body must be JSON"}), 400

        err = _validate_required(data, ["name"])
        if err:
            return jsonify({"status": "error", "message": err}), 400

        kind, target_dir = _kind_dir(BLDG_KINDS, data.get("kind"), DEFAULT_BLDG_KIND)

        with _STORE_LOCK:
            # Reuse an existing building of the SAME kind with identical content
            # (same name too — a renamed copy is a distinct record).
            dup = _find_duplicate([target_dir], data)
            if dup is not None:
                return jsonify({"status": "success", "id": dup["id"],
                                "name": dup["name"], "deduped": True}), 200

            bldg_id = _client_or_new_id(data)
            data["id"]      = bldg_id
            data["version"] = data.get("version") or 1
            data["name"]    = _unique_name(BLDG_KINDS, data["name"])
            data.pop("kind", None)
            _set_kind_tag(data, kind, BLDG_KINDS)

            _atomic_write(target_dir / f"{bldg_id}.json", data)
        return jsonify({"status": "success", "id": bldg_id, "name": data["name"]}), 201
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/buildings/<bldg_id>', methods=['GET'])
def get_building(bldg_id):
    """Return the full BuildingDef for a given ID."""
    path = _find_record(BUILDINGS_DIR, BLDG_KINDS, bldg_id)
    if path is None:
        return jsonify({"status": "error", "message": f"Building '{bldg_id}' not found"}), 404
    try:
        return jsonify(_load_json(path))
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/buildings/<bldg_id>', methods=['PUT'])
def update_building(bldg_id):
    """Overwrite an existing building in place, bumping its version."""
    path = _find_record(BUILDINGS_DIR, BLDG_KINDS, bldg_id)
    if path is None:
        return jsonify({"status": "error", "message": f"Building '{bldg_id}' not found"}), 404

    try:
        data = request.get_json(silent=True)
        if not data:
            return jsonify({"status": "error", "message": "Request body must be JSON"}), 400

        with _STORE_LOCK:
            existing = _load_json(path)
            data["id"]      = bldg_id
            data["version"] = existing.get("version", 0) + 1
            if "favorite" not in data:
                data["favorite"] = existing.get("favorite", False)
            data.pop("kind", None)
            _set_kind_tag(data, path.parent.name, BLDG_KINDS)

            _atomic_write(path, data)
        return jsonify({"status": "success", "id": bldg_id, "version": data["version"]})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/buildings/<bldg_id>/archive', methods=['POST'])
def archive_building(bldg_id):
    """Soft-delete: move building to _archive/buildings/. Never hard-deletes."""
    src = _find_record(BUILDINGS_DIR, BLDG_KINDS, bldg_id)
    if src is None:
        return jsonify({"status": "error", "message": f"Building '{bldg_id}' not found"}), 404

    try:
        with _STORE_LOCK:
            dst = ARCHIVE_DIR / "buildings" / f"{bldg_id}.json"
            shutil.move(str(src), str(dst))
        return jsonify({"status": "success", "message": f"Building '{bldg_id}' archived."})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@app.route('/api/buildings/<bldg_id>/favorite', methods=['POST'])
def toggle_building_favorite(bldg_id):
    """Flip the favorite flag on a building (does not bump version)."""
    path = _find_record(BUILDINGS_DIR, BLDG_KINDS, bldg_id)
    if path is None:
        return jsonify({"status": "error", "message": f"Building '{bldg_id}' not found"}), 404

    try:
        with _STORE_LOCK:
            data = _load_json(path)
            data["favorite"] = not data.get("favorite", False)
            _atomic_write(path, data)
        return jsonify({"status": "success", "id": bldg_id, "favorite": data["favorite"]})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


# ---------------------------------------------------------------------------
# Maintenance
# ---------------------------------------------------------------------------

@app.route('/api/maintenance/dedup', methods=['POST'])
def run_dedup():
    """Collapse duplicate buildings/environments already on disk and report what changed."""
    try:
        with _STORE_LOCK:
            report = _dedup_existing_records()
        return jsonify({"status": "success", **report})
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == '__main__':
    print("Starting Local Model to Unity Bridge Server...")
    print("  Health:       http://localhost:5002/health")
    print("  Models:       http://localhost:5002/api/list")
    print("  Layout gen:   http://localhost:5002/api/layout/generate  (POST)")
    print("  Sites:        http://localhost:5002/api/sites             (GET)")
    print("  Inputs:       http://localhost:5002/api/inputs            (GET, POST)")
    print("  Environments: http://localhost:5002/api/environments      (GET, POST)")
    print("  Buildings:    http://localhost:5002/api/buildings         (GET, POST)")
    # Debug (auto-reload + Werkzeug debugger) is opt-in: set M2U_DEBUG=1. The server
    # binds 0.0.0.0 so LAN viewers (VR headset / 2nd PC) can reach it, which is
    # exactly why the interactive debugger must not be on by default.
    app.run(host='0.0.0.0', port=5002, debug=os.getenv("M2U_DEBUG", "0") == "1")
