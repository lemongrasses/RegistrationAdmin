using System.IO;
using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Logging.Abstractions;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.App.ViewModels;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.Core.Diagnostics;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.GoogleSheets.Auth;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.App.Tests;

public sealed class SettingsConnectionTests
{
    [Fact]
    public void ValidUrlWithoutConnection_KeepsFirstTimeLoginVisible()
    {
        var auth = new TestAuth();
        var settings = Create(auth);

        Assert.NotNull(SpreadsheetIdParser.Parse(settings.SpreadsheetInput));
        Assert.True(settings.NeedsSetup);
        Assert.Equal(0, auth.Attempts);
        Assert.True(settings.ConnectCommand.CanExecute(null));

        var changes = new List<string?>();
        settings.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        settings.IsConnected = true;
        Assert.False(settings.NeedsSetup);
        Assert.Contains(nameof(SettingsViewModel.NeedsSetup), changes);
        settings.IsConnected = false;
        Assert.True(settings.NeedsSetup);
    }

    [Fact]
    public async Task CancelBrowserLogin_ReleasesBusyState_AndAllowsImmediateRetry()
    {
        var auth = new TestAuth { WaitForCancellation = true };
        var settings = Create(auth);
        var attempt = settings.LoginCommand.ExecuteAsync(null);
        await auth.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(settings.IsBusy);
        Assert.True(settings.IsSigningIn);
        Assert.False(settings.ConnectCommand.CanExecute(null));
        Assert.True(settings.CancelLoginCommand.CanExecute(null));
        settings.CancelLoginCommand.Execute(null);
        await attempt.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(settings.IsBusy);
        Assert.False(settings.IsSigningIn);
        Assert.True(settings.NeedsSetup);
        Assert.True(settings.ConnectCommand.CanExecute(null));
        Assert.False(settings.CancelLoginCommand.CanExecute(null));
        Assert.Contains("重新嘗試", settings.StatusMessage);
        Assert.Empty(settings.SourceSheets);

        auth.WaitForCancellation = false;
        await settings.LoginCommand.ExecuteAsync(null);
        Assert.Equal(2, auth.Attempts);
        Assert.False(settings.IsBusy);
    }

    [Fact]
    public async Task BrowserFailure_AllowsAnotherLoginAttempt()
    {
        var auth = new TestAuth();
        var settings = Create(auth);
        await settings.LoginCommand.ExecuteAsync(null);

        Assert.False(settings.IsBusy);
        Assert.False(settings.IsSigningIn);
        Assert.True(settings.NeedsSetup);
        Assert.True(settings.ConnectCommand.CanExecute(null));
        await settings.LoginCommand.ExecuteAsync(null);
        Assert.Equal(2, auth.Attempts);
    }

    [Fact]
    public async Task FailedSignOut_DoesNotContinueToLogin()
    {
        var auth = new TestAuth { FailSignOut = true };
        var settings = Create(auth);
        await settings.ReconnectCommand.ExecuteAsync(null);
        Assert.Equal(0, auth.Attempts);
        Assert.False(settings.IsBusy);
    }

    [Fact]
    public async Task Startup_RequiresLogin_EvenWithSpreadsheetConfigured()
    {
        var auth = new TestAuth();
        var settings = Create(auth);
        Assert.False(settings.IsSignedIn);
        Assert.False(settings.CanAutoConnect);
        await settings.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(0, auth.Attempts);
        Assert.Contains("登入封面", settings.StatusMessage);
    }

    [Fact]
    public async Task Startup_ValidSavedAuthorization_SkipsCoverWithoutInteractiveLogin()
    {
        var auth = new TestAuth { RestoreSaved = true, Succeed = true };
        var settings = Create(auth, "");
        Assert.True(settings.IsRestoringSession);
        Assert.False(settings.ShowLoginCover);
        await settings.RestoreSessionAsync();
        Assert.True(settings.IsSignedIn);
        Assert.False(settings.IsRestoringSession);
        Assert.False(settings.ShowLoginCover);
        Assert.Equal("fixture@example.test", settings.AccountText);
        Assert.Equal(0, auth.Attempts);
        Assert.Equal(1, auth.RestoreAttempts);
        await settings.RestoreSessionAsync();
        Assert.Equal(1, auth.RestoreAttempts);
    }

    [Fact]
    public async Task Startup_NoSavedAuthorization_ShowsCoverWithoutOpeningBrowser()
    {
        var auth = new TestAuth();
        var settings = Create(auth);
        await settings.RestoreSessionAsync();
        Assert.False(settings.IsSignedIn);
        Assert.True(settings.ShowLoginCover);
        Assert.False(settings.IsBusy);
        Assert.Equal(0, auth.Attempts);
        Assert.Equal("", settings.StatusMessage);
    }

