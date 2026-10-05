using Velopack;

namespace RegistrationAdmin.App;

/// <summary>
/// 自訂進入點：Velopack 安裝／更新／解除安裝時會以特殊參數啟動程式，
/// 必須在 WPF 啟動前先交給 <see cref="VelopackApp"/> 處理（處理完會直接結束處理程序）。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
