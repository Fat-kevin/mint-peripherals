"""Mint leaf app icon — 白色薄荷叶浮在薄荷绿圆角方块上。

呼应 Mint Studio 设计系统：#12A886 底（比 Ion #0E9A76 略亮，保证任务栏存在感），
白色单叶 + 茎，>=32px 加主叶脉；小尺寸省略叶脉保持锐利。
每个尺寸 8x 超采样渲染后 LANCZOS 缩回，避免 Windows 缩放发糊。
ICO 打包沿用 32-bit DIB（256 为 PNG）。
"""

from pathlib import Path
import struct

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent
APP_ASSETS = ROOT.parents[2] / "src" / "DeviceCenterPreview.App" / "Assets"

MINT = (18, 168, 134, 255)       # #12A886
MINT_DEEP = (11, 138, 110, 255)  # 叶脉/茎的深薄荷
WHITE = (255, 255, 255, 255)

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SS = 8  # supersample factor


def rounded_background(size):
    """Full-bleed rounded square, radius 22.5% — Win11 图标观感。"""
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    radius = size * 0.225
    draw.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=MINT)
    return image


def leaf_polygon(cx, cy, length, width, angle_deg):
    """尖头椭圆叶形（尖在尖端、圆弧两肩、收窄叶基），绕中心旋转。"""
    import math

    points = []
    # 尖端 → 右肩 → 基部 → 左肩 → 回尖端；两肩用抛物线近似。
    steps = 24
    for i in range(steps + 1):
        t = i / steps  # 0=尖端 1=基部
        x = length * (0.5 - t)              # 尖端在前
        spread = math.sin(math.pi * (0.5 - 0.5 * t) * 0.92 + 0.08 * math.pi)
        y = width * 0.5 * spread
        points.append((x, y))
    for i in range(steps + 1):
        t = 1 - i / steps                    # 基部 → 尖端（另一侧）
        x = length * (0.5 - t)
        spread = math.sin(math.pi * (0.5 - 0.5 * t) * 0.92 + 0.08 * math.pi)
        y = -width * 0.5 * spread
        points.append((x, y))

    rad = math.radians(angle_deg)
    cos_a, sin_a = math.cos(rad), math.sin(rad)
    return [(cx + x * cos_a - y * sin_a, cy + x * sin_a + y * cos_a) for x, y in points]


def render(size):
    big = size * SS
    image = rounded_background(big)
    draw = ImageDraw.Draw(image)

    cx, cy = big * 0.50, big * 0.52
    length = big * 0.52
    width = big * 0.34
    angle = -38  # 屏幕坐标系 y 轴向下，负角 = 叶尖指向右上、茎在左下

    # 茎：从叶基延伸到左下，圆头粗线
    import math
    rad = math.radians(angle)
    base_x = cx - math.cos(rad) * length * 0.42
    base_y = cy - math.sin(rad) * length * 0.42
    stem_end_x = base_x - math.cos(rad) * big * 0.10
    stem_end_y = base_y - math.sin(rad) * big * 0.10
    stem_w = max(2, int(big * 0.045))
    draw.line([(base_x, base_y), (stem_end_x, stem_end_y)], fill=WHITE, width=stem_w)
    draw.ellipse(
        [stem_end_x - stem_w / 2, stem_end_y - stem_w / 2,
         stem_end_x + stem_w / 2, stem_end_y + stem_w / 2],
        fill=WHITE,
    )

    # 叶身
    draw.polygon(leaf_polygon(cx, cy, length, width, angle), fill=WHITE)

    # 主叶脉：>=32px 才画，避免小尺寸糊
    if size >= 32:
        vein_w = max(2, int(big * 0.032))
        tip_x = cx + math.cos(rad) * length * 0.44
        tip_y = cy + math.sin(rad) * length * 0.44
        end_x = cx + math.cos(rad) * length * 0.12
        end_y = cy + math.sin(rad) * length * 0.12
        draw.line([(end_x, end_y), (tip_x * 0.55 + end_x * 0.45, tip_y * 0.55 + end_y * 0.45)],
                  fill=MINT_DEEP, width=vein_w)

    return image.resize((size, size), Image.Resampling.LANCZOS)


def dib_frame(image):
    """32-bit bottom-up DIB with an all-zero AND mask (复用既有格式)。"""
    width, height = image.size
    rgba = image.load()
    xor = bytearray()
    for y in range(height - 1, -1, -1):
        for x in range(width):
            r, g, b, a = rgba[x, y]
            xor += bytes((b, g, r, a))

    stride = ((width + 31) // 32) * 4
    mask = bytes(stride * height)
    header = struct.pack(
        "<IiiHHIIiiII",
        40, width, height * 2, 1, 32, 0, len(xor), 0, 0, 0, 0,
    )
    return header + bytes(xor) + mask


def png_frame(image):
    import io
    stream = io.BytesIO()
    image.save(stream, format="PNG", optimize=True)
    return stream.getvalue()


def build_ico(frames, target):
    sizes = sorted(frames)
    blobs = []
    for size in sizes:
        image = frames[size]
        blobs.append((size, png_frame(image) if size >= 256 else dib_frame(image)))

    header = struct.pack("<HHH", 0, 1, len(blobs))
    directory = bytearray()
    offset = 6 + 16 * len(blobs)
    for size, blob in blobs:
        directory += struct.pack(
            "<BBBBHHII",
            0 if size >= 256 else size,
            0 if size >= 256 else size,
            0, 0, 1, 32, len(blob), offset,
        )
        offset += len(blob)
    target.write_bytes(header + directory + b"".join(blob for _, blob in blobs))


def main():
    frames = {}
    for size in SIZES:
        image = render(size)
        frames[size] = image
        image.save(ROOT / f"mint-{size}.png")

    APP_ASSETS.mkdir(parents=True, exist_ok=True)
    build_ico(frames, APP_ASSETS / "PeripheralHub.ico")
    frames[16].save(APP_ASSETS / "PeripheralHub-16.png")
    frames[24].save(APP_ASSETS / "PeripheralHub-24.png")
    frames[32].save(APP_ASSETS / "PeripheralHub-32.png")
    frames[256].save(APP_ASSETS / "PeripheralHub.png")

    print("frames:", ", ".join(str(s) for s in SIZES))
    print("ico:", (APP_ASSETS / "PeripheralHub.ico").stat().st_size, "bytes")


if __name__ == "__main__":
    main()
