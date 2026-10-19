<#
    Проверка приложения «как человек» на настоящей Windows.

    Что делает скрипт — и только это:
      1) запускает программу так же, как её запускает человек: двойным щелчком, без ключей;
      2) ждёт, пока откроется настоящее окно, и снимает его;
      3) проходит мастер первого запуска так же, как человек: «Начать» → три ответа → «Дальше» →
         совет → «Позже» (без применения — система сборочной машины не меняется);
      4) щёлкает по большой кнопке «Оптимизировать соединение» тем же способом, что и мышью
         (UI Automation), и снимает результат;
      5) открывает игру в разделе «Игры» — путь подставляется заранее безобидным notepad.exe,
         чтобы проверить сам механизм запуска, а не наличие игры на сборочной машине;
      6) проходит по всем шести разделам меню, снимая каждый;
      7) проверяет, что программа не упала, и показывает журнал запуска.

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

function Find-AllByName($root, [string]$name)
{
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Test-TextInWindow($root, [string]$needle)
{
    # Задача F: честные состояния новых блоков («Данных пока нет», кнопки проверок) обязаны
    # быть видны человеку — ищем их среди всех текстов и кнопок окна.
    foreach ($element in @($root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)))
    {
        try
        {
            if ($null -ne $element.Current.Name -and $element.Current.Name.Contains($needle)) { return $true }
        }
        catch { }
    }
    return $false
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

# Проверка «Открыть игру» без установки игры: заранее записываем путь так, как будто человек
# однажды указал его сам. Вместо игры — блокнот: проверяется механизм запуска, а не наличие Fortnite.
$settingsPath = Join-Path $env:ProgramData 'PingForge\settings.json'
$settingsDir = Split-Path $settingsPath -Parent
if (-not (Test-Path $settingsDir)) { New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null }
$notepad = Join-Path $env:WINDIR 'System32\notepad.exe'
@{
    PerfProfile = 'balanced'
    OfferRestorePoint = $true
    StartMinimized = $false
    CloseToTray = $true
    JournalRowsShown = 20
    SetupCompleted = $false
    SetupAnswersRu = ''
    GamePaths = @{ 'fortniteclient-win64-shipping.exe' = $notepad }
} | ConvertTo-Json -Depth 4 | Set-Content -Path $settingsPath -Encoding UTF8
Say "подготовлено: путь к «игре» = $notepad (проверка запуска без установки игры)"

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

# ── 2. Мастер первого запуска ───────────────────────────────────────────────────────────────────
Say ''
Say '── Шаг 2: мастер первого запуска (как в BoosterX)'
$mainWindow = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$startButton = Find-ByName $mainWindow 'Начать' 8000
if ($null -ne $startButton)
{
    Save-Window $handle 'первый-запуск'
    Invoke-Element $startButton 'Начать' | Out-Null
    Start-Sleep -Milliseconds 800

    # Три ответа — так же, как их выбирает человек.
    foreach ($answer in @('Только игры', 'Средний', 'Высокий пинг'))
    {
        $option = Find-ByName $mainWindow $answer 6000
        if ($null -eq $option) { throw "В мастере нет ответа «$answer» — вопросы не построились" }
        Invoke-Element $option $answer | Out-Null
        Start-Sleep -Milliseconds 250
    }

    Save-Window $handle 'мастер-вопросы'

    $nextButton = Find-ByName $mainWindow 'Дальше' 6000
    if ($null -eq $nextButton) { throw 'В мастере нет кнопки «Дальше»' }
    Invoke-Element $nextButton 'Дальше' | Out-Null
    Start-Sleep -Milliseconds 900

    $adviceText = Get-TextOfMainScreen $mainWindow
    Save-Window $handle 'мастер-совет'
    if ($adviceText -notmatch 'Низкий пинг' -and $adviceText -notmatch 'Что предлагаю')
    {
        throw 'Мастер не показал совет после трёх ответов — проверьте текст выше'
    }

    Say 'мастер ответил советом'
    $laterButton = Find-ByName $mainWindow 'Позже' 6000
    if ($null -eq $laterButton) { throw 'В мастере нет кнопки «Позже» — выйти без изменений нельзя' }
    Invoke-Element $laterButton 'Позже' | Out-Null
    Start-Sleep -Milliseconds 900
    Say 'мастер закрыт кнопкой «Позже»: ничего не применено'
}
else
{
    Say 'мастер первого запуска не появился — значит, уже пройден'
}

Save-Window $handle 'главный-экран'

# ── 3. Нажатие большой кнопки ───────────────────────────────────────────────────────────────────
Say ''
Say '── Шаг 3: нажимаю «Оптимизировать соединение»'
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

    # Ответ узнаём по делу, а не по времени: либо игра не найдена, либо оптимизация пошла,
    # либо сетевой слой не подключён (это уже отказ сборки, и его надо видеть отдельно).
    if ($answerText -match 'Запущенная игра не найдена' -or $answerText -match 'не подключён' -or
        $answerText -match 'Остановить и вернуть как было' -or $answerText -match 'не запустилась')
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

if ($answerText -notmatch 'Запущенная игра не найдена' -and $answerText -notmatch 'Остановить и вернуть как было')
{
    $shown = ($answerText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 20) -join ' / '

    if ($answerText -match 'не подключён')
    {
        throw 'Сетевой слой не подключён: кнопка ответила «движок не подключён». Это значит, что служба собрана без сетевого движка.'
    }

    throw "Программа не ответила на нажатие за 30 секунд. Что было на экране: $shown"
}

# Главная проверка этого шага: движок подключён и действительно искал игру. На сборочной машине
# игры нет — значит, честный ответ именно «запущенная игра не найдена», а не «движок не подключён».
if ($answerText -match 'Запущенная игра не найдена')
{
    Say 'сетевой движок подключён: служба искала игру в таблице соединений и честно сказала, что её нет'
}

Say 'программа ответила на нажатие:'
foreach ($line in ($answerText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 12))
{
    Say "    $($line.Trim())"
}

# ── 3б. Движок при существующем «игровом» потоке ────────────────────────────────────────────────
# На сборочной машине игры нет и быть не может. Но проверить, что движок видит живой поток,
# можно честно: поднимаем настоящее TCP-соединение на игровой порт 3074 (его использует Xbox/CoD)
# к публичному адресу. Процесс при этом не из списка известных игр — значит, правильный ответ
# программы: «соединение вижу, игрой его назвать не могу», с именем процесса и адресом.
# Никакого «оптимизировал Fortnite» здесь не будет и быть не должно.
Say ''
Say '── Шаг 3б: проверяю движок, когда в системе есть живое соединение на игровой порт'
Say '    (эмуляция честная: TCP на порт 3074, процесс не из списка игр, адрес публичный)'

$helper = $null
try
{
    # Только IPv4: служба по умолчанию читает таблицу TCPv4, и соединение по IPv6 она бы не увидела.
    $helperCommand = "`$ip = [Net.Dns]::GetHostAddresses('portquiz.net') | " +
        "Where-Object { `$_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1; " +
        "`$c = New-Object Net.Sockets.TcpClient; `$c.Connect(`$ip, 3074); Start-Sleep -Seconds 240"

    # Start-Process склеивает аргументы через пробел и не расставляет кавычки сам:
    # без них команда развалится на куски и вспомогательный процесс сразу выйдет.
    $helper = Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile',
        '-Command',
        ('"' + $helperCommand + '"')
    )

    # Ждём, пока соединение действительно появится в таблице Windows: без этого проверка
    # нажала бы кнопку раньше, чем поток возник, и результат был бы случайным.
    $flowDeadline = (Get-Date).AddSeconds(30)
    $flowReady = $false
    while ((Get-Date) -lt $flowDeadline)
    {
        Start-Sleep -Milliseconds 500
        $established = @(Get-NetTCPConnection -RemotePort 3074 -State Established -ErrorAction SilentlyContinue |
            Where-Object { $_.RemoteAddress -match '^\d+\.\d+\.\d+\.\d+$' })
        if ($established.Count -gt 0)
        {
            $flowReady = $true
            Say "соединение IPv4 на порт 3074 установлено (PID $($established[0].OwningProcess), $($established[0].RemoteAddress))"
            break
        }
    }

    if (-not $flowReady)
    {
        # Это не отказ программы, а отсутствие сети до порта 3074 на сборочной машине.
        # Честнее написать «не проверено», чем покрасить прогон в красный из-за чужой сети.
        Say 'ПРОПУЩЕНО: на этой машине не открывается соединение на порт 3074 — проверку потока провести нельзя'
    }

    if ($flowReady)
    {
        # Убираем карточку прошлого ответа: иначе ожидание «что скажет программа» сразу увидит
        # старый текст «игра не найдена» и засчитает его за новый ответ.
        $dismiss = Find-ByName $mainWindow 'Понятно' 3000
        if ($null -ne $dismiss)
        {
            Invoke-Element $dismiss 'Понятно' | Out-Null
            Start-Sleep -Milliseconds 400
        }

        $optimizeAgain = Find-ByName $mainWindow 'Оптимизировать соединение' 10000
        if ($null -eq $optimizeAgain) { throw 'Кнопки «Оптимизировать соединение» нет перед повторным нажатием' }
        Invoke-Element $optimizeAgain 'Оптимизировать соединение' | Out-Null

        $flowText = ''
        $deadline = (Get-Date).AddSeconds(40)
        while ((Get-Date) -lt $deadline)
        {
            Start-Sleep -Milliseconds 700
            if ($process.HasExited) { break }

            Close-Dialogs $process.Id $handle | Out-Null
            $flowText = Get-TextOfMainScreen $mainWindow

            if ($flowText -match 'не вход(ит|ят) в список известных игр' -or $flowText -match 'Запущенная игра не найдена')
            {
                break
            }
        }

        $handle = (Get-Process -Id $process.Id).MainWindowHandle
        Save-Window $handle 'похожее-соединение'

        if ($flowText -notmatch 'не вход(ит|ят) в список известных игр')
        {
            $shown = ($flowText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 20) -join ' / '
            throw "Движок не увидел живое соединение на игровом порту. Что было на экране: $shown"
        }

        if ($flowText -notmatch '3074')
        {
            throw 'В ответе нет адреса и порта найденного соединения — человек не поймёт, о чём речь'
        }

        Say 'движок увидел живое соединение на игровом порту и честно отказался называть его игрой'
        foreach ($line in ($flowText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 14))
        {
            Say "    $($line.Trim())"
        }
    }
}
finally
{
    if ($null -ne $helper -and -not $helper.HasExited)
    {
        Stop-Process -Id $helper.Id -Force -ErrorAction SilentlyContinue
    }
}

