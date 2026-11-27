using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Lyricod;

/// <summary>
/// UI strings from Strings/&lt;language&gt;/Resources.resw: in Windows' language, or the one chosen
/// in Settings › General (read once at startup, so a change applies after a restart).
/// </summary>
static class L
{
    static (ResourceMap Map, ResourceContext Context)? _strings;

    /// <summary>The language the strings started in ("" for Windows'), to tell whether a change needs a restart.</summary>
    public static string Language { get; private set; } = "";

    /// <summary>"" for Windows' language, or "en-US" / "zh-Hans".</summary>
    public static void Start(string language)
    {
        Language = language;
        try
        {
            var manager = new ResourceManager();
            var context = manager.CreateResourceContext();
            if (language.Length > 0) context.QualifierValues["Language"] = language;
            _strings = (manager.MainResourceMap.GetSubtree("Resources"), context);
        }
        catch (Exception) { _strings = null; } // strings then show as their keys
    }

    public static string Get(string key)
    {
        if (_strings is not { } s) Start("");
        try
        {
            return _strings is { } r && r.Map.TryGetValue(key, r.Context)?.ValueAsString is { Length: > 0 } value ? value : key;
        }
        catch (Exception) { return key; }
    }

    /// <summary>A tooltip that is also what a screen reader calls the control: for a button that shows only an icon.</summary>
    public static void Tip(DependencyObject control, string key)
    {
        string text = Get(key);
        ToolTipService.SetToolTip(control, text);
        AutomationProperties.SetName(control, text);
    }

    /// <summary>A lyrics source in the UI's language: the providers name it in English ("QQ Music + NetEase").</summary>
    public static string Source(string source) => string.Join(" + ", source.Split(" + ").Select(part => part switch
    {
        "QQ Music" => Get("QQMusic"),
        "NetEase" => Get("ChooseLyricsNetease"),
        "LRCLIB" => Get("ChooseLyricsLrclib"),
        _ => part,
    }));

    /// <summary>For times and dates inside a string: the chosen language's, so a Chinese UI on an English Windows says 23:51.</summary>
    public static System.Globalization.CultureInfo Culture
    {
        get
        {
            try { return Language.Length > 0 ? System.Globalization.CultureInfo.GetCultureInfo(Language) : System.Globalization.CultureInfo.CurrentCulture; }
            catch (System.Globalization.CultureNotFoundException) { return System.Globalization.CultureInfo.CurrentCulture; }
        }
    }

    public static string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}
