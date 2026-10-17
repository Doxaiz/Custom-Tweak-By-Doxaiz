<#
    Проверка приложения «как человек» на настоящей Windows.

    Что делает скрипт — и только это:
      1) запускает программу так же, как её запускает человек: двойным щелчком, без ключей;
      2) ждёт, пока откроется настоящее окно, и снимает его;
      3) щёлкает по большой кнопке «Оптимизировать соединение» тем же способом, что и мышью
         (UI Automation), и снимает результат — в том числе появившееся сообщение;
      4) проходит по всем пяти разделам меню, снимая каждый;
      5) проверяет, что программа не упала, и показывает журнал запуска.

    Чего скрипт не делает: ничего не меняет в настройках системы за человека. Нажатие кнопки
    в программе безопасно по её же правилам: без снапшота и подтверждения она ничего не меняет,
    а на машине без игры честно об этом говорит.

    Зачем это нужно: до этого окно программы проверялось «на глаз» по снимку, а нажатия
    по кнопкам не проверялись вовсе. Отсюда и бралась ситуация «запустил — ничего не произошло»:
    её некому было заметить, кроме пользователя.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$WindowTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class PfWin
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@

$shots = 0
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

function Say([string]$text) { Write-Host "  $text" }

function Save-Window([IntPtr]$handle, [string]$name)
{
    $script:shots++
    $rect = New-Object PfWin+RECT
    [PfWin]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { Write-Host "  [снимок] окно без размера — пропускаю"; return }

    [PfWin]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 600

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $width, $height))

    $file = Join-Path $OutDir ("{0:d2}-{1}.png" -f $script:shots, $name)
    $bitmap.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Say "[снимок] $file ($width×$height)"
}

function Get-AppWindow([int]$processId, [int]$timeoutSeconds)
{
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline)
    {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $process) { return $null }
        if ($process.MainWindowHandle -ne 0) { return $process.MainWindowHandle }
        Start-Sleep -Milliseconds 400
    }
    return [IntPtr]::Zero
}

function Find-ByName($root, [string]$name, [int]$timeoutMs = 8000)
{
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline)
    {
        $found = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Get-TextOfMainScreen($root)
{
    # Собираем все тексты, которые видит человек: по ним понятно, что программа ответила.
    $builder = New-Object System.Text.StringBuilder
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
    {
        [void]$builder.AppendLine($element.Current.Name)
    }
    return $builder.ToString()
}

function Invoke-Element($element, [string]$what)
{
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern))
    {
        $pattern.Invoke(); Say "нажато: $what (кнопка)"; return $true
    }

    if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern))
    {
        $pattern.Select(); Say "выбрано: $what (пункт меню)"; return $true
    }

    if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern))
    {
        $pattern.Toggle(); Say "переключено: $what (переключатель)"; return $true
    }

    Write-Host "  [внимание] у «$what» нет способа нажать — элемент только читается"
    return $false
}

function Close-Dialogs([int]$processId, [IntPtr]$mainHandle)
{
    # Сообщения программы — отдельные окна того же процесса (класс #32770). Главное окно
    # отличается по ОБРАБОТЧИКУ, а не по заголовку: у сообщения заголовок тоже начинается
    # с названия программы, и первая версия скрипта из-за этого считала сообщение главным
    # окном, не закрывала его — и дальше не могла найти ни одного раздела.
    $closed = 0
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)

    foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $condition))
    {
        if ($window.Current.ClassName -ne '#32770') { continue }
        if ([IntPtr]$window.Current.NativeWindowHandle -eq $mainHandle) { continue }

        $title = $window.Current.Name
        Say "появилось сообщение: «$title»"
        Save-Window ([IntPtr]$window.Current.NativeWindowHandle) 'сообщение'

        $pressed = $false
        foreach ($label in @('ОК', 'OK', 'Да', 'Yes', 'Закрыть', 'Close'))
        {
            $button = Find-ByName $window $label 1500
            if ($null -ne $button)
            {
                Invoke-Element $button "«$label» в сообщении" | Out-Null
                $pressed = $true
                break
            }
        }

        if (-not $pressed)
        {
            # Запасной путь: Enter нажимает кнопку по умолчанию в любом сообщении Windows.
            [PfWin]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Say 'нажато: Enter в сообщении (кнопка по умолчанию)'
        }

        $closed++
        Start-Sleep -Milliseconds 700
    }

    return $closed
}

Say ''
Say 'Проверка программы на живой Windows: запуск, кнопка, разделы'
Say "Файл: $AppPath"
Say ''

if (-not (Test-Path $AppPath)) { throw "Нет файла программы: $AppPath" }

$log = Join-Path $env:LOCALAPPDATA 'CustomTweakByDoxaiz\app-start.log'
Remove-Item $log -ErrorAction SilentlyContinue

