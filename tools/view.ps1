<#
.SYNOPSIS
    Запускает просмотрщик графа из отдельной папки, чтобы работающий процесс не блокировал сборку.

.DESCRIPTION
    Долго живущий процесс, запущенный прямо из bin, держит Data1c.Core.dll и Data1c.Cli.dll,
    из-за чего dotnet build и Rider падают с MSB3027/MSB3021 «файл занят другим процессом».
    Скрипт публикует CLI в artifacts\viewer и запускает его оттуда: каталог сборки остаётся свободным.

.EXAMPLE
    pwsh tools\view.ps1 C:\dump\1c_files -Open
    pwsh tools\view.ps1 C:\dump\1c_files -Port 9090 -Extra --sections,CommonModules
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Dump,

    [int]$Port = 0,

    [switch]$Open,

    [switch]$NoBuild,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Extra
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts/viewer'

# 1. Прежние экземпляры держат файлы сборки — останавливаем.
$running = Get-Process data1c -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Останавливаю прежние экземпляры просмотрщика: $($running.Id -join ', ')"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# 2. Публикуем в отдельную папку (каталог bin остаётся свободным для сборки).
if (-not $NoBuild) {
    Write-Host 'Публикую CLI в artifacts\viewer…'
    dotnet publish (Join-Path $root 'src/Data1c.Cli/Data1c.Cli.csproj') -c Release -o $output --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw "Публикация не удалась (код $LASTEXITCODE)"
    }
}

$exe = Join-Path $output 'data1c.exe'
if (-not (Test-Path $exe)) {
    throw "Не найден $exe — запустите скрипт без -NoBuild"
}

# 3. Запускаем из artifacts: сборка проекта этому больше не мешает.
$arguments = @('view', $Dump)
if ($Port -gt 0) { $arguments += @('--port', $Port) }
if ($Open) { $arguments += '--open' }
if ($Extra) { $arguments += $Extra }

Write-Host "Запускаю: $exe $($arguments -join ' ')"
& $exe @arguments
