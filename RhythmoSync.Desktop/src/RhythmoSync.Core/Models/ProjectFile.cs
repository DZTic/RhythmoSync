using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhythmoSync.Core.Models;

/// <summary>
/// Représentation sur disque d'un projet (.rsp). Le format JSON camelCase est
/// strictement compatible avec les fichiers produits par l'ancienne version web :
/// { version, timestamp, dialogues, totalLanes, syncOffset, zoomLevel, fps, videoPath }.
/// </summary>
public sealed class ProjectFile
{
    public string Version { get; set; } = "1.0";
    public long Timestamp { get; set; }
    public List<DialogueBlock> Dialogues { get; set; } = [];
    public int TotalLanes { get; set; } = 3;
    public double SyncOffset { get; set; }
    public double ZoomLevel { get; set; } = RhythmoConstants.DefaultPps;
    public double Fps { get; set; } = 25;
    public string? VideoPath { get; set; }
}

public static class ProjectIo
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ProjectFile Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<ProjectFile>(json, Options)
            ?? throw new InvalidDataException("Fichier de projet vide ou illisible.");
        if (string.IsNullOrEmpty(project.Version))
            throw new InvalidDataException("Fichier de projet invalide (champ 'version' manquant).");
        return project;
    }

    public static void Save(string path, ProjectFile project)
    {
        project.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(path, JsonSerializer.Serialize(project, Options));
    }
}
