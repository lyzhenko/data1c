# MCP-сервер Data1C: установка и настройка

Практическая инструкция по подключению MCP-сервера разбора выгрузки 1С к DeepSeek Harness (`dsh`).
Собрана по реальной настройке: все команды и сообщения проверены на этой машине.

---

## 1. Что это такое

`Data1c.Mcp` — MCP-сервер (stdio, JSON-RPC 2.0) над библиотекой разбора выгрузки конфигурации 1С.
Он даёт агенту 16 инструментов: `status`, `open`, `search`, `similar`, `grep`, `node`, `neighbors`,
`code`, `metadata`, `entrypoints`, `platform`, `check`, `types`, `rights`, `conventions`, `reload`.

Схема работы:

```
DeepSeek Harness (профиль desktop или web)
   └── плагин @deepseek-ai/dsh-mcp-client      ← запись в cordis.patch.yml
         └── процесс Data1c.Mcp.exe --dump … --platform   ← stdio-программа
               ├── файлы выгрузки 1С (XML + BSL)
               ├── справка платформы (.hbk) — при ключе --platform
               └── индекс SQLite <выгрузка>/.data1c/index.db
```

Инструменты видны модели как `mcp__data1c__<имя>`. Внешних зависимостей у сервера нет — только BCL
и SQLite, поэтому он собирается без доступа к nuget.org.

---

## 2. Что нужно перед началом

| Требование | Как проверить |
|---|---|
| Windows, .NET 10 runtime | `dotnet --list-runtimes` → должна быть строка `Microsoft.NETCore.App 10.x` |
| Установленная платформа 1С (для `--platform`) | `C:\Program Files\1cv8\...\bin\shcntx_ru.hbk` |
| Выгрузка конфигурации в файлы | `Test-Path '<путь>\Configuration.xml'` → `True` |
| dsh | `Get-Command dsh` |
| Место на диске под индекс | ориентир — единицы ГБ (пример ниже: 6,7 ГБ) |

.NET SDK нужен только если вы собираете сервер из исходников.

---

## 3. Установка сервера

### 3.1 Собрать из репозитория

```powershell
git clone <адрес-репозитория> D:\dev\data1c
cd D:\dev\data1c
dotnet build Data1C.sln -c Release
# или штатным скриптом: tools\build.ps1
```

Результат сборки — каталог `src\Data1c.Mcp\bin\Release\net10.0\`.

### 3.2 Положить сборку в постоянное место

Копировать каталог нужно **целиком**: apphost `Data1c.Mcp.exe` без `Data1c.Mcp.deps.json`,
`Data1c.Mcp.runtimeconfig.json`, соседних `Data1c.*.dll`, `Microsoft.Data.Sqlite.dll`,
`SQLitePCLRaw*.dll` и `runtimes\win-x64\native\e_sqlite3.dll` не запустится.

Рекомендуемое место — отдельный каталог рядом с dsh, но **не внутри `profiles\`**: любой каталог в
`%USERPROFILE%\.dsh\profiles\` dsh считает профилем.

```powershell
$dst = "$env:USERPROFILE\.dsh\tools\mcp-data1c"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "D:\dev\data1c\src\Data1c.Mcp\bin\Release\net10.0\*" $dst -Recurse -Force
```

### 3.3 Проверить, что сервер запускается

```powershell
& "$env:USERPROFILE\.dsh\tools\mcp-data1c\Data1c.Mcp.exe" --help
```

Должна напечататься справка. Полная проверка протокола (сервер должен ответить `initialize`
и списком из 16 инструментов):

```powershell
$exe = "$env:USERPROFILE\.dsh\tools\mcp-data1c\Data1c.Mcp.exe"
$req = @(
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}',
  '{"jsonrpc":"2.0","method":"notifications/initialized"}',
  '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
)
$req | & $exe --dump 'D:\1c_conf' --platform 2>&1 | Select-Object -First 2
```

Ожидаемая первая строка (диагностика идёт в stderr, stdout занят протоколом):

```
data1c-mcp 0.1.0: выгрузка D:\1c_conf; состояние: ожидание; индекс: D:\1c_conf\.data1c\index.db (готов)
```

---

## 4. Подключение к dsh

### 4.1 Куда вписывать

Патч-слои применяются в таком порядке:

1. бандлы профиля;
2. `%USERPROFILE%\.dsh\profiles\<профиль>\cordis.patch.yml`;
3. `%USERPROFILE%\.dsh\cordis.patch.yml` — домашний, действует сразу для всех профилей;
4. оверлеи `--patch <файл>`, переданные в командной строке `dsh` (ключ можно повторять).

Профили: `desktop` — приложение DeepSeek Harness; `web` — запуск `dsh web`.

В `web` и `desktop` включён `patchReload: live`: корректная правка патча пересобирает дерево без
перезапуска. Гарантированный способ применить правку — перезапустить harness.

Запись должна быть **одна**: одинаковый `id` (`mcp-data1c`) и одинаковый `serverName` (`data1c`)
в одном слое приводят к тому, что вторая запись не загрузится.

### 4.2 Минимальный фрагмент (выгрузку открывает агент)

```yaml
- insert:
    - id: mcp-data1c
      name: "@deepseek-ai/dsh-mcp-client"
      config:
        serverName: data1c
        transport: stdio
        command: 'C:\Users\<пользователь>\.dsh\tools\mcp-data1c\Data1c.Mcp.exe'
        args:
          - '--platform'
        toolCallTimeoutMs: 1800000
        failOnStartupError: false
