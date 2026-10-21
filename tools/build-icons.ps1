# Regenerates the application icon set (SVG master, PNGs, multi-frame ICO) from tools\DarkCipher.AgentControl.IconBuilder.
# Output: src\DarkCipher.AgentControl.App\Assets\AppIcon. The files are checked in; tests verify them.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scratch = if ($env:DCAC_DEV_SCRATCH) { $env:DCAC_DEV_SCRATCH } else { Join-Path $env:USERPROFILE '.dcac-dev' }
$env:TEMP = Join-Path $scratch 'tmp'; $env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
$out = Join-Path $root 'src\DarkCipher.AgentControl.App\Assets\AppIcon'
dotnet run --project (Join-Path $root 'tools\DarkCipher.AgentControl.IconBuilder') -c Release -- $out
if ($LASTEXITCODE -ne 0) { throw "Icon builder failed ($LASTEXITCODE)" }
