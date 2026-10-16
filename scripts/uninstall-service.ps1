#Requires -RunAsAdministrator
<#
    Снятие службы Custom Tweak By Doxaiz.

    Что делает:
      1) останавливает и удаляет службу PingForge;
      2) по желанию удаляет папку установки и данные программы.

    По умолчанию данные (%ProgramData%\PingForge) сохраняются: там журнал изменений и снапшоты,
    по которым можно вернуть прежние настройки. Удалять их без спроса — плохая идея.

    Примеры:
        .\uninstall-service.ps1
        .\uninstall-service.ps1 -RemoveData
        .\uninstall-service.ps1 -InstallDir "D:\Games Utils\CustomTweak" -RemoveData
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\Custom Tweak By Doxaiz",
    [string]$ServiceName = 'PingForge',
    [switch]$RemoveData,
    [switch]$KeepFiles
)

$ErrorActionPreference = 'Stop'

function Say([string]$text) { Write-Host $text }
function Ok([string]$text) { Write-Host "  [готово] $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "  [внимание] $text" -ForegroundColor Yellow }

$dataDir = Join-Path $env:ProgramData 'PingForge'

Say ''
Say 'Custom Tweak By Doxaiz — снятие'
Say ''

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Warn 'Нужны права администратора. Перезапустите PowerShell от имени администратора.'
    exit 1
}

# ── 1. Программа в трее ─────────────────────────────────────────────────────────────────────────

Say 'Шаг 1. Закрываю программу, если она запущена.'
Get-Process -Name 'CustomTweakByDoxaiz' -ErrorAction SilentlyContinue |
    ForEach-Object {
        $_.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 500
        if (-not $_.HasExited) { Stop-Process -Id $_.Id -Force }
    }
Ok 'Программа закрыта (если была запущена).'

# ── 2. Служба ───────────────────────────────────────────────────────────────────────────────────

Say ''
Say 'Шаг 2. Останавливаю и удаляю службу.'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Ok "Служба «$ServiceName» удалена."
} else {
    Warn "Служба «$ServiceName» не найдена — пропускаю."
}

# ── 3. Файлы ────────────────────────────────────────────────────────────────────────────────────

Say ''
if ($KeepFiles) {
    Warn 'Ключ -KeepFiles: файлы программы остаются на месте.'
} else {
    Say 'Шаг 3. Удаляю файлы программы.'
    if (Test-Path $InstallDir) {
        Remove-Item -Path $InstallDir -Recurse -Force
        Ok "Папка удалена: $InstallDir"
    } else {
        Warn "Папка не найдена: $InstallDir"
    }

    $shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Custom Tweak By Doxaiz.lnk'
    if (Test-Path $shortcut) {
        Remove-Item -Path $shortcut -Force
        Ok 'Ярлык из меню «Пуск» удалён.'
    }
}

# ── 4. Данные ───────────────────────────────────────────────────────────────────────────────────

Say ''
if ($RemoveData) {
    Say 'Шаг 4. Удаляю данные программы (журнал, снапшоты, настройки).'
    if (Test-Path $dataDir) {
        Remove-Item -Path $dataDir -Recurse -Force
        Ok "Данные удалены: $dataDir"
    } else {
        Warn "Данных нет: $dataDir"
    }
    Warn 'ВНИМАНИЕ: вместе с данными исчезли снапшоты. Вернуть прежние настройки средствами программы больше нельзя.'
} else {
    Say 'Шаг 4. Данные оставлены на месте.'
    Ok "Журнал, снапшоты и настройки: $dataDir"
    Say '     Если они больше не нужны — запустите с ключом -RemoveData.'
}

Say ''
Say 'Снятие завершено.'
Say ''
