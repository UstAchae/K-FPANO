# Fits the RAILINK rails and battery (the F-PANO's fpano_railink group) to each helmet that can carry the F-PANO
# (user: "导轨现在和有些头盔的位置没对上...先试下让它对齐吧"). The rails are one rigid model made for the Ops-Core FAST SF
# shell; on other shells they float or sink. For each vanilla helmet whose mod_nvg slot takes the GPNVG-18 (so the
# F-PANO), its prefab is read from the game's bundle: its meshes in its root frame and its mod_nvg node. The F-PANO's
# root sits on that node turned +90 deg about X (as the game turns every attached item). The rails' helmet-facing
# points (normals toward the shell) are fitted onto the shell's outer surface: rotation, translation and one scale
# (0.85..1.2), point-to-plane, iteratively, with nothing of the rails left inside the shell. The result, per helmet
# template id, is the group node's local transform under the F-PANO root (item/railink_fit.json), which the client
# sets when the F-PANO hangs on that helmet's mod_nvg.
# Run: python item/fit_helmets.py   (writes item/railink_fit.json and item/fit_renders/*.obj)
import json, sys
from pathlib import Path
import numpy as np
import UnityPy

ITEM = Path(__file__).resolve().parent
GAME = Path(r'D:/games/SPT-4.1.6')
WIN = GAME / 'EscapeFromTarkov_Data/StreamingAssets/Windows'
SRC = ITEM / 'Unity/Assets/Source/fde'
GP = '5c0558060db834001b735271'
HEAD = '5a341c4086f77401f2541505'
TRIS = []  # the last prefab's triangles, for the check renders
RAILINK_NODES = ['fpano_railink_rails', 'fpano_railink_inside', 'fpano_railink_battery']


def qmat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def mat2quat(R):
    U, _, Vt = np.linalg.svd(R); R = U @ Vt
    w = np.sqrt(max(0.0, 1 + R[0, 0] + R[1, 1] + R[2, 2])) / 2
    x = np.sqrt(max(0.0, 1 + R[0, 0] - R[1, 1] - R[2, 2])) / 2
    y = np.sqrt(max(0.0, 1 - R[0, 0] + R[1, 1] - R[2, 2])) / 2
    z = np.sqrt(max(0.0, 1 - R[0, 0] - R[1, 1] + R[2, 2])) / 2
    x = np.copysign(x, R[2, 1] - R[1, 2]); y = np.copysign(y, R[0, 2] - R[2, 0]); z = np.copysign(z, R[1, 0] - R[0, 1])
    q = np.array([x, y, z, w]); return q / np.linalg.norm(q)


def read_bin(path):
    with open(path, 'rb') as f:
        nv, nt, ns = np.fromfile(f, np.int32, 3)
        V = np.fromfile(f, np.float32, nv * 3).reshape(-1, 3); N = np.fromfile(f, np.float32, nv * 3).reshape(-1, 3)
        np.fromfile(f, np.float32, nv * 2); ni = np.fromfile(f, np.int32, 1)[0]; T = np.fromfile(f, np.int32, ni).reshape(-1, 3)
    return V.astype(float), N.astype(float), T


def helmets():
    items = json.load(open(GAME / 'SPT_Runtime/SPT_Data/database/templates/items.json', encoding='utf-8'))
    out = {}
    for i, it in items.items():
        if it.get('_parent') != HEAD: continue
        for s in (it.get('_props') or {}).get('Slots') or []:
            if s['_name'] == 'mod_nvg' and any(GP in (f.get('Filter') or []) for f in s['_props'].get('filters') or []):
                out[i] = it['_props']['Prefab']['path']
    return out


