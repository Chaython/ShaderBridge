$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot 'artifacts\win-x64'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish .\src\ShaderBridge\ShaderBridge.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false `
  -o $out

Write-Host "Published to $out" -ForegroundColor Green
