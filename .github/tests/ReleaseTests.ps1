$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scripts = Join-Path $repositoryRoot '.github/scripts'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$work = [IO.Path]::GetFullPath((Join-Path $tempRoot ('JETReleaseTests-' + [Guid]::NewGuid().ToString('N'))))
if (-not $work.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or $work -eq $tempRoot) { throw 'Unsafe test directory.' }
New-Item -ItemType Directory -Path $work | Out-Null
$savedRepo = $env:GH_REPO
$savedDelays = $env:GH_RETRY_DELAYS
$existingGh = Get-Command gh -CommandType Function -ErrorAction SilentlyContinue
$savedGh = if ($existingGh) { $existingGh.ScriptBlock } else { $null }
$script:passed = 0
$env:GH_RETRY_DELAYS = '0,0'

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Release test failed: $Message" }
    $script:passed++
}
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true }
    Assert $caught $Message
}
function New-State {
    @{
        Releases = @{}; Calls = [Collections.Generic.List[object]]::new()
        Uploads = [Collections.Generic.List[string]]::new(); TransientFailures = 0
        RepoFailure = ''; TagSha = ('0' * 40)
    }
}
function Add-Fixture($State, [string]$Tag, [int]$Number, [string]$Commit, [byte[]]$Bytes) {
    $folder = Join-Path $work ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    $exe = Join-Path $folder 'JustEnoughTools.exe'
    [IO.File]::WriteAllBytes($exe, $Bytes)
    $manifest = [ordered]@{
        schemaVersion = 2; releaseNumber = $Number; commitSha = $Commit; assetName = 'JustEnoughTools.exe'
        sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = $Bytes.Length; deltaPatches = @()
    }
    $json = Join-Path $folder 'update.json'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $json -Encoding UTF8
    $entry = @{ Assets = @{ 'JustEnoughTools.exe' = $exe; 'update.json' = $json }; Target = $Commit }
    $State.Releases[$Tag] = $entry
    [pscustomobject]@{ Entry = $entry; Manifest = $manifest; Exe = $exe; Json = $json }
}

# This function intercepts all release requests. No test uses GitHub credentials or the network.
function global:gh {
    $arguments = @($args | ForEach-Object { [string]$_ })
    $state = $global:JetReleaseMock
    $state.Calls.Add($arguments)
    $global:LASTEXITCODE = 0
    if ($state.TransientFailures -gt 0) {
        $state.TransientFailures--
        Write-Error 'HTTP 503: Service unavailable' -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }
    if ($arguments[0] -eq 'api') {
        if ($arguments -contains 'PATCH') {
            $state.TagSha = ($arguments | Where-Object { $_ -like 'sha=*' } | Select-Object -First 1).Substring(4)
            return '{}'
        }
        if ($arguments[1] -like '*/git/ref/tags/latest') { return (@{ object = @{ sha = $state.TagSha } } | ConvertTo-Json -Compress) }
        if ($state.RepoFailure) {
            Write-Error $state.RepoFailure -ErrorAction Continue
            $global:LASTEXITCODE = 1
            return
        }
        return 'MarkZamore/JustEnoughTools'
    }
    $operation = $arguments[1]
    if ($operation -eq 'list') {
        return (ConvertTo-Json -InputObject @($state.Releases.Keys | ForEach-Object { @{ tagName = $_ } }) -Compress)
    }
    $tag = $arguments[2]
    if ($operation -eq 'create') {
        $assets = @{}
        foreach ($value in $arguments) {
            if (Test-Path -LiteralPath $value -PathType Leaf) { $assets[[IO.Path]::GetFileName($value)] = $value }
        }
        $targetIndex = [Array]::IndexOf($arguments, '--target')
        $state.Releases[$tag] = @{ Assets = $assets; Target = $arguments[$targetIndex + 1] }
        return 'https://example.invalid/release'
    }
    if (-not $state.Releases.ContainsKey($tag)) {
        Write-Error 'release not found' -ErrorAction Continue
        $global:LASTEXITCODE = 1
        return
    }
    $entry = $state.Releases[$tag]
    switch ($operation) {
        'view' {
            if ($arguments -contains 'assets') {
                return (@{ assets = @($entry.Assets.Keys | ForEach-Object { @{ name = $_ } }) } | ConvertTo-Json -Depth 4 -Compress)
            }
            return (@{ tagName = $tag; isDraft = [bool]$entry.Draft } | ConvertTo-Json -Compress)
        }
        'download' {
            $directory = $arguments[[Array]::IndexOf($arguments, '--dir') + 1]
            foreach ($index in 0..($arguments.Length - 2)) {
                if ($arguments[$index] -ne '--pattern') { continue }
                $name = $arguments[$index + 1]
                if (-not $entry.Assets.ContainsKey($name)) {
                    Write-Error 'no assets match the file pattern' -ErrorAction Continue
                    $global:LASTEXITCODE = 1
                    return
                }
                Copy-Item -LiteralPath $entry.Assets[$name] -Destination (Join-Path $directory $name) -Force
            }
            return
        }
        'upload' {
            foreach ($value in $arguments[3..($arguments.Length - 1)]) {
                if ($value.StartsWith('--')) { continue }
                $name = [IO.Path]::GetFileName($value)
                $entry.Assets[$name] = $value
                $state.Uploads.Add($name)
            }
            return
        }
        'edit' { return }
        'delete-asset' { $entry.Assets.Remove($arguments[3]); return }
        'delete' { $state.Releases.Remove($tag); return }
        default { throw "Unrecognized mocked gh operation: $arguments" }
    }
}

