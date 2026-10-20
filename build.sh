#!/bin/bash
# Builds WatchDog.dll on Linux/macOS (cross-compiles; the plugin runs on Windows). Needs git and the .NET 10 SDK.
# See build.ps1 for why Skua is downloaded.
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
SKUA_COMMIT='967f07e4b2ed7d0180e761644e1e1319d6e5db9a'   # auqw/skua, Skua 1.4.5
DEPS="$ROOT/.deps/skua"
if [ ! -d "$DEPS/.git" ]; then
  echo "== Downloading Skua (for its interface assemblies)"
  git clone -q https://github.com/auqw/skua.git "$DEPS"
fi
git -C "$DEPS" fetch -q origin
git -C "$DEPS" checkout -q -f "$SKUA_COMMIT"
echo "== Building Skua.Core.Interfaces"
dotnet build "$DEPS/Skua.Core.Interfaces/Skua.Core.Interfaces.csproj" -c Release -p:EnableWindowsTargeting=true -v quiet
LIB="$ROOT/.deps/lib"; mkdir -p "$LIB"
cp "$DEPS/Skua.Core.Interfaces/bin/Release/net10.0-windows/Skua.Core.Interfaces.dll" "$LIB/"
cp "$DEPS/Skua.Core.Models/bin/Release/net10.0/Skua.Core.Models.dll" "$LIB/"
cp "$DEPS/Skua.Core.Utils/bin/Release/net10.0/Skua.Core.Utils.dll" "$LIB/"
echo "== Building Watch Dog"
dotnet build "$ROOT/src/WatchDog.csproj" -c Release -p:SkuaLib="$LIB" -v quiet
echo "== Done: $ROOT/src/bin/Release/net10.0-windows/WatchDog.dll"
