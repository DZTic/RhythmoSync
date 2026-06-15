using System.Globalization;
using System.Windows;
using RhythmoSync.Core;

namespace RhythmoSync.App.Tools;

/// <summary>
/// Boîte de dialogue « Réglages » (port de la modale SETTINGS + STATS web) :
/// durée par défaut, décalage de synchro, statistiques du projet, et actions
/// « réinitialiser la vue » / « tout effacer ».
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly ProjectState _state;

    /// <summary>L'utilisateur a demandé la réinitialisation de la vue (zoom, hauteur, tête de lecture).</summary>
    public bool ResetViewRequested { get; private set; }

    public SettingsDialog(ProjectState state)
    {
        InitializeComponent();
        _state = state;

        DurationBox.Text = state.DefaultBlockDuration.ToString("0.##", CultureInfo.InvariantCulture);
        SyncOffsetBox.Text = Math.Round(state.SyncOffset * 1000).ToString(CultureInfo.InvariantCulture);

        StatBlocks.Text = state.Dialogues.Count.ToString(CultureInfo.InvariantCulture);
        var total = state.Dialogues.Sum(d => d.Duration);
        StatDuration.Text = total.ToString("0.00", CultureInfo.InvariantCulture) + " s";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(DurationBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration < 0.1)
        {
            MessageBox.Show(this, "Durée par défaut invalide (≥ 0,1 s).", "Réglages", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!double.TryParse(SyncOffsetBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetMs))
        {
            MessageBox.Show(this, "Décalage de synchro invalide (en millisecondes).", "Réglages", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _state.DefaultBlockDuration = duration;
        _state.SyncOffset = offsetMs / 1000.0;
        DialogResult = true;
    }

    private void OnResetView(object sender, RoutedEventArgs e)
    {
        ResetViewRequested = true;
        DialogResult = true;
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        if (_state.Dialogues.Count == 0)
        {
            MessageBox.Show(this, "Aucun bloc à effacer.", "Tout effacer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Supprimer les {_state.Dialogues.Count} bloc(s) de la timeline ?\nCette action est annulable (Ctrl+Z).",
                "Tout effacer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _state.SetDialogues([]);
        StatBlocks.Text = "0";
        StatDuration.Text = "0.00 s";
    }
}
