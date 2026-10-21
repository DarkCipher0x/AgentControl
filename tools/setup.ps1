# DarkCipher AgentControl - developer bootstrap. Checks and fetches everything a build needs.
# ASCII only (Windows PowerShell 5.1). Safe to re-run. Default mode only CHECKS and restores packages.
#
#   .\tools\setup.ps1                  check prerequisites, restore NuGet packages and the local WiX tool
#   .\tools\setup.ps1 -InstallMissing  also install a missing .NET 8 SDK with winget (official source only)
#
# Installing software changes the machine, so it only happens when -InstallMissing is passed.
# Packages stay in .nuget\packages inside this folder; scratch goes to DCAC_DEV_SCRATCH
# (default %USERPROFILE%\.dcac-dev), never inside the repository.
param([switch]$InstallMissing)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$devScratch = $env:DCAC_DEV_SCRATCH
if ([string]::IsNullOrWhiteSpace($devScratch)) { $devScratch = Join-Path $env:USERPROFILE ".dcac-dev" }
$env:TEMP = Join-Path $devScratch "tmp"; $env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"
New-Item -ItemType Directory -Force $env:TEMP | Out-Null

$problems = New-Object System.Collections.Generic.List[string]
function Ok($m)   { Write-Host "  [ok]   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [warn] $m" -ForegroundColor Yellow }
function Bad($m)  { Write-Host "  [FAIL] $m" -ForegroundColor Red; $problems.Add($m) }
function Step($m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }

Step "Operating system"
if ([Environment]::OSVersion.Version.Major -ge 10 -and [Environment]::Is64BitOperatingSystem) { Ok "Windows 10/11 x64" } else { Bad "Windows 10/11 x64 is required" }

Step ".NET SDK (global.json pins 8.0 latestPatch)"
$want = (Get-Content (Join-Path $root "global.json") -Raw | ConvertFrom-Json).sdk.version
function HasSdk8 { try { (& dotnet --list-sdks 2>$null) -match '^8\.0\.' } catch { $false } }
if (-not (HasSdk8)) {
    if ($InstallMissing) {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Bad "winget not found; install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0" }
        else {
            Write-Host "  installing Microsoft.DotNet.SDK.8 from the winget source ..."
            winget install --id Microsoft.DotNet.SDK.8 --source winget --exact --accept-package-agreements --accept-source-agreements
            $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
        }
    } else { Bad ".NET 8 SDK missing. Re-run with -InstallMissing, or install it from https://dotnet.microsoft.com/download/dotnet/8.0" }
}
if (HasSdk8) {
    Ok ("SDK " + (& dotnet --version) + " (pinned $want, rollForward latestPatch)")
    Push-Location $root
    try { $v = (& dotnet --version); if ($LASTEXITCODE -ne 0) { Bad "global.json cannot be satisfied by the installed SDKs" } } finally { Pop-Location }
}

if ($problems.Count -eq 0) {
    Step "Local tools (WiX 5.0.2 for the MSI)"
    Push-Location $root
    try {
        dotnet tool restore | Out-Null
        if ($LASTEXITCODE -eq 0) { Ok "wix restored" } else { Bad "dotnet tool restore failed (offline?)" }

        Step "NuGet packages (locked restore, official feed only, into .nuget\packages)"
        dotnet restore DarkCipherAgentControl.sln -p:LockedRestore=true -v q
        if ($LASTEXITCODE -eq 0) { Ok "locked restore succeeded (hashes match packages.lock.json)" } else { Bad "locked restore failed" }
    } finally { Pop-Location }
}

Step "Optional components (never installed by this script)"
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) { Ok "Git $((& git --version) -replace 'git version ','')" } else { Warn "Git not found (optional; agents use it)" }
$oll = Get-Command ollama -ErrorAction SilentlyContinue
if ($oll) { Ok "Ollama $((& ollama --version 2>$null) -join ' ')" } else { Warn "Ollama not found (optional; needed for local models. Install from https://ollama.com/download)" }
$cc = Join-Path $env:USERPROFILE ".local\bin\claude.exe"
if (Test-Path $cc) { Ok "Claude Code present" } else { Warn "Claude Code not found (optional)" }
$free = [math]::Round((Get-PSDrive -Name ($root.Substring(0,1))).Free / 1GB, 1)
if ($free -ge 5) { Ok "$free GB free on the project drive" } else { Warn "only $free GB free; builds need about 5 GB" }

Write-Host ""
if ($problems.Count -eq 0) { Write-Host "Ready. Next: .\build.ps1 (bump <Version> in Directory.Build.props first)." -ForegroundColor Green; exit 0 }
Write-Host "Not ready:" -ForegroundColor Red; $problems | ForEach-Object { Write-Host "  - $_" }; exit 1
