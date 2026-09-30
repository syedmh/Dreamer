from collections import deque
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "assets" / "sprites"
CANVAS_SIZE = (640, 1000)

WALK_RIGHT_ROW = (0, 75, 1536, 445)
WALK_LEFT_ROW = (0, 565, 1536, 915)

CLAP_FRAMES = [
    (10, 955, 155, 1195),
    (150, 955, 300, 1195),
    (300, 955, 445, 1195),
    (440, 955, 590, 1195),
    (585, 955, 740, 1195),
]


def is_background(pixel):
    red, green, blue = pixel[:3]
    maximum = max(red, green, blue)
    minimum = min(red, green, blue)
    luminance = (54 * red + 183 * green + 19 * blue) // 256
    return maximum <= 58 and luminance <= 48 and maximum - minimum <= 28


def remove_connected_background(image, largest_only=True):
    source = image.convert("RGB")
    width, height = source.size
    pixels = source.load()
    background = bytearray(width * height)
    queue = deque()

    def enqueue(x, y):
        index = y * width + x
        if not background[index] and is_background(pixels[x, y]):
            background[index] = 1
            queue.append((x, y))

    for x in range(width):
        enqueue(x, 0)
        enqueue(x, height - 1)
    for y in range(height):
        enqueue(0, y)
        enqueue(width - 1, y)

    while queue:
        x, y = queue.popleft()
        if x:
            enqueue(x - 1, y)
        if x + 1 < width:
            enqueue(x + 1, y)
        if y:
            enqueue(x, y - 1)
        if y + 1 < height:
            enqueue(x, y + 1)

    alpha = Image.new("L", (width, height), 255)
    alpha_pixels = alpha.load()
    for y in range(height):
        for x in range(width):
            if background[y * width + x]:
                alpha_pixels[x, y] = 0

    alpha = alpha.filter(ImageFilter.GaussianBlur(0.7))
    result = source.convert("RGBA")
    result.putalpha(alpha)
    return keep_largest_component(result) if largest_only else result


def keep_largest_component(image):
    alpha = image.getchannel("A")
    width, height = image.size
    pixels = alpha.load()
    visited = bytearray(width * height)
    components = []

    for y in range(height):
        for x in range(width):
            index = y * width + x
            if visited[index] or pixels[x, y] < 24:
                continue

            queue = deque([(x, y)])
            visited[index] = 1
            component = []
            while queue:
                next_x, next_y = queue.popleft()
                component.append((next_x, next_y))
                for offset_x, offset_y in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                    neighbor_x = next_x + offset_x
                    neighbor_y = next_y + offset_y
                    if not (0 <= neighbor_x < width and 0 <= neighbor_y < height):
                        continue
                    neighbor_index = neighbor_y * width + neighbor_x
                    if visited[neighbor_index] or pixels[neighbor_x, neighbor_y] < 24:
                        continue
                    visited[neighbor_index] = 1
                    queue.append((neighbor_x, neighbor_y))
            components.append(component)

    if not components:
        return image

    keep = set(max(components, key=len))
    output = image.copy()
    output_pixels = output.load()
    for y in range(height):
        for x in range(width):
            if (x, y) not in keep:
                red, green, blue, _ = output_pixels[x, y]
                output_pixels[x, y] = (red, green, blue, 0)
    return output


def visible_bounds(image):
    return image.getchannel("A").getbbox()


