using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RegistrationAdmin.App.Infrastructure;
using Velopack;
using Velopack.Sources;

namespace RegistrationAdmin.App.ViewModels;

/// <summary>
/// 程式更新：啟動後在背景檢查更新來源，有新版就先下載；下載完成後在左下角顯示「重新啟動並更新」。
/// 更新來源由隨程式發佈的 update-feed.txt 決定（共用資料夾路徑或 GitHub Releases 網址）。
/// 免安裝版（zip）與開發中執行時不檢查更新。
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly ILogger<UpdateViewModel> _logger;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public UpdateViewModel(IDialogService dialogs, ILogger<UpdateViewModel> logger)
    {
        _dialogs = dialogs;
        _logger = logger;
        var feed = UpdateFeed.Read();
        if (feed.Length == 0)
        {
            return;
        }

        try
        {
            var manager = new UpdateManager(UpdateFeed.CreateSource(feed));
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "無法建立更新管理員");
        }
    }

    /// <summary>主畫面有未儲存的變更時回傳 true；更新前會先詢問。</summary>
    public Func<bool> HasUnsavedChanges { get; set; } = () => false;

    /// <summary>已安裝版且有設定更新來源，才顯示「檢查更新」。</summary>
    public bool CanUpdate => _manager is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    public bool HasStatus => StatusText.Length > 0;

    [ObservableProperty]
    private bool _isUpdateReady;

    [ObservableProperty]
    private string _readyText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    /// <summary>啟動時的背景檢查：沒有新版或失敗時不打擾使用者。</summary>
    public async Task CheckInBackgroundAsync()
    {
        if (_manager is null)
        {
            return;
        }

        await CheckCoreAsync(userInitiated: false);
    }

    private bool CanCheck() => _manager is not null && !IsChecking;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => CheckCoreAsync(userInitiated: true);

    private async Task CheckCoreAsync(bool userInitiated)
    {
        if (_manager is null || IsUpdateReady)
        {
            return;
        }

        IsChecking = true;
        if (userInitiated)
        {
            StatusText = "正在檢查更新…";
        }

        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                StatusText = userInitiated ? "已是最新版本。" : "";
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            StatusText = $"正在下載新版本 {version}…";
            await _manager.DownloadUpdatesAsync(update);
            _pending = update;
            ReadyText = $"新版本 {version} 已下載";
            IsUpdateReady = true;
            StatusText = "";
            _logger.LogInformation("已下載更新 {Version}", version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "檢查或下載更新失敗");
            StatusText = userInitiated ? "目前無法檢查更新（更新來源無法連線）。稍後再試即可，不影響使用。" : "";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void RestartToUpdate()
    {
        if (_manager is null || _pending is null)
        {
            return;
        }

        var message = HasUnsavedChanges()
            ? "程式將關閉並安裝新版本，完成後自動重新開啟。\n\n這筆報名有尚未儲存的變更，更新會捨棄這些變更。"
            : "程式將關閉並安裝新版本，完成後自動重新開啟。登入狀態與設定都會保留。";
        if (!_dialogs.Confirm(message, "更新程式", "重新啟動並更新", danger: HasUnsavedChanges()))
        {
            return;
        }

        _logger.LogInformation("套用更新 {Version} 並重新啟動", _pending.TargetFullRelease.Version);
        Serilog.Log.CloseAndFlush();
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}

/// <summary>更新來源設定：exe 旁的 update-feed.txt，第一個非註解行就是來源；空白表示不檢查更新。</summary>
public static class UpdateFeed
{
    public const string FileName = "update-feed.txt";

    public static string Read()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                return "";
            }

            return File.ReadLines(path)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>https://github.com/… 使用 GitHub Releases；其他網址或資料夾路徑（含 \\server\share）直接讀取。</summary>
    public static IUpdateSource CreateSource(string feed) =>
        feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? new GithubSource(feed, accessToken: null, prerelease: false)
            : feed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? new SimpleWebSource(feed)
                : new SimpleFileSource(new DirectoryInfo(Environment.ExpandEnvironmentVariables(feed)));
}
