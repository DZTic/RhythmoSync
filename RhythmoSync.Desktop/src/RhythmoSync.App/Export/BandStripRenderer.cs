using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RhythmoSync.Core.Models;
using RhythmoSync.Media;

namespace RhythmoSync.App.Export;

/// <summary>
/// Rend la bande rythmo en tuiles BGRA pour l'export vidéo, avec le même style que
/// RhythmoBandControl à l'écran, en mode « propre » : sans poignées, sans alertes
/// « trop rapide », sans indicateur de snap. Les tuiles sont rendues paresseusement
/// (RenderTargetBitmap), ce qui supprime le plafond de 32 000 px de l'ancien export.
/// </summary>
public sealed class BandStripRenderer : IBandStripSource
{
    private readonly IReadOnlyList<DialogueBlock> _dialogues;
    private readonly int _lanes;
    private readonly double _laneHeight;   // hauteur d'une piste, déjà mise à l'échelle
    private readonly double _pps;          // pixels par seconde dans l'export
    private readonly double _scale;        // laneScale (épaisseurs, polices, rayons)

    public int TotalWidthPx { get; }
    public int HeightPx { get; }
    public int TileWidthPx => 4096;

    public BandStripRenderer(
        IReadOnlyList<DialogueBlock> dialogues, int lanes,
        double bandHeightPx, double pps, double scale, double videoDuration)
    {
        _dialogues = dialogues;
        _lanes = Math.Max(1, lanes);
        HeightPx = (int)Math.Round(bandHeightPx);
        _laneHeight = bandHeightPx / _lanes;
        _pps = pps;
        _scale = scale;
        TotalWidthPx = Math.Max(TileWidthPx, (int)Math.Ceiling(videoDuration * pps));
    }

    private static readonly Typeface BlockTypeface =
        new(new FontFamily("Consolas, Courier New"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public byte[] GetTile(int tileIndex)
    {
        // Le rendu WPF doit se faire sur le thread UI ; l'export tourne en arrière-plan.
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
            return RenderTile(tileIndex);
        return app.Dispatcher.Invoke(() => RenderTile(tileIndex));
    }

    private byte[] RenderTile(int tileIndex)
    {
        var x0 = (double)tileIndex * TileWidthPx;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Fond — même couleur que la bande à l'écran
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), null,
                new Rect(0, 0, TileWidthPx, HeightPx));

            // Séparateurs de pistes (pointillés) + bordures haut/bas
            var lanePen = new Pen(new SolidColorBrush(Color.FromRgb(0x2d, 0x37, 0x48)), Math.Max(1, _scale))
            {
                DashStyle = new DashStyle([6, 4], 0),
            };
            lanePen.Freeze();
            for (var i = 1; i < _lanes; i++)
                dc.DrawLine(lanePen, new Point(0, i * _laneHeight), new Point(TileWidthPx, i * _laneHeight));

            var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x4a, 0x55, 0x68)), 1);
            borderPen.Freeze();
            dc.DrawLine(borderPen, new Point(0, 0.5), new Point(TileWidthPx, 0.5));
            dc.DrawLine(borderPen, new Point(0, HeightPx - 0.5), new Point(TileWidthPx, HeightPx - 0.5));

            // Blocs visibles dans cette tuile (en coordonnées locales : x − x0)
            var t0 = x0 / _pps;
            var t1 = (x0 + TileWidthPx) / _pps;
            var vPad = 3 * _scale;
            foreach (var block in _dialogues)
            {
                if (block.EndTime < t0 || block.StartTime > t1) continue;

                var bx = block.StartTime * _pps - x0;
                var bw = Math.Max(2, block.Duration * _pps);
                var lane = Math.Clamp(block.Lane, 0, _lanes - 1);
                var by = lane * _laneHeight + vPad;
                var bh = _laneHeight - vPad * 2;

                Color color;
                try { color = (Color)ColorConverter.ConvertFromString(block.Color); }
                catch { color = Color.FromRgb(0x63, 0x66, 0xf1); }

                var fill = new SolidColorBrush(color) { Opacity = 0.35 };
                fill.Freeze();
                var stroke = new Pen(new SolidColorBrush(color), 1);
                stroke.Freeze();
                dc.DrawRoundedRectangle(fill, stroke, new Rect(bx, by, bw, bh), 4 * _scale, 4 * _scale);

                if (block.Text.Length == 0) continue;

                // Texte étiré sur la largeur du bloc (même rendu que l'écran),
                // avec une pseudo-ombre (copie noire décalée) pour la lisibilité.
                // Toujours sur une seule ligne : les retours à la ligne deviennent des espaces.
                var fontSize = Math.Max(8, bh * 0.6);
                var oneLine = block.Text.ReplaceLineEndings(" ");
                var white = MakeText(oneLine, fontSize, Brushes.White);
                var black = MakeText(oneLine, fontSize, Brushes.Black);
                white.MaxLineCount = 1;
                black.MaxLineCount = 1;
                var natural = white.WidthIncludingTrailingWhitespace;
                if (natural < 0.1) continue;

                var textY = by + (bh - white.Height) / 2;
                dc.PushTransform(new TranslateTransform(bx, textY));
                dc.PushTransform(new ScaleTransform(bw / natural, 1));
                dc.DrawText(black, new Point(Math.Max(0.5, _scale * 0.7), Math.Max(0.5, _scale * 0.7)));
                dc.DrawText(white, new Point(0, 0));
                dc.Pop();
                dc.Pop();
            }
        }

        var bitmap = new RenderTargetBitmap(TileWidthPx, HeightPx, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[TileWidthPx * HeightPx * 4];
        bitmap.CopyPixels(pixels, TileWidthPx * 4, 0);
        return pixels;
    }

    private static FormattedText MakeText(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, BlockTypeface, size, brush, 1.0);
}
