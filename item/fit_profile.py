# Lays the RAILINK rail onto each helmet's own side rail by their SIDE PROFILES, the way the user judges it (user:
# "明显没对齐，还有，我每个头盔都得这么一遍遍给你指出来吗？"). In the F-PANO root frame (X across, Y up, Z forward):
#   - the helmet's rail: the helmet prefab's mesh islands on one side (|x| > 0.055) that are long (> 9 cm along Z)
#     and stand off the shell; each candidate's side silhouette (Z, Y) at 0.5 mm;
#   - the RAILINK rail: its rails mesh on one side, side silhouette likewise;
#   - a similarity in the side plane (move in Z and Y, pitch about X, scale) maximising the overlap (IoU) of the two
#     silhouettes: coarse grid then refinement. The best candidate island wins.
# A helmet whose best IoU stays under OK has no separate rail island (rail and shell one piece, or no rail): it is
# listed for a look and keeps the default placement. Every helmet gets an overlay image (helmet rail white outline,
# RAILINK blue outline, 1 cm grid read from the same numbers) in item/fit_renders/profile_<shape>.png and a sheet.
# Writes item/railink_manual.json (fit_manual.py's terms) and runs fit_manual.py to write railink_fit.json.
# Run: python item/fit_profile.py
import json, subprocess, sys
import numpy as np
import UnityPy
from PIL import Image, ImageDraw
from fit_helmets import helmets, read_bin, qmat, SRC, ITEM, WIN

RES = 0.0005                       # silhouette pixel (m)
CENTRE = None
BOX = ((-0.32, 0.04), (-0.08, 0.16))   # Z, Y window of the side plane
OK = 0.55
OUT = ITEM / 'fit_renders'


def world_matrices(env):
    T = {o.path_id: o.read() for o in env.objects if o.type.name == 'Transform'}
    def world(t):
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
    return T, world


def helmet_tris(path):
    """All triangles of the helmet's enabled LOD0-ish renderers in the F-PANO root frame, with island labels."""
    env = UnityPy.load(str(WIN / path)); T, world = world_matrices(env)
    slot = next(world(t) for t in T.values() if t.m_GameObject.read().m_Name == 'mod_nvg')
    Rx = np.eye(4); Rx[:3, :3] = qmat((np.sin(np.pi / 4), 0, 0, np.cos(np.pi / 4))); Hi = np.linalg.inv(slot @ Rx)
    by_go = {t.m_GameObject.path_id: t for t in T.values()}
    mfs = {o.read().m_GameObject.path_id: o.read() for o in env.objects if o.type.name == 'MeshFilter'}
    tris, labels, nxt = [], [], 0
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
        _, lab = np.unique(lab, return_inverse=True)
        tris.append(W[F]); labels.append(lab + nxt); nxt += lab.max() + 1
    return np.vstack(tris), np.concatenate(labels)


def mask(tris, res=None):
    """Side silhouette (Z, Y) of triangles as a boolean image over BOX."""
    res = res or RES
    (z0, z1), (y0, y1) = BOX
    w, h = int((z1 - z0) / res), int((y1 - y0) / res)
    im = Image.new('1', (w, h), 0); d = ImageDraw.Draw(im)
    for t in tris: d.polygon([((v[2] - z0) / res, (y1 - v[1]) / res) for v in t], fill=1)
    return np.array(im)


def pts(m, res=None):
    """Pixel centres of a mask as (z, y)."""
    res = res or RES
    (z0, _), (_, y1) = BOX
    r, c = np.nonzero(m)
    return np.stack([z0 + (c + 0.5) * res, y1 - (r + 0.5) * res], 1)


def place(A_pts, centre, tz, ty, pitch, s):
    a = np.radians(pitch); C, S = np.cos(a), np.sin(a)
    p = A_pts - centre
    return s * (p[:, 0] * C + p[:, 1] * S) + centre[0] + tz, s * (-p[:, 0] * S + p[:, 1] * C) + centre[1] + ty


def iou(A_pts, B_mask, res, centre, tz, ty, pitch, s):
    """IoU of the placed RAILINK silhouette (A_pts sampled at res/2, so it stays solid when scaled) and B_mask."""
    (z0, _), (_, y1) = BOX
    z, y = place(A_pts, centre, tz, ty, pitch, s)
    c = ((z - z0) / res).astype(np.int64); r = ((y1 - y) / res).astype(np.int64)
    ok = (r >= 0) & (r < B_mask.shape[0]) & (c >= 0) & (c < B_mask.shape[1])
    u = np.unique(r[ok] * B_mask.shape[1] + c[ok])
    inter = B_mask.ravel()[u].sum()
    return inter / (len(u) + (~ok).sum() * 0 + B_mask.sum() - inter)


def best_fit(RT, B_tris):
    """Coarse grid at 1.5 mm, then pattern search at 0.5 mm."""
    out = []
    for res in (0.0015, RES):
        A = pts(mask(RT, res / 2), res / 2); B = mask(B_tris, res)
        out.append((A, B, res))
    centre = CENTRE
    A, B, res = out[0]
    best = (-1, None)
    for tz in np.arange(-0.06, 0.061, 0.006):
        for ty in np.arange(-0.05, 0.091, 0.006):
            for pitch in (-15, -10, -5, 0, 5, 10, 15):
                for sc in (0.9, 1.0, 1.1):
                    v = iou(A, B, res, centre, tz, ty, pitch, sc)
                    if v > best[0]: best = (v, np.array([tz, ty, pitch, sc]))
    v, x = best
    A, B, res = out[1]
    v = iou(A, B, res, centre, *x)
    step = np.array([0.003, 0.003, 2.5, 0.04])
    for _ in range(6):
        improved = True
        while improved:
            improved = False
            for i in range(4):
                for sgn in (-1, 1):
                    y = x.copy(); y[i] += sgn * step[i]; y[3] = np.clip(y[3], 0.85, 1.15); y[2] = np.clip(y[2], -20, 20)
                    vv = iou(A, B, res, centre, *y)
                    if vv > v + 1e-6: v, x, improved = vv, y, True
        step /= 2
    return v, x, A, B


