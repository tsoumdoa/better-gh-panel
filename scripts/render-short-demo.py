"""Render the 15-second illustrated Panel+ tour. Requires Pillow and ffmpeg."""
import math
import subprocess
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "docs" / "demo" / "panel-plus-short.mp4"
W, H, FPS = 1280, 720, 24
FONT = Path("C:/Windows/Fonts")
regular = str(FONT / "segoeui.ttf")
bold = str(FONT / "segoeuib.ttf")
mono = str(FONT / "consola.ttf")
fonts = {}

def font(size, kind="regular"):
    key = (size, kind)
    if key not in fonts:
        fonts[key] = ImageFont.truetype({"regular": regular, "bold": bold, "mono": mono}[kind], size)
    return fonts[key]

def text(draw, xy, value, size, fill, kind="regular"):
    draw.text(xy, value, font=font(size, kind), fill=fill)

def box(draw, xy, fill, radius=0, outline=None, width=1):
    draw.rounded_rectangle(xy, radius=radius, fill=fill, outline=outline, width=width)

def curve(draw, pts, color, width):
    points = []
    for n in range(101):
        t = n / 100
        x = (1-t)**3*pts[0][0] + 3*(1-t)**2*t*pts[1][0] + 3*(1-t)*t*t*pts[2][0] + t**3*pts[3][0]
        y = (1-t)**3*pts[0][1] + 3*(1-t)**2*t*pts[1][1] + 3*(1-t)*t*t*pts[2][1] + t**3*pts[3][1]
        points.append((x, y))
    draw.line(points, fill=color, width=width, joint="curve")

