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


def build_runtime_site_context(site_width_ft=None, site_height_ft=None, lot_boundary=None):
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

        text_parts = [prompt_text]
        if runtime_context:
            text_parts.append(runtime_context)
        combined_text = "\n".join(text_parts)

        media_type = detect_image_media_type(image_bytes)
        image_b64 = base64.standard_b64encode(image_bytes).decode("utf-8")

        # Vision + spatial-reasoning task with a large structured-JSON output, so
        # we turn on adaptive thinking and stream the response. Streaming keeps us
        # clear of the SDK's non-streaming HTTP timeout guard at high max_tokens,
        # and get_final_message() reassembles the full message for us.
        with claude_client.messages.stream(
            model=CLAUDE_MODEL_ID,
            max_tokens=CLAUDE_MAX_TOKENS,
            thinking={"type": "adaptive"},
            messages=[
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "image",
                            "source": {
                                "type": "base64",
                                "media_type": media_type,
                                "data": image_b64,
                            },
                        },
                        {
                            "type": "text",
                            "text": combined_text,
                        },
                    ],
                }
            ],
        ) as stream:
            response = stream.get_final_message()

        if response.stop_reason == "max_tokens":
            raise ResponseTruncatedError(
                f"Claude's response was truncated at the {CLAUDE_MAX_TOKENS}-token "
                "limit before it finished the JSON. Increase CLAUDE_MAX_TOKENS and "
                "try again."
            )

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
):

    if sketch_path is None:
        print("No sketch path was provided")
        return

    sketch_path = Path(sketch_path)
    prompt_path = Path(prompt_path)
    output_path = Path(output_path)

    if not sketch_path.exists():
        print(f"{sketch_path} does not exist")
        return
    
    if not prompt_path.exists():
        print(f"{prompt_path} does not exist")
        return

    try:
        normalized_boundary = normalize_lot_boundary(lot_boundary)
    except ValueError as exc:
        print(f"Invalid lot boundary: {exc}")
        return

    runtime_context = build_runtime_site_context(
        site_width_ft=site_width_ft,
        site_height_ft=site_height_ft,
        lot_boundary=normalized_boundary,
    )

    prompt_text = prompt_path.read_text(encoding="utf-8")
    image_bytes = sketch_path.read_bytes()

    print(f"🔄 [Processing {sketch_path}...")
    output = call_model_with_retries(prompt_text, runtime_context, image_bytes)

    if output is not None:
        output_path.write_text(output, encoding="utf-8")
        print(f"Success: {output_path.name}")
        if normalized_boundary is not None:
            print(f"Applied lot boundary with {len(normalized_boundary)} points")

    return output


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
        