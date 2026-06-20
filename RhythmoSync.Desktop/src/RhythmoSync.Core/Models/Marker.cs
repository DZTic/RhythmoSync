namespace RhythmoSync.Core.Models;

/// <summary>
/// Marqueur nommé sur la timeline (scène, take, point In/Out). Immuable.
/// Sérialisé dans le .rsp ; absent des anciens fichiers → aucune incidence.
/// </summary>
public sealed record Marker
{
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Position du marqueur, en secondes.</summary>
    public double Time { get; init; }

    public string Label { get; init; } = "";
}
