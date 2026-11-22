# Hand alignment of the RAILINK group per helmet shape (user: "那你就手动对齐吧"): fit_helmets.py's automatic fit
# only kept the rails off the shell; the user wants them on the helmet's own rails (where the real RAILINK replaces
# them), or along the rim where a helmet has none. Per helmet shape (its bundle folder), item/railink_manual.json
# holds hand-set values in the F-PANO root frame (X across, Y up the shroud, Z forward):
#   ty, tz  move up / forward (m);  pitch  turn about X (deg, + lifts the front);  s  scale;  sx  extra width scale
# about the rails' centre. This script writes item/railink_fit.json for the client (same format as fit_helmets.py,
# one entry per helmet template id; scale may be a 3-vector) and, with --render, a side and a top view per shape
# into item/fit_renders/manual_*.png and one contact sheet.
# Run: python item/fit_manual.py [--render] [shape ...]
import json, os, sys, subprocess
from pathlib import Path
import numpy as np
from fit_helmets import helmets, prefab, read_bin, qmat, mat2quat, TRIS, SRC, RAILINK_NODES, ITEM

BLENDER = os.environ.get('BLENDER', 'blender')   # Blender 4+ (set BLENDER to its full path if it is not on PATH)
OUT = ITEM / 'fit_renders'
MANUAL = ITEM / 'railink_manual.json'
DEFAULT = {'ty': 0.0, 'tz': 0.0, 'pitch': 0.0, 's': 1.0, 'sx': 1.0, 'dxL': 0.0, 'dxR': 0.0, 'dzBack': 0.0}


def rails():
    parts = [read_bin(SRC / f'{n}.bin') for n in RAILINK_NODES]
    offs = np.cumsum([0] + [len(p[0]) for p in parts])
    return np.vstack([p[0] for p in parts]), np.vstack([p[2] + o for p, o in zip(parts, offs)])


def local(p, centre):
    """The group's local matrix under the F-PANO root for one shape's values."""
    a = np.radians(p['pitch']); c, s_ = np.cos(a), np.sin(a)
    R = np.array([[1, 0, 0], [0, c, -s_], [0, s_, c]])
    S = np.diag([p['s'] * p['sx'], p['s'], p['s']])
    M = np.eye(4); M[:3, :3] = R @ S; M[:3, 3] = centre + [0, p['ty'], p['tz']] - R @ S @ centre
    return M


