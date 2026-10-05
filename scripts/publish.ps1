# 產出 win-x64 自包含（self-contained）發佈，不需另裝 .NET Runtime：
#   1. 安裝程式（Velopack）：artifacts/installer/RegistrationAdminApp-win-Setup.exe
#      使用者安裝不需系統管理員權限，會建立開始功能表與桌面捷徑，並出現在「設定 → 應用程式」可解除安裝。
#   2. 更新套件：同一資料夾的 *.nupkg 與 releases.win.json，會一併複製到更新來源資料夾，已安裝的電腦啟動時自動下載更新。
#   3. 免安裝版：artifacts/RegistrationAdmin-win-x64.zip（不會自動更新）。
#   4. 交付安裝包：artifacts/交付安裝包/<版本>/（安裝程式、使用手冊與安裝說明）。
#
# 用法：
#   .\scripts\publish.ps1 -NoUpload              # 未簽章本機測試
#   .\scripts\publish.ps1 -Version 0.2.4 -SigningProvider CertificateStore -NoUpload
#   .\scripts\publish.ps1 -Version 0.2.4 -SigningProvider CertificateStore  # 驗證後發佈
#
# 更新來源（使用者電腦去哪裡找新版）只有一個設定值：config/update-feed.local.txt（不進版控）；沒有時使用 config/update-feed.txt 範本。
# 可以是資料夾路徑（本機或 \\伺服器\共用資料夾），或 GitHub Releases 網址（https://github.com/<組織>/<儲存庫>）。
# 使用 GitHub 時會自動建立 Release 並上傳；需要使用者環境變數 REGISTRATIONADMIN_GITHUB_TOKEN（該儲存庫 Contents 讀寫權限）。
#
# OAuth 用戶端：會把 config/google-oauth-client.json（若沒有，則使用 config 下唯一的 client_secret_*.json）
# 以 google-oauth-client.json 名稱放進發佈檔。這個檔案不進版控。
#
# 簽章設定與正式版流程：docs/release-signing.md。未簽章測試必須使用 -NoUpload。
param(
  [string]$Version,
  [string]$UpdateFeed,
  [switch]$NoUpload,   # 只在本機產生安裝程式，不發佈到更新來源（測試用）
  [ValidateSet('None', 'CertificateStore', 'ArtifactSigning')]
  [string]$SigningProvider = 'None',
  [string]$ArtifactsDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
. (Join-Path $PSScriptRoot 'signing.ps1')

# Fail before publishing/building/deleting outputs if signing configuration is missing.
if ($SigningProvider -eq 'None' -and -not $NoUpload) {
  throw 'Unsigned packaging requires -NoUpload. Select -SigningProvider CertificateStore or ArtifactSigning to publish.'
}
$signing = if ($SigningProvider -ne 'None') { Get-ReleaseSigningConfiguration -Provider $SigningProvider } else { $null }
# Avoid accidental ambient Velopack signing options (including secret-bearing parameters).
foreach ($name in @('VPK_SIGN_PARAMS', 'VPK_SIGN_TEMPLATE', 'VPK_AZURE_TRUSTED_SIGN_FILE')) {
  if ([Environment]::GetEnvironmentVariable($name)) { throw "Clear ambient $name; use the explicit SigningProvider configuration." }
}
$artifactRoot = [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $ArtifactsDirectory))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path (Get-Location).Path 'artifacts'))
if ($artifactRoot -ne $allowedRoot -and -not $artifactRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'ArtifactsDirectory must be artifacts or a subdirectory of artifacts.'
}
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

$packId = 'RegistrationAdminApp'   # 安裝位置 %LOCALAPPDATA%\RegistrationAdminApp；設定與登入資料另存在 %LOCALAPPDATA%\RegistrationAdmin，更新與解除安裝都不會動到
$packTitle = '活動報名後台'
$packAuthors = '高精地圖研究發展中心'

if (-not $Version) {
  $Version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $NoUpload -and ([version]$Version -le [version]'0.2.3')) {
  throw 'Published v0.2.0 through v0.2.3 are immutable. Use a new version (v0.2.4 or later).'
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
$out = Join-Path $artifactRoot 'RegistrationAdmin-win-x64'
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
$zip = "$out.zip"
# Remove a stale portable artifact before starting; failed builds must not leave it looking current.
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }

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
Copy-Item docs/操作說明.md $out -ErrorAction SilentlyContinue
Copy-Item docs/manual/使用手冊.pdf $out -ErrorAction SilentlyContinue

