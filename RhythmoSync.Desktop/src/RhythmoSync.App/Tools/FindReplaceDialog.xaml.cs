using System.Windows;

namespace RhythmoSync.App.Tools;

/// <summary>
/// Boîte de dialogue « Rechercher et remplacer » (port de la modale FIND de la
/// version web). Les champs sont exposés par <see cref="Find"/> / <see cref="Replace"/>
/// une fois le dialogue validé ; le remplacement effectif est fait par l'appelant.
/// </summary>
public partial class FindReplaceDialog : Window
{
    public string Find { get; private set; } = "";
    public string Replace { get; private set; } = "";

    public FindReplaceDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => FindBox.Focus();
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FindBox.Text))
        {
            MessageBox.Show(this, "Entrez le texte à rechercher.",
                "Rechercher et remplacer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Find = FindBox.Text;
        Replace = ReplaceBox.Text;
        DialogResult = true;
    }
}
