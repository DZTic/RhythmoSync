using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RhythmoSync.Core;
using RhythmoSync.Core.Models;

namespace RhythmoSync.App.Controls;

/// <summary>
/// Panneau d'édition du bloc sélectionné (port de l'EditorSidebar web) : texte,
/// personnage, couleur, piste, durée, plus suppression et infos de timecode.
/// Visible quand exactement un bloc est sélectionné, sinon un message d'invite.
///
/// Sémantique d'historique identique au web : un snapshot est pris à la prise de
/// focus d'un champ, et les frappes suivantes mettent à jour sans empiler
/// l'historique (skipHistory). Les rafraîchissements ne réécrivent jamais le
/// champ en cours d'édition (préservation du curseur).
/// </summary>
public sealed class BlockEditorPanel : ScrollViewer
{
    // Palette identique à celle de l'EditorSidebar web.
    private static readonly string[] Swatches =
        ["#6366f1", "#10b981", "#f59e0b", "#ef4444", "#8b5cf6", "#06b6d4", "#f97316", "#ec4899"];

    private static readonly Brush WarnFg = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    private ProjectState? _state;
    private bool _loading;
    private string? _focusSnapshotId; // bloc pour lequel un snapshot de focus est déjà pris

    // ── Conteneurs ──
    private readonly StackPanel _fields = new();
    private readonly TextBlock _hint;

    // ── Champs ──
    private readonly Border _headerSwatch;
    private readonly TextBlock _headerId;
    private readonly TextBox _textBox;
    private readonly Border _warning;
    private readonly TextBlock _warningText;
    private readonly TextBox _characterBox;
    private readonly Border[] _swatches;
    private readonly TextBox _hexBox;
    private readonly TextBox _laneBox;
    private readonly TextBox _durationBox;
    private readonly CheckBox _lockCheck;
    private readonly TextBlock _startText;
    private readonly TextBlock _endText;

    public BlockEditorPanel()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        _hint = new TextBlock
        {
            Text = "Sélectionnez un bloc pour modifier son texte, son personnage, sa couleur, sa piste et sa durée.",
            Foreground = Res<Brush>("TextMuted"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 16, 14, 14),
            Visibility = Visibility.Collapsed,
        };

        // En-tête : pastille de couleur + « Édition du bloc » + id court
        _headerSwatch = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        _headerId = new TextBlock { Foreground = Res<Brush>("TextMuted"), FontSize = 10, FontFamily = new FontFamily("Consolas") };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(_headerSwatch);
        var headerTexts = new StackPanel();
        headerTexts.Children.Add(new TextBlock { Text = "Édition du bloc", Foreground = Res<Brush>("TextPrimary"), FontSize = 12, FontWeight = FontWeights.Bold });
        headerTexts.Children.Add(_headerId);
        header.Children.Add(headerTexts);

