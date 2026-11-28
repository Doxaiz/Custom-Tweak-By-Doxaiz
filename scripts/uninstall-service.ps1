#Requires -RunAsAdministrator
<#
    Снятие службы Custom Tweak By Doxaiz — с заботой о следах (задача T).

    Что делает:
      0) спрашивает у службы, что программа меняла (журнал, незакрытые операции, снапшоты);
      1) если есть применённые изменения — предлагает вернуть настройки Windows как было ИЛИ
         явно оставить их; по умолчанию «вернуть». Откат не удался — удаление останавливается;
      2) останавливает и удаляет службу PingForge;
      3) по желанию удаляет папку установки и данные программы.

    По умолчанию данные (%ProgramData%\PingForge) сохраняются: там журнал изменений и снапшоты,
    по которым можно вернуть прежние настройки даже после этого скрипта.

    Правила, за которые отвечает C# (PingForge.Service.UninstallGuard), здесь только исполняются:
      - RemoveData без снятых изменений работает лишь с явным -Force;
      - вариант «оставить изменения» требует -KeepChangesAndRemove -ConfirmKeepChanges — двух
        отдельных флагов, чтобы он не выбрался случайно;
      - без флага и без терминала скрипт НИЧЕГО не удаляет: выходит с кодом 2 и объяснением.

    Примеры:
        .\uninstall-service.ps1
        .\uninstall-service.ps1 -RemoveData
        .\uninstall-service.ps1 -RollbackAndRemove -RemoveData
        .\uninstall-service.ps1 -KeepChangesAndRemove -ConfirmKeepChanges
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\Custom Tweak By Doxaiz",
    [string]$ServiceName = 'PingForge',
    [switch]$RemoveData,
    [switch]$KeepFiles,
    [switch]$RollbackAndRemove,
    [switch]$KeepChangesAndRemove,
    [switch]$ConfirmKeepChanges,
    [switch]$Force,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'
$MainPipe = 'PingForge.Engine.v1'
$ReadOnlyPipe = 'PingForge.Engine.ReadOnly.v1'
$FrameLimit = 1048576

function Say([string]$text) { Write-Host $text }
function Ok([string]$text) { Write-Host "  [готово] $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "  [внимание] $text" -ForegroundColor Yellow }
function Fail([string]$text, [int]$code) { Write-Host "  [стоп] $text" -ForegroundColor Red; exit $code }

# Один разговор со службой по общему каналу: кадр = 4 байта длины (little-endian) + UTF-8 JSON.
# Тот же протокол, что у окна и у тестов — отдельного «секретного» способа скрипт не имеет.
function Invoke-EngineCommand {
    param(
        [string]$PipeName,
        [string]$Command,
        [hashtable]$CmdArgs,
        [int]$ConnectTimeoutMs = 5000,
        [int]$ReadTimeoutSec = 300
    )

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect($ConnectTimeoutMs)
    } catch {
        $pipe.Dispose()
        return $null
    }

    try {
        $request = [ordered]@{ id = [Guid]::NewGuid().ToString('N'); command = $Command }
        if ($CmdArgs) { $request['args'] = $CmdArgs }
        $payload = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $request -Compress))
        $pipe.Write([BitConverter]::GetBytes([int32]$payload.Length), 0, 4)
        $pipe.Write($payload, 0, $payload.Length)
        $pipe.Flush()

        $header = New-Object byte[] 4
        if (-not (Read-Exact $pipe $header 4)) { return [pscustomobject]@{ ok = $false; messageRu = 'служба оборвала ответ' } }
        $length = [BitConverter]::ToInt32($header, 0)
        if ($length -le 0 -or $length -gt $FrameLimit) {
            return [pscustomobject]@{ ok = $false; messageRu = "некорректная длина кадра: $length" }
        }

        $body = New-Object byte[] $length
        if (-not (Read-Exact $pipe $body $length)) { return [pscustomobject]@{ ok = $false; messageRu = 'ответ оборвался' } }
        return [Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
    } catch {
        return [pscustomobject]@{ ok = $false; messageRu = $_.Exception.Message }
    } finally {
        $pipe.Dispose()
    }
}

function Read-Exact([System.IO.Stream]$Stream, [byte[]]$Buffer, [int]$Count) {
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($Buffer, $offset, $Count - $offset)
        if ($read -le 0) { return $false }
        $offset += $read
    }
    return $true
}

