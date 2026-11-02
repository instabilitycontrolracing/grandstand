#!/usr/bin/env python3
"""#549 mock-up stills: "can the cards have more room?" - options drawn over the real column card.

Input: the parts from `storycard_preview OUT --roomshots` (OUT/room_parts/bg.png + card_*.png, 1920x1080).
Output: one PNG per option per card in OUT_DIR (1920x1080, design units = pixels).
Each still: the card as it is drawn today, scaled evenly (no stretching) into the option's footprint, the gained area
tinted green, today's footprint as a dashed gold line, and the numbers. A mock-up, not a render of new code.

    python3 card_room.py PARTS_DIR OUT_DIR
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFont

TODAY = (1290, 172, 1884, 1042)          # StoryCardsColumn: ColW 594, ColY0 172, bottom 1042 (measured)
OPTIONS = [
    ("today", "TODAY", TODAY, "the column as it is: 594 x 870"),
    ("A_up", "A - UP INTO THE EMPTY STRIP", (1290, 126, 1884, 1042),
     "top 172 -> 126: the strip under the session bar (bar ends at 110) is empty outside battles; +46 tall"),
    ("B_edges", "B - A + THE SAFE EDGES", (1290, 126, 1896, 1056),
     "A, plus right edge to the radio card's 24 px margin and bottom to 1056: +12 wide, +60 tall"),
    ("C_wider", "C - B + INTO THE CENTRE THIRD", (1200, 126, 1896, 1056),
     "B, plus the left edge 90 px into the centre third (where the action is): +102 wide, +60 tall"),
]


def font(size):
    for f in ("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",):
        if os.path.exists(f):
            return ImageFont.truetype(f, size)
    return ImageFont.load_default()


def dashed(d, box, color, width=3, dash=14, gap=10):
    x0, y0, x1, y1 = box
    for (a, b) in (((x0, y0), (x1, y0)), ((x1, y0), (x1, y1)), ((x1, y1), (x0, y1)), ((x0, y1), (x0, y0))):
        length = abs(b[0] - a[0]) + abs(b[1] - a[1])
        dx = (b[0] - a[0]) / max(1, length)
        dy = (b[1] - a[1]) / max(1, length)
        t = 0
        while t < length:
            e = min(length, t + dash)
            d.line((a[0] + dx * t, a[1] + dy * t, a[0] + dx * e, a[1] + dy * e), fill=color, width=width)
            t += dash + gap


def compose(parts, out, card):
    bg = Image.open(os.path.join(parts, "bg.png")).convert("RGBA")
    cimg = Image.open(os.path.join(parts, f"card_{card}.png")).convert("RGBA")
    pad = 6
    crop = cimg.crop((TODAY[0] - pad, TODAY[1] - pad, TODAY[2] + pad, TODAY[3] + pad))
    ow, oh = TODAY[2] - TODAY[0], TODAY[3] - TODAY[1]
    for key, title, box, note in OPTIONS:
        im = bg.copy()
        nw, nh = box[2] - box[0], box[3] - box[1]
        s = min(nw / ow, nh / oh)
        cw, ch = int(round(crop.width * s)), int(round(crop.height * s))
        sc = crop.resize((cw, ch), Image.LANCZOS)
        # right / top aligned like the column (it hangs from the right edge under the bar)
        x = box[2] - int(round((ow + pad) * s))
        y = box[1] - int(round(pad * s))
        if key != "today":
            tint = Image.new("RGBA", im.size, (0, 0, 0, 0))
            td = ImageDraw.Draw(tint)
            td.rectangle(box, fill=(40, 220, 120, 70))
            td.rectangle(TODAY, fill=(0, 0, 0, 0))
            im = Image.alpha_composite(im, tint)
        im.alpha_composite(sc, (x, y))
        d = ImageDraw.Draw(im)
        dashed(d, TODAY, (232, 194, 90, 255))
        if key != "today":
            d.rectangle(box, outline=(40, 220, 120, 255), width=3)
        # the caption sits over the sky in the centre, clear of the card and the tower
        gain = (nw * nh) / (ow * oh) - 1
        d.rectangle((320, 150, 1180, 330), fill=(0, 0, 0, 200))
        d.text((340, 165), f"#549  {title}", font=font(30), fill=(255, 255, 255, 255))
        d.text((340, 210), f"{card}:  {nw} x {nh}  (area {gain * 100:+.0f}%)", font=font(24), fill=(232, 194, 90, 255))
        words, line, y = note.split(" "), "", 250
        for w in words:
            if len(line) + len(w) > 62:
                d.text((340, y), line, font=font(20), fill=(200, 210, 220, 255)); y += 26; line = ""
            line += w + " "
        d.text((340, y), line, font=font(20), fill=(200, 210, 220, 255))
        im.convert("RGB").save(os.path.join(out, f"room_{key}_{card}.png"))


def main():
    parts, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    for card in ("RaceSoFar", "PitStops", "FastestLaps"):
        if os.path.exists(os.path.join(parts, f"card_{card}.png")):
            compose(parts, out, card)


if __name__ == "__main__":
    main()
