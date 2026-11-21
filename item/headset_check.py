# Where the helmet headsets sit on the RAILINK rails: front and side views of the rails and each headset, on the EXFIL
# (the ComTacs' own helmet) and on bare rails (the rails item alone), in the F-PANO root frame.
# Run: python item/headset_check.py   -> item/fit_renders/headsets_*.png
import json, sys
import numpy as np
import UnityPy
from PIL import Image, ImageDraw
import fit_profile as FP
from fit_helmets import read_bin, qmat, SRC, ITEM, WIN
from split_items import G, M, HEADSETS, FAST

items = json.load(open(WIN.parents[2] / 'SPT_Runtime/SPT_Data/database/templates/items.json', encoding='utf-8'))


def meshes(path):
    """Vertices of the prefab's LOD0 renderers in its root frame."""
    env = UnityPy.load(str(WIN / path)); T, world = FP.world_matrices(env)
    by_go = {t.m_GameObject.path_id: t for t in T.values()}
    mfs = {o.read().m_GameObject.path_id: o.read() for o in env.objects if o.type.name == 'MeshFilter'}
    out = []
    for o in env.objects:
        if o.type.name not in ('MeshRenderer', 'SkinnedMeshRenderer'): continue
        r = o.read(); go = r.m_GameObject.read()
        if 'LOD1' in go.m_Name or 'LOD2' in go.m_Name: continue
        m = r.m_Mesh.read() if o.type.name == 'SkinnedMeshRenderer' else (mfs[r.m_GameObject.path_id].m_Mesh.read() if r.m_GameObject.path_id in mfs else None)
        if m is None: continue
        V = np.array([[float(x) for x in l.split()[1:4]] for l in m.export().splitlines() if l.startswith('v ')])
        if not len(V): continue
        V[:, 0] *= -1; W = world(by_go[r.m_GameObject.path_id]); out.append(V @ W[:3, :3].T + W[:3, 3])
    return np.vstack(out)


def slots(path):
    env = UnityPy.load(str(WIN / path)); T, world = FP.world_matrices(env)
    return {t.m_GameObject.read().m_Name: world(t) for t in T.values()}


def rails_pts(fit=None):
    info = json.loads((SRC / 'info_rails.json').read_text())
    P = np.vstack([read_bin(SRC / f'{n}.bin')[0] for n in ('fpano_railink_rails',)])
    if fit is None: return P
    side = np.clip((np.abs(P[:, 0]) - 0.045) / 0.03, 0, 1)
    P = P.copy(); P[:, 0] += np.where(P[:, 0] < 0, fit['left'][0], fit['right'][0]) * side
    sc = fit['scale'][0]; return P @ (qmat(fit['rot']) * sc).T + np.array(fit['pos'])


def tr(m, P): return P @ m[:3, :3].T + m[:3, 3]


fits = json.loads((ITEM / 'railink_fit.json').read_text())
heads = json.loads((ITEM / 'railink_headsets.json').read_text())
plates = []
for ids, tpl, path, slot in HEADSETS:
    S = slots(path); hroot_to_fp = np.linalg.inv(S['mod_nvg'] @ G)
    for hid in ids:
        H = meshes(items[hid]['_props']['Prefab']['path'])
        on_helmet = tr(hroot_to_fp @ S[slot] @ G, H)
        bare = heads[hid]['onRails']; bare_m = M(bare['pos'], bare['rot']) @ G   # rails root frame, rails at bare['fit']
        plates.append((hid, 'on ' + items[tpl]['_name'], rails_pts(fits[tpl]), on_helmet))
        plates.append((hid, 'bare rails (now)', rails_pts(fits[bare['fit']]), tr(bare_m, H)))
        if len(sys.argv) > 1:
            Sf = slots(FAST); f = fits['5a154d5cfcdbcb001a3b00da']
            new = np.linalg.inv(M(f['pos'], f['rot'], f['scale'][0])) @ np.linalg.inv(Sf['mod_nvg'] @ G) @ S[slot]
            new[:3, :3] /= np.cbrt(np.linalg.det(new[:3, :3]))
            plates.append((hid, 'bare rails (new)', rails_pts(), tr(new @ G, H)))
W, PX = 300, 1400                                    # panel size, pixels per metre
img = Image.new('RGB', (2 * W, len(plates) * W), 'white'); d = ImageDraw.Draw(img)
for i, (hid, what, R, H) in enumerate(plates):
    for j, (a, b, name, sgn) in enumerate(((0, 1, 'front x-y', 1), (2, 1, 'side z-y', 1))):
        ox, oy = j * W + W / 2, i * W + W / 2 + 30
        cx = (np.r_[R[:, a], H[:, a]].min() + np.r_[R[:, a], H[:, a]].max()) / 2
        for P, c in ((R, (0, 0, 0)), (H, (220, 0, 0))):
            for x, y in zip(P[::5, a], P[::5, b]): d.point((ox + (x - cx) * PX, oy - y * PX), c)
        d.text((j * W + 4, i * W + 4), items[hid]['_name'] + ' | ' + what + ' | ' + name, fill=(0, 0, 200))
img.save(ITEM / 'fit_renders/headsets.png')
