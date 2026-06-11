using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhythmoSync.Media;

/// <summary>Segment transcrit par Whisper (temps en secondes).</summary>
public sealed record WhisperSegment(string Text, double StartTime, double Duration);

public sealed record WhisperTranscription(
    IReadOnlyList<WhisperSegment> Segments, string Language, double Duration);

/// <summary>Étape + message + pourcentage global (0–100) pendant la transcription.</summary>
public sealed record WhisperProgressInfo(string Stage, string Message, double Percent);

// ─────────────────────────────────────────────────────────────────────────────
// Transcription locale via whisper.cpp en sous-processus (port de diarization.rs).
// L'ancienne version Rust était restée à l'état de maquette : check/download/run
// renvoyaient des données factices. Ici le flux est réellement implémenté :
// FFmpeg extrait l'audio en WAV 16 kHz mono → whisper-cli.exe -oj produit un
// JSON de segments horodatés → parsing en WhisperSegment.
// ─────────────────────────────────────────────────────────────────────────────
public static partial class WhisperService
{
    public sealed record ModelInfo(string Name, string SizeLabel);

    /// <summary>Modèles proposés au téléchargement (mêmes choix que l'ancien WhisperPanel).</summary>
    public static readonly ModelInfo[] KnownModels =
    [
        new("tiny", "75 Mo"),
        new("base", "142 Mo"),
        new("small", "466 Mo"),
        new("medium", "1,5 Go"),
    ];

    private const string ModelUrlTemplate =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{0}.bin";

    /// <summary>
    /// En dessous de cette taille, un ggml-*.bin est forcément invalide : l'ancienne
    /// app Tauri créait des fichiers VIDES pour contourner son propre avertissement,
    /// et il peut en rester sur le disque.
    /// </summary>
    private const long MinValidModelBytes = 1_000_000;

    public static string DefaultWhisperDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RhythmoSync Studio", "whisper");

    // ── Localisation de whisper-cli.exe ──────────────────────────────────────

    /// <summary>
    /// Cherche whisper-cli.exe : dossiers whisper/ (et whisper/Release/ — layout du
    /// build CMake) à côté de l'exe puis en remontant l'arborescence (mode dev :
    /// l'exe tourne dans bin/Debug/... mais le dépôt contient whisper/Release/),
    /// puis %APPDATA%, puis le PATH.
    /// </summary>
    public static string? FindCli()
    {
        foreach (var dir in CandidateWhisperDirs())
        {
            string[] candidates =
            [
                Path.Combine(dir, "whisper-cli.exe"),
                Path.Combine(dir, "Release", "whisper-cli.exe"),
                Path.Combine(dir, "bin", "whisper-cli.exe"),
            ];
            foreach (var candidate in candidates)
                if (File.Exists(candidate))
                    return candidate;
        }
        return FindCliInPath();
    }

