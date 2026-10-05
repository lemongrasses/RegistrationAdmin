using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RegistrationAdmin.Core.Identity;

/// <summary>
/// 原始回應識別：
/// source_key = SHA-256(profile_id + 時間戳序號 + 標準化 Email + 標準化電話)；
/// source_fingerprint = SHA-256(依欄名排序後的全部答案)，用來偵測原始回答被人工修改。
/// </summary>
public static class SourceIdentity
{
    private const char FieldSeparator = '\u001F';
    private const char RecordSeparator = '\u001E';

    /// <summary>時間戳序號以 invariant "R" 格式序列化；沒有序號時退回原文字並加前綴區分。</summary>
    public static string TimestampToken(double? serial, string formattedFallback) =>
        serial is double s
            ? s.ToString("R", CultureInfo.InvariantCulture)
            : "TEXT:" + (formattedFallback ?? "").Trim();

    public static string ComputeSourceKey(string profileId, string timestampToken, string normalizedEmail, string normalizedPhone)
    {
        var payload = string.Join(FieldSeparator, profileId, timestampToken, normalizedEmail, normalizedPhone);
        return Sha256Hex(payload);
    }

    public static string ComputeFingerprint(string timestampToken, IEnumerable<KeyValuePair<string, string>> answers)
    {
        var sb = new StringBuilder();
        sb.Append("ts=").Append(timestampToken);
        foreach (var (field, value) in answers.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            sb.Append(RecordSeparator).Append(field).Append('=').Append((value ?? "").Trim());
        }

        return Sha256Hex(sb.ToString());
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
