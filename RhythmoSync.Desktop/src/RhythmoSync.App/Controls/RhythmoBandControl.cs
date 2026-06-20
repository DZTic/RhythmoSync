using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RhythmoSync.Core;
using RhythmoSync.Core.Models;

namespace RhythmoSync.App.Controls;

/// <summary>
/// Bande rythmo native. Architecture "retained mode" :
///  - les blocs sont des DrawingVisual rasterisés une seule fois par WPF (l'équivalent
///    du node.cache() de Konva, mais gratuit : c'est le fonctionnement normal de WPF) ;
///  - le défilement à 60 fps ne modifie qu'une TranslateTransform sur le conteneur,
///    composée sur GPU — aucun redessin par frame ;
///  - virtualisation : seuls les blocs dans la fenêtre visible (± marge) ont un visuel.
/// </summary>
public sealed class RhythmoBandControl : FrameworkElement
{
    private const double VPad = 3;
    private const double EdgeGrabPx = 8;
    private const double VirtualizeMarginSeconds = 3;

    private ProjectState _state = null!;
    private readonly VisualCollection _children;
    private readonly DrawingVisual _background = new();
    private readonly ContainerVisual _movingRoot = new();
    private readonly DrawingVisual _ruler = new();
    private readonly DrawingVisual _markersVisual = new();
    private readonly DrawingVisual _snapIndicator = new();
    private readonly DrawingVisual _syncOverlay = new();
    private readonly TranslateTransform _scroll = new();

    private readonly Dictionary<string, BlockVisual> _blockVisuals = [];
    private readonly List<string> _staleIds = [];

    private double _time;
    private double _pixelsPerDip = 1.0;
    private bool _isPlaying;

    // Ruler cache (régénéré uniquement quand la fenêtre visible change de tick)
    private long _rulerFirstTick = long.MinValue;
    private double _rulerSecsPerTick = -1;

    // Geste souris en cours
    private enum DragMode { None, Scrub, MoveBlock, ResizeLeft, ResizeRight }
    private DragMode _drag = DragMode.None;
    private Point _mouseDownPoint;
    private double _lastScrubX;
    private string _dragBlockId = "";
    private double _grabOffsetTime;             // temps entre le curseur et le début du bloc
    private Dictionary<string, (double Start, int Lane)> _multiDragOrigin = [];
    private double _dragOriginStart;
    private int _dragOriginLane;
    private bool _dragMoved;

    public event Action<double>? SeekRequested;
    public event Action<DialogueBlock, Rect>? EditRequested;

    public bool IsPlaying
    {
        get => _isPlaying;
        set { _isPlaying = value; if (value) Cursor = Cursors.Arrow; }
    }