def outline(m):
    e = m & ~(np.roll(m, 1, 0) & np.roll(m, -1, 0) & np.roll(m, 1, 1) & np.roll(m, -1, 1))
    return e


def overlay(key, helm_side, rail_m, B_mask, fitted_m, score, path):
    """Helmet side in grey, its rail in white outline, RAILINK (fitted) in blue outline, a 1 cm grid."""
    (z0, z1), (y0, y1) = BOX
    h, w = B_mask.shape
    img = np.zeros((h, w, 3), np.uint8)
    img[helm_side] = (70, 70, 70)
    for m, col in ((B_mask, (255, 255, 255)), (fitted_m, (60, 140, 255))):
        e = outline(m)
        for dy in (0, 1):
            for dx in (0, 1): img[np.roll(np.roll(e, dy, 0), dx, 1)] = col
    im = Image.fromarray(img).crop((int((-0.30 - z0) / RES), int((y1 - 0.14) / RES), int((0.0 - z0) / RES), int((y1 + 0.04) / RES)))
    d = ImageDraw.Draw(im); cz0 = -0.30; cy1 = 0.14
    for zc in range(-30, 1):
        u = (zc / 100 - cz0) / RES; d.line([(u, 0), (u, im.height)], fill=(90, 90, 40) if zc % 5 else (170, 170, 60))
        if zc % 5 == 0: d.text((u + 2, 2), f'z{zc}', fill=(240, 240, 90))
    for yc in range(-4, 15):
        v = (cy1 - yc / 100) / RES; d.line([(0, v), (im.width, v)], fill=(90, 90, 40) if yc % 5 else (170, 170, 60))
        if yc % 5 == 0: d.text((2, v + 2), f'y{yc}', fill=(240, 240, 90))
    d.text((6, im.height - 16), f'{key}  IoU {score:.2f}', fill=(255, 255, 255))
    im.save(path); return im


def main():
    rv, _, rt = read_bin(SRC / 'fpano_railink_rails.bin')
    RT = rv[rt]; RT = RT[RT[:, :, 0].mean(1) > 0.04]           # one side of the RAILINK rail
    global CENTRE
    allP = np.vstack([read_bin(SRC / f'{n}.bin')[0] for n in ('fpano_railink_rails', 'fpano_railink_inside', 'fpano_railink_battery')])
    c3 = (allP.min(0) + allP.max(0)) / 2; CENTRE = np.array([c3[2], c3[1]])
    shapes = {}
    for tpl, path in sorted(helmets().items()): shapes.setdefault(path.split('/')[-2], path)
    manual, tiles, report = {}, [], []
    for key, path in shapes.items():
        T, lab = helmet_tris(path)
        cx = T[:, :, 0].mean(1)
        side = np.abs(cx) > 0.055
        cands = []
        for l in np.unique(lab[side]):
            sel = lab == l; t = T[sel]
            if sel.sum() < 40: continue
            zs = t[:, :, 2]; xs = np.abs(t[:, :, 0])
            if zs.max() - zs.min() < 0.09 or xs.max() < 0.07: continue
            for sgn in (1, -1):                                  # each side of a symmetric island separately
                ts = t[np.sign(t[:, :, 0].mean(1)) == sgn]
                if len(ts) > 20: cands.append((l, sgn, ts))
        best = (-1, None, None, None, None)
        for l, sgn, ts in cands:
            if mask(ts, 0.0015).sum() < 60: continue
            score, x, Ap, B = best_fit(RT, ts)
            if score > best[0]: best = (score, x, B, Ap, (l, sgn))
        helm_side = mask(T[side & (T[:, :, 0].mean(1) < 0)])
        score, x, B, Ap, which = best
        if x is None:
            report.append(f'{key}: no rail-like island'); continue
        tz, ty, pitch, s = x
        fz, fy = place(Ap, CENTRE, tz, ty, pitch, s)
        (z0, _), (_, y1) = BOX
        F = np.zeros_like(B); r = ((y1 - fy) / RES).astype(int); c = ((fz - z0) / RES).astype(int)
        ok = (r >= 0) & (r < F.shape[0]) & (c >= 0) & (c < F.shape[1]); F[r[ok], c[ok]] = True
        tiles.append(overlay(key, helm_side, None, B, F, score, OUT / f'profile_{key}.png'))
        status = 'ok' if score >= OK else 'LOOK'
        report.append(f'{key:30s} IoU {score:.2f} {status}  up {ty*1000:5.1f} fwd {tz*1000:5.1f} mm  pitch {pitch:5.1f}  scale {s:.3f}')
        if score >= OK:
            manual[key] = {'ty': round(float(ty), 4), 'tz': round(float(tz), 4), 'pitch': round(float(pitch), 2), 's': round(float(s), 4),
                           'sx': 1.0, 'auto': 'profile', 'iou': round(float(score), 3)}
    print('\n'.join(report))
    (ITEM / 'railink_manual.json').write_text(json.dumps(manual, indent=1))
    cols = 3; w, h = tiles[0].size; rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new('RGB', (w * cols, h * rows))
    for i, t in enumerate(tiles): sheet.paste(t, ((i % cols) * w, (i // cols) * h))
    sheet.save(OUT / 'profile_sheet.png')
    subprocess.run([sys.executable, str(ITEM / 'fit_manual.py')])


if __name__ == '__main__':
    main()
