using System.Windows.Controls;
using System.Windows.Input;
using RegistrationAdmin.App.ViewModels;

namespace RegistrationAdmin.App.Views;

public partial class RegistrationListView : UserControl
{
    public RegistrationListView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 視窗矮時（例如 1366×768、125% 縮放）先收起摘要卡與名單標題，讓名單至少顯示數筆。
    /// 摘要卡的數字仍可在左側「待處理」數字與名單底部筆數看到。
    /// </summary>
    private void OnSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        var height = e.NewSize.Height;
        CardsPanel.Visibility = height < 720 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        ListHeader.Visibility = height < 600 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>點整列即開啟詳細資料。</summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { DataContext: RegistrationRowViewModel row } && DataContext is MainViewModel vm)
        {
            vm.OpenRowCommand.Execute(row);
        }
    }

    /// <summary>Enter 開啟目前選取的報名。</summary>
    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel { SelectedRow: { } row } vm)
        {
            vm.OpenRowCommand.Execute(row);
            e.Handled = true;
        }
    }
}
