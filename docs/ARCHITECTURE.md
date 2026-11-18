# Архитектура PdmLite

## Принципы

1. **Ядро не знает об Inventor и о конкретных модулях.** Все контракты — в `PdmLite.Core`.
2. **Модули не ссылаются друг на друга.** Общение — через интерфейсы ядра и шину событий.
3. **Один набор модулей — два хоста:** плагин Inventor и внешнее приложение Explorer.
4. **Inventor API изолирован** в адаптере — ядро и модули тестируются без Inventor.
5. **Массовые операции** (переименование и т.п.) — пошаговые, с резервной копией и откатом.

## Компоненты

```
          ┌──────────────────────┐   PdmLite.Link   ┌──────────────────────┐
 Хосты:   │ PdmLite.AddIn        │◄── каналы ──────►│ PdmLite.Explorer     │
          │ + PdmLite.AddIn.Core │   Windows, JSON  │ (WPF-приложение)     │
          │ (внутри Inventor,    │                  │                      │
          │  Inventor API)       │── процесс ──────►│ служебный режим:     │
          └──────────┬───────────┘   свойств        │ свойства через       │
                     │                              │ PdmLite.Apprentice   │
                     │                              └──────────┬───────────┘
                     │   загружают одни и те же модули         │
                     ▼                                         ▼
          ┌───────────────────────────────────────────────────────────┐
          │ Модули: ProjectTree, Properties, References, Rename,      │
          │         Library, Scripts, InventorLink (только Explorer)  │
          └───────────────┬───────────────────────────┬───────────────┘
                          ▼                           ▼
          ┌──────────────────────────────┐  ┌─────────────────────────┐
          │ PdmLite.Index — индекс ссылок,│  │ PdmLite.Library —       │
          │ карточка проекта, операции    │  │ стандартная библиотека  │
          │ с файлами                     │  │                         │
          └───────────────┬──────────────┘  └────────────┬────────────┘
                          └──────────────┬───────────────┘
                                         ▼
          PdmLite.Hosting (загрузка модулей, DI, настройки, журналы)
          PdmLite.Data (EF Core, PostgreSQL)
          PdmLite.Core — домен, контракты, события (без Inventor и WPF)
```

Отдельных адаптеров `PdmLite.Inventor` в коде нет: работа с Inventor API находится в
`PdmLite.AddIn.Core` (`InventorProperties`, `InventorPropertyStore`, `InventorProjects`,
`InventorComponentPlacer`, `InventorOpenDocuments` и т.д.) и выставляется модулям через интерфейсы ядра
(`IDocumentPropertyStore`, `IOpenDocuments`, `IComponentPlacer`, `IDocumentLauncher`, …).

Запись свойств из плагина идёт не через Inventor API, а через Apprentice **в отдельном процессе**:
плагин запускает PdmLite Explorer в служебном режиме без окна (`ApprenticeHost`, протокол — `PropertyHost`
в ядре, одна строка JSON на запрос). Причина: внутри Inventor Apprentice не работает, а Inventor API не может
записать копию файла Content Center, открытого в сборке.

## Структура решения