    [Fact]
    public async Task Startup_RestoreFails_ShowsCoverAndAllowsManualRetry()
    {
        var auth = new TestAuth { FailRestore = true, Succeed = true };
        var settings = Create(auth);
        await settings.RestoreSessionAsync();
        Assert.False(settings.IsSignedIn);
        Assert.True(settings.ShowLoginCover);
        Assert.Equal(0, auth.Attempts);
        Assert.False(settings.IsBusy);
        Assert.True(settings.LoginCommand.CanExecute(null));
        await settings.LoginCommand.ExecuteAsync(null);
        Assert.True(settings.IsSignedIn);
        Assert.False(settings.ShowLoginCover);
    }

    [Fact]
    public async Task Login_WithoutSpreadsheet_UnlocksSessionWithoutReadingSheet()
    {
        var auth = new TestAuth { Succeed = true };
        var settings = Create(auth, "");
        await settings.LoginCommand.ExecuteAsync(null);
        Assert.True(settings.IsSignedIn);
        Assert.False(settings.IsConnected);
        Assert.False(settings.CanAutoConnect);
        Assert.Equal("fixture@example.test", settings.AccountText);
        Assert.Empty(settings.SourceSheets);
        Assert.False(settings.IsBusy);
    }

    [Fact]
    public async Task Logout_ClearsPreviousAccountData_AndBlocksShortcuts()
    {
        var harness = await Harness.CreateAsync();
        Assert.NotEmpty(harness.Main.Rows);
        await harness.Main.Settings.SignOutCommand.ExecuteAsync(null);
        Assert.False(harness.Main.Settings.IsSignedIn);
        Assert.Empty(harness.Main.Rows);
        Assert.Empty(harness.Workspace.Registrations);
        Assert.False(harness.Workspace.IsLoaded);
        Assert.False(harness.Main.RefreshCommand.CanExecute(null));
        Assert.False(harness.Main.ExportCommand.CanExecute(null));
        Assert.False(harness.Main.SaveCommand.CanExecute(null));
        await harness.Main.RefreshCommand.ExecuteAsync(null);
        Assert.False(harness.Workspace.IsLoaded);
    }

    [Fact]
    public async Task BusyWorkspace_DoesNotAllowLogoutDuringSync()
    {
        var harness = await Harness.CreateAsync();
        harness.Main.IsBusy = true;
        await harness.Main.Settings.SignOutCommand.ExecuteAsync(null);
        Assert.True(harness.Main.Settings.IsSignedIn);
        Assert.True(harness.Workspace.IsLoaded);
    }

    private static SettingsViewModel Create(TestAuth auth, string spreadsheetInput = "https://docs.google.com/spreadsheets/d/test-spreadsheet/edit")
    {
        // Constructor values avoid writing to the real user's local settings.
        var local = new LocalSettings { SpreadsheetInput = spreadsheetInput };
        var workspace = new RegistrationWorkspace(new FakeStore(Fixtures.Table(Fixtures.AtoE)),
            new FixedClock(), new SequentialIds(), "test");
        return new SettingsViewModel(local, auth, new SchemaManager(auth, local), workspace,
            new FakeDialogs(), NullLogger<SettingsViewModel>.Instance);
    }

    private sealed class TestAuth : ISheetsServiceProvider
    {
        public bool IsAuthorized => false;
        public string? AccountEmail => Succeed ? "fixture@example.test" : null;
        public bool Succeed { get; set; }
        public bool RestoreSaved { get; set; }
        public bool FailRestore { get; set; }
        public int RestoreAttempts { get; private set; }
        public Task<SheetsService?> TryRestoreAsync(CancellationToken cancellationToken)
        {
            RestoreAttempts++;
            if (FailRestore) throw new InvalidOperationException("保存的 Google 授權失效");
            return Task.FromResult<SheetsService?>(RestoreSaved ? new SheetsService() : null);
        }
        public int Attempts { get; private set; }
        public bool WaitForCancellation { get; set; }
        public bool FailSignOut { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SheetsService> GetAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            Started.TrySetResult();
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (Succeed) return new SheetsService();
            throw new InvalidOperationException("模擬瀏覽器登入失敗");
        }

        public Task SignOutAsync(CancellationToken cancellationToken) => FailSignOut
            ? Task.FromException(new IOException("模擬登出失敗"))
            : Task.CompletedTask;
    }
}
