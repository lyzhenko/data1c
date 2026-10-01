<#
.SYNOPSIS
    Собирает решение Data1C.sln одной командой в любом режиме песочницы.

.DESCRIPTION
    Скрипт нужен потому, что обычный «dotnet build» в ограниченных режимах (read-only
    песочница, запрет именованных каналов, общий каталог obj) молча падает: MSBuild
    оставляет в кэше «зависшие» серверы сборки и переиспользуемые узлы, после чего
    сборка завершается без понятной ошибки. Поэтому перед сборкой обязательно
    выполняется «dotnet build-server shutdown», а сама сборка идёт с -nodeReuse:false
    (не переиспользовать узлы MSBuild) и -m:1 (ровно один процесс — без обмена
    данными через каналы между узлами).

    Скрипт работает из любого текущего каталога: корень репозитория вычисляется от
    расположения самого файла ($PSScriptRoot), а не от текущего каталога.

.PARAMETER Configuration
    Конфигурация сборки: Debug (по умолчанию) или Release.

.PARAMETER NoShutdown
    Не останавливать серверы сборки перед сборкой. Нужен только для отладки самого
    скрипта: без остановки серверов обход перестаёт работать.

.EXAMPLE
    pwsh -File tools\build.ps1

.EXAMPLE
    pwsh -File tools\build.ps1 -Configuration Release

.NOTES
    Возвращает 0 при успешной сборке и ненулевой код, если сборка не удалась.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoShutdown
)

$ErrorActionPreference = 'Stop'

# Корень репозитория — родитель каталога tools, где лежит этот скрипт.
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Data1C.sln'

if (-not (Test-Path -LiteralPath $solution)) {
    Write-Host "Не найден файл решения: $solution" -ForegroundColor Red
    exit 2
}

Write-Host "Сборка Data1C ($Configuration)" -ForegroundColor Cyan
Write-Host "  решение: $solution"

if (-not $NoShutdown) {
    # Останавливаем серверы MSBuild и компилятора: в песочнице они переживают
    # завершение процесса и потом мешают сборке (файлы заняты, каналы недоступны).
    & dotnet build-server shutdown
}

# -nodeReuse:false — не оставлять узлы MSBuild после сборки.
# -m:1 — один процесс сборки: в ограниченных режимах узлы не могут договориться
#         между собой, и многопроцессная сборка завершается непонятной ошибкой.
& dotnet build $solution -c $Configuration -v q --nologo -nodeReuse:false -m:1
$exitCode = $LASTEXITCODE

if ($exitCode -eq 0) {
    Write-Host "Сборка успешна ($Configuration)." -ForegroundColor Green
}
else {
    Write-Host "Сборка НЕ УДАЛАСЬ (код $exitCode, $Configuration)." -ForegroundColor Red
}

exit $exitCode