```
PdmLite.sln                  # всё решение (для AddIn, AddIn.Core, Apprentice, Explorer нужен Inventor 2025)
PdmLite.NoInventor.slnf      # всё, что собирается без Inventor: так собирает CI
├─ src/
│  ├─ PdmLite.Core/          # домен, контракты, шина событий, правила имён, IO (net8.0)
│  ├─ PdmLite.Link/          # связь плагин ⇄ Explorer: именованные каналы, JSON (net8.0)
│  ├─ PdmLite.Data/          # EF Core + PostgreSQL: схемы library.* и project.*, миграции (net8.0)
│  ├─ PdmLite.Hosting/       # загрузчик модулей, DI, настройки, журналы (net8.0)
│  ├─ PdmLite.Index/         # индекс ссылок, карточка проекта, переименование/перемещение (net8.0)
│  ├─ PdmLite.Library/       # стандартная библиотека: каталог, импорт, хранилище, резервные копии (net8.0)
│  ├─ PdmLite.UI.Shared/     # общие WPF-стили, размещение панелей, меню команд, иконки
│  ├─ PdmLite.Apprentice/    # iProperties и ссылки через Apprentice (Inventor View)
│  ├─ PdmLite.AddIn/         # загрузчик плагина (его видит Inventor)
│  ├─ PdmLite.AddIn.Core/    # хост: плагин Inventor (в изолированном контексте), работа с Inventor API
│  ├─ PdmLite.Explorer/      # хост: внешнее приложение; служебный режим «процесс свойств»
│  └─ Shared/                # исходники, общие для нескольких проектов (имена наборов iProperties)
├─ modules/
│  ├─ PdmLite.Modules.ProjectTree/    # дерево проекта
│  ├─ PdmLite.Modules.Properties/     # карточка документа
│  ├─ PdmLite.Modules.References/     # связи: где используется, состав, индекс и карточка проекта
│  ├─ PdmLite.Modules.Rename/         # переименование и перемещение с переносом ссылок, обновление связей
│  ├─ PdmLite.Modules.Library/        # стандартная библиотека: панель, каталог, импорт, проверки, копии
│  ├─ PdmLite.Modules.Scripts/        # внешние утилиты
│  └─ PdmLite.Modules.InventorLink/   # только Explorer: связь с запущенным Inventor
├─ tools/
│  ├─ CollectComponents/, ShortenNames/   # утилиты, поставляемые с PdmLite
│  ├─ PdmLite.Setup/                      # загрузчик установщика
│  └─ PdmLite.Data.Design/                # фабрика контекста для `dotnet ef migrations`
├─ tests/
│  ├─ PdmLite.Core.Tests/    # по папкам функций: Core, Hosting, Infrastructure, Link, Session, Utilities…
│  └─ PdmLite.Library.Tests/ # по папкам функций: Backup, Items, Import, Index, Storage…; нужен PostgreSQL
└─ deploy/                   # .addin-манифест, install.ps1, установщик (Inno Setup)
```

Модули не ссылаются друг на друга — только на ядро и общие библиотеки (`Index`, `Library`, `Data`,
`UI.Shared`); хосты подключают модули ссылками на проекты.

## Контракт модуля

```csharp
public interface IPdmModule
{
    ModuleInfo Info { get; }                      // Id, имя, версия, зависимости, SupportedHosts
    void ConfigureServices(IServiceCollection s); // регистрация своих сервисов
    void Initialize(IModuleContext ctx);          // подписки на события, регистрация UI
    void Shutdown();
}
```

- Включение/отключение — через `modules.json`; хост проверяет зависимости.
- UI модуль регистрирует через `IUiRegistry` (кнопки ленты, пункты контекстного меню, панели).
- Хост, в котором работает модуль, — `HostKind` (`Inventor`, `Explorer`): передаётся в `ConfigureServices`
  и доступен в `IModuleContext.Host`; `ModuleInfo.SupportedHosts` ограничивает, где модуль загружается.
  Недоступные в хосте действия модуль скрывает сам.

## Загрузка плагина в Inventor

Inventor 2025 загружает плагины .NET 8 в общий контекст сборок, где уже есть его собственные
версии библиотек (например, Microsoft.Extensions.* 6.0). Чтобы версии плагина не конфликтовали
с ними, плагин разделён на две части:

```
<папка плагина>\
  PdmLite.AddIn.dll         загрузчик без зависимостей — его указывает PdmLite.addin
  core\
    PdmLite.AddIn.Core.dll  плагин
    PdmLite.*.dll, Microsoft.Extensions.*.dll, Serilog*.dll …  — свои версии
```

Загрузчик создаёт отдельный `AssemblyLoadContext` (`AddInLoadContext`) и загружает в него
плагин по его `.deps.json`. Общими с Inventor остаются только Inventor API и сборки .NET/WPF.
Вызовы `Activate`/`Deactivate` загрузчик передаёт плагину через интерфейс
`ApplicationAddInServer` из Inventor API.

