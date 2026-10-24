[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$HostRoot,[string]$TaskName='QDNH-Pruebas',
      [ValidateSet('Logon','StartupSerialOnly','StartupAudioExperimental')][string]$StartupMode='Logon')
. "$PSScriptRoot/Host.Common.ps1"
$root=Resolve-HostRoot $HostRoot
$script=Get-HostPath $root 'tools/Start-QdnhHost.ps1'
if (-not (Test-Path -LiteralPath $script)) { throw 'Instale primero el paquete en HostRoot.' }
if ($StartupMode -eq 'StartupSerialOnly') { Assert-SerialOnlyConfiguration $root }
$user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { throw 'La tarea ya existe; no se sobrescribe.' }
if (-not $PSCmdlet.ShouldProcess($TaskName,"Registrar $StartupMode sin iniciar ahora")) { return }
$arguments='-NoProfile -ExecutionPolicy RemoteSigned -WindowStyle Hidden -File "'+$script+'" -HostRoot "'+$root+'" -Resume'
if ($StartupMode -eq 'Logon') {
    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
} else {
    $trigger=New-ScheduledTaskTrigger -AtStartup
    $trigger.Delay='PT30S'
    $principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    if ($StartupMode -eq 'StartupSerialOnly') { $arguments+=' -AllowSession0SerialOnly' }
    else { $arguments+=' -AllowSession0AudioExperimental' }
}
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
