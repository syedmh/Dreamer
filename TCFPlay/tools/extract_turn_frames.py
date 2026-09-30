from collections import deque
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "GirlFrames.png"
OUTPUT = ROOT / "assets" / "girl-turn"
X_BOUNDARIES = [0, 125, 236, 352, 467, 585, 703, 822, 944, 1068]
ART_TOP = 135
ART_BOTTOM = 516
WHITE_MINIMUM = 218
WHITE_TOLERANCE = 30
ALPHA_THRESHOLD = 8
PADDING = 3


def is_near_white(pixel):
    red, green, blue, _ = pixel
    return (
        min(red, green, blue) >= WHITE_MINIMUM
        and max(red, green, blue) - min(red, green, blue) <= WHITE_TOLERANCE
    )


def remove_panel_background(
    image,
    closing_size=7,
    upper_closing_size=None,
    upper_height=0,
    preserve_enclosed_band=None,
    preserve_enclosed_min_x_ratio=0,
    localized_closing=None,
    preserve_polygons=None,
):
    pixels = image.load()
    width, height = image.size
    foreground = Image.new("L", image.size)
    foreground_pixels = foreground.load()
    for y in range(height):
        for x in range(width):
            foreground_pixels[x, y] = 0 if is_near_white(pixels[x, y]) else 255

    # Close small anti-aliased gaps in the character outline before finding the
    # panel background. This retains enclosed white clothing while still
    # removing the white sheet behind the character.
    barrier = foreground.filter(ImageFilter.MaxFilter(closing_size))
    if upper_closing_size is not None and upper_height > 0:
        upper_barrier = foreground.filter(ImageFilter.MaxFilter(upper_closing_size))
        barrier.paste(
            upper_barrier.crop((0, 0, width, min(height, upper_height))),
            (0, 0),
        )
    if localized_closing is not None:
        left_ratio, top_ratio, right_ratio, bottom_ratio, region_size = localized_closing
        bounds = foreground.getbbox()
        if bounds is not None:
            left, top, right, bottom = bounds
            region = (
                round(left + (right - left) * left_ratio),
                round(top + (bottom - top) * top_ratio),
                round(left + (right - left) * right_ratio),
                round(top + (bottom - top) * bottom_ratio),
            )
            localized_barrier = foreground.filter(ImageFilter.MaxFilter(region_size))
            barrier.paste(localized_barrier.crop(region), region)
    barrier_pixels = barrier.load()
    pending = deque()
    visited = set()

    for x in range(width):
        pending.append((x, 0))
        pending.append((x, height - 1))
    for y in range(1, height - 1):
        pending.append((0, y))
        pending.append((width - 1, y))

    while pending:
        x, y = pending.popleft()
        if (x, y) in visited:
            continue
        visited.add((x, y))
        if barrier_pixels[x, y]:
            continue
        if x > 0:
            pending.append((x - 1, y))
        if x + 1 < width:
            pending.append((x + 1, y))
        if y > 0:
            pending.append((x, y - 1))
        if y + 1 < height:
            pending.append((x, y + 1))

    interior = Image.new("L", image.size, 255)
    interior_pixels = interior.load()
    for x, y in visited:
        interior_pixels[x, y] = 0
    protected_white = interior.filter(ImageFilter.MinFilter(closing_size))
    protected_pixels = protected_white.load()
    polygon_pixels = None
    if preserve_polygons:
        polygon_mask = Image.new("L", image.size)
        polygon_draw = ImageDraw.Draw(polygon_mask)
        for polygon in preserve_polygons:
            polygon_draw.polygon(
                [(round(x * width), round(y * height)) for x, y in polygon],
                fill=255,
            )
        polygon_pixels = polygon_mask.load()

    for y in range(height):
        for x in range(width):
            preserve_enclosed = False
            if preserve_enclosed_band is not None:
                band_top, band_bottom, max_gap = preserve_enclosed_band
                if (
                    band_top <= y < min(height, band_bottom)
                    and x >= width * preserve_enclosed_min_x_ratio
                ):
                    horizontal = (
                        any(
                            foreground_pixels[candidate, y]
                            for candidate in range(max(0, x - max_gap), x)
                        )
                        and any(
                            foreground_pixels[candidate, y]
                            for candidate in range(x + 1, min(width, x + max_gap + 1))
                        )
                    )
                    vertical = (
                        any(
                            foreground_pixels[x, candidate]
                            for candidate in range(max(0, y - max_gap), y)
                        )
                        and any(
                            foreground_pixels[x, candidate]
                            for candidate in range(y + 1, min(height, y + max_gap + 1))
                        )
                    )
                    preserve_enclosed = horizontal or vertical
            if (
                is_near_white(pixels[x, y])
                and not protected_pixels[x, y]
                and not preserve_enclosed
                and not (polygon_pixels and polygon_pixels[x, y])
            ):
                red, green, blue, _ = pixels[x, y]
                pixels[x, y] = (red, green, blue, 0)


def trim_transparent(image):
    alpha = image.getchannel("A")
    mask = alpha.point(lambda value: 255 if value > ALPHA_THRESHOLD else 0)
    bounds = mask.getbbox()
    if bounds is None:
        raise RuntimeError("Extracted frame contains no visible artwork")
    left, top, right, bottom = bounds
    return image.crop(
        (
            max(0, left - PADDING),
            max(0, top - PADDING),
            min(image.width, right + PADDING),
            min(image.height, bottom + PADDING),
        )
    )


def main():
    source = Image.open(SOURCE).convert("RGBA")
    OUTPUT.mkdir(parents=True, exist_ok=True)

    for index, (left, right) in enumerate(
        zip(X_BOUNDARIES, X_BOUNDARIES[1:]), start=1
    ):
        frame = source.crop((left + 1, ART_TOP, right, ART_BOTTOM))
        remove_panel_background(frame)
        frame = trim_transparent(frame)
        destination = OUTPUT / f"turn-{index:02d}.png"
        frame.save(destination, optimize=True)
        print(
            f"{destination.relative_to(ROOT)} "
            f"source=({left + 1},{ART_TOP})-({right},{ART_BOTTOM}) "
            f"output={frame.width}x{frame.height}"
        )


if __name__ == "__main__":
    main()