def normalize(image, target_height=930, baseline=980):
    bounds = visible_bounds(image)
    if not bounds:
        raise ValueError("No foreground found")
    cropped = image.crop(bounds)
    scale = target_height / cropped.height
    width = max(1, round(cropped.width * scale))
    resized = cropped.resize((width, target_height), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", CANVAS_SIZE, (0, 0, 0, 0))
    x = (CANVAS_SIZE[0] - width) // 2
    y = baseline - target_height
    canvas.alpha_composite(resized, (x, y))
    return canvas


def save_frames(sheet, rectangles, prefix):
    for index, rectangle in enumerate(rectangles):
        frame = remove_connected_background(sheet.crop(rectangle))
        normalize(frame).save(OUTPUT / f"{prefix}-{index}.png", optimize=True)


def find_character_components(image):
    source = image.convert("RGB")
    width, height = image.size
    pixels = source.load()
    visited = bytearray(width * height)
    components = []

    for y in range(height):
        for x in range(width):
            index = y * width + x
            if visited[index] or max(pixels[x, y]) <= 62:
                continue

            queue = deque([(x, y)])
            visited[index] = 1
            component = []
            while queue:
                next_x, next_y = queue.popleft()
                component.append((next_x, next_y))
                for offset_x, offset_y in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                    neighbor_x = next_x + offset_x
                    neighbor_y = next_y + offset_y
                    if not (0 <= neighbor_x < width and 0 <= neighbor_y < height):
                        continue
                    neighbor_index = neighbor_y * width + neighbor_x
                    if visited[neighbor_index] or max(pixels[neighbor_x, neighbor_y]) <= 62:
                        continue
                    visited[neighbor_index] = 1
                    queue.append((neighbor_x, neighbor_y))

            xs = [point[0] for point in component]
            ys = [point[1] for point in component]
            bounds = (min(xs), min(ys), max(xs) + 1, max(ys) + 1)
            if bounds[3] - bounds[1] >= 220 and len(component) >= 500:
                components.append((bounds, component))

    return sorted(components, key=lambda item: item[0][0])


def fill_mask_holes(mask):
    width, height = mask.size
    pixels = mask.load()
    exterior = bytearray(width * height)
    queue = deque()

    def enqueue(x, y):
        index = y * width + x
        if not exterior[index] and pixels[x, y] == 0:
            exterior[index] = 1
            queue.append((x, y))

    for x in range(width):
        enqueue(x, 0)
        enqueue(x, height - 1)
    for y in range(height):
        enqueue(0, y)
        enqueue(width - 1, y)

    while queue:
        x, y = queue.popleft()
        if x:
            enqueue(x - 1, y)
        if x + 1 < width:
            enqueue(x + 1, y)
        if y:
            enqueue(x, y - 1)
        if y + 1 < height:
            enqueue(x, y + 1)

    for y in range(height):
        for x in range(width):
            if pixels[x, y] == 0 and not exterior[y * width + x]:
                pixels[x, y] = 255
    return mask


def save_row_frames(sheet, row_rectangle, prefix):
    row = sheet.crop(row_rectangle).convert("RGB")
    components = find_character_components(row)
    for index, (bounds, component) in enumerate(components):
        mask = Image.new("L", row.size, 0)
        mask_pixels = mask.load()
        for x, y in component:
            mask_pixels[x, y] = 255
        selector = mask.crop(bounds).filter(ImageFilter.MaxFilter(15))
        selector = fill_mask_holes(selector)
        frame = remove_connected_background(
            row.crop(bounds),
            largest_only=False,
        )
        frame.putalpha(ImageChops.multiply(frame.getchannel("A"), selector))
        frame = keep_largest_component(frame)
        normalized = normalize(frame)
        if prefix == "walk-right" and index == 13:
            normalized.paste((0, 0, 0, 0), (0, 820, 235, CANVAS_SIZE[1]))
        normalized.save(
            OUTPUT / f"{prefix}-{index}.png",
            optimize=True,
        )
    return len(components)


def clear_generated_walk_frames():
    for pattern in ("walk-right-*.png", "walk-left-*.png"):
        for frame in OUTPUT.glob(pattern):
            frame.unlink()


def build_clap_frames(sheet, standing):
    standing_lower = standing.copy()
    clear_to = 625
    standing_lower.paste((0, 0, 0, 0), (0, 0, CANVAS_SIZE[0], clear_to))

    for index, rectangle in enumerate(CLAP_FRAMES):
        upper = remove_connected_background(sheet.crop(rectangle))
        bounds = visible_bounds(upper)
        if not bounds:
            continue
        upper = upper.crop(bounds)
        target_height = 820
        scale = target_height / upper.height
        width = round(upper.width * scale)
        upper = upper.resize((width, target_height), Image.Resampling.LANCZOS)

        canvas = standing_lower.copy()
        x = (CANVAS_SIZE[0] - width) // 2
        y = 8
        upper_alpha = upper.getchannel("A")
        alpha_pixels = upper_alpha.load()
        fade_start = target_height - 155
        for alpha_y in range(fade_start, target_height):
            fade = 1 - ((alpha_y - fade_start) / (target_height - fade_start))
            for alpha_x in range(width):
                alpha_pixels[alpha_x, alpha_y] = round(alpha_pixels[alpha_x, alpha_y] * fade)
        upper.putalpha(upper_alpha)
        canvas.alpha_composite(upper, (x, y))
        canvas.save(OUTPUT / f"clap-{index}.png", optimize=True)


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)

    idle_source = Image.open(ROOT / "Avatar2.jpg").crop((0, 0, 768, 1008))
    idle = normalize(remove_connected_background(idle_source), target_height=950, baseline=985)
    idle.save(OUTPUT / "idle.png", optimize=True)

    clear_generated_walk_frames()
    walking_sheet = Image.open(ROOT / "Walking.png")
    right_count = save_row_frames(walking_sheet, WALK_RIGHT_ROW, "walk-right")
    left_count = save_row_frames(walking_sheet, WALK_LEFT_ROW, "walk-left")
    if (right_count, left_count) != (14, 15):
        raise ValueError(
            f"Expected 14 right and 15 left walking frames, got {right_count} and {left_count}",
        )

    sheet = Image.open(ROOT / "AvatorMove2.jpg")
    standing = normalize(
        remove_connected_background(sheet.crop((130, 28, 242, 306))),
        target_height=950,
        baseline=985,
    )
    build_clap_frames(sheet, standing)

    print(f"Generated sprite avatar assets in {OUTPUT}")


if __name__ == "__main__":
    main()
