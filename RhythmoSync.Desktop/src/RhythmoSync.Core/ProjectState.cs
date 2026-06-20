using RhythmoSync.Core.Models;

namespace RhythmoSync.Core;

/// <summary>
/// État central du projet (portage du store Zustand). La liste de dialogues est
/// traitée comme immuable : chaque mutation remplace la liste entière, et
/// l'historique undo/redo ne stocke que des références de listes (50 niveaux max).
/// </summary>
public sealed class ProjectState
{
    public const int MaxHistory = 50;

    private List<DialogueBlock> _dialogues = [];
    private readonly List<List<DialogueBlock>> _past = [];
    private readonly List<List<DialogueBlock>> _future = [];
    private readonly List<string> _selected = [];

    public IReadOnlyList<DialogueBlock> Dialogues => _dialogues;
    public IReadOnlyList<string> SelectedIds => _selected;
    public List<DialogueBlock> Clipboard { get; } = [];

    /// <summary>Déclenché quand la liste de dialogues change (ajout, édition, undo…).</summary>
    public event Action? DialoguesChanged;
    public event Action? SelectionChanged;
    /// <summary>Déclenché quand un paramètre de vue change (zoom, pistes, snap…).</summary>
    public event Action? ViewChanged;
    /// <summary>Déclenché quand une piste du mixeur audio change (volume, mute, solo, fichier…).</summary>
    public event Action? AudioTracksChanged;

    // ── Mixeur audio ─────────────────────────────────────────────────────────

    private List<AudioTrack> _audioTracks = DefaultAudioTracks();
    public IReadOnlyList<AudioTrack> AudioTracks => _audioTracks;

    /// <summary>Les trois pistes historiques de la version web : Original, Voix, Bruitages.</summary>
    public static List<AudioTrack> DefaultAudioTracks() =>
    [
        new() { Id = "original", Name = "Original", IsOriginal = true },
        new() { Id = "voix", Name = "Voix" },
        new() { Id = "bruitages", Name = "Bruitages" },
    ];

    public void UpdateAudioTrack(string id, Func<AudioTrack, AudioTrack> update)
    {
        _audioTracks = _audioTracks.Select(t => t.Id == id ? update(t) : t).ToList();
        AudioTracksChanged?.Invoke();
    }

    /// <summary>Gain effectif d'une piste, mute et solo résolus (0 si muette ou écrasée par un solo).</summary>
    public double EffectiveTrackVolume(AudioTrack track)
    {
        var anySolo = _audioTracks.Any(t => t.Solo);
        return track.Muted || (anySolo && !track.Solo) ? 0 : track.Volume;
    }

    // ── Paramètres de vue / réglages ─────────────────────────────────────────

    private double _zoomLevel = RhythmoConstants.DefaultPps;
    public double ZoomLevel
    {
        get => _zoomLevel;
        set { var v = Math.Clamp(value, 20, 1200); if (v != _zoomLevel) { _zoomLevel = v; ViewChanged?.Invoke(); } }
    }

    private double _syncOffset;
    public double SyncOffset
    {
        get => _syncOffset;
        set { if (value != _syncOffset) { _syncOffset = value; ViewChanged?.Invoke(); } }
    }

    private int _totalLanes = 3;
    public int TotalLanes
    {
        get => _totalLanes;
        set { var v = Math.Clamp(value, 1, 12); if (v != _totalLanes) { _totalLanes = v; ViewChanged?.Invoke(); } }
    }

    private double _laneHeight = RhythmoConstants.LaneHeight;
    public double LaneHeightPx
    {
        get => _laneHeight;
        set { var v = Math.Clamp(value, 40, 200); if (v != _laneHeight) { _laneHeight = v; ViewChanged?.Invoke(); } }
    }

    private bool _snapEnabled = true;
    public bool SnapEnabled
    {
        get => _snapEnabled;
        set { if (value != _snapEnabled) { _snapEnabled = value; ViewChanged?.Invoke(); } }
    }

    public double Fps { get; set; } = 25;
    public double DefaultBlockDuration { get; set; } = 2.0;
    public string? VideoPath { get; set; }

    public double TotalBandHeight => _totalLanes * _laneHeight;

    public bool CanUndo => _past.Count > 0;
    public bool CanRedo => _future.Count > 0;

    // ── Historique ───────────────────────────────────────────────────────────

