namespace Lyricod.Core;

internal static class LyricData
{
    internal const int MaxBytes = 4 * 1024 * 1024;

    internal static byte[] Read(Stream input)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > MaxBytes) throw new InvalidDataException("Lyrics are too large");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static async Task<string> ReadText(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Lyrics are too large");
        using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > MaxBytes) throw new InvalidDataException("Lyrics are too large");
            output.Write(buffer, 0, count);
        }
        using var bounded = new ByteArrayContent(output.ToArray());
        bounded.Headers.ContentType = content.Headers.ContentType;
        return await bounded.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
