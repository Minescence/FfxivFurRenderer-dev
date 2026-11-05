"""Grow the TheSleeveExperiment fur recipe on Hikari's DT Big Fluffy Coat and export strands in game model space.

Run (inside the reference .blend so its node groups and material are available):
  blender -b --disable-autoexec TheSleeveExperiment.blend -P grow_coat_fur.py -- COAT.mdl COAT-mdl.json OUT_DIR [AMOUNT] [LAYOUT.json]

AMOUNT scales the child-strand density (reference: 1e6/m^2 at render, x0.05 in viewport). Default 0.05.
LAYOUT.json is the `furlab dump-layout` output; default is coat-layout.json next to COAT-mdl.json.
Coat meshes = materials named e0239/a0122/glv/ril/rir; body-skin meshes = material b0001 (occluders only).
After growing, strands whose root has fewer than 1 of 16 hemisphere rays escaping (4 cm, vs coat + body BVH) are culled.

Outputs (OUT_DIR):
  coat.furstrands  'FURS', u32 version=1, u32 curves, u32 pointsPerCurve; per point float3 position (game model space, Y-up, metres) + float radius
  coat.furskin     u32 curves; per curve u16[8] bone index (into the MDL bone-name list) + f32[8] weights, root skin interpolated barycentrically
  coat.furmesh     u32 vertices, u32 triangles; per vertex float3 position + float3 smooth normal (game model space); per vertex
                   u16[8] bone index + f32[8] weights; triangles x u32[3] indices. The coat faces the fur grows on (undercoat).
  coat-fur.json    counts, bone names, recipe values
"""
import bpy, bmesh, json, os, struct, sys
import numpy as np
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

args = sys.argv[sys.argv.index("--") + 1:]
mdl_path, meta_path, out_dir = args[:3]
amount = float(args[3]) if len(args) > 3 else 0.05
layout_path = args[4] if len(args) > 4 else os.path.join(os.path.dirname(meta_path), "coat-layout.json")
os.makedirs(out_dir, exist_ok=True)
mdl = np.fromfile(mdl_path, np.uint8)
meta = json.load(open(meta_path, encoding="utf-8"))
lod = meta["lods"][0]
bone_names = meta["boneNames"]
layouts = json.load(open(layout_path, encoding="utf-8"))["layouts"]
mat_paths = meta["materialPaths"]
COAT_MATS = {i for i, m in enumerate(mat_paths) if any(t in m for t in ("e0239", "a0122", "glv", "ril", "rir")) and "b0001" not in m}
BODY_MATS = {i for i, m in enumerate(mat_paths) if "b0001" in m}
if not COAT_MATS:
    raise SystemExit(f"no coat material in {mat_paths}")
N_RAYS, MIN_ESCAPE, RAY_DIST = 16, 1, 0.04


def layout_for(mesh_index):
    return layouts[mesh_index]


def read_elements(mesh, layout):
    n = mesh["vertexCount"]
    out = {}
    for stream in layout["streams"]:
        base, stride = stream["absoluteBufferOffset"], stream["stride"]
        block = mdl[base:base + n * stride].reshape(n, stride)
        for e in stream["elements"]:
            raw = np.ascontiguousarray(block[:, e["byteOffset"]:e["byteOffset"] + e["byteWidth"]])
            out[(e["usageName"], e["usageIndex"])] = (e["dataTypeName"], raw)
    return out


def skin_bytes(dtype_name, raw):
    if dtype_name in ("UShort4",):  # DT 8-influence packing: 8 x u8
        return raw.reshape(-1, 8)
    if dtype_name in ("UByte4", "NByte4"):
        return raw.reshape(-1, 4)
    raise SystemExit(f"unsupported skin element type {dtype_name}")


