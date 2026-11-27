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
        Assert.Equal(IslandHover.ByDirection, settings.IslandHover);
        Assert.Equal("#8CD9FF", settings.IslandColor);
        Assert.Equal(settings.SungColor, settings.IslandColor);
        settings.SungColor = "#FFD36E";
        Assert.Equal("#8CD9FF", settings.IslandColor);
    }

    [Theory]
    [InlineData("{}", "#8CD9FF")]
    [InlineData("{\"SungColor\":\"#FFD36E\"}", "#FFD36E")]
    [InlineData("{\"IslandAlbumColour\":false,\"SungColor\":\"#FF8FB1\"}", "#FF8FB1")]
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

    [Fact]
    public void IslandIsOnAndDockedByDefault()
    {
        var settings = new Settings();
        Assert.True(settings.ShowIsland);
        Assert.Equal(IslandStyle.Default, settings.IslandStyle);
        Assert.True(settings.IslandLocked);
        Assert.Null(settings.IslandX);
        Assert.Null(settings.IslandY);
        // A file saved with the island off keeps it off.
        Assert.False(JsonSerializer.Deserialize<Settings>("{\"ShowIsland\":false}", Json)!.ShowIsland);
    }

    [Fact]
    public void FloatingDisplayPointRoundTripsWithItsEdge()
    {
        var settings = new Settings { IslandX = 1920, IslandY = 1040, IslandDisplayX = 1770, IslandDisplayY = 1023 };
        var saved = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Json), Json)!;
        Assert.Equal(1920, saved.IslandX);
        Assert.Equal(1040, saved.IslandY);
        Assert.Equal(1770, saved.IslandDisplayX);
        Assert.Equal(1023, saved.IslandDisplayY);
        var old = JsonSerializer.Deserialize<Settings>("{\"IslandX\":100,\"IslandY\":50}", Json)!;
        Assert.Null(old.IslandDisplayX);
        Assert.Null(old.IslandDisplayY);
    }

    [Fact]
    public void BeingNewIsntSaved()
    {
        // Only a first run's fresh settings are new (and may then pick defaults for the PC).
        Assert.False(new Settings().IsNew);
        Assert.DoesNotContain("IsNew", JsonSerializer.Serialize(new Settings(), Json));
        Assert.False(JsonSerializer.Deserialize<Settings>("{\"IsNew\":true}", Json)!.IsNew);
    }

    [Fact]
    public void GlassIsOffByDefaultAndItsTransparencyStaysInRange()
    {
        var settings = new Settings();
        Assert.Equal(IslandTheme.Classic, settings.IslandTheme);
        Assert.Equal(GlassEffect.Liquid, settings.GlassEffect); // what makes it Lyricod Glass
        Assert.Equal(GlassEffect.Liquid, JsonSerializer.Deserialize<Settings>("{\"GlassEffect\":9}", Json)!.GlassEffect);
        // Lifelike, with the tint and amounts it was tuned with.
        Assert.True(settings.GlassShine);
        Assert.Equal(ShineStyle.Lifelike, settings.GlassShineStyle);
        Assert.True(settings.GlassIsLifelike);
        Assert.Equal(new GlassLook(GlassTint.Light, 88, 7, 50), Look(settings));
        Assert.Null(settings.GlassLookAside);
        settings.GlassTransparency = 0;
        Assert.Equal(10, settings.GlassTransparency);
        settings.GlassTransparency = 100;
        Assert.Equal(90, settings.GlassTransparency);
        Assert.Equal(90, JsonSerializer.Deserialize<Settings>("{\"GlassTransparency\":120}", Json)!.GlassTransparency);
        var saved = JsonSerializer.Deserialize<Settings>("{\"LiquidBlur\":150,\"LiquidRefraction\":-5}", Json)!;
        Assert.Equal(100, saved.LiquidBlur);
        Assert.Equal(0, saved.LiquidRefraction);
    }

    /// <summary>What an earlier version saved, for someone who had made the glass their own.</summary>
    const string OldFile = """
        {"IslandTheme":"Glass","GlassTransparency":70,"GlassTint":"Album","GlassShine":true,"GlassShineStyle":"Soft",
         "GlassEffect":"Liquid","LiquidBlur":25,"LiquidRefraction":40}
        """;
    static readonly GlassLook Own = new(GlassTint.Album, 70, 25, 40);

    static GlassLook Look(Settings settings) => new(settings.GlassTint, settings.GlassTransparency, settings.LiquidBlur, settings.LiquidRefraction);

    [Fact]
    public void AnOlderFileKeepsItsGlass()
    {
        var plain = JsonSerializer.Deserialize<Settings>("""
            {"GlassTransparency":90,"GlassTint":"Neutral","GlassShine":true,"GlassShineStyle":"Bright","LiquidBlur":10,"LiquidRefraction":50}
            """, Json)!;
        Assert.Equal(ShineStyle.Bright, plain.GlassShineStyle);
        Assert.False(plain.GlassIsLifelike);
        Assert.Equal(GlassLook.Others, Look(plain));
        Assert.Null(plain.GlassLookAside);

        var own = JsonSerializer.Deserialize<Settings>(OldFile, Json)!;
        Assert.Equal(ShineStyle.Soft, own.GlassShineStyle);
        Assert.Equal(Own, Look(own));
    }

    [Fact]
    public void AFileWithoutAStyleIsBrightWithItsOwnValues()
    {
        var settings = Settings.Read("{\"GlassShine\":true,\"GlassTint\":\"Album\",\"GlassTransparency\":70,\"LiquidBlur\":25,\"LiquidRefraction\":40}")!;
        Assert.Equal(ShineStyle.Bright, settings.GlassShineStyle);
        Assert.Equal(Own, Look(settings));
        settings.ChooseGlassShine(ShineStyle.Lifelike);
        Assert.Equal(Own, settings.GlassLookAside);
        // One it can't read is the same; a file with no glass in it starts as a new install does.
        Assert.Equal(ShineStyle.Bright, Settings.Read("{\"GlassTransparency\":70,\"GlassShineStyle\":\"Mirror\"}")!.GlassShineStyle);
        Assert.Equal(ShineStyle.Lifelike, Settings.Read("{\"DesktopFontSize\":40}")!.GlassShineStyle);
    }

    [Fact]
    public void LifelikeStartsTunedAndTheOtherEffectsGetBackWhatTheyHad()
    {
        var settings = JsonSerializer.Deserialize<Settings>(OldFile, Json)!;
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        settings.ChooseGlassShine(ShineStyle.Lifelike);
        Assert.Equal(GlassLook.Lifelike, Look(settings));
        Assert.Equal(Own, settings.GlassLookAside);
        // What the Settings window's lists and sliders follow.
        Assert.Equal([nameof(Settings.GlassShineStyle), nameof(Settings.GlassLookAside), nameof(Settings.GlassTint),
            nameof(Settings.GlassTransparency), nameof(Settings.LiquidBlur), nameof(Settings.LiquidRefraction)], changed);

        settings.ChooseGlassShine(ShineStyle.Bright);
        Assert.Equal(Own, Look(settings));
        Assert.Equal(GlassLook.Lifelike, settings.GlassLookAside);
        // Bright, Soft and off share one set.
        settings.ChooseGlassShine(ShineStyle.Soft);
        settings.ChooseGlassShine(null);
        Assert.Equal(Own, Look(settings));
        Assert.Equal(GlassLook.Lifelike, settings.GlassLookAside);
    }

    [Fact]
    public void LifelikeKeepsWhatWasChangedUnderIt()
    {
        var settings = new Settings();
        var mine = new GlassLook(GlassTint.Album, 80, 12, 60);
        (settings.GlassTint, settings.GlassTransparency, settings.LiquidBlur, settings.LiquidRefraction) = (mine.Tint, mine.Transparency, mine.Blur, mine.Refraction);
        settings.ChooseGlassShine(ShineStyle.Bright);
        Assert.Equal(new GlassLook(GlassTint.Neutral, 90, 10, 50), Look(settings));
        settings.LiquidBlur = 30;
        settings.ChooseGlassShine(ShineStyle.Lifelike);
        Assert.Equal(mine, Look(settings));

        // Off with Lifelike still the style is one of the others.
        settings.ChooseGlassShine(null);
        Assert.Equal(ShineStyle.Lifelike, settings.GlassShineStyle);
        Assert.Equal(GlassLook.Others with { Blur = 30 }, Look(settings));
        settings.ChooseGlassShine(ShineStyle.Lifelike);
        Assert.Equal(mine, Look(settings));
    }

    [Fact]
    public void TheSetAsideIsSavedAndRead()
    {
        var settings = JsonSerializer.Deserialize<Settings>(OldFile, Json)!;
        settings.ChooseGlassShine(ShineStyle.Lifelike);
        var json = JsonSerializer.Serialize(settings, Json);
        Assert.Contains("\"GlassLookAside\":{\"Tint\":\"Album\",\"Transparency\":70,\"Blur\":25,\"Refraction\":40}", json);
        Assert.DoesNotContain(nameof(Settings.GlassIsLifelike), json);
        var saved = Settings.Read(json)!;
        Assert.Equal(GlassLook.Lifelike, Look(saved));
        Assert.Equal(Own, saved.GlassLookAside);
        saved.ChooseGlassShine(ShineStyle.Soft);
        Assert.Equal(Own, Look(saved));
        Assert.Contains("\"GlassLookAside\":null", JsonSerializer.Serialize(new Settings(), Json));
    }

    [Theory]
    [InlineData("{\"Tint\":\"Mirror\",\"Transparency\":70,\"Blur\":25,\"Refraction\":40}")]
    [InlineData("{\"Tint\":\"Album\",\"Transparency\":\"most\",\"Blur\":25,\"Refraction\":40}")]
    [InlineData("\"Album\"")]
    public void ASetAsideThisVersionCantReadStartsTunedAgainAndTheRestIsRead(string aside)
    {
        var settings = Settings.Read("{\"DesktopFontSize\":40,\"GlassTint\":\"Album\",\"GlassShineStyle\":\"Lifelike\",\"GlassLookAside\":" + aside
            + ",\"GlassTransparency\":80,\"LiquidBlur\":12,\"IslandSize\":\"Large\"}")!;
        Assert.Null(settings.GlassLookAside);
        Assert.Equal(40, settings.DesktopFontSize);
        Assert.Equal(IslandSize.Large, settings.IslandSize);
        Assert.Equal(new GlassLook(GlassTint.Album, 80, 12, 50), Look(settings));
        settings.ChooseGlassShine(ShineStyle.Bright);
        Assert.Equal(GlassLook.Others, Look(settings));
    }

    [Fact]
    public void RestoringGlassDefaultsGivesANewInstallsGlassAndNothingElse()
    {
        var settings = JsonSerializer.Deserialize<Settings>(OldFile, Json)!;
        settings.GlassEffect = GlassEffect.Frosted;
        settings.IslandSize = IslandSize.Large;
        settings.ChooseGlassShine(ShineStyle.Lifelike);
        settings.LiquidRefraction = 80;
        settings.ChooseGlassShine(null);
        settings.RestoreGlassDefaults();

        var fresh = new Settings();
        Assert.Equal(fresh.GlassEffect, settings.GlassEffect);
        Assert.Equal(fresh.GlassShine, settings.GlassShine);
        Assert.Equal(fresh.GlassShineStyle, settings.GlassShineStyle);
        Assert.Equal(Look(fresh), Look(settings));
        Assert.Null(settings.GlassLookAside);
        Assert.Equal(IslandTheme.Glass, settings.IslandTheme);
        Assert.Equal(IslandSize.Large, settings.IslandSize);
        // The others start tuned again too.
        settings.ChooseGlassShine(ShineStyle.Bright);
        Assert.Equal(GlassLook.Others, Look(settings));
    }

    [Theory]
    [InlineData("\"Clear\"", GlassEffect.Clear, 0)]
    [InlineData("\"Frosted\"", GlassEffect.Frosted, 1)]
    [InlineData("\"Liquid\"", GlassEffect.Liquid, 2)]
    public void GlassEffectsKeepTheirNamesAndNumbers(string json, GlassEffect expected, int number)
    {
        Assert.Equal(number, (int)expected);
        Assert.Equal(expected, JsonSerializer.Deserialize<Settings>("{\"GlassEffect\":" + json + "}", Json)!.GlassEffect);
    }

    [Theory]
    [InlineData("{\"IslandTheme\":\"Classic\"}", IslandTheme.Classic, GlassTint.Light)]
    [InlineData("{\"IslandTheme\":\"Glass\",\"GlassTint\":\"Album\"}", IslandTheme.Glass, GlassTint.Album)]
    [InlineData("{\"IslandTheme\":1,\"GlassTint\":1}", IslandTheme.Glass, GlassTint.Album)]
    [InlineData("{\"IslandTheme\":\"Glass\",\"GlassTint\":\"Light\"}", IslandTheme.Glass, GlassTint.Light)]
    [InlineData("{\"IslandTheme\":1,\"GlassTint\":2}", IslandTheme.Glass, GlassTint.Light)]
    [InlineData("{\"IslandTheme\":9,\"GlassTint\":9}", IslandTheme.Classic, GlassTint.Neutral)]
    public void ThemeValuesKeepTheirNamesAndNumbers(string json, IslandTheme theme, GlassTint tint)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json, Json)!;
        Assert.Equal(theme, settings.IslandTheme);
        Assert.Equal(tint, settings.GlassTint);
    }

    [Theory]
    [InlineData("\"Default\"", IslandStyle.Default, 0)]
    [InlineData("\"Floating\"", IslandStyle.Floating, 1)]
    public void StyleValuesKeepTheirNamesAndNumbers(string json, IslandStyle expected, int number)
    {
        Assert.Equal(number, (int)expected);
        Assert.Equal(expected, JsonSerializer.Deserialize<Settings>("{\"IslandStyle\":" + json + "}", Json)!.IslandStyle);
        Assert.Equal(IslandStyle.Default, JsonSerializer.Deserialize<Settings>("{\"IslandStyle\":7}", Json)!.IslandStyle);
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
