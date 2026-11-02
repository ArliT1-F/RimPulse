#!/usr/bin/env python3
"""Regenerate RimPulse's small icon assets from one vector description.

The repository has no image tooling dependency: this script uses only the Python standard library and
renders a supersampled raster directly into PNG and ICO containers. Run ``--check`` in CI to fail when a
committed asset no longer matches its generator, so the binary assets cannot drift from their source.

    python3 packaging/generate-icons.py            # rewrite packaging/linux/*.png, packaging/windows/*.ico
    python3 packaging/generate-icons.py --check    # verify only; never writes

The ICO uses PNG-compressed entries (Vista and newer). RimPulse targets .NET 10, whose Windows support
starts at Windows 10, so no legacy BMP entries are needed.
"""

from __future__ import annotations

import argparse
import math
import struct
import sys
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent
LINUX_DIR = ROOT / "linux"
WINDOWS_DIR = ROOT / "windows"

# All icon sizes are rendered from the same normalized geometry.
ICO_SIZES = (16, 32, 48, 64, 128, 256)
PNG_SIZE = 256
SAMPLES = 4  # 4x4 supersampling per pixel; enough antialiasing at these sizes.

BACKGROUND = (13, 16, 21)  # App.axaml CanvasBrush
ACCENT = (95, 227, 161)  # App.axaml AccentBrush
BORDER = (35, 43, 54)  # App.axaml PanelBorderBrush


def _rounded_rect_inside(u: float, v: float) -> bool:
    """Inside a rounded square with a 20% corner radius (coordinates are 0..1)."""
    radius = 0.20
    x = min(u, 1.0 - u)
    y = min(v, 1.0 - v)
    if x >= radius or y >= radius:
        return u > 0.0 and u < 1.0 and v > 0.0 and v < 1.0
    return (x - radius) ** 2 + (y - radius) ** 2 <= radius**2


def _distance_to_segment(px: float, py: float, ax: float, ay: float, bx: float, by: float) -> float:
    dx, dy = bx - ax, by - ay
    length_squared = dx * dx + dy * dy
    if length_squared == 0.0:
        return math.hypot(px - ax, py - ay)
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / length_squared))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


PULSE = (
    (0.19, 0.50),
    (0.35, 0.50),
    (0.43, 0.35),
    (0.53, 0.67),
    (0.62, 0.40),
    (0.70, 0.50),
    (0.81, 0.50),
)


def _ring_inside(u: float, v: float) -> bool:
    """A thick circular pulse ring with a gap in the upper-right quadrant."""
    dx, dy = u - 0.5, v - 0.5
    radius = math.hypot(dx, dy)
    outer, inner = 0.350, 0.290
    if not (inner <= radius <= outer):
        return False
    angle = math.atan2(dy, dx)
    return not (-1.05 <= angle <= -0.30)


def _pulse_inside(u: float, v: float) -> bool:
    thickness = 0.032
    for (ax, ay), (bx, by) in zip(PULSE, PULSE[1:]):
        if _distance_to_segment(u, v, ax, ay, bx, by) <= thickness:
            return True
    return False


def _border_inside(u: float, v: float) -> bool:
    """A one-pixel-ish outline just inside the rounded square."""
    if not _rounded_rect_inside(u, v):
        return False
    inset = 0.014
    return not (
        inset < u < 1.0 - inset
        and inset < v < 1.0 - inset
        and _rounded_rect_inside((u - inset) / (1.0 - 2 * inset), (v - inset) / (1.0 - 2 * inset))
    )


def _sample(u: float, v: float) -> tuple[int, int, int, int]:
    if not _rounded_rect_inside(u, v):
        return (0, 0, 0, 0)
    if _ring_inside(u, v) or _pulse_inside(u, v):
        return (*ACCENT, 255)
    if _border_inside(u, v):
        return (*BORDER, 255)
    return (*BACKGROUND, 255)


def render(size: int) -> bytes:
    """Return an RGBA PNG for a square icon of ``size`` pixels."""
    step = 1.0 / SAMPLES
    rows = bytearray()
    for y in range(size):
        for x in range(size):
            red = green = blue = alpha = 0
            for sy in range(SAMPLES):
                u_y = (y + (sy + 0.5) * step) / size
                for sx in range(SAMPLES):
                    u_x = (x + (sx + 0.5) * step) / size
                    r, g, b, a = _sample(u_x, u_y)
                    red += r * a
                    green += g * a
                    blue += b * a
                    alpha += a
            if alpha:
                # Un-premultiply the accumulated colour.
                rows += bytes((red // alpha, green // alpha, blue // alpha, alpha // (SAMPLES * SAMPLES)))
            else:
                rows += b"\x00\x00\x00\x00"

    raw = b"".join(b"\x00" + bytes(rows[y * size * 4 : (y + 1) * size * 4]) for y in range(size))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def make_ico(images: list[tuple[int, bytes]]) -> bytes:
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    directory = bytearray()
    payload = bytearray()
    for size, png in images:
        width_byte = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", width_byte, width_byte, 0, 0, 1, 32, len(png), offset)
        payload += png
        offset += len(png)
    return header + bytes(directory) + bytes(payload)


def generated_assets() -> dict[Path, bytes]:
    assets: dict[Path, bytes] = {}
    pngs = [(size, render(size)) for size in ICO_SIZES]
    assets[LINUX_DIR / "rimpulse.png"] = dict(pngs)[PNG_SIZE]
    assets[WINDOWS_DIR / "rimpulse.ico"] = make_ico(pngs)
    return assets


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="verify committed assets instead of writing them")
    args = parser.parse_args()

    assets = generated_assets()
    if args.check:
        stale = []
        for path, expected in assets.items():
            if not path.is_file() or path.read_bytes() != expected:
                stale.append(str(path.relative_to(ROOT)))
        if stale:
            print("Icon assets are missing or out of date:", ", ".join(stale), file=sys.stderr)
            print("Run: python3 packaging/generate-icons.py", file=sys.stderr)
            return 1
        print(f"Icon assets match the generator ({len(assets)} files).")
        return 0

    for path, content in assets.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        print(f"Wrote {path.relative_to(ROOT)} ({len(content)} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
