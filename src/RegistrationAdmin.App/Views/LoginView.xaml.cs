using System.Windows.Controls;
using System.Windows;

namespace RegistrationAdmin.App.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/RegistrationAdmin;component/Themes/Theme.xaml", UriKind.Relative),
        });
        InitializeComponent();
    }
}
