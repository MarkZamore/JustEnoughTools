param([string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $repository 'JustEnoughTools.exe' }
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw "EXE не найден: $ExecutablePath" }
$releaseNumber = [Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath).FileBuildPart
if ($releaseNumber -lt 1) { throw 'В EXE отсутствует номер выпуска.' }
& dotnet build (Join-Path $PSScriptRoot 'JustEnoughTools.Tests.csproj') --configuration Release "-p:ReleaseNumber=$releaseNumber" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверки.' }
$runner = Join-Path $PSScriptRoot 'bin\Release\net48\JustEnoughTools.Tests.exe'
& $runner $ExecutablePath
if ($LASTEXITCODE -ne 0) { throw 'Проверки JustEnoughTools завершились с ошибкой.' }
