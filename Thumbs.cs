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
