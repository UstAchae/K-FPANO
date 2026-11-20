#!/bin/bash
# preview.sh <dump-dir> <out-name> [set]: the chain rerun offline on a buffer dump, outputs in the scratchpad
C="$(cd "$(dirname "$0")/.." && pwd)"
S=/c/Users/LeeCh/AppData/Local/Temp/claude/1b8f5aa763d6006b/3a37a105-6aef-4e65-b6f6-2215a0ae1fce/scratchpad
cp "$C/EnvgChain.cs" "$C/Unity/Assets/Editor/EnvgChain.cs"
O="$(cygpath -w $S/prev/$2)"
mkdir -p "$S/prev/$2" "$C/build/out"
"D:/Unity 2022.3.43f1/Editor/Unity.exe" -batchmode -quit -projectPath "$C/Unity" -executeMethod EnvgPreview.Run -envgDump "$1" -envgOut "$O" ${3:+-envgSet "$3"} ${4:+-envgCrop "$4"} ${5:+-envgReproj} -logFile "$C/build/out/preview.log"
grep -a "ENVG_PREVIEW_DONE\|ENVG preview\|error CS\|Exception" "$C/build/out/preview.log" | head -5
