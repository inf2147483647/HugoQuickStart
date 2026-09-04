using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace HugoQuickStart.Services;

/// <summary>
/// 从目标 EXE/快捷方式提取真实图标（Windows Shell API）。
/// 若路径无效或提取失败返回 null，界面保持占位符。
/// </summary>
public static class AppIconLoader
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const int MaxIconSize = 48;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 解析路径并提取图标。支持绝对路径与相对安装目录的相对路径。
    /// 应在后台线程调用，避免阻塞 UI。
    /// </summary>
    public static AvaloniaBitmap? ExtractForPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var fullPath = ProcessLauncher.ResolvePath(path);
        if (string.IsNullOrEmpty(fullPath))
            return null;

        return ExtractIconFromFile(fullPath);
    }

    /// <summary>返回放大到 48x48 的 PNG 位图（Avalonia），失败返回 null。</summary>
    private static AvaloniaBitmap? ExtractIconFromFile(string fullPath)
    {
        var info = new SHFILEINFO();
        var hResult = SHGetFileInfo(fullPath, 0, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);

        if (hResult == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            using var icon = Icon.FromHandle(info.hIcon);
            using var src = icon.ToBitmap();

            using var ms = new MemoryStream();
            if (src.Width >= MaxIconSize && src.Height >= MaxIconSize)
            {
                src.Save(ms, ImageFormat.Png);
            }
            else
            {
                // 放大到 48x48，保证显示清晰
                using var resized = new Bitmap(MaxIconSize, MaxIconSize);
                using var g = Graphics.FromImage(resized);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(src, 0, 0, MaxIconSize, MaxIconSize);
                resized.Save(ms, ImageFormat.Png);
            }

            ms.Seek(0, SeekOrigin.Begin);
            return new AvaloniaBitmap(ms);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (info.hIcon != IntPtr.Zero)
                DestroyIcon(info.hIcon);
        }
    }
}
