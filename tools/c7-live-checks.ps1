param(
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$SmokePath
)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:ProgramFiles 'Custom Tweak By Doxaiz'
$serviceLog = Join-Path $env:ProgramData 'PingForge\service.log'
$failures = @()
$pfUser = 'pfcheck'
$pfCreated = $false
$pfPassword = $null

function Note-Failure([string]$text) {
    $script:failures += $text
    Write-Host "FAIL: $text"
    Ann 'error' "FAIL: $text"
}

function Ann([string]$level, [string]$text) {
    $safe = $text -replace '%', '%25' -replace "`r", '' -replace "`n", '%0A'
    Write-Host "::$level file=tools/c7-live-checks.ps1,line=1::$safe"
}

function Run-Smoke([string[]]$arguments, [string]$title) {
    Write-Host "-- $title"
    $tmp = [System.IO.Path]::GetTempFileName()
    & $SmokePath @arguments > $tmp 2>&1
    $code = $LASTEXITCODE
    $out = Get-Content $tmp -Raw
    Remove-Item $tmp -ErrorAction SilentlyContinue
    Write-Host $out
    Ann $(if ($code -eq 0) { 'notice' } else { 'error' }) "$title exit=$code : $out"
    Write-Host "   exit: $code"
    return $code
}

function Run-AsUser([string]$exe, [string[]]$arguments, [string]$user, [string]$password) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.Arguments = ($arguments -join ' ')
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = Split-Path $exe
    $info.UserName = $user
    $info.Domain = $env:COMPUTERNAME
    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    $info.Password = $secure
    $process = [System.Diagnostics.Process]::Start($info)
    $out = $process.StandardOutput.ReadToEnd()
    $err = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    Write-Host $out
    if ($err) { Write-Host $err }
    return @{ Code = $process.ExitCode; Output = $out + $err }
}

# Значения, которые проверяются «своими глазами»: ветка пользователя (её видит человек) и одна машинная настройка.
$menuDesktopKey = 'HKCU:\Control Panel\Desktop'
$mouseControlKey = 'HKCU:\Control Panel\Mouse'
$multimediaKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'

function Get-TweakValues {
    return @{
        MenuShowDelay = (Get-ItemProperty -Path $menuDesktopKey -Name MenuShowDelay -ErrorAction SilentlyContinue).MenuShowDelay
        MouseSpeed    = (Get-ItemProperty -Path $mouseControlKey -Name MouseSpeed -ErrorAction SilentlyContinue).MouseSpeed
        Throttling    = (Get-ItemProperty -Path $multimediaKey -Name NetworkThrottlingIndex -ErrorAction SilentlyContinue).NetworkThrottlingIndex
    }
}

# Чтение глобального параметра TCP своим процессом: так его увидел бы человек в netsh.
function Get-TcpAutoTuning {
    $out = & netsh interface tcp show global 2>&1 | Out-String
    $m = [regex]::Match($out, '(?im)^\s*(?:Auto-?[Tt]uning [Ll]evel|Уровень автонастройки)[^:]*:\s*(\S+)')
    if ($m.Success) { return $m.Groups[1].Value.Trim().ToLowerInvariant() }
    return ''
}

function Format-TweakValues($values) {
    if ($null -eq $values) { return 'нет данных' }
    return "MenuShowDelay=$($values.MenuShowDelay); MouseSpeed=$($values.MouseSpeed); NetworkThrottlingIndex=$($values.Throttling)"
}