$dataDir = Join-Path $env:ProgramData 'PingForge'

Say ''
Say 'Custom Tweak By Doxaiz — снятие'
Say ''

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Fail 'Нужны права администратора. Перезапустите PowerShell от имени администратора.' 1
}

if ($RollbackAndRemove -and $KeepChangesAndRemove) {
    Fail 'Нельзя одновременно «вернуть как было» и «оставить изменения» — выберите что-то одно.' 1
}

# ── 0. Что программа оставляла в системе ──────────────────────────────────────────────────────

Say 'Шаг 0. Спрашиваю у службы, есть ли применённые изменения.'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$serviceRunning = $null -ne $service -and $service.Status -eq 'Running'

$applied = 0; $open = 0; $snapshots = 0; $running = $false
$tracesUnknown = $true
if ($serviceRunning) {
    $check = Invoke-EngineCommand -PipeName $ReadOnlyPipe -Command 'uninstall-check' -ReadTimeoutSec 20
    if ($check -and $check.ok) {
        $data = $null
        if ($check.dataJson) { $data = $check.dataJson | ConvertFrom-Json }
        if ($null -ne $data) {
            $applied = [int]$data.appliedChanges
            $open = [int]$data.openOperations
            $snapshots = [int]$data.snapshots
            $running = [bool]$data.optimizationRunning
            $tracesUnknown = $false
        }
        Say "  Служба отвечает: $($check.messageRu)"
    } else {
        $reason = if ($check) { $check.messageRu } else { 'служба не ответила (старая версия?)' }
        Warn "Точного ответа нет: $reason. Считаю это применёнными изменениями — молча удалять не буду."
    }
} else {
    Warn 'Служба не запущена — спросить не у кого. Если в системе что-то менялось, вернуть это скрипт не сможет.'
}

$mustAsk = $tracesUnknown -or $applied -gt 0 -or $open -gt 0 -or $running

# ── 1. Выбор человека (три варианта, первый по умолчанию) ────────────────────────────────────

$nothingToRevert = $false
$choice = $null
if ($RollbackAndRemove) {
    $choice = 'RollbackThenRemove'
} elseif ($KeepChangesAndRemove) {
    if (-not $ConfirmKeepChanges) {
        Fail ('Внимание: настройки Windows останутся изменёнными, а откатывать их будет нечем — программа ' +
            'удалится вместе со снапшотами. Это выбор, а не случайность: добавьте -ConfirmKeepChanges.') 2
    }
    $choice = 'KeepChangesAndRemove'
} elseif ($mustAsk) {
    $question = "Программа оставляла след: применённых изменений $applied, незакрытых операций $open, снапшотов $snapshots" +
        $(if ($running) { ', оптимизация идёт' } else { '' }) + '.'
    $questionLines = @(
        '  1) Вернуть настройки Windows как было и удалить программу (по умолчанию)',
        '  2) Удалить программу, оставив изменения — откатывать будет нечем',
        '  3) Отмена'
    )
    if ($NonInteractive -or -not [Environment]::UserInteractive) {
        Fail ($question + ' Запуск без терминала, а выбор нужен. Явно: -RollbackAndRemove (вернуть как было), ' +
            '-KeepChangesAndRemove -ConfirmKeepChanges (оставить) — или отмена. Ничего не удалено.') 2
    }
    Say $question
    $questionLines | ForEach-Object { Say $_ }
    $answer = (Read-Host 'Ваш выбор [1]').Trim()
    switch ($answer) {
        '1' { $choice = 'RollbackThenRemove' }
        '2' { $choice = 'KeepChangesAndRemove' }
        '3' { $choice = 'Cancel' }
        ''  { $choice = 'RollbackThenRemove' }
        default { $choice = 'Cancel' }
    }
} else {
    Say 'Применённых изменений нет — спрашивать не о чём.'
    $choice = 'RollbackThenRemove'   # откатывать нечего, путь тот же: ничего не делаем
    $nothingToRevert = $true
}

if ($choice -eq 'Cancel') {
    Say 'Удаление отменено. Файлы, служба и данные не тронуты.'
    exit 5
}

if ($choice -eq 'KeepChangesAndRemove') {
    Warn 'Выбран вариант «оставить изменения». Настройки Windows останутся такими, как их поставила программа.'
}

