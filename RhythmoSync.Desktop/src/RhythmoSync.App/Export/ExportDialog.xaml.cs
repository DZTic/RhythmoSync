using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RhythmoSync.Core;
using RhythmoSync.Media;

namespace RhythmoSync.App.Export;

/// <summary>
/// Dialogue d'export vidéo : options (bitrate, échelle de bande, letterbox, encodeur,
/// audio, plage), progression (%, fps effectifs, temps restant) et annulation.
/// </summary>
public partial class ExportDialog : Window
{
    private readonly ProjectState _state;
    private readonly string _videoPath;
    private readonly string _ffmpegPath;
    private readonly double _duration;
    private readonly int _nativeWidth;
    private readonly int _nativeHeight;

    private CancellationTokenSource? _cts;
    private bool _exporting;

    private static readonly (string Label, double Mbps)[] BitratePresets =
    [
        ("480p", 2.5),
        ("720p", 5.0),
        ("1080p", 8.0),
    ];

    public ExportDialog(ProjectState state, string videoPath, string ffmpegPath,
        double duration, int nativeWidth, int nativeHeight)
    {
        InitializeComponent();
        _state = state;
        _videoPath = videoPath;
        _ffmpegPath = ffmpegPath;
        _duration = duration;
        _nativeWidth = nativeWidth;
        _nativeHeight = nativeHeight;

        foreach (var (label, _) in BitratePresets) ResolutionCombo.Items.Add(label);
        ResolutionCombo.SelectedIndex = 2; // 1080p / 8 Mbps

        EncoderCombo.Items.Add("Auto (GPU si disponible)");
        EncoderCombo.Items.Add("CPU (libx264)");
        EncoderCombo.SelectedIndex = 0;

        RangeEndBox.Text = duration.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // ── Options ────────────────────────────────────────────────────────────────

    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BitrateBox is null || ResolutionCombo.SelectedIndex < 0) return;
        BitrateBox.Text = BitratePresets[ResolutionCombo.SelectedIndex].Mbps
            .ToString("0.0", CultureInfo.InvariantCulture);
    }

    private void OnBandScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BandScaleLabel is not null)
            BandScaleLabel.Text = "×" + e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private void OnRangeToggled(object sender, RoutedEventArgs e)
    {
        if (RangePanel is not null) RangePanel.IsEnabled = RangeCheck.IsChecked == true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCancelExport(object sender, RoutedEventArgs e) => _cts?.Cancel();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_exporting)
        {
            // Pas de fermeture sauvage pendant l'encodage : on demande d'abord.
            if (MessageBox.Show(this, "Un export est en cours. L'annuler ?", "Export en cours",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _cts?.Cancel();
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    // ── Lancement de l'export ─────────────────────────────────────────────────

    private async void OnStartExport(object sender, RoutedEventArgs e)
    {
        // Validation des entrées
        if (!double.TryParse(BitrateBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) || mbps <= 0 || mbps > 100)
        {
            MessageBox.Show(this, "Bitrate invalide (entre 0,1 et 100 Mbps).", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        double rangeStart = 0, rangeEnd = _duration;
        if (RangeCheck.IsChecked == true)
        {
            if (!double.TryParse(RangeStartBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out rangeStart) ||
                !double.TryParse(RangeEndBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out rangeEnd) ||
                rangeStart < 0 || rangeEnd > _duration + 0.5 || rangeEnd - rangeStart < 0.5)
            {
                MessageBox.Show(this, $"Plage invalide (entre 0 et {_duration:0.#} s, durée minimale 0,5 s).",
                    "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            rangeEnd = Math.Min(rangeEnd, _duration);
        }

        var videoName = Path.GetFileNameWithoutExtension(_videoPath);
        var saveDialog = new SaveFileDialog
        {
            Filter = "Vidéo MP4 (*.mp4)|*.mp4",
            FileName = $"rhythmosync_master_{DateTimeOffset.Now.ToUnixTimeSeconds()}.mp4",
        };
        if (saveDialog.ShowDialog(this) != true) return;

        // Capture des réglages (le travail tourne hors du thread UI)
        var includeAudio = AudioCheck.IsChecked == true;
        var forceCpu = EncoderCombo.SelectedIndex == 1;
        var detectLetterbox = LetterboxCheck.IsChecked == true;
        var bandScale = BandScaleSlider.Value;
        var resolutionLabel = (string)ResolutionCombo.SelectedItem;

        SetExportingUi(true);
        _cts = new CancellationTokenSource();

        try
        {
            // 1. Letterbox
            ProgressLeft.Text = "Détection du letterbox…";
            var (cropTop, cropBottom) = detectLetterbox
                ? await VideoExporter.DetectLetterboxAsync(_ffmpegPath, _videoPath, _nativeWidth, _nativeHeight, _duration, _cts.Token)
                : (0, _nativeHeight);

            // 2. Layout 1920×1080 (vidéo en haut, bande remplit le bas)
            var dialogues = _state.Dialogues.ToList();
            var actualLanes = dialogues.Count > 0 ? dialogues.Max(d => d.Lane) + 1 : 1;
            var bandStripHeight = actualLanes * _state.LaneHeightPx;
            var layout = ExportLayout.Compute(
                _nativeWidth, cropBottom - cropTop,
                bandStripHeight, bandScale,
                _state.ZoomLevel, RhythmoConstants.SyncLinePositionX);

            // 3. Bande en tuiles, même style que l'écran (mode propre)
            ProgressLeft.Text = "Pré-rendu de la bande rythmo…";
            var renderer = new BandStripRenderer(
                dialogues, actualLanes,
                layout.BandRenderHeight, layout.ExportPps, layout.LaneScale, _duration);

            var settings = new ExportSettings
            {
                FfmpegPath = _ffmpegPath,
                VideoPath = _videoPath,
                OutputPath = saveDialog.FileName,
                VideoWidth = _nativeWidth,
                Fps = _state.Fps,
                Bitrate = (long)(mbps * 1_000_000),
                CropTop = cropTop,
                CropBottom = cropBottom,
                ExportWidth = layout.ExportWidth,
                ExportHeight = layout.ExportHeight,
                VideoRenderHeight = layout.VideoRenderHeight,
                BandRenderHeight = layout.BandRenderHeight,
                Pps = layout.ExportPps,
                SyncOffsetEffective = _state.SyncOffset - RhythmoConstants.SyncLinePositionX / _state.ZoomLevel,
                StartTime = rangeStart,
                EndTime = rangeEnd,
                SyncLineX = layout.SyncLineX,
                IncludeAudio = includeAudio,
                ForceCpuEncoder = forceCpu,
                Title = $"{videoName} — RhythmoSync Master",
                Comment = $"Bande rythmo : {actualLanes} piste(s), {dialogues.Count} bloc(s) — {resolutionLabel} @ {_state.Fps}fps",
                Description = $"Exporté par RhythmoSync Studio le {DateTimeOffset.Now:yyyy-MM-dd HH:mm}",
            };

            // 4. Encodage (les Progress<> sont créés ici → callbacks sur le thread UI)
            ProgressLeft.Text = "Encodage en cours…";
            var progress = new Progress<ExportProgressInfo>(p =>
            {
                ExportProgressBar.Value = p.Percent;
                ProgressLeft.Text = $"Encodage… {p.Percent}%";
                ProgressRight.Text = $"{p.EffectiveFps:0.0} fps — reste {p.EstimatedRemaining}";
            });
            var encoderUsed = new Progress<EncoderInfo>(info => ProgressRight.Text = info.Label);

            var ct = _cts.Token;
            var result = await Task.Run(
                () => VideoExporter.ExportAsync(settings, renderer, progress, encoderUsed, ct), ct);

            ExportProgressBar.Value = 100;
            ProgressLeft.Text = "Terminé !";
            ProgressRight.Text = "";
            MessageBox.Show(this, result + $"\n\nFichier : {saveDialog.FileName}",
                "Export réussi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgressLeft.Text = "Export annulé.";
            ProgressRight.Text = "";
        }
        catch (Exception ex)
        {
            ProgressLeft.Text = "Échec de l'export.";
            MessageBox.Show(this, "Erreur d'exportation : " + ex.Message,
                "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetExportingUi(false);
        }
    }

    private void SetExportingUi(bool exporting)
    {
        _exporting = exporting;
        OptionsPanel.IsEnabled = !exporting;
        ProgressPanel.Visibility = exporting || ExportProgressBar.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Visibility = exporting ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = !exporting;
        if (exporting) ExportProgressBar.Value = 0;
    }
}
