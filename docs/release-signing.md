# Windows signed releases

The existing WPF/.NET 10, self-contained single-file `win-x64` application is packaged with the pinned Velopack 1.2.161 CLI. `scripts/publish.ps1` uses Velopack's supported `--signTemplate` mechanism to invoke SignTool at the application and Setup packaging stages. There is no new installer or update architecture. OAuth handling and the GitHub update code are unchanged.

The update repository remains **https://github.com/lemongrasses/RegistrationAdmin-releases**, configured in `config/update-feed.local.txt`. Source code lives in the separate RegistrationAdmin repository. Do not replace, re-sign, or upload assets to v0.2.0–v0.2.3. The script rejects uploads for these versions. This change does not bump `Directory.Build.props`; explicitly select `-Version 0.2.4` for the next release.

## Prerequisites

- Windows x64, .NET 10 SDK, and the repository's local Velopack tool (`dotnet tool restore`).
- The existing Google Desktop OAuth JSON at `config/google-oauth-client.json` (or the existing single `client_secret_*.json` fallback). Do not change OAuth settings for signing.
- Windows SDK **x64 SignTool**, an Internet connection for RFC3161 timestamps and trust/revocation verification, and normal Windows public CA trust. Set `REGISTRATIONADMIN_SIGNTOOL` to an existing absolute `signtool.exe` path, or put it on PATH.
- One of the production providers below. No signing account/certificate is supplied by this repository.

## Provider: publicly trusted OV certificate

Obtain a public code-signing certificate in the publisher's legal name from a public CA. Provision its private key using the CA's supported hardware token/HSM and Windows CSP/KSP; unlock it on the signing machine using the provider's secure mechanism. The script selects an exact certificate in the Windows Personal (`My`) store, never a password on a command line. Providers needing raw PIN/password arguments are not supported by this implementation: configure secure provider authentication instead.

Set process environment variables in your signing shell (the placeholders below must be replaced):

```powershell
$env:REGISTRATIONADMIN_SIGNTOOL = 'C:\absolute\SDK\x64\signtool.exe'
$env:REGISTRATIONADMIN_SIGN_CERT_THUMBPRINT = '<40-hex certificate thumbprint>'
$env:REGISTRATIONADMIN_SIGN_CERT_STORE = 'CurrentUser' # default; or LocalMachine
$env:REGISTRATIONADMIN_SIGN_TIMESTAMP_URL = 'http://timestamp.digicert.com' # default; or your CA RFC3161 endpoint
```

The certificate must be valid, have an accessible private key and the Code Signing EKU. Verification also checks that each executable was signed with this exact certificate. SignTool's `/sha1` selects the certificate by its thumbprint; **the file digest is SHA-256**, not SHA-1.

## Provider: Microsoft Artifact Signing

First confirm that the publisher is eligible for the service. Complete identity validation, create an account and **Public Trust** certificate profile, and assign the signing identity the **Artifact Signing Certificate Profile Signer** role. Private Trust and test profiles are unsuitable for public distribution.

Install the supported Artifact Signing client tools: current x64 Windows SDK SignTool, matching x64 `Azure.CodeSigning.Dlib.dll` and dependencies, .NET 8 runtime and Visual C++ runtime as required by Microsoft. The application's self-contained .NET 10 deployment does not install the signing machine's .NET 8 dependency.

Create metadata **outside the repository**, or use ignored `config/artifact-signing.local.json`:

```json
{
  "Endpoint": "<your account's regional HTTPS endpoint>",
  "CodeSigningAccountName": "<your actual account name>",
  "CertificateProfileName": "<your actual Public Trust certificate profile>"
}
```

```powershell
$env:REGISTRATIONADMIN_SIGNTOOL = 'C:\absolute\SDK\x64\signtool.exe'
$env:REGISTRATIONADMIN_SIGN_DLIB = 'C:\absolute\client\x64\Azure.CodeSigning.Dlib.dll'
$env:REGISTRATIONADMIN_SIGN_METADATA = 'C:\absolute\private\metadata.json'
# Default for ArtifactSigning: http://timestamp.acs.microsoft.com
```

Authentication uses the client's `DefaultAzureCredential`. For an interactive signing workstation, use `az login` with the authorized identity; follow Microsoft's metadata `ExcludeCredentials` guidance to select the intended credential source. CI can use managed/workload identity, or secret-manager injected `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, and `AZURE_CLIENT_SECRET`. Never put secret values in scripts, metadata, Git, command arguments, transcripts, or build logs. The release script does not read or print authentication tokens. Missing authentication/permissions fails at signing; file-path preflight alone does not prove the account is usable.

## Local commands (from the repository root)

Intentional unsigned development package, with no GitHub token or signing credential required:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Version 0.2.4 -NoUpload
```

Local production signed build using OV:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Version 0.2.4 -SigningProvider CertificateStore -NoUpload
```

Local production signed build using Artifact Signing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Version 0.2.4 -SigningProvider ArtifactSigning -NoUpload
```

`-ExecutionPolicy Bypass` follows the repository's existing process-local script invocation; it does not change the machine's persistent execution policy. Use your organization's approved script execution policy where required. Never disable SmartScreen, Defender, UAC, MOTW or other Windows security checks.

Use `-ArtifactsDirectory artifacts/signing-validation` to isolate test packaging from the normal artifacts directory. Only `artifacts` or its subdirectories are accepted. `-NoUpload` skips feed downloads and uploads entirely, so local validation produces a full update package without a delta. Clear ambient `VPK_SIGN_PARAMS`, `VPK_SIGN_TEMPLATE`, and `VPK_AZURE_TRUSTED_SIGN_FILE`; the script requires explicit provider configuration rather than potentially secret-bearing ambient signing arguments. Unsigned upload is deliberately rejected.

