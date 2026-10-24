[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$SourcePackage,[Parameter(Mandatory)][string]$HostRoot,[Parameter(Mandatory)][string]$ReleaseName,[string]$InitialConfig)
. "$PSScriptRoot/Host.Common.ps1"
$root = Resolve-HostRoot $HostRoot
Assert-ReleaseName $ReleaseName
$source = (Resolve-Path -LiteralPath $SourcePackage).Path
if (-not (Test-Path -LiteralPath (Join-Path $source 'QDNH.exe') -PathType Leaf)) { throw 'Falta QDNH.exe en el paquete.' }
if (@(Get-ChildItem -LiteralPath $source -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'El paquete contiene enlaces.' }
if ($root.StartsWith($source + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $root -eq $source) { throw 'HostRoot debe estar fuera del paquete.' }
Assert-HostStopped $root
$release = Get-HostPath $root ('releases/' + $ReleaseName)
if (Test-Path -LiteralPath $release) { throw 'La versión de destino ya existe; no se sobrescribe.' }
if (-not (Test-Path -LiteralPath (Get-HostPath $root 'state/default.conf')) -and -not $InitialConfig) { throw 'La primera instalación requiere InitialConfig; no se eligen dispositivos automáticamente.' }
if ($InitialConfig -and -not (Test-Path -LiteralPath $InitialConfig -PathType Leaf)) { throw 'No existe InitialConfig.' }
if (-not $PSCmdlet.ShouldProcess($root,"Guardar copia, instalar $ReleaseName y cambiar referencia (sin arrancar)")) { return }
New-Item -ItemType Directory -Force -Path $root,(Get-HostPath $root 'releases'),(Get-HostPath $root 'state'),(Get-HostPath $root 'backups'),(Get-HostPath $root 'tools') | Out-Null
if (Test-Path -LiteralPath (Get-HostPath $root 'current.json')) {
    $current = Get-CurrentRelease $root
    $backup = Get-HostPath $root ('backups/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff') + '.zip')
    Compress-Archive -LiteralPath (Get-HostPath $root 'current.json'),(Get-HostPath $root 'state') -DestinationPath $backup
}
Copy-Item -LiteralPath $source -Destination $release -Recurse
if (-not (Test-Path -LiteralPath (Get-HostPath $root 'state/default.conf'))) { Copy-Item -LiteralPath $InitialConfig -Destination (Get-HostPath $root 'state/default.conf') }
Copy-Item -Path "$PSScriptRoot/*.ps1" -Destination (Get-HostPath $root 'tools') -Force
Write-CurrentRelease $root $ReleaseName
[pscustomobject]@{ HostRoot=$root; ReleaseName=$ReleaseName; Started=$false; StatePreserved=$true }