    /// <summary>Pousse l'état courant dans l'historique (appelé AVANT une mutation interactive, ex. début de drag).</summary>
    public void SnapshotHistory()
    {
        if (_past.Count > 0 && ReferenceEquals(_past[^1], _dialogues)) return;
        _past.Add(_dialogues);
        if (_past.Count > MaxHistory) _past.RemoveAt(0);
        _future.Clear();
    }

    public void Undo()
    {
        if (_past.Count == 0) return;
        _future.Insert(0, _dialogues);
        _dialogues = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        PruneSelection();
        DialoguesChanged?.Invoke();
    }

    public void Redo()
    {
        if (_future.Count == 0) return;
        _past.Add(_dialogues);
        _dialogues = _future[0];
        _future.RemoveAt(0);
        PruneSelection();
        DialoguesChanged?.Invoke();
    }

    private void Commit(List<DialogueBlock> newList, bool snapshot)
    {
        if (snapshot) SnapshotHistory();
        _dialogues = newList;
        DialoguesChanged?.Invoke();
    }

    private void PruneSelection()
    {
        var valid = _dialogues.Select(d => d.Id).ToHashSet();
        if (_selected.RemoveAll(id => !valid.Contains(id)) > 0) SelectionChanged?.Invoke();
    }

    // ── Mutations de dialogues ───────────────────────────────────────────────

    public void AddDialogue(DialogueBlock block) => Commit([.. _dialogues, block], snapshot: true);

    public void AddDialogues(IEnumerable<DialogueBlock> blocks) => Commit([.. _dialogues, .. blocks], snapshot: true);

    public void SetDialogues(List<DialogueBlock> blocks) => Commit(blocks, snapshot: true);

    /// <summary>
    /// Applique un changement à un bloc. <paramref name="skipHistory"/> permet les mises à
    /// jour continues (drag, redimensionnement) sans polluer l'historique — le snapshot
    /// est alors pris une seule fois au début du geste.
    /// </summary>
    public void UpdateDialogue(string id, Func<DialogueBlock, DialogueBlock> update, bool skipHistory = false)
    {
        var list = new List<DialogueBlock>(_dialogues.Count);
        foreach (var d in _dialogues)
            list.Add(d.Id == id ? AutoStretch(d, update(d)) : d);
        Commit(list, snapshot: !skipHistory);
    }

    public void UpdateDialogues(IReadOnlyDictionary<string, Func<DialogueBlock, DialogueBlock>> updates, bool skipHistory = false)
    {
        var list = new List<DialogueBlock>(_dialogues.Count);
        foreach (var d in _dialogues)
            list.Add(updates.TryGetValue(d.Id, out var update) ? AutoStretch(d, update(d)) : d);
        Commit(list, snapshot: !skipHistory);
    }

    /// <summary>Si le texte a changé et rend le bloc trop rapide, allonge automatiquement la durée.</summary>
    private static DialogueBlock AutoStretch(DialogueBlock before, DialogueBlock after)
    {
        if (after.Text == before.Text || after.Duration != before.Duration) return after;
        var minDuration = after.Text.Length / RhythmoConstants.MaxCharsPerSecond;
        return after.Duration < minDuration ? after with { Duration = minDuration + 0.5 } : after;
    }

    public void DeleteDialogues(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        var set = ids.ToHashSet();
        Commit(_dialogues.Where(d => !set.Contains(d.Id)).ToList(), snapshot: true);
        if (_selected.RemoveAll(set.Contains) > 0) SelectionChanged?.Invoke();
    }

    /// <summary>Vide tous les dialogues en un seul pas d'historique (annulable). No-op si déjà vide.</summary>
    public void ClearAllDialogues()
    {
        if (_dialogues.Count == 0) return;
        Commit([], snapshot: true);
        if (_selected.Count > 0) { _selected.Clear(); SelectionChanged?.Invoke(); }
    }

    public void GroupSelected()
    {
        if (_selected.Count < 2) return;
        var groupId = Guid.NewGuid().ToString();
        var set = _selected.ToHashSet();
        Commit(_dialogues.Select(d => set.Contains(d.Id) ? d with { GroupId = groupId } : d).ToList(), snapshot: true);
    }

    public void UngroupSelected()
    {
        if (_selected.Count == 0) return;
        var set = _selected.ToHashSet();
        Commit(_dialogues.Select(d => set.Contains(d.Id) ? d with { GroupId = null } : d).ToList(), snapshot: true);
    }

