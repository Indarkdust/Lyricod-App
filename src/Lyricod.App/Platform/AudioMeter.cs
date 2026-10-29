using System.Diagnostics;
using Lyricod.Core;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Lyricod.Platform;

/// <summary>Listens only to the followed player's process tree without changing its playback.</summary>
public sealed class AudioMeter : IDisposable
{
    public const int BandCount = BandAnalyzer.BandCount;
    readonly object _control = new(), _gate = new();
    Thread? _thread;
    CancellationTokenSource? _stop;
    string? _appId;
    string _status = "idle";
    float[]? _levels;
    long _updatedAt;
    bool _disposed;

    public string Status { get { lock (_gate) return _status; } }

    /// <summary>Listening can't work here (an older Windows, or the player's audio can't be reached).</summary>
    public bool Failed { get { lock (_gate) return _status.Contains("failed", StringComparison.Ordinal); } }

    /// <summary>Returns a copy so the UI cannot change the audio thread's levels.</summary>
    public float[]? Levels()
    {
        lock (_gate)
            return _levels != null && Stopwatch.GetElapsedTime(_updatedAt).TotalSeconds < 0.35
                ? (float[])_levels.Clone() : null;
    }

    public void Start(string appId)
    {
        lock (_control)
        {
            if (_disposed) return;
            if (string.Equals(appId, _appId, StringComparison.OrdinalIgnoreCase) && _thread?.IsAlive == true) return;
            StopWorker();
            if (string.IsNullOrWhiteSpace(appId)) return;
            try
            {
                _appId = appId;
                _stop = new CancellationTokenSource();
                var token = _stop.Token;
                SetStatus("starting " + appId);
                _thread = new Thread(() => Capture(appId, token)) { IsBackground = true, Name = "Lyricod audio meter" };
                _thread.Start();
            }
            catch (Exception ex) { StopWorker(); SetStatus("start failed: " + ex.Message); }
        }
    }

    public void Stop() { lock (_control) StopWorker(); }

    public void Dispose()
    {
        lock (_control) { _disposed = true; StopWorker(); }
    }

