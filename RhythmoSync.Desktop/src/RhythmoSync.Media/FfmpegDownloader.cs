using System.IO.Compression;
using System.Net.Http;

namespace RhythmoSync.Media;

/// <summary>
/// Téléchargement automatique de FFmpeg (port de download_ffmpeg d'export.rs).
/// Récupère le build "essentials" de gyan.dev et extrait ffmpeg.exe vers
/// %APPDATA%\RhythmoSync Studio\ffmpeg\ffmpeg.exe.
/// </summary>
public static class FfmpegDownloader
{
    // Deux sources : gyan.dev (comme l'ancienne app) puis GitHub en secours —
    // sur certaines machines la chaîne de certificats de gyan.dev n'est pas approuvée.
    private static readonly string[] DownloadUrls =
    [
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
    ];

    public static string TargetPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RhythmoSync Studio", "ffmpeg", "ffmpeg.exe");

    /// <param name="progress">Progression du téléchargement, 0 → 1 (puis 1 pendant l'extraction).</param>
    public static async Task<string> DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Exception? lastError = null;
        foreach (var url in DownloadUrls)
        {
            try
            {
                return await DownloadFromAsync(url, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw new InvalidOperationException(
            "Téléchargement de FFmpeg impossible depuis toutes les sources : " + lastError?.Message, lastError);
    }

    private static async Task<string> DownloadFromAsync(string url, IProgress<double>? progress, CancellationToken ct)
    {
        var targetPath = TargetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var tempZip = Path.Combine(Path.GetTempPath(), $"ffmpeg-download-{Guid.NewGuid():N}.zip");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(tempZip);
                var buffer = new byte[1 << 16];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                    if (total > 0) progress?.Report((double)written / total);
                }
            }

            using var archive = ZipFile.OpenRead(tempZip);
            var entry = archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("ffmpeg.exe introuvable dans l'archive téléchargée.");
            entry.ExtractToFile(targetPath, overwrite: true);
            return targetPath;
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
