using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RegistrationAdmin.App.Views;
using RegistrationAdmin.App.ViewModels;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;

namespace RegistrationAdmin.App.Tests;

public sealed class LoginViewTests
{
    [Fact]
    public void Settings_RendersPickerAndUrlFallback_AfterLogin()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var local = new LocalSettings();
                var auth = new OfflineAuth();
                var workspace = new RegistrationWorkspace(new FakeStore(Fixtures.Table(Fixtures.AtoE)), new FixedClock(), new SequentialIds(), "test");
                var settings = new SettingsViewModel(local, auth, new SchemaManager(auth, local), workspace,
                    new FakeDialogs(), NullLogger<SettingsViewModel>.Instance, new NoOpPicker());
                settings.IsSignedIn = true;
                settings.IsRestoringSession = false;
                settings.AccountText = "已登入 Google";
                var view = new SettingsView { DataContext = settings };
                view.FontFamily = (FontFamily)view.Resources["UiFont"];
                view.FontSize = 14;
                var root = new Border { Background = (Brush)view.Resources["PageBrush"], Child = view };
                root.Measure(new Size(1000, 820));
                root.Arrange(new Rect(0, 0, 1000, 820));
                root.UpdateLayout();
                Assert.Contains(Descendants<Button>(view), b => Equals(b.Content, "選擇試算表…") && b.ActualWidth > 0 && b.IsEnabled);
                Assert.Contains(Descendants<TextBox>(view), b => b.ActualWidth > 0);
                var destination = Environment.GetEnvironmentVariable("RA_SETTINGS_QA_PATH");
                if (!string.IsNullOrEmpty(destination))
                {
                    var bitmap = new RenderTargetBitmap(1000, 820, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = System.IO.File.Create(destination);
                    encoder.Save(output);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "試算表選擇畫面渲染逾時。");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class NoOpPicker : RegistrationAdmin.GoogleSheets.Auth.ISpreadsheetPicker
    {
        public Task<RegistrationAdmin.GoogleSheets.Auth.PickedSpreadsheet?> PickAsync(string accountEmail, CancellationToken cancellationToken) =>
            Task.FromResult<RegistrationAdmin.GoogleSheets.Auth.PickedSpreadsheet?>(null);
    }

    [Fact]
    public void Cover_RendersAtMinimumWindowSize_WithoutGoogleOrRegistrationData()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new LoginView();
                var local = new LocalSettings();
                var auth = new OfflineAuth();
                var workspace = new RegistrationWorkspace(new FakeStore(Fixtures.Table(Fixtures.AtoE)), new FixedClock(), new SequentialIds(), "test");
                var settings = new SettingsViewModel(local, auth, new SchemaManager(auth, local), workspace,
                    new FakeDialogs(), NullLogger<SettingsViewModel>.Instance);
                view.DataContext = settings;
                view.FontFamily = (FontFamily)view.Resources["UiFont"];
                view.FontSize = 14;
                view.CommandBindings.Add(new System.Windows.Input.CommandBinding(HelpCommands.Open, (_, _) => { }));
                var root = new Border { Background = (Brush)view.Resources["PageBrush"], Child = view };
                root.Measure(new Size(960, 580));
                root.Arrange(new Rect(0, 0, 960, 580));
                root.UpdateLayout();
                var buttons = Descendants<Button>(view).ToList();
                Assert.Contains(buttons, b => Equals(b.Content, "登入 Google") && b.ActualWidth > 0);
                Assert.Contains(buttons, b => Equals(b.Content, "使用手冊（F1）"));
                Assert.Contains(buttons, b => Equals(b.Content, "取消登入") && b.Visibility == Visibility.Collapsed);
                var destination = Environment.GetEnvironmentVariable("RA_LOGIN_QA_PATH");
                if (!string.IsNullOrEmpty(destination))
                {
                    // Only this fixture-free cover is rendered; no account or registration data.
                    root.Measure(new Size(1100, 820));
                    root.Arrange(new Rect(0, 0, 1100, 820));
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1100, 820, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = System.IO.File.Create(destination);
                    encoder.Save(output);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "登入封面渲染逾時。");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
