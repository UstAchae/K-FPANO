# Turns the parts export_parts.py wrote (Blender frame, metres) into what the Unity builder reads, per colour:
#   item/Unity/Assets/Source/<colour>/<node>.bin   each node's mesh in its own frame
#                                     info.json    nodes (rest poses), flip rotations, thermal lens marker
#                                     albedo/spec/normal_<atlas>.png  maps for the game's p0 shader, per atlas
#
# The model (3DMA "NVG_L3_Harris_FPGPNVG", two colours side by side: FDE, and BLK with ".001" names) hangs as:
# Base (shroud adapter) > Frame > Mount_Main > Bridge > the goggle (all four tubes, one mesh) and its Cable.
# The Frame turns on the Base (the Frame's origin, along X): that is the flip hinge, next to the shroud, as on a
# G24-type mount (Mount_Main's own pivot, at the Frame's front boss, left the goggle hanging out in front when
# flipped). The Base stays on the helmet; Frame, Mount_Main, Bridge, goggle and cable flip.
#
# Frames. The device node copies the GPNVG-18 (nvg_l3_gpnvg-18_anvis_LOD0): eyepieces toward +Y (the eye looks
# along -Y), mount toward +Z, the wearer's right +X. The model has its eyepieces at +Y and its right at -X, so x is
# mirrored; the device node's origin is put where the GPNVG-18's eyecups are, from the centre of the two eyepieces'
# rearmost rim, so the GPNVG-18's deployed hinge pose places it first; then the whole goggle is seated on the
# helmet by its Base plate (as K-ENVG-B does).
# Run: python item/prepare.py <parts dir> <textures dir>
import json, sys
from pathlib import Path
import numpy as np
from PIL import Image
from native_look import match_spec, stretch_albedo

