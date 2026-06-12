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

    // ── Mixeur audio multi-pistes ─────────────────────────────────────────────
    // La piste « Original » module le volume de MediaElement ; les pistes
    // externes (Voix, Bruitages…) sont des MediaPlayer asservis au transport.
    private Audio.AudioMixer? _mixer;

    private string? _ffmpegPath;
    private string? _currentProjectPath;
    private CancellationTokenSource? _waveformCts;
    private DialogueBlock? _editingBlock;
    private int _frameCount;

    // ── Proxy All-Intra ───────────────────────────────────────────────────────
    // _state.VideoPath reste TOUJOURS le fichier original (.rsp, export, waveform,
    // letterbox) ; seul Media.Source reçoit le proxy quand le format est illisible.
    private string? _playbackPath;          // ce que lit réellement MediaElement
    private VideoProbeResult? _videoInfo;   // sonde FFmpeg du fichier ORIGINAL
    private CancellationTokenSource? _proxyCts;
    private string? _waveformVideoPath;     // anti-doublon (proxy + MediaOpened)

    private bool IsPlayingProxy =>
        _playbackPath is not null && _state.VideoPath is not null &&
        !string.Equals(_playbackPath, _state.VideoPath, StringComparison.OrdinalIgnoreCase);

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

        _mixer = new Audio.AudioMixer(_state, GetClockTime);
        _mixer.TrackFailed += (name, message) =>
            StatusLeft.Text = $"Piste « {name} » : lecture impossible ({message}).";
        Mixer.Initialize(_state);
        _state.AudioTracksChanged += ApplyAudioVolumes;
        ApplyAudioVolumes();

        _ffmpegPath = FfmpegLocator.Find();
        UpdateStatusBar();
        UpdateProxyCacheButton();

        // Fichier passé en ligne de commande : projet .rsp/.json ou vidéo
        if (App.StartupFile is { } startupFile)
        {
            Loaded += (_, _) =>
            {
                var ext = Path.GetExtension(startupFile).ToLowerInvariant();
                if (ext is ".rsp" or ".json") OpenProjectFromPath(startupFile);
                else LoadVideo(startupFile);
            };
        }

        PreviewKeyDown += OnWindowKeyDown;
        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            _mixer?.Dispose();
        };
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

        // Recalage des pistes externes du mixeur (2 fois/seconde suffit).
        if (_isPlaying && _frameCount % 32 == 0)
            _mixer?.UpdateDrift();
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
        _mixer?.Seek(time);
    }

    private void TogglePlay()
    {
        if (!_mediaReady) return;
        if (_isPlaying)
        {
            Media.Pause();
            _mixer?.Pause();
            _isPlaying = false;
        }
        else
        {
            if (_duration > 0 && GetClockTime() >= _duration - 0.05) SeekTo(0);
            Media.Play();
            _mixer?.Play();
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
        _mixer?.Pause();
    }

    private async void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        VideoPlaceholder.Visibility = Visibility.Visible;

        var original = _state.VideoPath;
        // Repli du flux hybride : un format que la sonde croyait lisible a échoué
        // → on propose le proxy. Sauf si c'est déjà le proxy qui échoue (pas de boucle).
        if (original is not null && !IsPlayingProxy)
        {
            var answer = MessageBox.Show(this,
                "Windows ne peut pas lire cette vidéo : " + (e.ErrorException?.Message ?? "format non supporté") +
                "\n\nVoulez-vous générer une copie de lecture (proxy H.264) avec FFmpeg ?\n" +
                "La vidéo originale reste utilisée pour la sauvegarde et l'export.",
                "Format non supporté", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                if (_videoInfo is null && _ffmpegPath is not null)
                {
                    try { _videoInfo = await VideoProber.ProbeAsync(_ffmpegPath, original); }
                    catch { }
                }
                await LoadViaProxyAsync(original);
                return;
            }
        }

        MessageBox.Show(this,
            "Impossible de lire cette vidéo : " + (e.ErrorException?.Message ?? "format non supporté") +
            "\n\nLe lecteur Windows natif lit les MP4 (H.264/AAC), WMV et AVI.",
            "Erreur vidéo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ── Forme d'onde ──────────────────────────────────────────────────────────

    private async Task GenerateWaveformAsync()
    {
        var videoPath = _state.VideoPath;
        if (videoPath is null || !File.Exists(videoPath)) return;

        // Déjà lancée pour cette vidéo (cas proxy : démarrée pendant l'encodage,
        // puis MediaOpened du proxy re-déclenche) — ne pas tout recommencer.
        if (videoPath == _waveformVideoPath) return;
        _waveformVideoPath = videoPath;

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
            // _duration vaut 0 quand on démarre pendant l'encodage du proxy → sonde
            var knownDuration = _duration > 0 ? _duration : _videoInfo?.Duration ?? 0;
            var numSamples = (int)Math.Clamp(knownDuration * 40, 2000, 65536);
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
    private async Task<bool> TryDownloadFfmpegAsync(string purpose = "générer la forme d'onde audio")
    {
        if (_ffmpegDownloadDeclined) return false;
        var answer = MessageBox.Show(this,
            $"FFmpeg est nécessaire pour {purpose}.\n\n" +
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
        _proxyCts?.Cancel();
        Media.Stop();
        Media.Source = null;
        _mixer?.Pause();
        _mediaReady = false;
        _isPlaying = false;
        _duration = 0;
        _playbackPath = null;
        _videoInfo = null;
        _waveformVideoPath = null;
        Band.IsPlaying = false;
        PlayButton.Content = "▶  Lecture";
        DurationText.Text = "/ 00:00:00:00";
        ProxyPanel.Visibility = Visibility.Collapsed;
        VideoPlaceholder.Text = "Importez une vidéo pour commencer  (Ctrl+I)";
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
        OpenProjectFromPath(dialog.FileName);
    }

    private void OpenProjectFromPath(string fileName)
    {
        try
        {
            var project = ProjectIo.Load(fileName);
            _state.ImportProject(project);
            _currentProjectPath = fileName;

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
            Filter = "Vidéo|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.flv;*.ts;*.m2ts;*.mpg;*.mpeg;*.ogv|Tous les fichiers (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        LoadVideo(dialog.FileName);
    }

    /// <summary>
    /// Flux hybride : sonde FFmpeg → les formats connus illisibles (MKV, HEVC…)
    /// partent directement en proxy All-Intra ; les autres tentent la lecture
    /// native, avec repli proxy proposé par OnMediaFailed.
    /// </summary>
    private async void LoadVideo(string path)
    {
        _state.VideoPath = path; // l'original, toujours
        _proxyCts?.Cancel();
        _videoInfo = null;
        _waveformVideoPath = null; // le garde anti-doublon ne vaut que pour CE chargement
        ProxyPanel.Visibility = Visibility.Collapsed;
        VideoPlaceholder.Text = "Chargement de la vidéo…";
        VideoPlaceholder.Visibility = Visibility.Visible;
        UpdateStatusBar();

        if (_ffmpegPath is not null)
        {
            try { _videoInfo = await VideoProber.ProbeAsync(_ffmpegPath, path); }
            catch { /* sonde indisponible : tentative native, MediaFailed couvrira */ }
        }

        if (_videoInfo is { NeedsProxy: true })
            await LoadViaProxyAsync(path);
        else
            PlayFile(path);
    }

    /// <summary>Branche réellement un fichier (original ou proxy) sur MediaElement.</summary>
    private void PlayFile(string playbackPath)
    {
        _playbackPath = playbackPath;
        _mediaReady = false;
        Media.Source = new Uri(playbackPath);
        // MediaOpened prendra le relais (durée, waveform, première image)
        Media.Play();
        Media.Pause();
        UpdateStatusBar();
    }

    /// <summary>
    /// Charge la vidéo via un proxy : cache si disponible, sinon encodage non
    /// bloquant (progression dans le placeholder, bande et waveform utilisables).
    /// </summary>
    private async Task LoadViaProxyAsync(string originalPath)
    {
        if (_ffmpegPath is null && !await TryDownloadFfmpegAsync("lire ce format vidéo (une copie de lecture doit être encodée)"))
        {
            VideoPlaceholder.Text = "Format non lu par Windows — FFmpeg est requis pour générer une copie de lecture.";
            return;
        }

        if (ProxyGenerator.TryGetCached(originalPath) is { } cached)
        {
            StatusLeft.Text = "Copie de lecture trouvée dans le cache.";
            PlayFile(cached);
            return;
        }

        // La waveform vient de l'original : on la lance sans attendre le proxy
        _ = GenerateWaveformAsync();

        _proxyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _proxyCts = cts;
        VideoPlaceholder.Visibility = Visibility.Collapsed;
        ProxyPanel.Visibility = Visibility.Visible;
        ProxyBar.Value = 0;
        var reason = _videoInfo?.Reason is { Length: > 0 } r ? r : "Format non lu par Windows";
        ProxyText.Text = $"Encodage de la copie de lecture… 0 %\n({reason})";

        try
        {
            var progress = new Progress<double>(p =>
            {
                ProxyBar.Value = p * 100;
                ProxyText.Text = $"Encodage de la copie de lecture… {p * 100:0} %\n({reason})";
            });
            var duration = _videoInfo?.Duration ?? 0;
            var ffmpeg = _ffmpegPath!;
            var proxyPath = await Task.Run(
                () => ProxyGenerator.GenerateAsync(ffmpeg, originalPath, duration, progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            PlayFile(proxyPath);
        }
        catch (OperationCanceledException)
        {
            VideoPlaceholder.Text = "Encodage de la copie de lecture annulé.";
            VideoPlaceholder.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            VideoPlaceholder.Text = "Impossible de générer la copie de lecture.";
            VideoPlaceholder.Visibility = Visibility.Visible;
            MessageBox.Show(this, "Échec de l'encodage du proxy : " + ex.Message,
                "Erreur proxy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (_proxyCts == cts) ProxyPanel.Visibility = Visibility.Collapsed;
            UpdateStatusBar();
            UpdateProxyCacheButton();
        }
    }

    private void OnProxyCancel(object sender, RoutedEventArgs e) => _proxyCts?.Cancel();

    private void OnClearProxyCache(object sender, RoutedEventArgs e)
    {
        var size = ProxyGenerator.GetCacheSizeBytes();
        if (size == 0) { UpdateStatusBar(); return; }
        if (MessageBox.Show(this,
                $"Supprimer toutes les copies de lecture ({ProxyGenerator.FormatSize(size)}) ?\n" +
                "Elles seront régénérées automatiquement au besoin.",
                "Vider le cache des proxys", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var freed = ProxyGenerator.ClearCache();
        StatusLeft.Text = $"Cache des proxys vidé ({ProxyGenerator.FormatSize(freed)} libérés).";
        UpdateProxyCacheButton();
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
        if (_ffmpegPath is null && !await TryDownloadFfmpegAsync("exporter la vidéo"))
        {
            MessageBox.Show(this, "FFmpeg est indispensable pour l'export vidéo.",
                "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // L'export décode l'ORIGINAL : si on lit un proxy (1080p max), les dimensions
        // de MediaElement seraient celles du proxy → on prend celles de la sonde.
        var nativeWidth = IsPlayingProxy && _videoInfo is { Width: > 0, Height: > 0 }
            ? _videoInfo.Width : Media.NaturalVideoWidth;
        var nativeHeight = IsPlayingProxy && _videoInfo is { Width: > 0, Height: > 0 }
            ? _videoInfo.Height : Media.NaturalVideoHeight;
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

    // ── Transcription Whisper ────────────────────────────────────────────────

    private async void OnWhisper(object sender, RoutedEventArgs e)
    {
        if (_state.VideoPath is null || !File.Exists(_state.VideoPath))
        {
            MessageBox.Show(this, "Importez d'abord une vidéo avant de lancer la transcription.",
                "Transcription Whisper", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // La transcription analyse l'ORIGINAL (pas le proxy) : il faut FFmpeg pour
        // en extraire l'audio au format attendu par whisper-cli.
        if (_ffmpegPath is null && !await TryDownloadFfmpegAsync("extraire l'audio pour la transcription"))
        {
            MessageBox.Show(this, "FFmpeg est indispensable pour extraire l'audio à transcrire.",
                "Transcription Whisper", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_isPlaying) TogglePlay();
        var dialog = new Whisper.WhisperDialog(_state, _state.VideoPath, _ffmpegPath!)
        {
            Owner = this,
        };
        dialog.ShowDialog();
        if (dialog.ImportedCount > 0)
            StatusLeft.Text = $"{dialog.ImportedCount} segment(s) Whisper importé(s) dans la bande.";
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
            _mixer?.SetRate(rate);
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media is not null) ApplyAudioVolumes();
    }

    /// <summary>
    /// Applique le volume maître (curseur du transport) combiné aux gains du
    /// mixeur : la piste « Original » module MediaElement, les autres leurs players.
    /// </summary>
    private void ApplyAudioVolumes()
    {
        var original = _state.AudioTracks.FirstOrDefault(t => t.IsOriginal);
        var originalGain = original is null ? 1.0 : _state.EffectiveTrackVolume(original);
        Media.Volume = VolumeSlider.Value * originalGain;
        if (_mixer is not null) _mixer.MasterVolume = VolumeSlider.Value;
    }

    private void OnToggleMixer(object sender, RoutedEventArgs e)
    {
        var show = MixerPanel.Visibility != Visibility.Visible;
        MixerPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MixerButton.Background = show ? (Brush)FindResource("Accent") : (Brush)FindResource("BgControl");
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
        if (_state.VideoPath is { } vp)
            parts.Add(Path.GetFileName(vp) + (IsPlayingProxy ? "  (lecture via proxy)" : ""));
        parts.Add(_ffmpegPath is null ? "FFmpeg : introuvable" : "FFmpeg : OK");
        StatusRight.Text = string.Join("   •   ", parts);
    }

    /// <summary>
    /// Séparé d'UpdateStatusBar (appelé à chaque DialoguesChanged, y compris
    /// pendant les drags) : on n'énumère le dossier du cache qu'aux moments utiles.
    /// </summary>
    private void UpdateProxyCacheButton()
    {
        var cacheSize = ProxyGenerator.GetCacheSizeBytes();
        ProxyCacheButton.Visibility = cacheSize > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (cacheSize > 0)
            ProxyCacheButton.Content = $"🧹 Proxys : {ProxyGenerator.FormatSize(cacheSize)}";
    }
}
