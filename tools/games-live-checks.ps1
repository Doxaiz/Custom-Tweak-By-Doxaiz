param(
    [Parameter(Mandatory = $true)][string]$SmokePath,
    [int]$HoldSeconds = 150,
    [string]$UdpTarget = '1.1.1.1',
    [int]$UdpPort = 9005
)

# Игровые потоки на живой Windows (G1). Здесь проверяется то, чего не видно на Linux и в юнит-тестах
# с заранее нарисованными строками таблицы: настоящая таблица соединений Windows, настоящие имена
# процессов и настоящая трассировка UDP — глазами самой службы.
#
# Чего здесь НЕТ и почему: настоящие Fortnite и Minecraft на сборочную машину не ставятся (лицензия,
# аккаунт, GPU, десятки гигабайт). Поэтому проверяется распознавание ЖИВОГО ПОТОКА процесса с именем
# клиента игры: копия powershell.exe с именем javaw.exe держит TCP-соединение на порт 25565, копия
# с именем FortniteClient-Win64-Shipping.exe шлёт UDP на игровой порт. Служба об этом не знает —
# она видит то же, что видит у человека с запущенной игрой: процесс, адрес, порт, протокол.
#
# Проверки:
#   1) Minecraft (Java): поток на 25565 → служба называет именно эту игру и этот адрес;
#   2) имя javaw.exe с посторонним портом (2222) → Minecraft НЕ называется (никакого угадывания);
#   3) Fortnite: UDP-поток на 9005 → служба называет Fortnite, а не «неизвестный процесс».
#
# Если наружу не пускают (сеть раннера закрыта), проверка честно пишет «пропущено» с причиной,
# а не зелёный результат. Код возврата: 0 — проверено и совпало, 1 — расхождение.

$ErrorActionPreference = 'Stop'

$work = Join-Path $env:TEMP 'pf-games-live'
$serviceName = 'PingForge'
$failures = @()

function Ann([string]$level, [string]$text) {
    $safe = $text -replace '%', '%25' -replace "`r", '' -replace "`n", '%0A'
    Write-Host "::$level file=tools/games-live-checks.ps1,line=1::$safe"
}

function Stop-Emulator($process) {
    if ($null -eq $process) { return }
    try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
}

# Служба должна быть жива: проверяем её, а не собственную копию логики.
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Ann 'error' 'G службы нет: живая проверка игровых потоков требует установленной службы'
    exit 1
}

if ($service.Status -ne 'Running') {
    Start-Service $serviceName -ErrorAction SilentlyContinue
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
}

if (-not (Test-Path $SmokePath)) {
    Ann 'error' "G нет утилиты проверки: $SmokePath"
    exit 1
}

Write-Host "Утилита проверки: $SmokePath"
Write-Host "Служба: $serviceName ($((Get-Service -Name $serviceName).Status))"

# ── готовим эмуляторы клиентов ───────────────────────────────────────────────────────────────
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work | Out-Null

$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path $shell)) {
    $shell = (Get-Process -Id $PID).Path
}

$mcExe = Join-Path $work 'javaw.exe'
$fortniteExe = Join-Path $work 'FortniteClient-Win64-Shipping.exe'
Copy-Item $shell $mcExe -Force
Copy-Item $shell $fortniteExe -Force

$tcpScript = Join-Path $work 'hold-tcp.ps1'
@'
param([string]$Target, [int]$Port, [string]$Marker, [int]$HoldSeconds = 150)
$ErrorActionPreference = 'SilentlyContinue'
$hosts = @($Target, 'portquiz.net', 'mc.hypixel.net', 'play.cubecraft.net') | Select-Object -Unique
foreach ($name in $hosts) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $task = $client.ConnectAsync($name, $Port)
        if ($task.Wait(8000) -and $client.Connected) {
            Set-Content -Path $Marker -Value "connected $name $Port" -Encoding ascii
            $deadline = (Get-Date).AddSeconds($HoldSeconds)
            while ((Get-Date) -lt $deadline) {
                if (-not $client.Connected) { break }
                Start-Sleep -Seconds 1
            }
            exit 0
        }
    } catch { }
}
Set-Content -Path $Marker -Value "failed $Port" -Encoding ascii
exit 3
'@ | Set-Content $tcpScript -Encoding utf8

