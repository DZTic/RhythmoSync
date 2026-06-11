using System.Diagnostics;
using System.Globalization;

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
    public static async Task<WaveformData> GenerateAsync(
        string ffmpegPath, string mediaPath, int numSamples, CancellationToken ct = default)
    {
        numSamples = Math.Clamp(numSamples, 128, 65536);

        var (duration, sampleRate) = await ProbeAsync(ffmpegPath, mediaPath, ct);

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
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");       // PCM brut 16 bits LE
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("pipe:1");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer FFmpeg.");
        _ = process.StandardError.ReadToEndAsync(ct); // drainer stderr pour éviter le blocage du pipe

        var totalSamples = Math.Max(1L, (long)(duration * sampleRate));
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

        return new WaveformData(peaks, duration, sampleRate);

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
