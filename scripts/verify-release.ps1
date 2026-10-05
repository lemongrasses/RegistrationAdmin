# Verify the final deliverables, not just the dotnet publish input.
param(
  [Parameter(Mandatory)][string]$Version,
  [string]$ArtifactsDirectory = 'artifacts',
  [string]$SignTool = $env:REGISTRATIONADMIN_SIGNTOOL,
  [string]$Thumbprint
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'signing.ps1')
if (-not $SignTool) {
  $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
  if ($command) { $SignTool = $command.Source }
}
if (-not $SignTool -or -not (Test-Path -LiteralPath $SignTool -PathType Leaf)) { throw 'Windows SDK SignTool is required for release verification.' }
$root = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$installer = Join-Path $root 'installer'
$temp = Join-Path $root ('verify-' + [Guid]::NewGuid().ToString('N'))
try {
  Assert-ReleaseSignature -Path (Join-Path $root 'RegistrationAdmin-win-x64/RegistrationAdmin.exe') -SignTool $SignTool -Thumbprint $Thumbprint
  Assert-ReleaseSignature -Path (Join-Path $installer 'RegistrationAdminApp-win-Setup.exe') -SignTool $SignTool -Thumbprint $Thumbprint
  foreach ($file in Get-ChildItem -LiteralPath $installer -Filter '*.exe' -File) {
    Assert-ReleaseSignature -Path $file.FullName -SignTool $SignTool -Thumbprint $Thumbprint
  }
  $archives = @(
    @{ Path = (Join-Path $installer "RegistrationAdminApp-$Version-full.nupkg"); App = 'lib/app/RegistrationAdmin.exe'; Updater = 'lib/app/Squirrel.exe' },
    @{ Path = (Join-Path $installer 'RegistrationAdminApp-win-Portable.zip'); App = 'current/RegistrationAdmin.exe'; Updater = 'Update.exe' },
    @{ Path = (Join-Path $root 'RegistrationAdmin-win-x64.zip'); App = 'RegistrationAdmin.exe'; Updater = $null }
  )
  $index = 0
  foreach ($archive in $archives) {
    $destination = Join-Path $temp ([string]$index++)
    Expand-ReleaseArchive -Path $archive.Path -Destination $destination
    Assert-ReleaseSignature -Path (Join-Path $destination $archive.App) -SignTool $SignTool -Thumbprint $Thumbprint
    if ($archive.Updater) {
      Assert-ReleaseSignature -Path (Join-Path $destination $archive.Updater) -SignTool $SignTool -Thumbprint $Thumbprint
    }
    # Includes execution stubs and Update.exe/Squirrel.exe, even when located in nested folders.
    foreach ($binary in Get-ChildItem -LiteralPath $destination -Recurse -File -Filter '*.exe') {
      Assert-ReleaseSignature -Path $binary.FullName -SignTool $SignTool -Thumbprint $Thumbprint
    }
  }
  Write-Host 'All release executable signatures and timestamps verified.' -ForegroundColor Green
} finally {
  if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