# ── 2. 安裝程式與更新套件（Velopack 在打包階段簽章） ──
dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }
$releases = Join-Path $artifactRoot 'installer'
if (Test-Path $releases) { Remove-Item -LiteralPath $releases -Recurse -Force }
$feedIsFolder = -not $NoUpload -and $UpdateFeed -and -not ($UpdateFeed -match '^https?://')
$feedPath = if ($feedIsFolder) { [Environment]::ExpandEnvironmentVariables($UpdateFeed) } else { $null }
$feedIsGitHub = -not $NoUpload -and $UpdateFeed -match '^https://github\.com/[^/]+/[^/]+/?$'
if ($feedIsGitHub) {
  $UpdateFeed = $UpdateFeed.TrimEnd('/')
  # 上傳用 token 只放在使用者環境變數，不寫進任何檔案。
  $ghToken = $env:REGISTRATIONADMIN_GITHUB_TOKEN
  if (-not $ghToken) { $ghToken = [Environment]::GetEnvironmentVariable('REGISTRATIONADMIN_GITHUB_TOKEN', 'User') }
  if (-not $ghToken) { throw '更新來源是 GitHub，但找不到上傳用的 token。請先設定使用者環境變數 REGISTRATIONADMIN_GITHUB_TOKEN（見部署手冊 5.1）。' }
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

$packArguments = @('vpk', 'pack', '--packId', $packId, '--packVersion', $Version,
  '--runtime', 'win-x64',
  '--packDir', $out, '--mainExe', 'RegistrationAdmin.exe', '--packTitle', $packTitle,
  '--packAuthors', $packAuthors, '--icon', 'src/RegistrationAdmin.App/Assets/app.ico',
  '--shortcuts', 'Desktop,StartMenuRoot', '-o', $releases)
if ($signing) {
  $template = $signing.Template
  # Windows PowerShell 5.1 / legacy native argument passing requires escaped embedded quotes.
  if ($PSVersionTable.PSVersion -lt [version]'7.3' -or
      (Get-Variable PSNativeCommandArgumentPassing -ErrorAction SilentlyContinue).Value -eq 'Legacy') {
    $template = $template.Replace('"', '\"')
  }
  $packArguments += @('--signTemplate', $template)
}
dotnet @packArguments
if ($LASTEXITCODE -ne 0) { throw 'vpk pack 失敗' }

# ── 3. 嚴格驗證，然後從已簽章的套件建立免安裝版 ──
if ($signing) {
  $verification = Join-Path $artifactRoot ('verify-' + [Guid]::NewGuid().ToString('N'))
  try {
    $fullPackage = Join-Path $releases "$packId-$Version-full.nupkg"
    Expand-ReleaseArchive -Path $fullPackage -Destination $verification
    $payload = Join-Path $verification 'lib/app'
    Assert-ReleaseSignature -Path (Join-Path $payload 'RegistrationAdmin.exe') -SignTool $signing.Tool -Thumbprint $signing.Thumbprint
    # Velopack signs a staging copy; copy signed application binaries back for the original portable ZIP.
    foreach ($binary in Get-ChildItem -LiteralPath $out -Recurse -File | Where-Object Extension -In @('.exe', '.dll')) {
      $relative = $binary.FullName.Substring($out.Length + 1)
      $signedBinary = Join-Path $payload $relative
      if (-not (Test-Path -LiteralPath $signedBinary -PathType Leaf)) { throw "Packaged binary is missing: $relative" }
      Copy-Item -LiteralPath $signedBinary -Destination $binary.FullName -Force
    }
  } finally {
    if (Test-Path -LiteralPath $verification) { Remove-Item -LiteralPath $verification -Recurse -Force }
  }
}
Compress-Archive -Path "$out/*" -DestinationPath $zip
if ($signing) {
  & (Join-Path $PSScriptRoot 'verify-release.ps1') -Version $Version -ArtifactsDirectory $artifactRoot -SignTool $signing.Tool -Thumbprint $signing.Thumbprint
} else {
  Write-Host 'UNSIGNED DEVELOPMENT PACKAGE: local only; do not distribute as a signed release.' -ForegroundColor Yellow
}

& (Join-Path $PSScriptRoot 'prepare-handoff.ps1') -Version $Version -ArtifactsDirectory $artifactRoot

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
if ($NoUpload) {
  Write-Host 'NoUpload: artifacts created locally; nothing downloaded from or uploaded to the update feed.' -ForegroundColor Cyan
} elseif ($feedIsFolder) {
  Write-Host "已發佈到更新來源：$feedPath（已安裝的電腦下次開啟程式時會自動下載）" -ForegroundColor Green
} elseif ($feedIsGitHub) {
  Write-Host "已發佈到 GitHub：$UpdateFeed/releases（已安裝的電腦下次開啟程式時會自動下載）" -ForegroundColor Green
  Write-Host '新使用者：請直接交付上方的交付資料夾；GitHub Release 持續提供線上更新。' -ForegroundColor Green
} elseif ($UpdateFeed) {
  Write-Host "更新來源是網址：請把 $releases 內的 .nupkg、releases.win.json 上傳到 $UpdateFeed" -ForegroundColor Yellow
}
