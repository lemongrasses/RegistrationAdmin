using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.App.Presentation;
using RegistrationAdmin.Core.Diagnostics;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.GoogleSheets.Auth;

namespace RegistrationAdmin.App.ViewModels;

/// <summary>
/// 設定頁：一般使用者只看連線狀態；OAuth、試算表、來源工作表、管理資料區與診斷放在「進階（管理員）」。
/// 本程式沒有使用者、角色或權限頁。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const string AdvancedHint = "這些設定通常不需要修改。錯誤設定可能讓系統無法讀取報名資料。";

    private readonly LocalSettings _settings;
    private readonly ISheetsServiceProvider _auth;
    private readonly SchemaManager _schema;
    private readonly RegistrationWorkspace _workspace;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ISpreadsheetPicker? _picker;
    private readonly Action _saveSettings;
    private CancellationTokenSource? _operationCancellation;
    private bool _startupChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoginCover))]
    private bool _isRestoringSession = true;

    public bool ShowLoginCover => !IsSignedIn && !IsRestoringSession;

    /// <summary>啟動時自動恢復登入，不開啟瀏覽器；失敗才顯示登入封面。</summary>
    public async Task RestoreSessionAsync()
    {
        if (_startupChecked) return;
        _startupChecked = true;
        try
        {
            var restored = false;
            var success = await RunAsync("正在確認 Google 登入…", async ct =>
            {
                IsSigningIn = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                restored = await _auth.TryRestoreAsync(timeout.Token) is not null;
                timeout.Token.ThrowIfCancellationRequested();
                if (restored)
                {
                    AccountText = _auth.AccountEmail is { Length: > 0 } email ? email : "已登入 Google";
                    StatusMessage = "已恢復登入。";
                }
                else StatusMessage = "";
            });
            if (success && restored) IsSignedIn = true;
        }
        finally
        {
            IsRestoringSession = false;
        }
    }

    public SettingsViewModel(
        LocalSettings settings,
        ISheetsServiceProvider auth,
        SchemaManager schema,
        RegistrationWorkspace workspace,
        IDialogService dialogs,
        ILogger<SettingsViewModel> logger,
        ISpreadsheetPicker? picker = null,
        Action? saveSettings = null)
    {
        _settings = settings;
        _auth = auth;
        _schema = schema;
        _workspace = workspace;
        _dialogs = dialogs;
        _logger = logger;
        _picker = picker;
        _saveSettings = saveSettings ?? settings.Save;
        _clientSecretPath = settings.ClientSecretPath;
        _spreadsheetInput = settings.SpreadsheetInput;
        _accountText = "尚未登入 Google";
    }

    /// <summary>設定完整且本機已有授權時，啟動後自動更新名單（不會無預警開啟瀏覽器）。</summary>
    public bool CanAutoConnect => IsSignedIn && _settings.IsConfigured;

    public Func<bool>? CanEndSession { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAutoConnect))]
    [NotifyPropertyChangedFor(nameof(ShowLoginCover))]
    [NotifyCanExecuteChangedFor(nameof(InitializeSchemaCommand))]
    [NotifyCanExecuteChangedFor(nameof(PickSpreadsheetCommand))]
    private bool _isSignedIn;

    partial void OnIsSignedInChanged(bool value)
    {
        if (value) return;
        ClearConnection();
    }

    private void ClearConnection()
    {
        IsConnected = false;
        SourceSheets.Clear();
        SelectedSourceSheet = null;
        SpreadsheetTitle = "尚未連線";
        DiagnosticsText = "";
        HealthOk = false;
        HealthText = "連線後會自動檢查報名表的欄位。";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task LoginAsync()
    {
        var success = await RunAsync("正在登入 Google，請在預設瀏覽器完成登入…", async ct =>
        {
            IsSigningIn = true;
            await _auth.GetAsync(ct);
            ct.ThrowIfCancellationRequested();
            AccountText = _auth.AccountEmail is { Length: > 0 } email ? email : "已登入 Google";
            StatusMessage = "登入成功。";
        });
        if (success) IsSignedIn = true;
    }

    /// <summary>保留首次設定步驟直到連線成功；貼上網址並不代表已登入。</summary>
    public bool NeedsSetup => !IsConnected;

    public event EventHandler? ConnectionReady;
    public event EventHandler? SpreadsheetChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelOperation), nameof(CancelOperationText))]
    [NotifyCanExecuteChangedFor(nameof(CancelLoginCommand))]
    private bool _isPickingSpreadsheet;

    public bool CanCancelOperation => IsSigningIn || IsPickingSpreadsheet;
    public string CancelOperationText => IsPickingSpreadsheet ? "取消選擇" : "取消登入";

    private bool CanPickSpreadsheet() => IsSignedIn && !IsBusy && _picker is not null;

    [RelayCommand(CanExecute = nameof(CanPickSpreadsheet))]
    private async Task PickSpreadsheetAsync()
    {
        if (!CanPickSpreadsheet() || CanEndSession?.Invoke() == false) return;
        await RunAsync("正在開啟 Google 試算表選擇器，請在預設瀏覽器選擇…", async ct =>
        {
            IsPickingSpreadsheet = true;
            var selected = await _picker!.PickAsync(_auth.AccountEmail ?? "", ct);
            ct.ThrowIfCancellationRequested();
            if (selected is null)
            {
                StatusMessage = "已取消選擇，原本的試算表設定仍保留。";
                return;
            }
            if (selected.Id != _settings.SpreadsheetId)
            {
                SpreadsheetInput = $"https://docs.google.com/spreadsheets/d/{selected.Id}/edit";
                ClearConnection();
                SpreadsheetChanged?.Invoke(this, EventArgs.Empty);
            }
            SpreadsheetTitle = selected.Title;
            StatusMessage = "已選取試算表，請按「連線試算表」確認資料與管理分頁。";
        });
    }

    public ObservableCollection<SheetInfo> SourceSheets { get; } = new();

    public string LogDirectory => AppPaths.LogDirectory;

    public string VersionText => $"版本 {AppInfo.Version}";

    public string TechnicalVersionText =>
        $"版本 {AppInfo.Version}｜設定檔 {DefaultProfile.ProfileId}｜表單版本 {DefaultProfile.FormSchemaVersion}｜.NET {Environment.Version}";

    [ObservableProperty]
    private string _clientSecretPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsSetup))]
    private string _spreadsheetInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel), nameof(ConnectionTone), nameof(NeedsSetup))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelOperation))]
    [NotifyCanExecuteChangedFor(nameof(CancelLoginCommand))]
    private bool _isSigningIn;

    [ObservableProperty]
    private string _accountText;

    [ObservableProperty]
    private string _spreadsheetTitle = "尚未連線";

    [ObservableProperty]
    private string _healthText = "連線後會自動檢查報名表的欄位。";

    [ObservableProperty]
    private bool _healthOk;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InitializeSchemaCommand))]
    private SheetInfo? _selectedSourceSheet;

    [ObservableProperty]
    private string _diagnosticsText = "";

    [ObservableProperty]
    private string _lastTechnicalError = "";

    [ObservableProperty]
    private bool _isAdvancedOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand), nameof(ConnectCommand), nameof(InitializeSchemaCommand), nameof(SignOutCommand), nameof(ReconnectCommand), nameof(PickSpreadsheetCommand))]
    private bool _isBusy;

    public string ConnectionLabel => IsConnected ? "已連線" : "未連線";

    public ChipTone ConnectionTone => IsConnected ? ChipTone.Success : ChipTone.Neutral;

    partial void OnClientSecretPathChanged(string value)
    {
        _settings.ClientSecretPath = value?.Trim() ?? "";
        SaveSettings();
    }

    partial void OnSpreadsheetInputChanged(string value)
    {
        _settings.SpreadsheetInput = value?.Trim() ?? "";
        SaveSettings();
    }

    /// <summary>名單更新後，同步顯示連線與欄位檢查結果。</summary>
    public void UpdateFromWorkspace()
    {
        if (_workspace.IsLoaded)
        {
            IsConnected = true;
            if (_auth.AccountEmail is { Length: > 0 } email)
            {
                AccountText = email;
            }

            if (_workspace.SourceSheetTitle.Length > 0 && SpreadsheetTitle == "尚未連線")
            {
                SpreadsheetTitle = _workspace.Config.EventName.Length > 0 ? _workspace.Config.EventName : _workspace.SourceSheetTitle;
            }
        }

        if (_workspace.Health is { } health)
        {
            HealthOk = health.IsHealthy;
            var checkedAt = _workspace.LastSyncedAt is { } t ? FriendlyTime.Format(t, DateTime.Now) : "剛才";
            HealthText = health.IsHealthy
                ? $"最後檢查：{checkedAt}　沒有發現欄位或資料結構問題。"
                : "報名表的欄位和系統設定對不上，暫時無法讀取名單。請管理員打開下方「進階（管理員）」查看檢查結果。";
        }
    }

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedOpen = !IsAdvancedOpen;

    [RelayCommand]
    private void BrowseClientSecret()
    {
        var path = _dialogs.OpenFile("Google OAuth 用戶端 JSON (*.json)|*.json");
        if (path is not null)
        {
            ClientSecretPath = path;
        }
    }

    private bool CanRun() => !IsBusy;

    /// <summary>檢查連線：授權（必要時開啟系統瀏覽器）、讀取試算表，正常時更新名單。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ConnectAsync()
    {
        if (!IsSignedIn)
        {
            StatusMessage = "請先在登入封面登入 Google。";
            return;
        }
        if (SpreadsheetIdParser.Parse(SpreadsheetInput) is null)
        {
            StatusMessage = "請先貼上報名資料試算表的網址。";
            return;
        }

        var needsSchema = false;
        var connected = await RunAsync("正在檢查試算表連線…", async ct =>
        {
            IsSigningIn = true;
            try
            {
                await _auth.GetAsync(ct);
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                IsSigningIn = false;
            }
            AccountText = _auth.AccountEmail is { Length: > 0 } email ? email : "已登入 Google";
            StatusMessage = "已登入 Google，正在檢查試算表連線…";
            var info = await _schema.GetSpreadsheetAsync(ct);
            SpreadsheetTitle = info.Title;
            IsConnected = true;

            SourceSheets.Clear();
            foreach (var sheet in info.Sheets.Where(s => !ManagementSheetNames.All.Contains(s.Title)))
            {
                SourceSheets.Add(sheet);
            }

            var configured = _workspace.IsLoaded ? _workspace.Config.SourceSheetId : null;
            SelectedSourceSheet = SourceSheets.FirstOrDefault(s => s.SheetId == configured) ?? SourceSheets.FirstOrDefault();

            var missing = ManagementSheetNames.All.Where(n => info.FindByTitle(n) is null).ToList();
            needsSchema = missing.Count > 0;
            DiagnosticsText = $"連線成功：{info.Title}（工作表 {info.Sheets.Count} 張）\n" +
                              (missing.Count == 0 ? "管理資料區完整。" : "尚缺管理分頁：" + string.Join("、", missing));
            StatusMessage = missing.Count == 0 ? "連線正常。" : "已連線，但這份試算表還沒有管理資料區。";
            _logger.LogInformation("連線成功，工作表 {Count} 張，缺少管理分頁 {Missing} 張", info.Sheets.Count, missing.Count);

        });
        if (connected && !needsSchema) ConnectionReady?.Invoke(this, EventArgs.Empty);

        // 只有一張回應工作表時不必讓使用者自己選，直接建立管理資料區（仍會先確認）。
        if (needsSchema && SourceSheets.Count == 1 && SelectedSourceSheet is not null)
        {
            await InitializeSchemaAsync();
        }
        else if (needsSchema)
        {
            IsAdvancedOpen = true;
            StatusMessage = "請在「進階（管理員）」選擇原始回應工作表，再按「建立管理資料區」。";
        }
    }

    /// <summary>重新連線：清除本機登入後重新登入（可換帳號）。</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ReconnectAsync()
    {
        if (CanEndSession?.Invoke() == false) return;
        if (!_dialogs.Confirm("將登出目前的 Google 帳號，並開啟瀏覽器重新登入。可以在瀏覽器中選擇其他帳號。", "重新連線", "重新連線"))
        {
            return;
        }

        var signedOut = await RunAsync("正在登出…", async ct =>
        {
            await _auth.SignOutAsync(ct);
            IsConnected = false;
            IsSignedIn = false;
            AccountText = "尚未登入 Google";
        });
        if (signedOut)
        {
            StatusMessage = "已登出，請在封面重新登入 Google。";
        }
    }

    private bool CanInitialize() => IsSignedIn && !IsBusy && SelectedSourceSheet is not null;

    [RelayCommand(CanExecute = nameof(CanInitialize))]
    private async Task InitializeSchemaAsync()
    {
        if (!CanInitialize()) return;
        var source = SelectedSourceSheet;
        if (source is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                $"將以「{source.Title}」作為 Google Form 原始回應，並在同一份試算表建立或補齊程式使用的管理分頁。\n\n" +
                "原始回應工作表不會被修改；已存在的後台資料會保留。",
                "建立管理資料區", "建立管理資料區"))
        {
            return;
        }

        var initialized = await RunAsync("正在建立管理資料區…", async ct =>
        {
            var result = await _schema.InitializeAsync(source.SheetId, ct);
            DiagnosticsText = result.Describe();
            StatusMessage = "管理資料區已就緒，正在更新名單。";
            _logger.LogInformation("管理分頁初始化：新建 {Created} 張、補欄 {Added} 個", result.CreatedSheets.Count, result.AddedAdminColumns.Count);
        });
        if (initialized) ConnectionReady?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task SignOutAsync()
    {
        if (CanEndSession?.Invoke() == false) return;
        if (!_dialogs.Confirm("將登出目前的 Google 帳號並刪除這台電腦上的登入資料。之後需要重新登入才能讀取名單。", "登出 Google", "登出", danger: true))
        {
            return;
        }

        await RunAsync("正在登出…", async ct =>
        {
            await _auth.SignOutAsync(ct);
            IsConnected = false;
            IsSignedIn = false;
            AccountText = "尚未登入 Google";
            StatusMessage = "已登出。";
        });
    }

    [RelayCommand]
    private void ShowHealth()
    {
        DiagnosticsText = _workspace.Health?.Describe() ?? "尚未執行欄位檢查（請先更新名單）。";
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        AppPaths.EnsureCreated();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDirectory}\"") { UseShellExecute = true });
    }

    /// <summary>診斷資料：遮蔽後日誌＋版本／OS／schema 摘要。不含 token、Spreadsheet ID 或完整試算表內容。</summary>
    [RelayCommand]
    private void CreateDiagnosticsPackage()
    {
        var path = _dialogs.SaveFile($"RegistrationAdmin-診斷資料-{DateTime.Now:yyyyMMdd-HHmm}.zip", "ZIP 壓縮檔 (*.zip)|*.zip");
        if (path is null)
        {
            return;
        }

        var staging = Path.Combine(Path.GetTempPath(), "ra-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var logTarget = Directory.CreateDirectory(Path.Combine(staging, "logs"));
            if (Directory.Exists(AppPaths.LogDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "*.log"))
                {
                    // 日誌寫入中仍可讀取（FileShare.ReadWrite），內容再遮蔽一次。
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    File.WriteAllText(Path.Combine(logTarget.FullName, Path.GetFileName(file)), Redactor.Redact(reader.ReadToEnd()), Encoding.UTF8);
                }
            }

            var info = new StringBuilder();
            info.AppendLine(TechnicalVersionText);
            info.AppendLine($"OS：{Environment.OSVersion}｜64 位元：{Environment.Is64BitOperatingSystem}｜處理程序 64 位元：{Environment.Is64BitProcess}");
            info.AppendLine($"文化：{System.Globalization.CultureInfo.CurrentCulture.Name}｜時區：{TimeZoneInfo.Local.Id}");
            info.AppendLine($"已授權：{_auth.IsAuthorized}｜本機 token：{_settings.HasStoredToken}｜已設定試算表：{_settings.SpreadsheetId.Length > 0}");
            info.AppendLine($"已載入：{_workspace.IsLoaded}｜報名筆數：{_workspace.Registrations.Count}｜待人工連結：{_workspace.PendingRelinks.Count}｜碰撞：{_workspace.CollidedSources.Count}");
            info.AppendLine($"管理 schema：{_workspace.Config.SchemaVersion}｜表單 schema：{_workspace.Config.FormSchemaVersion}");
            info.AppendLine();
            info.AppendLine(_workspace.Health?.Describe() ?? "尚未執行來源欄位檢查。");
            if (LastTechnicalError.Length > 0)
            {
                info.AppendLine();
                info.AppendLine("最近一次錯誤：" + LastTechnicalError);
            }

            File.WriteAllText(Path.Combine(staging, "info.txt"), Redactor.Redact(info.ToString()), Encoding.UTF8);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            ZipFile.CreateFromDirectory(staging, path);
            DiagnosticsText = "已建立診斷資料：" + path;
            StatusMessage = "已建立診斷資料，可以寄給管理員。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "建立診斷資料失敗");
            StatusMessage = "無法建立診斷資料，請換一個儲存位置再試。";
        }
        finally
        {
            try
            {
                Directory.Delete(staging, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private bool CanCancelLogin() => CanCancelOperation && _operationCancellation is { IsCancellationRequested: false };

    [RelayCommand(CanExecute = nameof(CanCancelLogin))]
    private void CancelLogin()
    {
        StatusMessage = IsPickingSpreadsheet ? "正在取消選擇…" : "正在取消登入…";
        _operationCancellation?.Cancel();
        CancelLoginCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return false;
        }
        IsBusy = true;
        StatusMessage = message;
        // 瀏覽器授權最多等待 5 分鐘，避免使用者關掉瀏覽器後程式一直等待。
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _operationCancellation = timeout;
        try
        {
            await action(timeout.Token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "設定操作失敗：{Operation}", message);
            StatusMessage = ex is OperationCanceledException
                ? IsPickingSpreadsheet ? "選擇已取消或等候逾時，可以按「選擇試算表」重新嘗試。" : "登入已取消或等候逾時，可以按「登入 Google」重新嘗試。"
                : UserMessages.Describe(ex);
            LastTechnicalError = $"{DateTime.Now:yyyy-MM-dd HH:mm}　{message}\n{ex.GetType().Name}：{Redactor.Redact(ex.Message)}";
            DiagnosticsText = LastTechnicalError;
            return false;
        }
        finally
        {
            _operationCancellation = null;
            IsSigningIn = false;
            IsPickingSpreadsheet = false;
            IsBusy = false;
        }
    }

    private void SaveSettings()
    {
        try
        {
            _saveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "無法儲存本機設定");
        }
    }
}
