using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using RhythmoSync.Core;
using RhythmoSync.Core.Models;
using RhythmoSync.Media;

namespace RhythmoSync.App.Whisper;

/// <summary>
/// Dialogue de transcription Whisper (port de WhisperPanel.tsx) : choix du modèle
/// (téléchargement/suppression), langue, lancement de l'analyse avec progression,
/// prévisualisation des segments avec répartition Personnage A/B sur deux pistes,
/// puis import dans la bande rythmo.
/// </summary>
public partial class WhisperDialog : Window
{
    private readonly ProjectState _state;
    private readonly string _videoPath;
    private readonly string _ffmpegPath;

    private readonly string? _cliPath;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private readonly ObservableCollection<SegmentVm> _segments = [];
    private readonly List<string> _modelNames = [];
    private bool _updatingSelectAll;

    /// <summary>Nombre de blocs importés dans la bande (lu par MainWindow après ShowDialog).</summary>
    public int ImportedCount { get; private set; }

    private static readonly string[] ProfileColors = ["#8b5cf6", "#10b981"];
    internal static readonly Brush[] ProfileBrushes = CreateProfileBrushes();

    private static Brush[] CreateProfileBrushes()
    {
        var brushes = new Brush[ProfileColors.Length];
        for (var i = 0; i < ProfileColors.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ProfileColors[i]));
            brush.Freeze();
            brushes[i] = brush;
        }
        return brushes;
    }

    private static readonly (string Code, string Label)[] Languages =
    [
        ("auto", "Auto-détection"),
        ("fr", "Français"),
        ("en", "English"),
        ("es", "Español"),
        ("de", "Deutsch"),
        ("it", "Italiano"),
        ("pt", "Português"),
        ("ja", "日本語"),
        ("zh", "中文"),
        ("ko", "한국어"),
        ("ru", "Русский"),
        ("ar", "العربية"),
    ];

    public WhisperDialog(ProjectState state, string videoPath, string ffmpegPath)
    {
        InitializeComponent();
        _state = state;
        _videoPath = videoPath;
        _ffmpegPath = ffmpegPath;

        VideoFileText.Text = Path.GetFileName(videoPath);
        SegmentsList.ItemsSource = _segments;

        foreach (var (_, label) in Languages) LanguageCombo.Items.Add(label);
        LanguageCombo.SelectedIndex = 0;

        _cliPath = WhisperService.FindCli();
        CliStateText.Text = _cliPath is null
            ? "⚠ whisper-cli.exe introuvable. Placez le dossier « whisper » de whisper.cpp à côté de l'application " +
              $"(ou dans {WhisperService.DefaultWhisperDir})."
            : $"whisper-cli : {_cliPath}";

        RefreshModelList(preferred: "base");
    }

    // ── Modèles ───────────────────────────────────────────────────────────────

    private void RefreshModelList(string? preferred)
    {
        var installed = WhisperService.ListInstalledModels();
        _modelNames.Clear();
        ModelCombo.Items.Clear();

        foreach (var info in WhisperService.KnownModels)
        {
            _modelNames.Add(info.Name);
            ModelCombo.Items.Add($"{info.Name} — {info.SizeLabel}");
        }
        // Modèles déposés à la main (large-v3, quantifiés…) : utilisables aussi
        foreach (var name in installed.Where(n => !_modelNames.Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            _modelNames.Add(name);
            ModelCombo.Items.Add(name);
        }

        var index = preferred is null ? -1 : _modelNames.FindIndex(n => n.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = _modelNames.FindIndex(n => installed.Contains(n, StringComparer.OrdinalIgnoreCase));
        ModelCombo.SelectedIndex = index >= 0 ? index : 0;
        UpdateModelState();
    }

    private string SelectedModel =>
        ModelCombo.SelectedIndex >= 0 && ModelCombo.SelectedIndex < _modelNames.Count
            ? _modelNames[ModelCombo.SelectedIndex] : "base";

    private void OnModelChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateModelState();

    private void UpdateModelState()
    {
        if (ModelStateText is null) return; // appelé pendant InitializeComponent
        var installed = WhisperService.FindModelFile(SelectedModel) is not null;
        ModelStateText.Text = installed ? "● installé" : "non installé";
        ModelStateText.Foreground = installed ? new SolidColorBrush(Color.FromRgb(0x10, 0xb9, 0x81))
                                              : (Brush)FindResource("TextMuted");
        DownloadModelButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        DeleteModelButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        TranscribeButton.IsEnabled = installed && _cliPath is not null && !_busy;
    }

    private async void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var model = SelectedModel;
        SetBusy(true, $"Téléchargement du modèle {model}…");
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p =>
            {
                TranscribeBar.Value = p * 100;
                ProgressMessageText.Text = $"{p * 100:0} %";
            });
            ProgressStageText.Text = $"Téléchargement du modèle {model} (Hugging Face)…";
            await WhisperService.DownloadModelAsync(model, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            ShowError("Téléchargement annulé.");
        }
        catch (Exception ex)
        {
            ShowError("Échec du téléchargement du modèle : " + ex.Message);
        }
        finally
        {
            SetBusy(false);
            RefreshModelList(model);
        }
    }

    private void OnDeleteModel(object sender, RoutedEventArgs e)
    {
        var model = SelectedModel;
        if (MessageBox.Show(this,
                $"Supprimer le modèle « {model} » du disque ?\nIl pourra être retéléchargé au besoin.",
                "Supprimer le modèle", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        WhisperService.DeleteModel(model);
        RefreshModelList(model);
    }

    // ── Transcription ─────────────────────────────────────────────────────────

    private async void OnTranscribe(object sender, RoutedEventArgs e)
    {
        if (_busy || _cliPath is null) return;
        var modelPath = WhisperService.FindModelFile(SelectedModel);
        if (modelPath is null) return;
        var language = Languages[Math.Max(0, LanguageCombo.SelectedIndex)].Code;

        SetBusy(true, "Préparation…");
        ResultsPanel.Visibility = Visibility.Collapsed;
        _segments.Clear();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<WhisperProgressInfo>(p =>
            {
                TranscribeBar.Value = p.Percent;
                ProgressStageText.Text = p.Stage;
                ProgressMessageText.Text = p.Message;
            });
            var result = await WhisperService.TranscribeAsync(
                _ffmpegPath, _cliPath, modelPath, _videoPath, language, progress, _cts.Token);

            BuildSegments(result);
            var langLabel = result.Language.Length > 0 ? $" · langue : {result.Language}" : "";
            SummaryText.Text = $"{result.Segments.Count} segment(s) détecté(s) · durée parlée {FormatTime(result.Duration)}{langLabel}";
            ResultsPanel.Visibility = Visibility.Visible;
            TranscribeButton.Content = "🪄  Réanalyser";
            ImportButton.Visibility = Visibility.Visible;
            UpdateImportButton();
        }
        catch (OperationCanceledException)
        {
            ShowError("Analyse annulée.");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnCancelTranscribe(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void BuildSegments(WhisperTranscription result)
    {
        // Auto-assignation : un silence de plus de 1,2 s suggère un changement
        // d'interlocuteur (même heuristique que l'ancien WhisperPanel).
        var speaker = 0;
        double lastEnd = 0;
        foreach (var seg in result.Segments)
        {
            if (seg.StartTime - lastEnd > 1.2) speaker = (speaker + 1) % ProfileColors.Length;
            lastEnd = seg.StartTime + seg.Duration;

            var vm = new SegmentVm(this, seg) { ProfileIndex = speaker, IsSelected = true };
            vm.PropertyChanged += OnSegmentPropertyChanged;
            _segments.Add(vm);
        }
    }

    private void OnSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SegmentVm.IsSelected)) return;
        UpdateImportButton();
        _updatingSelectAll = true;
        SelectAllCheck.IsChecked = _segments.All(s => s.IsSelected);
        _updatingSelectAll = false;
    }

    private void UpdateImportButton()
    {
        var count = _segments.Count(s => s.IsSelected);
        ImportButton.Content = $"⤵  Importer {count} segment(s)";
        ImportButton.IsEnabled = count > 0 && !_busy;
    }

    private void OnSelectAllToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSelectAll) return;
        var value = SelectAllCheck.IsChecked == true;
        foreach (var vm in _segments) vm.IsSelected = value;
    }

    private void OnSegmentProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SegmentVm vm)
            vm.ProfileIndex = (vm.ProfileIndex + 1) % ProfileColors.Length;
    }

    private void OnProfileNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        foreach (var vm in _segments) vm.RefreshProfile();
    }

    internal string ProfileNameOf(int index)
    {
        var box = index == 0 ? ProfileABox : ProfileBBox;
        var name = box?.Text.Trim();
        return string.IsNullOrEmpty(name) ? (index == 0 ? "Personnage A" : "Personnage B") : name;
    }

    // ── Import dans la bande ──────────────────────────────────────────────────

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var selected = _segments.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0) return;

        var maxLane = _state.TotalLanes - 1;
        var blocks = selected.Select(s => new DialogueBlock
        {
            Text = s.Text,
            StartTime = s.StartTime,
            Duration = s.Duration,
            CharacterName = ProfileNameOf(s.ProfileIndex),
            Color = ProfileColors[s.ProfileIndex],
            Lane = Math.Min(s.ProfileIndex, maxLane),
        }).ToList();

        _state.AddDialogues(blocks); // snapshot d'historique inclus → Ctrl+Z annule l'import
        ImportedCount = blocks.Count;
        Close();
    }

    // ── Divers ────────────────────────────────────────────────────────────────

    private void SetBusy(bool busy, string? stage = null)
    {
        _busy = busy;
        ErrorText.Visibility = Visibility.Collapsed;
        ConfigPanel.IsEnabled = !busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelTranscribeButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TranscribeButton.IsEnabled = !busy && _cliPath is not null && WhisperService.FindModelFile(SelectedModel) is not null;
        ImportButton.IsEnabled = !busy && _segments.Any(s => s.IsSelected);
        if (busy)
        {
            TranscribeBar.Value = 0;
            ProgressStageText.Text = stage ?? "";
            ProgressMessageText.Text = "";
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }

    private static string FormatTime(double seconds)
    {
        var m = (int)(seconds / 60);
        return $"{m}:{seconds % 60:00.0}";
    }

    // ── ViewModel de segment ──────────────────────────────────────────────────

    public sealed class SegmentVm : INotifyPropertyChanged
    {
        private readonly WhisperDialog _owner;
        private bool _isSelected;
        private int _profileIndex;

        public SegmentVm(WhisperDialog owner, WhisperSegment segment)
        {
            _owner = owner;
            Text = segment.Text;
            StartTime = segment.StartTime;
            Duration = segment.Duration;
        }

        public string Text { get; }
        public double StartTime { get; }
        public double Duration { get; }

        public string TimeLabel => $"{FormatTime(StartTime)}  ·  {Duration:0.0}s";

        public bool IsSelected
        {
            get => _isSelected;
            set { if (value != _isSelected) { _isSelected = value; Notify(nameof(IsSelected)); } }
        }

        public int ProfileIndex
        {
            get => _profileIndex;
            set
            {
                if (value == _profileIndex) return;
                _profileIndex = value;
                Notify(nameof(ProfileIndex));
                RefreshProfile();
            }
        }

        public string ProfileName => _owner.ProfileNameOf(_profileIndex);
        public Brush ProfileBrush => ProfileBrushes[_profileIndex];

        public void RefreshProfile()
        {
            Notify(nameof(ProfileName));
            Notify(nameof(ProfileBrush));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