        // Texte
        _textBox = MakeTextBox(multiline: true);
        _textBox.TextChanged += (_, _) => ApplyText();
        _warningText = new TextBlock { Foreground = WarnFg, FontSize = 10, TextWrapping = TextWrapping.Wrap };
        _warning = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xEF, 0x44, 0x44)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xEF, 0x44, 0x44)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 6, 0, 0),
            Child = _warningText, Visibility = Visibility.Collapsed,
        };

        // Personnage
        _characterBox = MakeTextBox(multiline: false);
        _characterBox.TextChanged += (_, _) => ApplyCharacter();

        // Couleur : 8 préréglages + champ hex
        var swatchRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        _swatches = new Border[Swatches.Length];
        for (var i = 0; i < Swatches.Length; i++)
        {
            var hex = Swatches[i];
            var swatch = new Border
            {
                Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(6),
                Background = BrushFromHex(hex), Cursor = Cursors.Hand, ToolTip = hex,
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
            };
            swatch.MouseLeftButtonUp += (_, _) => ApplyColor(hex, snapshot: true);
            _swatches[i] = swatch;
            swatchRow.Children.Add(swatch);
        }
        _hexBox = MakeTextBox(multiline: false);
        _hexBox.MaxLength = 7;
        _hexBox.ToolTip = "Couleur personnalisée (#rrggbb) — Entrée pour appliquer";
        _hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        _hexBox.LostFocus += (_, _) => ApplyHex();

        // Piste + durée
        _laneBox = MakeTextBox(multiline: false);
        _laneBox.TextAlignment = TextAlignment.Center;
        _laneBox.FontFamily = new FontFamily("Consolas");
        _laneBox.TextChanged += (_, _) => ApplyLane();

        _durationBox = MakeTextBox(multiline: false);
        _durationBox.TextAlignment = TextAlignment.Right;
        _durationBox.FontFamily = new FontFamily("Consolas");
        _durationBox.TextChanged += (_, _) => ApplyDuration();
        var resetDuration = new Button
        {
            Content = "⟲", Style = Res<Style>("ToolButton"), Width = 30, Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Réinitialiser à la durée par défaut",
        };
        resetDuration.Click += (_, _) =>
        {
            if (CurrentBlock() is not { } b || _state is null) return;
            _state.SnapshotHistory();
            _state.UpdateDialogue(b.Id, d => d with { Duration = _state.DefaultBlockDuration });
        };
        var durationRow = new DockPanel();
        DockPanel.SetDock(resetDuration, Dock.Right);
        durationRow.Children.Add(resetDuration);
        durationRow.Children.Add(_durationBox);

        var laneDurationGrid = new Grid();
        laneDurationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        laneDurationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        laneDurationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var laneCol = new StackPanel();
        laneCol.Children.Add(Label("PISTE"));
        laneCol.Children.Add(_laneBox);
        Grid.SetColumn(laneCol, 0);
        var durationCol = new StackPanel();
        durationCol.Children.Add(Label("DURÉE (S)"));
        durationCol.Children.Add(durationRow);
        Grid.SetColumn(durationCol, 2);
        laneDurationGrid.Children.Add(laneCol);
        laneDurationGrid.Children.Add(durationCol);

        // Timecode (lecture seule)
        _startText = MonoValue();
        _endText = MonoValue();
        var info = new Border
        {
            Background = Res<Brush>("BgDeep"), BorderBrush = Res<Brush>("BorderSubtle"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 4, 0, 0),
        };
        var infoStack = new StackPanel();
        infoStack.Children.Add(InfoRow("Début", _startText));
        infoStack.Children.Add(InfoRow("Fin", _endText));
        info.Child = infoStack;

        // Verrouillage (anti-déplacement accidentel)
        _lockCheck = new CheckBox
        {
            Content = "🔒  Verrouiller (anti-déplacement)",
            Foreground = Res<Brush>("TextPrimary"),
            FontSize = 12,
            Margin = new Thickness(0, 16, 0, 0),
            ToolTip = "Empêche le déplacement et le redimensionnement (Ctrl+L). Le texte reste éditable.",
        };
        _lockCheck.Click += (_, _) => ApplyLock();

        // Suppression
        var deleteButton = new Button
        {
            Content = "🗑  Supprimer le bloc", Style = Res<Style>("ToolButton"),
            Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(10, 6, 10, 6),
            Foreground = WarnFg,
        };
        deleteButton.Click += (_, _) =>
        {
            if (CurrentBlock() is { } b) _state?.DeleteDialogues([b.Id]);
        };

        // Assemblage
        _fields.Margin = new Thickness(14, 14, 14, 14);
        _fields.Children.Add(header);
        _fields.Children.Add(Label("TEXTE DU DIALOGUE"));
        _fields.Children.Add(_textBox);
        _fields.Children.Add(_warning);
        _fields.Children.Add(Spacer());
        _fields.Children.Add(Label("PERSONNAGE"));
        _fields.Children.Add(_characterBox);
        _fields.Children.Add(Spacer());
        _fields.Children.Add(Label("COULEUR DU BLOC"));
        _fields.Children.Add(swatchRow);
        _fields.Children.Add(_hexBox);
        _fields.Children.Add(Spacer());
        _fields.Children.Add(laneDurationGrid);
        _fields.Children.Add(Spacer());
        _fields.Children.Add(info);
        _fields.Children.Add(_lockCheck);
        _fields.Children.Add(deleteButton);

        var root = new Grid();
        root.Children.Add(_fields);
        root.Children.Add(_hint);
        Content = root;
    }

    public void Initialize(ProjectState state)
    {
        _state = state;
        state.SelectionChanged += Refresh;
        state.DialoguesChanged += Refresh;
        Refresh();
    }

    // ── Rafraîchissement ──────────────────────────────────────────────────────

    private DialogueBlock? CurrentBlock()
    {
        if (_state is null || _state.SelectedIds.Count != 1) return null;
        var id = _state.SelectedIds[0];
        return _state.Dialogues.FirstOrDefault(d => d.Id == id);
    }

    private void Refresh()
    {
        var block = CurrentBlock();
        if (block is null)
        {
            _fields.Visibility = Visibility.Collapsed;
            _hint.Visibility = Visibility.Visible;
            _hint.Text = _state is { } s && s.SelectedIds.Count > 1
                ? $"{s.SelectedIds.Count} blocs sélectionnés. Sélectionnez un seul bloc pour l'éditer."
                : "Sélectionnez un bloc pour modifier son texte, son personnage, sa couleur, sa piste et sa durée.";
            _focusSnapshotId = null;
            return;
        }

        _fields.Visibility = Visibility.Visible;
        _hint.Visibility = Visibility.Collapsed;

        _loading = true;
        try
        {
            // Champs éditables : ne pas écraser celui qui a le focus (curseur/saisie)
            if (!_textBox.IsKeyboardFocused) _textBox.Text = block.Text;
            if (!_characterBox.IsKeyboardFocused) _characterBox.Text = block.CharacterName;
            if (!_laneBox.IsKeyboardFocused) _laneBox.Text = (block.Lane + 1).ToString(CultureInfo.InvariantCulture);
            if (!_durationBox.IsKeyboardFocused) _durationBox.Text = block.Duration.ToString("0.##", CultureInfo.InvariantCulture);
            if (!_hexBox.IsKeyboardFocused) _hexBox.Text = block.Color;

            // Reflets en lecture seule
            _headerSwatch.Background = BrushFromHex(block.Color);
            _headerId.Text = "#" + (block.Id.Length >= 6 ? block.Id[^6..] : block.Id);
            _startText.Text = block.StartTime.ToString("0.000", CultureInfo.InvariantCulture) + " s";
            _endText.Text = block.EndTime.ToString("0.000", CultureInfo.InvariantCulture) + " s";
            _lockCheck.IsChecked = block.IsLocked;

            var tooFast = block.IsTooFast;
            _warning.Visibility = tooFast ? Visibility.Visible : Visibility.Collapsed;
            if (tooFast)
                _warningText.Text = $"Le texte défilera trop vite ({Math.Round(block.Text.Length / block.Duration)} car/s).";

            for (var i = 0; i < _swatches.Length; i++)
            {
                var selected = string.Equals(Swatches[i], block.Color, StringComparison.OrdinalIgnoreCase);
                _swatches[i].BorderBrush = selected ? Brushes.White : Brushes.Transparent;
                _swatches[i].BorderThickness = new Thickness(selected ? 2 : 0);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    // ── Application des changements ────────────────────────────────────────────

    /// <summary>Snapshot d'historique pris une seule fois par session de focus sur un bloc.</summary>
    private void SnapshotOncePerFocus()
    {
        if (CurrentBlock() is not { } b || _state is null) return;
        if (_focusSnapshotId == b.Id) return;
        _state.SnapshotHistory();
        _focusSnapshotId = b.Id;
    }

    private void ApplyText()
    {
        if (_loading || CurrentBlock() is not { } b || _state is null) return;
        var text = _textBox.Text;
        _state.UpdateDialogue(b.Id, d => d with { Text = text }, skipHistory: true);
    }

    private void ApplyCharacter()
    {
        if (_loading || CurrentBlock() is not { } b || _state is null) return;
        var name = _characterBox.Text;
        _state.UpdateDialogue(b.Id, d => d with { CharacterName = name }, skipHistory: true);
    }

    private void ApplyLane()
    {
        if (_loading || CurrentBlock() is not { } b || _state is null) return;
        if (!int.TryParse(_laneBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var oneBased)) return;
        var lane = Math.Clamp(oneBased - 1, 0, Math.Max(0, _state.TotalLanes - 1));
        _state.UpdateDialogue(b.Id, d => d with { Lane = lane }, skipHistory: true);
    }

    private void ApplyDuration()
    {
        if (_loading || CurrentBlock() is not { } b || _state is null) return;
        if (!double.TryParse(_durationBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return;
        if (seconds < 0.1) return;
        _state.UpdateDialogue(b.Id, d => d with { Duration = seconds }, skipHistory: true);
    }

    private void ApplyLock()
    {
        if (CurrentBlock() is not { } b || _state is null) return;
        var locked = _lockCheck.IsChecked == true;
        _state.SnapshotHistory();
        _state.UpdateDialogue(b.Id, d => d with { IsLocked = locked });
    }

    private void ApplyColor(string hex, bool snapshot)
    {
        if (CurrentBlock() is not { } b || _state is null) return;
        if (snapshot) _state.SnapshotHistory();
        _state.UpdateDialogue(b.Id, d => d with { Color = hex });
    }

    private void ApplyHex()
    {
        if (_loading || CurrentBlock() is not { } b || _state is null) return;
        var hex = _hexBox.Text.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (!IsValidHex(hex) || string.Equals(hex, b.Color, StringComparison.OrdinalIgnoreCase)) return;
        ApplyColor(hex, snapshot: true);
    }

    // ── Fabriques d'UI ────────────────────────────────────────────────────────

    private TextBox MakeTextBox(bool multiline)
    {
        var box = new TextBox
        {
            Background = Res<Brush>("BgDeep"),
            Foreground = Res<Brush>("TextPrimary"),
            BorderBrush = Res<Brush>("BorderSubtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12,
            CaretBrush = Brushes.White,
        };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 64;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        box.GotKeyboardFocus += (_, _) => SnapshotOncePerFocus();
        return box;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = Res<Brush>("TextMuted"),
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 4),
    };

    private static FrameworkElement Spacer() => new Border { Height = 14 };

    private static TextBlock MonoValue() => new()
    {
        Foreground = Res<Brush>("TextPrimary"),
        FontFamily = new FontFamily("Consolas"),
        FontWeight = FontWeights.Bold,
        FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private static DockPanel InfoRow(string label, TextBlock value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(new TextBlock { Text = label, Foreground = Res<Brush>("TextMuted"), FontSize = 10 });
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        return row;
    }

    private static bool IsValidHex(string hex) =>
        hex.Length == 7 && hex[0] == '#' &&
        hex.Skip(1).All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    private static Brush BrushFromHex(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1)); }
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}
