using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.App.Presentation;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.UseCases;

namespace RegistrationAdmin.App.ViewModels;

/// <summary>
/// 單筆報名的處理頁。所有修改先放在 Working，按「儲存變更」才寫入 Google Sheets；
/// 左側原始答案一律唯讀。
/// </summary>
public sealed partial class RegistrationEditorViewModel : ObservableObject
{
    private static readonly string[] CodedColumns =
    {
        AdminColumns.RegistrationStatus, AdminColumns.MembershipStatus, AdminColumns.EligibilityStatus,
        AdminColumns.PaymentStatus, AdminColumns.InvoiceStatus, AdminColumns.DuplicateStatus, AdminColumns.OverrideMealCode,
    };

    private static readonly string[] ValueColumns =
    {
        AdminColumns.WaitlistPosition, AdminColumns.ExpectedAmount, AdminColumns.PaymentDeadlineAt, AdminColumns.PaymentReportedAt,
        AdminColumns.PaidAt, AdminColumns.VerifiedAt, AdminColumns.InvoiceIssuedAt, AdminColumns.InvoiceNumber,
    };

    private readonly RegistrationWorkspace _workspace;
    private readonly IDialogService _dialogs;
    private readonly Func<DateTime> _today;

    public RegistrationEditorViewModel(
        Registration registration,
        RegistrationWorkspace workspace,
        LookupOptions options,
        IDialogService dialogs,
        Func<DateTime>? today = null,
        bool loadHistory = true)
    {
        Registration = registration;
        _workspace = workspace;
        _dialogs = dialogs;
        _today = today ?? (() => DateTime.Today);
        Options = options;
        Working = registration.Admin.Clone();
        Edit = new EditableRecord(Working);
        Edit.FieldChanged += (_, _) => OnWorkingChanged();

        RawGroups = BuildRawGroups(registration);
        Consents = LogicalFields.Consents.Select(c => new ConsentItem(c.Label, registration.Consent(c.Field))).ToList();
        OverrideFields = Registration.OverridableFields
            .Select(f => new OverrideFieldViewModel(LogicalFields.Label(f), FriendlyTime.OrBlank(registration.Raw(f)), Registration.OverrideColumnFor(f)!, Edit))
            .ToList();
        OpenIssues = registration.Issues.Where(i => !i.Accepted)
            .OrderBy(i => i.Severity)
            .Select(i => new RecordIssueViewModel(i, registration))
            .ToList();
        DuplicateCandidates = BuildDuplicateCandidates(registration, workspace, options);

        RefreshDerived();
        if (loadHistory)
        {
            _ = LoadHistoryAsync();
        }
        else
        {
            HistoryStatus = "";
        }
    }

    public Registration Registration { get; }

    public AdminRecord Working { get; }

    public EditableRecord Edit { get; }

    public LookupOptions Options { get; }

    public FieldErrors Errors { get; } = new();

    // ── 標題 ──
    public string DisplayName => FriendlyTime.OrBlank(Registration.Effective(LogicalFields.FullName, Working));

