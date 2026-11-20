#!/bin/bash
# Build the F-PANO client plugin (DLL + shader bundle) into client/build/out. Run from anywhere.
set -e
C="$(cd "$(dirname "$0")/.." && pwd)"
cd "$C"
cp EnvgChain.cs Unity/Assets/Editor/EnvgChain.cs
mkdir -p build/out
"C:/Program Files/Unity/Hub/Editor/6000.6.5f1/Editor/Data/DotNetSdk/dotnet.exe" "C:/Program Files/Unity/Hub/Editor/6000.6.5f1/Editor/Data/DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll" @build/csc.rsp 2>&1 | grep -E "error" && exit 1 || true
"D:/Unity 2022.3.43f1/Editor/Unity.exe" -batchmode -quit -nographics -projectPath Unity -executeMethod BuildShaders.Build -logFile build/out/shader-build.log
grep -q SHADER_BUNDLE_SUCCESS build/out/shader-build.log || { grep -a "error" build/out/shader-build.log | head; exit 1; }
ls -la build/out/JustachaeFPANO.dll build/out/fpanofusion.bundle