positions, normals, faces, weights, joints, is_coat = [], [], [], [], [], []
offset = 0
for mesh in meta["meshes"]:
    if mesh["lod"] != 0 or mesh["materialIndex"] not in COAT_MATS | BODY_MATS:
        continue
    el = read_elements(mesh, layout_for(mesh["index"]))
    pos = el[("Position", 0)][1].view("<f4").reshape(-1, 3)
    nrm = el[("Normal", 0)][1].view("<f4").reshape(-1, 3)
    w = skin_bytes(*el[("BlendWeights", 0)]).astype(np.float32)
    j = skin_bytes(*el[("BlendIndices", 0)]).astype(np.int64)
    sums = w.sum(axis=1)
    if not np.allclose(sums, 255, atol=2):
        raise SystemExit(f"mesh {mesh['index']}: weight bytes sum to {sums.min()}..{sums.max()}, not 255; skin packing assumption is wrong")
    table = np.array(meta["boneTables"][mesh["boneTableIndex"]]["boneIndices"])
    j = np.where(w > 0, table[np.minimum(j, len(table) - 1)], 0)  # local -> global bone index
    start = lod["indexBufferOffset"] + mesh["startIndex"] * 2
    idx = mdl[start:start + mesh["indexCount"] * 2].view("<u2").astype(np.int64).reshape(-1, 3)
    positions.append(pos); normals.append(nrm); weights.append(w / 255); joints.append(j); faces.append(idx + offset)
    is_coat.append(np.full(len(idx), mesh["materialIndex"] in COAT_MATS))
    offset += len(pos)
pos_game = np.concatenate(positions)
normals = np.concatenate(normals)
is_coat = np.concatenate(is_coat)
weights = np.concatenate(weights)
joints = np.concatenate(joints)
faces_all = np.concatenate(faces)
faces = faces_all[is_coat]  # fur grows on coat faces only; body faces are occluders
to_blender = lambda p: np.stack([p[:, 0], -p[:, 2], p[:, 1]], axis=1)   # game (x,y,z) -> Blender (x,-z,y)
to_game = lambda p: np.stack([p[:, 0], p[:, 2], -p[:, 1]], axis=1)

me = bpy.data.meshes.new("HikariCoat")
me.from_pydata(to_blender(pos_game).tolist(), [], faces.tolist())
me.update()
# Hair grows along the surface normal. from_pydata leaves the mesh flat-shaded (faceted strand directions) and the game mesh
# splits vertices at UV/material seams (partings). Use the game's own vertex normals, averaged over vertices that share a
# position, as smooth custom normals.
_, weld = np.unique(np.round(pos_game * 1e4).astype(np.int64), axis=0, return_inverse=True)
weld = weld.reshape(-1)
smooth = np.zeros((weld.max() + 1, 3)); np.add.at(smooth, weld, normals)
smooth = smooth[weld]; smooth /= np.linalg.norm(smooth, axis=1, keepdims=True).clip(1e-8)
# Undercoat: the coat surface itself, drawn under the fur in the emitter's colour (the reference's Material.004).
used, tri = np.unique(faces, return_inverse=True)
with open(os.path.join(out_dir, "coat.furmesh"), "wb") as f:
    f.write(struct.pack("<II", len(used), len(faces)))
    f.write(np.concatenate([pos_game[used], smooth[used]], axis=1).astype("<f4").tobytes())
    rec = np.empty(len(used), np.dtype([("i", "<u2", 8), ("w", "<f4", 8)])); rec["i"] = joints[used]; rec["w"] = weights[used]
    f.write(rec.tobytes())
    f.write(tri.reshape(-1).astype("<u4").tobytes())
me.shade_smooth()
me.normals_split_custom_set_from_vertices(to_blender(smooth).tolist())
coat = bpy.data.objects.new("HikariCoat", me)
bpy.context.scene.collection.objects.link(coat)
area = sum(p.area for p in me.polygons)

# Clone the reference fur stack and retarget it to the coat.
src = bpy.data.objects["Curves.002"]
fur = src.copy()
fur.data = src.data.copy()
fur.name = "HikariCoatFur"
fur.parent = None
fur.matrix_world = coat.matrix_world
bpy.context.scene.collection.objects.link(fur)
fur.data.surface = coat
for m in fur.modifiers:
    for item in m.node_group.interface.items_tree:
        if getattr(item, "in_out", None) != "INPUT":
            continue
        x = getattr(m.properties.inputs, item.identifier)
        if not hasattr(x, "value"):  # panel header sharing the socket's name
            continue
        if item.name == "Surface":
            x.value = coat
        if item.name == "Viewport Amount" and m.node_group.name.startswith("Interpolate"):
            x.value = amount
# Hide the original scene so only the coat fur is evaluated/saved for preview.
for o in bpy.data.objects:
    if o not in (coat, fur) and o.type in ("CURVES", "MESH"):
        o.hide_viewport = o.hide_render = True

