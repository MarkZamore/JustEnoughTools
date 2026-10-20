$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$helper = Join-Path $repositoryRoot '.github/scripts/Get-SourceFingerprint.ps1'
$verifier = Join-Path $repositoryRoot '.github/scripts/Verify-TrackedExecutable.ps1'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixture = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('JETSourceTests-' + [Guid]::NewGuid().ToString('N'))))
if (-not $fixture.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or $fixture -eq $temporaryRoot) {
    throw 'Unsafe source-provenance fixture path.'
}
$utf8 = [Text.UTF8Encoding]::new($false)
$passed = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Source fingerprint test failed: $Message" }
    $script:passed++
}
function Write-Fixture([string]$Relative, [string]$Text) {
    $path = Join-Path $fixture $Relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Text, $utf8)
}
function Commit-Fixture([switch]$Amend) {
    & git -C $fixture -c core.safecrlf=false add --all
    if ($LASTEXITCODE -ne 0) { throw 'Fixture staging failed.' }
    $arguments = @('-C', $fixture, '-c', 'user.name=Source Fixture', '-c', 'user.email=source-fixture@example.invalid',
        'commit', '--quiet', '--message', 'Fixture')
    if ($Amend) { $arguments += '--amend' }
    & git @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Fixture commit failed.' }
}
try {
    [void][IO.Directory]::CreateDirectory($fixture)
    & git -C $fixture init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Fixture initialization failed.' }
    Write-Fixture '.gitattributes' "* text=auto`n*.cs text eol=lf`n*.ps1 text eol=lf`n*.ico binary`n*.exe binary`n"
    Write-Fixture 'Source/Build.ps1' "param()`n"
    Write-Fixture 'Source/JustEnoughTools.csproj' "<Project />`n"
    Write-Fixture 'Source/Changelog.md' "## 1`n- Source provenance fixture.`n"
    Write-Fixture 'Source/packages.lock.json' "{`"version`":1}`n"
    Write-Fixture 'THIRD-PARTY-NOTICES.txt' "Fixture only.`n"
    Write-Fixture 'Directory.Build.props' "<Project />`n"
    Write-Fixture 'global.json' "{}`n"
    [void][IO.Directory]::CreateDirectory((Join-Path $fixture 'Source/Assets'))
    [IO.File]::WriteAllBytes((Join-Path $fixture 'Source/Assets/JustEnoughTools.ico'), [byte[]]@(1, 2, 3, 4))
    [void][IO.Directory]::CreateDirectory((Join-Path $fixture '.github/scripts'))
    Copy-Item -LiteralPath $helper -Destination (Join-Path $fixture '.github/scripts/Get-SourceFingerprint.ps1')
    $source = "internal static class App { [System.STAThread] private static int Main() { return 0; } }`n"
    Write-Fixture 'Source/App.cs' $source
    $initial = & $helper -RepositoryRoot $fixture
    Check ([string]$initial -match '^[0-9a-f]{64}$') 'fingerprint works before the first commit'

    Write-Fixture 'Source/App.cs' ($source.Replace("`n", "`r`n"))
    Check ((& $helper -RepositoryRoot $fixture) -eq $initial) 'CRLF and LF working trees have the same identity'
    Write-Fixture 'Source/App.cs' $source
    foreach ($directory in @('Build', 'bin', 'obj', 'Patch', 'Tools')) {
        Write-Fixture "Source/$directory/Generated.cs" "This is ignored development output.`n"
    }
    Check ((& $helper -RepositoryRoot $fixture) -eq $initial) 'generated output and separate tools are excluded'
    Write-Fixture 'Source/Theme.xaml' "<ResourceDictionary />`n"
    $withTheme = & $helper -RepositoryRoot $fixture
    Check ($withTheme -ne $initial) 'application XAML is included'

    # A tiny, harmless test EXE provides the real metadata/PE path without rebuilding the application.
    $assemblyInfo = @"
using System.Reflection;
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyMetadata("ReleaseNumber", "1")]
[assembly: AssemblyMetadata("SourceFingerprint", "$withTheme")]
"@
    Write-Fixture 'Build/AssemblyInfo.cs' $assemblyInfo
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $executable = Join-Path $fixture 'JustEnoughTools.exe'
    & $compiler /nologo /target:winexe /platform:x64 "/out:$executable" `
        (Join-Path $fixture 'Source/App.cs') (Join-Path $fixture 'Build/AssemblyInfo.cs')
    if ($LASTEXITCODE -ne 0) { throw 'The provenance fixture EXE could not be compiled.' }
    Check ((& $helper -RepositoryRoot $fixture) -eq $withTheme) 'the root EXE and generated metadata do not make the hash self-referential'
    Commit-Fixture
    Check ((& $helper -RepositoryRoot $fixture -Committed) -eq $withTheme) 'committed input blobs equal the pre-commit identity'
    $answer = & $verifier -RepositoryRoot $fixture -ReleaseNumber 1
    Check ($answer.ReleaseNumber -eq 1) 'matching tracked EXE metadata is accepted'

    Write-Fixture 'Source/App.cs' ($source.Replace('return 0;', 'return 1;'))
    Commit-Fixture -Amend
    $rejected = $false
    try { & $verifier -RepositoryRoot $fixture -ReleaseNumber 1 | Out-Null }
    catch { $rejected = $_.Exception.Message -like '*was not built from this commit source*' }
    Check $rejected 'source changed after building with the same version is rejected'
    Write-Host "Source provenance: $passed checks passed."
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved).StartsWith('JETSourceTests-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
