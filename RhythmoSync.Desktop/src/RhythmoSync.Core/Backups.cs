namespace RhythmoSync.Core;

/// <summary>
/// Logique pure des sauvegardes automatiques : nommage horodaté et rotation
/// (ne conserver que les N plus récentes). L'écriture sur disque et le minuteur
/// relèvent de la couche application.
/// </summary>
public static class Backups
{
    public const int DefaultKeep = 10;
    public const int DefaultIntervalMinutes = 5;

    /// <summary>
    /// Nom de fichier de sauvegarde horodaté, ex. « monfilm-20260620-143005.rsp.bak ».
    /// L'horodatage <c>yyyyMMdd-HHmmss</c> est trié de façon chronologique en ordre lexical.
    /// </summary>
    public static string TimestampedName(string baseName, DateTime when)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "projet";
        return $"{baseName}-{when:yyyyMMdd-HHmmss}.rsp.bak";
    }

    /// <summary>
    /// À partir des sauvegardes triées de la plus récente à la plus ancienne,
    /// retourne celles à supprimer pour n'en conserver que <paramref name="keep"/>.
    /// </summary>
    public static List<T> EntriesToPrune<T>(IReadOnlyList<T> newestFirst, int keep = DefaultKeep)
    {
        if (newestFirst is null || newestFirst.Count == 0) return [];
        if (keep <= 0) return newestFirst.ToList();
        return newestFirst.Skip(keep).ToList();
    }
}
