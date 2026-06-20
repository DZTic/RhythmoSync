using System.IO;
using RhythmoSync.Core.Models;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class ProjectIoTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rs_test_{Guid.NewGuid():N}.rsp");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsProject()
    {
        var project = new ProjectFile
        {
            Dialogues = [new DialogueBlock { Id = "a", Text = "hi", StartTime = 1, Duration = 2 }],
            TotalLanes = 4,
            SyncOffset = 0.5,
            Fps = 30,
            VideoPath = @"C:\videos\clip.mp4",
            AudioTracks = ProjectState.DefaultAudioTracks(),
        };

        ProjectIo.Save(_path, project);
        var loaded = ProjectIo.Load(_path);

        Assert.Single(loaded.Dialogues);
        Assert.Equal("hi", loaded.Dialogues[0].Text);
        Assert.Equal(4, loaded.TotalLanes);
        Assert.Equal(0.5, loaded.SyncOffset);
        Assert.Equal(30, loaded.Fps);
        Assert.Equal(@"C:\videos\clip.mp4", loaded.VideoPath);
        Assert.NotNull(loaded.AudioTracks);
        Assert.Equal(3, loaded.AudioTracks!.Count);
    }

    [Fact]
    public void SaveThenLoad_PreservesLockedState()
    {
        var project = new ProjectFile
        {
            Dialogues =
            [
                new DialogueBlock { Id = "a", IsLocked = true },
                new DialogueBlock { Id = "b", IsLocked = false },
            ],
        };

        ProjectIo.Save(_path, project);
        var loaded = ProjectIo.Load(_path);

        Assert.True(loaded.Dialogues.First(d => d.Id == "a").IsLocked);
        Assert.False(loaded.Dialogues.First(d => d.Id == "b").IsLocked);
    }

    [Fact]
    public void Save_StampsTimestamp()
    {
        var project = new ProjectFile();
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        ProjectIo.Save(_path, project);

        Assert.True(project.Timestamp >= before);
    }

    [Fact]
    public void Save_WritesCamelCaseJson()
    {
        ProjectIo.Save(_path, new ProjectFile { TotalLanes = 2 });
        var json = File.ReadAllText(_path);

        Assert.Contains("\"totalLanes\"", json);
        Assert.DoesNotContain("\"TotalLanes\"", json);
    }

    [Fact]
    public void Load_AcceptsWebCompatibleCamelCaseJson()
    {
        const string json = """
        {
          "version": "1.0",
          "dialogues": [{ "id": "x", "text": "salut", "startTime": 3, "duration": 1.5 }],
          "totalLanes": 6,
          "syncOffset": 0.2,
          "fps": 24
        }
        """;
        File.WriteAllText(_path, json);

        var loaded = ProjectIo.Load(_path);

        Assert.Equal("1.0", loaded.Version);
        Assert.Single(loaded.Dialogues);
        Assert.Equal(3, loaded.Dialogues[0].StartTime);
        Assert.Equal(1.5, loaded.Dialogues[0].Duration);
        Assert.Equal(6, loaded.TotalLanes);
    }

    [Fact]
    public void Load_ThrowsOnNullContent()
    {
        File.WriteAllText(_path, "null");
        Assert.Throws<InvalidDataException>(() => ProjectIo.Load(_path));
    }

    [Fact]
    public void Load_ThrowsWhenVersionMissing()
    {
        File.WriteAllText(_path, """{ "version": "", "dialogues": [] }""");
        Assert.Throws<InvalidDataException>(() => ProjectIo.Load(_path));
    }
}
