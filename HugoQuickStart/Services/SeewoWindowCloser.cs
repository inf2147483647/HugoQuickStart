using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HugoQuickStart.Services;

/// <summary>
/// 以高权限（SYSTEM）关闭希沃管家悬浮窗的工作逻辑。
/// 该能力会由 SYSTEM 工作进程调用，因此必须做严格校验：
/// 只允许隐藏"希沃服务助手（SeewoServiceAssistant）"拥有的、可见的、顶层且非全屏的窗口，
/// 避免这条提权通道被滥用为"任意窗口隐藏器"。
/// </summary>
public static class SeewoWindowCloser
{
    private const string TargetProcessName = "SeewoServiceAssistant";

    private const uint SW_HIDE = 0;
    private const uint GA_ROOT = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>大于显示器工作区 2/3 的窗口视为全屏/锁屏等，不处理。</summary>
    private const double MaxWindowRatio = 2.0 / 3.0;

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>
    /// 校验并以 SYSTEM 权限隐藏指定窗口。成功返回 true；失败时 <paramref name="reason"/> 说明原因。
    /// </summary>
    public static bool TryHide(uint hwndValue, out string reason)
    {
        var hwnd = new IntPtr(unchecked((long)hwndValue));
        return TryHide(hwnd, out reason);
    }

    /// <summary>校验并隐藏指定窗口（见类型注释中的校验规则）。</summary>
    public static bool TryHide(IntPtr hwnd, out string reason)
    {
        reason = string.Empty;

        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            reason = "窗口句柄无效或窗口已销毁";
            return false;
        }

        if (!IsWindowVisible(hwnd))
        {
            reason = "窗口当前不可见，无需处理";
            return false;
        }

        if (GetAncestor(hwnd, GA_ROOT) != hwnd)
        {
            reason = "目标不是顶层窗口，拒绝处理";
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || !IsTargetProcess(pid))
        {
            reason = "目标窗口不属于希沃服务助手进程，拒绝处理";
            return false;
        }

        if (IsOversizedWindow(hwnd))
        {
            reason = "目标窗口接近全屏（可能是锁屏等），拒绝处理";
            return false;
        }

        ShowWindow(hwnd, SW_HIDE);

        // 与普通权限路径一致：ShowWindow 的返回值不代表本次隐藏成功，必须复查真实可见性
        if (IsWindowVisible(hwnd))
        {
            reason = $"隐藏失败（错误码 {Marshal.GetLastWin32Error()}）";
            return false;
        }

        reason = "已隐藏";
        return true;
    }

    private static bool IsTargetProcess(uint pid)
    {
        try
        {
            var name = Process.GetProcessById((int)pid).ProcessName;
            return string.Equals(name, TargetProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>窗口尺寸是否超过所在显示器工作区的 2/3（用于排除全屏/锁屏窗口）。</summary>
    private static bool IsOversizedWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return false;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        var workWidth = info.rcWork.Right - info.rcWork.Left;
        var workHeight = info.rcWork.Bottom - info.rcWork.Top;
        if (workWidth <= 0 || workHeight <= 0)
            return false;

        return rect.Width > workWidth * MaxWindowRatio || rect.Height > workHeight * MaxWindowRatio;
    }
}
