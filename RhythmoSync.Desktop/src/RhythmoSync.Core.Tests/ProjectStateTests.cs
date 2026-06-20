using RhythmoSync.Core;
using RhythmoSync.Core.Models;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class ProjectStateTests
{
    private static DialogueBlock Block(string id, double start = 0, double dur = 1, string text = "", int lane = 0) =>
        new() { Id = id, StartTime = start, Duration = dur, Text = text, Lane = lane };

    // ── Historique undo / redo ───────────────────────────────────────────────

    [Fact]
    public void Add_PushesHistoryAndCanUndoRedo()
    {
        var s = new ProjectState();
        Assert.False(s.CanUndo);

        s.AddDialogue(Block("a"));
        Assert.Single(s.Dialogues);
        Assert.True(s.CanUndo);
        Assert.False(s.CanRedo);

        s.Undo();
        Assert.Empty(s.Dialogues);
        Assert.True(s.CanRedo);

        s.Redo();
        Assert.Single(s.Dialogues);
        Assert.Equal("a", s.Dialogues[0].Id);
    }

    [Fact]
    public void NewMutation_ClearsRedoStack()
    {
        var s = new ProjectState();
        s.AddDialogue(Block("a"));
        s.Undo();
        Assert.True(s.CanRedo);

        s.AddDialogue(Block("b"));
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void History_IsCappedAtMaxHistory()
    {
        var s = new ProjectState();
        for (var i = 0; i < ProjectState.MaxHistory + 15; i++)
            s.AddDialogue(Block($"b{i}"));

        Assert.Equal(ProjectState.MaxHistory, s.PastDepth);
    }

    [Fact]
    public void UndoRedo_AreNoOpsWhenStacksEmpty()
    {
        var s = new ProjectState();
        s.Undo(); // ne doit pas lever
        s.Redo();
        Assert.Empty(s.Dialogues);
    }

    // ── Clamps des paramètres de vue ──────────────────────────────────────────

    [Theory]
    [InlineData(10, 40)]
    [InlineData(80, 80)]
    [InlineData(500, 200)]
    public void LaneHeightPx_IsClampedTo40_200(double input, double expected)
    {
        var s = new ProjectState { LaneHeightPx = input };
        Assert.Equal(expected, s.LaneHeightPx);
    }

    [Theory]
    [InlineData(5, 20)]
    [InlineData(200, 200)]
    [InlineData(5000, 1200)]
    public void ZoomLevel_IsClampedTo20_1200(double input, double expected)
    {
        var s = new ProjectState { ZoomLevel = input };
        Assert.Equal(expected, s.ZoomLevel);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(99, 12)]
    public void TotalLanes_IsClampedTo1_12(int input, int expected)
    {
        var s = new ProjectState { TotalLanes = input };
        Assert.Equal(expected, s.TotalLanes);
    }

    [Fact]
    public void ViewChanged_FiresOnlyOnRealChange()
    {
        var s = new ProjectState();
        var fired = 0;
        s.ViewChanged += () => fired++;

        s.LaneHeightPx = 120;
        s.LaneHeightPx = 120; // valeur identique → pas d'événement
        Assert.Equal(1, fired);
    }

    // ── Auto-stretch sur changement de texte ─────────────────────────────────

    [Fact]
    public void UpdateDialogue_StretchesDurationWhenTextBecomesTooFast()
    {
        var s = new ProjectState();
        s.AddDialogue(Block("a", dur: 1, text: ""));

        // 100 caractères à 20 car/s ⇒ durée minimale 5 s, donc 5,5 s après marge
        s.UpdateDialogue("a", d => d with { Text = new string('x', 100) });

        var block = s.Dialogues[0];
        Assert.Equal(5.5, block.Duration, 9);
        Assert.False(block.IsTooFast);
    }

    [Fact]
    public void UpdateDialogue_DoesNotShrinkComfortableBlock()
    {
        var s = new ProjectState();
        s.AddDialogue(Block("a", dur: 10, text: "court"));
        s.UpdateDialogue("a", d => d with { Text = "toujours court" });
        Assert.Equal(10, s.Dialogues[0].Duration, 9);
    }

    [Fact]
    public void UpdateDialogue_SkipHistory_DoesNotPushSnapshot()
    {
        var s = new ProjectState();
        s.AddDialogue(Block("a"));
        var depth = s.PastDepth;

        s.UpdateDialogue("a", d => d with { StartTime = 2 }, skipHistory: true);
        Assert.Equal(depth, s.PastDepth);
        Assert.Equal(2, s.Dialogues[0].StartTime);
    }

    // ── Suppression / groupes / sélection ────────────────────────────────────

    [Fact]
    public void DeleteDialogues_RemovesBlocksAndClearsSelection()
    {
        var s = new ProjectState();
        s.SetDialogues([Block("a"), Block("b")]);
        s.SelectBlock("a");
        s.DeleteDialogues(["a"]);

        Assert.Single(s.Dialogues);
        Assert.Equal("b", s.Dialogues[0].Id);
        Assert.False(s.IsSelected("a"));
    }

    [Fact]
    public void GroupSelected_AssignsSharedGroupId()
    {
        var s = new ProjectState();
        s.SetDialogues([Block("a"), Block("b")]);
        s.SelectBlock("a");
        s.SelectBlock("b", multi: true);

        s.GroupSelected();

        var gidA = s.Dialogues.First(d => d.Id == "a").GroupId;
        var gidB = s.Dialogues.First(d => d.Id == "b").GroupId;
        Assert.NotNull(gidA);
        Assert.Equal(gidA, gidB);
    }

    [Fact]
    public void SelectBlock_SelectsWholeGroup()
    {
        var s = new ProjectState();
        s.SetDialogues([Block("a"), Block("b")]);
        s.SelectBlock("a");
        s.SelectBlock("b", multi: true);
        s.GroupSelected();

        s.SelectBlock(null);          // tout désélectionner
        s.SelectBlock("a");           // sélectionner un membre du groupe

        Assert.True(s.IsSelected("a"));
        Assert.True(s.IsSelected("b"));
    }

    // ── Outils timeline ──────────────────────────────────────────────────────

    [Fact]
    public void ShiftTimeline_OffsetsAndClampsAtZero()
    {
        var s = new ProjectState();
        s.SetDialogues([Block("a", start: 1), Block("b", start: 5)]);

        s.ShiftTimeline(-3);

        Assert.Equal(0, s.Dialogues.First(d => d.Id == "a").StartTime); // 1-3 → clampé à 0
        Assert.Equal(2, s.Dialogues.First(d => d.Id == "b").StartTime); // 5-3
    }

    [Fact]
    public void GlobalFindReplace_IsCaseInsensitiveAndCountsOccurrences()
    {
        var s = new ProjectState();
        s.SetDialogues([
            new DialogueBlock { Id = "a", Text = "Bonjour bonjour", CharacterName = "BONJOUR" },
            new DialogueBlock { Id = "b", Text = "rien" },
        ]);

        var count = s.GlobalFindReplace("bonjour", "salut");

        Assert.Equal(3, count);
        Assert.Equal("salut salut", s.Dialogues.First(d => d.Id == "a").Text);
        Assert.Equal("salut", s.Dialogues.First(d => d.Id == "a").CharacterName);
    }

    [Fact]
    public void GlobalFindReplace_NoMatch_DoesNotTouchHistory()
    {
        var s = new ProjectState();
        s.SetDialogues([new DialogueBlock { Id = "a", Text = "rien" }]);
        var depth = s.PastDepth;

        var count = s.GlobalFindReplace("absent", "x");

        Assert.Equal(0, count);
        Assert.Equal(depth, s.PastDepth);
    }

    // ── Mixeur audio ─────────────────────────────────────────────────────────

    [Fact]
    public void EffectiveTrackVolume_RespectsMuteAndSolo()
    {
        var s = new ProjectState();
        var voix = s.AudioTracks.First(t => t.Id == "voix");

        // Pas de solo : volume nominal
        Assert.Equal(voix.Volume, s.EffectiveTrackVolume(voix));

        // Solo sur « voix » : les autres pistes sont écrasées à 0
        s.UpdateAudioTrack("voix", t => t with { Solo = true });
        voix = s.AudioTracks.First(t => t.Id == "voix");
        var original = s.AudioTracks.First(t => t.Id == "original");
        Assert.Equal(voix.Volume, s.EffectiveTrackVolume(voix));
        Assert.Equal(0, s.EffectiveTrackVolume(original));
    }

    // ── Import / export round-trip ───────────────────────────────────────────

    [Fact]
    public void ToProjectFile_ThenImport_RestoresState()
    {
        var s = new ProjectState { Fps = 30, SyncOffset = 0.25, TotalLanes = 5 };
        s.SetDialogues([Block("a", start: 1, dur: 2, text: "hello")]);

        var file = s.ToProjectFile();
        var restored = new ProjectState();
        restored.ImportProject(file);

        Assert.Equal(30, restored.Fps);
        Assert.Equal(0.25, restored.SyncOffset);
        Assert.Equal(5, restored.TotalLanes);
        Assert.Single(restored.Dialogues);
        Assert.Equal("hello", restored.Dialogues[0].Text);
        Assert.False(restored.CanUndo); // l'historique est réinitialisé à l'import
    }
}
