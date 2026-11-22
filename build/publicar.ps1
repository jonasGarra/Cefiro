<#
.SYNOPSIS
    Publica Céfiro (autocontenido, win-x64: no hace falta instalar .NET) y genera el instalador.

.DESCRIPTION
    1. Publica el servicio, la interfaz y la herramienta de consola en artifacts\publish\Cefiro.
    2. Compila installer\Cefiro.iss con Inno Setup 6 y deja el instalador en artifacts\instalador.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publicar.ps1
    powershell -ExecutionPolicy Bypass -File build\publicar.ps1 -SinInstalador
#>
param(
    [string] $Configuration = 'Release',
    [switch] $SinInstalador
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnet) { $dotnet.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw 'No se encuentra el SDK de .NET 10 (dotnet.exe).' }

$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = [regex]::Match($props, '<Version>([^<]+)</Version>').Groups[1].Value
Write-Host "Céfiro $version · $Configuration · win-x64 autocontenido" -ForegroundColor Cyan

$publish = Join-Path $root 'artifacts\publish\Cefiro'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

$projects = @(
    'src\Cefiro.Service\Cefiro.Service.csproj',
    'src\Cefiro.App\Cefiro.App.csproj',
    'tools\Cefiro.Cli\Cefiro.Cli.csproj'
)

foreach ($project in $projects) {
    Write-Host "  publicando $project…"
    & $dotnet publish (Join-Path $root $project) `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:DebugType=none `
        -o $publish -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Error publicando $project" }
}

$size = (Get-ChildItem $publish -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Publicado en {0} ({1:0} MB)" -f $publish, $size) -ForegroundColor Green

if ($SinInstalador) { return }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning 'No se encuentra Inno Setup 6: instálalo (https://jrsoftware.org/isinfo.php) para generar el instalador.'
    return
}

$output = Join-Path $root 'artifacts\instalador'
& $iscc /Q "/DMyAppVersion=$version" "/DPublishDir=$publish" "/O$output" (Join-Path $root 'installer\Cefiro.iss')
if ($LASTEXITCODE -ne 0) { throw 'Error compilando el instalador' }

$setup = Join-Path $output "Cefiro-Setup-$version.exe"
Write-Host ("Instalador: {0} ({1:0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
