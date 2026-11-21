# The helmets' own side rails, as removable pieces (user: "能改成能拆的吗？" then "那就只让它能够装在这 9 种头盔上面"):
# for the nine helmet shapes whose rails are separate mesh islands (fit_profile.py found them), per side the island
# whose side silhouette overlaps the fitted RAILINK rail best. Written to item/railink_hide.json, per shape: the
# renderer's GameObject name, its mesh's name, its triangle count, and the indices (into the mesh's triangle list, in
# the order the mesh stores them, all submeshes in turn) of the triangles the client drops while RAILINK rails are on
# that helmet. Also used by fit_depth.py so the RAILINK rails rest on the shell where the own rails were.
# Run: python item/rail_islands.py
import json
import numpy as np
import UnityPy
import fit_profile as FP
from fit_helmets import helmets, read_bin, qmat, SRC, ITEM, WIN

SHAPES = ['helmet_opscore_fast', 'helmet_hops_core_fast', 'helmet_crye_airframe', 'helmet_mich2001', 'helmet_mich2002',
          'helmet_team_wendy_exfil', 'helmet_tc800', 'helmet_galvion_caiman', 'helmet_tor_2']
MIN_IOU = 0.35


def renderers(path):
    """Per enabled LOD0 renderer: (GameObject name, mesh name, triangles in the F-PANO root frame, island labels)."""
    env = UnityPy.load(str(WIN / path)); T, world = FP.world_matrices(env)
    slot = next(world(t) for t in T.values() if t.m_GameObject.read().m_Name == 'mod_nvg')
    Rx = np.eye(4); Rx[:3, :3] = qmat((np.sin(np.pi / 4), 0, 0, np.cos(np.pi / 4))); Hi = np.linalg.inv(slot @ Rx)
    by_go = {t.m_GameObject.path_id: t for t in T.values()}
    mfs = {o.read().m_GameObject.path_id: o.read() for o in env.objects if o.type.name == 'MeshFilter'}
    out = []
    for o in env.objects:
        if o.type.name != 'MeshRenderer': continue
        r = o.read(); go = r.m_GameObject.read()
        if not r.m_Enabled or 'LOD1' in go.m_Name or 'LOD2' in go.m_Name or r.m_GameObject.path_id not in mfs: continue
        m = mfs[r.m_GameObject.path_id].m_Mesh.read(); V, F = [], []
        for l in m.export().splitlines():
            k = l.split()
            if k and k[0] == 'v': V.append([float(x) for x in k[1:4]])
            elif k and k[0] == 'f': F.append([int(c.split('/')[0]) - 1 for c in k[1:4]])
        if not F: continue
        V = np.array(V); V[:, 0] *= -1; F = np.array(F)
        M = Hi @ world(by_go[r.m_GameObject.path_id]); W = V @ M[:3, :3].T + M[:3, 3]
        key = np.unique(np.round(W * 1e4).astype(np.int64), axis=0, return_inverse=True)[1].ravel()
        par = np.arange(key.max() + 1)
        def find(x):
            while par[x] != x: par[x] = par[par[x]]; x = par[x]
            return x
        for t in F:
            rt = find(key[t[0]])
            for c in t[1:]: par[find(key[c])] = rt
        lab = np.array([find(key[t[0]]) for t in F])
        out.append((go.m_Name, m.m_Name, W[F], lab))
    return out


def main():
    manual = json.loads((ITEM / 'railink_manual.json').read_text())
    rv, _, rt = read_bin(SRC / 'fpano_railink_rails.bin'); RT = rv[rt]; RT = RT[RT[:, :, 0].mean(1) > 0.04]
    allP = np.vstack([read_bin(SRC / f'{n}.bin')[0] for n in ('fpano_railink_rails', 'fpano_railink_inside', 'fpano_railink_battery')])
    c3 = (allP.min(0) + allP.max(0)) / 2; FP.CENTRE = np.array([c3[2], c3[1]])
    res = 0.001; A = FP.pts(FP.mask(RT, res / 2), res / 2)
    shapes = {}
    for tpl, path in sorted(helmets().items()): shapes.setdefault(path.split('/')[-2], path)
    table = {}
    for key in SHAPES:
        p = manual[key]
        z, y = FP.place(A, FP.CENTRE, p['tz'], p['ty'], p['pitch'], p['s'])
        (z0, _), (_, y1) = FP.BOX
        F = np.zeros_like(FP.mask(RT[:1], res)); r = ((y1 - y) / res).astype(int); c = ((z - z0) / res).astype(int)
        ok = (r >= 0) & (r < F.shape[0]) & (c >= 0) & (c < F.shape[1]); F[r[ok], c[ok]] = True
        entry = []
        for go, mesh, T, lab in renderers(shapes[key]):
            cx = T[:, :, 0].mean(1)
            for sgn, side in ((-1, 'L'), (1, 'R')):
                best = (0.0, None)
                for l in np.unique(lab[(np.sign(cx) == sgn) & (np.abs(cx) > 0.055)]):
                    sel = (lab == l) & (np.sign(cx) == sgn)
                    zs = T[sel][:, :, 2]
                    if sel.sum() < 20 or zs.max() - zs.min() < 0.06: continue
                    B = FP.mask(T[sel], res)
                    v = (B & F).sum() / max(1, (B | F).sum())
                    if v > best[0]: best = (v, l)
                if best[1] is not None and best[0] >= MIN_IOU:
                    idx = np.nonzero((lab == best[1]) & (np.sign(cx) == sgn))[0]
                    entry.append({'renderer': go, 'mesh': mesh, 'triangles': int(len(T)), 'side': side, 'iou': round(float(best[0]), 3), 'drop': idx.tolist()})
                    print(f'{key:28s} {side}: {go} island of {len(idx)} triangles, overlap {best[0]:.2f}')
                else:
                    print(f'{key:28s} {side}: no rail island (best overlap {best[0]:.2f})')
        table[key] = entry
    (ITEM / 'railink_hide.json').write_text(json.dumps(table))


if __name__ == '__main__':
    main()
