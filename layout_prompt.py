import os
import time
import random
import json
import base64
import subprocess
import sys
from pathlib import Path
from typing import Iterable
from dotenv import load_dotenv
from google import genai
from google.genai import types
import anthropic
import matplotlib.pyplot as plt
from matplotlib.patches import Rectangle, Circle

load_dotenv()

# =========================================================
# Provider selection
# =========================================================
CURRENT_PROVIDER = "claude"  # "gemini" or "claude"

# =========================================================
# Gemini configuration
# =========================================================
GEMINI_MODEL_ID = "gemini-2.5-flash"
GEMINI_API_KEY = os.getenv("GOOGLE_API_KEY")

# =========================================================
# Claude configuration
# =========================================================
CLAUDE_MODEL_ID = "claude-opus-4-8"
CLAUDE_API_KEY = os.getenv("ANTHROPIC_API_KEY")
CLAUDE_MAX_TOKENS = 16000

# =========================================================
# Shared configuration
# =========================================================
# Anchor default paths to this module's directory so they resolve correctly
# regardless of the caller's working directory (e.g. the server runs from
# server/, where a relative "prompts/..." path would not exist).
_MODULE_DIR = Path(__file__).resolve().parent
if str(_MODULE_DIR) not in sys.path:
    sys.path.insert(0, str(_MODULE_DIR))
from sketch_prep import prepare_sketch, normalize_rotation_request, AUTO as AUTO_ROTATION  # noqa: E402
from brief_prompt import BRIEF_MODEL_ID, parse_brief, is_empty_brief, _transform as _transform_schema  # noqa: E402,F401
from layout_schema import LAYOUT_SCHEMA, LAYOUT_OUTPUT_SCHEMA  # noqa: E402,F401

# Explicit so an SDK default change can never silently alter the layout call.
CLAUDE_EFFORT = "high"
# Structured output on the layout call is OFF: the full layout schema exceeds the API's grammar
# size limit, and the accepted subset (layout_schema.LAYOUT_OUTPUT_SCHEMA) made the model return
# a near-empty layout in testing (2026-09-09). The prompt plus the server's jsonschema check keep
# the JSON in shape instead. Flip this to experiment; a rejected schema still falls back cleanly.
LAYOUT_STRUCTURED_OUTPUT = False

PROMPT_PATH = _MODULE_DIR / "prompts" / "site_parsing.md"
OUTPUT_PATH = _MODULE_DIR / "sample_output.json"
MAX_RETRIES = 3

# =========================================================
# Client setup
# =========================================================
# Each client is only constructed when its API key is present, so the module
# can be imported (and one provider used) even if the other key is missing.
gemini_client = genai.Client(api_key=GEMINI_API_KEY) if GEMINI_API_KEY else None
claude_client = anthropic.Anthropic(api_key=CLAUDE_API_KEY) if CLAUDE_API_KEY else None


def normalize_lot_boundary(lot_boundary):
    if lot_boundary is None:
        return None

    if not isinstance(lot_boundary, Iterable) or isinstance(lot_boundary, (str, bytes)):
        raise ValueError("lot_boundary must be a list-like collection of [y, x] points")

    normalized = []
    for idx, point in enumerate(lot_boundary):
        if not isinstance(point, Iterable) or isinstance(point, (str, bytes)):
            raise ValueError(f"lot_boundary point {idx} must be [y, x]")

        values = list(point)
        if len(values) != 2:
            raise ValueError(f"lot_boundary point {idx} must contain exactly 2 values: [y, x]")

        y, x = values
        if not isinstance(y, (int, float)) or not isinstance(x, (int, float)):
            raise ValueError(f"lot_boundary point {idx} must contain numeric [y, x] values")

        if not (0 <= y <= 1000 and 0 <= x <= 1000):
            raise ValueError(
                f"lot_boundary point {idx} is out of normalized range 0-1000: {values}"
            )

        normalized.append([int(round(y)), int(round(x))])

    if len(normalized) < 3:
        raise ValueError("lot_boundary must have at least 3 points")

    return normalized


