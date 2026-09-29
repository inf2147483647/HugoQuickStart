using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HugoQuickStart.Models;

namespace HugoQuickStart.Services;

/// <summary>
/// 快捷应用的全局快捷键：基于 Win32 RegisterHotKey 注册、WM_HOTKEY 触发。
/// 热键消息由宿主窗口的 WndProc 子类化钩子转发到 <see cref="HandleMessage"/>。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    // ---- Win32 修饰键（RegisterHotKey 的 fsModifiers）----
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    /// <summary>抑制长按自动重复触发（否则按住不放会反复启动目标程序）。</summary>
    private const uint MOD_NOREPEAT = 0x4000;

    /// <summary>本程序占用的热键 ID 区间起点（每次注册递增，避免与系统/其它组件冲突）。</summary>
    private const int HotkeyIdBase = 0xA000;

    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, AppItem> _registered = new();
    private readonly List<int> _registeredIds = new();
    private IntPtr _hwnd;
    private Action<AppItem>? _onTriggered;

    /// <summary>绑定宿主窗口句柄与触发回调（须在窗口 Show、hwnd 建立之后调用）。</summary>
    public void Attach(IntPtr hwnd, Action<AppItem> onTriggered)
    {
        _hwnd = hwnd;
        _onTriggered = onTriggered;
    }

    /// <summary>
    /// 按当前应用列表重建全部热键注册（先全部注销再重新注册）。
    /// 返回注册失败项的描述列表，供界面提示用户；单项失败不影响其余热键。
    /// </summary>
    public IReadOnlyList<string> Rebind(IEnumerable<AppItem> items)
    {
        UnregisterAll();

        var failures = new List<string>();
        if (_hwnd == IntPtr.Zero)
            return failures;

        // 同一组合重复设置时，后者会被系统判定为"已被占用"而注册失败，
        // 这里按解析后的（修饰键, 虚拟键）先行查重，给出更准确的提示
        // （无论是 "Ctrl+Alt+A" 还是 "Alt+Ctrl+A" 都视为同一组合）。
        var seen = new Dictionary<(uint Modifiers, uint VirtualKey), string>();
        var nextId = HotkeyIdBase;

        foreach (var item in items)
        {
            var text = item.Hotkey?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            if (!TryParse(text, out var modifiers, out var virtualKey))
            {
                var message = $"「{item.Name}」的快捷键 {text} 无法识别，已跳过";
                failures.Add(message);
                LogService.Warn("快捷键", message);
                continue;
            }

            if (seen.TryGetValue((modifiers, virtualKey), out var owner))
            {
                var message = $"「{item.Name}」的快捷键 {text} 与「{owner}」重复，未生效";
                failures.Add(message);
                LogService.Warn("快捷键", message);
                continue;
            }

            // Ctrl 与功能键（Ctrl+F1–F12 等）常被显卡驱动、输入法或其它常驻软件预先占用，
            // RegisterHotKey 会直接返回 false（不抛异常）；此处统一按"注册失败"处理并回报，
            // 绝不中断其余热键的注册，也绝不让异常冒泡到消息泵。
            var registered = false;
            try
            {
                registered = RegisterHotKey(_hwnd, nextId, modifiers | MOD_NOREPEAT, virtualKey);
            }
            catch (Exception ex)
            {
                LogService.Error("快捷键",
                    $"注册 {text} 时发生异常：{ex.GetType().Name}: {ex.Message}");
            }

            if (!registered)
            {
                var error = Marshal.GetLastWin32Error();
                var reason = DescribeRegisterError(error);
                var message = $"「{item.Name}」的快捷键 {text} 注册失败：{reason}";
                failures.Add(message);
                LogService.Warn("快捷键", $"{message}（错误码 {error}）");
                continue;
            }

            _registered[nextId] = item;
            _registeredIds.Add(nextId);
            seen[(modifiers, virtualKey)] = item.Name;
            nextId++;
        }

        return failures;
    }

    /// <summary>处理 WndProc 收到的 WM_HOTKEY（wParam 为热键 ID）。</summary>
    public void HandleMessage(int hotkeyId)
    {
        if (_registered.TryGetValue(hotkeyId, out var item))
            _onTriggered?.Invoke(item);
    }

    /// <summary>注销全部已注册热键。</summary>
    public void UnregisterAll()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (var id in _registeredIds)
            {
                try
                {
                    UnregisterHotKey(_hwnd, id);
                }
                catch
                {
                    // 注销失败不致命：进程退出时系统会统一回收
                }
            }
        }

        _registeredIds.Clear();
        _registered.Clear();
    }

    public void Dispose() => UnregisterAll();

    private static string DescribeRegisterError(int error) => error switch
    {
        ERROR_HOTKEY_ALREADY_REGISTERED => "该组合已被其它程序占用",
        ERROR_ACCESS_DENIED => "权限不足",
        _ => $"系统返回错误码 {error}"
    };

    // ================= 快捷键文本：解析 / 规范化 =================

    /// <summary>
    /// 规范化生成快捷键文本（如 "Ctrl+Alt+A"）。
    /// 规则：单独按下的字母/数字（未同时含 Ctrl 与 Alt）一律补齐为 Ctrl+Alt+字母/数字——
    /// 否则该键会被系统级拦截，导致在其它程序里也无法正常输入该字符。
    /// 功能键（F1–F24）不受此规则限制，可单独使用。无法识别的主键返回 null。
    /// </summary>
    public static string? BuildText(bool ctrl, bool alt, bool shift, string? keyToken)
    {
        if (string.IsNullOrEmpty(keyToken))
            return null;

        if (IsLetterOrDigitToken(keyToken) && (!ctrl || !alt))
        {
            ctrl = true;
            alt = true;
        }

        var parts = new List<string>(4);
        if (ctrl)
            parts.Add("Ctrl");
        if (alt)
            parts.Add("Alt");
        if (shift)
            parts.Add("Shift");
        parts.Add(keyToken);
        return string.Join("+", parts);
    }

    /// <summary>
    /// 解析快捷键文本为 Win32 修饰键与虚拟键码。修饰键顺序不敏感（如 "Alt+Ctrl+A" 亦可）。
    /// 无有效主键或含无法识别的片段时返回 false。
    /// </summary>
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasKey = false;

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    continue;
                case "alt":
                    modifiers |= MOD_ALT;
                    continue;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    continue;
                case "win":
                    modifiers |= MOD_WIN;
                    continue;
            }

            var vk = TokenToVirtualKey(token);
            if (vk == 0 || hasKey)
                return false;

            virtualKey = vk;
            hasKey = true;
        }

        return hasKey;
    }

    /// <summary>字母/数字主键 token：单字符且为 A–Z 或 0–9。</summary>
    private static bool IsLetterOrDigitToken(string token)
    {
        if (token.Length != 1)
            return false;

        var c = token[0];
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
    }

    /// <summary>主键 token → 虚拟键码：字母 A–Z、数字 0–9、功能键 F1–F24。无法识别返回 0。</summary>
    private static uint TokenToVirtualKey(string token)
    {
        if (IsLetterOrDigitToken(token))
            return char.ToUpperInvariant(token[0]);

        // VK_F1 = 0x70，依次递增到 VK_F24 = 0x87
        if ((token[0] == 'F' || token[0] == 'f') &&
            int.TryParse(token.AsSpan(1), out var index) &&
            index is >= 1 and <= 24)
        {
            return (uint)(0x70 + index - 1);
        }

        return 0;
    }
}
