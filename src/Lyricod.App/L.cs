using Microsoft.Windows.ApplicationModel.Resources;

namespace Lyricod;

/// <summary>
/// UI strings from Strings/&lt;language&gt;/Resources.resw: in Windows' language, or the one chosen in
/// Settings › General (set once at startup, before any string is read, as on the Mac it applies
/// after a restart).
/// </summary>
static class L
{
    static (ResourceMap Map, ResourceContext Context)? _strings;

    /// <summary>The language the strings were started in ("" for Windows'), to tell whether a
    /// change in Settings needs a restart.</summary>
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

    public static string Format(string key, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}
