"""Export evaluated hair curves from a .blend to .furstrands + JSON stats.

Run: blender -b --disable-autoexec FILE.blend -P export_strands.py -- OUT_DIR

.furstrands layout (little endian):
  char[4] 'FURS', u32 version=1, u32 curveCount, u32 pointsPerCurve
  per point: float3 position (Blender world space, Z-up, metres), float radius
Curves with a different point count are resampled is NOT done; they are rejected.
"""
import bpy, json, os, struct, sys
import numpy as np

out_dir = sys.argv[sys.argv.index("--") + 1]
os.makedirs(out_dir, exist_ok=True)
dg = bpy.context.evaluated_depsgraph_get()
stats = {"blend": bpy.data.filepath, "objects": {}}


def bounds(points):
    return [points.min(axis=0).round(4).tolist(), points.max(axis=0).round(4).tolist()]


for obj in bpy.data.objects:
    if obj.type == "MESH" and obj.name.startswith("Full"):
        mw = np.array(obj.matrix_world)
        co = np.empty(len(obj.data.vertices) * 3, np.float32)
        obj.data.vertices.foreach_get("co", co)
        co = co.reshape(-1, 3) @ mw[:3, :3].T + mw[:3, 3]
        stats["objects"][obj.name] = {"kind": "emitter", "bounds": bounds(co)}
    if obj.type == "ARMATURE":
        stats["objects"][obj.name] = {"kind": "armature", "location": list(obj.location)}
    if obj.type != "CURVES" or obj.hide_render:
        continue
    ev = obj.evaluated_get(dg)
    data = ev.data
    n = len(data.curves)
    if n == 0:
        continue
    offsets = np.empty(n + 1, np.int32)
    data.curve_offset_data.foreach_get("value", offsets)
    sizes = np.diff(offsets)
    if not np.all(sizes == sizes[0]):
        raise SystemExit(f"{obj.name}: variable points per curve {sizes.min()}..{sizes.max()}")
    ppc = int(sizes[0])
    pos = np.empty(len(data.points) * 3, np.float32)
    data.position_data.foreach_get("vector", pos)
    pos = pos.reshape(-1, 3)
    mw = np.array(ev.matrix_world, np.float32)
    pos = pos @ mw[:3, :3].T + mw[:3, 3]
    rad_attr = data.attributes.get("radius")
    rad = np.full(len(pos), 0.005, np.float32)
    if rad_attr is not None:
        rad_attr.data.foreach_get("value", rad)
    curves = pos.reshape(n, ppc, 3)
    seg = np.linalg.norm(np.diff(curves, axis=1), axis=2).sum(axis=1)
    rad_c = rad.reshape(n, ppc)
    mats = [m.name for m in data.materials if m]
    with open(os.path.join(out_dir, f"{obj.name}.furstrands"), "wb") as f:
        f.write(b"FURS" + struct.pack("<III", 1, n, ppc))
        f.write(np.concatenate([pos, rad[:, None]], axis=1).astype("<f4").tobytes())
    stats["objects"][obj.name] = {
        "kind": "strands", "parent": obj.parent.name if obj.parent else None,
        "curves": n, "pointsPerCurve": ppc, "materials": mats,
        "attributes": sorted(a.name for a in data.attributes if not a.name.startswith(".")),
        "lengthP5_P50_P95": np.percentile(seg, [5, 50, 95]).round(4).tolist(),
        "radiusRoot_Tip_median": [float(np.median(rad_c[:, 0])), float(np.median(rad_c[:, -1]))],
        "bounds": bounds(pos),
    }

# Skin binding: each strand root copies the top-4 weights of the nearest evaluated (posed) body vertex.
# Output <name>.furskin: u32 curveCount, then per curve u16[4] bone-table index + f32[4] weights; bone table in stats.
from mathutils import kdtree, Vector
body_names = [n for n in ("c0201e0000_top_0", "c0201e0279_dwn_0.002") if n in bpy.data.objects]
body_pos, body_w = [], []
for name in body_names:
    obj = bpy.data.objects[name]
    ev = obj.evaluated_get(dg)
    mesh = ev.to_mesh()
    groups = [g.name for g in obj.vertex_groups]
    for v in mesh.vertices:
        body_pos.append(ev.matrix_world @ v.co)
        body_w.append(sorted(((groups[g.group], g.weight) for g in v.groups if g.weight > 0), key=lambda x: -x[1])[:4])
    ev.to_mesh_clear()
tree = kdtree.KDTree(len(body_pos))
for i, p in enumerate(body_pos):
    tree.insert(p, i)
tree.balance()
bone_table = {}
for name, entry in stats["objects"].items():
    if entry.get("kind") != "strands":
        continue
    raw = np.fromfile(os.path.join(out_dir, f"{name}.furstrands"), "<f4", offset=16).reshape(entry["curves"], entry["pointsPerCurve"], 4)
    idx = np.zeros((entry["curves"], 4), "<u2")
    wts = np.zeros((entry["curves"], 4), "<f4")
    dist = np.empty(entry["curves"], np.float32)
    for c in range(entry["curves"]):
        _, vi, d = tree.find(Vector(raw[c, 0, :3].tolist()))
        dist[c] = d
        ws = body_w[vi]
        total = sum(w for _, w in ws) or 1.0
        for k, (bone, w) in enumerate(ws):
            idx[c, k] = bone_table.setdefault(bone, len(bone_table))
            wts[c, k] = w / total
    with open(os.path.join(out_dir, f"{name}.furskin"), "wb") as f:
        f.write(struct.pack("<I", entry["curves"]))
        for c in range(entry["curves"]):
            f.write(idx[c].tobytes() + wts[c].tobytes())
    entry["rootToBodyDistanceP50_P95_Max"] = np.percentile(dist, [50, 95, 100]).round(4).tolist()

arm = bpy.data.objects.get("Miqote Female MekRig")
stats["boneTable"] = [{"name": b, "poseMatrixWorld": [list(r) for r in (arm.matrix_world @ arm.pose.bones[b].matrix)] if arm and b in arm.pose.bones else None}
                      for b, _ in sorted(bone_table.items(), key=lambda kv: kv[1])]
stats["bodyMeshes"] = body_names

with open(os.path.join(out_dir, "strands-stats.json"), "w") as f:
    json.dump(stats, f, indent=1)
