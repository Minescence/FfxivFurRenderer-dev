"""Dump the hair-relevant structure of a .blend as JSON (run: blender -b --disable-autoexec FILE -P inspect_blend.py -- out.json)."""
import bpy, json, sys

def val(v):
    try:
        if hasattr(v, "__len__") and not isinstance(v, str):
            return [val(x) for x in v]
    except TypeError:
        pass
    return v if isinstance(v, (int, float, str, bool, type(None))) else str(v)

def node_tree(nt):
    if not nt:
        return None
    nodes = []
    for n in nt.nodes:
        inputs = {}
        for s in n.inputs:
            if not s.is_linked and hasattr(s, "default_value"):
                inputs[s.name] = val(s.default_value)
        entry = {"name": n.name, "type": n.bl_idname, "inputs": inputs}
        if n.bl_idname == "GeometryNodeGroup" and n.node_tree:
            entry["group"] = n.node_tree.name
        if n.bl_idname == "ShaderNodeValToRGB":
            entry["ramp"] = [(e.position, val(e.color)) for e in n.color_ramp.elements]
        nodes.append(entry)
    links = [f"{l.from_node.name}.{l.from_socket.name} -> {l.to_node.name}.{l.to_socket.name}" for l in nt.links]
    return {"nodes": nodes, "links": links}

out = {"version": bpy.app.version_string, "scene": {}, "objects": [], "node_groups": {}}
sc = bpy.context.scene
out["scene"] = {"engine": sc.render.engine, "camera": sc.camera.name if sc.camera else None,
                "resolution": [sc.render.resolution_x, sc.render.resolution_y], "frame": sc.frame_current,
                "world": node_tree(sc.world.node_tree) if sc.world and sc.world.use_nodes else None}
for o in bpy.data.objects:
    e = {"name": o.name, "type": o.type, "parent": o.parent.name if o.parent else None,
         "location": val(o.location), "rotation": val(o.rotation_euler), "scale": val(o.scale),
         "hide_render": o.hide_render, "materials": [s.material.name for s in o.material_slots if s.material],
         "modifiers": []}
    for m in o.modifiers:
        me = {"name": m.name, "type": m.type, "show_render": m.show_render}
        if m.type == "NODES" and m.node_group:
            me["group"] = m.node_group.name
            me["inputs"] = {}
            for item in m.node_group.interface.items_tree:
                if getattr(item, "in_out", None) != "INPUT":
                    continue
                try:
                    me["inputs"][item.name] = val(m[item.identifier])
                except Exception:
                    me["inputs"][item.name] = val(getattr(item, "default_value", None))
        if m.type == "ARMATURE" and m.object:
            me["armature"] = m.object.name
        if m.type == "PARTICLE_SYSTEM":
            ps = m.particle_system.settings
            me["particles"] = {k: val(getattr(ps, k)) for k in (
                "type", "count", "hair_length", "hair_step", "child_type", "child_nbr", "rendered_child_count",
                "clump_factor", "clump_shape", "roughness_1", "roughness_2", "roughness_endpoint", "kink",
                "kink_amplitude", "kink_frequency", "root_radius", "tip_radius", "radius_scale", "child_length",
                "child_radius", "child_roundness", "display_step", "render_step") if hasattr(ps, k)}
        e["modifiers"].append(me)
    if o.type == "CURVES":
        c = o.data
        e["curves"] = {"curve_count": len(c.curves), "point_count": len(c.points),
                       "surface": c.surface.name if c.surface else None, "surface_uv_map": c.surface_uv_map,
                       "attributes": [(a.name, a.domain, a.data_type) for a in c.attributes]}
        e["evaluated"] = None
    if o.type == "MESH":
        me = o.data
        e["mesh"] = {"verts": len(me.vertices), "faces": len(me.polygons), "uv_layers": [u.name for u in me.uv_layers],
                     "vertex_groups": len(o.vertex_groups), "attributes": [(a.name, a.domain, a.data_type) for a in me.attributes]}
    out["objects"].append(e)

dg = bpy.context.evaluated_depsgraph_get()
for e in out["objects"]:
    if e["type"] == "CURVES":
        ev = bpy.data.objects[e["name"]].evaluated_get(dg)
        e["evaluated"] = {"curve_count": len(ev.data.curves), "point_count": len(ev.data.points)}

for ng in bpy.data.node_groups:
    out["node_groups"][ng.name] = {"type": ng.bl_idname, **(node_tree(ng) or {})}
out["materials"] = {m.name: node_tree(m.node_tree) for m in bpy.data.materials if m.use_nodes}
out["texts"] = [t.name for t in bpy.data.texts]

path = sys.argv[sys.argv.index("--") + 1]
with open(path, "w") as f:
    json.dump(out, f, indent=1)
