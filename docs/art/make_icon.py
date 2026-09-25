# Usage: python make_icon.py <out.ico> <preview.png>   (needs Pillow)
# Original app icon: a blueprint grid, an orange conveyor belt (with flow chevrons) through a splitter into a factory
# block. No game artwork, logo or trademark: generic industrial shapes only.
from PIL import Image, ImageDraw, ImageFilter
import sys

OUT_ICO = sys.argv[1]
OUT_PNG = sys.argv[2]

BG_TOP, BG_BOT = (27, 39, 51), (36, 54, 72)
GRID = (74, 128, 170)
ORANGE, ORANGE_DK = (242, 140, 40), (178, 92, 18)
STEEL, STEEL_LT, STEEL_DK = (156, 172, 186), (206, 218, 228), (88, 104, 118)
WHITE = (240, 246, 250)


def draw(S, simple=False):
    # an opaque canvas (semi-transparent strokes blend on it); the rounded corners are cut at the end
    im = Image.new('RGB', (S, S))
    g = ImageDraw.Draw(im)
    for y in range(S):
        t = y / (S - 1)
        g.line([(0, y), (S, y)], fill=tuple(int(BG_TOP[i] + (BG_BOT[i] - BG_TOP[i]) * t) for i in range(3)))
    mask = Image.new('L', (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.2), fill=255)
    d = ImageDraw.Draw(im, 'RGBA')
    u = S / 16.0  # a 16-unit design grid

    # blueprint grid (not at the smallest sizes)
    if not simple:
        for k in range(1, 16):
            w = max(1, int(S / 256)) if k % 4 else max(1, int(S / 128))
            a = 38 if k % 4 else 70
            d.line([(k * u, 1.2 * u), (k * u, S - 1.2 * u)], fill=GRID + (a,), width=w)
            d.line([(1.2 * u, k * u), (S - 1.2 * u, k * u)], fill=GRID + (a,), width=w)

    # factory block (top right): a building with a sawtooth roof and a chimney
    bx0, by0, bx1, by1 = 8.6 * u, 3.4 * u, 13.4 * u, 8.6 * u
    d.rectangle([12.0 * u, 2.2 * u, 12.9 * u, 4.6 * u], fill=STEEL_DK)             # chimney
    teeth = 3
    tw = (bx1 - bx0) / teeth
    roof = [(bx0, by0 + 1.3 * u)]
    for i in range(teeth):
        roof += [(bx0 + i * tw, by0), (bx0 + (i + 1) * tw, by0 + 1.3 * u)]
    roof += [(bx1, by1), (bx0, by1)]
    d.polygon(roof, fill=STEEL)
    d.polygon(roof, outline=STEEL_DK, width=max(1, int(S / 96)))
    if not simple:
        for i in range(teeth):                                                        # roof glazing
            x = bx0 + i * tw
            d.polygon([(x + 0.15 * tw, by0 + 0.35 * u), (x + 0.15 * tw, by0 + 1.15 * u), (x + 0.85 * tw, by0 + 1.15 * u)], fill=STEEL_LT)
    # door (where the belt enters)
    d.rectangle([bx0 + 1.6 * u, by1 - 2.2 * u, bx0 + 3.2 * u, by1], fill=(44, 60, 76))

    # the belt: from the bottom left, right, then up into the factory door; a branch leaves the splitter to the right
    bw = 2.0 * u if not simple else 2.6 * u
    path = [(1.6 * u, 12.4 * u), (bx0 + 2.4 * u, 12.4 * u), (bx0 + 2.4 * u, by1 - 0.2 * u)]
    branch = [(bx0 + 2.4 * u, 12.4 * u), (14.4 * u, 12.4 * u)]

    def belt(pts, width):
        for a, b in zip(pts, pts[1:]):
            d.line([a, b], fill=ORANGE_DK, width=int(width + 0.5 * u))
        for p in pts[1:-1]:
            r = (width + 0.5 * u) / 2
            d.rectangle([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=ORANGE_DK)
        for a, b in zip(pts, pts[1:]):
            d.line([a, b], fill=ORANGE, width=int(width))
        for p in pts[1:-1]:
            r = width / 2
            d.rectangle([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=ORANGE)

    belt(branch, bw * 0.8)
    belt(path, bw)

    # flow chevrons on the belt
    if not simple:
        def chev(cx, cy, dx, dy, s):
            px, py = -dy, dx
            pts = [(cx - dx * s * 0.5 + px * s * 0.6, cy - dy * s * 0.5 + py * s * 0.6), (cx + dx * s * 0.5, cy + dy * s * 0.5),
                   (cx - dx * s * 0.5 - px * s * 0.6, cy - dy * s * 0.5 - py * s * 0.6)]
            d.line(pts, fill=(60, 34, 8), width=max(1, int(S / 64)), joint='curve')
        for x in (3.2, 5.6):
            chev(x * u, 12.4 * u, 1, 0, 0.9 * u)
        chev(bx0 + 2.4 * u, 10.0 * u, 0, -1, 0.9 * u)
        chev(13.2 * u, 12.4 * u, 1, 0, 0.75 * u)

    # the splitter where the belt turns: a steel box with a white outline
    sx, sy, sr = bx0 + 2.4 * u, 12.4 * u, 1.45 * u if not simple else 1.7 * u
    d.rounded_rectangle([sx - sr, sy - sr, sx + sr, sy + sr], radius=0.35 * u, fill=STEEL_LT, outline=WHITE, width=max(1, int(S / 80)))
    if not simple:
        d.rounded_rectangle([sx - 0.55 * u, sy - 0.55 * u, sx + 0.55 * u, sy + 0.55 * u], radius=0.15 * u, fill=STEEL_DK)
    out = im.convert('RGBA')
    out.putalpha(mask)
    return out


big = draw(1024)
big.save(OUT_PNG)
frames = []
for s in (256, 128, 64, 48, 32, 24, 16):
    src = draw(1024, simple=s <= 24)
    frames.append(src.resize((s, s), Image.LANCZOS))
frames[0].save(OUT_ICO, format='ICO', sizes=[(f.width, f.height) for f in frames], append_images=frames[1:])
# preview sheet: the sizes side by side (small ones enlarged, pixelated)
sheet = Image.new('RGBA', (1024 + 40 + 5 * 136, 1024), (240, 240, 240, 255))
sheet.paste(big, (0, 0), big)
x = 1064
for f in frames[2:]:
    up = f.resize((128, 128), Image.NEAREST)
    sheet.paste(up, (x, 440), up)
    x += 136
sheet.save(OUT_PNG.replace('.png', '-sizes.png'))
print('ok', [f.size for f in frames])
