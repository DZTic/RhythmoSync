using System.IO;
using System.Text.Json;
using RhythmoSync.Core;

namespace RhythmoSync.App;

/// <summary>
/// Persiste la liste des projets récents dans
/// %AppData%\RhythmoSync Studio\recent.json. À la lecture, les fichiers qui
/// n'existent plus sont écartés. Toute erreur d'E/S est silencieuse (best-effort).
/// </summary>
internal sealed class RecentProjectsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RhythmoSync Studio", "recent.json");

    private List<string> _paths = [];

    public IReadOnlyList<string> Paths => _paths;

    public RecentProjectsStore() => Load();

    /// <summary>Place le projet en tête de liste et enregistre.</summary>
    public void Add(string path)
    {
        _paths = RecentProjects.Add(_paths, path);
        Save();
    }

    /// <summary>Retire un projet (ex. fichier devenu introuvable) et enregistre.</summary>
    public void Remove(string path)
    {
        if (_paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
            Save();
    }

    public void Clear()
    {
        _paths = [];
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
            _paths = list.Where(File.Exists).ToList();
        }
        catch
        {
            _paths = [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_paths));
        }
        catch
        {
            // Persistance best-effort : on n'interrompt jamais l'utilisateur pour ça.
        }
    }
}
