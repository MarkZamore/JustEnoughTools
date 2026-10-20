param(
    [Parameter(Mandatory = $true)][int]$ReleaseNumber,
    [Parameter(Mandatory = $true)][string]$CommitSha,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$PreviousInfoFile,
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [Parameter(Mandatory = $true)][string]$Repository
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GhRetry.ps1"
$env:GH_REPO = $Repository
$previous = Get-Content -LiteralPath $PreviousInfoFile -Raw | ConvertFrom-Json
$expectedHash = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
$expectedSize = (Get-Item -LiteralPath $ExecutablePath).Length
if ($ReleaseNumber -lt 1 -or $CommitSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'Invalid release identity.' }

function Sync-LatestTag {
    $refText = Invoke-Gh api "repos/$Repository/git/ref/tags/latest"
    if ($LASTEXITCODE -ne 0) { throw "Latest tag lookup failed: $(Get-GhFailureText)" }
    if (($refText | ConvertFrom-Json).object.sha -ne $CommitSha) {
        Invoke-Gh api --method PATCH "repos/$Repository/git/refs/tags/latest" -f "sha=$CommitSha" -F 'force=true' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Latest tag could not be advanced: $(Get-GhFailureText)" }
    }
}
function Remove-ObsoleteReleaseFiles([string[]]$Keep) {
    $assetText = Invoke-Gh release view latest --json assets
    if ($LASTEXITCODE -ne 0) { throw "Published assets could not be read: $(Get-GhFailureText)" }
    foreach ($asset in ($assetText | ConvertFrom-Json).assets) {
        if ($asset.name -notin $Keep) {
            Invoke-Gh release delete-asset latest $asset.name --yes
            if ($LASTEXITCODE -ne 0) { throw "Stale release asset could not be removed: $(Get-GhFailureText)" }
        }
    }
    $rows = Invoke-Gh release list --limit 100 --json tagName
    if ($LASTEXITCODE -ne 0) { throw "Archive list could not be read: $(Get-GhFailureText)" }
    $archiveTags = ($rows | ConvertFrom-Json).tagName | Where-Object { $_ -match '^update-base-([1-9][0-9]*)$' } |
        Sort-Object { [int]($_ -replace '^update-base-', '') } -Descending
    foreach ($tag in @($archiveTags | Select-Object -Skip 2)) {
        Invoke-Gh release delete $tag --cleanup-tag --yes
        if ($LASTEXITCODE -ne 0) { throw "Old archive $tag could not be removed: $(Get-GhFailureText)" }
    }
}

$releaseInfoText = Invoke-Gh release view latest --json 'tagName,isDraft'
$exists = $LASTEXITCODE -eq 0
if (-not $exists -and -not (Test-GhNotFoundFailure (Get-GhFailureText))) {
    throw "Latest release lookup failed: $(Get-GhFailureText)"
}
if ($exists) {
    # Re-check before mutation, including retries and reruns of an old workflow.
    $checkDirectory = Join-Path $WorkDirectory 'jet-publish-check'
    New-Item -ItemType Directory -Path $checkDirectory -Force | Out-Null
    Invoke-Gh release download latest --pattern 'update.json' --dir $checkDirectory --clobber | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $live = $null
        try {
            $livePath = Join-Path $checkDirectory 'update.json'
            if ((Get-Item -LiteralPath $livePath).Length -gt 64KB) { throw 'Manifest is too large.' }
            $candidate = Get-Content -LiteralPath $livePath -Raw | ConvertFrom-Json
            if ($candidate.schemaVersion -ne 2 -or $candidate.assetName -ne 'JustEnoughTools.exe' -or
                [int]$candidate.releaseNumber -lt 1 -or $candidate.commitSha -notmatch '^[0-9a-fA-F]{40}$' -or
                $candidate.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$candidate.sizeBytes -le 0 -or
                [long]$candidate.sizeBytes -gt 64MB) { throw 'Manifest identity is invalid.' }
            $live = $candidate
        }
        catch { Write-Host "Repairing an invalid live manifest with the verified full release: $($_.Exception.Message)" }
        if ($null -ne $live -and [int]$live.releaseNumber -gt $ReleaseNumber) { throw 'Refusing to replace a newer published release.' }
        if ($null -ne $live -and [int]$live.releaseNumber -eq $ReleaseNumber) {
            if ($live.commitSha -ne $CommitSha -or $live.sha256 -ne $expectedHash -or
                [long]$live.sizeBytes -ne $expectedSize) { throw 'An existing release number is immutable.' }
            if ($previous.AlreadyPublished) {
                if (($releaseInfoText | ConvertFrom-Json).isDraft) {
                    Invoke-Gh release edit latest --draft=false --latest --target $CommitSha
                    if ($LASTEXITCODE -ne 0) { throw "The completed draft could not be published: $(Get-GhFailureText)" }
                }
                Sync-LatestTag
                $patches = @($live.deltaPatches)
                if ($patches.Count -gt 2 -or @($patches | Where-Object { $_.assetName -notmatch '^JustEnoughTools(?:\.from-[1-9][0-9]*)?\.bsdiff$' }).Count) {
                    throw 'The live delta manifest is invalid.'
                }
                Remove-ObsoleteReleaseFiles (@('JustEnoughTools.exe', 'update.json') + @($patches | ForEach-Object { $_.assetName }))
                Write-Host 'The exact release was retained and interrupted cleanup completed.'
                return
            }
        }
    }
    elseif (-not ((Test-GhNotFoundFailure (Get-GhFailureText)) -or
                   (Get-GhFailureText) -match '(?i)no assets match|no asset.*found')) {
        throw "Live manifest lookup failed: $(Get-GhFailureText)"
    }
}
if ($previous.AlreadyPublished) { throw 'The live release changed after capture; rerun the workflow.' }
& "$PSScriptRoot/Verify-ReleaseAssets.ps1" -ArtifactDirectory $ArtifactDirectory -ReleaseNumber $ReleaseNumber -CommitSha $CommitSha
$publishedExe = Join-Path $ArtifactDirectory 'JustEnoughTools.exe'
if ((Get-FileHash -LiteralPath $publishedExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'The release payload is not the committed root executable.'
}
if (-not $exists) {
    Invoke-Gh release create latest --draft --target $CommitSha --title 'Latest JustEnoughTools.exe' `
        --notes "Release $ReleaseNumber from commit $CommitSha."
    if ($LASTEXITCODE -ne 0) { throw "Latest release could not be created: $(Get-GhFailureText)" }
}
$payload = @($publishedExe)
$payload += Get-ChildItem -LiteralPath $ArtifactDirectory -Filter '*.bsdiff' -File | Sort-Object Name | Select-Object -ExpandProperty FullName
Invoke-Gh release upload latest @payload --clobber
if ($LASTEXITCODE -ne 0) { throw "Release payload upload failed: $(Get-GhFailureText)" }
# The manifest is the pointer. Publish it after all files it describes exist.
Invoke-Gh release upload latest (Join-Path $ArtifactDirectory 'update.json') --clobber
if ($LASTEXITCODE -ne 0) { throw "Update manifest upload failed: $(Get-GhFailureText)" }
Invoke-Gh release edit latest --draft=false --latest --target $CommitSha --title 'Latest JustEnoughTools.exe' `
    --notes "Release $ReleaseNumber from commit $CommitSha."
if ($LASTEXITCODE -ne 0) { throw "Latest release could not be finalized: $(Get-GhFailureText)" }
Sync-LatestTag

Remove-ObsoleteReleaseFiles (@('JustEnoughTools.exe', 'update.json') + @($payload | ForEach-Object { [IO.Path]::GetFileName($_) }))
Write-Host "Published release $ReleaseNumber."
