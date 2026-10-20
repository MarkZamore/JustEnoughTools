param(
    [Parameter(Mandatory = $true)][int]$CommitNumber,
    [Parameter(Mandatory = $true)][string]$CommitSha,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$BasesFile,
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [Parameter(Mandatory = $true)][string]$Repository
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GhRetry.ps1"
$env:GH_REPO = $Repository
if ($CommitNumber -lt 1 -or $CommitSha -notmatch '^[0-9a-fA-F]{40}$' -or
    -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'Invalid release inputs.' }
$currentCommit = $CommitSha.ToLowerInvariant()
$currentHash = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
$currentSize = (Get-Item -LiteralPath $ExecutablePath).Length
$outcome = [ordered]@{
    HasPrevious = $false; AlreadyPublished = $false; ReleaseNumber = $CommitNumber
    PreviousExe = ''; PreviousManifest = ''; PreviousCommit = ''; PreviousSha256 = ''; PreviousReleaseNumber = 0
}
'[]' | Set-Content -LiteralPath $BasesFile -Encoding UTF8

function Throw-GhRequestFailure([string]$Message) {
    $error = [InvalidOperationException]::new("$Message $(Get-GhFailureText)")
    $error.Data['GitHubRequestFailure'] = $true
    throw $error
}
function Throw-ImmutableFailure([string]$Message) {
    $error = [InvalidOperationException]::new($Message)
    $error.Data['ImmutableReleaseFailure'] = $true
    throw $error
}
function Download-Asset([string]$Tag, [string]$Name, [string]$Directory) {
    Invoke-Gh release download $Tag --pattern $Name --dir $Directory --clobber | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $problem = Get-GhFailureText
        if ((Test-GhNotFoundFailure $problem) -or $problem -match '(?i)no assets match|no asset.*found') {
            throw "Release $Tag is missing $Name."
        }
        Throw-GhRequestFailure "Could not download $Name from $Tag."
    }
}
function Get-ValidatedBase([string]$Tag, [string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    Download-Asset $Tag 'update.json' $Directory
    $manifestPath = Join-Path $Directory 'update.json'
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 64KB) { throw 'The base manifest is too large.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $commit = ([string]$manifest.commitSha).ToLowerInvariant()
    $sha = ([string]$manifest.sha256).ToLowerInvariant()
    $size = [long]$manifest.sizeBytes
    $number = [int]$manifest.releaseNumber
    if ($manifest.schemaVersion -ne 2 -or $manifest.assetName -ne 'JustEnoughTools.exe' -or
        $commit -notmatch '^[0-9a-f]{40}$' -or $sha -notmatch '^[0-9a-f]{64}$' -or
        $size -le 0 -or $size -gt 64MB -or $number -lt 1) { throw "Release $Tag has an invalid manifest." }
    if ($Tag -eq 'latest') {
        if ($number -gt $CommitNumber) { Throw-ImmutableFailure 'This checkout is older than the published latest release.' }
        if ($number -eq $CommitNumber -and
            ($commit -ne $currentCommit -or $sha -ne $currentHash -or $size -ne $currentSize)) {
            Throw-ImmutableFailure 'An existing release number cannot be reused for another commit or executable.'
        }
    }
    Download-Asset $Tag 'JustEnoughTools.exe' $Directory
    $exe = Join-Path $Directory 'JustEnoughTools.exe'
    if ((Get-Item -LiteralPath $exe).Length -ne $size -or
        (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha) {
        throw "Release $Tag executable does not match its manifest."
    }
    [pscustomobject]@{
        tag = $Tag; executablePath = $exe; manifestPath = $manifestPath
        releaseNumber = $number; commitSha = $commit; sha256 = $sha; sizeBytes = $size
    }
}

# Do not mistake an inaccessible repository or exhausted retries for a first release.
$resolved = Invoke-Gh api "repos/$Repository" --jq '.full_name'
if ($LASTEXITCODE -ne 0) { Throw-GhRequestFailure 'The repository could not be accessed.' }
if ([string]$resolved -ne $Repository) { throw 'GitHub resolved an unexpected repository.' }
$releaseInfoText = Invoke-Gh release view latest --json 'tagName,isDraft'
if ($LASTEXITCODE -ne 0) {
    if (-not (Test-GhNotFoundFailure (Get-GhFailureText))) { Throw-GhRequestFailure 'Latest release lookup failed.' }
    Write-Host 'First release: publishing a full executable without deltas.'
    return [pscustomobject]$outcome
}
$releaseInfo = $releaseInfoText | ConvertFrom-Json

try { $previous = Get-ValidatedBase 'latest' (Join-Path $WorkDirectory 'jet-base-latest') }
catch {
    if ($_.Exception.Data['GitHubRequestFailure'] -or $_.Exception.Data['ImmutableReleaseFailure']) { throw }
    Write-Host "Latest is incomplete and cannot be a delta base: $($_.Exception.Message). Publishing a full executable."
    return [pscustomobject]$outcome
}
$outcome.HasPrevious = $true
$outcome.PreviousExe = $previous.executablePath
$outcome.PreviousManifest = $previous.manifestPath
$outcome.PreviousCommit = $previous.commitSha
$outcome.PreviousSha256 = $previous.sha256
$outcome.PreviousReleaseNumber = $previous.releaseNumber
if ($previous.releaseNumber -eq $CommitNumber) {
    $outcome.AlreadyPublished = -not [bool]$releaseInfo.isDraft
    if ($releaseInfo.isDraft) {
        $outcome.HasPrevious = $false
        Write-Host 'This exact release is still a draft; completing its publication.'
    }
    else { Write-Host 'This exact commit and executable are already published.' }
    return [pscustomobject]$outcome
}

$bases = [Collections.Generic.List[object]]::new()
$bases.Add($previous)
$releaseText = Invoke-Gh release list --limit 100 --json tagName
if ($LASTEXITCODE -ne 0) { Throw-GhRequestFailure 'Release archive lookup failed.' }
$archiveTags = ($releaseText | ConvertFrom-Json).tagName |
    Where-Object { $_ -match '^update-base-([1-9][0-9]*)$' } |
    Sort-Object { [int]($_ -replace '^update-base-', '') } -Descending
foreach ($tag in $archiveTags) {
    if ($bases.Count -ge 2) { break }
    if ([int]($tag -replace '^update-base-', '') -ge $previous.releaseNumber) { continue }
    try {
        $archive = Get-ValidatedBase $tag (Join-Path $WorkDirectory "jet-$tag")
        if ($archive.releaseNumber -ne [int]($tag -replace '^update-base-', '')) { throw 'Archive number mismatch.' }
        if (-not ($bases | Where-Object { $_.sha256 -eq $archive.sha256 })) { $bases.Add($archive) }
    }
    catch {
        if ($_.Exception.Data['GitHubRequestFailure']) { throw }
        Write-Host "Skipping invalid archive $tag`: $($_.Exception.Message)"
    }
}
ConvertTo-Json -InputObject $bases.ToArray() -Depth 4 | Set-Content -LiteralPath $BasesFile -Encoding UTF8
[pscustomobject]$outcome
