# Blender side of fit_manual.py: per shape, the helmet (grey) and the RAILINK group (blue) in the F-PANO root frame
# (X across, Y up the shroud, Z forward), seen from the right (+X) and from above (+Y).
import sys
from pathlib import Path
import bpy
from mathutils import Vector, Matrix

args = sys.argv[sys.argv.index('--') + 1:]
out, keys = Path(args[0]), args[1:]
for key in keys:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'; sc.display.shading.color_type = 'MATERIAL'; sc.display.shading.show_cavity = True
    sc.render.resolution_x = 900; sc.render.resolution_y = 560
    bpy.ops.wm.obj_import(filepath=str(out / f'manual_{key}.obj'), forward_axis='Y', up_axis='Z')
    for o in bpy.context.selected_objects:
        m = bpy.data.materials.new(o.name)
        m.diffuse_color = (0.3, 0.55, 0.95, 1) if o.name.startswith('rails') else (0.95, 0.2, 0.2, 1) if o.name.startswith('band') else (0.6, 0.6, 0.6, 1)
        o.data.materials.append(m)
    cam = bpy.data.cameras.new('c'); co = bpy.data.objects.new('c', cam); sc.collection.objects.link(co); sc.camera = co
    cam.type = 'ORTHO'; cam.ortho_scale = float(__import__('os').environ.get('FIT_SCALE','0.30'))
    centre = Vector((0.0, -0.01, float(__import__('os').environ.get('FIT_Z','-0.12'))))
    rails = [o for o in bpy.data.objects if o.name.startswith('rails')]
    for view, d, up, show in (('side', Vector((1, 0, 0)), Vector((0, 1, 0)), True), ('top', Vector((0, 0.35, -1)).normalized(), Vector((0, 1, 0)), True)):
        for o in rails: o.hide_render = not show                   # 'top' is a view from behind and above
        c = centre if view == 'side' else Vector((0.0, 0.03, -0.14))
        co.location = c + d * 0.8
        z = -d; x = z.cross(up).normalized(); y = x.cross(z)
        co.matrix_world = Matrix(((x.x, y.x, -z.x, co.location.x), (x.y, y.y, -z.y, co.location.y), (x.z, y.z, -z.z, co.location.z), (0, 0, 0, 1)))
        sc.render.filepath = str(out / f'manual_{key}_{view}.png'); bpy.ops.render.render(write_still=True)
