# Points the built bundles' materials at the game's own p0 reflective shader, with the GPNVG-18's body material as
# the property template (its tint, specular, gloss and cubemap; our textures), then writes server/bundles/... and
# server/bundles.json. Glass: the same shader, near black with a strong reflection off the game's metal cubemap
# (as the clip-ons' objectives). Usage: python item/finalize.py
import copy, json, sys
from pathlib import Path
import UnityPy

ITEM = Path(__file__).resolve().parent
sys.path.insert(0, str(ITEM))
import game_refs as refs

GAME = Path(r'D:/games/SPT-4.1.6/EscapeFromTarkov_Data/StreamingAssets/Windows')
GPNVG = 'assets/content/items/equipment/customizable/gpnvg/nvg_l3_gpnvg-18_anvis.bundle'
TEMPLATE = 'nvg_l3_gpnvg-18_anvis_LOD0'
COLOURS = ('fde', 'blk')
GLASS = ((0.02, 0.02, 0.022), (1.0, 1.0, 1.0), 3.2, 2.4)  # tint, spec colour, _Glossness, _Specularness
# The glass has no albedo map, so the shader's default white alpha makes the cubemap reflection ten times the
# body's (alpha ~0.1): at 0.8 the lenses glowed lilac-white in the dark hideout. A faint blue-violet coating instead.
GLASS_REFLECT = (0.09, 0.14, 0.13, 1.0)  # the eyepieces' blue-green coating, as in the user's reference photos
DEPENDENCIES = ['shaders', 'cubemaps']
OUT = ITEM.parent / 'server'

env = UnityPy.Environment()
gpnvg = env.load_file((GAME / GPNVG).read_bytes(), name='gpnvg')
template = next((o.read_typetree(), o.assets_file) for o in refs.objects(gpnvg)
                if o.type.name == 'Material' and o.read_typetree()['m_Name'] == TEMPLATE)


def rgba(c): return {'r': c[0], 'g': c[1], 'b': c[2], 'a': c[3]}


def finalize_material(o, t, glass):
    tree, source = template
    own = dict(t['m_SavedProperties']['m_TexEnvs'])
    shader = tree['m_Shader']
    shader_path = source.externals[shader['m_FileID'] - 1].path
    assert shader_path == refs.SHADERS
    t['m_Shader'] = {'m_FileID': refs.external_id(o.assets_file, shader_path), 'm_PathID': shader['m_PathID']}
    t['m_ValidKeywords'] = []; t['m_InvalidKeywords'] = []
    t['m_CustomRenderQueue'] = tree['m_CustomRenderQueue']; t['stringTagMap'] = []; t['disabledShaderPasses'] = []
    props = copy.deepcopy(tree['m_SavedProperties'])
    if glass:
        floats = dict(props['m_Floats']); colors = dict(props['m_Colors'])
        color, spec, gloss, specular = GLASS
        floats['_Glossness'] = gloss; floats['_Specularness'] = specular
        colors['_Color'] = rgba(color + (1.0,)); colors['_SpecColor'] = rgba(spec + (1.0,)); colors['_ReflectColor'] = rgba(GLASS_REFLECT)
        props['m_Floats'] = list(floats.items()); props['m_Colors'] = list(colors.items())
    tex = []
    for slot, v in props['m_TexEnvs']:
        ref = v['m_Texture']; v = copy.deepcopy(v)
        mine = own.get(slot, {}).get('m_Texture', {'m_PathID': 0})
        if slot == '_Cube':
            # The template's cubemap for the body, the crisp metal one for glass; both live in the game's cubemaps.
            cube = refs.METAL_CUBE if glass else ref['m_PathID']
            assert source.externals[ref['m_FileID'] - 1].path == refs.CUBES
            v['m_Texture'] = {'m_FileID': refs.external_id(o.assets_file, refs.CUBES), 'm_PathID': cube}
        elif mine['m_PathID'] and not glass: v['m_Texture'] = mine
        else: v['m_Texture'] = {'m_FileID': 0, 'm_PathID': 0}  # the template's own maps are inside its bundle
        tex.append((slot, v))
    props['m_TexEnvs'] = tex
    t['m_SavedProperties'] = props
    o.save_typetree(t)


manifest = []
KEYS = [(f'ustachae_fpano/fpano_{c}.bundle', {'body', 'mount', 'glass', 'rails', 'inside', 'battery'}) for c in COLOURS]
for key, expected in KEYS:
    built = env.load_file((ITEM / 'built' / key).read_bytes(), name=key)
    done = set()
    for o in refs.objects(built):
        if o.type.name == 'Material':
            t = o.read_typetree()
            kind = t['m_Name'].replace('mat_', '')  # CreateAsset renamed them after the asset file
            finalize_material(o, t, kind == 'glass'); done.add(kind)
    assert done == expected, (key, done)
    out = OUT / 'bundles' / key
    out.parent.mkdir(parents=True, exist_ok=True)
    for name, f in list(built.files.items()):
        if str(name).endswith('.resS'): built.files[name] = refs.Stored(f.bytes, getattr(f, 'flags', 0))
    out.write_bytes(built.save(packer='lz4'))
    manifest.append({'key': key, 'dependencyKeys': DEPENDENCIES})

    check = UnityPy.Environment().load_file(out.read_bytes(), name=key)
    for o in refs.objects(check):
        if o.type.name == 'Material':
            t = o.read_typetree()
            assert t['m_Shader']['m_FileID'] and o.assets_file.externals[t['m_Shader']['m_FileID'] - 1].path == refs.SHADERS
    print('FPANO_FINALIZE_PASS', key, out.stat().st_size)

(OUT / 'bundles.json').write_text(json.dumps({'manifest': manifest}, indent=2), encoding='utf-8')
