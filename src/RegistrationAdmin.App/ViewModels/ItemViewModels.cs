using CommunityToolkit.Mvvm.ComponentModel;
using RegistrationAdmin.App.Presentation;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Search;
using RegistrationAdmin.Core.Sync;

namespace RegistrationAdmin.App.ViewModels;

public sealed record OptionItem(string Code, string Label)
{
    public override string ToString() => Label;
}

public sealed record IssueFilterOption(IssueFilter Value, string Label);

public sealed record LabeledValue(string Label, string Value);

public sealed record SummaryItem(string Label, string Value, string Hint = "");

/// <summary>下拉選單的選項（由 _Lookups 產生）。</summary>
public sealed class LookupOptions
{
    private LookupOptions(LookupCatalog catalog)
    {
        Catalog = catalog;
        RegistrationStatuses = Build(LookupDomains.RegistrationStatus);
        MembershipStatuses = Build(LookupDomains.MembershipStatus);
        EligibilityStatuses = Build(LookupDomains.EligibilityStatus);
        PaymentStatuses = Build(LookupDomains.PaymentStatus);
        InvoiceStatuses = Build(LookupDomains.InvoiceStatus);
        MealCodes = Build(LookupDomains.MealCode);
        MealOverrideCodes = WithBlank(MealCodes, "（不修正，使用原始回應）");
        DuplicateStatuses = WithBlank(Build(LookupDomains.DuplicateStatus), "未判定");

        FilterRegistrationStatuses = WithBlank(RegistrationStatuses, "全部");
        FilterMembershipStatuses = WithBlank(MembershipStatuses, "全部");
        FilterEligibilityStatuses = WithBlank(EligibilityStatuses, "全部");
        FilterPaymentStatuses = WithBlank(PaymentStatuses, "全部");
        FilterInvoiceStatuses = WithBlank(InvoiceStatuses, "全部");
        FilterMealCodes = WithBlank(MealCodes, "全部");
    }

    public LookupCatalog Catalog { get; }

    public IReadOnlyList<OptionItem> RegistrationStatuses { get; }
    public IReadOnlyList<OptionItem> MembershipStatuses { get; }
    public IReadOnlyList<OptionItem> EligibilityStatuses { get; }
    public IReadOnlyList<OptionItem> PaymentStatuses { get; }
    public IReadOnlyList<OptionItem> InvoiceStatuses { get; }
    public IReadOnlyList<OptionItem> MealCodes { get; }
    public IReadOnlyList<OptionItem> MealOverrideCodes { get; }
    public IReadOnlyList<OptionItem> DuplicateStatuses { get; }

    public IReadOnlyList<OptionItem> FilterRegistrationStatuses { get; }
    public IReadOnlyList<OptionItem> FilterMembershipStatuses { get; }
    public IReadOnlyList<OptionItem> FilterEligibilityStatuses { get; }
    public IReadOnlyList<OptionItem> FilterPaymentStatuses { get; }
    public IReadOnlyList<OptionItem> FilterInvoiceStatuses { get; }
    public IReadOnlyList<OptionItem> FilterMealCodes { get; }

    public IReadOnlyList<IssueFilterOption> IssueFilters { get; } = new[]
    {
        new IssueFilterOption(IssueFilter.Any, "全部"),
        new IssueFilterOption(IssueFilter.HasError, "有錯誤"),
        new IssueFilterOption(IssueFilter.HasErrorOrWarning, "有錯誤或警告"),
        new IssueFilterOption(IssueFilter.HasDuplicate, "疑似重複"),
        new IssueFilterOption(IssueFilter.NoOpenIssues, "無待處理問題"),
    };

    public IReadOnlyList<OptionItem> SeverityFilters { get; } = new[]
    {
        new OptionItem("", "全部"),
        new OptionItem("error", "錯誤"),
        new OptionItem("warning", "警告"),
        new OptionItem("info", "提示"),
    };

    public static LookupOptions From(LookupCatalog catalog) => new(catalog);

    public string Label(string domain, string code) => Catalog.Label(domain, code);

    private IReadOnlyList<OptionItem> Build(string domain) =>
        Catalog.Options(domain).Select(i => new OptionItem(i.Code, i.LabelZh.Length > 0 ? i.LabelZh : i.Code)).ToList();

    private static IReadOnlyList<OptionItem> WithBlank(IEnumerable<OptionItem> items, string blankLabel) =>
        new[] { new OptionItem("", blankLabel) }.Concat(items).ToList();
}

