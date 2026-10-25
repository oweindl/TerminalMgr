using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace TerminalMgr.App;

/// <summary>
/// The popup that shows the full text of a shortened clipboard entry: it opens after the pointer rested on the entry for a moment
/// (about 400 ms), stays open while the pointer is over the entry or over the popup itself (so one can scroll and select in it)
/// and closes shortly after the pointer left both.
/// </summary>
public sealed class HoverPreview
{
    private readonly DispatcherTimer _show, _hide;
    private readonly Popup _popup;
    private readonly Border _border;
    private readonly ScrollViewer _scroll;
    private readonly TextBox _text;
    private readonly Func<(double Width, double Height)> _limits;
    private readonly Func<FrameworkElement, bool> _pointerOver;
    private FrameworkElement? _target;
    private string _pending = "";

    /// <param name="limits">Width of the terminal area and height of the window; the popup takes that width and 60 % of that height at most.</param>
    /// <param name="pointerOver">Tests replace the mouse check.</param>
    public HoverPreview(Dispatcher dispatcher, Func<(double Width, double Height)> limits, TimeSpan? delay = null, TimeSpan? grace = null, Func<FrameworkElement, bool>? pointerOver = null)
    {
        _limits = limits;
        _pointerOver = pointerOver ?? (element => element.IsMouseOver);
        _text = new TextBox {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0),
            Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xED)), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12
        };
        _scroll = new ScrollViewer { Content = _text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _border = new Border {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x1F)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x71, 0x65)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = _scroll
        };
        _popup = new Popup { Child = _border, StaysOpen = true, Placement = PlacementMode.Left, PopupAnimation = PopupAnimation.None, HorizontalOffset = -4 };
        _show = new DispatcherTimer(delay ?? TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) => Open(), dispatcher) { IsEnabled = false };
        _hide = new DispatcherTimer(grace ?? TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => TryClose(), dispatcher) { IsEnabled = false };
    }

    public bool IsOpen => _popup.IsOpen;
    public string ShownText => _text.Text;
    public double MaxHeight => _scroll.MaxHeight;
    public double MaxWidth => _border.MaxWidth;
    public TextBox TextBox => _text;

    /// <summary>The pointer entered an entry: the popup opens after the delay unless the pointer leaves first.</summary>
    public void Enter(FrameworkElement target, string fullText)
    {
        _hide.Stop();
        if (IsOpen && ReferenceEquals(_target, target)) return;
        if (IsOpen) Close();
        _target = target;
        _pending = fullText;
        _show.Stop();
        _show.Start();
    }

    public void Leave(FrameworkElement target)
    {
        _show.Stop();
        if (!IsOpen) { if (ReferenceEquals(_target, target)) _target = null; return; }
        _hide.Stop();
        _hide.Start();
    }

    public void Close()
    {
        _show.Stop();
        _hide.Stop();
        _popup.IsOpen = false;
        _target = null;
    }

    private void Open()
    {
        _show.Stop();
        if (_target is null || !_pointerOver(_target)) return; // the pointer left while the delay ran
        var (width, height) = _limits();
        _text.Text = _pending;
        _border.MaxWidth = Math.Max(240, width);
        _scroll.MaxHeight = Math.Max(120, height * 0.6);
        _popup.PlacementTarget = _target;
        _popup.IsOpen = true;
    }

    private void TryClose()
    {
        _hide.Stop();
        if ((_target is not null && _pointerOver(_target)) || _pointerOver(_border))
        {
            _hide.Start(); // still over the entry or the popup: keep it open
            return;
        }
        Close();
    }
}
