# Preview pictures of helmet + RAILINK rails + helmet headset as the client places them (0.2.29: perHelmet pose and
# scaleX), for the user ("直接给我看下预览图"). Flat-shaded software render with a depth sort, in the F-PANO root frame
# (+y up, +z forward). The helmet's own rails are left out as the client hides them (railink_hide.json).
# Run: python item/render_headsets.py <out dir>
import json, sys
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw
ARGS = sys.argv[1:]; sys.argv = sys.argv[:1]
import fit_profile as FP
import UnityPy
from fit_helmets import read_bin, qmat, WIN, SRC, ITEM
from split_items import M, G
from rail_islands import renderers
import headset_check as HC

fits = json.loads((ITEM / 'railink_fit.json').read_text()); heads = json.loads((ITEM / 'railink_headsets.json').read_text())
hide = json.loads((ITEM / 'railink_hide.json').read_text())


def F(f): return M(f['pos'], f['rot'], f['scale'][0])


def helmet(tpl):
    f = fits[tpl]; drop = {}
    for e in hide.get(f['shape'], []): drop.setdefault((e['renderer'], e['mesh']), set()).update(e['drop'])
    tris = []
    for go, mesh, T, lab in renderers(f['prefab']):
        d = set()
        for k, v in drop.items():
            if k[1] == mesh or k[0] == go: d |= v
        keep = np.array([i not in d for i in range(len(T))], bool)
        tris.append(T[keep])
    return np.vstack(tris)


def rails(f):
    out = []
    for name in ('fpano_railink_rails', 'fpano_railink_inside', 'fpano_railink_battery'):
        V, _, T = read_bin(SRC / f'{name}.bin'); P = V.copy()
        if name != 'fpano_railink_battery':
            side = np.clip((np.abs(P[:, 0]) - 0.045) / 0.03, 0, 1)
            P[:, 0] += np.where(P[:, 0] < 0, f['left'][0], f['right'][0]) * side
        else:
            P[:, 2] += f['back'][2]
        P = P @ (qmat(f['rot']) * f['scale'][0]).T + np.array(f['pos'])
        out.append(P[T])
    return np.vstack(out)


def headset_tris(hid):
    env = UnityPy.load(str(WIN / HC.items[hid]['_props']['Prefab']['path'])); T, world = FP.world_matrices(env)
    by_go = {t.m_GameObject.path_id: t for t in T.values()}
    mfs = {o.read().m_GameObject.path_id: o.read() for o in env.objects if o.type.name == 'MeshFilter'}
    out = []
    for o in env.objects:
        if o.type.name not in ('MeshRenderer', 'SkinnedMeshRenderer'): continue
        r = o.read(); go = r.m_GameObject.read()
        if 'LOD1' in go.m_Name or 'LOD2' in go.m_Name: continue
        m = r.m_Mesh.read() if o.type.name == 'SkinnedMeshRenderer' else (mfs[r.m_GameObject.path_id].m_Mesh.read() if r.m_GameObject.path_id in mfs else None)
        if m is None: continue
        V, Fc = [], []
        for l in m.export().splitlines():
            k = l.split()
            if k and k[0] == 'v': V.append([float(x) for x in k[1:4]])
            elif k and k[0] == 'f': Fc.append([int(c.split('/')[0]) - 1 for c in k[1:4]])
        if not Fc: continue
        V = np.array(V); V[:, 0] *= -1; W = world(by_go[r.m_GameObject.path_id])
        out.append((V @ W[:3, :3].T + W[:3, 3])[np.array(Fc)])
    return np.vstack(out)


def place(f, hid, tpl):
    p = heads[hid]['perHelmet'].get(tpl) or {**heads[hid]['onGroup'], 'scaleX': 1.0}
    m = F(f) @ M(p['pos'], p['rot']); m[:3, :3] /= np.cbrt(np.linalg.det(m[:3, :3]))
    return m @ np.diag([p.get('scaleX', 1.0), 1, 1, 1]) @ G


def bent(hid, tpl, T):
    """The headset's triangles (its local frame) with the arms bent as the client bends them (fit_headsets.py)."""
    p = heads[hid]['perHelmet'].get(tpl)
    if not p or 'bendAxis' not in p: return T
    from fit_headsets import bend
    return bend(T.reshape(-1, 3), p).reshape(T.shape)