ITEM = Path(__file__).resolve().parent
UNITY = ITEM / 'Unity/Assets/Source'
COLOURS = {'fde': '', 'blk': '.001'}  # our colour key -> the model's object name suffix
PART = 'NVG_L3_Harris_FPGPNVG'
MPART = 'NVG_Mount_L3Harris_FPGPNVG'
BODY = [PART, PART + '_Bridge']                            # the goggle's atlas (FPGPNVG)
CABLE = PART + '_Cable'                                     # its power cable, its own skinned node (POWER_*)
BASE = [MPART + '_Base']                                    # stays on the helmet (FPG21 atlas)
FRAME = [MPART + 'Frame']                                   # flips on the Base (FPG21 atlas)
ARM = [MPART + '_Mount_Main']                               # tilts on the Frame's front boss (FPG21 atlas)
TILT_MODEL = np.array([0.0, -0.17196, 1.79386])             # Mount_Main's origin on the Frame's boss: the tilt pin (FDE)
# Adjustable mount for K-3PS-C's NightVisionMount (user: "支架可调节"), the K-ENVG-B layout: the Base stays; a
# carriage (height) slides up the shroud (root +Y) carrying the flip hinge "axis"; the Frame flips with it; the
# Mount_Main tilts on the Frame's front boss (its own origin, along X); the Bridge and goggle slide fore and aft
# under it along the optical axis (slide node +Z = away from the face). Travel is K-3PS-C's to choose.
HINGE_MODEL = np.array([0.0, -0.14438, 1.79386])            # the Frame's origin on the Base: the flip hinge (FDE copy)
COLOUR_SHIFT = {'fde': 0.0, 'blk': 0.2445}                  # the BLK copy stands 0.2445 m along +X in the model
# Eyepieces: each side one wide oval opening (inner and outer tube side by side), rearmost rim at y -0.1236; the
# tubes' axes at z 1.7018 (objectives at x +-0.0283 inner, +-0.0416 outer, front plane y -0.2332). Thermal
# objective: above the right inner tube, its window at y -0.2195, centred x -0.012 z 1.7335 (model frame).
EYEPIECE_MODEL = np.array([0.0, -0.1236, 1.7018])
THERMAL_MODEL = np.array([-0.0120, -0.2195, 1.7335])
EYES = np.array([0.0, 0.0505, -0.0757])                     # the GPNVG-18's eyecups (see K-ENVG-B)
AXIS_POS = np.array([-0.000388, -0.032200, 0.045600])       # GPNVG-18 "axis" localPosition
AXIS_ROT = (-0.4786032, 0.0, 0.0, 0.8780313)                # its deployed rotation
GP_ON = (0.47860321, -5.5665769e-08, 1.9227921e-08, -0.87803131)
GP_OFF = (0.98575121, -3.6230801e-08, -4.6429719e-08, 0.16821051)
PLATE_Y = -0.003   # the Base plate's centre on the shroud face (root frame), as the ENVG-B's
SEAT_GAP = 0.0005
FLIP_DEG = 160.0  # about the Base hinge: the goggle up against the helmet front (165 touched it)
TEX_SIZE = 2048
# Eyepiece glass. The model's eyepiece window, inside each oval rubber eyecup, is two flat panes meeting at a crease
# at |x| 0.0407: the inner tube's, flat at y -0.1401 facing the eye (|x| 0.0144..0.0407), and the outer tube's,
# turned 28.7 deg to face inward (normal (-+0.4796, 0.8775, 0), reaching |x| 0.0645 at the rim), together a
# 50 x 31.7 mm stadium (z 1.6861..1.7178); textured as flat grey. The real GPNVG-18 / F-PANO eyepiece (the user's
# reference photos) is a figure-8 of two round lenses, one per tube, overlapping a little. Ours: one domed lens on
# each pane (r 15.85 mm, half the window's height; 1.2 mm crown), the inner one centred 15.85 mm in from the window's
# inner end, the outer one 15.85 mm in from its outer end along its pane, both 0.3 mm proud of their pane, so the
# pair fills the window and meets in a seam over the crease. The panes stay, darkened, behind (LENS_DARK): the
# figure-8's waist shows dark housing, as on the real goggle. The right inner lens carries the thermal picture
# (user: "右数第二个目镜有热轮廓").
PANE_INNER_X = (0.0144, 0.0407)
PANE_Y = -0.1401
PANE_Z = (1.6861, 1.7178)
PANE_OUTER_TILT = np.radians(28.7)
PANE_OUTER_END = 0.0645
LENS_R = 0.01585
LENS_CROWN = 0.0012
LENS_LIFT = 0.0003
LENS_DARK_UV = (0.16, 0.64)  # a dark grey texel of the model's own pane paint
# Power (user: "后面应该和 ENVG-B 一样有块电池", then "改用这个" with the 3DMA Ops-Core RAILINK model, and "连导轨一起"):
# the RAILINK powered rail system for the FAST SF shell (the model's "SHSF" set: rails with their inner parts, and the
# battery pack on the back); the F-PANO is powered through the rails, as on the real system. 3DMA builds its helmet
# items in one scene, so the RAILINK parts are placed exactly as the F-PANO's own (to_device with the FDE copy's
# frame, then the same seat): checked on the helmet, the battery sits on its back and the rails along its sides.
# They hang on our root and do not flip. The headlamp, light and strobes of the set are left out.
POWER_PLUG_MODEL = (0.0810, -0.1222, 1.7574)    # the cable plug's centre (its axis (0.55, 0.83, -0.10)), model frame
POWER_SOCKET_MODEL = (0.0848, -0.1222, 1.7475)  # the left rail's front socket, on the plug's height along y
POWER_PLUG, POWER_BEND = 0.017, 0.028           # the plug moved whole; the cable bending to it beyond
POWER_NEAR, POWER_FAR = 0.025, 0.075            # skin weights: the socket's bone near it, the goggle's far from it
SIDE_FROM, SIDE_TO = 0.045, 0.075                # rails' skin weights: the group within |x| < 4.5 cm, a side past 7.5 cm
RAILINK_TEX = Path(r'D:/games/models/RAILINK/extracted/Helmet_OpsCore_RAILINK_v02/Textures/PBR_Helmet_OpsCore_RAILINK_SHSF_tx')
RAILINK = {'rails': ('Helmet_OpsCore_RAILINK_SHSF_Rail', 0), 'inside': ('Helmet_OpsCore_RAILINK_SHSF_Rail', 1),
           'battery': ('Helmet_OpsCore_RAILINK_SHSF_Battery', 0)}  # atlas: (part, material slot)
