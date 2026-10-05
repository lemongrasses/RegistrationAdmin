using System.Runtime.Versioning;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Util.Store;

namespace RegistrationAdmin.GoogleSheets.Auth;

/// <summary>連線設定（由 App 的本機設定提供）。</summary>
public interface IConnectionSettings
{
    /// <summary>Google Cloud Desktop App OAuth client 的 JSON 檔路徑。</summary>
    string ClientSecretPath { get; }

    string SpreadsheetId { get; }

    /// <summary>加密 token 的保存資料夾（%LOCALAPPDATA% 下）。</summary>
    string TokenDirectory { get; }
}

public interface ISheetsServiceProvider
{
    bool IsAuthorized { get; }

    /// <summary>目前授權的 Google 帳號 Email（取不到時為 null）。</summary>
    string? AccountEmail { get; }

    /// <summary>取得已授權的 SheetsService；第一次會開啟系統瀏覽器完成 Google OAuth。</summary>
    Task<SheetsService> GetAsync(CancellationToken cancellationToken);

    Task SignOutAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Google Desktop OAuth：系統瀏覽器＋loopback redirect。只要求 spreadsheets scope，
/// 不要求 Drive 權限。Refresh token 以 DPAPI 保存。
/// 注意：spreadsheets scope 在 Google 端可讀寫該帳號有權存取的所有試算表；
/// 程式只操作設定的 Spreadsheet ID。若要在 Google 端也縮到單一檔案，請用只被分享此檔的專用帳號授權。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GoogleSheetsAuthorizer : ISheetsServiceProvider, IDisposable
{
    public const string ApplicationName = "RegistrationAdmin";
    private const string UserKey = "registration-admin";
    private static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets, "openid", "email" };

    private readonly IConnectionSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SheetsService? _service;

    public GoogleSheetsAuthorizer(IConnectionSettings settings)
    {
        _settings = settings;
    }

    public bool IsAuthorized => _service is not null;

    public string? AccountEmail { get; private set; }

    public async Task<SheetsService> GetAsync(CancellationToken cancellationToken)
    {
        if (_service is not null)
        {
            return _service;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_service is not null)
            {
                return _service;
            }

            var secretPath = _settings.ClientSecretPath;
            if (string.IsNullOrWhiteSpace(secretPath) || !File.Exists(secretPath))
            {
                throw new FileNotFoundException("找不到 Google OAuth 用戶端檔（google-oauth-client.json）。此檔應隨程式一起發佈；請聯絡維護人員，或在「設定與診斷 → 進階」選擇檔案。");
            }

            ClientSecrets secrets;
            await using (var stream = File.OpenRead(secretPath))
            {
                secrets = (await GoogleClientSecrets.FromStreamAsync(stream, cancellationToken).ConfigureAwait(false)).Secrets;
            }

            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                Scopes,
                UserKey,
                cancellationToken,
                new DpapiDataStore(_settings.TokenDirectory)).ConfigureAwait(false);

            AccountEmail = ReadEmail(credential.Token.IdToken);
            _service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });
            return _service;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_service?.HttpClientInitializer is UserCredential credential)
            {
                try
                {
                    await credential.RevokeTokenAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 撤銷失敗（例如離線）仍清除本機 token。
                }
            }

            _service?.Dispose();
            _service = null;
            AccountEmail = null;
            IDataStore store = new DpapiDataStore(_settings.TokenDirectory);
            await store.ClearAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>從 Google 回傳的 id_token 取出 email（只用於畫面顯示，不做簽章驗證）。</summary>
    private static string? ReadEmail(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: >= 2 })
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _service?.Dispose();
        _gate.Dispose();
    }
}
