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
BODY = [PART, PART + '_Bridge', PART + '_Cable']          # the goggle's atlas (FPGPNVG)
BASE = [MPART + '_Base']                                    # stays on the helmet (FPG21 atlas)
ARM = [MPART + 'Frame', MPART + '_Mount_Main']              # flips (FPG21 atlas)
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
# Eyepiece glass. The model's own lens is one flat pane per side inside the oval eyecup (plane y -0.1401, x
# 0.0144..0.0407, rounded on the inner side only, textured dark), over a flat opaque cap (y -0.1426) that fills
# the eyecup's opening (x 0.0113..0.0557, z 1.6846..1.7193). The real GPNVG-18 / F-PANO eyepiece is a figure-8
# (the user's reference photo: two equal round lenses side by side, overlapping a little, filling most of the oval):
# the model's pane is left out (LENS_DROP) and ours put in its plane, its own glass node: two discs of r 13.5 mm
# per side, 17 mm apart (inner at |x| 0.0250, outer at |x| 0.0420), at the tubes' height z 1.7018, so the pair spans
# the opening's 44 mm. The right inner disc carries the thermal picture (user: "右数第二个目镜有热轮廓").
GLASS_Y = -0.1401
GLASS_Z = 1.7018
GLASS_R = 0.0135
GLASS_X = {'inner': 0.0250, 'outer': 0.0420}
LENS_DROP = dict(y=(-0.1404, -0.1398), ax=(0.0140, 0.0410), z=(1.685, 1.719))  # the model's pane (body, facing +Y)
ATLASES = {'body': ('NVG_L3Harris_FPGPNVG', BODY), 'mount': ('NVG_Mount_L3Harris_FPG21', BASE + ARM)}


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
        if n == PART:  # leave out the model's own eyepiece panes (see LENS_DROP)
            c = (pos.reshape(-1, 3, 3).mean(1)) - [COLOUR_SHIFT[colour], 0, 0]; ny = nor.reshape(-1, 3, 3).mean(1)[:, 1]
            L = LENS_DROP
            drop = (ny > 0.97) & (c[:, 1] > L['y'][0]) & (c[:, 1] < L['y'][1]) & (np.abs(c[:, 0]) > L['ax'][0]) & (np.abs(c[:, 0]) < L['ax'][1]) & (c[:, 2] > L['z'][0]) & (c[:, 2] < L['z'][1])
            keep = np.repeat(~drop, 3); pos, nor, uv = pos[keep], nor[keep], uv[keep]
            print(f"{colour}: the model's eyepiece panes left out: {int(drop.sum())} triangles")
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
    hinge = seated(to_device(colour, (HINGE_MODEL + [COLOUR_SHIFT[colour], 0, 0])[None]))[0]
    W = {'nvg_fpano_mount_LOD0': (np.eye(3), np.zeros(3)),
         'axis': (Rdev, hinge),
         'fpano_arm': (Rdev, hinge),
         'nvg_fpano_LOD0': (Rdev, seated(np.zeros((1, 3)))[0])}
    parent = {'nvg_fpano_mount_LOD0': None, 'axis': None, 'fpano_arm': 'axis', 'nvg_fpano_LOD0': 'axis'}
    meshes = {'nvg_fpano_mount_LOD0': (BASE, 'mount'), 'fpano_arm': (ARM, 'mount'), 'nvg_fpano_LOD0': (BODY, 'body')}
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
    q_on = mat2quat(Rdev)
    dq = qmul(GP_OFF, (-GP_ON[0], -GP_ON[1], -GP_ON[2], GP_ON[3]))
    sgn = np.sign((2 * np.arctan2(dq[0], dq[3]) + np.pi) % (2 * np.pi) - np.pi)
    q_off = np.array(qmul(qx(sgn * np.radians(FLIP_DEG)), q_on))
    return nodes, q_on, q_off


def channel(path, size):
    return np.asarray(Image.open(path).convert('L').resize((size, size), Image.LANCZOS)).astype(np.float64) / 255


def textures(tex, colour, out):
    """Per atlas: the colour's base colour; AO, roughness, metalness and normal are the FDE set's (shared)."""
    for atlas, (stem, _) in ATLASES.items():
        size = TEX_SIZE if atlas == 'body' else 1024
        base = np.asarray(Image.open(tex / f'{stem}_{colour.upper()}_BaseColor.png').convert('RGB').resize((size, size), Image.LANCZOS)).astype(np.float64) / 255
        ao, rough, metal = (channel(tex / f'{stem}_FDE_{k}.png', size) for k in ('AO', 'Roughness', 'Metalness'))
        gloss = 1 - rough
        albedo = base * (0.25 + 0.75 * ao[..., None])
        painted = base.max(-1) > 2 / 255
        albedo = stretch_albedo(albedo, painted)
        reflect = np.clip(0.04 + 0.30 * metal * gloss + 0.12 * gloss ** 2, 0, 0.5)
        spec = match_spec((0.15 + 0.85 * gloss ** 1.5 * (0.5 + 0.5 * metal)) * (0.4 + 0.6 * ao), painted)
        save = lambda name, rgb, alpha=1.0: Image.fromarray((np.clip(np.dstack([rgb, np.broadcast_to(alpha, rgb.shape[:2])]), 0, 1) * 255).astype(np.uint8), 'RGBA').save(out / f'{name}_{atlas}.png')
        save('albedo', albedo, reflect)
        save('spec', np.dstack([spec] * 3))
        normal = np.asarray(Image.open(tex / f'{stem}_FDE_OpenGL_Normal.png').convert('RGB').resize((size, size), Image.LANCZOS)).astype(np.float64) / 255
        save('normal', normal)


