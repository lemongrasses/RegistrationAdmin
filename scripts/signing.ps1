# Shared release signing configuration. No passwords or tokens belong in command lines.
function Get-ReleaseSigningConfiguration {
  param([ValidateSet('CertificateStore', 'ArtifactSigning')][string]$Provider)
  $certificate = $null

  if ($Provider -eq 'CertificateStore' -and $env:REGISTRATIONADMIN_SIGN_CERT_THUMBPRINT -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Set REGISTRATIONADMIN_SIGN_CERT_THUMBPRINT to the public OV code-signing certificate thumbprint (40 hex characters).'
  }
  if ($Provider -eq 'ArtifactSigning' -and (-not $env:REGISTRATIONADMIN_SIGN_DLIB -or -not $env:REGISTRATIONADMIN_SIGN_METADATA)) {
    throw 'ArtifactSigning requires REGISTRATIONADMIN_SIGN_DLIB and REGISTRATIONADMIN_SIGN_METADATA; configure an authenticated Public Trust profile first.'
  }
  $tool = $env:REGISTRATIONADMIN_SIGNTOOL
  if (-not $tool) {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { $tool = $command.Source }
  }
  if (-not $tool -or -not (Test-Path -LiteralPath $tool -PathType Leaf)) {
    throw 'Install Windows SDK SignTool and set REGISTRATIONADMIN_SIGNTOOL to its absolute signtool.exe path.'
  }
  $tool = (Resolve-Path -LiteralPath $tool).Path
  $timestamp = $env:REGISTRATIONADMIN_SIGN_TIMESTAMP_URL
  if (-not $timestamp) {
    $timestamp = if ($Provider -eq 'ArtifactSigning') { 'http://timestamp.acs.microsoft.com' } else { 'http://timestamp.digicert.com' }
  }
  $uri = $null
  if (-not [Uri]::TryCreate($timestamp, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https') -or $uri.UserInfo) {
    throw 'REGISTRATIONADMIN_SIGN_TIMESTAMP_URL must be an HTTP(S) RFC3161 endpoint without credentials.'
  }
  $arguments = @('sign', '/fd', 'SHA256', '/tr', $timestamp, '/td', 'SHA256')
  if ($Provider -eq 'CertificateStore') {
    $store = $env:REGISTRATIONADMIN_SIGN_CERT_STORE
    if (-not $store) { $store = 'CurrentUser' }
    if ($store -notin @('CurrentUser', 'LocalMachine')) { throw 'REGISTRATIONADMIN_SIGN_CERT_STORE must be CurrentUser or LocalMachine.' }
    $certificate = Get-Item "Cert:\$store\My\$env:REGISTRATIONADMIN_SIGN_CERT_THUMBPRINT" -ErrorAction SilentlyContinue
    if (-not $certificate -or -not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
      throw 'The selected signing certificate is missing, expired, or has no accessible private key.'
    }
    $usages = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
      ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
    if ('1.3.6.1.5.5.7.3.3' -notin $usages) {
      throw 'The selected certificate must have the Code Signing EKU.'
    }
    $arguments += @('/sha1', $certificate.Thumbprint, '/s', 'My')
    if ($store -eq 'LocalMachine') { $arguments += '/sm' }
  } else {
    foreach ($path in @($env:REGISTRATIONADMIN_SIGN_DLIB, $env:REGISTRATIONADMIN_SIGN_METADATA)) {
      if (-not [IO.Path]::IsPathRooted($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'Artifact Signing dlib and metadata must be existing absolute file paths.'
      }
    }
    try { $metadata = Get-Content -LiteralPath $env:REGISTRATIONADMIN_SIGN_METADATA -Raw | ConvertFrom-Json }
    catch { throw 'Artifact Signing metadata must be valid JSON.' }
    if (-not $metadata.Endpoint -or -not $metadata.CodeSigningAccountName -or -not $metadata.CertificateProfileName) {
      throw 'Artifact Signing metadata requires Endpoint, CodeSigningAccountName, and CertificateProfileName.'
    }
    $arguments += @('/dlib', (Resolve-Path -LiteralPath $env:REGISTRATIONADMIN_SIGN_DLIB).Path,
      '/dmdf', (Resolve-Path -LiteralPath $env:REGISTRATIONADMIN_SIGN_METADATA).Path)
  }
  # Velopack substitutes and quotes {{file}}. Only non-secret configuration enters this template.
  $parts = @($tool) + $arguments
  foreach ($part in $parts) {
    if ($part.Contains('"') -or $part.Contains("`r") -or $part.Contains("`n")) { throw 'Signing configuration cannot contain quotes or newlines.' }
  }
  $template = (($parts | ForEach-Object { '"' + $_ + '"' }) -join ' ') + ' {{file}}'
  [pscustomobject]@{ Tool = $tool; Template = $template; Thumbprint = $(if ($certificate) { $certificate.Thumbprint } else { $null }) }
}

function Assert-ReleaseSignature {
  param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$SignTool, [string]$Thumbprint)
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required signed binary is missing: $Path" }
  & $SignTool verify /pa /all /tw $Path
  if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed (including timestamp warnings): $Path" }
  $signature = Get-AuthenticodeSignature -LiteralPath $Path
  if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate -or $signature.SignatureType -ne 'Authenticode') {
    throw "A valid embedded, trusted, timestamped signature is required: $Path"
  }
  if ($Thumbprint -and $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
    throw "Unexpected signing certificate: $Path"
  }
}

function Expand-ReleaseArchive {
  param([string]$Path, [string]$Destination)
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $Path).Path, $Destination)
}
