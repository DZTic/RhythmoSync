using System.Diagnostics;

namespace RhythmoSync.Media;

/// <summary>
/// Localise ffmpeg.exe sur la machine. Cherche dans l'ordre :
/// 1. à côté de l'exécutable (dossier ffmpeg/ ou racine),
/// 2. le dossier de données de l'ancienne app Tauri (%APPDATA%\com.rhythmosync.studio\ffmpeg),
/// 3. le PATH système.
/// </summary>
public static class FfmpegLocator
{
    public static string? Find()
    {
        var exeDir = AppContext.BaseDirectory;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        string[] candidates =
        [
            Path.Combine(exeDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(exeDir, "ffmpeg.exe"),
            Path.Combine(appData, "com.rhythmosync.studio", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(appData, "RhythmoSync Studio", "ffmpeg", "ffmpeg.exe"),
        ];

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        return FindInPath();
    }

    private static string? FindInPath()
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", "ffmpeg")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return first is not null && File.Exists(first) ? first : null;
        }
        catch
        {
            return null;
        }
    }
}
