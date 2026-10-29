using System.ComponentModel;
using System.Diagnostics;
using Lyricod.Core;
using Lyricod.Core.Romanization;
using Lyricod.Platform;
using Microsoft.UI.Dispatching;

namespace Lyricod;

/// <summary>
/// The one source of truth, like the macOS app's PlaybackModel: follows the player through
/// SMTC, keeps the playhead between reports, loads lyrics when the song changes, and moves
/// to each next line on a single timer (no ticking many times a second). Lives on the UI
/// thread; <see cref="Changed"/> fires there.
/// </summary>
public sealed class PlaybackModel
{
    [Flags]
    public enum Change { None = 0, Track = 1, Lyrics = 2, Line = 4, Clock = 8, Artwork = 16 }

    public event Action<Change>? Changed;

    public NowPlaying? NowPlaying { get; private set; }
    public Track? Track { get; private set; }
    /// <summary>Lyrics as shown: credits and pronunciation filtered by the settings.</summary>
    public Lyrics? Lyrics { get; private set; }
    public bool IsSearching { get; private set; }
    public int? CurrentIndex { get; private set; }
    public LyricLine? CurrentLine => CurrentIndex is int i && Lyrics is { } l ? l.Lines[i] : null;
    public bool IsPlaying => _playhead.IsPlaying;
    /// <summary>Changes whenever the playhead re-anchors; animations re-plan on it.</summary>
    public int ClockVersion => _playhead.Version;
    /// <summary>The last thing that went wrong, for the debug state file.</summary>
    public string? LastError { get; private set; }
    /// <summary>This song's own offset, on top of the global one.</summary>
    public double SongOffset { get; private set; }
    /// <summary>The cover art (encoded image bytes), once the player hands it over.</summary>
    public byte[]? Artwork { get; private set; }
    /// <summary>The cover's dominant colour, tuned for black backgrounds.</summary>
    public Windows.UI.Color? ArtworkColor { get; private set; }
    /// <summary>The cover's size in pixels, for diagnostics.</summary>
    public (int Width, int Height)? ArtworkSize { get; private set; }
    /// <summary>Song length in seconds; 0 when the player doesn't say.</summary>
    public double Duration => NowPlaying?.Duration ?? 0;
    /// <summary>The player lets Lyricod move it to another point (click a line, drag the progress bar).</summary>
    public bool CanSeek => NowPlaying?.CanSeek == true;
    /// <summary>The player's own position (no offset), for progress bars.</summary>
    public double PlayerPosition => Math.Clamp(_playhead.RawPosition(Now), 0, Duration > 0 ? Duration : double.MaxValue);
    /// <summary>"Microsoft Edge", "Spotify"… for Settings.</summary>
    public string? PlayerName => NowPlaying is { } n ? MediaSession.DisplayName(n.AppId) : null;
    public bool LyricsPinned => Track is { } track && _overrides[track].Lyrics != null;
    public int CachedSongs => _provider.CachedCount();
    /// <summary>The players Windows knows about now, for Settings › General › Follow.</summary>
    public IReadOnlyList<string> PlayerIds => _media.AppIds;

    readonly DispatcherQueue _ui;
    readonly MediaSession _media;
    readonly LyricsProvider _provider;
    readonly Romanizer _romanizer;
    readonly TrackOverrides _overrides;
    readonly Settings _settings;
    readonly Playhead _playhead = new();
    readonly DispatcherQueueTimer _lineTimer;
    readonly DispatcherQueueTimer _safetyPoll;
    /// <summary>Everything the source returned, with pronunciation worked out.</summary>
    Lyrics? _allLyrics;
    CancellationTokenSource? _loading;
    int _artworkTries;
    /// <summary>The song's length was corrected after it started (and searched again with it).</summary>
    bool _lengthCorrected;
    bool _refreshing, _refreshAgain;
    /// <summary>Lyricod is quitting (<see cref="Stop"/>).</summary>
    bool _stopped;

    /// <summary>Seconds on a steady clock.</summary>
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public PlaybackModel(DispatcherQueue ui, MediaSession media, LyricsProvider provider, Romanizer romanizer,
                         TrackOverrides overrides, Settings settings)
    {
        _ui = ui;
        _media = media;
        _provider = provider;
        _romanizer = romanizer;
        _overrides = overrides;
        _settings = settings;
        _lineTimer = ui.CreateTimer();
        _lineTimer.IsRepeating = false;
        _lineTimer.Tick += (_, _) => Tick();
        // Players report changes as they happen; this only catches a missed report.
        _safetyPoll = ui.CreateTimer();
        _safetyPoll.Interval = TimeSpan.FromSeconds(2);
        _safetyPoll.Tick += (_, _) => _ = Refresh();
        _media.Changed += OnMediaChanged;
        _media.DetailsChanged += OnMediaDetailsChanged;
        _settings.PropertyChanged += OnSettingChanged;
        _media.Choice = _settings.PlayerChoice;
        ApplyOffset();
    }

