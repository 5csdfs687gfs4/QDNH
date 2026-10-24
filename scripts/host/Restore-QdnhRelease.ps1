[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$HostRoot,[Parameter(Mandatory)][string]$ReleaseName)
. "$PSScriptRoot/Host.Common.ps1"
$root=Resolve-HostRoot $HostRoot
Assert-ReleaseName $ReleaseName
Assert-HostStopped $root
$release=Get-HostPath $root ('releases/' + $ReleaseName)
if (-not (Test-Path -LiteralPath (Join-Path $release 'QDNH.exe') -PathType Leaf)) { throw 'La versión anterior no existe.' }
if (-not $PSCmdlet.ShouldProcess($root,"Volver a $ReleaseName conservando configuración actual")) { return }
$backup=Get-HostPath $root ('backups/before-rollback-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff') + '.zip')
Compress-Archive -LiteralPath (Get-HostPath $root 'current.json'),(Get-HostPath $root 'state') -DestinationPath $backup
Write-CurrentRelease $root $ReleaseName
[pscustomobject]@{ ReleaseName=$ReleaseName; ConfigurationRestored=$false; Started=$false }