def build_axis_note(site_width_ft=None, site_height_ft=None, sketch_info=None):
    """
    Spells out which image axis each real dimension spans, plus what was done to
    the image, so the model lays out for the parcel's true proportions. Empty
    when no dimensions are known (auto-trace mode estimates them itself).
    """
    if not site_width_ft or not site_height_ft:
        return ""
    ft_y = site_width_ft / 1000.0
    ft_x = site_height_ft / 1000.0
    lines = [
        f"Axes: the first coordinate y runs down the image and spans site_width_ft = {site_width_ft:g} ft "
        f"({ft_y:.3f} ft per canvas unit). The second coordinate x runs across the image and spans "
        f"site_height_ft = {site_height_ft:g} ft ({ft_x:.3f} ft per canvas unit)."
    ]
    if sketch_info and sketch_info.get("rotation_deg"):
        lines.append(
            f"The sketch was rotated {sketch_info['rotation_deg']} degrees counter-clockwise before you "
            "received it so its long side matches the parcel. Read labels in that rotated frame."
        )
    if sketch_info and sketch_info.get("resampled"):
        lines.append(
            f"The image was resampled to the parcel's true proportions ({site_width_ft:g} ft tall by "
            f"{site_height_ft:g} ft across), so every pixel covers the same distance in feet on both "
            "axes: a square in the image is a square on the ground. Use the sketch for arrangement and "
            "adjacency; size each footprint so it is buildable at these real dimensions."
        )
    else:
        lines.append(
            "The image is NOT to scale: canvas units differ in feet between the two axes. "
            "Size every bounding box from the feet per unit above, not from how square it looks."
        )
    long_ft = max(site_width_ft, site_height_ft)
    short_ft = min(site_width_ft, site_height_ft)
    if short_ft > 0 and long_ft / short_ft >= 2.0:
        along = "y (down the image)" if site_width_ft >= site_height_ft else "x (across the image)"
        lines.append(
            f"This parcel is long and narrow ({long_ft:g} by {short_ft:g} ft). Arrange the program along "
            f"the long axis {along}. Keep buildings at least 20 ft deep across the short axis, leave room "
            "for a path along the long axis, and keep every box inside the boundary."
        )
    return "\n".join(lines)


# Free-text designer notes are capped so a pasted document cannot crowd out the prompt.
NOTES_MAX_CHARS = 2000


BRIEF_RULES = """The brief below is the authoritative reading of those notes (brief_prompt.py parsed them). \
Follow it exactly:
- buildings: each entry is one building the designer wants. Emit it exactly once in generated_buildings \
(generated_objects only for a kiosk-like structure) with "area_name" equal to its "name" and "brief_ref" \
equal to its "ref". Use "where" to pick which drawn block it is. Copy "style" when given and leave style \
out otherwise. Use "floors" when given. Never draw a block the sketch does not show; if no drawn block \
fits, leave the entry out and the server will report it.
- splits: a group with count N is ONE drawn block that becomes N buildings. Emit N generated_buildings \
entries that divide that block into N equal shares along its longer side, touching (neighbours share an \
edge), in member order starting at the end with the lowest coordinate. Each carries its member's \
brief_ref, name, style and floors.
- program_totals: spread the quantity over the blocks listed in "across" (every drawn block when empty) \
by adjusting floors and uses. Never change the block count to meet a total.
- paths and fences: each entry with "exclude": false is a drawn walkway, road or fence the designer \
describes; emit it with "brief_ref" equal to its "ref" and the given material or type, width or height. \
For an entry with "exclude": true, emit nothing that matches it.
- props: for each entry with "exclude": false emit prefab_instances of that type in the arrangement and \
count given, each with "brief_ref" equal to its "ref". An excluded type is not emitted at all.
- ignored and unparsed sentences need no action.
"""


