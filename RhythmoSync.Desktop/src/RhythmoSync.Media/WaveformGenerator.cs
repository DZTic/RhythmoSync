using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RhythmoSync.Media;

/// <summary>Pics min/max normalisés [-1, 1], entrelacés [min, max] par bucket.</summary>
public sealed record WaveformData(float[] Peaks, double Duration, int SampleRate);

/// <summary>
/// Génération de la forme d'onde (portage de waveform.rs). FFmpeg décode l'audio en
/// PCM s16le mono streamé sur stdout ; les échantillons sont bucketés en min/max au
/// fil de l'eau — contrairement à la version Rust, on ne charge jamais tout le PCM
/// en mémoire (une vidéo de 2 h ≈ 600 Mo de PCM).
/// </summary>
public static class WaveformGenerator
{
    /// <summary>
    /// Fréquence cible optimale pour l'analyse d'enveloppe de forme d'onde.
    /// 8 000 Hz mono capture l'intégralité du spectre de dynamique audio utile tout en
    /// divisant par 6 le volume de données transitant depuis FFmpeg et le temps CPU en C#.
    /// </summary>
    public const int TargetSampleRate = 8000;

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RhythmoSync Studio", "waveforms");

    /// <summary>
    /// Retourne le chemin du fichier cache .wavecache pour cette source et cette résolution.
    /// </summary>
    public static string GetCachePath(string mediaPath, int numSamples)
    {
        var info = new FileInfo(mediaPath);
        var key = $"{mediaPath}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}|{numSamples}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        return Path.Combine(CacheDir, $"{stem}_{hash}.wavecache");
    }