## Связь плагина и Explorer

Два процесса связаны **именованными каналами Windows** (`PdmLite.Link`), сообщения — строки JSON.
Канал создаётся для текущего пользователя (`PipeOptions.CurrentUserOnly`), имя:
`PdmLite.{Inventor|Explorer}.{пользователь}`.

```
┌───────────── Inventor ─────────────┐            ┌──────────── PdmLite Explorer ───────────┐
│ PdmLite.AddIn                      │            │ модуль InventorLink                     │
│  InventorLinkServer  ◄─────────────┼── запросы ─┤  InventorConnection (переподключение)  │
│   ping, getStatus, openDocument    │            │   двойной щелчок → openDocument         │
│                      ──────────────┼── события ►│   события → шина (ActiveDocumentChanged)│
│   activeDocumentChanged,           │            │                                         │
│   documentSaved, documentClosed    │            │ ExplorerLinkServer                      │
│                                    │            │   openProject, revealDocument           │
│  «Показать в Explorer» ────────────┼── запрос ─►│   (и для второго экземпляра Explorer)   │
│   (или запуск Explorer с --reveal) │            │                                         │
└────────────────────────────────────┘            └─────────────────────────────────────────┘
```

- Explorer работает и без Inventor: связь восстанавливается автоматически, когда Inventor запускается.
- Плагин находит Explorer по пути, который Explorer записывает в `%AppData%\PdmLite\settings.json`
  при каждом запуске (или в папке установки `%LOCALAPPDATA%\PdmLite\Explorer`).
- Explorer — единственный экземпляр: повторный запуск передаёт аргументы работающему окну.
- Inventor API вызывается только в главном потоке Inventor (`IUiDispatcher`).
- Общие данные (индекс ссылок, библиотека, журнал библиотеки) — в PostgreSQL; канал — для оперативного
  взаимодействия («открой», «покажи», «что сейчас активно»).

## Модули

| Модуль | Состояние | Назначение |
|---|---|---|
| **ProjectTree** | сделан | Дерево проекта: папки и файлы; `DockableWindow` в Inventor, главное окно в Explorer |
| **Properties** | сделан | Карточка документа (iProperties) |
| **References** | сделан | Индекс ссылок проекта в PostgreSQL (`project.files`, `project.file_references`, сервис `PdmLite.Index`): «из чего состоит», «где используется» (прямо и на всех уровнях), битые ссылки; карточка состояния проекта |
| **Rename** | сделан | Переименование и перемещение файлов и папок с переносом ссылок и откатом; «Обновить связи проекта» |
| **Library** | сделан (шаги Б1–Б4, Б6) | Стандартная библиотека — см. [LIBRARY.md](LIBRARY.md) |
| **Scripts** | сделан | Запуск внешних утилит из `Документы\PdmLite\Scripts` |
| **InventorLink** | сделан | Только Explorer: связь с запущенным Inventor |
| **Preview** | не начат | Миниатюры, просмотр PDF чертежей |

### Операция переименования

```
Validate → BuildPlan → Preview (диалог) → Backup → Execute → UpdateIndex → Done
                                                     │ ошибка
                                                     ▼
                                                  Rollback
```

## Модель данных

Контекст — `PdmDbContext` (`src/PdmLite.Data`), миграции — `src/PdmLite.Data/Migrations`.

```
library.categories        разделы библиотеки (дерево)
library.items             компоненты: обозначение, тип, стандарт, производитель, путь, версия, геометрия
library.item_versions     прежние версии компонента (файлы — в _versions)
library.item_properties   свойства компонента
library.item_references   состав сборок библиотеки
library.category_rules    правила раздела
library.designation_rules правила замены в обозначениях
library.log               журнал изменений библиотеки
project.files             индекс ссылок: документы проекта (обозначение, наименование, размер, дата)
project.file_references   индекс ссылок: ссылки между документами
```

