[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$HostRoot,[string]$TaskName='QDNH-Pruebas')
. "$PSScriptRoot/Host.Common.ps1"
$root=Resolve-HostRoot $HostRoot
$script=Get-HostPath $root 'tools/Start-QdnhHost.ps1'
if (-not (Test-Path -LiteralPath $script)) { throw 'Instale primero el paquete en HostRoot.' }
$user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { throw 'La tarea ya existe; no se sobrescribe.' }
if (-not $PSCmdlet.ShouldProcess($TaskName,'Registrar inicio al iniciar sesión del usuario actual (sin iniciar ahora)')) { return }
$arguments='-NoProfile -ExecutionPolicy RemoteSigned -WindowStyle Hidden -File "'+$script+'" -HostRoot "'+$root+'" -Resume'
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
$principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null
