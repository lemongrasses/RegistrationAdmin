# 還原、建置並執行單元測試。需先安裝 .NET 10 SDK（x64）。
# 用法：在專案根目錄執行  powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

dotnet --version
dotnet restore RegistrationAdmin.slnx
dotnet build RegistrationAdmin.slnx -c Release --no-restore
dotnet test tests/RegistrationAdmin.Tests/RegistrationAdmin.Tests.csproj -c Release --no-build --logger "console;verbosity=normal"

Write-Host ''
Write-Host '建置與單元測試完成。Google 整合測試請見 docs/開發者說明.md。' -ForegroundColor Green
