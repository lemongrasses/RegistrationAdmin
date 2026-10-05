using System.Globalization;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Sync;

namespace RegistrationAdmin.Core.Abstractions;

/// <summary>一次完整讀取的試算表內容。</summary>
public sealed record WorkbookSnapshot(
    EventConfig Config,
    IReadOnlyList<FieldMapping> FieldMappings,
    IReadOnlyList<LookupItem> Lookups,
    SourceTable Source,
    IReadOnlyList<AdminRecord> AdminRows);

/// <summary>
/// 試算表存取。刻意沒有任何寫入原始回應工作表的方法：
/// 只能寫 _Admin 與 _ChangeLog。
/// </summary>
public interface IRegistrationStore
{
    Task<WorkbookSnapshot> LoadAsync(CancellationToken cancellationToken);

    Task AppendAdminRowsAsync(IReadOnlyList<AdminRecord> rows, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken);

    /// <summary>寫入前重讀 row_version；不一致時丟出 <see cref="ConcurrencyConflictException"/>，整批不寫。</summary>
    Task UpdateAdminRowsAsync(IReadOnlyList<AdminRowUpdate> updates, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken);
}

/// <summary>選用：讀取單筆報名的 _ChangeLog（詳細資料「異動摘要」使用）。</summary>
public interface IChangeLogReader
{
    Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(string recordId, CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public interface IIdGenerator
{
    string NewId();
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public string NewId() => Guid.NewGuid().ToString("D");
}

/// <summary>以 Asia/Taipei 為準的時鐘。</summary>
public sealed class TaipeiClock : IClock
{
    public static TimeZoneInfo TimeZone { get; } = ResolveTimeZone();

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZone);

    private static TimeZoneInfo ResolveTimeZone()
    {
        foreach (var id in new[] { "Asia/Taipei", "Taipei Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Asia/Taipei", TimeSpan.FromHours(8), "Asia/Taipei", "Asia/Taipei");
    }
}

public static class TimeFormat
{
    private static readonly string[] UserFormats =
    {
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd", "yyyy/M/d", "yyyy/MM/dd HH:mm", "yyyy/M/d HH:mm",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss",
    };

    public static string Iso(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string Display(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>管理者輸入的日期／日期時間是否可解析。</summary>
    public static bool IsValidUserDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return DateTimeOffset.TryParseExact(value.Trim(), UserFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out _);
    }
}

public class RegistrationAdminException : Exception
{
    public RegistrationAdminException(string message) : base(message)
    {
    }

    public RegistrationAdminException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>雲端 row_version 與載入時不同：可能有其他人或其他電腦修改。不自動覆寫。</summary>
public sealed class ConcurrencyConflictException : RegistrationAdminException
{
    public ConcurrencyConflictException(IReadOnlyList<string> recordIds)
        : base("雲端資料在您載入後已被修改（row_version 不同），本次未寫入。請重新整理後再編輯。")
    {
        RecordIds = recordIds;
    }

    public IReadOnlyList<string> RecordIds { get; }
}

public sealed class SchemaHealthException : RegistrationAdminException
{
    public SchemaHealthException(SchemaHealthResult health) : base(health.Describe())
    {
        Health = health;
    }

    public SchemaHealthResult Health { get; }
}

public sealed class SchemaNotInitializedException : RegistrationAdminException
{
    public SchemaNotInitializedException(string message) : base(message)
    {
    }
}

public sealed class BusinessRuleException : RegistrationAdminException
{
    public BusinessRuleException(IReadOnlyList<ValidationMessage> messages)
        : base(string.Join(Environment.NewLine, messages.Select(m => m.Message)))
    {
        Messages = messages;
    }

    public IReadOnlyList<ValidationMessage> Messages { get; }
}