def render(parts, view, path, size=900, box=None, title=''):
    """parts: [(tris (n,3,3), rgb)]; view: (yaw deg, pitch deg); box: (cx, cy, half) in view coords to zoom."""
    yaw, pitch = np.radians(view[0]), np.radians(view[1])
    Ry = np.array([[np.cos(yaw), 0, np.sin(yaw)], [0, 1, 0], [-np.sin(yaw), 0, np.cos(yaw)]])
    Rx = np.array([[1, 0, 0], [0, np.cos(pitch), -np.sin(pitch)], [0, np.sin(pitch), np.cos(pitch)]])
    R = Rx @ Ry; light = np.array([0.4, 0.7, 0.6]); light /= np.linalg.norm(light)
    allT, cols = [], []
    for T, c in parts:
        P = T @ R.T; n = np.cross(P[:, 1] - P[:, 0], P[:, 2] - P[:, 0]); n /= np.linalg.norm(n, axis=1, keepdims=True) + 1e-12
        sh = 0.35 + 0.65 * np.abs(n @ light)
        allT.append(P); cols.append(np.clip(np.array(c)[None] * sh[:, None], 0, 255))
    P = np.vstack(allT); C = np.vstack(cols)
    if box is None:
        xy = P[:, :, :2].reshape(-1, 2); cx, cy = (xy.min(0) + xy.max(0)) / 2; half = (xy.max(0) - xy.min(0)).max() / 2 * 1.08
    else: cx, cy, half = box
    s = size / (2 * half); order = np.argsort(P[:, :, 2].mean(1))   # far (small z: away from viewer at +z) first
    img = Image.new('RGB', (size, size), (34, 36, 40)); d = ImageDraw.Draw(img)
    for i in order:
        q = [((x - cx) * s + size / 2, size / 2 - (y - cy) * s) for x, y, _ in P[i]]
        d.polygon(q, fill=tuple(int(v) for v in C[i]))
    d.text((10, 10), title, fill=(255, 255, 255))
    img.save(path); return R


if __name__ == '__main__':
    OUT = Path(ARGS[0])
    OUT.mkdir(parents=True, exist_ok=True)
    CASES = [('5e4bfc1586f774264f7582d3', '5a16b9fffcdbcb0176308b34', 'TC800_RAC'),
             ('5e4bfc1586f774264f7582d3', '66b5f69ea7f72d197e70bcdb', 'TC800_ComTacV'),
             ('5f60b34a41e30a4ab12a6947', '66b5f6a28ca68c6461709ed8', 'Caiman_ComTacVI')]
    HEL, RAIL, HS = (150, 152, 158), (196, 172, 122), (70, 74, 70)
    for tpl, hid, name in CASES:
        f = fits[tpl]; Hm = helmet(tpl); Rl = rails(f); Hs = bent(hid, tpl, headset_tris(hid))
        W = place(f, hid, tpl); Hs = Hs @ W[:3, :3].T + W[:3, 3]
        p = heads[hid]['perHelmet'].get(tpl) or {}; sx = p.get('scaleX', 1.0)
        parts = [(Hm, HEL), (Rl, RAIL), (Hs, HS)]
        label = f"{name}  arms bent {p.get('bendNegX', 0) * 1000:+.1f} / {p.get('bendPosX', 0) * 1000:+.1f} mm" 
        render(parts, (-90, 0), OUT / f'{name}_left.png', title=label + '  left side')
        render(parts, (-135, 15), OUT / f'{name}_front_left.png', title=label + '  front-left')
        render(parts, (180, 10), OUT / f'{name}_back.png', title=label + '  back')
        # the left clip: headset vertices nearest the left rail, zoomed in the left-side view
        yaw = np.radians(-90); Ry = np.array([[np.cos(yaw), 0, np.sin(yaw)], [0, 1, 0], [-np.sin(yaw), 0, np.cos(yaw)]])
        Rv = Rl.reshape(-1, 3); Hv = Hs.reshape(-1, 3); L = Hv[:, 0] < 0
        near = Hv[L][np.argsort(np.min(np.linalg.norm(Hv[L][:, None, :] - Rv[None, ::40], axis=2), 1))[:200]].mean(0)
        c = Ry @ near
        render(parts, (-90, 0), OUT / f'{name}_left_clip.png', box=(c[0], c[1], 0.035), title=label + '  left clip close-up')
        print(name, 'done')
