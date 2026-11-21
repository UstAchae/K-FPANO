# The side-profile fits (fit_profile.py, fit_eye.py) place the RAILINK in the side plane (Z, Y) only; on shells
# wider or narrower than the FAST SF the rails then sink into the shell or float off it. This sets each shape's width
# scale sx (about the centre line x = 0) so that the rails' inner faces rest on the shell's outer surface:
#   - the shell's outer surface per side as a height map over (Y, Z), 2 mm cells: the largest |x| of the helmet's
#     surface (vertices, edge midpoints and centroids of its triangles) in each cell;
#   - the RAILINK's inner faces: vertices of the rails and their inner parts whose normals point toward x = 0, placed
#     with the shape's side-plane values;
#   - their gap |x| - shell over cells the shell covers; sx makes the 5th percentile gap CONTACT (the rail touches the
#     shell at its lowest points, as when screwed on).
# Updates item/railink_manual.json and runs fit_manual.py.
# Run: python item/fit_depth.py
import json, subprocess, sys
import numpy as np
from fit_helmets import helmets, read_bin, SRC, ITEM
from fit_profile import helmet_tris
from fit_manual import local

CELL, CONTACT, QS = 0.002, 0.0005, 0.25
INSIDE = 0.12
HIDE = json.loads((ITEM / 'railink_hide.json').read_text()) if (ITEM / 'railink_hide.json').exists() else {}


def heightmap(T):
    pts = np.vstack([T.reshape(-1, 3), T.mean(1), (T[:, 0] + T[:, 1]) / 2, (T[:, 1] + T[:, 2]) / 2, (T[:, 2] + T[:, 0]) / 2])
    H = {}
    ky = np.floor(pts[:, 1] / CELL).astype(int); kz = np.floor(pts[:, 2] / CELL).astype(int); sd = np.sign(pts[:, 0]).astype(int)
    ax = np.abs(pts[:, 0])
    order = np.argsort(ax)
    for i in order: H[(ky[i], kz[i], sd[i])] = ax[i]               # largest last
    return H


def backmap(T):
    """The shell's back surface per (X, Y) 2 mm cell: the smallest Z (rearmost) of its points."""
    pts = np.vstack([T.reshape(-1, 3), T.mean(1)])
    H = {}
    kx = np.floor(pts[:, 0] / CELL).astype(int); ky = np.floor(pts[:, 1] / CELL).astype(int)
    for i in np.argsort(-pts[:, 2]): H[(kx[i], ky[i])] = pts[i, 2]   # rearmost last
    return H


def main():
    """Per shape, each side's lateral move dx (local X, outward +) putting the QS quantile of the inner faces' gap
    to the shell (helmet rails included: the RAILINK is laid over them) at CONTACT."""
    manual = json.loads((ITEM / 'railink_manual.json').read_text())
    parts = [read_bin(SRC / f'{n}.bin') for n in ('fpano_railink_rails', 'fpano_railink_inside')]
    V = np.vstack([p[0] for p in parts]); N = np.vstack([p[1] for p in parts])
    V = V[(N[:, 0] * np.sign(V[:, 0]) < -0.3) & (np.abs(V[:, 0]) > 0.075)]   # inner faces of the side rails
    bparts = [read_bin(SRC / 'fpano_railink_battery.bin')] + parts
    BV = np.vstack([p[0] for p in bparts]); BN = np.vstack([p[1] for p in bparts])
    BV = BV[np.abs(BV[:, 0]) < 0.045]                                       # the back: battery and crossbar
    allP = np.vstack([read_bin(SRC / f'{n}.bin')[0] for n in ('fpano_railink_rails', 'fpano_railink_inside', 'fpano_railink_battery')])
    centre = (allP.min(0) + allP.max(0)) / 2
    shapes = {}
    for tpl, path in sorted(helmets().items()): shapes.setdefault(path.split('/')[-2], path)
    for key, path in shapes.items():
        if HIDE and key not in HIDE: continue                      # the rails fit only the helmets in railink_hide.json
        T, _ = helmet_tris(path)
        if key in HIDE:                                            # the shell without the own rails the RAILINK replaces
            from rail_islands import renderers
            T = np.vstack([np.delete(t, [i for e in HIDE[key] if e['renderer'] == go for i in e['drop']], axis=0)
                           for go, _, t, _ in renderers(path)])
        H = heightmap(T)
        p = {'ty': 0.0, 'tz': 0.0, 'pitch': 0.0, 's': 1.0, **manual.get(key, {})}; p['sx'] = 1.0
        L = local(p, centre); X = V @ L[:3, :3].T + L[:3, 3]
        out = {}
        for side, sg in (('L', -1), ('R', 1)):
            Xs = X[np.sign(X[:, 0]) == sg]
            sh = np.array([H.get((int(np.floor(x[1] / CELL)), int(np.floor(x[2] / CELL)), sg), np.nan) for x in Xs]); ok = ~np.isnan(sh)
            if ok.sum() < 30: out[side] = 0.0; continue
            gap = np.abs(Xs[ok, 0]) - sh[ok]
            # outward until at most INSIDE of the inner faces lie inside the shell (a binary search on the move); 3% left
            # a gap where the own rails came off (user: "导轨和头盔之间有空隙，应该让导轨缩小一些贴到头盔表面")
            lo, hi = -0.02, 0.05
            for _ in range(40):
                mid = (lo + hi) / 2
                if float((gap + mid < -CONTACT).mean()) > INSIDE: lo = mid
                else: hi = mid
            out[side] = hi / p['s']
        # the back (battery and crossbar): moved along the group's Z (+ toward the helmet) so that 2% of its vertices
        # lie inside the shell's back (behind it in Z is outside); a binary search on that share.
        HB = backmap(T); back = BV @ L[:3, :3].T + L[:3, 3]
        kx = np.floor(back[:, 0] / CELL).astype(int); ky = np.floor(back[:, 1] / CELL).astype(int)
        sh = np.array([HB.get((a, b), np.nan) for a, b in zip(kx, ky)]); ok = ~np.isnan(sh)
        def inside(dz): return float(((back[ok, 2] + dz) > sh[ok]).mean())
        lo, hi = -0.06, 0.06
        for _ in range(40):
            mid = (lo + hi) / 2
            if inside(mid) > 0.02: hi = mid
            else: lo = mid
        out['back'] = lo / p['s'] if ok.sum() > 30 else 0.0
        manual.setdefault(key, p); manual[key]['sx'] = 1.0; manual[key]['dzBack'] = round(out['back'], 4)
        manual[key]['dxL'], manual[key]['dxR'] = round(out['L'], 4), round(out['R'], 4)
        print(f'{key:30s} left {out["L"]*1000:6.1f} mm  right {out["R"]*1000:6.1f} mm  back {out["back"]*1000:6.1f} mm')
    (ITEM / 'railink_manual.json').write_text(json.dumps(manual, indent=1))
    subprocess.run([sys.executable, str(ITEM / 'fit_manual.py')])


if __name__ == '__main__':
    main()
