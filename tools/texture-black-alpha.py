#!/usr/bin/env python3
"""texture-black-alpha.py — a cut-out mask for atlases photographed on black (Poly Haven's grass / reed cards ship a
diffuse on a black background and no alpha map): alpha = 0 where the diffuse is near black, 255 on the blades, a soft
ramp between, the blade colour padded outwards (texture-solidify) so mips and bilinear filtering never pull black into
the edges. Writes <name>_rgba.png next to the diffuse and, with --gltf, points the model's base-colour image at it and
sets the material to MASK / double-sided.

    texture-black-alpha.py <diffuse.jpg> [--threshold 0.06] [--soft 0.06] [--gltf model.gltf ...]
"""
import argparse, json, os, sys
from PIL import Image, ImageFilter
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import import_module
solidify = import_module("texture-solidify").solidify


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("diffuse"); ap.add_argument("--threshold", type=float, default=0.06); ap.add_argument("--soft", type=float, default=0.06)
    ap.add_argument("--gltf", nargs="*", default=[])
    a = ap.parse_args()
    img = Image.open(a.diffuse).convert("RGB")
    lum = img.convert("L")
    lo, hi = a.threshold * 255.0, (a.threshold + a.soft) * 255.0
    alpha = lum.point(lambda v: 0 if v <= lo else (255 if v >= hi else int(255.0 * (v - lo) / max(hi - lo, 1.0))))
    # a 3x3 max keeps thin blade tips that the threshold would nibble away
    alpha = alpha.filter(ImageFilter.MaxFilter(3))
    rgba = img.copy(); rgba.putalpha(alpha)
    rgba = solidify(rgba, 16)
    base, _ = os.path.splitext(a.diffuse)
    out = base.replace("_diff", "") + "_rgba.png" if "_diff" in base else base + "_rgba.png"
    rgba.save(out, optimize=True)
    cover = sum(1 for v in alpha.resize((256, 256)).getdata() if v > 127) / 65536.0
    print(f"{os.path.basename(out)}: {rgba.size[0]}x{rgba.size[1]}, {cover * 100:.1f} % opaque")
    for g in a.gltf:
        d = json.load(open(g, encoding="utf-8"))
        gdir = os.path.dirname(os.path.abspath(g))
        rel = os.path.relpath(out, gdir).replace(os.sep, "/")
        diff_rel = os.path.relpath(os.path.abspath(a.diffuse), gdir).replace(os.sep, "/")
        changed = 0
        for im in d.get("images", []):
            if im.get("uri") == diff_rel: im["uri"] = rel; changed += 1
        for m in d.get("materials", []):
            m["alphaMode"] = "MASK"; m["alphaCutoff"] = 0.5; m["doubleSided"] = True
        json.dump(d, open(g, "w", encoding="utf-8"), indent=1)
        print(f"  {os.path.basename(g)}: {changed} image(s) -> {rel}, materials MASK / double-sided")


if __name__ == "__main__":
    main()
