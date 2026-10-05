using System.Windows;
using System.Windows.Controls;

namespace RegistrationAdmin.App.Views;

/// <summary>確認對話框：Enter 確認、Esc 取消；需要理由時未填寫不能確認。</summary>
public partial class ConfirmDialog : Window
{
    private bool _requiresReason;

    private ConfirmDialog()
    {
        InitializeComponent();
    }

    public static bool Show(Window? owner, string title, string message, string confirmText, string? cancelText, bool danger,
        string? reasonLabel, out string reason)
    {
        var dialog = new ConfirmDialog
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        if (danger)
        {
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerButton");
        }

        if (cancelText is null)
        {
            dialog.CancelButton.Visibility = Visibility.Collapsed;
            dialog.ConfirmButton.IsCancel = true;
        }
        else
        {
            dialog.CancelButton.Content = cancelText;
        }

        if (reasonLabel is not null)
        {
            dialog._requiresReason = true;
            dialog.ReasonPanel.Visibility = Visibility.Visible;
            dialog.ReasonLabel.Text = reasonLabel;
            dialog.ConfirmButton.IsEnabled = false;
            dialog.Loaded += (_, _) => dialog.ReasonBox.Focus();
        }
        else
        {
            // 危險動作預設停在「取消」，避免誤按 Enter。
            dialog.Loaded += (_, _) => (danger && cancelText is not null ? dialog.CancelButton : dialog.ConfirmButton).Focus();
        }

        var ok = dialog.ShowDialog() == true;
        reason = dialog.ReasonBox.Text.Trim();
        return ok;
    }

    private void OnReasonChanged(object sender, TextChangedEventArgs e) =>
        ConfirmButton.IsEnabled = !_requiresReason || ReasonBox.Text.Trim().Length > 0;

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
