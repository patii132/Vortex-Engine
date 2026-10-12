#!/usr/bin/env python3
"""palm-atlas-recolor.py — the Yughues palm atlas re-coloured for date palms: its trunk half is grey-white diamonds at
full contrast (a pineapple on screen), a date palm's bark is ochre-brown with the leaf bases as darker rims; its frond is a
dark olive cycad leaf, a date palm's is a lighter yellow-green. The trunk half (u 0..0.5) is mapped through a brown ramp
with the contrast compressed, the frond half (u 0.5..1) is brightened and warmed; the alpha stays.

    palm-atlas-recolor.py --in palm_straight_diffuse.png --out date_palm_diffuse.png [--bark-contrast 0.55] [--frond-gain 1.18]
"""
import argparse
from PIL import Image

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", required=True); ap.add_argument("--out", required=True)
    ap.add_argument("--bark-contrast", type=float, default=0.55)
    ap.add_argument("--bark-mid", type=float, default=0.42)
    ap.add_argument("--frond-gain", type=float, default=1.18)
    a = ap.parse_args()
    im = Image.open(a.inp).convert("RGBA")
    w, h = im.size
    px = im.load()
    dark = (0.30, 0.19, 0.11)    # the rims between the leaf bases
    light = (0.84, 0.66, 0.45)   # the sunlit face of a leaf base
    half = w // 2
    for y in range(h):
        for x in range(w):
            r, g, b, al = px[x, y]
            if x < half:
                l = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0
                t = a.bark_mid + a.bark_contrast * (l - 0.5)
                t = 0.0 if t < 0.0 else (1.0 if t > 1.0 else t)
                # a little of the source's own hue variation keeps the moss / dust patches
                kr = 1.0 + 0.12 * ((r - l * 255.0) / 255.0)
                kg = 1.0 + 0.12 * ((g - l * 255.0) / 255.0)
                px[x, y] = (int(255 * min(1.0, (dark[0] + (light[0] - dark[0]) * t) * kr)),
                            int(255 * min(1.0, (dark[1] + (light[1] - dark[1]) * t) * kg)),
                            int(255 * min(1.0, (dark[2] + (light[2] - dark[2]) * t))), al)
            else:
                # the frond: brighter, the green warmed towards yellow (more red, a touch less blue)
                px[x, y] = (min(255, int(r * a.frond_gain * 1.08)), min(255, int(g * a.frond_gain)), min(255, int(b * a.frond_gain * 0.9)), al)
    im.save(a.out, optimize=True)
    print(a.out, im.size)

if __name__ == "__main__":
    main()
