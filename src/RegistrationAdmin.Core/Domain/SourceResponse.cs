namespace RegistrationAdmin.Core.Domain;

/// <summary>原始回應工作表的唯讀內容。</summary>
/// <param name="Headers">第一列標題。</param>
/// <param name="Rows">資料列（第 2 列起）。</param>
public sealed record SourceTable(int SheetId, string SheetTitle, IReadOnlyList<string> Headers, IReadOnlyList<SourceRow> Rows);

/// <param name="RowNumber">工作表列號（1 起算，含標題列）。只作提示，不是永久鍵。</param>
/// <param name="Formatted">FORMATTED_VALUE 讀到的文字。</param>
/// <param name="Unformatted">UNFORMATTED_VALUE＋SERIAL_NUMBER 讀到的值（時間戳為日期序號）。</param>
public sealed record SourceRow(int RowNumber, IReadOnlyList<string> Formatted, IReadOnlyList<object?> Unformatted);

/// <summary>一筆依邏輯欄位解析後的原始表單回應。</summary>
public sealed class SourceResponse
{
    public required int RowNumber { get; init; }

    /// <summary>試算表日期序號（未格式化）。</summary>
    public required double? TimestampSerial { get; init; }

    /// <summary>時間戳的顯示文字（yyyy-MM-dd HH:mm:ss），由序號換算；無序號時為原文字。</summary>
    public required string TimestampText { get; init; }

    public required IReadOnlyDictionary<string, string> Answers { get; init; }

    public required IReadOnlyDictionary<string, bool> OptionFlags { get; init; }

    public required string NormalizedEmail { get; init; }

    public required string NormalizedPhone { get; init; }

    public required string MealCode { get; init; }

    public required string MealOtherText { get; init; }

    public required IReadOnlyList<string> Channels { get; init; }

    public required string SourceKey { get; init; }

    public required string Fingerprint { get; init; }

    public DateTime? SubmittedAt => TimestampSerial is double s ? DateTime.FromOADate(s) : null;

    public string Get(string logicalField) => Answers.TryGetValue(logicalField, out var v) ? v : "";

    public bool Flag(string logicalField) => OptionFlags.TryGetValue(logicalField, out var v) && v;
}
