# Splits the F-PANO prefab into four items (user: "把这个导轨系统单独做成一个物品，F-PANO 的电池盒，支架，夜视仪，
# 单独做成三个物品"), each with its own node list in its own root frame, written as info_<item>.json next to info.json:
#   rails    the RAILINK rails: fpano_railink (moved per helmet by the client) with its L / R / back bones, the rails
#            and their inner parts (skinned), the power socket; slot nodes: mod_battery (on the back bone), mod_equipment
#            (headset; the client sets it per headset from railink_headsets.json), mod_mount_000 / 001 (ARC rail
#            adapters, left / right). The F-PANO mount goes in the helmet's own mod_nvg (user, asked: "支架装在头盔夜视仪
#            槽"; the client refuses it on a helmet without the rails);
#   battery  the RAILINK Battery Pack+ alone, in the group's frame (its slot node cancels the game's turn);
#   mount    the F-PANO mount: the Base plate and the jointed arm (carriage, axis = flip hinge, frame, tilt, arm, slide),
#            a mod_nvg slot node where the goggle's device node was;
#   goggle   the F-PANO itself: nvg_fpano_LOD0 at the root's identity with its glass, the power cable skinned to it and
#            to a placeholder socket bone the client rebinds to the rails' socket.
# The game turns an attached item's root +90 deg about X under its slot node (G); every slot node carries G^-1 so
# that the attached item's frame is the frame the slot was laid out in.
# FAST's own headset and rail-adapter slots give the RAILINK's: their poses on the FAST MT, mapped into the group
# frame through the FAST's fitted transform (railink_fit.json), so a RAC headset or an adapter sits as on that helmet.
import json
import numpy as np
from prepare import qmat, mat2quat, ITEM, write_mesh
from fit_helmets import WIN, read_bin
import UnityPy

G = np.eye(4); G[:3, :3] = qmat((np.sin(np.pi / 4), 0, 0, np.cos(np.pi / 4)))
FAST_TPL = '5a154d5cfcdbcb001a3b00da'
FAST = 'assets/content/items/equipment/helmet_opscore_fast/item_equipment_helmet_opscore_fast_black.bundle'
# The helmet headsets the rails take, each laid as on its own helmet: (headset template ids, helmet template, helmet
# prefab, the helmet's headset slot node).
HEADSETS = [(['5a16b9fffcdbcb0176308b34'], FAST_TPL, FAST, 'mod_equipment_001'),
            (['66b5f69ea7f72d197e70bcdb', '66b5f6a28ca68c6461709ed8'], '5e00c1ad86f774747333222c',
             'assets/content/items/equipment/helmet_team_wendy_exfil/item_equipment_helmet_team_wendy_exfil_black.bundle', 'mod_equipment_000')]
ITEMS = {'rails': 'fpano_railink_rails', 'battery': 'fpano_railink_battery', 'mount': 'nvg_fpano_mount', 'goggle': 'nvg_fpano',
         'hl2a': 'fpano_light_hl2a', 'mpls': 'fpano_light_mpls', 'strobe': 'fpano_light_strobe'}
