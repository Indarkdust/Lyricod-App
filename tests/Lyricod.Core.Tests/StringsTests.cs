using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Lyricod.Core.Tests;

public class StringsTests
{
    [Fact]
    public void BothLanguagesHaveTheSameStringsAndPlaceholders()
    {
        var english = Strings("en-US");
        var chinese = Strings("zh-Hans");
        // A string missing from one language shows as its key there.
        Assert.Equal(english.Keys.Order(), chinese.Keys.Order());
        foreach (var (key, text) in english) Assert.True(Placeholders(text).SequenceEqual(Placeholders(chinese[key])), key);
    }

    static Dictionary<string, string> Strings(string language, [CallerFilePath] string here = "") =>
        XDocument.Load(Path.Combine(Path.GetDirectoryName(here)!, "../../src/Lyricod.App/Strings", language, "Resources.resw"))
            .Root!.Elements("data").ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

    static IEnumerable<string> Placeholders(string text) => Regex.Matches(text, @"\{\d+").Select(m => m.Value).Distinct().Order();
}
