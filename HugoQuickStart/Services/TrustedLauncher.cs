using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HugoQuickStart.Services;

/// <summary>
/// 受信任启动器（参考 NSudo 的思路）：以更高权限启动本程序自身，用于关闭普通权限无法操作的窗口。
/// 流程：管理员实例启用 SeDebugPrivilege → 从 SYSTEM 进程（winlogon 等）获取并复制 SYSTEM 主令牌
/// → CreateProcessWithTokenW 以 SYSTEM 身份启动工作进程。
/// 注意：整条链路的前提是当前进程已具备管理员权限（普通权限无法启用 SeDebugPrivilege，
/// 也无法打开 SYSTEM 进程的令牌），因此调用方需先通过 UAC 提权。
/// </summary>
public static class TrustedLauncher
{
    private const int ErrorNotAllAssigned = 1300;

    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenAllAccess = 0x000F01FF;

    private const uint SePrivilegeEnabled = 0x00000002;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    private const uint ProcessQueryInformation = 0x0400;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    private const int TokenIntegrityLevel = 25;
    private const int SystemIntegrityRid = 0x4000;

    /// <summary>可用于复制 SYSTEM 令牌的候选进程（均为 SYSTEM 账户运行）。</summary>
    private static readonly string[] SystemProcessCandidates = { "winlogon", "services", "lsass", "wininit" };

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr handle, out int exitCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAllPrivileges,
        ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int infoLength, out int returnLength);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string applicationName,
        string commandLine, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    /// <summary>当前进程是否已提升为管理员（高完整性级别）。</summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前进程是否已达到 SYSTEM 完整性级别（工作进程自检，避免普通权限直接调用）。</summary>
    public static bool IsSystem()
    {
        var token = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                return false;

            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size <= 0)
                return false;

            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                return false;

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes } }
            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            var rid = Marshal.ReadInt32(GetSidSubAuthority(sid, count - 1));
            return rid >= SystemIntegrityRid;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>
    /// 以 SYSTEM 身份运行本程序自身并等待其结束。
    /// 失败时通过 <paramref name="error"/> 返回原因，成功时 <paramref name="exitCode"/> 为子进程退出码。
    /// </summary>
    public static bool TryRunAsSystem(string exePath, string arguments, out int exitCode, out string error)
    {
        exitCode = -1;
        error = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            error = "仅支持 Windows";
            return false;
        }

        if (!IsElevated())
        {
            error = "当前进程不是管理员，无法启用 SeDebugPrivilege";
            return false;
        }

        if (!EnablePrivilege("SeDebugPrivilege"))
        {
            error = "启用 SeDebugPrivilege 失败";
            return false;
        }

        var systemToken = IntPtr.Zero;
        try
        {
            if (!TryOpenSystemToken(out systemToken))
            {
                error = "未能从系统进程复制到 SYSTEM 令牌";
                return false;
            }

            var startupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            var commandLine = $"\"{exePath}\" {arguments}";
            var workingDirectory = Path.GetDirectoryName(exePath);

            if (!CreateProcessWithTokenW(systemToken, 0, exePath, commandLine,
                    CreateUnicodeEnvironment | CreateNoWindow, IntPtr.Zero, workingDirectory,
                    ref startupInfo, out var processInfo))
            {
                error = $"以 SYSTEM 身份创建进程失败（错误码 {Marshal.GetLastWin32Error()}）";
                return false;
            }

            try
            {
                WaitForSingleObject(processInfo.hProcess, 15000);
                if (!GetExitCodeProcess(processInfo.hProcess, out exitCode))
                {
                    error = "读取子进程退出码失败";
                    return false;
                }
            }
            finally
            {
                CloseHandle(processInfo.hThread);
                CloseHandle(processInfo.hProcess);
            }

            return exitCode == 0;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (systemToken != IntPtr.Zero)
                CloseHandle(systemToken);
        }
    }

    /// <summary>从 SYSTEM 进程复制一个主令牌（须已启用 SeDebugPrivilege）。</summary>
    private static bool TryOpenSystemToken(out IntPtr systemToken)
    {
        systemToken = IntPtr.Zero;

        foreach (var name in SystemProcessCandidates)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                var processHandle = IntPtr.Zero;
                var sourceToken = IntPtr.Zero;
                try
                {
                    processHandle = OpenProcess(ProcessQueryInformation, false, (uint)process.Id);
                    if (processHandle == IntPtr.Zero)
                        continue;

                    if (!OpenProcessToken(processHandle, TokenDuplicate | TokenQuery, out sourceToken))
                        continue;

                    if (DuplicateTokenEx(sourceToken, TokenAllAccess, IntPtr.Zero,
                            SecurityImpersonation, TokenPrimary, out systemToken))
                        return true;

                    systemToken = IntPtr.Zero;
                }
                catch
                {
                    // 单个候选进程失败不影响继续尝试其它候选
                }
                finally
                {
                    if (sourceToken != IntPtr.Zero) CloseHandle(sourceToken);
                    if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
                }
            }
        }

        return false;
    }

    /// <summary>在当前进程令牌上启用指定特权；成功返回 true。</summary>
    private static bool EnablePrivilege(string privilegeName)
    {
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                return false;

            if (!LookupPrivilegeValueW(null, privilegeName, out var luid))
                return false;

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // 特权不存在时 AdjustTokenPrivileges 仍返回成功，需按 GetLastError 判定
            return Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