# The RAILINK lights (user: "做成 2", separate light items on the rails). Each is laid out for the LEFT side, where the
# model has the HL2-A and a strobe; the MPLS (model: right front) is mirrored into it. The rails' right-side slots are
# the left ones mirrored (scale -1 in x), so a light on the right is the mirror image of it on the left, at the same
# height (0.2.17 turned them 180 deg about the forward axis, which put the right strobe 10 cm low: user, in game). Modes (the game's TacticalComboVisualController: mode_000 = off, mode_00k
# with its lights): white light as the game's flashlights (spot, shadows); IR as the game's IR illuminators (IkLight:
# seen only through night vision); the strobes' lights are named strobe_light (the client flashes them).
WHITE, RED, GREEN, BLUE = [1, 1, 1, 1], [1, 0.12, 0.08, 1], [0.15, 1, 0.25, 1], [0.2, 0.35, 1, 1]
LIGHT_ITEMS = {
    'hl2a': {'mesh': 'fpano_light_hl2a', 'turn': False, 'front': 'z', 'modes': [
        # the user's list: IR low, IR high, white low, white high
        dict(light='spot', lightColor=[1, 0, 0, 1], intensity=0.6, range=30.0, angle=50.0, ir=True),
        dict(light='spot', lightColor=[1, 0, 0, 1], intensity=1.2, range=65.0, angle=50.0, ir=True),
        dict(light='spot', lightColor=WHITE, intensity=0.6, range=25.0, angle=60.0),
        dict(light='spot', lightColor=WHITE, intensity=1.3, range=55.0, angle=60.0)]},
    'mpls': {'mesh': 'fpano_light_mpls', 'turn': True, 'front': 'z', 'modes': [   # turn: mirrored into the left side
        # the user's list: red and white
        dict(light='spot', lightColor=RED, intensity=0.5, range=10.0, angle=80.0),
        dict(light='spot', lightColor=WHITE, intensity=0.5, range=12.0, angle=80.0)]},
    # CORE Survival HEL-STAR Rail Strobe (the RAILINK one; the model's board reads "IR VIS"): IR or visible, each dim or
    # bright; the client flashes the "strobe_light"s (both strobes together, as the user's videos) and draws the lens
    # glow at "strobe_lens" (facing out of the strobe, local +z)
    'strobe': {'mesh': 'fpano_light_strobe_L', 'turn': False, 'front': 'out', 'modes': [
        # no scene light: only the lens glow comes and goes (user: "那个闪的光是什么？感觉根本就是不是物体发出的光，让那个
        # 光晕出现再消失不久行了？"); the modes keep an empty strobe_light each, the client reads the mode from them
        dict(light='', ir=True, strobe=True), dict(light='', ir=True, strobe=True),
        dict(light='', strobe=True), dict(light='', strobe=True)]},
}
LIGHT_SLOTS = {'hl2a': ('mod_flashlight_000', 'mod_flashlight_001'), 'strobe': ('mod_tactical_000', 'mod_tactical_001')}


def M(pos, rot, scale=1.0):
    m = np.eye(4); m[:3, :3] = qmat(rot) * scale; m[:3, 3] = pos; return m


def node(name, parent, m, mesh='', mat='', **kw):
    s = np.cbrt(np.linalg.det(m[:3, :3])); R = m[:3, :3] / s
    assert abs(s - 1) < 1e-4, (name, s)
    return {'name': name, 'parent': parent, 'pos': m[:3, 3].tolist(), 'rot': mat2quat(R).tolist(), 'mesh': mesh, 'mat': mat, **kw}


def worlds(nodes):
    by = {n['name']: n for n in nodes}; W = {}
    def w(name):
        if name in W: return W[name]
        n = by[name]; L = M(n['pos'], n['rot'])
        W[name] = (w(n['parent']) if n['parent'] else np.eye(4)) @ L
        return W[name]
    for n in nodes: w(n['name'])
    return W


def helmet_slots(path):
    """A helmet's slot nodes in the F-PANO root frame on it (its mod_nvg turned by G)."""
    env = UnityPy.load(str(WIN / path))
    T = {o.path_id: o.read() for o in env.objects if o.type.name == 'Transform'}
    def world(t):
        chain = []
        while True:
            chain.append(t)
            if not t.m_Father or t.m_Father.path_id == 0: break
            t = T[t.m_Father.path_id]
        m = np.eye(4)
        for t in reversed(chain):
            q = t.m_LocalRotation; p = t.m_LocalPosition
            m = m @ M((p.x, p.y, p.z), (q.x, q.y, q.z, q.w))
        return m
    by = {t.m_GameObject.read().m_Name: world(t) for t in T.values()}
    root = np.linalg.inv(by['mod_nvg'] @ G)
    out = {k: root @ v for k, v in by.items() if k.startswith('mod_')}
    out['mod_nvg_world'] = by['mod_nvg'] @ G                     # the F-PANO root frame in the helmet's root frame
    out.update({'__root_' + k: v for k, v in by.items() if k.startswith('mod_')})   # slots in the helmet's root frame
    return out


