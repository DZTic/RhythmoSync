using System.IO;
using RhythmoSync.Core;
using RhythmoSync.Core.Models;

namespace RhythmoSync.App;

/// <summary>Une sauvegarde automatique sur disque (chemin + date + libellé d'affichage).</summary>
internal sealed record BackupEntry(string Path, DateTime When)
{
    public string Display => When.ToString("dddd d MMMM yyyy, HH:mm:ss");
}

/// <summary>
/// Sauvegardes automatiques dans %AppData%\RhythmoSync Studio\backups\.
/// Écrit des copies horodatées du projet et applique la rotation (N versions).
/// Toutes les E/S sont best-effort : un échec n'interrompt jamais l'utilisateur.
/// </summary>
internal sealed class BackupManager
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RhythmoSync Studio", "backups");

    /// <summary>
    /// Écrit une sauvegarde horodatée du projet et purge les versions au-delà de
    /// <paramref name="keep"/>. <paramref name="baseName"/> est dérivé du nom du
    /// projet courant (sans extension). Retourne le chemin écrit, ou null si échec.
    /// </summary>
    public string? Write(ProjectFile project, string baseName, int keep = Backups.DefaultKeep)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, Backups.TimestampedName(baseName, DateTime.Now));
            ProjectIo.Save(path, project);
            Prune(keep);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sauvegardes existantes, de la plus récente à la plus ancienne.</summary>
    public IReadOnlyList<BackupEntry> List()
    {
        try
        {
            if (!Directory.Exists(Dir)) return [];
            return Directory.EnumerateFiles(Dir, "*.rsp.bak")
                .Select(p => new BackupEntry(p, File.GetLastWriteTime(p)))
                .OrderByDescending(b => b.When)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private void Prune(int keep)
    {
        foreach (var stale in Backups.EntriesToPrune(List(), keep))
        {
            try { File.Delete(stale.Path); } catch { /* best-effort */ }
        }
    }
}
