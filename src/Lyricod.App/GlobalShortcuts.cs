using System.ComponentModel;
using Lyricod.Core;
using Lyricod.Platform;
using Microsoft.UI.Xaml;
using WinUIEx;
using WinUIEx.Messaging;

namespace Lyricod;

/// <summary>The desktop window survives hiding, so shortcuts work even with no lyrics visible.</summary>
public sealed class GlobalShortcuts : IDisposable
{
    readonly Settings _settings;
    readonly nint _hwnd;
    readonly WindowMessageMonitor _messages;
    readonly Action<ShortcutAction> _perform;
    readonly Dictionary<int, ShortcutAction> _registered = [];
    readonly HashSet<ShortcutAction> _failed = [];
    bool _paused, _disposed;
    int _nextId;

    public event Action? Changed;

    public GlobalShortcuts(Window window, Settings settings, Action<ShortcutAction> perform)
    {
        _hwnd = window.GetWindowHandle();
        _settings = settings;
        _perform = perform;
        _messages = new WindowMessageMonitor(window);
        _messages.WindowMessageReceived += OnMessage;
        settings.PropertyChanged += OnSettingChanged;
        Register();
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            if (_disposed || _paused == value) return;
            _paused = value;
            Register();
        }
    }

    public KeyCombo? Combo(ShortcutAction action) =>
        _settings.Shortcuts.TryGetValue(action.ToString(), out var saved) && KeyCombo.TryParse(saved, out var combo) ? combo : null;

    public bool Failed(ShortcutAction action) => _failed.Contains(action);

    public ShortcutAction? ActionUsing(KeyCombo combo, ShortcutAction except)
    {
        foreach (var action in Enum.GetValues<ShortcutAction>())
            if (action != except && Combo(action) == combo) return action;
        return null;
    }

    public void Set(ShortcutAction action, KeyCombo? combo)
    {
        if (combo is { } c && (!c.IsUsable || ActionUsing(c, action) != null)) return;
        var saved = new Dictionary<string, string>(_settings.Shortcuts);
        if (combo is { } value) saved[action.ToString()] = value.Stored;
        else saved.Remove(action.ToString());
        _settings.Shortcuts = saved;
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.Shortcuts)) Register();
    }

    void Register()
    {
        Unregister();
        if (!_paused)
        {
            _failed.Clear();
            var used = new HashSet<KeyCombo>();
            foreach (var action in Enum.GetValues<ShortcutAction>())
            {
                if (!_settings.Shortcuts.ContainsKey(action.ToString())) continue;
                // New IDs keep queued messages from an earlier registration from running a different action.
                int id = ++_nextId;
                if (id > 0xBFFF) { _failed.Add(action); continue; }
                if (Combo(action) is { IsUsable: true } combo
                    && used.Add(combo)
                    && Win32.RegisterHotKey(_hwnd, id, (uint)combo.Modifiers | Win32.MOD_NOREPEAT, combo.VirtualKey))
                    _registered[id] = action;
                else _failed.Add(action);
            }
        }
        Changed?.Invoke();
    }

    void Unregister()
    {
        foreach (int id in _registered.Keys) Win32.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    void OnMessage(object? sender, WindowMessageEventArgs e)
    {
        if (e.Message.MessageId != Win32.WM_HOTKEY) return;
        e.Handled = true;
        e.Result = 0;
        if (!_paused && !_disposed && _registered.TryGetValue((int)e.Message.WParam, out var action)) _perform(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.PropertyChanged -= OnSettingChanged;
        Unregister();
        _messages.WindowMessageReceived -= OnMessage;
        _messages.Dispose();
    }
}
