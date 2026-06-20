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

    // ── Fusion de blocs ──────────────────────────────────────────────────────

    [Fact]
    public void MergeSelected_CombinesIntervalAndText()
    {
        var s = new ProjectState();
        s.SetDialogues([
            new DialogueBlock { Id = "a", StartTime = 1, Duration = 2, Text = "Bonjour", CharacterName = "Jean", Color = "#abc", Lane = 0 },
            new DialogueBlock { Id = "b", StartTime = 3, Duration = 1, Text = "le monde", CharacterName = "Marie", Color = "#def", Lane = 0 },
        ]);
        s.SelectBlock("a");
        s.SelectBlock("b", multi: true);

        Assert.True(s.CanMergeSelected);
        Assert.True(s.MergeSelected());

        Assert.Single(s.Dialogues);
        var m = s.Dialogues[0];
        Assert.Equal("a", m.Id);                 // identité du bloc le plus précoce
        Assert.Equal(1, m.StartTime, 9);
        Assert.Equal(4, m.EndTime, 9);           // de 1 à 4
        Assert.Equal("Bonjour le monde", m.Text);
        Assert.Equal("Jean", m.CharacterName);   // personnage/couleur du 1er bloc
        Assert.Equal("#abc", m.Color);
        Assert.True(s.IsSelected("a"));          // le bloc fusionné reste sélectionné
    }

    [Fact]
    public void MergeSelected_OrdersByStartTimeRegardlessOfSelectionOrder()
    {
        var s = new ProjectState();
        s.SetDialogues([
            new DialogueBlock { Id = "late", StartTime = 5, Duration = 1, Text = "deux", Lane = 0 },
            new DialogueBlock { Id = "early", StartTime = 1, Duration = 1, Text = "un", Lane = 0 },
        ]);
        s.SelectBlock("late");
        s.SelectBlock("early", multi: true);

        Assert.True(s.MergeSelected());
        var m = s.Dialogues[0];
        Assert.Equal("early", m.Id);
        Assert.Equal("un deux", m.Text);
        Assert.Equal(1, m.StartTime, 9);
        Assert.Equal(6, m.EndTime, 9);
    }

    [Fact]
    public void CanMergeSelected_FalseForDifferentLanes()
    {
        var s = new ProjectState();
        s.SetDialogues([
            new DialogueBlock { Id = "a", StartTime = 1, Duration = 1, Lane = 0 },
            new DialogueBlock { Id = "b", StartTime = 2, Duration = 1, Lane = 1 },
        ]);
        s.SelectBlock("a");
        s.SelectBlock("b", multi: true);

        Assert.False(s.CanMergeSelected);
        Assert.False(s.MergeSelected());
        Assert.Equal(2, s.Dialogues.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void MergeSelected_RequiresExactlyTwoBlocks(int selectionCount)
    {
        var s = new ProjectState();
        s.SetDialogues([
            new DialogueBlock { Id = "a", StartTime = 0, Duration = 1, Lane = 0 },
            new DialogueBlock { Id = "b", StartTime = 1, Duration = 1, Lane = 0 },
            new DialogueBlock { Id = "c", StartTime = 2, Duration = 1, Lane = 0 },
        ]);
        var ids = new[] { "a", "b", "c" }.Take(selectionCount).ToList();
        s.SelectBlock(ids[0]);
        foreach (var id in ids.Skip(1)) s.SelectBlock(id, multi: true);

        Assert.False(s.CanMergeSelected);
        Assert.False(s.MergeSelected());
    }

    [Fact]
    public void MergeSelected_IsUndoable()
    {
        var s = new ProjectState();
        s.SetDialogues([
            new DialogueBlock { Id = "a", StartTime = 0, Duration = 1, Text = "x", Lane = 0 },
            new DialogueBlock { Id = "b", StartTime = 1, Duration = 1, Text = "y", Lane = 0 },
        ]);
        s.SelectBlock("a");
        s.SelectBlock("b", multi: true);
        s.MergeSelected();
        Assert.Single(s.Dialogues);

        s.Undo();
        Assert.Equal(2, s.Dialogues.Count);
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
