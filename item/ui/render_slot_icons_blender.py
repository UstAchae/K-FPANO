# Slot pictures for the RAILINK's battery slot (mod_battery) and the helmets' rails slot (mod_equipment_railink): the
# battery pack and the rails as grey silhouettes, transparent, as the game's own slot icons (the game looks up
# "Slots/<slot name>" and shows a white box without one). Run: blender -b -P this -- <obj dir> <out dir>
import bpy, sys
from mathutils import Vector, Matrix
src, out = sys.argv[-2:]
for name, d, scale in (('battery', Vector((0.0, 0.15, -1.0)), 0.17), ('rails', Vector((1.0, 0.12, 0.0)), 0.36)):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.wm.obj_import(filepath=f'{src}/icon_{name}.obj', forward_axis='Y', up_axis='Z')
    objs = [o for o in bpy.context.scene.objects if o.type == 'MESH']
    for o in objs:
        m = bpy.data.materials.new('m'); m.diffuse_color = (0.25, 0.25, 0.25, 1); o.data.materials.append(m)
        for p in o.data.polygons: p.use_smooth = True
    sc = bpy.context.scene; sc.render.engine = 'BLENDER_WORKBENCH'; sc.display.shading.color_type = 'MATERIAL'; sc.display.shading.light = 'STUDIO'
    sc.render.film_transparent = True; sc.render.resolution_x = 280; sc.render.resolution_y = 240
    vs = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]
    lo = Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs))); hi = Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
    c = (lo + hi) / 2
    cam = bpy.data.cameras.new('c'); co = bpy.data.objects.new('c', cam); sc.collection.objects.link(co); sc.camera = co; cam.type = 'ORTHO'; cam.ortho_scale = scale
    d = d.normalized(); co.location = c + d * 0.8
    z = -d; up = Vector((0, 1, 0)); x = z.cross(up).normalized(); y = x.cross(z)   # the root frame's +Y is up
    co.matrix_world = Matrix(((x.x, y.x, -z.x, co.location.x), (x.y, y.y, -z.y, co.location.y), (x.z, y.z, -z.z, co.location.z), (0, 0, 0, 1)))
    if name == 'rails': sc.render.resolution_y = 200
    sc.render.filepath = f'{out}/slot_{name}_big.png'; bpy.ops.render.render(write_still=True)
