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
        var chunks = content.Replace("\r\n", "\n").Split("\n\n");
        foreach (var chunk in chunks)
        {
            var lines = chunk.Split('\n');
            if (lines.Length < 3) continue;

            var timeIdx = 1;
            if (!lines[1].Contains("-->"))
            {
                timeIdx = 0;
                if (!lines[0].Contains("-->")) continue;
            }

            var parts = lines[timeIdx].Split("-->");
            if (parts.Length < 2) continue;

            var start = ParseSrtTime(parts[0]);
            var end = ParseSrtTime(parts[1]);
            var text = string.Join("\n", lines[(timeIdx + 1)..]).Trim();

            blocks.Add(new DialogueBlock
            {
                Text = text,
                StartTime = start,
                Duration = Math.Max(0.1, end - start),
                CharacterName = "Import",
                Color = "#8b5cf6",
                Lane = 0,
            });
        }
        return blocks;
    }

    public static List<DialogueBlock> ParseVtt(string content)
    {
        var blocks = new List<DialogueBlock>();
        var lines = content.Replace("\r\n", "\n").Split('\n');

        var i = 0;
        if (lines.Length > 0 && lines[0].StartsWith("WEBVTT")) i++;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) { i++; continue; }

            if (line.Contains("-->"))
            {
                var parts = line.Split("-->");
                if (parts.Length >= 2)
                {
                    var start = ParseVttTime(parts[0]);
                    // Retire les éventuelles options d'alignement (« align:center » …)
                    var endToken = parts[1].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    var end = ParseVttTime(endToken);

                    i++;
                    var sb = new StringBuilder();
                    while (i < lines.Length && lines[i].Trim().Length > 0)
                    {
                        sb.Append(lines[i]).Append('\n');
                        i++;
                    }

                    blocks.Add(new DialogueBlock
                    {
                        Text = sb.ToString().Trim(),
                        StartTime = start,
                        Duration = Math.Max(0.1, end - start),
                        CharacterName = "Import",
                        Color = "#10b981",
                        Lane = 0,
                    });
                }
                else i++;
            }
            else i++;
        }
        return blocks;
    }

    // « 00:00:00,000 » — certains outils écrivent les millisecondes avec un point
    // (« 00:00:00.000 ») : les deux séparateurs sont acceptés, sinon tous les temps
    // retomberaient silencieusement à 0.
    private static double ParseSrtTime(string timeStr)
    {
        var parts = timeStr.Trim().Split(':');
        if (parts.Length != 3) return 0;
        var secMs = parts[2].Split(',', '.');
        var seconds = secMs.Length == 2 ? Num(secMs[0]) + Num(secMs[1]) / 1000.0 : Num(parts[2]);
        return Num(parts[0]) * 3600 + Num(parts[1]) * 60 + seconds;
    }

    // « HH:MM:SS.mmm » ou « MM:SS.mmm »
    private static double ParseVttTime(string timeStr)
    {
        var parts = timeStr.Trim().Split(':');
        return parts.Length switch
        {
            3 => Num(parts[0]) * 3600 + Num(parts[1]) * 60 + Num(parts[2]),
            2 => Num(parts[0]) * 60 + Num(parts[1]),
            _ => 0,
        };
    }

    private static double Num(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, Inv, out var v) ? v : 0;
}
