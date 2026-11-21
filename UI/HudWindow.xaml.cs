using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CaretHUD.Interop;
using CaretHUD.Models;
using CaretHUD.Services;

namespace CaretHUD.UI;

public partial class HudWindow : Window
{
    /// <summary>窗口外围留白（给阴影留出绘制空间）。XAML 里的 Grid 也用它，避免两处数字对不上。</summary>
    public const double LayoutMargin = 16;

    private static readonly SolidColorBrush ChineseBrush = CreateFrozen(0x6E, 0xA8, 0xFF);
    private static readonly SolidColorBrush EnglishBrush = CreateFrozen(0xE8, 0xEC, 0xF2);

    private bool _needTopmost;
    private static readonly Duration FadeInDuration = new(TimeSpan.FromMilliseconds(110));
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(90));

    private int _fadeToken;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;

    public HudWindow()
    {
        InitializeComponent();

        if (Content is FrameworkElement root)
            root.Margin = new Thickness(LayoutMargin);

        SourceInitialized += (_, _) => ApplyWindowStyles();
        // 每次重新出现时把窗口插回置顶组顶部，之后移动不再重插 z 序
        IsVisibleChanged += (_, _) => { if (IsVisible) _needTopmost = true; };
    }

    private static SolidColorBrush CreateFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    public void UpdateContent(InputStatus status)
    {
        ImeText.Text = status.IsChinese ? "中" : "英";
        ImeText.Foreground = status.IsChinese ? ChineseBrush : EnglishBrush;
        CapsBadge.Visibility = status.CapsLock ? Visibility.Visible : Visibility.Collapsed;

        // 数字键盘：开启时不显示任何东西，关闭时才显示提醒标记
        NumBadge.Visibility = status.NumLock ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>胶囊当前的透明度（用于判断"窗口可见但其实是透明的"）。</summary>
    public double ContentOpacity => Root.Opacity;
    /// <summary>出现时淡入：消除窗口"硬弹出"的突兀感（不改变位置更新时间）。</summary>
    public void ShowAnimated()
    {
        // 递增令牌：如果此刻正有一个淡出在跑，它的完成回调要作废，否则会在显示后把窗口藏掉
        _fadeToken++;

        if (!IsVisible)
        {
            Root.Opacity = 0;
            Show();
        }

        Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(Root.Opacity, 1, FadeInDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>消失时淡出：先做 90ms 透明度动画再隐藏。</summary>
    public void HideAnimated()
    {
        if (!IsVisible)
            return;

        var token = ++_fadeToken;
        var fade = new DoubleAnimation(1, 0, FadeOutDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            // 淡出期间又被要求显示：取消隐藏
            if (token != _fadeToken)
                return;
            try
            {
                Root.BeginAnimation(UIElement.OpacityProperty, null);
                Root.Opacity = 0;
                Hide();
            }
            catch (InvalidOperationException)
            {
                // 窗口已关闭：忽略
            }
        };
        Root.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public void MoveNear(Rect caretScreen)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (!NativeMethods.GetWindowRect(hwnd, out var wr))
            return;

        var w = wr.Width;
        var h = wr.Height;
        if (w <= 0 || h <= 0)
            return;

        // 窗口含阴影留白，位置按胶囊本体计算
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var margin = LayoutMargin * scale;
        var capsuleW = Math.Max(1, w - margin * 2);
        var capsuleH = Math.Max(1, h - margin * 2);

        var vsLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vsTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vsRight = vsLeft + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vsBottom = vsTop + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        var gap = 4 * scale;

        // 目标：胶囊位于光标正下方，水平与光标居中对齐
        var caretCenterX = caretScreen.Left + caretScreen.Width / 2;
        var capsuleLeft = caretCenterX - capsuleW / 2;
        var capsuleTop = caretScreen.Bottom + gap;

        // 水平夹紧
        if (capsuleLeft < vsLeft)
            capsuleLeft = vsLeft;
        if (capsuleLeft + capsuleW > vsRight)
            capsuleLeft = vsRight - capsuleW;

        // 垂直避让：下方放不下就翻到上方，再不行夹紧
        if (capsuleTop + capsuleH > vsBottom)
        {
            capsuleTop = caretScreen.Top - gap - capsuleH;
            if (capsuleTop < vsTop)
                capsuleTop = vsTop;
        }

        var winX = (int)Math.Round(capsuleLeft - margin);
        var winY = (int)Math.Round(capsuleTop - margin);

        // 死区：位移不足 1px 不动窗口，避免每条状态都触发一次合成
        if (Math.Abs(winX - _lastX) <= 1 && Math.Abs(winY - _lastY) <= 1)
            return;
        _lastX = winX;
        _lastY = winY;

        if (AppLog.Enabled)
        {
            AppLog.Write(
                $"move caret={caretScreen.Left:0},{caretScreen.Top:0} {caretScreen.Width:0}x{caretScreen.Height:0} " +
                $"win={winX},{winY} scale={scale:0.##} capsule={capsuleW:0}x{capsuleH:0}");
        }

        // 平时移动不重插 z 序（SWP_NOZORDER），减少合成抖动；
        // 每次浮窗重新出现时重插一次，保证它压在其它置顶窗口（桌宠/播放器）上面
        var flags = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        if (_needTopmost)
            _needTopmost = false;
        else
            flags |= NativeMethods.SWP_NOZORDER;

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, winX, winY, 0, 0, flags);
    }
}






