using RhythmoSync.Core;
using RhythmoSync.Core.Models;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class SubtitleIoTests
{
    [Theory]
    [InlineData("scene.srt", SubtitleIo.TextFormat.Srt)]
    [InlineData("scene.VTT", SubtitleIo.TextFormat.Vtt)]
    [InlineData("notes.txt", SubtitleIo.TextFormat.Txt)]
    [InlineData("data.csv", SubtitleIo.TextFormat.Csv)]
    public void FormatFromExtension_RecognisesKnownFormats(string path, SubtitleIo.TextFormat expected)
    {
        Assert.Equal(expected, SubtitleIo.FormatFromExtension(path));
    }

    [Fact]
    public void FormatFromExtension_ReturnsNullForUnknown()
    {
        Assert.Null(SubtitleIo.FormatFromExtension("movie.mp4"));
    }

    [Fact]
    public void ExportSrt_ProducesNumberedTimedBlocks()
    {
        var blocks = new[]
        {
            new DialogueBlock { StartTime = 1, Duration = 2, Text = "Bonjour" },
        };

        var srt = SubtitleIo.ExportSrt(blocks);

        Assert.Contains("1\n", srt);
        Assert.Contains("00:00:01,000 --> 00:00:03,000", srt);
        Assert.Contains("Bonjour", srt);
    }

    [Fact]
    public void ExportThenParse_Srt_PreservesTiming()
    {
        var blocks = new[]
        {
            new DialogueBlock { StartTime = 1.5, Duration = 2.25, Text = "Première" },
            new DialogueBlock { StartTime = 10, Duration = 1, Text = "Seconde" },
        };

        var parsed = SubtitleIo.ParseSrt(SubtitleIo.ExportSrt(blocks));

        Assert.Equal(2, parsed.Count);
        Assert.Equal(1.5, parsed[0].StartTime, 3);
        Assert.Equal(2.25, parsed[0].Duration, 3);
        Assert.Equal("Première", parsed[0].Text);
        Assert.Equal("Seconde", parsed[1].Text);
    }

    [Fact]
    public void ExportThenParse_Vtt_PreservesTiming()
    {
        var blocks = new[]
        {
            new DialogueBlock { StartTime = 2, Duration = 3, Text = "Salut" },
        };

        var vtt = SubtitleIo.ExportVtt(blocks);
        Assert.StartsWith("WEBVTT", vtt);

        var parsed = SubtitleIo.ParseVtt(vtt);
        Assert.Single(parsed);
        Assert.Equal(2, parsed[0].StartTime, 3);
        Assert.Equal(3, parsed[0].Duration, 3);
        Assert.Equal("Salut", parsed[0].Text);
    }

    [Fact]
    public void ParseSrt_AcceptsChunksWithoutIndexLine()
    {
        const string srt = "00:00:00,500 --> 00:00:01,500\nSans index\n";
        var parsed = SubtitleIo.ParseSrt(srt);

        Assert.Single(parsed);
        Assert.Equal(0.5, parsed[0].StartTime, 3);
        Assert.Equal(1.0, parsed[0].Duration, 3);
        Assert.Equal("Sans index", parsed[0].Text);
    }

    [Fact]
    public void ParseSrt_AcceptsDotMilliseconds()
    {
        // Certains outils écrivent « 00:00:01.500 » au lieu de la virgule SRT
        // canonique : les temps ne doivent pas retomber silencieusement à 0.
        const string srt = "1\n00:00:01.500 --> 00:00:03.250\nPoint décimal\n";
        var parsed = SubtitleIo.ParseSrt(srt);

        Assert.Single(parsed);
        Assert.Equal(1.5, parsed[0].StartTime, 3);
        Assert.Equal(1.75, parsed[0].Duration, 3);
    }

    [Fact]
    public void ExportSrt_RoundsMillisecondsInsteadOfTruncating()
    {
        // 1.001 s : la décomposition champ par champ tronquait à « 000 » ms.
        var blocks = new[] { new DialogueBlock { StartTime = 1.001, Duration = 1, Text = "x" } };
        var srt = SubtitleIo.ExportSrt(blocks);

        Assert.Contains("00:00:01,001 --> 00:00:02,001", srt);
    }

    [Fact]
    public void ParseVtt_StripsCueAlignmentOptions()
    {
        const string vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000 align:center line:90%\nTexte\n";
        var parsed = SubtitleIo.ParseVtt(vtt);

        Assert.Single(parsed);
        Assert.Equal(1, parsed[0].StartTime, 3);
        Assert.Equal(1, parsed[0].Duration, 3);
    }

    [Fact]
    public void ExportCsv_QuotesFieldsAndEscapesQuotes()
    {
        var blocks = new[]
        {
            new DialogueBlock { Id = "id1", StartTime = 0, Duration = 1, CharacterName = "Jean", Text = "Il dit \"oui\", puis part" },
        };

        var csv = SubtitleIo.ExportCsv(blocks);

        Assert.Contains("ID,StartTime,Duration,EndTime,Character,Text", csv);
        Assert.Contains("\"Jean\"", csv);
        // les guillemets internes sont doublés, la virgule reste protégée dans le champ
        Assert.Contains("\"Il dit \"\"oui\"\", puis part\"", csv);
    }

    [Fact]
    public void Export_SortsByStartTime()
    {
        var blocks = new[]
        {
            new DialogueBlock { StartTime = 5, Duration = 1, Text = "Deux" },
            new DialogueBlock { StartTime = 1, Duration = 1, Text = "Un" },
        };

        var txt = SubtitleIo.ExportTxt(blocks);
        Assert.True(txt.IndexOf("Un", System.StringComparison.Ordinal)
                  < txt.IndexOf("Deux", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ThrowsForUnsupportedExtension()
    {
        Assert.Throws<System.NotSupportedException>(() => SubtitleIo.Parse("x", "file.ass"));
    }
}
