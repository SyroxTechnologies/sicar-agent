# Instala StockandriaAgent como Windows Service.
# Uso:
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-InstallDir <ruta>] [-BackendUrl <url>] [-LinkToken <token>]

param(
    [string]$InstallDir = "C:\Program Files\StockandriaAgent",
    [string]$BackendUrl = "",
    [string]$LinkToken = "",
    [string]$ServiceName = "StockandriaAgent",
    [string]$DisplayName = "Stockandria Agent",
    [string]$Description = "Agente local de integracion SICAR para Stockandria"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Este script debe ejecutarse como administrador."
    exit 1
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$publishDir = Join-Path $scriptRoot "publish"

if (-not (Test-Path $publishDir)) {
    Write-Host "No se encontro $publishDir. Generando build de release (self-contained, no requiere .NET)..."
    Push-Location $scriptRoot
    try {
        dotnet publish src/StockandriaAgent/StockandriaAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publishDir
        if ($LASTEXITCODE -ne 0) {
            Write-Error "La compilacion fallo."
            exit 1
        }
    } finally {
        Pop-Location
    }
}

Write-Host "Verificando servicio existente..."
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Servicio existente detectado. Deteniendo y eliminando antes de reinstalar..."
    sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}

Write-Host "Copiando archivos a $InstallDir ..."
Copy-Item -Path (Join-Path $publishDir '*') -Destination $InstallDir -Recurse -Force

$exePath = Join-Path $InstallDir "StockandriaAgent.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "No se encontro $exePath tras copiar. Revisar el publish."
    exit 1
}

if ($BackendUrl -ne "") {
    [System.Environment]::SetEnvironmentVariable("STOCKANDRIA_Backend__Url", $BackendUrl, "Machine")
    Write-Host "Variable Backend:Url establecida para la maquina."
}

if ($LinkToken -ne "") {
    [System.Environment]::SetEnvironmentVariable("STOCKANDRIA_LINK_TOKEN", $LinkToken, "Machine")
    Write-Host "Link token almacenado como variable de entorno (se consumira en el primer arranque)."
}

Write-Host "Creando servicio $ServiceName ..."
sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "$DisplayName" | Out-Null
sc.exe description $ServiceName "$Description" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

Write-Host "Iniciando servicio..."
sc.exe start $ServiceName | Out-Null

Write-Host "Instalacion completada. Logs en $InstallDir\logs\"
