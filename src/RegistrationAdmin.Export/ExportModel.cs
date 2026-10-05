using System.Globalization;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;

namespace RegistrationAdmin.Export;

public enum ExportValueMode
{
    /// <summary>有效值：override 有值則優先。</summary>
    Effective = 0,

    /// <summary>原始表單回答。</summary>
    Raw = 1,
}

public enum ExportFormat
{
    Xlsx = 0,
    Csv = 1,
}

public sealed record ExportContext(ExportValueMode Mode, LookupCatalog Lookups)
{
    public string Field(Registration r, string logicalField) =>
        Mode == ExportValueMode.Raw ? r.Raw(logicalField) : r.Effective(logicalField);
}

/// <param name="IsText">以文字保存（registration_no、電話、統編等），避免前導零消失。</param>
/// <param name="IsSensitive">聯絡、統編或付款識別；未勾選「包含敏感欄位」時不輸出。</param>
/// <param name="IsApplicantNotes">申請人備註與得知管道；一般審核名單可選擇不帶。</param>
public sealed record ExportColumn(
    string Key,
    string Header,
    Func<Registration, ExportContext, string> Value,
    bool IsText = false,
    bool IsSensitive = false,
    bool IsApplicantNotes = false);

public static class ExportColumns
{
    public static readonly IReadOnlyList<ExportColumn> All = new[]
    {
        new ExportColumn("registration_no", "報名編號", (r, _) => r.RegistrationNo, IsText: true),
        new ExportColumn("submitted_at", "報名時間", (r, _) => r.Source?.TimestampText ?? r.Admin[AdminColumns.SourceTimestamp]),
        new ExportColumn("registration_status", "報名狀態", (r, c) => c.Lookups.Label(LookupDomains.RegistrationStatus, r.Admin.RegistrationStatus)),
        new ExportColumn("full_name", "姓名", (r, c) => c.Field(r, LogicalFields.FullName)),
        new ExportColumn("organization", "服務／所屬單位", (r, c) => c.Field(r, LogicalFields.OrganizationName)),
        new ExportColumn("job_title", "職稱", (r, c) => c.Field(r, LogicalFields.JobTitle)),
        new ExportColumn("email", "Email", (r, c) => c.Field(r, LogicalFields.Email), IsSensitive: true),
        new ExportColumn("phone", "聯絡電話", (r, c) => c.Field(r, LogicalFields.Phone), IsText: true, IsSensitive: true),
        new ExportColumn("invoice_title", "發票抬頭", (r, c) => c.Field(r, LogicalFields.InvoiceTitle)),
        new ExportColumn("tax_id", "統一編號", (r, c) => c.Field(r, LogicalFields.TaxId), IsText: true, IsSensitive: true),
        new ExportColumn("meal", "午餐", (r, c) => c.Mode == ExportValueMode.Raw
            ? r.Raw(LogicalFields.Meal)
            : c.Lookups.Label(LookupDomains.MealCode, r.EffectiveMealCode())),
        new ExportColumn("meal_other", "午餐其他說明", (r, c) => c.Mode == ExportValueMode.Raw ? r.Source?.MealOtherText ?? "" : r.EffectiveMealOtherText()),
        new ExportColumn("dietary_notes", "飲食備註", (r, _) => r.Admin[AdminColumns.DietaryNotes]),
        new ExportColumn("membership_status", "會員核對", (r, c) => c.Lookups.Label(LookupDomains.MembershipStatus, r.Admin.MembershipStatus)),
        new ExportColumn("membership_reference", "會員識別參考", (r, _) => r.Admin[AdminColumns.MembershipReference], IsText: true, IsSensitive: true),
        new ExportColumn("eligibility_status", "資格", (r, c) => c.Lookups.Label(LookupDomains.EligibilityStatus, r.Admin.EligibilityStatus)),
        new ExportColumn("review_notes", "審核備註", (r, _) => r.Admin[AdminColumns.ReviewNotes]),
        new ExportColumn("waitlist_position", "候補順位", (r, _) => r.Admin[AdminColumns.WaitlistPosition]),
        new ExportColumn("cancellation_reason", "取消原因", (r, _) => r.Admin[AdminColumns.CancellationReason]),
        new ExportColumn("expected_amount", "應付金額", (r, _) => r.Admin[AdminColumns.ExpectedAmount]),
        new ExportColumn("payment_status", "付款狀態", (r, c) => c.Lookups.Label(LookupDomains.PaymentStatus, r.Admin.PaymentStatus)),
        new ExportColumn("payment_deadline_at", "付款期限", (r, _) => r.Admin[AdminColumns.PaymentDeadlineAt]),
        new ExportColumn("payer_name", "匯款人", (r, _) => r.Admin[AdminColumns.PayerName], IsSensitive: true),
        new ExportColumn("bank_name", "銀行", (r, _) => r.Admin[AdminColumns.BankName], IsSensitive: true),
        new ExportColumn("account_last5", "帳號末五碼", (r, _) => r.Admin[AdminColumns.AccountLast5], IsText: true, IsSensitive: true),
        new ExportColumn("payment_reference", "付款參考", (r, _) => r.Admin[AdminColumns.PaymentReference], IsText: true, IsSensitive: true),
        new ExportColumn("payment_reported_at", "回報匯款時間", (r, _) => r.Admin[AdminColumns.PaymentReportedAt]),
        new ExportColumn("paid_at", "付款日期", (r, _) => r.Admin[AdminColumns.PaidAt]),
        new ExportColumn("verified_at", "核帳日期", (r, _) => r.Admin[AdminColumns.VerifiedAt]),
        new ExportColumn("finance_notes", "財務備註", (r, _) => r.Admin[AdminColumns.FinanceNotes]),
        new ExportColumn("invoice_status", "發票狀態", (r, c) => c.Lookups.Label(LookupDomains.InvoiceStatus, r.Admin.InvoiceStatus)),
        new ExportColumn("invoice_number", "發票號碼", (r, _) => r.Admin[AdminColumns.InvoiceNumber], IsText: true),
        new ExportColumn("invoice_issued_at", "發票開立日", (r, _) => r.Admin[AdminColumns.InvoiceIssuedAt]),
        new ExportColumn("invoice_notes", "發票備註", (r, _) => r.Admin[AdminColumns.InvoiceNotes]),
        new ExportColumn("applicant_notes", "申請人備註", (r, _) => r.Raw(LogicalFields.ApplicantNotes), IsApplicantNotes: true),
        new ExportColumn("acquisition_channels", "得知管道", (r, _) => string.Join("、", r.Source?.Channels ?? Array.Empty<string>()), IsApplicantNotes: true),
        new ExportColumn("admin_notes", "內部備註", (r, _) => r.Admin[AdminColumns.AdminNotes]),
        new ExportColumn("open_issues", "待處理問題", (r, _) => string.Join(",", r.Issues.Where(i => !i.Accepted).Select(i => i.Code).Distinct())),
    };

