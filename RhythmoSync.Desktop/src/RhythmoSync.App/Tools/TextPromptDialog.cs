using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RhythmoSync.App.Tools;

/// <summary>
/// Petite boîte de dialogue de saisie d'une ligne de texte (nommer / renommer).
/// Construite en code pour rester légère et réutilisable.
/// </summary>
internal sealed class TextPromptDialog : Window
{
    private readonly TextBox _box;

    /// <summary>Texte saisi, espaces de bord retirés.</summary>
    public string Value => _box.Text.Trim();

    public TextPromptDialog(Window owner, string title, string prompt, string initial = "")
    {
        Owner = owner;
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Res("BgPanel", Color.FromRgb(0x1e, 0x29, 0x3b));

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = prompt,
            Foreground = Res("TextPrimary", Colors.White),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });

        _box = new TextBox
        {
            Text = initial,
            Background = Res("BgDeep", Color.FromRgb(0x0f, 0x17, 0x25)),
            Foreground = Res("TextPrimary", Colors.White),
            BorderBrush = Res("BorderSubtle", Color.FromRgb(0x33, 0x41, 0x55)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            CaretBrush = Brushes.White,
        };
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        root.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button { Content = "OK", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Annuler", MinWidth = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void Accept()
    {
        DialogResult = true;
    }

    private static SolidColorBrush Res(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b;
        return new SolidColorBrush(fallback);
    }
}
