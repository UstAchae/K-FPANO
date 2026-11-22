# Packs a release zip laid out as the game folder (unzip into the SPT folder):
#   SPT_Runtime/user/mods/UstAchae-FPANO/   the server mod (DLL, db, bundles.json, bundles)
#   BepInEx/plugins/JustachaeFPANO/          the client plugin (DLL, shader bundle)
# from the built outputs in this repo (server/src/bin/Release, server/bundles, client/build/out). Checks that the server
# and client carry the same version (one version for the whole mod) and writes dist/K-FPANO-<version>.zip. Prepares only:
# nothing is published.
# Usage: python package.py
import re, sys, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SERVER_DIR, CLIENT_DIR = 'SPT_Runtime/user/mods/UstAchae-FPANO', 'BepInEx/plugins/JustachaeFPANO'

server_ver = re.search(r'Version \{ get; init; \} = new\("([^"]+)"\)', (ROOT / 'server/src/FpanoMod.cs').read_text(encoding='utf8')).group(1)
client_ver = re.search(r'Version = "([^"]+)"', (ROOT / 'client/EnvgFusionPlugin.cs').read_text(encoding='utf8')).group(1)
if server_ver != client_ver:
    sys.exit(f'server {server_ver} and client {client_ver} differ: one version for the whole mod')

files = {ROOT / 'server/src/bin/Release/UstAchae.Fpano.dll': f'{SERVER_DIR}/UstAchae.Fpano.dll',
         ROOT / 'server/db/items.json': f'{SERVER_DIR}/db/items.json',
         ROOT / 'server/bundles.json': f'{SERVER_DIR}/bundles.json',
         ROOT / 'client/build/out/JustachaeFPANO.dll': f'{CLIENT_DIR}/JustachaeFPANO.dll',
         ROOT / 'client/build/out/fpanofusion.bundle': f'{CLIENT_DIR}/fpanofusion.bundle',
         ROOT / 'README.md': 'K-FPANO README.md'}
for b in sorted((ROOT / 'server/bundles').rglob('*.bundle')):
    files[b] = f'{SERVER_DIR}/bundles/{b.relative_to(ROOT / "server/bundles").as_posix()}'
missing = [str(p) for p in files if not p.exists()]
if missing:
    sys.exit('missing (build first): ' + ', '.join(missing))
for p, name in files.items():   # the built DLLs must carry the version
    if p.suffix == '.dll' and server_ver.encode('utf-16-le') not in p.read_bytes():
        sys.exit(f'{p.name} is not built as {server_ver}')

out = ROOT / 'dist' / f'K-FPANO-{server_ver}.zip'
out.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for p, name in files.items():
        z.write(p, name)
print(f'{out} ({out.stat().st_size / 1e6:.1f} MB, {len(files)} files)')
