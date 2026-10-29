using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lyricod.Core.Tests;

public class IslandSettingsTests
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void DefaultsStayTheSame()
    {
        var settings = new Settings();
        Assert.Equal(IslandHover.Expand, settings.IslandHover);
        Assert.Equal("#8CD9FF", settings.IslandColor);
        Assert.Equal(settings.SungColor, settings.IslandColor);
        settings.SungColor = "#FFD36E";
        Assert.Equal("#8CD9FF", settings.IslandColor);
    }

    [Theory]
    [InlineData("{}", "#8CD9FF")]
    [InlineData("{\"SungColor\":\"#FFD36E\"}", "#FFD36E")]
    [InlineData("{\"IslandAlbumColour\":false,\"SungColor\":\"#FF8FB1\"}", "#FF8FB1")]
    [InlineData("{\"SungColor\":\"#9BF59B\",\"IslandAlbumColour\":true}", "#9BF59B")]
    public void OldFilesCopyTheDesktopColourOnce(string json, string expected)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json, Json)!;
        Assert.Equal(expected, settings.IslandColor);
        settings.SungColor = "#FFFFFF";
        Assert.Equal(expected, settings.IslandColor);
        var saved = JsonSerializer.Serialize(settings, Json);
        Assert.Equal(expected, JsonSerializer.Deserialize<Settings>(saved, Json)!.IslandColor);
    }

    [Theory]
    [InlineData("{\"IslandColor\":\"#8CD9FF\",\"SungColor\":\"#FFD36E\"}")]
    [InlineData("{\"SungColor\":\"#FFD36E\",\"IslandColor\":\"#8CD9FF\"}")]
    public void SavedIslandColourWinsInEitherPropertyOrder(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json, Json)!;
        Assert.Equal("#8CD9FF", settings.IslandColor);
    }

    [Fact]
    public void IslandColourNotifiesAndRoundTripsIndependently()
    {
        var settings = new Settings();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        settings.IslandColor = "#ABCDEF";
        settings.IslandColor = "#ABCDEF";
        Assert.Equal([nameof(Settings.IslandColor)], changed);
        Assert.Equal("#8CD9FF", settings.SungColor);
        var saved = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Json), Json)!;
        Assert.Equal("#ABCDEF", saved.IslandColor);
        Assert.Equal("#8CD9FF", saved.SungColor);
    }

    [Theory]
    [InlineData("\"Expand\"", IslandHover.Expand, 0)]
    [InlineData("\"Hide\"", IslandHover.Hide, 1)]
    [InlineData("\"Nothing\"", IslandHover.Nothing, 2)]
    [InlineData("\"ByDirection\"", IslandHover.ByDirection, 3)]
    public void HoverValuesKeepTheirNamesAndNumbers(string json, IslandHover expected, int number)
    {
        Assert.Equal(number, (int)expected);
        Assert.Equal(expected, JsonSerializer.Deserialize<Settings>("{\"IslandHover\":" + json + "}", Json)!.IslandHover);
        Assert.Equal(expected, JsonSerializer.Deserialize<Settings>("{\"IslandHover\":" + number + "}", Json)!.IslandHover);
    }
}