def build_brief_block(notes, brief=None):
    """
    Optional designer notes about the sketch (program, floor counts, names, sizes)
    appended after the site context, followed by the structured brief when one was
    parsed from them. Empty string when there is nothing usable.
    """
    if not isinstance(notes, str):
        return ""
    text = notes.strip()
    if not text:
        return ""
    if len(text) > NOTES_MAX_CHARS:
        text = text[:NOTES_MAX_CHARS].rstrip() + " [truncated]"
    block = (
        "\nDesigner notes for this sketch. Where they conflict with visual massing cues "
        "(floor counts, uses, names, sizes) the notes win. Still keep every placement inside "
        "lot_boundary and follow the output schema as documented. If the notes assign a style "
        "letter A to F to a building (matched by the name or use they give), emit "
        "\"style\": \"<LETTER>\" on that generated_buildings entry; leave style out of every "
        "other building and never choose a letter yourself. If the notes or the sketch name a "
        "building (a shop, a theater), emit \"sign\": \"<WORD>\" on its entry, one uppercase "
        "word of at most 16 letters; leave sign out of unnamed buildings:\n"
        f"{text}\n"
    )
    if isinstance(brief, dict) and not is_empty_brief(brief):
        block += (
            "\n" + BRIEF_RULES +
            "\n```json\n" + json.dumps(brief, indent=2) + "\n```\n"
        )
    return block


# Kept for callers that only have notes.
build_notes_block = build_brief_block


def build_runtime_site_context(site_width_ft=None, site_height_ft=None, lot_boundary=None, sketch_info=None,
                               notes=None, brief=None):
    site_scale = {
        "site_width_ft": site_width_ft,
        "site_height_ft": site_height_ft,
        "normalized_canvas": [0, 0, 1000, 1000],
        "lot_boundary": lot_boundary,
        "scale_note": (
            "All placements must fall within lot_boundary. Bounding boxes define placement. "
            "Real-world dimensions define scale."
        ),
    }

    if lot_boundary is not None:
        # Authoritative boundary supplied (named site preset or explicit bounds):
        # the model must keep it as-is and confine everything to it.
        directive = (
            "The lot_boundary above is AUTHORITATIVE. Echo it verbatim into "
            "site_scale.lot_boundary and keep every placement inside it."
        )
        axis_note = build_axis_note(site_width_ft, site_height_ft, sketch_info)
        if axis_note:
            directive += "\n" + axis_note
    else:
        # Auto-derive: no boundary given, so the model traces the parcel from the
        # sketch. Unity shapes the terrain to whatever polygon comes back, painting
        # the area outside it as water — so the traced outline must be accurate.
        directive = (
            "No lot_boundary is supplied. You MUST trace the site/parcel outline from "
            "the sketch and output it as site_scale.lot_boundary — an ordered list of "
            "[y, x] vertices (normalized 0-1000) following the parcel edge. Treat that "
            "edge as the edge of the buildable area; everything outside it is off-site "
            "(water/void). Keep every placement inside the boundary you trace."
        )
        if site_width_ft is None and site_height_ft is None:
            directive += (
                " Also estimate site_width_ft and site_height_ft (in feet) from the "
                "sketch's scale cues and fill them into site_scale."
            )

    return (
        "\nRuntime site context for this request:\n"
        f"{directive}\n\n"
        "```json\n"
        f"{json.dumps(site_scale, indent=2)}\n"
        "```\n"
        + build_brief_block(notes, brief)
    )

