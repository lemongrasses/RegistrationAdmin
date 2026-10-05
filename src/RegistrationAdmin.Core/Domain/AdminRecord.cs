using System.Globalization;

namespace RegistrationAdmin.Core.Domain;

/// <summary>_Admin 工作表的欄位名稱與順序。</summary>
public static class AdminColumns
{
    // 識別與連結
    public const string RecordId = "record_id";
    public const string RegistrationNo = "registration_no";
    public const string ProfileId = "profile_id";
    public const string SourceKey = "source_key";
    public const string SourceTimestamp = "source_timestamp";
    public const string SourceEmailSnapshot = "source_email_snapshot";
    public const string SourceRowHint = "source_row_hint";
    public const string SourceFingerprint = "source_fingerprint";
    public const string SourceChanged = "source_changed";

    // 審核與名額
    public const string MembershipStatus = "membership_status";
    public const string MembershipReference = "membership_reference";
    public const string MembershipNotes = "membership_notes";
    public const string EligibilityStatus = "eligibility_status";
    public const string EligibilityNotes = "eligibility_notes";
    public const string RegistrationStatus = "registration_status";
    public const string ReviewNotes = "review_notes";
    public const string ReviewedAt = "reviewed_at";
    public const string SeatReservedAt = "seat_reserved_at";
    public const string WaitlistPosition = "waitlist_position";
    public const string CancelledAt = "cancelled_at";
    public const string CancellationReason = "cancellation_reason";

    // 付款
    public const string ExpectedAmount = "expected_amount";
    public const string PaymentStatus = "payment_status";
    public const string PaymentDeadlineAt = "payment_deadline_at";
    public const string PayerName = "payer_name";
    public const string BankName = "bank_name";
    public const string AccountLast5 = "account_last5";
    public const string PaymentReference = "payment_reference";
    public const string PaymentReportedAt = "payment_reported_at";
    public const string PaidAt = "paid_at";
    public const string VerifiedAt = "verified_at";
    public const string FinanceNotes = "finance_notes";

    // 發票
    public const string InvoiceStatus = "invoice_status";
    public const string InvoiceNumber = "invoice_number";
    public const string InvoiceIssuedAt = "invoice_issued_at";
    public const string InvoiceNotes = "invoice_notes";

    // 非破壞式修正
    public const string OverrideName = "override_name";
    public const string OverrideOrganization = "override_organization";
    public const string OverrideJobTitle = "override_job_title";
    public const string OverrideEmail = "override_email";
    public const string OverridePhone = "override_phone";
    public const string OverrideInvoiceTitle = "override_invoice_title";
    public const string OverrideTaxId = "override_tax_id";
    public const string OverrideMealCode = "override_meal_code";
    public const string MealOtherText = "meal_other_text";
    public const string DietaryNotes = "dietary_notes";

    // 系統
    public const string DuplicateStatus = "duplicate_status";
    public const string DuplicateOfRecordId = "duplicate_of_record_id";
    public const string AcceptedIssueCodes = "accepted_issue_codes";
    public const string IssueNotes = "issue_notes";
    public const string AdminNotes = "admin_notes";
    public const string IsArchived = "is_archived";
    public const string RowVersion = "row_version";
    public const string CreatedAt = "created_at";
    public const string UpdatedAt = "updated_at";

    public static readonly IReadOnlyList<string> All = new[]
    {
        RecordId, RegistrationNo, ProfileId, SourceKey, SourceTimestamp, SourceEmailSnapshot, SourceRowHint, SourceFingerprint, SourceChanged,
        MembershipStatus, MembershipReference, MembershipNotes, EligibilityStatus, EligibilityNotes, RegistrationStatus, ReviewNotes, ReviewedAt,
        SeatReservedAt, WaitlistPosition, CancelledAt, CancellationReason,
        ExpectedAmount, PaymentStatus, PaymentDeadlineAt, PayerName, BankName, AccountLast5, PaymentReference, PaymentReportedAt, PaidAt, VerifiedAt, FinanceNotes,
        InvoiceStatus, InvoiceNumber, InvoiceIssuedAt, InvoiceNotes,
        OverrideName, OverrideOrganization, OverrideJobTitle, OverrideEmail, OverridePhone, OverrideInvoiceTitle, OverrideTaxId, OverrideMealCode, MealOtherText, DietaryNotes,
        DuplicateStatus, DuplicateOfRecordId, AcceptedIssueCodes, IssueNotes, AdminNotes, IsArchived, RowVersion, CreatedAt, UpdatedAt,
    };

