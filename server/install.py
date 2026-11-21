# Installs the server mod (DLL, db, bundles, bundles.json) into a game folder, default D:/games/SPT-TEST.
# Refuses while that folder's SPT server or game is running (both lock files there).
# Usage: python server/install.py [game folder]
import shutil, subprocess, sys
from pathlib import Path

SERVER = Path(__file__).resolve().parent
GAME = Path(sys.argv[1] if len(sys.argv) > 1 else r'D:/games/SPT-TEST')
DEST = GAME / 'SPT_Runtime/user/mods/UstAchae-FPANO'

ps = subprocess.run(['powershell', '-NoProfile', '-Command',
                     "Get-Process SPT.Server,EscapeFromTarkov -ErrorAction SilentlyContinue | ForEach-Object { $_.Path }"],
                    capture_output=True, text=True).stdout
for path in ps.split():
    if Path(path).resolve().is_relative_to(GAME.resolve()):
        raise SystemExit(f'{path} is running; close it first')

files = {SERVER / 'src/bin/Release/UstAchae.Fpano.dll': DEST / 'UstAchae.Fpano.dll',
         SERVER / 'db/items.json': DEST / 'db/items.json',
         SERVER / 'bundles.json': DEST / 'bundles.json'}
for b in (SERVER / 'bundles').rglob('*.bundle'):
    files[b] = DEST / 'bundles' / b.relative_to(SERVER / 'bundles')
for old in (DEST / 'bundles').rglob('*.bundle') if (DEST / 'bundles').exists() else []:
    if old not in files.values(): old.unlink(); print('removed', old.relative_to(GAME))   # bundles of an earlier layout
for src, dst in files.items():
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    assert dst.read_bytes() == src.read_bytes()
    print('installed', dst.relative_to(GAME))
