using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Lyricod;

static class Images
{
    /// <summary>Cover art decoded no wider than <paramref name="width"/> pixels (players hand over
    /// covers far larger than they're shown).</summary>
    public static async Task<BitmapImage?> FromBytes(byte[]? bytes, int width)
    {
        if (bytes == null) return null;
        try
        {
            var image = new BitmapImage { DecodePixelWidth = width, DecodePixelType = DecodePixelType.Logical };
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception) { return null; } // not an image Windows can read
    }
}