def visualize_output(site_data):

    terrain_colors = {
    "grass": "#7fbf7f",
    "pavement": "#bdbdbd",
    "asphalt": "#8c8c8c"
    }

    prefab_colors = {
        "oak_tree": "#2e8b57",
        "wooden_bench": "#8b5a2b"
    }

    default_terrain_color = "#cccccc"
    default_prefab_color = "#4f81bd"

    # -----------------------------
    # Styling helpers
    # -----------------------------
    terrain_colors = {
        "grass": "#7fbf7f",
        "pavement": "#bdbdbd",
        "asphalt": "#8c8c8c"
    }

    prefab_colors = {
        "oak_tree": "#2e8b57",
        "wooden_bench": "#8b5a2b"
    }

    default_terrain_color = "#cccccc"
    default_prefab_color = "#4f81bd"

    # -----------------------------
    # Figure setup
    # -----------------------------
    canvas = site_data["site_scale"]["normalized_canvas"]
    x_min, y_min, x_max, y_max = canvas

    fig, ax = plt.subplots(figsize=(10, 10))
    ax.set_xlim(x_min, x_max)
    ax.set_ylim(y_min, y_max)
    ax.set_aspect("equal")

    # Optional: invert Y so it feels more like screen / layout coordinates
    ax.invert_yaxis()

    # -----------------------------
    # Draw terrain zones
    # -----------------------------
    for zone in site_data["terrain_zones"]:
        x1, y1, x2, y2 = zone["bounding_box"]
        width = x2 - x1
        height = y2 - y1
        color = terrain_colors.get(zone["terrain_type"], default_terrain_color)

        rect = Rectangle(
            (x1, y1),
            width,
            height,
            facecolor=color,
            edgecolor="black",
            linewidth=1,
            alpha=0.6
        )
        ax.add_patch(rect)

        cx = x1 + width / 2
        cy = y1 + height / 2
        ax.text(
            cx,
            cy,
            f'{zone["area_name"]}\n({zone["terrain_type"]})',
            ha="center",
            va="center",
            fontsize=9,
            color="black"
        )

    # -----------------------------
    # Draw generated objects (buildings)
    # -----------------------------
    for obj in site_data["generated_objects"]:
        x1, y1, x2, y2 = obj["bounding_box"]
        width = x2 - x1
        height = y2 - y1

        rect = Rectangle(
            (x1, y1),
            width,
            height,
            facecolor="orange",
            edgecolor="darkred",
            linewidth=2,
            alpha=0.7
        )
        ax.add_patch(rect)

        cx, cy = obj["center_point"]
        ax.text(
            cx,
            cy,
            f'{obj["area_name"]}\n({obj["object_type"]})',
            ha="center",
            va="center",
            fontsize=10,
            fontweight="bold",
            color="black"
        )

    for obj in site_data["generated_buildings"]:
        x1, y1, x2, y2 = obj["bounding_box"]
        width = x2 - x1
        height = y2 - y1

        rect = Rectangle(
            (x1, y1),
            width,
            height,
            facecolor="orange",
            edgecolor="darkred",
            linewidth=2,
            alpha=0.7
        )
        ax.add_patch(rect)

        cx, cy = obj["center_point"]
        ax.text(
            cx,
            cy,
            f'{obj["area_name"]}',
            ha="center",
            va="center",
            fontsize=10,
            fontweight="bold",
            color="black"
        )

    # -----------------------------
    # Draw prefab instances as circles
    # -----------------------------
    for prefab in site_data["prefab_instances"]:
        cx, cy = prefab["center_point"]
        x1, y1, x2, y2 = prefab["footprint_box"]

        # Radius based on footprint size
        radius = max(x2 - x1, y2 - y1) * 0.18 * prefab.get("scale_multiplier", 1.0)
        color = prefab_colors.get(prefab["prefab_type"], default_prefab_color)

        circle = Circle(
            (cx, cy),
            radius=radius,
            facecolor=color,
            edgecolor="black",
            linewidth=1.5,
            alpha=0.9
        )
        ax.add_patch(circle)

        ax.text(
            cx,
            cy,
            f'{prefab["area_name"]}\n({prefab["prefab_type"]})',
            ha="center",
            va="center",
            fontsize=8,
            color="white"
        )

    # -----------------------------
    # Draw fences as polylines
    # -----------------------------
    for fence in site_data.get("fences", []):
        pts = fence.get("points", [])
        if len(pts) < 2:
            continue
        # Points are [y, x]; plot value[0] on the x-axis to match center_point usage above.
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        ax.plot(xs, ys, color="saddlebrown", linewidth=2.5, linestyle="-", marker="o", markersize=3)
        ax.text(
            xs[0],
            ys[0],
            f'{fence.get("area_name", "fence")}\n({fence.get("fence_type", "")})',
            ha="center",
            va="center",
            fontsize=8,
            color="saddlebrown"
        )

    # -----------------------------
    # Final plot formatting
    # -----------------------------
    ax.set_title("Site Layout Visualization", fontsize=14, pad=12)
    ax.set_xlabel("Normalized X")
    ax.set_ylabel("Normalized Y")
    ax.grid(True, linestyle="--", alpha=0.3)

    plt.tight_layout()
    plt.show()


