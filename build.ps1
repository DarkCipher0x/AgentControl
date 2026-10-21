# DarkCipher AgentControl - release build and packaging (ASCII only; Windows PowerShell 5.1 compatible).
#
#  1. locked restore (+ local WiX tool)      6. headless self-test (scratch data folder, no windows)
#  2. Release build                          7. package: self-contained zip, per-machine MSI, SDK nupkg, dcx-ext zip,
#  3. full test suite                           sample .dcxext packages, docs zip, release notes, build info
#  4. NuGet vulnerability scan               8. MSI ICE validation + administrative extract + self-test of the extract
#  5. SBOM (CycloneDX) + licence report      9. the sanitised public source zip (allowlist, privacy scan, gitleaks)
#                                           10. SHA256SUMS.txt for every artefact, then re-verified from disk
# Publishing is a separate command: release.ps1 (docs\RELEASE.md).
#
# Packages (.nuget) and output (dist\<version>\, never overwritten) stay in this folder. Temp files, staging and
# self-test scratch go to the developer scratch folder (DCAC_DEV_SCRATCH, default %USERPROFILE%\.dcac-dev),
# because this repository sits in %USERPROFILE%\Projects, which the app protects by default.
# Nothing is signed: no code-signing certificate exists.
param([switch]$SkipSelfTest)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$devScratch = $env:DCAC_DEV_SCRATCH
if ([string]::IsNullOrWhiteSpace($devScratch)) { $devScratch = Join-Path $env:USERPROFILE ".dcac-dev" }
$env:TEMP = Join-Path $devScratch "tmp"; $env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Step($name) { Write-Host ""; Write-Host "==> $name" -ForegroundColor Cyan }
function Check($code, $what) { if ($code -ne 0) { throw "$what failed (exit $code)" } }
function Sha($path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }
function Zip($dir, $zip) { if (Test-Path $zip) { Remove-Item $zip }; [IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false) }
function WriteText($path, $text) { [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false)) }

function RunSelfTest($exe, $label) {
    $scratch = Join-Path $devScratch ("selftest-" + $label)
    if (Test-Path $scratch) { Remove-Item -Recurse -Force -Confirm:$false $scratch }
    New-Item -ItemType Directory -Force (Join-Path $scratch "data") | Out-Null
    $env:DCAC_DATA_DIR = Join-Path $scratch "data"
    try { $p = Start-Process -FilePath $exe -ArgumentList @("--selftest", (Join-Path $scratch "report")) -Wait -PassThru }
    finally { Remove-Item Env:DCAC_DATA_DIR -ErrorAction SilentlyContinue }
    $report = [IO.File]::ReadAllText((Join-Path $scratch "report\report.txt"))
    return @{ Code = $p.ExitCode; Report = $report; Summary = ($report -split "`n")[0].Trim() }
}

