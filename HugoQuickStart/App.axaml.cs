using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace HugoQuickStart;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // 系统关机/注销时 Avalonia 会先触发 ShutdownRequested，再依次关闭各窗口；
            // 此时必须跳过退出确认框，否则 Closing 里取消关闭会阻塞系统关机。
            // 本程序不会主动调用 Shutdown()，该事件实际只在系统关机/注销时触发。
            desktop.ShutdownRequested += (_, _) => mainWindow.AllowSystemShutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}