param(
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$SmokePath
)

# Задача U на живой Windows. Здесь проверяется то, что нельзя проверить на Linux:
#   1) копия базы снимается ДО применения твиков (появляется pingforge.db.bak-…);
#   2) испорченный заголовок базы не мешает службе запуститься — никакого цикла рестартов;
#   3) испорченный файл не затирается, а откладывается рядом (pingforge.db.corrupt-…);
#   4) база заменяется резервной копией, и служба снова отвечает по каналу.
# Шаг герметичный: в начале и в конце убирём за собой (служба, файлы, данные).

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:ProgramFiles 'Custom Tweak By Doxaiz'
$dataDir = Join-Path $env:ProgramData 'PingForge'
$dbPath = Join-Path $dataDir 'pingforge.db'
$serviceLog = Join-Path $dataDir 'service.log'
$serviceName = 'PingForge'
$failures = @()

function Ann([string]$level, [string]$text) {
    $safe = $text -replace '%', '%25' -replace "`r", '' -replace "`n", '%0A'
    Write-Host "::$level file=tools/u-live-checks.ps1,line=1::$safe"
}

function Fail([string]$text) {
    $script:failures += $text
    Write-Host "FAIL: $text"
    Ann 'error' "FAIL: $text"
}

function Stop-EngineService {
    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $svc) { return }
    if ($svc.Status -eq 'Stopped') { return }

    sc.exe stop $serviceName | Out-Null
    try {
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    } catch {
        Write-Host "  служба не остановилась за 60 с (состояние: $($svc.Status)) — снимаю процесс"
        Stop-Process -Name 'CustomTweakByDoxaiz.Service' -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }
}

function Remove-All {
    Stop-EngineService
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
    if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue }
}

try {
    New-Item -ItemType Directory -Force test-results | Out-Null
    Start-Transcript -Path test-results\u-live.log -Force | Out-Null

    if (-not (Test-Path $SetupPath)) { throw "нет установщика: $SetupPath" }
    if (-not (Test-Path $SmokePath)) { throw "нет утилиты проверки: $SmokePath" }

    Remove-All
    Ann 'notice' 'U start: повреждённая база не должна ронять службу'

    $install = Start-Process $SetupPath -ArgumentList '/S' -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "установщик вернул код $($install.ExitCode)" }
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(90))

    for ($i = 0; $i -lt 60 -and -not (Test-Path $dbPath); $i++) { Start-Sleep -Seconds 1 }
    if (-not (Test-Path $dbPath)) { throw "база не появилась после установки: $dbPath" }
    Ann 'notice' "U база создана службой: $((Get-Item $dbPath).Length) байт"

    # 1) Копия перед применением: безобидный твик применяется и сразу откатывается.
    $applyOut = & $SmokePath '--apply-and-rollback' 2>&1 | Out-String
    $applyCode = $LASTEXITCODE
    Write-Host $applyOut
    Ann 'notice' "U apply-and-rollback exit=$applyCode"
    $backups = @(Get-ChildItem $dataDir -Filter 'pingforge.db.bak-*' -ErrorAction SilentlyContinue)
    if ($backups.Count -eq 0) {
        Fail 'резервная копия перед применением не появилась'
    } else {
        Ann 'notice' "U копия перед применением есть: $($backups[0].Name)"
    }

    # 2) Портим заголовок базы — так выглядит сбой питания или вмешательство антивируса.
    Stop-EngineService
    $garbage = New-Object byte[] 4096
    for ($i = 0; $i -lt $garbage.Length; $i++) { $garbage[$i] = 0x78 }
    [System.IO.File]::WriteAllBytes($dbPath, $garbage)
    Ann 'notice' 'U заголовок базы испорчен: 4096 байт мусора'

    # 3) Служба обязана подняться сама: повреждённый файл — не повод падать в цикле рестартов.
    sc.exe start $serviceName | Out-Null
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(90))
    Start-Sleep -Seconds 6
    $svc = Get-Service -Name $serviceName
    if ($svc.Status -ne 'Running') { Fail "служба не работает после порчи базы: $($svc.Status)" }

    $log = if (Test-Path $serviceLog) { Get-Content $serviceLog -Raw } else { '' }
    if ($log -notmatch 'База: файл был повреждён') { Fail 'в журнале службы нет строки о повреждённой базе' }
    if ($log -notmatch 'заменён резервной копией') { Fail 'служба не сказала, что взяла резервную копию' }
    if ($log -match 'Начал с чистой базы') { Fail 'копия была, но служба начала с чистой базы' }

    $corrupt = @(Get-ChildItem $dataDir -Filter 'pingforge.db.corrupt-*' -ErrorAction SilentlyContinue)
    if ($corrupt.Count -eq 0) {
        Fail 'испорченный файл не сохранён рядом'
    } else {
        Ann 'notice' "U испорченный файл сохранён: $($corrupt[0].Name)"
    }
    if (-not (Test-Path $dbPath)) { Fail 'база не восстановлена на место' }

    # 4) Служба снова отвечает по основному каналу.
    $pingOut = & $SmokePath 2>&1 | Out-String
    $pingCode = $LASTEXITCODE
    Write-Host $pingOut
    if ($pingCode -ne 0) { Fail "служба не отвечает после восстановления: exit=$pingCode" }

    Remove-All
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { Fail 'служба осталась после уборки' }
} catch {
    Fail $_.Exception.Message
} finally {
    try { Stop-Transcript | Out-Null } catch { }
}

if ($failures.Count -gt 0) {
    Ann 'error' "U итог: провалов $($failures.Count)"
    exit 1
}

Ann 'notice' 'U OK: копия до применения, повреждённая база заменена копией, файл сохранён, служба отвечает'
exit 0