def extract_json(response: str) -> str:
    try:
        start = response.index('{')
        end = response.rindex('}')
        return response[start:end + 1]
    except ValueError as e:
        raise ValueError(f"Could not extract JSON from response: {e}\n\nRaw response:\n{response}")


class ResponseTruncatedError(Exception):
    """
    Raised when the model stopped because it hit the output token limit,
    not because it actually finished. The JSON will be incomplete, so this
    is treated as a distinct failure mode from rate limits / API errors.
    """
    pass


def detect_image_media_type(image_bytes: bytes) -> str:
    """
    Sniff the image MIME type from its magic bytes. Both Claude and Gemini
    reject a request whose declared media type doesn't match the actual image
    (e.g. sending a JPEG tagged as image/png is a 400), so we can't hardcode
    one. Falls back to image/png for unrecognized data.
    """
    if image_bytes.startswith(b"\x89PNG\r\n\x1a\n"):
        return "image/png"
    if image_bytes.startswith(b"\xff\xd8\xff"):
        return "image/jpeg"
    if image_bytes.startswith(b"GIF8"):
        return "image/gif"
    if image_bytes[:4] == b"RIFF" and image_bytes[8:12] == b"WEBP":
        return "image/webp"
    return "image/png"


def _call_model(prompt_text, runtime_context, image_bytes):
    """
    Makes a single API call to whichever provider CURRENT_PROVIDER points to.
    Returns the raw text response from the model (not yet JSON-extracted).
    Raises on any API error so the retry wrapper can decide what to do with it.
    Raises ResponseTruncatedError specifically if the response was cut off
    before the model finished (i.e. it hit the max output token limit).
    """
    if CURRENT_PROVIDER == "gemini":
        if gemini_client is None:
            raise RuntimeError(
                "Gemini provider selected but GOOGLE_API_KEY is not set. "
                "Add it to your .env file."
            )

        media_type = detect_image_media_type(image_bytes)

        contents = [prompt_text]
        if runtime_context:
            contents.append(runtime_context)
        contents.append(types.Part.from_bytes(data=image_bytes, mime_type=media_type))

        response = gemini_client.models.generate_content(
            model=GEMINI_MODEL_ID,
            contents=contents,
        )

        finish_reason = None
        if getattr(response, "candidates", None):
            finish_reason = getattr(response.candidates[0], "finish_reason", None)
        if finish_reason is not None and "MAX_TOKENS" in str(finish_reason).upper():
            raise ResponseTruncatedError(
                "Gemini's response was truncated before it finished the JSON "
                "(hit the max output token limit). Increase the output token "
                "budget for this model and try again."
            )

        return response.text

    elif CURRENT_PROVIDER == "claude":
        if claude_client is None:
            raise RuntimeError(
                "Claude provider selected but ANTHROPIC_API_KEY is not set. "
                "Add it to your .env file."
            )

        media_type = detect_image_media_type(image_bytes)
        image_b64 = base64.standard_b64encode(image_bytes).decode("utf-8")

        # The prompt file is identical on every call, so it goes in `system` behind a cache
        # breakpoint; everything that varies (image, site context, notes, brief) is the user turn.
        system_blocks = [{"type": "text", "text": prompt_text, "cache_control": {"type": "ephemeral"}}]
        user_content = [
            {"type": "image", "source": {"type": "base64", "media_type": media_type, "data": image_b64}},
            {"type": "text", "text": runtime_context or "Produce the layout JSON for this sketch."},
        ]

        # Vision + spatial-reasoning task with a large structured-JSON output, so
        # we turn on adaptive thinking and stream the response. Streaming keeps us
        # clear of the SDK's non-streaming HTTP timeout guard at high max_tokens,
        # and get_final_message() reassembles the full message for us. The output
        # schema (site_scale + buildings typed, the rest free; the full schema is too
        # large for the grammar compiler, see layout_schema.py) constrains the output;
        # if the API rejects it, one retry without the schema keeps generation working
        # and extract_json takes over either way.
        def _stream(output_config):
            with claude_client.messages.stream(
                model=CLAUDE_MODEL_ID,
                max_tokens=CLAUDE_MAX_TOKENS,
                thinking={"type": "adaptive"},
                output_config=output_config,
                system=system_blocks,
                messages=[{"role": "user", "content": user_content}],
            ) as stream:
                return stream.get_final_message()

        if LAYOUT_STRUCTURED_OUTPUT:
            structured = {"effort": CLAUDE_EFFORT,
                          "format": {"type": "json_schema", "schema": _transform_schema(LAYOUT_OUTPUT_SCHEMA)}}
            try:
                response = _stream(structured)
            except anthropic.BadRequestError as exc:
                msg = str(exc)
                if not any(k in msg for k in ("output_config", "format", "schema")):
                    raise
                print(f"⚠️ Structured output rejected ({msg[:200]}); retrying without a schema.")
                response = _stream({"effort": CLAUDE_EFFORT})
        else:
            response = _stream({"effort": CLAUDE_EFFORT})

        if response.stop_reason == "refusal":
            raise RuntimeError("Claude declined to produce a layout for this sketch (stop_reason=refusal).")
        if response.stop_reason == "max_tokens":
            raise ResponseTruncatedError(
                f"Claude's response was truncated at the {CLAUDE_MAX_TOKENS}-token "
                "limit before it finished the JSON. Increase CLAUDE_MAX_TOKENS and "
                "try again."
            )

        usage = getattr(response, "usage", None)
        if usage is not None:
            print(f"📊 tokens: in {getattr(usage, 'input_tokens', '?')}, "
                  f"cache read {getattr(usage, 'cache_read_input_tokens', 0) or 0}, "
                  f"out {getattr(usage, 'output_tokens', '?')}")

        return "".join(
            block.text for block in response.content if getattr(block, "type", None) == "text"
        )

    else:
        raise ValueError(
            f"Unknown CURRENT_PROVIDER: {CURRENT_PROVIDER!r}. Expected 'gemini' or 'claude'."
        )


