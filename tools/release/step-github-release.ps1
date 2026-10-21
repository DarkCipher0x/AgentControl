# Release step "release": the GitHub Release "AgentControl <ver>" on tag v<ver> (pushed by the mirror step). ASCII only.
#   Notes   docs/release-notes/<ver>.md from the staged public copy (the same text as the repository), plus the downloads
#   Files   everything in dist\<ver>\ the verify step checked: MSI, portable zip, docs zip, source zip, the extension SDK
#           package, the dcx-ext tool, the sample extensions, SBOM, licences, build info, release notes and SHA256SUMS.txt
# GitHub reports its own SHA-256 of every upload; it must equal ours. A release that exists is completed, never changed:
# missing files are uploaded, present files must match, and a mismatch stops the step (fix: a new version).
# Sign-in: the GitHub token Git Credential Manager already holds, in memory only.
param([Parameter(Mandatory)][string]$Version, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
$cfg = Get-ReleaseConfig
$api = $cfg.publicRepoApi
$dist = Get-DistDir $Version
$tag = "v$Version"
$rf = Get-ReleaseFiles $Version
$top = Join-Path (Get-StagingDir $Version) (Get-SourceName $Version)
if (-not (Test-Path $top)) { throw "no public copy staged for ${Version}: run the sanitise step first" }

# 1. The files, each already checked against SHA256SUMS by the verify step.
$assets = @($rf.installer, $rf.portable, $rf.guide, $rf.source, $rf.tool, $rf.sbom, $rf.licences, $rf.buildinfo, $rf.notes, $rf.sums) | ForEach-Object { Join-Path $dist $_ }
$assets += @(Get-ChildItem $dist -Filter '*.nupkg' | ForEach-Object { $_.FullName })
$assets += @(Get-ChildItem (Join-Path $dist 'samples') -Filter '*.dcxext' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
foreach ($a in $assets) { if (-not (Test-Path $a)) { throw "missing $a" } }
$names = @($assets | ForEach-Object { Split-Path $_ -Leaf })
if (@($names | Group-Object | Where-Object Count -gt 1).Count) { throw 'two release files have the same name' }

# 2. The notes: the release notes from the public copy, then what to download.
$notes = [IO.File]::ReadAllText((Join-Path $top "docs\release-notes\$Version.md"))
$notes = ($notes -replace '^# [^\n]*\n', '').Trim()
$sdk = (Get-ChildItem $dist -Filter '*.nupkg' | Select-Object -First 1).Name
$body = @"
$notes

## Download

| File | What it is |
|---|---|
| ``$($rf.installer)`` | The installer (Windows 10/11 x64, per-machine, one administrator approval). Upgrades any earlier version. |
| ``$($rf.portable)`` | Portable: the self-contained app, no .NET install needed. |
| ``$($rf.guide)`` | The full documentation. |
| ``$($rf.source)`` | The source this release was built from (the same files as this repository at ``$tag``). |
| ``$sdk`` | The extension SDK (contracts only). |
| ``$($rf.tool)`` | dcx-ext, the extension tooling (needs the .NET 8 runtime). |
| ``*.dcxext`` | The sample extensions (two data-only, two code). |
| ``$($rf.sbom)``, ``$($rf.licences)`` | CycloneDX SBOM and third-party licences. |
| ``$($rf.buildinfo)`` | Build date, SDK, test, self-test and installer validation results. |
| ``$($rf.sums)`` | SHA-256 of every file in the release folder. |

The installer and the exe aren't code-signed yet, so Windows SmartScreen will warn. Check the file against ``SHA256SUMS.txt`` first:
``Get-FileHash .\$($rf.installer) -Algorithm SHA256``
"@

# 3. GitHub: the tag must be there (the mirror step pushes it), then create or complete the release.
$h = Get-GitHubHeaders
$tagRef = Get-GitHubOrNull "$api/git/ref/tags/$tag" $h
if (-not $tagRef) {
    if ($DryRun) { return New-StepResult 'would-do' "would create release $tag (after the mirror step pushes the tag) with $($assets.Count) files" }
    throw "tag $tag is not on GitHub: run the mirror step first"
}
$rel = Get-GitHubOrNull "$api/releases/tags/$tag" $h
if (-not $rel) {
    if ($DryRun) { return New-StepResult 'would-do' "would create release $tag with $($assets.Count) files" }
    $json = @{ tag_name = $tag; name = "AgentControl $Version"; body = $body; draft = $false; prerelease = $false; make_latest = 'true' } | ConvertTo-Json
    $rel = Invoke-RestMethod "$api/releases" -Method Post -Headers $h -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))
    Write-Host "  created release $tag"
}
$present = @{}
foreach ($x in @($rel.assets)) { if ($x) { $present[$x.name] = $x } }
$uploaded = 0; $matched = 0; $todo = @()
foreach ($a in $assets) {
    $n = Split-Path $a -Leaf
    $local = 'sha256:' + (Get-FileHash $a -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($present.ContainsKey($n)) {
        if ($present[$n].digest -and $present[$n].digest -ne $local) { throw "GitHub already has $n with a different SHA-256 ($($present[$n].digest)). A published file is never replaced: release a new version." }
        $matched++; continue
    }
    if ($DryRun) { $todo += $n; continue }
    $up = ($rel.upload_url -replace '\{.*\}$', '') + '?name=' + [uri]::EscapeDataString($n)
    $ct = if ($n -match '\.(txt|md|json)$') { 'text/plain' } elseif ($n -like '*.zip' -or $n -like '*.dcxext' -or $n -like '*.nupkg') { 'application/zip' } else { 'application/octet-stream' }
    $r = Invoke-RestMethod $up -Method Post -Headers $h -ContentType $ct -InFile $a -TimeoutSec 1800
    if ($r.digest -and $r.digest -ne $local) { throw "GitHub stored $n with a different SHA-256 ($($r.digest))" }
    Write-Host ("  uploaded {0} ({1:N1} MB), SHA-256 confirmed" -f $n, ($r.size / 1MB))
    $uploaded++
}
if ($DryRun -and $todo.Count) { return New-StepResult 'would-do' "release $tag exists; would upload $($todo.Count) missing file(s): $($todo -join ', ')" }
# The release is complete and every file matches: record what the website publishes (dist\<ver>\release-manifest.json).
if (-not $DryRun) { & (Join-Path $PSScriptRoot 'write-manifest.ps1') -Version $Version | Out-Host }
if ($uploaded -eq 0) { return New-StepResult 'skipped' "release $tag already has all $matched files, every SHA-256 matching" }
New-StepResult 'done' "release $tag ($($rel.html_url)): $uploaded uploaded, $matched already there, every SHA-256 matching"
