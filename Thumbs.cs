using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LocalSendDrop;

/// <summary>
/// Small previews of files (photos, videos, PDFs: whatever Windows can thumbnail),
/// sent along with a transfer request so the receiver sees what's coming.
/// </summary>
static class Thumbs
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);

    const int Size_ = 96;

    /// <summary>A JPEG data URI of the file's thumbnail, or null if Windows has none.</summary>
    public static string? DataUri(string path)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            // SIIGBF_THUMBNAILONLY: no generic file-type icons, only real previews.
            if (factory.GetImage(new Size(Size_, Size_), 0x8 | 0x1, out var hbitmap) != 0) return null;
            try
            {
                using var source = Image.FromHbitmap(hbitmap);
                var scale = Math.Min(1.0, (double)Size_ / Math.Max(source.Width, source.Height));
                using var small = new Bitmap(source, Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
                using var ms = new MemoryStream();
                var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var quality = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 75L) } };
                small.Save(ms, jpeg, quality);
                return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            }
            finally { DeleteObject(hbitmap); }
        }
        catch { return null; }
    }

    static readonly Dictionary<string, (Image Image, bool Photo)?> _previews = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A real preview (Photo) or else the file's own icon, for the shelf. Loads in the
    /// background: returns null the first time and calls <paramref name="ready"/> when it's in.
    /// </summary>
    public static (Image Image, bool Photo)? Preview(string path, Action ready)
    {
        lock (_previews)
        {
            if (_previews.TryGetValue(path, out var cached)) return cached;
            _previews[path] = null;
        }
        Task.Run(() =>
        {
            // SIIGBF_THUMBNAILONLY, then SIIGBF_ICONONLY.
            var photo = Shell(path, 0x8 | 0x1, 96);
            (Image, bool)? result = photo != null ? (photo, true) : Shell(path, 0x4, 96) is { } icon ? (icon, false) : null;
            if (result == null && File.Exists(path))
                try { if (Icon.ExtractAssociatedIcon(path) is { } assoc) result = (assoc.ToBitmap(), false); } catch { }
            lock (_previews) _previews[path] = result;
            if (result != null) ready();
        });
        return null;
    }

    /// <summary>The shell's image for a file, keeping its transparency.</summary>
    static Bitmap? Shell(string path, int flags, int size)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new Size(size, size), flags, out var hbitmap) != 0) return null;
            try
            {
                using var rgb = Image.FromHbitmap(hbitmap);
                if (Image.GetPixelFormatSize(rgb.PixelFormat) < 32) return new Bitmap(rgb);
                // FromHbitmap drops alpha; the bits still carry it, so read them again as premultiplied ARGB.
                var data = rgb.LockBits(new Rectangle(Point.Empty, rgb.Size), ImageLockMode.ReadOnly, rgb.PixelFormat);
                try
                {
                    var hasAlpha = false;
                    for (var y = 0; y < data.Height && !hasAlpha; y++)
                        for (var x = 0; x < data.Width; x++)
                            if (Marshal.ReadByte(data.Scan0, y * data.Stride + x * 4 + 3) != 0) { hasAlpha = true; break; }
                    if (!hasAlpha) return new Bitmap(rgb);
                    using var argb = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppPArgb, data.Scan0);
                    return new Bitmap(argb);
                }
                finally { rgb.UnlockBits(data); }
            }
            finally { DeleteObject(hbitmap); }
        }
        catch { return null; }
    }

    public static Image? FromDataUri(string? uri)
    {
        if (uri == null || !uri.StartsWith("data:image/")) return null;
        try
        {
            var comma = uri.IndexOf(',');
            return Image.FromStream(new MemoryStream(Convert.FromBase64String(uri[(comma + 1)..])));
        }
        catch { return null; }
    }
}