# ── 4. Открытие игры ───────────────────────────────────────────────────────────────────────────
Say ''
Say '── Шаг 4: открываю игру в разделе «Игры»'
$gamesNav = Find-ByName $mainWindow 'Игры' 8000
if ($null -eq $gamesNav) { throw 'В меню нет раздела «Игры»' }
Invoke-Element $gamesNav 'Игры' | Out-Null
Start-Sleep -Milliseconds 900

$openButtons = Find-AllByName $mainWindow 'Открыть'
if ($openButtons.Count -eq 0) { throw 'В разделе «Игры» нет ни одной кнопки «Открыть» — список игр не построился' }
Say "кнопок «Открыть» в списке: $($openButtons.Count)"

# Первая карточка — с заранее подставленным путём (блокнот вместо игры).
Invoke-Element $openButtons[0] 'Открыть' | Out-Null
Start-Sleep -Seconds 4

$notepadRunning = @(Get-Process -Name notepad -ErrorAction SilentlyContinue).Count -gt 0
if (-not $notepadRunning) { throw 'По кнопке «Открыть» игра не запустилась — блокнота в процессах нет' }
Say 'игра запущена: процесс найден в системе'
Save-Window $handle 'игра-запущена'
Get-Process -Name notepad -ErrorAction SilentlyContinue | Stop-Process -Force

