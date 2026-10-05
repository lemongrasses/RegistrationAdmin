using System.Runtime.Versioning;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
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

    /// <summary>只使用保存的授權恢復登入；沒有授權時回傳 null，絕不開啟瀏覽器。</summary>
    Task<SheetsService?> TryRestoreAsync(CancellationToken cancellationToken) => Task.FromResult<SheetsService?>(null);

    /// <summary>刪除這台電腦保存的登入資料（不影響其他電腦的登入）。</summary>
    Task SignOutAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Google Desktop OAuth：系統瀏覽器＋loopback redirect。登入要求 spreadsheets、openid、email，
/// 選擇器的 drive.file 授權由 GoogleSpreadsheetPicker 獨立處理。Refresh token 以 DPAPI 保存。
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

    public async Task<SheetsService> GetAsync(CancellationToken cancellationToken) =>
        (await GetCoreAsync(interactive: true, cancellationToken).ConfigureAwait(false))!;

    public Task<SheetsService?> TryRestoreAsync(CancellationToken cancellationToken) =>
        GetCoreAsync(interactive: false, cancellationToken);

    private async Task<SheetsService?> GetCoreAsync(bool interactive, CancellationToken cancellationToken)
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

            var store = new DpapiDataStore(_settings.TokenDirectory);
            var savedToken = interactive ? null : await store.GetAsync<TokenResponse>(UserKey).ConfigureAwait(false);
            if (!interactive && savedToken is null) return null;
            cancellationToken.ThrowIfCancellationRequested();

            var secretPath = _settings.ClientSecretPath;
            if (string.IsNullOrWhiteSpace(secretPath) || !File.Exists(secretPath))
            {
                throw new FileNotFoundException("找不到 Google OAuth 用戶端檔（google-oauth-client.json）。此檔應隨程式一起發佈；請聯絡維護人員，或在登入封面的「登入設定（維護者）」選擇檔案。");
            }

            ClientSecrets secrets;
            await using (var stream = File.OpenRead(secretPath))
            {
                secrets = (await GoogleClientSecrets.FromStreamAsync(stream, cancellationToken).ConfigureAwait(false)).Secrets;
            }

            // 恢復登入不呼叫 AuthorizationBroker，避免過期或撤銷的授權自動開啟瀏覽器。
            var credential = interactive ? await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                Scopes,
                UserKey,
                cancellationToken,
                store).ConfigureAwait(false) : new UserCredential(new GoogleAuthorizationCodeFlow(
                    new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = secrets, Scopes = Scopes, DataStore = store }),
                    UserKey, savedToken!);

            // 每次建立工作階段都向 Google 確認登入仍有效，不只信任本機保存的 token。
            var accessToken = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException("無法確認 Google 登入。請檢查網路後重試；若授權已失效，請清除保存的登入資料再登入。");
            }
            using var identity = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!identity.RootElement.TryGetProperty("sub", out var subject) || string.IsNullOrWhiteSpace(subject.GetString())
                || !identity.RootElement.TryGetProperty("email", out var email) || string.IsNullOrWhiteSpace(email.GetString()))
            {
                throw new InvalidOperationException("Google 未提供帳號資訊。請清除保存的登入資料後重新登入並允許帳號授權。");
            }
            AccountEmail = email.GetString();
            _service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });
            return _service;
        }
        catch (TokenResponseException ex) when (!interactive && ex.Error?.Error == "invalid_grant")
        {
            await new DpapiDataStore(_settings.TokenDirectory).DeleteAsync<TokenResponse>(UserKey).ConfigureAwait(false);
            throw new InvalidOperationException("Google 登入已失效，請重新登入。", ex);
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
            // 只刪除這台電腦的 token，不向 Google 撤銷：撤銷會移除整個授權，
            // 讓其他電腦上同一個 Google 帳號的登入一起失效。要全面停用請到 Google 帳戶「第三方應用程式」移除。
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

    public void Dispose()
    {
        _service?.Dispose();
        _gate.Dispose();
    }
}