def call_model_with_retries(prompt_text, runtime_context, image_bytes):
    """
    Retry + dispatch logic. Looks at CURRENT_PROVIDER and calls the matching
    provider via _call_model, retrying on rate-limit/overload errors. Returns
    the extracted JSON string, or None if every retry failed / a permanent
    error occurred.
    """
    output = None

    for attempt in range(MAX_RETRIES):
        try:
            print(f"📡 [Calling {CURRENT_PROVIDER} (Attempt {attempt+1})...")
            raw_output = _call_model(prompt_text, runtime_context, image_bytes)
            output = extract_json(raw_output)
            break

        except ResponseTruncatedError as e:
            # Retrying with the same token budget would just truncate again,
            # so fail loud immediately instead of burning retries.
            print(f"✂️ {e}")
            break

        except Exception as e:
            err_msg = str(e)
            if "429" in err_msg or "503" in err_msg or "529" in err_msg:
                # If we hit a wall, wait a full minute + jitter
                wait = 60 + random.uniform(5, 15)
                print(f"🚨 API Overloaded. Sleeping {wait:.1f}s before retry...")
                time.sleep(wait)
            else:
                print(f"❌ Permanent Error: {e}")
                break

    return output


def process_sketch(
    lot_boundary=None,
    site_width_ft=None,
    site_height_ft=None,
    sketch_path=None,
    prompt_path=PROMPT_PATH,
    output_path=OUTPUT_PATH,
    sketch_rotation=AUTO_ROTATION,
    notes=None,
    brief=None,
):
    """
    Runs one sketch through the layout model. Returns (json_text_or_None, sketch_info).

    notes is optional free text from the designer (program, floors, names) that is
    appended to the runtime context; brief is the structured reading of those notes
    from brief_prompt.parse_brief (None to send the notes alone); see build_brief_block.

    sketch_info describes what sketch_prep did to the image (rotation, resample)
    and is None when the image was sent untouched or nothing ran.
    """

    if sketch_path is None:
        print("No sketch path was provided")
        return None, None

    sketch_path = Path(sketch_path)
    prompt_path = Path(prompt_path)
    output_path = Path(output_path)

    if not sketch_path.exists():
        print(f"{sketch_path} does not exist")
        return None, None

    if not prompt_path.exists():
        print(f"{prompt_path} does not exist")
        return None, None

    try:
        normalized_boundary = normalize_lot_boundary(lot_boundary)
    except ValueError as exc:
        print(f"Invalid lot boundary: {exc}")
        return None, None

    image_bytes = sketch_path.read_bytes()

    # Orient and resample the sketch to the parcel's real proportions when we know
    # them (Unity's site-targeted requests). Auto-trace requests keep the image as
    # drawn unless an explicit rotation was asked for.
    try:
        sketch_rotation = normalize_rotation_request(sketch_rotation)
    except ValueError as exc:
        print(f"Invalid sketch rotation: {exc}")
        return None, None

    sketch_info = None
    try:
        image_bytes, sketch_info = prepare_sketch(
            image_bytes, site_width_ft, site_height_ft, requested=sketch_rotation
        )
    except Exception as exc:  # unreadable image: send the original bytes, the model may still cope
        print(f"Sketch prep skipped ({exc}); sending the image as uploaded.")
        image_bytes = sketch_path.read_bytes()
    if sketch_info is not None:
        print(f"Sketch prep: rotation {sketch_info['rotation_deg']} deg"
              f"{' (auto)' if sketch_info['auto_rotated'] else ''}, "
              f"{sketch_info['original_px']} -> {sketch_info['prepared_px']} px"
              f"{', resampled to site proportions' if sketch_info['resampled'] else ''}")

    runtime_context = build_runtime_site_context(
        site_width_ft=site_width_ft,
        site_height_ft=site_height_ft,
        lot_boundary=normalized_boundary,
        sketch_info=sketch_info,
        notes=notes,
        brief=brief,
    )

    prompt_text = prompt_path.read_text(encoding="utf-8")

    print(f"🔄 [Processing {sketch_path}...")
    output = call_model_with_retries(prompt_text, runtime_context, image_bytes)

    if output is not None:
        output_path.write_text(output, encoding="utf-8")
        print(f"Success: {output_path.name}")
        if normalized_boundary is not None:
            print(f"Applied lot boundary with {len(normalized_boundary)} points")

    return output, sketch_info


