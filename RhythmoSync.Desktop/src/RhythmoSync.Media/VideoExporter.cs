using System.Diagnostics;
using System.Globalization;

namespace RhythmoSync.Media;

// ─────────────────────────────────────────────────────────────────────────────
// Export vidéo avec incrustation de la bande rythmo (port d'export.rs).
// Pipeline : FFmpeg décodeur → frames BGRA brutes → composition en C#
// (copies de lignes entières, pas de boucle par pixel) → FFmpeg encodeur
// (GPU détecté : NVENC / AMF / QuickSync, sinon libx264).
// La bande est fournie en tuiles paresseuses (IBandStripSource), ce qui
// corrige le plafond de 32 000 px de l'ancien strip PNG (bande tronquée
// après quelques minutes de vidéo).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Fournit la bande rythmo pré-rendue, par tuiles horizontales (BGRA).</summary>
public interface IBandStripSource
{
    /// <summary>Largeur totale de la bande en pixels (durée × pps).</summary>
    int TotalWidthPx { get; }

    /// <summary>Hauteur des tuiles — doit valoir exactement BandRenderHeight.</summary>
    int HeightPx { get; }

    int TileWidthPx { get; }

    /// <summary>Pixels BGRA de la tuile (TileWidthPx × HeightPx × 4), opaque.</summary>
    byte[] GetTile(int tileIndex);
}

/// <summary>Piste externe du mixeur à inclure dans l'export : fichier + gain effectif (0–1).</summary>
public sealed record ExternalAudioTrack(string Path, double Gain);

/// <summary>
/// Prise de doublage à mixer dans l'export : fichier WAV + début ABSOLU sur la
/// timeline vidéo (le début du bloc, en secondes) + gain effectif (0–1).
/// </summary>
public sealed record TakeAudioClip(string Path, double StartTime, double Gain);

public sealed record ExportSettings
{
    public required string FfmpegPath { get; init; }
    public required string VideoPath { get; init; }
    public required string OutputPath { get; init; }
    public required int VideoWidth { get; init; }   // largeur native de la source
    public double Fps { get; init; } = 25;
    public long Bitrate { get; init; } = 8_000_000;
    public int CropTop { get; init; }
    public int CropBottom { get; init; }            // exclusif (= hauteur native si pas de crop)
    public int ExportWidth { get; init; } = 1920;
    public int ExportHeight { get; init; } = 1080;
    public int VideoRenderHeight { get; init; }
    public int BandRenderHeight { get; init; }
    public double Pps { get; init; }
    /// <summary>Décalage replié : syncOffset − syncLineX/zoomUI (même convention que l'ancien export).</summary>
    public double SyncOffsetEffective { get; init; }
    public double StartTime { get; init; }
    public required double EndTime { get; init; }
    public int SyncLineX { get; init; }
    public bool IncludeAudio { get; init; } = true;

    /// <summary>
    /// Gain effectif (0–1) de la piste « Original » du mixeur, appliqué à l'audio
    /// de la vidéo source. 1 = comportement historique ; 0 = source muette
    /// (piste mutée ou écrasée par un solo).
    /// </summary>
    public double OriginalAudioGain { get; init; } = 1.0;

    /// <summary>Pistes externes du mixeur (Voix, Bruitages…) à mixer avec l'audio source.</summary>
    public IReadOnlyList<ExternalAudioTrack> ExternalAudioTracks { get; init; } = [];

    /// <summary>
    /// Prises de doublage actives des blocs (une par bloc au plus), calées sur leur
    /// bloc à l'export — c'est la promesse de <c>DialogueBlock.AudioFile</c> : la
    /// prise active est lue ET exportée.
    /// </summary>
    public IReadOnlyList<TakeAudioClip> Takes { get; init; } = [];

