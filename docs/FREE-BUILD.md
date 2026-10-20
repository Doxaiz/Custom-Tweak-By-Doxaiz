# Свободная сборка без GitHub Billing

GitHub Actions сейчас падает: `payments have failed / spending limit`. Ниже — бесплатные обходы для WPF.

## 1. Azure Pipelines (рекомендуется, бесплатно для Public)
*Public-проект = **неограниченные минуты** на `windows-latest` + 10 параллельных агентов. Приватный = 1800 мин/мес.*

1. Зайди в https://dev.azure.com → `New Project` → подключи GitHub репо `Doxaiz/Custom-Tweak-By-Doxaiz` (OAuth).
2. `Pipelines → Create Pipeline → Azure Repos Git → Existing Azure Pipelines YAML file` → выбери `azure-pipelines.yml` из корня.
3. Запусти: на каждый `push main/tags v*` и PR соберётся как в GitHub: `build → tests (874) → compact/portable → NSIS → SHA256`.
4. Артефакты: `CustomTweakByDoxaiz-win-x64` (exe/zip/png/log). При желании добавь публикацию в GitHub Releases через `GitHubRelease@1` таск с `PAT`.

> Файл уже в репе: [`azure-pipelines.yml`](../azure-pipelines.yml) — полный аналог `.github/workflows/build.yml`.

## 2. GitLab CI
*Хостинговые `saas-windows-medium-amd64` (Beta) доступны на Free-тарифе.*

1. Зеркало: `GitLab → Create project → Run CI/CD for external repo → GitHub` или пуш в `gitlab.com`.
2. Файл [` .gitlab-ci.yml`](../.gitlab-ci.yml) уже добавлен — три стадии `build/test/package`, NSIS ставится через `choco`.
3. Артефакты хранятся 1 неделю.

## 3. CircleCI (Windows VM)
Бесплатно 30k кредитов (Public — до 80k через конвертацию). Добавь `.circleci/config.yml` с `executor: win/server-2022`.

## 4. Локально на Windows (самый быстрый)
Без облака, NSIS/WiX/CreateInstall — всё бесплатно:

```powershell
# 1. .NET 10 + NSIS (choco)
choco install dotnet --version 10.0.100 -y
choco install nsis -y

# 2. Сборка
dotnet build PingForge.slnx -c Release
dotnet test PingForge.slnx -c Release
dotnet publish src/PingForge.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/portable
dotnet publish src/PingForge.Service.Host -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/portable
Copy-Item scripts/*.ps1 dist/portable -Force

# 3. Установщик (есть готовый CustomTweakByDoxaiz.nsi)
$ver=(Select-String '<Version>(.*)</Version>' Directory.Build.props).Matches[0].Groups[1].Value
makensis -INPUTCHARSET UTF8 "-DVERSION=$ver" "-DSOURCE_DIR=$((Resolve-Path dist/portable).Path)" "-DOUT_DIR=$((Resolve-Path dist).Path)" installer/CustomTweakByDoxaiz.nsi

# 4. Релиз вручную
gh release create v0.2.0 --title "Custom Tweak By Doxaiz v0.2.0" --notes "см. CHANGELOG" dist/*.exe dist/*.zip dist/SHA256SUMS.txt
```

* WiX Toolset: `dotnet tool install --global wix && wix build installer/Product.wxs`
* CreateInstall Light / Installer Studio — альтернативы с GUI, но проверяй лицензию.

После починки биллинга можно вернуться на GitHub Actions — все файлы совместимы.
