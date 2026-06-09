using RhythmoSync.Core.Models;

namespace RhythmoSync.Core;

public enum SnapTargetKind { SyncLine, BlockEdge }

/// <summary>Résultat d'un snap : temps corrigé + position de l'indicateur visuel.</summary>
public readonly record struct SnapResult(double SnappedTime, double IndicatorTime, SnapTargetKind Kind);

/// <summary>
/// Magnétisme des blocs (portage de computeSnap de RhythmoBand.tsx).
/// Le seuil est adaptatif : ~15 pixels à l'écran quel que soit le zoom.
/// </summary>
public static class SnapEngine
{
    private const double ThresholdPx = 15;

    /// <summary>
    /// Snap du déplacement d'un bloc : son début ou sa fin peut accrocher la ligne
    /// de synchro ou les bords des autres blocs. Retourne null si aucun snap.
    /// </summary>
    public static SnapResult? SnapMove(
        double rawStartTime, double duration, string blockId,
        IReadOnlyList<DialogueBlock> all, double targetSyncTime, double zoom)
    {
        var threshold = ThresholdPx / zoom;
        var endTime = rawStartTime + duration;
        SnapResult? best = null;
        var bestDelta = double.MaxValue;

        void TryStart(double candidate, SnapTargetKind kind)
        {
            var delta = Math.Abs(rawStartTime - candidate);
            if (delta < threshold && delta < bestDelta)
            {
                bestDelta = delta;
                best = new SnapResult(candidate, candidate, kind);
            }
        }

        void TryEnd(double candidate, SnapTargetKind kind)
        {
            var newStart = candidate - duration;
            var delta = Math.Abs(endTime - candidate);
            if (delta < threshold && newStart >= 0 && delta < bestDelta)
            {
                bestDelta = delta;
                best = new SnapResult(newStart, candidate, kind);
            }
        }

        TryStart(targetSyncTime, SnapTargetKind.SyncLine);
        TryEnd(targetSyncTime, SnapTargetKind.SyncLine);

        foreach (var other in all)
        {
            if (other.Id == blockId) continue;
            TryStart(other.StartTime, SnapTargetKind.BlockEdge);
            TryStart(other.EndTime, SnapTargetKind.BlockEdge);
            TryEnd(other.StartTime, SnapTargetKind.BlockEdge);
            TryEnd(other.EndTime, SnapTargetKind.BlockEdge);
        }

        return best;
    }

    /// <summary>
    /// Snap d'un bord pendant un redimensionnement : le temps candidat (début ou fin
    /// du bloc) accroche la ligne de synchro ou les bords des autres blocs.
    /// </summary>
    public static SnapResult? SnapEdge(
        double rawTime, string blockId,
        IReadOnlyList<DialogueBlock> all, double targetSyncTime, double zoom)
    {
        var threshold = 20.0 / zoom;
        SnapResult? best = null;
        var bestDelta = threshold;

        void Try(double candidate, SnapTargetKind kind)
        {
            var delta = Math.Abs(rawTime - candidate);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = new SnapResult(candidate, candidate, kind);
            }
        }

        Try(targetSyncTime, SnapTargetKind.SyncLine);
        foreach (var other in all)
        {
            if (other.Id == blockId) continue;
            Try(other.StartTime, SnapTargetKind.BlockEdge);
            Try(other.EndTime, SnapTargetKind.BlockEdge);
        }

        return best;
    }
}