Пользователей, ролей, проектов как сущностей и журнала операций с файлами проекта в базе пока нет:
работает один пользователь с правами администратора (см. [DECISIONS.md](DECISIONS.md)).

## Технические особенности

- Inventor API вызывается только из главного потока (`IUiDispatcher`).
- Тяжёлое сканирование — в фоне, с прогрессом и отменой.
- Apprentice требует установленного Inventor или Inventor View.
- Файлы проекта читаются и пишутся напрямую в рабочей папке; файлы библиотеки — через `LibraryStorage`
  (`PdmLite.Library.Storage`). Общей абстракции хранилища с версиями (check-in / check-out) пока нет —
  её нужно будет ввести перед этапом 2.

## Порядок реализации

1. ✔ Core, Hosting, каркасы AddIn и Explorer.
2. ✔ Data (PostgreSQL); работа с Inventor API (в AddIn.Core) и Apprentice.
3. ✔ Индекс ссылок (References).
4. ✔ ProjectTree.
5. ✔ Properties; Preview — не начат.
6. ✔ Rename.
7. ✔ Стандартная библиотека (Б1–Б4, Б6).
8. ✔ Тесты, установщик, документация.

## Индекс ссылок проекта

- Сканирование (Explorer, Apprentice): документы `.ipt/.iam/.idw/.dwg/.ipn` папки проекта без `OldVersions`;
  у каждого — обозначение, наименование и ссылки (`FileDescriptor`). Повторно читаются только файлы с другим
  размером или датой; пропавшие удаляются из индекса. Остановить можно в любой момент — прочитанное сохраняется пачками.
- При открытии уже проиндексированного проекта Explorer тихо дочитывает изменения в фоне.
- Пути сравниваются без учёта регистра и вида разделителя (`ReferenceIndexService.Key`).
- Панель «Связи» читает только базу — работает и в Inventor.
- Основа для переименования и обновления сборок: какие документы затронет изменение файла.


## Переименование и перемещение

Модуль `PdmLite.Modules.Rename` (окно и меню), логика — `PdmLite.Index/Operations` (без UI, с тестами):

- `FileOperationRequests` — правило имён (`FileNameBuilder`), связанные документы, списки «было → станет»
  для переименования и перемещения, iProperties.
- `ProjectFileOperations` — план и выполнение:
  1. **План** (только чтение): проверки (внутри проекта, имя свободно, нет недопустимых символов и
     двойных имён, длина пути, «только чтение») и кто ссылается на переносимые файлы — по индексу ссылок
     (перед этим он дочитывается) или, без базы, чтением всех документов проекта.
  2. **Выполнение**: документы, загруженные в Inventor, должны быть закрыты (`IOpenDocuments`: в Inventor
     они сохраняются, закрываются и после операции открываются снова; из Explorer — список через связь,
     закрывает пользователь) → копии изменяемых на месте документов и журнал — в
     `%LOCALAPPDATA%\PdmLite\operations\<время>` → файлы копируются по новым путям (старые пока на месте,
     ссылки на них находятся) → в каждом документе-родителе ссылки заменяются (Apprentice
     `FileDescriptor.ReplaceReference`) и **проверяются** чтением → пишутся iProperties → старые файлы удаляются,
     опустевшие папки убираются, индекс ссылок обновляется.
  3. Сбой до удаления старых файлов — **откат**: новые копии удаляются, изменённые документы
     восстанавливаются из копий.

**Папки** переименовываются и переносятся так же: их файлы — обычными переносами, после них папка
переносится целиком (OldVersions, пустые подпапки), старая удаляется.

**Обновить связи проекта** (`FindBrokenLinksAsync` + `PlanRepair`): потерянные ссылки — по индексу или
чтением документов; кандидаты — файлы проекта с тем же именем; выполнение — тем же `ExecuteAsync`
(только замена ссылок, с копиями, проверкой и откатом). Ссылка на отсутствующий файл заменяется Apprentice
`FileDescriptor.ReplaceReference` — так же, как «Замена ссылок» в Design Assistant.
