namespace RhythmoSync.Core.Models;

/// <summary>
/// Piste du mixeur audio. Immuable, comme <see cref="DialogueBlock"/>.
/// Le format JSON camelCase est compatible avec l'ancienne version web :
/// { id, name, url, volume, muted, solo, isOriginal }.
/// </summary>
public sealed record AudioTrack
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; init; } = "Piste";

    /// <summary>
    /// Chemin du fichier audio sur disque. L'ancienne app web y stockait une URL
    /// (souvent un blob: éphémère) : un chemin inexistant est simplement ignoré
    /// à la lecture, seuls les réglages de la piste sont restaurés.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>Gain de la piste, de 0.0 à 1.0.</summary>
    public double Volume { get; init; } = 1.0;

    public bool Muted { get; init; }
    public bool Solo { get; init; }

    /// <summary>Piste liée à l'audio de la vidéo (pas de fichier propre).</summary>
    public bool IsOriginal { get; init; }
}
