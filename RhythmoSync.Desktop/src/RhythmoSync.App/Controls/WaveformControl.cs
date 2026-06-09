using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RhythmoSync.Core;
using RhythmoSync.Media;

namespace RhythmoSync.App.Controls;

/// <summary>
/// Affichage de la forme d'onde en tuiles de 30 s virtualisées (port du tiling de
/// Waveform.tsx). Chaque tuile est un DrawingVisual figé, dessiné une seule fois ;
/// le défilement n'est qu'une TranslateTransform. Les tuiles hors champ sont
/// démontées pour libérer la mémoire.
/// </summary>
public sealed class WaveformControl : FrameworkElement
{
    private const double TileSeconds = 30;
    private const int TileBuffer = 1;

    private ProjectState _state = null!;
    private readonly VisualCollection _children;
    private readonly DrawingVisual _background = new();
    private readonly ContainerVisual _movingRoot = new();
    private readonly DrawingVisual _syncOverlay = new();
    private readonly TranslateTransform _scroll = new();
    private readonly Dictionary<int, DrawingVisual> _tiles = [];

    private WaveformData? _data;
    private double _time;
    private double _renderedPps = -1;

    private bool _scrubbing;
    private double _lastScrubX;
    private Point _mouseDownPoint;
    private bool _dragMoved;

    public event Action<double>? SeekRequested;

    public WaveformControl()
    {
        _children = new VisualCollection(this);
        ClipToBounds = true;
        _movingRoot.Transform = _scroll;
        _children.Add(_background);
        _children.Add(_movingRoot);
        _children.Add(_syncOverlay);

        SizeChanged += (_, _) => { RedrawBackground(); RedrawSyncOverlay(); ClearTiles(); };
        Loaded += (_, _) => { RedrawBackground(); RedrawSyncOverlay(); };
    }

    public void Initialize(ProjectState state)
    {
        _state = state;
        _state.ViewChanged += () => { ClearTiles(); UpdateTime(_time); };
    }

    public void SetWaveform(WaveformData? data)
    {
        _data = data;
        ClearTiles();
        UpdateTime(_time);
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    private void ClearTiles()
    {
        _movingRoot.Children.Clear();
        _tiles.Clear();
        _renderedPps = _state?.ZoomLevel ?? -1;
    }

    public void UpdateTime(double time)
    {
        if (_state is null) return;
        _time = time;

        var pps = _state.ZoomLevel;
        if (pps != _renderedPps) ClearTiles();

        var targetX = Math.Round(RhythmoConstants.SyncLinePositionX - (time + _state.SyncOffset) * pps);
        if (_scroll.X != targetX) _scroll.X = targetX;

        if (_data is null) return;

        // Virtualisation des tuiles : visibles + marge
        var t0 = (0 - _scroll.X) / pps;
        var t1 = (ActualWidth - _scroll.X) / pps;
        var firstTile = Math.Max(0, (int)(t0 / TileSeconds) - TileBuffer);
        var lastTile = Math.Min((int)(_data.Duration / TileSeconds), (int)(t1 / TileSeconds) + TileBuffer);

        var stale = _tiles.Keys.Where(i => i < firstTile || i > lastTile).ToList();
        foreach (var i in stale)
        {
            _movingRoot.Children.Remove(_tiles[i]);
            _tiles.Remove(i);
        }

        for (var i = firstTile; i <= lastTile; i++)
        {
            if (_tiles.ContainsKey(i)) continue;
            var tile = RenderTile(i, pps);
            _tiles[i] = tile;
            _movingRoot.Children.Add(tile);
        }
    }

    private static readonly SolidColorBrush WaveBrush = Make(Color.FromRgb(0x81, 0x8c, 0xf8));
    private static readonly Pen WavePen = MakePen(WaveBrush, 1);
    private static readonly Pen AxisPen = MakePen(Make(Color.FromArgb(80, 0x81, 0x8c, 0xf8)), 1);

    private static SolidColorBrush Make(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen MakePen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }

    private DrawingVisual RenderTile(int index, double pps)
    {
        var visual = new DrawingVisual { Transform = new TranslateTransform(index * TileSeconds * pps, 0) };
        if (_data is null) return visual;

        var peaks = _data.Peaks;
        var numBuckets = peaks.Length / 2;
        var duration = _data.Duration;
        var height = Math.Max(10, ActualHeight);
        var midY = height / 2;
        var amp = height / 2 - 2;

        var tileStart = index * TileSeconds;
        var tileWidth = (int)Math.Ceiling(Math.Min(TileSeconds, duration - tileStart) * pps);
        if (tileWidth <= 0) return visual;

        var geometry = new StreamGeometry();
        using (var geo = geometry.Open())
        {
            for (var x = 0; x < tileWidth; x++)
            {
                var tA = tileStart + x / pps;
                var tB = tileStart + (x + 1) / pps;
                var b0 = Math.Clamp((int)(tA / duration * numBuckets), 0, numBuckets - 1);
                var b1 = Math.Clamp((int)(tB / duration * numBuckets), b0, numBuckets - 1);

                float min = 0f, max = 0f;
                for (var b = b0; b <= b1; b++)
                {
                    if (peaks[b * 2] < min) min = peaks[b * 2];
                    if (peaks[b * 2 + 1] > max) max = peaks[b * 2 + 1];
                }

                geo.BeginFigure(new Point(x, midY - max * amp), false, false);
                geo.LineTo(new Point(x, midY - min * amp), true, false);
            }
        }
        geometry.Freeze();

        using var dc = visual.RenderOpen();
        dc.DrawLine(AxisPen, new Point(0, midY), new Point(tileWidth, midY));
        dc.DrawGeometry(null, WavePen, geometry);
        return visual;
    }

    private void RedrawBackground()
    {
        using var dc = _background.RenderOpen();
        dc.DrawRectangle(Make(Color.FromRgb(0x0b, 0x10, 0x1c)), null, new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)));
    }

    private void RedrawSyncOverlay()
    {
        using var dc = _syncOverlay.RenderOpen();
        var x = RhythmoConstants.SyncLinePositionX;
        dc.DrawLine(MakePen(Make(Color.FromRgb(0xef, 0x44, 0x44)), 2), new Point(x, 0), new Point(x, Math.Max(0, ActualHeight)));
    }

    // ── Scrub à la souris (drag = défilement, clic = saut) ───────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_state is null) return;
        _scrubbing = true;
        _dragMoved = false;
        _mouseDownPoint = e.GetPosition(this);
        _lastScrubX = _mouseDownPoint.X;
        Cursor = Cursors.ScrollWE;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_scrubbing || _state is null) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _mouseDownPoint.X) > 4) _dragMoved = true;
        var deltaX = p.X - _lastScrubX;
        _lastScrubX = p.X;
        if (deltaX != 0) SeekRequested?.Invoke(_time - deltaX / _state.ZoomLevel);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_scrubbing && !_dragMoved && _state is not null)
        {
            var timeDiff = (_mouseDownPoint.X - RhythmoConstants.SyncLinePositionX) / _state.ZoomLevel;
            SeekRequested?.Invoke(_time + timeDiff);
        }
        _scrubbing = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }
}
