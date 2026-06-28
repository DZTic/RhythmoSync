namespace RhythmoSync.Core.Models;

/// <summary>
/// Bloc de dialogue de la bande rythmo. Immuable : toute modification crée une
/// nouvelle instance (via <c>with</c>), ce qui rend l'historique undo/redo trivial
/// (l'historique ne stocke que des références de listes).
/// </summary>
public sealed record DialogueBlock
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Text { get; init; } = "";

    /// <summary>Temps de début, en secondes.</summary>
    public double StartTime { get; init; }

    /// <summary>Durée, en secondes.</summary>
    public double Duration { get; init; }

    public string CharacterName { get; init; } = "";

    /// <summary>Couleur hexadécimale "#rrggbb".</summary>
    public string Color { get; init; } = "#6366f1";

    /// <summary>Index de piste (0-indexé).</summary>
    public int Lane { get; init; }

    /// <summary>Identifiant de groupe : les blocs d'un même groupe se sélectionnent ensemble.</summary>
    public string? GroupId { get; init; }

    /// <summary>
    /// Bloc verrouillé : ne peut être ni déplacé ni redimensionné (anti-déplacement
    /// accidentel en session). Reste sélectionnable et éditable via le panneau.
    /// </summary>
    public bool IsLocked { get; init; }

    /// <summary>
    /// Chemin absolu du WAV enregistré pour ce bloc (doublage). Null = aucune prise.
    /// Même convention que <see cref="AudioTrack.Url"/> : chemin ignoré s'il n'existe
    /// plus sur disque. Omis du JSON quand null (rétro-compatible).
    /// </summary>
    public string? AudioFile { get; init; }

    public double EndTime => StartTime + Duration;

    /// <summary>Vrai si le texte dépasse la vitesse de lecture recommandée (20 car/s).</summary>
    public bool IsTooFast => Duration > 0 && Text.Length / Duration > RhythmoConstants.MaxCharsPerSecond;
}
