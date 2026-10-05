using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.App.ViewModels;
using RegistrationAdmin.App.Views;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.GoogleSheets.Auth;
using Serilog;

namespace RegistrationAdmin.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                new RedactingTextFormatter(),
                Path.Combine(AppPaths.LogDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "未處理的例外");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未觀察的工作例外");
            args.SetObserved();
        };

        try
        {
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices(RegisterServices)
                .Build();
            await _host.StartAsync();

            Log.Information("啟動 RegistrationAdmin {Version}", AppInfo.Version);
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "啟動失敗");
            MessageBox.Show("程式啟動失敗，詳細資訊已寫入日誌：" + AppPaths.LogDirectory, "RegistrationAdmin", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(_ => LocalSettings.Load());
        services.AddSingleton<IConnectionSettings>(sp => sp.GetRequiredService<LocalSettings>());
        services.AddSingleton<GoogleSheetsAuthorizer>();
        services.AddSingleton<ISheetsServiceProvider>(sp => sp.GetRequiredService<GoogleSheetsAuthorizer>());
        services.AddSingleton<SchemaManager>();
        services.AddSingleton<ISpreadsheetPicker, GoogleSpreadsheetPicker>();
        services.AddSingleton<IRegistrationStore, GoogleSheetsRegistrationStore>();
        services.AddSingleton<IClock, TaipeiClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddSingleton(sp => new RegistrationWorkspace(
            sp.GetRequiredService<IRegistrationStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IIdGenerator>(),
            AppInfo.Version));
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 執行緒未處理的例外");
        MessageBox.Show(
            "發生未預期的錯誤。尚未儲存的修改仍在畫面上，請先嘗試儲存；詳細資訊已寫入遮蔽後的日誌。",
            "RegistrationAdmin",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
