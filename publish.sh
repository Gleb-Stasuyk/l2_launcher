#!/bin/bash
# Build the Windows launcher on Linux/Windows: win-x64, self-contained, single-file -> dist/L2Launcher.exe
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET=${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}
"$DOTNET" test tests/L2Launcher.Tests
rm -rf dist && mkdir -p dist
"$DOTNET" publish src/L2Launcher/L2Launcher.csproj -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:EnableWindowsTargeting=true -o dist/publish
cp dist/publish/L2Launcher.exe dist/
cp launcher.example.json dist/launcher.json
sha256sum dist/L2Launcher.exe | tee dist/L2Launcher.exe.sha256
