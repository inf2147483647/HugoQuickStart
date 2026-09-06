using Avalonia;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HugoQuickStart;

class Program
{
    // 单实例命名互斥体（Local\ 会话级，无需提权），避免程序被重复启动。
    private const string SingleInstanceMutexName = @"Local\HugoQuickStart.SingleInstance";

    // 持有互斥体引用，防止被 GC 提前回收而失去单实例保护。
    private static Mutex? _singleInstanceMutex;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (!AcquireSingleInstance())
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogCrash("AppDomain.UnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash("Main", ex);
            throw;
        }
    }

    /// <summary>尝试抢占单实例互斥体。若已有实例持有则返回 false，否则获取所有权并返回 true。</summary>
    private static bool AcquireSingleInstance()
    {
        try
        {
            // initialOwnership=false：创建时不自动持有，随后用 WaitOne(0) 尝试获取。
            _singleInstanceMutex = new Mutex(false, SingleInstanceMutexName, out var createdNew);

            // 已存在且当前没有进程持有（前一实例已正常退出）→ 重新接管。
            if (!createdNew && _singleInstanceMutex.WaitOne(TimeSpan.Zero))
                return true;

            // 新建成功 → WaitOne 会立即成功；或已有实例持有 → WaitOne 返回 false。
            return _singleInstanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // 上一实例异常退出，互斥体被释放，我们已接管所有权，允许启动。
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 无权限使用命名互斥体时放弃单实例限制，继续正常启动。
            return true;
        }
    }

    /// <summary>把未处理异常写入程序安装目录下的 crash.log，便于定位崩溃原因。</summary>
    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignore logging failures
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
