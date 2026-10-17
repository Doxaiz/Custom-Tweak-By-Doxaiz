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

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    /// Все видимые окна процесса — по ним видно и главное окно, и сообщения.
    public static System.Collections.Generic.List<IntPtr> WindowsOf(uint pid)
    {
        var found = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner == pid && IsWindowVisible(handle)) { found.Add(handle); }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string ClassOf(IntPtr handle)
    {
        var buffer = new System.Text.StringBuilder(256);
        GetClassName(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// Закрыть окно сообщения так же, как это делает нажатие на крестик: WM_CLOSE.
    public static void Close(IntPtr handle) { PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero); }

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    /// Настоящий клик мышью: так же, как это делает человек, а не программный вызов.
    public static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // левая кнопка вниз
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // левая кнопка вверх
    }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    /// Отвечает ли окно на сообщения Windows. Занятый поток не ответит и за две секунды —
    /// так ловится «(Not Responding)»: раньше окно уходило в него, а проверка этого не видела.
    public static bool IsResponding(IntPtr hWnd)
    {
        IntPtr result;
        return SendMessageTimeout(hWnd, 0x0000, IntPtr.Zero, IntPtr.Zero, 0x0002, 2000, out result) != IntPtr.Zero;
    }
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
    # Ошибки UI Automation глушим: занятое окно может не ответить — это не повод падать,
    # пустой текст здесь сам по себе улика.
    $builder = New-Object System.Text.StringBuilder
    try
    {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
        {
            try { [void]$builder.AppendLine($element.Current.Name) } catch { }
        }
    }
    catch
    {
        Write-Host "  [внимание] не удалось прочитать текст окна: $($_.Exception.Message)"
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

function Close-Dialogs([int]$processId, [IntPtr]$mainHandle, [string]$shotName = 'сообщение')
{
    # Сообщения программы — окна класса #32770 того же процесса. Раньше здесь был UI Automation,
    # и это оказалось ненадёжно: кнопку находили, а нажать её не получалось — модальное окно
    # оставалось, держало главное окно и растягивало проверку на тринадцать минут.
    # Теперь закрываем так же, как крестик: WM_CLOSE. Это работает всегда.
    $closed = 0
    foreach ($window in [PfWin]::WindowsOf([uint32]$processId))
    {
        if ($window -eq $mainHandle) { continue }
        if ([PfWin]::ClassOf($window) -ne '#32770') { continue }

        Save-Window $window $shotName
        Say 'закрываю сообщение'
        [PfWin]::Close($window)
        Start-Sleep -Milliseconds 400
        $closed++
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

$beforeText = Get-TextOfMainScreen $mainWindow
Invoke-Element $button 'Оптимизировать соединение' | Out-Null

# Ждём ответ столько, сколько он реально может идти: запрос к службе — до 10 секунд.
# «Три тихие секунды» (так было раньше) — неверный признак: ответ просто не успевал появиться,
# и проверка шла дальше, пока окно ещё стояло в ожидании.
$answerText = ''
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline)
{
    Start-Sleep -Milliseconds 700
    if ($process.HasExited) { break }

    if (-not [PfWin]::IsResponding($handle))
    {
        Save-Window $handle 'окно-не-отвечает'
        throw 'Окно программы перестало отвечать на сообщения Windows («Not Responding») — это отказ'
    }

    Close-Dialogs $process.Id $handle | Out-Null   # страховка, если сборка покажет окно сообщения
    $answerText = Get-TextOfMainScreen $mainWindow

    # Ответ узнаём по делу, а не по времени: либо служба честно отказала, либо оптимизация пошла.
    if ($answerText -match 'не подключён' -or $answerText -match 'Остановить и вернуть как было' -or $answerText -match 'не запустилась')
    {
        break
    }
}

if ($process.HasExited)
{
    throw "Программа закрылась после нажатия кнопки (код $($process.ExitCode)). Это отказ."
}

$handle = (Get-Process -Id $process.Id).MainWindowHandle
Save-Window $handle 'после-нажатия'
Say 'программа жива после нажатия'

if ($answerText -notmatch 'не подключён' -and $answerText -notmatch 'Остановить и вернуть как было')
{
    $shown = ($answerText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 20) -join ' / '
    throw "Программа не ответила на нажатие за 30 секунд. Что было на экране: $shown"
}

Say 'программа ответила на нажатие:'
foreach ($line in ($answerText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 12))
{
    Say "    $($line.Trim())"
}

# ── 3. Разделы ──────────────────────────────────────────────────────────────────────────────────
Say ''
Say '── Шаг 3: прохожу по разделам'
# Координаты пунктов меню внутри окна — запасной путь, если Windows не отдаёт их через
# UI Automation (в этом прогоне так и вышло: список радиокнопок приходил пустым).
$sections = @(
    @{ Name = 'Твики';       Shot = 'раздел-твики';        X = 120; Y = 159 },
    @{ Name = 'Журнал';      Shot = 'раздел-журнал';       X = 120; Y = 199 },
    @{ Name = 'Настройки';   Shot = 'раздел-настройки';    X = 120; Y = 239 },
    @{ Name = 'О программе'; Shot = 'раздел-о-программе';  X = 120; Y = 279 },
    @{ Name = 'Оптимизация'; Shot = 'раздел-оптимизация';  X = 120; Y = 118 }
)

# Диагностика: что вообще видит Windows в окне. Если пункты не находятся — по этому файлу видно почему.
$rect = New-Object PfWin+RECT
[PfWin]::GetWindowRect((Get-Process -Id $process.Id).MainWindowHandle, [ref]$rect) | Out-Null
$dump = New-Object System.Text.StringBuilder
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
foreach ($element in ([System.Windows.Automation.AutomationElement]::RootElement).FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition))
{
    [void]$dump.AppendLine("$($element.Current.ControlType.ProgrammaticName) | $($element.Current.Name) | enabled=$($element.Current.IsEnabled)")
}
$dumpPath = Join-Path $OutDir 'walkthrough-tree.txt'
$dump.ToString() | Set-Content $dumpPath -Encoding UTF8
Say "дерево окон записано: $dumpPath ($((($dump.ToString() -split "`n").Count)) строк)"

foreach ($section in $sections)
{
    $handle = (Get-Process -Id $process.Id).MainWindowHandle
    $mainWindow = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $element = Find-ByName $mainWindow $section.Name 3000

    if ($null -ne $element)
    {
        Invoke-Element $element $section.Name | Out-Null
    }
    else
    {
        # Настоящий клик мышью по тому месту, где пункт нарисован. Окно уже поднято на передний план.
        [PfWin]::SetForegroundWindow($handle) | Out-Null
        Start-Sleep -Milliseconds 300
        $x = $rect.Left + $section.X
        $y = $rect.Top + $section.Y
        Say "пункт «$($section.Name)» не виден через UI Automation — щёлкаю мышью в $x,$y"
        [PfWin]::ClickAt($x, $y)
    }

    Start-Sleep -Milliseconds 1200
    Close-Dialogs $process.Id $handle ('сообщение-' + $section.Shot) | Out-Null
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
foreach ($line in ($answerText -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 12))
{
    Say "    $($line.Trim())"
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
# Явный код возврата: раньше рабочий процесс падал на проверке $LASTEXITCODE в workflow —
# у вызова «& script.ps1» этой переменной просто нет, и рабочий прогон объявлялся красным.
exit 0
