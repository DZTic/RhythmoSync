using System.Windows.Input;
using RhythmoSync.Core;

namespace RhythmoSync.App.Controls;

/// <summary>
/// Gestion commune de la molette sur la timeline (bande rythmo + forme d'onde) :
///  - molette seule = défilement temporel, proportionnel à la fenêtre visible
///    (donc d'un « ressenti » constant quel que soit le zoom). Molette vers le
///    haut = avancer dans le temps.
///  - Ctrl + molette = zoom centré sur le point sous le curseur.
/// </summary>
internal static class TimelineWheel
{
    /// <param name="seek">Repositionne la tête de lecture (typiquement via SeekRequested).</param>
    public static void Handle(MouseWheelEventArgs e, ProjectState state, double time,
                              double cursorX, double viewWidth, Action<double> seek)
    {
        var notches = e.Delta / 120.0;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Zoom centré sur le curseur : on garde le temps situé sous le curseur
            // immobile en compensant la tête de lecture (fixée sous la ligne de
            // synchro). Sans cette compensation, le zoom convergerait vers la ligne
            // rouge plutôt que vers le pointeur.
            var oldPps = state.ZoomLevel;
            var newPps = Math.Clamp(oldPps * Math.Pow(1.2, notches), 20, 1200);
            if (newPps == oldPps) return;
            var cursorOffset = cursorX - RhythmoConstants.SyncLinePositionX;
            var timeShift = cursorOffset * (1 / oldPps - 1 / newPps);
            state.ZoomLevel = newPps;
            seek(Math.Max(0, time + timeShift));
        }
        else
        {
            var step = viewWidth / state.ZoomLevel * 0.15 * notches;
            seek(Math.Max(0, time + step));
        }
    }
}
