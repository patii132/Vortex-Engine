#!/usr/bin/env python3
"""palm-gen.py — a procedural date palm as glTF: a tapered, slightly leaning trunk, a dense crown of pinnate fronds in
three rings (young ones rising, the middle ring level, the old ones drooping) and a skirt of dead fronds hanging down the
trunk — the silhouette of Phoenix dactylifera that oasis pictures are made of. Every frond is a folded strip of quads
carrying ONE frond cut-out from a palm atlas (the Yughues pack's right half: a whole leaf on alpha), so a 36-frond crown is
~1 000 triangles. Three primitives (trunk / fronds / dead fronds) share the atlas; the dead fronds take a brown tint.

    palm-gen.py --atlas palm_diffuse.png [--normal palm_normal.png] --out Assets/Models/Palms/date_palm_a/date_palm_a.gltf
                [--height 10] [--lean 0.6] [--fronds 36] [--dead 10] [--frond-length 3.6] [--seed 1]

Atlas layout (u, v in glTF texture space): the trunk fills u 0..0.5 (tiled along the trunk), one frond fills u 0.5..1
with its base at v = 1 and its tip at v = 0. Needs Pillow only for copying the textures next to the model.
"""
import argparse, json, math, os, random, struct, sys

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--atlas", required=True); ap.add_argument("--normal")
    ap.add_argument("--out", required=True)
    ap.add_argument("--height", type=float, default=10.0, help="trunk height (m)")
    ap.add_argument("--lean", type=float, default=0.6, help="how far the top leans sideways (m)")
    ap.add_argument("--radius", type=float, default=0.32, help="trunk radius at the base (m)")
    ap.add_argument("--fronds", type=int, default=36)
    ap.add_argument("--dead", type=int, default=10)
    ap.add_argument("--frond-length", type=float, default=3.6)
    ap.add_argument("--frond-width", type=float, default=1.05)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--name", default=None)
    ap.add_argument("--fern", action="store_true", help="a fern / cycad clump instead of a palm: fronds from the ground, no crown rings")
    ap.add_argument("--litter", action="store_true", help="one dead frond lying on the ground (the litter under a grove); --fronds = how many in the pile")
    a = ap.parse_args()
    rnd = random.Random(a.seed)

    P, N, T = [], [], []          # positions, normals, uvs
    prims = {"trunk": [], "fronds": [], "dead": []}

    def vertex(p, n, uv):
        P.append(p); N.append(n); T.append(uv); return len(P) - 1

    # ---------------------------------------------------------------- the trunk: rings along a bent axis
    H = a.height
    lean_dir = rnd.uniform(0, 2 * math.pi)
    def axis(t):   # the trunk's centre line: a parabola bending towards lean_dir, steeper near the top
        s = t * t
        return (math.cos(lean_dir) * a.lean * s, H * t, math.sin(lean_dir) * a.lean * s)
    rings, segs = 14, 14
    ring_idx = []
    for r in range(0 if a.litter else rings + 1):
        t = r / rings
        c = axis(t)
        rad = a.radius * (1.0 - 0.28 * t) * (1.0 + 0.12 * math.sin(t * 9.0 + 1.0))   # a little taper, slight bulges
        if t > 0.92: rad *= 1.0 + 1.4 * (t - 0.92) / 0.08 * 0.5                      # the crownshaft swells at the top
        row = []
        for s in range(segs + 1):
            ang = 2 * math.pi * s / segs
            nx, nz = math.cos(ang), math.sin(ang)
            p = (c[0] + nx * rad, c[1], c[2] + nz * rad)
            # the bark tiles every 1.6 m along the trunk (u 0..0.5 = the atlas' trunk half)
            row.append(vertex(p, (nx, 0.0, nz), (0.5 * s / segs, (H * t) / 1.6)))
        ring_idx.append(row)
    for r in range(0 if a.litter else rings):
        for s in range(segs):
            i0, i1, i2, i3 = ring_idx[r][s], ring_idx[r][s + 1], ring_idx[r + 1][s + 1], ring_idx[r + 1][s]
            prims["trunk"] += [i0, i2, i1, i0, i3, i2]
    # caps: a closed tube keeps its end rings under the engine's LOD decimation (an open boundary collapsed and the base floated)
    for r, ny in (() if a.litter else ((0, -1.0), (rings, 1.0))):
        c = axis(r / rings)
        centre = vertex((c[0], c[1] - 0.05 if r == 0 else c[1], c[2]), (0.0, ny, 0.0), (0.25, 0.5))
        for s in range(segs):
            i0, i1 = ring_idx[r][s], ring_idx[r][s + 1]
            prims["trunk"] += ([centre, i1, i0] if r == 0 else [centre, i0, i1])
    top = axis(1.0)

    # ---------------------------------------------------------------- the fronds: folded strips along a drooping rachis
    def frond(base, yaw, pitch0, droop, length, width, prim, fold=0.35, twist=0.0):
        """base: start point; yaw: direction around the trunk; pitch0: start angle above horizontal (rad); droop: how
        much the pitch falls over the length (rad); the strip is two half-cards folded down by `fold` (rad)."""
        n_seg = 9
        pts, frames = [], []
        p = list(base)
        for i in range(n_seg + 1):
            t = i / n_seg
            pitch = pitch0 - droop * (t * t)                                # the rachis bends more and more
            d = (math.cos(yaw) * math.cos(pitch), math.sin(pitch), math.sin(yaw) * math.cos(pitch))
            if i > 0:
                step = length / n_seg
                p = [p[0] + d[0] * step, p[1] + d[1] * step, p[2] + d[2] * step]
            # the side vector (perpendicular to the rachis, horizontal-ish), then the up vector
            side = (-math.sin(yaw), 0.0, math.cos(yaw))
            up = (d[1] * side[2] - d[2] * side[1], d[2] * side[0] - d[0] * side[2], d[0] * side[1] - d[1] * side[0])
            ul = math.sqrt(up[0] ** 2 + up[1] ** 2 + up[2] ** 2) or 1.0
            up = (up[0] / ul, up[1] / ul, up[2] / ul)
            if up[1] < 0: up = (-up[0], -up[1], -up[2])
            pts.append(tuple(p)); frames.append((side, up))
        # width profile: the atlas frond is widest around 40 % of its length — the card follows that loosely
        def w_at(t): return width * (0.35 + 0.65 * math.sin(math.pi * min(1.0, t * 1.15)) ** 0.7)
        rows = []
        for i in range(n_seg + 1):
            t = i / n_seg
            side, up = frames[i]
            w = w_at(t)
            c = pts[i]
            # three vertices across: left edge (folded down), the rachis, right edge (folded down)
            dy = math.sin(fold) * w * 0.5
            def off(k):   # k = -1, 0, 1
                return (c[0] + side[0] * w * 0.5 * k * math.cos(fold) - up[0] * dy * abs(k),
                        c[1] + side[1] * w * 0.5 * k * math.cos(fold) - up[1] * dy * abs(k),
                        c[2] + side[2] * w * 0.5 * k * math.cos(fold) - up[2] * dy * abs(k))
            v = 1.0 - t   # the atlas frond: base at v = 1, tip at v = 0
            row = [vertex(off(-1), up, (0.5, v)), vertex(off(0), up, (0.75, v)), vertex(off(1), up, (1.0, v))]
            rows.append(row)
        for i in range(n_seg):
            for k in range(2):
                i0, i1, i2, i3 = rows[i][k], rows[i][k + 1], rows[i + 1][k + 1], rows[i + 1][k]
                prims[prim] += [i0, i1, i2, i0, i2, i3]

    L, W = a.frond_length, a.frond_width
    n = a.fronds
    if a.litter:
        # dead fronds on the sand: flat, a little curl at the tip, each turned its own way; the pile's centre is the origin
        for i in range(n):
            yaw = rnd.uniform(0, 2 * math.pi)
            w = W * rnd.uniform(0.9, 1.1)
            lift = math.sin(0.3) * w * 0.5 + 0.015 + 0.02 * i   # the folded edges touch the sand, the rachis rides above it
            base = (rnd.uniform(-0.3, 0.3) * (n > 1), lift, rnd.uniform(-0.3, 0.3) * (n > 1))
            frond(base, yaw, 0.03 + rnd.uniform(0.0, 0.04), -0.06 + rnd.uniform(-0.03, 0.03), L * rnd.uniform(0.85, 1.1), w, "dead", fold=0.3)
        n = 0
    if a.fern:
        # a cycad / fern clump: fronds from a short stub in a bowl — the undergrowth of the reference
        for i in range(n):
            yaw = 2 * math.pi * (i + rnd.uniform(-0.3, 0.3)) / n
            t = i / max(1, n - 1)
            pitch = rnd.uniform(0.35, 1.15)
            base = (top[0] + math.cos(yaw) * 0.08, top[1] + rnd.uniform(0.0, 0.1), top[2] + math.sin(yaw) * 0.08)
            frond(base, yaw, pitch, 0.9 + rnd.uniform(-0.2, 0.3), L * rnd.uniform(0.75, 1.1), W * rnd.uniform(0.85, 1.1), "fronds", fold=0.45)
        n = 0
    # three rings: young (rising steeply), middle (level, long), old (drooping); each frond gets its own jitter
    ring_spec = [] if n == 0 else [(0.30, 1.20, 0.55, 0.78), (0.40, 0.55, 0.95, 1.0), (0.30, 0.05, 1.35, 0.95)]   # share, pitch, droop, length
    k = 0
    for share, pitch, droop, lf in ring_spec:
        cnt = max(1, int(round(n * share)))
        for i in range(cnt):
            yaw = 2 * math.pi * (i + rnd.uniform(-0.3, 0.3)) / cnt + k * 0.37
            base = (top[0] + math.cos(yaw) * 0.18, top[1] - 0.15 + rnd.uniform(-0.1, 0.1), top[2] + math.sin(yaw) * 0.18)
            frond(base, yaw, pitch + rnd.uniform(-0.15, 0.15), droop + rnd.uniform(-0.2, 0.2), L * lf * rnd.uniform(0.85, 1.1), W * rnd.uniform(0.85, 1.1), "fronds")
        k += 1
    # the skirt: dead fronds hanging down the trunk under the crown
    for i in range(a.dead):
        yaw = 2 * math.pi * (i + rnd.uniform(-0.3, 0.3)) / max(1, a.dead)
        base = (top[0] + math.cos(yaw) * 0.22, top[1] - 0.6 - rnd.uniform(0.0, 0.5), top[2] + math.sin(yaw) * 0.22)
        frond(base, yaw, -1.25 + rnd.uniform(-0.15, 0.1), 0.25, L * 0.75 * rnd.uniform(0.8, 1.0), W * 0.8, "dead", fold=0.5)

    # ---------------------------------------------------------------- glTF
    out_dir = os.path.dirname(os.path.abspath(a.out)); os.makedirs(out_dir, exist_ok=True)
    base = os.path.splitext(os.path.basename(a.out))[0]
    name = a.name or base
    pbuf, nbuf, tbuf = bytearray(), bytearray(), bytearray()
    mins = [1e30] * 3; maxs = [-1e30] * 3
    for p, nn, uv in zip(P, N, T):
        for c in range(3): mins[c] = min(mins[c], p[c]); maxs[c] = max(maxs[c], p[c])
        pbuf += struct.pack("<3f", *p); nbuf += struct.pack("<3f", *nn); tbuf += struct.pack("<2f", *uv)
    ibufs = {}
    for key in ("trunk", "fronds", "dead"):
        b = bytearray()
        for i in prims[key]: b += struct.pack("<I", i)
        ibufs[key] = bytes(b)
    blob = bytes(pbuf) + bytes(nbuf) + bytes(tbuf) + ibufs["trunk"] + ibufs["fronds"] + ibufs["dead"]
    with open(os.path.join(out_dir, base + ".bin"), "wb") as f: f.write(blob)
    # textures next to the model
    from PIL import Image
    images = []
    Image.open(a.atlas).convert("RGBA").save(os.path.join(out_dir, base + "_diffuse.png"), optimize=True)
    images.append({"uri": base + "_diffuse.png"})
    if a.normal and os.path.exists(a.normal):
        Image.open(a.normal).convert("RGB").save(os.path.join(out_dir, base + "_normal.png"), optimize=True)
        images.append({"uri": base + "_normal.png"})
    textures = [{"sampler": 0, "source": i} for i in range(len(images))]
    def mat(nm, tint, cutout):
        m = {"name": nm, "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "baseColorFactor": tint, "metallicFactor": 0.0, "roughnessFactor": 0.85}, "doubleSided": True}
        if len(images) > 1: m["normalTexture"] = {"index": 1}
        if cutout: m["alphaMode"] = "MASK"; m["alphaCutoff"] = 0.45
        return m
    materials = [mat(name + "_trunk", [0.9, 0.86, 0.78, 1.0], False), mat(name + "_fronds", [1.0, 1.0, 1.0, 1.0], True), mat(name + "_dead", [0.62, 0.46, 0.26, 1.0], True)]
    off_t = len(pbuf) + len(nbuf) + len(tbuf)
    views = [
        {"buffer": 0, "byteOffset": 0, "byteLength": len(pbuf), "target": 34962},
        {"buffer": 0, "byteOffset": len(pbuf), "byteLength": len(nbuf), "target": 34962},
        {"buffer": 0, "byteOffset": len(pbuf) + len(nbuf), "byteLength": len(tbuf), "target": 34962},
        {"buffer": 0, "byteOffset": off_t, "byteLength": len(ibufs["trunk"]), "target": 34963},
        {"buffer": 0, "byteOffset": off_t + len(ibufs["trunk"]), "byteLength": len(ibufs["fronds"]), "target": 34963},
        {"buffer": 0, "byteOffset": off_t + len(ibufs["trunk"]) + len(ibufs["fronds"]), "byteLength": len(ibufs["dead"]), "target": 34963},
    ]
    accessors = [
        {"bufferView": 0, "componentType": 5126, "count": len(P), "type": "VEC3", "min": mins, "max": maxs},
        {"bufferView": 1, "componentType": 5126, "count": len(P), "type": "VEC3"},
        {"bufferView": 2, "componentType": 5126, "count": len(P), "type": "VEC2"},
        {"bufferView": 3, "componentType": 5125, "count": len(prims["trunk"]), "type": "SCALAR"},
        {"bufferView": 4, "componentType": 5125, "count": len(prims["fronds"]), "type": "SCALAR"},
        {"bufferView": 5, "componentType": 5125, "count": len(prims["dead"]), "type": "SCALAR"},
    ]
    primitives = [{"attributes": {"POSITION": 0, "NORMAL": 1, "TEXCOORD_0": 2}, "indices": 3 + i, "material": i} for i in range(3) if accessors[3 + i]["count"] > 0]
    gltf = {
        "asset": {"version": "2.0", "generator": "Vortex palm-gen"},
        "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0, "name": name}],
        "meshes": [{"name": name, "primitives": primitives}],
        "materials": materials, "images": images, "textures": textures,
        "samplers": [{"magFilter": 9729, "minFilter": 9987, "wrapS": 10497, "wrapT": 10497}],
        "buffers": [{"uri": base + ".bin", "byteLength": len(blob)}],
        "bufferViews": views, "accessors": accessors,
    }
    with open(a.out, "w", encoding="utf-8") as f: json.dump(gltf, f, indent=1)
    tris = sum(len(v) for v in prims.values()) // 3
    print(f"{a.out}: {len(P)} vertices, {tris} triangles, {a.fronds} fronds + {a.dead} dead, height {maxs[1]:.1f} m")

if __name__ == "__main__":
    main()
