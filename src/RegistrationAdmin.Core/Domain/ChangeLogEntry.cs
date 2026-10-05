namespace RegistrationAdmin.Core.Domain;

/// <summary>
/// _ChangeLog 的一列。只記錄欄名，敏感欄位另列於 sensitive_fields_changed，
/// 任何欄位的值都不寫入異動紀錄。
/// </summary>
public sealed record ChangeLogEntry(
    string ChangeId,
    string CorrelationId,
    string RecordId,
    string Operation,
    string ChangedFields,
    string SensitiveFieldsChanged,
    string ChangedAt,
    string AppVersion,
    string Notes)
{
    public static readonly IReadOnlyList<string> SheetColumns = new[]
    {
        "change_id", "correlation_id", "record_id", "operation", "changed_fields",
        "sensitive_fields_changed", "changed_at", "app_version", "notes",
    };

    public IReadOnlyList<string> ToSheetRow() => new[]
    {
        ChangeId, CorrelationId, RecordId, Operation, ChangedFields, SensitiveFieldsChanged, ChangedAt, AppVersion, Notes,
    };

    public static ChangeLogEntry Create(
        string changeId,
        string correlationId,
        string recordId,
        string operation,
        IEnumerable<string> changedColumns,
        string changedAt,
        string appVersion,
        string notes = "")
    {
        var cols = changedColumns.Where(c => !AdminColumns.SystemMaintained.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
        var normal = cols.Where(c => !AdminColumns.Sensitive.Contains(c));
        var sensitive = cols.Where(c => AdminColumns.Sensitive.Contains(c));
        return new ChangeLogEntry(
            changeId,
            correlationId,
            recordId,
            operation,
            string.Join(",", normal),
            string.Join(",", sensitive),
            changedAt,
            appVersion,
            notes);
    }
}

public static class ChangeOperations
{
    public const string Create = "create";
    public const string Update = "update";
    public const string StatusChange = "status_change";
    public const string Relink = "relink";
    public const string AcceptSourceChange = "accept_source_change";
    public const string AcceptIssue = "accept_issue";
}
