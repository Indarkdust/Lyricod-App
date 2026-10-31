using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Lyricod.Core.Romanization;
using Microsoft.UI.Dispatching;
using Windows.Globalization;

namespace Lyricod.Platform;

/// <summary>
/// Japanese words and their kana readings from Windows' own analyser (the one the Japanese
/// IME uses), for romaji. Text between words (spaces the analyser drops, punctuation) comes
/// back with no reading.
/// </summary>
public sealed class JapaneseReader : IJapaneseReader
{
    /// <summary>
    /// Runs work where the analyser works. It can refuse threads from the thread pool (where lyrics
    /// are worked out) with the same "ClassFactory" error while working on single-threaded (STA)
    /// threads like the UI's, so it gets one of its own; failing that, the UI thread lends a hand.
    /// </summary>
    static Action<Action>? _invoke;

    /// <summary>
    /// Whether Windows can analyse Japanese here. The analyser comes with the Japanese input
    /// method; without it Windows refuses to create it ("ClassFactory cannot supply requested
    /// class"), and Japanese pronunciation comes only from the lyric sources' own romaji.
    /// Checked on first use, which is on the UI thread at startup.
    /// </summary>
    public static bool IsAvailable { get; } = Probe();

    static bool Probe()
    {
        var worker = new StaWorker();
        if (Works(worker.Invoke))
        {
            _invoke = worker.Invoke;
            return true;
        }
        worker.Dispose();
        if (DispatcherQueue.GetForCurrentThread() is { } ui && Works(action => action()))
        {
            _invoke = action => OnUiThread(ui, action);
            return true;
        }
        return false;
    }

    static bool Works(Action<Action> invoke)
    {
        bool works = false;
        try { invoke(() => works = JapanesePhoneticAnalyzer.GetWords("日本", false).Count > 0); }
        catch (Exception) { /* not here */ }
        return works;
    }

    static void OnUiThread(DispatcherQueue ui, Action action)
    {
        if (ui.HasThreadAccess)
        {
            action();
            return;
        }
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        if (!ui.TryEnqueue(() =>
            {
                try { action(); }
                catch (Exception e) { error = e; }
                finally { done.Set(); }
            }))
            throw new InvalidOperationException("The UI thread has stopped.");
        // A few milliseconds a line; never wait on a UI thread that's going away.
        if (!done.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The Japanese analyser didn't answer.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    // The analyser takes at most 100 characters at a time.
    const int MaxChunk = 100;

    public IReadOnlyList<WordReading> Read(string text)
    {
        IReadOnlyList<WordReading> words = [];
        (_invoke ?? (action => action()))(() => words = ReadHere(text));
        return words;
    }

    static List<WordReading> ReadHere(string text)
    {
        var words = new List<WordReading>();
        for (int start = 0; start < text.Length; start += MaxChunk)
        {
            var chunk = text.Substring(start, Math.Min(MaxChunk, text.Length - start));
            int at = 0;
            foreach (var phoneme in JapanesePhoneticAnalyzer.GetWords(chunk, false))
            {
                var display = phoneme.DisplayText;
                int found = display.Length == 0 ? -1 : chunk.IndexOf(display, at, StringComparison.Ordinal);
                if (found < 0) continue;
                if (found > at) words.Add(new WordReading(chunk[at..found], null));
                bool isWord = display.Any(char.IsLetterOrDigit);
                words.Add(new WordReading(display, isWord ? phoneme.YomiText : null));
                at = found + display.Length;
            }
            if (at < chunk.Length) words.Add(new WordReading(chunk[at..], null));
        }
        return words;
    }

    /// <summary>A thread of its own in a single-threaded apartment, running one piece of work at a time.</summary>
    sealed class StaWorker : IDisposable
    {
        readonly BlockingCollection<Action> _work = [];

        public StaWorker()
        {
            var thread = new Thread(() =>
            {
                foreach (var work in _work.GetConsumingEnumerable()) work();
            })
            { IsBackground = true, Name = "Japanese reader" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>Runs <paramref name="action"/> on the thread and waits for it; its exception comes back here.</summary>
        public void Invoke(Action action)
        {
            Exception? error = null;
            using var done = new ManualResetEventSlim();
            _work.Add(() =>
            {
                try { action(); }
                catch (Exception e) { error = e; }
                finally { done.Set(); }
            });
            done.Wait();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        public void Dispose() => _work.CompleteAdding();
    }
}
