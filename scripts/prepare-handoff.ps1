# Collect an existing installer for direct delivery without rebuilding or uploading.
param(
  [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$')][string]$Version,
  [string]$ArtifactsDirectory = 'artifacts'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $ArtifactsDirectory))
$allowedRoot = Join-Path $projectRoot 'artifacts'
if ($artifactRoot -ne $allowedRoot -and -not $artifactRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'ArtifactsDirectory must be artifacts or a subdirectory of artifacts.'
}
$installerName = 'RegistrationAdminApp-win-Setup.exe'
$source = Join-Path $artifactRoot "installer/$installerName"
$manual = Join-Path $projectRoot 'docs/manual/使用手冊.pdf'
$manifest = Get-Content -LiteralPath (Join-Path $artifactRoot 'installer/releases.win.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$currentAssets = Get-Content -LiteralPath (Join-Path $artifactRoot 'installer/assets.win.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not (@($manifest.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Version }).Count) -or
    -not (@($currentAssets | Where-Object { $_.Type -eq 'Full' -and $_.RelativeFileName -eq "RegistrationAdminApp-$Version-full.nupkg" }).Count)) {
  throw "Version $Version does not match the current installer assets."
}
foreach ($required in @($source, $manual)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing handoff file: $required" }
}
$destination = Join-Path $artifactRoot "交付安裝包/$Version"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $destination $installerName) -Force
Copy-Item -LiteralPath $manual -Destination (Join-Path $destination '使用手冊.pdf') -Force
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$copiedHash = (Get-FileHash -LiteralPath (Join-Path $destination $installerName) -Algorithm SHA256).Hash
if ($sourceHash -ne $copiedHash) { throw 'Installer copy failed SHA256 verification.' }
Set-Content -LiteralPath (Join-Path $destination 'SHA256.txt') -Value "$copiedHash  $installerName" -Encoding UTF8
$signature = Get-AuthenticodeSignature -LiteralPath $source
$signatureNote = if ($signature.Status -eq 'Valid') { '安裝檔的數位簽章已通過本機驗證。' } else { "安裝檔簽章狀態：$($signature.Status)。此資料夾不會替安裝檔加上簽章。" }
Set-Content -LiteralPath (Join-Path $destination '安裝說明.txt') -Encoding UTF8 -Value @"
活動報名後台 $Version（Windows 64 位元）

1. 將整個資料夾複製給使用者（例如 USB 或組織共用資料夾）。
2. 執行 $installerName，安裝完成後會開啟程式並建立捷徑。
3. 登入 Google，連線到有權限的報名試算表；詳細操作見使用手冊.pdf。

安裝程式已包含必要執行環境與設定，不需另裝 .NET。
更新來源沿用安裝檔內的設定；正式版本持續透過 GitHub Release 提供線上更新。
SHA256.txt 可用來核對安裝檔在複製前後是否一致。
$signatureNote
資料夾交付仍可能出現 Windows 安全提示。
"@
Write-Host "交付資料夾：$destination" -ForegroundColor Green