def main(argv):
    render = '--render' in argv; only = [a for a in argv if not a.startswith('--')]
    manual = json.loads(MANUAL.read_text()) if MANUAL.exists() else {}
    RP, RT = rails(); centre = (RP.min(0) + RP.max(0)) / 2
    global RAILMASK
    nb = len(read_bin(SRC / 'fpano_railink_battery.bin')[0]); RAILMASK = np.r_[np.ones(len(RP) - nb), np.zeros(nb)]   # battery last
    Rx90 = np.eye(4); Rx90[:3, :3] = qmat((np.sin(np.pi / 4), 0, 0, np.cos(np.pi / 4)))
    table, shapes, p_of = {}, {}, {}
    for tpl, path in sorted(helmets().items()):
        key = path.split('/')[-2]
        p = {**DEFAULT, **manual.get(key, {})}
        L = local(p, centre)
        LS = L[:3, :3]; sc = np.linalg.norm(LS, axis=0); LR = LS / sc
        table[tpl] = {'pos': L[:3, 3].round(5).tolist(), 'rot': mat2quat(LR).round(6).tolist(),
                      'scale': [round(float(v), 4) for v in sc], 'prefab': path, 'shape': key,
                      'left': [round(-p['dxL'], 5), 0.0, 0.0], 'right': [round(p['dxR'], 5), 0.0, 0.0], 'back': [0.0, 0.0, round(p['dzBack'], 5)]}  # fpano_railink_L / _R / _back localPosition
        shapes.setdefault(key, (tpl, path, L)); p_of[key] = p
    (ITEM / 'railink_fit.json').write_text(json.dumps(table, indent=1))
    print(len(table), 'helmets,', len(shapes), 'shapes written to railink_fit.json')
    if not render: return
    OUT.mkdir(exist_ok=True)
    keys = [k for k in shapes if not only or k in only]
    for key in keys:
        tpl, path, L = shapes[key]
        HIDE = json.loads((ITEM / 'railink_hide.json').read_text()) if (ITEM / 'railink_hide.json').exists() else {}
        if key in HIDE:                                            # drawn without its own rails, as the client shows it
            from rail_islands import renderers
            shell = np.vstack([np.delete(t, [i for e in HIDE[key] if e['renderer'] == go for i in e['drop']], axis=0)
                               for go, _, t, _ in renderers(path)]).reshape(-1, 3)
        else:
            P, N, slot = prefab(path)
            H = slot @ Rx90; Hi = np.linalg.inv(H)
            shell = np.vstack(TRIS).reshape(-1, 3) @ Hi[:3, :3].T + Hi[:3, 3]  # the helmet in the F-PANO root frame
        Y = RP.copy(); t = np.clip((np.abs(Y[:, 0]) - 0.045) / 0.03, 0, 1); t = t * t * (3 - 2 * t)   # the rails' side skinning
        Y[:, 0] += t * np.where(Y[:, 0] < 0, -p_of[key]['dxL'], p_of[key]['dxR']) * (RAILMASK)
        Y[:, 2] += np.where(RAILMASK > 0, 1 - t, 1.0) * p_of[key]['dzBack']                       # the back bone
        X = Y @ L[:3, :3].T + L[:3, 3]
        with open(OUT / f'manual_{key}.obj', 'w') as f:
            f.write('o shell\n'); f.writelines('v %f %f %f\n' % tuple(v) for v in shell)
            f.writelines('f %d %d %d\n' % (3 * i + 1, 3 * i + 2, 3 * i + 3) for i in range(len(shell) // 3))
            f.write('o rails\n'); f.writelines('v %f %f %f\n' % tuple(v) for v in X)
            f.writelines('f %d %d %d\n' % tuple(t + len(shell) + 1) for t in RT)
            band = OUT / f'railband_{key}.npy'
            if band.exists():                                      # the helmet's own rails as found, in red
                hb = np.load(band)[::4]; T3 = shell.reshape(-1, 3, 3); cen = T3.mean(1)
                near = np.zeros(len(cen), bool)
                for i in range(0, len(cen), 2000):
                    near[i:i + 2000] = ((cen[i:i + 2000, None, :] - hb[None, :, :]) ** 2).sum(-1).min(1) < 0.003 ** 2
                B = T3[near].reshape(-1, 3); base = len(shell) + len(X) + 1
                f.write('o band\n'); f.writelines('v %f %f %f\n' % tuple(v) for v in B)
                f.writelines('f %d %d %d\n' % (base + 3 * i, base + 3 * i + 1, base + 3 * i + 2) for i in range(len(B) // 3))
    subprocess.run([BLENDER, '-b', '--factory-startup', '-P', str(ITEM / 'fit_manual_render.py'), '--', str(OUT)] + keys,
                   capture_output=True)
    from PIL import Image, ImageDraw
    # a 1 cm grid over the side views: the camera looks from +X, image right is -Z (back), up is +Y; 900 x 560 px
    # over 0.30 m, centred on Y -0.01, Z -0.12 (fit_manual_render.py)
    px = 900 / 0.30
    def grid(path):
        im = Image.open(path).convert('RGB'); d = ImageDraw.Draw(im)
        for zc in range(-30, 5):
            u = 450 - (zc / 100 + 0.12) * px
            d.line([(u, 0), (u, 559)], fill=(70, 70, 40) if zc % 5 else (160, 160, 60), width=1)
            if zc % 5 == 0: d.text((u + 2, 2), f'z{zc}', fill=(230, 230, 90))
        for yc in range(-12, 11):
            v = 280 - (yc / 100 + 0.01) * px
            d.line([(0, v), (899, v)], fill=(70, 70, 40) if yc % 5 else (160, 160, 60), width=1)
            if yc % 5 == 0: d.text((2, v + 2), f'y{yc}', fill=(230, 230, 90))
        im.save(path)
    for key in keys: grid(OUT / f'manual_{key}_side.png')
    tiles = []
    for key in keys:
        ims = [Image.open(OUT / f'manual_{key}_{v}.png').convert('RGB') for v in ('side', 'top')]
        t = Image.new('RGB', (ims[0].width * 2, ims[0].height + 22), 'white')
        t.paste(ims[0], (0, 22)); t.paste(ims[1], (ims[0].width, 22))
        ImageDraw.Draw(t).text((6, 4), key, fill='black'); tiles.append(t)
    cols = 2; w, h = tiles[0].size; rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new('RGB', (w * cols, h * rows), 'white')
    for i, t in enumerate(tiles): sheet.paste(t, ((i % cols) * w, (i // cols) * h))
    sheet.save(OUT / 'manual_sheet.png'); print('sheet', OUT / 'manual_sheet.png')


if __name__ == '__main__':
    main(sys.argv[1:])