    void StopWorker()
    {
        _stop?.Cancel();
        if (_thread != null && (_thread.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
            _thread.Join();
        _thread = null;
        _stop?.Dispose();
        _stop = null;
        _appId = null;
        lock (_gate) { _levels = null; _status = LastStatus = "idle"; }
    }

    void SetStatus(string value) { lock (_gate) _status = LastStatus = value; }

    /// <summary>Debug: the latest status of any meter (for the state file).</summary>
    public static string LastStatus { get; private set; } = "idle";

    unsafe void Capture(string appId, CancellationToken token)
    {
        bool initialized = false, started = false;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        using var ready = new AutoResetEvent(false);
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                throw new PlatformNotSupportedException("Process loopback requires Windows build 20348 or later.");
            AudioNative.Check(AudioNative.CoInitializeEx(0, 0)); // MTA
            initialized = true;
            uint? pid = FindRootProcess(appId);
            if (pid == null) throw new InvalidOperationException("No process for " + appId);
            using (var completion = new AudioActivationCompletion())
            {
                var parameters = new AudioActivationParameters { ActivationType = 1, TargetProcessId = pid.Value };
                var variant = new AudioActivationVariant
                {
                    Type = 65, // VT_BLOB
                    Size = (uint)sizeof(AudioActivationParameters), Data = (nint)(&parameters)
                };
                Guid iid = typeof(IAudioClient).GUID;
                nint operation = 0;
                try
                {
                    AudioNative.Check(AudioNative.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", in iid,
                        in variant, completion, out operation));
                    completion.Done.Task.WaitAsync(TimeSpan.FromSeconds(10), token).GetAwaiter().GetResult();
                    token.ThrowIfCancellationRequested();
                    client = AudioNative.Wrap<IAudioClient>(completion.Take());
                }
                finally { if (operation != 0) Marshal.Release(operation); }
            }
            var format = new AudioWaveFormat
            {
                FormatTag = 3, Channels = 2, SamplesPerSecond = 48000,
                AverageBytesPerSecond = 48000 * 8, BlockAlign = 8, BitsPerSample = 32
            };
            // Let WASAPI convert the player's format to our fixed float analysis format.
            AudioNative.Check(client.Initialize(0, 0x00020000 | 0x00040000 | 0x80000000, 200_000, 0, in format, 0)); // 20 ms, as in Microsoft's loopback sample
            AudioNative.Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            Guid captureId = typeof(IAudioCaptureClient).GUID;
            AudioNative.Check(client.GetService(in captureId, out nint capturePointer));
            capture = AudioNative.Wrap<IAudioCaptureClient>(capturePointer);
            var analyzer = new BandAnalyzer();
            var samples = new float[BandAnalyzer.FftSize];
            var levels = new float[BandCount];
            int filled = 0;
            WaitHandle[] waits = [token.WaitHandle, ready];
            AudioNative.Check(client.Start());
            started = true;
            SetStatus($"capturing {appId} (PID {pid}), 48000 Hz, 2 ch");
            while (WaitHandle.WaitAny(waits) == 1)
            {
                AudioNative.Check(capture.GetNextPacketSize(out uint packet));
                while (packet != 0 && !token.IsCancellationRequested)
                {
                    AudioNative.Check(capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _));
                    try
                    {
                        // Do not splice samples across a lost packet.
                        if ((flags & 1) != 0) filled = 0;
                        bool silent = (flags & 2) != 0;
                        float* input = (float*)data;
                        for (uint i = 0; i < frames; i++)
                        {
                            samples[filled++] = silent ? 0 : (input[i * 2] + input[i * 2 + 1]) * 0.5f;
                            if (filled != samples.Length) continue;
                            analyzer.Analyze(samples, levels, out bool hasSound);
                            lock (_gate)
                            {
                                if (hasSound) _levels ??= new float[BandCount];
                                if (_levels != null) levels.CopyTo(_levels, 0);
                                // Silent packets must not keep fallback animation from taking over.
                                if (hasSound) _updatedAt = Stopwatch.GetTimestamp();
                            }
                            filled = 0;
                        }
                    }
                    finally { AudioNative.Check(capture.ReleaseBuffer(frames)); }
                    AudioNative.Check(capture.GetNextPacketSize(out packet));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus($"capture failed: {ex.Message} (0x{ex.HResult:X8})"); }
        finally
        {
            if (started) client!.Stop();
            AudioNative.Release(capture);
            AudioNative.Release(client);
            if (initialized) AudioNative.CoUninitialize();
            lock (_gate) _levels = null;
        }
    }

    /// <summary>Choosing the root includes browser audio children in the loopback tree.</summary>
    public static unsafe uint? FindRootProcess(string appId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(appId)) return null;
            string name = Path.GetFileName(appId);
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
            nint snapshot = AudioNative.CreateToolhelp32Snapshot(2, 0);
            if (snapshot == -1) return null;
            try
            {
                var entry = new AudioProcessEntry { Size = (uint)sizeof(AudioProcessEntry) };
                var matches = new Dictionary<uint, uint>();
                if (!AudioNative.Process32First(snapshot, ref entry)) return null;
                do
                {
                    string executable = new(entry.ExeFile);
                    if (string.Equals(executable, name, StringComparison.OrdinalIgnoreCase))
                        matches[entry.ProcessId] = entry.ParentProcessId;
                } while (AudioNative.Process32Next(snapshot, ref entry));
                // A session id cannot distinguish multiple independent instances of one exe.
                foreach (uint pid in matches.Keys.Order())
                    if (!matches.ContainsKey(matches[pid])) return pid;
                return null;
            }
            finally { AudioNative.CloseHandle(snapshot); }
        }
        catch (Exception) { return null; }
    }
}

