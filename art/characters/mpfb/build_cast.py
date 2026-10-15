"""Builds the framework-check cast from shared parts (#10028).

usage: python3 -I art/characters/mpfb/build_cast.py <work dir> [name ...]

Reads cast.json and, for each character:
- fits its MPFB body (bodies/<recipe>.npy from dump_body.py, in the work dir) onto its
  skeleton frame;
- cuts its parts from MakeHuman's fixed topology:
  - body: skin and under-clothes;
  - face: the shared face layout, eye openings fanned shut;
  - outer: shells offset from the body's own faces, MakeHuman UVs;
  - skirt: the skirt helper flared, cylindrical UVs;
  - hair and hat: the hair helper or a made hat;
- paints its textures from the costume's region list on the canonical body (paint.py);
- writes content/game/characters/cast/<name>.glb, its clip pack (#10018: one per character) and
  the cast manifest the product reads.

Every costume region is defined once on the canonical body (the neutral MPFB body fitted to UAL's
T-pose, storybook proportions). The same costume therefore fits every body, because they share
MakeHuman's topology and UV layout.
"""
import json
import math
import zlib
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent))
from basemesh import BaseMesh, to_gltf  # noqa: E402
from fit import fit, ual_name  # noqa: E402
from paint import Canvas, band_canvas, in_region, rasterise, hexrgb, HIP_Y  # noqa: E402
from parts import part, welded_normals  # noqa: E402
from fit_to_ual import inflate  # noqa: E402
import frame as frames  # noqa: E402

ROOT = HERE.parents[2]
UAL = ROOT / "content/animations/UAL1_Standard.glb"
OUT = ROOT / "content/game/characters/cast"
SIZES = {"body": 512, "outer": 512, "skirt": 256, "hair": 128, "hat": 128, "face": 128}
REPEAT_SLOTS = {"skirt", "hair", "hat"}
DOUBLE_SIDED = {"skirt", "hair", "hat"}  # thin shells seen from inside at hems and brims
FACE_CELL_INSET = 0  # the face sheets fill each cell edge to edge
SOCKET_FILL_ITERATIONS = 40
FAN_FORWARD = 0.001  # eye fans sit at the lid rims, in front of the lids' inner surfaces


def cylinder_uv(points, axis_x, axis_z, top, bottom):
    """u around the body (front 0.5), v down from top to bottom."""
    u = np.arctan2(points[:, 0] - axis_x, points[:, 2] - axis_z) / (2 * math.pi) + 0.5
    v = (top - points[:, 1]) / (top - bottom)
    return np.stack([u, v], 1)


def unwrap_seam(corner_uv):
    """Triangles crossing the back seam take u past 1 instead of wrapping (sampler repeats u)."""
    uv = corner_uv.copy()
    u = uv[..., 0]
    span = u.max(1) - u.min(1)
    wrap = span > 0.5
    u[wrap] = np.where(u[wrap] < 0.5, u[wrap] + 1, u[wrap])
    return uv


def flip_v(uv):
    """parts.part takes OBJ-style UVs (v up); cylinder and layout UVs are image-style (v down)."""
    out = uv.copy()
    out[..., 1] = 1 - out[..., 1]
    return out