```

Такой вариант удобен тем, что одна запись работает для любого проекта: нужную выгрузку агент
открывает инструментом `open`.

### 4.3 Фрагмент с фиксированной выгрузкой и расширением

```yaml
- insert:
    - id: mcp-data1c
      name: "@deepseek-ai/dsh-mcp-client"
      config:
        serverName: data1c
        transport: stdio
        command: 'C:\Users\<пользователь>\.dsh\tools\mcp-data1c\Data1c.Mcp.exe'
        args:
          - '--dump'
          - 'D:\1c_conf'
          - '--dump'
          - 'D:\1c_extentsion'
          - '--platform'
        toolCallTimeoutMs: 1800000
        failOnStartupError: false
```

Правила оформления аргументов:

- **каждый аргумент — отдельная строка списка**, в одинарных кавычках;
- обратный слэш внутри одинарных кавычек YAML не экранируется, поэтому пути Windows пишутся как есть;
- **`--dump` повторяется для каждого источника**: первый каталог — база, следующие — расширения
  (они перекрывают базу по совпадающим относительным путям);
- указывается **каталог**, где лежит `Configuration.xml`, а не сам файл;
- ключ `--dump` можно не задавать вовсе — тогда выгрузку открывает агент инструментом `open`.

### 4.4 Проверить собранное дерево

```powershell
dsh --dump-config        # покажет итоговые параметры записи
```

---

## 5. Ключи самого сервера

Передаются в `args` записи.

| Ключ | Значение |
|---|---|
| `--dump <путь>` | Каталог выгрузки. Ключ можно повторять: первый — база, следующие — расширения. Можно не задавать. |
| `--index <путь>` | Файл индекса. По умолчанию `<выгрузка>/.data1c/index.db`. |
| `--no-index` | Не использовать индекс: разбирать выгрузку в память при каждом запуске. |
| `--watch <секунды>` | Следить за выгрузкой и пересобирать индекс при изменениях (по умолчанию выключено). |
| `--no-external` | Не держать в индексе внешние цели: индекс заметно меньше. |
| `--platform` | Подключить справку установленной платформы 1С (`.hbk`). |
| `--locale <код>` | Язык справочных файлов платформы, по умолчанию `ru`. |
| `--platform-root <путь>` | Каталог установленных платформ (можно повторять). |
| `--sections <список>` | Разбирать только эти секции выгрузки, через запятую. |
| `--no-bsl` | Не разбирать модули BSL. |
| `--no-calls` | Не строить связи вызовов. |
| `--max-dop <N>` | Степень параллелизма разбора (по умолчанию — по числу процессоров). |
| `--lazy` | Начать разбор при первом вызове инструмента, а не при старте. |
| `--help` | Показать справку. |

## 6. Поля MCP-клиента

| Поле | По умолчанию | Значение |
|---|---|---|
| `serverName` | обязательно | Пространство имён инструментов: `mcp__<serverName>__<tool>`. |
| `transport` | обязательно | `stdio` (наш случай) или `streamable-http`. |
| `command` / `args` / `env` / `cwd` | — | Для stdio: программа, аргументы, переменные окружения, рабочий каталог. |
| `url` / `headers` | — | Для `streamable-http`. |
| `toolCallTimeoutMs` | 60000 | Таймаут одного вызова инструмента. Для больших выгрузок ставьте с запасом. |
| `failOnStartupError` | false | `true` — отказ подключения при старте валит harness и виден сразу; `false` — harness стартует молча, без инструментов. |

---

## 7. Рабочий цикл агента

```
open → status → search / grep / metadata → similar / conventions → code / types / entrypoints / rights
     → правка файлов выгрузки → check (в том числе по тексту черновика) → reload
