param(
    [Parameter(Mandatory = $true)][int]$ReleaseNumber,
    [Parameter(Mandatory = $true)][string]$CommitSha,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$BasesFile,
    [Parameter(Mandatory = $true)][string]$PreviousInfoFile,
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [Parameter(Mandatory = $true)][string]$DeltaTool,
    [Parameter(Mandatory = $true)][string]$WorkDirectory,
    [Parameter(Mandatory = $true)][string]$Repository
)
$ErrorActionPreference = 'Stop'
$previous = Get-Content -LiteralPath $PreviousInfoFile -Raw | ConvertFrom-Json
if ($previous.AlreadyPublished) { Write-Host 'Exact release already exists; retaining its assets.'; return }
if ($ReleaseNumber -lt 1 -or $CommitSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'Invalid release identity.' }
if (-not (Test-Path -LiteralPath $DeltaTool -PathType Leaf)) { throw 'The delta tool has not been built.' }
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$exe = Join-Path $ArtifactDirectory 'JustEnoughTools.exe'
Copy-Item -LiteralPath $ExecutablePath -Destination $exe -Force
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $exe).Length
if ($size -le 0 -or $size -gt 64MB) { throw 'The release EXE exceeds the updater payload limit.' }

if ($previous.HasPrevious) {
    & "$PSScriptRoot/Archive-PreviousBase.ps1" -ReleaseNumber $previous.PreviousReleaseNumber `
        -ExecutablePath $previous.PreviousExe -ManifestPath $previous.PreviousManifest `
        -CommitSha $previous.PreviousCommit -Repository $Repository -WorkDirectory $WorkDirectory
}

$bases = @(Get-Content -LiteralPath $BasesFile -Raw | ConvertFrom-Json | Where-Object { $null -ne $_ })
if ($bases.Count -gt 2) { throw 'No more than two direct delta bases may be published.' }
$deltaJobs = @()
try {
    for ($index = 0; $index -lt $bases.Count; $index++) {
        $base = $bases[$index]
        if ([int]$base.releaseNumber -lt 1 -or [int]$base.releaseNumber -ge $ReleaseNumber -or
            (Get-Item -LiteralPath $base.executablePath).Length -ne [long]$base.sizeBytes -or
            (Get-FileHash -LiteralPath $base.executablePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $base.sha256) {
            throw 'A delta base changed after verification.'
        }
        $asset = if ($index -eq 0) { 'JustEnoughTools.bsdiff' } else { "JustEnoughTools.from-$($base.releaseNumber).bsdiff" }
        $patchPath = Join-Path $ArtifactDirectory $asset
        $roundTrip = Join-Path $WorkDirectory ("jet-roundtrip-" + $base.releaseNumber + '-' + [Guid]::NewGuid().ToString('N') + '.exe')
        $deltaJobs += Start-Job -Name "delta from $($base.releaseNumber)" -ScriptBlock {
            param($tool, $baseExe, $newExe, $patch, $roundTrip, $expectedHash, $expectedSize)
            $ErrorActionPreference = 'Stop'
            try {
                & dotnet $tool create $baseExe $newExe $patch
                if ($LASTEXITCODE -ne 0) { throw 'Delta generation failed.' }
                & dotnet $tool apply $baseExe $patch $roundTrip
                if ($LASTEXITCODE -ne 0) { throw 'Delta roundtrip failed.' }
                if ((Get-Item -LiteralPath $roundTrip).Length -ne $expectedSize -or
                    (Get-FileHash -LiteralPath $roundTrip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
                    throw 'The patch does not reproduce the exact committed release executable.'
                }
                $patchSize = (Get-Item -LiteralPath $patch).Length
                if ($patchSize -le 0 -or $patchSize -gt 64MB) { throw 'The delta exceeds the updater limit.' }
                [pscustomobject]@{
                    Sha256 = (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant()
                    SizeBytes = $patchSize
                }
            }
            finally { Remove-Item -LiteralPath $roundTrip -Force -ErrorAction SilentlyContinue }
        } -ArgumentList $DeltaTool, $base.executablePath, $exe, $patchPath, $roundTrip, $hash, $size
    }
    $patches = [Collections.Generic.List[object]]::new()
    if ($deltaJobs.Count) { Wait-Job -Job $deltaJobs | Out-Null }
    for ($index = 0; $index -lt $deltaJobs.Count; $index++) {
        $job = $deltaJobs[$index]
        $answer = @(Receive-Job -Job $job -ErrorAction Continue | Where-Object { $null -ne $_ -and $null -ne $_.Sha256 })
        if ($job.State -ne 'Completed' -or $answer.Count -ne 1) {
            throw "Delta preparation failed in '$($job.Name)': $($job.ChildJobs[0].JobStateInfo.Reason.Message)"
        }
        $base = $bases[$index]
        $asset = if ($index -eq 0) { 'JustEnoughTools.bsdiff' } else { "JustEnoughTools.from-$($base.releaseNumber).bsdiff" }
        $patches.Add([ordered]@{
            algorithm = 'bsdiff'; algorithmVersion = 1
            baseReleaseNumber = [int]$base.releaseNumber; baseCommitSha = [string]$base.commitSha
            baseSha256 = [string]$base.sha256; assetName = $asset
            sha256 = [string]$answer[0].Sha256; sizeBytes = [long]$answer[0].SizeBytes
        })
    }
    $manifest = [ordered]@{
        schemaVersion = 2; commitSha = $CommitSha.ToLowerInvariant(); releaseNumber = $ReleaseNumber
        version = [string]$ReleaseNumber; publishedAtUtc = [DateTime]::UtcNow.ToString('o')
        assetName = 'JustEnoughTools.exe'; sha256 = $hash; sizeBytes = $size
        deltaPatches = $patches.ToArray()
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'update.json') -Encoding UTF8
}
finally {
    foreach ($job in $deltaJobs) {
        if ($job.State -eq 'Running') { Stop-Job -Job $job }
        Remove-Job -Job $job -Force
    }
}
& "$PSScriptRoot/Verify-ReleaseAssets.ps1" -ArtifactDirectory $ArtifactDirectory -ReleaseNumber $ReleaseNumber -CommitSha $CommitSha
