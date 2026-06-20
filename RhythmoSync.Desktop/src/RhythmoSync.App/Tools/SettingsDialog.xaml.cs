using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RhythmoSync.Core;

namespace RhythmoSync.App.Tools;

/// <summary>
/// Boîte de dialogue « Réglages » (port de la modale SETTINGS + STATS web) :
/// durée par défaut, décalage de synchro, statistiques du projet, actions
/// « réinitialiser la vue » / « tout effacer », et liste de référence des raccourcis.
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

        BuildShortcutList();
    }

    /// <summary>Liste de référence des raccourcis (reflète <c>MainWindow.OnWindowKeyDown</c>).</summary>
    private void BuildShortcutList()
    {
        (string Keys, string Label)[] shortcuts =
        [
            ("Espace", "Lecture / Pause"),
            ("Ctrl+Z  /  Ctrl+Y", "Annuler / Rétablir"),
            ("Suppr", "Supprimer la sélection"),
            ("Ctrl+C  /  Ctrl+V", "Copier / Coller"),
            ("Ctrl+G  /  Ctrl+Maj+G", "Grouper / Dégrouper"),
            ("Ctrl+M", "Fusionner deux blocs sélectionnés"),
            ("← / →", "Tête de lecture (image ; Maj = 1 s)"),
            ("Ctrl+← / →", "Décaler la sélection"),
            ("Ctrl++  /  Ctrl+−", "Zoom avant / arrière"),
            ("Ctrl+S  /  Ctrl+Maj+S", "Enregistrer / Enregistrer sous"),
            ("Ctrl+O", "Ouvrir un projet"),
            ("Ctrl+I", "Importer une vidéo"),
            ("Ctrl+H", "Rechercher / Remplacer"),
            ("F11 / Échap", "Mode Présentation / quitter"),
        ];

        var muted = (Brush)FindResource("TextMuted");
        var primary = (Brush)FindResource("TextPrimary");
        foreach (var (keys, label) in shortcuts)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var keyText = new TextBlock
            {
                Text = keys, Foreground = primary, FontFamily = new FontFamily("Consolas"),
                FontSize = 11, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            DockPanel.SetDock(keyText, Dock.Right);
            row.Children.Add(keyText);
            row.Children.Add(new TextBlock { Text = label, Foreground = muted, FontSize = 11 });
            ShortcutList.Children.Add(row);
        }
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
