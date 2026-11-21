# The Peltor ComTac V / VI as the user set them on the RAILINK rails (2026-11-21, from photos and marked previews):
#  - the mount (hub + bar, one closed piece per side) moved rigidly onto the RAILINK's front run, 1.2 cm ahead of the
#    bend's start (z -0.1285 on the FAST shape; the front of the run is left for a light): carried over from how the
#    game seats it on its own (EXFIL) rail: that rail's face frame at the mount (along the run, outward normal,
#    across) mapped onto the RAILINK face frame there (outer-envelope plane fit; along: the straighter rail edge;
#    the own rail's run averaged over both sides);
#  - the cup with its buttons and boom mic: moved only (no turn), its centre 5.45 cm behind the hub, its top 12 mm
#    under the rail's lower edge there;
#  - the arm rods and wires that run up to the mount: their top follows the mount, their bottom the cup, smoothly;
#  - the neck band: its ends follow the cups, then laid around the outside of the helmet's back, the rails and the
#    battery, 6 mm off.
# The game's ComTac meshes are not readable at run time, so the shapes are baked here, per rail helmet, as whole
# meshes (positions, normals, uvs, the two submeshes' triangles) in the headset ROOT's local frame for the root at
# its own-helmet place relative to the helmet's root (railink_headsets.json "pos"/"rot", frame "helmet root"); the
# client swaps the ComTac's MeshFilter meshes for these while it is on the rails. LOD0 only (LOD1 shows it too).
# Writes item/Unity/Assets/Source/headsets/<mesh>__<helmet>.bin and item/railink_headset_meshes.json.
# Run: python item/bake_headsets.py [helmet template ...]
import json, sys
from pathlib import Path
import numpy as np
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler
ARGS = sys.argv[1:]; sys.argv = sys.argv[:1]
import os
DEBUG = Path(os.environ['BAKE_DEBUG']) if os.environ.get('BAKE_DEBUG') else None
import fit_profile as FP
import fit_headsets as FH
import render_headsets as RH
from fit_helmets import read_bin, SRC, qmat, WIN, ITEM
from split_items import M, G, helmet_slots

OUT = ITEM / 'Unity/Assets/Source/headsets'; OUT.mkdir(parents=True, exist_ok=True)
items = json.load(open(WIN.parents[2] / 'SPT_Runtime/SPT_Data/database/templates/items.json', encoding='utf-8'))
fits = json.loads((ITEM / 'railink_fit.json').read_text()); heads = json.loads((ITEM / 'railink_headsets.json').read_text())
rail_helmets = json.loads((ITEM.parent / 'server/db/items.json').read_text())['railHelmets']
EXFIL = '5e00c1ad86f774747333222c'; FAST = '5a154d5cfcdbcb001a3b00da'
COMTACS = ['66b5f69ea7f72d197e70bcdb', '66b5f6a28ca68c6461709ed8']
SPOT_Z_FAST = -0.1285          # the mount's place along the run, on the FAST shape (moved per helmet through the fit)
CUP_BACK = 0.0545; CUP_GAP = 0.012; BAND_OFF = 0.006


def F(f): return M(f['pos'], f['rot'], f['scale'][0])


def rails_only(f):
    v, _, t = read_bin(SRC / 'fpano_railink_rails.bin'); side = np.clip((np.abs(v[:, 0]) - 0.045) / 0.03, 0, 1); v = v.copy()
    v[:, 0] += np.where(v[:, 0] < 0, f['left'][0], f['right'][0]) * side
    return v @ (qmat(f['rot']) * f['scale'][0]).T + np.array(f['pos'])


