namespace RegistrationAdmin.Core.Domain;

/// <summary>_FieldMap 的一列：原始回應工作表標題 → 邏輯欄位。</summary>
/// <param name="SourceHeader">
/// 回應工作表的實際標題。可用「|」列出明確允許的替代標題（例如「時間戳記|Timestamp」），
/// 程式不做模糊猜測。
/// </param>
/// <param name="OptionMatch">
/// normalizer_code 為 boolean_option 時，儲存格內容必須包含的選項文字；
/// 用於一題多個 Checkbox 選項（例如資料使用同意與三日全勤同意在同一欄）。
/// </param>
public sealed record FieldMapping(
    string ProfileId,
    string LogicalField,
    string SourceHeader,
    string ResponseType,
    bool Required,
    string NormalizerCode,
    string ValidationCode,
    string OptionMatch,
    bool Active,
    string SchemaVersion)
{
    public static readonly IReadOnlyList<string> SheetColumns = new[]
    {
        "profile_id", "logical_field", "source_header", "response_type", "required",
        "normalizer_code", "validation_code", "option_match", "active", "schema_version",
    };

    public IReadOnlyList<string> HeaderAlternatives =>
        SourceHeader.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IReadOnlyList<string> ToSheetRow() => new[]
    {
        ProfileId, LogicalField, SourceHeader, ResponseType, SheetBool.Format(Required),
        NormalizerCode, ValidationCode, OptionMatch, SheetBool.Format(Active), SchemaVersion,
    };

    public static FieldMapping FromSheetRow(IReadOnlyDictionary<string, string> row)
    {
        string Get(string key) => row.TryGetValue(key, out var v) ? v.Trim() : "";
        return new FieldMapping(
            Get("profile_id"),
            Get("logical_field"),
            Get("source_header"),
            Get("response_type"),
            SheetBool.Parse(Get("required")),
            Get("normalizer_code"),
            Get("validation_code"),
            Get("option_match"),
            SheetBool.Parse(Get("active"), defaultValue: true),
            Get("schema_version"));
    }
}

public static class ResponseTypes
{
    public const string Text = "text";
    public const string Single = "single";
    public const string Multi = "multi";
    public const string DateTime = "datetime";
}

public static class NormalizerCodes
{
    public const string None = "";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string BooleanOption = "boolean_option";
    public const string Meal = "meal";
    public const string MultiChoice = "multi_choice";
    public const string TaxId = "tax_id";
}

/// <summary>Google Sheets 內布林值的讀寫規則：寫 TRUE／FALSE，讀時接受常見變體。</summary>
public static class SheetBool
{
    public static string Format(bool value) => value ? "TRUE" : "FALSE";

    public static bool Parse(string? value, bool defaultValue = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "T" or "Y" or "YES" or "1" or "是" => true,
            "FALSE" or "F" or "N" or "NO" or "0" or "否" => false,
            _ => defaultValue,
        };
    }
}