dg = bpy.context.evaluated_depsgraph_get()
ev = fur.evaluated_get(dg).data
n = len(ev.curves)
offs = np.empty(n + 1, np.int32); ev.curve_offset_data.foreach_get("value", offs)
sizes = np.diff(offs)
ppc = int(np.bincount(sizes).argmax())
p = np.empty(len(ev.points) * 3, np.float32); ev.position_data.foreach_get("vector", p)
p = p.reshape(-1, 3)
r = np.empty(len(ev.points), np.float32); ev.attributes["radius"].data.foreach_get("value", r)
# The generator emits a few degenerate curves (1 point); keep only full-length ones.
keep_points = np.repeat(sizes == ppc, sizes)
dropped = int((sizes != ppc).sum())
p, r, n = p[keep_points], r[keep_points], int((sizes == ppc).sum())

# Root skin (barycentric on the nearest coat triangle) + interior culling in one pass over the roots.
bvh = BVHTree.FromPolygons(to_blender(pos_game).tolist(), faces.tolist())
occ = BVHTree.FromPolygons(to_blender(pos_game).tolist(), faces_all.tolist())  # coat + body
rng = np.random.default_rng(1)
k_ = np.arange(N_RAYS) + 0.5
cos_t = 1 - k_ / N_RAYS  # Fibonacci hemisphere, cos in (0,1]
sin_t = np.sqrt(1 - cos_t ** 2); phi = k_ * np.pi * (3 - 5 ** 0.5)
hemi = np.stack([sin_t * np.cos(phi), sin_t * np.sin(phi), cos_t], axis=1)  # z = along normal
roots = p.reshape(n, ppc, 3)[:, 0]
nrm_b = to_blender(normals)
skin_idx = np.zeros((n, 8), "<u2"); skin_w = np.zeros((n, 8), "<f4"); miss = 0.0
kept = np.zeros(n, bool)
root_n = np.zeros((n, 3))
for c in range(n):
    loc, _, fi, d = bvh.find_nearest(roots[c].tolist())
    miss = max(miss, d)
    tri = faces[fi]
    a, b, cc = (to_blender(pos_game[tri]))
    v0, v1, v2 = b - a, cc - a, np.array(loc) - a
    d00, d01, d11, d20, d21 = v0 @ v0, v0 @ v1, v1 @ v1, v2 @ v0, v2 @ v1
    den = d00 * d11 - d01 * d01 or 1.0
    bv = (d11 * d20 - d01 * d21) / den; bw = (d00 * d21 - d01 * d20) / den
    bary = (1 - bv - bw, bv, bw)
    nz = bary[0] * nrm_b[tri[0]] + bary[1] * nrm_b[tri[1]] + bary[2] * nrm_b[tri[2]]
    nz /= np.linalg.norm(nz) or 1.0
    t1 = np.cross(nz, rng.normal(size=3)); t1 /= np.linalg.norm(t1)
    t2 = np.cross(nz, t1)
    dirs = hemi[:, :1] * t1 + hemi[:, 1:2] * t2 + hemi[:, 2:] * nz
    org = (np.array(loc) + nz * 0.002).tolist()
    escaped = 0
    for dv in dirs.tolist():
        if occ.ray_cast(org, dv, RAY_DIST)[0] is None:
            escaped += 1
            if escaped >= MIN_ESCAPE:
                break
    if escaped < MIN_ESCAPE:
        continue
    kept[c] = True
    root_n[c] = nz
    acc = {}
    for k, vi in enumerate(tri):
        for jj, ww in zip(joints[vi], weights[vi]):
            if ww > 0:
                acc[int(jj)] = acc.get(int(jj), 0.0) + bary[k] * ww
    # Up to 8, like the game's DT skinning: top-4 drops 15-25% of the weight at the back collar and shifts roots ~2 cm.
    top = sorted(acc.items(), key=lambda kv: -kv[1])[:8]
    total = sum(w for _, w in top) or 1.0
    for k, (jj, ww) in enumerate(top):
        skin_idx[c, k] = jj; skin_w[c, k] = ww / total

