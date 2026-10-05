using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;

namespace RegistrationAdmin.Tests.TestData;

/// <summary>
/// 規劃 13.1 的固定測試資料。Email 一律使用保留網域 example.test，電話與統編皆為虛構值。
/// </summary>
internal static class Fixtures
{
    public const string ConsentData = "我已閱讀活動資訊，並同意主辦單位於本活動報名、聯繫、簽到、及活動行政作業範圍內使用本人填寫之資料。";
    public const string ConsentAttendance = "我已充分了解本工作坊為三天連貫課程，並確認我能夠三日全勤出席。";
    public const string ConsentBoth = ConsentData + ", " + ConsentAttendance;
    public const string Fee = "我已了解示範活動工作坊為酌收報名費之活動，後續將依主辦單位通知完成繳費或相關確認程序。";
    public const string Accuracy = "我確認以上資料填寫正確，並了解主辦單位將依名額狀況與資料完整性進行報名確認。";

    public static readonly string[] Headers =
    {
        "時間戳記",
        "請詳閱活動說明後再行勾選",
        "姓名",
        "服務/所屬單位 (如：某大學某學系)",
        "職稱",
        "Email (用於接收報名確認及活動資訊)",
        "聯絡電話(僅供活動聯繫使用)",
        "報名費說明確認",
        "發票抬頭",
        "統一編號",
        "午餐需求",
        "備註 (如：特殊飲食需求等)",
        "您是如何得知本次活動？",
        "送出前確認",
    };

    public static double Serial(int minuteOffset) => new DateTime(2026, 10, 1, 9, 0, 0).AddMinutes(minuteOffset).ToOADate();

    public sealed record Answer(
        string Name,
        string Org,
        string Email,
        string Phone,
        string TaxId,
        string Meal,
        double Serial,
        string Notes = "",
        string Channels = "",
        string Consents = ConsentBoth,
        string FeeText = Fee,
        string AccuracyText = Accuracy,
        string JobTitle = "工程師",
        string? InvoiceTitle = null);

    public static SourceRow Row(int rowNumber, Answer a)
    {
        var formatted = new List<string>
        {
            DateTime.FromOADate(a.Serial).ToString("yyyy/M/d tt h:mm:ss"),
            a.Consents,
            a.Name,
            a.Org,
            a.JobTitle,
            a.Email,
            a.Phone,
            a.FeeText,
            a.InvoiceTitle ?? a.Org,
            a.TaxId,
            a.Meal,
            a.Notes,
            a.Channels,
            a.AccuracyText,
        };
        var unformatted = new List<object?> { a.Serial };
        unformatted.AddRange(formatted.Skip(1));
        return new SourceRow(rowNumber, formatted, unformatted);
    }

    public static Answer A => new("測試甲", "甲公司", "alpha@example.test", "0911000001", "01234567", "葷食", Serial(0), Channels: "電子郵件/EDM");
    public static Answer B => new("測試乙", "乙大學", "beta@example.test", "0911000002", "11111111", "素食", Serial(1), Channels: "單位內部公告");
    public static Answer C => new("測試甲", "丙機構", "gamma@example.test", "0911000003", "22222222", "不需午餐", Serial(2), Channels: "社群媒體 (FB/IG/Line等)");
    public static Answer D => new("測試丁", "甲公司", "delta@example.test", "0911000004", "個人", "其他：不吃牛", Serial(3),
        Notes: "不吃牛肉，其餘皆可", Channels: "同事/朋友推薦, 其他");
    public static Answer E => new("測試戊", "丁公司", "alpha@example.test", "0911000005", "33333333", "葷食", Serial(4));
    public static Answer F => new("測試己", "戊研究所", "foxtrot@example.test", "0911000006", "44444444", "素食", Serial(5));

    public static IReadOnlyList<Answer> AtoE => new[] { A, B, C, D, E };

    public static SourceTable Table(IEnumerable<Answer> answers, string[]? headers = null)
    {
        var rows = answers.Select((a, i) => Row(i + 2, a)).ToList();
        return new SourceTable(123456, "表單回應 1", headers ?? Headers, rows);
    }

    /// <summary>名額測試用 P01–P21，Email 與電話皆唯一。</summary>
    public static IReadOnlyList<Answer> Pool(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Answer(
                $"名額{i:D2}",
                $"名額單位{i:D2}",
                $"p{i:D2}@example.test",
                $"09220000{i:D2}",
                $"5555{i:D4}",
                "葷食",
                Serial(100 + i)))
            .ToList();
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(8));
}

internal sealed class SequentialIds : IIdGenerator
{
    private int _next;

    public string NewId() => $"00000000-0000-0000-0000-{Interlocked.Increment(ref _next):D12}";
}

/// <summary>記憶體內的試算表替身。原始回應只能由測試直接設定，store 本身沒有寫入原始回應的方法。</summary>
internal sealed class FakeStore : IRegistrationStore
{
    private readonly List<AdminRecord> _admin = new();

    public FakeStore(SourceTable source)
    {
        Source = source;
    }

    public SourceTable Source { get; set; }

    public List<ChangeLogEntry> ChangeLog { get; } = new();

    public int AppendCalls { get; private set; }

    public int AppendedRows { get; private set; }

    /// <summary>下一次寫入時模擬網路中斷。</summary>
    public bool FailNextWrite { get; set; }

    public IReadOnlyList<AdminRecord> AdminRows => _admin;

    public Task<WorkbookSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var copies = _admin.Select(a => a.Clone()).ToList();
        return Task.FromResult(new WorkbookSnapshot(
            EventConfig.Default,
            DefaultProfile.FieldMappings,
            DefaultProfile.Lookups,
            Source,
            copies));
    }

    public Task AppendAdminRowsAsync(IReadOnlyList<AdminRecord> rows, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        AppendCalls++;
        foreach (var row in rows)
        {
            var copy = row.Clone();
            copy.RowNumber = _admin.Count + 2;
            row.RowNumber = copy.RowNumber;
            _admin.Add(copy);
            AppendedRows++;
        }

        ChangeLog.AddRange(changeLog);
        return Task.CompletedTask;
    }

    public Task UpdateAdminRowsAsync(IReadOnlyList<AdminRowUpdate> updates, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        var conflicts = new List<string>();
        foreach (var update in updates)
        {
            var current = _admin.Single(a => a.RecordId == update.Record.RecordId);
            if (update.ExpectedRowVersion is int expected && current.RowVersion != expected)
            {
                conflicts.Add(update.Record.RecordId);
            }
        }

        if (conflicts.Count > 0)
        {
            throw new ConcurrencyConflictException(conflicts);
        }

        foreach (var update in updates)
        {
            var index = _admin.FindIndex(a => a.RecordId == update.Record.RecordId);
            if (update.OnlyColumns is null)
            {
                var copy = update.Record.Clone();
                copy.RowNumber = _admin[index].RowNumber;
                _admin[index] = copy;
            }
            else
            {
                foreach (var column in update.OnlyColumns)
                {
                    _admin[index][column] = update.Record[column];
                }
            }
        }

        ChangeLog.AddRange(changeLog);
        return Task.CompletedTask;
    }

    /// <summary>模擬另一台電腦修改了某列。</summary>
    public void TouchFromElsewhere(string recordId)
    {
        var row = _admin.Single(a => a.RecordId == recordId);
        row[AdminColumns.RowVersion] = (row.RowVersion + 1).ToString();
    }

    private void ThrowIfFailing()
    {
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new HttpRequestException("模擬網路中斷");
        }
    }
}
