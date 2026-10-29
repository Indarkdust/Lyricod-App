using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lyricod.Core;

/// <summary>Per-song choices the user made: hand-picked lyrics and a timing offset.</summary>
public sealed class TrackOverride
{
    [JsonPropertyName("pinned")] public LyricsRef? Pinned { get; set; }
    /// <summary>Older macOS files stored only a NetEase id.</summary>
    [JsonPropertyName("neteaseID")] public long? NeteaseId { get; set; }
    [JsonPropertyName("offset")] public double? Offset { get; set; }

    /// <summary>Lyrics the user chose for this song.</summary>
    [JsonIgnore]
    public LyricsRef? Lyrics
    {
        get => Pinned ?? (NeteaseId is { } id ? LyricsRef.Netease(id) : null);
        set { Pinned = value; NeteaseId = null; }
    }

    [JsonIgnore] public bool IsEmpty => Lyrics == null && (Offset ?? 0) == 0;
}

/// <summary>
/// The user's per-song choices, kept outside the lyrics cache so clearing the cache keeps them.
/// Not thread-safe: use from the UI thread.
/// </summary>
public sealed class TrackOverrides
{
    readonly string _file;
    readonly Dictionary<string, TrackOverride> _map;

    public TrackOverrides(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try { _map = JsonSerializer.Deserialize<Dictionary<string, TrackOverride>>(File.ReadAllText(file)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { _map = []; }
    }

    public TrackOverride this[Track track]
    {
        get => _map.TryGetValue(LyricsProvider.CacheKey(track), out var o) ? o : new TrackOverride();
        set
        {
            var key = LyricsProvider.CacheKey(track);
            if (value.IsEmpty) _map.Remove(key); else _map[key] = value;
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_map));
            File.Move(tmp, _file, overwrite: true);
        }
    }
}
