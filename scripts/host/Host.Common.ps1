Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Resolve-HostRoot([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($resolved -eq [IO.Path]::GetPathRoot($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'HostRoot no puede ser la raíz de una unidad.' }
    if ($resolved -match '["\r\n]') { throw 'Ruta de host inválida.' }
    if (Test-Path -LiteralPath $resolved) {
        $items = @((Get-Item -LiteralPath $resolved)) + @(Get-ChildItem -LiteralPath $resolved -Force -Recurse)
        if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'El host contiene enlaces o puntos de reanálisis; no se modifica.' }
    }
    return $resolved
}
function Get-HostPath([string]$Root, [string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Ruta fuera del host solicitado.' }
    return $path
}
function Assert-ReleaseName([string]$Name) {
    if ($Name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$' -or $Name -match '\.\.') { throw 'Nombre de versión inválido.' }
}
function Get-CurrentRelease([string]$Root) {
    $pointer = Get-Content -Raw -LiteralPath (Get-HostPath $Root 'current.json') | ConvertFrom-Json
    Assert-ReleaseName $pointer.ReleaseName
    $release = Get-HostPath $Root ('releases/' + $pointer.ReleaseName)
    if (-not (Test-Path -LiteralPath (Join-Path $release 'QDNH.exe') -PathType Leaf)) { throw 'La versión no contiene QDNH.exe.' }
    return $pointer
}
function Write-CurrentRelease([string]$Root,[string]$ReleaseName) {
    Assert-ReleaseName $ReleaseName
    $temporary = Get-HostPath $Root ('current.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    @{ ReleaseName=$ReleaseName; UpdatedUtc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination (Get-HostPath $Root 'current.json') -Force
}
function Assert-NoRunningChild([string]$Root) {
    $marker = Get-HostPath $Root 'child.json'
    if (Test-Path -LiteralPath $marker) {
        $identity = Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json
        $running = Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $running -and $running.StartTime.ToUniversalTime().Ticks -eq [long]$identity.StartTimeUtcTicks) {
            throw 'QDNH sigue activo en este host. Espere la salida limpia antes de iniciar otra instancia o cambiar versión.'
        }
    }
}
function Assert-HostStopped([string]$Root) {
    Assert-NoRunningChild $Root
    $lockPath = Get-HostPath $Root 'supervisor.lock'
    if (Test-Path -LiteralPath $lockPath) {
        $handle = $null
        try { $handle = [IO.File]::Open($lockPath,'Open','ReadWrite','None') }
        catch { throw 'El supervisor está activo. Solicite parada y espere antes de instalar o revertir.' }
        finally { if ($null -ne $handle) { $handle.Dispose() } }
    }
}
function Assert-SerialOnlyConfiguration([string]$Root) {
    $lines=Get-Content -LiteralPath (Get-HostPath $Root 'state/default.conf')
    $mode='all'
    for ($i=0;$i+1 -lt $lines.Count;$i+=2) { if ($lines[$i] -eq 'Mode') { $mode=$lines[$i+1] } }
    if ($mode -ine 'serial') { throw 'El arranque Session 0 de modo serie requiere Mode=serial en la configuración.' }
}
