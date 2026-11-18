# Установка PdmLite

Есть два способа:

| Способ | Кому | Что нужно на компьютере |
|---|---|---|
| **Из исходников скриптами `deploy\`** (раздел 1) | Тем, у кого есть исходники и кто сам собирает и обновляет PdmLite | Windows x64, Inventor 2025, .NET 8 SDK |
| **Готовый установщик `PdmLite-Setup-0.1-*.exe`** (раздел 2) | Обычным пользователям | Windows x64, Inventor 2025 |

В обоих случаях установка идёт **для текущего пользователя Windows** (папки в профиле пользователя).
Права администратора нужны только для установки PostgreSQL.

---

## 1. Установка из исходников (`deploy\install.ps1`)

### 1.1. Что нужно один раз

1. **Windows 10/11 x64** и **Autodesk Inventor 2025** (или Inventor View — для Explorer без Inventor).
2. **.NET 8 SDK x64**: <https://dotnet.microsoft.com/download/dotnet/8.0>. Проверка в терминале: `dotnet --list-sdks`
   должен показать строку `8.0.…` (подойдёт и более новый SDK).
3. **Исходники PdmLite** — одним из способов:
   - `git clone https://github.com/iposmygaev-pixel/Inventor-PDMLite.git`
   - или на GitHub: **Code → Download ZIP** (ветка `main`) и распаковать.

Все команды ниже выполняются в PowerShell **из папки исходников** — той, где лежат `PdmLite.sln` и папка `deploy`.

### 1.2. База данных PostgreSQL — один раз

```powershell
powershell -ExecutionPolicy Bypass -File deploy\install-postgres.ps1
```

Что делает скрипт:

1. Если PostgreSQL не установлен — ставит **PostgreSQL 16** через `winget` (появится запрос UAC).
2. Спрашивает пароль администратора PostgreSQL (пользователь `postgres`):
   - при новой установке — **придумайте и запомните** его;
   - если PostgreSQL уже стоит — введите существующий.
3. Создаёт роль и базу `pdmlite` со случайным паролем.
4. Записывает подключение в `%APPDATA%\PdmLite\database.json`. Пароль хранится зашифрованным (DPAPI) —
   прочитать его может только этот пользователь Windows.

Таблицы PdmLite создаёт сам при первом подключении.

Параметры (если нужно не по умолчанию): `-Port 5433`, `-Database pdmlite2`, `-User pdmlite2`.

> Если скрипт пишет «Роль pdmlite уже существует» — база для PdmLite уже настраивалась. Повторно запускать
> не нужно; если `database.json` потерян, укажите подключение вручную в Explorer → «Настройки библиотеки…»
> или запустите скрипт с другим именем роли: `-User pdmlite2 -Database pdmlite2`.

### 1.3. Сборка и установка программы

