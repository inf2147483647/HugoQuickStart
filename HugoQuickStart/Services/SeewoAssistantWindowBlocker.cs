using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HugoQuickStart.Services;

/// <summary>
/// 拦截希沃服务助手（SeewoServiceAssistant.exe）的右下角悬浮窗。
/// 采用“窗口事件钩子 + 周期扫描”双保险：
///   1) SetWinEventHook 监听窗口创建/显示，出现即隐藏（避免闪烁）；
///   2) 周期扫描兜底，处理钩子注册前已存在或重新显示的窗口。
/// 只隐藏“可见、顶层、非全屏、锚定在屏幕工作区右下角”的小悬浮窗，
/// 不结束进程、不影响屏幕锁/全屏窗口，避免破坏希沃其它正常功能。
/// </summary>
public sealed class SeewoAssistantWindowBlocker : IDisposable
{
    private const string TargetProcessName = "SeewoServiceAssistant";

    // 右下角锚定容差（像素）：窗口右下角距工作区右下角不超过该值即视为“右下角悬浮窗”
    private const int CornerTolerance = 200;

    // 大于屏幕宽/高 2/3 的窗口视为全屏/主窗口，不拦截（例如屏幕锁）
    private const double MaxWindowRatio = 2.0 / 3.0;

    private const uint SW_HIDE = 0;
    private const uint GA_ROOT = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // EVENT_OBJECT_CREATE(0x8000)..EVENT_OBJECT_LOCATIONCHANGE(0x800B)
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectShow = 0x8002;

    private const uint OBJID_WINDOW = 0;
    private const uint CHILDID_SELF = 0;
    private const uint WM_QUIT = 0x0012;

    private readonly object _sync = new();
    private readonly object _scanLock = new();
    private bool _enabled;
    private bool _disposed;

    private Timer? _scanTimer;
    private Thread? _hookThread;
    private volatile uint _hookThreadOsId;
    private readonly ManualResetEventSlim _hookReady = new(false);
    private IntPtr _objectHook;
    private volatile bool _hookThreadRunning;

    // 事件钩子回调的委托必须保持引用，防止被 GC 回收
    private readonly WinEventDelegate _winEventCallback;

    private readonly string _logPath =
        Path.Combine(AppContext.BaseDirectory, "seewo-blocker.log");

    public SeewoAssistantWindowBlocker(bool enabled = true)
    {
        _winEventCallback = OnWinEvent;
        Enabled = enabled;
    }

