param(
    [Parameter(Mandatory = $true)][int]$ReleaseNumber,
    [string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$exe = Join-Path $root 'JustEnoughTools.exe'
if ($ReleaseNumber -lt 1 -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'A positive release number and tracked root JustEnoughTools.exe are required.'
}
if ((Get-Item -LiteralPath $exe).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The release executable must be a regular file.'
}
& git -C $root ls-files --error-unmatch -- JustEnoughTools.exe *> $null
if ($LASTEXITCODE -ne 0) { throw 'JustEnoughTools.exe must be tracked by Git.' }
$tracked = (& git -C $root rev-parse 'HEAD:JustEnoughTools.exe').Trim()
if ($LASTEXITCODE -ne 0) { throw 'The current commit does not contain JustEnoughTools.exe.' }
$actual = (& git -C $root hash-object -- $exe).Trim()
if ($LASTEXITCODE -ne 0 -or $tracked -ne $actual) { throw 'The executable differs from the committed bytes.' }

$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
if ($info.FileVersion -cne [string]$ReleaseNumber) {
    throw "The root EXE FileVersion must be $ReleaseNumber; found '$($info.FileVersion)'."
}
$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($exe)
if ($assemblyName.Name -ne 'JustEnoughTools' -or $assemblyName.Version.Build -ne $ReleaseNumber) {
    throw 'The root EXE has unexpected assembly metadata.'
}
$bytes = [IO.File]::ReadAllBytes($exe)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
if ($peOffset -lt 0x40 -or $peOffset + 26 -gt $bytes.Length -or
    [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550 -or
    [BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) {
    throw 'The root EXE is not an x64 PE executable.'
}
$assembly = [Reflection.Assembly]::Load($bytes)
$number = @($assembly.GetCustomAttributesData() | Where-Object {
    $_.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute' -and
    $_.ConstructorArguments.Count -eq 2 -and $_.ConstructorArguments[0].Value -eq 'ReleaseNumber'
} | ForEach-Object { [string]$_.ConstructorArguments[1].Value })
if ($number.Count -ne 1 -or $number[0] -ne [string]$ReleaseNumber) {
    throw 'The root EXE ReleaseNumber metadata does not match the commit count.'
}
$fingerprint = @($assembly.GetCustomAttributesData() | Where-Object {
    $_.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute' -and
    $_.ConstructorArguments.Count -eq 2 -and $_.ConstructorArguments[0].Value -eq 'SourceFingerprint'
} | ForEach-Object { [string]$_.ConstructorArguments[1].Value })
$expectedFingerprint = & "$PSScriptRoot/Get-SourceFingerprint.ps1" -RepositoryRoot $root -Committed
if ($fingerprint.Count -ne 1 -or $fingerprint[0] -ne [string]$expectedFingerprint) {
    throw 'The tracked EXE was not built from this commit source. Rebuild before committing.'
}
$changelog = Join-Path $root 'Source\Changelog.md'
if (-not (Test-Path -LiteralPath $changelog) -or
    (Get-Content -LiteralPath $changelog -Raw) -notmatch ('(?m)^## {0}\s*$' -f $ReleaseNumber)) {
    throw "Source/Changelog.md needs a ## $ReleaseNumber entry."
}
$size = (Get-Item -LiteralPath $exe).Length
if ($size -gt 64MB) { throw 'The executable exceeds the updater payload limit of 64 MiB.' }
[pscustomobject]@{
    ExecutablePath = $exe
    ReleaseNumber = $ReleaseNumber
    Sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    SizeBytes = $size
}
