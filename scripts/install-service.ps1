#Requires -RunAsAdministrator
<#
    Установка службы Custom Tweak By Doxaiz.

    Что делает этот скрипт — и только это:
      1) копирует файлы программы и службы в папку установки;
      2) регистрирует службу Windows с именем PingForge и запускает её;
      3) настраивает перезапуск при сбое (3 попытки);
      4) добавляет ярлык программы в меню «Пуск».

    Чего скрипт не делает: не меняет настройки сети, не трогает реестр Windows, не отключает
    Защитник и брандмауэр, не создаёт маршрутов. Всё, что меняет систему, делает сама программа —
    после нажатия кнопки в окне и с вашего подтверждения.

    Примеры запуска:
        .\install-service.ps1
        .\install-service.ps1 -InstallDir "D:\Games Utils\CustomTweak" -NoShortcut
        .\install-service.ps1 -SkipService          # только файлы, без службы (для проверки)
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\Custom Tweak By Doxaiz",
    [string]$ServiceName = 'PingForge',
    [switch]$SkipService,
    [switch]$NoShortcut,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say([string]$text) { Write-Host $text }
function Ok([string]$text) { Write-Host "  [готово] $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "  [внимание] $text" -ForegroundColor Yellow }
function Fail([string]$text) { Write-Host "  [ошибка] $text" -ForegroundColor Red }

Say ''
Say 'Custom Tweak By Doxaiz — установка'
Say "Папка установки: $InstallDir"
Say ''

# ── 1. Проверки перед началом ───────────────────────────────────────────────────────────────────

$appSource = Join-Path $scriptDir 'CustomTweakByDoxaiz.exe'
$serviceSource = Join-Path $scriptDir 'CustomTweakByDoxaiz.Service.exe'

if (-not (Test-Path $appSource)) {
    Fail "Не найден файл программы: $appSource"
    Fail 'Распакуйте архив целиком и запустите скрипт из той же папки.'
    exit 1
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Fail 'Нужны права администратора: без них службу не зарегистрировать.'
    Fail 'Щёлкните правой кнопкой по PowerShell → «Запуск от имени администратора» и повторите.'
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and -not $Force) {
    Warn "Служба «$ServiceName» уже установлена (состояние: $($existing.Status))."
    Warn 'Переустановка: повторите с ключом -Force. Снять совсем: .\uninstall-service.ps1'
    exit 1
}

# ── 2. Копирование файлов ───────────────────────────────────────────────────────────────────────

Say 'Шаг 1. Копирую файлы.'
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Скрипт зовут из двух мест: из распакованного архива (тогда файлы надо скопировать) и из установщика,
# который уже положил файлы на место. Во втором случае копировать файл сам в себя нельзя.
$sameFolder = ($scriptDir.TrimEnd('\') -ieq $InstallDir.TrimEnd('\'))
if ($sameFolder) {
    Ok 'Скрипт запущен из папки установки: файлы уже на месте.'
} else {
    Get-ChildItem -Path $scriptDir -File | Where-Object { $_.Extension -in '.exe', '.dll', '.json', '.ps1', '.md' } |
        ForEach-Object { Copy-Item $_.FullName -Destination $InstallDir -Force }
}

# Из компактной сборки нужен рантайм .NET; проверяем и говорим прямо, если его нет.
if (-not (Test-Path (Join-Path $InstallDir 'e_sqlite3.dll'))) {
    Warn 'Похоже, архив распакован не полностью: не хватает библиотеки SQLite.'
}

$appPath = Join-Path $InstallDir 'CustomTweakByDoxaiz.exe'
$servicePath = Join-Path $InstallDir 'CustomTweakByDoxaiz.Service.exe'
Ok "Файлы на месте: $InstallDir"

if ($SkipService) {
    Warn 'Ключ -SkipService: служба не регистрируется. Программа запустится, но менять настройки не сможет.'
    Say ''
    Say "Запуск программы: $appPath"
    exit 0
}

if (-not (Test-Path $servicePath)) {
    Fail "Не найден файл службы: $servicePath"
    exit 1
}

# ── 3. Регистрация службы ───────────────────────────────────────────────────────────────────────

Say ''
Say 'Шаг 2. Регистрирую службу Windows.'

if ($existing) {
    Say "  Останавливаю прежнюю службу «$ServiceName»…"
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Кавычки вокруг пути обязательны: в пути есть пробелы.
$binaryPath = '"' + $servicePath + '"'
$displayName = 'PingForge (Custom Tweak By Doxaiz)'
$description = 'Оптимизация соединения для игр: маршрут до игрового сервера, приоритет пакетов, твики Windows. Служба работает от LocalSystem и принимает команды от окна программы.'

# Описание службы передаём, только если эта версия PowerShell его умеет: у старых
# Windows PowerShell параметра -Description у New-Service может не быть, а падать
# из-за подписи к службе нельзя.
$newService = @{
    Name           = $ServiceName
    BinaryPathName = $binaryPath
    DisplayName    = $displayName
    StartupType    = 'Automatic'
}
if ((Get-Command New-Service).Parameters.ContainsKey('Description')) {
    $newService['Description'] = $description
}

try {
    New-Service @newService -ErrorAction Stop | Out-Null
} catch {
    Fail "Не удалось зарегистрировать службу: $($_.Exception.Message)"
    Fail "Вручную то же самое: sc.exe create $ServiceName binPath= `"$servicePath`" start= auto"
    exit 1
}
Ok 'Служба зарегистрирована, запуск: автоматически.'

# Перезапуск при сбое: три попытки с минутой между ними. Служба не должна «умереть молча».
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Ok 'Настроен перезапуск при сбое (3 попытки).'

try {
    Start-Service -Name $ServiceName -ErrorAction Stop
} catch {
    Fail "Служба зарегистрирована, но не запустилась: $($_.Exception.Message)"
    Say '  Что говорит Windows:'
    sc.exe query $ServiceName | ForEach-Object { Say "    $_" }
    exit 1
}

Start-Sleep -Seconds 3
$state = (Get-Service -Name $ServiceName).Status
if ($state -eq 'Running') {
    Ok "Служба работает (состояние: $state)."
} else {
    Warn "Служба в состоянии «$state» — это не норма. Посмотрите журнал:"
    Warn "%ProgramData%\PingForge\service.log"
}

# ── 4. Ярлык в меню «Пуск» ──────────────────────────────────────────────────────────────────────

if (-not $NoShortcut) {
    Say ''
    Say 'Шаг 3. Ярлык в меню «Пуск».'
    $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    $shortcutPath = Join-Path $startMenu 'Custom Tweak By Doxaiz.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $appPath
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description = 'Оптимизация соединения для игр'
    $shortcut.Save()
    Ok "Ярлык создан: $shortcutPath"
}

# ── 5. Итог ─────────────────────────────────────────────────────────────────────────────────────

Say ''
Say 'Готово. Что дальше:'
Say "  1) Запустите программу: $appPath"
Say '  2) Нажмите «Оптимизировать соединение» — до нажатия программа ничего не меняет.'
Say '  3) Вернуть прежние настройки можно кнопкой «Вернуть как было» или пунктом в трее.'
Say ''
Say "Журнал службы: %ProgramData%\PingForge\service.log"
Say "Файл настроек: %ProgramData%\PingForge\settings.json"
Say "Снять программу: .\uninstall-service.ps1"
Say ''
Say 'Важное: программа не трогает анти-читы, не влезает в игру, не отключает Защитник и не меняет'
Say 'настройки роутера. Подробности — в разделе «О программе».'
Say ''
