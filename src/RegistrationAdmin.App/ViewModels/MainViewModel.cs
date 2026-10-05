using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.App.Presentation;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Search;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.Export;

namespace RegistrationAdmin.App.ViewModels;

/// <summary>畫面上目前顯示的頁面。</summary>
public enum AppPage
{
    List,
    Detail,
    Export,
    Settings,
}

/// <summary>左側導航的四個入口。</summary>
public enum NavSection
{
    Pending,
    All,
    Export,
    Settings,
}

public enum NoticeKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record ExportTemplateItem(ExportTemplate Template, string Description)
{
    public string Name => Template.Name;
}

/// <summary>
/// 主視窗：導航、名單（待處理／全部）、單筆處理、匯出與設定。
/// 所有資料規則都在 Core；這裡只負責畫面狀態與文案。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, string> TemplateDescriptions = new Dictionary<string, string>
    {
        ["all"] = "所有報名與後台處理欄位。",
        ["pending_review"] = "新報名與審核中的名單，方便核對會員與資格。",
        ["approved_pending_payment"] = "已通過、等待付款的名單，方便通知繳費。",
        ["confirmed"] = "已確認參加的名單，可用於簽到表與名牌。",
        ["waitlisted"] = "候補名單，依候補順位排列。",
        ["payment_check"] = "付款與核帳資料，方便對帳。",
        ["invoice"] = "發票抬頭、統編與開立狀態。",
        ["meal"] = "午餐統計與名單（不含不通過、取消、付款逾期）。",
    };

    private readonly RegistrationWorkspace _workspace;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Func<DateTime> _now;
    private IReadOnlyList<Registration> _filtered = Array.Empty<Registration>();
    private bool _suppressFilter;
    private int _noticeVersion;

    public MainViewModel(
        RegistrationWorkspace workspace,
        SettingsViewModel settings,
        IDialogService dialogs,
        ILogger<MainViewModel> logger,
        Func<DateTime>? now = null)
    {
        _workspace = workspace;
        _dialogs = dialogs;
        _logger = logger;
        _now = now ?? (() => DateTime.Now);
        Settings = settings;
        Settings.CanEndSession = () => !IsBusy && ConfirmLeave();
        Settings.SpreadsheetChanged += (_, _) => ClearWorkspace();
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(SettingsViewModel.IsSignedIn) or nameof(SettingsViewModel.IsBusy))) return;
            if (e.PropertyName == nameof(SettingsViewModel.IsSignedIn) && !Settings.IsSignedIn)
            {
                ClearWorkspace();
            }
            RefreshCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
            ExportCommand.NotifyCanExecuteChanged();
            RelinkCommand.NotifyCanExecuteChanged();
            AcceptIssueCommand.NotifyCanExecuteChanged();
            AcceptSourceChangeCommand.NotifyCanExecuteChanged();
        };
        Settings.ConnectionReady += async (_, _) => await OnConnectionReadyAsync();
        _options = LookupOptions.From(LookupCatalog.Default);
        ExportTemplateOptions = ExportTemplates.All
            .Select(t => new ExportTemplateItem(t, TemplateDescriptions.TryGetValue(t.Id, out var d) ? d : ""))
            .ToList();
        _selectedExportTemplate = ExportTemplateOptions[0];
    }

    public SettingsViewModel Settings { get; }

    private void ClearWorkspace()
    {
        _workspace.ClearSession();
        Rows.Clear();
        Cards.Clear();
        SourceProblems.Clear();
        PendingRelinks.Clear();
        SelectedRow = null;
        SelectedPendingRelink = null;
        _filtered = Array.Empty<Registration>();
        SetNav(NavSection.Settings);
        LastUpdatedText = "尚未更新名單";
        Notice = "";
        EventTitle = EventConfig.Default.EventName;
        EventSubtitle = "原始表單資料唯讀";
        PendingBadge = "";
        OnPropertyChanged(nameof(IsLoaded));
    }

    public ObservableCollection<RegistrationRowViewModel> Rows { get; } = new();

    public ObservableCollection<SummaryCard> Cards { get; } = new();

    /// <summary>需要管理員處理的來源連結問題（只在「設定 → 進階」顯示）。</summary>
    public ObservableCollection<IssueRowViewModel> SourceProblems { get; } = new();

    public ObservableCollection<PendingRelinkItem> PendingRelinks { get; } = new();

    public IReadOnlyList<ExportTemplateItem> ExportTemplateOptions { get; }

    public bool HasUnsavedChanges => Editor?.IsDirty == true;

    public bool IsLoaded => _workspace.IsLoaded;

    [ObservableProperty]
    private LookupOptions _options;

    // ── 頁面 ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListPage), nameof(IsDetailPage), nameof(IsExportPage), nameof(IsSettingsPage))]
    private AppPage _currentPage = AppPage.List;

    private NavSection _nav = NavSection.Pending;

    public NavSection Nav
    {
        get => _nav;
        set => Navigate(value);
    }

    public bool IsListPage => CurrentPage == AppPage.List;
    public bool IsDetailPage => CurrentPage == AppPage.Detail;
    public bool IsExportPage => CurrentPage == AppPage.Export;
    public bool IsSettingsPage => CurrentPage == AppPage.Settings;

    // ── 頁首 ──
    [ObservableProperty]
    private string _eventTitle = DefaultProfile.ConfigDefaults.First(kv => kv.Key == EventConfig.Keys.EventName).Value;

    [ObservableProperty]
    private string _eventSubtitle = "原始表單資料唯讀";

    [ObservableProperty]
    private string _lastUpdatedText = "尚未更新名單";

    [ObservableProperty]
    private string _pendingBadge = "";

    // ── 忙碌與提示 ──
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(SaveCommand), nameof(RelinkCommand), nameof(AcceptIssueCommand),
        nameof(AcceptSourceChangeCommand), nameof(ExportCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    [ObservableProperty]
    private NoticeKind _noticeKind;

    public bool HasNotice => Notice.Length > 0;

    // ── 名單 ──
    [ObservableProperty]
    private QuickFilter _quickFilter = QuickFilter.Pending;

    [ObservableProperty]
    private string _keyword = "";

    [ObservableProperty]
    private bool _showMoreFilters;

    [ObservableProperty]
    private string _filterRegistrationStatus = "";

    [ObservableProperty]
    private string _filterMembershipStatus = "";

    [ObservableProperty]
    private string _filterEligibilityStatus = "";

    [ObservableProperty]
    private string _filterPaymentStatus = "";

    [ObservableProperty]
    private string _filterInvoiceStatus = "";

    [ObservableProperty]
    private string _filterMealCode = "";

    [ObservableProperty]
    private bool _includeArchived;

    [ObservableProperty]
    private string _moreFiltersLabel = "更多篩選";

    [ObservableProperty]
    private string _listTitle = "需要處理的報名";

    [ObservableProperty]
    private string _listSubtitle = "優先顯示尚未完成或有問題的資料";

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyTitle = "尚未載入名單";

    [ObservableProperty]
    private string _emptyDetail = "請到「設定」連線 Google 試算表，或按「更新名單」。";

    [ObservableProperty]
    private RegistrationRowViewModel? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    [NotifyCanExecuteChangedFor(nameof(AcceptSourceChangeCommand), nameof(SaveCommand), nameof(DiscardChangesCommand))]
    private RegistrationEditorViewModel? _editor;

    // ── 匯出 ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportCountText))]
    private ExportTemplateItem _selectedExportTemplate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportCountText))]
    private bool _exportCurrentFilter;

    [ObservableProperty]
    private bool _exportAsCsv;

    [ObservableProperty]
    private bool _exportRawValues;

    [ObservableProperty]
    private bool _exportIncludeSensitive = true;

    [ObservableProperty]
    private bool _exportIncludeApplicantNotes = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastExport))]
    private string _lastExportPath = "";

    public bool HasLastExport => LastExportPath.Length > 0;

    public string CurrentFilterCountText => $"目前名單的搜尋與篩選結果（{_filtered.Count} 筆）";

    public string ExportCountText
    {
        get
        {
            if (!_workspace.IsLoaded)
            {
                return "尚未載入名單，請先更新名單。";
            }

            var count = ExportSource().Count(SelectedExportTemplate.Template.Include);
            return count == 0 ? "目前沒有符合這個範本的報名。" : $"將匯出 {count} 筆報名。";
        }
    }

    // ── 進階：來源連結 ──
    [ObservableProperty]
    private PendingRelinkItem? _selectedPendingRelink;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RelinkCommand))]
    private OrphanItem? _selectedRelinkCandidate;

    public async Task InitializeAsync()
    {
        if (!Settings.IsSignedIn) return;
        if (Settings.CanAutoConnect)
        {
            await RefreshCommand.ExecuteAsync(null);
            if (!_workspace.IsLoaded)
            {
                SetNav(NavSection.Settings);
            }
        }
        else
        {
            SetNav(NavSection.Settings);
            ShowNotice("第一次使用請先連線 Google 試算表。", NoticeKind.Info);
        }
    }

    partial void OnKeywordChanged(string value) => ApplyFilter();
    partial void OnFilterRegistrationStatusChanged(string value) => ApplyFilter();
    partial void OnFilterMembershipStatusChanged(string value) => ApplyFilter();
    partial void OnFilterEligibilityStatusChanged(string value) => ApplyFilter();
    partial void OnFilterPaymentStatusChanged(string value) => ApplyFilter();
    partial void OnFilterInvoiceStatusChanged(string value) => ApplyFilter();
    partial void OnFilterMealCodeChanged(string value) => ApplyFilter();
    partial void OnIncludeArchivedChanged(bool value) => ApplyFilter();

    partial void OnQuickFilterChanged(QuickFilter value)
    {
        // 「待處理」籤與左側「待處理」入口是同一件事；其他籤屬於「全部名單」。
        var nav = value == QuickFilter.Pending ? NavSection.Pending : NavSection.All;
        if (_nav is NavSection.Pending or NavSection.All && _nav != nav)
        {
            _nav = nav;
            OnPropertyChanged(nameof(Nav));
        }

        ApplyFilter();
    }

    partial void OnSelectedExportTemplateChanged(ExportTemplateItem value) =>
        ExportIncludeApplicantNotes = value.Template.IncludeApplicantNotesByDefault;

    partial void OnSelectedPendingRelinkChanged(PendingRelinkItem? value) =>
        SelectedRelinkCandidate = value?.Candidates.FirstOrDefault();

    // ── 導航 ──

    /// <summary>切換左側入口；有未儲存的修改時先確認。</summary>
    public bool Navigate(NavSection target)
    {
        if (!Settings.IsSignedIn) return false;
        if (target == _nav && CurrentPage != AppPage.Detail)
        {
            return true;
        }

        if (CurrentPage == AppPage.Detail && !ConfirmLeave())
        {
            // 還原導航選取（在繫結更新結束後通知）。
            Defer(() => OnPropertyChanged(nameof(Nav)));
            return false;
        }

        SetNav(target);
        return true;
    }

    private void SetNav(NavSection target)
    {
        _nav = target;
        OnPropertyChanged(nameof(Nav));
        Editor = null;
        switch (target)
        {
            case NavSection.Pending:
                CurrentPage = AppPage.List;
                SetQuickFilter(QuickFilter.Pending);
                break;
            case NavSection.All:
                CurrentPage = AppPage.List;
                if (QuickFilter == QuickFilter.Pending)
                {
                    SetQuickFilter(QuickFilter.All);
                }

                break;
            case NavSection.Export:
                CurrentPage = AppPage.Export;
                OnPropertyChanged(nameof(ExportCountText));
                OnPropertyChanged(nameof(CurrentFilterCountText));
                break;
            case NavSection.Settings:
                CurrentPage = AppPage.Settings;
                break;
        }
    }

    private void SetQuickFilter(QuickFilter value)
    {
        if (QuickFilter != value)
        {
            QuickFilter = value;
        }
        else
        {
            ApplyFilter();
        }
    }

    private bool ConfirmLeave() =>
        !HasUnsavedChanges
        || _dialogs.Confirm("這筆報名有尚未儲存的變更。離開後這些變更會被捨棄。", "要離開這筆報名嗎？", "捨棄變更並離開", danger: true);

    [RelayCommand]
    private void OpenRow(RegistrationRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null)
        {
            return;
        }

        SelectedRow = row;
        Editor = NewEditor(row.Registration);
        CurrentPage = AppPage.Detail;
    }

    [RelayCommand]
    private void BackToList()
    {
        if (CurrentPage != AppPage.Detail || !ConfirmLeave())
        {
            return;
        }

        Editor = null;
        CurrentPage = AppPage.List;
        ApplyFilter();
    }

    [RelayCommand]
    private void SetQuick(QuickFilter value) => QuickFilter = value;

    // ── 名單 ──

    private bool CanRun() => Settings.IsSignedIn && !Settings.IsBusy && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        if (!CanRun()) return;
        if (CurrentPage == AppPage.Detail && HasUnsavedChanges
            && !_dialogs.Confirm("這筆報名有尚未儲存的變更。更新名單會捨棄這些變更。", "要更新名單嗎？", "捨棄變更並更新", danger: true))
        {
            return;
        }

        var openRecordId = CurrentPage == AppPage.Detail ? Editor?.Registration.RecordId : null;
        SyncReport? report = null;
        var ok = await RunBusyAsync("正在更新名單…", async ct =>
        {
            report = await _workspace.RefreshAsync(ct);
            _logger.LogInformation(
                "同步完成：來源 {Rows} 列、新增 {Created}、找不到來源 {Orphans}、碰撞 {Collisions}、待重新連結 {Pending}、來源異動 {Changed}",
                report.SourceRows, report.Created, report.Orphans, report.Collisions, report.PendingRelinks, report.SourceChanged);
        });

        Settings.UpdateFromWorkspace();
        if (_workspace.IsLoaded)
        {
            RebuildAll();
            if (openRecordId is not null && _workspace.Registrations.FirstOrDefault(r => r.RecordId == openRecordId) is { } reopened)
            {
                Editor = NewEditor(reopened);
            }
            else if (CurrentPage == AppPage.Detail)
            {
                Editor = null;
                CurrentPage = AppPage.List;
            }
        }

        if (ok && report is not null)
        {
            var text = report.Created > 0 ? $"名單已更新，新增 {report.Created} 筆報名。" : "名單已更新。";
            if (report.Orphans + report.Collisions + report.PendingRelinks > 0)
            {
                ShowNotice(text + "有幾筆原始資料的對應需要管理員確認（設定 → 進階）。", NoticeKind.Warning);
            }
            else
            {
                ShowNotice(text, NoticeKind.Success);
            }
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _suppressFilter = true;
        Keyword = "";
        FilterRegistrationStatus = "";
        FilterMembershipStatus = "";
        FilterEligibilityStatus = "";
        FilterPaymentStatus = "";
        FilterInvoiceStatus = "";
        FilterMealCode = "";
        IncludeArchived = false;
        _suppressFilter = false;
        ApplyFilter();
    }

    // ── 單筆 ──

    private bool CanSave() => CanRun() && Editor is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave()) return;
        var editor = Editor;
        if (editor is null)
        {
            return;
        }

        if (!editor.IsDirty)
        {
            ShowNotice("沒有需要儲存的變更。", NoticeKind.Info);
            return;
        }

        var errors = _workspace.ValidateSave(editor.Registration, editor.Working);
        if (errors.Count > 0)
        {
            editor.ShowValidation(errors);
            ShowNotice($"還有 {errors.Count} 個地方需要修正才能儲存，請看「處理這筆報名」上方的說明。", NoticeKind.Warning);
            return;
        }

        var name = editor.DisplayName;
        var ok = await RunBusyAsync("正在儲存…", async ct =>
        {
            await _workspace.SaveAsync(editor.Registration, editor.Working, ct);
            _logger.LogInformation("已儲存 {RegistrationNo}", editor.Registration.RegistrationNo);
        }, onError: message => editor.ShowError("儲存失敗，您輸入的內容仍保留在畫面上。" + Environment.NewLine + message));

        if (ok)
        {
            RebuildAfterLocalChange(editor.Registration);
            ShowNotice($"已儲存「{name}」的變更（{_now():HH:mm}）。", NoticeKind.Success);
        }
    }

    private bool CanDiscard() => Editor is not null;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void DiscardChanges()
    {
        if (Editor is null)
        {
            return;
        }

        if (Editor.IsDirty && !_dialogs.Confirm("將捨棄這筆報名尚未儲存的變更，恢復為上次儲存的內容。", "要取消變更嗎？", "取消變更", danger: true))
        {
            return;
        }

        Editor = NewEditor(Editor.Registration);
        ShowNotice("已取消尚未儲存的變更。", NoticeKind.Info);
    }

    private bool CanAcceptIssue(RecordIssueViewModel? issue) => CanRun() && issue?.CanAccept == true;

    /// <summary>確認問題沒關係（接受例外）：立即寫入，需要填寫原因。</summary>
    [RelayCommand(CanExecute = nameof(CanAcceptIssue))]
    private async Task AcceptIssueAsync(RecordIssueViewModel? issue)
    {
        if (issue is null)
        {
            return;
        }

        if (HasUnsavedChanges)
        {
            ShowNotice("請先儲存或取消目前的變更，再確認這個問題。", NoticeKind.Warning);
            return;
        }

        var note = _dialogs.AskReason("確認這個問題沒關係", $"「{issue.Title}」\n\n確認後這個提示不再出現在待處理名單，並會立即記錄。",
            "原因（例如：已電話確認）", "確認並記錄");
        if (note is null)
        {
            return;
        }

        var registration = issue.Registration;
        var ok = await RunBusyAsync("正在記錄…", ct => _workspace.AcceptIssueAsync(registration, issue.Issue.Code, note, ct));
        if (ok)
        {
            RebuildAfterLocalChange(registration);
            ShowNotice("已記錄，這個問題不再列為待處理。", NoticeKind.Success);
        }
    }

    private bool CanAcceptSourceChange() => CanRun() && Editor?.Registration.SourceChanged == true;

    [RelayCommand(CanExecute = nameof(CanAcceptSourceChange))]
    private async Task AcceptSourceChangeAsync()
    {
        var editor = Editor;
        if (editor is null)
        {
            return;
        }

        if (editor.IsDirty)
        {
            ShowNotice("請先儲存或取消目前的變更。", NoticeKind.Warning);
            return;
        }

        if (!_dialogs.Confirm("請先確認左側「原始報名資料」是最新的內容。確認後這筆不再顯示「報名者修改過原始答案」。",
                "確認最新內容", "我已確認"))
        {
            return;
        }

        var ok = await RunBusyAsync("正在記錄…", ct => _workspace.AcceptSourceChangeAsync(editor.Registration, ct));
        if (ok)
        {
            RebuildAfterLocalChange(editor.Registration);
            ShowNotice("已記錄確認。", NoticeKind.Success);
        }
    }

    // ── 匯出 ──

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Export()
    {
        if (!CanRun()) return;
        if (!_workspace.IsLoaded)
        {
            ShowNotice("請先更新名單，再匯出。", NoticeKind.Warning);
            return;
        }

        var template = SelectedExportTemplate.Template;
        var format = ExportAsCsv ? ExportFormat.Csv : ExportFormat.Xlsx;
        var extension = format == ExportFormat.Csv ? "csv" : "xlsx";
        var safeName = string.Concat(template.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var fileName = $"{safeName}_{_now():yyyyMMdd_HHmm}.{extension}";
        var filter = format == ExportFormat.Csv ? "CSV 檔 (*.csv)|*.csv" : "Excel 活頁簿 (*.xlsx)|*.xlsx";
        var path = _dialogs.SaveFile(fileName, filter);
        if (path is null)
        {
            return;
        }

        var query = ExportCurrentFilter ? CurrentQuery() : new RegistrationQuery();
        var request = new ExportRequest
        {
            Template = template,
            Registrations = ExportSource(),
            Lookups = _workspace.Lookups,
            Mode = ExportRawValues ? ExportValueMode.Raw : ExportValueMode.Effective,
            IncludeSensitive = ExportIncludeSensitive,
            IncludeApplicantNotes = ExportIncludeApplicantNotes,
            FilterSummary = ExportCurrentFilter ? DescribeQuickFilter() + query.Describe(_workspace.Lookups) : "全部報名（不含封存）",
            DataVersion = $"名單更新於 {(_workspace.LastSyncedAt is { } t ? TimeFormat.Display(t) : "—")}；來源工作表「{_workspace.SourceSheetTitle}」",
            GeneratedAt = DateTimeOffset.Now,
        };

        try
        {
            var count = ExportService.Export(request, format, path);
            LastExportPath = path;
            ShowNotice($"已匯出「{template.Name}」{count} 筆：{Path.GetFileName(path)}", NoticeKind.Success);
            _logger.LogInformation("匯出 {Template} {Format}：{Count} 筆", template.Id, format, count);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "匯出失敗");
            ShowNotice("無法寫入檔案。檔案可能正在 Excel 中開啟，請關閉後再試。", NoticeKind.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "匯出失敗");
            ShowNotice("沒有寫入這個資料夾的權限，請選擇其他位置。", NoticeKind.Error);
        }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        if (LastExportPath.Length > 0 && File.Exists(LastExportPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastExportPath}\"") { UseShellExecute = true });
        }
    }

    // ── 進階：來源連結 ──

    private bool CanRelink() => CanRun() && SelectedPendingRelink is not null && SelectedRelinkCandidate is not null;

    [RelayCommand(CanExecute = nameof(CanRelink))]
    private async Task RelinkAsync()
    {
        var pending = SelectedPendingRelink;
        var candidate = SelectedRelinkCandidate;
        if (pending is null || candidate is null)
        {
            return;
        }

        if (!_dialogs.Confirm($"將後台資料 {candidate.Admin.RegistrationNo} 改為對應原始回應第 {pending.Pending.Source.RowNumber} 列。\n" +
                              "原有的審核、付款等後台資料會保留。", "重新連結原始資料", "重新連結"))
        {
            return;
        }

        var ok = await RunBusyAsync("正在重新連結…", ct => _workspace.RelinkAsync(pending.Pending, candidate.Admin, ct));
        if (ok)
        {
            ShowNotice($"已將 {candidate.Admin.RegistrationNo} 重新連結。", NoticeKind.Success);
            Editor = null;
            RebuildAll();
        }
    }

    [RelayCommand]
    private void CloseNotice() => Notice = "";

    // ── 內部 ──

    private RegistrationEditorViewModel NewEditor(Registration registration) =>
        new(registration, _workspace, Options, _dialogs, () => _now().Date, loadHistory: _workspace.IsLoaded);

    private async Task OnConnectionReadyAsync()
    {
        var wasLoaded = _workspace.IsLoaded;
        await RefreshCommand.ExecuteAsync(null);
        if (!wasLoaded && _workspace.IsLoaded)
        {
            SetNav(NavSection.Pending);
        }
    }

    public void ShowNotice(string text, NoticeKind kind)
    {
        NoticeKind = kind;
        Notice = text;
        var version = ++_noticeVersion;
        if (kind is NoticeKind.Success or NoticeKind.Info)
        {
            // 成功訊息不阻斷操作，數秒後自動收起；錯誤與警告保留到使用者關閉。
            _ = HideLaterAsync(version);
        }
    }

    private async Task HideLaterAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (version == _noticeVersion)
        {
            Notice = "";
        }
    }

    private async Task<bool> RunBusyAsync(string message, Func<CancellationToken, Task> action, Action<string>? onError = null)
    {
        if (!CanRun())
        {
            return false;
        }

        IsBusy = true;
        BusyText = message;
        try
        {
            await action(CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            var text = UserMessages.Describe(ex);
            _logger.LogWarning(ex, "操作失敗：{Operation}", message);
            Settings.LastTechnicalError = $"{_now():yyyy-MM-dd HH:mm}　{message}\n{ex.GetType().Name}：{Core.Diagnostics.Redactor.Redact(ex.Message)}";
            onError?.Invoke(text);
            ShowNotice(text, NoticeKind.Error);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            UpdateLastUpdated();
        }
    }

    private void UpdateLastUpdated()
    {
        if (_workspace.LastSyncedAt is { } synced)
        {
            LastUpdatedText = "最後更新 " + FriendlyTime.Format(synced, _now());
        }
    }

    private void RebuildAll()
    {
        Options = LookupOptions.From(_workspace.Lookups);
        RebuildHeader();
        ApplyFilter();
        RebuildRelinks();
        OnPropertyChanged(nameof(IsLoaded));
    }

    private void RebuildAfterLocalChange(Registration registration)
    {
        RebuildHeader();
        ApplyFilter();
        if (CurrentPage == AppPage.Detail)
        {
            Editor = NewEditor(registration);
        }
    }

    private void RebuildHeader()
    {
        var config = _workspace.Config;
        EventTitle = config.EventName.Length > 0 ? config.EventName : EventTitle;
        var parts = new List<string>();
        var dates = DateRange(config.Get(EventConfig.Keys.EventStartDate), config.Get(EventConfig.Keys.EventEndDate));
        if (dates.Length > 0)
        {
            parts.Add(dates);
        }

        if (config.Capacity > 0)
        {
            parts.Add($"名額 {config.Capacity} 人");
        }

        parts.Add("原始表單資料唯讀");
        EventSubtitle = string.Join(" · ", parts);

        var summary = _workspace.Dashboard();
        var available = summary.Capacity.Available;
        var review = summary.ByStatus.GetValueOrDefault(RegistrationStatus.Submitted) + summary.ByStatus.GetValueOrDefault(RegistrationStatus.UnderReview);
        Cards.Clear();
        Cards.Add(new SummaryCard("全部報名", N(summary.Total), available > 0 ? $"尚有 {available} 個名額" : "名額已滿"));
        Cards.Add(new SummaryCard("待審核", N(review), "需要會員／資格確認"));
        Cards.Add(new SummaryCard("待付款", N(summary.PaymentPending), "已通過，尚未核帳"));
        Cards.Add(new SummaryCard("已確認", N(summary.ByStatus.GetValueOrDefault(RegistrationStatus.Confirmed)), "報名程序已完成"));

        var pending = _workspace.Registrations.Count(r => !r.Admin.IsArchived && WorkQueue.NeedsAttention(r));
        PendingBadge = pending > 0 ? N(pending) : "";
        UpdateLastUpdated();
    }

    public static string DateRange(string start, string end)
    {
        if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var s))
        {
            return "";
        }

        if (!DateTime.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) || e.Date == s.Date)
        {
            return $"{s.Month} 月 {s.Day} 日";
        }

        return s.Month == e.Month && s.Year == e.Year
            ? $"{s.Month} 月 {s.Day}–{e.Day} 日"
            : $"{s.Month} 月 {s.Day} 日–{e.Month} 月 {e.Day} 日";
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private RegistrationQuery CurrentQuery() => new()
    {
        Keyword = Keyword,
        RegistrationStatus = FilterRegistrationStatus ?? "",
        MembershipStatus = FilterMembershipStatus ?? "",
        EligibilityStatus = FilterEligibilityStatus ?? "",
        PaymentStatus = FilterPaymentStatus ?? "",
        InvoiceStatus = FilterInvoiceStatus ?? "",
        MealCode = FilterMealCode ?? "",
        IncludeArchived = IncludeArchived,
    };

    private string DescribeQuickFilter() => QuickFilter switch
    {
        QuickFilter.Pending => "待處理；",
        QuickFilter.Errors => "有錯誤；",
        QuickFilter.Duplicates => "疑似重複；",
        _ => "",
    };

    private IReadOnlyList<Registration> ExportSource() =>
        ExportCurrentFilter ? _filtered : RegistrationFilter.Apply(_workspace.Registrations, new RegistrationQuery());

    /// <summary>依目前的快速篩選籤與條件重建名單。</summary>
    public void ApplyFilter()
    {
        if (_suppressFilter)
        {
            return;
        }

        var selectedId = SelectedRow?.RecordId;
        var now = _now();
        _filtered = RegistrationFilter.Apply(_workspace.Registrations, CurrentQuery())
            .Where(r => WorkQueue.Matches(QuickFilter, r))
            // 待處理：需要審核或有問題的排在只等付款的前面；同一組新的在前。
            .OrderBy(r => QuickFilter == QuickFilter.Pending && WorkQueue.Problems(r).Count == 0 ? 1 : 0)
            .ThenByDescending(r => r.Source?.SubmittedAt ?? DateTime.MinValue)
            .ToList();

        Rows.Clear();
        foreach (var registration in _filtered)
        {
            Rows.Add(new RegistrationRowViewModel(registration, Options, now));
        }

        SelectedRow = selectedId is null ? null : Rows.FirstOrDefault(r => r.RecordId == selectedId);

        var advanced = new[] { FilterRegistrationStatus, FilterMembershipStatus, FilterEligibilityStatus, FilterPaymentStatus, FilterInvoiceStatus, FilterMealCode }
            .Count(f => !string.IsNullOrEmpty(f)) + (IncludeArchived ? 1 : 0);
        MoreFiltersLabel = advanced > 0 ? $"更多篩選（{advanced}）" : "更多篩選";

        (ListTitle, ListSubtitle) = QuickFilter switch
        {
            QuickFilter.Pending => ("需要處理的報名", "優先顯示尚未完成或有問題的資料"),
            QuickFilter.Errors => ("有錯誤的報名", "資料缺漏或格式錯誤，需要修正或確認"),
            QuickFilter.Duplicates => ("疑似重複的報名", "Email、電話或姓名＋單位相同，請人工判斷"),
            _ => ("全部報名", "可搜尋、篩選，或點欄位標題排序"),
        };

        var noun = QuickFilter == QuickFilter.Pending ? "筆待處理資料" : "筆資料";
        ResultText = $"顯示 {_filtered.Count} {noun}" + (_filtered.Count != _workspace.Registrations.Count ? $"（共 {_workspace.Registrations.Count} 筆報名）" : "");
        IsEmpty = _filtered.Count == 0;
        var filtered = Keyword.Trim().Length > 0 || advanced > 0;
        (EmptyTitle, EmptyDetail) = (_workspace.IsLoaded, QuickFilter, filtered) switch
        {
            (false, _, _) => ("尚未載入名單", "請到「設定」確認 Google 連線，或按右上角「更新名單」。"),
            (true, _, true) => ("找不到符合條件的報名", "請換個關鍵字，或按「清除篩選」。"),
            (true, QuickFilter.Pending, _) => ("目前沒有待處理的報名", "所有報名都已處理完成。新的報名會在「更新名單」後出現。"),
            (true, QuickFilter.Errors, _) => ("目前沒有資料錯誤", "所有報名的必填欄位與格式都正常。"),
            (true, QuickFilter.Duplicates, _) => ("目前沒有疑似重複的報名", ""),
            _ => ("還沒有任何報名", "Google Form 收到報名後，按「更新名單」就會出現。"),
        };

        OnPropertyChanged(nameof(CurrentFilterCountText));
        OnPropertyChanged(nameof(ExportCountText));
    }

    private void RebuildRelinks()
    {
        var byId = _workspace.Registrations.GroupBy(r => r.RecordId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        SourceProblems.Clear();
        foreach (var issue in _workspace.AllIssues.Where(i =>
                     i.Code is IssueCodes.SourceKeyCollision or IssueCodes.Orphan or IssueCodes.PossibleRelink or IssueCodes.AdminDuplicateKey))
        {
            SourceProblems.Add(new IssueRowViewModel(issue, issue.RecordId.Length > 0 && byId.TryGetValue(issue.RecordId, out var r) ? r : null));
        }

        PendingRelinks.Clear();
        foreach (var pending in _workspace.PendingRelinks)
        {
            PendingRelinks.Add(new PendingRelinkItem(pending));
        }

        SelectedPendingRelink = PendingRelinks.FirstOrDefault();
    }

    private static void Defer(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}