    /// <summary>由系統維護、不算使用者修改的欄位。</summary>
    public static readonly IReadOnlySet<string> SystemMaintained = new HashSet<string>(StringComparer.Ordinal)
    {
        RowVersion, UpdatedAt, CreatedAt, SourceRowHint,
    };

    /// <summary>可能含個資或付款識別的欄位。異動紀錄只記欄名、不記值。</summary>
    public static readonly IReadOnlySet<string> Sensitive = new HashSet<string>(StringComparer.Ordinal)
    {
        SourceEmailSnapshot, MembershipReference, PayerName, AccountLast5, PaymentReference, BankName,
        OverrideName, OverrideEmail, OverridePhone, OverrideInvoiceTitle, OverrideTaxId,
    };

    /// <summary>可由使用者在畫面編輯的日期時間欄位。</summary>
    public static readonly IReadOnlyList<string> UserDateFields = new[]
    {
        PaymentDeadlineAt, PaymentReportedAt, PaidAt, VerifiedAt, InvoiceIssuedAt,
    };
}

/// <summary>
/// _Admin 的一列：只保存與原始回應的連結、後台狀態與修正值，不複製原始答案。
/// 以欄名為鍵的字典保存，可保留 Sheets 上使用者自行加入的額外欄位。
/// </summary>
public sealed class AdminRecord
{
    private readonly Dictionary<string, string> _values;

    public AdminRecord()
    {
        _values = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private AdminRecord(Dictionary<string, string> values, int? rowNumber)
    {
        _values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        RowNumber = rowNumber;
    }

    /// <summary>本次讀取時在 _Admin 的列號（只作寫入定位，不是永久鍵）。</summary>
    public int? RowNumber { get; set; }

    public IReadOnlyDictionary<string, string> Values => _values;

    public string this[string column]
    {
        get => _values.TryGetValue(column, out var v) ? v : "";
        set => _values[column] = value ?? "";
    }

    public string RecordId => this[AdminColumns.RecordId];
    public string RegistrationNo => this[AdminColumns.RegistrationNo];
    public string SourceKey => this[AdminColumns.SourceKey];
    public string SourceFingerprint => this[AdminColumns.SourceFingerprint];
    public string RegistrationStatus => this[AdminColumns.RegistrationStatus];
    public string MembershipStatus => this[AdminColumns.MembershipStatus];
    public string EligibilityStatus => this[AdminColumns.EligibilityStatus];
    public string PaymentStatus => this[AdminColumns.PaymentStatus];
    public string InvoiceStatus => this[AdminColumns.InvoiceStatus];
    public string DuplicateStatus => this[AdminColumns.DuplicateStatus];
    public bool IsArchived => SheetBool.Parse(this[AdminColumns.IsArchived]);

    public int RowVersion =>
        int.TryParse(this[AdminColumns.RowVersion], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public IReadOnlySet<string> AcceptedIssueCodes =>
        this[AdminColumns.AcceptedIssueCodes]
            .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    public AdminRecord Clone() => new(_values, RowNumber);

    /// <summary>與另一筆比較，回傳值不同的欄名（以兩邊欄名聯集計算，空字串視同缺欄）。</summary>
    public IReadOnlyList<string> DiffColumns(AdminRecord other)
    {
        return _values.Keys.Union(other._values.Keys, StringComparer.Ordinal)
            .Where(k => !string.Equals(this[k], other[k], StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    public static AdminRecord FromValues(IReadOnlyDictionary<string, string> values, int? rowNumber)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in values)
        {
            dict[k] = v ?? "";
        }

        return new AdminRecord(dict, rowNumber);
    }
}

/// <summary>寫入 _Admin 的要求。</summary>
/// <param name="ExpectedRowVersion">寫入前雲端 row_version 必須等於此值；null 代表不檢查（僅限系統中繼資料）。</param>
/// <param name="OnlyColumns">只寫入這些欄位；null 代表整列。</param>
public sealed record AdminRowUpdate(AdminRecord Record, int? ExpectedRowVersion, IReadOnlyList<string>? OnlyColumns = null);
