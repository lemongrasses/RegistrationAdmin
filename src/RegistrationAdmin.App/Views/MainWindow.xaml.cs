using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using RegistrationAdmin.App.Infrastructure;
using RegistrationAdmin.App.ViewModels;

namespace RegistrationAdmin.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IDialogService _dialogs;
    private readonly UpdateViewModel _updates;
    private HelpWindow? _helpWindow;

    public MainWindow(MainViewModel viewModel, IDialogService dialogs, UpdateViewModel updates)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _dialogs = dialogs;
        DataContext = viewModel;
        updates.HasUnsavedChanges = () => viewModel.HasUnsavedChanges;
        UpdatePanel.DataContext = updates;
        _updates = updates;
        _viewModel.Settings.PropertyChanged += OnSessionChanged;
        Closed += (_, _) => _viewModel.Settings.PropertyChanged -= OnSessionChanged;
        FitToWorkArea();
        Loaded += OnLoaded;
        Closing += OnClosing;
        InputBindings.Add(new KeyBinding(new FocusSearchCommand(this), Key.F, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(HelpCommands.Open, OpenHelp));
    }

    private void OpenHelp(object sender, ExecutedRoutedEventArgs e)
    {
        var topic = e.Parameter as string ?? (!_viewModel.Settings.IsSignedIn ? "1" : _viewModel.CurrentPage switch
        {
            AppPage.Detail => "6",
            AppPage.Export => "12",
            AppPage.Settings => "1",
            _ => "4",
        });
        if (_helpWindow is null)
        {
            _helpWindow = new HelpWindow(_dialogs) { Owner = this };
            _helpWindow.Closed += (_, _) => _helpWindow = null;
            _helpWindow.Show();
        }
        _helpWindow.OpenTopic(topic);
        e.Handled = true;
    }

    /// <summary>1366×768／125% 縮放時預設尺寸會超出螢幕：改為填滿工作區。</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (Width > area.Width || Height > area.Height)
        {
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            WindowState = WindowState.Maximized;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.Settings.RestoreSessionAsync();
        await _updates.CheckInBackgroundAsync();
    }

    private async void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsSignedIn) && _viewModel.Settings.IsSignedIn)
            await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.HasUnsavedChanges
            && !_dialogs.Confirm("這筆報名有尚未儲存的變更。關閉程式會捨棄這些變更。", "要關閉程式嗎？", "捨棄變更並關閉", danger: true))
        {
            e.Cancel = true;
        }
        if (!e.Cancel && _viewModel.Settings.CancelLoginCommand.CanExecute(null))
            _viewModel.Settings.CancelLoginCommand.Execute(null);
    }

    private sealed class FocusSearchCommand(MainWindow window) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => window._viewModel.Settings.IsSignedIn;

        public void Execute(object? parameter)
        {
            if (window._viewModel.CurrentPage == AppPage.List)
            {
                window.ListPage.FocusSearch();
            }
        }
    }
}
