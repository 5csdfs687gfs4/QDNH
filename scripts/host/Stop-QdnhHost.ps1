[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$HostRoot)
. "$PSScriptRoot/Host.Common.ps1"
$root=Resolve-HostRoot $HostRoot
if ($PSCmdlet.ShouldProcess($root,'Solicitar salida limpia Q y detener supervisor')) { Set-Content -LiteralPath (Get-HostPath $root 'stop.flag') -Value 'stop' }