    public string SubTitle
    {
        get
        {
            var parts = new List<string> { Registration.RegistrationNo };
            if (Registration.Source?.SubmittedAt is { } t)
            {
                parts.Add(FriendlyTime.Format(t, DateTime.Now) + " 送出");
            }

            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    public string StatusLabel => Options.Label(LookupDomains.RegistrationStatus, CurrentStatus);

    public ChipTone StatusTone => StatusTones.Registration(CurrentStatus);

    public bool IsArchived => Working.IsArchived;

    private string CurrentStatus => Working.RegistrationStatus.Length == 0 ? RegistrationStatus.Submitted : Working.RegistrationStatus;

    // ── 左側：原始資料 ──
    public IReadOnlyList<RawGroup> RawGroups { get; }

    public IReadOnlyList<ConsentItem> Consents { get; }

    public bool HasSource => Registration.Source is not null;

    // ── 問題 ──
    public IReadOnlyList<RecordIssueViewModel> OpenIssues { get; }

    public bool HasIssues => OpenIssues.Count > 0;

    public bool HasErrors => OpenIssues.Any(i => i.Issue.Severity == IssueSeverity.Error);

    public string IssueBannerTitle => $"這筆報名的資料有 {OpenIssues.Count} 個問題需要確認";

    public string IssueBannerSummary => string.Join("；", OpenIssues.Select(i => i.Title).Distinct()) + "。";

    // ── 右側：處理 ──
    public IReadOnlyList<OverrideFieldViewModel> OverrideFields { get; }

    public IReadOnlyList<OptionItem> DuplicateCandidates { get; }

    public bool ShowDuplicateSection =>
        DuplicateCandidates.Count > 0 || Working.DuplicateStatus.Length > 0;

    public ObservableCollection<FlowAction> OtherActions { get; } = new();

    public ObservableCollection<string> PendingChanges { get; } = new();

    public ObservableCollection<string> Messages { get; } = new();

    public ObservableCollection<HistoryItem> History { get; } = new();

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrimaryAction))]
    private FlowAction? _primaryAction;

    [ObservableProperty]
    private bool _showPaymentSection;

    [ObservableProperty]
    private string _actionNote = "";

    [ObservableProperty]
    private string _historyStatus = "讀取中…";

    public bool HasPrimaryAction => PrimaryAction is not null;

    public bool HasMessages => Messages.Count > 0;

    public string EffectiveMealText
    {
        get
        {
            var code = Registration.EffectiveMealCode(Working);
            var label = code.Length == 0 ? "尚未填寫" : Options.Label(LookupDomains.MealCode, code);
            var other = Registration.EffectiveMealOtherText(Working);
            return other.Length > 0 ? $"{label}（{other}）" : label;
        }
    }

    // ── 命令 ──

    [RelayCommand]
    private void RunPrimary()
    {
        if (PrimaryAction is { } action)
        {
            Run(action, null);
        }
    }

    [RelayCommand]
    private void RunOther(FlowAction? action)
    {
        if (action is null)
        {
            return;
        }

        string? reason = null;
        if (action.ReasonColumn is not null)
        {
            reason = _dialogs.AskReason(action.Label.TrimEnd('…'), action.Description + "\n\n按「儲存變更」後才會寫入。", action.ReasonLabel,
                action.Label.TrimEnd('…'), danger: true);
            if (reason is null)
            {
                return;
            }
        }
        else if (action.RequiresConfirm
                 && !_dialogs.Confirm(action.Description + "\n\n按「儲存變更」後才會寫入。", action.Label, action.Label, danger: true))
        {
            return;
        }

        Run(action, reason);
    }

    [RelayCommand]
    private void SetToday(string? column)
    {
        if (!string.IsNullOrEmpty(column))
        {
            Edit[column] = _today().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>顯示儲存前／儲存失敗的驗證結果；內容不會被清空。</summary>
    public void ShowValidation(IReadOnlyList<ValidationMessage> messages)
    {
        Messages.Clear();
        foreach (var message in messages)
        {
            Messages.Add(UserMessages.Validation(message));
        }

        Errors.Set(messages.Select(m => (m.Field, UserMessages.Validation(m))));
        OnPropertyChanged(nameof(HasMessages));
    }

    public void ShowError(string message)
    {
        Messages.Clear();
        Messages.Add(message);
        OnPropertyChanged(nameof(HasMessages));
    }

    public void ClearMessages()
    {
        Messages.Clear();
        Errors.Clear();
        OnPropertyChanged(nameof(HasMessages));
    }

    private void Run(FlowAction action, string? reason)
    {
        var before = CurrentStatus;
        var messages = FlowActions.Execute(action, Registration, Working, _workspace, _today(), reason);
        if (messages.Count > 0)
        {
            ActionNote = "";
            ShowValidation(messages);
            return;
        }

        ClearMessages();
        Edit.RaiseAll();
        OnWorkingChanged();
        ActionNote = before != CurrentStatus
            ? $"已改為「{StatusLabel}」，尚未儲存。確認上方列出的變更後，按右上角「儲存變更」。"
            : "已套用，尚未儲存。確認上方列出的變更後，按右上角「儲存變更」。";
    }

    private void OnWorkingChanged()
    {
        IsDirty = Working.DiffColumns(Registration.Admin).Any(c => !AdminColumns.SystemMaintained.Contains(c));
        if (Errors.Count > 0)
        {
            ClearMessages();
        }

        RefreshDerived();
    }

    private void RefreshDerived()
    {
        PrimaryAction = FlowActions.Primary(Working);
        OtherActions.Clear();
        foreach (var action in FlowActions.Others(Working))
        {
            OtherActions.Add(action);
        }

        ShowPaymentSection = CurrentStatus is not (RegistrationStatus.Submitted or RegistrationStatus.UnderReview or RegistrationStatus.Waitlisted)
                             || Working.PaymentStatus is not ("" or PaymentStatus.NotRequested);

        PendingChanges.Clear();
        foreach (var line in DescribeChanges(Registration.Admin, Working, Options, DuplicateCandidates))
        {
            PendingChanges.Add(line);
        }

        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(EffectiveMealText));
        OnPropertyChanged(nameof(IsArchived));
        OnPropertyChanged(nameof(ShowDuplicateSection));
    }

    /// <summary>「儲存後會變更」的清單，給使用者在儲存前確認。</summary>
    public static IReadOnlyList<string> DescribeChanges(AdminRecord original, AdminRecord working, LookupOptions options,
        IReadOnlyList<OptionItem>? duplicateCandidates = null)
    {
        var changed = working.DiffColumns(original)
            .Where(c => !AdminColumns.SystemMaintained.Contains(c) && !FieldLabels.IsSystem(c))
            .OrderBy(c => c == AdminColumns.RegistrationStatus ? -1 : IndexOf(c))
            .ToList();

        var lines = new List<string>();
        foreach (var column in changed)
        {
            var label = FieldLabels.Label(column);
            var before = original[column];
            var after = working[column];
            if (CodedColumns.Contains(column))
            {
                lines.Add($"{label}：{CodeLabel(options, column, before)} → {CodeLabel(options, column, after)}");
            }
            else if (column == AdminColumns.IsArchived)
            {
                lines.Add(SheetBool.Parse(after) ? "封存這筆報名" : "取消封存");
            }
            else if (column == AdminColumns.DuplicateOfRecordId)
            {
                var target = duplicateCandidates?.FirstOrDefault(c => c.Code == after)?.Label ?? (after.Length == 0 ? "（不指定）" : "另一筆報名");
                lines.Add($"{label}：{target}");
            }
            else if (ValueColumns.Contains(column))
            {
                lines.Add($"{label}：{Blank(before)} → {Blank(after)}");
            }
            else
            {
                lines.Add(after.Trim().Length == 0 ? $"{label}：清除" : $"{label}：已修改");
            }
        }

        return lines;
    }

    private static int IndexOf(string column)
    {
        for (var i = 0; i < AdminColumns.All.Count; i++)
        {
            if (AdminColumns.All[i] == column)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static string Blank(string value) => value.Trim().Length == 0 ? "（空白）" : value.Trim();

    private static string CodeLabel(LookupOptions options, string column, string code)
    {
        if (code.Length == 0)
        {
            return column switch
            {
                AdminColumns.OverrideMealCode => "使用原始答案",
                AdminColumns.DuplicateStatus => "未判定",
                _ => "（空白）",
            };
        }

        var domain = column switch
        {
            AdminColumns.RegistrationStatus => LookupDomains.RegistrationStatus,
            AdminColumns.MembershipStatus => LookupDomains.MembershipStatus,
            AdminColumns.EligibilityStatus => LookupDomains.EligibilityStatus,
            AdminColumns.PaymentStatus => LookupDomains.PaymentStatus,
            AdminColumns.InvoiceStatus => LookupDomains.InvoiceStatus,
            AdminColumns.DuplicateStatus => LookupDomains.DuplicateStatus,
            _ => LookupDomains.MealCode,
        };
        return options.Label(domain, code);
    }

    /// <summary>從異動紀錄讀取本筆的歷程（只含欄名，不含值）。失敗只影響此區塊。</summary>
    private async Task LoadHistoryAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var entries = await _workspace.LoadHistoryAsync(Registration.RecordId, timeout.Token);
            History.Clear();
            foreach (var e in entries.Take(30))
            {
                var fields = FieldLabels.Describe(e.ChangedFields + "," + e.SensitiveFieldsChanged);
                var what = Operation(e.Operation) + (fields.Length > 0 ? "：" + fields : "");
                History.Add(new HistoryItem(e.ChangedAt.Replace('T', ' ').Split('+')[0].Split('.')[0], what));
            }

            HistoryStatus = History.Count == 0 ? "還沒有修改紀錄。" : "";
        }
        catch (Exception)
        {
            HistoryStatus = "暫時無法讀取修改紀錄，不影響處理這筆報名。";
        }
    }

    private static string Operation(string op) => op switch
    {
        ChangeOperations.Create => "建立後台資料",
        ChangeOperations.StatusChange => "變更報名狀態",
        ChangeOperations.Relink => "重新連結原始資料",
        ChangeOperations.AcceptSourceChange => "確認原始答案變更",
        ChangeOperations.AcceptIssue => "確認例外",
        _ => "修改",
    };

    private static IReadOnlyList<RawGroup> BuildRawGroups(Registration registration)
    {
        if (registration.Source is null)
        {
            return Array.Empty<RawGroup>();
        }

        var issues = registration.Issues.Where(i => !i.Accepted).ToList();

        RawField Field(string logical)
        {
            var raw = registration.Raw(logical);
            var column = Registration.OverrideColumnFor(logical);
            var corrected = column is null ? "" : registration.Admin[column].Trim();
            var warning = string.Join(" ", issues.Where(i => i.Field == logical).Select(i => i.Message));
            return new RawField(LogicalFields.Label(logical), FriendlyTime.OrBlank(raw), raw.Trim().Length == 0, corrected, warning);
        }

        var s = registration.Source;
        var meal = registration.Raw(LogicalFields.Meal);
        var mealWarning = string.Join(" ", issues.Where(i => i.Field == LogicalFields.Meal).Select(i => i.Message));
        var mealCorrected = registration.Admin[AdminColumns.OverrideMealCode];
        var channels = string.Join("、", s.Channels);

        return new[]
        {
            new RawGroup("基本資料", new[] { Field(LogicalFields.FullName), Field(LogicalFields.OrganizationName), Field(LogicalFields.JobTitle) }),
            new RawGroup("聯絡方式", new[] { Field(LogicalFields.Email), Field(LogicalFields.Phone) }),
            new RawGroup("發票資訊", new[] { Field(LogicalFields.InvoiceTitle), Field(LogicalFields.TaxId) }),
            new RawGroup("其他資訊", new[]
            {
                new RawField("午餐", FriendlyTime.OrBlank(meal), meal.Trim().Length == 0,
                    mealCorrected.Length > 0 ? LookupCatalog.Default.Label(LookupDomains.MealCode, mealCorrected) : "", mealWarning),
                new RawField("備註", FriendlyTime.OrBlank(registration.Raw(LogicalFields.ApplicantNotes)), registration.Raw(LogicalFields.ApplicantNotes).Trim().Length == 0),
                new RawField("如何得知活動", FriendlyTime.OrBlank(channels), channels.Length == 0),
                new RawField("送出時間", FriendlyTime.OrBlank(s.TimestampText), s.TimestampText.Length == 0),
            }),
        };
    }

    private static IReadOnlyList<OptionItem> BuildDuplicateCandidates(Registration registration, RegistrationWorkspace workspace, LookupOptions options)
    {
        var ids = registration.Issues
            .Where(i => IssueCodes.IsDuplicate(i.Code) && i.RelatedRecordId is { Length: > 0 })
            .Select(i => i.RelatedRecordId!)
            .Append(registration.Admin[AdminColumns.DuplicateOfRecordId])
            .Where(id => id.Length > 0 && id != registration.RecordId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return ids
            .Select(id => workspace.Registrations.FirstOrDefault(r => r.RecordId == id))
            .Where(r => r is not null)
            .Select(r => new OptionItem(r!.RecordId,
                $"{r.RegistrationNo} {r.Effective(LogicalFields.FullName)}（{options.Label(LookupDomains.RegistrationStatus, r.Admin.RegistrationStatus)}）"))
            .ToList();
    }
}