## Portable ZIP and verification gate

Previously, `RegistrationAdmin-win-x64.zip` was created **before** `vpk pack`. Velopack signs a staging copy, so merely adding signing to `vpk pack` would leave that ZIP's application unsigned.

For signed builds, the script now extracts the newly signed full nupkg, verifies `lib/app/RegistrationAdmin.exe`, copies the packaged application binaries back to the publish directory, and only then creates the original standalone ZIP. It retains the standalone layout and its existing lack of automatic updates. The separate Velopack `RegistrationAdminApp-win-Portable.zip` also contains signed executable payloads. **ZIPs and nupkgs are containers, not Authenticode-signed files**; their executable contents are signed. Delta packages contain patch data, not independently signable EXEs; they reconstruct the already signed target binaries.

Before any feed upload, `verify-release.ps1` requires these files and verifies every EXE in the current full package and both portable ZIPs, including Velopack execution stubs, `Squirrel.exe` (the packaged updater) and portable `Update.exe`, as well as the published app and final Setup. It runs `signtool verify /pa /all /tw`, rejects **all nonzero exit codes, including warnings**, and requires `Get-AuthenticodeSignature` to report a valid embedded signature and timestamp certificate. Signing uses `/fd SHA256 /tr <RFC3161 endpoint> /td SHA256`. Missing timestamps, invalid trust, absent required files, signing or verification failures stop the script before upload. Downloaded older packages used as delta bases are not re-signed or subjected to the new-version signature gate.

Run the same full gate manually:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-release.ps1 -Version 0.2.4
# For OV, also supply -Thumbprint $env:REGISTRATIONADMIN_SIGN_CERT_THUMBPRINT
# For isolated validation, also supply -ArtifactsDirectory artifacts/signing-validation
```

Individual checks (each SignTool exit code must be zero):

```powershell
& $env:REGISTRATIONADMIN_SIGNTOOL verify /pa /all /tw /v .\artifacts\RegistrationAdmin-win-x64\RegistrationAdmin.exe
if ($LASTEXITCODE -ne 0) { throw 'Application signature verification failed' }
& $env:REGISTRATIONADMIN_SIGNTOOL verify /pa /all /tw /v .\artifacts\installer\RegistrationAdminApp-win-Setup.exe
if ($LASTEXITCODE -ne 0) { throw 'Setup signature verification failed' }
Get-AuthenticodeSignature .\artifacts\RegistrationAdmin-win-x64\RegistrationAdmin.exe | Format-List Status,SignerCertificate,TimeStamperCertificate
Get-AuthenticodeSignature .\artifacts\installer\RegistrationAdminApp-win-Setup.exe | Format-List Status,SignerCertificate,TimeStamperCertificate
```

On an Artifact Signing build, personally check that the certificate subject matches your approved publisher; certificates rotate, so do not pin their short-lived thumbprints. Trust verification follows the signing machine's Windows trust policy. Use a clean, normally configured Windows machine to confirm public trust; do not install private/self-signed roots into end-user trusted stores.

## Publishing v0.2.4 (publisher action only)

1. Review the changes; provision the chosen provider and secure authentication. Confirm the expected publisher name, public trust chain and timestamp.
2. Run `dotnet test RegistrationAdmin.slnx -c Release`, then the signed `-NoUpload` command and full verification gate above. Test the installer on a clean Windows VM, the standalone ZIP, Google OAuth and update from v0.2.3. The first actual signed build still needs this validation; unsigned tests do not establish signing success.
3. Confirm `config/update-feed.local.txt` points to `https://github.com/lemongrasses/RegistrationAdmin-releases` and that v0.2.4 does not already exist. Set the existing `REGISTRATIONADMIN_GITHUB_TOKEN` securely (process or user environment) with Contents read/write permission for that releases repository only. Do not print it.
4. Only when ready, rerun the chosen production command **without `-NoUpload`**. For OV:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Version 0.2.4 -SigningProvider CertificateStore
   ```

   For Artifact Signing, replace `CertificateStore` with `ArtifactSigning`. This rebuilds, downloads the existing latest release as a delta base, signs/packages/verifies, and then uses the existing Velopack GitHub upload workflow to publish the new tag. Do not manually upload failed/unsigned build outputs. Do not rerun an upload against an already published tag or replace older assets.

5. Download the new Setup from the releases repository, verify it again and test the v0.2.3-to-v0.2.4 update. If separately distributing `RegistrationAdmin-win-x64.zip`, use only the ZIP from the successful signed build; the existing Velopack upload workflow does not automatically upload that additional standalone ZIP.

## SmartScreen and limits

Public signing provides verified publisher identity and tamper detection. New signed downloads can still show SmartScreen warnings until reputation accumulates; signing does not guarantee immediate reputation or Defender approval. Keep all normal Windows security checks enabled. Existing unsigned releases are unchanged. No production credentials are available in this checkout, so actual provider authentication, signing, timestamps, public trust on a clean end-user machine and signed delta reconstruction remain publisher validation steps.

References: [Velopack signing](https://docs.velopack.io/packaging/signing), [Microsoft Artifact Signing integration prerequisites and metadata](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations), [SignTool options and return codes](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool), [Microsoft SmartScreen reputation guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).
