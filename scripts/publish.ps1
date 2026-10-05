# 產出 win-x64 自包含（self-contained）發佈，不需另裝 .NET Runtime：
#   1. 安裝程式（Velopack）：artifacts/installer/RegistrationAdmin-win-Setup.exe
#      使用者安裝不需系統管理員權限，會建立開始功能表與桌面捷徑，並出現在「設定 → 應用程式」可解除安裝。
#   2. 更新套件：同一資料夾的 *.nupkg 與 releases.win.json，會一併複製到更新來源資料夾，已安裝的電腦啟動時自動下載更新。
#   3. 免安裝版：artifacts/RegistrationAdmin-win-x64.zip（不會自動更新）。
#
# 用法：
#   .\scripts\publish.ps1                         # 版本取自 Directory.Build.props
#   .\scripts\publish.ps1 -Version 0.2.1          # 發佈新版本（版本號必須比上一版大）
#   .\scripts\publish.ps1 -UpdateFeed \\server\share\RegistrationAdmin   # 暫時改用其他更新來源
#
# 更新來源（使用者電腦去哪裡找新版）只有一個設定值：config/update-feed.local.txt（不進版控）；沒有時使用 config/update-feed.txt 範本。
# 可以是資料夾路徑（本機或 \\伺服器\共用資料夾），或 GitHub Releases 網址（https://github.com/<組織>/<儲存庫>）。
# 使用 GitHub 時會自動建立 Release 並上傳；需要使用者環境變數 REGISTRATIONADMIN_GITHUB_TOKEN（該儲存庫 Contents 讀寫權限）。
#
# OAuth 用戶端：會把 config/google-oauth-client.json（若沒有，則使用 config 下唯一的 client_secret_*.json）
# 以 google-oauth-client.json 名稱放進發佈檔。這個檔案不進版控。
#
# 注意：未簽章；Windows SmartScreen 可能顯示警告。正式版需組織的程式碼簽章憑證（vpk pack --signParams）。
param(
  [string]$Version,
  [string]$UpdateFeed,
  [switch]$NoUpload   # 只在本機產生安裝程式，不發佈到更新來源（測試用）
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$packId = 'RegistrationAdminApp'   # 安裝位置 %LOCALAPPDATA%\RegistrationAdminApp；設定與登入資料另存在 %LOCALAPPDATA%\RegistrationAdmin，更新與解除安裝都不會動到
$packTitle = '活動報名後台'
$packAuthors = 'lemongrasses'

if (-not $Version) {
  $Version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $UpdateFeed) {
  $feedFile = if (Test-Path config/update-feed.local.txt) { 'config/update-feed.local.txt' } else { 'config/update-feed.txt' }
  $UpdateFeed = Get-Content $feedFile -Encoding utf8 |
    ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } | Select-Object -First 1
}
Write-Host "版本：$Version" -ForegroundColor Cyan
Write-Host "更新來源：$(if ($UpdateFeed) { $UpdateFeed } else { '（未設定，不檢查更新）' })" -ForegroundColor Cyan

# ── OAuth 用戶端 ──
$oauth = 'config/google-oauth-client.json'
if (-not (Test-Path $oauth)) {
  $candidates = @(Get-ChildItem config -Filter 'client_secret_*.json' -ErrorAction SilentlyContinue)
  if ($candidates.Count -ne 1) {
    throw "找不到 OAuth 用戶端檔。請把 Google Cloud 下載的 Desktop app 用戶端 JSON 放在 config\google-oauth-client.json（不要提交到 git）。"
  }
  $oauth = $candidates[0].FullName
}

# ── 1. dotnet publish ──
$out = 'artifacts/RegistrationAdmin-win-x64'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish src/RegistrationAdmin.App/RegistrationAdmin.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none `
  -p:Version=$Version `
  -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失敗' }

Copy-Item $oauth (Join-Path $out 'google-oauth-client.json') -Force
Set-Content (Join-Path $out 'update-feed.txt') -Value @('# 程式更新來源（由 scripts\publish.ps1 產生）', $UpdateFeed) -Encoding utf8