class Builder:
    def __init__(self, work):
        self.work = Path(work)
        self.cast = json.loads((HERE / "cast.json").read_text())
        self.base = BaseMesh()
        self.layout = json.loads((HERE / "face_layout.json").read_text())
        story = self.cast["storybook"]
        self.story = (story["head"], story["hands"], story["feet"])
        self.canonical = self.cached("canonical.npy", lambda: fit(self.base, to_gltf(np.load(self.work / "bodies/neutral.npy")), UAL, *self.story)[0])
        tri_v, tri_t, uv = self.base.obj
        self.tri_v, self.tri_t, self.uv = tri_v, tri_t, uv
        self.body = self.base.faces_in("body")
        cn = welded_normals(self.canonical, tri_v[self.body])
        self.canon_normals = cn
        self.canvas_attrs = self.cached("canvas-attrs.npz", self.rasterise_layout)

    def cached(self, name, make):
        path = self.work / name
        if path.exists():
            data = np.load(path)
            return dict(data) if path.suffix == ".npz" else data
        value = make()
        if isinstance(value, dict):
            np.savez(path, **value)
        else:
            np.save(path, value)
        return value

    def rasterise_layout(self):
        corner_uv = self.uv[self.tri_t[self.body]]
        attrs = np.concatenate([self.canonical[self.tri_v[self.body]], self.canon_normals[self.tri_v[self.body]]], -1)
        out, covered = rasterise(SIZES["body"], corner_uv, attrs)
        return {"attrs": out, "covered": covered}

    def frame_rig(self, name):
        spec = self.cast["frames"][name]
        if spec is None:
            return UAL
        path = self.work / f"frame-{name}.glb"
        if not path.exists():
            frames.main(UAL, HERE / spec, path)
        return path

    def canvas(self, seed):
        return Canvas(SIZES["body"], self.canvas_attrs["attrs"], self.canvas_attrs["covered"], seed)

    def build(self, ch, out=OUT):
        name = ch["name"]
        costume = self.cast["costumes"][ch["costume"]]
        rig = self.frame_rig(ch["frame"])
        positions = to_gltf(np.load(self.work / f"bodies/{ch['recipe']}.npy"))
        final, joints, weights, dense, index, target, order, facts = fit(self.base, positions, rig, *self.story, girths=ch.get("girth"))
        tri_v, tri_t, uv = self.tri_v, self.tri_t, self.uv
        canon_c = self.canonical[tri_v].mean(1)
        # Fill the eye sockets up to their lid rims, so the painted eyes sit on a near-flat face.
        sockets = np.zeros(len(tri_v), bool)
        sockets[self.layout["sockets"]] = True
        final = inflate(final, tri_v, sockets, ~sockets & self.body, np.arange(len(final)), SOCKET_FILL_ITERATIONS)
        normals = welded_normals(final, tri_v[self.body])
        face = np.zeros(len(tri_v), bool)
        face[self.layout["triangles"]] = True
        payload, slots, textures = {}, [], {}
        work = self.work / name
        work.mkdir(parents=True, exist_ok=True)

        # Outer shells: the body's own faces, pushed out along its normals.
        outer = np.zeros(len(tri_v), bool)
        offset_tri = np.zeros(len(tri_v))
        for item in costume.get("outer", []):
            m = self.body & ~face & in_region(item["region"], canon_c, **self.opts(item))
            outer |= m
            offset_tri[m] = np.maximum(offset_tri[m], item["offset"])
        vert_offset = np.zeros(len(final))
        for k in range(3):
            np.maximum.at(vert_offset, tri_v[outer][:, k], offset_tri[outer])
        shell = final + normals * vert_offset[:, None]

        # Skirt: the skirt helper between its waist and hem, flared below the hips.
        skirt_spec = costume.get("skirt")
        skirt_tris = None
        under_skirt = np.zeros(len(tri_v), bool)
        if skirt_spec:
            top, hem = skirt_spec["top"], skirt_spec["hem"]
            sk = self.base.faces_in("helper-skirt") & (canon_c[:, 1] < top + 0.02) & (canon_c[:, 1] > hem)
            axis_x, axis_z = 0.0, float(self.canonical[self.base.groups["helper-skirt"], 2].mean())
            canon_uv = cylinder_uv(self.canonical, axis_x, axis_z, top, hem)
            if "open" in skirt_spec:
                lo, hi = skirt_spec["open"]
                tri_u = canon_uv[tri_v][:, :, 0].mean(1)
                sk &= ~((tri_u > lo) & (tri_u < hi) & (canon_c[:, 1] < HIP_Y - 0.06))
            ids = np.unique(tri_v[sk])
            depth = np.clip((top - self.canonical[ids, 1]) / (top - hem), 0, 1)
            centre = np.array([0.0, 0.0, float(final[ids, 2].mean())])
            radial = final[ids] - centre
            radial[:, 1] = 0
            radial /= np.maximum(np.linalg.norm(radial, axis=1, keepdims=True), 1e-6)
            skirt_pos = final.copy()
            # Hug the waist, then stand off and flare towards the hem.
            skirt_pos[ids] += radial * (0.006 + skirt_spec["offset"] * depth[:, None] ** 0.6 + skirt_spec["flare"] * depth[:, None] ** 1.3)
            skirt_tris = sk
            if "open" not in skirt_spec:
                under_skirt = self.body & in_region("legs", canon_c, top=HIP_Y - 0.08, bottom=hem + 0.06)

        # Face fans: one new vertex per eye or mouth opening.
        extra = []
        for loop in self.layout["eyeLoops"]:
            loop = np.array(loop)
            centre = final[loop].mean(0) + np.array([0, 0, FAN_FORWARD])
            extra.append((loop, centre))
        n0 = len(final)
        all_pos = np.vstack([final] + [c for _, c in extra]) if extra else final
        all_norm = np.vstack([normals] + [normals[l].mean(0) for l, _ in extra]) if extra else normals
        all_joints = np.vstack([joints] + [joints[l[0]] for l, _ in extra]) if extra else joints
        all_weights = np.vstack([weights] + [weights[l[0]] for l, _ in extra]) if extra else weights
        face_uv = {int(k): v for k, v in self.layout["uv"].items()}
        fans = []
        for k, (loop, _) in enumerate(extra):
            face_uv[n0 + k] = list(np.mean([face_uv[int(v)] for v in loop], 0))
            fans += [[loop[i], loop[(i + 1) % len(loop)], n0 + k] for i in range(len(loop))]
        fans = np.array(fans, int)
        if len(fans):
            a, b, c = all_pos[fans[:, 0]], all_pos[fans[:, 1]], all_pos[fans[:, 2]]
            flip = np.cross(b - a, c - a)[:, 2] < 0
            fans[flip] = fans[flip][:, [1, 0, 2]]

        # Parts, in slot order: the face stays slot 1 (CharacterStyleTest.FaceSlot).
        body_tris = self.body & ~face & ~outer & ~under_skirt
        part(payload, "body", all_pos, all_norm, all_joints, all_weights, tri_v[body_tris], uv[tri_t[body_tris]])
        slots.append("body")
        face_tri_v = np.vstack([tri_v[face], fans]) if len(fans) else tri_v[face]
        face_corner = flip_v(np.array([[face_uv[int(v)] for v in t] for t in face_tri_v]))
        part(payload, "face", all_pos, all_norm, all_joints, all_weights, face_tri_v, face_corner)
        slots.append("face")
        if outer.any():
            part(payload, "outer", shell, normals, joints, weights, tri_v[outer], uv[tri_t[outer]])
            slots.append("outer")
        if skirt_tris is not None and skirt_tris.any():
            corner = unwrap_seam(canon_uv[tri_v[skirt_tris]])
            snormals = welded_normals(skirt_pos, tri_v[skirt_tris])
            part(payload, "skirt", skirt_pos, snormals, joints, weights, tri_v[skirt_tris], flip_v(corner))
            slots.append("skirt")
        head_items = self.head_parts(ch, final, joints, weights, dense, index, order, target)
        for slot, (pos, nrm, jn, wt, tris, corner) in head_items.items():
            part(payload, slot, pos, nrm, jn, wt, tris, flip_v(corner))
            slots.append(slot)

        # Textures.
        face_img, skin = self.face_texture(ch)
        textures["face"] = face_img
        textures["body"] = self.paint_layer(costume.get("inner", []), skin, seed=zlib.crc32(ch["costume"].encode()) % 9973, base_skin=True, scarf=costume.get("scarf"),
                                            scalp=ch.get("hair", {}).get("colour") if ch.get("hair", {}).get("style", "none") != "none" else None)
        if "outer" in slots:
            textures["outer"] = self.paint_layer(costume["outer"], skin, seed=zlib.crc32(ch["costume"].encode()) % 7919 + 1, base_skin=False)
        if "skirt" in slots:
            textures["skirt"] = self.paint_skirt(skirt_spec, seed=zlib.crc32(ch["costume"].encode()) % 6151)
        for slot in head_items:
            textures[slot] = self.paint_band(ch["hat"]["colour"] if slot == "hat" else ch["hair"]["colour"], SIZES[slot], seed=len(slot) + zlib.crc32(name.encode()) % 97,
                                             strands=slot == "hair")
        slot_list = []
        for slot in slots:
            # Slot textures are embedded in the GLB; only the face swaps need loose content files.
            path = work / f"{name}-{slot}.png"
            textures[slot].save(path)
            slot_list.append({"name": slot, "texture": str(path), "roughness": 0.85 if slot != "face" else 0.7,
                              **({"wrap": "repeat"} if slot in REPEAT_SLOTS else {}), **({"doubleSided": True} if slot in DOUBLE_SIDED else {})})
        np.savez(work / "payload.npz", **payload)
        (work / "slots.json").write_text(json.dumps(slot_list, indent=1))
        subprocess.run([sys.executable, "-I", str(HERE.parent / "assemble_character.py"), str(work / "payload.npz"), str(work / "slots.json"),
                        str(out / f"{name}.glb"), "--rig", str(rig)], check=True)
        subprocess.run([sys.executable, "-I", str(HERE.parent / "assemble_character.py"), str(work / "payload.npz"), str(work / "slots.json"),
                        str(work / f"{name}-clips.glb"), "--rig", str(rig), "--with-clips"], check=True, capture_output=True)
        if out == OUT:
            subprocess.run([sys.executable, "-I", str(HERE.parent / "make_clip_pack.py"), str(rig), str(OUT / f"ual-clips-{name}.glb"), f"cast-{name}"], check=True)
        faces = self.expression_textures(ch)
        entry = {"name": name, "mesh": f"characters/cast/{name}.glb", "pack": f"characters/cast/ual-clips-{name}.glb",
                 "faces": faces, "scale": ch["scale"], "frame": ch["frame"], "home": ch.get("home", ""),
                 "posture": ch.get("posture", {}), "slots": slots}
        facts.update({"name": name, "triangles": {s: int(len(payload[f"{s}_indices"]) // 3) for s in slots},
                      "skin": "#%02x%02x%02x" % tuple(int(v) for v in skin)})
        (work / "facts.json").write_text(json.dumps(facts, indent=1))
        print("built", name, facts["triangles"], "fitted", facts["fittedHeight"], "m", flush=True)
        return entry

    @staticmethod
    def opts(item):
        return {k: item[k] for k in ("top", "bottom", "to", "front", "halfWidth") if k in item}

    def head_parts(self, ch, final, joints, weights, dense, index, order, target):
        """Hair from the hair helper, and a hat: a cap or scarf from the same helper, or a brimmed hat."""
        out = {}
        hair = ch.get("hair", {})
        hat = ch.get("hat")
        tri_v = self.tri_v
        canon_c = self.canonical[tri_v].mean(1)
        helper = self.base.faces_in("helper-hair")
        head_ids = self.base.groups["helper-hair"]
        top_y = float(self.canonical[head_ids, 1].max())
        axis_x = 0.0
        axis_z = float(self.canonical[head_ids, 2].mean())
        bottom = {"short": 1.62, "bun": 1.62, "long": 1.32}.get(hair.get("style", "none"))
        head_index = order.index("Head")
        # The hair helper hangs over the forehead to the brows: keep the face (and its hairline) clear.
        face_box = self.layout["box"]
        nose = self.canonical[[int(k) for k in self.layout["uv"]]]
        nose_tip = nose[np.argmax(nose[:, 2])]
        over_face = (np.abs(canon_c[:, 0] - nose_tip[0]) < face_box["halfWidth"] * 0.95) & (canon_c[:, 2] > nose_tip[2] - 0.075) \
            & (canon_c[:, 1] < nose_tip[1] + face_box["high"] - 0.01)
        helper = helper & ~over_face
        if bottom is not None:
            sel = helper & (canon_c[:, 1] > bottom)
            pos = final.copy()
            nrm = welded_normals(final, tri_v[sel])
            corner = unwrap_seam(cylinder_uv(self.canonical, axis_x, axis_z, top_y, bottom)[tri_v[sel]])
            if hair["style"] == "bun":
                bun = self.sphere(final, head_ids, 0.065, back=True)
                pos, nrm, jn, wt, tris, corner = self.merge((pos, nrm, joints, weights, tri_v[sel], corner), bun, head_index)
                out["hair"] = (pos, nrm, jn, wt, tris, corner)
            else:
                out["hair"] = (pos, nrm, joints, weights, tri_v[sel], corner)
        if hat:
            if hat["kind"] in ("cap", "scarf"):
                low = 1.60 if hat["kind"] == "cap" else 1.45
                sel = helper & (canon_c[:, 1] > low)
                nrm = welded_normals(final, tri_v[sel])
                pos = final + nrm * (0.014 if hat["kind"] == "cap" else 0.01)
                corner = unwrap_seam(cylinder_uv(self.canonical, axis_x, axis_z, top_y, low)[tri_v[sel]])
                out["hat"] = (pos, nrm, joints, weights, tri_v[sel], corner)
            elif hat["kind"] == "brim":
                out["hat"] = self.brim_hat(final, head_ids, head_index, joints.shape[1])
        return out

    def sphere(self, final, head_ids, radius, back):
        """A hair bun at the back of the head: (positions, normals, triangles, uv)."""
        head = final[head_ids]
        centre = np.array([head[:, 0].mean(), head[:, 1].max() - 0.07, head[:, 2].min() - radius * 0.35])
        rings, segs = 6, 12
        pts, uvs = [], []
        for i in range(rings + 1):
            th = math.pi * i / rings
            for j in range(segs + 1):
                ph = 2 * math.pi * j / segs
                d = np.array([math.sin(th) * math.cos(ph), math.cos(th), math.sin(th) * math.sin(ph)])
                pts.append(centre + d * radius * np.array([1, 0.85, 1]))
                uvs.append([j / segs, i / rings])
        tris = []
        for i in range(rings):
            for j in range(segs):
                a, b = i * (segs + 1) + j, (i + 1) * (segs + 1) + j
                tris += [[a, b, a + 1], [a + 1, b, b + 1]]
        pts = np.array(pts)
        return pts, (pts - centre) / radius, np.array(tris), np.array(uvs)

    def merge(self, part_a, extra, head_index):
        pos, nrm, jn, wt, tris, corner = part_a
        epos, enrm, etris, euv = extra
        n = len(pos)
        ej = np.zeros((len(epos), 4), jn.dtype)
        ej[:, 0] = head_index
        ew = np.zeros((len(epos), 4))
        ew[:, 0] = 1
        return (np.vstack([pos, epos]), np.vstack([nrm, enrm]), np.vstack([jn, ej]), np.vstack([wt, ew]),
                np.vstack([tris, etris + n]), np.concatenate([corner, euv[etris]]))

    def brim_hat(self, final, head_ids, head_index, width):
        """A wide-brimmed felt hat on the head joint: crown cylinder plus brim, both sides drawn."""
        head = final[head_ids]
        cx, cz = head[:, 0].mean(), head[:, 2].mean() - 0.005
        top = head[:, 1].max()
        r = (head[:, 0].max() - head[:, 0].min()) / 2 * 1.08
        base_y = top - 0.075
        segs = 20
        pts, uvs, tris = [], [], []

        def ring(radius, y, v):
            start = len(pts)
            for j in range(segs + 1):
                a = 2 * math.pi * j / segs
                pts.append([cx + math.sin(a) * radius, y, cz + math.cos(a) * radius])
                uvs.append([j / segs, v])
            return start

        rings = [ring(r * 2.1, base_y - 0.005, 1.0), ring(r * 1.02, base_y, 0.8), ring(r * 0.98, top + 0.06, 0.25), ring(r * 0.6, top + 0.075, 0.1)]
        for a, b in zip(rings, rings[1:]):
            for j in range(segs):
                tris += [[a + j, a + j + 1, b + j], [a + j + 1, b + j + 1, b + j]]
        centre = len(pts)
        pts.append([cx, top + 0.08, cz])
        uvs.append([0.5, 0.0])
        tris += [[rings[-1] + j, rings[-1] + j + 1, centre] for j in range(segs)]
        # Underside of the brim, so it reads from below.
        under = [ring(r * 2.1, base_y - 0.008, 1.0), ring(r * 1.0, base_y - 0.004, 0.9)]
        tris += [[under[0] + j + 1, under[0] + j, under[1] + j] for j in range(segs)] + [[under[0] + j + 1, under[1] + j, under[1] + j + 1] for j in range(segs)]
        pts, tris = np.array(pts), np.array(tris)
        nrm = np.zeros_like(pts)
        face_n = np.cross(pts[tris[:, 1]] - pts[tris[:, 0]], pts[tris[:, 2]] - pts[tris[:, 0]])
        for k in range(3):
            np.add.at(nrm, tris[:, k], face_n)
        nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
        jn = np.zeros((len(pts), width), np.int64)
        jn[:, 0] = head_index
        wt = np.zeros((len(pts), width))
        wt[:, 0] = 1
        return pts, nrm, jn, wt, tris, np.array(uvs)[tris]

    def face_cell(self, sheet, cell):
        img = Image.open(HERE / "faces" / sheet).convert("RGB")
        size = img.width // 2
        x, y = cell % 2, cell // 2
        return img.crop((x * size, y * size, (x + 1) * size, (y + 1) * size))

    def face_texture(self, ch):
        sheet, cell = ch["face"].split(":")
        img = self.face_cell(sheet, int(cell))
        a = np.asarray(img, np.float64)
        h = a.shape[0]
        # Skin: the cheeks, either side of the nose, below the eyes.
        patch = np.concatenate([a[int(h * 0.48):int(h * 0.58), int(h * 0.12):int(h * 0.25)].reshape(-1, 3),
                                a[int(h * 0.48):int(h * 0.58), int(h * 0.75):int(h * 0.88)].reshape(-1, 3)])
        skin = np.median(patch, 0)
        # Cheeks carry the painted blush: pull the body's skin a little towards grey.
        skin = skin + (skin.mean() - skin) * 0.25
        return img.convert("RGBA").resize((SIZES["face"],) * 2, Image.BOX), skin

    def expression_textures(self, ch):
        """Face textures per expression in content; characters without a set use their neutral face for all."""
        name = ch["name"]
        out = {}
        if "expressions" in ch:
            for cell, expr in enumerate(("neutral", "blink", "talk", "smile")):
                path = OUT / f"{name}-face-{expr}.png"
                self.face_cell(ch["expressions"], cell).convert("RGBA").resize((SIZES["face"],) * 2, Image.BOX).save(path)
                out[expr] = f"characters/cast/{path.name}"
        else:
            for expr in ("neutral", "blink", "talk", "smile"):
                out[expr] = f"characters/cast/{name}-face.png"
        return out

    def paint_layer(self, items, skin, seed, base_skin, scarf=None, scalp=None):
        c = self.canvas(seed)
        if base_skin:
            skin_hex = "#%02x%02x%02x" % tuple(int(v) for v in skin)
            c.fill(c.covered, skin_hex, value_jitter=0.04, hue_jitter=0.015)
        else:
            c.fill(c.covered, items[0]["colour"] if items else "#808080")
        for item in items:
            mask = c.region(item["region"], **self.opts(item))
            c.fill(mask, item["colour"])
            if "folds" in item:
                c.folds(mask, item["folds"])
            if "edge" in item:
                c.edges(mask, item["edge"])
            if "buttons" in item:
                front = mask & (np.abs(c.pos[..., 0]) < 0.02) & (c.pos[..., 2] > 0)
                c.strokes(front, item["buttons"], 6, 0.008, 0.012, alpha=(200, 240))
        if scalp:
            # Under the hair shell the scalp takes the hair colour, so its cut edges read as hair.
            box = self.layout["box"]
            nose = self.canonical[[int(k) for k in self.layout["uv"]]]
            tip = nose[np.argmax(nose[:, 2])]
            head = c.region("head")
            hairline = tip[1] + box["high"] - 0.02
            behind = (c.pos[..., 2] < tip[2] - 0.09) & (np.abs(c.pos[..., 0] - tip[0]) > box["halfWidth"] * 0.9)
            c.fill(head & ((c.pos[..., 1] > hairline) | (behind & (c.pos[..., 1] > tip[1] - 0.02))), scalp, value_jitter=0.08, hue_jitter=0.0)
        if scarf:
            neck = c.region("neck") | c.region("torso", bottom=1.33, front=0.05)
            c.fill(neck & ~c.region("head"), scarf)
            c.folds(neck, "#6f2c24")
        c.form_shading()
        return c.image()

    def paint_skirt(self, spec, seed):
        size = SIZES["skirt"]
        c = band_canvas(size, seed)
        c.fill(c.covered, spec["colour"], stroke_len=0.15)
        u = np.linspace(0, 1, size)[None, :].repeat(size, 0)
        v = np.linspace(0, 1, size)[:, None].repeat(size, 1)
        if "folds" in spec:
            c.strokes(c.covered & (v > 0.15), spec["folds"], 60, 0.35, 0.012)
        apron = spec.get("apron")
        if apron:
            depth = (spec["top"] - apron["hem"]) / (spec["top"] - spec["hem"])
            m = (u > apron["from"]) & (u < apron["to"]) & (v < depth)
            c.fill(m, apron["colour"], stroke_len=0.12)
            c.edges(m, apron["edge"], 1)
        # Hem and waistband: a darker painted edge.
        c.rgb[(v > 0.96) | (v < 0.03)] *= 0.72
        return c.image(0)

    def paint_band(self, colour, size, seed, strands):
        c = band_canvas(size, seed)
        c.fill(c.covered, colour, stroke_len=0.3 if strands else 0.15, value_jitter=0.1, hue_jitter=0.005)
        if strands:
            dark = "#%02x%02x%02x" % tuple(int(v * 0.65) for v in hexrgb(colour))
            c.strokes(c.covered, dark, 40, 0.5, 0.02)
        return c.image(0)


def main(work, *names):
    builder = Builder(work)
    OUT.mkdir(parents=True, exist_ok=True)
    manifest_path = OUT / "cast.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {"characters": []}
    entries = {e["name"]: e for e in manifest["characters"]}
    for ch in builder.cast["characters"]:
        if names and ch["name"] not in names:
            continue
        if "expressions" not in ch:
            sheet, cell = ch["face"].split(":")
            builder.face_texture(ch)[0].save(OUT / f"{ch['name']}-face.png")
        entries[ch["name"]] = builder.build(ch)
    # Studies (one costume on other bodies) build into the work dir, not content.
    study_dir = Path(work) / "studies"
    study_dir.mkdir(exist_ok=True)
    for st in builder.cast.get("studies", []):
        if names and st["name"] not in names:
            continue
        builder.build(st, study_dir)
    order = [c["name"] for c in builder.cast["characters"]]
    manifest = {"note": "Character framework check (#10028): built by art/characters/mpfb/build_cast.py; a test aid.",
                "characters": [entries[n] for n in order if n in entries]}
    manifest_path.write_text(json.dumps(manifest, indent=1) + "\n")


if __name__ == "__main__":
    main(*sys.argv[1:])