Закройте **Inventor** и **PdmLite Explorer**, затем:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\install.ps1
```

Что делает скрипт:

1. Собирает плагин и Explorer (`dotnet publish`, конфигурация Release). Первый раз — несколько минут:
   скачиваются пакеты NuGet, нужен интернет.
2. Кладёт новые файлы сначала во временную папку, потом заменяет ими старые **целиком**.
   При ошибке старая версия возвращается на место.
3. Регистрирует плагин в Inventor: `%APPDATA%\Autodesk\Inventor 2025\Addins\PdmLite.addin`.
4. Создаёт ярлык **«PdmLite Explorer»** на рабочем столе и в меню «Пуск».

В конце скрипт печатает, куда всё установлено.

Параметры: `-Configuration Debug` (отладочная сборка), `-InventorVersion 2025`.

### 1.4. Первый запуск

1. Запустите Inventor — на ленте появится вкладка **PdmLite**.
2. Запустите **PdmLite Explorer** (ярлык на рабочем столе).
3. Explorer → **«Настройки библиотеки…»**: проверьте подключение к базе и выберите папку библиотеки
   (по умолчанию `Документы\PdmLite\Library`).

Дальше — по [README](../README.md#что-реализовано) и [описанию библиотеки](LIBRARY.md#как-пользоваться-б1).

### 1.5. Обновление

Получите новые исходники (`git pull` или новый ZIP), закройте Inventor и Explorer и снова запустите

```powershell
powershell -ExecutionPolicy Bypass -File deploy\install.ps1
```

Программа заменяется целиком. Библиотека, база, резервные копии и настройки не трогаются.
`install-postgres.ps1` повторно запускать не нужно.

### 1.6. Удаление

```powershell
powershell -ExecutionPolicy Bypass -File deploy\uninstall.ps1
```

Удаляет плагин, Explorer, регистрацию в Inventor и ярлыки. **Сохраняются**: библиотека, резервные копии,
база PostgreSQL, настройки и журналы. Если PdmLite ставился EXE-установщиком, скрипт откажется работать —
удаляйте через «Параметры Windows → Приложения → PdmLite».

---

## 2. Готовый установщик EXE

Два варианта:

- `PdmLite-Setup-0.1-Online.exe` — PostgreSQL скачивается во время установки;
- `PdmLite-Setup-0.1-Offline.exe` — PostgreSQL внутри, интернет не нужен.

Запустите EXE от имени обычного пользователя и следуйте шагам мастера. Он проверит Inventor 2025, установит
отдельный PostgreSQL (служба `pdmlite-postgresql-16`, порт 55439, только localhost), развернёт начальную
библиотеку и зарегистрирует плагин. Обновление — запуск нового установщика; удаление — «Параметры Windows →
Приложения → PdmLite» (данные сохраняются).

Как **собрать** эти установщики и что именно они делают — в [RELEASE.md](RELEASE.md).

---

## 3. Где что лежит

| Что | Где |
|---|---|
| Плагин Inventor | `%LOCALAPPDATA%\PdmLite\AddIn` (сам плагин — в подпапке `core`) |
| PdmLite Explorer | `%LOCALAPPDATA%\PdmLite\Explorer\PdmLite.Explorer.exe` |
| Регистрация плагина | `%APPDATA%\Autodesk\Inventor 2025\Addins\PdmLite.addin` |
| Журналы | `%LOCALAPPDATA%\PdmLite\logs` |
| Настройки | `%APPDATA%\PdmLite\*.json` (`database.json` — подключение к базе, `library.json` — папки библиотеки и копий) |
| Библиотека (по умолчанию) | `Документы\PdmLite\Library` |
| Резервные копии библиотеки (по умолчанию) | `Документы\PdmLite\Backups` |
| Внешние утилиты | `Документы\PdmLite\Scripts` |

Повреждённый файл настроек не мешает запуску: рядом сохраняется его копия
`<имя>.json.damaged-<время>.bak`, а в журнал пишется причина.

---

## 4. Файлы папки `deploy`

### Для ручной установки (раздел 1)

| Файл | Зачем | Как запускать |
|---|---|---|
| `install-postgres.ps1` | Один раз: PostgreSQL 16, роль и база `pdmlite`, файл подключения | `powershell -ExecutionPolicy Bypass -File deploy\install-postgres.ps1` |
| `install.ps1` | Собрать и установить или обновить плагин и Explorer | `powershell -ExecutionPolicy Bypass -File deploy\install.ps1` |
| `uninstall.ps1` | Удалить программу, оставив данные | `powershell -ExecutionPolicy Bypass -File deploy\uninstall.ps1` |
| `create-shortcut.ps1` | Ярлык «PdmLite Explorer». `install.ps1` вызывает его сам; отдельно нужен, например, для сборки из Visual Studio | `powershell -ExecutionPolicy Bypass -File deploy\create-shortcut.ps1` (или `-Target <путь к exe>`) |
| `PdmLite.addin.template` | Шаблон регистрации плагина в Inventor; путь к DLL подставляют `install.ps1`, сборка проекта и установщик | Не запускается |

### Для сборки EXE-установщиков (раздел 2, подробно — [RELEASE.md](RELEASE.md))

| Файл | Зачем | Как запускать |
|---|---|---|
| `build-installer-online.cmd`, `build-installer-offline.cmd` (в корне исходников) | Собрать Online или Offline установщик двойным щелчком | Двойной щелчок |
| `build-release.ps1` | То же из PowerShell, с параметрами: `-Variant Online/Offline/Both`, `-LibraryBackup <zip>`, `-PostgreSqlInstaller <exe>`, `-IsccPath <ISCC.exe>` | `powershell -ExecutionPolicy Bypass -File deploy\build-release.ps1 -Variant Both` |
| `installer\PdmLite.iss` | Рецепт установщика Inno Setup; компилирует `build-release.ps1` | Не запускается вручную |
| `installer\postgresql.json` | Закреплённые версия, адрес и SHA-256 установщика PostgreSQL | Меняется только при обновлении PostgreSQL |
| `installer\install-postgresql-machine.ps1` | Шаг установщика с правами администратора: установка PostgreSQL | Запускает установщик |
| `installer\application-update.ps1` | Замена папок программы при обновлении с откатом | Запускает установщик |
| `installer\find-inno-setup.ps1`, `installer\release-hashes.ps1` | Поиск компилятора Inno Setup; файлы `SHA256SUMS*.txt` | Вызываются из `build-release.ps1` |
| `installer\legacy-postgres-functions.ps1` | Общие функции настройки PostgreSQL для `install-postgres.ps1` | Подключается скриптом |

### Проверки (запускает CI на каждом push)

`installer\test-*.ps1` — проверки удаления, обновления, прав на файл с паролем, функций PostgreSQL,
контрольных сумм и компиляция рецептов Inno Setup. Вручную их запускать не нужно; при желании:
`powershell -ExecutionPolicy Bypass -File deploy\installer\test-update.ps1`.

---

## 5. Если что-то не так

| Сообщение или симптом | Что делать |
|---|---|
| «…выполнение сценариев отключено в этой системе» | Запускать именно так, как в инструкции: `powershell -ExecutionPolicy Bypass -File …` |
| «Закройте Inventor перед установкой» / «Закройте PdmLite Explorer» | Закрыть программы (проверить в диспетчере задач) и повторить |
| «Не найдена сборка Inventor API: …Autodesk.Inventor.Interop.dll» | Inventor установлен не в стандартную папку. Перед `install.ps1` выполнить `$env:InventorInteropPath = 'D:\…\Autodesk.Inventor.Interop.dll'` в том же окне PowerShell |
| `dotnet` не найден | Установить .NET 8 SDK и открыть **новое** окно PowerShell |
| «Не найден winget» | Установить PostgreSQL 16 вручную (<https://www.postgresql.org/download/windows/>) и снова запустить `install-postgres.ps1` |
| В Inventor нет вкладки PdmLite | Inventor → «Инструменты» → «Надстройки»: PdmLite должен быть загружен; проверить файл `PdmLite.addin` и журналы в `%LOCALAPPDATA%\PdmLite\logs` |
| Explorer не подключается к базе | Explorer → «Настройки библиотеки…» → «Проверить подключение»; служба PostgreSQL должна быть запущена (`services.msc`) |
