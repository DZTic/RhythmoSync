using RhythmoSync.Core;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class BackupsTests
{
    [Fact]
    public void TimestampedName_EmbedsSortableTimestamp()
    {
        var name = Backups.TimestampedName("monfilm", new DateTime(2026, 6, 20, 14, 30, 5));
        Assert.Equal("monfilm-20260620-143005.rsp.bak", name);
    }

    [Fact]
    public void TimestampedName_FallsBackForBlankBaseName()
    {
        var name = Backups.TimestampedName("  ", new DateTime(2026, 1, 1, 0, 0, 0));
        Assert.StartsWith("projet-", name);
    }

    [Fact]
    public void TimestampedNames_AreChronologicalInLexicalOrder()
    {
        var earlier = Backups.TimestampedName("p", new DateTime(2026, 6, 20, 9, 0, 0));
        var later = Backups.TimestampedName("p", new DateTime(2026, 6, 20, 18, 0, 0));
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void EntriesToPrune_KeepsNewestN()
    {
        var newestFirst = new[] { "v5", "v4", "v3", "v2", "v1" };
        var prune = Backups.EntriesToPrune(newestFirst, keep: 3);
        Assert.Equal(["v2", "v1"], prune);
    }

    [Fact]
    public void EntriesToPrune_NothingToPruneWhenUnderLimit()
    {
        var prune = Backups.EntriesToPrune(new[] { "a", "b" }, keep: 10);
        Assert.Empty(prune);
    }

    [Fact]
    public void EntriesToPrune_KeepZeroPrunesEverything()
    {
        var prune = Backups.EntriesToPrune(new[] { "a", "b" }, keep: 0);
        Assert.Equal(["a", "b"], prune);
    }

    [Fact]
    public void EntriesToPrune_EmptyInputIsEmpty()
    {
        Assert.Empty(Backups.EntriesToPrune(System.Array.Empty<string>(), keep: 5));
    }
}
