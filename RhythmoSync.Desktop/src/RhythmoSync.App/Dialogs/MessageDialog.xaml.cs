using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RhythmoSync.App.Dialogs;

public enum MessageKind { Info, Success, Warning, Error }

/// <summary>
/// Boîte de dialogue d'information au thème sombre de l'application (remplace les
/// MessageBox système, clairs et hors charte). Affiche une pastille colorée selon
/// le type, un titre, un message, et — optionnellement — le fichier produit avec
/// un bouton « Ouvrir le dossier ».
/// </summary>
public partial class MessageDialog : Window
{
    private string? _revealPath;

    public MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>Affiche une boîte de dialogue thématisée modale au-dessus de <paramref name="owner"/>.</summary>
    public static void Show(Window? owner, MessageKind kind, string title, string message, string? filePath = null)
    {
        var dlg = new MessageDialog();
        if (owner is not null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.Populate(kind, title, message, filePath);
        dlg.ShowDialog();
    }

    private void Populate(MessageKind kind, string title, string message, string? filePath)
    {
        var (glyph, color) = kind switch
        {
            MessageKind.Success => ("✓", Color.FromRgb(0x22, 0xc5, 0x5e)),
            MessageKind.Warning => ("!", Color.FromRgb(0xf5, 0x9e, 0x0b)),
            MessageKind.Error   => ("✕", Color.FromRgb(0xef, 0x44, 0x44)),
            _                   => ("i", Color.FromRgb(0x63, 0x66, 0xf1)),
        };

        var accent = new SolidColorBrush(color);
        accent.Freeze();
        IconGlyph.Text = glyph;
        IconBadge.Background = accent;
        AccentStripe.Background = accent;

        TitleText.Text = title;
        MessageText.Text = message;

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _revealPath = filePath;
            FileNameText.Text = Path.GetFileName(filePath);
            FileNameText.ToolTip = filePath;
            FileChip.Visibility = Visibility.Visible;
            RevealButton.Visibility = File.Exists(filePath) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (_revealPath is null) return;
        try
        {
            // Ouvre l'Explorateur sur le dossier en sélectionnant le fichier produit.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_revealPath}\"") { UseShellExecute = true });
        }
        catch { /* l'Explorateur peut être indisponible : on ignore silencieusement */ }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