try {
    Write-Host '-- Silent install'
    $install = Start-Process $SetupPath -ArgumentList '/S' -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Setup exit $($install.ExitCode)" }
    (Get-Service -Name PingForge).WaitForStatus('Running', [TimeSpan]::FromSeconds(90))
    Write-Host 'Service: Running'

    # 1) Administrator identity: service must name THIS process, not SYSTEM.
    if ((Run-Smoke @('--expect-identity') 'Admin --expect-identity') -ne 0) {
        Note-Failure 'admin --expect-identity'
    }

    # 2) Temporary non-admin user pfcheck (password is generated, never printed).
    $pfPassword = [Guid]::NewGuid().ToString('N') + 'Aa1!'
    try {
        $securePf = ConvertTo-SecureString $pfPassword -AsPlainText -Force
        New-LocalUser -Name $pfUser -Password $securePf -Description 'C7 live check (temporary)' | Out-Null
    } catch {
        Ann 'error' "New-LocalUser failed: $($_.Exception.Message); trying net user"
        $netOut = & net user $pfUser $pfPassword /add 2>&1 | Out-String
        Ann 'error' "net user exit=$LASTEXITCODE : $netOut"
        if ($LASTEXITCODE -ne 0) { throw 'user pfcheck was not created' }
    }
    if (-not (Get-LocalUser -Name $pfUser -ErrorAction SilentlyContinue)) { throw 'user pfcheck was not created' }
    $pfCreated = $true
    Ann 'notice' 'pfcheck user created'

    $publicSmokeDir = Join-Path $env:PUBLIC 'PingForgeSmoke'
    New-Item -ItemType Directory -Force $publicSmokeDir | Out-Null
    Copy-Item (Join-Path (Split-Path $SmokePath) '*') $publicSmokeDir -Force
    $publicSmoke = Join-Path $publicSmokeDir (Split-Path $SmokePath -Leaf)
    Ann 'notice' "smoke copied to $publicSmoke"

    $who = Run-AsUser (Join-Path $env:SystemRoot 'System32\whoami.exe') @('/user', '/groups') $pfUser $pfPassword
    Ann 'notice' "pfcheck whoami exit=$($who.Code) : $($who.Output)"

    $asUser = Run-AsUser $publicSmoke @('--expect-identity') $pfUser $pfPassword
    Write-Host "-- pfcheck --expect-identity exit: $($asUser.Code)"
    Ann $(if ($asUser.Code -eq 0) { 'notice' } else { 'error' }) "pfcheck --expect-identity exit=$($asUser.Code) : $($asUser.Output)"
    if ($asUser.Code -ne 0) { Note-Failure 'pfcheck --expect-identity' }
    if ($asUser.Output -match 'clientIsAdmin=True') { Note-Failure 'pfcheck reported clientIsAdmin=True' }

    $sidMatch = [regex]::Match($asUser.Output, 'S-1-5-21-[0-9-]+')
    if (-not $sidMatch.Success) {
        Note-Failure 'pfcheck SID not found in smoke output'
        $pfSid = $null
    } else {
        $pfSid = $sidMatch.Value
        Write-Host "pfcheck SID: $pfSid"
    }

    # 3) pfcheck tries to apply: denial must come BEFORE any tweak lookup/application.
    $tryApply = Run-AsUser $publicSmoke @('--try-apply') $pfUser $pfPassword
    Write-Host "-- pfcheck --try-apply exit: $($tryApply.Code)"
    Ann $(if ($tryApply.Code -eq 0) { 'notice' } else { 'error' }) "pfcheck --try-apply exit=$($tryApply.Code) : $($tryApply.Output)"
    if ($tryApply.Code -ne 0) { Note-Failure 'pfcheck --try-apply (denial before apply expected)' }

    # Same attempt against the MAIN pipe: ACL refusal is also a valid C2-layer proof.
    $tryMain = Run-AsUser $publicSmoke @('PingForge.Engine.v1', '--try-apply') $pfUser $pfPassword
    Write-Host "-- pfcheck main-pipe --try-apply exit: $($tryMain.Code) (0 = dispatcher denial, 1 = ACL/unavailable)"
    Ann 'notice' "pfcheck main-pipe --try-apply exit=$($tryMain.Code) : $($tryMain.Output)"

    # The denial must be written to service.log with pfcheck SID, not SYSTEM.
    Start-Sleep -Seconds 1
    if ($pfSid) {
        if (-not (Test-Path $serviceLog)) {
            Note-Failure "service log not found: $serviceLog"
        } elseif (-not (Select-String -Path $serviceLog -SimpleMatch $pfSid -Quiet)) {
            Note-Failure "pfcheck SID not found in service.log"
        } else {
            Write-Host 'service.log contains pfcheck SID'
            Ann 'notice' "service.log contains pfcheck SID $pfSid"
        }
    }

    # 4) Administrator really can apply harmless tweaks and roll them back — с проверкой значений.
    #    Смоук печатает по каждому твику «было / стало / что увидела система» и после отката
    #    применяет набор заново: прежние значения обязаны совпасть, иначе откат только на словах.
    if ((Run-Smoke @('--apply-and-rollback') 'Admin --apply-and-rollback (значения до/после/откат)') -ne 0) {
        Note-Failure 'admin --apply-and-rollback'
    }

    # 5) Настройка пользователя обязана оказаться в ветке ЕГО профиля, а не в профиле службы.
    #    Служба идёт от LocalSystem: её HKCU — это профиль системы. Поэтому проверяем не ответом
    #    службы, а собственным чтением из своей учётной записи — так же, как это увидел бы человек.
    $userBefore = Get-TweakValues
    Ann 'notice' "до применения: $(Format-TweakValues $userBefore)"

    if ((Run-Smoke @('--apply-hold') 'Admin --apply-hold (настройки пользователя и системы)') -ne 0) {
        Note-Failure 'admin --apply-hold'
    }

    $userApplied = Get-TweakValues
    Ann 'notice' "после применения: $(Format-TweakValues $userApplied)"
    if ("$($userApplied.MenuShowDelay)" -ne '0') {
        Note-Failure "MenuShowDelay в ветке пользователя = «$($userApplied.MenuShowDelay)», ожидалось 0 (запись ушла не в тот профиль?)"
    }
    if ("$($userApplied.MouseSpeed)" -ne '0') {
        Note-Failure "MouseSpeed в ветке пользователя = «$($userApplied.MouseSpeed)», ожидалось 0"
    }
    if (@('4294967295', '-1') -notcontains "$($userApplied.Throttling)") {
        Note-Failure "NetworkThrottlingIndex = «$($userApplied.Throttling)», ожидалось 4294967295"
    }

    if ((Run-Smoke @('--rollback-only') 'Admin --rollback-only (после настроек пользователя)') -ne 0) {
        Note-Failure 'admin --rollback-only'
    }

    $userAfter = Get-TweakValues
    Ann 'notice' "после отката: $(Format-TweakValues $userAfter)"
    foreach ($name in @('MenuShowDelay', 'MouseSpeed', 'Throttling')) {
        if ("$($userAfter[$name])" -ne "$($userBefore[$name])") {
            Note-Failure "после отката $name = «$($userAfter[$name])», а было «$($userBefore[$name])»"
        }
    }

    # 6) netsh-бэкенд: служба обязана уметь менять глобальный параметр TCP и возвращать его откатом.
    #    Значение перед опытом меняем сами: на чистой машине твик оказался бы «уже был таким»,
    #    и запись через netsh осталась бы недоказанной. Смотрим на результат своим netsh, не ответом службы.
    $tuningBefore = Get-TcpAutoTuning
    Ann 'notice' "autotuninglevel до опыта: «$tuningBefore»"
    if (-not $tuningBefore) {
        Note-Failure 'netsh не назвал autotuninglevel — проверку netsh-бэкенда провести нельзя'
    } else {
        $prepared = if ($tuningBefore -eq 'normal') { 'restricted' } else { 'normal' }
        & netsh interface tcp set global "autotuninglevel=$prepared" | Out-Host
        $afterPrepare = Get-TcpAutoTuning
        if ($afterPrepare -ne $prepared) {
            Note-Failure "подготовка autotuninglevel=«$prepared» не удалась (сейчас «$afterPrepare»)"
        } else {
            if ((Run-Smoke @('--apply-hold', '--tweaks=TCP_AUTOTUNING_NORMAL') 'Admin --apply-hold TCP_AUTOTUNING_NORMAL') -ne 0) {
                Note-Failure 'admin --apply-hold TCP_AUTOTUNING_NORMAL'
            }

            $tuningApplied = Get-TcpAutoTuning
            Ann 'notice' "применение TCP_AUTOTUNING_NORMAL: autotuninglevel=«$tuningApplied» (ожидалось normal)"
            if ($tuningApplied -ne 'normal') {
                Note-Failure "netsh не увидел применения: autotuninglevel=«$tuningApplied», ожидалось normal"
            }

            if ((Run-Smoke @('--rollback-only') 'Admin --rollback-only (после netsh-твика)') -ne 0) {
                Note-Failure 'admin --rollback-only (после netsh-твика)'
            }

            $tuningRolledBack = Get-TcpAutoTuning
            Ann 'notice' "после отката: autotuninglevel=«$tuningRolledBack» (ожидалось «$prepared»)"
            if ($tuningRolledBack -ne $prepared) {
                Note-Failure "откат вернул autotuninglevel=«$tuningRolledBack», а было «$prepared»"
            }
        }

        # Возвращаем то, что было до опыта, даже если проверка выше не прошла.
        & netsh interface tcp set global "autotuninglevel=$tuningBefore" | Out-Host
        $tuningRestored = Get-TcpAutoTuning
        Ann 'notice' "autotuninglevel после опыта: «$tuningRestored» (до опыта «$tuningBefore»)"
        if ($tuningRestored -ne $tuningBefore) {
            Note-Failure "не удалось вернуть исходный autotuninglevel=«$tuningBefore» (сейчас «$tuningRestored»)"
        }
    }
}
catch {
    Ann 'error' "catch at line $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.GetType().Name): $($_.Exception.Message)"
    Note-Failure $_.Exception.Message
}
finally {
    # Safety rollback and account cleanup always run.
    try {
        if (Test-Path $SmokePath) {
            & $SmokePath --rollback-only
            Write-Host "safety --rollback-only exit: $LASTEXITCODE (non-zero is expected when the snapshot is already rolled back)"
        }
    } catch { Write-Host "safety rollback note: $($_.Exception.Message)" }

    if ($pfCreated) {
        & net user $pfUser /delete | Out-Host
        Write-Host 'pfcheck deleted'
    }

    try {
        $uninstaller = Join-Path $installDir 'Uninstall.exe'
        if (Test-Path $uninstaller) {
            $remove = Start-Process $uninstaller -ArgumentList '/S' -Wait -PassThru
            Write-Host "uninstall exit: $($remove.ExitCode)"
        }
    } catch { Write-Host "uninstall note: $($_.Exception.Message)" }
}

if ($failures.Count -gt 0) {
    throw ("C7 live checks failed: " + ($failures -join '; '))
}

Write-Host 'C7 live checks: all passed.'
