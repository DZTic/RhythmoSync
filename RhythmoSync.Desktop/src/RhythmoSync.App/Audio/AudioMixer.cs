using System.IO;
using System.Windows.Media;
using RhythmoSync.Core;
using RhythmoSync.Core.Models;

namespace RhythmoSync.App.Audio;

/// <summary>
/// Lecture des pistes externes du mixeur : un <see cref="MediaPlayer"/> WPF par
/// piste chargée, asservi au transport de la vidéo (port du syncExternalAudio de
/// l'ancienne version web : les éléments &lt;audio&gt; suivaient la &lt;video&gt;).
/// Les players n'ont pas d'horloge commune avec MediaElement : on les recale au
/// play/seek, puis périodiquement quand la dérive dépasse le seuil.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    private const double DriftThresholdSeconds = 0.15;

    private sealed class TrackPlayer
    {
        public required MediaPlayer Player { get; init; }
        public required string Url { get; init; }
        public bool Ready;
    }

    private readonly ProjectState _state;
    private readonly Func<double> _clock;
    private readonly Dictionary<string, TrackPlayer> _players = [];

    private bool _isPlaying;
    private double _rate = 1.0;
    private double _masterVolume = 1.0;

    /// <summary>Une piste n'a pas pu être lue : (nom de la piste, message d'erreur).</summary>
    public event Action<string, string>? TrackFailed;

    public AudioMixer(ProjectState state, Func<double> clock)
    {
        _state = state;
        _clock = clock;
        _state.AudioTracksChanged += SyncTracks;
        SyncTracks();
    }

    /// <summary>Volume maître (le curseur du transport), multiplié au gain de chaque piste.</summary>
    public double MasterVolume
    {
        get => _masterVolume;
        set { _masterVolume = Math.Clamp(value, 0, 1); ApplyVolumes(); }
    }

    // ── Transport (appelé par MainWindow en miroir de MediaElement) ──────────

    public void Play()
    {
        _isPlaying = true;
        var time = TimeSpan.FromSeconds(_clock());
        foreach (var tp in _players.Values.Where(p => p.Ready))
        {
            tp.Player.Position = time;
            tp.Player.Play();
        }
    }

    public void Pause()
    {
        _isPlaying = false;
        foreach (var tp in _players.Values.Where(p => p.Ready))
            tp.Player.Pause();
    }

    public void Seek(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        foreach (var tp in _players.Values.Where(p => p.Ready))
            tp.Player.Position = time;
    }

    public void SetRate(double rate)
    {
        _rate = rate;
        foreach (var tp in _players.Values.Where(p => p.Ready))
            tp.Player.SpeedRatio = rate;
    }

    /// <summary>
    /// Recale les pistes qui ont dérivé de la vidéo (appelé périodiquement
    /// pendant la lecture, pas à chaque frame : lire Position a un coût).
    /// </summary>
    public void UpdateDrift()
    {
        if (!_isPlaying) return;
        var clock = _clock();
        foreach (var tp in _players.Values.Where(p => p.Ready))
        {
            if (Math.Abs(tp.Player.Position.TotalSeconds - clock) > DriftThresholdSeconds)
                tp.Player.Position = TimeSpan.FromSeconds(clock);
        }
    }

    // ── Suivi de l'état des pistes ────────────────────────────────────────────

    /// <summary>Ouvre/ferme les players pour refléter les fichiers des pistes.</summary>
    private void SyncTracks()
    {
        // Ferme les players des pistes supprimées ou dont le fichier a changé
        foreach (var (id, tp) in _players.ToList())
        {
            var track = _state.AudioTracks.FirstOrDefault(t => t.Id == id);
            if (track is null || track.IsOriginal || !string.Equals(track.Url, tp.Url, StringComparison.OrdinalIgnoreCase))
            {
                tp.Player.Close();
                _players.Remove(id);
            }
        }

        // Ouvre les nouvelles pistes
        foreach (var track in _state.AudioTracks)
        {
            if (track.IsOriginal || track.Url is not { Length: > 0 } url || _players.ContainsKey(track.Id))
                continue;
            // URL blob: d'un vieux projet web ou fichier déplacé : réglages
            // conservés, lecture ignorée.
            if (!File.Exists(url)) continue;

            var trackId = track.Id;
            var trackName = track.Name;
            var player = new MediaPlayer { ScrubbingEnabled = true };
            var tp = new TrackPlayer { Player = player, Url = url };
            player.MediaOpened += (_, _) =>
            {
                tp.Ready = true;
                player.SpeedRatio = _rate;
                ApplyVolumes();
                player.Position = TimeSpan.FromSeconds(_clock());
                if (_isPlaying) player.Play();
            };
            player.MediaFailed += (_, e) =>
            {
                _players.Remove(trackId);
                player.Close();
                TrackFailed?.Invoke(trackName, e.ErrorException?.Message ?? "format non supporté");
            };
            _players[trackId] = tp;
            player.Open(new Uri(url));
        }

        ApplyVolumes();
    }

    private void ApplyVolumes()
    {
        foreach (var track in _state.AudioTracks)
            if (_players.TryGetValue(track.Id, out var tp))
                tp.Player.Volume = _masterVolume * _state.EffectiveTrackVolume(track);
    }

    public void Dispose()
    {
        _state.AudioTracksChanged -= SyncTracks;
        foreach (var tp in _players.Values) tp.Player.Close();
        _players.Clear();
    }
}
