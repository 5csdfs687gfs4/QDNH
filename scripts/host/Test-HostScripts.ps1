[CmdletBinding()]
param([Parameter(Mandatory)][string]$ScratchRoot,[string]$SupervisorFixture)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$testRoot=Join-Path ([IO.Path]::GetFullPath($ScratchRoot)) ('host-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$source=Join-Path $testRoot 'package'; New-Item -ItemType Directory -Path $source | Out-Null
# Un archivo de texto como doble de paquete: nunca se ejecuta QDNH ni se abre hardware.
Set-Content -LiteralPath (Join-Path $source 'QDNH.exe') -Value 'fixture-v1'
$config=Join-Path $testRoot 'initial.conf'; Set-Content -LiteralPath $config -Value "SharedRadioMode`nTrue"
$hostPath=Join-Path $testRoot 'host'
$dryHost=Join-Path $testRoot 'dry-host'
& "$PSScriptRoot/Install-QdnhRelease.ps1" -SourcePackage $source -HostRoot $dryHost -ReleaseName 'v1' -InitialConfig $config -WhatIf
if (Test-Path -LiteralPath $dryHost) { throw 'WhatIf modificó el destino.' }
& "$PSScriptRoot/Install-QdnhRelease.ps1" -SourcePackage $source -HostRoot $hostPath -ReleaseName 'v1' -InitialConfig $config | Out-Null
$state=Join-Path $hostPath 'state/default.conf'; $original=[IO.File]::ReadAllBytes($state)
& "$PSScriptRoot/Install-QdnhRelease.ps1" -SourcePackage $source -HostRoot $hostPath -ReleaseName 'v2' | Out-Null
$pointer=Get-Content -Raw -LiteralPath (Join-Path $hostPath 'current.json') | ConvertFrom-Json
if ($pointer.ReleaseName -ne 'v2') { throw 'No se seleccionó v2.' }
if (@(Get-ChildItem -LiteralPath (Join-Path $hostPath 'backups') -Filter '*.zip').Count -ne 1) { throw 'Falta copia de seguridad.' }
if ([Convert]::ToBase64String($original) -ne [Convert]::ToBase64String([IO.File]::ReadAllBytes($state))) { throw 'Se alteró la configuración.' }
& "$PSScriptRoot/Restore-QdnhRelease.ps1" -HostRoot $hostPath -ReleaseName 'v1' | Out-Null
$pointer=Get-Content -Raw -LiteralPath (Join-Path $hostPath 'current.json') | ConvertFrom-Json
if ($pointer.ReleaseName -ne 'v1') { throw 'Falló reversión.' }
$denied=$false
try { & "$PSScriptRoot/Restore-QdnhRelease.ps1" -HostRoot $hostPath -ReleaseName '../escape' } catch { $denied=$true }
if (-not $denied) { throw 'Se permitió escapar del destino.' }
$lock=[IO.File]::Open((Join-Path $hostPath 'supervisor.lock'),'OpenOrCreate','ReadWrite','None')
try {
    $denied=$false
    try { & "$PSScriptRoot/Install-QdnhRelease.ps1" -SourcePackage $source -HostRoot $hostPath -ReleaseName 'v3' } catch { $denied=$true }
    if (-not $denied) { throw 'Se modificó host con supervisor activo.' }
} finally { $lock.Dispose() }
$supervisorVerified=$false
if ($SupervisorFixture) {
    $supervisedRoot=Join-Path $testRoot 'supervised-host'
    & "$PSScriptRoot/Install-QdnhRelease.ps1" -SourcePackage $SupervisorFixture -HostRoot $supervisedRoot -ReleaseName 'fixture' -InitialConfig $config | Out-Null
    $supervisorScript=Join-Path $PSScriptRoot 'Start-QdnhHost.ps1'
    Set-Content -LiteralPath (Join-Path $supervisedRoot 'state/fixture-freeze-once.flag') -Value 'freeze'
    $arguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$supervisorScript+'" -HostRoot "'+$supervisedRoot+'" -MaximumRestarts 2 -StartupGraceSeconds 3 -HeartbeatTimeoutSeconds 1 -GracefulStopSeconds 1 -BackoffBaseSeconds 1 -DegradedTimeoutSeconds 0'
    $supervisor=Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        $startsPath=Join-Path $supervisedRoot 'state/fixture-starts.log'
        $starts=@()
        while ([DateTime]::UtcNow -lt $deadline) {
            if (Test-Path -LiteralPath $startsPath) { $starts=@(Get-Content -LiteralPath $startsPath) }
            if ($starts.Count -ge 2) { break }
            Start-Sleep -Milliseconds 100
        }
        if ($starts.Count -lt 2) { throw 'El guardian no reinicio el proceso vivo con heartbeat congelado.' }
        $firstPid=[int]($starts[0].Split(' ')[0])
        if (Get-Process -Id $firstPid -ErrorAction SilentlyContinue) { throw 'La instancia congelada sigue viva.' }
        $marker=Join-Path $supervisedRoot 'child.json'
        $identity=Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json
        if (-not (Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue)) { throw 'La segunda instancia no sigue viva.' }
        $guardLog=Get-ChildItem -LiteralPath (Join-Path $supervisedRoot 'logs') -Filter '*.log' | ForEach-Object { Get-Content -LiteralPath $_.FullName }
        if (-not ($guardLog -match 'heartbeat sin avanzar')) { throw 'Falta evidencia de deteccion del bloqueo real.' }
        & "$PSScriptRoot/Stop-QdnhHost.ps1" -HostRoot $supervisedRoot
        if (-not $supervisor.WaitForExit(10000)) { throw 'El doble no confirmó parada limpia.' }
        if ($supervisor.ExitCode -ne 0) { throw 'El supervisor terminó con error.' }
        if (Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue) { throw 'El doble sigue abierto tras la parada.' }
        $supervisorVerified=$true
    } finally {
        if (-not $supervisor.HasExited) { Set-Content -LiteralPath (Join-Path $supervisedRoot 'stop.flag') -Value 'stop'; [void]$supervisor.WaitForExit(16000) }
        $supervisor.Dispose()
    }
}
$parseCount=0
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $parseErrors=$null; $tokens=$null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$parseErrors)
    if ($parseErrors.Count) { throw "Errores de sintaxis en $($file.Name): $parseErrors" }
    $parseCount++
}
[pscustomobject]@{ Success=$true; Checks=6; ScriptsParsed=$parseCount; TestDirectory=$testRoot; HardwareAccessed=$false; ScheduledTasksChanged=$false; SupervisorFixtureVerified=$supervisorVerified; FrozenHeartbeatRestartVerified=$supervisorVerified } | ConvertTo-Json
