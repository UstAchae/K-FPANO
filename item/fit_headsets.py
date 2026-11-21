# Per rail helmet, where each helmet headset sits on the RAILINK rails so its two arm clips meet them as on its own
# helmet (user, 0.2.28: "这个耳机和 RALINK 还是没接触上"). The rails' sides sit closer together on narrow shells
# (TC 800, AirFrame, MICH), so the headset placed by onGroup alone stood up to 12 mm out. Its clips: the vertices within
# 3 mm of the rails on its own helmet (RAC: FAST MT; ComTacs: EXFIL). On each helmet, per side, the clips' offset to the
# nearest rail point minus that on the own helmet is the move that side needs; the headset is moved by their mean and
# squeezed or stretched in x (about its middle) by their difference, three rounds. Writes "perHelmet" into
# item/railink_headsets.json: helmet template -> {pos, rot (fpano_railink local, its scaled units, as onGroup), scaleX}.
# Run: python item/fit_headsets.py   (after split_items has written onGroup)
import json, sys
import numpy as np
sys.argv = sys.argv[:1]
from split_items import M, G
from prepare import mat2quat
import headset_check as HC
ITEMD = HC.ITEM
fits = json.loads((ITEMD / 'railink_fit.json').read_text()); heads = json.loads((ITEMD / 'railink_headsets.json').read_text())
rail_helmets = json.loads((ITEMD.parent / 'server/db/items.json').read_text())['railHelmets']
OWN = {'5a16b9fffcdbcb0176308b34': '5a154d5cfcdbcb001a3b00da', '66b5f69ea7f72d197e70bcdb': '5e00c1ad86f774747333222c',
       '66b5f6a28ca68c6461709ed8': '5e00c1ad86f774747333222c'}


def F(f): return M(f['pos'], f['rot'], f['scale'][0])


def nearest(R, Q):
    return np.vstack([R[((Q[i:i + 300, None, :] - R[None]) ** 2).sum(-1).argmin(1)] for i in range(0, len(Q), 300)])


def world(f, pos, rot, sx):
    """The headset's root in the F-PANO root frame on that helmet: group x slot node (scale x by sx) x the game's G."""
    m = F(f) @ M(pos, rot); m[:3, :3] /= np.cbrt(np.linalg.det(m[:3, :3]))
    return m @ np.diag([sx, 1, 1, 1]) @ G


def offsets(R, P):
    v = nearest(R, P) - P; L = P[:, 0] < 0
    return np.median(v[L], 0), np.median(v[~L], 0)


meshes = {}
for hid, own in OWN.items():
    if hid not in heads: continue
    H = meshes.setdefault(HC.items[hid]['_props']['Prefab']['path'], HC.meshes(HC.items[hid]['_props']['Prefab']['path']))
    g = heads[hid]['onGroup']; f0 = fits[own]
    R0 = HC.rails_pts(f0)[::2]; P0 = HC.tr(world(f0, g['pos'], g['rot'], 1.0), H)
    clip = np.linalg.norm(nearest(R0, P0) - P0, axis=1) < 0.003
    Hc = H[clip]; refL, refR = offsets(R0, P0[clip])
    per = {}
    for tpl in rail_helmets:
        f = fits[tpl]; R = HC.rails_pts(f)[::2]; pos = np.array(g['pos'], float); sx = 1.0
        Rf = (np.array(F(f))[:3, :3]) / f['scale'][0]
        for _ in range(3):
            W = world(f, pos, g['rot'], sx); P = HC.tr(W, Hc); oL, oR = offsets(R, P)
            dL, dR = oL - refL, oR - refR
            half = (np.median(P[P[:, 0] >= 0, 0]) - np.median(P[P[:, 0] < 0, 0])) / 2
            sx *= 1 + ((dR[0] - dL[0]) / 2) / half
            move = (dL + dR) / 2; move[0] = (dL[0] + dR[0]) / 2
            pos = pos + Rf.T @ move / f['scale'][0]
        W = world(f, pos, g['rot'], sx); oL, oR = offsets(R, HC.tr(W, Hc))
        print(f"{hid[-4:]} {f['shape']:24s} scaleX {sx:.3f}  left {np.linalg.norm(oL - refL) * 1000:4.1f} mm, right {np.linalg.norm(oR - refR) * 1000:4.1f} mm from own-helmet contact")
        per[tpl] = {'pos': pos.tolist(), 'rot': g['rot'], 'scaleX': float(sx)}
    heads[hid]['perHelmet'] = per
(ITEMD / 'railink_headsets.json').write_text(json.dumps(heads, indent=1))
