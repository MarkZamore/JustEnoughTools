param([string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $repository 'JustEnoughTools.exe' }
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw "EXE не найден: $ExecutablePath" }
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath)
if ($fileVersion.FileVersion -notmatch '^[1-9][0-9]{0,4}$' -or
    $fileVersion.ProductVersion -cne $fileVersion.FileVersion) {
    throw 'Версия файла и продукта должны содержать только номер выпуска.'
}
$releaseNumber = [int]$fileVersion.FileVersion
if ($releaseNumber -gt 65535) { throw 'Номер выпуска превышает допустимый диапазон.' }
& dotnet build (Join-Path $PSScriptRoot 'JustEnoughTools.Tests.csproj') --configuration Release "-p:ReleaseNumber=$releaseNumber" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверки.' }
$runner = Join-Path $PSScriptRoot 'bin\Release\net48\JustEnoughTools.Tests.exe'
& $runner $ExecutablePath
if ($LASTEXITCODE -ne 0) { throw 'Проверки JustEnoughTools завершились с ошибкой.' }
