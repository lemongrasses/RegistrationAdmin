using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;

namespace RegistrationAdmin.GoogleSheets.Auth;

public sealed record PickedSpreadsheet(string Id, string Title);

public interface ISpreadsheetPicker
{
    Task<PickedSpreadsheet?> PickAsync(string accountEmail, CancellationToken cancellationToken);
}

/// <summary>Google 官方桌面 Picker：預設瀏覽器＋本機回呼，獨立 drive.file 授權不覆蓋 Sheets 登入。</summary>
public sealed class GoogleSpreadsheetPicker : ISpreadsheetPicker
{
    private readonly IConnectionSettings _settings;
    private readonly Action<string> _openBrowser;
    private readonly Func<HttpClient> _createClient;

    public GoogleSpreadsheetPicker(IConnectionSettings settings) : this(settings,
        url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }),
        () => new HttpClient { Timeout = TimeSpan.FromSeconds(30) }) { }

    internal GoogleSpreadsheetPicker(IConnectionSettings settings, Action<string> openBrowser, Func<HttpClient> createClient)
    {
        _settings = settings;
        _openBrowser = openBrowser;
        _createClient = createClient;
    }
    public const string Scope = "https://www.googleapis.com/auth/drive.file";
    public const string SpreadsheetMimeType = "application/vnd.google-apps.spreadsheet";

    public async Task<PickedSpreadsheet?> PickAsync(string accountEmail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountEmail))
            throw new InvalidOperationException("請先登入 Google，再選擇試算表。");
        await using var secretStream = File.OpenRead(_settings.ClientSecretPath);
        var secrets = (await GoogleClientSecrets.FromStreamAsync(secretStream, cancellationToken)).Secrets;
        using var callback = new PickerCallbackListener();
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var url = BuildAuthorizationUrl(secrets.ClientId, accountEmail, callback.RedirectUri, state, verifier);
        _openBrowser(url);
        var result = await callback.ReceiveAsync(state, cancellationToken);
        if (result is null) return null;

        using var client = _createClient();
        using var tokenResponse = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = secrets.ClientId, ["client_secret"] = secrets.ClientSecret,
                ["code"] = result.Code, ["code_verifier"] = verifier,
                ["redirect_uri"] = callback.RedirectUri, ["grant_type"] = "authorization_code",
            }), cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("Google 選擇器授權失敗，請重新選擇。維護者請確認 Google Picker API 已啟用。也可以改用貼上網址。");
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        var accessToken = token.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("Google 未提供選擇器授權，請重新選擇。");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var about = await ReadDriveAsync(client, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)", cancellationToken);
        var pickedAccount = about.RootElement.GetProperty("user").GetProperty("emailAddress").GetString();
        if (!string.Equals(accountEmail, pickedAccount, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("選擇器使用的 Google 帳號與程式登入帳號不同。請重新選擇並使用相同帳號，或先更換程式登入帳號。");
        using var file = await ReadDriveAsync(client,
            $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(result.FileId)}?supportsAllDrives=true&fields=name,mimeType,trashed,capabilities(canEdit)", cancellationToken);
        var data = file.RootElement;
        if (data.GetProperty("mimeType").GetString() != SpreadsheetMimeType || data.GetProperty("trashed").GetBoolean())
            throw new InvalidOperationException("請選擇未放入垃圾桶的 Google 試算表，Excel 檔案需要先轉成 Google 試算表。");
        if (!data.GetProperty("capabilities").GetProperty("canEdit").GetBoolean())
            throw new InvalidOperationException("目前帳號沒有這份試算表的編輯權限，請選擇其他試算表或請擁有者分享編輯權限。");
        return new PickedSpreadsheet(result.FileId, data.GetProperty("name").GetString() ?? "Google 試算表");
    }

    private static async Task<JsonDocument> ReadDriveAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("無法確認選取的試算表。維護者請確認 Google Drive API 與 Google Picker API 已啟用，並確認帳號有編輯權限。也可以改用貼上網址。");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    internal static string BuildAuthorizationUrl(string clientId, string email, string redirect, string state, string verifier)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = Scope, ["prompt"] = "consent", ["trigger_onepick"] = "true",
            ["mimetypes"] = SpreadsheetMimeType, ["allow_multiple"] = "false", ["include_granted_scopes"] = "false",
            ["login_hint"] = email, ["state"] = state, ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
        };
        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", query.Select(kv =>
            Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record PickerCallback(string Code, string FileId);

/// <summary>僅監聽隨機 loopback 連接埠；取消與例外時立即釋放，不需要系統管理員 HTTP URL ACL。</summary>
internal sealed class PickerCallbackListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _path = "/picker/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + "/";

    public PickerCallbackListener()
    {
        _listener.Start();
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{_path}";
    }

    public string RedirectUri { get; }

    public async Task<PickerCallback?> ReceiveAsync(string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var connection = await _listener.AcceptTcpClientAsync(ct);
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var stream = connection.GetStream();
            string? line;
            try { line = await ReadRequestLineAsync(stream, requestTimeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            if (line is null || line.Length > 16384) continue;
            var parts = line.Split(' ');
            if (parts.Length != 3 || parts[0] != "GET" || !parts[1].StartsWith(_path + "?", StringComparison.Ordinal))
            {
                await RespondAsync(stream, "404 Not Found", "請返回程式重新選擇。", ct);
                continue;
            }
            var query = ParseQuery(parts[1][(_path.Length + 1)..]);
            if (!query.TryGetValue("state", out var state) || state != expectedState)
            {
                await RespondAsync(stream, "400 Bad Request", "這次選擇已失效，請返回程式重新選擇。", ct);
                continue;
            }
            await RespondAsync(stream, "200 OK", "已收到選擇結果，請返回活動報名後台。您可以關閉此分頁。", ct);
            return ParseResult(query);
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var headers = new StringBuilder();
        var buffer = new byte[512];
        while (headers.Length < 16384)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) return null;
            headers.Append(Encoding.ASCII.GetString(buffer, 0, count));
            if (headers.Length > 16384) return null;
            var request = headers.ToString();
            if (request.Contains("\r\n\r\n", StringComparison.Ordinal))
                return request[..request.IndexOf("\r\n", StringComparison.Ordinal)];
        }
        return null;
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            if (!result.TryAdd(key, value)) throw new InvalidOperationException("Google 選擇器回傳重複參數，請重新選擇。");
        }
        return result;
    }

    internal static PickerCallback? ParseResult(IReadOnlyDictionary<string, string> query)
    {
        if (query.TryGetValue("error", out var error))
        {
            if (error is "access_denied" or "cancelled" or "canceled") return null;
            throw new InvalidOperationException("Google 選擇器無法完成授權。請重新選擇；維護者請確認 Google Picker API 已啟用，也可以改用貼上網址。");
        }
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)
            || !query.TryGetValue("picked_file_ids", out var id) || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]+\z"))
            throw new InvalidOperationException("Google 未回傳單一試算表，請重新選擇一份 Google 試算表。");
        return new PickerCallback(code, id);
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><meta charset='utf-8'><title>活動報名後台</title><p>" + message + "</p>");
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, ct);
        await stream.WriteAsync(body, ct);
    }

    public void Dispose() => _listener.Stop();
}
