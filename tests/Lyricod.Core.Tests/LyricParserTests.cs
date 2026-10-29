namespace Lyricod.Core.Tests;

/// Ported from the macOS app's LyricParserTests: both apps must read lyrics the same way.
public class LyricParserTests
{
    [Fact]
    public void LrcWithRepeatedTimestampsAndMetadata()
    {
        const string lrc = "[ar:Someone]\n[00:01.50][00:10.00]Chorus\n[00:05.250]Verse\n[00:08.00]";
        var l = LyricParser.ParseLrc(lrc, null, "t")!;
        Assert.Equal(["Chorus", "Verse", "Chorus"], l.Lines.Select(x => x.Text));
        Assert.Equal(1.5, l.Lines[0].Start);
        Assert.Equal(8.0, l.Lines[1].End); // empty entry ends the previous line
        Assert.Null(l.LineIndex(0.5));
        Assert.Equal(1, l.LineIndex(6));
        Assert.Equal(2, l.LineIndex(99));
    }

    [Fact]
    public void NeteaseJsonCreditLines()
    {
        var lrc = """{"t":1000,"c":[{"tx":"作曲: "},{"tx":"周杰伦"}]}""" + "\n[00:28.95]故事的小黄花";
        var l = LyricParser.ParseLrc(lrc, null, "t")!;
        Assert.Equal("作曲: 周杰伦", l.Lines[0].Text);
        Assert.Equal(2, l.Lines.Count);
        Assert.Equal([true, false], l.Lines.Select(x => x.IsCredit));
        Assert.True(LyricParser.IsCreditLine("词：周杰伦"));
        Assert.True(LyricParser.IsCreditLine("Composer: Max Martin"));
        Assert.False(LyricParser.IsCreditLine("故事的小黄花：从出生那年就飘着"));
    }

    [Fact]
    public void YrcWordsIncludingParenthesesInText()
    {
        var l = LyricParser.ParseYrc("[16210,3460](16210,670,0)Hello (16880,410,0)(oh)(17290,500,0) world", null, "t")!;
        var words = l.Lines[0].Words!;
        Assert.Equal(["Hello ", "(oh)", " world"], words.Select(w => w.Text));
        Assert.Equal(16.88, words[1].Start, 9);
        Assert.Equal(16.21 + 3.46, l.Lines[0].End, 9);
        Assert.Equal(0.5, words[0].Progress(16.21 + 0.335), 9);
    }

    [Fact]
    public void YrcRestoresDroppedSpacesOnlyForWordSplitLines()
    {
        Assert.Equal("I'm going through withdrawals",
            LyricParser.ParseYrc("[38430,2130](38430,240,0)I'm(38670,300,0)going (38970,180,0)through (39150,1410,0)withdrawals", null, "t")!.Lines[0].Text);
        Assert.Equal("Hellooo", LyricParser.ParseYrc("[0,900](0,300,0)Hel(300,300,0)lo(600,300,0)oo", null, "t")!.Lines[0].Text);
        Assert.Equal("故事的", LyricParser.ParseYrc("[0,900](0,300,0)故(300,300,0)事(600,300,0)的", null, "t")!.Lines[0].Text);
    }

    [Fact]
    public void EnhancedLrcWordTimings()
    {
        var l = LyricParser.ParseLrc("[00:05.49]<00:05.490>词<00:06.500>曲 <00:07.000>人<00:08.000>\n[00:10.00]next", null, "t")!;
        Assert.Equal("词曲 人", l.Lines[0].Text);
        var words = l.Lines[0].Words!;
        Assert.Equal(["词", "曲 ", "人"], words.Select(w => w.Text));
        Assert.Equal(1.01, words[0].Duration, 6);
        Assert.Equal(1.0, words[2].Duration, 6); // closed by the trailing tag
        Assert.Null(l.Lines[1].Words);
    }

    [Fact]
    public void CreditsOnlyUploadIsNotLyrics()
    {
        var junk = """{"c":[{"tx":"作词: "},{"tx":"无"}]}""" + "\n" + """{"c":[{"tx":"作曲: "},{"tx":"无"}]}""";
        Assert.Null(new RawLyrics { Source = "t", Lrc = junk }.Parse());
    }

    [Fact]
    public void TranslationAttachesToNearestLine()
    {
        var l = LyricParser.ParseLrc("[00:01.00]Hello\n[00:05.00]World", null, "t")!;
        LyricParser.AttachTranslation("[00:01.20]你好\n[00:04.90]世界", l);
        Assert.Equal(["你好", "世界"], l.Lines.Select(x => x.Translation));
    }

    [Fact]
    public void QrcTimingFollowsEachWord()
    {
        const string qrc = """
            [ti:晴天]
            [kana:1よね1づ]
            [0,2250]晴(0,160)天(160,160) (320,160)((1440,160)Jay(1600,160))(2080,160)
            [2250,2250]词(2250,450)：(2700,450)周(3150,450)
            [530,529](970,88)
            """;
        var l = LyricParser.ParseQrc(qrc, "[00:00.00]Sunny day\n[00:02.25]//", "t")!;
        Assert.Equal(["晴天 (Jay)", "词：周"], l.Lines.Select(x => x.Text));
        var words = l.Lines[0].Words!;
        Assert.Equal(["晴", "天", " ", "(", "Jay", ")"], words.Select(w => w.Text));
        Assert.Equal(1.6, words[4].Start, 9);
        Assert.Equal(0.16, words[4].Duration, 9);
        Assert.Equal(2.25, l.Lines[0].End, 9);
        Assert.True(l.Lines[1].IsCredit);
        Assert.Equal("Sunny day", l.Lines[0].Translation);
        Assert.Null(l.Lines[1].Translation);
    }

