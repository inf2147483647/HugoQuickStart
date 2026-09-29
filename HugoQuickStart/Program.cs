using Avalonia;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HugoQuickStart.Services;

namespace HugoQuickStart;

class Program
{
    // 单实例命名互斥体（Local\ 会话级，无需提权），避免程序被重复启动。
    private const string SingleInstanceMutexName = @"Local\HugoQuickStart.SingleInstance";

    // ---- 高权限隐藏窗口的工作模式参数 ----
    private const string HideWindowOption = "--hide-window";
    private const string AsSystemFlag = "--as-system";
    private const string ElevateFlag = "--elevate";

    // 工作进程退出码
    private const int ExitOk = 0;
    private const int ExitWorkerFailed = 2;
    private const int ExitElevateFailed = 3;

    // 持有互斥体引用，防止被 GC 提前回收而失去单实例保护。
    private static Mutex? _singleInstanceMutex;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 高权限隐藏窗口的工作模式：不建界面、不占单实例互斥体，处理完立即退出。
        if (HandleWindowHideCommand(args))
            return;

        if (!AcquireSingleInstance())
        {
            LogService.Info("初始化", "检测到已有实例正在运行，本次启动已退出");
            return;
        }

        LogService.Info("初始化",
            $"应用启动（版本 {typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}）");

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

    /// <summary>
    /// 处理"高权限隐藏窗口"工作模式（由主程序在普通隐藏连续失败后触发）：
    ///   --hide-window &lt;hwnd&gt; --elevate    ：管理员实例：复制 SYSTEM 令牌并启动下述工作进程；
    ///   --hide-window &lt;hwnd&gt; --as-system  ：SYSTEM 工作进程：校验并隐藏目标窗口后退出。
    /// 返回 true 表示本次启动属于该模式且已处理完毕，调用方应直接退出（不进入正常界面流程）。
    /// </summary>
    private static bool HandleWindowHideCommand(string[] args)
    {
        var hwndText = GetOptionValue(args, HideWindowOption);
        if (hwndText == null)
            return false;

        var asSystem = HasFlag(args, AsSystemFlag);
        var elevate = HasFlag(args, ElevateFlag);
        if (!asSystem && !elevate)
            return false;

        if (!TryParseHwnd(hwndText, out var hwndValue))
        {
            LogService.Warn("提权拦截", $"无效的窗口句柄参数：{hwndText}");
            Environment.Exit(ExitWorkerFailed);
            return true;
        }

        if (asSystem)
        {
            // 双重保险：非 SYSTEM 令牌一律拒绝，避免该入口被普通权限进程直接利用
            if (!TrustedLauncher.IsSystem())
            {
                LogService.Warn("提权拦截", "工作进程未获得 SYSTEM 权限，已拒绝执行");
                Environment.Exit(ExitWorkerFailed);
                return true;
            }

            var hidden = SeewoWindowCloser.TryHide((uint)hwndValue, out var reason);
            LogService.Info("提权拦截",
                $"SYSTEM 工作进程：hwnd=0x{hwndValue:X} → {(hidden ? "已隐藏" : "未隐藏")}（{reason}）");
            Environment.Exit(hidden ? ExitOk : ExitWorkerFailed);
            return true;
        }

        // --elevate：管理员实例 → 取得 SYSTEM 令牌 → 以 SYSTEM 启动 as-system 工作进程
        var exePath = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrEmpty(exePath))
        {
            LogService.Warn("提权拦截", "无法获取当前程序路径，提权取消");
            Environment.Exit(ExitElevateFailed);
            return true;
        }

        var launched = TrustedLauncher.TryRunAsSystem(
            exePath, $"{HideWindowOption} {hwndValue} {AsSystemFlag}", out var exitCode, out var error);

        if (launched && exitCode == ExitOk)
        {
            LogService.Warn("提权拦截", $"已通过 SYSTEM 权限隐藏窗口 hwnd=0x{hwndValue:X}");
            Environment.Exit(ExitOk);
        }
        else
        {
            LogService.Error("提权拦截",
                $"提权隐藏窗口失败：{error}{(launched ? $"（工作进程退出码 {exitCode}）" : string.Empty)}");
            Environment.Exit(ExitElevateFailed);
        }

        return true;
    }

    /// <summary>读取形如 <c>--option value</c> 的参数值；不存在时返回 null。</summary>
    private static string? GetOptionValue(string[] args, string option)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>解析窗口句柄（支持十进制与 0x 十六进制）。</summary>
    private static bool TryParseHwnd(string text, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        // 窗口句柄为 32 位句柄值
        return parsed && value <= uint.MaxValue;
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
            LogService.Error("未处理异常", $"[{source}] {ex}");
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
