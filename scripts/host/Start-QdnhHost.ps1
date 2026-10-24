[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HostRoot,[switch]$Resume,
    [ValidateRange(0,20)][int]$MaximumRestarts=5,
    [ValidateRange(1,600)][int]$HeartbeatTimeoutSeconds=15,
    [ValidateRange(1,600)][int]$StartupGraceSeconds=90,
    [ValidateRange(0,3600)][int]$DegradedTimeoutSeconds=120,
    [ValidateRange(1,120)][int]$GracefulStopSeconds=15,
    [ValidateRange(1,60)][int]$BackoffBaseSeconds=2,
    [switch]$AllowSession0SerialOnly,[switch]$AllowSession0AudioExperimental
)
. "$PSScriptRoot/Host.Common.ps1"
$root=Resolve-HostRoot $HostRoot
if ([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
    if ($AllowSession0SerialOnly) { Assert-SerialOnlyConfiguration $root }
    elseif (-not $AllowSession0AudioExperimental) { throw 'Session 0 necesita autorización explícita de prueba. Para WASAPI validado usar inicio de sesión.' }
}
Assert-HostStopped $root
$stopPath=Get-HostPath $root 'stop.flag'
if ($Resume -and (Test-Path -LiteralPath $stopPath)) { Remove-Item -LiteralPath $stopPath }
$lock=[IO.File]::Open((Get-HostPath $root 'supervisor.lock'),'OpenOrCreate','ReadWrite','None')
$child=$null
try {
    New-Item -ItemType Directory -Force -Path (Get-HostPath $root 'logs') | Out-Null
    $failures=0
    while (-not (Test-Path -LiteralPath $stopPath)) {
        $current=Get-CurrentRelease $root
        $executable=Get-HostPath $root ('releases/' + $current.ReleaseName + '/QDNH.exe')
        $heartbeat=Get-HostPath $root 'state/host-heartbeat.json'
        $childStop=Get-HostPath $root ('state/stop-child-'+[Guid]::NewGuid().ToString('N')+'.flag')
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=$executable; $start.Arguments='--headless'; $start.WorkingDirectory=Get-HostPath $root 'state'
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $start.EnvironmentVariables['QDNH_HEARTBEAT_FILE']=$heartbeat
        $start.EnvironmentVariables['QDNH_HOST_STOP_FILE']=$childStop
        $child=New-Object Diagnostics.Process; $child.StartInfo=$start
        if (-not $child.Start()) { throw 'No se pudo iniciar QDNH.' }
        $startedTicks=$child.StartTime.ToUniversalTime().Ticks
        @{ ProcessId=$child.Id; StartTimeUtcTicks=$startedTicks } | ConvertTo-Json | Set-Content -LiteralPath (Get-HostPath $root 'child.json') -Encoding UTF8
        $log=Get-HostPath $root ('logs/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff') + '.log')
        Add-Content -LiteralPath $log -Value ('INICIO PID='+$child.Id+' version='+$current.ReleaseName)
        $out=$child.StandardOutput.ReadLineAsync(); $err=$child.StandardError.ReadLineAsync()
        $clock=[Diagnostics.Stopwatch]::StartNew()
        $sequence=-1L; $lastProgress=0.0; $lastState='Initializing'; $stateSince=0.0; $serving=$false
        $stopRequested=$false; $automaticRestart=$false; $stopAt=[double]::PositiveInfinity
        while (-not $child.HasExited) {
            $now=$clock.Elapsed.TotalSeconds
            if ($out.IsCompleted) { $line=$out.Result; if ($null -ne $line) { Add-Content -LiteralPath $log -Value $line; $out=$child.StandardOutput.ReadLineAsync() } }
            if ($err.IsCompleted) { $line=$err.Result; if ($null -ne $line) { Add-Content -LiteralPath $log -Value ('ERROR: '+$line); $err=$child.StandardError.ReadLineAsync() } }
            if ((Test-Path -LiteralPath $stopPath) -and -not $stopRequested) {
                Set-Content -LiteralPath $childStop -Value 'stop'
                try { $child.StandardInput.WriteLine('Q'); $child.StandardInput.Flush() } catch {}
                $stopRequested=$true; $stopAt=$now+$GracefulStopSeconds
            }
            if (-not $stopRequested) {
                try {
                    $probe=Get-Content -Raw -LiteralPath $heartbeat -ErrorAction Stop | ConvertFrom-Json
                    if ($probe.Protocol -eq 1 -and $probe.ProcessId -eq $child.Id -and [long]$probe.ProcessStartTimeUtcTicks -eq $startedTicks -and [long]$probe.Sequence -gt $sequence -and $probe.State -in @('Initializing','Running','Degraded')) {
                        $sequence=[long]$probe.Sequence; $lastProgress=$now
                        if ($lastState -ne $probe.State) { $lastState=$probe.State; $stateSince=$now; Add-Content -LiteralPath $log -Value ('SALUD '+$lastState) }
                        if ($lastState -ne 'Initializing') { $serving=$true }
                    }
                } catch { } # Lectura parcial/archivo todavía ausente: cuenta como falta de progreso, no como éxito.
                $reason=$null
                if ((-not $serving -and $now -gt $StartupGraceSeconds) -or ($lastState -eq 'Initializing' -and $now-$stateSince -gt $StartupGraceSeconds)) { $reason='arranque sin completar' }
                elseif (($serving -or $now -gt $StartupGraceSeconds) -and $now-$lastProgress -gt $HeartbeatTimeoutSeconds) { $reason='heartbeat sin avanzar' }
                elseif ($DegradedTimeoutSeconds -gt 0 -and $lastState -eq 'Degraded' -and $now-$stateSince -gt $DegradedTimeoutSeconds) { $reason='dispositivo esperado no disponible durante el plazo configurado' }
                if ($null -ne $reason) {
                    Add-Content -LiteralPath $log -Value ('REINICIO SOLICITADO: '+$reason)
                    Set-Content -LiteralPath $childStop -Value 'stop'
                    try { $child.StandardInput.WriteLine('Q'); $child.StandardInput.Flush() } catch {}
                    $automaticRestart=$true; $stopRequested=$true; $stopAt=$now+$GracefulStopSeconds
                }
            }
            if ($stopRequested -and $now -gt $stopAt) {
                if ($automaticRestart) {
                    # Solo nuestro hijo y su identidad exacta; nunca buscar procesos por nombre.
                    if ($child.StartTime.ToUniversalTime().Ticks -ne $startedTicks) { throw 'Cambió la identidad del proceso; no se termina.' }
                    Add-Content -LiteralPath $log -Value 'Proceso bloqueado: terminación de la instancia supervisada; la siguiente fuerza ReleaseKey al abrir UART.'
                    $child.Kill()
                    if (-not $child.WaitForExit(5000)) { throw 'No se pudo detener la instancia bloqueada; no se abre otra.' }
                } else {
                    Add-Content -LiteralPath $log -Value 'Parada manual sin confirmar: se mantiene espera para revisar PTT/hardware.'
                    $stopAt=[double]::PositiveInfinity
                }
            }
            Start-Sleep -Milliseconds 100
        }
        $exit=$child.ExitCode
        Add-Content -LiteralPath $log -Value ('SALIDA '+$exit)
        $child.Dispose(); $child=$null
        if ((Test-Path -LiteralPath $stopPath) -or (-not $automaticRestart -and $exit -eq 0)) { break }
        if ($clock.Elapsed.TotalSeconds -ge 600) { $failures=0 }
        $failures++
        if ($failures -gt $MaximumRestarts) { throw 'Límite de reinicios alcanzado; revisar registros y hardware.' }
        Start-Sleep -Seconds ([Math]::Min(30,$BackoffBaseSeconds*$failures))
    }
}
finally {
    if ($null -ne $child -and -not $child.HasExited) {
        try { Set-Content -LiteralPath $childStop -Value 'stop'; $child.StandardInput.WriteLine('Q'); $child.StandardInput.Flush() } catch {}
    }
    $lock.Dispose()
}
