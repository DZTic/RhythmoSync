using RhythmoSync.Core;
using RhythmoSync.Core.Models;
using Xunit;

namespace RhythmoSync.Core.Tests;

public class SnapEngineTests
{
    private const double Zoom = 200; // px/s ⇒ seuil SnapMove = 15/200 = 0,075 s

    private static DialogueBlock Block(string id, double start, double dur) =>
        new() { Id = id, StartTime = start, Duration = dur };

    [Fact]
    public void SnapMove_SnapsStartToSyncLineWithinThreshold()
    {
        var result = SnapEngine.SnapMove(
            rawStartTime: 1.04, duration: 2, blockId: "x",
            all: [], targetSyncTime: 1.0, zoom: Zoom);

        Assert.NotNull(result);
        Assert.Equal(1.0, result!.Value.SnappedTime, 9);
        Assert.Equal(SnapTargetKind.SyncLine, result.Value.Kind);
    }

    [Fact]
    public void SnapMove_ReturnsNullWhenNothingIsClose()
    {
        var result = SnapEngine.SnapMove(
            rawStartTime: 5.0, duration: 2, blockId: "x",
            all: [], targetSyncTime: 1.0, zoom: Zoom);

        Assert.Null(result);
    }

    [Fact]
    public void SnapMove_SnapsStartToAnotherBlockEdge()
    {
        var others = new[] { Block("a", start: 1, dur: 2) }; // EndTime = 3
        var result = SnapEngine.SnapMove(
            rawStartTime: 3.02, duration: 1, blockId: "x",
            all: others, targetSyncTime: 99, zoom: Zoom);

        Assert.NotNull(result);
        Assert.Equal(3.0, result!.Value.SnappedTime, 9);
        Assert.Equal(SnapTargetKind.BlockEdge, result.Value.Kind);
    }

    [Fact]
    public void SnapMove_IgnoresTheMovingBlockItself()
    {
        var others = new[] { Block("x", start: 3, dur: 1) }; // même id que le bloc déplacé
        var result = SnapEngine.SnapMove(
            rawStartTime: 3.02, duration: 1, blockId: "x",
            all: others, targetSyncTime: 99, zoom: Zoom);

        Assert.Null(result);
    }

    [Fact]
    public void SnapMove_EndSnapRejectedWhenItWouldPushStartNegative()
    {
        // La fin accrocherait targetSync=0,5 mais newStart = 0,5 - 2 < 0 → rejeté.
        // Le début (-1,49) n'est pas proche de 0,5 non plus → aucun snap valide.
        var result = SnapEngine.SnapMove(
            rawStartTime: -1.49, duration: 2, blockId: "x",
            all: [], targetSyncTime: 0.5, zoom: Zoom);

        Assert.Null(result);
    }

    [Fact]
    public void SnapEdge_SnapsToSyncLine()
    {
        var result = SnapEngine.SnapEdge(
            rawTime: 2.05, blockId: "x",
            all: [], targetSyncTime: 2.0, zoom: Zoom); // seuil = 20/200 = 0,1 s

        Assert.NotNull(result);
        Assert.Equal(2.0, result!.Value.SnappedTime, 9);
        Assert.Equal(SnapTargetKind.SyncLine, result.Value.Kind);
    }

    [Fact]
    public void SnapEdge_PrefersClosestCandidate()
    {
        var others = new[] { Block("a", start: 2.0, dur: 1) }; // bords à 2,0 et 3,0
        // rawTime 2,02 : proche du bord de bloc (2,0) ; la sync (2,5) est hors seuil
        var result = SnapEngine.SnapEdge(
            rawTime: 2.02, blockId: "x",
            all: others, targetSyncTime: 2.5, zoom: Zoom);

        Assert.NotNull(result);
        Assert.Equal(2.0, result!.Value.SnappedTime, 9);
        Assert.Equal(SnapTargetKind.BlockEdge, result.Value.Kind);
    }
}
