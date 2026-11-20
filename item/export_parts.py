# Blender side of the item pipeline: reads the purchased model (.blend, or FBX) and writes every mesh part, in world space (Blender
# frame, metres), triangulated, with per-corner normals and UVs, to item/src/parts/<part>.npz.
# Run: blender -b --factory-startup --python item/export_parts.py -- <NVG_L3Harris_FPGPNVG.blend> <out dir>
import sys
from pathlib import Path
import bpy
import numpy as np

src, out = sys.argv[-2:]
out = Path(out); out.mkdir(parents=True, exist_ok=True)
if src.endswith('.blend'):
    bpy.ops.wm.open_mainfile(filepath=src)
else:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src)
deps = bpy.context.evaluated_depsgraph_get()
for o in bpy.context.scene.objects:
    if o.type != 'MESH':
        continue
    m = o.evaluated_get(deps).to_mesh()
    m.calc_loop_triangles()
    w = o.matrix_world
    nw = w.to_3x3().inverted().transposed()
    uv = m.uv_layers.active.data
    pos, nor, tex = [], [], []
    for t in m.loop_triangles:
        for li in t.loops:
            l = m.loops[li]
            pos.append(tuple(w @ m.vertices[l.vertex_index].co))
            nor.append(tuple((nw @ l.normal).normalized()))
            tex.append(tuple(uv[li].uv))
    np.savez(out / (o.name + '.npz'), pos=np.array(pos, np.float32), nor=np.array(nor, np.float32), uv=np.array(tex, np.float32))
    print('PART', o.name, len(pos) // 3, 'triangles')
