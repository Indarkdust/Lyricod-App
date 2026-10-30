namespace Lyricod;

public sealed partial class PlaybackModel
{
    async void LoadArtwork(string trackId)
    {
        try
        {
            _artworkTries++;
            var bytes = await _media.ReadArtworkAsync();
            if (bytes == null || NowPlaying?.TrackId != trackId) return;
            if (Artwork != null && bytes.AsSpan().SequenceEqual(Artwork)) return; // the same cover again
            Windows.UI.Color? colour = null;
            (int, int)? size = null;
            try { (colour, size) = await Inspect(bytes); } catch (Exception) { /* not an image we can read */ }
            if (NowPlaying?.TrackId != trackId) return;
            Artwork = bytes;
            ArtworkColor = colour;
            ArtworkSize = size;
            Changed?.Invoke(Change.Artwork);
        }
        catch (Exception e) { LastError = "artwork: " + e.Message; }
    }

    /// <summary>The cover's main colour and its size in pixels.</summary>
    static async Task<(Windows.UI.Color? Colour, (int, int) Size)> Inspect(byte[] bytes)
    {
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = 32, ScaledHeight = 32 },
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        var size = ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        return (Lyricod.Core.ArtworkColor.Dominant(pixels.DetachPixelData()) is { } c ? Windows.UI.Color.FromArgb(255, c.R, c.G, c.B) : null, size);
    }
}