/// <summary>清單的一列（快照；同步或儲存後重建）。</summary>
public sealed class RegistrationRowViewModel
{
    public RegistrationRowViewModel(Registration registration, LookupOptions options, DateTime now)
    {
        Registration = registration;
        var admin = registration.Admin;
        RecordId = registration.RecordId;
        RegistrationNo = registration.RegistrationNo;
        SubmittedAtValue = registration.Source?.SubmittedAt;
        SubmittedAt = SubmittedAtValue is { } t ? FriendlyTime.Format(t, now) : admin[AdminColumns.SourceTimestamp];
        Name = FriendlyTime.OrBlank(registration.Effective(LogicalFields.FullName));
        Organization = FriendlyTime.OrBlank(registration.Effective(LogicalFields.OrganizationName));
        JobTitle = registration.Effective(LogicalFields.JobTitle);
        Email = registration.Effective(LogicalFields.Email);
        Phone = registration.Effective(LogicalFields.Phone);
        SubLine = string.Join(" · ", new[] { RegistrationNo, SubmittedAt }.Where(s => s.Length > 0));
        Status = options.Label(LookupDomains.RegistrationStatus, admin.RegistrationStatus.Length == 0 ? RegistrationStatus.Submitted : admin.RegistrationStatus);
        StatusTone = StatusTones.Registration(admin.RegistrationStatus);
        Membership = options.Label(LookupDomains.MembershipStatus, admin.MembershipStatus.Length == 0 ? MembershipStatus.Unchecked : admin.MembershipStatus);
        MembershipTone = StatusTones.Membership(admin.MembershipStatus);
        Eligibility = options.Label(LookupDomains.EligibilityStatus, admin.EligibilityStatus);
        Payment = options.Label(LookupDomains.PaymentStatus, admin.PaymentStatus.Length == 0 ? PaymentStatus.NotRequested : admin.PaymentStatus);
        PaymentTone = StatusTones.Payment(admin.PaymentStatus);
        Invoice = options.Label(LookupDomains.InvoiceStatus, admin.InvoiceStatus);
        Meal = options.Label(LookupDomains.MealCode, registration.EffectiveMealCode());
        WaitlistPosition = admin[AdminColumns.WaitlistPosition];
        Problems = WorkQueue.Problems(registration);
        ProblemSummary = WorkQueue.ProblemSummary(Problems);
        ProblemDetail = Problems.Count == 0 ? "沒有需要處理的問題" : string.Join(Environment.NewLine, Problems.Select(p => "• " + p));
        HasError = WorkQueue.HasOpenError(registration);
        HasProblems = Problems.Count > 0;
        IsArchived = admin.IsArchived;
        IsOrphan = registration.IsOrphan;
        if (IsArchived)
        {
            Status += "（已封存）";
        }
    }

    public Registration Registration { get; }
    public string RecordId { get; }
    public string RegistrationNo { get; }
    public DateTime? SubmittedAtValue { get; }
    public string SubmittedAt { get; }
    public string Name { get; }
    public string SubLine { get; }
    public string Organization { get; }
    public string JobTitle { get; }
    public string Email { get; }
    public string Phone { get; }
    public string Status { get; }
    public ChipTone StatusTone { get; }
    public string Membership { get; }
    public ChipTone MembershipTone { get; }
    public string Eligibility { get; }
    public string Payment { get; }
    public ChipTone PaymentTone { get; }
    public string Invoice { get; }
    public string Meal { get; }
    public string WaitlistPosition { get; }
    public IReadOnlyList<string> Problems { get; }
    public string ProblemSummary { get; }
    public string ProblemDetail { get; }
    public bool HasProblems { get; }
    public bool HasError { get; }
    public bool IsArchived { get; }
    public bool IsOrphan { get; }

    /// <summary>螢幕閱讀器朗讀的列內容。</summary>
    public override string ToString() => $"{Name}，{Status}，會員{Membership}，{Payment}，{ProblemSummary}";
}

/// <summary>摘要卡。</summary>
public sealed record SummaryCard(string Title, string Value, string Caption);

/// <summary>詳細頁左側的一個原始欄位。</summary>
public sealed record RawField(string Label, string Value, bool IsBlank, string Corrected = "", string Warning = "");

public sealed record RawGroup(string Title, IReadOnlyList<RawField> Fields);

public sealed record ConsentItem(string Label, bool Checked)
{
    public string Text => (Checked ? "✓ 已勾選：" : "✗ 未勾選：") + Label;
}

/// <summary>詳細頁上的一個待處理問題。</summary>
public sealed class RecordIssueViewModel
{
    public RecordIssueViewModel(Issue issue, Registration registration)
    {
        Issue = issue;
        Registration = registration;
        Title = WorkQueue.ShortIssue(issue);
        Detail = WorkQueue.LongIssue(issue);
        Tone = issue.Severity == IssueSeverity.Error ? ChipTone.Danger : ChipTone.Warning;
        SeverityLabel = issue.Severity == IssueSeverity.Error ? "需要修正" : "請確認";
    }