def prefab(path):
    """(points, normals) of the prefab's mesh surfaces in its root frame (area-weighted samples), and the
    mod_nvg node's world matrix (R, t)."""
    env = UnityPy.load(str(WIN / path))
    T = {o.path_id: o.read() for o in env.objects if o.type.name == 'Transform'}
    by_go = {t.m_GameObject.path_id: t for t in T.values()}
    def world(t):
        R, p, s = np.eye(3), np.zeros(3), np.ones(3)
        chain = []
        while True:
            chain.append(t)
            if not t.m_Father or t.m_Father.path_id == 0: break
            t = T[t.m_Father.path_id]
        M = np.eye(4)
        for t in reversed(chain):
            q = t.m_LocalRotation; lp = t.m_LocalPosition; ls = t.m_LocalScale
            L = np.eye(4); L[:3, :3] = qmat((q.x, q.y, q.z, q.w)) * np.array([ls.x, ls.y, ls.z]); L[:3, 3] = [lp.x, lp.y, lp.z]
            M = M @ L
        return M
    slot = next((world(t) for t in T.values() if t.m_GameObject.read().m_Name == 'mod_nvg'), None)
    P, N = [], []
    TRIS.clear()
    for o in env.objects:
        if o.type.name != 'MeshRenderer': continue
        r = o.read()
        if not r.m_Enabled: continue
        tr = by_go[r.m_GameObject.path_id]
        mf = next((c for c in env.objects if c.type.name == 'MeshFilter' and c.read().m_GameObject.path_id == r.m_GameObject.path_id), None)
        if mf is None: continue
        m = mf.read().m_Mesh.read()
        V, F = [], []
        for l in m.export().splitlines():
            k = l.split()
            if not k: continue
            if k[0] == 'v': V.append([float(x) for x in k[1:4]])
            elif k[0] == 'f': F.append([int(c.split('/')[0]) - 1 for c in k[1:4]])
        if not F: continue
        V = np.array(V); V[:, 0] *= -1; F = np.array(F)[:, ::-1]
        M = world(tr); W = V @ M[:3, :3].T + M[:3, 3]
        A, B, C = W[F[:, 0]], W[F[:, 1]], W[F[:, 2]]
        n = np.cross(B - A, C - A); area = np.linalg.norm(n, axis=1) / 2
        ok = area > 1e-10; A, B, C, n, area = A[ok], B[ok], C[ok], n[ok], area[ok]
        n /= np.linalg.norm(n, axis=1, keepdims=True)
        k = np.maximum(1, np.round(area / 2e-6).astype(int))          # about one sample per 2 mm2
        idx = np.repeat(np.arange(len(A)), k)
        rng = np.random.default_rng(0); u = rng.random((len(idx), 2)); flip = u.sum(1) > 1; u[flip] = 1 - u[flip]
        P.append(A[idx] + u[:, :1] * (B[idx] - A[idx]) + u[:, 1:] * (C[idx] - A[idx])); N.append(n[idx])
        TRIS.append(np.stack([A, B, C], 1))
    return np.vstack(P), np.vstack(N), slot


class Grid:
    """Nearest-point lookup over a fixed point set (uniform hash grid, 1 cm cells, 27-cell search)."""
    def __init__(self, P, cell=0.01):
        self.P, self.c = P, cell
        keys = np.floor(P / cell).astype(np.int64)
        self.cells = {}
        for i, k in enumerate(map(tuple, keys)): self.cells.setdefault(k, []).append(i)
        self.cells = {k: np.array(v) for k, v in self.cells.items()}
    def nearest(self, Q):
        out = np.full(len(Q), -1); best = np.full(len(Q), np.inf)
        keys = np.floor(Q / self.c).astype(np.int64)
        for j, k in enumerate(map(tuple, keys)):
            cand = [self.cells.get((k[0] + a, k[1] + b, k[2] + c)) for a in (-1, 0, 1) for b in (-1, 0, 1) for c in (-1, 0, 1)]
            cand = [x for x in cand if x is not None]
            if not cand: continue
            cand = np.concatenate(cand); d = ((self.P[cand] - Q[j]) ** 2).sum(1); i = d.argmin()
            out[j], best[j] = cand[i], d[i]
        return out, np.sqrt(best)