[GeneratedComClass]
internal sealed partial class AudioActivationCompletion : IAudioActivationCompletionHandler, IAudioAgileObject, IDisposable
{
    readonly object _gate = new();
    nint _client;
    bool _disposed;
    internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ActivateCompleted(nint operation)
    {
        IAudioActivationOperation? wrapper = null;
        nint pointer = 0;
        try
        {
            // The callback borrows the operation; Wrap consumes an owned reference.
            Marshal.AddRef(operation);
            wrapper = AudioNative.Wrap<IAudioActivationOperation>(operation);
            AudioNative.Check(wrapper.GetActivateResult(out int result, out pointer));
            AudioNative.Check(result);
            lock (_gate)
            {
                if (!_disposed) { _client = pointer; pointer = 0; }
            }
            Done.TrySetResult();
        }
        catch (Exception ex) { Done.TrySetException(ex); }
        finally
        {
            if (pointer != 0) Marshal.Release(pointer);
            AudioNative.Release(wrapper);
        }
        return 0;
    }

    internal nint Take()
    {
        lock (_gate)
        {
            nint result = _client;
            _client = 0;
            if (result == 0) throw new InvalidOperationException("Activation returned no audio client.");
            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_client != 0) Marshal.Release(_client);
            _client = 0;
        }
    }
}

[GeneratedComInterface, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
internal partial interface IAudioAgileObject { }

[GeneratedComInterface, Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
internal partial interface IAudioActivationCompletionHandler
{
    [PreserveSig] int ActivateCompleted(nint operation);
}

[GeneratedComInterface, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
internal partial interface IAudioActivationOperation
{
    [PreserveSig] int GetActivateResult(out int result, out nint activatedInterface);
}

[GeneratedComInterface, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, in AudioWaveFormat format, nint sessionId);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, in AudioWaveFormat format, out nint closestMatch);
    [PreserveSig] int GetMixFormat(out nint format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint handle);
    [PreserveSig] int GetService(in Guid iid, out nint service);
}

[GeneratedComInterface, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
internal partial interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong counterPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct AudioWaveFormat
{
    internal ushort FormatTag, Channels;
    internal uint SamplesPerSecond, AverageBytesPerSecond;
    internal ushort BlockAlign, BitsPerSample, ExtraSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioActivationParameters
{
    internal int ActivationType;
    internal uint TargetProcessId;
    internal int ProcessLoopbackMode; // INCLUDE_TARGET_PROCESS_TREE = 0
}

// PROPVARIANT's BLOB starts at offset 8, with its pointer aligned for x64/ARM64.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct AudioActivationVariant
{
    [FieldOffset(0)] internal ushort Type;
    [FieldOffset(8)] internal uint Size;
    [FieldOffset(16)] internal nint Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AudioProcessEntry
{
    internal uint Size, Usage, ProcessId;
    internal nuint DefaultHeapId;
    internal uint ModuleId, Threads, ParentProcessId;
    internal int PriorityClassBase;
    internal uint Flags;
    internal fixed char ExeFile[260];
}

internal static partial class AudioNative
{
    static readonly StrategyBasedComWrappers Wrappers = new();

    internal static T Wrap<T>(nint pointer)
    {
        if (pointer == 0) throw new InvalidOperationException("Missing COM interface.");
        try { return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(pointer); }
    }

    internal static void Release(object? value) { if (value is ComObject wrapper) wrapper.FinalRelease(); }
    internal static void Check(int result) { Marshal.ThrowExceptionForHR(result); }

    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ActivateAudioInterfaceAsync(string device, in Guid iid, in AudioActivationVariant parameters,
        IAudioActivationCompletionHandler completion, out nint operation);
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32First(nint snapshot, ref AudioProcessEntry entry);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32Next(nint snapshot, ref AudioProcessEntry entry);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