function Capture($State, [int]$Number, [string]$Sha, [byte[]]$Bytes) {
    $global:JetReleaseMock = $State
    $folder = Join-Path $work ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    $exe = Join-Path $folder 'JustEnoughTools.exe'
    [IO.File]::WriteAllBytes($exe, $Bytes)
    $bases = Join-Path $folder 'bases.json'
    $answer = & "$scripts/Capture-PreviousRelease.ps1" -CommitNumber $Number -CommitSha $Sha `
        -ExecutablePath $exe -BasesFile $bases -WorkDirectory $folder -Repository 'MarkZamore/JustEnoughTools'
    [pscustomobject]@{ Result = $answer; BasesFile = $bases; Folder = $folder; Exe = $exe }
}

try {
    & "$PSScriptRoot/SourceFingerprintTests.ps1"
    $parseErrors = @()
    Get-ChildItem -LiteralPath $scripts -Filter '*.ps1' -File | ForEach-Object {
        $tokens = $null; $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors)
        $parseErrors += $errors
    }
    Assert ($parseErrors.Count -eq 0) 'all release scripts parse'
    . "$scripts/GhRetry.ps1"
    $global:JetReleaseMock = New-State
    $global:JetReleaseMock.TransientFailures = 1
    $response = Invoke-Gh api 'repos/MarkZamore/JustEnoughTools' --jq '.full_name'
    Assert ($LASTEXITCODE -eq 0 -and $response -eq 'MarkZamore/JustEnoughTools') 'transient 503 retries and recovers'
    Assert ($global:JetReleaseMock.Calls.Count -eq 2) 'retry makes exactly one additional request'
    Assert (-not (Test-GhTransientFailure 'HTTP 403: Resource not accessible by integration')) 'authorization failure is not retried'

    $shaA = 'a' * 40; $shaB = 'b' * 40; $shaC = 'c' * 40
    $bytes1 = [Text.Encoding]::UTF8.GetBytes(('old-version-one-' * 100))
    $bytes2 = [Text.Encoding]::UTF8.GetBytes(('old-version-two-' * 100))
    $bytes3 = [Text.Encoding]::UTF8.GetBytes(('new-version-three-' * 100))
    $state = New-State
    $first = Capture $state 1 $shaA $bytes1
    Assert (-not $first.Result.HasPrevious) 'first release has no previous base'
    Assert (@(Get-Content -LiteralPath $first.BasesFile -Raw | ConvertFrom-Json).Count -eq 0) 'first release has no deltas'
    $state = New-State; $state.RepoFailure = 'HTTP 403: Resource not accessible by integration'
    Assert-Throws { Capture $state 1 $shaA $bytes1 } '403 must not be treated as a first release'
    Assert ($state.Calls.Count -eq 1) '403 stops without retries'

    $state = New-State
    $latest = Add-Fixture $state 'latest' 3 $shaC $bytes3
    $same = Capture $state 3 $shaC $bytes3
    Assert $same.Result.AlreadyPublished 'same commit and bytes are an idempotent rerun'
    $latest.Entry.Draft = $true
    $draft = Capture $state 3 $shaC $bytes3
    Assert (-not $draft.Result.AlreadyPublished -and -not $draft.Result.HasPrevious) 'an interrupted draft is completed instead of skipped or archived'
    $latest.Entry.Draft = $false
    Assert-Throws { Capture $state 3 $shaC $bytes2 } 'same number with different bytes is rejected'
    Assert-Throws { Capture $state 2 $shaB $bytes2 } 'an old workflow cannot replace a newer release'

    $state = New-State
    $latest = Add-Fixture $state 'latest' 2 $shaB $bytes2
    $bad = Add-Fixture $state 'update-base-1' 1 $shaA $bytes1
    [IO.File]::AppendAllText($bad.Exe, 'corrupt')
    $captured = Capture $state 3 $shaC $bytes3
    $baseRows = @(Get-Content -LiteralPath $captured.BasesFile -Raw | ConvertFrom-Json)
    Assert ($baseRows.Count -eq 1 -and $baseRows[0].releaseNumber -eq 2) 'a corrupt archive is skipped without discarding the valid latest base'

    $state = New-State
    $latest = Add-Fixture $state 'latest' 2 $shaB $bytes2
    $older = Add-Fixture $state 'update-base-1' 1 $shaA $bytes1
    $captured = Capture $state 3 $shaC $bytes3
    $baseRows = @(Get-Content -LiteralPath $captured.BasesFile -Raw | ConvertFrom-Json)
    Assert ($baseRows.Count -eq 2) 'two previous published versions become two direct bases'
    $global:JetReleaseMock = $state
    & "$scripts/Archive-PreviousBase.ps1" -ReleaseNumber 2 -ExecutablePath $latest.Exe -ManifestPath $latest.Json `
        -CommitSha $shaB -Repository 'MarkZamore/JustEnoughTools' -WorkDirectory $captured.Folder
    Assert ($state.Releases['update-base-2'].Target -eq $shaB) 'archive tag targets the previous commit'
    & "$scripts/Archive-PreviousBase.ps1" -ReleaseNumber 2 -ExecutablePath $latest.Exe -ManifestPath $latest.Json `
        -CommitSha $shaB -Repository 'MarkZamore/JustEnoughTools' -WorkDirectory $captured.Folder
    Assert (@($state.Calls | Where-Object { $_[1] -eq 'create' -and $_[2] -eq 'update-base-2' }).Count -eq 1) 'verified archive is reused'
    Assert (@($state.Calls | Where-Object { $_[1] -eq 'delete' }).Count -eq 0) 'no valid archive is deleted before replacement'

    $previousInfo = Join-Path $captured.Folder 'previous.json'
    # The archive path above is tested independently; asset construction must also work without archiving.
    @{ HasPrevious = $false; AlreadyPublished = $false } | ConvertTo-Json | Set-Content -LiteralPath $previousInfo -Encoding UTF8
    $artifacts = Join-Path $captured.Folder 'artifacts'
    $tool = Join-Path $repositoryRoot 'Source/Patch/bin/Release/net10.0/DeltaPatchTool.dll'
    if (-not (Test-Path -LiteralPath $tool)) { throw 'Build Source/Patch/DeltaPatchTool.csproj before running release tests.' }
    & "$scripts/Build-ReleaseAssets.ps1" -ReleaseNumber 3 -CommitSha $shaC -ExecutablePath $captured.Exe `
        -BasesFile $captured.BasesFile -PreviousInfoFile $previousInfo -ArtifactDirectory $artifacts `
        -DeltaTool $tool -WorkDirectory $captured.Folder -Repository 'MarkZamore/JustEnoughTools'
    $manifest = Get-Content -LiteralPath (Join-Path $artifacts 'update.json') -Raw | ConvertFrom-Json
    Assert (@($manifest.deltaPatches).Count -eq 2) 'real bsdiff generation and roundtrip produce two direct patches'
    Assert ($manifest.sha256 -eq (Get-FileHash -LiteralPath $captured.Exe -Algorithm SHA256).Hash.ToLowerInvariant()) 'release contains exact tracked payload bytes'
    $junk = Join-Path $artifacts 'unexpected.txt'
    [IO.File]::WriteAllText($junk, 'unexpected')
    Assert-Throws { & "$scripts/Verify-ReleaseAssets.ps1" -ArtifactDirectory $artifacts -ReleaseNumber 3 -CommitSha $shaC } 'unexpected release assets are rejected'
    Remove-Item -LiteralPath $junk

    $state = New-State; $global:JetReleaseMock = $state
    & "$scripts/Publish-Release.ps1" -ReleaseNumber 3 -CommitSha $shaC -ExecutablePath $captured.Exe `
        -PreviousInfoFile $previousInfo -ArtifactDirectory $artifacts -WorkDirectory $captured.Folder `
        -Repository 'MarkZamore/JustEnoughTools'
    Assert ($state.Uploads[$state.Uploads.Count - 1] -eq 'update.json') 'manifest is uploaded last'
    Assert ($state.TagSha -eq $shaC) 'latest Git ref advances after publication'
    $before = $state.Uploads.Count
    $state.Releases['latest'].Assets['obsolete.bsdiff'] = $captured.Exe
    foreach ($old in 1..3) { $state.Releases["update-base-$old"] = @{ Assets = @{}; Target = $shaA } }
    @{ HasPrevious = $true; AlreadyPublished = $true } | ConvertTo-Json | Set-Content -LiteralPath $previousInfo -Encoding UTF8
    & "$scripts/Publish-Release.ps1" -ReleaseNumber 3 -CommitSha $shaC -ExecutablePath $captured.Exe `
        -PreviousInfoFile $previousInfo -ArtifactDirectory $artifacts -WorkDirectory $captured.Folder `
        -Repository 'MarkZamore/JustEnoughTools'
    Assert ($state.Uploads.Count -eq $before) 'same release rerun does not mutate assets'
    Assert (-not $state.Releases['latest'].Assets.ContainsKey('obsolete.bsdiff')) 'rerun completes stale asset cleanup'
    Assert (-not $state.Releases.ContainsKey('update-base-1') -and $state.Releases.ContainsKey('update-base-2') -and
        $state.Releases.ContainsKey('update-base-3')) 'rerun retains only the two latest archive bases'
    @{ HasPrevious = $false; AlreadyPublished = $false } | ConvertTo-Json | Set-Content -LiteralPath $previousInfo -Encoding UTF8
    $broken = Join-Path $captured.Folder 'broken-update.json'
    [IO.File]::WriteAllText($broken, '{broken')
    $state.Releases['latest'].Assets['update.json'] = $broken
    & "$scripts/Publish-Release.ps1" -ReleaseNumber 3 -CommitSha $shaC -ExecutablePath $captured.Exe `
        -PreviousInfoFile $previousInfo -ArtifactDirectory $artifacts -WorkDirectory $captured.Folder `
        -Repository 'MarkZamore/JustEnoughTools'
    Assert ($state.Releases['latest'].Assets['update.json'] -eq (Join-Path $artifacts 'update.json')) 'invalid live manifest can be repaired with verified assets'
    Write-Host "Release infrastructure: $script:passed checks passed."
}
finally {
    $env:GH_REPO = $savedRepo
    $env:GH_RETRY_DELAYS = $savedDelays
    Remove-Item Function:gh -Force -ErrorAction SilentlyContinue
    if ($savedGh) { Set-Item -Path Function:global:gh -Value $savedGh }
    Remove-Variable JetReleaseMock -Scope Global -ErrorAction SilentlyContinue
    $resolvedWork = [IO.Path]::GetFullPath($work)
    if ($resolvedWork.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedWork).StartsWith('JETReleaseTests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
