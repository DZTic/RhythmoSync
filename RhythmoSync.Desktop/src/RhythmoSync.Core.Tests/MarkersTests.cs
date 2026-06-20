using RhythmoSync.Core;
using RhythmoSync.Core.Models;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class MarkersTests
{
    [Fact]
    public void AddMarker_KeepsListSortedByTime()
    {
        var s = new ProjectState();
        s.AddMarker(5, "fin");
        s.AddMarker(1, "début");
        s.AddMarker(3, "milieu");

        Assert.Equal(["début", "milieu", "fin"], s.Markers.Select(m => m.Label));
        Assert.Equal([1.0, 3.0, 5.0], s.Markers.Select(m => m.Time));
    }

    [Fact]
    public void AddMarker_ClampsNegativeTimeToZero()
    {
        var s = new ProjectState();
        s.AddMarker(-4, "x");
        Assert.Equal(0, s.Markers[0].Time);
    }

    [Fact]
    public void AddMarker_RaisesMarkersChanged()
    {
        var s = new ProjectState();
        var fired = 0;
        s.MarkersChanged += () => fired++;
        s.AddMarker(1, "a");
        Assert.Equal(1, fired);
    }

    [Fact]
    public void RemoveMarker_RemovesById()
    {
        var s = new ProjectState();
        var id = s.AddMarker(2, "a");
        s.AddMarker(3, "b");

        s.RemoveMarker(id);

        Assert.Single(s.Markers);
        Assert.Equal("b", s.Markers[0].Label);
    }

    [Fact]
    public void RemoveMarker_UnknownIdIsNoOp()
    {
        var s = new ProjectState();
        s.AddMarker(1, "a");
        var fired = 0;
        s.MarkersChanged += () => fired++;

        s.RemoveMarker("nope");

        Assert.Single(s.Markers);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void RenameMarker_ChangesLabel()
    {
        var s = new ProjectState();
        var id = s.AddMarker(1, "ancien");
        s.RenameMarker(id, "nouveau");
        Assert.Equal("nouveau", s.Markers[0].Label);
    }

    [Fact]
    public void RenameMarker_SameLabelDoesNotRaiseEvent()
    {
        var s = new ProjectState();
        var id = s.AddMarker(1, "même");
        var fired = 0;
        s.MarkersChanged += () => fired++;
        s.RenameMarker(id, "même");
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Markers_SurviveProjectFileRoundTrip()
    {
        var s = new ProjectState();
        s.AddMarker(2, "scène 1");
        s.AddMarker(8, "scène 2");

        var file = s.ToProjectFile();
        var restored = new ProjectState();
        restored.ImportProject(file);

        Assert.Equal(2, restored.Markers.Count);
        Assert.Equal(["scène 1", "scène 2"], restored.Markers.Select(m => m.Label));
    }

    [Fact]
    public void ResetProject_ClearsMarkers()
    {
        var s = new ProjectState();
        s.AddMarker(1, "a");
        s.ResetProject();
        Assert.Empty(s.Markers);
    }

    [Fact]
    public void ImportProject_NullMarkers_YieldsEmptyList()
    {
        var s = new ProjectState();
        s.AddMarker(1, "a");
        s.ImportProject(new ProjectFile { Markers = null });
        Assert.Empty(s.Markers);
    }
}
