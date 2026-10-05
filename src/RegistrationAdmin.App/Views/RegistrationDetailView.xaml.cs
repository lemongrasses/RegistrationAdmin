using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RegistrationAdmin.App.Views;

public partial class RegistrationDetailView : UserControl
{
    public RegistrationDetailView()
    {
        InitializeComponent();
    }

    /// <summary>「其他動作…」以選單列出低頻／高風險動作（每個動作執行前都會再確認）。</summary>
    private void OnOtherActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
