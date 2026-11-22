#!/bin/bash
# preview.sh <dump-dir> <out-name> [set]: the chain rerun offline on a buffer dump, outputs in the scratchpad
C="$(cd "$(dirname "$0")/.." && pwd)"
S="${PREVIEW_OUT:-$C/build/out/preview}"   # where the previews go (set PREVIEW_OUT to put them elsewhere)
cp "$C/EnvgChain.cs" "$C/Unity/Assets/Editor/EnvgChain.cs"
O="$(cygpath -w $S/prev/$2)"
mkdir -p "$S/prev/$2" "$C/build/out"
"D:/Unity 2022.3.43f1/Editor/Unity.exe" -batchmode -quit -projectPath "$C/Unity" -executeMethod EnvgPreview.Run -envgDump "$1" -envgOut "$O" ${3:+-envgSet "$3"} ${4:+-envgCrop "$4"} ${5:+-envgReproj} -logFile "$C/build/out/preview.log"
grep -a "ENVG_PREVIEW_DONE\|ENVG preview\|error CS\|Exception" "$C/build/out/preview.log" | head -5