RAILINK_STEM = {'rails': 'Rails', 'inside': 'Inside', 'battery': 'Battery'}
ATLASES = {'body': ('NVG_L3Harris_FPGPNVG', BODY), 'mount': ('NVG_Mount_L3Harris_FPG21', BASE + FRAME + ARM),
           'rails': None, 'inside': None, 'battery': None}


def qmat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def mirror(p): return np.stack([-p[:, 0], p[:, 1], p[:, 2]], 1)


def rot_x(a):
    c, s = np.cos(a), np.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def qx(a): return (np.sin(a / 2), 0.0, 0.0, np.cos(a / 2))


def mat2quat(R):
    U, _, Vt = np.linalg.svd(R); R = U @ Vt
    w = np.sqrt(max(0.0, 1 + R[0, 0] + R[1, 1] + R[2, 2])) / 2
    x = np.sqrt(max(0.0, 1 + R[0, 0] - R[1, 1] - R[2, 2])) / 2
    y = np.sqrt(max(0.0, 1 - R[0, 0] + R[1, 1] - R[2, 2])) / 2
    z = np.sqrt(max(0.0, 1 - R[0, 0] - R[1, 1] + R[2, 2])) / 2
    x = np.copysign(x, R[2, 1] - R[1, 2]); y = np.copysign(y, R[0, 2] - R[2, 0]); z = np.copysign(z, R[1, 0] - R[0, 1])
    q = np.array([x, y, z, w]); q /= np.linalg.norm(q)
    assert np.allclose(qmat(q), R, atol=1e-6)
    return q


def plate(pts, tol=0.0008, iters=4000):
    """The largest flat face among the points (RANSAC, seeded), refined by a least-squares fit."""
    rng = np.random.default_rng(1)
    pts = np.unique(pts.round(5), axis=0)
    best = None
    for _ in range(iters):
        a, b, c = pts[rng.choice(len(pts), 3, replace=False)]
        n = np.cross(b - a, c - a); l = np.linalg.norm(n)
        if l < 1e-9: continue
        inl = np.abs((pts - a) @ (n / l)) < tol
        if best is None or inl.sum() > best.sum(): best = inl
    P = pts[best]; c = P.mean(0)
    n = np.linalg.svd(P - c)[2][2]
    return (n if n[2] > 0 else -n), c


def to_device(colour, p):
    """Model points (FDE copy's place) -> the device node's frame."""
    off = EYES - mirror(EYEPIECE_MODEL[None])[0]
    q = p - np.array([COLOUR_SHIFT[colour], 0, 0])
    return mirror(q) + off