    [Fact]
    public void QrcInsideDecryptedXml()
    {
        const string doc = """
            <?xml version="1.0" encoding="utf-8"?>
            <QrcInfos>
            <LyricInfo LyricCount="1">
            <Lyric_1 LyricType="1" LyricContent="[ti:x]
            [1000,1000]I&apos;m(1000,500) "here"(1500,500)
            "/>
            </LyricInfo>
            </QrcInfos>
            """;
        var l = LyricParser.ParseQrc(doc, null, "t")!;
        Assert.Single(l.Lines);
        Assert.Equal("I'm \"here\"", l.Lines[0].Text);
    }

    [Fact]
    public void QrcDecrypterRejectsGarbage()
    {
        Assert.Null(QrcDecrypter.Decrypt(""));
        Assert.Null(QrcDecrypter.Decrypt("XYZ"));
        Assert.Null(QrcDecrypter.Decrypt("0011223344556677")); // not a zlib stream
    }

    [Fact]
    public void InstrumentCreditsAtTheEnd()
    {
        Assert.True(LyricParser.IsCreditLine("小号：古嘉䜣"));
        Assert.True(LyricParser.IsCreditLine("长笛: 宫崎千佳"));
        Assert.True(LyricParser.IsCreditLine("法国号：萧崇杰"));
        Assert.True(LyricParser.IsCreditLine("Recording & Mix：浦本雅史"));
        Assert.True(LyricParser.IsCreditLine("Guitar：真壁陽平"));
        Assert.True(LyricParser.IsCreditLine("Composed by：Ed Sheeran/Steve Mac/Johnny McDaid"));
        Assert.True(LyricParser.IsCreditLine("Produced by：Steve Mac"));
        Assert.True(LyricParser.IsCreditLine("Written by: Taylor Swift"));
        Assert.False(LyricParser.IsCreditLine("Stand by: me")); // "by" only after a role
        Assert.False(LyricParser.IsCreditLine("越想努力赶上光的影"));
        Assert.False(LyricParser.IsCreditLine("Je t’aimais"));
        Assert.False(LyricParser.IsCreditLine("我说：你别走")); // a colon late in a sung line isn't a role
    }

    // Credits are recognised in every format, so "Show credits" hides them and a credits-only
    // upload isn't taken for lyrics.
    [Fact]
    public void YrcAndPlainTextCredits()
    {
        var timed = LyricParser.ParseYrc("[0,1000](0,1000,0)作词：测试\n[1000,2000](1000,2000,0)Hello", null, "NetEase")!;
        Assert.Equal([true, false], timed.Lines.Select(l => l.IsCredit));
        Assert.Null(new RawLyrics { Source = "NetEase", Yrc = "[0,1000](0,1000,0)作词：测试" }.Parse());

        var plain = LyricParser.PlainText("作词：测试\nHello", "t")!;
        Assert.Equal([true, false], plain.Lines.Select(l => l.IsCredit));
        Assert.Null(new RawLyrics { Source = "t", Plain = "作词：测试" }.Parse());
    }

    [Fact]
    public void EnhancedLrcKeepsTextBeforeTheFirstWordTag()
    {
        var l = LyricParser.ParseLrc("[00:01.00]Hello <00:02.00>world\n[00:04.00]End", null, "t")!;
        Assert.Equal("Hello world", l.Lines[0].Text);
        var words = l.Lines[0].Words!;
        Assert.Equal(["Hello ", "world"], words.Select(w => w.Text));
        Assert.Equal((1.0, 1.0), (words[0].Start, words[0].Duration)); // from the line's start to the first tag
        Assert.Equal((2.0, 2.0), (words[1].Start, words[1].Duration));

        // Fully tagged lines and a closing tag with no text after it are unchanged.
        var tagged = LyricParser.ParseLrc("[00:01.00]<00:01.00>你<00:01.50>好<00:02.00>\n[00:03.00]x", null, "t")!;
        Assert.Equal("你好", tagged.Lines[0].Text);
        Assert.Equal([0.5, 0.5], tagged.Lines[0].Words!.Select(w => w.Duration));
        // Only spaces before the first tag: nothing extra.
        var spaced = LyricParser.ParseLrc("[00:01.00] <00:01.20>Hi\n[00:02.00]x", null, "t")!;
        Assert.Equal(["Hi"], spaced.Lines[0].Words!.Select(w => w.Text));
    }

    [Fact]
    public void SourceRomajiAttachesButNotItsChineseNotice()
    {
        var l = LyricParser.ParseLrc("[00:00.00]밤편지 - IU\n[00:01.00]이 밤", null, "t")!;
        LyricParser.AttachPronunciation("[00:00.00]以下音译标注由AI工具生产\n[00:01.00]i  bam", l);
        Assert.Null(l.Lines[0].Pronunciation);
        Assert.Equal("i bam", l.Lines[1].Pronunciation?.Text);
    }
}
