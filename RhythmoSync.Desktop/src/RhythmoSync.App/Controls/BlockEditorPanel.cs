using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RhythmoSync.App.Audio;
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
    private string? _focusSnapshotId;   // bloc pour lequel un snapshot de focus est déjà pris
    private string? _displayedBlockId;  // bloc actuellement reflété par les champs

    // DialoguesChanged arrive à ~60 Hz pendant un drag et à chaque frappe : le
    // rafraîchissement est coalescé (une passe quand le dispatcher respire) et différé
    // si le panneau est masqué — BuildTakesList fait des File.Exists (I/O disque) qu'il
    // ne faut surtout pas exécuter à chaque frame sur le thread UI.
    private bool _refreshQueued;
    private bool _pendingWhileHidden;

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

    // ── Enregistrement (doublage) ──
    private readonly ComboBox _micCombo;
    private readonly Button _recordButton;
    private readonly TextBlock _takeStatus;
    private readonly StackPanel _takesList;   // une ligne par prise (A/B/C…), reconstruite à chaque Refresh
    private bool _settingMics;   // évite de lever MicDeviceChanged pendant le peuplement
    private bool _isRecording;

    /// <summary>Bouton « Enregistrer / Arrêter » cliqué : MainWindow décide démarrage vs arrêt.</summary>
    public event Action? RecordToggleRequested;
    /// <summary>Rendre active la prise d'index donné (celle qui sera lue/exportée).</summary>
    public event Action<int>? SelectTakeRequested;
    /// <summary>Écouter la prise d'index donné.</summary>
    public event Action<int>? PlayTakeRequested;
    /// <summary>Télécharger (enregistrer sous) la prise d'index donné.</summary>
    public event Action<int>? DownloadTakeRequested;
    /// <summary>Supprimer la prise d'index donné.</summary>
    public event Action<int>? DeleteTakeRequested;
    /// <summary>Le micro choisi a changé (id du périphérique, ou null = défaut système).</summary>
    public event Action<string?>? MicDeviceChanged;

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

        // ── Enregistrement (prise de doublage du bloc) ──
        _micCombo = new ComboBox
        {
            Background = Res<Brush>("BgDeep"), Foreground = Res<Brush>("TextPrimary"),
            BorderBrush = Res<Brush>("BorderSubtle"), FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6, 3, 6, 3),
            ToolTip = "Micro utilisé pour l'enregistrement",
        };
        _micCombo.SelectionChanged += (_, _) =>
        {
            if (_settingMics) return;
            MicDeviceChanged?.Invoke((_micCombo.SelectedItem as ComboBoxItem)?.Tag as string);
        };

        _recordButton = new Button
        {
            Content = "●  Enregistrer", Style = Res<Style>("ToolButton"),
            Padding = new Thickness(10, 6, 10, 6), Foreground = WarnFg,
            ToolTip = "Démarre la lecture 3 s avant le bloc puis enregistre la voix (R)",
        };
        _recordButton.Click += (_, _) => RecordToggleRequested?.Invoke();

        _takeStatus = new TextBlock
        {
            Text = "Aucune prise", Foreground = Res<Brush>("TextMuted"), FontSize = 11,
            Margin = new Thickness(0, 8, 0, 4), VerticalAlignment = VerticalAlignment.Center,
        };
        _takesList = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };

        var recordingSection = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        recordingSection.Children.Add(Label("ENREGISTREMENT"));
        recordingSection.Children.Add(_micCombo);
        recordingSection.Children.Add(_recordButton);
        recordingSection.Children.Add(_takeStatus);
        recordingSection.Children.Add(_takesList);

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
        _fields.Children.Add(recordingSection);
        _fields.Children.Add(_lockCheck);
        _fields.Children.Add(deleteButton);

        var root = new Grid();
        root.Children.Add(_fields);
        root.Children.Add(_hint);
        Content = root;

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || !_pendingWhileHidden) return;
            _pendingWhileHidden = false;
            RefreshCore();
        };
    }

    public void Initialize(ProjectState state)
    {
        _state = state;
        state.SelectionChanged += Refresh;
        state.DialoguesChanged += Refresh;
        Refresh();
    }

    /// <summary>Remplit la liste des micros et sélectionne celui actif (null = défaut système).</summary>
    public void SetMics(IReadOnlyList<MicDevice> devices, string? selectedId)
    {
        _settingMics = true;
        try
        {
            _micCombo.Items.Clear();
            _micCombo.Items.Add(new ComboBoxItem { Content = "Micro par défaut", Tag = null });
            foreach (var d in devices)
                _micCombo.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });

            var match = _micCombo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == selectedId);
            _micCombo.SelectedItem = match ?? _micCombo.Items[0];
        }
        finally { _settingMics = false; }
    }

    /// <summary>Met à jour l'apparence du bouton et verrouille les contrôles pendant une prise.</summary>
    public void SetRecording(bool recording)
    {
        _isRecording = recording;
        _recordButton.Content = recording ? "■  Arrêter" : "●  Enregistrer";
        if (recording) _recordButton.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        else _recordButton.ClearValue(BackgroundProperty); // rétablit le fond du style
        _recordButton.Foreground = recording ? Brushes.White : WarnFg;
        _micCombo.IsEnabled = !recording;
        Refresh();
    }

    // ── Rafraîchissement ──────────────────────────────────────────────────────

    private DialogueBlock? CurrentBlock()
    {
        if (_state is null || _state.SelectedIds.Count != 1) return null;
        var id = _state.SelectedIds[0];
        return _state.Dialogues.FirstOrDefault(d => d.Id == id);
    }

    /// <summary>Rafraîchissement coalescé : plusieurs événements → une seule passe.</summary>
    private void Refresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            if (!IsVisible) { _pendingWhileHidden = true; return; }
            RefreshCore();
        }));
    }

    private void RefreshCore()
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
            _displayedBlockId = null;
            return;
        }

        _fields.Visibility = Visibility.Visible;
        _hint.Visibility = Visibility.Collapsed;

        // Si on bascule sur un AUTRE bloc, on réécrit tous les champs même celui qui a
        // le focus : la protection « ne pas écraser le champ en cours de saisie » ne
        // vaut que pour les rafraîchissements du même bloc (sinon le champ garderait la
        // valeur de l'ancien bloc — p.ex. un n° de piste périmé).
        var blockChanged = block.Id != _displayedBlockId;
        _displayedBlockId = block.Id;

        _loading = true;
        try
        {
            // Champs éditables : ne pas écraser celui qui a le focus (curseur/saisie),
            // sauf quand on vient de changer de bloc.
            if (blockChanged || !_textBox.IsKeyboardFocused) _textBox.Text = block.Text;
            if (blockChanged || !_characterBox.IsKeyboardFocused) _characterBox.Text = block.CharacterName;
            if (blockChanged || !_laneBox.IsKeyboardFocused) _laneBox.Text = (block.Lane + 1).ToString(CultureInfo.InvariantCulture);
            if (blockChanged || !_durationBox.IsKeyboardFocused) _durationBox.Text = block.Duration.ToString("0.##", CultureInfo.InvariantCulture);
            if (blockChanged || !_hexBox.IsKeyboardFocused) _hexBox.Text = block.Color;

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

            // Section enregistrement : liste des prises du bloc (A/B/C…).
            BuildTakesList(block);
        }
        finally
        {
            _loading = false;
        }
    }

    // ── Liste des prises (A/B/C…) ──────────────────────────────────────────────

    private static readonly Brush TakeActiveFg = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));

    /// <summary>Reconstruit la liste des prises du bloc : statut + une ligne par prise.</summary>
    private void BuildTakesList(DialogueBlock block)
    {
        _takesList.Children.Clear();
        var takes = block.TakeList;
        var count = takes.Count;

        _takeStatus.Text = _isRecording ? "● Enregistrement…"
            : count == 0 ? "Aucune prise"
            : count == 1 ? "1 prise" : $"{count} prises";
        _takeStatus.Foreground = count > 0 && !_isRecording ? TakeActiveFg : Res<Brush>("TextMuted");

        for (var i = 0; i < count; i++)
        {
            var isActive = string.Equals(takes[i], block.AudioFile, StringComparison.OrdinalIgnoreCase);
            _takesList.Children.Add(BuildTakeRow(i, TakeLabel(i), isActive, File.Exists(takes[i])));
        }
    }

    /// <summary>Une ligne : sélecteur de prise active (gauche) + écouter / télécharger / supprimer (droite).</summary>
    private FrameworkElement BuildTakeRow(int index, string label, bool isActive, bool exists)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };

        // Actions, ancrées à droite (ajout du plus à droite vers la gauche).
        var del = SmallTakeButton("🗑", "Supprimer cette prise");
        del.Foreground = WarnFg;
        del.IsEnabled = !_isRecording;
        del.Click += (_, _) => DeleteTakeRequested?.Invoke(index);
        DockPanel.SetDock(del, Dock.Right);
        row.Children.Add(del);

        var download = SmallTakeButton("⤓", "Télécharger cette prise (WAV)");
        download.IsEnabled = exists && !_isRecording;
        download.Click += (_, _) => DownloadTakeRequested?.Invoke(index);
        DockPanel.SetDock(download, Dock.Right);
        row.Children.Add(download);

        var play = SmallTakeButton("▶", "Écouter cette prise");
        play.IsEnabled = exists && !_isRecording;
        play.Click += (_, _) => PlayTakeRequested?.Invoke(index);
        DockPanel.SetDock(play, Dock.Right);
        row.Children.Add(play);

        // Sélecteur de prise active : remplit le reste de la ligne (LastChildFill).
        var select = new Button
        {
            Content = (isActive ? "● " : "○ ") + label + (exists ? "" : "  (introuvable)"),
            Style = Res<Style>("ToolButton"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 4, 8, 4),
            FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
            Foreground = isActive ? TakeActiveFg : Res<Brush>("TextPrimary"),
            IsEnabled = !_isRecording,
            ToolTip = isActive ? "Prise active (lue et exportée)" : "Rendre cette prise active",
        };
        select.Click += (_, _) => SelectTakeRequested?.Invoke(index);
        row.Children.Add(select);

        return row;
    }

    private Button SmallTakeButton(string glyph, string tip) => new()
    {
        Content = glyph, Style = Res<Style>("ToolButton"),
        Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(4, 0, 0, 0),
        MinWidth = 30, ToolTip = tip,
    };

    /// <summary>« Prise A », « Prise B »… puis AA, AB… (le débordement reste théorique).</summary>
    private static string TakeLabel(int index)
    {
        var s = "";
        index++;
        while (index > 0) { index--; s = (char)('A' + index % 26) + s; index /= 26; }
        return "Prise " + s;
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