def choose_sketch_path():
    # On macOS, Tkinter crashes when called from a background thread (Flask handler)
    # because NSWindow must be instantiated on the main thread. Use osascript instead,
    # which spawns a separate process and is safe from any thread.
    if sys.platform == "darwin":
        script = (
            'POSIX path of (choose file with prompt "Select a sketch image" '
            'of type {"public.image"})'
        )
        result = subprocess.run(
            ["osascript", "-e", script],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode == 0:
            selected_path = result.stdout.strip()
            if selected_path:
                return Path(selected_path)
    else:
        try:
            import tkinter as tk
            from tkinter import filedialog

            root = tk.Tk()
            root.withdraw()
            root.attributes("-topmost", True)

            selected_path = filedialog.askopenfilename(
                title="Select a sketch image",
                filetypes=[
                    ("Image files", "*.png *.jpg *.jpeg *.webp *.bmp"),
                    ("All files", "*.*"),
                ],
            )

            root.destroy()
            if selected_path:
                return Path(selected_path)
        except Exception:
            pass

    user_input = input("Enter the full path to your sketch image (or press Enter to cancel): ").strip()
    if not user_input:
        return None

    return Path(user_input)

if __name__ == "__main__":
    # selected_sketch_path = choose_sketch_path()
    # if selected_sketch_path is None:
    #     print("No sketch selected. Exiting.")
    #     raise SystemExit(0)

    # layout_output = process_sketch(
    #     sketch_path=selected_sketch_path,
    #     lot_boundary=[[0, 0], [0, 1000], [1000, 0], [1000, 1000]],
    #     site_width_ft=1000,
    #     site_height_ft=1000,
    # )

    with open('sample_output.json', 'r', encoding='utf-8') as file:
        data = json.load(file)
        print(data)
        visualize_output(data)
        