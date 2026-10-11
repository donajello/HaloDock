using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HaloDock;

/// <summary>Loads the same icons/thumbnails Explorer shows, with proper alpha.</summary>
public static class ShellIcons
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Clear() => Cache.Clear();

    public static ImageSource? Get(string path, int size)
    {
        var key = path + "|" + size;
        if (!Cache.TryGetValue(key, out var image))
        {
            image = LoadShellImage(path, size) ?? LoadAssociatedIcon(path);
            Cache[key] = image;
        }
        return image;
    }

    static ImageSource? LoadShellImage(string path, int size)
    {
        object? obj = null;
        try
        {
            var iid = typeof(NativeMethods.IShellItemImageFactory).GUID;
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out obj);
            var factory = (NativeMethods.IShellItemImageFactory)obj;
            // The item's own icon (what Explorer shows in icon view), never a content thumbnail.
            const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
            if (factory.GetImage(new NativeMethods.SIZE(size, size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbm) != 0
                || hbm == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbm); }
            finally { NativeMethods.DeleteObject(hbm); }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (obj != null) Marshal.ReleaseComObject(obj);
        }
    }

    static ImageSource? LoadAssociatedIcon(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// CreateBitmapSourceFromHBitmap drops the alpha channel of shell bitmaps (icons get black backgrounds),
    /// so copy the DIB bits ourselves.
    /// </summary>
    static BitmapSource FromHBitmap(IntPtr hbm)
    {
        var got = NativeMethods.GetObject(hbm, Marshal.SizeOf<NativeMethods.DIBSECTION>(), out var ds);
        var bm = ds.dsBm;
        if (got == 0 || bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero)
        {
            var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        int w = bm.bmWidth, h = bm.bmHeight, stride = bm.bmWidthBytes;
        var bottomUp = ds.dsBmih.biHeight > 0;
        var pixels = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            var srcRow = bottomUp ? h - 1 - y : y;
            Marshal.Copy(bm.bmBits + srcRow * stride, pixels, y * stride, stride);
        }

        // Photo thumbnails often come back with an all-zero alpha channel: treat those as opaque.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }

        var src = BitmapSource.Create(w, h, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, stride);
        src.Freeze();
        return src;
    }
}