class Envelope:
    """The shell's outer surface seen from its centre: per direction bin (BIN degrees) the outermost sample. Inner
    faces of the shell, its straps and pads lie beneath it and are left out; the helmet's own rails, being outermost,
    are part of it."""
    BIN = 1.5
    def __init__(self, P, N):
        self.c = P.mean(0)
        r, b = self.polar(P)
        order = np.lexsort((-r, b)); b_s = b[order]
        first = np.r_[True, b_s[1:] != b_s[:-1]]
        keep = order[first]
        self.P, self.N = P[keep], N[keep]
        out = ((self.P - self.c) * self.N).sum(1) < 0
        self.N[out] *= -1                                         # outward
        self.r = dict(zip(b[keep], r[keep]))
        self.grid = Grid(self.P)
    def polar(self, X):
        d = X - self.c; r = np.linalg.norm(d, axis=1); u = d / r[:, None]
        th = np.degrees(np.arccos(np.clip(u[:, 2], -1, 1))); ph = np.degrees(np.arctan2(u[:, 1], u[:, 0])) + 180
        return r, (np.floor(th / self.BIN) * 1000 + np.floor(ph / self.BIN)).astype(np.int64)
    def depth(self, X):
        """How far each point lies inside the envelope (positive inside), 0 where no envelope sample shares its bin."""
        r, b = self.polar(X)
        env = np.array([self.r.get(k, np.nan) for k in b])
        return np.nan_to_num(env - r, nan=-1.0)


MAX_TURN, MAX_MOVE, SCALE = np.radians(12), 0.04, (0.9, 1.1)   # the fit stays near the 3DMA placement
STEP_TURN, STEP_MOVE = np.radians(1.5), 0.004


def fit(rail_P, rail_N, shell_P, shell_N, iters=80):
    """Similarity transform (s, R, t) laying the rails on the shell's outer surface with none of them inside it,
    within MAX_TURN / MAX_MOVE / SCALE of where they are: point-to-plane against the envelope next to each point,
    damped steps."""
    env = Envelope(shell_P, shell_N)
    centre = env.c
    inward = ((centre - rail_P) * rail_N).sum(1) > 0.0          # rail faces turned toward the shell
    contact = rail_P[inward][::3]
    every = rail_P[::4]
    s, R, t = 1.0, np.eye(3), np.zeros(3)
    def rows_for(X, target, near=0.02, side=None):
        idx, d = env.grid.nearest(X)
        ok = (idx >= 0) & (d < near)
        p, n, x = env.P[idx[ok]], env.N[idx[ok]], X[ok]
        h = ((x - p) * n).sum(1)                                 # height over the surface (outward +)
        if side == 'inside': keep = h < 0.0005; x, n, h = x[keep], n[keep], h[keep]
        A = np.hstack([np.cross(x - centre, n), n, ((x - centre) * n).sum(1)[:, None]])
        return A, target - h
    for it in range(iters):
        A1, b1 = rows_for(s * contact @ R.T + t, 0.0015, near=0.025)
        A2, b2 = rows_for(s * every @ R.T + t, 0.002, near=0.02, side='inside')
        if len(b1) < 30: break
        A = np.vstack([A1, 3.0 * A2]); b = np.concatenate([b1, 3.0 * b2])
        A = np.vstack([A, np.diag([0.02] * 3 + [0.02] * 3 + [0.05])]); b = np.concatenate([b, np.zeros(7)])  # damping
        x, *_ = np.linalg.lstsq(A, b, rcond=None)
        w_, dt, ds = x[:3], x[3:6], x[6]
        th = np.linalg.norm(w_)
        if th > STEP_TURN: w_ = w_ * STEP_TURN / th; th = STEP_TURN
        if np.linalg.norm(dt) > STEP_MOVE: dt = dt * STEP_MOVE / np.linalg.norm(dt)
        dR = np.eye(3) if th < 1e-12 else (lambda k: np.eye(3) + np.sin(th) * k + (1 - np.cos(th)) * k @ k)(
            np.array([[0, -w_[2], w_[1]], [w_[2], 0, -w_[0]], [-w_[1], w_[0], 0]]) / th)
        s2 = float(np.clip(s * (1 + np.clip(ds, -0.01, 0.01)), *SCALE)); f = s2 / s
        R2 = dR @ R; t2 = dR @ (f * (t - centre)) + centre + dt
        ang = np.arccos(np.clip((np.trace(R2) - 1) / 2, -1, 1))
        # keep within the limits: the move is measured at the rails' own centre
        rc = rail_P.mean(0); move = (s2 * R2 @ rc + t2) - rc
        if ang > MAX_TURN or np.linalg.norm(move) > MAX_MOVE: break
        s, R, t = s2, R2, t2
        if np.linalg.norm(x) < 1e-5: break
    idx, d = env.grid.nearest(s * contact @ R.T + t)
    X = s * every @ R.T + t; ix, dx = env.grid.nearest(X); okx = (ix >= 0) & (dx < 0.02)
    inside = float((((X[okx] - env.P[ix[okx]]) * env.N[ix[okx]]).sum(1) < -0.002).mean()) if okx.any() else 0.0
    return s, R, t, float(np.median(d[idx >= 0])), inside


