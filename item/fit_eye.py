# The helmets whose own rail is not a separate mesh piece (fit_profile.py found none): their rail band read by eye
# from fit_eye_render.py's side views (helmet alone, F-PANO root frame, 1 cm grid), as polygons in that image's
# pixels (900 x 560 px over Z 0..-0.30, Y centred on 0.05: z = -u / 3000, y = 0.05 + (280 - v) / 3000). A helmet with a
# rail gets that rail's outline; one without gets a band along its side just above the rim, where the RAILINK rail
# would replace a FAST-type rail. The RAILINK rail's side silhouette is fitted onto each polygon as fit_profile.py
# does (overlap), and an overlay is written to item/fit_renders/eye_fit_<shape>.png. Results join the profile fits
# (item/railink_profile_auto.json) in item/railink_manual.json; fit_manual.py then writes railink_fit.json.
# Run: python item/fit_eye.py
import json, subprocess, sys
import numpy as np
from PIL import Image, ImageDraw
import fit_profile as FP
from fit_helmets import read_bin, SRC, ITEM

POLY = {  # shape: (rail or band, polygon in the eye image's pixels)
    'helmet_bastion': ('rail', [(230, 320), (160, 230), (300, 200), (380, 155), (540, 120), (640, 125), (700, 150), (740, 180), (745, 260),
                                (720, 300), (690, 295), (600, 200), (540, 185), (410, 265), (330, 300), (300, 345)]),
    'helmet_neosteel': ('rail', [(195, 500), (200, 430), (260, 370), (380, 330), (490, 300), (600, 292), (630, 325), (700, 380), (760, 430),
                                 (750, 470), (700, 440), (620, 390), (560, 365), (470, 365), (400, 375), (330, 400), (250, 440), (230, 500)]),
    'helmet_flux': ('rail', [(195, 465), (215, 395), (265, 330), (400, 268), (505, 212), (600, 192), (690, 215), (690, 300), (600, 282),
                             (545, 330), (450, 348), (360, 400), (320, 460), (250, 475)]),
    'helmet_strike_ballistic': ('rail', [(230, 460), (250, 400), (330, 370), (450, 345), (540, 300), (610, 250), (660, 240), (700, 330),
                                         (690, 380), (620, 340), (540, 350), (450, 375), (330, 420), (300, 460)]),
    'helmet_ulach': ('band', [(190, 470), (300, 430), (450, 390), (600, 350), (740, 330), (750, 290), (600, 300), (450, 340), (300, 385), (190, 420)]),
    'helmet_lshz': ('rail', [(175, 470), (240, 320), (470, 238), (480, 330), (545, 340), (565, 250), (740, 360), (740, 425), (690, 480),
                             (620, 440), (545, 350), (470, 340), (330, 400), (240, 450)]),
    'helmet_nfm_hjelm': ('rail', [(30, 545), (40, 500), (150, 465), (290, 400), (290, 300), (370, 258), (455, 310), (570, 290), (650, 262),
                                  (720, 290), (740, 420), (690, 440), (600, 380), (560, 330), (490, 320), (450, 330), (300, 400), (160, 480), (60, 545)]),
    'helmet_lshz2dtm': ('band', [(150, 500), (400, 490), (550, 470), (700, 440), (790, 420), (790, 370), (700, 380), (550, 400), (400, 420), (150, 430)]),
    'helmet_diamond_age_bastion': ('band', [(60, 480), (300, 455), (480, 435), (560, 425), (650, 445), (760, 470), (760, 400), (650, 375),
                                            (560, 360), (480, 370), (300, 390), (60, 420)]),
    'helmet_6b47': ('band', [(200, 480), (330, 480), (560, 480), (700, 460), (760, 420), (760, 360), (700, 380), (560, 390), (330, 390), (200, 410)]),
    'helmet_6b47covered': ('band', [(200, 480), (330, 470), (560, 470), (700, 450), (760, 410), (760, 350), (700, 370), (560, 390), (330, 390), (200, 410)]),
}


def to_frame(poly):
    return [(-u / 3000.0, 0.05 + (280 - v) / 3000.0) for u, v in poly]   # (z, y)


def poly_mask(zy, res):
    (z0, z1), (y0, y1) = FP.BOX
    w, h = int((z1 - z0) / res), int((y1 - y0) / res)
    im = Image.new('1', (w, h), 0)
    ImageDraw.Draw(im).polygon([((z - z0) / res, (y1 - y) / res) for z, y in zy], fill=1)
    return np.array(im)


