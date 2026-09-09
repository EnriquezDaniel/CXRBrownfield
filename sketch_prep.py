"""
Sketch preparation before the layout model sees it: orientation and resampling.

Coordinate convention (matches Unity's LayoutConverter / SiteFit and the prompt):
the model's canvas is [y, x] with y running DOWN the image and x running ACROSS.
`site_width_ft` is the real extent along y (image rows, Unity X) and
`site_height_ft` the extent along x (image columns, Unity Z); Unity turns the
plan half a turn so the top of the image lands at +X. So a site whose
width_ft exceeds its height_ft is "tall" on the page: its long side runs up the
page, and a sketch drawn landscape for such a site has to be rotated.

Pure functions (Pillow only, no Flask) so they can be checked with a plain
python run; see docs/server-api.md for the request/response fields.
"""
from __future__ import annotations

import io

from PIL import Image, ImageOps

# A side has to be this much longer than the other before an image or a site
# counts as tall/wide. Near-square inputs stay as drawn.
ASPECT_TOL = 1.15
# Longest edge of the prepared image. Claude downsamples anything above ~1568 px.
LONG_SIDE_PX = 1568
# Floor for the short edge so a very thin parcel keeps legible strokes.
MIN_SHORT_PX = 160
VALID_ROTATIONS = (0, 90, 180, 270)
AUTO = "auto"


def normalize_rotation_request(value):
    """'auto' (default for None) or one of 0/90/180/270 as an int. Raises ValueError otherwise."""
    if value is None:
        return AUTO
    if isinstance(value, str):
        text = value.strip().lower()
        if text in ("", AUTO):
            return AUTO
        try:
            value = int(float(text))
        except ValueError as exc:
            raise ValueError(f"sketch_rotation must be 'auto' or one of {VALID_ROTATIONS}, got {value!r}") from exc
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"sketch_rotation must be 'auto' or one of {VALID_ROTATIONS}, got {value!r}")
    deg = int(round(value)) % 360
    if deg not in VALID_ROTATIONS:
        raise ValueError(f"sketch_rotation must be 'auto' or one of {VALID_ROTATIONS}, got {value!r}")
    return deg


def decide_rotation(img_w, img_h, site_width_ft, site_height_ft, requested=AUTO):
    """
    Degrees (counter-clockwise) to rotate the sketch before sending it.

    An explicit request wins. In auto mode the sketch is rotated 90 degrees only
    when it is clearly landscape while the site is clearly tall on the page (or
    the reverse). Missing sizes, or an ambiguous image or site, mean 0.
    """
    req = normalize_rotation_request(requested)
    if req != AUTO:
        return req
    if not img_w or not img_h or not site_width_ft or not site_height_ft:
        return 0
    if img_w <= 0 or img_h <= 0 or site_width_ft <= 0 or site_height_ft <= 0:
        return 0

    image_tall = img_h / img_w > ASPECT_TOL
    image_wide = img_w / img_h > ASPECT_TOL
    site_tall = site_width_ft / site_height_ft > ASPECT_TOL   # width_ft spans the rows
    site_wide = site_height_ft / site_width_ft > ASPECT_TOL
    if (image_wide and site_tall) or (image_tall and site_wide):
        return 90
    return 0


def target_size_px(site_width_ft, site_height_ft):
    """(columns, rows) of the resampled image so feet per pixel match on both axes."""
    long_ft = max(site_width_ft, site_height_ft)
    short_ft = min(site_width_ft, site_height_ft)
    short_px = max(MIN_SHORT_PX, int(round(LONG_SIDE_PX * short_ft / long_ft)))
    if site_width_ft >= site_height_ft:
        return (short_px, LONG_SIDE_PX)      # tall on the page: rows carry the long side
    return (LONG_SIDE_PX, short_px)


def prepare_sketch(image_bytes, site_width_ft=None, site_height_ft=None, requested=AUTO):
    """
    Rotate and resample a sketch for the layout model. Returns (png_bytes, info).

    With both site dimensions the image is rotated per decide_rotation and then
    resampled to the parcel's true proportions (uniform feet per pixel). Without
    them (auto-trace mode) only an explicit rotation is applied.
    """
    req = normalize_rotation_request(requested)
    img = Image.open(io.BytesIO(image_bytes))
    img.load()
    img = ImageOps.exif_transpose(img) or img
    original = img.size

    has_dims = bool(site_width_ft and site_height_ft and site_width_ft > 0 and site_height_ft > 0)
    if has_dims:
        rotation = decide_rotation(original[0], original[1], site_width_ft, site_height_ft, req)
    else:
        rotation = 0 if req == AUTO else req
    auto_rotated = req == AUTO and rotation != 0

    if rotation:
        img = img.rotate(rotation, expand=True)   # Pillow rotates counter-clockwise

    resampled = False
    if has_dims:
        size = target_size_px(site_width_ft, site_height_ft)
        if size != img.size:
            img = img.resize(size, Image.LANCZOS)
        resampled = True

    if img.mode not in ("RGB", "RGBA", "L"):
        img = img.convert("RGB")
    buf = io.BytesIO()
    img.save(buf, format="PNG")

    info = {
        "rotation_deg": rotation,
        "auto_rotated": auto_rotated,
        "original_px": [original[0], original[1]],
        "prepared_px": [img.size[0], img.size[1]],
        "resampled": resampled,
        "ft_per_unit_y": (site_width_ft / 1000.0) if has_dims else None,
        "ft_per_unit_x": (site_height_ft / 1000.0) if has_dims else None,
    }
    return buf.getvalue(), info
