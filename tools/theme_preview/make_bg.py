"""#601: a procedural trackside TV picture (3840x2160) to put the theme previews on - sky, a tree line, a grandstand,
kerbs, the track running into the distance and two blurred race cars. No photos, nothing personal or trademarked.
    python3 make_bg.py out.png [dusk]
"""
import sys, random
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

W, H = 3840, 2160
out = sys.argv[1] if len(sys.argv) > 1 else "bg.png"
dusk = len(sys.argv) > 2 and sys.argv[2] == "dusk"
rnd = random.Random(601)
hz = int(H * 0.40)

img = Image.new("RGB", (W, H))
px = np.zeros((H, W, 3), np.float32)
# sky: a soft vertical gradient with hazy cloud bands
top, bot = (np.array([78, 112, 158]), np.array([186, 200, 214])) if not dusk else (np.array([40, 46, 84]), np.array([226, 150, 110]))
for y in range(hz):
    k = y / hz
    px[y, :, :] = top * (1 - k) + bot * k
noise = np.random.default_rng(1).random((H // 64 + 2, W // 64 + 2)).astype(np.float32)
cl = np.array(Image.fromarray((noise * 255).astype(np.uint8)).resize((W, H), Image.BICUBIC).filter(ImageFilter.GaussianBlur(40)), np.float32) / 255
px[:hz] += ((cl[:hz] - 0.5) * 40)[..., None]
# ground below the horizon
px[hz:] = np.array([52, 74, 44])
img = Image.fromarray(np.clip(px, 0, 255).astype(np.uint8))
d = ImageDraw.Draw(img)
# tree line (bumpy silhouette)
pts = [(0, hz)]
x = 0
while x <= W:
    pts.append((x, hz - 60 - rnd.randint(0, 120)))
    x += rnd.randint(30, 90)
pts += [(W, hz + 20), (0, hz + 20)]
d.polygon(pts, fill=(30, 48, 32))
# grandstand on the right: a block with rows of people (speckles) and a roof
gs = (int(W * 0.58), hz - 230, int(W * 1.0), hz + 70)
d.rectangle(gs, fill=(92, 96, 104))
for row in range(gs[1] + 30, gs[3] - 10, 16):
    for col in range(gs[0] + 8, gs[2], 10):
        c = rnd.choice([(200, 60, 50), (230, 230, 230), (40, 70, 160), (60, 60, 60), (220, 190, 60), (120, 120, 130)])
        d.rectangle((col, row, col + 6, row + 9), fill=c)
d.polygon([(gs[0] - 40, gs[1]), (gs[2], gs[1] - 70), (gs[2], gs[1] - 40), (gs[0] - 40, gs[1] + 24)], fill=(210, 214, 220))
# grass verge and the track (perspective trapezoid)
d.rectangle((0, hz + 20, W, H), fill=(62, 96, 52))
road = [(int(W * 0.40), hz + 20), (int(W * 0.62), hz + 20), (int(W * 1.25), H), (int(W * -0.25), H)]
d.polygon(road, fill=(58, 60, 64))
# kerbs along both edges (red / white blocks getting bigger towards the camera)
def edge(t, side):
    x0, y0 = road[0] if side < 0 else road[1]
    x1, y1 = road[3] if side < 0 else road[2]
    return x0 + (x1 - x0) * t, y0 + (y1 - y0) * t
for side in (-1, 1):
    t = 0.0
    i = 0
    while t < 1:
        dt = 0.012 + t * 0.05
        a, b = edge(t, side), edge(min(1, t + dt), side)
        wdt = 6 + 90 * t
        col = (200, 30, 36) if i % 2 == 0 else (236, 236, 236)
        d.polygon([(a[0], a[1]), (b[0], b[1]), (b[0] - side * wdt * 1.4, b[1]), (a[0] - side * wdt, a[1])], fill=col)
        t += dt; i += 1
# centre dashes
t = 0.02
while t < 1:
    dt = 0.02 + t * 0.06
    xa = W * 0.51 + (W * 0.5 - W * 0.51) * t; ya = hz + 20 + (H - hz - 20) * t
    yb = hz + 20 + (H - hz - 20) * min(1, t + dt * 0.5)
    wd = 3 + 26 * t
    d.rectangle((xa - wd, ya, xa + wd, yb), fill=(222, 222, 222))
    t += dt * 1.6
img = img.filter(ImageFilter.GaussianBlur(2.2))
# asphalt grain
arr = np.array(img, np.int16)
g = np.random.default_rng(2).integers(-9, 10, (H, W, 1))
arr[hz + 20:] += g[hz + 20:]
img = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))

def car(cx, cy, s, body, accent):
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    q = ImageDraw.Draw(layer)
    q.ellipse((cx - 260 * s, cy + 20 * s, cx + 260 * s, cy + 80 * s), fill=(0, 0, 0, 120))                 # shadow
    q.rounded_rectangle((cx - 240 * s, cy - 70 * s, cx + 240 * s, cy + 40 * s), 40 * s, fill=body)          # body
    q.polygon([(cx - 120 * s, cy - 70 * s), (cx - 70 * s, cy - 150 * s), (cx + 70 * s, cy - 150 * s), (cx + 120 * s, cy - 70 * s)], fill=(24, 26, 30, 255))
    q.rectangle((cx - 250 * s, cy - 120 * s, cx + 250 * s, cy - 96 * s), fill=body)                        # rear wing
    q.rectangle((cx - 240 * s, cy - 20 * s, cx + 240 * s, cy - 2 * s), fill=accent)                        # livery stripe
    for wx in (-180, 180):
        q.ellipse((cx + (wx - 55) * s, cy - 10 * s, cx + (wx + 55) * s, cy + 70 * s), fill=(16, 16, 18, 255))
    q.rectangle((cx - 210 * s, cy - 40 * s, cx - 150 * s, cy - 26 * s), fill=(255, 60, 40, 255))             # lights
    q.rectangle((cx + 150 * s, cy - 40 * s, cx + 210 * s, cy - 26 * s), fill=(255, 60, 40, 255))
    return layer.filter(ImageFilter.GaussianBlur(3 * s))

img = img.convert("RGBA")
img.alpha_composite(car(int(W * 0.53), int(H * 0.56), 0.55, (40, 90, 200, 255), (240, 240, 240, 255)))
img.alpha_composite(car(int(W * 0.47), int(H * 0.80), 1.35, (236, 236, 240, 255), (200, 30, 40, 255)))
img = img.convert("RGB")
if dusk:
    arr = np.array(img, np.float32) * np.array([0.72, 0.66, 0.78])
    img = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8))
img.save(out)
print("wrote", out)
