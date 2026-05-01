$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $repoRoot "dist"
$portableDir = Join-Path $distDir "portable"
$publishDir = Join-Path $portableDir "publish"
$zipPath = Join-Path $portableDir "ByeWhiteList-VPN_Portable_win-x64.zip"

Write-Host "== Publish Portable (Release, win-x64) ==" -ForegroundColor Cyan
if (Test-Path $portableDir) { Remove-Item $portableDir -Recurse -Force }
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
} finally {
  Pop-Location
}

Write-Host "== Zip Portable ==" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Готово. Portable архив:" -ForegroundColor Green
Write-Host "  $zipPath" -ForegroundColor Green
