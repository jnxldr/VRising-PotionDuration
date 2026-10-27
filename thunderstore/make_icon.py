"""Draw the Thunderstore icon (256x256 PNG): a potion flask with a clock badge. Pure Python, no PIL.

Usage: python make_icon.py <out.png>
"""
import math
import struct
import sys
import zlib

SIZE = 256
SS = 4  # supersampling per axis


def rgba(hex_color, a=255):
    h = hex_color.lstrip("#")
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)


PLATE = rgba("#241c2e")
GLASS = rgba("#3a3346")
OUTLINE = rgba("#d6dde8")
LIQUID = rgba("#1f9a48")
LIQUID_TOP = rgba("#4fd27c")
CORK = rgba("#8a5a34")
HIGHLIGHT = rgba("#ffffff", 110)
BADGE = rgba("#e8c15a")
BADGE_EDGE = rgba("#241c2e")


def rounded_rect(x, y, x0, y0, x1, y1, r):
    # Signed distance to a rounded rectangle (negative inside).
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    hx, hy = (x1 - x0) / 2 - r, (y1 - y0) / 2 - r
    dx, dy = abs(x - cx) - hx, abs(y - cy) - hy
    return math.hypot(max(dx, 0), max(dy, 0)) + min(max(dx, dy), 0) - r


def circle(x, y, cx, cy, r):
    return math.hypot(x - cx, y - cy) - r


def segment(x, y, ax, ay, bx, by, r):
    px, py, vx, vy = x - ax, y - ay, bx - ax, by - ay
    t = max(0.0, min(1.0, (px * vx + py * vy) / (vx * vx + vy * vy)))
    return math.hypot(px - vx * t, py - vy * t) - r


def sample(x, y):
    """Colour at one sub-pixel; later layers paint over earlier ones."""
    color = (0, 0, 0, 0)
    if rounded_rect(x, y, 12, 12, 244, 244, 44) < 0:
        color = PLATE
    glass = min(circle(x, y, 120, 150, 72), rounded_rect(x, y, 100, 50, 140, 110, 6))
    if glass < 6:
        color = OUTLINE
    if glass < 0:
        surface = 128 + 5 * math.sin(x / 11)
        if y > surface + 6:
            color = LIQUID
        elif y > surface:
            color = LIQUID_TOP
        else:
            color = GLASS
        if circle(x, y, 92, 128, 16) < 0:
            color = blend(color, HIGHLIGHT)
    if rounded_rect(x, y, 90, 42, 150, 58, 6) < 0:
        color = OUTLINE
    if rounded_rect(x, y, 104, 18, 136, 44, 5) < 0:
        color = CORK
    badge = circle(x, y, 186, 186, 44)
    if badge < 6:
        color = BADGE_EDGE
    if badge < 0:
        color = BADGE
        if min(segment(x, y, 186, 186, 186, 156, 4.5), segment(x, y, 186, 186, 208, 196, 4.5)) < 0 \
                or circle(x, y, 186, 186, 7) < 0:
            color = BADGE_EDGE
    return color


def blend(dst, src):
    a = src[3] / 255
    return tuple(round(dst[i] * (1 - a) + src[i] * a) for i in range(3)) + (dst[3],)


def render():
    rows = []
    for py in range(SIZE):
        row = bytearray([0])  # PNG filter type 0
        for px in range(SIZE):
            acc = [0, 0, 0, 0]
            for sy in range(SS):
                for sx in range(SS):
                    r, g, b, a = sample(px + (sx + 0.5) / SS, py + (sy + 0.5) / SS)
                    acc[0] += r * a
                    acc[1] += g * a
                    acc[2] += b * a
                    acc[3] += a
            n = SS * SS
            alpha = acc[3] / n
            if acc[3]:
                row += bytes((round(acc[0] / acc[3]), round(acc[1] / acc[3]), round(acc[2] / acc[3]), round(alpha)))
            else:
                row += bytes(4)
        rows.append(bytes(row))
    return b"".join(rows)


def write_png(path, raw):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)  # 8-bit RGBA
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


if __name__ == "__main__":
    write_png(sys.argv[1], render())
