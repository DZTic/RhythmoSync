using System.IO;
using System.Windows.Media;
using RhythmoSync.Core;

namespace RhythmoSync.App.Audio;

/// <summary>
/// Lecture des prises de doublage enregistrées par bloc. Calqué sur
/// <see cref="AudioMixer"/> (un <see cref="MediaPlayer"/> par clip, asservi au
/// transport), mais chaque prise est placée à <c>block.StartTime</c> : le player
/// est positionné à <c>horloge − début du bloc</c> et ne joue que dans la fenêtre
/// du clip.
///
/// Virtualisation (comme le tiling de la forme d'onde) : seuls les players des
/// prises proches de la tête de lecture sont ouverts ; les autres sont fermés pour
/// libérer la mémoire. Le volume suit la piste « Voix » du mixeur.
/// </summary>
public sealed class TakeMixer : IDisposable
{
    private const double PreloadSeconds = 4;    // ouvre le player ce délai avant le bloc
    private const double TailSeconds = 2;        // garde le player ce délai après le clip
    private const double DriftThreshold = 0.12;  // recalage si l'écart dépasse ce seuil

    private sealed class TakePlayer
    {
        public required MediaPlayer Player { get; init; }
        public required string Url { get; init; }
        public double Start;
        public bool Ready;
        public double ClipDuration;  // secondes, 0 tant qu'inconnue
        public bool Active;          // en cours de lecture
    }

    private readonly ProjectState _state;
    private readonly Dictionary<string, TakePlayer> _players = [];

    private double _rate = 1.0;
    private double _masterVolume = 1.0;
    private bool _isPlaying;

    /// <summary>Une prise n'a pas pu être lue : (id du bloc, message d'erreur).</summary>
    public event Action<string, string>? TakeFailed;

    public TakeMixer(ProjectState state)
    {
        _state = state;
        _state.AudioTracksChanged += ApplyVolumes;
        _state.DialoguesChanged += OnDialoguesChanged;
    }

    /// <summary>Volume maître (curseur du transport), multiplié au gain de la piste « Voix ».</summary>
    public double MasterVolume
    {
        get => _masterVolume;
        set { _masterVolume = Math.Clamp(value, 0, 1); ApplyVolumes(); }
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    public void Play() => _isPlaying = true; // l'activation par clip se fait dans Update

    public void Pause()
    {
        _isPlaying = false;
        foreach (var tp in _players.Values.Where(p => p.Active))
        {
            tp.Player.Pause();
            tp.Active = false;
        }
    }

    public void Seek(double clock)
    {
        foreach (var tp in _players.Values.Where(p => p.Ready))
        {
            var local = clock - tp.Start;
            if (local >= 0 && (tp.ClipDuration <= 0 || local < tp.ClipDuration))
                tp.Player.Position = TimeSpan.FromSeconds(local);
        }
    }

    public void SetRate(double rate)
    {
        _rate = rate;
        foreach (var tp in _players.Values) tp.Player.SpeedRatio = rate;
    }

    /// <summary>
    /// À appeler chaque frame depuis la boucle de rendu. Ouvre/ferme les players selon
    /// la proximité de la tête de lecture, et (en lecture) démarre/positionne/arrête
    /// chaque prise sous le pointeur. Gère aussi le recalage de dérive au fil de l'eau.
    /// </summary>
    public void Update(double clock)
    {
        // 1. Players nécessaires (prises proches de la tête de lecture).
        var nearby = new HashSet<string>();
        foreach (var b in _state.Dialogues)
        {
            if (b.AudioFile is not { Length: > 0 } file) continue;
            var estEnd = b.StartTime + Math.Max(b.Duration, 0.1) + TailSeconds;
            if (clock < b.StartTime - PreloadSeconds || clock > estEnd) continue;
            if (!File.Exists(file)) continue;

            nearby.Add(b.Id);
            if (_players.TryGetValue(b.Id, out var existing))
                existing.Start = b.StartTime;
            else
                Open(b.Id, file, b.StartTime);
        }

        // 2. Ferme les players hors champ.
        foreach (var (id, tp) in _players.ToList())
        {
            if (nearby.Contains(id)) continue;
            tp.Player.Close();
            _players.Remove(id);
        }

        if (!_isPlaying) return;

        // 3. Active / positionne / arrête les prises sous la tête de lecture.
        foreach (var tp in _players.Values)
        {
            if (!tp.Ready) continue;
            var local = clock - tp.Start;
            var within = local >= 0 && (tp.ClipDuration <= 0 || local < tp.ClipDuration);
            if (within)
            {
                if (!tp.Active)
                {
                    tp.Player.Position = TimeSpan.FromSeconds(Math.Max(0, local));
                    tp.Player.Play();
                    tp.Active = true;
                }
                else if (Math.Abs(tp.Player.Position.TotalSeconds - local) > DriftThreshold)
                {
                    tp.Player.Position = TimeSpan.FromSeconds(Math.Max(0, local));
                }
            }
            else if (tp.Active)
            {
                tp.Player.Pause();
                tp.Active = false;
            }
        }
    }

    // ── Gestion des players ───────────────────────────────────────────────────

    private void Open(string id, string url, double start)
    {
        var player = new MediaPlayer { ScrubbingEnabled = true };
        var tp = new TakePlayer { Player = player, Url = url, Start = start };
        player.MediaOpened += (_, _) =>
        {
            tp.Ready = true;
            tp.ClipDuration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
            player.SpeedRatio = _rate;
            player.Volume = VoiceVolume();
        };
        player.MediaFailed += (_, e) =>
        {
            _players.Remove(id);
            player.Close();
            TakeFailed?.Invoke(id, e.ErrorException?.Message ?? "format non supporté");
        };
        _players[id] = tp;
        player.Volume = VoiceVolume();
        player.Open(new Uri(url));
    }

    /// <summary>Ferme les players dont la prise a été supprimée, déplacée ou le bloc effacé.</summary>
    private void OnDialoguesChanged()
    {
        foreach (var (id, tp) in _players.ToList())
        {
            var block = _state.Dialogues.FirstOrDefault(d => d.Id == id);
            if (block?.AudioFile is { } file && string.Equals(file, tp.Url, StringComparison.OrdinalIgnoreCase))
                tp.Start = block.StartTime;
            else
            {
                tp.Player.Close();
                _players.Remove(id);
            }
        }
    }

    /// <summary>Gain effectif de la piste « Voix » (mute/solo résolus), ou 1 si absente.</summary>
    private double VoiceVolume()
    {
        var voix = _state.AudioTracks.FirstOrDefault(t => t.Id == "voix")
                   ?? _state.AudioTracks.FirstOrDefault(t => string.Equals(t.Name, "Voix", StringComparison.OrdinalIgnoreCase));
        var effective = voix is null ? 1.0 : _state.EffectiveTrackVolume(voix);
        return _masterVolume * effective;
    }

    private void ApplyVolumes()
    {
        var v = VoiceVolume();
        foreach (var tp in _players.Values) tp.Player.Volume = v;
    }

    public void Dispose()
    {
        _state.AudioTracksChanged -= ApplyVolumes;
        _state.DialoguesChanged -= OnDialoguesChanged;
        foreach (var tp in _players.Values) tp.Player.Close();
        _players.Clear();
    }
}
