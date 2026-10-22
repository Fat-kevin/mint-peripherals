"""Build the Backplane / 中枢 app icon from the approved concept frames.

The 16/20/24 frames are hand-drawn pixel art. 40 is derived from 48 so the
中 stays legible. Each ICO entry is a 32-bit DIB (256 is PNG) instead of
letting Windows downscale one bitmap, which would blur the tray/title-bar glyph.
"""

from pathlib import Path
import struct

from PIL import Image

ROOT = Path(__file__).resolve().parent
APP_ASSETS = ROOT.parents[2] / "src" / "DeviceCenterPreview.App" / "Assets"

MASTER = {
    16: ROOT / "zhong-16-crisp.png",
    20: ROOT / "zhong-20-crisp.png",
    24: ROOT / "zhong-24-crisp.png",
    32: ROOT / "zhong-32.png",
    48: ROOT / "zhong-48.png",
    64: ROOT / "zhong-64.png",
    128: ROOT / "zhong-128.png",
    256: ROOT / "zhong-256.png",
}

# 40 is not a separate hand-drawn frame; derive it from 48, then write it back
# as a concept asset for review.
DERIVED = {40: (48, "LANCZOS")}


def load_frames():
    frames = {}
    for size, path in MASTER.items():
        frames[size] = Image.open(path).convert("RGBA")
    for size, (source, resample) in DERIVED.items():
        image = frames[source].resize((size, size), getattr(Image.Resampling, resample))
        image.save(ROOT / f"zhong-{size}.png")
        frames[size] = image
    return frames


def dib_frame(image):
    """Return a 32-bit bottom-up DIB with an all-zero AND mask."""
    width, height = image.size
    rgba = image.load()
    xor = bytearray()
    for y in range(height - 1, -1, -1):
        for x in range(width):
            r, g, b, a = rgba[x, y]
            xor += bytes((b, g, r, a))

    # 1bpp AND mask, rows padded to 4 bytes. Alpha in the XOR bitmap is
    # authoritative for 32bpp icons; the mask is kept opaque.
    stride = ((width + 31) // 32) * 4
    mask = bytes(stride * height)

    header = struct.pack(
        "<IiiHHIIiiII",
        40,          # biSize
        width,       # biWidth
        height * 2,  # biHeight (XOR + AND)
        1,           # biPlanes
        32,          # biBitCount
        0,           # biCompression = BI_RGB
        len(xor),    # biSizeImage
        0, 0,        # ppm
        0, 0,        # clrUsed / clrImportant
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
            0,
            0,
            1,
            32,
            len(blob),
            offset,
        )
        offset += len(blob)

    target.write_bytes(header + directory + b"".join(blob for _, blob in blobs))


def main():
    frames = load_frames()
    APP_ASSETS.mkdir(parents=True, exist_ok=True)

    build_ico(frames, APP_ASSETS / "PeripheralHub.ico")
    frames[16].save(APP_ASSETS / "PeripheralHub-16.png")
    frames[24].save(APP_ASSETS / "PeripheralHub-24.png")
    frames[32].save(APP_ASSETS / "PeripheralHub-32.png")
    frames[256].save(APP_ASSETS / "PeripheralHub.png")

    print("frames:", ", ".join(str(s) for s in sorted(frames)))
    print("ico:", (APP_ASSETS / "PeripheralHub.ico").stat().st_size, "bytes")


if __name__ == "__main__":
    main()