def main():
    out_dir = ITEM / 'fit_renders'; out_dir.mkdir(exist_ok=True)
    info = json.load(open(SRC / 'info.json'))
    rails = [read_bin(SRC / f'{n}.bin') for n in RAILINK_NODES]
    RP = np.vstack([r[0] for r in rails]); RN = np.vstack([r[1] for r in rails])
    offs = np.cumsum([0] + [len(r[0]) for r in rails]); RT = np.vstack([r[2] + o for r, o in zip(rails, offs)])
    Rx90 = np.eye(4); Rx90[:3, :3] = qmat((np.sin(np.pi / 4), 0, 0, np.cos(np.pi / 4)))
    table, seen = {}, {}
    for tpl, path in sorted(helmets().items()):
        P, N, slot = prefab(path)
        if slot is None: print(tpl, 'no mod_nvg node in', path); continue
        H = slot @ Rx90                                             # the F-PANO root in the helmet's frame
        rp = RP @ H[:3, :3].T + H[:3, 3]; rn = RN @ H[:3, :3].T
        key = path.split('/')[-2]
        if key in seen and seen[key][0] is not None:
            s, R, t, med, n = seen[key][1]
        else:
            s, R, t, med, n = fit(rp, rn, P, N)
            seen[key] = (1, (s, R, t, med, n))
            print(f'   default: {fit(rp, rn, P, N, iters=0)[4]*100:.0f}% inside, median gap {fit(rp, rn, P, N, iters=0)[3]*1000:.1f} mm')
        # local transform of the group under the F-PANO root: L = H^-1 (s R . + t) H
        Hi = np.linalg.inv(H)
        M = np.eye(4); M[:3, :3] = s * R; M[:3, 3] = t
        L = Hi @ M @ H
        Ls = np.cbrt(np.linalg.det(L[:3, :3])); LR = L[:3, :3] / Ls
        table[tpl] = {'pos': L[:3, 3].round(5).tolist(), 'rot': mat2quat(LR).round(6).tolist(), 'scale': round(float(Ls), 4),
                      'prefab': path, 'medianGap': round(med, 4), 'insideShare': round(n, 3)}
        print(f'{tpl} {key:32s} scale {Ls:.3f} move {np.linalg.norm(L[:3,3])*1000:5.1f} mm turn {np.degrees(np.arccos(np.clip((np.trace(LR)-1)/2,-1,1))):5.1f} deg  median gap {med*1000:.1f} mm, {n*100:.0f}% inside')
        with open(out_dir / f'{key}.obj', 'w') as f:  # the helmet, the rails as they were and as fitted
            V0 = np.vstack(TRIS).reshape(-1, 3)
            f.write('o shell' + chr(10)); f.writelines('v %f %f %f' % tuple(v) + chr(10) for v in V0)
            f.writelines('f %d %d %d' % (3 * i + 1, 3 * i + 2, 3 * i + 3) + chr(10) for i in range(len(V0) // 3))
            base = len(V0) + 1
            for tag, X in (('default', rp), ('fitted', s * rp @ R.T + t)):
                f.write(f'o {tag}' + chr(10)); f.writelines('v %f %f %f' % tuple(v) + chr(10) for v in X)
                f.writelines('f %d %d %d' % tuple(Tri + base) + chr(10) for Tri in RT)
                base += len(X)
    (ITEM / 'railink_fit.json').write_text(json.dumps(table, indent=1))
    print(len(table), 'helmets written')


if __name__ == '__main__':
    main()
