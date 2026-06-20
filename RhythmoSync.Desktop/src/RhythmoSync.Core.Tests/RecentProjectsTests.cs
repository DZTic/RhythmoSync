using RhythmoSync.Core;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class RecentProjectsTests
{
    [Fact]
    public void Add_PutsNewPathInFront()
    {
        var list = RecentProjects.Add(["b.rsp", "c.rsp"], "a.rsp");
        Assert.Equal(["a.rsp", "b.rsp", "c.rsp"], list);
    }

    [Fact]
    public void Add_MovesExistingPathToFrontWithoutDuplicating()
    {
        var list = RecentProjects.Add(["a.rsp", "b.rsp", "c.rsp"], "c.rsp");
        Assert.Equal(["c.rsp", "a.rsp", "b.rsp"], list);
    }

    [Fact]
    public void Add_DedupesCaseInsensitively()
    {
        var list = RecentProjects.Add([@"C:\Proj\Film.rsp"], @"c:\proj\film.RSP");
        Assert.Single(list);
        Assert.Equal(@"c:\proj\film.RSP", list[0]); // la nouvelle casse remplace l'ancienne entrée
    }

    [Fact]
    public void Add_CapsAtMax()
    {
        var existing = Enumerable.Range(0, 10).Select(i => $"p{i}.rsp").ToList();
        var list = RecentProjects.Add(existing, "new.rsp", max: 10);

        Assert.Equal(10, list.Count);
        Assert.Equal("new.rsp", list[0]);
        Assert.DoesNotContain("p9.rsp", list); // le plus ancien est évincé
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_IgnoresBlankPath(string blank)
    {
        var list = RecentProjects.Add(["a.rsp"], blank);
        Assert.Equal(["a.rsp"], list);
    }

    [Fact]
    public void Add_HandlesNullExisting()
    {
        var list = RecentProjects.Add(null!, "a.rsp");
        Assert.Equal(["a.rsp"], list);
    }

    [Fact]
    public void Add_DoesNotMutateInputList()
    {
        var input = new List<string> { "a.rsp" };
        RecentProjects.Add(input, "b.rsp");
        Assert.Equal(["a.rsp"], input);
    }
}
