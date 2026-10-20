param(
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][int]$ReleaseNumber,
    [Parameter(Mandatory = $true)][string]$CommitSha
)
$ErrorActionPreference = 'Stop'
$manifestPath = Join-Path $ArtifactDirectory 'update.json'
if ((Get-Item -LiteralPath $manifestPath).Length -gt 64KB) { throw 'Update manifest is too large.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 2 -or $manifest.assetName -ne 'JustEnoughTools.exe' -or
    [int]$manifest.releaseNumber -ne $ReleaseNumber -or $manifest.commitSha -ne $CommitSha -or
    $CommitSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'Update manifest identity is invalid.' }
$expected = [Collections.Generic.List[string]]::new()
$expected.Add('JustEnoughTools.exe'); $expected.Add('update.json')
function Assert-File([string]$Name, [long]$Size, [string]$Hash) {
    if ($Size -le 0 -or $Size -gt 64MB -or $Hash -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid metadata for $Name." }
    $path = Join-Path $ArtifactDirectory $Name
    if ((Get-Item -LiteralPath $path).Length -ne $Size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash.ToLowerInvariant()) {
        throw "Release asset $Name does not match its manifest."
    }
}
Assert-File $manifest.assetName $manifest.sizeBytes $manifest.sha256
$patches = @($manifest.deltaPatches)
if ($patches.Count -gt 2) { throw 'More than two direct deltas were generated.' }
$seenBases = @{}
foreach ($patch in $patches) {
    if ($patch.algorithm -ne 'bsdiff' -or $patch.algorithmVersion -ne 1 -or
        $patch.assetName -notmatch '^JustEnoughTools(?:\.from-[1-9][0-9]*)?\.bsdiff$' -or
        [int]$patch.baseReleaseNumber -lt 1 -or [int]$patch.baseReleaseNumber -ge $ReleaseNumber -or
        $patch.baseCommitSha -notmatch '^[0-9a-fA-F]{40}$' -or $patch.baseSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $expected.Contains([string]$patch.assetName) -or $seenBases.ContainsKey([string]$patch.baseReleaseNumber)) {
        throw 'A delta manifest entry is invalid or duplicated.'
    }
    $seenBases[[string]$patch.baseReleaseNumber] = $true
    Assert-File $patch.assetName $patch.sizeBytes $patch.sha256
    $expected.Add([string]$patch.assetName)
}
$files = @(Get-ChildItem -LiteralPath $ArtifactDirectory -File)
if ($files.Count -ne $expected.Count -or @($files | Where-Object { -not $expected.Contains($_.Name) }).Count) {
    throw 'The release directory contains missing or unexpected assets.'
}
Write-Host "Verified release $ReleaseNumber with $($patches.Count) direct delta(s)."
