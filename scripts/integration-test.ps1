# 對「複製的測試試算表」執行 Google 整合測試。絕不可指向正式回應試算表。
param(
  [Parameter(Mandatory = $true)][string]$SpreadsheetId,
  [Parameter(Mandatory = $true)][string]$ClientSecretPath,
  # 省略時沿用測試試算表 _Config 已記錄的來源工作表
  [int]$SourceSheetId = -1
)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$env:RA_IT_SPREADSHEET_ID = $SpreadsheetId
$env:RA_IT_CLIENT_SECRET = $ClientSecretPath
if ($SourceSheetId -ge 0) { $env:RA_IT_SOURCE_SHEET_ID = "$SourceSheetId" } else { Remove-Item Env:RA_IT_SOURCE_SHEET_ID -ErrorAction SilentlyContinue }
dotnet test tests/RegistrationAdmin.IntegrationTests/RegistrationAdmin.IntegrationTests.csproj -c Release --logger "console;verbosity=normal"
