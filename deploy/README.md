# deploy — установка и сборка установщиков PdmLite

Полная инструкция: [docs/INSTALL.md](../docs/INSTALL.md).

Быстрый путь (из папки исходников, Inventor и Explorer закрыты):

```powershell
# один раз: PostgreSQL и база pdmlite
powershell -ExecutionPolicy Bypass -File deploy\install-postgres.ps1
# установка и каждое обновление
powershell -ExecutionPolicy Bypass -File deploy\install.ps1
# удаление (данные сохраняются)
powershell -ExecutionPolicy Bypass -File deploy\uninstall.ps1
```

| Файл | Зачем |
|---|---|
| `install-postgres.ps1` | Один раз: PostgreSQL 16 через winget, роль и база `pdmlite`, файл подключения `%APPDATA%\PdmLite\database.json` |
| `install.ps1` | Собрать и установить или обновить плагин и Explorer в `%LOCALAPPDATA%\PdmLite`, зарегистрировать плагин, создать ярлык |
| `uninstall.ps1` | Удалить ручную установку; библиотека, база и настройки остаются |
| `create-shortcut.ps1` | Ярлык «PdmLite Explorer» (вызывается из `install.ps1`; отдельно — для сборки из Visual Studio) |
| `PdmLite.addin.template` | Шаблон регистрации плагина в Inventor |
| `build-release.ps1` | Сборка EXE-установщиков Online и Offline (см. [docs/RELEASE.md](../docs/RELEASE.md)); то же двойным щелчком — `build-installer-*.cmd` в корне |
| `installer\` | Рецепт Inno Setup, закреплённая версия PostgreSQL, шаги установщика и их проверки (`test-*.ps1` запускает CI) |
