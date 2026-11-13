# Renders the Mod DB logo (480x320): a world map wrapped onto a cylinder.
# Usage: make-logo.py <map.png> <out.png> [seam column]  (Earth: ../Earth/preview/global.png out.png 144)
import math, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFont

W, H = 480, 320
src = np.asarray(Image.open(sys.argv[1]).convert("RGB")).astype(np.float32) / 255
src = src[2:-2, 2:-2]  # drop the border lines
if len(sys.argv) > 3:  # put the seam (jump line) at u = 0
    src = np.roll(src, -int(sys.argv[3]), axis=1)
sh, sw, _ = src.shape

# Background: dark sea-blue gradient
yy = np.linspace(0, 1, H)[:, None]
bg = (1 - yy) * np.array([0.10, 0.16, 0.26]) + yy * np.array([0.03, 0.06, 0.11])
img = np.repeat(bg[:, None, :], W, axis=1)

cx, R, b = 140.0, 84.0, 22.0           # centre, radius, ellipse half-height
top, height = 38.0, 248.0              # top cap centre y, side height
rot = 0.33                              # Atlantic in front: the Americas, Europe and Africa

ys, xs = np.mgrid[0:H, 0:W].astype(np.float32)
dx = (xs + 0.5 - cx) / R
inside = np.abs(dx) < 1
c = np.sqrt(np.clip(1 - dx * dx, 0, 1))
h = ys + 0.5 - top - b * c
side = inside & (h >= 0) & (h <= height)
theta = np.arcsin(np.clip(dx, -1, 1))
u = ((theta / (2 * math.pi) + rot) % 1.0)
v = np.clip(h / height, 0, 1)
tex = src[np.clip((v * (sh - 1)).astype(int), 0, sh - 1), np.clip((u * (sw - 1)).astype(int), 0, sw - 1)]
# seam mist near u = 0
d = np.minimum(u, 1 - u) * sw
mist = np.clip(1 - d / (0.02 * sw), 0, 1)[..., None] * 0.65
tex = tex * (1 - mist) + np.array([0.85, 0.9, 0.95]) * mist
light = (0.45 + 0.6 * np.clip(c * 0.8 + dx * -0.35 + 0.2, 0, 1))[..., None]
img[side] = np.clip(tex * light, 0, 1)[side]

# Top cap: the north pole, ice inside the Wall's ring
ex = (xs + 0.5 - cx) / R
ey = (ys + 0.5 - top) / b
r2 = ex * ex + ey * ey
cap = r2 <= 1
ice = np.array([0.93, 0.96, 1.0]) - 0.12 * np.clip(r2, 0, 1)[..., None] * np.array([1, 0.8, 0.5])
img[cap] = ice[cap]
ring = cap & (r2 > 0.80)
img[ring] = np.array([0.80, 0.87, 0.95])

out = Image.fromarray((img * 255).astype(np.uint8))
d = ImageDraw.Draw(out)
# outline of the bottom rim (south wall)
d.arc([cx - R, top + height - b, cx + R, top + height + b], 0, 180, fill=(220, 232, 245), width=3)
# east-west arrow around the drum
ay = top + height * 0.5
d.arc([cx - R - 18, ay - b - 14, cx + R + 18, ay + b + 14], 25, 160, fill=(255, 214, 120), width=3)
ax, ay2 = cx + (R + 18) * math.cos(math.radians(20)), ay + (b + 14) * math.sin(math.radians(20))
d.polygon([(ax + 2, ay2 - 12), (ax + 12, ay2 + 4), (ax - 8, ay2 + 2)], fill=(255, 214, 120))

def font(names, size):
    for n in names:
        try: return ImageFont.truetype("C:/Windows/Fonts/" + n, size)
        except OSError: pass
    return ImageFont.load_default()

f1 = font(["georgiab.ttf", "timesbd.ttf"], 40)
f2 = font(["georgia.ttf", "times.ttf"], 18)
tx = 268
d.text((tx, 40), "Cylinder", font=f1, fill=(240, 244, 250))
d.text((tx, 84), "World", font=f1, fill=(240, 244, 250))
d.text((tx + 2, 138), "East-west wrap", font=f2, fill=(190, 205, 225))
d.text((tx + 2, 162), "Climate by latitude", font=f2, fill=(190, 205, 225))
d.text((tx + 2, 186), "Ice walls at the poles", font=f2, fill=(190, 205, 225))
out.save(sys.argv[2])
