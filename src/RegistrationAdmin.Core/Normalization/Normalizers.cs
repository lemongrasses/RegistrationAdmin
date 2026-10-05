using System.Text;
using System.Text.RegularExpressions;
using RegistrationAdmin.Core.Domain;

namespace RegistrationAdmin.Core.Normalization;

/// <summary>
/// 比對用正規化。正規化值只用於比對與驗證，畫面與匯出仍保留原始值。
/// </summary>
public static partial class Normalizers
{
    private static readonly HashSet<char> PhoneSeparators = new() { ' ', '-', '(', ')', '.', '　', '\t' };

    /// <summary>NFKC（全形轉半形）並去除前後空白。</summary>
    public static string Nfkc(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.Normalize(NormalizationForm.FormKC).Trim();

    /// <summary>Email：去除前後空白、轉小寫。不套用 Gmail 去點或加號規則。</summary>
    public static string Email(string? value) => Nfkc(value).ToLowerInvariant();

    /// <summary>電話：只移除明確允許的分隔符（空白、-、括號、點），不推測國碼。</summary>
    public static string Phone(string? value)
    {
        var s = Nfkc(value);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!PhoneSeparators.Contains(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>姓名、單位比對鍵：NFKC、移除所有空白、轉小寫。</summary>
    public static string NameKey(string? value)
    {
        var s = Nfkc(value);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    public static bool IsValidEmail(string? value)
    {
        var s = Email(value);
        return s.Length > 0 && EmailRegex().IsMatch(s);
    }

    /// <summary>電話是否像有效號碼：8–15 位數字，可有開頭 + 與 # 分機。</summary>
    public static bool IsPlausiblePhone(string? value)
    {
        var s = Phone(value);
        return s.Length > 0 && PhoneRegex().IsMatch(s);
    }

    public static bool IsTaxId8Digits(string? value) => TaxIdRegex().IsMatch(Nfkc(value));

    /// <summary>Checkbox／單選題是否勾選了包含 optionMatch 文字的選項。</summary>
    public static bool OptionSelected(string? cell, string? optionMatch)
    {
        var text = Nfkc(cell);
        if (text.Length == 0)
        {
            return false;
        }

        var match = Nfkc(optionMatch);
        return match.Length == 0 || text.Contains(match, StringComparison.Ordinal);
    }

    /// <summary>
    /// 午餐：葷食／素食／不需午餐為標準選項；其他與自由文字一律為 other，
    /// 並保留去除「其他：」前綴後的文字。
    /// </summary>
    public static (string Code, string OtherText) Meal(string? raw)
    {
        var s = Nfkc(raw);
        if (s.Length == 0)
        {
            return ("", "");
        }

        switch (s)
        {
            case "葷食":
                return (MealCode.Meat, "");
            case "素食":
                return (MealCode.Vegetarian, "");
            case "不需午餐":
            case "不需":
                return (MealCode.None, "");
        }

        if (s.StartsWith("其他", StringComparison.Ordinal))
        {
            var rest = s["其他".Length..].TrimStart(':', ' ', '、', ',').Trim();
            return (MealCode.Other, rest);
        }

        return (MealCode.Other, s);
    }

    /// <summary>Google Forms 複選答案以「, 」串接。</summary>
    public static IReadOnlyList<string> MultiChoice(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
        {
            return Array.Empty<string>();
        }

        return s.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^\+?\d{8,15}(#\d{1,6})?$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^\d{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex TaxIdRegex();
}

/// <summary>工作表標題比對用正規化：NFKC、連續空白合併、去除前後空白。</summary>
public static partial class HeaderText
{
    public static string Normalize(string? header)
    {
        var s = Normalizers.Nfkc(header);
        return Whitespace().Replace(s, " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
