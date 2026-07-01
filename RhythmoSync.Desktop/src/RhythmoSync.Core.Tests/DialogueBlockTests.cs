using RhythmoSync.Core.Models;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class DialogueBlockTests
{
    [Fact]
    public void EndTime_IsStartPlusDuration()
    {
        var block = new DialogueBlock { StartTime = 3.5, Duration = 2.0 };
        Assert.Equal(5.5, block.EndTime, 9);
    }

    [Theory]
    [InlineData(100, 2.0, true)]   // 50 car/s > 20 → trop rapide
    [InlineData(20, 2.0, false)]   // 10 car/s ≤ 20
    [InlineData(40, 2.0, false)]   // exactement 20 car/s : n'est pas « > 20 »
    [InlineData(41, 2.0, true)]    // 20,5 car/s > 20
    public void IsTooFast_ComparesCharsPerSecondToThreshold(int textLength, double duration, bool expected)
    {
        var block = new DialogueBlock { Text = new string('a', textLength), Duration = duration };
        Assert.Equal(expected, block.IsTooFast);
    }

    [Fact]
    public void IsTooFast_FalseWhenDurationIsZero()
    {
        var block = new DialogueBlock { Text = "beaucoup de texte ici", Duration = 0 };
        Assert.False(block.IsTooFast);
    }

    [Fact]
    public void WithExpression_ProducesIndependentCopy()
    {
        var a = new DialogueBlock { Text = "a", StartTime = 1 };
        var b = a with { Text = "b" };
        Assert.Equal("a", a.Text);
        Assert.Equal("b", b.Text);
        Assert.Equal(a.StartTime, b.StartTime);
        Assert.Equal(a.Id, b.Id); // l'identité est conservée par `with`
    }

    [Fact]
    public void TakeList_IsEmpty_WhenNoTakeAtAll()
    {
        Assert.Empty(new DialogueBlock().TakeList);
    }

    [Fact]
    public void TakeList_FallsBackToAudioFile_WhenTakesAbsent()
    {
        // Rétro-compat : un bloc d'avant la fonctionnalité multi-prises (AudioFile seul).
        var block = new DialogueBlock { AudioFile = @"C:\rec\a.wav" };
        Assert.Equal([@"C:\rec\a.wav"], block.TakeList);
    }

    [Fact]
    public void TakeList_UsesTakes_WhenPresent()
    {
        var block = new DialogueBlock
        {
            AudioFile = @"C:\rec\b.wav",
            Takes = [@"C:\rec\a.wav", @"C:\rec\b.wav"],
        };
        Assert.Equal([@"C:\rec\a.wav", @"C:\rec\b.wav"], block.TakeList);
    }
}
