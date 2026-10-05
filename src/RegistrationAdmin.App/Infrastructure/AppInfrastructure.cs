using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using RegistrationAdmin.Core.Diagnostics;
using RegistrationAdmin.GoogleSheets.Auth;
using Serilog.Events;
using Serilog.Formatting;

namespace RegistrationAdmin.App.Infrastructure;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegistrationAdmin");

    public static string LogDirectory => Path.Combine(Root, "logs");

    public static string TokenDirectory => Path.Combine(Root, "tokens");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(TokenDirectory);
    }
}

public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";
}

/// <summary>
/// 本機設定：只保存 OAuth client 檔路徑與試算表網址／ID，不保存任何報名資料。
/// </summary>
public sealed class LocalSettings : IConnectionSettings
{
    /// <summary>隨程式發佈的 OAuth 用戶端檔（放在 exe 旁），一般使用者不需設定。</summary>
    public static string BundledClientSecretPath { get; } = Path.Combine(AppContext.BaseDirectory, "google-oauth-client.json");

    /// <summary>進階：維護人員自行指定的 OAuth 用戶端檔；空白時使用隨程式發佈的檔案。</summary>
    public string ClientSecretPath { get; set; } = "";

    /// <summary>實際使用的 OAuth 用戶端檔：自訂檔存在時優先，否則使用隨程式發佈的檔案。</summary>
    public string EffectiveClientSecretPath =>
        !string.IsNullOrWhiteSpace(ClientSecretPath) && File.Exists(ClientSecretPath) ? ClientSecretPath : BundledClientSecretPath;

    string IConnectionSettings.ClientSecretPath => EffectiveClientSecretPath;

    /// <summary>使用者貼上的試算表網址或 ID（原文）。</summary>
    public string SpreadsheetInput { get; set; } = "";

    public string SpreadsheetId => SpreadsheetIdParser.Parse(SpreadsheetInput) ?? "";

    string IConnectionSettings.TokenDirectory => AppPaths.TokenDirectory;

    public bool HasStoredToken =>
        Directory.Exists(AppPaths.TokenDirectory) && Directory.EnumerateFiles(AppPaths.TokenDirectory, "*.bin").Any();

    public bool IsConfigured => File.Exists(EffectiveClientSecretPath) && SpreadsheetId.Length > 0;

    public static LocalSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var dto = JsonSerializer.Deserialize<SettingsDto>(json);
                if (dto is not null)
                {
                    return new LocalSettings { ClientSecretPath = dto.ClientSecretPath ?? "", SpreadsheetInput = dto.SpreadsheetInput ?? "" };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 設定檔損毀時以空白設定啟動。
        }

        return new LocalSettings();
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(new SettingsDto { ClientSecretPath = ClientSecretPath, SpreadsheetInput = SpreadsheetInput },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.SettingsFile, json);
    }

    private sealed class SettingsDto
    {
        public string? ClientSecretPath { get; set; }

        public string? SpreadsheetInput { get; set; }
    }
}

/// <summary>Serilog 文字格式：訊息與例外一律經過 <see cref="Redactor"/> 遮蔽後才寫檔。</summary>
public sealed class RedactingTextFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        output.Write(logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        output.Write(" [");
        output.Write(logEvent.Level switch
        {
            LogEventLevel.Verbose => "VRB",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            _ => "FTL",
        });
        output.Write("] ");
        output.Write(Redactor.Redact(logEvent.RenderMessage(CultureInfo.InvariantCulture)));
        if (logEvent.Exception is not null)
        {
            output.Write(" | ");
            output.Write(Redactor.Redact(logEvent.Exception.ToString()));
        }

        output.WriteLine();
    }
}

public interface IDialogService
{
    void ShowError(string message, string title = "無法完成");

    void ShowInfo(string message, string title = "訊息");

    /// <summary>一般確認（是／否）。</summary>
    bool Confirm(string message, string title = "請確認", string confirmText = "確定", bool danger = false);

    /// <summary>需要填寫理由的確認；取消時回傳 null。</summary>
    string? AskReason(string title, string message, string reasonLabel, string confirmText, bool danger = false);

    string? SaveFile(string defaultFileName, string filter);

    string? OpenFile(string filter);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public void ShowError(string message, string title = "無法完成") =>
        Views.ConfirmDialog.Show(Owner, title, message, "知道了", cancelText: null, danger: false, reasonLabel: null, out _);

    public void ShowInfo(string message, string title = "訊息") =>
        Views.ConfirmDialog.Show(Owner, title, message, "知道了", cancelText: null, danger: false, reasonLabel: null, out _);

    public bool Confirm(string message, string title = "請確認", string confirmText = "確定", bool danger = false) =>
        Views.ConfirmDialog.Show(Owner, title, message, confirmText, "取消", danger, reasonLabel: null, out _);

    public string? AskReason(string title, string message, string reasonLabel, string confirmText, bool danger = false) =>
        Views.ConfirmDialog.Show(Owner, title, message, confirmText, "取消", danger, reasonLabel, out var reason) ? reason : null;

    public string? SaveFile(string defaultFileName, string filter)
    {
        var dialog = new SaveFileDialog { FileName = defaultFileName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return ShowDialog(dialog) ? dialog.FileName : null;
    }

    public string? OpenFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        return ShowDialog(dialog) ? dialog.FileName : null;
    }

    private static bool ShowDialog(Microsoft.Win32.CommonDialog dialog) =>
        (Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;
}