$udpScript = Join-Path $work 'send-udp.ps1'
@'
param([string]$Target, [int]$Port, [int]$Seconds = 150)
$ErrorActionPreference = 'SilentlyContinue'
$udp = New-Object System.Net.Sockets.UdpClient
$bytes = [byte[]](1..8)
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    try { $udp.Send($bytes, $bytes.Length, $Target, $Port) | Out-Null } catch { }
    Start-Sleep -Milliseconds 250
}
'@ | Set-Content $udpScript -Encoding utf8

function Wait-Marker([string]$path, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $path) {
            $text = (Get-Content $path -Raw).Trim()
            if ($text.StartsWith('connected') -or $text.StartsWith('failed')) { return $text }
        }
        Start-Sleep -Milliseconds 500
    }
    return ''
}

function Read-Field([string]$json, [string]$name) {
    if ([string]::IsNullOrWhiteSpace($json)) { return '' }
    $m = [regex]::Match($json, '"' + $name + '"\s*:\s*"([^"]*)"')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

function Invoke-RealtimeCheck([string]$title, [switch]$Quiet) {
    # -Quiet: повторный опрос, пока служба ещё не назвала игру. Вывод и аннотация — только
    # у последней, итоговой попытки, иначе журнал прогона превращается в десять одинаковых строк.
    if (-not $Quiet) { Write-Host "── $title" }
    $out = & $SmokePath '--realtime' 2>&1 | Out-String
    $code = $LASTEXITCODE
    if (-not $Quiet) { Write-Host $out }

    # В аннотацию кладём и ответ службы: «exit 2» без причины читается как загадка,
    # а причина важна — здесь проверяется НАЗЫВАНИЕ игры, а не замер пинга.
    if (-not $Quiet) {
        $startLine = (($out -split "`r?`n" | Where-Object { $_.StartsWith('Старт:') }) -join ' ').Trim()
        if ($startLine.Length -gt 300) { $startLine = $startLine.Substring(0, 300) }
        Ann 'notice' "G $title — exit $code; $startLine"
    }

    # Служба отдаёт два ответа: в снимке старта есть игра и адрес с портом, в отчёте остановки — адрес
    # без порта. Берём игру и адрес из старта, остановку используем как запасной источник и как признак,
    # что оптимизация действительно завершилась.
    $startJson = (($out -split "`r?`n" | Where-Object { $_.StartsWith('StartData:') }) -join "`n")
    $stopJson = (($out -split "`r?`n" | Where-Object { $_.StartsWith('StopData:') }) -join "`n")

    $game = Read-Field $startJson 'gameTitleRu'
    if ([string]::IsNullOrWhiteSpace($game)) { $game = Read-Field $stopJson 'gameTitleRu' }

    # Адрес с портом есть только в ответе старта: в отчёте остановки служба называет адрес без порта.
    $endpoint = Read-Field $startJson 'endpoint'
    if ([string]::IsNullOrWhiteSpace($endpoint)) { $endpoint = Read-Field $stopJson 'endpoint' }

    return @{ Code = $code; Output = $out; Game = $game; Endpoint = $endpoint }
}

# ── 1. Minecraft (Java): настоящий TCP-поток на 25565 ─────────────────────────────────────────
$mcMarker = Join-Path $work 'mc.marker'
$mc = Start-Process -FilePath $mcExe -PassThru -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $tcpScript,
    '-Target', 'portquiz.net', '-Port', '25565', '-Marker', $mcMarker, '-HoldSeconds', $HoldSeconds)

$mcState = Wait-Marker $mcMarker 25
$mcSkipped = $false
if (-not $mcState.StartsWith('connected')) {
    $mcSkipped = $true
    Ann 'warning' "G Minecraft: наружу на порт 25565 не пустили ($mcState) — эта часть пропущена, а не зачтена"
    Write-Host 'Пропуск: нет исходящего TCP на 25565. Так бывает в закрытых сетях раннера.'
} else {
    Write-Host "Поток javaw.exe: $mcState"
    Start-Sleep -Seconds 3

    $result = Invoke-RealtimeCheck 'Minecraft (Java), порт 25565'
    if ($result.Game -ne 'Minecraft (Java)') {
        $failures += "Minecraft не назван: игра «$($result.Game)», адрес «$($result.Endpoint)»"
    } elseif ($result.Endpoint -notmatch ':25565$') {
        $failures += "Minecraft назван, но адрес не тот: «$($result.Endpoint)» вместо порта 25565"
    } else {
        Ann 'notice' "G Minecraft OK: $($result.Game) $($result.Endpoint)"
    }
}

