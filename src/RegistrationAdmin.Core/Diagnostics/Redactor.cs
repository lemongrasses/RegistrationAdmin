using System.Text.RegularExpressions;

namespace RegistrationAdmin.Core.Diagnostics;

/// <summary>
/// 日誌／診斷文字遮蔽。日誌本來就只寫數量與代碼；這裡是最後一道防線，
/// 遮蔽 Email、電話與統編類數字、身分證字號及 OAuth token。
/// </summary>
public static partial class Redactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var s = TokenJson().Replace(text, m => m.Groups[1].Value + "\"[token]\"");
        s = GoogleAccessToken().Replace(s, "[token]");
        s = GoogleRefreshToken().Replace(s, "[token]");
        s = Email().Replace(s, "[email]");
        s = NationalId().Replace(s, "[id]");
        s = LongNumber().Replace(s, "[number]");
        return s;
    }

    [GeneratedRegex("(\"(?:access_token|refresh_token|id_token|client_secret|code)\"\\s*:\\s*)\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex TokenJson();

    [GeneratedRegex(@"ya29\.[A-Za-z0-9_\-\.]+")]
    private static partial Regex GoogleAccessToken();

    [GeneratedRegex(@"1//[A-Za-z0-9_\-]{10,}")]
    private static partial Regex GoogleRefreshToken();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Z][12]\d{8}\b")]
    private static partial Regex NationalId();

    /// <summary>連續 8 位以上數字，或以 - 分隔共 9 位以上數字；涵蓋統編與電話，保留 yyyy-MM-dd 日期。</summary>
    [GeneratedRegex(@"(?<!\d)(?:\d{8,}|\+?\d(?:-?\d){8,})(?!\d)")]
    private static partial Regex LongNumber();
}

/// <summary>從 Google Sheets 網址或直接貼上的 ID 取出 Spreadsheet ID。</summary>
public static partial class SpreadsheetIdParser
{
    public static string? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var text = input.Trim();
        var url = UrlPattern().Match(text);
        if (url.Success)
        {
            return url.Groups[1].Value;
        }

        return IdPattern().IsMatch(text) ? text : null;
    }

    [GeneratedRegex(@"/spreadsheets/d/([A-Za-z0-9\-_]+)")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^[A-Za-z0-9\-_]{20,}$")]
    private static partial Regex IdPattern();
}
