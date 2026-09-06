using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;
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

    // ---------- 高分辨率图标（IShellItemImageFactory，取系统可用最高清图，通常 256px） ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr hbmp);
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private const int HiResRequestSize = 256;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory factory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[]? lpvBits, ref BitmapInfoHeader lpbi, uint uUsage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>
    /// 把 HBITMAP 解码为保留 Alpha 通道的 32bpp ARGB 位图。
    /// 注意 Bitmap.FromHbitmap 会丢弃 Alpha（透明区域变黑底），因此改用 GetDIBits 读像素。
    /// </summary>
    private static Bitmap? DecodeHbitmapArgb(IntPtr hbmp)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return null;

        try
        {
            var header = new BitmapInfoHeader { biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>() };
            if (GetDIBits(hdc, hbmp, 0, 0, null, ref header, DIB_RGB_COLORS) == 0)
                return null;

            var width = header.biWidth;
            var height = Math.Abs(header.biHeight);
            var pixelBytes = header.biBitCount / 8;
            if (width <= 0 || height <= 0 || pixelBytes <= 0)
                return null;

            var srcStride = ((width * header.biBitCount + 31) / 32) * 4;

            // top-down 行序读取像素
            var readHeader = new BitmapInfoHeader
            {
                biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = header.biBitCount,
                biCompression = BI_RGB
            };
            var buffer = new byte[srcStride * height];
            if (GetDIBits(hdc, hbmp, 0, (uint)height, buffer, ref readHeader, DIB_RGB_COLORS) == 0)
                return null;

            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                if (pixelBytes == 4 && srcStride == width * 4)
                {
                    // 32bpp 且行对齐一致：整体拷贝（内存同为 B,G,R,A）
                    Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        var srcRow = y * srcStride;
                        var dstRow = data.Scan0 + y * data.Stride;
                        for (var x = 0; x < width; x++)
                        {
                            var si = srcRow + x * pixelBytes;
                            var di = dstRow + x * 4;
                            Marshal.WriteByte(di, buffer[si]);        // B
                            Marshal.WriteByte(di + 1, buffer[si + 1]); // G
                            Marshal.WriteByte(di + 2, buffer[si + 2]); // R
                            Marshal.WriteByte(di + 3, pixelBytes == 4 ? buffer[si + 3] : (byte)255);
                        }
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    /// <summary>提取最高清图标（256px）为 PNG；失败返回 null。</summary>
    private static AvaloniaBitmap? TryExtractHiRes(string fullPath)
    {
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            if (SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
                return null;

            var hr = factory.GetImage(
                new NativeSize { cx = HiResRequestSize, cy = HiResRequestSize },
                SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out var hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero)
                return null;

            try
            {
                using var src = DecodeHbitmapArgb(hbmp) ?? Bitmap.FromHbitmap(hbmp);
                using var ms = new MemoryStream();
                src.Save(ms, ImageFormat.Png);
                ms.Seek(0, SeekOrigin.Begin);
                return new AvaloniaBitmap(ms);
            }
            finally
            {
                DeleteObject(hbmp);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

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

    /// <summary>提取图标：优先系统最高清（256px），失败时回退 32px 放大到 48x48。</summary>
    private static AvaloniaBitmap? ExtractIconFromFile(string fullPath)
    {
        var hiRes = TryExtractHiRes(fullPath);
        if (hiRes != null)
            return hiRes;

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

    private static readonly Dictionary<string, AvaloniaBitmap> BuiltinCache = new();

    /// <summary>
    /// 加载内置预设图标（嵌入资源 Assets/presets/&lt;key&gt;.png，如 classisland/secrandom/easinote5）。
    /// 带进程级缓存；失败返回 null。可安全地在 UI 线程调用。
    /// </summary>
    public static AvaloniaBitmap? LoadBuiltinIcon(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        if (BuiltinCache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var uri = new Uri($"avares://HugoQuickStart/Assets/presets/{key}.png");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            var bitmap = new AvaloniaBitmap(stream);
            BuiltinCache[key] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