# ── 2. Откат: остановить → снять QoS и маршруты → вернуть снапшоты → проверить остатки ──────

# «Данные можно удалять без -Force» — только когда снимать нечего ИЛИ откат действительно прошёл.
# Выбор «оставить изменения» сохранных прав на -RemoveData не даёт: изменения остались, снапшоты — нет.
$rollbackDone = $nothingToRevert
if ($choice -eq 'RollbackThenRemove' -and ($mustAsk -or $applied -gt 0 -or $open -gt 0)) {
    if (-not $serviceRunning) {
        Warn 'Служба не запущена — откатить изменения нечем. Снимаю только службу и файлы, -RemoveData без -Force после этого откажу.'
    } else {
        Say 'Шаг 1. Возвращаю настройки Windows как было.'
        # Порядок шагов — как в UninstallGuard.RollbackSequenceWireNames: остановить, снять свои
        # маршруты и QoS, вернуть последний снапшот, проверить остатки. Снапшотов нет — шаг возврата
        # пропускаем честно (снимать нечего), а не «падает и ладно».
        $steps = @('realtime-stop', 'repair-network')
        if ($tracesUnknown -or $snapshots -gt 0) { $steps += 'rollback' }
        $steps += 'status'
        $failedStep = $null
        foreach ($name in $steps) {
            $result = Invoke-EngineCommand -PipeName $MainPipe -Command $name
            if (-not $result -or -not $result.ok) { $failedStep = $name }
            if ($failedStep) { break }
            Say "  [шаг] $($name): $($result.messageRu)"
        }

        if ($failedStep) {
            Fail ("Откат не выполнен до конца: шаг «$failedStep» не прошёл. Удаление остановлено — " +
                'файлы, служба и данные на месте. Почините шаг (смотрите журнал в окне программы) и повторите.') 3
        }
        Ok 'Настройки возвращены, остатков нет.'
        $rollbackDone = $true
        $mustAsk = $false
    }
}

# ── 3. Программа в трее ───────────────────────────────────────────────────────────────────────

Say ''
Say 'Закрываю программу, если она запущена.'
Get-Process -Name 'CustomTweakByDoxaiz' -ErrorAction SilentlyContinue |
    ForEach-Object {
        $_.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 500
        if (-not $_.HasExited) { Stop-Process -Id $_.Id -Force }
    }
Ok 'Программа закрыта (если была запущена).'

# ── 4. Служба ─────────────────────────────────────────────────────────────────────────────────

Say ''
Say 'Останавливаю и удаляю службу.'
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

# ── 5. Файлы ──────────────────────────────────────────────────────────────────────────────────

Say ''
if ($KeepFiles) {
    Warn 'Ключ -KeepFiles: файлы программы остаются на месте.'
} else {
    Say 'Удаляю файлы программы.'
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

# ── 6. Данные: журнал, снапшоты, настройки ────────────────────────────────────────────────────

Say ''
if ($RemoveData) {
    Say 'Удаляю данные программы (журнал, снапшоты, настройки).'
    $hasDb = Test-Path (Join-Path $dataDir 'pingforge.db')
    if ($hasDb -and -not $rollbackDone -and -not $Force) {
        Fail ('Удалять данные нельзя: изменения не сняты, а вместе с данными исчезнут снапшоты, которыми ' +
            'их можно вернуть. Сделайте откат (-RollbackAndRemove) или удалите с -Force, понимая, что ' +
            'откатывать будет нечем.') 4
    }
    if (Test-Path $dataDir) {
        Remove-Item -Path $dataDir -Recurse -Force
        Ok "Данные удалены: $dataDir"
        if ($tracesUnknown -or $applied -gt 0) {
            Warn 'ВМЕСТЕ С ДАННЫМИ УДАЛЕНЫ СНАПШОТЫ. Вернуть прежние настройки средствами программы больше нельзя.'
        }
    } else {
        Warn "Данных нет: $dataDir"
    }
} else {
    Say 'Данные оставлены на месте.'
    Ok "Журнал, снапшоты и настройки: $dataDir"
    Say '     Если они больше не нужны — запустите с ключом -RemoveData (со снапшотами он потребует снятых изменений или -Force).'
}

Say ''
Say 'Снятие завершено. Изменения возвращены туда, где были до программы; программа удалена полностью.'
Say ''
exit 0