# ── 5. Разделы ──────────────────────────────────────────────────────────────────────────────────
Say ''
Say '── Шаг 5: прохожу по разделам'
# Координаты пунктов меню внутри окна — запасной путь, если Windows не отдаёт их через
# UI Automation (в этом прогоне так и вышло: список радиокнопок приходил пустым).
$sections = @(
    @{ Name = 'Игры';        Shot = 'раздел-игры';         X = 120; Y = 159 },
    @{ Name = 'Твики';       Shot = 'раздел-твики';        X = 120; Y = 199 },
    @{ Name = 'Журнал';      Shot = 'раздел-журнал';       X = 120; Y = 239 },
    @{ Name = 'Настройки';   Shot = 'раздел-настройки';    X = 120; Y = 279 },
    @{ Name = 'О программе'; Shot = 'раздел-о-программе';  X = 120; Y = 319 },
    @{ Name = 'Оптимизация'; Shot = 'раздел-оптимизация';  X = 120; Y = 118 }
)

# Задача F: в трёх разделах обязаны быть новые блоки. Ожидания — подстроки, которые видит человек.
# Задача I: в «О программе» — кнопка обновлений и честный статус «ещё не проверяли» (доказательство,
# что до нажатия программа никуда не ходила: иначе статус был бы другим).
$sectionExpectations = @{
    'Твики'       = @(@('Задержка ввода', 'карточка задержки ввода'), @('Проверить задержку ввода', 'кнопка проверки задержки ввода'))
    'Настройки'   = @(@('Проверка настроек', 'блок проверки настроек'), @('Проверить настройки', 'кнопка проверки настроек'))
    'Оптимизация' = @(@('График сети', 'карточка графика сети'), @('Данных пока нет', 'честная надпись пустого графика'))
    'О программе' = @(@('Проверить обновления', 'кнопка проверки обновлений'), @('Обновления ещё не проверяли', 'честный статус до нажатия'), @('Текущая версия', 'строка текущей версии'))
}

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

    # Задача F: новые блоки обязаны быть видны — снимок без блока был бы «красивой картинкой».
    if ($sectionExpectations.ContainsKey($section.Name))
    {
        $sectionRoot = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $process.Id).MainWindowHandle)
        foreach ($expectation in $sectionExpectations[$section.Name])
        {
            if (-not (Test-TextInWindow $sectionRoot $expectation[0]))
            {
                throw "В разделе «$($section.Name)» не видно: $($expectation[1]) (искал текст «$($expectation[0])»)"
            }
            Say "раздел «$($section.Name)»: $($expectation[1]) — на месте"
        }
    }
}