def figure8(side):
    """Triangle corners (model frame) of one eyepiece's figure-8 glass, facing the eye (+Y); side +1 or -1 (x sign)."""
    ci, co = side * GLASS_X['inner'], side * GLASS_X['outer']
    mid = (ci + co) / 2
    a = np.linspace(0, 2 * np.pi, 160, endpoint=False)
    # the union's outline seen from its midpoint: for each direction the farther of the two circles' boundaries
    out = []
    for t in a:
        d = np.array([np.cos(t), np.sin(t)])
        best = 0.0
        for c in (ci, co):
            o = np.array([mid - c, 0.0])  # ray start relative to the circle's centre
            b = o @ d; disc = b * b - (o @ o - GLASS_R ** 2)
            if disc >= 0: best = max(best, -b + np.sqrt(disc))
        out.append((mid + d[0] * best, GLASS_Z + d[1] * best))
    out = np.array(out); P = []
    for i in range(len(out)):
        j = (i + 1) % len(out)
        for x, z in ((mid, GLASS_Z), (out[j][0], out[j][1]), (out[i][0], out[i][1])):
            P.append((x, GLASS_Y, z))
    P = np.array(P)
    # wind each triangle to face +Y (the eye)
    T = P.reshape(-1, 3, 3)
    n = np.cross(T[:, 1] - T[:, 0], T[:, 2] - T[:, 0])[:, 1]
    T[n < 0] = T[n < 0][:, [0, 2, 1]]
    return T.reshape(-1, 3)


def glass(colour):
    """The two eyepieces' glass in the device frame (positions, normals, uvs), and each disc's centre there."""
    P = np.vstack([figure8(+1), figure8(-1)]) + [COLOUR_SHIFT[colour], 0, 0]
    p = to_device(colour, P)
    idx = np.arange(len(p)).reshape(-1, 3)[:, [0, 2, 1]].ravel()  # the mirror reverses the winding
    p = p[idx]; n = np.tile([0.0, 1.0, 0.0], (len(p), 1)); u = np.zeros((len(p), 2), np.float32)
    discs = {}
    for side, name in ((-1, 'right'), (+1, 'left')):  # model -X is the wearer's right
        for k, x in GLASS_X.items():
            c = to_device(colour, np.array([[side * x + COLOUR_SHIFT[colour], GLASS_Y, GLASS_Z]]))[0]
            discs[f'{name}_{k}'] = {'centre': c.round(5).tolist(), 'radius': GLASS_R}
    return (p, n, u), discs


def main(parts, tex):
    for colour in COLOURS:
        out = UNITY / colour; out.mkdir(parents=True, exist_ok=True)
        nodes, q_on, q_off = rig(parts, colour, out)
        (gp, gn, gu), discs = glass(colour)
        write_mesh(out / 'nvg_fpano_glass_LOD0.bin', gp, gn, gu)
        nodes.append({'name': 'nvg_fpano_glass_LOD0', 'parent': 'nvg_fpano_LOD0', 'pos': [0, 0, 0], 'rot': [0, 0, 0, 1],
                      'mesh': 'nvg_fpano_glass_LOD0', 'mat': 'glass'})
        lens = to_device(colour, (THERMAL_MODEL + [COLOUR_SHIFT[colour], 0, 0])[None])[0]
        info = {'colour': colour, 'thermalLens': lens.tolist(), 'onRotation': list(map(float, q_on)),
                'offRotation': list(map(float, q_off)), 'nodes': nodes}
        (out / 'info.json').write_text(json.dumps(info, indent=1))
        if colour == 'fde':
            (ITEM / 'nodes.json').write_text(json.dumps({'nodes': nodes, 'thermalLens': lens.tolist(),
                                                          'eyepieceDiscs (device frame, the glass faces +Y)': discs}, indent=1))
            print('eyepiece discs', discs)
        print(colour, 'thermal lens at', lens.round(4))
        textures(tex, colour, out)


if __name__ == '__main__':
    main(Path(sys.argv[1]), Path(sys.argv[2]))
