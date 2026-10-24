[CmdletBinding()]
param([Parameter(Mandatory)][string]$HostRoot,[switch]$Resume,[int]$MaximumRestarts=5)
. "$PSScriptRoot/Host.Common.ps1"
if ([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) { throw 'Se requiere sesión interactiva de Windows para validar WASAPI; no ejecutar como servicio Session 0.' }
$root=Resolve-HostRoot $HostRoot
if ($MaximumRestarts -lt 0 -or $MaximumRestarts -gt 20) { throw 'MaximumRestarts debe estar entre 0 y 20.' }
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
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=$executable; $start.WorkingDirectory=Get-HostPath $root 'state'
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $child=New-Object Diagnostics.Process; $child.StartInfo=$start
        if (-not $child.Start()) { throw 'No se pudo iniciar QDNH.' }
        @{ ProcessId=$child.Id; StartTimeUtcTicks=$child.StartTime.ToUniversalTime().Ticks } | ConvertTo-Json | Set-Content -LiteralPath (Get-HostPath $root 'child.json') -Encoding UTF8
        # Mantener stdin abierto evita que Console.ReadLine entre en un bucle de EOF.
        $log=Get-HostPath $root ('logs/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff') + '.log')
        $out=$child.StandardOutput.ReadLineAsync(); $err=$child.StandardError.ReadLineAsync()
        $stopRequested=$false; $stopAt=[DateTime]::MaxValue
        while (-not $child.HasExited) {
            if ((Test-Path -LiteralPath $stopPath) -and -not $stopRequested) {
                $child.StandardInput.WriteLine('Q'); $child.StandardInput.Flush()
                $stopRequested=$true; $stopAt=[DateTime]::UtcNow.AddSeconds(15)
            }
            if ($out.IsCompleted) { $line=$out.Result; if ($null -ne $line) { Add-Content -LiteralPath $log -Value $line; $out=$child.StandardOutput.ReadLineAsync() } }
            if ($err.IsCompleted) { $line=$err.Result; if ($null -ne $line) { Add-Content -LiteralPath $log -Value ('ERROR: '+$line); $err=$child.StandardError.ReadLineAsync() } }
            if ($stopRequested -and [DateTime]::UtcNow -gt $stopAt) { throw 'QDNH no confirmó salida limpia: se mantiene el proceso para revisar PTT y evitar una terminación forzada.' }
            Start-Sleep -Milliseconds 100
        }
        $exit=$child.ExitCode; $child.Dispose(); $child=$null
        if ($stopRequested -or $exit -eq 0) { break }
        $failures++
        if ($failures -gt $MaximumRestarts) { throw 'Límite de reinicios alcanzado; revisar registros y hardware.' }
        Start-Sleep -Seconds ([Math]::Min(30,2*$failures))
    }
}
finally {
    # Nunca matar el proceso de radio: si no respondió a Q queda para diagnóstico físico.
    if ($null -ne $child -and -not $child.HasExited) { try { $child.StandardInput.WriteLine('Q'); $child.StandardInput.Flush() } catch {} }
    $lock.Dispose()
}