# ── 6. Проверка обновлений (задача I) ──────────────────────────────────────────────────────────
Say ''
Say '── Шаг 6: нажимаю «Проверить обновления» в разделе «О программе»'
$handle = (Get-Process -Id $process.Id).MainWindowHandle
$mainWindow = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$aboutNav = Find-ByName $mainWindow 'О программе' 8000
if ($null -eq $aboutNav) { throw 'В меню нет раздела «О программе»' }
Invoke-Element $aboutNav 'О программе' | Out-Null
Start-Sleep -Milliseconds 900

$checkUpdates = Find-ByName $mainWindow 'Проверить обновления' 8000
if ($null -eq $checkUpdates) { throw 'Кнопки «Проверить обновления» нет в разделе «О программе»' }
Invoke-Element $checkUpdates 'Проверить обновления' | Out-Null

# Честных исходов три, и любой из них — настоящий результат, а не заготовка: «Доступна версия»,
# «Обновлений нет» и «Проверить не удалось». Третий на сборочной машине даже вероятнее: репозиторий
# приватный, и анонимный GitHub релизов не отдаёт — программа обязана сказать именно это, а не
# молчать и не выдумывать «обновлений нет». Падаем только если итога нет вообще.
$updateText = ''
$updateDeadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $updateDeadline)
{
    Start-Sleep -Milliseconds 700
    if ($process.HasExited) { break }

    Close-Dialogs $process.Id $handle | Out-Null
    $rootNow = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $process.Id).MainWindowHandle)
    $updateText = Get-TextOfMainScreen $rootNow

    if ($updateText -match 'Доступна версия|Обновлений нет|Проверить не удалось') { break }
}

Save-Window ((Get-Process -Id $process.Id).MainWindowHandle) 'обновления-проверены'

if ($updateText -notmatch 'Доступна версия|Обновлений нет|Проверить не удалось')
{
    $shown = ($updateText -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 20) -join ' / '
    throw "Проверка обновлений не дала честного итога за 60 секунд. Что было на экране: $shown"
}

$updateOutcome = if ($updateText -match 'Доступна версия') { 'найдена новая версия — программа показала номер, но скачивать ничего не стала' }
    elseif ($updateText -match 'Обновлений нет') { 'обновлений нет — GitHub назвал ту же версию' }
    else { 'проверить не удалось — GitHub не отдал релизы анонимно, и программа сказала об этом честно' }
Say "исход проверки обновлений: $updateOutcome"

# ── 7. Итог ─────────────────────────────────────────────────────────────────────────────────────
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