grown = n
# Overhang: strands near an open mesh border (collar flaps, hems, cuffs) lean out across it, so slits between separate coat
# sheets are covered by fur like a real pelt's edge instead of showing what is underneath.
OVERHANG_DIST, OVERHANG_DEG = 0.045, 85.0
welded = weld[faces]
edges = np.sort(np.concatenate([welded[:, [0, 1]], welded[:, [1, 2]], welded[:, [2, 0]]]), axis=1)
uniq, cnt = np.unique(edges, axis=0, return_counts=True)
rep_pos = np.zeros((weld.max() + 1, 3)); rep_pos[weld] = to_blender(pos_game)
border = uniq[cnt == 1]
samples = (rep_pos[border[:, :1]] * (1 - np.linspace(0, 1, 6))[None, :, None] + rep_pos[border[:, 1:]] * np.linspace(0, 1, 6)[None, :, None]).reshape(-1, 3)
kd = KDTree(len(samples))
for i, q in enumerate(samples.tolist()):
    kd.insert(q, i)
kd.balance()
pts = p.reshape(n, ppc, 3)
leaned = 0
for c in np.flatnonzero(kept):
    q, _, d = kd.find(roots[c].tolist())
    if d >= OVERHANG_DIST:
        continue
    nz = root_n[c]
    out = np.array(q) - roots[c]; out -= nz * (out @ nz)
    if np.linalg.norm(out) < 1e-6:
        continue
    out /= np.linalg.norm(out)
    axis = np.cross(nz, out); theta = np.radians(OVERHANG_DEG) * (1 - d / OVERHANG_DIST)
    v = pts[c] - pts[c, 0]
    pts[c] = pts[c, 0] + v * np.cos(theta) + np.cross(axis, v) * np.sin(theta) + np.outer(v @ axis, axis) * (1 - np.cos(theta))
    leaned += 1
p = pts.reshape(-1, 3)
p = p.reshape(n, ppc, 3)[kept].reshape(-1, 3); r = r.reshape(n, ppc)[kept].reshape(-1)
skin_idx, skin_w = skin_idx[kept], skin_w[kept]
n = int(kept.sum())

pg = to_game(p)
with open(os.path.join(out_dir, "coat.furstrands"), "wb") as f:
    f.write(b"FURS" + struct.pack("<III", 1, n, ppc))
    f.write(np.concatenate([pg, r[:, None]], axis=1).astype("<f4").tobytes())
with open(os.path.join(out_dir, "coat.furskin"), "wb") as f:
    f.write(struct.pack("<I", n))
    f.write(b"".join(skin_idx[c].tobytes() + skin_w[c].tobytes() for c in range(n)))
seg = np.linalg.norm(np.diff(pg.reshape(n, ppc, 3), axis=1), axis=2).sum(axis=1)
json.dump({
    "source": mdl_path, "coatMeshes": [m["index"] for m in meta["meshes"] if m["lod"] == 0 and m["materialIndex"] in COAT_MATS],
    "bodyOccluderMeshes": [m["index"] for m in meta["meshes"] if m["lod"] == 0 and m["materialIndex"] in BODY_MATS],
    "overhang": {"distance": OVERHANG_DIST, "degrees": OVERHANG_DEG, "strands": leaned},
    "cull": {"rays": N_RAYS, "minEscape": MIN_ESCAPE, "maxDistance": RAY_DIST, "grown": grown, "kept": n, "culled": grown - n},
    "coatVertices": int(np.unique(faces).size), "coatTriangles": int(len(faces)), "coatSurfaceAreaM2": round(area, 4),
    "amount": amount, "curves": n, "droppedDegenerateCurves": dropped, "pointsPerCurve": ppc,
    "projectedCurvesAtRenderDensity": int(n / amount) if amount else None,
    "lengthP5_P50_P95": np.percentile(seg, [5, 50, 95]).round(4).tolist(),
    "maxRootDistanceToCoat": round(float(miss), 6),
    "boneNames": bone_names,
    "recipe": "TheSleeveExperiment Curves.002 stack: guides 30000/m2 len 0.05 4pts seed 246; radius 0.0003 shape 0.25; children 1e6/m2 seed 151; clump 0.3/0.4 offset 0.015 falloff 0.0025; trim random 0.025",
}, open(os.path.join(out_dir, "coat-fur.json"), "w"), indent=1)
print("DONE", n, "curves")