    public async Task StartAsync()
    {
        await _media.StartAsync();
        await Refresh();
    }

    /// <summary>
    /// For quitting: stops following the player and both timers, and raises <see cref="Changed"/>
    /// no more, so nothing runs against windows that have closed.
    /// </summary>
    public void Stop()
    {
        _stopped = true;
        Changed = null;
        _lineTimer.Stop();
        _safetyPoll.Stop();
        _loading?.Cancel();
        _settings.PropertyChanged -= OnSettingChanged;
        _media.Changed -= OnMediaChanged;
        _media.DetailsChanged -= OnMediaDetailsChanged;
        _media.Dispose();
    }

    void OnMediaChanged() => _ui.TryEnqueue(() => _ = Refresh());

    // A cover can arrive (or be corrected) well after the song starts: read it again each time.
    void OnMediaDetailsChanged() => _ui.TryEnqueue(() => { if (NowPlaying is { } n) LoadArtwork(n.TrackId); });

    /// <summary>The lyric position (offset applied) now, or at a given steady-clock instant.</summary>
    public double Position(double? at = null) => _playhead.Position(at ?? Now);

    // ---- Following the player ----

    async Task Refresh()
    {
        // One read at a time; a change during a read triggers one more.
        if (_refreshing) { _refreshAgain = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                Apply(await _media.ReadAsync());
            } while (_refreshAgain);
        }
        finally { _refreshing = false; }
    }

    void Apply(NowPlaying? now)
    {
        if (_stopped) return; // a read that finished after quitting
        var change = Change.None;
        if (now == null)
        {
            if (NowPlaying != null)
            {
                NowPlaying = null; Track = null; Lyrics = null; _allLyrics = null; CurrentIndex = null;
                Artwork = null; ArtworkColor = null;
                _loading?.Cancel();
                IsSearching = false; // the cancelled search won't say it's done
                SongOffset = 0; _playhead.Offset = _settings.Offset;
                _playhead.Anchor(0, Now, false);
                change = Change.Track | Change.Lyrics | Change.Line | Change.Clock | Change.Artwork;
            }
            _safetyPoll.Stop();
            Finish(change);
            return;
        }

        bool trackChanged = now.TrackId != NowPlaying?.TrackId;
        var previous = NowPlaying;
        NowPlaying = now;
        double at = Now;
        if (now.HasTimeline)
        {
            // The report's position, carried to this instant.
            double position = now.Position + (now.IsPlaying ? (DateTimeOffset.Now - now.PositionAt).TotalSeconds : 0);
            if (_playhead.Report(position, at, now.IsPlaying, force: trackChanged)) change |= Change.Clock;
        }
        else if (trackChanged)
        {
            // No position from the player: time the song from now.
            _playhead.Anchor(0, at, now.IsPlaying);
            change |= Change.Clock;
        }
        else if (previous?.IsPlaying != now.IsPlaying)
        {
            _playhead.Anchor(_playhead.RawPosition(at), at, now.IsPlaying);
            change |= Change.Clock;
        }

        if (trackChanged)
        {
            Track = new Track { TrackId = now.TrackId, Title = now.Title, Artist = now.Artist, Album = now.Album, Duration = now.Duration };
            change |= Change.Track;
            ApplyOffset();
            LoadLyrics(Track, ignoreCache: false);
            Artwork = null; ArtworkColor = null; _artworkTries = 0;
            _lengthCorrected = false;
            change |= Change.Artwork;
        }
        else if (Track is { } current && !_lengthCorrected && now.Duration > 0 && Math.Abs(now.Duration - current.Duration) > 2)
        {
            // Some players (QQ Music) report a new song with the last one's length for a moment.
            // The length helps pick the right lyrics, so search again with the real one, once.
            _lengthCorrected = true;
            Track = current with { Duration = now.Duration };
            change |= Change.Track;
            ApplyOffset(); // the length is part of the song's key, so its saved timing may differ
            LoadLyrics(Track, ignoreCache: false);
        }
        // Browsers hand over the cover a moment after the song: ask again on the next few reports.
        if (Artwork == null && _artworkTries < 4) LoadArtwork(now.TrackId);
        // Polled whenever a song is loaded: a missed report (a player starting after loading)
        // mustn't leave the clock stopped.
        _safetyPoll.Start();
        Finish(change);
    }

    void Finish(Change change)
    {
        if (change != Change.None) Changed?.Invoke(change);
        Tick();
    }

    // ---- Artwork ----

    async void LoadArtwork(string trackId)
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

    // ---- Lyrics ----

    /// <summary>Searches again for this song, skipping the saved lyrics.</summary>
    public void SearchAgain()
    {
        if (Track is not { } t) return;
        _provider.ClearCache(t);
        LoadLyrics(t, ignoreCache: true);
    }

    /// <summary>Searches by hand without changing the lyrics in use.</summary>
    public async Task<List<LyricsCandidate>> LyricsCandidates(string query)
    {
        if (Track is not { } track) return [];
        LyricsSource[] preferred = PlayerName?.Contains("QQ", StringComparison.OrdinalIgnoreCase) == true
            ? [LyricsSource.Qq, LyricsSource.Netease, LyricsSource.Lrclib]
            : [LyricsSource.Netease, LyricsSource.Qq, LyricsSource.Lrclib];
        try { return await _provider.Candidates(query, track, preferred); }
        catch (Exception e) { LastError = "candidates: " + e.Message; return []; }
    }

    /// <summary>Previews the source's lyrics without changing playback or the cache.</summary>
    public async Task<Lyrics?> PreviewLyrics(LyricsRef reference)
    {
        try { return (await _provider.Fetch(reference))?.Parse(); }
        catch (Exception e) { LastError = "preview: " + e.Message; return null; }
    }

    public void PinLyrics(LyricsRef reference)
    {
        if (Track is not { } track) return;
        var choice = _overrides[track];
        choice.Lyrics = reference;
        _overrides[track] = choice;
        LoadLyrics(track, ignoreCache: true);
    }

    /// <summary>Searches afresh so the cached manual choice cannot win again.</summary>
    public void UnpinLyrics()
    {
        if (Track is not { } track) return;
        var choice = _overrides[track];
        choice.Lyrics = null;
        _overrides[track] = choice;
        _provider.ClearCache(track);
        LoadLyrics(track, ignoreCache: true);
    }

    public void ClearSavedLyrics() => _provider.ClearAll();

    void LoadLyrics(Track track, bool ignoreCache)
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        _allLyrics = null;
        Lyrics = null;
        CurrentIndex = null;
        IsSearching = true;
        var pinned = _overrides[track].Lyrics;
        Changed?.Invoke(Change.Lyrics | Change.Line);
        _ = Task.Run(async () =>
        {
            Lyrics? found = null, early = null;
            try
            {
                found = await _provider.GetLyrics(track, ignoreCache, pinned, cts.Token, cached =>
                {
                    early = cached;
                    try { _romanizer.Fill(cached); }
                    catch (Exception e) { LastError = "pronunciation: " + e.Message; }
                    _ui.TryEnqueue(() =>
                    {
                        if (cts.IsCancellationRequested || Track?.TrackId != track.TrackId) return;
                        IsSearching = false;
                        _allLyrics = cached;
                        ApplyDisplayFilters();
                    });
                });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { LastError = "lyrics: " + e.Message; }
            // The saved copy is already showing, and the check found nothing better (or failed).
            if (early != null && (found == null || ReferenceEquals(found, early))) return;
            // Pronunciation is extra: if working it out fails, the lyrics still show.
            if (found != null)
            {
                try { _romanizer.Fill(found); } // the analysers take a few ms a line
                catch (Exception e) { LastError = "pronunciation: " + e.GetType().Name + ": " + e.Message; }
            }
            _ui.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested || Track?.TrackId != track.TrackId) return;
                IsSearching = false;
                _allLyrics = found;
                ApplyDisplayFilters();
            });
        });
    }

    /// <summary>Builds <see cref="Lyrics"/> from what the source returned, per the settings.</summary>
    void ApplyDisplayFilters()
    {
        if (_allLyrics is not { } all) { Lyrics = null; CurrentIndex = null; Changed?.Invoke(Change.Lyrics | Change.Line); return; }
        var languages = _settings.PronunciationLanguages.ToHashSet();
        var lines = all.Lines.Where(l => _settings.ShowCredits || !l.IsCredit)
            .Select((l, i) => l with
            {
                Id = i, // ids are row indices
                Pronunciation = l.Pronunciation is { Language: { } lang } p && languages.Contains(lang) ? p : null,
            }).ToList();
        Lyrics = lines.Count == 0 ? null : new Lyrics(lines, all.Source, all.IsSynced) { Ref = all.Ref };
        CurrentIndex = null;
        Changed?.Invoke(Change.Lyrics);
        Tick();
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.ShowCredits) or nameof(Settings.PronunciationLanguages):
                ApplyDisplayFilters();
                break;
            case nameof(Settings.Offset):
                ApplyOffset();
                break;
            case nameof(Settings.PlayerChoice):
                _media.Choice = _settings.PlayerChoice;
                break;
        }
    }

    // ---- Timing ----

    void ApplyOffset()
    {
        SongOffset = Track is { } t ? _overrides[t].Offset ?? 0 : 0;
        _playhead.Offset = _settings.Offset + SongOffset;
        // Re-anchoring where it is bumps the clock version, so the sweep re-plans.
        _playhead.Anchor(_playhead.RawPosition(Now), Now, _playhead.IsPlaying);
        Changed?.Invoke(Change.Clock);
        Tick();
    }

    /// <summary>Nudges this song's timing (positive shows lyrics earlier) and remembers it.</summary>
    public void SetSongOffset(double value)
    {
        if (Track is not { } t) return;
        var o = _overrides[t];
        o.Offset = Math.Round(value, 2);
        _overrides[t] = o;
        ApplyOffset();
    }

    /// <summary>
    /// Updates the current line, then sets one timer for when the next line starts. Anything
    /// that moves the playhead, pauses, or changes the lyrics calls this again.
    /// </summary>
    void Tick()
    {
        if (_stopped) return;
        double now = Position();
        var index = Lyrics?.LineIndex(now);
        if (index != CurrentIndex)
        {
            CurrentIndex = index;
            Changed?.Invoke(Change.Line);
        }
        WriteDebugState(now);
        _lineTimer.Stop();
        if (!IsPlaying || Lyrics is not { IsSynced: true } lyrics) return;
        int next = index is int i ? i + 1 : 0;
        if (next >= lyrics.Lines.Count) return;
        _lineTimer.Interval = TimeSpan.FromSeconds(Math.Max(lyrics.Lines[next].Start - now, 0) + 0.005);
        _lineTimer.Start();
    }

    /// <summary>Debug: LYRICOD_STATE_FILE=path writes the playhead and line for checks from outside.</summary>
    static readonly string? DebugStatePath = Environment.GetEnvironmentVariable("LYRICOD_STATE_FILE");

    public string DiagnosticsText => Diagnostics.Text(
        UpdateChecker.TestBuild is { } build ? $"{UpdateChecker.Version} (test build {build})"
            : $"{UpdateChecker.Version} ({typeof(App).Assembly.GetName().Version?.Revision ?? 0})",
        $"Windows {Environment.OSVersion.Version}",
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        PlayerName, NowPlaying?.AppId, IsPlaying, Track, Lyrics, IsSearching, LyricsPinned,
        SongOffset, _settings.Offset, LastError, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        NowPlaying?.HasTimeline, ArtworkSize, Fonts.DiagnosticsText, UpdateChecker.Kind);

    void WriteDebugState(double position)
    {
        if (DebugStatePath == null) return;
        try
        {
            File.WriteAllText(DebugStatePath, System.Text.Json.JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now, title = NowPlaying?.Title, artist = NowPlaying?.Artist, app = NowPlaying?.AppId,
                duration = NowPlaying?.Duration, searchedWithDuration = Track?.Duration, source = Lyrics?.Source,
                reportedPosition = NowPlaying?.Position, reportedAt = NowPlaying?.PositionAt, hasTimeline = NowPlaying?.HasTimeline,
                reportedPlaying = NowPlaying?.IsPlaying, playing = IsPlaying, position, lines = Lyrics?.Lines.Count,
                synced = Lyrics?.IsSynced, firstLine = Lyrics?.Lines.FirstOrDefault()?.Start, index = CurrentIndex, line = CurrentLine?.Text, error = LastError,
                meter = Platform.AudioMeter.LastStatus,
            }));
        }
        catch (IOException) { }
    }

    // ---- Controls ----

    /// <summary>Moves the player to where <paramref name="line"/> starts.</summary>
    public void Seek(LyricLine line) => SeekPlayer(line.Start - _playhead.Offset);

    /// <summary>Moves the player to <paramref name="seconds"/> of its own time. Lyricod's clock moves
    /// there at once, as on the Mac: some players report the new position only a second later.</summary>
    public async void SeekPlayer(double seconds)
    {
        try
        {
            double target = Math.Clamp(seconds, 0, Duration > 0 ? Duration : double.MaxValue);
            if (!await _media.Seek(target) || _stopped) return;
            _playhead.Anchor(target, Now, _playhead.IsPlaying);
            Finish(Change.Clock);
            await Refresh();
        }
        catch (Exception) { /* the player went away */ }
    }

    public Task PlayPause() => _media.PlayPause();
    public Task Next() => _media.Next();
    public Task Previous() => _media.Previous();
}
