<#
.SYNOPSIS
    Собирает и прогоняет тесты Data1C одной командой в любом режиме песочницы.

.DESCRIPTION
    Скрипт запускает собственный раннер тестов (tools\Data1c.TestRunner), потому что
    стандартный «dotnet test» в этом окружении не может поднять testhost: дочерний
    процесс не получает доступ к handle родителя. Раннер — обычный исполняемый файл,
    он работает и в ограниченных режимах.

    Перед сборкой обязательно вызывается «dotnet build-server shutdown», а сборка идёт
    с -nodeReuse:false и -m:1: иначе MSBuild в песочнице молча падает, оставляя после
    себя занятые файлы и переиспользуемые узлы (подробности — в tools\build.ps1).

    В конце печатается понятный итог, а код возврата ненулевой, если тесты не прошли.

    Скрипт работает из любого текущего каталога: корень репозитория вычисляется от
    расположения самого файла ($PSScriptRoot).

.PARAMETER Configuration
    Конфигурация сборки: Debug (по умолчанию) или Release.

.PARAMETER Filter
    Подстрока полного имени теста (без учёта регистра), например «DumpTextReader».
    Передаётся раннеру как --filter=<строка>.

.PARAMETER NoBuild
    Не собирать решение, а сразу запустить уже собранный раннер. Полезно при частых
    прогонах: сборка занимает больше времени, чем сами тесты.

.EXAMPLE
    pwsh -File tools\test.ps1

.EXAMPLE
    pwsh -File tools\test.ps1 -Filter DumpTextReader

.EXAMPLE
    pwsh -File tools\test.ps1 -Configuration Release -NoBuild

.NOTES
    Возвращает 0, если все тесты прошли, и ненулевой код иначе (1 — падения тестов,
    2 — окружение: нет решения или не собран раннер).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [string] $Filter,

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

# Корень репозитория — родитель каталога tools, где лежит этот скрипт.
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Data1C.sln'
$runner = Join-Path $root "tools\Data1c.TestRunner\bin\$Configuration\net10.0\data1c-test.dll"

if (-not (Test-Path -LiteralPath $solution)) {
    Write-Host "Не найден файл решения: $solution" -ForegroundColor Red
    exit 2
}

if (-not $NoBuild) {
    Write-Host "Сборка Data1C ($Configuration)" -ForegroundColor Cyan

    # См. tools\build.ps1: без остановки серверов сборки и без -nodeReuse:false -m:1
    # сборка в песочнице может завершиться молчаливым сбоем.
    & dotnet build-server shutdown
    & dotnet build $solution -c $Configuration -v q --nologo -nodeReuse:false -m:1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Сборка НЕ УДАЛАСЬ (код $LASTEXITCODE). Тесты не запускались." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    Write-Host "Сборка успешна." -ForegroundColor Green
}

if (-not (Test-Path -LiteralPath $runner)) {
    Write-Host "Не найден раннер тестов: $runner" -ForegroundColor Red
    Write-Host 'Соберите решение без -NoBuild: pwsh -File tools\test.ps1' -ForegroundColor Yellow
    exit 2
}

if ($Filter) {
    Write-Host "Тесты (фильтр «$Filter»)" -ForegroundColor Cyan
    $output = & dotnet $runner "--filter=$Filter" 2>&1
}
else {
    Write-Host 'Тесты (весь набор)' -ForegroundColor Cyan
    $output = & dotnet $runner 2>&1
}

$exitCode = $LASTEXITCODE

# Раннер печатает строку «Пройдено: N, не пройдено: M, пропущено: K». Разбираем её,
# чтобы отличить падение тестов от фильтра, который не нашёл ни одного теста.
$summary = $output | Select-String -Pattern 'Пройдено:\s*(\d+),\s*не пройдено:\s*(\d+),\s*пропущено:\s*(\d+)' | Select-Object -Last 1
$output | Write-Output

if ($exitCode -eq 0 -and $summary -and $summary.Matches[0].Groups[1].Value -eq '0') {
    Write-Host 'ИТОГ: ни один тест не выполнен — проверьте фильтр.' -ForegroundColor Red
    exit 3
}

if ($exitCode -eq 0) {
    $passed = $summary.Matches[0].Groups[1].Value
    Write-Host "ИТОГ: все тесты пройдены ($passed)." -ForegroundColor Green
}
else {
    Write-Host "ИТОГ: есть непройденные тесты (код $exitCode). Подробности — выше, в блоках FAIL." -ForegroundColor Red
}

exit $exitCode