    public Issue Issue { get; }
    public Registration Registration { get; }
    public string Title { get; }
    public string Detail { get; }
    public ChipTone Tone { get; }
    public string SeverityLabel { get; }
    public bool IsSourceChange => Issue.Code == IssueCodes.SourceChanged;

    /// <summary>可以「確認沒問題」略過的問題；結構性問題與來源變更另有處理方式。</summary>
    public bool CanAccept =>
        !Issue.Accepted
        && Issue.Code is not (IssueCodes.SourceKeyCollision or IssueCodes.Orphan or IssueCodes.PossibleRelink
            or IssueCodes.AdminDuplicateKey or IssueCodes.SourceChanged);
}

public sealed record HistoryItem(string When, string What);

public sealed class IssueRowViewModel
{
    public IssueRowViewModel(Issue issue, Registration? registration)
    {
        Issue = issue;
        Registration = registration;
        SeverityLabel = issue.Severity switch
        {
            IssueSeverity.Error => "錯誤",
            IssueSeverity.Warning => "警告",
            _ => "提示",
        };
        RegistrationNo = issue.RegistrationNo;
        Name = registration?.Effective(LogicalFields.FullName) ?? "";
        SourceRow = issue.SourceRowNumber?.ToString() ?? "";
        FieldLabel = LogicalFields.Label(issue.Field);
        AcceptedText = issue.Accepted ? "已接受例外" : "";
    }

    public Issue Issue { get; }
    public Registration? Registration { get; }
    public string SeverityLabel { get; }
    public string RegistrationNo { get; }
    public string Name { get; }
    public string SourceRow { get; }
    public string FieldLabel { get; }
    public string Code => Issue.Code;
    public string Message => Issue.Message;
    public string AcceptedText { get; }

    /// <summary>結構性問題（碰撞、找不到來源、待重新連結）不能以「接受例外」略過。</summary>
    public bool CanAccept =>
        Registration is not null
        && !Issue.Accepted
        && Issue.Code is not (IssueCodes.SourceKeyCollision or IssueCodes.Orphan or IssueCodes.PossibleRelink or IssueCodes.AdminDuplicateKey);
}

public sealed class PendingRelinkItem
{
    public PendingRelinkItem(PendingRelink pending)
    {
        Pending = pending;
        var s = pending.Source;
        Description = $"原始回應第 {s.RowNumber} 列｜{s.TimestampText}｜{s.Get(LogicalFields.FullName)}｜{s.Get(LogicalFields.OrganizationName)}";
        Candidates = pending.Candidates.Select(c => new OrphanItem(c)).ToList();
    }

    public PendingRelink Pending { get; }
    public string Description { get; }
    public IReadOnlyList<OrphanItem> Candidates { get; }
}

public sealed class OrphanItem
{
    public OrphanItem(AdminRecord admin)
    {
        Admin = admin;
        Description = $"{admin.RegistrationNo}｜原時間 {admin[AdminColumns.SourceTimestamp]}｜{admin.RegistrationStatus}";
    }

    public AdminRecord Admin { get; }
    public string Description { get; }
}

/// <summary>以欄名索引的可繫結工作副本：{Binding Edit[review_notes]}。</summary>
public sealed class EditableRecord : ObservableObject
{
    public EditableRecord(AdminRecord record)
    {
        Record = record;
    }

    public event EventHandler<string>? FieldChanged;

    public AdminRecord Record { get; }

    public string this[string column]
    {
        get => Record[column];
        set
        {
            var next = value ?? "";
            if (string.Equals(Record[column], next, StringComparison.Ordinal))
            {
                return;
            }

            Record[column] = next;
            OnPropertyChanged("Item[]");
            FieldChanged?.Invoke(this, column);
        }
    }

    public void RaiseAll() => OnPropertyChanged("Item[]");
}

public sealed class OverrideFieldViewModel : ObservableObject
{
    private readonly EditableRecord _edit;

    public OverrideFieldViewModel(string label, string rawValue, string column, EditableRecord edit)
    {
        Label = label;
        RawValue = rawValue;
        Column = column;
        _edit = edit;
    }

    public string Label { get; }
    public string RawValue { get; }
    public string Column { get; }

    public string Value
    {
        get => _edit[Column];
        set
        {
            _edit[Column] = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>欄位旁的錯誤訊息：{Binding Errors[paid_at]}。</summary>
public sealed class FieldErrors : ObservableObject
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    public string this[string field] => _errors.TryGetValue(field, out var text) ? text : "";

    public int Count => _errors.Count;

    public void Set(IEnumerable<(string Field, string Message)> errors)
    {
        _errors.Clear();
        foreach (var (field, message) in errors)
        {
            _errors[field] = _errors.TryGetValue(field, out var existing) ? existing + "\n" + message : message;
        }

        OnPropertyChanged("Item[]");
        OnPropertyChanged(nameof(Count));
    }

    public void Clear() => Set(Array.Empty<(string, string)>());
}
