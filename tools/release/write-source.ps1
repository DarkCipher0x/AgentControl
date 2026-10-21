# Writes the public source of one version: dist\<ver>\DarkCipherAgentControl-<ver>-source.zip, one top folder
# DarkCipherAgentControl-<ver>-source/, '/' separators (ASCII only: Windows PowerShell 5.1 reads scripts as ANSI).
# build.ps1 runs it before the checksums, so every build carries the source it was built from, fit for publication.
#   - Every file in the project (minus build output and local caches) must be classified: tools\public-allowlist.txt or
#     tools\private-list.txt. An unclassified file stops the build, so nothing is published or dropped without a decision.
#   - Only the public files are staged, and the staged copy must pass every gate (Test-PublicTree: no key or package
#     files, every image approved, the privacy scan, gitleaks). Any hit stops the build and no archive is written.
# This zip is the single source of truth: the public repository, the GitHub Release and the website all come from it.
# Usage: .\tools\release\write-source.ps1 -Version 0.1.10 [-OutDir <folder>]
param([Parameter(Mandatory)][string]$Version, [string]$OutDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseLib.ps1')
if (-not $OutDir) { $OutDir = Get-DistDir $Version }
$name = Get-SourceName $Version
$root = $script:ProjectRoot

# 1. Classify every file.
$all = @(Get-TreeFiles $root)
$split = Split-ByAllowlist $all
if ($split.Unclassified.Count) {
    $split.Unclassified | ForEach-Object { Write-Host "  unclassified: $_" -ForegroundColor Red }
    throw "$($split.Unclassified.Count) file(s) are in neither tools\public-allowlist.txt nor tools\private-list.txt (above): classify them"
}
$public = @($split.Public)
if ($public.Count -lt 100) { throw "only $($public.Count) public files - is this the project root?" }

# 2. Stage the public files and run every gate on the staged copy.
$stage = Join-Path ([IO.Path]::GetTempPath()) ('dcac-source-' + [guid]::NewGuid().ToString('N'))
$top = Join-Path $stage $name
try {
    foreach ($f in $public) {
        $dest = Join-Path $top $f.Replace('/', '\')
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $f.Replace('/', '\')) -Destination $dest
    }
    $problems = @(Test-PublicTree $top $public)
    if ($problems.Count) {
        $problems | Select-Object -First 60 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "the public source failed $($problems.Count) check(s) (above); no archive was written"
    }

    # 3. The archive: entries named with '/', in a stable order.
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $zip = Join-Path $OutDir "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $fs = [IO.File]::Open($zip, 'CreateNew')
    $archive = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in ($public | Sort-Object)) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $top $f.Replace('/', '\')), "$name/$f", [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose(); $fs.Dispose() }
}
finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ("Source: {0} ({1} public files, {2} private files kept out); allowlist, images, privacy scan and gitleaks clean" -f $zip, $public.Count, $split.Private.Count)
