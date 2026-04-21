# Desinstala StockandriaAgent del sistema.
param(
    [string]$ServiceName = "StockandriaAgent",
    [string]$InstallDir = "C:\Program Files\StockandriaAgent",
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Este script debe ejecutarse como administrador."
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Deteniendo servicio..."
    sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Servicio eliminado."
} else {
    Write-Host "No hay servicio $ServiceName instalado."
}

if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "Archivos eliminados de $InstallDir"
}

if ($RemoveData) {
    $dataDir = Join-Path $env:ProgramData "StockandriaAgent"
    if (Test-Path $dataDir) {
        Remove-Item -Path $dataDir -Recurse -Force
        Write-Host "Datos locales eliminados de $dataDir"
    }
    [System.Environment]::SetEnvironmentVariable("STOCKANDRIA_LINK_TOKEN", $null, "Machine")
    [System.Environment]::SetEnvironmentVariable("STOCKANDRIA_Backend__Url", $null, "Machine")
}

Write-Host "Desinstalacion completada."