    private static readonly Dictionary<string, ExportColumn> ByKey = All.ToDictionary(c => c.Key, StringComparer.Ordinal);

    public static ExportColumn Get(string key) =>
        ByKey.TryGetValue(key, out var c) ? c : throw new ArgumentException($"未知的匯出欄位：{key}", nameof(key));
}

public sealed record ExportTemplate(
    string Id,
    string Name,
    Func<Registration, bool> Include,
    IReadOnlyList<string> ColumnKeys,
    bool IncludeApplicantNotesByDefault = false,
    bool IncludeMealSummary = false,
    Func<IEnumerable<Registration>, IEnumerable<Registration>>? Order = null)
{
    public override string ToString() => Name;
}

public static class ExportTemplates
{
    private static readonly HashSet<string> Inactive = new(StringComparer.Ordinal)
    {
        RegistrationStatus.Rejected, RegistrationStatus.Cancelled, RegistrationStatus.PaymentExpired,
    };

    private static bool Is(Registration r, params string[] statuses) => statuses.Contains(r.Admin.RegistrationStatus, StringComparer.Ordinal);

    public static readonly ExportTemplate AllRegistrations = new(
        "all", "全部報名", _ => true,
        new[]
        {
            "registration_no", "submitted_at", "registration_status", "full_name", "organization", "job_title", "email", "phone",
            "invoice_title", "tax_id", "meal", "meal_other", "dietary_notes", "membership_status", "membership_reference",
            "eligibility_status", "review_notes", "waitlist_position", "cancellation_reason", "expected_amount", "payment_status",
            "paid_at", "verified_at", "invoice_status", "invoice_number", "applicant_notes", "acquisition_channels", "admin_notes",
            "open_issues",
        },
        IncludeApplicantNotesByDefault: true);

    public static readonly ExportTemplate PendingReview = new(
        "pending_review", "待審核",
        r => Is(r, RegistrationStatus.Submitted, RegistrationStatus.UnderReview),
        new[]
        {
            "registration_no", "submitted_at", "registration_status", "full_name", "organization", "job_title", "email", "phone",
            "membership_status", "membership_reference", "eligibility_status", "review_notes", "open_issues",
            "applicant_notes", "acquisition_channels",
        });

    public static readonly ExportTemplate ApprovedPendingPayment = new(
        "approved_pending_payment", "通過待付款",
        r => Is(r, RegistrationStatus.ApprovedPendingPayment),
        new[]
        {
            "registration_no", "full_name", "organization", "email", "phone", "expected_amount", "payment_status",
            "payment_deadline_at", "invoice_title", "tax_id",
        });

    public static readonly ExportTemplate Confirmed = new(
        "confirmed", "已確認",
        r => Is(r, RegistrationStatus.Confirmed),
        new[] { "registration_no", "full_name", "organization", "job_title", "email", "phone", "meal", "meal_other", "dietary_notes" });