$version = ([xml](Get-Content -Raw (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
$dist = Join-Path $root "dist\$version"
if (Test-Path $dist) { throw "dist\$version already exists. Bump <Version> in Directory.Build.props (every build gets a new version)." }
$stage = Join-Path $devScratch "stage\$version"
if (Test-Path $stage) { Remove-Item -Recurse -Force -Confirm:$false $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Write-Host "DarkCipher AgentControl $version"
$info = [ordered]@{ product = "DarkCipher AgentControl"; version = $version; built = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss zzz"); dotnetSdk = (dotnet --version); signed = "NO - no code-signing certificate exists" }

Step "Locked restore + local tools"
Push-Location $root
try {
    dotnet restore (Join-Path $root "DarkCipherAgentControl.sln") -p:LockedRestore=true
    Check $LASTEXITCODE "Restore"
    dotnet tool restore | Out-Null
    Check $LASTEXITCODE "Tool restore"
} finally { Pop-Location }

Step "Build (Release)"
dotnet build (Join-Path $root "DarkCipherAgentControl.sln") -c Release --no-restore -v q
Check $LASTEXITCODE "Build"

Step "Tests"
$testOut = dotnet test (Join-Path $root "tests\DarkCipher.AgentControl.Tests") -c Release --no-build -v q 2>&1 | Out-String
Write-Host $testOut.Trim()
Check $LASTEXITCODE "Tests"
$info.tests = ([regex]::Match($testOut, "(Passed!|Failed!)[^\r\n]*")).Value

Step "Vulnerability scan (NuGet advisories, transitive)"
$vuln = dotnet list (Join-Path $root "DarkCipherAgentControl.sln") package --vulnerable --include-transitive 2>&1 | Out-String
if ($vuln -match "has the following vulnerable packages") { Write-Host $vuln; throw "Vulnerable packages found" }
$info.vulnerabilityScan = "no known vulnerable packages (nuget.org advisories, transitive)"
Write-Host $info.vulnerabilityScan

if (-not $SkipSelfTest) {
    Step "Headless self-test (Release build)"
    $st = RunSelfTest (Join-Path $root "src\DarkCipher.AgentControl.App\bin\Release\net8.0-windows\win-x64\DarkCipherAgentControl.exe") "release"
    Write-Host $st.Report
    Check $st.Code "Self-test"
    $info.selfTest = $st.Summary
}

New-Item -ItemType Directory -Force $dist | Out-Null

Step "Publish self-contained app"
$app = Join-Path $stage "app"
dotnet publish (Join-Path $root "src\DarkCipher.AgentControl.App") -c Release -r win-x64 --self-contained true -p:DebugType=none -o $app -v q
Check $LASTEXITCODE "Publish"
Zip $app (Join-Path $dist "DarkCipherAgentControl-$version-win-x64.zip")

Step "MSI (per-machine, Program Files\DarkCipher Systems, WiX 5)"
$msi = Join-Path $dist "DarkCipherAgentControl-$version-win-x64.msi"
Push-Location $root
try {
    $iconFile = Join-Path $root "src\DarkCipher.AgentControl.App\Assets\AppIcon\app.ico"
    dotnet tool run wix build "installer\DarkCipherAgentControl.wxs" -arch x64 -d "Version=$version" -d "PublishDir=$app" -d "IconFile=$iconFile" -o $msi
    Check $LASTEXITCODE "wix build"
    # ICE38/43/57 assume Start-menu shortcuts are per-user; in a per-machine package the shortcuts go to the all-users
# Start menu and the HKLM key path is correct, so these three are suppressed. Every other ICE still runs.
dotnet tool run wix msi validate -sice ICE38 -sice ICE43 -sice ICE57 $msi
    Check $LASTEXITCODE "MSI ICE validation"
} finally { Pop-Location }
Remove-Item (Join-Path $dist "*.wixpdb") -ErrorAction SilentlyContinue
$info.msiValidation = "ICE validation passed"

Step "Verify MSI by administrative extract (nothing is installed)"
$extract = Join-Path $stage "msi-extract"
$p = Start-Process -FilePath "msiexec.exe" -ArgumentList @("/a", "`"$msi`"", "/qn", "TARGETDIR=`"$extract`"") -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Administrative extract failed (msiexec exit $($p.ExitCode))" }
$extractedExe = Get-ChildItem -Recurse -Path $extract -Filter "DarkCipherAgentControl.exe" | Select-Object -First 1
if (-not $extractedExe) { throw "The MSI does not contain DarkCipherAgentControl.exe" }
$appFiles = (Get-ChildItem -Recurse -File $app).Count
$msiFiles = (Get-ChildItem -Recurse -File $extractedExe.Directory.FullName).Count
if ($appFiles -ne $msiFiles) { throw "MSI contains $msiFiles files but the publish folder has $appFiles" }
if ((Sha $extractedExe.FullName) -ne (Sha (Join-Path $app "DarkCipherAgentControl.exe"))) { throw "MSI exe differs from the published exe" }
if (-not $SkipSelfTest) {
    $st2 = RunSelfTest $extractedExe.FullName "msi"
    Check $st2.Code "Self-test of the MSI payload"
    $info.msiPayloadSelfTest = $st2.Summary
    Write-Host $st2.Summary
}
$info.msiExtract = "administrative extract OK; $msiFiles files, identical exe hash to the published app"

Step "SDK NuGet package"
# Packs from a fresh Release build (the publish step above builds dependencies without symbols).
dotnet pack (Join-Path $root "src\DarkCipher.AgentControl.Sdk") -c Release -o $dist -v q
Check $LASTEXITCODE "SDK pack"

Step "dcx-ext tool"
$tool = Join-Path $stage "dcx-ext"
dotnet publish (Join-Path $root "tools\DarkCipher.AgentControl.ExtTool") -c Release -o $tool -p:DebugType=none -v q
Check $LASTEXITCODE "dcx-ext publish"
Zip $tool (Join-Path $dist "dcx-ext-$version-win-x64.zip")

Step "Sample extension packages"
$dcx = Join-Path $tool "dcx-ext.exe"
$samples = @(
    @{ Src = "samples\stack-template-rust"; Out = "com.example.rust-stacks-1.0.0.dcxext" },
    @{ Src = "samples\model-source-coding"; Out = "com.example.coding-catalog-1.0.0.dcxext" },
    @{ Src = "samples\agent-detector\bin\Release\net8.0"; Out = "com.example.agent-detector-1.0.0.dcxext" },
    @{ Src = "samples\dashboard-panel\bin\Release\net8.0"; Out = "com.example.model-panel-1.0.0.dcxext" }
)
$sampleDir = Join-Path $dist "samples"
New-Item -ItemType Directory -Force $sampleDir | Out-Null
foreach ($s in $samples) {
    & $dcx pack (Join-Path $root $s.Src) (Join-Path $sampleDir $s.Out) | Out-Host
    Check $LASTEXITCODE ("pack " + $s.Out)
}

Step "SBOM and licence report"
$components = @{}
$assetFiles = @(Get-ChildItem -Path (Join-Path $root "src") -Recurse -Filter project.assets.json) + @(Get-ChildItem -Path (Join-Path $root "tools") -Recurse -Filter project.assets.json)
foreach ($f in $assetFiles) {
    $assets = [IO.File]::ReadAllText($f.FullName) | ConvertFrom-Json
    foreach ($prop in $assets.libraries.PSObject.Properties) {
        if ($prop.Value.type -ne "package") { continue }
        $parts = $prop.Name.Split("/")
        $key = $prop.Name.ToLowerInvariant()
        if ($components.ContainsKey($key)) { continue }
        $nuspec = Join-Path $root (".nuget\packages\" + $parts[0].ToLowerInvariant() + "\" + $parts[1] + "\" + $parts[0].ToLowerInvariant() + ".nuspec")
        $license = "unknown"
        if (Test-Path $nuspec) {
            $meta = ([xml][IO.File]::ReadAllText($nuspec)).package.metadata
            if ($meta.license) { $license = $meta.license.'#text' } elseif ($meta.licenseUrl) { $license = $meta.licenseUrl }
        }
        $components[$key] = [ordered]@{ type = "library"; name = $parts[0]; version = $parts[1]; purl = "pkg:nuget/$($parts[0])@$($parts[1])"; hashes = @(@{ alg = "SHA-512"; content = $prop.Value.sha512 }); licenses = @(@{ expression = $license }) }
    }
}
# The self-contained app also ships the .NET runtime packs.
$deps = [IO.File]::ReadAllText((Join-Path $app "DarkCipherAgentControl.deps.json")) | ConvertFrom-Json
foreach ($prop in $deps.libraries.PSObject.Properties) {
    if ($prop.Name -notlike "runtimepack.*") { continue }
    $parts = $prop.Name.Substring(12).Split("/")
    $components[$prop.Name.ToLowerInvariant()] = [ordered]@{ type = "framework"; name = $parts[0]; version = $parts[1]; purl = "pkg:nuget/$($parts[0])@$($parts[1])"; licenses = @(@{ expression = "MIT" }) }
}
$sbom = [ordered]@{
    bomFormat = "CycloneDX"; specVersion = "1.5"; version = 1
    metadata = [ordered]@{ timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"); component = [ordered]@{ type = "application"; name = "DarkCipher AgentControl"; version = $version; supplier = @{ name = "DarkCipher Systems" } } }
    components = @($components.Values | Sort-Object { $_.name })
}
WriteText (Join-Path $dist "sbom.cdx.json") ($sbom | ConvertTo-Json -Depth 8)
WriteText (Join-Path $dist "THIRD-PARTY-LICENCES.txt") ((@("Third-party components in DarkCipher AgentControl $version", "") + ($components.Values | Sort-Object { $_.name } | ForEach-Object { "{0} {1}  licence: {2}" -f $_.name, $_.version, $_.licenses[0].expression })) -join "`r`n")
Write-Host "$($components.Count) components recorded."

Step "Privacy gate (no user name or local user paths in shipped documents)"
$needle = $env:USERNAME
# docs\specs holds internal working specs (verbatim requests). They are never shipped (see the bundle step), so not scanned.
$leaks = Get-ChildItem -Recurse -File (Join-Path $root "docs"), (Join-Path $root "schemas") | Where-Object { $_.FullName -notlike "*\docs\specs\*" -and [IO.File]::ReadAllText($_.FullName) -match [regex]::Escape($needle) }
$leaks += Get-Item (Join-Path $root "README.md"), (Join-Path $root "CHANGELOG.md") | Where-Object { [IO.File]::ReadAllText($_.FullName) -match [regex]::Escape($needle) }
if ($leaks) { $leaks | ForEach-Object { Write-Host "  contains the user name: $($_.FullName)" -ForegroundColor Red }; throw "Privacy gate failed" }
Write-Host "No user name in shipped documents."

Step "Documentation bundle and release notes"
$docStage = Join-Path $stage "docs-bundle"
New-Item -ItemType Directory -Force $docStage | Out-Null
Copy-Item (Join-Path $root "README.md"), (Join-Path $root "CHANGELOG.md") $docStage
New-Item -ItemType Directory -Force (Join-Path $docStage "docs") | Out-Null
Get-ChildItem (Join-Path $root "docs") | Where-Object { $_.Name -ne "specs" } | Copy-Item -Destination (Join-Path $docStage "docs") -Recurse
Copy-Item -Recurse (Join-Path $root "schemas") (Join-Path $docStage "schemas")
Zip $docStage (Join-Path $dist "DarkCipherAgentControl-$version-docs.zip")
$notes = Join-Path $root "docs\release-notes\$version.md"
if (-not (Test-Path $notes)) { throw "Missing release notes: docs\release-notes\$version.md" }
Copy-Item $notes (Join-Path $dist "RELEASE-NOTES.md")
WriteText (Join-Path $dist "BUILD-INFO.txt") (($info.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }) -join "`r`n")

Step "Public source (allowlist, images, privacy scan, gitleaks)"
# The single source of truth for everything published: the public repository, the GitHub Release and the website all
# come from this zip (tools\release\write-source.ps1; release.ps1 publishes it, docs\RELEASE.md).
& (Join-Path $root "tools\release\write-source.ps1") -Version $version -OutDir $dist
if (-not $?) { throw "Public source failed" }

Step "SHA256SUMS.txt"
$files = Get-ChildItem -Recurse -File $dist | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | Sort-Object FullName
$lines = $files | ForEach-Object { "{0}  {1}" -f (Sha $_.FullName), $_.FullName.Substring($dist.Length + 1).Replace("\", "/") }
WriteText (Join-Path $dist "SHA256SUMS.txt") (($lines -join "`n") + "`n")

Step "Verify every checksum from disk"
$bad = 0
foreach ($line in [IO.File]::ReadAllLines((Join-Path $dist "SHA256SUMS.txt"))) {
    if ($line.Length -eq 0) { continue }
    $hash = $line.Substring(0, 64); $rel = $line.Substring(66)
    $ok = (Sha (Join-Path $dist $rel.Replace("/", "\"))) -eq $hash
    if (-not $ok) { $bad++; Write-Host "MISMATCH $rel" -ForegroundColor Red }
}
if ($bad -gt 0) { throw "$bad checksum mismatches" }
# The portable zip must contain exactly the published files.
$check = Join-Path $stage "zip-check"
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $dist "DarkCipherAgentControl-$version-win-x64.zip"), $check)
$diff = Compare-Object (Get-ChildItem -Recurse -File $app | ForEach-Object { $_.FullName.Substring($app.Length) + "|" + (Sha $_.FullName) }) (Get-ChildItem -Recurse -File $check | ForEach-Object { $_.FullName.Substring($check.Length) + "|" + (Sha $_.FullName) })
if ($diff) { throw "Portable zip contents differ from the publish folder" }
Write-Host "All $($files.Count) artefacts verified; zip contents identical to the published app."

Step "Done"
Get-ChildItem -Recurse -File $dist | Sort-Object FullName | ForEach-Object { "{0,12:N0}  {1}" -f $_.Length, $_.FullName.Substring($dist.Length + 1) }
Write-Host ""
Write-Host "Build $version complete: $dist  (UNSIGNED)" -ForegroundColor Green