def split(out, info):
    nodes = info['nodes']; W = worlds(nodes); by = {n['name']: n for n in nodes}
    Gi = np.linalg.inv(G)
    # mount: as before, the device node replaced by the goggle's slot
    mount_names = ['nvg_fpano_mount_LOD0', 'fpano_carriage', 'axis', 'fpano_frame', 'fpano_tilt', 'fpano_arm', 'fpano_slide']
    mount = [dict(by[n]) for n in mount_names]
    dev = by['nvg_fpano_LOD0']
    mount.append(node('mod_nvg', 'fpano_slide', M(dev['pos'], dev['rot']) @ Gi))
    # goggle: the F-PANO WITH its mount, one item (user: "要不支架和 F-PANO 做成一个物品吧"): the mount's nodes, the device
    # node under the slide as before, its glass; the cable in the root frame, skinned to the device and to a
    # placeholder socket the client rebinds to the rails' socket.
    goggle = [dict(by[n]) for n in mount_names] + [dict(dev), dict(by['nvg_fpano_glass_LOD0'])]
    goggle.append(node('fpano_power_socket', '', W['fpano_power_socket']))
    goggle.append(dict(by['fpano_power_cable'], hidden=True))  # shown by the client once bound to the rails' socket
    # rails: the group, its bones, the rails and inner parts, the socket; slot nodes
    # the battery is part of the rails since 0.2.15 (user: "把电池和 RALINK 做成一个物品吧"), on the back bone
    rails = [dict(by[n]) for n in ('fpano_railink', 'fpano_railink_L', 'fpano_railink_R', 'fpano_railink_back',
                                   'fpano_power_socket', 'fpano_railink_rails', 'fpano_railink_inside', 'fpano_railink_battery')]
    bv = read_bin(out / 'fpano_railink_battery.bin')[0]; bc = (bv.min(0) + bv.max(0)) / 2
    # the F-PANO on the rails (user: "让 F-PANO 也能装在 RALINK 上面"): its slot where a helmet's mod_nvg puts it, so the
    # goggle's root is the rails' root (on a helmet the client puts this node on the helmet's own mod_nvg)
    rails.append(node('mod_nvg', '', Gi))
    # what the helmet's headset slot also held (the EXFIL's ear covers and WTT's EXFIL mandibles share its
    # mod_equipment_000 with the ComTacs; user: "原本装 RALINK 之前能装备的东西...应该还能够装备这些东西并继承之前的关系"):
    # a slot at the rails' root, G^-1 like mod_nvg, so the item sits exactly where it sat in the helmet's slot (the rails'
    # root is that slot's frame; the client moves only fpano_railink). The server fills its filter per helmet.
    rails.append(node('mod_equipment_000', '', Gi))
    fits = json.loads((ITEM / 'railink_fit.json').read_text())
    def group_inv(tpl):
        fit = fits[tpl]; sc = fit['scale'][0] if isinstance(fit['scale'], list) else fit['scale']
        return np.linalg.inv(M(fit['pos'], fit['rot'], sc)), fit
    def unscaled(m):                                            # a slot node in the group frame, its scale removed
        m = m.copy(); s = np.cbrt(np.linalg.det(m[:3, :3])); m[:3, :3] /= s; return m
    Lgi, fit = group_inv(FAST_TPL)
    fast = helmet_slots(FAST); mnt = fast['mod_mount']
    # Headsets: placed as on their own helmet RELATIVE TO THE HEAD (the helmet's root), so the cups sit on the ears and
    # the arms' clips meet the rails (checked on the EXFIL, Tor-2, FAST and MICH 2001); railink_headsets.json gives,
    # per headset template, the local pose of its slot node under the HELMET's root; the client sets the rails'
    # mod_equipment node there in world.
    heads = {}
    for ids, tpl, path, slot in HEADSETS:
        env = UnityPy.load(str(WIN / path))
        t = next(o.read() for o in env.objects if o.type.name == 'Transform' and o.read().m_GameObject.read().m_Name == slot)
        q = t.m_LocalRotation; p = t.m_LocalPosition
        # on rails with no helmet: the rails take the shape they have on the headset's own helmet (that helmet's fit:
        # railink_fit.json, group pose, scale and side offsets) and the headset sits as on that helmet, in the rails'
        # ROOT frame (the F-PANO root frame on that helmet). On the FAST shape the EXFIL ComTacs' arms stood 3 cm off
        # the rails each side (user, 0.2.15: "ComTac 耳机和 RALINK 应该相连的部分还是有空隙").
        hs = helmet_slots(path); m = unscaled(np.linalg.inv(hs['mod_nvg_world']) @ hs['__root_' + slot])
        # onGroup: the same relation to the RAILS, in fpano_railink's local frame (its scaled units; the node itself gets
        # 1/scale like the other slot nodes), so on any helmet the headset clips onto the rails as fitted there. Placed by
        # the head instead, the ComTac on the Caiman hung 2.9 cm forward and 1.4 cm low of the rails (user: "耳机装的位置
        # 也不太合适，应该往后一点，往上一点点").
        gi, _ = group_inv(tpl); g = gi @ np.linalg.inv(hs['mod_nvg_world']) @ hs['__root_' + slot]
        gr = unscaled(g)
        for i in ids: heads[i] = {'pos': [p.x, p.y, p.z], 'rot': [q.x, q.y, q.z, q.w], 'frame': 'helmet root',
                                  'onRails': {'pos': [float(v) for v in m[:3, 3]], 'rot': mat2quat(m[:3, :3]).tolist(), 'frame': 'rails root',
                                              'fit': tpl},
                                  'onGroup': {'pos': [float(v) for v in g[:3, 3]], 'rot': mat2quat(gr[:3, :3]).tolist(), 'frame': 'fpano_railink local, scaled units'}}
    (ITEM / 'railink_headsets.json').write_text(json.dumps(heads, indent=1))
    rac = helmet_slots(FAST)['mod_equipment_001']
    heads['5a16b9fffcdbcb0176308b34'] = node('mod_equipment', 'fpano_railink', unscaled(Lgi @ rac))
    rails.append(dict(heads['5a16b9fffcdbcb0176308b34']))     # the RAC's place by default
    left = unscaled(Lgi @ mnt); left[:3, 3] -= np.array(fit['left'])   # on the left bone at the FAST's fit
    # the right side: the left's mirror image (the rotation conjugated by the x mirror, still a proper rotation). 0.2.37
    # turned it 180 deg about the forward axis, which flipped the left's downward tilt to upward: the right PRA / SCRA
    # stood slanted up (user: "右边的槽位现在是歪的装上去后是斜着朝上的").
    mx = np.diag([-1.0, 1.0, 1.0])
    right = left.copy(); right[:3, :3] = mx @ left[:3, :3] @ mx; right[:3, 3] = left[:3, 3] * [-1, 1, 1]
    side = 'fpano_railink_L' if mnt[0, 3] < 0 else 'fpano_railink_R'
    other = 'fpano_railink_R' if side == 'fpano_railink_L' else 'fpano_railink_L'
    rails.append(node('mod_mount_000', side, left)); rails.append(node('mod_mount_001', other, right))
    for n in rails:
        if n['name'] in ('fpano_railink_rails', 'fpano_railink_inside'): n['bones'] = ['fpano_railink_back', 'fpano_railink_L', 'fpano_railink_R']
    battery = [dict(by['fpano_railink_battery'], parent='', pos=(-bc).tolist())]   # kept for owned copies; fits nowhere
    lights = light_items(out, rails)
    for item, ns in (('rails', rails), ('battery', battery), ('mount', mount), ('goggle', goggle), *lights.items()):  # mount: kept for owned copies
        (out / f'info_{item}.json').write_text(json.dumps({**{k: v for k, v in info.items() if k != 'nodes'}, 'item': item,
                                                           'root': f'{ITEMS[item]}_{info["colour"]}', 'nodes': ns}, indent=1))
    return {k: [n['name'] for n in v] for k, v in (('rails', rails), ('battery', battery), ('mount', mount), ('goggle', goggle), *lights.items())}


