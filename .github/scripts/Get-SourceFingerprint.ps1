param(
    [string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [switch]$Committed
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$required = @(
    'Source/Build.ps1', 'Source/JustEnoughTools.csproj', 'Source/Changelog.md', 'Source/packages.lock.json',
    'Source/Assets/JustEnoughTools.ico', 'THIRD-PARTY-NOTICES.txt', 'Directory.Build.props', 'global.json',
    '.gitattributes', '.github/scripts/Get-SourceFingerprint.ps1'
)
function Test-FingerprintInput([string]$Path) {
    if ($Path -in $required) { return $true }
    if ($Path -notmatch '^Source/' -or $Path -match '^Source/(?:Build|bin|obj|Patch|Tools)/') { return $false }
    if ([IO.Path]::GetExtension($Path) -in @('.cs', '.xaml', '.csproj', '.manifest')) { return $true }
    return $Path -match '^Source/Assets/.+\.ico$'
}
if ($Committed) {
    $files = @(& git -C $root -c core.quotePath=false ls-tree -r --name-only HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'A committed HEAD is required to verify release provenance.' }
}
else {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $root 'Source') -Recurse -File | ForEach-Object {
        $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    }) + $required
}
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $files) {
    $path = ([string]$file).Replace('\', '/')
    if (Test-FingerprintInput $path) { [void]$paths.Add($path) }
}
foreach ($path in $required) {
    if (-not $paths.Contains($path)) { throw "A fingerprint input is missing: $path" }
}
$ordered = [string[]]@($paths)
[Array]::Sort($ordered, [StringComparer]::Ordinal)
$records = [Text.StringBuilder]::new("JustEnoughTools.SourceFingerprint.v1`n")
foreach ($path in $ordered) {
    if ($path.IndexOfAny([char[]]@("`r", "`n", "`0")) -ge 0) { throw 'A source path contains an invalid control character.' }
    if ($Committed) { $hash = & git -C $root rev-parse "HEAD:$path" }
    else {
        $full = Join-Path $root $path
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "A fingerprint input is missing: $path" }
        # Use Git's clean rules, so CRLF working trees and LF checkouts have the same identity.
        $hash = & git -C $root -c core.safecrlf=false hash-object "--path=$path" -- $full
    }
    if ($LASTEXITCODE -ne 0 -or [string]$hash -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
        throw "Could not fingerprint $path."
    }
    [void]$records.Append($path).Append([char]0).Append([string]$hash).Append("`n")
}
$sha256 = [Security.Cryptography.SHA256]::Create()
try { [BitConverter]::ToString($sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($records.ToString()))).Replace('-', '').ToLowerInvariant() }
finally { $sha256.Dispose() }
