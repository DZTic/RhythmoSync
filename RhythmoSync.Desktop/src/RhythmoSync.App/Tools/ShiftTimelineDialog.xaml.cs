using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RhythmoSync.App.Tools;

/// <summary>
/// Boîte de dialogue « Décaler la timeline » (port de la modale SHIFT de la
/// version web). Saisit un décalage en secondes ; le résultat est exposé par
/// <see cref="Offset"/> une fois le dialogue validé.
/// </summary>
public partial class ShiftTimelineDialog : Window
{
    private static readonly Regex NumberChars = new(@"^[0-9.,\-]+$");

    /// <summary>Décalage saisi en secondes (valide uniquement si ShowDialog a renvoyé true).</summary>
    public double Offset { get; private set; }

    public ShiftTimelineDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { OffsetBox.Focus(); OffsetBox.SelectAll(); };
    }

    private void OnOffsetPreviewInput(object sender, TextCompositionEventArgs e)
    {
        // N'autorise que ce qui peut composer un nombre décimal signé
        e.Handled = !NumberChars.IsMatch(e.Text);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(OffsetBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var offset))
        {
            MessageBox.Show(this, "Décalage invalide — entrez un nombre de secondes (ex. 1.5 ou -0.4).",
                "Décaler la timeline", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Offset = offset;
        DialogResult = true;
    }
}
