#!/usr/bin/env python3
"""Generate a stylised Aria architecture diagram for the README.

Theme: dark Mechanicus / cogitator terminal, blood-red accents, amber text,
monospace labels. Output: docs/img/architecture-overview.png
"""

from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT_PATH = ROOT / "docs" / "img" / "architecture-overview.png"

# ---------------------------------------------------------------------------
# Palette
# ---------------------------------------------------------------------------
BG = "#0a0a0b"            # near-black background
GRID = "#141418"          # subtle grid lines
PANEL_BG = "#111114"      # panel fill
PANEL_BORDER = "#8b0000"  # blood-red border
PANEL_BORDER_HI = "#dc143c"  # highlighted border
TEXT = "#e8dcc0"          # parchment/amber text
TEXT_DIM = "#9e9480"      # dimmer text
ACCENT = "#ff4d4d"        # bright crimson
AMBER = "#ffb000"         # amber/gold
GREEN = "#00c853"         # status green
CYAN = "#00e5ff"          # cross-node flow highlight

FONT_PATH = "/System/Library/Fonts/Menlo.ttc"
FONT_TITLE = None
FONT_HEADER = None
FONT_BODY = None
FONT_SMALL = None


def _load_fonts() -> None:
    global FONT_TITLE, FONT_HEADER, FONT_BODY, FONT_SMALL
    FONT_TITLE = ImageFont.truetype(FONT_PATH, 36)
    FONT_HEADER = ImageFont.truetype(FONT_PATH, 24)
    FONT_BODY = ImageFont.truetype(FONT_PATH, 18)
    FONT_SMALL = ImageFont.truetype(FONT_PATH, 14)


# ---------------------------------------------------------------------------
# Drawing helpers
# ---------------------------------------------------------------------------
def rounded_rect(
    draw: ImageDraw.ImageDraw,
    xy: tuple[int, int, int, int],
    radius: int,
    fill: str | None = None,
    outline: str | None = None,
    width: int = 2,
) -> None:
    draw.rounded_rectangle(xy, radius=radius, fill=fill, outline=outline, width=width)


def draw_panel(
    img: Image.Image,
    draw: ImageDraw.ImageDraw,
    x: int,
    y: int,
    w: int,
    h: int,
    title: str,
    subtitle: str | None = None,
    accent: str = PANEL_BORDER,
    glow: bool = True,
) -> None:
    """Draw a cogitator-style panel with corner brackets and optional glow."""
    pad = 3
    if glow:
        # soft outer glow
        glow_layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
        glow_draw = ImageDraw.Draw(glow_layer)
        glow_draw.rounded_rectangle(
            [x - pad, y - pad, x + w + pad, y + h + pad],
            radius=12,
            outline=accent,
            width=4,
        )
        glow_layer = glow_layer.filter(ImageFilter.GaussianBlur(radius=8))
        img.paste(glow_layer, (0, 0), glow_layer)

    # main panel
    rounded_rect(draw, [x, y, x + w, y + h], radius=10, fill=PANEL_BG, outline=accent, width=2)

    # corner brackets
    bracket_len = 18
    bracket_width = 3
    color = PANEL_BORDER_HI
    corners = [
        (x, y, 1, 1),            # top-left
        (x + w, y, -1, 1),       # top-right
        (x, y + h, 1, -1),       # bottom-left
        (x + w, y + h, -1, -1),  # bottom-right
    ]
    for cx, cy, sx, sy in corners:
        # horizontal stroke
        hx1, hy1 = cx + sx * bracket_width // 2, cy
        hx2, hy2 = cx + sx * bracket_len, cy
        draw.line([(hx1, hy1), (hx2, hy2)], fill=color, width=bracket_width)
        # vertical stroke
        vx1, vy1 = cx, cy + sy * bracket_width // 2
        vx2, vy2 = cx, cy + sy * bracket_len
        draw.line([(vx1, vy1), (vx2, vy2)], fill=color, width=bracket_width)

    # title
    tx, ty = x + 18, y + 16
    draw.text((tx, ty), title, font=FONT_HEADER, fill=TEXT)
    if subtitle:
        bbox = draw.textbbox((tx, ty), title, font=FONT_HEADER)
        draw.text((tx, ty + (bbox[3] - bbox[1]) + 6), subtitle, font=FONT_SMALL, fill=TEXT_DIM)