    // ── Sélection ────────────────────────────────────────────────────────────

    public void SelectBlock(string? id, bool multi = false)
    {
        if (id is null)
        {
            if (_selected.Count == 0) return;
            _selected.Clear();
            SelectionChanged?.Invoke();
            return;
        }

        // Sélection automatique du groupe entier
        var target = _dialogues.FirstOrDefault(d => d.Id == id);
        var related = target?.GroupId is { } gid
            ? _dialogues.Where(d => d.GroupId == gid).Select(d => d.Id).ToList()
            : [id];

        if (multi)
        {
            if (_selected.Contains(id))
                _selected.RemoveAll(related.Contains);
            else
                foreach (var r in related.Where(r => !_selected.Contains(r))) _selected.Add(r);
        }
        else
        {
            _selected.Clear();
            _selected.AddRange(related);
        }
        SelectionChanged?.Invoke();
    }

    public bool IsSelected(string id) => _selected.Contains(id);

    // ── Outils ───────────────────────────────────────────────────────────────

    public void ShiftTimeline(double offsetSeconds) =>
        Commit(_dialogues.Select(d => d with { StartTime = Math.Max(0, d.StartTime + offsetSeconds) }).ToList(), snapshot: true);

    /// <summary>
    /// Remplace <paramref name="find"/> par <paramref name="replace"/> (insensible à la
    /// casse) dans le texte et le nom de personnage de tous les blocs. Ne touche
    /// l'historique que s'il y a au moins une occurrence.
    /// </summary>
    /// <returns>Nombre d'occurrences remplacées.</returns>
    public int GlobalFindReplace(string find, string replace)
    {
        if (string.IsNullOrEmpty(find)) return 0;
        var count = 0;
        var newList = _dialogues.Select(d =>
        {
            count += CountOccurrences(d.Text, find) + CountOccurrences(d.CharacterName, find);
            return d with
            {
                Text = ReplaceIgnoreCase(d.Text, find, replace),
                CharacterName = ReplaceIgnoreCase(d.CharacterName, find, replace),
            };
        }).ToList();
        if (count > 0) Commit(newList, snapshot: true);
        return count;
    }

    private static string ReplaceIgnoreCase(string source, string find, string replace) =>
        source.Replace(find, replace, StringComparison.OrdinalIgnoreCase);

    private static int CountOccurrences(string source, string find)
    {
        if (string.IsNullOrEmpty(source)) return 0;
        int count = 0, index = 0;
        while ((index = source.IndexOf(find, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += find.Length;
        }
        return count;
    }

    // ── Import / export / reset ──────────────────────────────────────────────

    public ProjectFile ToProjectFile() => new()
    {
        Dialogues = [.. _dialogues],
        TotalLanes = _totalLanes,
        SyncOffset = _syncOffset,
        ZoomLevel = _zoomLevel,
        Fps = Fps,
        VideoPath = VideoPath,
        AudioTracks = [.. _audioTracks],
    };

    public void ImportProject(ProjectFile file)
    {
        _dialogues = file.Dialogues ?? [];
        _totalLanes = file.TotalLanes > 0 ? Math.Clamp(file.TotalLanes, 1, 12) : 3;
        _syncOffset = file.SyncOffset;
        _zoomLevel = file.ZoomLevel > 0 ? Math.Clamp(file.ZoomLevel, 20, 1200) : RhythmoConstants.DefaultPps;
        Fps = file.Fps > 0 ? file.Fps : 25;
        VideoPath = file.VideoPath;
        _audioTracks = file.AudioTracks is { Count: > 0 } tracks
            ? tracks.Select(t => t with { Volume = Math.Clamp(t.Volume, 0, 1) }).ToList()
            : DefaultAudioTracks();
        _past.Clear();
        _future.Clear();
        _selected.Clear();
        DialoguesChanged?.Invoke();
        SelectionChanged?.Invoke();
        ViewChanged?.Invoke();
        AudioTracksChanged?.Invoke();
    }

    public void ResetProject()
    {
        _dialogues = [];
        _totalLanes = 3;
        _syncOffset = 0;
        Fps = 25;
        VideoPath = null;
        _audioTracks = DefaultAudioTracks();
        _past.Clear();
        _future.Clear();
        _selected.Clear();
        DialoguesChanged?.Invoke();
        SelectionChanged?.Invoke();
        ViewChanged?.Invoke();
        AudioTracksChanged?.Invoke();
    }
}
