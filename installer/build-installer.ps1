$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $repoRoot "dist"
$publishDir = Join-Path $distDir "publish"
$installerOutDir = Join-Path $distDir "installer"
$issPath = Join-Path $PSScriptRoot "byeWhiteList.iss"
$dotnetHome = Join-Path $repoRoot ".dotnet_home"

if (-not (Test-Path $dotnetHome)) { New-Item -ItemType Directory -Path $dotnetHome | Out-Null }
$env:DOTNET_CLI_HOME = $dotnetHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

Write-Host "== Publish (Release, win-x64) ==" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Push-Location $repoRoot
try {
  dotnet publish ".\ByeWhiteList.Windows.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
}
finally {
  Pop-Location
}

Write-Host "== Build Inno Setup ==" -ForegroundColor Cyan
if (-not (Test-Path $installerOutDir)) { New-Item -ItemType Directory -Path $installerOutDir | Out-Null }

$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if (-not $iscc) {
  Write-Host "Inno Setup (iscc) not found in PATH." -ForegroundColor Yellow
  Write-Host "Install Inno Setup and run:" -ForegroundColor Yellow
  Write-Host ('  iscc "' + $issPath + '"') -ForegroundColor Yellow
  exit 0
}

& $iscc.Source $issPath

Write-Host "Done. Installer output folder:" -ForegroundColor Green
Write-Host ("  " + $installerOutDir) -ForegroundColor Green
