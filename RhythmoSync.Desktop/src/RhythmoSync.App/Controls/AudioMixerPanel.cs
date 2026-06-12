using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RhythmoSync.Core;
using RhythmoSync.Core.Models;

namespace RhythmoSync.App.Controls;

/// <summary>
/// Panneau « Mixeur audio » (port du renderMixer de l'ancienne EditorSidebar) :
/// une ligne par piste avec chargement de fichier (pistes externes), Mute, Solo
/// et curseur de volume. Les lignes sont construites une fois puis mises à jour
/// en place — on ne reconstruit jamais un Slider pendant son drag.
/// </summary>
public sealed class AudioMixerPanel : ScrollViewer
{
    private static readonly Brush MuteOnBg = new SolidColorBrush(Color.FromArgb(0x30, 0xEF, 0x44, 0x44));
    private static readonly Brush MuteOnFg = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush SoloOnBg = new SolidColorBrush(Color.FromArgb(0x30, 0xEA, 0xB3, 0x08));
    private static readonly Brush SoloOnFg = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));

    private sealed class TrackRow
    {
        public required FrameworkElement Root { get; init; }
        public required Button MuteButton { get; init; }
        public required Button SoloButton { get; init; }
        public required Button ClearButton { get; init; }
        public required TextBlock FileLabel { get; init; }
        public required Slider Volume { get; init; }
        public required TextBlock VolumeLabel { get; init; }
    }

    private ProjectState? _state;
    private readonly StackPanel _tracks = new();
    private readonly Dictionary<string, TrackRow> _rows = [];
    private bool _updatingUi;

    public AudioMixerPanel()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        var root = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
        root.Children.Add(new TextBlock
        {
            Text = "🎚  MIXEUR AUDIO",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Res<Brush>("TextMuted"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        root.Children.Add(_tracks);
        Content = root;
    }

    public void Initialize(ProjectState state)
    {
        _state = state;
        state.AudioTracksChanged += RefreshUi;
        RefreshUi();
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    // ── Construction / mise à jour des lignes ────────────────────────────────

    private void RefreshUi()
    {
        if (_state is null || _updatingUi) return;

        // Reconstruit tout si la liste de pistes elle-même a changé (import, reset)
        if (_rows.Count != _state.AudioTracks.Count ||
            _state.AudioTracks.Any(t => !_rows.ContainsKey(t.Id)))
        {
            _tracks.Children.Clear();
            _rows.Clear();
            foreach (var track in _state.AudioTracks)
            {
                var row = BuildRow(track);
                _rows[track.Id] = row;
                _tracks.Children.Add(row.Root);
            }
        }

        _updatingUi = true;
        try
        {
            foreach (var track in _state.AudioTracks)
            {
                var row = _rows[track.Id];

                row.MuteButton.Background = track.Muted ? MuteOnBg : Brushes.Transparent;
                row.MuteButton.Foreground = track.Muted ? MuteOnFg : Res<Brush>("TextMuted");
                row.SoloButton.Background = track.Solo ? SoloOnBg : Brushes.Transparent;
                row.SoloButton.Foreground = track.Solo ? SoloOnFg : Res<Brush>("TextMuted");

                row.FileLabel.Text = track.IsOriginal
                    ? "Audio de la vidéo"
                    : track.Url is { Length: > 0 } url && File.Exists(url)
                        ? Path.GetFileName(url)
                        : "Aucun fichier — 📂 pour en charger un";
                row.ClearButton.Visibility = !track.IsOriginal && track.Url is { Length: > 0 }
                    ? Visibility.Visible : Visibility.Collapsed;

                if (Math.Abs(row.Volume.Value - track.Volume) > 0.005)
                    row.Volume.Value = track.Volume;
                row.VolumeLabel.Text = $"{track.Volume * 100:0} %";
                row.Volume.Opacity = _state.EffectiveTrackVolume(track) > 0 ? 1.0 : 0.4;
            }
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private TrackRow BuildRow(AudioTrack track)
    {
        var id = track.Id;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        // Ligne 1 : nom + boutons (📂 ✕ M S)
        var header = new DockPanel { LastChildFill = false };
        header.Children.Add(new TextBlock
        {
            Text = track.Name,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = Res<Brush>("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);

        Button MakeSmallButton(string content, string tooltip)
        {
            var b = new Button
            {
                Content = content,
                Style = Res<Style>("ToolButton"),
                Padding = new Thickness(5, 1, 5, 1),
                FontSize = 10,
                MinWidth = 24,
                ToolTip = tooltip,
            };
            buttons.Children.Add(b);
            return b;
        }

        var fileButton = MakeSmallButton("📂", "Charger un fichier audio dans cette piste");
        var clearButton = MakeSmallButton("✕", "Retirer le fichier de cette piste");
        var muteButton = MakeSmallButton("M", "Muter / démuter la piste");
        var soloButton = MakeSmallButton("S", "Solo : n'entendre que cette piste");
        muteButton.FontWeight = FontWeights.Bold;
        soloButton.FontWeight = FontWeights.Bold;
        muteButton.BorderBrush = Brushes.Transparent;
        soloButton.BorderBrush = Brushes.Transparent;
        if (track.IsOriginal) fileButton.Visibility = Visibility.Collapsed;

        header.Children.Add(buttons);
        panel.Children.Add(header);

        // Ligne 2 : fichier chargé
        var fileLabel = new TextBlock
        {
            FontSize = 10,
            Foreground = Res<Brush>("TextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 4),
        };
        panel.Children.Add(fileLabel);

        // Ligne 3 : volume
        var volumeRow = new DockPanel();
        var volumeLabel = new TextBlock
        {
            Text = "100 %",
            FontSize = 10,
            Width = 34,
            TextAlignment = TextAlignment.Right,
            Foreground = Res<Brush>("TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(volumeLabel, Dock.Right);
        volumeRow.Children.Add(volumeLabel);
        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = track.Volume,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        volumeRow.Children.Add(volume);
        panel.Children.Add(volumeRow);

        // ── Événements → état ──
        muteButton.Click += (_, _) => _state!.UpdateAudioTrack(id, t => t with { Muted = !t.Muted });
        soloButton.Click += (_, _) => _state!.UpdateAudioTrack(id, t => t with { Solo = !t.Solo });
        volume.ValueChanged += (_, e) =>
        {
            if (!_updatingUi) _state!.UpdateAudioTrack(id, t => t with { Volume = e.NewValue });
        };
        fileButton.Click += (_, _) => PickFile(id);
        clearButton.Click += (_, _) => _state!.UpdateAudioTrack(id, t => t with { Url = null });

        return new TrackRow
        {
            Root = panel,
            MuteButton = muteButton,
            SoloButton = soloButton,
            ClearButton = clearButton,
            FileLabel = fileLabel,
            Volume = volume,
            VolumeLabel = volumeLabel,
        };
    }

    private void PickFile(string trackId)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.ogg|Tous les fichiers (*.*)|*.*",
            Title = "Charger un fichier audio",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _state!.UpdateAudioTrack(trackId, t => t with { Url = dialog.FileName });
    }
}
