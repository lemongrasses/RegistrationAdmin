using System.Windows.Input;

namespace RegistrationAdmin.App.Views;

public static class HelpCommands
{
    public static RoutedUICommand Open { get; } = new("使用手冊", nameof(Open), typeof(HelpCommands));
}
