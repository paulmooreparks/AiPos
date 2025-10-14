<#!
.SYNOPSIS
    Sync (clone or update) a read-only snapshot of the legacy PosKernel repository into .legacy-poskernel/cache.
.DESCRIPTION
    Maintains a quarantined reference copy of the deprecated PosKernel code so we can inspect old implementations
    without polluting the active AiPos workspace. The snapshot is stripped of build outputs and git history.

    Architectural Principle: Legacy references must not silently drift into active code; this is a forensic mirror only.
.PARAMETER RepoUrl
    Remote URL of the legacy PosKernel repository.
.PARAMETER Ref
    Branch, tag, or commit SHA to fetch. Defaults to 'main'.
#>
[CmdletBinding()]
param(
    [string]$RepoUrl = "https://github.com/paulmooreparks/PosKernel.git",
    [string]$Ref = "main"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path | Split-Path -Parent
$legacyRoot = Join-Path $root '.legacy-poskernel'
$cache = Join-Path $legacyRoot 'cache'
$manifestPath = Join-Path $legacyRoot 'MANIFEST.json'

Write-Host "[legacy-sync] Preparing legacy PosKernel reference cache..."
if (-not (Test-Path $legacyRoot)) { New-Item -ItemType Directory -Path $legacyRoot | Out-Null }
if (-not (Test-Path $cache)) { New-Item -ItemType Directory -Path $cache | Out-Null }

$tempClone = Join-Path $env:TEMP "poskernel_legacy_clone_$(Get-Date -Format yyyyMMddHHmmss)"
Write-Host "[legacy-sync] Cloning $RepoUrl (ref: $Ref) -> $tempClone"
& git clone --depth 1 --branch $Ref $RepoUrl $tempClone | Out-Null

# Remove existing cache contents (safe since this dir is gitignored)
Get-ChildItem -Force $cache | Remove-Item -Force -Recurse

# Copy needed source (exclude .git, bin, obj, target, node_modules if any)
$excludes = @('.git','bin','obj','target','node_modules','.idea','.vs')
Get-ChildItem -Force -Recurse $tempClone | ForEach-Object {
    $rel = $_.FullName.Substring($tempClone.Length).TrimStart('\\','/')
    if (-not $rel) { return }
    $first = $rel.Split([IO.Path]::DirectorySeparatorChar)[0]
    if ($excludes -contains $first) { return }
    if ($_.PsIsContainer) {
        $destDir = Join-Path $cache $rel
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir | Out-Null }
    } else {
        $destFile = Join-Path $cache $rel
        $dir = Split-Path -Parent $destFile
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        Copy-Item $_.FullName $destFile
    }
}

# Gather minimal manifest information
Push-Location $tempClone
$commit = (& git rev-parse HEAD).Trim()
$commitDate = (& git log -1 --format=%cI).Trim()
$short = (& git rev-parse --short HEAD).Trim()
Pop-Location

# Build file inventory hash
$files = Get-ChildItem -Recurse -File $cache | Select-Object FullName, Length | Sort-Object FullName
$hashInput = ($files | ForEach-Object { "{0}:{1}" -f ($_.FullName.Substring($cache.Length)), $_.Length }) -join "`n"
$sha256 = [System.BitConverter]::ToString((New-Object System.Security.Cryptography.SHA256Managed).ComputeHash([Text.Encoding]::UTF8.GetBytes($hashInput))).Replace('-','').ToLowerInvariant()

$manifest = [ordered]@{
    repo = $RepoUrl
    ref = $Ref
    commit = $commit
    commitShort = $short
    commitDate = $commitDate
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    fileCount = $files.Count
    contentHashSha256 = $sha256
}
$manifest | ConvertTo-Json -Depth 3 | Out-File -FilePath $manifestPath -Encoding UTF8

Remove-Item -Recurse -Force $tempClone
Write-Host "[legacy-sync] Legacy PosKernel snapshot updated (commit $short)"
Write-Host "[legacy-sync] Files: $($files.Count)  Hash: $sha256"
Write-Host "[legacy-sync] Manifest: $manifestPath"
