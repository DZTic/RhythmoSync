using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RhythmoSync.Media;

/// <summary>Résultat de la sonde vidéo (port de get_video_info d'export.rs).</summary>
public sealed record VideoProbeResult
{
    public string CodecName { get; init; } = "unknown";
    public string Container { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public double Duration { get; init; }
    public double Fps { get; init; }

    /// <summary>true si le format est connu comme illisible par MediaElement → proxy direct.</summary>
    public bool NeedsProxy { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>
/// Sonde le conteneur/codec d'une vidéo via la sortie stderr de « ffmpeg -i ».
/// La liste de compatibilité vise MediaElement (Media Foundation), pas WebView2 :
/// MP4/H.264, WMV, AVI… passent ; MKV, WebM, HEVC, AV1… partent en proxy.
/// Les cas inconnus tentent la lecture native (repli sur MediaFailed).
/// </summary>
public static partial class VideoProber
{
    // Conteneurs que Media Foundation refuse quel que soit le codec
    private static readonly string[] BadContainers =
        ["mkv", "webm", "flv", "ogv", "ogg", "ts", "m2ts", "mts", "vob"];

    // Codecs sans décodeur Media Foundation par défaut (HEVC/AV1 exigent une
    // extension du Store rarement installée — on les traite comme illisibles)
    private static readonly string[] BadCodecs =
        ["hevc", "h265", "vp8", "vp9", "av1", "prores", "dnxhd", "mpeg2video", "theora"];

    public static async Task<VideoProbeResult> ProbeAsync(
        string ffmpegPath, string videoPath, CancellationToken ct = default)
    {
        var container = Path.GetExtension(videoPath).TrimStart('.').ToLowerInvariant();

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        // -t 0.05 : les infos de flux sont déjà imprimées, inutile de décoder le fichier
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("0.05");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer FFmpeg.");
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var videoLine = stderr.Split('\n').FirstOrDefault(l => l.Contains("Video:")) ?? "";
        var codecMatch = CodecRegex().Match(videoLine);
        var sizeMatch = SizeRegex().Match(videoLine);
        var fpsMatch = FpsRegex().Match(videoLine);

        var codec = codecMatch.Success ? codecMatch.Groups[1].Value.ToLowerInvariant() : "unknown";
        var (needsProxy, reason) = Evaluate(container, codec);

        return new VideoProbeResult
        {
            CodecName = codec,
            Container = container,
            Width = sizeMatch.Success ? int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
            Height = sizeMatch.Success ? int.Parse(sizeMatch.Groups[2].Value, CultureInfo.InvariantCulture) : 0,
            Duration = ParseDuration(stderr) ?? 0,
            Fps = fpsMatch.Success ? double.Parse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
            NeedsProxy = needsProxy,
            Reason = reason,
        };
    }

    private static (bool NeedsProxy, string Reason) Evaluate(string container, string codec)
    {
        if (BadContainers.Contains(container))
            return (true, $"Conteneur .{container} non lu par le décodeur Windows — proxy requis");
        if (BadCodecs.Contains(codec))
            return (true, $"Codec {codec.ToUpperInvariant()} non lu par le décodeur Windows — proxy requis");
        return (false, "");
    }

    private static double? ParseDuration(string stderr)
    {
        var match = DurationRegex().Match(stderr);
        if (!match.Success) return null;
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
             + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
             + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    // « Video: hevc (Main) (hev1 / 0x31766568), yuv420p(tv), 1920x1080 […], 23.98 fps »
    [GeneratedRegex(@"Video:\s*(\w+)")]
    private static partial Regex CodecRegex();

    [GeneratedRegex(@"\b(\d{2,5})x(\d{2,5})\b")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"([\d.]+)\s*fps")]
    private static partial Regex FpsRegex();

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):([\d.]+)")]
    private static partial Regex DurationRegex();
}