def mirror_bin(src, dst):
    """A mesh file mirrored in x (positions, normals; triangle winding reversed), uvs kept."""
    with open(src, 'rb') as f:
        nv, nt, ns = np.fromfile(f, np.int32, 3)
        V = np.fromfile(f, np.float32, nv * 3).reshape(-1, 3); N = np.fromfile(f, np.float32, nv * 3).reshape(-1, 3)
        U = np.fromfile(f, np.float32, nv * 2); ni = np.fromfile(f, np.int32, 1)[0]; T = np.fromfile(f, np.int32, ni).reshape(-1, 3)
    V = V.copy(); N = N.copy(); V[:, 0] *= -1; N[:, 0] *= -1; T = T[:, [0, 2, 1]].copy()
    with open(dst, 'wb') as f:
        np.array([nv, nt, ns], np.int32).tofile(f); V.tofile(f); N.tofile(f); U.tofile(f)
        np.array([T.size], np.int32).tofile(f); T.astype(np.int32).tofile(f)


def split_dome(src, dome, base):
    """The strobe's window glass as its own mesh, for the client's flash: only the glass in the figure-8 window lights
    (user: "这个应该不是整个都闪，只是里面的玻璃材质才闪"; 0.2.24 lit the whole clear housing). The model has no glass
    piece: the window is the opening of the tan figure-8 frame on the strobe's top, the clear housing showing through
    it. Seen from above (+y), the frame's triangles (opaque, tan in the base colour) enclose three regions; the window is
    the middle one. A sheet over it on a 0.25 mm grid, each point dropped onto the clear housing's top surface there,
    lifted 0.2 mm. `base` is the whole strobe, unchanged."""
    import cv2
    from PIL import Image
    from prepare import RAILINK_TEX
    stem = RAILINK_TEX / 'Helmet_OpsCore_RAILINK_SHSF_Strobe_'
    op = np.asarray(Image.open(f'{stem}Opacity.png').convert('L')).astype(float) / 255
    col = np.asarray(Image.open(f'{stem}BaseColor.png').convert('RGB')).astype(float)
    data = open(src, 'rb').read(); open(base, 'wb').write(data)
    with open(src, 'rb') as f:
        nv, nt, ns = np.fromfile(f, np.int32, 3)
        V = np.fromfile(f, np.float32, nv * 3).reshape(-1, 3); N = np.fromfile(f, np.float32, nv * 3).reshape(-1, 3)
        U = np.fromfile(f, np.float32, nv * 2).reshape(-1, 2); ni = np.fromfile(f, np.int32, 1)[0]; T = np.fromfile(f, np.int32, ni).reshape(-1, 3)
    h, w = op.shape; uv = U[T].mean(1)
    rc = (np.clip(((1 - uv[:, 1]) % 1) * h, 0, h - 1).astype(int), np.clip((uv[:, 0] % 1) * w, 0, w - 1).astype(int))
    o, c = op[rc], col[rc]; P = V[T].astype(float)
    clear = o < 0.8
    tan = ~clear & (c[:, 0] > 90) & (c[:, 1] > 80) & (c[:, 0] - c[:, 2] > 20)
    R = 0.0001; x0, z0 = V[:, 0].min(), V[:, 2].min(); W_ = int(np.ptp(V[:, 0]) / R) + 2; H_ = int(np.ptp(V[:, 2]) / R) + 2
    m = np.zeros((H_, W_), np.uint8)
    for t in np.nonzero(tan)[0]:
        cv2.fillPoly(m, [np.round(np.c_[(P[t, :, 0] - x0) / R, (P[t, :, 2] - z0) / R]).astype(np.int32)], 255)
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((7, 7), np.uint8))
    ff = m.copy(); cv2.floodFill(ff, np.zeros((H_ + 2, W_ + 2), np.uint8), (0, 0), 128)
    n_lab, lab, st, cen = cv2.connectedComponentsWithStats((ff == 0).astype(np.uint8))
    fy, fx = np.nonzero(m); mid = np.array([fx.mean(), fy.mean()])
    win = lab == 1 + int(np.argmin([np.linalg.norm(cen[k] - mid) for k in range(1, n_lab)]))   # the middle region
    step = 0.00025; gx = np.arange(V[:, 0].min(), V[:, 0].max(), step); gz = np.arange(V[:, 2].min(), V[:, 2].max(), step)
    X, Z = np.meshgrid(gx, gz); inside = win[np.clip(((Z - z0) / R).astype(int), 0, H_ - 1), np.clip(((X - x0) / R).astype(int), 0, W_ - 1)]
    Y = np.full(X.shape, np.nan)
    for t in np.nonzero(clear)[0]:                  # the clear housing's top surface over each grid point
        a, b, cc = P[t]; M2 = np.array([[b[0] - a[0], cc[0] - a[0]], [b[2] - a[2], cc[2] - a[2]]])
        if abs(np.linalg.det(M2)) < 1e-12: continue
        lo, hi = P[t].min(0), P[t].max(0); sel = inside & (X >= lo[0]) & (X <= hi[0]) & (Z >= lo[2]) & (Z <= hi[2])
        if not sel.any(): continue
        l = np.linalg.solve(M2, np.vstack([X[sel] - a[0], Z[sel] - a[2]]))
        ok = (l[0] >= -1e-6) & (l[1] >= -1e-6) & (l.sum(0) <= 1 + 1e-6)
        y = a[1] + l[0] * (b[1] - a[1]) + l[1] * (cc[1] - a[1])
        idx = tuple(np.array(np.nonzero(sel))[:, ok]); cur = Y[idx]; Y[idx] = np.where(np.isnan(cur) | (y[ok] > cur), y[ok], cur)
    Y += 0.0002
    verts, tris = {}, []
    def vid(i, k):
        if (i, k) not in verts: verts[(i, k)] = len(verts)
        return verts[(i, k)]
    for i in range(X.shape[0] - 1):
        for k in range(X.shape[1] - 1):
            q = [(i, k), (i, k + 1), (i + 1, k + 1), (i + 1, k)]
            if all(inside[a_] and not np.isnan(Y[a_]) for a_ in q):
                v = [vid(*a_) for a_ in q]; tris += [(v[0], v[2], v[1]), (v[0], v[3], v[2])]
    keys = sorted(verts, key=verts.get)
    GV = np.array([[X[a_], Y[a_], Z[a_]] for a_ in keys], np.float32); GT = np.array(tris, np.int32)
    tn = np.cross(GV[GT[:, 1]] - GV[GT[:, 0]], GV[GT[:, 2]] - GV[GT[:, 0]])
    if tn[:, 1].mean() < 0: GT = GT[:, [0, 2, 1]]    # facing up, out of the window
    GN = np.zeros_like(GV); np.add.at(GN, GT.ravel(), np.repeat(np.cross(GV[GT[:, 1]] - GV[GT[:, 0]], GV[GT[:, 2]] - GV[GT[:, 0]]), 3, 0))
    GN /= np.linalg.norm(GN, axis=1, keepdims=True) + 1e-12
    GU = np.zeros((len(GV), 2), np.float32)
    with open(dome, 'wb') as f:
        np.array([len(GV), len(GT), 1], np.int32).tofile(f); GV.tofile(f); GN.astype(np.float32).tofile(f); GU.tofile(f)
        np.array([GT.size], np.int32).tofile(f); GT.tofile(f)
    return len(GV), len(GT)


