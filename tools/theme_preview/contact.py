"""#601: contact sheet of the theme stills (2 columns, captions under each).  python3 contact.py STILLS_DIR OUT.png"""
import sys, glob, os
from PIL import Image, ImageDraw, ImageFont
files = sorted(glob.glob(os.path.join(sys.argv[1], "*.png")))
names = {}
np_ = os.path.join(sys.argv[1], "names.txt")
if os.path.exists(np_):
    for ln in open(np_, encoding="utf-8"):
        if "\t" in ln:
            f, n = ln.rstrip("\n").split("\t", 1)
            names[f] = n
cw, ch, pad, cap = 1280, 720, 24, 56
cols = 2
rows = (len(files) + cols - 1) // cols
sheet = Image.new("RGB", (cols * cw + (cols + 1) * pad, rows * (ch + cap) + (rows + 1) * pad), (14, 16, 22))
d = ImageDraw.Draw(sheet)
try:
    font = ImageFont.truetype(sys.argv[3] if len(sys.argv) > 3 else "DejaVuSans-Bold.ttf", 30)
except OSError:
    font = ImageFont.load_default()
for i, f in enumerate(files):
    x = pad + (i % cols) * (cw + pad); y = pad + (i // cols) * (ch + cap + pad)
    sheet.paste(Image.open(f).convert("RGB").resize((cw, ch), Image.LANCZOS), (x, y))
    name = names.get(os.path.basename(f)) or os.path.splitext(os.path.basename(f))[0].split("_", 1)[1].replace("-", " ")
    name = name.upper()
    d.text((x + 4, y + ch + 12), name, fill=(255, 216, 118), font=font)
sheet.save(sys.argv[2])
print("wrote", sys.argv[2], sheet.size)
