using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RhythmoSync.Media;

/// <summary>
/// Génération de vidéos proxy All-Intra (port de generate_proxy_video d'export.rs).
/// Pour les formats que MediaElement refuse (MKV, HEVC…), on encode une copie
/// H.264 All-Intra (chaque image est un keyframe → seeking instantané), plafonnée
/// à 1080p, avec l'encodeur GPU détecté (NVENC/AMF/QSV) ou libx264 en repli.
///
/// Différences voulues avec l'ancien code :
///  - le hash du cache couvre chemin + taille + date de modif (un fichier remplacé
///    au même endroit invalidait pas l'ancien proxy → vidéo périmée rejouée) ;
///  - la progression passe par « -progress pipe:1 » (l'ancien parsait time= sur
///    stderr que « -loglevel error » supprimait) ;
///  - sortie forcée en yuv420p (un HEVC 10 bits donnait sinon un proxy High10
///    que MediaElement refuse aussi).
/// </summary>
public static class ProxyGenerator
{
    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RhythmoSync Studio", "proxies");

    /// <summary>Chemin du proxy pour cette source (que le fichier existe ou non).</summary>
    public static string GetCachePath(string videoPath)
    {
        var info = new FileInfo(videoPath);
        var key = $"{videoPath}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(CacheDir, $"{stem}_{hash}_proxy.mp4");
    }

    /// <summary>Retourne le proxy en cache pour cette source, ou null.</summary>
    public static string? TryGetCached(string videoPath)
    {
        var path = GetCachePath(videoPath);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Encode le proxy (ou retourne celui du cache). <paramref name="durationHint"/>
    /// vient de la sonde et sert au calcul de progression (0 → 1).
    /// </summary>
    public static async Task<string> GenerateAsync(
        string ffmpegPath, string videoPath, double durationHint,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (TryGetCached(videoPath) is { } cached)
        {
            progress?.Report(1);
            return cached;
        }

        Directory.CreateDirectory(CacheDir);
        var proxyPath = GetCachePath(videoPath);
        var encoder = VideoExporter.DetectBestEncoder(ffmpegPath);

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        void Args(params string[] args) { foreach (var a in args) psi.ArgumentList.Add(a); }

        Args("-hide_banner", "-loglevel", "error", "-progress", "pipe:1",
            "-hwaccel", "auto",
            "-i", videoPath,
            // 1080p max sans agrandissement (le proxy est l'image de travail permanente)
            "-vf", @"scale=-2:min(1080\,ih)",
            "-c:v", encoder.Encoder);
        switch (encoder.Encoder)
        {
            case "h264_nvenc":
                Args("-preset", "p1", "-rc", "constqp", "-qp", "28");
                break;
            case "h264_amf":
                Args("-quality", "speed", "-rc", "cqp", "-qp_i", "28", "-qp_p", "28");
                break;
            case "h264_qsv":
                Args("-preset", "veryfast", "-global_quality", "28");
                break;
            default: // libx264
                Args("-preset", "ultrafast", "-crf", "28", "-sc_threshold", "0");
                break;
        }
        Args("-g", "1", "-keyint_min", "1",  // All-Intra : chaque image est un keyframe
            "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k",
            "-movflags", "+faststart",
            "-y", proxyPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer FFmpeg.");
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            // Flux -progress : lignes « clé=valeur », out_time_us = position encodée en µs
            using var reader = process.StandardOutput;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (durationHint <= 0) continue;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var us))
                {
                    progress?.Report(Math.Clamp(us / 1e6 / durationHint, 0, 0.99));
                }
            }
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { if (File.Exists(proxyPath)) File.Delete(proxyPath); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            try { if (File.Exists(proxyPath)) File.Delete(proxyPath); } catch { }
            throw new InvalidOperationException("L'encodage du proxy a échoué : " + await stderrTask);
        }

        progress?.Report(1);
        return proxyPath;
    }

    // ── Gestion du cache ──────────────────────────────────────────────────────

    public static long GetCacheSizeBytes()
    {
        if (!Directory.Exists(CacheDir)) return 0;
        return new DirectoryInfo(CacheDir).EnumerateFiles("*.mp4").Sum(f => f.Length);
    }

    /// <summary>Supprime tous les proxys. Retourne le nombre d'octets libérés.</summary>
    public static long ClearCache()
    {
        if (!Directory.Exists(CacheDir)) return 0;
        long freed = 0;
        foreach (var file in new DirectoryInfo(CacheDir).EnumerateFiles("*.mp4"))
        {
            try { var len = file.Length; file.Delete(); freed += len; }
            catch { /* proxy en cours de lecture : ignoré */ }
        }
        return freed;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} Mo",
        _ => $"{bytes / 1024.0:0} Ko",
    };
}
