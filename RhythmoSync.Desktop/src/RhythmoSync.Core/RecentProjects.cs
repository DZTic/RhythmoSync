namespace RhythmoSync.Core;

/// <summary>
/// Logique pure de la liste « projets récents » : insertion en tête,
/// dédoublonnage insensible à la casse (chemins Windows) et plafonnement.
/// La persistance sur disque (%AppData%) et le filtrage des fichiers disparus
/// relèvent de la couche application.
/// </summary>
public static class RecentProjects
{
    public const int DefaultMax = 10;

    /// <summary>
    /// Retourne une nouvelle liste avec <paramref name="path"/> placé en tête, les
    /// doublons (même chemin, casse ignorée) retirés, tronquée à <paramref name="max"/>
    /// entrées. Un chemin vide ou blanc laisse la liste inchangée (copie défensive).
    /// </summary>
    public static List<string> Add(IEnumerable<string> existing, string path, int max = DefaultMax)
    {
        var list = existing?.ToList() ?? [];
        if (string.IsNullOrWhiteSpace(path) || max <= 0) return list;

        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }
}