# ── 2. 免安裝版 zip ──
$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out/*" -DestinationPath $zip

# ── 3. 安裝程式與更新套件（Velopack） ──
dotnet tool restore | Out-Null
$releases = 'artifacts/installer'
if (Test-Path $releases) { Remove-Item $releases -Recurse -Force }
$feedIsFolder = -not $NoUpload -and $UpdateFeed -and -not ($UpdateFeed -match '^https?://')
$feedPath = if ($feedIsFolder) { [Environment]::ExpandEnvironmentVariables($UpdateFeed) } else { $null }
$feedIsGitHub = -not $NoUpload -and $UpdateFeed -match '^https://github\.com/[^/]+/[^/]+/?$'
if ($feedIsGitHub) {
  $UpdateFeed = $UpdateFeed.TrimEnd('/')
  # 上傳用 token 只放在使用者環境變數，不寫進任何檔案。
  $ghToken = $env:REGISTRATIONADMIN_GITHUB_TOKEN
  if (-not $ghToken) { $ghToken = [Environment]::GetEnvironmentVariable('REGISTRATIONADMIN_GITHUB_TOKEN', 'User') }
  if (-not $ghToken) { throw '更新來源是 GitHub，但找不到上傳用的 token。請先設定使用者環境變數 REGISTRATIONADMIN_GITHUB_TOKEN（fine-grained token，該儲存庫 Contents 讀寫）。' }
}

# 先取回更新來源上的最新版，才能產生較小的差異更新檔（delta）。
if ($feedIsFolder -and (Test-Path (Join-Path $feedPath 'releases.win.json'))) {
  dotnet vpk download local --path $feedPath -o $releases
  if ($LASTEXITCODE -ne 0) { throw 'vpk download 失敗' }
}
if ($feedIsGitHub) {
  # 第一次發佈時 GitHub 上還沒有任何版本，取不到是正常的。
  dotnet vpk download github --repoUrl $UpdateFeed --token $ghToken -o $releases
  if ($LASTEXITCODE -ne 0) { Write-Host '（GitHub 上還沒有舊版本，這次只產生完整更新檔）' -ForegroundColor DarkGray }
}

dotnet vpk pack `
  --packId $packId `
  --packVersion $Version `
  --packDir $out `
  --mainExe RegistrationAdmin.exe `
  --packTitle $packTitle `
  --packAuthors $packAuthors `
  --shortcuts 'Desktop,StartMenuRoot' `
  -o $releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack 失敗' }

if ($feedIsFolder) {
  New-Item -ItemType Directory -Force $feedPath | Out-Null
  dotnet vpk upload local --path $feedPath -o $releases
  if ($LASTEXITCODE -ne 0) { throw 'vpk upload 失敗' }
}
if ($feedIsGitHub) {
  dotnet vpk upload github --repoUrl $UpdateFeed --token $ghToken -o $releases --publish --releaseName "活動報名後台 $Version" --tag "v$Version"
  if ($LASTEXITCODE -ne 0) { throw 'vpk upload github 失敗' }
}

Write-Host ''
Write-Host "安裝程式：$releases\$packId-win-Setup.exe" -ForegroundColor Green
Write-Host "免安裝版：$zip" -ForegroundColor Green
if ($feedIsFolder) {
  Write-Host "已發佈到更新來源：$feedPath（已安裝的電腦下次開啟程式時會自動下載）" -ForegroundColor Green
} elseif ($feedIsGitHub) {
  Write-Host "已發佈到 GitHub：$UpdateFeed/releases（已安裝的電腦下次開啟程式時會自動下載）" -ForegroundColor Green
  Write-Host "新使用者的安裝程式下載網址：$UpdateFeed/releases/latest/download/$packId-win-Setup.exe" -ForegroundColor Green
} elseif ($UpdateFeed) {
  Write-Host "更新來源是網址：請把 $releases 內的 .nupkg、releases.win.json 上傳到 $UpdateFeed" -ForegroundColor Yellow
}