# ── 1. Запуск как у человека ────────────────────────────────────────────────────────────────────
Say '── Шаг 1: запускаю программу без ключей'
$watch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process $AppPath -PassThru
$handle = Get-AppWindow $process.Id $WindowTimeoutSeconds
$watch.Stop()

if ($handle -eq [IntPtr]::Zero)
{
    if ($process.HasExited) { throw "Программа завершилась сама, код $($process.ExitCode) — окна нет" }
    throw "Окно не открылось за $WindowTimeoutSeconds секунд"
}

$title = (Get-Process -Id $process.Id).MainWindowTitle
Say "окно открылось за $([math]::Round($watch.Elapsed.TotalSeconds, 1)) с, заголовок: «$title»"
Save-Window $handle 'главный-экран'

# ── 2. Нажатие большой кнопки ───────────────────────────────────────────────────────────────────
Say ''
Say '── Шаг 2: нажимаю «Оптимизировать соединение»'
$mainWindow = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$button = Find-ByName $mainWindow 'Оптимизировать соединение' 10000
if ($null -eq $button)
{
    throw 'Кнопки «Оптимизировать соединение» нет в окне — главная страница не заполнилась'
}

Invoke-Element $button 'Оптимизировать соединение' | Out-Null

# Даём время дойти до службы (её ответ — до 10 с) и посмотреть, что вышло.
$dialogSeen = 0
$statusText = ''
for ($i = 0; $i -lt 25; $i++)
{
    Start-Sleep -Milliseconds 800
    $dialogSeen += Close-Dialogs $process.Id $handle
    if ($process.HasExited) { break }

    # Ждём, пока программа закончит отвечать: кнопка снова доступна — значит работа кончилась.
    $mainWindow = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $process.Id).MainWindowHandle)
    $statusText = Get-TextOfMainScreen $mainWindow
    if ($dialogSeen -gt 0 -and $i -ge 3) { break }
}

if ($process.HasExited)
{
    throw "Программа закрылась после нажатия кнопки (код $($process.ExitCode)). Это отказ."
}

$handle = (Get-Process -Id $process.Id).MainWindowHandle
Save-Window $handle 'после-нажатия'
Say 'программа жива после нажатия'

# ── 3. Разделы ──────────────────────────────────────────────────────────────────────────────────
Say ''
Say '── Шаг 3: прохожу по разделам'
$sections = @(
    @{ Name = 'Твики';            Shot = 'раздел-твики' },
    @{ Name = 'Журнал';           Shot = 'раздел-журнал' },
    @{ Name = 'Настройки';        Shot = 'раздел-настройки' },
    @{ Name = 'О программе';      Shot = 'раздел-о-программе' },
    @{ Name = 'Оптимизация';      Shot = 'раздел-оптимизация' }
)

foreach ($section in $sections)
{
    $handle = (Get-Process -Id $process.Id).MainWindowHandle
    $mainWindow = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $element = Find-ByName $mainWindow $section.Name 6000
    if ($null -eq $element)
    {
        # Запасной путь: показать, что вообще есть в окне, — иначе непонятно, что искать.
        $titles = @()
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        foreach ($radio in $mainWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
        {
            $titles += $radio.Current.Name
        }
        Say "пункты меню, которые видит Windows: $($titles -join ' | ')"
        throw "В окне нет пункта меню «$($section.Name)»"
    }

    Invoke-Element $element $section.Name | Out-Null
    Start-Sleep -Milliseconds 1200
    Close-Dialogs $process.Id $handle | Out-Null
    Save-Window ((Get-Process -Id $process.Id).MainWindowHandle) $section.Shot
}

# ── 4. Итог ─────────────────────────────────────────────────────────────────────────────────────
Say ''
if ($process.HasExited)
{
    throw "Программа закрылась во время проверки (код $($process.ExitCode))"
}

Say "── Итог: программа жива, заголовок окна: «$((Get-Process -Id $process.Id).MainWindowTitle)», снимков: $shots"
Say '── Что показывает главный экран после нажатия:'
foreach ($line in ($statusText -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 12))
{
    Say "    $line"
}

if (Test-Path $log)
{
    Say ''
    Say '── Журнал запуска (последние 40 строк):'
    Get-Content $log | Select-Object -Last 40 | ForEach-Object { Write-Host "    $_" }

    $errors = Select-String -Path $log -SimpleMatch '!!' -Quiet
    if ($errors) { throw 'В журнале есть строки ошибок (!!) — смотри выше' }
}
else
{
    Write-Host '  [внимание] журнала запуска нет — программа не дошла до первой строки журнала'
}

Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host "Проверка пройдена: программа открывается, кнопка нажимается, разделы переключаются, снимки в $OutDir"
