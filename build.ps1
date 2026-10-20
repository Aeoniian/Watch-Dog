# Builds WatchDog.dll. Needs git and the .NET 10 SDK.
# Watch Dog is compiled against Skua 1.4.5's interface assemblies (the oldest client it supports; VibeSkua 2.0.x has
# everything it uses). They are not stored in this repository: this script downloads Skua at a fixed commit into
# .deps\skua and builds just those three projects.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$skuaCommit = '967f07e4b2ed7d0180e761644e1e1319d6e5db9a'   # auqw/skua, Skua 1.4.5
$deps = Join-Path $root '.deps\skua'

if (-not (Test-Path (Join-Path $deps '.git'))) {
    Write-Host '== Downloading Skua (for its interface assemblies)'
    git clone -q https://github.com/auqw/skua.git $deps
}
git -C $deps fetch -q origin
git -C $deps checkout -q -f $skuaCommit

Write-Host '== Building Skua.Core.Interfaces'
dotnet build (Join-Path $deps 'Skua.Core.Interfaces\Skua.Core.Interfaces.csproj') -c Release -p:EnableWindowsTargeting=true -v quiet
$lib = Join-Path $root '.deps\lib'
New-Item -ItemType Directory -Force $lib | Out-Null
Copy-Item -Force (Join-Path $deps 'Skua.Core.Interfaces\bin\Release\net10.0-windows\Skua.Core.Interfaces.dll') $lib
Copy-Item -Force (Join-Path $deps 'Skua.Core.Models\bin\Release\net10.0\Skua.Core.Models.dll') $lib
Copy-Item -Force (Join-Path $deps 'Skua.Core.Utils\bin\Release\net10.0\Skua.Core.Utils.dll') $lib

Write-Host '== Building Watch Dog'
dotnet build (Join-Path $root 'src\WatchDog.csproj') -c Release "-p:SkuaLib=$lib" -v quiet
$out = Join-Path $root 'src\bin\Release\net10.0-windows\WatchDog.dll'
Write-Host "== Done: $out"