def load(parts, colour, names):
    """Device-frame triangle corners (positions, normals, uvs) of the named parts."""
    P, N, U = [], [], []
    for n in names:
        d = np.load(parts / f'{n}{COLOURS[colour]}.npz')
        pos, nor, uv = d['pos'].astype(np.float64), d['nor'].astype(np.float64), d['uv']
        if n == PART:  # the model's eyepiece panes, darkened (see LENS_DARK_UV)
            c = (pos.reshape(-1, 3, 3).mean(1)) - [COLOUR_SHIFT[colour], 0, 0]; nm = nor.reshape(-1, 3, 3).mean(1)
            ax = np.abs(c[:, 0]); inz = (c[:, 2] > PANE_Z[0] - 0.002) & (c[:, 2] < PANE_Z[1] + 0.002)
            inner = (nm[:, 1] > 0.97) & (np.abs(c[:, 1] - PANE_Y) < 0.0003) & (ax > PANE_INNER_X[0] - 0.001) & (ax < PANE_INNER_X[1] + 0.001) & inz
            outer = (np.abs(np.abs(nm[:, 0]) - np.sin(PANE_OUTER_TILT)) < 0.06) & (np.sign(nm[:, 0]) == -np.sign(c[:, 0])) & (np.abs(nm[:, 2]) < 0.06)                     & (c[:, 1] > -0.1405) & (c[:, 1] < -0.1230) & (ax > 0.0405) & (ax < 0.0647) & inz & (nm[:, 1] > 0.85)                     & (np.abs((ax - PANE_INNER_X[1]) * -np.sin(PANE_OUTER_TILT) + (c[:, 1] - PANE_Y) * np.cos(PANE_OUTER_TILT)) < 0.0026)
            dark = np.repeat(inner | outer, 3); uv = uv.copy(); uv[dark] = LENS_DARK_UV
            print(f"{colour}: the model's eyepiece panes darkened: {int(inner.sum())} inner, {int(outer.sum())} outer triangles")
        p, nr = to_device(colour, pos), mirror(nor)
        idx = np.arange(len(p)).reshape(-1, 3)[:, [0, 2, 1]].ravel()  # the mirror reverses the winding
        P.append(p[idx]); N.append(nr[idx]); U.append(uv[idx])
    return np.concatenate(P), np.concatenate(N), np.concatenate(U)


def gp_deployed(p): return AXIS_POS + p @ qmat(AXIS_ROT).T  # device frame -> root frame, the GPNVG-18's pose


def seat_for(parts, colour):
    """Rigid correction (rotation about X, translation) seating the Base plate on the shroud face (root frame)."""
    m = gp_deployed(load(parts, colour, [BASE[0]])[0])
    n, c = plate(m)
    a = np.arctan2(n[1], n[2])
    R = rot_x(a)
    m2 = (m - c) @ R.T + c
    t = np.array([-c[0], PLATE_Y - c[1], SEAT_GAP - m2[:, 2].min()]) + c - R @ c
    print(f'{colour}: shroud plate normal {n.round(3)}, turned {np.degrees(a):.1f} deg about X, moved {(t + R @ c - c).round(4)}')
    return R, t


def weld(P, N, U):
    key = np.round(np.hstack([P * 1e5, N * 1e3, U * 1e5])).astype(np.int64)
    _, first, inverse = np.unique(key, axis=0, return_index=True, return_inverse=True)
    return P[first], N[first], U[first], inverse.ravel().astype(np.int32)