```

Что полезно помнить:

- разбор — снимок; после правки модулей вызывайте `reload`, иначе поиск и граф покажут прежнее
  состояние (`code` и `check` читают файлы с диска и видят правку сразу);
- `grep` разбор не ждёт: ищет по файлам сразу, имена владельцев подтягивает в пределах `waitMs`
  (по умолчанию 60 с), иначе отвечает `ownersResolved: false`;
- пока harness держит сервер запущенным, Release-сборка **не может перезаписать** `Data1c.Mcp.dll`
  (файл занят) — собирайте Debug или публикуйте в отдельный каталог.

---

## 8. Типичные ошибки

| Симптом | Причина | Лечение |
|---|---|---|
| `Неизвестный ключ <путь>`, exit=2, печатается справка | второй каталог задан без повтора `--dump` | повторить ключ перед каждым путём |
| `Unhandled exception. System.BadImageFormatException: Bad IL format` | `dotnet.exe` запускает `.exe`-apphost | запускать `.exe` напрямую либо `.dll` через `dotnet` |
| `состояние: ошибка: Каталог выгрузки не найден: <путь>` | опечатка в пути выгрузки/расширения | проверить `Test-Path '<путь>\Configuration.xml'` |
| Инструменты `mcp__data1c__*` пропали, вызов даёт `unknown tool` | сервер не стартовал, а `failOnStartupError: false` скрыл отказ | смотреть диагностику сервера вручную (п. 3.3), поставить `true`, перезапустить harness |
| Первый вызов инструмента идёт минутами, `indexReady: false` | собирается индекс | дождаться по `status`; `toolCallTimeoutMs` с запасом |
| После правки набора `--dump` индекс собирается заново | сменился состав источников | это нормально; помогает `--no-external` |
| Не создаётся `.data1c` рядом с выгрузкой | выгрузка на шаре только для чтения | `--index <локальный путь>` |
| Индекс занимает единицы ГБ | в него попадают все обращения и внешние цели | `--no-external`, `--sections`, `--index` на нужный диск |

---

## 9. Готовый фрагмент под текущую машину

Проверенная конфигурация (профиль `desktop`, файл
`%USERPROFILE%\.dsh\profiles\desktop\cordis.patch.yml`):

```yaml
- insert:
    - id: mcp-data1c
      name: "@deepseek-ai/dsh-mcp-client"
      config:
        serverName: data1c
        transport: stdio
        command: 'C:\Users\Лыженко Александр\.dsh\profiles\mcp_1с\Data1c.Mcp.exe'
        args:
          - '--dump'
          - 'D:\1c_conf'
          - '--dump'
          - 'D:\1c_extentsion'
          - '--platform'
        toolCallTimeoutMs: 1800000
        failOnStartupError: false
```

Что здесь к чему:

| Элемент | Значение |
|---|---|
| `Data1c.Mcp.exe` | сборка сервера; сейчас лежит в `profiles\mcp_1с\` (лучше перенести в `.dsh\tools\mcp-data1c\`, чтобы каталог не выглядел профилем) |
| `D:\1c_conf` | база: каталог с `Configuration.xml`, `ConfigDumpInfo.xml` и секциями (`Catalogs`, `Documents`, …) |
| `D:\1c_extentsion` | расширение «ЭЛ» (в `Configuration.xml`: `ConfigurationExtensionPurpose = Customization`); в имени каталога опечатка — «extentsion» |
| `D:\1c_conf\.data1c\index.db` | индекс по этой выгрузке, ~6,7 ГБ |
| репозиторий | `D:\dev\data1c`, исходный фрагмент — `tools\harness\mcp-data1c.patch.yml` |

Проверка после перезапуска harness: в чате `status` должен показать
`выгрузка D:\1c_conf + D:\1c_extentsion`; когда состояние сменится на `готов` — работают все
инструменты, `search` отвечает за десятки миллисекунд.

> Примечание: при добавлении расширения прежний индекс, собранный только по базе, не переиспользуется —
> первый запрос запускает сборку заново (для этой конфигурации: 47 526 объектов метаданных).