    public bool ForceCpuEncoder { get; init; }
    public string Title { get; init; } = "";
    public string Comment { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed record ExportProgressInfo(int Percent, double EffectiveFps, string EstimatedRemaining);

public sealed record EncoderInfo(string Encoder, string Label, bool IsGpu);

public static class VideoExporter
{
    // ── Détection de l'encodeur (port de detect_best_encoder) ────────────────

    private static readonly (string Encoder, string Label)[] GpuEncoders =
    [
        ("h264_nvenc", "NVIDIA NVENC (GPU)"),
        ("h264_amf", "AMD AMF (GPU)"),
        ("h264_qsv", "Intel QuickSync (GPU)"),
    ];

    public static EncoderInfo DetectBestEncoder(string ffmpegPath)
    {
        foreach (var (encoder, label) in GpuEncoders)
        {
            // Un test d'encodage réel sur une source nulle vérifie que le pilote marche
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "nullsrc=s=256x256:d=0.1", "-c:v", encoder, "-f", "null", "-" })
                psi.ArgumentList.Add(arg);
            try
            {
                using var process = Process.Start(psi);
                if (process is null) continue;
                process.StandardError.ReadToEnd();
                process.WaitForExit(15000);
                if (process.HasExited && process.ExitCode == 0)
                    return new EncoderInfo(encoder, label, IsGpu: true);
            }
            catch { }
        }
        return new EncoderInfo("libx264", "CPU (libx264)", IsGpu: false);
    }

    // ── Détection du letterbox (port de la détection canvas d'App.tsx) ───────