def face_frame(pts, at, side, r=0.016):
    q = pts[(np.sign(pts[:, 0]) == side) & (np.abs(pts[:, 1] - at[1]) < r) & (np.abs(pts[:, 2] - at[2]) < r) & (np.abs(pts[:, 0]) > 0.06)]
    cells = {}
    for v in q:
        k = (int((v[1] - at[1]) // 0.002), int((v[2] - at[2]) // 0.002))
        if k not in cells or abs(v[0]) > abs(cells[k][0]): cells[k] = v
    E = np.array(list(cells.values())); E = E[np.abs(E[:, 0]) >= np.median(np.abs(E[:, 0]))]
    coef = np.linalg.lstsq(np.c_[np.ones(len(E)), E[:, 1], E[:, 2]], E[:, 0], rcond=None)[0]
    n = np.array([1.0, -coef[1], -coef[2]]) * side; n /= np.linalg.norm(n)
    zz, lows, highs = [], [], []
    for z2 in np.linspace(at[2] - r, at[2] + r, 9):
        w = pts[(np.sign(pts[:, 0]) == side) & (np.abs(pts[:, 0]) > 0.06) & (np.abs(pts[:, 2] - z2) < 0.002) & (np.abs(pts[:, 1] - at[1]) < 0.04)]
        if len(w) > 3: zz.append(z2); lows.append(w[:, 1].min()); highs.append(w[:, 1].max())
    fr = [np.polyfit(zz, e, 1, full=True) for e in (lows, highs)]
    k = min(fr, key=lambda x: x[1][0] if len(x[1]) else 0)[0][0]
    t = np.array([0.0, -k, -1.0]); t -= n * (t @ n); t /= np.linalg.norm(t)
    c = np.array([coef[0] + coef[1] * at[1] + coef[2] * at[2], at[1], at[2]])
    return c, np.column_stack([t, n, np.cross(t, n)])


def rail_band(RV, z, side):
    q = RV[(np.sign(RV[:, 0]) == side) & (np.abs(RV[:, 0]) > 0.07) & (np.abs(RV[:, 2] - z) < 0.002)]
    return float(np.percentile(np.abs(q[:, 0]), 90)), float((q[:, 1].min() + q[:, 1].max()) / 2), float(q[:, 1].min())


def mesh_of(hid):
    """the headset's LOD0 mesh: (name, positions in the root's frame, normals, uv0, [triangles per submesh])"""
    env = UnityPy.load(str(WIN / items[hid]['_props']['Prefab']['path'])); T, world = FP.world_matrices(env)
    by_go = {t.m_GameObject.path_id: t for t in T.values()}
    for o in env.objects:
        if o.type.name != 'MeshFilter': continue
        mf = o.read(); go = mf.m_GameObject.read()
        if 'LOD0' not in go.m_Name: continue
        m = mf.m_Mesh.read(); h = MeshHandler(m); h.process()
        W = world(by_go[mf.m_GameObject.path_id])
        nv = len(h.m_Vertices) if np.ndim(h.m_Vertices) > 1 else len(h.m_Vertices) // 3
        V = np.array(h.m_Vertices, float).reshape(nv, -1)[:, :3]; N = np.array(h.m_Normals, float).reshape(nv, -1)[:, :3]
        V = V @ W[:3, :3].T + W[:3, 3]; N = N @ W[:3, :3].T
        MESH_NODE[m.m_Name] = W          # the LOD node's place under the root (the ComTac's is turned -90 deg about x)
        return m.m_Name, V, N, np.array(h.m_UV0, float).reshape(nv, -1)[:, :2], [np.array(t, np.int64).reshape(-1, 3) for t in h.get_triangles()]
    raise SystemExit(f'{hid}: no LOD0 mesh')


MESH_NODE = {}
SEAT = {}


def islands(V, tris):
    key = np.unique(np.round(V * 1e5).astype(np.int64), axis=0, return_inverse=True)[1].ravel()
    par = np.arange(key.max() + 1)
    def fnd(x):
        while par[x] != x: par[x] = par[par[x]]; x = par[x]
        return x
    for t in tris:
        r = fnd(key[t[0]])
        for c in t[1:]: par[fnd(key[c])] = r
    lab = np.array([fnd(k) for k in key]); return np.unique(lab, return_inverse=True)[1]


def xf(T, P): return P @ T[:3, :3].T + T[:3, 3]


def bake(hid, tpl, mesh, own):
    name, V, N, UV, subs = mesh
    tris = np.vstack(subs); lab = islands(V, tris); n_isl = lab.max() + 1
    So = own['slots']; Wslot = own['slot']                      # the headset's slot pose in its own helmet's root
    W0 = np.linalg.inv(So['mod_nvg_world']) @ Wslot @ G       # worn on its own helmet (the game's seat on its rail)
    f = fits[tpl]; Sh = helmet_slots(f['prefab']) if tpl not in own['cache'] else own['cache'][tpl]
    own['cache'][tpl] = Sh
    Wh = np.linalg.inv(Sh['mod_nvg_world']) @ Wslot @ G       # worn on this helmet (cups on the ears)
    P0 = xf(W0, V); Ph = xf(Wh, V)
    cent = np.array([Ph[lab == i].mean(0) for i in range(n_isl)]); cnt = np.bincount(lab)
    span = np.array([np.ptp(Ph[lab == i][:, 0]) for i in range(n_isl)])
    band = int(np.argmax(np.where(span > 0.15, cnt, -1)))
    mounts = {s: int(np.argmax(np.where((np.sign(cent[:, 0]) == s) & (cent[:, 1] > 0.02), cnt, -1))) for s in (-1, 1)}
    RV = rails_only(f); Rl = RH.rails(f)
    zs = xf(F(f) @ np.linalg.inv(F(fits[FAST])), np.array([[0.1, 0.05, SPOT_Z_FAST]]))[0, 2]
    own_rail = own['rail']
    Pn = Ph.copy(); Nn = xf(np.c_[np.r_[Wh[:3, :3], np.zeros((1, 3))], [0, 0, 0, 1]], N) if False else N @ Wh[:3, :3].T
    TC = {}
    for s in (-1, 1):
        mi = lab == mounts[s]; Mv = P0[mi]
        # the bar's own frame from its back plate: the mount's vertices within 4 mm of its deepest point along the
        # outward direction (x * side); long axis and plate normal by PCA of those, "across" pointing up
        n0 = np.array([float(s), 0.0, 0.0]); dep = Mv @ n0; plate = Mv[dep < dep.min() + 0.004]; pcen = plate.mean(0)
        u_, sv_, vt_ = np.linalg.svd(plate - pcen, full_matrices=False)
        e3 = vt_[2] * np.sign(vt_[2] @ n0); e1 = vt_[0] - e3 * (vt_[0] @ e3); e1 /= np.linalg.norm(e1)
        e2 = np.cross(e3, e1)
        if e2[1] < 0: e1 = -e1; e2 = -e2                          # across points up: the hub stays above the bar
        Fb = np.column_stack([e1, e3, e2])
        # the RAILINK there: the face plane (outer envelope) and the run along the channel's middle (the mean of the
        # top and bottom edges' slopes; the top edge alone left it 5 deg flat)
        face, mid, _ = rail_band(RV, zs, s)
        cn, Fn = face_frame(RV, np.array([s * face, mid, zs]), s); nR = Fn[:, 1]
        zz = np.linspace(zs + 0.02, zs - 0.02, 9); tops, lows = [], []
        for z2 in zz:
            q = RV[(np.sign(RV[:, 0]) == s) & (np.abs(RV[:, 0]) > 0.06) & (np.abs(RV[:, 2] - z2) < 0.002) & (np.abs(RV[:, 1] - mid) < 0.04)]
            tops.append(q[:, 1].max()); lows.append(q[:, 1].min())
        k = (np.polyfit(zz, tops, 1)[0] + np.polyfit(zz, lows, 1)[0]) / 2
        tR = np.array([0.0, -k, -1.0]); tR -= nR * (tR @ nR); tR /= np.linalg.norm(tR)
        aR = np.cross(nR, tR)
        if aR[1] < 0: tR = -tR; aR = -aR
        FR = np.column_stack([tR, nR, aR])
        Rm = FR @ Fb.T
        Ta = np.eye(4); Ta[:3, :3] = Rm; Ta[:3, 3] = cn - Rm @ pcen                # the plate's centre on the face
        Pn[mi] = xf(Ta, Mv); Nn[mi] = (N[mi] @ W0[:3, :3].T) @ Rm.T
        SEAT[s] = (float(np.degrees(np.arccos(np.clip(abs(e1 @ Fb[:, 0]), 0, 1)))), len(plate), sv_[:2].round(4).tolist())
        hub = xf(Ta, Mv.mean(0)[None])[0]
        side = [i for i in range(n_isl) if np.sign(cent[i, 0]) == s and i not in (band, mounts[s])]
        cup_i = max(side, key=lambda i: cnt[i]); cupQ = Ph[lab == cup_i]; cup_top = cupQ[:, 1].max()
        cz = hub[2] - CUP_BACK; _, _, low = rail_band(RV, cz, s)
        Tc = np.eye(4); Tc[1, 3] = low - CUP_GAP - cup_top; Tc[2, 3] = cz - cupQ[:, 2].mean(); TC[s] = Tc
        hub0_h = Ph[mi].mean(0)
        for i in side:
            k = lab == i; Q = Ph[k]; Qc = xf(Tc, Q)
            if Q[:, 1].max() > cup_top + 0.008:                  # a rod or wire up to the mount: top with the mount
                Qa = xf(Ta, P0[k])
                t = np.clip((Q[:, 1] - cup_top) / max(1e-6, hub0_h[1] - cup_top), 0, 1); t = t * t * (3 - 2 * t)
                Pn[k] = Qc + (Qa - Qc) * t[:, None]
                na = (N[k] @ W0[:3, :3].T) @ Rm.T; nh = N[k] @ Wh[:3, :3].T; nn = nh + (na - nh) * t[:, None]
                Nn[k] = nn / np.linalg.norm(nn, axis=1, keepdims=True)
            else:
                Pn[k] = Qc
    # the neck band: ends with the cups, laid outside the helmet, the rails and the battery
    k = lab == band; Q = Ph[k].copy()
    for s in (-1, 1):
        w = np.clip(s * Q[:, 0] / 0.09, 0, 1); w = w * w * (3 - 2 * w); Q[:, 1:] += w[:, None] * TC[s][1:3, 3]
    Hm = own['helmets'].setdefault(tpl, RH.helmet(tpl))
    # pushed out sideways only (about a vertical axis through the helmet), at its own height, past whatever of the
    # shell, rails and battery is at that height in that direction (a push from the helmet's centre lifted it over
    # the rails)
    Hv = np.vstack([Hm.reshape(-1, 3)[::3], Rl.reshape(-1, 3)[::2]]); c0 = Hm.reshape(-1, 3).mean(0)
    hd = (Hv - c0)[:, [0, 2]]; hr = np.linalg.norm(hd, axis=1); hu = hd / hr[:, None]; hy = Hv[:, 1]
    d = (Q - c0)[:, [0, 2]]; r = np.linalg.norm(d, axis=1); u = d / r[:, None]
    near = ((u @ hu.T) > np.cos(np.radians(5))) & (np.abs(Q[:, 1][:, None] - hy[None]) < 0.006)
    rs = np.where(near.any(1), np.where(near, hr[None], 0).max(1), 0); out = rs + BAND_OFF
    Qo = Q.copy(); grow = np.maximum(out - r, 0)
    Qo[:, 0] += u[:, 0] * grow; Qo[:, 2] += u[:, 1] * grow; Pn[k] = Qo
    # back to the root's frame at its helmet-root place on this helmet
    # back to the MESH's own frame: the root at its helmet-root place on this helmet, then the LOD node under it (the
    # client swaps the mesh on that node; 0.2.31 wrote the root's frame and the node's -90 deg turned it over in game)
    Wm = Wh @ MESH_NODE[name]; Wi = np.linalg.inv(Wm)
    L = xf(Wi, Pn); NL = Nn @ Wi[:3, :3].T; NL /= np.linalg.norm(NL, axis=1, keepdims=True)
    assert np.allclose(xf(Wm, L), Pn, atol=1e-6)                 # root x node x mesh lands where the preview has it
    return L, NL, (Hm, Rl, Pn, tris)


def write_bin(path, V, N, UV, subs):
    with open(path, 'wb') as f:
        np.array([len(V), sum(len(s) for s in subs), len(subs)], np.int32).tofile(f)
        V.astype(np.float32).tofile(f); N.astype(np.float32).tofile(f); UV.astype(np.float32).tofile(f)
        for s in subs: np.array([s.size], np.int32).tofile(f); s.astype(np.int32).tofile(f)


if __name__ == '__main__':
    targets = ARGS or rail_helmets
    index = json.loads((ITEM / 'railink_headset_meshes.json').read_text()) if (ITEM / 'railink_headset_meshes.json').exists() and ARGS else {}
    So = helmet_slots(fits[EXFIL]['prefab'])
    for hid in COMTACS:
        mesh = mesh_of(hid); h = heads[hid]
        own = {'slots': So, 'slot': M(h['pos'], h['rot']), 'rail': FH.own_rails(EXFIL), 'cache': {}, 'helmets': {}}
        entry = index.setdefault(hid, {'mesh': mesh[0], 'helmets': {}})
        done = {}
        for tpl in targets:
            fkey = json.dumps(fits[tpl], sort_keys=True)
            if fkey in done: entry['helmets'][tpl] = done[fkey]; continue
            L, NL, dbg = bake(hid, tpl, mesh, own)
            asset = f'{mesh[0]}__{fits[tpl]["shape"]}_{tpl[-4:]}'
            write_bin(OUT / f'{asset}.bin', L, NL, mesh[3], mesh[4])
            entry['helmets'][tpl] = done[fkey] = asset
            np.save(DEBUG / f'bake_debug_{asset}.npy', dbg[2]) if DEBUG else None
            print(f'{items[hid]["_name"]} on {fits[tpl]["shape"]} ({tpl[-4:]}): {asset}')
        entry['bare'] = entry['helmets'].get(EXFIL)
    (ITEM / 'railink_headset_meshes.json').write_text(json.dumps(index, indent=1))
