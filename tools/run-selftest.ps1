# Runs the headless self-test against a throw-away data folder in the developer scratch folder
# (DCAC_DEV_SCRATCH, default %USERPROFILE%\.dcac-dev). The scratch folder is kept outside the
# repository because the repository sits in %USERPROFILE%\Projects, which the app protects by default.
# Never touches the real data store (%LOCALAPPDATA%\DarkCipher\AgentControl). ASCII only (PowerShell 5.1).
param(
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$devScratch = $env:DCAC_DEV_SCRATCH
if ([string]::IsNullOrWhiteSpace($devScratch)) { $devScratch = Join-Path $env:USERPROFILE ".dcac-dev" }
$scratch = Join-Path $devScratch "selftest"
if (Test-Path $scratch) { Remove-Item -Recurse -Force -Confirm:$false $scratch }
New-Item -ItemType Directory -Force (Join-Path $scratch "data") | Out-Null

if (-not $NoBuild) {
    $env:TEMP = Join-Path $devScratch "tmp"; $env:TMP = $env:TEMP
    New-Item -ItemType Directory -Force $env:TEMP | Out-Null
    dotnet build (Join-Path $root "src\DarkCipher.AgentControl.App") -c $Configuration -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

$exe = Join-Path $root "src\DarkCipher.AgentControl.App\bin\$Configuration\net8.0-windows\win-x64\DarkCipherAgentControl.exe"
$env:DCAC_DATA_DIR = Join-Path $scratch "data"
$report = Join-Path $scratch "report"
$p = Start-Process -FilePath $exe -ArgumentList @("--selftest", $report) -Wait -PassThru
$file = Join-Path $report "report.txt"
if (Test-Path $file) { [IO.File]::ReadAllText($file) } else { "No report was written (the process crashed before the self-test started). Check the Windows Application event log." }
"Exit code: $($p.ExitCode)"
exit $p.ExitCode
