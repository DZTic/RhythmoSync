using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RhythmoSync.Core;
using RhythmoSync.Core.Models;
using RhythmoSync.Media;

namespace RhythmoSync.App;

public partial class MainWindow : Window
{
    private readonly ProjectState _state = new();

    // ── Horloge de lecture ────────────────────────────────────────────────────
    // MediaElement.Position ne se met à jour que par paliers ; comme dans la
    // version web (rAF + performance.now), on extrapole entre deux paliers avec
    // un Stopwatch pour obtenir un défilement parfaitement fluide.
    private bool _isPlaying;
    private double _duration;
    private double _lastMediaPos = -1;
    private long _anchorTicks;
    private double _playbackRate = 1.0;
    private bool _mediaReady;

    private string? _ffmpegPath;
    private string? _currentProjectPath;
    private CancellationTokenSource? _waveformCts;
    private DialogueBlock? _editingBlock;
    private int _frameCount;

    public MainWindow()
    {
        InitializeComponent();

        Band.Initialize(_state);
        Wave.Initialize(_state);
        Band.SeekRequested += SeekTo;
        Wave.SeekRequested += SeekTo;
        Band.EditRequested += BeginEditBlock;

        _state.DialoguesChanged += UpdateStatusBar;
        _state.ViewChanged += () =>
        {
            LaneCountText.Text = _state.TotalLanes.ToString();
            if (Math.Abs(ZoomSlider.Value - _state.ZoomLevel) > 0.5) ZoomSlider.Value = _state.ZoomLevel;
            SnapCheck.IsChecked = _state.SnapEnabled;
        };

        foreach (var fps in new[] { 23.976, 24, 25, 29.97, 30, 50, 60 })
            FpsCombo.Items.Add(fps.ToString(CultureInfo.InvariantCulture));
        FpsCombo.SelectedItem = "25";

        foreach (var rate in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            RateCombo.Items.Add("×" + rate.ToString(CultureInfo.InvariantCulture));
        RateCombo.SelectedIndex = 2;

        Media.Volume = VolumeSlider.Value;

        _ffmpegPath = FfmpegLocator.Find();
        UpdateStatusBar();

        PreviewKeyDown += OnWindowKeyDown;
        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    // ── Boucle de rendu (équivalent du requestAnimationFrame web) ────────────

    private void OnRendering(object? sender, EventArgs e)
    {
        var time = GetClockTime();
        Band.UpdateTime(time);
        Wave.UpdateTime(time);

        // Le timecode n'a pas besoin de 60 Hz : mise à jour 1 frame sur 4.
        if (++_frameCount % 4 == 0)
            TimecodeText.Text = FormatTimecode(time);
    }

    private double GetClockTime()
    {
        if (!_mediaReady) return 0;
        var mediaPos = Media.Position.TotalSeconds;
        var now = Stopwatch.GetTimestamp();
        if (mediaPos != _lastMediaPos)
        {
            _lastMediaPos = mediaPos;
            _anchorTicks = now;
        }
        var time = mediaPos;
        if (_isPlaying)
            time += (now - _anchorTicks) / (double)Stopwatch.Frequency * _playbackRate;
        return Math.Clamp(time, 0, _duration > 0 ? _duration : double.MaxValue);
    }

    private string FormatTimecode(double seconds)
    {
        var fps = Math.Max(1, _state.Fps);
        var h = (int)(seconds / 3600);
        var m = (int)(seconds / 60) % 60;
        var s = (int)seconds % 60;
        var f = (int)((seconds - Math.Floor(seconds)) * fps);
        return $"{h:00}:{m:00}:{s:00}:{f:00}";
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    private void SeekTo(double time)
    {
        if (!_mediaReady) return;
        time = Math.Clamp(time, 0, _duration);
        Media.Position = TimeSpan.FromSeconds(time);
        _lastMediaPos = -1; // ré-ancrage de l'extrapolation à la prochaine frame
    }

    private void TogglePlay()
    {
        if (!_mediaReady) return;
        if (_isPlaying)
        {
            Media.Pause();
            _isPlaying = false;
        }
        else
        {
            if (_duration > 0 && GetClockTime() >= _duration - 0.05) SeekTo(0);
            Media.Play();
            _isPlaying = true;
        }
        Band.IsPlaying = _isPlaying;
        PlayButton.Content = _isPlaying ? "⏸  Pause" : "▶  Lecture";
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        _duration = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan.TotalSeconds : 0;
        DurationText.Text = "/ " + FormatTimecode(_duration);
        VideoPlaceholder.Visibility = Visibility.Collapsed;
        UpdateStatusBar();
        // Affiche la première image
        Media.Play();
        Media.Pause();
        SeekTo(0);
        _ = GenerateWaveformAsync();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        Band.IsPlaying = false;
        PlayButton.Content = "▶  Lecture";
        Media.Pause();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        VideoPlaceholder.Visibility = Visibility.Visible;
        MessageBox.Show(this,
            "Impossible de lire cette vidéo : " + (e.ErrorException?.Message ?? "format non supporté") +
            "\n\nLe lecteur Windows natif lit les MP4 (H.264/AAC), WMV et AVI. " +
            "Pour les autres formats (MKV, HEVC…), convertissez la vidéo ou installez les codecs.",
            "Erreur vidéo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ── Forme d'onde ──────────────────────────────────────────────────────────

    private async Task GenerateWaveformAsync()
    {
        var videoPath = _state.VideoPath;
        if (videoPath is null || !File.Exists(videoPath)) return;

        if (_ffmpegPath is null && !await TryDownloadFfmpegAsync())
        {
            StatusLeft.Text = "FFmpeg introuvable — forme d'onde indisponible.";
            return;
        }
        if (_ffmpegPath is null) return;

        _waveformCts?.Cancel();
        var cts = new CancellationTokenSource();
        _waveformCts = cts;
        StatusLeft.Text = "Génération de la forme d'onde…";
        try
        {
            var numSamples = (int)Math.Clamp(_duration * 40, 2000, 65536);
            var data = await Task.Run(() => WaveformGenerator.GenerateAsync(_ffmpegPath, videoPath, numSamples, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                Wave.SetWaveform(data);
                StatusLeft.Text = "Forme d'onde prête.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusLeft.Text = "Forme d'onde indisponible : " + ex.Message;
        }
    }

    private bool _ffmpegDownloadDeclined;

    /// <summary>Propose et effectue le téléchargement de FFmpeg (équivalent du flux de l'ancienne app).</summary>
    private async Task<bool> TryDownloadFfmpegAsync()
    {
        if (_ffmpegDownloadDeclined) return false;
        var answer = MessageBox.Show(this,
            "FFmpeg est nécessaire pour générer la forme d'onde audio.\n\n" +
            "Voulez-vous le télécharger automatiquement (~90 Mo, une seule fois) ?",
            "FFmpeg requis", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            _ffmpegDownloadDeclined = true;
            return false;
        }

        try
        {
            var progress = new Progress<double>(p =>
                StatusLeft.Text = $"Téléchargement de FFmpeg… {p * 100:0}%");
            _ffmpegPath = await FfmpegDownloader.DownloadAsync(progress);
            StatusLeft.Text = "FFmpeg installé.";
            UpdateStatusBar();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Échec du téléchargement de FFmpeg : " + ex.Message,
                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ── Fichiers ──────────────────────────────────────────────────────────────

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Les données non sauvegardées seront perdues. Continuer ?",
                "Nouveau projet", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _state.ResetProject();
        _currentProjectPath = null;
        UnloadVideo();
    }

    private void UnloadVideo()
    {
        Media.Stop();
        Media.Source = null;
        _mediaReady = false;
        _isPlaying = false;
        _duration = 0;
        Band.IsPlaying = false;
        PlayButton.Content = "▶  Lecture";
        DurationText.Text = "/ 00:00:00:00";
        VideoPlaceholder.Visibility = Visibility.Visible;
        Wave.SetWaveform(null);
        UpdateStatusBar();
    }

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Projet RhythmoSync (*.rsp;*.json)|*.rsp;*.json|Tous les fichiers (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var project = ProjectIo.Load(dialog.FileName);
            _state.ImportProject(project);
            _currentProjectPath = dialog.FileName;

            if (project.VideoPath is { } videoPath && File.Exists(videoPath))
            {
                LoadVideo(videoPath);
            }
            else if (project.VideoPath is not null)
            {
                UnloadVideo();
                MessageBox.Show(this,
                    $"Le projet a été chargé, mais la vidéo associée est introuvable :\n{project.VideoPath}",
                    "Vidéo manquante", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Impossible d'ouvrir le projet : " + ex.Message,
                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSaveProject(object sender, RoutedEventArgs e) => SaveProject(saveAs: false);

    private void SaveProject(bool saveAs)
    {
        var path = _currentProjectPath;
        if (saveAs || path is null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Projet RhythmoSync (*.rsp)|*.rsp|JSON (*.json)|*.json",
                FileName = "projet-rhythmosync.rsp",
            };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }

        try
        {
            ProjectIo.Save(path, _state.ToProjectFile());
            _currentProjectPath = path;
            StatusLeft.Text = $"Projet enregistré : {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Erreur lors de la sauvegarde : " + ex.Message,
                "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImportVideo(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Vidéo|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm|Tous les fichiers (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        LoadVideo(dialog.FileName);
    }

    private void LoadVideo(string path)
    {
        _state.VideoPath = path;
        _mediaReady = false;
        Media.Source = new Uri(path);
        // MediaOpened prendra le relais (durée, waveform, première image)
        Media.Play();
        Media.Pause();
        UpdateStatusBar();
    }

    // ── Export vidéo ─────────────────────────────────────────────────────────

    private async void OnExportVideo(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady || _state.VideoPath is null)
        {
            MessageBox.Show(this, "Importez d'abord une vidéo avant d'exporter.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_state.Dialogues.Count == 0)
        {
            MessageBox.Show(this, "Le projet ne contient aucun bloc de dialogue à incruster.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_ffmpegPath is null && !await TryDownloadFfmpegAsync())
        {
            MessageBox.Show(this, "FFmpeg est indispensable pour l'export vidéo.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var nativeWidth = Media.NaturalVideoWidth;
        var nativeHeight = Media.NaturalVideoHeight;
        if (nativeWidth <= 0 || nativeHeight <= 0)
        {
            MessageBox.Show(this, "Dimensions de la vidéo inconnues — réessayez après le chargement complet.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_isPlaying) TogglePlay();
        var dialog = new Export.ExportDialog(_state, _state.VideoPath, _ffmpegPath!,
            _duration, nativeWidth, nativeHeight)
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    // ── Blocs ─────────────────────────────────────────────────────────────────

    private void OnAddBlock(object sender, RoutedEventArgs e)
    {
        var time = GetClockTime() + _state.SyncOffset;
        var color = RhythmoConstants.CharacterColors[_state.Dialogues.Count % RhythmoConstants.CharacterColors.Length];
        var block = new DialogueBlock
        {
            Text = "Texte",
            StartTime = Math.Max(0, time),
            Duration = _state.DefaultBlockDuration,
            Lane = 0,
            Color = color,
        };
        _state.AddDialogue(block);
        _state.SelectBlock(block.Id);
    }

    private void BeginEditBlock(DialogueBlock block, Rect rect)
    {
        if (_isPlaying) TogglePlay();
        _editingBlock = block;
        Canvas.SetLeft(EditBox, rect.X);
        Canvas.SetTop(EditBox, rect.Y);
        EditBox.Width = rect.Width;
        EditBox.Height = rect.Height;
        EditBox.FontSize = Math.Max(12, rect.Height * 0.45);
        EditBox.Text = block.Text;
        EditBox.Visibility = Visibility.Visible;
        EditBox.Focus();
        EditBox.SelectAll();
    }

    private void CommitEdit(bool save)
    {
        if (_editingBlock is null) return;
        var block = _editingBlock;
        _editingBlock = null;
        EditBox.Visibility = Visibility.Collapsed;
        if (save)
        {
            var text = EditBox.Text;
            _state.UpdateDialogue(block.Id, d => d with { Text = text });
        }
        Focus();
    }

    private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitEdit(save: true); e.Handled = true; }
        else if (e.Key == Key.Escape) { CommitEdit(save: false); e.Handled = true; }
    }

    private void OnEditBoxLostFocus(object sender, RoutedEventArgs e) => CommitEdit(save: true);

    // ── Réglages (barre d'outils) ────────────────────────────────────────────

    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_state is not null) _state.ZoomLevel = e.NewValue;
    }

    private void OnSnapChanged(object sender, RoutedEventArgs e)
    {
        if (_state is not null) _state.SnapEnabled = SnapCheck.IsChecked == true;
    }

    private void OnLaneMinus(object sender, RoutedEventArgs e) => _state.TotalLanes--;
    private void OnLanePlus(object sender, RoutedEventArgs e) => _state.TotalLanes++;

    private void OnFpsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_state is not null && FpsCombo.SelectedItem is string s &&
            double.TryParse(s, CultureInfo.InvariantCulture, out var fps))
            _state.Fps = fps;
    }

    private void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RateCombo.SelectedItem is string s &&
            double.TryParse(s.TrimStart('×'), CultureInfo.InvariantCulture, out var rate))
        {
            _playbackRate = rate;
            Media.SpeedRatio = rate;
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media is not null) Media.Volume = e.NewValue;
    }

    // ── Raccourcis clavier ───────────────────────────────────────────────────

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Laisse les zones de texte gérer leur propre saisie
        if (Keyboard.FocusedElement is TextBox) return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                e.Handled = true;
                break;

            case Key.Z when ctrl:
                _state.Undo();
                e.Handled = true;
                break;

            case Key.Y when ctrl:
                _state.Redo();
                e.Handled = true;
                break;

            case Key.Delete or Key.Back:
                if (_state.SelectedIds.Count > 0)
                {
                    _state.DeleteDialogues(_state.SelectedIds.ToList());
                    e.Handled = true;
                }
                break;

            case Key.S when ctrl:
                SaveProject(saveAs: shift);
                e.Handled = true;
                break;

            case Key.O when ctrl:
                OnOpenProject(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.I when ctrl:
                OnImportVideo(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.G when ctrl && shift:
                _state.UngroupSelected();
                e.Handled = true;
                break;

            case Key.G when ctrl:
                _state.GroupSelected();
                e.Handled = true;
                break;

            case Key.C when ctrl:
                CopySelection();
                e.Handled = true;
                break;

            case Key.V when ctrl:
                PasteClipboard();
                e.Handled = true;
                break;

            case Key.Left or Key.Right:
            {
                var step = shift ? 1.0 : 1.0 / Math.Max(1, _state.Fps);
                var delta = e.Key == Key.Left ? -step : step;
                if (ctrl && _state.SelectedIds.Count > 0)
                    NudgeSelection(delta);
                else
                    SeekTo(GetClockTime() + delta);
                e.Handled = true;
                break;
            }

            case Key.Add or Key.OemPlus when ctrl:
                _state.ZoomLevel *= 1.2;
                e.Handled = true;
                break;

            case Key.Subtract or Key.OemMinus when ctrl:
                _state.ZoomLevel /= 1.2;
                e.Handled = true;
                break;
        }
    }

    private void NudgeSelection(double deltaSeconds)
    {
        var updates = new Dictionary<string, Func<DialogueBlock, DialogueBlock>>();
        foreach (var id in _state.SelectedIds)
            updates[id] = d => d with { StartTime = Math.Max(0, d.StartTime + deltaSeconds) };
        _state.UpdateDialogues(updates);
    }

    private void CopySelection()
    {
        var blocks = _state.Dialogues.Where(d => _state.IsSelected(d.Id)).ToList();
        if (blocks.Count == 0) return;
        _state.Clipboard.Clear();
        _state.Clipboard.AddRange(blocks);
        StatusLeft.Text = $"{blocks.Count} bloc(s) copié(s).";
    }

    private void PasteClipboard()
    {
        if (_state.Clipboard.Count == 0) return;
        var origin = _state.Clipboard.Min(b => b.StartTime);
        var target = GetClockTime() + _state.SyncOffset;
        var pasted = _state.Clipboard
            .Select(b => b with { Id = Guid.NewGuid().ToString(), StartTime = Math.Max(0, b.StartTime - origin + target), GroupId = null })
            .ToList();
        _state.AddDialogues(pasted);
        _state.SelectBlock(null);
        foreach (var b in pasted) _state.SelectBlock(b.Id, multi: true);
    }

    // ── Barre d'état ─────────────────────────────────────────────────────────

    private void UpdateStatusBar()
    {
        var parts = new List<string> { $"{_state.Dialogues.Count} bloc(s)" };
        if (_state.VideoPath is { } vp) parts.Add(Path.GetFileName(vp));
        parts.Add(_ffmpegPath is null ? "FFmpeg : introuvable" : "FFmpeg : OK");
        StatusRight.Text = string.Join("   •   ", parts);
    }
}
