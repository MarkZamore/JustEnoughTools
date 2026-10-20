param(
    [int]$ReleaseNumber = 1,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if ($ReleaseNumber -lt 1 -or $ReleaseNumber -gt 65535) { throw 'ReleaseNumber должен быть от 1 до 65535.' }
$repository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = $repository }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$intermediate = Join-Path $PSScriptRoot 'Build'
$tools = Join-Path $intermediate 'Tools'
[void][IO.Directory]::CreateDirectory($tools)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$generator = Join-Path $tools 'GenerateIcon.exe'
& $compiler /nologo /target:exe /optimize+ "/out:$generator" /r:System.Drawing.dll (Join-Path $PSScriptRoot 'Tools\GenerateIcon.cs')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать генератор иконки.' }
$assets = Join-Path $PSScriptRoot 'Assets'
[void][IO.Directory]::CreateDirectory($assets)
& $generator (Join-Path $assets 'JustEnoughTools.ico')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать иконку.' }
$project = Join-Path $PSScriptRoot 'JustEnoughTools.csproj'
& dotnet restore $project "-p:ReleaseNumber=$ReleaseNumber" "-p:BaseOutputPath=$intermediate\bin\" "-p:BaseIntermediateOutputPath=$intermediate\obj\" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось восстановить зависимости JustEnoughTools.' }
$fingerprint = & (Join-Path $repository '.github\scripts\Get-SourceFingerprint.ps1') -RepositoryRoot $repository
if ([string]$fingerprint -notmatch '^[0-9a-f]{64}$') { throw 'Не удалось определить отпечаток исходников.' }
& dotnet build $project --configuration Release --no-restore "-p:ReleaseNumber=$ReleaseNumber" "-p:SourceFingerprint=$fingerprint" "-p:BaseOutputPath=$intermediate\bin\" "-p:BaseIntermediateOutputPath=$intermediate\obj\" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать JustEnoughTools.' }
$built = Join-Path $intermediate 'bin\Release\net48\JustEnoughTools.exe'
if (-not (Test-Path -LiteralPath $built)) { throw "Сборка не создала $built" }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$destination = Join-Path $OutputDirectory 'JustEnoughTools.exe'
Copy-Item -LiteralPath $built -Destination $destination -Force
Get-Item -LiteralPath $destination | Select-Object FullName,Length