    /// <summary>
    /// Tente de charger la forme d'onde depuis le cache disque persistant.
    /// </summary>
    public static WaveformData? TryLoadFromDiskCache(string mediaPath, int numSamples)
    {
        try
        {
            var path = GetCachePath(mediaPath, numSamples);
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            var magic = reader.ReadInt32(); // 0x45564157 pour "WAVE"
            if (magic != 0x45564157) return null;

            var version = reader.ReadInt32();
            if (version != 1) return null;

            var duration = reader.ReadDouble();
            var sampleRate = reader.ReadInt32();
            var peaksCount = reader.ReadInt32();
            if (peaksCount <= 0 || peaksCount > 1_000_000) return null;

            var peaks = new float[peaksCount];
            var byteCount = peaksCount * sizeof(float);
            var span = MemoryMarshal.AsBytes(peaks.AsSpan());
            var totalRead = 0;
            while (totalRead < byteCount)
            {
                var read = stream.Read(span[totalRead..]);
                if (read == 0) return null;
                totalRead += read;
            }

            return new WaveformData(peaks, duration, sampleRate);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Enregistre les données de forme d'onde dans le cache disque persistant de manière atomique.
    /// </summary>
    public static void SaveToDiskCache(string mediaPath, int numSamples, WaveformData data)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var path = GetCachePath(mediaPath, numSamples);
            var tempPath = path + ".tmp";

            using (var stream = File.Create(tempPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x45564157); // "WAVE"
                writer.Write(1);          // version 1
                writer.Write(data.Duration);
                writer.Write(data.SampleRate);
                writer.Write(data.Peaks.Length);
                var span = MemoryMarshal.AsBytes(data.Peaks.AsSpan());
                stream.Write(span);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Ignorer silencieusement si l'écriture échoue
        }
    }

    public static async Task<WaveformData> GenerateAsync(
        string ffmpegPath, string mediaPath, int numSamples, CancellationToken ct = default)
    {
        numSamples = Math.Clamp(numSamples, 128, 65536);

        if (TryLoadFromDiskCache(mediaPath, numSamples) is { } cached)
            return cached;

        var (duration, _) = await ProbeAsync(ffmpegPath, mediaPath, ct);

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");          // mono
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(TargetSampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");       // PCM brut 16 bits LE
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("pipe:1");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer FFmpeg.");
        _ = process.StandardError.ReadToEndAsync(ct); // drainer stderr pour éviter le blocage du pipe

        var totalSamples = Math.Max(1L, (long)(duration * TargetSampleRate));
        var samplesPerBucket = Math.Max(1.0, totalSamples / (double)numSamples);

        var peaks = new float[numSamples * 2];
        for (var i = 0; i < numSamples; i++) { peaks[i * 2] = 0f; peaks[i * 2 + 1] = 0f; }

        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[1 << 16];
        long sampleIndex = 0;
        short min = short.MaxValue, max = short.MinValue;
        var bucketIndex = 0;
        var bucketHasData = false;
        var carry = -1; // octet impair restant d'une lecture précédente

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            var offset = 0;
            if (carry >= 0 && read > 0)
            {
                var sample = (short)(carry | (buffer[0] << 8));
                offset = 1;
                carry = -1;
                Accumulate(sample);
            }

            var pairs = (read - offset) / 2;
            for (var i = 0; i < pairs; i++)
            {
                var k = offset + i * 2;
                Accumulate((short)(buffer[k] | (buffer[k + 1] << 8)));
            }

            if (((read - offset) & 1) == 1) carry = buffer[read - 1];
        }

        FlushBucket();
        await process.WaitForExitAsync(ct);

        if (sampleIndex < 1)
            throw new InvalidDataException("Aucune donnée audio trouvée dans le fichier.");

        var result = new WaveformData(peaks, duration, TargetSampleRate);
        SaveToDiskCache(mediaPath, numSamples, result);
        return result;

        void Accumulate(short sample)
        {
            var targetBucket = (int)Math.Min(numSamples - 1, sampleIndex / samplesPerBucket);
            if (targetBucket != bucketIndex)
            {
                FlushBucket();
                bucketIndex = targetBucket;
            }
            if (sample < min) min = sample;
            if (sample > max) max = sample;
            bucketHasData = true;
            sampleIndex++;
        }

        void FlushBucket()
        {
            if (bucketHasData && bucketIndex < numSamples)
            {
                peaks[bucketIndex * 2] = Math.Clamp(min / (float)short.MaxValue, -1f, 1f);
                peaks[bucketIndex * 2 + 1] = Math.Clamp(max / (float)short.MaxValue, -1f, 1f);
            }
            min = short.MaxValue;
            max = short.MinValue;
            bucketHasData = false;
        }
    }

    // ── Probe durée + sample rate (parse de la sortie stderr de ffmpeg -i) ────

    private static async Task<(double Duration, int SampleRate)> ProbeAsync(
        string ffmpegPath, string mediaPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(mediaPath);
        // -t 0.05 : les infos de flux sont déjà imprimées, inutile de décoder le fichier
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("0.05");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer FFmpeg.");
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var duration = ParseDuration(stderr)
            ?? throw new InvalidDataException("Impossible de lire la durée du fichier.");
        var sampleRate = ParseSampleRate(stderr) ?? 44100;
        return (duration, sampleRate);
    }

    private static double? ParseDuration(string stderr)
    {
        const string prefix = "Duration: ";
        var line = stderr.Split('\n').FirstOrDefault(l => l.Contains(prefix));
        if (line is null) return null;
        var start = line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = line.IndexOf(',', start);
        if (end < 0) return null;
        var parts = line[start..end].Split(':');
        if (parts.Length != 3) return null;
        if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out var h)) return null;
        if (!double.TryParse(parts[1], CultureInfo.InvariantCulture, out var m)) return null;
        if (!double.TryParse(parts[2], CultureInfo.InvariantCulture, out var s)) return null;
        return h * 3600 + m * 60 + s;
    }

    private static int? ParseSampleRate(string stderr)
    {
        var line = stderr.Split('\n').FirstOrDefault(l => l.Contains("Audio:") && l.Contains(" Hz"));
        if (line is null) return null;
        var hzPos = line.IndexOf(" Hz", StringComparison.Ordinal);
        var before = line[..hzPos];
        var numStart = before.LastIndexOf(' ');
        return int.TryParse(before[(numStart + 1)..], out var rate) ? rate : null;
    }
}
