# Blender side of fit_eye.py: the helmet alone from its right (+X), F-PANO root frame, orthographic, a fixed window
# (Z 0 .. -0.30 across 900 px, Y centred on 0.05 over 560 px), shaded so raised rails read.
import sys
from pathlib import Path
import bpy
from mathutils import Vector, Matrix
args = sys.argv[sys.argv.index('--') + 1:]
out, keys = Path(args[0]), args[1:]
for key in keys:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'; sh = sc.display.shading
    sh.light = 'STUDIO'; sh.color_type = 'SINGLE'; sh.single_color = (0.75, 0.75, 0.75); sh.show_cavity = True
    sh.cavity_type = 'BOTH'; sh.curvature_ridge_factor = 2.0; sh.curvature_valley_factor = 2.0; sh.show_object_outline = True
    sc.render.resolution_x = 900; sc.render.resolution_y = 560
    bpy.ops.wm.obj_import(filepath=str(out / f'manual_{key}.obj'), forward_axis='Y', up_axis='Z')
    for o in list(bpy.context.selected_objects):
        if not o.name.startswith('shell'): bpy.data.objects.remove(o)
    cam = bpy.data.cameras.new('c'); co = bpy.data.objects.new('c', cam); sc.collection.objects.link(co); sc.camera = co
    cam.type = 'ORTHO'; cam.ortho_scale = 0.30
    centre = Vector((0.0, 0.05, -0.15)); d = Vector((1, 0, 0)); up = Vector((0, 1, 0))
    co.location = centre + d * 0.8; z = -d; x = z.cross(up).normalized(); y = x.cross(z)
    co.matrix_world = Matrix(((x.x, y.x, -z.x, co.location.x), (x.y, y.y, -z.y, co.location.y), (x.z, y.z, -z.z, co.location.z), (0, 0, 0, 1)))
    sc.render.filepath = str(out / f'eye_{key}.png'); bpy.ops.render.render(write_still=True)
