param(
    [Parameter(Mandatory = $true)][int]$ReleaseNumber,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$CommitSha,
    [Parameter(Mandatory = $true)][string]$Repository,
    [string]$WorkDirectory = [IO.Path]::GetTempPath()
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GhRetry.ps1"
$env:GH_REPO = $Repository
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ReleaseNumber -lt 1 -or $CommitSha -notmatch '^[0-9a-fA-F]{40}$' -or
    $manifest.schemaVersion -ne 2 -or $manifest.assetName -ne 'JustEnoughTools.exe' -or
    [int]$manifest.releaseNumber -ne $ReleaseNumber -or $manifest.commitSha -ne $CommitSha -or
    $manifest.sha256 -ne $hash -or [long]$manifest.sizeBytes -ne (Get-Item -LiteralPath $ExecutablePath).Length) {
    throw 'The previous executable and manifest do not form a valid archive base.'
}
$tag = "update-base-$ReleaseNumber"
Invoke-Gh release view $tag --json tagName *> $null
if ($LASTEXITCODE -eq 0) {
    # An archive is immutable. A retry must prove and reuse it, not delete it first.
    $directory = Join-Path $WorkDirectory "jet-archive-check-$ReleaseNumber"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Invoke-Gh release download $tag --pattern 'update.json' --pattern 'JustEnoughTools.exe' --dir $directory --clobber | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The existing archive $tag could not be verified: $(Get-GhFailureText)" }
    $existing = Get-Content -LiteralPath (Join-Path $directory 'update.json') -Raw | ConvertFrom-Json
    $existingExe = Join-Path $directory 'JustEnoughTools.exe'
    if ($existing.schemaVersion -ne 2 -or $existing.assetName -ne 'JustEnoughTools.exe' -or
        $existing.commitSha -ne $CommitSha -or [int]$existing.releaseNumber -ne $ReleaseNumber -or
        $existing.sha256 -ne $hash -or [long]$existing.sizeBytes -ne (Get-Item -LiteralPath $ExecutablePath).Length -or
        (Get-FileHash -LiteralPath $existingExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash -or
        (Get-Item -LiteralPath $existingExe).Length -ne [long]$existing.sizeBytes) {
        throw "Existing archive $tag has different bytes or provenance; refusing to overwrite it."
    }
    Write-Host "Reused verified archive $tag."
    return
}
if (-not (Test-GhNotFoundFailure (Get-GhFailureText))) { throw "Archive lookup failed: $(Get-GhFailureText)" }
Invoke-Gh release create $tag $ExecutablePath $ManifestPath --target $CommitSha --prerelease `
    --title "Update base $ReleaseNumber" --notes "Verified binary base for commit $CommitSha."
if ($LASTEXITCODE -ne 0) { throw "Archive $tag could not be created: $(Get-GhFailureText)" }