    private static IEnumerable<string> CandidateWhisperDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(root);
            for (var depth = 0; dir is not null && depth < 7; depth++, dir = dir.Parent)
            {
                var whisperDir = Path.Combine(dir.FullName, "whisper");
                if (seen.Add(whisperDir)) yield return whisperDir;
            }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var dir in new[]
        {
            DefaultWhisperDir,
            Path.Combine(appData, "com.rhythmosync.studio", "whisper"),
        })
            if (seen.Add(dir)) yield return dir;
    }

    private static string? FindCliInPath()
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", "whisper-cli")
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

    // ── Modèles ggml-*.bin ───────────────────────────────────────────────────

    private static IEnumerable<string> ModelSearchDirs()
    {
        foreach (var dir in CandidateWhisperDirs())
        {
            yield return dir;
            yield return Path.Combine(dir, "models");
            yield return Path.Combine(dir, "Release"); // au cas où les modèles vivent avec l'exe
        }
    }

    /// <summary>Noms des modèles réellement installés (« base », « small »…).</summary>
    public static List<string> ListInstalledModels()
    {
        var models = new List<string>();
        foreach (var dir in ModelSearchDirs())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "ggml-*.bin"))
            {
                if (new FileInfo(file).Length < MinValidModelBytes) continue;
                var name = Path.GetFileNameWithoutExtension(file)["ggml-".Length..];
                if (!models.Contains(name, StringComparer.OrdinalIgnoreCase)) models.Add(name);
            }
        }
        return models;
    }

    /// <summary>Chemin du fichier ggml-{model}.bin, ou null si non installé.</summary>
    public static string? FindModelFile(string model)
    {
        foreach (var dir in ModelSearchDirs())
        {
            var file = Path.Combine(dir, $"ggml-{model}.bin");
            if (File.Exists(file) && new FileInfo(file).Length >= MinValidModelBytes)
                return file;
        }
        return null;
    }

    /// <summary>Télécharge un modèle depuis Hugging Face vers %APPDATA%\RhythmoSync Studio\whisper\models.</summary>
    public static async Task<string> DownloadModelAsync(
        string model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(DefaultWhisperDir, "models");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"ggml-{model}.bin");
        var temp = target + ".part";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await http.GetAsync(
                string.Format(ModelUrlTemplate, model), HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(temp))
            {
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

            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>Supprime toutes les copies du modèle (y compris les stubs vides de l'ancienne app).</summary>
    public static void DeleteModel(string model)
    {
        foreach (var dir in ModelSearchDirs())
        {
            var file = Path.Combine(dir, $"ggml-{model}.bin");
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }
    }

    // ── Transcription ────────────────────────────────────────────────────────

    /// <param name="language">Code ISO (« fr », « en »…) ou « auto ».</param>
    public static async Task<WhisperTranscription> TranscribeAsync(
        string ffmpegPath, string cliPath, string modelPath, string videoPath,
        string language, IProgress<WhisperProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "rhythmosync-whisper-" + Guid.NewGuid().ToString("N"));
        var wavPath = tempBase + ".wav";
        var jsonPath = tempBase + ".json";

        try
        {
            progress?.Report(new("Extraction", "Extraction de l'audio (WAV 16 kHz mono)…", 3));
            await ExtractAudioAsync(ffmpegPath, videoPath, wavPath, ct);

            progress?.Report(new("Transcription", "Analyse de l'audio par Whisper… 0 %", 10));
            await RunWhisperCliAsync(cliPath, modelPath, wavPath, tempBase, language, progress, ct);

            if (!File.Exists(jsonPath))
                throw new InvalidOperationException("whisper-cli n'a pas produit de fichier JSON de résultat.");
            return ParseResult(await File.ReadAllTextAsync(jsonPath, ct));
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch { }
        }
    }

    /// <summary>whisper-cli ne lit que le WAV : on extrait la piste audio au format attendu (16 kHz mono PCM).</summary>
    private static async Task ExtractAudioAsync(string ffmpegPath, string videoPath, string wavPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", videoPath,
            "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
            "-f", "wav", wavPath,
        })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer FFmpeg pour extraire l'audio.");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Extraction audio échouée (code {process.ExitCode}) : {await stderr}");
    }

    private static async Task RunWhisperCliAsync(
        string cliPath, string modelPath, string wavPath, string outputBase,
        string language, IProgress<WhisperProgressInfo>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Les DLL ggml/whisper vivent à côté de l'exe
            WorkingDirectory = Path.GetDirectoryName(cliPath)!,
        };
        foreach (var arg in new[]
        {
            "-m", modelPath,
            "-f", wavPath,
            "-l", string.IsNullOrWhiteSpace(language) ? "auto" : language,
            "-oj", "-of", outputBase,
            "--print-progress",
            "-t", Math.Clamp(Environment.ProcessorCount - 1, 1, 8).ToString(),
        })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Impossible de démarrer whisper-cli.exe.");
        var stdoutDrain = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTail = new Queue<string>();

        try
        {
            // « whisper_print_progress_callback: progress =  35% » arrive sur stderr
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
            {
                stderrTail.Enqueue(line);
                if (stderrTail.Count > 25) stderrTail.Dequeue();

                var match = ProgressRegex().Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
                    progress?.Report(new("Transcription",
                        $"Analyse de l'audio par Whisper… {percent:0} %",
                        10 + Math.Clamp(percent, 0, 100) * 0.9));
            }
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"whisper-cli a échoué (code {process.ExitCode}) :\n{string.Join("\n", stderrTail)}");
    }

    /// <summary>Parse le JSON de whisper-cli (-oj) : transcription[].offsets en millisecondes.</summary>
    private static WhisperTranscription ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var language = "";
        if (root.TryGetProperty("result", out var result) &&
            result.TryGetProperty("language", out var lang))
            language = lang.GetString() ?? "";

        var segments = new List<WhisperSegment>();
        double maxEnd = 0;
        if (root.TryGetProperty("transcription", out var transcription))
        {
            foreach (var item in transcription.EnumerateArray())
            {
                var text = item.GetProperty("text").GetString()?.Trim() ?? "";
                if (text.Length == 0) continue;
                var offsets = item.GetProperty("offsets");
                var from = offsets.GetProperty("from").GetInt64() / 1000.0;
                var to = offsets.GetProperty("to").GetInt64() / 1000.0;
                if (to <= from) continue;
                segments.Add(new WhisperSegment(text, from, to - from));
                maxEnd = Math.Max(maxEnd, to);
            }
        }

        return new WhisperTranscription(segments, language, maxEnd);
    }

    [GeneratedRegex(@"progress\s*=\s*(\d+)%")]
    private static partial Regex ProgressRegex();
}
