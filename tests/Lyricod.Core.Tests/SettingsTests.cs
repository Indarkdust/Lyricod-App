using System.Text.Json;

namespace Lyricod.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void OldSettingsKeepLookupEnabled()
    {
        Assert.True(new Lyricod.Settings().LookUpOtherPlayers);
        Assert.True(JsonSerializer.Deserialize<Lyricod.Settings>("{}")!.LookUpOtherPlayers);
    }

    [Fact]
    public void LookupChoiceRoundTripsAndNotifies()
    {
        var settings = new Lyricod.Settings();
        var properties = new List<string?>();
        settings.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        settings.LookUpOtherPlayers = false;
        Assert.False(JsonSerializer.Deserialize<Lyricod.Settings>(JsonSerializer.Serialize(settings))!.LookUpOtherPlayers);
        Assert.Equal([nameof(Lyricod.Settings.LookUpOtherPlayers)], properties);
    }
}