def write_mesh(path, P, N, U):
    P, N, U, tri = weld(P, N, U)
    with open(path, 'wb') as f:
        np.array([len(P), len(tri) // 3, 1], np.int32).tofile(f)
        P.astype(np.float32).tofile(f); N.astype(np.float32).tofile(f); U.astype(np.float32).tofile(f)
        np.array([len(tri)], np.int32).tofile(f); tri.tofile(f)
    print(f'{path.relative_to(ITEM)}: {len(P)} vertices, {len(tri) // 3} triangles')


def rig(parts, colour, out):
    Rs, ts = seat_for(parts, colour)
    Ron = qmat(AXIS_ROT)
    Rdev = Rs @ Ron                                            # the deployed goggle in the root frame
    def seated(p): return (p @ Ron.T + AXIS_POS) @ Rs.T + ts   # device frame -> root frame
    model = lambda p: seated(to_device(colour, (p + [COLOUR_SHIFT[colour], 0, 0])[None]))[0]
    hinge, pin = model(HINGE_MODEL), model(TILT_MODEL)
    away = Rdev @ np.array([0.0, -1.0, 0.0])                   # along the optical axis, away from the face
    lat = np.array([1.0, 0, 0]); lat = lat - (lat @ away) * away; lat /= np.linalg.norm(lat)
    Rslide = np.column_stack([lat, np.cross(away, lat), away])
    W = {'nvg_fpano_mount_LOD0': (np.eye(3), np.zeros(3)),
         'fpano_carriage': (np.eye(3), hinge),
         'axis': (Rdev, hinge),
         'fpano_frame': (Rdev, hinge),
         'fpano_tilt': (Rdev, pin),
         'fpano_arm': (Rdev, pin),
         'fpano_slide': (Rslide, pin),
         'nvg_fpano_LOD0': (Rdev, seated(np.zeros((1, 3)))[0])}
    parent = {'nvg_fpano_mount_LOD0': None, 'fpano_carriage': None, 'axis': 'fpano_carriage', 'fpano_frame': 'axis',
              'fpano_tilt': 'fpano_frame', 'fpano_arm': 'fpano_tilt', 'fpano_slide': 'fpano_arm', 'nvg_fpano_LOD0': 'fpano_slide'}
    meshes = {'nvg_fpano_mount_LOD0': (BASE, 'mount'), 'fpano_frame': (FRAME, 'mount'), 'fpano_arm': (ARM, 'mount'),
              'nvg_fpano_LOD0': (BODY, 'body')}
    nodes = []
    for name, par in parent.items():
        R, t = W[name]
        if par is None: lr, lt = R, t
        else:
            PR, Pt = W[par]; lr = PR.T @ R; lt = PR.T @ (t - Pt)
        node = {'name': name, 'parent': par or '', 'pos': lt.tolist(), 'rot': mat2quat(lr).tolist(), 'mesh': '', 'mat': ''}
        if name in meshes:
            names, mat = meshes[name]
            p, n, u = load(parts, colour, names)
            p, n = seated(p), n @ Rdev.T                       # root frame
            write_mesh(out / f'{name}.bin', (p - t) @ R, n @ R, u); node['mesh'] = name; node['mat'] = mat
        nodes.append(node)
    # The goggle's power cable, plugged into the left rail's front socket (user: "直接插进去"). In the 3DMA scene its plug
    # hangs 1 cm over the socket: the plug (within POWER_PLUG of its centre) is moved onto the socket's axis, the cable
    # bending down to it over the next POWER_BEND. The cable is skinned to two bones, the goggle's device node and a
    # socket node on the root: within POWER_NEAR of the socket it stays in the socket, past POWER_FAR it follows the
    # goggle, between it blends, so it stays plugged in as the goggle flips and is adjusted.
    plug_c, sock_c = to_device(colour, np.array([POWER_PLUG_MODEL, POWER_SOCKET_MODEL]) + [COLOUR_SHIFT[colour], 0, 0])
    shift = sock_c - plug_c; shift[1] = 0.0                    # across the plug's axis only (the axis runs along y)
    p, n, u = load(parts, colour, [CABLE])
    d = np.linalg.norm(p - plug_c, axis=1)
    f = np.clip((POWER_PLUG + POWER_BEND - d) / POWER_BEND, 0, 1); f = f * f * (3 - 2 * f)
    p = p + f[:, None] * shift
    sock = seated(sock_c[None])[0]
    # The RAILINK group: the rails, the battery and the cable's socket under one node on the root (identity), which the
    # client moves per helmet (item/railink_fit.json, from fit_helmets.py).
    nodes.append({'name': 'fpano_railink', 'parent': '', 'pos': [0, 0, 0], 'rot': [0, 0, 0, 1], 'mesh': '', 'mat': ''})
    # Each side's rail can be moved out or in on its own (fpano_railink_L / _R, local +-X), so a rigid rail made for
    # the FAST SF rests on wider or narrower shells: the rails and their inner parts are skinned to the group and the
    # two side nodes, weighted by |x| (the rear crossbar stretches), the battery stays on the group.
    for side, sx_ in (('L', -1), ('R', 1), ('back', 0)):   # back: the battery and the rails' rear crossbar
        nodes.append({'name': f'fpano_railink_{side}', 'parent': 'fpano_railink', 'pos': [0, 0, 0], 'rot': [0, 0, 0, 1], 'mesh': '', 'mat': ''})
    nodes.append({'name': 'fpano_power_socket', 'parent': 'fpano_railink_L', 'pos': sock.tolist(), 'rot': [0, 0, 0, 1], 'mesh': '', 'mat': ''})
    write_mesh(out / 'fpano_power_cable.bin', seated(p), n @ Rdev.T, u)
    nodes.append({'name': 'fpano_power_cable', 'parent': '', 'pos': [0, 0, 0], 'rot': [0, 0, 0, 1], 'mesh': 'fpano_power_cable', 'mat': 'body',
                  'skin': True, 'bones': ['nvg_fpano_LOD0', 'fpano_power_socket'], 'weightCentre': sock.tolist(),
                  'weightNear': POWER_NEAR, 'weightFar': POWER_FAR})
    # The RAILINK rails and battery, on the root (see RAILINK), in the root frame.
    for atlas, (part, slot) in RAILINK.items():
        d = np.load(parts.parent / 'railink' / f'{part}.npz')
        keep = np.repeat(d['mat'] == slot, 3)
        p = to_device('fde', d['pos'].astype(np.float64)[keep]); n = mirror(d['nor'].astype(np.float64)[keep]); u = d['uv'][keep]
        idx = np.arange(len(p)).reshape(-1, 3)[:, [0, 2, 1]].ravel()  # the mirror reverses the winding
        name = f'fpano_railink_{atlas}'
        write_mesh(out / f'{name}.bin', seated(p[idx]), n[idx] @ Rdev.T, u[idx])
        node = {'name': name, 'parent': 'fpano_railink_back' if atlas == 'battery' else 'fpano_railink', 'pos': [0, 0, 0], 'rot': [0, 0, 0, 1], 'mesh': name, 'mat': atlas}
        if atlas != 'battery':
            node.update(sideSkin=True, bones=['fpano_railink_back', 'fpano_railink_L', 'fpano_railink_R'], sideFrom=SIDE_FROM, sideTo=SIDE_TO)
        nodes.append(node)
    q_on = mat2quat(Rdev)
    dq = qmul(GP_OFF, (-GP_ON[0], -GP_ON[1], -GP_ON[2], GP_ON[3]))
    sgn = np.sign((2 * np.arctan2(dq[0], dq[3]) + np.pi) % (2 * np.pi) - np.pi)
    q_off = np.array(qmul(qx(sgn * np.radians(FLIP_DEG)), q_on))
    root = 'nvg_fpano_mount_LOD0'
    joints = {'height': {'node': 'fpano_carriage', 'axisInRoot': [0.0, 1.0, 0.0], 'note': 'slides up the shroud; the Base stays'},
              'flip': {'node': 'fpano_carriage/axis', 'axisLocal': 'X', 'degrees': FLIP_DEG},
              'tilt': {'node': 'fpano_carriage/axis/fpano_frame/fpano_tilt', 'axisLocal': 'X', 'pivot': "Mount_Main's pin on the Frame's front boss"},
              'slide': {'node': 'fpano_carriage/axis/fpano_frame/fpano_tilt/fpano_arm/fpano_slide', 'axisLocal': 'Z (away from the face)'},
              'device': 'fpano_carriage/axis/fpano_frame/fpano_tilt/fpano_arm/fpano_slide/nvg_fpano_LOD0'}
    return nodes, q_on, q_off, joints


def channel(path, size):
    return np.asarray(Image.open(path).convert('L').resize((size, size), Image.LANCZOS)).astype(np.float64) / 255


def atlas_files(tex, atlas, colour):
    """(base colour file, file of a shared map by its kind) of an atlas: the F-PANO's own sets are named by colour
    (FDE carries the shared maps), the RAILINK's have no colour in the FDE names and "BLK" in the black base colour."""
    if atlas in RAILINK:
        stem = f'Helmet_OpsCore_RAILINK_SHSF_{RAILINK_STEM[atlas]}'
        return RAILINK_TEX / f'{stem}_{"BLK_" if colour == "blk" else ""}BaseColor.png', lambda k: RAILINK_TEX / f'{stem}_{k}.png'
    stem = ATLASES[atlas][0]
    return tex / f'{stem}_{colour.upper()}_BaseColor.png', lambda k: tex / f'{stem}_FDE_{k}.png'


def textures(tex, colour, out):
    """Per atlas: the colour's base colour; AO, roughness, metalness and normal are shared by the colours."""
    for atlas in ATLASES:
        size = TEX_SIZE if atlas == 'body' else 1024
        base_file, shared = atlas_files(tex, atlas, colour)
        base = np.asarray(Image.open(base_file).convert('RGB').resize((size, size), Image.LANCZOS)).astype(np.float64) / 255
        ao, rough, metal = (channel(shared(k), size) for k in ('AO', 'Roughness', 'Metalness'))
        gloss = 1 - rough
        albedo = base * (0.25 + 0.75 * ao[..., None])
        painted = base.max(-1) > 2 / 255
        albedo = stretch_albedo(albedo, painted)
        reflect = np.clip(0.04 + 0.30 * metal * gloss + 0.12 * gloss ** 2, 0, 0.5)
        spec = match_spec((0.15 + 0.85 * gloss ** 1.5 * (0.5 + 0.5 * metal)) * (0.4 + 0.6 * ao), painted)
        save = lambda name, rgb, alpha=1.0: Image.fromarray((np.clip(np.dstack([rgb, np.broadcast_to(alpha, rgb.shape[:2])]), 0, 1) * 255).astype(np.uint8), 'RGBA').save(out / f'{name}_{atlas}.png')
        save('albedo', albedo, reflect)
        save('spec', np.dstack([spec] * 3))
        normal = np.asarray(Image.open(shared('OpenGL_Normal')).convert('RGB').resize((size, size), Image.LANCZOS)).astype(np.float64) / 255
        save('normal', normal)


def lens_frames(side):
    """(centre, normal, in-plane x axis) of the inner and outer lens of one eyepiece, model frame; side +1 or -1."""
    zc = (PANE_Z[0] + PANE_Z[1]) / 2
    inner = (np.array([side * (PANE_INNER_X[0] + LENS_R), PANE_Y, zc]), np.array([0.0, 1.0, 0.0]), np.array([side * 1.0, 0, 0]))
    t = PANE_OUTER_TILT
    along = np.array([side * np.cos(t), np.sin(t), 0.0])       # up the outer pane, away from the crease
    crease = np.array([side * PANE_INNER_X[1], PANE_Y, zc])
    width = (PANE_OUTER_END - PANE_INNER_X[1]) / np.cos(t)     # the pane's width along itself
    outer = (crease + along * (width - LENS_R), np.array([-side * np.sin(t), np.cos(t), 0.0]), along)
    return {'inner': inner, 'outer': outer}


def dome(centre, normal, ax, rings=14, segs=72):
    """Triangle corners and smooth normals of a shallow spherical cap: radius LENS_R, crown LENS_CROWN, on the
    plane through `centre` with `normal`, lifted LENS_LIFT off it."""
    ay = np.cross(normal, ax); ay /= np.linalg.norm(ay)
    R = (LENS_R ** 2 + LENS_CROWN ** 2) / (2 * LENS_CROWN)    # the sphere's radius
    def pt(r, a):
        h = np.sqrt(R * R - r * r) - (R - LENS_CROWN)          # height over the rim
        p = centre + normal * (LENS_LIFT + h) + LENS_R * 0 + r * (np.cos(a) * ax + np.sin(a) * ay)
        sph = centre + normal * (LENS_LIFT + LENS_CROWN - R)   # the sphere's centre
        n = p - sph; return p, n / np.linalg.norm(n)
    P, N = [], []
    rs = np.linspace(0, LENS_R, rings + 1); As = np.linspace(0, 2 * np.pi, segs + 1)
    for i in range(rings):
        for k in range(segs):
            a = [pt(rs[i], As[k]), pt(rs[i + 1], As[k]), pt(rs[i + 1], As[k + 1]), pt(rs[i], As[k + 1])]
            for tri in ((0, 1, 2), (0, 2, 3)):
                if i == 0 and tri == (0, 2, 3): continue
                c = [a[m] for m in tri]
                f = np.cross(c[1][0] - c[0][0], c[2][0] - c[0][0])
                if f @ normal < 0: c = [c[0], c[2], c[1]]
                for p, n in c: P.append(p); N.append(n)
    return np.array(P), np.array(N)


def glass(colour):
    """The two eyepieces' figure-8 glass in the device frame (positions, normals, uvs), and each lens's centre and
    facing there."""
    P, N = [], []
    lenses = {}
    for side, name in ((-1, 'right'), (+1, 'left')):  # model -X is the wearer's right
        for k, (c, n, ax) in lens_frames(side).items():
            p, nn = dome(c + [COLOUR_SHIFT[colour], 0, 0], n, ax)
            P.append(p); N.append(nn)
            dc = to_device(colour, (c + [COLOUR_SHIFT[colour], 0, 0])[None])[0]
            lenses[f'{name}_{k}'] = {'centre': dc.round(5).tolist(), 'facing': mirror(n[None])[0].round(4).tolist(), 'radius': LENS_R}
    p = to_device(colour, np.vstack(P)); n = mirror(np.vstack(N))
    idx = np.arange(len(p)).reshape(-1, 3)[:, [0, 2, 1]].ravel()  # the mirror reverses the winding
    return (p[idx], n[idx], np.zeros((len(p), 2), np.float32)), lenses


def main(parts, tex):
    for colour in COLOURS:
        out = UNITY / colour; out.mkdir(parents=True, exist_ok=True)
        nodes, q_on, q_off, joints = rig(parts, colour, out)
        (gp, gn, gu), discs = glass(colour)
        write_mesh(out / 'nvg_fpano_glass_LOD0.bin', gp, gn, gu)
        nodes.append({'name': 'nvg_fpano_glass_LOD0', 'parent': 'nvg_fpano_LOD0', 'pos': [0, 0, 0], 'rot': [0, 0, 0, 1],
                      'mesh': 'nvg_fpano_glass_LOD0', 'mat': 'glass'})
        lens = to_device(colour, (THERMAL_MODEL + [COLOUR_SHIFT[colour], 0, 0])[None])[0]
        info = {'colour': colour, 'atlases': list(ATLASES), 'thermalLens': lens.tolist(), 'onRotation': list(map(float, q_on)),
                'offRotation': list(map(float, q_off)), 'nodes': nodes}
        (out / 'info.json').write_text(json.dumps(info, indent=1))
        from split_items import split
        print(colour, 'items:', split(out, info))
        if colour == 'fde':
            (ITEM / 'nodes.json').write_text(json.dumps({'nodes': nodes, 'joints': joints, 'thermalLens': lens.tolist(),
                                                          'eyepieceLenses (device frame: centre, facing toward the eye, radius)': discs}, indent=1))
            print('eyepiece discs', discs)
        print(colour, 'thermal lens at', lens.round(4))
        textures(tex, colour, out)


if __name__ == '__main__':
    main(Path(sys.argv[1]), Path(sys.argv[2]))