def frame(t):
    im = Image.new("RGB", (W, H), "#171f20")
    d = ImageDraw.Draw(im)
    # A restrained graph-paper ground and persistent identity.
    for x in range(0, W, 40):
        d.line((x, 0, x, H), fill="#1b2626")
    for y in range(0, H, 40):
        d.line((0, y, W, y), fill="#1b2626")
    text(d, (64, 40), "P+", 24, "#b9ed87", "bold")
    text(d, (108, 47), "PANEL+", 13, "#a8bbb0", "bold")
    text(d, (1040, 48), "ILLUSTRATED SAMPLE", 11, "#8a9c92", "mono")
    d.line((64, 91, 1216, 91), fill="#344642", width=1)

    if t < 5:
        label, title, detail = "01  /  INSPECT", "Which curves", "are longer?"
        note = "See measured lengths alongside your data."
    elif t < 10:
        label, title, detail = "02  /  FILTER", "Find curves over", "4,000 mm."
        note = "Compare by length, directly in Panel+."
    else:
        label, title, detail = "03  /  PREVIEW", "See the result", "in Rhino."
        note = "Select matching rows to preview geometry."
    text(d, (64, 150), label, 13, "#b9ed87", "mono")
    text(d, (61, 186), title, 48, "#f1f5ed", "bold")
    text(d, (61, 244), detail, 48, "#f1f5ed", "bold")
    d.line((64, 330, 110, 330), fill="#b9ed87", width=3)
    text(d, (64, 353), note, 18, "#aabbb0")
    text(d, (64, 633), "A clearer view of Grasshopper data.", 15, "#a2b3aa")
    d.rectangle((64, 676, 416, 679), fill="#364741")
    d.rectangle((64, 676, 64 + int(352 * min(t / 15, 1)), 679), fill="#b9ed87")

    # A schematic Rhino viewport. Selected rows are previewed at 10 seconds.
    box(d, (662, 125, 1216, 584), "#d9ddda", 10)
    box(d, (662, 125, 1216, 162), "#cbd2cd", 10)
    d.rectangle((662, 150, 1216, 162), fill="#cbd2cd")
    text(d, (683, 134), "RHINO  /  PERSPECTIVE", 12, "#54665c", "mono")
    for x in range(690, 1217, 48):
        d.line((x, 163, x, 584), fill="#ccd3ce")
    for y in range(185, 585, 48):
        d.line((662, y, 1216, y), fill="#ccd3ce")
    curves = [
        [(750, 476), (785, 374), (814, 442), (846, 354)],
        [(782, 508), (855, 300), (948, 457), (1015, 286)],
        [(894, 482), (923, 398), (987, 447), (1019, 366)],
        [(948, 512), (990, 292), (1095, 479), (1158, 253)],
    ]
    for i, pts in enumerate(curves):
        if t >= 10:
            color = "#569267" if i in (1, 3) else "#bdc6bf"
        else:
            color = "#b97870"
        curve(d, pts, color, 6 if t >= 10 and i in (1, 3) else 4)
    if t >= 10:
        box(d, (1003, 177, 1196, 209), "#f6faf4", 5)
        text(d, (1016, 182), "2 SELECTED CURVES", 12, "#427451", "bold")

    # Panel floats over the viewport, with matched rows kept in input order.
    x, y = 535, 261
    box(d, (x+5, y+8, x+412, y+304), "#8c9990", 6)
    box(d, (x, y, x+412, y+304), "#f7f9f4", 5, "#9daf9f")
    d.rectangle((x, y, x+5, y+42), fill="#5d886c")
    text(d, (x+17, y+8), "P+", 22, "#4c785a", "bold")
    filtered = t >= 6.5
    count = "2 / 4 items · 1 branch" if filtered else "4 items · 1 branch"
    text(d, (x+68, y+13), count, 13, "#778b7e")
    text(d, (x+339, y+13), "-   v", 15, "#7d9082")
    d.line((x+12, y+45, x+400, y+45), fill="#d8e2d7")
    if t < 5:
        search = "Length  ·  Display value"
    elif t < 6.5:
        typed = "4000"[:max(0, min(4, int((t - 5) * 3)))]
        search = "Length  >  " + typed + "│"
    else:
        search = "Length  >  4000"
    text(d, (x+17, y+54), search, 15, "#416e4d", "bold")
    text(d, (x+331, y+55), "All types", 12, "#7c8e80")
    d.line((x+12, y+83, x+400, y+83), fill="#d8e2d7")
    text(d, (x+18, y+91), "{0}    4 items", 13, "#73867a", "mono")
    values = [("0", "2,350"), ("1", "4,850"), ("2", "3,120"), ("3", "6,400")]
    shown = [values[1], values[3]] if filtered else values
    for idx, (number, value) in enumerate(shown):
        ry = y + 121 + idx * 30
        if t >= 10:
            d.rectangle((x+12, ry-1, x+399, ry+26), fill="#e1f0df")
        d.rectangle((x+15, ry+3, x+18, ry+22), fill="#80a988")
        text(d, (x+28, ry+1), number, 13, "#96a49a", "mono")
        text(d, (x+65, ry), "Crv", 14, "#607869")
        text(d, (x+181, ry), value + " mm", 14, "#314b39", "mono")
        d.line((x+14, ry+28, x+398, ry+28), fill="#e5ece3")
    d.line((x+12, y+260, x+400, y+260), fill="#d8e2d7")
    text(d, (x+17, y+271), "Preview: Selected", 12, "#65816c")
    text(d, (x+330, y+271), "Units  v", 12, "#65816c")
    # Pointer and click pulse provide motion between the clear, held states.
    if 4.3 <= t < 5:
        px, py = x+178, y+66
    elif 9 <= t < 10:
        px, py = x+200, y+(135 if t < 9.5 else 165)
    else:
        px = py = None
    if px is not None:
        r = int(7 + (t % 1) * 10)
        d.ellipse((px-r, py-r, px+r, py+r), outline="#5e9d6b", width=2)
        d.polygon([(px,py),(px,py+20),(px+6,py+15),(px+11,py+24),(px+15,py+22),(px+10,py+13),(px+19,py+13)], fill="#263c30", outline="white")
    return im

def main():
    cmd = ["ffmpeg", "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
           "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-", "-an", "-c:v", "libx264",
           "-crf", "19", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(OUT)]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    try:
        for n in range(15 * FPS):
            proc.stdin.write(frame(n / FPS).tobytes())
    finally:
        proc.stdin.close()
    if proc.wait():
        raise SystemExit("ffmpeg failed")
    frame(11).save(ROOT / "docs" / "demo" / "short-poster.jpg", quality=90)
    print(f"Created {OUT}")

if __name__ == "__main__":
    main()