    public static readonly ExportTemplate Waitlisted = new(
        "waitlisted", "候補",
        r => Is(r, RegistrationStatus.Waitlisted),
        new[] { "waitlist_position", "registration_no", "submitted_at", "full_name", "organization", "email", "phone", "review_notes" },
        Order: rs => rs.OrderBy(r => int.TryParse(r.Admin[AdminColumns.WaitlistPosition], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : int.MaxValue));

    public static readonly ExportTemplate PaymentCheck = new(
        "payment_check", "付款核對",
        r => Is(r, RegistrationStatus.ApprovedPendingPayment, RegistrationStatus.Confirmed, RegistrationStatus.PaymentExpired),
        new[]
        {
            "registration_no", "registration_status", "full_name", "organization", "expected_amount", "payment_status",
            "payment_deadline_at", "payer_name", "bank_name", "account_last5", "payment_reference", "payment_reported_at",
            "paid_at", "verified_at", "finance_notes",
        });

    public static readonly ExportTemplate Invoice = new(
        "invoice", "發票處理",
        r => Is(r, RegistrationStatus.ApprovedPendingPayment, RegistrationStatus.Confirmed),
        new[]
        {
            "registration_no", "full_name", "organization", "invoice_title", "tax_id", "email", "payment_status",
            "invoice_status", "invoice_number", "invoice_issued_at", "invoice_notes",
        });

    public static readonly ExportTemplate Meal = new(
        "meal", "午餐統計／名單",
        r => !Inactive.Contains(r.Admin.RegistrationStatus),
        new[] { "registration_no", "registration_status", "full_name", "organization", "meal", "meal_other", "dietary_notes", "applicant_notes" },
        IncludeApplicantNotesByDefault: true,
        IncludeMealSummary: true);

    public static readonly IReadOnlyList<ExportTemplate> All = new[]
    {
        AllRegistrations, PendingReview, ApprovedPendingPayment, Confirmed, Waitlisted, PaymentCheck, Invoice, Meal,
    };
}

public sealed class ExportRequest
{
    public required ExportTemplate Template { get; init; }

    /// <summary>目前畫面的篩選結果（依畫面順序）。範本條件會再套用一次。</summary>
    public required IReadOnlyList<Registration> Registrations { get; init; }

    public required LookupCatalog Lookups { get; init; }

    public ExportValueMode Mode { get; init; } = ExportValueMode.Effective;

    public bool IncludeSensitive { get; init; } = true;

    /// <summary>null 代表依範本預設。</summary>
    public bool? IncludeApplicantNotes { get; init; }

    public string FilterSummary { get; init; } = "";

    public string DataVersion { get; init; } = "";

    public DateTimeOffset GeneratedAt { get; init; }
}

public sealed record ExportTable(
    IReadOnlyList<ExportColumn> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<KeyValuePair<string, string>> Metadata,
    IReadOnlyList<KeyValuePair<string, string>> Summary);

public static class ExportTableBuilder
{
    public static ExportTable Build(ExportRequest request)
    {
        var template = request.Template;
        var includeNotes = request.IncludeApplicantNotes ?? template.IncludeApplicantNotesByDefault;
        var columns = template.ColumnKeys
            .Select(ExportColumns.Get)
            .Where(c => request.IncludeSensitive || !c.IsSensitive)
            .Where(c => includeNotes || !c.IsApplicantNotes)
            .ToList();

        IEnumerable<Registration> selected = request.Registrations.Where(template.Include);
        if (template.Order is not null)
        {
            selected = template.Order(selected);
        }

        var list = selected.ToList();
        var context = new ExportContext(request.Mode, request.Lookups);
        var rows = list
            .Select(r => (IReadOnlyList<string>)columns.Select(c => c.Value(r, context) ?? "").ToList())
            .ToList();

        var metadata = new List<KeyValuePair<string, string>>
        {
            new("產生時間", TimeFormat.Iso(request.GeneratedAt)),
            new("範本", template.Name),
            new("篩選條件", request.FilterSummary),
            new("資料版本", request.DataVersion),
            new("值", request.Mode == ExportValueMode.Raw ? "原始值" : "有效值（修正值優先）"),
            new("筆數", rows.Count.ToString(CultureInfo.InvariantCulture)),
            new("敏感欄位", request.IncludeSensitive ? "包含" : "不含"),
        };

        var summary = new List<KeyValuePair<string, string>>();
        if (template.IncludeMealSummary)
        {
            foreach (var group in list.GroupBy(r => r.EffectiveMealCode(), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var label = group.Key.Length == 0 ? "（未填）" : request.Lookups.Label(LookupDomains.MealCode, group.Key);
                summary.Add(new(label, group.Count().ToString(CultureInfo.InvariantCulture)));
            }

            summary.Add(new("合計", list.Count.ToString(CultureInfo.InvariantCulture)));
        }

        return new ExportTable(columns, rows, metadata, summary);
    }
}
