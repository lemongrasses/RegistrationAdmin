using Microsoft.Extensions.Logging.Abstractions;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.App.ViewModels;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.GoogleSheets.Auth;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.App.Tests;

public sealed class SettingsPickerTests
{
    [Fact]
    public async Task SelectingNewSheet_ClearsOldWorkspace_AndWaitsForExplicitConnection()
    {
        var picker = new FakePicker { Result = new("new-sheet", "新的報名資料") };
        var (main, workspace) = Create(picker);
        await main.RefreshCommand.ExecuteAsync(null);
        Assert.True(workspace.IsLoaded);
        await main.Settings.PickSpreadsheetCommand.ExecuteAsync(null);
        Assert.Equal("https://docs.google.com/spreadsheets/d/new-sheet/edit", main.Settings.SpreadsheetInput);
        Assert.Equal("新的報名資料", main.Settings.SpreadsheetTitle);
        Assert.True(main.Settings.IsSignedIn);
        Assert.False(main.Settings.IsConnected);
        Assert.False(workspace.IsLoaded);
        Assert.Empty(main.Rows);
        Assert.Equal(AppPage.Settings, main.CurrentPage);
    }

    [Fact]
    public async Task BrowserCancel_PreservesOriginalSheetAndWorkspace()
    {
        var (main, workspace) = Create(new FakePicker());
        await main.RefreshCommand.ExecuteAsync(null);
        await main.Settings.PickSpreadsheetCommand.ExecuteAsync(null);
        Assert.True(workspace.IsLoaded);
        Assert.NotEmpty(main.Rows);
        Assert.Contains("original-sheet", main.Settings.SpreadsheetInput);
        Assert.Contains("原本", main.Settings.StatusMessage);
    }

    [Fact]
    public async Task CancelPendingPicker_AllowsImmediateRetry()
    {
        var picker = new FakePicker { WaitForCancellation = true };
        var (main, _) = Create(picker);
        var pending = main.Settings.PickSpreadsheetCommand.ExecuteAsync(null);
        await picker.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(main.Settings.CancelLoginCommand.CanExecute(null));
        Assert.Equal("取消選擇", main.Settings.CancelOperationText);
        Assert.False(main.RefreshCommand.CanExecute(null));
        main.Settings.CancelLoginCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(main.Settings.IsBusy);
        Assert.False(main.Settings.IsPickingSpreadsheet);
        Assert.True(main.Settings.PickSpreadsheetCommand.CanExecute(null));
        picker.WaitForCancellation = false;
        await main.Settings.PickSpreadsheetCommand.ExecuteAsync(null);
        Assert.Equal(2, picker.Attempts);
    }

    [Fact]
    public async Task FailedPicker_PreservesSettingAndAllowsRetry()
    {
        var picker = new FakePicker { Fail = true };
        var (main, _) = Create(picker);
        await main.Settings.PickSpreadsheetCommand.ExecuteAsync(null);
        Assert.False(main.Settings.IsBusy);
        Assert.Contains("original-sheet", main.Settings.SpreadsheetInput);
        Assert.True(main.Settings.PickSpreadsheetCommand.CanExecute(null));
    }

    [Fact]
    public async Task NotLoggedIn_DoesNotOpenPicker()
    {
        var picker = new FakePicker();
        var (main, _) = Create(picker);
        main.Settings.IsSignedIn = false;
        Assert.False(main.Settings.PickSpreadsheetCommand.CanExecute(null));
        await main.Settings.PickSpreadsheetCommand.ExecuteAsync(null);
        Assert.Equal(0, picker.Attempts);
    }

    private static (MainViewModel Main, RegistrationWorkspace Workspace) Create(FakePicker picker)
    {
        var local = new LocalSettings { SpreadsheetInput = "original-sheet" };
        var auth = new OfflineAuth();
        var workspace = new RegistrationWorkspace(new FakeStore(Fixtures.Table(Fixtures.AtoE)), new FixedClock(), new SequentialIds(), "test");
        var dialogs = new FakeDialogs();
        var settings = new SettingsViewModel(local, auth, new SchemaManager(auth, local), workspace, dialogs,
            NullLogger<SettingsViewModel>.Instance, picker, saveSettings: () => { });
        settings.IsSignedIn = true;
        return (new MainViewModel(workspace, settings, dialogs, NullLogger<MainViewModel>.Instance), workspace);
    }

    private sealed class FakePicker : ISpreadsheetPicker
    {
        public PickedSpreadsheet? Result { get; set; }
        public bool WaitForCancellation { get; set; }
        public bool Fail { get; set; }
        public int Attempts { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PickedSpreadsheet?> PickAsync(string accountEmail, CancellationToken cancellationToken)
        {
            Attempts++;
            Started.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Fail) throw new InvalidOperationException("模擬 Google 選擇器失敗");
            return Result;
        }
    }
}
