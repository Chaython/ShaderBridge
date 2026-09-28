$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet --version
dotnet restore .\ShaderBridge.sln
dotnet build .\ShaderBridge.sln -c Release --no-restore
