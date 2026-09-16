using System.Globalization;
using System.Text;
using RhythmoSync.Core.Models;

namespace RhythmoSync.Core;

/// <summary>
/// Import/export des formats texte (port des fonctions web handleExportSRT/VTT/TXT/CSV
/// et de la commande Rust import_subtitles). Logique pure, sans dépendance UI.
/// </summary>
public static class SubtitleIo
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Formats texte exportables, alignés sur le menu Export de la version web.</summary>
    public enum TextFormat { Srt, Vtt, Txt, Csv }

    public static TextFormat? FormatFromExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".srt" => TextFormat.Srt,
            ".vtt" => TextFormat.Vtt,
            ".txt" => TextFormat.Txt,
            ".csv" => TextFormat.Csv,
            _ => null,
        };

    public static string Export(IEnumerable<DialogueBlock> dialogues, TextFormat format) => format switch
    {
        TextFormat.Srt => ExportSrt(dialogues),
        TextFormat.Vtt => ExportVtt(dialogues),
        TextFormat.Txt => ExportTxt(dialogues),
        TextFormat.Csv => ExportCsv(dialogues),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static IEnumerable<DialogueBlock> Sorted(IEnumerable<DialogueBlock> dialogues) =>
        dialogues.OrderBy(d => d.StartTime);

    // ── Exports ─────────────────────────────────────────────────────────────

    public static string ExportSrt(IEnumerable<DialogueBlock> dialogues)
    {
        var sb = new StringBuilder();
        var index = 1;
        foreach (var d in Sorted(dialogues))
        {
            sb.Append(index++).Append('\n');
            sb.Append(FormatTimestamp(d.StartTime, ',')).Append(" --> ").Append(FormatTimestamp(d.EndTime, ',')).Append('\n');
            sb.Append(d.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ExportVtt(IEnumerable<DialogueBlock> dialogues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var d in Sorted(dialogues))
        {
            sb.Append(FormatTimestamp(d.StartTime, '.')).Append(" --> ").Append(FormatTimestamp(d.EndTime, '.')).Append('\n');
            sb.Append(d.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ExportTxt(IEnumerable<DialogueBlock> dialogues)
    {
        var sb = new StringBuilder();
        foreach (var d in Sorted(dialogues))
            sb.Append('[').Append(FormatClock(d.StartTime)).Append("] ")
              .Append(d.CharacterName).Append(": ").Append(d.Text).Append('\n');
        return sb.ToString();
    }

    public static string ExportCsv(IEnumerable<DialogueBlock> dialogues)
    {
        var sb = new StringBuilder("ID,StartTime,Duration,EndTime,Character,Text\n");
        foreach (var d in Sorted(dialogues))
        {
            sb.Append(d.Id).Append(',')
              .Append(d.StartTime.ToString("0.000", Inv)).Append(',')
              .Append(d.Duration.ToString("0.000", Inv)).Append(',')
              .Append(d.EndTime.ToString("0.000", Inv)).Append(',')
              .Append(CsvQuote(d.CharacterName)).Append(',')
              .Append(CsvQuote(d.Text)).Append('\n');
        }
        return sb.ToString();
    }

    private static string CsvQuote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>« HH:MM:SS&lt;sep&gt;mmm » — sep = ',' (SRT) ou '.' (VTT).</summary>
    private static string FormatTimestamp(double seconds, char separator)
    {
        if (seconds < 0) seconds = 0;
        // Arrondi sur le total en millisecondes : décomposer champ par champ tronquait
        // (1,1 s → « 099 » ms au lieu de « 100 » à cause de la représentation binaire).
        var totalMs = (long)Math.Round(seconds * 1000);
        var h = totalMs / 3_600_000;
        var m = totalMs / 60_000 % 60;
        var s = totalMs / 1000 % 60;
        var ms = totalMs % 1000;
        return $"{h:00}:{m:00}:{s:00}{separator}{ms:000}";
    }

    /// <summary>« HH:MM:SS » sans millisecondes (transcript .txt).</summary>
    private static string FormatClock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var h = (int)(seconds / 3600);
        var m = (int)(seconds % 3600 / 60);
        var s = (int)(seconds % 60);
        return $"{h:00}:{m:00}:{s:00}";
    }

    // ── Import (port de import.rs) ──────────────────────────────────────────

    /// <summary>Parse un fichier .srt ou .vtt en blocs de dialogue (d'après l'extension).</summary>
    public static List<DialogueBlock> Parse(string content, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".srt" => ParseSrt(content),
            ".vtt" => ParseVtt(content),
            _ => throw new NotSupportedException("Format non supporté. Utilisez .srt ou .vtt."),
        };
    }

    public static List<DialogueBlock> ParseSrt(string content)
    {
        var blocks = new List<DialogueBlock>();
        if (string.IsNullOrWhiteSpace(content)) return blocks;

        var sb = new StringBuilder();
        double currentStart = 0;
        double currentEnd = 0;
        bool hasTiming = false;

        foreach (var lineSpan in MemoryExtensions.EnumerateLines(content.AsSpan()))
        {
            var trimmed = lineSpan.Trim();
            if (trimmed.IsEmpty)
            {
                if (hasTiming)
                {
                    FlushBlock(blocks, sb, currentStart, currentEnd, "#8b5cf6");
                    hasTiming = false;
                }
                continue;
            }

            var arrowIdx = trimmed.IndexOf("-->".AsSpan(), StringComparison.Ordinal);
            if (arrowIdx >= 0)
            {
                if (hasTiming)
                {
                    FlushBlock(blocks, sb, currentStart, currentEnd, "#8b5cf6");
                }

                var left = trimmed.Slice(0, arrowIdx).Trim();
                var right = trimmed.Slice(arrowIdx + 3).Trim();

                currentStart = ParseSrtTime(left);
                currentEnd = ParseSrtTime(right);
                hasTiming = true;
                sb.Clear();
                continue;
            }

            if (hasTiming)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(trimmed);
            }
        }

        if (hasTiming)
        {
            FlushBlock(blocks, sb, currentStart, currentEnd, "#8b5cf6");
        }

        return blocks;
    }

    public static List<DialogueBlock> ParseVtt(string content)
    {
        var blocks = new List<DialogueBlock>();
        if (string.IsNullOrWhiteSpace(content)) return blocks;

        var sb = new StringBuilder();
        double currentStart = 0;
        double currentEnd = 0;
        bool hasTiming = false;
        bool seenHeader = false;

        foreach (var lineSpan in MemoryExtensions.EnumerateLines(content.AsSpan()))
        {
            var trimmed = lineSpan.Trim();
            if (!seenHeader)
            {
                if (trimmed.StartsWith("WEBVTT".AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    seenHeader = true;
                    continue;
                }
                if (!trimmed.IsEmpty)
                {
                    seenHeader = true;
                }
            }

            if (trimmed.IsEmpty)
            {
                if (hasTiming)
                {
                    FlushBlock(blocks, sb, currentStart, currentEnd, "#10b981");
                    hasTiming = false;
                }
                continue;
            }

            var arrowIdx = trimmed.IndexOf("-->".AsSpan(), StringComparison.Ordinal);
            if (arrowIdx >= 0)
            {
                if (hasTiming)
                {
                    FlushBlock(blocks, sb, currentStart, currentEnd, "#10b981");
                }

                var left = trimmed.Slice(0, arrowIdx).Trim();
                var right = trimmed.Slice(arrowIdx + 3).Trim();

                var firstSpace = right.IndexOfAny(' ', '\t');
                var endToken = firstSpace >= 0 ? right.Slice(0, firstSpace) : right;

                currentStart = ParseVttTime(left);
                currentEnd = ParseVttTime(endToken);
                hasTiming = true;
                sb.Clear();
                continue;
            }

            if (hasTiming)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(trimmed);
            }
        }

        if (hasTiming)
        {
            FlushBlock(blocks, sb, currentStart, currentEnd, "#10b981");
        }

        return blocks;
    }

    private static void FlushBlock(List<DialogueBlock> blocks, StringBuilder sb, double start, double end, string color)
    {
        var text = sb.ToString().Trim();
        blocks.Add(new DialogueBlock
        {
            Text = text,
            StartTime = start,
            Duration = Math.Max(0.1, end - start),
            CharacterName = "Import",
            Color = color,
            Lane = 0,
        });
        sb.Clear();
    }

    // « 00:00:00,000 » — certains outils écrivent les millisecondes avec un point
    // (« 00:00:00.000 ») : les deux séparateurs sont acceptés, sinon tous les temps
    // retomberaient silencieusement à 0.
    private static double ParseSrtTime(ReadOnlySpan<char> span)
    {
        span = span.Trim();
        var firstColon = span.IndexOf(':');
        if (firstColon < 0) return 0;
        var secondColon = span.Slice(firstColon + 1).IndexOf(':');
        if (secondColon < 0) return 0;
        secondColon += firstColon + 1;

        var hSpan = span.Slice(0, firstColon);
        var mSpan = span.Slice(firstColon + 1, secondColon - firstColon - 1);
        var sSpan = span.Slice(secondColon + 1);

        if (!double.TryParse(hSpan, NumberStyles.Float, Inv, out var hours) ||
            !double.TryParse(mSpan, NumberStyles.Float, Inv, out var minutes))
        {
            return 0;
        }

        var sepIdx = sSpan.IndexOfAny(',', '.');
        double seconds;
        if (sepIdx >= 0)
        {
            var secIntSpan = sSpan.Slice(0, sepIdx);
            var msSpan = sSpan.Slice(sepIdx + 1);
            double.TryParse(secIntSpan, NumberStyles.Float, Inv, out var sec);
            double.TryParse(msSpan, NumberStyles.Float, Inv, out var ms);
            seconds = sec + ms / 1000.0;
        }
        else
        {
            double.TryParse(sSpan, NumberStyles.Float, Inv, out seconds);
        }

        return hours * 3600 + minutes * 60 + seconds;
    }

    // « HH:MM:SS.mmm » ou « MM:SS.mmm »
    private static double ParseVttTime(ReadOnlySpan<char> span)
    {
        span = span.Trim();
        var firstColon = span.IndexOf(':');
        if (firstColon < 0) return 0;

        var secondColon = span.Slice(firstColon + 1).IndexOf(':');
        if (secondColon >= 0)
        {
            secondColon += firstColon + 1;
            var hSpan = span.Slice(0, firstColon);
            var mSpan = span.Slice(firstColon + 1, secondColon - firstColon - 1);
            var sSpan = span.Slice(secondColon + 1);

            double.TryParse(hSpan, NumberStyles.Float, Inv, out var h);
            double.TryParse(mSpan, NumberStyles.Float, Inv, out var m);
            double.TryParse(sSpan, NumberStyles.Float, Inv, out var s);
            return h * 3600 + m * 60 + s;
        }
        else
        {
            var mSpan = span.Slice(0, firstColon);
            var sSpan = span.Slice(firstColon + 1);

            double.TryParse(mSpan, NumberStyles.Float, Inv, out var m);
            double.TryParse(sSpan, NumberStyles.Float, Inv, out var s);
            return m * 60 + s;
        }
    }
}
