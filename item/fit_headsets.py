# Per rail helmet, where each helmet headset sits on the RAILINK rails, its arm clips seated in the rails' ARC channel
# as the game seats them in its own helmet's rails, and its arms bent (not the whole headset squeezed) to reach rails
# that sit closer together or further apart (user, 0.2.29: "这样卡住符合现实吗？" -> both: clips in the channel, arms bend).
#
# Seating: on its own helmet (RAC: FAST MT, ComTacs: EXFIL) the game puts the headset with its clips in the helmet's
# own ARC rails (the islands railink_hide.json drops). The clips are the headset's vertices within 3 mm of those rails;
# per side, the clips' median offset to the nearest rail point there is the seated relation. On every rail helmet the
# headset is moved (y, z) and each arm bent in x until the clips' offsets to the RAILINK rails match it, three rounds.
#
# The bend, for the client (in the headset root's local frame, the game's): with h = dot(v, bendAxis), w =
# smoothstep(clamp((top - h) / (top - clip), 0, 1)), a vertex moves by bendNegX * w along local x when v.x < 0, by
# bendPosX * w when v.x >= 0 (metres; + = toward +x). Above the band's top nothing moves; from the clips down (cups)
# all of a side moves rigidly. Writes "perHelmet" into item/railink_headsets.json: helmet template -> {pos, rot
# (fpano_railink local, its scaled units, as onGroup), scaleX: 1, bendAxis, top, clip, bendNegX, bendPosX}.
# Run: python item/fit_headsets.py   (after split_items has written onGroup)
import json, sys
import numpy as np
sys.argv = sys.argv[:1]
from split_items import M, G, helmet_slots
from rail_islands import renderers
import headset_check as HC
ITEMD = HC.ITEM
fits = json.loads((ITEMD / 'railink_fit.json').read_text()); heads = json.loads((ITEMD / 'railink_headsets.json').read_text())
hide = json.loads((ITEMD / 'railink_hide.json').read_text())
rail_helmets = json.loads((ITEMD.parent / 'server/db/items.json').read_text())['railHelmets']
OWN = {'5a16b9fffcdbcb0176308b34': ('5a154d5cfcdbcb001a3b00da', 'mod_equipment_001'),
       '66b5f69ea7f72d197e70bcdb': ('5e00c1ad86f774747333222c', 'mod_equipment_000'),
       '66b5f6a28ca68c6461709ed8': ('5e00c1ad86f774747333222c', 'mod_equipment_000')}


def F(f): return M(f['pos'], f['rot'], f['scale'][0])


def nearest(R, Q):
    return np.vstack([R[((Q[i:i + 300, None, :] - R[None]) ** 2).sum(-1).argmin(1)] for i in range(0, len(Q), 300)])


def own_rails(tpl):
    """The helmet's own side-rail triangles' corners (the islands the client hides), F-PANO root frame."""
    f = fits[tpl]; drop = {}
    for e in hide.get(f['shape'], []):                   # both sides' islands (one entry per side, same mesh)
        drop.setdefault((e['renderer'], e['mesh']), set()).update(e['drop'])
    pts = []
    for go, mesh, T, lab in renderers(f['prefab']):
        d = set().union(*[v for k, v in drop.items() if k[1] == mesh or k[0] == go]) if drop else set()
        if d: pts.append(T[sorted(d)].reshape(-1, 3))
    return np.vstack(pts)


def bend(H, b):
    h = H @ np.array(b['bendAxis']); w = np.clip((b['top'] - h) / (b['top'] - b['clip']), 0, 1); w = w * w * (3 - 2 * w)
    out = H.copy(); out[:, 0] += np.where(H[:, 0] < 0, b['bendNegX'], b['bendPosX']) * w
    return out


def world(f, pos, rot):
    m = F(f) @ M(pos, rot); m[:3, :3] /= np.cbrt(np.linalg.det(m[:3, :3])); return m @ G


def offsets(R, P):
    v = nearest(R, P) - P; L = P[:, 0] < 0
    return np.median(v[L], 0), np.median(v[~L], 0)


if __name__ == '__main__':
    meshes = {}
    for hid, (own, slot) in OWN.items():
        if hid not in heads: continue
        path = HC.items[hid]['_props']['Prefab']['path']
        H = meshes.setdefault(path, HC.meshes(path))
        S = helmet_slots(fits[own]['prefab'])
        W0 = np.linalg.inv(S['mod_nvg_world']) @ S['__root_' + slot] @ G      # the game's own pose on its own helmet
        R0 = own_rails(own)[::2]; P0 = HC.tr(W0, H)
        clip = np.linalg.norm(nearest(R0, P0) - P0, axis=1) < 0.003
        refL, refR = offsets(R0, P0[clip])
        # the bend's axis and levels, in the headset's local frame: world up at its own-helmet pose
        axis = W0[:3, :3].T @ np.array([0, 1.0, 0]); axis /= np.linalg.norm(axis)
        top = float((H @ axis).max()); clip_h = float(np.median(H[clip] @ axis))
        print(f'{HC.items[hid]["_name"]}: {int(clip.sum())} clip vertices on its own helmet\'s rails; band top {top:.3f}, clips {clip_h:.3f} along {axis.round(2)}')
        g = heads[hid]['onGroup']; per = {}
        for tpl in rail_helmets:
            f = fits[tpl]; R = HC.rails_pts(f)[::2]; pos = np.array(g['pos'], float)
            b = {'bendAxis': axis.tolist(), 'top': top, 'clip': clip_h, 'bendNegX': 0.0, 'bendPosX': 0.0}
            Rf = np.array(F(f))[:3, :3] / f['scale'][0]
            for _ in range(4):
                P = HC.tr(world(f, pos, g['rot']), bend(H, b)[clip]); oL, oR = offsets(R, P)
                dL, dR = oL - refL, oR - refR
                move = (dL + dR) / 2; move[0] = 0.0
                pos = pos + Rf.T @ move / f['scale'][0]
                b['bendNegX'] += float(dL[0]); b['bendPosX'] += float(dR[0])
            P = HC.tr(world(f, pos, g['rot']), bend(H, b)[clip]); oL, oR = offsets(R, P)
            print(f"  {f['shape']:24s} arms {b['bendNegX'] * 1000:+5.1f} / {b['bendPosX'] * 1000:+5.1f} mm  seat error left {np.linalg.norm(oL - refL) * 1000:4.1f} mm, right {np.linalg.norm(oR - refR) * 1000:4.1f} mm")
            per[tpl] = {'pos': pos.tolist(), 'rot': g['rot'], 'scaleX': 1.0, **b}
        heads[hid]['perHelmet'] = per
    (ITEMD / 'railink_headsets.json').write_text(json.dumps(heads, indent=1))