    /// <summary>是否启用拦截。启用后立即启动钩子与周期扫描，停用则回收。</summary>
    public bool Enabled
    {
        get
        {
            lock (_sync) return _enabled;
        }
        set
        {
            lock (_sync)
            {
                if (_disposed || value == _enabled) return;
                _enabled = value;

                if (_enabled)
                {
                    StartHookThreadLocked();
                    StartScanTimerLocked();
                }
                else
                {
                    StopHookThreadLocked();
                    StopScanTimerLocked();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            StopHookThreadLocked();
            StopScanTimerLocked();
        }
        _hookReady.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---------------- 生命周期 ----------------

    private void StartScanTimerLocked()
    {
        _scanTimer?.Dispose();
        _scanTimer = new Timer(_ => ScanOnce(), null, 500, 1000);
    }

    private void StopScanTimerLocked()
    {
        _scanTimer?.Dispose();
        _scanTimer = null;
    }

    private void StartHookThreadLocked()
    {
        if (_hookThreadRunning) return;
        _hookThreadRunning = true;
        _hookThreadOsId = 0;
        _hookReady.Reset();

        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "SeewoAssistantWinEventHook"
        };
        _hookThread.Start();
    }

    private void StopHookThreadLocked()
    {
        if (!_hookThreadRunning) return;
        _hookThreadRunning = false;

        // 等待钩子线程记录其 OS 线程 ID，随后向它投递 WM_QUIT 结束消息循环
        if (!_hookReady.Wait(TimeSpan.FromMilliseconds(500)))
        {
            // 线程尚未进入消息循环，等待其自行退出（循环条件已置 false）
            _hookReady.Wait(TimeSpan.FromSeconds(2));
        }
        else if (_hookThreadOsId != 0)
        {
            PostThreadMessage(_hookThreadOsId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        var thread = _hookThread;
        if (thread != null && thread.IsAlive && !thread.Join(TimeSpan.FromSeconds(2)))
        {
            try { thread.Interrupt(); }
            catch { /* ignore */ }
        }
        _hookThread = null;
        _objectHook = IntPtr.Zero;
    }

    private void HookThreadMain()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return;

            _hookThreadOsId = GetCurrentThreadId();
            _hookReady.Set();

            _objectHook = SetWinEventHook(
                EventObjectCreate,
                EventObjectLocationChange,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            // 消息循环：WinEvent 回调运行在本线程，必须泵消息
            while (_hookThreadRunning)
            {
                if (!GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
                    break;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch
        {
            // ignore hook failures，周期扫描仍可兜底
        }
        finally
        {
            if (_objectHook != IntPtr.Zero)
            {
                UnhookWinEvent(_objectHook);
                _objectHook = IntPtr.Zero;
            }
        }
    }

    // ---------------- 窗口事件回调 ----------------

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_enabled) return;
        if (hwnd == IntPtr.Zero) return;
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        if (eventType != EventObjectCreate && eventType != EventObjectShow) return;

        TryHideWindow(hwnd, knownPids: null);
    }

    // ---------------- 周期扫描兜底 ----------------

    private void ScanOnce()
    {
        if (!_enabled || !OperatingSystem.IsWindows()) return;
        if (!Monitor.TryEnter(_scanLock)) return;
        try
        {
            var pids = GetTargetPidSet();
            if (pids.Count == 0) return;

            EnumWindows((hwnd, _) =>
            {
                if (!_enabled) return false;
                TryHideWindow(hwnd, pids);
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // ignore
        }
        finally
        {
            Monitor.Exit(_scanLock);
        }
    }

    private static HashSet<uint> GetTargetPidSet()
    {
        var set = new HashSet<uint>();
        try
        {
            foreach (var proc in Process.GetProcessesByName(TargetProcessName))
            {
                set.Add((uint)proc.Id);
            }
        }
        catch
        {
            // ignore
        }
        return set;
    }

    // ---------------- 匹配与隐藏 ----------------

    /// <summary>
    /// 对窗口做完整判定并隐藏。
    /// knownPids 为空时按进程名逐窗口核对（用于事件回调，窗口少）；扫描时传入已取回的 PID 集合。
    /// </summary>
    private void TryHideWindow(IntPtr hwnd, HashSet<uint>? knownPids)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!IsWindowVisible(hwnd)) return;

        // 只处理顶层窗口
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;

        // 几何判定（大多数无关窗口在此被过滤，避免昂贵的进程查询）
        if (!IsBottomRightFloating(hwnd)) return;

        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) return;

        if (knownPids != null)
        {
            if (!knownPids.Contains(pid)) return;
        }
        else if (!IsTargetPid(pid))
        {
            return;
        }

        if (IsWindowVisible(hwnd))
        {
            ShowWindow(hwnd, SW_HIDE);
            LogHidden(hwnd);
        }
    }

    private static bool IsTargetPid(uint pid)
    {
        try
        {
            var name = Process.GetProcessById((int)pid)?.ProcessName;
            return string.Equals(name, TargetProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判定窗口是否为“右下角悬浮窗”：
    /// 可见的顶层窗口，尺寸未超过屏幕 2/3（排除全屏锁屏），
    /// 且其右下角贴近所在显示器工作区的右下角。
    /// </summary>
    private static bool IsBottomRightFloating(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rect)) return false;
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var work = info.rcWork;
        int workW = work.Right - work.Left;
        int workH = work.Bottom - work.Top;
        if (workW <= 0 || workH <= 0) return false;

        // 排除接近全屏/大半屏的窗口（如屏幕锁、主窗口）
        if (rect.Width > workW * MaxWindowRatio || rect.Height > workH * MaxWindowRatio)
            return false;

        // 窗口右下角必须贴近工作区右下角
        int gapRight = work.Right - rect.Right;
        int gapBottom = work.Bottom - rect.Bottom;
        return gapRight >= 0 && gapBottom >= 0 &&
               gapRight <= CornerTolerance && gapBottom <= CornerTolerance;
    }

    private void LogHidden(IntPtr hwnd)
    {
        try
        {
            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            GetWindowRect(hwnd, out RECT rect);

            File.AppendAllText(_logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 已隐藏窗口 hwnd=0x{hwnd.ToInt64():X} " +
                $"class=\"{cls}\" title=\"{title}\" rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})" +
                Environment.NewLine);
        }
        catch
        {
            // ignore logging failures
        }
    }

    // ---------------- P/Invoke ----------------

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