    public RhythmoBandControl()
    {
        _children = new VisualCollection(this);
        ClipToBounds = true;
        Focusable = false;

        _movingRoot.Transform = _scroll;
        _movingRoot.Children.Add(_ruler);
        _movingRoot.Children.Add(_markersVisual);
        _movingRoot.Children.Add(_snapIndicator);

        _children.Add(_background);
        _children.Add(_movingRoot);
        _children.Add(_syncOverlay);

        Loaded += (_, _) =>
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            RedrawBackground();
            RedrawSyncOverlay();
        };
        SizeChanged += (_, _) => { RedrawBackground(); RedrawSyncOverlay(); InvalidateRuler(); };
    }

    public void Initialize(ProjectState state)
    {
        _state = state;
        _state.DialoguesChanged += () => UpdateTime(_time);
        _state.SelectionChanged += () => UpdateTime(_time);
        _state.ViewChanged += () =>
        {
            InvalidateMeasure();
            InvalidateRuler();
            RedrawBackground();
            RedrawSyncOverlay();
            RedrawMarkers();
            UpdateTime(_time);
        };
        _state.MarkersChanged += RedrawMarkers;
        Loaded += (_, _) => RedrawMarkers();
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var height = _state?.TotalBandHeight ?? 240;
        return new Size(width, height);
    }

    // ── Boucle de synchronisation (appelée chaque frame par la fenêtre) ──────

    /// <summary>
    /// Met à jour la position de défilement et la fenêtre de virtualisation.
    /// Quand rien n'a changé visuellement, seul le X de la TranslateTransform bouge :
    /// WPF recompose sur GPU sans redessiner quoi que ce soit.
    /// </summary>
    public void UpdateTime(double time)
    {
        if (_state is null) return;
        _time = time;

        var pps = _state.ZoomLevel;
        var targetX = Math.Round(RhythmoConstants.SyncLinePositionX - (time + _state.SyncOffset) * pps);
        if (_scroll.X != targetX) _scroll.X = targetX;

        VirtualizePass(pps);
        UpdateRuler(pps);
    }

    private void VirtualizePass(double pps)
    {
        var t0 = (0 - _scroll.X) / pps - VirtualizeMarginSeconds;
        var t1 = (ActualWidth - _scroll.X) / pps + VirtualizeMarginSeconds;

        var seen = new HashSet<string>();
        var dialogues = _state.Dialogues;
        for (var i = 0; i < dialogues.Count; i++)
        {
            var block = dialogues[i];
            if (block.EndTime < t0 || block.StartTime > t1) continue;
            seen.Add(block.Id);

            if (!_blockVisuals.TryGetValue(block.Id, out var visual))
            {
                visual = new BlockVisual(block.Id);
                _blockVisuals[block.Id] = visual;
                _movingRoot.Children.Add(visual);
            }

            var laneHeight = _state.LaneHeightPx;
            var width = Math.Max(RhythmoConstants.MinBlockWidthPx, block.Duration * pps);
            var selected = _state.IsSelected(block.Id);

            if (visual.NeedsRender(block, width, laneHeight, selected, _isPlaying))
                RenderBlock(visual, block, width, laneHeight, selected);

            visual.Position.X = block.StartTime * pps;
            visual.Position.Y = block.Lane * laneHeight;
        }

        // Démontage des visuels hors champ (libère la mémoire GPU, comme le tiling web)
        _staleIds.Clear();
        foreach (var id in _blockVisuals.Keys)
            if (!seen.Contains(id)) _staleIds.Add(id);
        foreach (var id in _staleIds)
        {
            _movingRoot.Children.Remove(_blockVisuals[id]);
            _blockVisuals.Remove(id);
        }
    }

    // ── Rendu d'un bloc (équivalent du node.cache() Konva, fait par WPF) ─────

    private sealed class BlockVisual : DrawingVisual
    {
        public readonly string Id;
        public readonly TranslateTransform Position = new();

        private string? _text;
        private string? _color;
        private double _width, _laneHeight;
        private bool _selected, _tooFast, _playing;

        public BlockVisual(string id)
        {
            Id = id;
            Transform = Position;
        }

        public bool NeedsRender(DialogueBlock block, double width, double laneHeight, bool selected, bool playing)
        {
            var tooFast = block.IsTooFast;
            if (_text == block.Text && _color == block.Color && _width == width &&
                _laneHeight == laneHeight && _selected == selected && _tooFast == tooFast &&
                (_playing == playing || !selected))
                return false;
            _text = block.Text;
            _color = block.Color;
            _width = width;
            _laneHeight = laneHeight;
            _selected = selected;
            _tooFast = tooFast;
            _playing = playing;
            return true;
        }
    }

    private static readonly Typeface BlockTypeface =
        new(new FontFamily("Consolas, Courier New"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Dictionary<string, (SolidColorBrush Fill, Pen Stroke)> BrushCache = [];

    private static (SolidColorBrush Fill, Pen Stroke) GetBrushes(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached)) return cached;
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(hex); }
        catch { color = Color.FromRgb(0x63, 0x66, 0xf1); }
        var fill = new SolidColorBrush(color) { Opacity = 0.35 };
        fill.Freeze();
        var stroke = new Pen(new SolidColorBrush(color), 1);
        stroke.Freeze();
        var entry = (fill, stroke);
        BrushCache[hex] = entry;
        return entry;
    }

    private static readonly SolidColorBrush TooFastFill = Frozen(new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)) { Opacity = 0.6 });
    private static readonly Pen TooFastStroke = FrozenPen(new Pen(Brushes.White, 2) { DashStyle = new DashStyle([4, 4], 0) });
    private static readonly Pen SelectedStroke = FrozenPen(new Pen(Brushes.White, 2));
    private static readonly Pen HandleLine = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1) { DashStyle = new DashStyle([2, 2], 0) });
    private static readonly Pen HandleCircleStroke = FrozenPen(new Pen(Brushes.Black, 1));
    private static readonly SolidColorBrush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa)));

    private static SolidColorBrush Frozen(SolidColorBrush brush) { brush.Freeze(); return brush; }
    private static Pen FrozenPen(Pen pen) { pen.Freeze(); return pen; }

    private void RenderBlock(BlockVisual visual, DialogueBlock block, double width, double laneHeight, bool selected)
    {
        var height = laneHeight - VPad * 2;
        using var dc = visual.RenderOpen();

        var (fill, stroke) = GetBrushes(block.Color);
        var pen = selected ? SelectedStroke : block.IsTooFast ? TooFastStroke : stroke;
        var background = block.IsTooFast ? TooFastFill : fill;

        dc.DrawRoundedRectangle(background, pen, new Rect(0, VPad, width, height), 4, 4);

        if (block.IsTooFast && width > 20)
        {
            var warn = MakeText("⚠", 12, Brushes.White);
            dc.DrawText(warn, new Point(4, laneHeight / 2 - 7));
        }

        if (block.Text.Length > 0)
        {
            // Texte étiré horizontalement pour remplir exactement la largeur du bloc
            // (même rendu que la version web : le texte "colle" au tempo du bloc).
            var fontSize = Math.Max(12, height * 0.6);
            var text = MakeText(block.Text, fontSize, Brushes.White);
            var natural = text.WidthIncludingTrailingWhitespace;
            if (natural > 0.1)
            {
                dc.PushTransform(new ScaleTransform(width / natural, 1));
                dc.DrawText(text, new Point(0, VPad + (height - text.Height) / 2));
                dc.Pop();
            }
        }

        if (selected && !_isPlaying)
        {
            // Poignées de redimensionnement + timecodes (hors lecture uniquement)
            dc.DrawLine(HandleLine, new Point(0, VPad), new Point(0, VPad + height));
            dc.DrawLine(HandleLine, new Point(width, VPad), new Point(width, VPad + height));
            dc.DrawEllipse(Brushes.White, HandleCircleStroke, new Point(0, laneHeight / 2), 6, 6);
            dc.DrawEllipse(Brushes.White, HandleCircleStroke, new Point(width, laneHeight / 2), 6, 6);

            var startLabel = MakeText($"{block.StartTime.ToString("0.00", CultureInfo.InvariantCulture)}s", 10, LabelBrush);
            dc.DrawText(startLabel, new Point(0, -12));
            var durationLabel = MakeText($"({block.Duration.ToString("0.00", CultureInfo.InvariantCulture)}s)", 10, LabelBrush);
            dc.DrawText(durationLabel, new Point(width + 10, laneHeight / 2 - 6));
        }
    }

    private FormattedText MakeText(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, BlockTypeface, size, brush, _pixelsPerDip);

    // ── Fond statique (lanes) + règle temporelle + ligne de synchro ──────────

    private void RedrawBackground()
    {
        using var dc = _background.RenderOpen();
        if (_state is null || ActualWidth <= 0) return;

        var width = ActualWidth;
        var height = _state.TotalBandHeight;
        dc.DrawRectangle(Frozen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27))), null, new Rect(0, 0, width, height));

        var lanePen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x2d, 0x37, 0x48)), 1) { DashStyle = new DashStyle([6, 4], 0) });
        for (var i = 1; i < _state.TotalLanes; i++)
        {
            var y = i * _state.LaneHeightPx;
            dc.DrawLine(lanePen, new Point(0, y), new Point(width, y));
        }

        var borderPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x4a, 0x55, 0x68)), 1));
        dc.DrawLine(borderPen, new Point(0, 0), new Point(width, 0));
        dc.DrawLine(borderPen, new Point(0, height), new Point(width, height));
    }

    private void RedrawSyncOverlay()
    {
        using var dc = _syncOverlay.RenderOpen();
        if (_state is null) return;
        var x = RhythmoConstants.SyncLinePositionX;
        var height = _state.TotalBandHeight;
        var red = Frozen(new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)));
        dc.DrawLine(FrozenPen(new Pen(red, 2)), new Point(x, 0), new Point(x, height));
        var arrow = new StreamGeometry();
        using (var geo = arrow.Open())
        {
            geo.BeginFigure(new Point(x - 6, 0), true, true);
            geo.LineTo(new Point(x + 6, 0), false, false);
            geo.LineTo(new Point(x, 10), false, false);
        }
        arrow.Freeze();
        dc.DrawGeometry(red, null, arrow);
    }

    /// <summary>
    /// Marqueurs nommés dessinés dans l'espace défilant (donc recalés gratuitement
    /// par la TranslateTransform). Re-rendu seulement quand les marqueurs, le zoom ou
    /// la hauteur changent — pas à chaque frame.
    /// </summary>
    private void RedrawMarkers()
    {
        using var dc = _markersVisual.RenderOpen();
        if (_state is null || _state.Markers.Count == 0) return;

        var pps = _state.ZoomLevel;
        var height = _state.TotalBandHeight;
        var cyan = Color.FromRgb(0x22, 0xd3, 0xee);
        var line = FrozenPen(new Pen(new SolidColorBrush(cyan) { Opacity = 0.85 }, 1.5) { DashStyle = new DashStyle([2, 3], 0) });
        var flag = Frozen(new SolidColorBrush(cyan));
        var labelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x06, 0x2a, 0x30)));

        foreach (var m in _state.Markers)
        {
            var x = m.Time * pps;
            dc.DrawLine(line, new Point(x, 0), new Point(x, height));
            var text = MakeText(string.IsNullOrWhiteSpace(m.Label) ? "●" : m.Label, 9, labelBrush);
            dc.DrawRoundedRectangle(flag, null, new Rect(x, 0, text.Width + 8, 13), 3, 3);
            dc.DrawText(text, new Point(x + 4, 1));
        }
    }

    private void InvalidateRuler() => _rulerFirstTick = long.MinValue;

    /// <summary>
    /// Règle temporelle dessinée dans l'espace défilant : contrairement à la version
    /// web, les graduations correspondent au temps réel sous la ligne de lecture.
    /// Re-rendue uniquement quand la fenêtre visible franchit une graduation.
    /// </summary>
    private void UpdateRuler(double pps)
    {
        const double targetTickSpacingPx = 100;
        double[] niceIntervals = [0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        var rawSecsPerTick = targetTickSpacingPx / pps;
        var secsPerTick = niceIntervals.FirstOrDefault(v => v >= rawSecsPerTick, 300);

        var t0 = Math.Max(0, (0 - _scroll.X) / pps);
        var firstTick = (long)Math.Floor(t0 / secsPerTick);
        if (firstTick == _rulerFirstTick && secsPerTick == _rulerSecsPerTick) return;
        _rulerFirstTick = firstTick;
        _rulerSecsPerTick = secsPerTick;

        var tickCount = (int)Math.Ceiling(ActualWidth / (secsPerTick * pps)) + 2;
        var tickPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x4a, 0x55, 0x68)), 1));
        var labelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6b, 0x7a, 0x90)));

        using var dc = _ruler.RenderOpen();
        for (var i = 0; i < tickCount; i++)
        {
            var sec = (firstTick + i) * secsPerTick;
            if (sec < 0) continue;
            var x = sec * pps;
            dc.DrawLine(tickPen, new Point(x, 0), new Point(x, 8));

            var mm = (int)(sec / 60);
            var ss = sec % 60;
            var label = secsPerTick < 1
                ? $"{mm}:{ss.ToString("00.0", CultureInfo.InvariantCulture)}"
                : $"{mm}:{((int)ss).ToString("00", CultureInfo.InvariantCulture)}";
            dc.DrawText(MakeText(label, 9, labelBrush), new Point(x + 3, 1));
        }
    }

    private void ShowSnapIndicator(SnapResult snap, double pps)
    {
        using var dc = _snapIndicator.RenderOpen();
        var color = snap.Kind == SnapTargetKind.SyncLine ? Color.FromRgb(0xef, 0x44, 0x44) : Color.FromRgb(0xfa, 0xcc, 0x15);
        var pen = new Pen(new SolidColorBrush(color) { Opacity = 0.8 }, 2) { DashStyle = new DashStyle([5, 5], 0) };
        pen.Freeze();
        var x = snap.IndicatorTime * pps;
        dc.DrawLine(pen, new Point(x, 0), new Point(x, _state.TotalBandHeight));
    }

    private void HideSnapIndicator()
    {
        using var dc = _snapIndicator.RenderOpen();
    }

    // ── Conversions & hit-testing mathématique (pas de hit-test visuel WPF) ──

    private double XToTime(double screenX) => (screenX - _scroll.X) / _state.ZoomLevel;

    private int YToLane(double y) =>
        Math.Clamp((int)(y / _state.LaneHeightPx), 0, _state.TotalLanes - 1);

    /// <summary>Marqueur sous le pointeur dans le bandeau supérieur (étiquette), sinon null.</summary>
    private Marker? HitMarker(Point p)
    {
        if (_state is null || p.Y > 14) return null;
        var pps = _state.ZoomLevel;
        foreach (var m in _state.Markers)
        {
            var x = m.Time * pps + _scroll.X;
            if (p.X >= x - 4 && p.X <= x + MakeText(string.IsNullOrWhiteSpace(m.Label) ? "●" : m.Label, 9, Brushes.Black).Width + 10)
                return m;
        }
        return null;
    }

    private DialogueBlock? HitBlock(Point p)
    {
        var time = XToTime(p.X);
        var lane = (int)(p.Y / _state.LaneHeightPx);
        var dialogues = _state.Dialogues;
        for (var i = dialogues.Count - 1; i >= 0; i--)
        {
            var d = dialogues[i];
            if (d.Lane == lane && time >= d.StartTime && time <= d.EndTime)
                return d;
        }
        return null;
    }

    /// <summary>Détecte une poignée de redimensionnement d'un bloc sélectionné.</summary>
    private (DialogueBlock Block, bool IsLeft)? HitResizeHandle(Point p)
    {
        var pps = _state.ZoomLevel;
        foreach (var d in _state.Dialogues)
        {
            if (!_state.IsSelected(d.Id)) continue;
            var lane = (int)(p.Y / _state.LaneHeightPx);
            if (d.Lane != lane) continue;
            var leftX = d.StartTime * pps + _scroll.X;
            var rightX = d.EndTime * pps + _scroll.X;
            if (Math.Abs(p.X - leftX) <= EdgeGrabPx) return (d, true);
            if (Math.Abs(p.X - rightX) <= EdgeGrabPx) return (d, false);
        }
        return null;
    }

    // ── Interactions souris ───────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_state is null) return;
        var p = e.GetPosition(this);
        _mouseDownPoint = p;
        _dragMoved = false;

        if (e.ClickCount == 2)
        {
            HandleDoubleClick(p);
            e.Handled = true;
            return;
        }

        // Clic sur l'étiquette d'un marqueur → téléportation de la tête de lecture.
        if (HitMarker(p) is { } marker)
        {
            SeekRequested?.Invoke(marker.Time);
            e.Handled = true;
            return;
        }

        if (!_isPlaying)
        {
            if (HitResizeHandle(p) is { } handle)
            {
                _state.SnapshotHistory();
                _drag = handle.IsLeft ? DragMode.ResizeLeft : DragMode.ResizeRight;
                _dragBlockId = handle.Block.Id;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            if (HitBlock(p) is { } block)
            {
                var multi = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                if (!_state.IsSelected(block.Id)) _state.SelectBlock(block.Id, multi);
                else if (multi) _state.SelectBlock(block.Id, true);

                _state.SnapshotHistory();
                _drag = DragMode.MoveBlock;
                _dragBlockId = block.Id;
                _grabOffsetTime = XToTime(p.X) - block.StartTime;
                _dragOriginStart = block.StartTime;
                _dragOriginLane = block.Lane;

                _multiDragOrigin = _state.SelectedIds.Count > 1
                    ? _state.Dialogues.Where(d => _state.IsSelected(d.Id))
                        .ToDictionary(d => d.Id, d => (d.StartTime, d.Lane))
                    : [];

                CaptureMouse();
                e.Handled = true;
                return;
            }

            _state.SelectBlock(null);
        }

        // Fond vide → scrub de la timeline (ou simple clic = saut)
        _drag = DragMode.Scrub;
        _lastScrubX = p.X;
        Cursor = Cursors.ScrollWE;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_state is null) return;
        var p = e.GetPosition(this);

        if (_drag == DragMode.None)
        {
            UpdateHoverCursor(p);
            return;
        }

        if (Math.Abs(p.X - _mouseDownPoint.X) > 4 || Math.Abs(p.Y - _mouseDownPoint.Y) > 4)
            _dragMoved = true;

        switch (_drag)
        {
            case DragMode.Scrub:
                var deltaX = p.X - _lastScrubX;
                _lastScrubX = p.X;
                if (deltaX != 0) SeekRequested?.Invoke(_time - deltaX / _state.ZoomLevel);
                break;
            case DragMode.MoveBlock:
                HandleMoveDrag(p);
                break;
            case DragMode.ResizeRight:
                HandleResizeRight(p);
                break;
            case DragMode.ResizeLeft:
                HandleResizeLeft(p);
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_state is null) return;

        if (_drag == DragMode.Scrub && !_dragMoved)
        {
            // Clic simple : saute pour amener le point cliqué sous la ligne de synchro
            var timeDiff = (_mouseDownPoint.X - RhythmoConstants.SyncLinePositionX) / _state.ZoomLevel;
            SeekRequested?.Invoke(_time + timeDiff);
        }

        _drag = DragMode.None;
        _multiDragOrigin = [];
        HideSnapIndicator();
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    private void HandleDoubleClick(Point p)
    {
        if (_state is null || _isPlaying) return;

        if (HitBlock(p) is { } block)
        {
            var pps = _state.ZoomLevel;
            var rect = new Rect(
                block.StartTime * pps + _scroll.X,
                block.Lane * _state.LaneHeightPx + VPad,
                Math.Max(50, block.Duration * pps),
                _state.LaneHeightPx - VPad * 2);
            EditRequested?.Invoke(block, rect);
        }
        else
        {
            // Double-clic sur une zone vide → création d'un bloc à cet endroit
            var time = Math.Max(0, XToTime(p.X));
            var lane = YToLane(p.Y);
            var color = RhythmoConstants.CharacterColors[_state.Dialogues.Count % RhythmoConstants.CharacterColors.Length];
            var created = new DialogueBlock
            {
                Text = "Texte",
                StartTime = time,
                Duration = _state.DefaultBlockDuration,
                Lane = lane,
                Color = color,
            };
            _state.AddDialogue(created);
            _state.SelectBlock(created.Id);
        }
    }

    private void UpdateHoverCursor(Point p)
    {
        if (_isPlaying) { Cursor = Cursors.Arrow; return; }
        if (HitResizeHandle(p) is not null) Cursor = Cursors.SizeWE;
        else if (HitBlock(p) is not null) Cursor = Cursors.Hand;
        else Cursor = Cursors.Arrow;
    }

    // ── Gestes : déplacement et redimensionnement ────────────────────────────

    private void HandleMoveDrag(Point p)
    {
        var pps = _state.ZoomLevel;
        var block = _state.Dialogues.FirstOrDefault(d => d.Id == _dragBlockId);
        if (block is null) return;

        var rawStart = Math.Max(0, XToTime(p.X) - _grabOffsetTime);
        var newLane = YToLane(p.Y);

        // Magnétisme (ligne de synchro + bords des autres blocs)
        var targetSyncTime = _time + _state.SyncOffset;
        SnapResult? snap = _state.SnapEnabled
            ? SnapEngine.SnapMove(rawStart, block.Duration, block.Id, _state.Dialogues, targetSyncTime, pps)
            : null;
        if (snap is { } s)
        {
            rawStart = s.SnappedTime;
            ShowSnapIndicator(s, pps);
        }
        else
        {
            HideSnapIndicator();
        }

        if (_multiDragOrigin.Count > 1)
        {
            var dt = rawStart - _dragOriginStart;
            var dLane = newLane - _dragOriginLane;
            var updates = new Dictionary<string, Func<DialogueBlock, DialogueBlock>>();
            foreach (var (id, origin) in _multiDragOrigin)
            {
                var start = Math.Max(0, origin.Start + dt);
                var lane = Math.Clamp(origin.Lane + dLane, 0, _state.TotalLanes - 1);
                updates[id] = d => d with { StartTime = start, Lane = lane };
            }
            _state.UpdateDialogues(updates, skipHistory: true);
        }
        else
        {
            // Anti-collision : on repousse le bloc hors de ses voisins de la même piste
            var duration = block.Duration;
            foreach (var other in _state.Dialogues)
            {
                if (other.Id == block.Id || other.Lane != newLane) continue;
                if (rawStart < other.EndTime && rawStart + duration > other.StartTime)
                {
                    rawStart = rawStart + duration / 2 < other.StartTime + other.Duration / 2
                        ? Math.Max(0, other.StartTime - duration)
                        : other.EndTime;
                }
            }
            var start = rawStart;
            var lane = newLane;
            _state.UpdateDialogue(block.Id, d => d with { StartTime = start, Lane = lane }, skipHistory: true);
        }
    }

    private void HandleResizeRight(Point p)
    {
        var pps = _state.ZoomLevel;
        var block = _state.Dialogues.FirstOrDefault(d => d.Id == _dragBlockId);
        if (block is null) return;

        // Largeur maximale : ne pas chevaucher le bloc suivant sur la même piste
        var maxEnd = double.MaxValue;
        foreach (var other in _state.Dialogues)
            if (other.Id != block.Id && other.Lane == block.Lane && other.StartTime >= block.StartTime)
                maxEnd = Math.Min(maxEnd, other.StartTime);

        var rawEnd = Math.Min(maxEnd, XToTime(p.X));
        var minEnd = block.StartTime + RhythmoConstants.MinBlockWidthPx / pps;
        rawEnd = Math.Max(minEnd, rawEnd);

        if (_state.SnapEnabled &&
            SnapEngine.SnapEdge(rawEnd, block.Id, _state.Dialogues, _time + _state.SyncOffset, pps) is { } snap &&
            snap.SnappedTime <= maxEnd && snap.SnappedTime >= minEnd)
        {
            rawEnd = snap.SnappedTime;
            ShowSnapIndicator(snap, pps);
        }
        else
        {
            HideSnapIndicator();
        }

        var duration = rawEnd - block.StartTime;
        _state.UpdateDialogue(block.Id, d => d with { Duration = duration }, skipHistory: true);
    }

    private void HandleResizeLeft(Point p)
    {
        var pps = _state.ZoomLevel;
        var block = _state.Dialogues.FirstOrDefault(d => d.Id == _dragBlockId);
        if (block is null) return;

        // Borne gauche : la fin du bloc précédent sur la même piste
        var minStart = 0.0;
        foreach (var other in _state.Dialogues)
            if (other.Id != block.Id && other.Lane == block.Lane && other.StartTime <= block.StartTime)
                minStart = Math.Max(minStart, other.EndTime);

        var end = block.EndTime;
        var maxStart = end - RhythmoConstants.MinBlockWidthPx / pps;
        var rawStart = Math.Clamp(XToTime(p.X), minStart, maxStart);

        if (_state.SnapEnabled &&
            SnapEngine.SnapEdge(rawStart, block.Id, _state.Dialogues, _time + _state.SyncOffset, pps) is { } snap &&
            snap.SnappedTime >= minStart && snap.SnappedTime <= maxStart)
        {
            rawStart = snap.SnappedTime;
            ShowSnapIndicator(snap, pps);
        }
        else
        {
            HideSnapIndicator();
        }

        var start = rawStart;
        _state.UpdateDialogue(block.Id, d => d with { StartTime = start, Duration = end - start }, skipHistory: true);
    }
}
