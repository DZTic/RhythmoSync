using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RhythmoSync.Core;

namespace RhythmoSync.App.Controls;

/// <summary>
/// Panneau d'historique (port du HistoryPanel web) : liste les états passés,
/// l'état courant (mis en évidence) et les états rétablissables. Cliquer sur une
/// entrée y navigue par enchaînement d'undo/redo. Construit en code, mis à jour
/// à chaque DialoguesChanged.
/// </summary>
public sealed class HistoryPanel : ScrollViewer
{
    private static readonly Brush CurrentFg = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush PastFg = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));
    private static readonly Brush FutureFg = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
    private static readonly Brush CurrentBg = new SolidColorBrush(Color.FromArgb(0x22, 0x63, 0x66, 0xF1));

    private ProjectState? _state;
    private bool _navigating;

    // DialoguesChanged arrive à ~60 Hz pendant un drag : la reconstruction est
    // coalescée (une seule passe quand le dispatcher respire) et différée tant que
    // le panneau est masqué — reconstruire 50 boutons par frame coûtait cher.
    private bool _rebuildQueued;
    private bool _pendingWhileHidden;

    private readonly TextBlock _subtitle;
    private readonly StackPanel _list = new();

    public HistoryPanel()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        var root = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
        root.Children.Add(new TextBlock
        {
            Text = "🕘  HISTORIQUE",
            FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = Res<Brush>("TextMuted"), Margin = new Thickness(0, 0, 0, 2),
        });
        _subtitle = new TextBlock { FontSize = 10, Foreground = Res<Brush>("TextMuted"), Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(_subtitle);

        var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 10) };
        var undo = new Button { Content = "⟲ Annuler", Style = Res<Style>("ToolButton"), Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(0, 5, 0, 5) };
        var redo = new Button { Content = "⟳ Rétablir", Style = Res<Style>("ToolButton"), Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(0, 5, 0, 5) };
        undo.Click += (_, _) => _state?.Undo();
        redo.Click += (_, _) => _state?.Redo();
        buttons.Children.Add(undo);
        buttons.Children.Add(redo);
        root.Children.Add(buttons);

        root.Children.Add(_list);
        Content = root;

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || !_pendingWhileHidden) return;
            _pendingWhileHidden = false;
            RebuildCore();
        };
    }

    public void Initialize(ProjectState state)
    {
        _state = state;
        state.DialoguesChanged += Rebuild;
        Rebuild();
    }

    /// <summary>Reconstruction coalescée : plusieurs événements → une seule passe.</summary>
    private void Rebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _rebuildQueued = false;
            if (!IsVisible) { _pendingWhileHidden = true; return; }
            RebuildCore();
        }));
    }

    private void RebuildCore()
    {
        if (_state is null || _navigating) return;

        var past = _state.PastDepth;
        var future = _state.FutureDepth;
        var pastCounts = _state.PastBlockCounts;
        var futureCounts = _state.FutureBlockCounts;
        var currentCount = _state.Dialogues.Count;
        var total = past + 1 + future;

        _subtitle.Text = $"{past} action(s) · {future} refaisable(s)";
        _list.Children.Clear();

        // Du plus récent (en haut) au plus ancien
        for (var index = total - 1; index >= 0; index--)
        {
            var isCurrent = index == past;
            var isFuture = index > past;
            var count = isCurrent ? currentCount : isFuture ? futureCounts[index - past - 1] : pastCounts[index];
            var label = index == 0 ? "État initial" : isFuture ? $"Rétablir #{index}" : $"Action #{index}";

            var target = index;
            _list.Children.Add(BuildEntry(label, count, isCurrent, isFuture, () => NavigateTo(target)));
        }

        if (total == 1)
        {
            _list.Children.Clear();
            _list.Children.Add(new TextBlock
            {
                Text = "Aucune action enregistrée. Créez ou modifiez des blocs pour voir l'historique.",
                Foreground = Res<Brush>("TextMuted"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    private Button BuildEntry(string label, int count, bool isCurrent, bool isFuture, Action onClick)
    {
        var title = new TextBlock
        {
            Text = label, FontSize = 12,
            FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
            Foreground = isCurrent ? CurrentFg : isFuture ? FutureFg : PastFg,
        };
        var sub = new TextBlock
        {
            Text = $"{count} dialogue(s)" + (isCurrent ? "  · actuel" : ""),
            FontSize = 10, FontFamily = new FontFamily("Consolas"), Foreground = Res<Brush>("TextMuted"),
        };
        var stack = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };
        stack.Children.Add(title);
        stack.Children.Add(sub);

        var border = new Border
        {
            CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 1, 0, 1),
            Background = isCurrent ? CurrentBg : Brushes.Transparent,
            Child = stack,
        };

        var button = new Button
        {
            Content = border, Cursor = isCurrent ? null : System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = FlatTemplate(),
        };
        if (!isCurrent) button.Click += (_, _) => onClick();
        return button;
    }

    private void NavigateTo(int targetIndex)
    {
        if (_state is null) return;
        var diff = targetIndex - _state.PastDepth;
        if (diff == 0) return;

        _navigating = true;
        try
        {
            if (diff < 0) for (var i = 0; i < -diff; i++) _state.Undo();
            else for (var i = 0; i < diff; i++) _state.Redo();
        }
        finally
        {
            _navigating = false;
        }
        Rebuild();
    }

    /// <summary>Gabarit de bouton plat (juste le contenu, sans le chrome Windows).</summary>
    private static ControlTemplate FlatTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        template.VisualTree = presenter;
        return template;
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}