    /// <summary>
    /// Extrait une image vers t≈10 % de la durée et cherche les bandes noires
    /// horizontales. Retourne (cropTop, cropBottom exclusif) en pixels natifs.
    /// </summary>
    public static async Task<(int CropTop, int CropBottom)> DetectLetterboxAsync(
        string ffmpegPath, string videoPath, int width, int height, double duration, CancellationToken ct = default)
    {
        var noCrop = (0, height);
        if (width <= 0 || height <= 0) return noCrop;

        var probeTime = Math.Min(duration * 0.1, 5.0);
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-ss", probeTime.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-frames:v", "1",
            "-f", "rawvideo", "-pix_fmt", "rgba",
            "pipe:1",
        })
            psi.ArgumentList.Add(arg);

        byte[] pixels;
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return noCrop;
            _ = process.StandardError.ReadToEndAsync(ct);
            pixels = new byte[width * height * 4];
            var read = 0;
            var stream = process.StandardOutput.BaseStream;
            while (read < pixels.Length)
            {
                var n = await stream.ReadAsync(pixels.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
            await process.WaitForExitAsync(ct);
            if (read < pixels.Length) return noCrop;
        }
        catch
        {
            return noCrop;
        }

        const double brightnessThreshold = 15;
        const int sampleCols = 20;
        bool IsRowBlack(int row)
        {
            double total = 0;
            for (var s = 0; s < sampleCols; s++)
            {
                var col = s * width / sampleCols;
                var idx = (row * width + col) * 4;
                total += pixels[idx] * 0.299 + pixels[idx + 1] * 0.587 + pixels[idx + 2] * 0.114;
            }
            return total / sampleCols < brightnessThreshold;
        }

        var cropTop = 0;
        var cropBottom = height;
        for (var y = 0; y < height; y++)
            if (!IsRowBlack(y)) { cropTop = y; break; }
        for (var y = height - 1; y >= cropTop; y--)
            if (!IsRowBlack(y)) { cropBottom = y + 1; break; }

        // Garde-fou : si la zone restante est trop petite, c'est un faux positif
        if (cropBottom - cropTop < height * 0.5) return noCrop;
        return (cropTop, cropBottom);
    }

    // ── Pipeline d'export ─────────────────────────────────────────────────────

    public static async Task<string> ExportAsync(
        ExportSettings s, IBandStripSource band,
        IProgress<ExportProgressInfo>? progress = null,
        IProgress<EncoderInfo>? encoderInfo = null,
        CancellationToken ct = default)
    {
        if (band.HeightPx != s.BandRenderHeight)
            throw new ArgumentException("La hauteur des tuiles doit valoir BandRenderHeight (aucune mise à l'échelle verticale à la composition).");

        var encoder = s.ForceCpuEncoder
            ? new EncoderInfo("libx264", "CPU (libx264)", false)
            : DetectBestEncoder(s.FfmpegPath);
        encoderInfo?.Report(encoder);

        var rangeDuration = Math.Max(0.01, s.EndTime - s.StartTime);
        var totalFrames = (uint)Math.Ceiling(rangeDuration * s.Fps);
        var cropH = s.CropBottom - s.CropTop;
        var inv = CultureInfo.InvariantCulture;

        // --- Processus décodeur : vidéo source → frames BGRA brutes sur stdout ---
        var decoderPsi = RawProcess(s.FfmpegPath);
        AddArgs(decoderPsi,
            "-hide_banner", "-loglevel", "error",
            "-hwaccel", "auto",
            "-ss", s.StartTime.ToString("0.######", inv),
            "-i", s.VideoPath,
            "-t", rangeDuration.ToString("0.######", inv),
            "-vf", string.Format(inv,
                "crop={0}:{1}:0:{2},scale={3}:{4}:force_original_aspect_ratio=decrease,pad={3}:{4}:(ow-iw)/2:(oh-ih)/2,fps={5}",
                s.VideoWidth, cropH, s.CropTop, s.ExportWidth, s.VideoRenderHeight, s.Fps),
            "-f", "rawvideo", "-pix_fmt", "bgra", "-an",
            "pipe:1");
        decoderPsi.RedirectStandardOutput = true;
        decoderPsi.RedirectStandardError = true;

        // --- Processus encodeur : frames composées sur stdin (+ audio source) → MP4 ---
        var encoderPsi = RawProcess(s.FfmpegPath);
        AddArgs(encoderPsi,
            "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "bgra",
            "-s", $"{s.ExportWidth}x{s.ExportHeight}",
            "-r", s.Fps.ToString("0.######", inv),
            "-i", "pipe:0");
        // --- Audio : mixage des pistes du mixeur (Original + Voix/Bruitages…) et des
        // prises de doublage actives, calées sur leur bloc (adelay). ---
        // Sans piste externe ni prise : chemin historique (audio source seul, map
        // optionnel — robuste aux vidéos muettes sans sonde préalable). Sinon :
        // filter_complex volume (+ adelay pour les prises) par entrée + amix, en
        // sondant d'abord la présence d'audio dans la source (un [1:a:0] manquant
        // ferait échouer tout l'encodage).
        var audioMapped = false;
        if (s.IncludeAudio)
        {
            var originalGain = Math.Clamp(s.OriginalAudioGain, 0, 1);
            var externals = s.ExternalAudioTracks
                .Where(t => t.Gain > 0 && File.Exists(t.Path))
                .Select(t => t with { Gain = Math.Clamp(t.Gain, 0, 1) })
                .ToList();
            // Prises pertinentes : gain audible, fichier présent, bloc avant la fin de plage.
            var takes = s.Takes
                .Where(t => t.Gain > 0 && t.StartTime < s.EndTime && File.Exists(t.Path))
                .Select(t => t with { Gain = Math.Clamp(t.Gain, 0, 1) })
                .ToList();

            void AddTimedInput(string path, double startInSource) => AddArgs(encoderPsi,
                "-ss", startInSource.ToString("0.######", inv),
                "-t", rangeDuration.ToString("0.######", inv),
                "-i", path);

            if (externals.Count == 0 && takes.Count == 0)
            {
                if (originalGain > 0)
                {
                    AddTimedInput(s.VideoPath, s.StartTime);
                    AddArgs(encoderPsi,
                        "-map", "0:v", "-map", "1:a:0?",
                        "-c:a", "aac", "-b:a", "192k");
                    if (originalGain < 1)
                        AddArgs(encoderPsi, "-af", string.Format(inv, "volume={0:0.####}", originalGain));
                    audioMapped = true;
                }
                // originalGain == 0 et rien d'autre à mixer → export muet, voulu
            }
            else
            {
                // (fichier, gain, -ss dans la source, retard d'insertion sur la timeline)
                var audioInputs = new List<(string Path, double Gain, double Ss, double Delay)>();
                if (originalGain > 0 && await HasAudioStreamAsync(s.FfmpegPath, s.VideoPath, ct))
                    audioInputs.Add((s.VideoPath, originalGain, s.StartTime, 0.0));
                audioInputs.AddRange(externals.Select(t => (t.Path, t.Gain, s.StartTime, 0.0)));
                // Une prise commence à son bloc : si la plage exportée commence après le
                // bloc, on entre dans le WAV (-ss) ; sinon on retarde la prise (adelay)
                // jusqu'à ce que la timeline atteigne le bloc.
                audioInputs.AddRange(takes.Select(t => (
                    t.Path, t.Gain,
                    Math.Max(0, s.StartTime - t.StartTime),
                    Math.Max(0, t.StartTime - s.StartTime))));

                if (audioInputs.Count > 0)
                {
                    var chains = new List<string>();
                    for (var i = 0; i < audioInputs.Count; i++)
                    {
                        var (path, gain, ss, delay) = audioInputs[i];
                        AddTimedInput(path, ss);
                        // L'entrée 0 est la vidéo brute sur stdin → audio à partir de 1
                        var chain = string.Format(inv, "[{0}:a:0]volume={1:0.####}", i + 1, gain);
                        if (delay > 0.0005)
                            chain += string.Format(inv, ",adelay=delays={0}:all=1", (long)Math.Round(delay * 1000));
                        chains.Add(chain + string.Format(inv, "[a{0}]", i));
                    }
                    // normalize=0 : amix ne doit pas re-pondérer, les gains du mixeur font
                    // foi. apad + -shortest : l'audio (prises plus courtes que la vidéo…)
                    // est complété de silence jusqu'à la fin de la vidéo, au lieu que
                    // -shortest tronque l'export à la fin du dernier son.
                    var mix = audioInputs.Count == 1
                        ? "[a0]apad[aout]"
                        : string.Concat(Enumerable.Range(0, audioInputs.Count).Select(i => $"[a{i}]"))
                          + string.Format(inv, "amix=inputs={0}:duration=longest:normalize=0,apad[aout]", audioInputs.Count);
                    AddArgs(encoderPsi,
                        "-filter_complex", string.Join(";", chains) + ";" + mix,
                        "-map", "0:v", "-map", "[aout]",
                        "-c:a", "aac", "-b:a", "192k");
                    audioMapped = true;
                }
            }
        }
        AddArgs(encoderPsi, "-c:v", encoder.Encoder);
        switch (encoder.Encoder)
        {
            case "h264_nvenc":
                AddArgs(encoderPsi, "-preset", "p4", "-rc", "vbr", "-rc-lookahead", "32");
                break;
            case "h264_amf":
                AddArgs(encoderPsi, "-quality", "balanced");
                break;
            case "h264_qsv":
                AddArgs(encoderPsi, "-preset", "medium");
                break;
            default:
                AddArgs(encoderPsi, "-preset", "medium");
                break;
        }
        AddArgs(encoderPsi,
            "-b:v", s.Bitrate.ToString(inv),
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            "-metadata", $"title={s.Title}",
            "-metadata", $"comment={s.Comment}",
            "-metadata", $"description={s.Description}",
            "-metadata", "encoder=RhythmoSync Studio");
        if (audioMapped) AddArgs(encoderPsi, "-shortest");
        AddArgs(encoderPsi, "-y", s.OutputPath);
        encoderPsi.RedirectStandardInput = true;
        encoderPsi.RedirectStandardError = true;

        using var decoder = Process.Start(decoderPsi)
            ?? throw new InvalidOperationException("Impossible de démarrer le décodeur FFmpeg.");
        using var encoder2 = Process.Start(encoderPsi)
            ?? throw new InvalidOperationException("Impossible de démarrer l'encodeur FFmpeg.");

        var decoderStderr = decoder.StandardError.ReadToEndAsync(CancellationToken.None);
        var encoderStderr = encoder2.StandardError.ReadToEndAsync(CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        uint frameCount = 0;

        try
        {
            var input = decoder.StandardOutput.BaseStream;
            var output = encoder2.StandardInput.BaseStream;

            var videoFrameSize = s.ExportWidth * s.VideoRenderHeight * 4;
            var outFrameSize = s.ExportWidth * s.ExportHeight * 4;
            var videoBuf = new byte[videoFrameSize];
            var outFrame = new byte[outFrameSize];
            var lastProgress = Stopwatch.StartNew();

            // Cache séquentiel de tuiles (l'accès avance toujours vers la droite)
            var tileCache = new Dictionary<int, byte[]>();
            byte[] Tile(int index)
            {
                if (!tileCache.TryGetValue(index, out var tile))
                {
                    tile = band.GetTile(index);
                    tileCache[index] = tile;
                    foreach (var old in tileCache.Keys.Where(k => k < index - 1).ToList())
                        tileCache.Remove(old);
                }
                return tile;
            }

            // Fond sombre (même couleur que la bande UI, #111827) pour hors-bande
            var darkRow = new byte[s.ExportWidth * 4];
            for (var i = 0; i < s.ExportWidth; i++)
            {
                darkRow[i * 4] = 0x27; darkRow[i * 4 + 1] = 0x18; darkRow[i * 4 + 2] = 0x11; darkRow[i * 4 + 3] = 0xFF;
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Lire une frame vidéo décodée complète
                var read = 0;
                while (read < videoFrameSize)
                {
                    var n = await input.ReadAsync(videoBuf.AsMemory(read, videoFrameSize - read), ct);
                    if (n == 0) break;
                    read += n;
                }
                if (read == 0) break;
                if (read < videoFrameSize)
                    throw new IOException($"Frame incomplète du décodeur ({read}/{videoFrameSize} octets) à la frame {frameCount}.");

                // Partie haute : la vidéo telle quelle
                Buffer.BlockCopy(videoBuf, 0, outFrame, 0, videoFrameSize);

                // Partie basse : section défilante de la bande (copies de segments par ligne)
                var time = s.StartTime + frameCount / s.Fps;
                var stripX = (int)((time + s.SyncOffsetEffective) * s.Pps);
                ComposeBandRows(outFrame, s, band, Tile, stripX, darkRow);

                // Ligne de synchro rouge (2 px) par-dessus la bande
                DrawSyncLine(outFrame, s);

                await output.WriteAsync(outFrame.AsMemory(0, outFrameSize), ct);
                frameCount++;

                if (lastProgress.ElapsedMilliseconds > 500)
                {
                    var elapsed = stopwatch.Elapsed.TotalSeconds;
                    var fpsEff = frameCount / Math.Max(0.001, elapsed);
                    var remFrames = totalFrames > frameCount ? totalFrames - frameCount : 0;
                    var remSecs = remFrames / Math.Max(0.001, fpsEff);
                    var percent = (int)Math.Min(100, frameCount * 100.0 / totalFrames);
                    progress?.Report(new ExportProgressInfo(percent, fpsEff, $"{(int)(remSecs / 60)}m {(int)(remSecs % 60)}s"));
                    lastProgress.Restart();
                }
            }

            output.Close(); // EOF → l'encodeur finalise le MP4
        }
        catch (OperationCanceledException)
        {
            try { decoder.Kill(entireProcessTree: true); } catch { }
            try { encoder2.Kill(entireProcessTree: true); } catch { }
            try { if (File.Exists(s.OutputPath)) File.Delete(s.OutputPath); } catch { }
            throw;
        }

        await decoder.WaitForExitAsync(ct);
        await encoder2.WaitForExitAsync(ct);

        if (decoder.ExitCode != 0)
            throw new InvalidOperationException($"Le décodeur FFmpeg a échoué (code {decoder.ExitCode}) : {await decoderStderr}");
        if (encoder2.ExitCode != 0)
            throw new InvalidOperationException($"L'encodeur FFmpeg a échoué (code {encoder2.ExitCode}) : {await encoderStderr}");

        var totalTime = stopwatch.Elapsed.TotalSeconds;
        var finalFps = frameCount / Math.Max(0.001, totalTime);
        progress?.Report(new ExportProgressInfo(100, finalFps, "Terminé !"));
        return $"Export terminé : {frameCount} frames en {totalTime:0.0}s ({finalFps:0.0} fps effectifs)";
    }

    /// <summary>
    /// Copie les lignes de la bande dans la frame de sortie. Contrairement à la version
    /// Rust (boucle par pixel), on copie des segments contigus par ligne et par tuile.
    /// </summary>
    private static void ComposeBandRows(
        byte[] outFrame, ExportSettings s, IBandStripSource band,
        Func<int, byte[]> tile, int stripX, byte[] darkRow)
    {
        var width = s.ExportWidth;
        var tileW = band.TileWidthPx;
        var bandTop = s.VideoRenderHeight;

        for (var row = 0; row < s.BandRenderHeight; row++)
        {
            var destBase = (bandTop + row) * width * 4;

            var col = 0;
            while (col < width)
            {
                var srcX = stripX + col;
                if (srcX < 0)
                {
                    // Avant le début de la bande : fond sombre
                    var run = Math.Min(width - col, -srcX);
                    Buffer.BlockCopy(darkRow, 0, outFrame, destBase + col * 4, run * 4);
                    col += run;
                }
                else if (srcX >= band.TotalWidthPx)
                {
                    Buffer.BlockCopy(darkRow, 0, outFrame, destBase + col * 4, (width - col) * 4);
                    col = width;
                }
                else
                {
                    var tileIndex = srcX / tileW;
                    var inTileX = srcX % tileW;
                    var run = Math.Min(width - col, Math.Min(tileW - inTileX, band.TotalWidthPx - srcX));
                    var tilePixels = tile(tileIndex);
                    Buffer.BlockCopy(tilePixels, (row * tileW + inTileX) * 4, outFrame, destBase + col * 4, run * 4);
                    col += run;
                }
            }
        }
    }

    private static void DrawSyncLine(byte[] outFrame, ExportSettings s)
    {
        var width = s.ExportWidth;
        var bandTop = s.VideoRenderHeight;
        for (var row = 0; row < s.BandRenderHeight; row++)
        {
            for (var dx = 0; dx < 2; dx++)
            {
                var x = s.SyncLineX + dx;
                if (x >= width) continue;
                var idx = ((bandTop + row) * width + x) * 4;
                outFrame[idx] = 0x44;     // B  (#ef4444 — même rouge que l'UI)
                outFrame[idx + 1] = 0x44; // G
                outFrame[idx + 2] = 0xEF; // R
                outFrame[idx + 3] = 0xFF; // A
            }
        }
    }

    /// <summary>La source contient-elle au moins un flux audio ? (même sonde stderr que VideoProber)</summary>
    private static async Task<bool> HasAudioStreamAsync(string ffmpegPath, string videoPath, CancellationToken ct)
    {
        var psi = RawProcess(ffmpegPath);
        AddArgs(psi, "-hide_banner", "-i", videoPath, "-t", "0.05", "-f", "null", "-");
        psi.RedirectStandardError = true;
        psi.RedirectStandardOutput = true;
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return stderr.Contains("Audio:");
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo RawProcess(string path) => new(path)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static void AddArgs(ProcessStartInfo psi, params string[] args)
    {
        foreach (var arg in args) psi.ArgumentList.Add(arg);
    }
}
