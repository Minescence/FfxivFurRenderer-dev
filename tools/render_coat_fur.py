"""Look target: the TheSleeveExperiment fur stack (Curves.002) on the game coat at full render density, rendered in Cycles
with the reference scene's lights, world and AgX view (the coat in bind pose, coat material Material.004).

  blender -b --disable-autoexec -t 6 TheSleeveExperiment.blend -P render_coat_fur.py -- COAT.mdl COAT-mdl.json LAYOUT.json OUT_DIR SAMPLES VIEW [preview|facing]

VIEW is a key of VIEWS. preview = bare coat in Workbench (framing); facing = bare coat with emission dot(normal, to camera) in
Raw, for fitting colour against facing. Renders on the CPU so the game keeps the GPU (a GPU render froze the game); 48
samples take about 10 minutes per view. Coat faces turned more than ~110 degrees from the camera get no fur. Nothing is saved.
"""
import bpy, json, os, sys
import numpy as np
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
mdl_path, meta_path, layout_path, out_dir, samples, view = args[:6]
preview = "preview" in args[6:]
facing_map = "facing" in args[6:]  # bare coat, emission = dot(normal, to camera), Raw output
os.makedirs(out_dir, exist_ok=True)
mdl = np.fromfile(mdl_path, np.uint8)
meta = json.load(open(meta_path, encoding="utf-8"))
layouts = json.load(open(layout_path, encoding="utf-8"))["layouts"]
COAT_MATS = {i for i, m in enumerate(meta["materialPaths"]) if any(t in m for t in ("e0239", "a0122", "glv", "ril", "rir")) and "b0001" not in m}

positions, normals, faces, offset = [], [], [], 0
for mesh in meta["meshes"]:
    if mesh["lod"] != 0 or mesh["materialIndex"] not in COAT_MATS:
        continue
    n = mesh["vertexCount"]
    el = {}
    for stream in layouts[mesh["index"]]["streams"]:
        base, stride = stream["absoluteBufferOffset"], stream["stride"]
        block = mdl[base:base + n * stride].reshape(n, stride)
        for e in stream["elements"]:
            el[(e["usageName"], e["usageIndex"])] = np.ascontiguousarray(block[:, e["byteOffset"]:e["byteOffset"] + e["byteWidth"]])
    positions.append(el[("Position", 0)].view("<f4").reshape(-1, 3))
    normals.append(el[("Normal", 0)].view("<f4").reshape(-1, 3))
    start = meta["lods"][0]["indexBufferOffset"] + mesh["startIndex"] * 2
    faces.append(mdl[start:start + mesh["indexCount"] * 2].view("<u2").astype(np.int64).reshape(-1, 3) + offset)
    offset += n
pos = np.concatenate(positions); nrm = np.concatenate(normals); faces = np.concatenate(faces)
to_blender = lambda p: np.stack([p[:, 0], -p[:, 2], p[:, 1]], axis=1)

VIEWS = {  # camera location, target (Blender space: character faces -Y, Z up, metres)
    "back": ((0.0, 1.45, 1.25), (0.0, 0.0, 1.12)),
    "puff": ((0.75, 0.75, 1.40), (0.17, 0.0, 1.32)),
    "front": ((0.6, -1.5, 1.3), (0.0, 0.0, 1.1)),
    "front0": ((0.0, -2.6, 0.95), (0.0, 0.0, 0.85)),  # straight on, the whole coat
}
cam_loc, cam_target = (np.array(v) for v in VIEWS[view])
if not preview:
    tri = to_blender(pos)[faces]
    fn = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0]); fn /= np.linalg.norm(fn, axis=1, keepdims=True).clip(1e-12)
    to_cam = cam_loc - tri.mean(1); to_cam /= np.linalg.norm(to_cam, axis=1, keepdims=True)
    keep = (fn * to_cam).sum(1) > -0.35
    print("FACES kept", int(keep.sum()), "of", len(faces), flush=True)
    faces = faces[keep]
me = bpy.data.meshes.new("HikariCoat")
me.from_pydata(to_blender(pos).tolist(), [], faces.tolist())
me.update()
_, weld = np.unique(np.round(pos * 1e4).astype(np.int64), axis=0, return_inverse=True)
weld = weld.reshape(-1)
smooth = np.zeros((weld.max() + 1, 3)); np.add.at(smooth, weld, nrm)
smooth = smooth[weld]; smooth /= np.linalg.norm(smooth, axis=1, keepdims=True).clip(1e-8)
me.shade_smooth()
me.normals_split_custom_set_from_vertices(to_blender(smooth).tolist())
me.materials.append(bpy.data.materials["Material.004"])
coat = bpy.data.objects.new("HikariCoat", me)
bpy.context.scene.collection.objects.link(coat)

src = bpy.data.objects["Curves.002"]
fur = src.copy(); fur.data = src.data.copy(); fur.name = "HikariCoatFur"
fur.parent = None; fur.matrix_world = coat.matrix_world
bpy.context.scene.collection.objects.link(fur)
fur.data.surface = coat
for m in fur.modifiers:
    for item in m.node_group.interface.items_tree:
        if getattr(item, "in_out", None) != "INPUT":
            continue
        x = getattr(m.properties.inputs, item.identifier)
        if not hasattr(x, "value"):
            continue
        if item.name == "Surface":
            x.value = coat
for o in bpy.data.objects:
    if o not in (coat, fur) and o.type in ("CURVES", "MESH", "CURVE"):
        o.hide_viewport = o.hide_render = True
if preview or facing_map:
    fur.hide_viewport = fur.hide_render = True
if facing_map:
    mat = bpy.data.materials.new("FacingMap"); mat.use_nodes = True
    nt = mat.node_tree; nt.nodes.clear()
    geo = nt.nodes.new("ShaderNodeNewGeometry"); dot = nt.nodes.new("ShaderNodeVectorMath"); dot.operation = "DOT_PRODUCT"
    em = nt.nodes.new("ShaderNodeEmission"); out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(geo.outputs["Normal"], dot.inputs[0]); nt.links.new(geo.outputs["Incoming"], dot.inputs[1])
    nt.links.new(dot.outputs["Value"], em.inputs["Color"]); nt.links.new(em.outputs[0], out.inputs[0])
    me.materials[0] = mat

sc = bpy.context.scene
if preview:
    sc.render.engine = "BLENDER_WORKBENCH"
else:
    if facing_map:
        sc.view_settings.view_transform = "Raw"; sc.world = None
        sc.render.image_settings.color_depth = "16"
        for o in bpy.data.objects:
            if o.type == "LIGHT": o.hide_render = True
    sc.cycles.device = "CPU"
    sc.cycles.samples = int(samples)
sc.render.resolution_x, sc.render.resolution_y, sc.render.resolution_percentage = 800, 1000, 100
sc.render.film_transparent = False
cam_data = bpy.data.cameras.new("TestCam"); cam_data.lens = 50
cam = bpy.data.objects.new("TestCam", cam_data); sc.collection.objects.link(cam); sc.camera = cam
cam.location = Vector(cam_loc.tolist())
cam.rotation_euler = (Vector(cam_target.tolist()) - cam.location).to_track_quat("-Z", "Y").to_euler()
sc.render.filepath = os.path.join(out_dir, f"coat_{view}{'_preview' if preview else ''}{'_facing' if facing_map else ''}.png")
bpy.ops.render.render(write_still=True)
print("RENDERED", sc.render.filepath, flush=True)
