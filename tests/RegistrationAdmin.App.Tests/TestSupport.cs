using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Logging.Abstractions;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.App.ViewModels;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.GoogleSheets.Auth;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.App.Tests;

/// <summary>記錄呼叫並回傳預先設定答案的對話框。</summary>
internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmResult { get; set; } = true;

    public string? ReasonResult { get; set; } = "測試理由";

    public List<string> Confirms { get; } = new();

    public List<string> Errors { get; } = new();

    public void ShowError(string message, string title = "無法完成") => Errors.Add(message);

    public void ShowInfo(string message, string title = "訊息")
    {
    }

    public bool Confirm(string message, string title = "請確認", string confirmText = "確定", bool danger = false)
    {
        Confirms.Add(title);
        return ConfirmResult;
    }

    public string? AskReason(string title, string message, string reasonLabel, string confirmText, bool danger = false)
    {
        Confirms.Add(title);
        return ReasonResult;
    }

    public string? SaveFile(string defaultFileName, string filter) => null;

    public string? OpenFile(string filter) => null;
}

/// <summary>測試不連 Google：任何授權呼叫都失敗。</summary>
internal sealed class OfflineAuth : ISheetsServiceProvider
{
    public bool IsAuthorized => false;

    public string? AccountEmail => null;

    public Task<SheetsService> GetAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("測試中不連線");

    public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed record Harness(MainViewModel Main, FakeStore Store, RegistrationWorkspace Workspace, FakeDialogs Dialogs)
{
    public static readonly DateTime Now = new(2026, 10, 2, 10, 0, 0);

    public static async Task<Harness> CreateAsync(IEnumerable<Fixtures.Answer>? answers = null)
    {
        var store = new FakeStore(Fixtures.Table(answers ?? Fixtures.AtoE));
        var workspace = new RegistrationWorkspace(store, new FixedClock(), new SequentialIds(), "test");
        var dialogs = new FakeDialogs();
        var local = new LocalSettings();
        var auth = new OfflineAuth();
        var settings = new SettingsViewModel(local, auth, new SchemaManager(auth, local), workspace, dialogs,
            NullLogger<SettingsViewModel>.Instance);
        settings.IsSignedIn = true; // 此 harness 專門驗證登入後的報名操作。
        var main = new MainViewModel(workspace, settings, dialogs, NullLogger<MainViewModel>.Instance, () => Now);
        await main.RefreshCommand.ExecuteAsync(null);
        return new Harness(main, store, workspace, dialogs);
    }

    public Registration ByName(string name, string org) =>
        Workspace.Registrations.Single(r => r.Raw(LogicalFields.FullName) == name && r.Raw(LogicalFields.OrganizationName) == org);

    public RegistrationEditorViewModel Editor(Registration registration) =>
        new(registration, Workspace, Main.Options, Dialogs, () => Now.Date, loadHistory: false);
}
