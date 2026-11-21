# Splits the F-PANO prefab into four items (user: "把这个导轨系统单独做成一个物品，F-PANO 的电池盒，支架，夜视仪，
# 单独做成三个物品"), each with its own node list in its own root frame, written as info_<item>.json next to info.json:
#   rails    the RAILINK rails: fpano_railink (moved per helmet by the client) with its L / R / back bones, the rails
#            and their inner parts (skinned), the power socket; slot nodes: mod_battery (on the back bone), mod_equipment
#            (headset; the client sets it per headset from railink_headsets.json), mod_mount_000 / 001 (ARC rail
#            adapters, left / right), mod_nvg (the F-PANO mount; the client puts it on the helmet's own mod_nvg; the
#            user kept the mount and goggle under the rails: "还是保留 F-PANO 和它的支架做进导轨的子分支里面吧");
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
ITEMS = {'rails': 'fpano_railink_rails', 'battery': 'fpano_railink_battery', 'mount': 'nvg_fpano_mount', 'goggle': 'nvg_fpano'}


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
    return {k: root @ v for k, v in by.items() if k.startswith('mod_')}


def split(out, info):
    nodes = info['nodes']; W = worlds(nodes); by = {n['name']: n for n in nodes}
    Gi = np.linalg.inv(G)
    # mount: as before, the device node replaced by the goggle's slot
    mount_names = ['nvg_fpano_mount_LOD0', 'fpano_carriage', 'axis', 'fpano_frame', 'fpano_tilt', 'fpano_arm', 'fpano_slide']
    mount = [dict(by[n]) for n in mount_names]
    dev = by['nvg_fpano_LOD0']
    mount.append(node('mod_nvg', 'fpano_slide', M(dev['pos'], dev['rot']) @ Gi))
    # goggle: the device node at the root, its glass; the cable and a placeholder socket in the device's frame
    Dw = W['nvg_fpano_LOD0']; Di = np.linalg.inv(Dw)
    goggle = [node('nvg_fpano_LOD0', '', np.eye(4), dev['mesh'], dev['mat']), dict(by['nvg_fpano_glass_LOD0'])]
    sock = Di @ W['fpano_power_socket']
    goggle.append(node('fpano_power_socket', '', sock))
    V, N, T = read_bin(out / 'fpano_power_cable.bin')
    uv = np.fromfile(out / 'fpano_power_cable.bin', np.float32)  # re-read the uvs below
    with open(out / 'fpano_power_cable.bin', 'rb') as f:
        nv = np.fromfile(f, np.int32, 3)[0]; np.fromfile(f, np.float32, nv * 6); U = np.fromfile(f, np.float32, nv * 2).reshape(-1, 2)
    P = V @ Di[:3, :3].T + Di[:3, 3]; Nn = N @ Di[:3, :3].T
    write_mesh(out / 'fpano_power_cable.bin', P[T.ravel()], Nn[T.ravel()], U[T.ravel()])
    cab = dict(by['fpano_power_cable']); cab['weightCentre'] = (Di[:3, :3] @ np.array(cab['weightCentre']) + Di[:3, 3]).tolist()
    goggle.append(cab)
    # rails: the group, its bones, the rails and inner parts, the socket; slot nodes
    rails = [dict(by[n]) for n in ('fpano_railink', 'fpano_railink_L', 'fpano_railink_R', 'fpano_railink_back',
                                   'fpano_power_socket', 'fpano_railink_rails', 'fpano_railink_inside')]
    rails.append(node('mod_nvg', '', Gi))                      # the client sets it on the helmet's own mod_nvg
    rails.append(node('mod_battery', 'fpano_railink_back', Gi))
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
        for i in ids: heads[i] = {'pos': [p.x, p.y, p.z], 'rot': [q.x, q.y, q.z, q.w], 'frame': 'helmet root'}
    (ITEM / 'railink_headsets.json').write_text(json.dumps(heads, indent=1))
    rac = helmet_slots(FAST)['mod_equipment_001']
    heads['5a16b9fffcdbcb0176308b34'] = node('mod_equipment', 'fpano_railink', unscaled(Lgi @ rac))
    rails.append(dict(heads['5a16b9fffcdbcb0176308b34']))     # the RAC's place by default
    left = unscaled(Lgi @ mnt); left[:3, 3] -= np.array(fit['left'])   # on the left bone at the FAST's fit
    rz = np.diag([-1.0, -1.0, 1.0, 1.0])                         # the right side: turned 180 deg about the forward axis
    right = left.copy(); right[:3, :3] = rz[:3, :3] @ left[:3, :3]; right[:3, 3] = left[:3, 3] * [-1, 1, 1]
    side = 'fpano_railink_L' if mnt[0, 3] < 0 else 'fpano_railink_R'
    other = 'fpano_railink_R' if side == 'fpano_railink_L' else 'fpano_railink_L'
    rails.append(node('mod_mount_000', side, left)); rails.append(node('mod_mount_001', other, right))
    for n in rails:
        if n['name'] in ('fpano_railink_rails', 'fpano_railink_inside'): n['bones'] = ['fpano_railink_back', 'fpano_railink_L', 'fpano_railink_R']
    battery = [dict(by['fpano_railink_battery'], parent='')]
    for item, ns in (('rails', rails), ('battery', battery), ('mount', mount), ('goggle', goggle)):
        (out / f'info_{item}.json').write_text(json.dumps({**{k: v for k, v in info.items() if k != 'nodes'}, 'item': item,
                                                           'root': f'{ITEMS[item]}_{info["colour"]}', 'nodes': ns}, indent=1))
    return {k: [n['name'] for n in v] for k, v in (('rails', rails), ('battery', battery), ('mount', mount), ('goggle', goggle))}
