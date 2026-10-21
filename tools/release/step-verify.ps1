# Release step "verify": the pre-publish checks on dist\<ver>\ (ASCII only). It only reads, so it is always safe to run again.
#   - every expected file is there (MSI, portable zip, docs zip, source zip, notes, build info, checksums, SBOM...);
#   - SHA256SUMS.txt verifies, and lists every file in the folder;
#   - the version agrees everywhere: the MSI, BUILD-INFO.txt, the source's Directory.Build.props, the release notes;
#   - the build passed: every unit test passed and both self-tests (release build and MSI payload) have no failures.
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$dist = Get-DistDir $Version
if (-not (Test-Path $dist)) { throw "dist\$Version does not exist: build it first (build.ps1)" }
$rf = Get-ReleaseFiles $Version
$name = Get-SourceName $Version
$missing = @($rf.Values | Where-Object { -not (Test-Path (Join-Path $dist $_)) })
if ($missing.Count) { throw "missing from dist\${Version}: $($missing -join ', ')" }

# Checksums: every line verifies, and every file is listed (only this pipeline's own record and manifest may be unlisted).
$listed = @{}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $dist 'SHA256SUMS.txt'))) {
    if (-not $line.Trim()) { continue }
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "SHA256SUMS.txt: unreadable line '$line'" }
    $want = $Matches[1]; $file = $Matches[2]
    $path = Join-Path $dist $file.Replace('/', '\')
    if (-not (Test-Path $path)) { throw "SHA256SUMS.txt lists $file, which is missing" }
    if ((Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $want) { throw "checksum mismatch: $file" }
    $listed[$file] = $true
}
$unlisted = @(Get-ChildItem $dist -Recurse -File | ForEach-Object { $_.FullName.Substring($dist.Length + 1).Replace('\', '/') } |
    Where-Object { $_ -ne 'SHA256SUMS.txt' -and $_ -ne 'release-state.json' -and $_ -ne 'release-manifest.json' -and -not $listed.ContainsKey($_) })
if ($unlisted.Count) { throw "files in dist\$Version that SHA256SUMS.txt does not cover: $($unlisted -join ', ')" }

# The version, everywhere it is written.
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @([string](Join-Path $dist $rf.installer), [int]0))
$view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Value FROM Property WHERE Property='ProductVersion'"))
[void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
$record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
$msiVersion = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
[void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($db)
if ($msiVersion -ne $Version) { throw "the MSI says $msiVersion, not $Version" }
$info = [IO.File]::ReadAllText((Join-Path $dist $rf.buildinfo))
if ($info -notmatch "(?m)^version: $([regex]::Escape($Version))\s*$") { throw "BUILD-INFO.txt is not for $Version" }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $dist $rf.source))
try {
    function Read-Entry([string]$path) {
        $e = $zip.GetEntry("$name/$path")
        if (-not $e) { throw "the source zip has no $path (expected under $name/)" }
        $r = New-Object IO.StreamReader($e.Open()); try { $r.ReadToEnd() } finally { $r.Dispose() }
    }
    if ((Read-Entry 'Directory.Build.props') -notmatch "<Version>$([regex]::Escape($Version))</Version>") { throw "the source's Directory.Build.props is not $Version" }
    $readme = Read-Entry 'README.md'
    if ($readme -match 'badge/version-([0-9.]+)-' -and $Matches[1] -ne $Version) { throw "the README badge says $($Matches[1]), not $Version" }
    if ((Read-Entry "docs/release-notes/$Version.md") -notmatch "^# DarkCipher AgentControl $([regex]::Escape($Version)) ") { throw "docs/release-notes/$Version.md does not start with '# DarkCipher AgentControl $Version'" }
    if (@($zip.Entries | Where-Object { $_.FullName.Contains('\') }).Count) { throw 'the source zip has backslash paths' }
}
finally { $zip.Dispose() }

# The build passed its own tests.
if ($info -notmatch '(?m)^tests: Passed!\s+-\s+Failed:\s+0, Passed:\s+(\d+)') { throw 'BUILD-INFO.txt: the tests did not all pass' }
$tests = $Matches[1]
if ($info -notmatch '(?m)^selfTest: .*?(\d+) ok, 0 failed') { throw 'BUILD-INFO.txt: the self-test had failures' }
$self = $Matches[1]
if ($info -notmatch '(?m)^msiPayloadSelfTest: .*?\d+ ok, 0 failed') { throw 'BUILD-INFO.txt: the MSI payload self-test had failures' }

New-StepResult 'done' "$($listed.Count) files match SHA256SUMS; MSI, build info, source and notes all say $Version; $tests tests and $self self-test checks passed (release build and MSI payload)"