def status_light(draw: ImageDraw.ImageDraw, x: int, y: int, label: str) -> None:
    r = 6
    draw.ellipse([x - r, y - r, x + r, y + r], fill=GREEN, outline="#004d00", width=1)
    draw.text((x + 14, y - 7), label, font=FONT_SMALL, fill=GREEN)


def text_size(draw: ImageDraw.ImageDraw, text: str, font: ImageFont.FreeTypeFont) -> tuple[int, int]:
    bbox = draw.textbbox((0, 0), text, font=font)
    return bbox[2] - bbox[0], bbox[3] - bbox[1]


def arrow_head(draw: ImageDraw.ImageDraw, x: int, y: int, angle: float, color: str, size: int = 10) -> None:
    """Draw an arrowhead at (x, y) pointing along angle (radians)."""
    pts = [
        (x + size * math.cos(angle), y + size * math.sin(angle)),
        (x + size * math.cos(angle + 2.4), y + size * math.sin(angle + 2.4)),
        (x + size * 0.35 * math.cos(angle + math.pi), y + size * 0.35 * math.sin(angle + math.pi)),
        (x + size * math.cos(angle - 2.4), y + size * math.sin(angle - 2.4)),
    ]
    draw.polygon(pts, fill=color)


def draw_arrow(
    draw: ImageDraw.ImageDraw,
    x1: int,
    y1: int,
    x2: int,
    y2: int,
    color: str = ACCENT,
    width: int = 2,
    label: str | None = None,
    dashed: bool = False,
) -> None:
    """Draw a straight arrow with optional label centered above/beside it."""
    if dashed:
        # simple dashed line
        dist = math.hypot(x2 - x1, y2 - y1)
        if dist == 0:
            return
        dx, dy = (x2 - x1) / dist, (y2 - y1) / dist
        step = 10
        for i in range(0, int(dist), step * 2):
            sx = int(x1 + dx * i)
            sy = int(y1 + dy * i)
            ex = int(x1 + dx * min(i + step, dist))
            ey = int(y1 + dy * min(i + step, dist))
            draw.line([(sx, sy), (ex, ey)], fill=color, width=width)
    else:
        draw.line([(x1, y1), (x2, y2)], fill=color, width=width)

    angle = math.atan2(y2 - y1, x2 - x1)
    arrow_head(draw, x2, y2, angle, color, size=10)

    if label:
        cx, cy = (x1 + x2) // 2, (y1 + y2) // 2
        lw, lh = text_size(draw, label, FONT_SMALL)
        # small dark backing for readability
        draw.rectangle([cx - lw // 2 - 4, cy - lh // 2 - 2, cx + lw // 2 + 4, cy + lh // 2 + 2], fill=BG)
        draw.text((cx - lw // 2, cy - lh // 2), label, font=FONT_SMALL, fill=color)


def draw_curved_arrow(
    draw: ImageDraw.ImageDraw,
    points: list[tuple[int, int]],
    color: str = CYAN,
    width: int = 3,
    label: str | None = None,
    label_offset: tuple[int, int] = (0, -20),
) -> None:
    """Draw a smooth curve through points and add an arrowhead at the end."""
    if len(points) < 2:
        return
    # draw polyline for simplicity but with enough points it's smooth
    draw.line(points, fill=color, width=width)
    # arrowhead at the end
    x1, y1 = points[-2]
    x2, y2 = points[-1]
    angle = math.atan2(y2 - y1, x2 - x1)
    arrow_head(draw, x2, y2, angle, color, size=10)

    if label:
        cx, cy = points[len(points) // 2]
        cx += label_offset[0]
        cy += label_offset[1]
        lw, lh = text_size(draw, label, FONT_SMALL)
        draw.rectangle([cx - lw // 2 - 4, cy - lh // 2 - 2, cx + lw // 2 + 4, cy + lh // 2 + 2], fill=BG)
        draw.text((cx - lw // 2, cy - lh // 2), label, font=FONT_SMALL, fill=color)


def badge(draw: ImageDraw.ImageDraw, x: int, y: int, number: int, color: str = CYAN) -> None:
    r = 14
    draw.ellipse([x - r, y - r, x + r, y + r], fill=color, outline=TEXT, width=2)
    label = str(number)
    lw, lh = text_size(draw, label, FONT_BODY)
    draw.text((x - lw // 2, y - lh // 2), label, font=FONT_BODY, fill=BG)


# ---------------------------------------------------------------------------
# Main composition
# ---------------------------------------------------------------------------
def draw_background(img: Image.Image, draw: ImageDraw.ImageDraw) -> None:
    draw.rectangle([0, 0, img.width, img.height], fill=BG)
    # subtle grid
    step = 40
    for x in range(0, img.width, step):
        draw.line([(x, 0), (x, img.height)], fill=GRID, width=1)
    for y in range(0, img.height, step):
        draw.line([(0, y), (img.width, y)], fill=GRID, width=1)
    # vignette via radial gradient approximation
    overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
    overlay_draw = ImageDraw.Draw(overlay)
    for i, alpha in enumerate(range(60, 0, -3)):
        overlay_draw.rectangle(
            [i * 20, i * 20, img.width - i * 20, img.height - i * 20],
            outline=(0, 0, 0, alpha),
        )
    img.paste(overlay, (0, 0), overlay)


def draw_section_title(draw: ImageDraw.ImageDraw, text: str, y: int, W: int) -> None:
    tw, _ = text_size(draw, text, FONT_TITLE)
    draw.text(((W - tw) // 2, y), text, font=FONT_TITLE, fill=TEXT)
    draw.line([(W // 2 - tw // 2 - 20, y + 42), (W // 2 + tw // 2 + 20, y + 42)], fill=PANEL_BORDER, width=2)


def main() -> None:
    _load_fonts()
    W, H = 1600, 1320
    img = Image.new("RGBA", (W, H), BG)
    draw = ImageDraw.Draw(img)

    draw_background(img, draw)

    # =======================================================================
    # SECTION 1 — Overall architecture
    # =======================================================================
    draw_section_title(draw, "COGITATOR ARCHITECTURE // MK.IV", 30, W)

    web_w, web_h = 420, 130
    bridge_w, bridge_h = 380, 170
    lm_w, lm_h = 300, 75
    files_w, files_h = 170, 65

    web_x = (W - web_w) // 2
    web_y = 110

    bridge1_x = 180
    bridge2_x = W - bridge_w - 180
    bridge_y = 290

    lm1_x = bridge1_x + (bridge_w - lm_w) // 2
    lm2_x = bridge2_x + (bridge_w - lm_w) // 2
    lm_y = bridge_y + bridge_h + 50

    files1_x = bridge1_x + 35
    files2_x = bridge2_x + bridge_w - files_w - 35
    files_y = lm_y + lm_h + 55

    # --- Aria.Web ---
    draw_panel(
        img, draw, web_x, web_y, web_w, web_h,
        "ARIA.WEB",
        subtitle="hosted  ||  local  —  vox-link",
        accent=PANEL_BORDER_HI,
        glow=True,
    )
    status_light(draw, web_x + 20, web_y + web_h - 28, "verified")
    draw.text((web_x + 145, web_y + web_h - 35), "SignalR hub  ·  access gate", font=FONT_SMALL, fill=TEXT_DIM)

    # --- Bridges ---
    draw_panel(img, draw, bridge1_x, bridge_y, bridge_w, bridge_h,
               "ARIA.BRIDGE  //  NODE 1", subtitle="e.g. Mac workstation", accent=PANEL_BORDER)
    status_light(draw, bridge1_x + 20, bridge_y + bridge_h - 28, "soul bound")
    draw.text((bridge1_x + 155, bridge_y + bridge_h - 35), "keys · OAuth · memory", font=FONT_SMALL, fill=TEXT_DIM)

    draw_panel(img, draw, bridge2_x, bridge_y, bridge_w, bridge_h,
               "ARIA.BRIDGE  //  NODE 2", subtitle="e.g. Windows workstation", accent=PANEL_BORDER)
    status_light(draw, bridge2_x + 20, bridge_y + bridge_h - 28, "soul bound")
    draw.text((bridge2_x + 155, bridge_y + bridge_h - 35), "keys · OAuth · memory", font=FONT_SMALL, fill=TEXT_DIM)

    # --- Local LMs ---
    draw_panel(img, draw, lm1_x, lm_y, lm_w, lm_h, "LOCAL LM", subtitle="LM Studio / Ollama / llama.cpp", accent=PANEL_BORDER, glow=False)
    draw_panel(img, draw, lm2_x, lm_y, lm_w, lm_h, "LOCAL LM", subtitle="LM Studio / Ollama / llama.cpp", accent=PANEL_BORDER, glow=False)

    # --- Files ---
    draw_panel(img, draw, files1_x, files_y, files_w, files_h, "/projects", subtitle="local files", accent="#4a0000", glow=False)
    draw_panel(img, draw, files2_x, files_y, files_w, files_h, "/projects", subtitle="local files", accent="#4a0000", glow=False)

    # --- Arrows: Web ↔ Bridges ---
    draw_arrow(draw, web_x + 25, web_y + web_h - 15, bridge1_x + bridge_w - 25, bridge_y + 20, color=ACCENT, width=2)
    draw.text((web_x - 170, web_y + web_h + 22), "direct tunnel", font=FONT_SMALL, fill=ACCENT)
    draw_arrow(draw, bridge1_x + bridge_w - 55, bridge_y + 20, web_x + 55, web_y + web_h - 15, color=ACCENT, width=2)
    draw.text((web_x + 100, web_y + web_h + 8), "responses", font=FONT_SMALL, fill=ACCENT)

    draw_arrow(draw, web_x + web_w - 25, web_y + web_h - 15, bridge2_x + 25, bridge_y + 20, color=ACCENT, width=2)
    draw.text((web_x + web_w + 25, web_y + web_h + 22), "direct tunnel", font=FONT_SMALL, fill=ACCENT)
    draw_arrow(draw, bridge2_x + 55, bridge_y + 20, web_x + web_w - 55, web_y + web_h - 15, color=ACCENT, width=2)
    draw.text((web_x + web_w - 130, web_y + web_h + 8), "responses", font=FONT_SMALL, fill=ACCENT)

    # --- Arrows: Bridge → LM ---
    draw_arrow(draw, bridge1_x + bridge_w // 2, bridge_y + bridge_h, lm1_x + lm_w // 2, lm_y, color=AMBER, width=2)
    draw.text((lm1_x + lm_w + 10, bridge_y + bridge_h + 20), "chat / probes", font=FONT_SMALL, fill=AMBER)
    draw_arrow(draw, bridge2_x + bridge_w // 2, bridge_y + bridge_h, lm2_x + lm_w // 2, lm_y, color=AMBER, width=2)
    draw.text((lm2_x - 120, bridge_y + bridge_h + 20), "chat / probes", font=FONT_SMALL, fill=AMBER)

    # --- Arrows: Bridge → Files ---
    draw_arrow(draw, bridge1_x + bridge_w // 2 - 30, bridge_y + bridge_h, files1_x + files_w // 2, files_y, color=TEXT_DIM, width=2)
    draw_arrow(draw, bridge2_x + bridge_w // 2 + 30, bridge_y + bridge_h, files2_x + files_w // 2, files_y, color=TEXT_DIM, width=2)

    # --- Section divider ---
    draw.line([(80, 760), (W - 80, 760)], fill=PANEL_BORDER, width=1)
    draw.text((80, 740), "// EXAMPLE FLOW", font=FONT_BODY, fill=TEXT_DIM)

    # =======================================================================
    # SECTION 2 — Cross-node file request example
    # =======================================================================
    section2_y = 820
    box_w, box_h = 280, 100
    gap = 100

    # 5 panels laid out horizontally: Node2 -> Web -> Node1 -> Files1 -> Web -> Node2
    # We use 5 positions and draw arrows between them.
    positions = [
        (180, section2_y + 50),                       # 0: Node2
        (520, section2_y),                            # 1: Web (higher, orchestrator)
        (860, section2_y + 50),                       # 2: Node1
        (1200, section2_y + 50),                      # 3: Files1
        (520, section2_y + 180),                      # 4: Web (return path)
    ]

    # Node2 box
    x, y = positions[0]
    draw_panel(img, draw, x, y, box_w, box_h, "NODE 2", subtitle="requests /proj@Node1", accent=PANEL_BORDER, glow=False)

    # Web box (outbound)
    x, y = positions[1]
    draw_panel(img, draw, x, y, box_w, box_h, "ARIA.WEB", subtitle="receives & routes", accent=PANEL_BORDER_HI, glow=True)

    # Node1 box
    x, y = positions[2]
    draw_panel(img, draw, x, y, box_w, box_h, "NODE 1", subtitle="owns the path", accent=PANEL_BORDER, glow=False)

    # Files1 box
    x, y = positions[3]
    draw_panel(img, draw, x, y, box_w, box_h, "/projects", subtitle="Node 1 local files", accent="#4a0000", glow=False)

    # Web box (return)
    x, y = positions[4]
    draw_panel(img, draw, x, y, box_w, box_h, "ARIA.WEB", subtitle="returns data", accent=PANEL_BORDER_HI, glow=True)

    # Arrows between the example panels
    flow_color = CYAN

    # 1. Node2 -> Web (outbound)
    x0, y0 = positions[0][0] + box_w, positions[0][1] + box_h // 2 - 10
    x1, y1 = positions[1][0], positions[1][1] + box_h // 2
    draw_curved_arrow(draw, [(x0, y0), (x0 + 40, y0), (x1, y1)], color=flow_color, width=3)
    badge(draw, (x0 + x1) // 2, (y0 + y1) // 2 - 15, 1, flow_color)
    draw.text((x0 + 20, y0 - 22), "request", font=FONT_SMALL, fill=flow_color)

    # 2. Web -> Node1
    x0, y0 = positions[1][0] + box_w, positions[1][1] + box_h // 2
    x1, y1 = positions[2][0], positions[2][1] + box_h // 2 - 10
    draw_curved_arrow(draw, [(x0, y0), (x0 + 40, y0), (x1, y1)], color=flow_color, width=3)
    badge(draw, (x0 + x1) // 2, (y0 + y1) // 2 - 15, 2, flow_color)
    draw.text((x0 + 20, y0 - 22), "route", font=FONT_SMALL, fill=flow_color)

    # 3. Node1 -> Files1
    x0, y0 = positions[2][0] + box_w, positions[2][1] + box_h // 2
    x1, y1 = positions[3][0], positions[3][1] + box_h // 2
    draw_arrow(draw, x0, y0, x1, y1, color=flow_color, width=2)
    badge(draw, (x0 + x1) // 2, (y0 + y1) // 2, 3, flow_color)
    draw.text((x0 + 30, y0 - 22), "read", font=FONT_SMALL, fill=flow_color)

    # 4. Files1 -> Web (return)
    x0, y0 = positions[3][0] + box_w // 2, positions[3][1] + box_h
    x1, y1 = positions[4][0] + box_w, positions[4][1] + box_h // 2
    draw_curved_arrow(draw, [(x0, y0), (x0, y0 + 40), (x1 + 40, y1), (x1, y1)], color=flow_color, width=3)
    badge(draw, (x0 + x1) // 2 + 40, (y0 + y1) // 2 + 25, 4, flow_color)
    draw.text((x0 + 10, y0 + 20), "file data", font=FONT_SMALL, fill=flow_color)

    # 5. Web -> Node2 (final delivery)
    x0, y0 = positions[4][0], positions[4][1] + box_h // 2
    x1, y1 = positions[0][0] + box_w, positions[0][1] + box_h // 2 + 10
    draw_curved_arrow(draw, [(x0, y0), (x0 - 60, y0), (x1 + 40, y1), (x1, y1)], color=flow_color, width=3)
    badge(draw, (x0 + x1) // 2, (y0 + y1) // 2 + 20, 5, flow_color)
    draw.text((x0 - 90, y0 + 15), "deliver", font=FONT_SMALL, fill=flow_color)

    # --- Legend (bottom-right to avoid the example-flow panels) ---
    leg_w, leg_h = 760, 120
    leg_x, leg_y = W - leg_w - 60, H - 180
    rounded_rect(draw, [leg_x, leg_y, leg_x + leg_w, leg_y + leg_h], radius=8, fill=PANEL_BG, outline=PANEL_BORDER, width=1)
    draw.text((leg_x + 16, leg_y + 12), "LEGEND", font=FONT_BODY, fill=TEXT)
    items = [
        (ACCENT, "SignalR direct tunnel"),
        (AMBER, "LLM calls / format probes"),
        (CYAN, "Cross-node file request example"),
        (GREEN, "Verified / soul-bound"),
        (TEXT_DIM, "Local file access"),
    ]
    lx = leg_x + 20
    ly = leg_y + 46
    col_width = 360
    for i, (color, desc) in enumerate(items):
        if i > 0 and i % 2 == 0:
            lx = leg_x + 20
            ly += 28
        draw.rectangle([lx, ly, lx + 16, ly + 12], fill=color, outline=TEXT, width=1)
        draw.text((lx + 24, ly - 3), desc, font=FONT_SMALL, fill=TEXT_DIM)
        lx += col_width

    # --- Bottom disclaimer ---
    note = "Every secret stays on its node; the server only orchestrates and forgets."
    nw, _ = text_size(draw, note, FONT_SMALL)
    draw.text(((W - nw) // 2, H - 25), note, font=FONT_SMALL, fill=TEXT_DIM)

    img = img.convert("RGB")
    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    img.save(OUT_PATH, "PNG")
    print(f"Saved architecture diagram to {OUT_PATH}")


if __name__ == "__main__":
    main()