def fit(RT, zy):
    outs = []
    for res in (0.0015, FP.RES):
        outs.append((FP.pts(FP.mask(RT, res / 2), res / 2), poly_mask(zy, res), res))
    A, B, res = outs[0]; best = (-1, None)
    for tz in np.arange(-0.06, 0.061, 0.006):
        for ty in np.arange(-0.05, 0.091, 0.006):
            for pitch in (-15, -10, -5, 0, 5, 10, 15):
                for sc in (0.9, 1.0, 1.1):
                    v = FP.iou(A, B, res, FP.CENTRE, tz, ty, pitch, sc)
                    if v > best[0]: best = (v, np.array([tz, ty, pitch, sc]))
    v, x = best; A, B, res = outs[1]; v = FP.iou(A, B, res, FP.CENTRE, *x)
    step = np.array([0.003, 0.003, 2.5, 0.04])
    for _ in range(6):
        improved = True
        while improved:
            improved = False
            for i in range(4):
                for sgn in (-1, 1):
                    y = x.copy(); y[i] += sgn * step[i]; y[3] = np.clip(y[3], 0.85, 1.15); y[2] = np.clip(y[2], -20, 20)
                    vv = FP.iou(A, B, res, FP.CENTRE, *y)
                    if vv > v + 1e-6: v, x, improved = vv, y, True
        step /= 2
    return v, x, A, B


def main():
    rv, _, rt = read_bin(SRC / 'fpano_railink_rails.bin'); RT = rv[rt]; RT = RT[RT[:, :, 0].mean(1) > 0.04]
    allP = np.vstack([read_bin(SRC / f'{n}.bin')[0] for n in ('fpano_railink_rails', 'fpano_railink_inside', 'fpano_railink_battery')])
    c3 = (allP.min(0) + allP.max(0)) / 2; FP.CENTRE = np.array([c3[2], c3[1]])
    manual = json.loads((ITEM / 'railink_profile_auto.json').read_text())
    manual = {k: v for k, v in manual.items() if k not in POLY and k != 'facecover_devtac'}   # good profile fits kept
    for key, (kind, poly) in POLY.items():
        zy = to_frame(poly)
        score, x, A, B = fit(RT, zy)
        tz, ty, pitch, s = x
        fz, fy = FP.place(A, FP.CENTRE, tz, ty, pitch, s)
        (z0, _), (_, y1) = FP.BOX
        F = np.zeros_like(B); r = ((y1 - fy) / FP.RES).astype(int); c = ((fz - z0) / FP.RES).astype(int)
        ok = (r >= 0) & (r < F.shape[0]) & (c >= 0) & (c < F.shape[1]); F[r[ok], c[ok]] = True
        # overlay on the eye render itself: the polygon white, the fitted RAILINK outline blue
        im = Image.open(ITEM / 'fit_renders' / f'eye_{key}_grid.png').convert('RGB'); d = ImageDraw.Draw(im)
        d.line(poly + [poly[0]], fill=(255, 255, 255), width=2)
        e = FP.outline(F); rr, cc = np.nonzero(e)
        zz = z0 + (cc + 0.5) * FP.RES; yy = y1 - (rr + 0.5) * FP.RES
        u = -zz * 3000; v = 280 - (yy - 0.05) * 3000
        for a, b in zip(u, v): d.point((a, b), fill=(40, 120, 255))
        d.text((6, 540), f'{key} {kind} IoU {score:.2f}', fill=(255, 255, 255)); im.save(ITEM / 'fit_renders' / f'eye_fit_{key}.png')
        manual[key] = {'ty': round(float(ty), 4), 'tz': round(float(tz), 4), 'pitch': round(float(pitch), 2), 's': round(float(s), 4),
                       'sx': 1.0, 'auto': f'eye {kind}', 'iou': round(float(score), 3)}
        print(f'{key:30s} {kind:4s} IoU {score:.2f}  up {ty*1000:5.1f} fwd {tz*1000:5.1f} mm  pitch {pitch:5.1f}  scale {s:.3f}')
    (ITEM / 'railink_manual.json').write_text(json.dumps(manual, indent=1))
    subprocess.run([sys.executable, str(ITEM / 'fit_manual.py')])


if __name__ == '__main__':
    main()
