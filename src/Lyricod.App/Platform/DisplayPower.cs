using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinUIEx;
using WinUIEx.Messaging;

namespace Lyricod.Platform;

/// <summary>Whether the display is on. While it's off nothing can be seen, so what only moves for the eye can stop.</summary>
static partial class DisplayPower
{
    public static bool Off { get; private set; }

    /// <summary>The display went off or came on (on the UI thread).</summary>
    public static event Action? Changed;

    static WindowMessageMonitor? _messages;

    [LibraryImport("user32.dll")]
    private static partial nint RegisterPowerSettingNotification(nint recipient, in Guid setting, uint flags);

    /// <summary>Starts listening, through a window that lives as long as the app does.</summary>
    public static void Watch(Window window)
    {
        // The display of this session, not the PC's own monitor: that one goes off under a Remote Desktop session.
        var displayState = new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5"); // GUID_SESSION_DISPLAY_STATUS
        if (_messages != null || RegisterPowerSettingNotification(window.GetWindowHandle(), in displayState, 0) == 0) return;
        _messages = new WindowMessageMonitor(window);
        _messages.WindowMessageReceived += (_, e) =>
        {
            // WM_POWERBROADCAST, PBT_POWERSETTINGCHANGE: a POWERBROADCAST_SETTING (which setting, a length, the value).
            if (e.Message.MessageId != 0x0218 || (long)e.Message.WParam != 0x8013) return;
            try
            {
                nint setting = (nint)(long)e.Message.LParam;
                if (Marshal.PtrToStructure<Guid>(setting) != displayState) return;
                bool off = Marshal.ReadInt32(setting, 20) == 0; // 0 off, 1 on, 2 dimmed
                if (off == Off) return;
                Off = off;
                Changed?.Invoke();
            }
            catch (Exception e2) { Log.Write(e2); }
        };
    }
}