def light_items(out, rails):
    """The light items' node lists (in their root frame: the left-side mount point of their kind), and the rails' slots
    for them (appended to `rails`): left on fpano_railink_L at that point, right on fpano_railink_R its mirror image."""
    Gi = np.linalg.inv(G)
    centre = lambda name: (lambda v: (v.min(0) + v.max(0)) / 2)(read_bin(out / f'{name}.bin')[0])
    points = {'hl2a': centre('fpano_light_hl2a'), 'strobe': centre('fpano_light_strobe_L')}   # each kind's mount point
    points['mpls'] = points['hl2a']
    for kind, (left, right) in LIGHT_SLOTS.items():
        c = points[kind]; L = np.eye(4); L[:3, 3] = c
        rails.append(node(left, 'fpano_railink_L', L @ Gi))
        # mirror S L Gi S = T(Sc) Gi S (Gi turns about x, which S keeps): position mirrored, rotation Gi, scale (-1,1,1);
        # the game's turn G on the light's root commutes with S too, so the light is the mirror image of the left one
        rails.append(dict(node(right, 'fpano_railink_R', L @ Gi), pos=[float(-c[0]), float(c[1]), float(c[2])], scale=[-1.0, 1.0, 1.0]))
    items = {}
    for kind, spec in LIGHT_ITEMS.items():
        mesh = spec['mesh']
        if spec['turn']:                               # mirrored into the left side
            mirror_bin(out / f'{mesh}.bin', out / f'{mesh}_L.bin'); mesh += '_L'
        v = read_bin(out / f"{mesh}.bin")[0]
        place = np.eye(4); place[:3, 3] -= points[kind]   # mesh (root frame, left side) -> item frame
        V = v + place[:3, 3]
        if kind == 'strobe':                           # the clear dome its own node (the client lights it in a flash)
            split_dome(out / f'{mesh}.bin', out / f'{mesh}_dome.bin', out / f'{mesh}_base.bin')
            ns = [node(mesh, '', place, mesh=f'{mesh}_base', mat='strobe'), node('strobe_dome', '', place, mesh=f'{mesh}_dome', mat='strobe')]
        else:
            ns = [node(mesh, '', place, mesh=mesh, mat=kind)]
        if spec['front'] == 'z':                       # the lens: the front-most vertices; the beam straight ahead
            lens = V[V[:, 2] > V[:, 2].max() - 0.002]; at = np.r_[lens[:, :2].mean(0), V[:, 2].max() + 0.002]
            L = np.eye(4); L[:3, 3] = at
            ns.append(node('lamp_lens', '', L))        # where the client draws the lamp's own glow, facing +z
        else:                                          # the strobe: its lens is the outer face of the clear housing
            at = np.r_[V[:, 0].min() - 0.001, (V[:, 1].min() + V[:, 1].max()) / 2, (V[:, 2].min() + V[:, 2].max()) / 2]
            L = np.eye(4); L[:3, 3] = at
            L[:3, :3] = np.column_stack([[0, 0, 1], [0, 1, 0], [-1, 0, 0]])   # local +z = -x, out of the strobe
            ns.append(node('strobe_lens', '', L))
        ns.append(node('mode_000', '', np.eye(4)))
        for k, mode in enumerate(spec['modes'], 1):
            m = np.eye(4); m[:3, 3] = at
            ns.append(node(f'mode_{k:03d}', '', np.eye(4)))
            ns.append(node('strobe_light' if mode.get('strobe') else f'light_{k:03d}', f'mode_{k:03d}', m,
                           **{**{kk: vv for kk, vv in mode.items() if kk != 'strobe'}, 'ir': bool(mode.get('ir'))}))
        items[kind] = ns
    return items