Stop-Emulator $mc
Remove-Item $mcMarker -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# ── 2. Отрицательная проверка: имя javaw.exe и посторонний порт ──────────────────────────────
$negMarker = Join-Path $work 'neg.marker'
$neg = Start-Process -FilePath $mcExe -PassThru -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $tcpScript,
    '-Target', 'portquiz.net', '-Port', '2222', '-Marker', $negMarker, '-HoldSeconds', $HoldSeconds)

$negState = Wait-Marker $negMarker 25
if (-not $negState.StartsWith('connected')) {
    Ann 'warning' "G javaw.exe на постороннем порту: наружу не пустили ($negState) — проверка «не угадывать» пропущена"
} else {
    Write-Host "Поток javaw.exe на постороннем порту: $negState"
    Start-Sleep -Seconds 3

    $result = Invoke-RealtimeCheck 'javaw.exe на постороннем порту 2222 (Minecraft называть нельзя)'
    if ($result.Game -eq 'Minecraft (Java)') {
        $failures += 'Minecraft назван по одному имени процесса, хотя порт чужой — это угадывание'
    } else {
        Ann 'notice' "G «не угадывает» OK: игра «$($result.Game)» (ожидалось: не Minecraft)"
    }
}

Stop-Emulator $neg
Remove-Item $negMarker -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# ── 3. Fortnite: настоящий UDP-поток на игровой порт ─────────────────────────────────────────
# UDP-ответ не нужен: служба узнаёт собеседника из событий сетевого стека, как у настоящей игры.
$fortnite = Start-Process -FilePath $fortniteExe -PassThru -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $udpScript,
    '-Target', $UdpTarget, '-Port', $UdpPort, '-Seconds', $HoldSeconds)

Write-Host "UDP-поток Fortnite: $UdpTarget`:$UdpPort"

# Служба узнаёт UDP-собеседника из событий сетевого стека, и на загруженной машине это занимает
# больше одной попытки: прогон 44235325818 увидел живой поток, но игра ещё не была названа —
# и проверка падала там, где всё работало. Спрашиваем до ~24 секунд; если игра так и не названа,
# ниже по-прежнему расхождение, а не «пропущено».
$fortniteResult = $null
$tried = 0
for ($attempt = 1; $attempt -le 8; $attempt++) {
    Start-Sleep -Seconds 3
    $fortniteResult = Invoke-RealtimeCheck "Fortnite, UDP на $UdpTarget`:$UdpPort" -Quiet
    $tried = $attempt
    if ($fortniteResult.Game -eq 'Fortnite' -and $fortniteResult.Endpoint -match (':' + $UdpPort + '$')) {
        Write-Host "Fortnite назван на попытке $attempt (ждут до 8)"
        break
    }
}

Write-Host "── Fortnite, UDP на $UdpTarget`:$UdpPort"
Ann 'notice' "G Fortnite — игра «$($fortniteResult.Game)», адрес «$($fortniteResult.Endpoint)», попыток: $tried"
if ($fortniteResult.Game -ne 'Fortnite') {
    $failures += "Fortnite не назван: игра «$($fortniteResult.Game)», адрес «$($fortniteResult.Endpoint)»"
} elseif ($fortniteResult.Endpoint -notmatch ":$UdpPort$") {
    $failures += "Fortnite назван, но адрес не тот: «$($fortniteResult.Endpoint)» вместо порта $UdpPort"
} else {
    Ann 'notice' "G Fortnite OK: $($fortniteResult.Game) $($fortniteResult.Endpoint)"
}

Stop-Emulator $fortnite
Start-Sleep -Seconds 1
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

# ── итог ─────────────────────────────────────────────────────────────────────────────────────
if ($mcSkipped) {
    Ann 'warning' 'G итог: проверено частично — сеть раннера не пустила TCP на 25565 (это не «зелено», а «пропущено»)'
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Ann 'error' "G расхождение: $failure"
    }
    exit 1
}

Ann 'notice' 'G OK: служба называет игру по живому потоку и не угадывает её по одному имени процесса'
exit 0
