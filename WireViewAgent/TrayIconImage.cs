using System.Buffers.Binary;

namespace WireView2.Agent;

/// <summary>
/// The GUI's icon (WireView2/Assets/Icons/bear.ico), embedded as
/// "wireview-agent.ico". An .ico file is a directory of images, each a PNG or a
/// DIB (BITMAPINFOHEADER, bottom-up pixel rows, then a 1-bit AND mask). Windows
/// hands an entry to CreateIconFromResourceEx as is; Linux converts the 32-bit
/// DIB entries to the ARGB32 big-endian pixmaps StatusNotifierItem wants.
/// </summary>
internal static class TrayIconImage
{
    private const string ResourceName = "wireview-agent.ico";

    internal readonly record struct Entry(int Width, int Height, int Offset, int Size);

    private static byte[]? s_ico;

    /// <summary>The embedded .ico, or null when it is missing (never in a normal build).</summary>
    public static byte[]? Ico
    {
        get
        {
            if (s_ico != null) return s_ico;
            using var stream = typeof(TrayIconImage).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return s_ico = bytes;
        }
    }

    /// <summary>The directory entries of an .ico file (empty when malformed).</summary>
    public static List<Entry> Entries(byte[] ico)
    {
        var list = new List<Entry>();
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1) return list;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int at = 6 + 16 * i;
            if (at + 16 > ico.Length) break;
            int w = ico[at] == 0 ? 256 : ico[at];
            int h = ico[at + 1] == 0 ? 256 : ico[at + 1];
            int size = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(at + 8));
            int offset = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(at + 12));
            if (size <= 0 || offset < 0 || (long)offset + size > ico.Length) continue;
            list.Add(new Entry(w, h, offset, size));
        }
        return list;
    }

    /// <summary>The smallest entry at least <paramref name="size"/> pixels wide,
    /// else the largest one.</summary>
    public static Entry? Pick(byte[] ico, int size)
    {
        var entries = Entries(ico);
        if (entries.Count == 0) return null;
        var fit = entries.Where(e => e.Width >= size).OrderBy(e => e.Width).FirstOrDefault();
        return fit.Size > 0 ? fit : entries.MaxBy(e => e.Width);
    }

    /// <summary>The 32-bit DIB entries of the given sizes as ARGB32, network byte
    /// order (A, R, G, B per pixel), row by row from the top: the SNI
    /// IconPixmap format.</summary>
    public static List<(int Width, int Height, byte[] Argb)> SniPixmaps(params int[] sizes)
    {
        var result = new List<(int, int, byte[])>();
        if (Ico is not { } ico) return result;
        foreach (var e in Entries(ico))
        {
            if (!sizes.Contains(e.Width) || e.Width != e.Height) continue;
            if (DibToArgb(ico.AsSpan(e.Offset, e.Size), e.Width, e.Height) is { } argb)
                result.Add((e.Width, e.Height, argb));
        }
        return result;
    }

    private static byte[]? DibToArgb(ReadOnlySpan<byte> dib, int w, int h)
    {
        if (dib.Length < 40) return null;
        int header = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        if (header < 40 || bpp != 32 || compression != 0) return null; // a PNG entry or a palette image

        int stride = w * 4;
        int maskStride = (w + 31) / 32 * 4;
        if (dib.Length < header + stride * h) return null;
        var pixels = dib[header..];
        var mask = dib.Length >= header + stride * h + maskStride * h ? dib[(header + stride * h)..] : default;

        var argb = new byte[w * h * 4];
        bool anyAlpha = false;
        for (int y = 0; y < h; y++)
        {
            var row = pixels.Slice((h - 1 - y) * stride, stride); // rows are stored bottom-up
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2], a = row[x * 4 + 3];
                argb[o] = a; argb[o + 1] = r; argb[o + 2] = g; argb[o + 3] = b;
                anyAlpha |= a != 0;
            }
        }
        // An old-style entry with no alpha: take transparency from the AND mask.
        if (!anyAlpha && !mask.IsEmpty)
        {
            for (int y = 0; y < h; y++)
            {
                var row = mask.Slice((h - 1 - y) * maskStride, maskStride);
                for (int x = 0; x < w; x++)
                    argb[(y * w + x) * 4] = (row[x / 8] & (0x80 >> (x % 8))) != 0 ? (byte)0 : (byte)255;
            }
        }
        return argb;
    }
}
